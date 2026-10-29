using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BrightVision.Civil;

internal static class CivilWallReportFixture
{
    private static int checks;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    private static void Near(double? actual, double expected, string message) { Check(actual.HasValue && Math.Abs(actual.Value - expected) < 1e-9, message); }
    private static void Refuses(Action action, string message)
    {
        bool refused = false; try { action(); } catch (InvalidDataException) { refused = true; } catch (ArgumentException) { refused = true; }
        Check(refused, message);
    }
    private static CivilMetric Metric(double value, string unit, string source) { return new CivilMetric { Status = "available", Value = value, Unit = unit, Source = source }; }
    private static CivilWallFact Wall(string id, double length, double height, double width, bool opening, bool empty)
    {
        var wall = new CivilWallFact { ElementId = id, UniqueId = "wall-" + id, Name = "墙 " + id, TypeUniqueId = id == "B" ? "type-B" : "type-A", TypeName = "同名类型",
            Status = "verified", GeometryVerified = true, InterwallOverlapCheckComplete = true, ParameterLength = Metric(length, "m", "axis"), ParameterHeight = Metric(height, "m", "height"), ParameterWidth = Metric(width, "m", "width"),
            GeometryVolumeMetres3 = length * height * width - (empty ? length * height : opening ? 2 : 0) * width, SymmetricDifferenceMetres3 = 0, GeometryToleranceMetres3 = 1e-8,
            NativeArea = Metric(123, "m2", "HOST_AREA_COMPUTED"), NativeVolume = Metric(456, "m3", "HOST_VOLUME_COMPUTED") };
        if (opening || empty) wall.Inserts.Add(new CivilWallInsert { ElementId = id + "-opening", UniqueId = id + "-opening-uid", HostUniqueId = wall.UniqueId, Kind = "rect-opening", Status = "recognized", Source = "exact rectangle",
            RectX0 = empty ? 0 : 1, RectX1 = empty ? length : 3, RectZ0 = empty ? 0 : 1, RectZ1 = empty ? height : 2 });
        return wall;
    }
    private static CivilWallCalculationBatch Batch()
    {
        var walls = new List<CivilWallFact> { Wall("A", 10, 3, .2, true, false), Wall("B", 5, 2, .3, false, false),
            new CivilWallFact { ElementId = "C", UniqueId = "wall-C", Name = "unsupported", Status = "unsupported", Issues = new List<string> { "缺少几何证明" } },
            new CivilWallFact { ElementId = "D", UniqueId = "wall-D", Name = "failed", Status = "failed", Issues = new List<string> { "读取失败" } }, Wall("E", 10, 3, .2, false, true) };
        var native = new CivilSnapshot { DocumentScope = "fixture-doc", SourcePath = "H:\\fixture\\wall-report.rvt", Revision = 9, ModelTitle = "@model", PhaseLabel = "新建", ScopeLabel = "当前工程", DesignOptionLabel = "主模型", CapturedAtUtc = "2026-10-29T00:00:00Z" };
        foreach (CivilWallFact wall in walls)
        {
            bool first = wall.ElementId == "A" || wall.ElementId == "E";
            native.Elements.Add(new CivilElementRecord { UniqueId = wall.UniqueId, ElementId = wall.ElementId, Name = wall.Name, KindCode = "wall", Supported = wall.Status == "verified", TypeName = wall.TypeName,
                LevelUniqueId = first ? "level-A" : wall.ElementId == "B" ? "level-B" : "level-C", LevelId = first ? "101" : wall.ElementId == "B" ? "102" : "103", LevelName = "同名楼层",
                RawLevelId = "raw-" + wall.ElementId, RawLevelName = "原始参照", LevelSource = "WALL_BASE_CONSTRAINT",
                Area = wall.NativeArea, Volume = wall.NativeVolume, Length = wall.ParameterLength });
        }
        return CivilWallCalculator.Calculate(new CivilWallSnapshot { NativeSnapshot = native, Walls = walls, GeometryViewUniqueId = "view-uid", GeometryViewName = "三维", GeometryPhaseId = "phase-id" });
    }
    private static CivilProjectSettings Project(CivilSnapshot snapshot)
    {
        var project = CivilProject.Create(snapshot, "合成工程");
        project.QuantityLevels.Add(new CivilQuantityLevel { Id = "q-A", Name = "同名计量层" }); project.QuantityLevels.Add(new CivilQuantityLevel { Id = "q-B", Name = "同名计量层" });
        project.LevelMappings.Add(new CivilLevelMapping { SourceLevelUniqueId = "level-A", QuantityLevelId = "q-A" }); project.LevelMappings.Add(new CivilLevelMapping { SourceLevelUniqueId = "level-B", QuantityLevelId = "q-B" });
        project.Segments.Add(new CivilSegment { Id = "s-A", Name = "同名施工段" }); project.Segments.Add(new CivilSegment { Id = "s-B", Name = "同名施工段" });
        foreach (string id in new[] { "A", "E" }) project.ElementSegments.Add(new CivilElementSegment { ElementUniqueId = "wall-" + id, SegmentId = "s-A" });
        foreach (string id in new[] { "B", "D" }) project.ElementSegments.Add(new CivilElementSegment { ElementUniqueId = "wall-" + id, SegmentId = "s-B" });
        return project;
    }
    private static List<List<string>> Csv(string text)
    {
        var result = new List<List<string>>(); var row = new List<string>(); var cell = new System.Text.StringBuilder(); bool quoted = false;
        for (int at = 0; at < text.Length; at++)
        {
            char current = text[at];
            if (current == '"') { if (quoted && at + 1 < text.Length && text[at + 1] == '"') { cell.Append('"'); at++; } else quoted = !quoted; }
            else if (current == ',' && !quoted) { row.Add(cell.ToString()); cell.Length = 0; }
            else if ((current == '\n' || current == '\r') && !quoted) { if (current == '\r' && at + 1 < text.Length && text[at + 1] == '\n') at++; row.Add(cell.ToString()); cell.Length = 0; result.Add(row); row = new List<string>(); }
            else cell.Append(current);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); result.Add(row); }
        Check(!quoted, "CSV quotes balance, including embedded newlines."); return result;
    }
    private static int Main(string[] args)
    {
        string output = args[0]; Directory.CreateDirectory(output);
        CivilWallCalculationBatch batch = Batch(); CivilSnapshot original = batch.Input.NativeSnapshot;
        CivilProjectSettings profile = Project(original); int profileRevision = profile.Revision;
        CivilWallReportData data = CivilWallReport.Create(batch, profile);
        Check(data.Rows.Count == 5 && data.Snapshot.ProjectId == profile.ProjectId, "All successful, unsupported and failed source walls are retained.");
        Check(!Object.ReferenceEquals(data.Snapshot, original) && original.ProjectId == "" && original.Elements.All(source => source.QuantityLevelId == "" && source.SegmentId == ""), "Project application does not mutate original snapshot or original source attribution.");
        Check(profile.Revision == profileRevision && profile.ElementSegments.Count == 4 && batch.Results[0].Fact.NativeArea.Value == 123, "Original project, fact and native metrics remain unchanged.");
        Check(Object.ReferenceEquals(data.Rows[0].Result, batch.Results[0]) && !Object.ReferenceEquals(data.Rows[0].Source, original.Elements[0]), "Report uses its applied source annotations and existing independent quantity result.");
        Near(data.Rows[0].Result.ResultAreaMetres2, 28, "Expected net projection is 28, independent of native area 123.");
        Near(data.Rows[0].Result.ResultVolumeMetres3, 5.6, "Expected net volume is 5.6, independent of native volume 456.");
        List<CivilWallReportGroup> grouped = CivilWallReport.Group(data.Rows, "quantity-level");
        Check(grouped.Count == 3 && grouped.Count(group => group.Name == "同名计量层") == 2, "Distinct quantity-level IDs remain separate despite identical names.");
        CivilWallReportGroup qa = grouped.Single(group => group.Key.EndsWith("id:q-A", StringComparison.Ordinal));
        Check(qa.WallCount == 2 && qa.CalculatedCount == 2 && qa.Rows.Count == 2, "Group retains exact sources for all-group location."); Near(qa.BasisAreaMetres2, 60, "Known gross projection sum."); Near(qa.OpeningAreaMetres2, 32, "Known deduplicated openings sum."); Near(qa.ResultAreaMetres2, 28, "Net projection sum preserves valid zero."); Near(qa.ResultVolumeMetres3, 5.6, "Volume group sum.");
        CivilWallReportGroup missing = grouped.Single(group => group.Key.EndsWith("missing", StringComparison.Ordinal));
        Check(missing.WallCount == 2 && missing.CalculatedCount == 0 && missing.UnsupportedCount == 1 && missing.FailedCount == 1 && !missing.ResultAreaMetres2.HasValue && !missing.ResultVolumeMetres3.HasValue, "Unmapped, all-unavailable group keeps coverage and blank totals instead of false zero.");
        Check(CivilWallReport.Group(data.Rows, "level").Count == 3, "Same-name reference levels are grouped by stable identity.");
        Check(CivilWallReport.Group(data.Rows, "segment").Count == 3, "Same-name segments remain separate and unassigned is visible.");
        Check(CivilWallReport.Group(data.Rows, "type").Count == 3, "Same-name native type IDs stay separate, missing type identity explicit.");
        Check(CivilWallReport.Filter(data, "", "", "q-A", "s-A").Count == 2, "Attribution filters intersect by ID.");
        Check(CivilWallReport.Filter(data, "", "", CivilWallReport.UnassignedId, "").Count == 2, "Unmapped quantity-level filter.");
        Check(CivilWallReport.Filter(data, "", "", "", CivilWallReport.UnassignedId).Count == 1, "Unassigned segment filter.");
        Check(CivilWallReport.Filter(data, "同名施工段", "calculated", "q-B", "s-B").Single().Source.UniqueId == "wall-B", "Search, status, quantity-level and segment use the same source rows.");
        Check(CivilWallReport.Filter(data, "wall-A", "", "", "").Single().Source.ElementId == "A", "Search includes exact stable source UID.");
        Check(CivilWallReport.Filter(data, "", "failed", "", "").Single().Source.UniqueId == "wall-D", "Failure is not silently excluded.");
        CivilWallReportData noProject = CivilWallReport.Create(batch);
        Check(Object.ReferenceEquals(noProject.Snapshot, original) && CivilWallReport.Filter(noProject, "", "", CivilWallReport.UnassignedId, "").Count == 5, "No project is allowed, with explicit unassigned reporting rather than inferred names.");
        List<CivilWallReportRow> onlyA = CivilWallReport.Filter(data, "wall-A", "", "", "");
        string text = CivilWallReport.ToCsv(data, onlyA, "quantity-level"); List<List<string>> csv = Csv(text);
        Check(text.Contains(CivilWallReport.SchemaId) && text.Contains(CivilWallCalculator.AreaRuleId) && text.Contains("wall-A") && text.Contains("A-opening-uid") && !text.Contains("wall-B") && !text.Contains("wall-E"), "V2 CSV carries only selected wall and its own complete ledger.");
        Check(csv.Any(row => row.Count == 2 && row[0] == "计量工程Id" && row[1] == profile.ProjectId) && csv.Any(row => row.Count == 2 && row[0] == "模型修订" && row[1] == "9") && text.Contains("view-uid"), "CSV preserves project, revision and explicit geometry context.");
        List<string> wallHeader = csv.Single(row => row.Count == CivilWallReport.WallColumnCount && row[0] == "墙ElementId");
        List<string> wallCsv = csv.Single(row => row.Count == CivilWallReport.WallColumnCount && row[0] == "A");
        Check(wallHeader.Count == 38 && wallCsv[wallHeader.IndexOf("净单侧投影面积m2")] == "28" && wallCsv[wallHeader.IndexOf("项目试算体积m3")] == "5.6", "CSV area and volume match independently known 28 / 5.6 values.");
        Check(wallCsv[wallHeader.IndexOf("Revit原生面积m2")] == "123" && wallCsv[wallHeader.IndexOf("Revit原生体积m3")] == "456" && text.Contains("HOST_AREA_COMPUTED") && text.Contains("HOST_VOLUME_COMPUTED"), "Native metrics remain separate source references.");
        Check(csv.Single(row => row.Count == CivilWallReport.GroupColumnCount && row[0] == "total")[3] == "1", "CSV total coverage equals selected source count.");
        List<string> ledgerHeader = csv.Single(row => row.Count == CivilWallReport.LedgerColumnCount && row[0] == "墙ElementId");
        Check(ledgerHeader.Count == 14 && csv.Where(row => row.Count == CivilWallReport.LedgerColumnCount && row[0] == "A").Sum(row => Double.Parse(row[6], CultureInfo.InvariantCulture)) == 28, "Signed-area ledger CSV conserves net projection independently.");
        Check(text.Contains("\"'@model\"") && text.Contains("不是抹灰、模板") && text.Contains("不执行几何切层或切段"), "CSV protects formula metadata and explicitly states measurement/attribution limits.");
        File.WriteAllText(Path.Combine(output, "wall-report-v2.csv"), text, new System.Text.UTF8Encoding(true));
        string emptyCsv = CivilWallReport.ToCsv(data, new CivilWallReportRow[0], "segment");
        List<string> emptyTotal = Csv(emptyCsv).Single(row => row.Count == CivilWallReport.GroupColumnCount && row[0] == "total");
        Check(emptyTotal[3] == "0" && emptyTotal[4] == "0" && emptyTotal[7] == "" && emptyTotal[10] == "", "Empty selected range exports zero coverage with blank quantities.");
        string failedCsv = CivilWallReport.ToCsv(data, data.Rows.Where(row => row.Result.Status != "calculated"), "quantity-level");
        List<string> failedTotal = Csv(failedCsv).Single(row => row.Count == CivilWallReport.GroupColumnCount && row[0] == "total");
        Check(failedTotal[3] == "2" && failedTotal[4] == "0" && failedTotal[5] == "1" && failedTotal[6] == "1" && failedTotal[9] == "", "All-refused CSV preserves exact coverage without fake quantities.");
        string zeroCsv = CivilWallReport.ToCsv(data, data.Rows.Where(row => row.Source.ElementId == "E"), "type");
        List<string> zeroTotal = Csv(zeroCsv).Single(row => row.Count == CivilWallReport.GroupColumnCount && row[0] == "total");
        Check(zeroTotal[9] == "0" && zeroTotal[10] == "0", "Legitimate zero remains numeric zero in CSV.");
        CivilWallCalculationBatch hostile = Batch(); hostile.Input.NativeSnapshot.ModelTitle = " \t=metadata"; hostile.Results[0].Fact.Name = "=CMD(\"quoted\")\nnew line";
        string hostileText = CivilWallReport.ToCsv(CivilWallReport.Create(hostile), null, "level"); List<List<string>> hostileCsv = Csv(hostileText);
        Check(hostileCsv.Any(row => row.Count == 2 && row[0] == "模型" && row[1].StartsWith("'", StringComparison.Ordinal)) && hostileCsv.Any(row => row.Count == 38 && row[0] == "A" && row[2].StartsWith("'=CMD", StringComparison.Ordinal) && row[2].Contains("\n")), "Quoted/multiline formulas stay inert text while valid data structure is retained.");
        Refuses(delegate { CivilWallReport.Group(new[] { data.Rows[0], data.Rows[0] }, "type"); }, "Duplicate wall rows cannot inflate group totals.");
        Refuses(delegate { CivilWallReport.ToCsv(data, new[] { data.Rows[0], data.Rows[0] }, "level"); }, "Duplicate selected rows are rejected before CSV.");
        Refuses(delegate { CivilWallReport.ToCsv(data, new[] { CivilWallReport.Create(Batch()).Rows[0] }, "level"); }, "Foreign row from another report is rejected even with same identities.");
        CivilWallReportData foreignData = CivilWallReport.Create(Batch());
        Refuses(delegate { CivilWallReport.Group(new[] { data.Rows[0], foreignData.Rows[1] }, "level"); }, "Distinct identities from different report owners still cannot be combined.");
        CivilWallReportData replacedData = CivilWallReport.Create(Batch()); replacedData.Rows[0] = foreignData.Rows[0];
        Refuses(delegate { CivilWallReport.Filter(replacedData, "", "", "", ""); }, "Replacing a report row with an external row cannot silently alter filter/coverage.");
        Refuses(delegate { CivilWallReport.Group(new CivilWallReportRow[0], "anything"); }, "Unknown group dimension rejects even for empty data.");
        Refuses(delegate { CivilWallReport.Filter(data, "", "unknown", "", ""); }, "Unknown result-status filter is rejected.");
        CivilProjectSettings wrong = CivilProject.Clone(profile); wrong.SourceDocumentScope = "other-doc";
        Refuses(delegate { CivilWallReport.Create(batch, wrong); }, "Different source document project cannot annotate the batch.");
        wrong = CivilProject.Clone(profile); wrong.SourcePath = "H:\\fixture\\copied-model.rvt";
        Refuses(delegate { CivilWallReport.Create(batch, wrong); }, "Copied RVT path cannot silently borrow original project attribution.");
        wrong = CivilProject.Clone(profile); wrong.QuantityLevels[0].Id = CivilWallReport.UnassignedId; wrong.LevelMappings[0].QuantityLevelId = CivilWallReport.UnassignedId;
        Refuses(delegate { CivilWallReport.Create(batch, wrong); }, "Reserved unassigned filter value cannot collide with a real quantity-level ID.");
        CivilWallCalculationBatch invalid = Batch(); invalid.Input.NativeSnapshot.Elements.RemoveAt(0);
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Missing native wall source is refused.");
        invalid = Batch(); invalid.Input.NativeSnapshot.Elements[0].ElementId = "different";
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Same UniqueId with different ElementId is refused.");
        invalid = Batch(); invalid.Input.NativeSnapshot.Elements[0].KindCode = "floor";
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Non-wall record cannot be used as wall quantity source.");
        invalid = Batch(); invalid.Input.NativeSnapshot.Elements.Add(invalid.Input.NativeSnapshot.Elements[0]);
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Duplicate native UID/ID is refused rather than silently deduplicated.");
        invalid = Batch(); invalid.Input.NativeSnapshot.Elements[1].ElementId = invalid.Input.NativeSnapshot.Elements[0].ElementId;
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Distinct native UIDs cannot share an ElementId.");
        invalid = Batch(); invalid.Input.NativeSnapshot.Elements.Add(null);
        Refuses(delegate { CivilWallReport.Create(invalid, profile); }, "Null native source cannot be filtered away by project copy.");
        invalid = Batch(); invalid.Results.RemoveAt(0);
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Partial result coverage cannot masquerade as complete input.");
        invalid = Batch(); invalid.Results.RemoveAt(2); invalid.Input.Walls.RemoveAt(2);
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Matching reduced fact/result lists cannot hide an omitted native unsupported wall.");
        invalid = Batch(); invalid.Input.NativeSnapshot.Elements.Add(new CivilElementRecord { ElementId = "outside-wall", UniqueId = "outside-wall-uid", KindCode = "wall" });
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Extra native wall without any fact/result is explicitly rejected.");
        invalid = Batch(); invalid.Results[0].Fact = Batch().Input.Walls[0];
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Fact reference from another batch is refused even when identity strings match.");
        invalid = Batch(); invalid.Results[2].ResultAreaMetres2 = 0;
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Unsupported result with a fake zero quantity is refused.");
        invalid = Batch(); invalid.Results[0].ResultAreaMetres2 = Double.NaN;
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Nonfinite passed projection cannot enter groups or CSV.");
        invalid = Batch(); invalid.Results[0].ResultVolumeMetres3 = null;
        Refuses(delegate { CivilWallReport.Create(invalid); }, "Incomplete passed volume cannot enter area-only sums.");
        CivilWallReportData overflow = CivilWallReport.Create(Batch()); overflow.Rows[0].Result.BasisAreaMetres2 = Double.MaxValue; overflow.Rows[1].Result.BasisAreaMetres2 = Double.MaxValue;
        Refuses(delegate { CivilWallReport.ToCsv(overflow, null, "type"); }, "Cross-group grand total overflow refuses rather than exporting an incomplete CSV.");
        CivilWallReportData stale = CivilWallReport.Create(Batch()); stale.Batch.Input.NativeSnapshot.Revision++;
        Refuses(delegate { CivilWallReport.Filter(stale, "", "", "", ""); }, "Changed native revision invalidates report access.");
        stale = CivilWallReport.Create(Batch(), profile); stale.Rows[0].Source.ElementId = "changed";
        Refuses(delegate { CivilWallReport.ToCsv(stale, null, "level"); }, "Changed applied source identity invalidates exported report rows.");
        stale = CivilWallReport.Create(Batch()); stale.Batch.Results.RemoveAt(0);
        Refuses(delegate { CivilWallReport.ToCsv(stale, null, "level"); }, "Changed batch coverage invalidates report even if source revision was not bumped.");
        stale = CivilWallReport.Create(Batch()); stale.Snapshot.Elements.Add(new CivilElementRecord { ElementId = "new-wall", UniqueId = "new-wall-uid", KindCode = "wall" });
        Refuses(delegate { CivilWallReport.Filter(stale, "", "", "", ""); }, "Added native wall without matching report row invalidates stale coverage.");
        CivilWallCalculationBatch mixedKinds = Batch(); mixedKinds.Input.NativeSnapshot.Elements.Add(new CivilElementRecord { ElementId = "floor-id", UniqueId = "floor-uid", KindCode = "floor" });
        Check(CivilWallReport.Create(mixedKinds).Rows.Count == 5, "Other native categories are permitted without inventing wall results for them.");
        File.WriteAllText(Path.Combine(output, "report.json"), "{\"result\":\"PASS\",\"assertions\":" + checks + ",\"schema\":\"" + CivilWallReport.SchemaId + "\",\"scope\":\"Own pure managed report/domain DLL; synthetic source identities, project annotations, projection/volume grouping and v2 CSV; no Revit, native window or child fixture executable\"}");
        Console.WriteLine("PASS " + checks + " wall report assertions."); return 0;
    }
}
