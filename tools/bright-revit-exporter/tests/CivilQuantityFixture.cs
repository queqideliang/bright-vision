using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using BrightVision.Civil;

internal static class CivilQuantityFixture
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static CivilMetric Available(double value, string unit) { return new CivilMetric { Status = "available", Value = value, Unit = unit, Source = "FIXED_INSTANCE_PARAMETER" }; }
    private static CivilMetric State(string status, string unit) { return new CivilMetric { Status = status, Unit = unit, Source = "FIXED_INSTANCE_PARAMETER" }; }
    private static CivilElementRecord Item(string id, string kind, string level, string levelName, double volume)
    {
        return new CivilElementRecord {
            ElementId = id, UniqueId = "unique-" + id, Name = "构件 " + id, KindCode = kind, KindName = kind,
            Category = "Revit-" + kind, Family = "Family A", TypeName = "Type B", LevelId = level, LevelName = levelName,
            PhaseStatus = "Existing", DesignOptionName = "Main Model", Supported = true,
            Length = Available(10, "m"), Area = Available(30, "m2"), Volume = Available(volume, "m3")
        };
    }
    private static CivilSnapshot Snapshot()
    {
        return new CivilSnapshot { ModelTitle = "测试工程", DocumentScope = "document-a", CapturedAtUtc = "2026-10-28T00:00:00Z",
            ScopeLabel = "当前工程，根文档", PhaseLabel = "Existing", DesignOptionLabel = "Main Model", Revision = 7,
            CandidateCount = 99, LinkInstanceCount = 2, MissingLinkCount = 1 };
    }
    private static List<List<string>> ParseCsv(string csv)
    {
        List<List<string>> rows = new List<List<string>>(); List<string> row = new List<string>(); StringBuilder field = new StringBuilder(); bool quoted = false;
        for (int index = 0; index < csv.Length; index++)
        {
            char ch = csv[index];
            if (ch == '"')
            {
                if (quoted && index + 1 < csv.Length && csv[index + 1] == '"') { field.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (!quoted && ch == ',') { row.Add(field.ToString()); field.Length = 0; }
            else if (!quoted && (ch == '\r' || ch == '\n'))
            {
                if (ch == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n') index++;
                row.Add(field.ToString()); field.Length = 0; rows.Add(row); row = new List<string>();
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        Check(!quoted, "CSV ended within a quoted field."); return rows;
    }
    private static List<List<string>> Table(string csv, out List<string> heading)
    {
        List<List<string>> rows = ParseCsv(csv); int start = rows.FindIndex(row => row.Count > 0 && row[0] == "分组标识");
        Check(start >= 0, "CSV schedule header not found."); heading = rows[start];
        return rows.Skip(start + 1).TakeWhile(row => row.Count > 1).ToList();
    }
    private static double SumPresent(IEnumerable<CivilScheduleRow> rows) { return rows.Where(row => row.Volume.Value.HasValue).Sum(row => row.Volume.Value.Value); }

    private static int Main()
    {
        try
        {
            CivilElementRecord empty = new CivilElementRecord(); CivilSnapshot snapshot = Snapshot();
            Check(empty.Issues != null && snapshot.Elements != null && snapshot.Exclusions != null && snapshot.Notices != null, "Default lists must be initialized.");
            Check(empty.Length.Status == "missing" && !empty.Length.Value.HasValue && empty.Length.Unit == "m", "Default metric fabricated zero or lost its unit.");
            Check(new CivilChoice { Label = "一层" }.ToString() == "一层" && new CivilChoice { Label = null }.ToString() == String.Empty, "Choice display contract failed.");
            Check(new CivilReadRequest().Scope == "view" && new CivilReadRequest().DesignOptionId == "-1", "Read request defaults failed.");

            CivilElementRecord wall1 = Item("W1", "wall", "L1", "一层", 1.25);
            CivilElementRecord wall2 = Item("W2", "wall", "L2", "一层", 2.75); wall2.Length = Available(20, "m"); wall2.Area = Available(60, "m2");
            CivilElementRecord beam = Item("B1", "beam", "L1", "一层", 3); beam.Length = Available(5, "m"); beam.Area = State("not-applicable", "m2");
            CivilElementRecord column = Item("C1", "structural-column", "L2", "一层", 4); column.Length = State("not-applicable", "m"); column.Area = State("not-applicable", "m2");
            CivilElementRecord floor = Item("F1", "floor", "L1", "一层", 5); floor.Length = State("not-applicable", "m"); floor.Area = Available(100, "m2");
            CivilElementRecord brace = Item("R1", "review", String.Empty, String.Empty, 999); brace.Supported = false; brace.ExclusionReason = "支撑不按梁计量";
            CivilElementRecord excluded = Item("E1", "wall", "L1", "一层", 888); excluded.ExclusionReason = "明确排除";
            List<CivilElementRecord> records = new List<CivilElementRecord> { wall1, wall2, beam, column, floor, brace, excluded };
            CivilScheduleRow total = CivilSchedule.Summarize(records);
            Check(total.Count == 7 && total.SupportedCount == 5, "Review/excluded record coverage was hidden.");
            Check(total.Length.Value == 35 && total.Area.Value == 190 && total.Volume.Value == 16, "Excluded or unsupported data entered a total.");
            Check(total.Length.Available == 3 && total.Length.NotApplicable == 2 && total.Length.Missing == 0, "Not-applicable length coverage was mixed with missing or zero.");
            Check(total.Area.Available == 3 && total.Area.NotApplicable == 2 && total.Volume.Available == 5, "Metric dimensions lost independent coverage.");
            Check(CivilSchedule.Summarize(new[] { brace }).Volume.Available == 0 && !CivilSchedule.Summarize(new[] { brace }).Volume.Value.HasValue, "Review records fabricated a total.");
            Check(CivilSchedule.BuildRows(records, "element").Count == 7, "Element mode dropped records.");
            List<CivilScheduleRow> kinds = CivilSchedule.BuildRows(records, "kind");
            Check(kinds.Count == 5 && kinds.Sum(row => row.Count) == 7 && SumPresent(kinds) == total.Volume.Value, "Kind summaries differ from detail scope.");
            Check(kinds.First(row => row.Elements[0].KindCode == "wall").SupportedCount == 2, "Excluded wall still contributed support coverage.");
            List<CivilScheduleRow> levels = CivilSchedule.BuildRows(records, "level");
            List<CivilScheduleRow> wallLevels = levels.Where(row => row.Elements[0].KindCode == "wall").ToList();
            Check(wallLevels.Count == 2 && wallLevels[0].Key != wallLevels[1].Key, "Same-name levels with different IDs were merged.");
            Check(SumPresent(levels) == total.Volume.Value && levels.Sum(row => row.Count) == records.Count, "Level+kind grouping duplicated or lost quantities.");

            Check(CivilSchedule.Filter(records, "unique-w1", "", "", false).Single() == wall1, "Case-insensitive UniqueId search failed.");
            Check(CivilSchedule.Filter(records, "family a", "beam", "L1", false).Single() == beam, "Combined family/kind/level filtering failed.");
            Check(CivilSchedule.Filter(records, "TYPE B", "wall", "L2", false).Single() == wall2, "Type/level filtering failed.");
            Check(CivilSchedule.Filter(records, "支撑", "", "", false).Single() == brace, "Review reason is not searchable.");
            Check(CivilSchedule.Filter(records, "Main Model", "", "", false).Count == 7, "Recorded design option is not searchable.");
            Check(CivilSchedule.Filter(records, "", "", "", true).Count == 2, "Problem filter lost exclusions or treated NA as a missing issue.");
            Check(CivilSchedule.Filter(records, "", "wall", "L1", true).Single() == excluded, "Problem filters do not share the schedule scope.");
            Check(CivilSchedule.Filter(records, "not-present", "", "", false).Count == 0 && CivilSchedule.Filter(null, "", "", "", false).Count == 0, "Empty filters created objects.");
            CivilElementRecord missingLevel = Item("ML", "wall", "-1", "", 1);
            Check(CivilSchedule.Filter(new[] { missingLevel }, "", "", "", true).Count == 1, "Missing original level did not become a visible issue.");
            Check(CivilSchedule.BuildRows(new[] { missingLevel }, "level")[0].LevelName == "未指定楼层", "Invalid level ID acquired an invented floor.");
            CivilElementRecord noIdentity1 = Item("", "wall", "L1", "一层", 1), noIdentity2 = Item("", "wall", "L1", "一层", 2);
            noIdentity1.UniqueId = noIdentity2.UniqueId = String.Empty;
            List<CivilScheduleRow> identityRows = CivilSchedule.BuildRows(new[] { noIdentity1, noIdentity2 }, "element");
            Check(identityRows.Count == 2 && identityRows[0].Key != identityRows[1].Key, "Missing source identities were merged.");
            Check(CivilSchedule.Filter(new[] { noIdentity1, noIdentity2 }, "", "", "", true).Count == 2, "Missing source provenance was hidden.");
            bool badMode = false; try { CivilSchedule.BuildRows(records, "other"); } catch (ArgumentException) { badMode = true; }
            Check(badMode, "Unknown grouping mode silently selected another scope.");

            List<CivilElementRecord> states = new List<CivilElementRecord>();
            foreach (string status in new[] { "available", "missing", "invalid", "unavailable", "not-applicable" })
            {
                CivilElementRecord item = Item(status, "wall", "L1", "一层", 0);
                item.Volume = status == "available" ? Available(0, "m3") : State(status, "m3"); states.Add(item);
            }
            CivilMetricSummary summary = CivilSchedule.Summarize(states).Volume;
            Check(summary.Value == 0 && summary.Available == 1 && summary.Missing == 1 && summary.Invalid == 1 && summary.Unavailable == 1 && summary.NotApplicable == 1, "Quantity statuses lost their separate coverage.");
            Check(!CivilSchedule.Summarize(states.Skip(1)).Volume.Value.HasValue, "All unavailable states fabricated a zero.");
            Check(CivilSchedule.Filter(states, "", "", "", true).Count == 3, "Issue filter misclassified valid zero or not-applicable state.");
            foreach (double invalid in new[] { -1, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
            {
                CivilElementRecord item = Item("invalid", "wall", "L1", "一层", invalid);
                CivilMetricSummary rejected = CivilSchedule.Summarize(new[] { item }).Volume;
                Check(rejected.Invalid == 1 && rejected.Available == 0 && !rejected.Value.HasValue, "An invalid available number entered a total.");
            }
            foreach (string status in new[] { "missing", "not-applicable", "unavailable", "unknown" })
            {
                CivilElementRecord item = Item("bad-state", "wall", "L1", "一层", 9); item.Volume.Status = status;
                Check(CivilSchedule.Summarize(new[] { item }).Volume.Invalid == 1, "Status/value inconsistency entered coverage as a valid state.");
            }
            CivilElementRecord wrongUnit = Item("wrong-unit", "wall", "L1", "一层", 1); wrongUnit.Volume.Unit = "ft3";
            Check(CivilSchedule.Summarize(new[] { wrongUnit }).Volume.Invalid == 1 && CivilSchedule.Filter(new[] { wrongUnit }, "", "", "", true).Count == 1, "Unconverted units entered an SI total.");
            CivilElementRecord nullMetric = Item("null", "wall", "L1", "一层", 1); nullMetric.Volume = null;
            Check(CivilSchedule.Summarize(new[] { nullMetric }).Volume.Missing == 1, "Null metric did not stay missing.");
            Check(CivilSchedule.Summarize(new CivilElementRecord[0]).Count == 0 && !CivilSchedule.Summarize(null).Volume.Value.HasValue, "An empty range fabricated a total.");
            CivilElementRecord huge1 = Item("huge1", "wall", "L1", "一层", Double.MaxValue), huge2 = Item("huge2", "wall", "L1", "一层", Double.MaxValue);
            CivilMetricSummary overflow = CivilSchedule.Summarize(new[] { huge1, huge2 }).Volume;
            Check(overflow.Overflow && !overflow.Value.HasValue && overflow.Available == 2, "Sum overflow was serialized as a finite number.");
            Check(CivilSchedule.Summarize(new[] { Item("large", "wall", "L1", "一层", 1e16), Item("small1", "wall", "L1", "一层", 1), Item("small2", "wall", "L1", "一层", 1) }).Volume.Value == 1e16 + 2, "Small contributions disappeared during summation.");
            Check(brace.Issues.Count == 0 && wall1.Volume.Value == 1.25 && missingLevel.LevelId == "-1", "Pure schedule functions mutated the original data.");

            snapshot.Exclusions.Add(new CivilExclusion { ElementId = "outside", Name = "被排除对象", Reason = "不在读取范围" }); snapshot.Notices.Add("实际范围提示");
            List<string> heading; string csv = CivilSchedule.ToCsv(snapshot, records, "element"); List<List<string>> table = Table(csv, out heading);
            Check(csv.Length > 0 && csv[0] != '\ufeff', "Domain CSV must leave BOM ownership to its file writer.");
            Check(table.Count == records.Count, "CSV and displayed detail have different scopes.");
            int volumeColumn = heading.IndexOf("原生体积(m3)"), sourceColumn = heading.IndexOf("体积来源与状态");
            Check(table.Where(row => row[5] == "0").All(row => row[volumeColumn] == "NA"), "Unsupported/excluded CSV values were not explicitly NA.");
            Check(table.Where(row => row[5] == "0").All(row => !String.IsNullOrWhiteSpace(row[9])), "Unsupported/excluded CSV rows lost their reason.");
            Check(table.Where(row => row[volumeColumn] != "NA" && row[volumeColumn] != "").Sum(row => Double.Parse(row[volumeColumn], CultureInfo.InvariantCulture)) == 16, "CSV detail does not sum to the same schedule total.");
            Check(table[0][7] == "W1" && table[0][8] == "unique-W1" && table[0][sourceColumn].Contains("FIXED_INSTANCE_PARAMETER"), "CSV lost source IDs or parameter provenance.");
            Check(csv.Contains("document-a") && csv.Contains("Existing") && csv.Contains("Main Model") && csv.Contains("实际范围提示"), "CSV lost scope/phase/option/read metadata.");
            List<CivilElementRecord> filtered = CivilSchedule.Filter(records, "", "wall", "", false);
            List<List<string>> grouped = Table(CivilSchedule.ToCsv(snapshot, filtered, "kind"), out heading);
            Check(grouped.Count == 1 && grouped[0][4] == "3" && grouped[0][5] == "2" && grouped[0][heading.IndexOf("原生体积(m3)")] == "4", "Filtered grouped CSV differs from filtered detail scope.");
            Check(CivilSchedule.Summarize(filtered).Volume.Value == 4, "Filtered summary includes other categories.");
            CivilElementRecord na = Item("NA", "wall", "L1", "一层", 1); na.Length = State("not-applicable", "m");
            List<List<string>> naTable = Table(CivilSchedule.ToCsv(snapshot, new[] { na }, "element"), out heading);
            Check(naTable[0][heading.IndexOf("原生长度(m)")] == "NA" && naTable[0][heading.IndexOf("长度不适用数")] == "1", "Not-applicable CSV state was rendered as zero or missing.");

            CivilElementRecord unsafeItem = Item("-42", "wall", "L1", "一层", 0.123456789012345);
            unsafeItem.Name = "=SUM(1,2)"; unsafeItem.UniqueId = "uid,\"A\"\n第二行";
            snapshot.ModelTitle = "=MODEL()"; snapshot.ScopeLabel = "\ufeff +COMMAND()";
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                string protectedCsv = CivilSchedule.ToCsv(snapshot, new[] { unsafeItem }, "element");
                List<List<string>> protectedTable = Table(protectedCsv, out heading);
                Check(protectedTable[0][1] == "'=SUM(1,2)" && protectedTable[0][7] == "'-42", "Formula-shaped user text was not protected.");
                Check(protectedTable[0][8] == unsafeItem.UniqueId, "CSV failed to preserve quoted commas, quotes or newlines.");
                Check(protectedCsv.Contains("\"'=MODEL()\"") && protectedCsv.Contains("\"'\ufeff +COMMAND()\""), "Metadata formula injection was not protected.");
                Check(Double.Parse(protectedTable[0][heading.IndexOf("原生体积(m3)")], CultureInfo.InvariantCulture) == unsafeItem.Volume.Value, "CSV rounded or localized the raw number.");
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
            List<List<string>> emptyTable = Table(CivilSchedule.ToCsv(snapshot, new CivilElementRecord[0], "element"), out heading);
            Check(emptyTable.Count == 0, "Empty current scope exported unrelated records.");
            Console.WriteLine("PASS: " + checks.ToString(CultureInfo.InvariantCulture) + " civil quantity assertions; pure C# domain, no Revit host.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
    }
}
