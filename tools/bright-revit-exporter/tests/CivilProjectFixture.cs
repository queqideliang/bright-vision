using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using BrightVision.Civil;

internal static class CivilProjectFixture
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; } catch (SerializationException) { rejected = true; }
        Check(rejected, message);
    }
    private static CivilElementRecord Element(string id, string kind, string level, string levelUniqueId, double volume)
    {
        return new CivilElementRecord {
            ElementId = id, UniqueId = "uid-" + id, Name = "构件" + id, KindCode = kind, KindName = kind, Supported = kind != "review",
            LevelId = level, LevelName = "同名原生层", RawLevelId = "-1", RawLevelName = String.Empty, LevelUniqueId = levelUniqueId, LevelSource = "FAMILY_LEVEL_PARAM",
            Length = new CivilMetric { Status = "available", Value = 1, Unit = "m", Source = "native-length" },
            Area = new CivilMetric { Status = "available", Value = 2, Unit = "m2", Source = "native-area" },
            Volume = new CivilMetric { Status = "available", Value = volume, Unit = "m3", Source = "native-volume" }
        };
    }
    private static List<List<string>> Csv(string text)
    {
        var result = new List<List<string>>(); var row = new List<string>(); var value = new StringBuilder(); bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (current == '"') { if (quoted && index + 1 < text.Length && text[index + 1] == '"') { value.Append('"'); index++; } else quoted = !quoted; }
            else if (!quoted && current == ',') { row.Add(value.ToString()); value.Length = 0; }
            else if (!quoted && (current == '\r' || current == '\n'))
            {
                if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                row.Add(value.ToString()); value.Length = 0; result.Add(row); row = new List<string>();
            }
            else value.Append(current);
        }
        Check(!quoted, "CSV quote pairing failed."); return result;
    }
    private static int Main(string output)
    {
        try
        {
            var native = new CivilSnapshot { ModelTitle = "原生模型", DocumentScope = "model-scope-A", SourcePath = Path.Combine(output, "model.rvt"), Revision = 9 };
            native.Levels.Add(new CivilLevelInfo { Id = "100", UniqueId = "level-A", Name = "同名层", ElevationMetres = -3.2 });
            native.Levels.Add(new CivilLevelInfo { Id = "200", UniqueId = "level-B", Name = "同名层", ElevationMetres = 0 });
            native.Elements.Add(Element("A", "wall", "100", "level-A", 1.25));
            native.Elements.Add(Element("B", "wall", "200", "level-B", 2.75));
            native.Elements.Add(Element("C", "floor", "100", "level-A", 3));
            native.Elements.Add(Element("D", "review", "-1", String.Empty, 999));
            native.Elements[0].Issues.Add("原生检查项"); native.Notices.Add("原生读取范围");
            var profile = CivilProject.Create(native, "=工程(测试)");
            Check(profile.SchemaVersion == 1 && profile.Revision == 1 && new Guid(profile.ProjectId) != Guid.Empty, "Create lost schema/identity/revision.");
            Check(profile.SourceDocumentScope == native.DocumentScope && profile.SourcePath == native.SourcePath, "Create did not bind path and scope.");
            Check(profile.QuantityLevels.Count == 0 && profile.LevelMappings.Count == 0 && profile.Segments.Count == 0, "Create invented reporting mappings.");
            Check(profile.CreatedAtUtc.Contains("T") && profile.UpdatedAtUtc == profile.CreatedAtUtc, "Create lost UTC timestamps.");
            profile.QuantityLevels.Add(new CivilQuantityLevel { Id = "Q1", Name = "同名计量层", ElevationMetres = -3.2 });
            profile.QuantityLevels.Add(new CivilQuantityLevel { Id = "Q2", Name = "同名计量层", ElevationMetres = null });
            profile.LevelMappings.Add(new CivilLevelMapping { SourceLevelUniqueId = "level-A", QuantityLevelId = "Q1" });
            profile.LevelMappings.Add(new CivilLevelMapping { SourceLevelUniqueId = "level-B", QuantityLevelId = "Q2" });
            profile.Segments.Add(new CivilSegment { Id = "S1", Name = "一区" }); profile.Segments.Add(new CivilSegment { Id = "S2", Name = "二区" });
            profile.ElementSegments.Add(new CivilElementSegment { ElementUniqueId = "uid-other-scope", SegmentId = "S1" });
            CivilProject.Validate(profile);
            var copy = CivilProject.Clone(profile);
            copy.QuantityLevels[0].Name = "取消时的修改"; copy.LevelMappings[0].QuantityLevelId = "Q2"; copy.Segments[0].Name = "其他";
            Check(profile.QuantityLevels[0].Name == "同名计量层" && profile.LevelMappings[0].QuantityLevelId == "Q1" && profile.Segments[0].Name == "一区", "Clone leaked edits into the active profile.");
            CivilProject.AssignSegment(profile, native.Elements.Take(2), "S1");
            Check(profile.Revision == 2 && profile.ElementSegments.Count == 3 && profile.ElementSegments.Any(item => item.ElementUniqueId == "uid-other-scope"), "Assignment erased other scope membership.");
            CivilProject.AssignSegment(profile, new[] { native.Elements[0], native.Elements[0] }, "S2");
            Check(profile.ElementSegments.Count(item => item.ElementUniqueId == "uid-A") == 1 && profile.ElementSegments.Single(item => item.ElementUniqueId == "uid-A").SegmentId == "S2", "Reassignment duplicated a source element.");
            int revision = profile.Revision;
            CivilProject.AssignSegment(profile, new[] { native.Elements[0] }, "S2"); CivilProject.AssignSegment(profile, new CivilElementRecord[0], "S1");
            Check(profile.Revision == revision, "No-op assignments advanced revision.");
            CivilProject.ClearAssignment(profile, new[] { native.Elements[1] });
            Check(profile.ElementSegments.Count == 2 && !profile.ElementSegments.Any(item => item.ElementUniqueId == "uid-B"), "ClearAssignment touched unrelated members.");
            revision = profile.Revision;
            Reject(() => CivilProject.AssignSegment(profile, native.Elements, "unknown"), "Unknown segment target was accepted.");
            Reject(() => CivilProject.AssignSegment(profile, new[] { new CivilElementRecord() }, "S1"), "Missing source identity was assigned.");
            Check(profile.Revision == revision && profile.ElementSegments.Count == 2, "Rejected assignments partially modified the project.");
            CivilSnapshot applied = CivilProject.Apply(native, profile);
            Check(!Object.ReferenceEquals(applied, native) && !Object.ReferenceEquals(applied.Elements[0], native.Elements[0]), "Apply mutated the native snapshot.");
            Check(!Object.ReferenceEquals(applied.Levels, native.Levels) && !Object.ReferenceEquals(applied.Levels[0], native.Levels[0]), "Native level catalogue was aliased.");
            Check(Object.ReferenceEquals(applied.Elements[0].Volume, native.Elements[0].Volume), "Apply changed the native metric representation.");
            Check(native.ProjectId == String.Empty && native.Elements[0].QuantityLevelId == String.Empty && native.Elements[0].ProjectIssues.Count == 0 && native.ProjectNotices.Count == 0, "Native snapshot acquired project annotations.");
            Check(applied.Elements[0].QuantityLevelId == "Q1" && applied.Elements[1].QuantityLevelId == "Q2" && applied.Elements[0].SegmentId == "S2", "Apply did not use stable source identities.");
            Check(applied.Elements[0].LevelId == "100" && applied.Elements[0].RawLevelId == "-1" && applied.Elements[0].LevelSource == "FAMILY_LEVEL_PARAM", "Apply overwrote original/resolved Revit levels.");
            Check(applied.Elements[1].SegmentName == "未分配施工段" && applied.Elements[3].QuantityLevelName == "未映射计量层", "Missing mappings were silently assigned.");
            Check(applied.Elements[0].Issues.SequenceEqual(native.Elements[0].Issues) && applied.Elements[1].ProjectIssues.Count == 1 && applied.ProjectNotices.Count == 2, "Project and native checks were mixed or hidden.");
            Check(CivilSchedule.Summarize(applied.Elements).Volume.Value == 7 && CivilSchedule.Summarize(native.Elements).Volume.Value == 7, "Mapping altered native quantities or counted unsupported content.");
            CivilSnapshot again = CivilProject.Apply(applied, profile);
            Check(again.Elements[3].ProjectIssues.Count == 2 && again.Elements[0].Issues.Count == 1, "Repeated Apply accumulated issues.");
            var other = CivilProject.Clone(profile); other.LevelMappings[0].QuantityLevelId = "Q2";
            CivilSnapshot switched = CivilProject.Apply(native, other);
            Check(switched.Elements[0].QuantityLevelId == "Q2" && applied.Elements[0].QuantityLevelId == "Q1", "Switching project mutated an earlier result.");
            applied.Elements[0].Issues.Add("仅副本修改"); applied.Notices.Add("仅副本提示");
            Check(native.Elements[0].Issues.Count == 1 && native.Notices.Count == 1, "Copied issue/notice lists still alias native data.");
            List<CivilScheduleRow> quantityRows = CivilSchedule.BuildRows(applied.Elements, "quantity-level");
            Check(quantityRows.Count == 4 && quantityRows.Sum(row => row.Count) == 4 && quantityRows.Where(row => row.Volume.Value.HasValue).Sum(row => row.Volume.Value.Value) == 7, "Quantity-level grouping changed scope or totals.");
            Check(quantityRows.Where(row => row.Elements[0].KindCode == "wall").Select(row => row.Key).Distinct().Count() == 2, "Same-name quantity levels were merged.");
            List<CivilScheduleRow> segmentRows = CivilSchedule.BuildRows(applied.Elements, "segment");
            Check(segmentRows.Sum(row => row.Count) == 4 && segmentRows.Where(row => row.Volume.Value.HasValue).Sum(row => row.Volume.Value.Value) == 7, "Segment grouping duplicated quantities.");
            Check(CivilSchedule.FilterProject(applied.Elements, "", "wall", "", false, "Q1", "S2").Single().ElementId == "A", "Project filters did not compose with legacy filters.");
            Check(CivilSchedule.Filter(applied.Elements, "FAMILY_LEVEL_PARAM", "", "", false).Count == 4 && CivilSchedule.Filter(applied.Elements, "二区", "", "", false).Single().ElementId == "A", "Search omitted project/reference-level provenance.");
            List<List<string>> csv = Csv(CivilSchedule.ToCsv(applied, applied.Elements, "quantity-level"));
            Check(csv.Any(row => row.Count == 2 && row[0] == "楼层口径" && row[1].Contains("原生参照楼层") && row[1].Contains("Element.LevelId/Name") && row[1].Contains("LevelSource")),
                "CSV did not explain the historical level column and original provenance fields.");
            int headerIndex = csv.FindIndex(row => row.Count > 0 && row[0] == "分组标识"); List<string> header = csv[headerIndex];
            Check(header.Take(10).SequenceEqual(new[] { "分组标识", "构件/分组", "业务类别", "原始楼层", "记录数", "受支持记录数", "未计量记录数", "来源ElementIds", "来源UniqueIds", "问题/未计量原因" }), "Phase1 CSV columns moved.");
            Check(header.IndexOf("计量工程标识") >= 34 && header.Contains("原始Element.LevelId") && header.Contains("参照层解析来源"), "CSV omitted the appended project/provenance contract.");
            List<List<string>> table = csv.Skip(headerIndex + 1).TakeWhile(row => row.Count > 1).ToList();
            Check(table.Count == quantityRows.Count && table.All(row => row.Count == header.Count), "CSV grouping or field count differs from displayed rows.");
            Check(table.All(row => row[header.IndexOf("计量工程名称")] == "'=工程(测试)"), "Formula-shaped project text was not protected in CSV.");
            Check(table.Where(row => row[header.IndexOf("原生体积(m3)")] != "NA").Sum(row => Double.Parse(row[header.IndexOf("原生体积(m3)")], CultureInfo.InvariantCulture)) == 7, "Project CSV changed totals or included unsupported values.");

            foreach (Action<CivilProjectSettings> damage in new Action<CivilProjectSettings>[] {
                value => value.SchemaVersion = 2, value => value.ProjectId = Guid.Empty.ToString(), value => value.Revision = 0,
                value => value.SourceDocumentScope = "", value => value.SourcePath = "relative.rvt", value => value.SourcePath = "RSN://server/model.rvt",
                value => value.SourcePath = "BIM 360://project/model.rvt", value => value.SourcePath = @"C:relative.rvt", value => value.SourcePath = @"\root-relative.rvt",
                value => value.SourcePath = @"C:\model.rvt:alternate.rvt", value => value.QuantityLevels[0].ElevationMetres = Double.NaN,
                value => value.QuantityLevels[0].ElevationMetres = Double.PositiveInfinity, value => value.QuantityLevels.Add(new CivilQuantityLevel { Id = "Q1", Name = "重复" }),
                value => value.LevelMappings.Add(new CivilLevelMapping { SourceLevelUniqueId = "level-A", QuantityLevelId = "Q2" }),
                value => value.LevelMappings[0].QuantityLevelId = "missing", value => value.Segments.Add(new CivilSegment { Id = "S1", Name = "重复" }),
                value => value.ElementSegments.Add(new CivilElementSegment { ElementUniqueId = "uid-A", SegmentId = "S1" }),
                value => value.ElementSegments[0].SegmentId = "missing", value => value.QuantityLevels = null,
                value => value.CreatedAtUtc = "not-a-date", value => value.PreferredScope = "remote-execute"
            }) { var invalid = CivilProject.Clone(profile); damage(invalid); Reject(() => CivilProject.Validate(invalid), "Invalid project settings were accepted."); }
            var foreign = CivilProject.Clone(profile); foreign.SourceDocumentScope = "another-document";
            Reject(() => CivilProject.Apply(native, foreign), "Cross-document profile was applied.");
            foreign = CivilProject.Clone(profile); foreign.SourcePath = Path.Combine(output, "other.rvt");
            Reject(() => CivilProject.Apply(native, foreign), "Same-scope different-path profile was applied.");
            var unsaved = new CivilSnapshot { DocumentScope = native.DocumentScope, ModelTitle = "未保存" };
            Reject(() => CivilProject.Create(unsaved, "工程"), "Unsaved model acquired a weak path binding.");

            string file = Path.Combine(output, "project.json");
            CivilProjectStore.Save(file, profile); var loaded = CivilProjectStore.Load(file);
            Check(loaded.ProjectId == profile.ProjectId && loaded.LevelMappings[0].SourceLevelUniqueId == "level-A" && loaded.ElementSegments.Count == 2, "JSON roundtrip lost identities or membership.");
            Check(loaded.Name == "=工程(测试)" && !loaded.QuantityLevels[1].ElevationMetres.HasValue && loaded.QuantityLevels[0].ElevationMetres == -3.2, "JSON interpreted data as a formula or lost nullable/negative elevations.");
            Check(File.ReadAllBytes(file)[0] == (byte)'{' && loaded.Revision == profile.Revision, "JSON format/revision was changed by Save.");
            loaded.Name = "保存后的工程"; CivilProject.Touch(loaded); CivilProjectStore.Save(file, loaded);
            Check(CivilProjectStore.Load(file).Name == loaded.Name && Directory.GetFiles(output, ".bright-civil-*.tmp").Length == 0, "Atomic update left temporary data or lost the saved edit.");
            byte[] original = File.ReadAllBytes(file); var stale = CivilProject.Clone(profile);
            Reject(() => CivilProjectStore.Save(file, stale), "A stale revision overwrote a newer project.");
            Check(File.ReadAllBytes(file).SequenceEqual(original), "Rejected stale Save changed the existing file.");
            var different = CivilProject.Clone(loaded); different.ProjectId = Guid.NewGuid().ToString();
            Reject(() => CivilProjectStore.Save(file, different), "Another project overwrote an existing project file.");
            string broken = Path.Combine(output, "broken.json"); File.WriteAllText(broken, "{ damaged json", new UTF8Encoding(false));
            Reject(() => CivilProjectStore.Load(broken), "Corrupt JSON was loaded."); Reject(() => CivilProjectStore.Save(broken, loaded), "Save overwrote corrupt existing JSON.");
            Check(File.ReadAllText(broken) == "{ damaged json", "Corrupt original was destroyed.");
            string unsupported = Path.Combine(output, "schema.json"); File.WriteAllText(unsupported, Encoding.UTF8.GetString(original).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"), new UTF8Encoding(false));
            Reject(() => CivilProjectStore.Load(unsupported), "Future schema JSON was loaded.");
            string oversized = Path.Combine(output, "oversized.json"); using (var stream = File.Create(oversized)) stream.SetLength(CivilProjectStore.MaxBytes + 1L);
            Reject(() => CivilProjectStore.Load(oversized), "Over-limit input was read.");
            var huge = CivilProject.Clone(profile); huge.ElementSegments.Clear();
            for (int index = 0; index < 50000; index++) huge.ElementSegments.Add(new CivilElementSegment { ElementUniqueId = index.ToString(CultureInfo.InvariantCulture) + new string('x', 120), SegmentId = "S1" });
            string tooLarge = Path.Combine(output, "too-large.json"); Reject(() => CivilProjectStore.Save(tooLarge, huge), "Over-limit output was saved.");
            Check(!File.Exists(tooLarge) && Directory.GetFiles(output, ".bright-civil-*.tmp").Length == 0, "Over-limit Save left an incomplete project file.");
            var hugeAssignments = huge.ElementSegments; int hugeRevision = huge.Revision;
            Reject(() => CivilProject.AssignSegment(huge, new[] { native.Elements[0] }, "S1"), "Over-limit assignment was accepted.");
            Check(Object.ReferenceEquals(huge.ElementSegments, hugeAssignments) && huge.ElementSegments.Count == 50000 && huge.Revision == hugeRevision,
                "Failed assignment replaced membership or advanced the revision.");
            var invalidTouch = CivilProject.Clone(profile); int touchRevision = invalidTouch.Revision; invalidTouch.LevelMappings[0].QuantityLevelId = "missing";
            Reject(() => CivilProject.Touch(invalidTouch), "Touch accepted invalid settings.");
            Check(invalidTouch.Revision == touchRevision, "Rejected Touch advanced the revision.");
            Reject(() => CivilProjectStore.Save("relative.json", profile), "Relative project output was accepted.");
            Console.WriteLine("PASS: " + checks.ToString(CultureInfo.InvariantCulture) + " project assertions; pure managed DLL, no Revit/UI/child executable.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
