using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BrightVision.Civil
{
    internal sealed class CivilChoice
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public CivilChoice() { Id = Label = String.Empty; }
        public override string ToString() { return Label ?? String.Empty; }
    }

    internal sealed class CivilReadRequest
    {
        public string Scope { get; set; }
        public string PhaseId { get; set; }
        public string DesignOptionId { get; set; }
        public CivilReadRequest() { Scope = "view"; PhaseId = String.Empty; DesignOptionId = "-1"; }
    }

    internal sealed class CivilMetric
    {
        public string Status { get; set; }
        public double? Value { get; set; }
        public string Unit { get; set; }
        public string Source { get; set; }
        public CivilMetric() { Status = "missing"; Unit = Source = String.Empty; }
    }

    internal sealed class CivilElementRecord
    {
        public string ElementId { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string KindCode { get; set; }
        public string KindName { get; set; }
        public string Family { get; set; }
        public string TypeName { get; set; }
        public string LevelId { get; set; }
        public string LevelName { get; set; }
        public string RawLevelId { get; set; }
        public string RawLevelName { get; set; }
        public string LevelUniqueId { get; set; }
        public string LevelSource { get; set; }
        public string QuantityLevelId { get; set; }
        public string QuantityLevelName { get; set; }
        public string SegmentId { get; set; }
        public string SegmentName { get; set; }
        public string PhaseStatus { get; set; }
        public string DesignOptionName { get; set; }
        public string ExclusionReason { get; set; }
        public bool Supported { get; set; }
        public List<string> Issues { get; set; }
        public List<string> ProjectIssues { get; set; }
        public CivilMetric Length { get; set; }
        public CivilMetric Area { get; set; }
        public CivilMetric Volume { get; set; }
        public CivilElementRecord()
        {
            ElementId = UniqueId = Name = Category = KindCode = KindName = Family = TypeName = LevelId = LevelName = PhaseStatus = DesignOptionName = ExclusionReason = String.Empty;
            RawLevelId = RawLevelName = LevelUniqueId = LevelSource = QuantityLevelId = QuantityLevelName = SegmentId = SegmentName = String.Empty;
            Issues = new List<string>();
            ProjectIssues = new List<string>();
            Length = new CivilMetric { Unit = "m" };
            Area = new CivilMetric { Unit = "m2" };
            Volume = new CivilMetric { Unit = "m3" };
        }
    }

    internal sealed class CivilExclusion
    {
        public string ElementId { get; set; }
        public string Name { get; set; }
        public string Reason { get; set; }
        public CivilExclusion() { ElementId = Name = Reason = String.Empty; }
    }

    internal sealed class CivilSnapshot
    {
        public List<CivilElementRecord> Elements { get; set; }
        public List<CivilExclusion> Exclusions { get; set; }
        public List<string> Notices { get; set; }
        public List<string> ProjectNotices { get; set; }
        public string ModelTitle { get; set; }
        public string DocumentScope { get; set; }
        public string SourcePath { get; set; }
        public List<CivilLevelInfo> Levels { get; set; }
        public string ProjectId { get; set; }
        public string ProjectName { get; set; }
        public int ProjectRevision { get; set; }
        public string CapturedAtUtc { get; set; }
        public string ScopeLabel { get; set; }
        public string PhaseLabel { get; set; }
        public string DesignOptionLabel { get; set; }
        public int CandidateCount { get; set; }
        public int LinkInstanceCount { get; set; }
        public int MissingLinkCount { get; set; }
        public long Revision { get; set; }
        public CivilSnapshot()
        {
            Elements = new List<CivilElementRecord>(); Exclusions = new List<CivilExclusion>(); Notices = new List<string>();
            ModelTitle = DocumentScope = CapturedAtUtc = ScopeLabel = PhaseLabel = DesignOptionLabel = String.Empty;
            SourcePath = ProjectId = ProjectName = String.Empty;
            Levels = new List<CivilLevelInfo>();
            ProjectNotices = new List<string>();
        }
    }

    internal sealed class CivilLevelInfo
    {
        public string Id { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public double? ElevationMetres { get; set; }
        public CivilLevelInfo() { Id = UniqueId = Name = String.Empty; }
    }

    internal sealed class CivilMetricSummary
    {
        public double? Value { get; set; }
        public int Available { get; set; }
        public int Missing { get; set; }
        public int Invalid { get; set; }
        public int Unavailable { get; set; }
        public int NotApplicable { get; set; }
        public bool Overflow { get; set; }
    }

    internal sealed class CivilScheduleRow
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string KindName { get; set; }
        public string LevelName { get; set; }
        public string QuantityLevelName { get; set; }
        public string SegmentName { get; set; }
        public string Issues { get; set; }
        public List<CivilElementRecord> Elements { get; set; }
        public int Count { get; set; }
        public int SupportedCount { get; set; }
        public CivilMetricSummary Length { get; set; }
        public CivilMetricSummary Area { get; set; }
        public CivilMetricSummary Volume { get; set; }
        public CivilScheduleRow()
        {
            Key = Label = KindName = LevelName = Issues = String.Empty;
            QuantityLevelName = SegmentName = String.Empty;
            Elements = new List<CivilElementRecord>();
            Length = new CivilMetricSummary(); Area = new CivilMetricSummary(); Volume = new CivilMetricSummary();
        }
    }

    internal static class CivilSchedule
    {
        private static readonly HashSet<string> SupportedKinds = new HashSet<string>(new[] { "wall", "beam", "structural-column", "architectural-column", "floor" }, StringComparer.OrdinalIgnoreCase);

        public static List<CivilElementRecord> Filter(IEnumerable<CivilElementRecord> elements, string search, string kindCode, string levelId, bool issuesOnly)
        {
            string query = (search ?? String.Empty).Trim();
            return Records(elements).Where(element =>
                (String.IsNullOrWhiteSpace(kindCode) || String.Equals(element.KindCode, kindCode, StringComparison.OrdinalIgnoreCase)) &&
                (String.IsNullOrWhiteSpace(levelId) || String.Equals(element.LevelId, levelId, StringComparison.Ordinal)) &&
                (!issuesOnly || ElementIssues(element).Count > 0) &&
                (query.Length == 0 || SearchText(element).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        public static List<CivilElementRecord> FilterProject(IEnumerable<CivilElementRecord> elements, string search, string kindCode, string levelId, bool issuesOnly, string quantityLevelId, string segmentId)
        {
            return Filter(elements, search, kindCode, levelId, issuesOnly).Where(element =>
                (String.IsNullOrEmpty(quantityLevelId) || element.QuantityLevelId == quantityLevelId) &&
                (String.IsNullOrEmpty(segmentId) || element.SegmentId == segmentId)).ToList();
        }

        public static List<CivilScheduleRow> BuildRows(IEnumerable<CivilElementRecord> elements, string groupMode)
        {
            List<CivilElementRecord> records = Records(elements);
            string mode = Mode(groupMode);
            List<CivilScheduleRow> result = new List<CivilScheduleRow>();
            if (mode == "element")
            {
                for (int index = 0; index < records.Count; index++)
                {
                    CivilElementRecord element = records[index];
                    string label = !String.IsNullOrWhiteSpace(element.Name) ? element.Name : !String.IsNullOrWhiteSpace(element.ElementId) ? "构件 " + element.ElementId : "未命名构件";
                    // Never merge records with missing identities or silently discard duplicate input records.
                    result.Add(Row("element:" + Part(element.UniqueId) + Part(element.ElementId) + ":" + index.ToString(CultureInfo.InvariantCulture), label, new List<CivilElementRecord> { element }));
                }
                return result;
            }
            Dictionary<string, List<CivilElementRecord>> groups = new Dictionary<string, List<CivilElementRecord>>(StringComparer.Ordinal);
            List<string> order = new List<string>();
            foreach (CivilElementRecord element in records)
            {
                string key = mode == "kind" ? "kind:" + Part(KindCode(element)) : mode == "quantity-level" ? "quantity-level:" + Part(element.QuantityLevelId) + Part(KindCode(element))
                    : mode == "segment" ? "segment:" + Part(element.SegmentId) + Part(KindCode(element)) : "level:" + Part(ValidLevelId(element.LevelId)) + Part(KindCode(element));
                List<CivilElementRecord> group;
                if (!groups.TryGetValue(key, out group)) { group = new List<CivilElementRecord>(); groups.Add(key, group); order.Add(key); }
                group.Add(element);
            }
            foreach (string key in order)
            {
                CivilElementRecord first = groups[key][0];
                string label = mode == "kind" ? KindName(first) : (mode == "quantity-level" ? QuantityLevelName(first) : mode == "segment" ? SegmentName(first) : LevelName(first)) + " · " + KindName(first);
                result.Add(Row(key, label, groups[key]));
            }
            return result;
        }

        public static CivilScheduleRow Summarize(IEnumerable<CivilElementRecord> elements)
        {
            return Row("total", "合计", Records(elements));
        }

        public static string ToCsv(CivilSnapshot snapshot, IEnumerable<CivilElementRecord> elements, string groupMode)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            string mode = Mode(groupMode);
            List<CivilElementRecord> records = Records(elements);
            CivilScheduleRow total = Summarize(records);
            List<CivilScheduleRow> rows = BuildRows(records, mode);
            StringBuilder csv = new StringBuilder();
            CsvLine(csv, "模型", snapshot.ModelTitle);
            CsvLine(csv, "来源文档标识", snapshot.DocumentScope);
            CsvLine(csv, "读取时间（UTC）", snapshot.CapturedAtUtc);
            CsvLine(csv, "读取范围", snapshot.ScopeLabel);
            CsvLine(csv, "阶段", snapshot.PhaseLabel);
            CsvLine(csv, "设计选项", snapshot.DesignOptionLabel);
            CsvLine(csv, "读取修订号", snapshot.Revision.ToString(CultureInfo.InvariantCulture));
            CsvLine(csv, "来源", "Revit 原生实例参数；不代表地区清单、定额、模板量或建筑面积");
            CsvLine(csv, "楼层口径", "前部历史“原始楼层”列为解析后的原生参照楼层；原始 Element.LevelId/Name 见末尾原始字段，参照层解析来源（LevelSource）说明解析依据。");
            CsvLine(csv, "分组", mode == "element" ? "按构件" : mode == "kind" ? "按业务类别" : mode == "quantity-level" ? "按计量层和业务类别" : mode == "segment" ? "按施工段和业务类别" : "按原始楼层和业务类别");
            CsvLine(csv, "导出范围", "当前传入的筛选记录集合；具体来源标识见量表");
            CsvLine(csv, "当前记录数", total.Count.ToString(CultureInfo.InvariantCulture));
            CsvLine(csv, "当前受支持记录数", total.SupportedCount.ToString(CultureInfo.InvariantCulture));
            CsvLine(csv, "原始读取候选数", snapshot.CandidateCount.ToString(CultureInfo.InvariantCulture));
            CsvLine(csv, "原始读取排除记录数", (snapshot.Exclusions == null ? 0 : snapshot.Exclusions.Count).ToString(CultureInfo.InvariantCulture));
            CsvLine(csv, "链接实例数（未纳入本期量表）", snapshot.LinkInstanceCount.ToString(CultureInfo.InvariantCulture));
            CsvLine(csv, "未加载链接数", snapshot.MissingLinkCount.ToString(CultureInfo.InvariantCulture));
            if (snapshot.Notices != null) foreach (string notice in snapshot.Notices) if (!String.IsNullOrWhiteSpace(notice)) CsvLine(csv, "读取提示", notice);
            CsvLine(csv, "来源文件路径", snapshot.SourcePath);
            CsvLine(csv, "计量工程标识", snapshot.ProjectId);
            CsvLine(csv, "计量工程名称", snapshot.ProjectName);
            CsvLine(csv, "计量工程修订号", snapshot.ProjectRevision.ToString(CultureInfo.InvariantCulture));
            if (snapshot.ProjectNotices != null) foreach (string notice in snapshot.ProjectNotices) if (!String.IsNullOrWhiteSpace(notice)) CsvLine(csv, "工程设置提示", notice);
            CsvLine(csv);
            List<string> heading = new List<string> { "分组标识", "构件/分组", "业务类别", "原始楼层", "记录数", "受支持记录数", "未计量记录数", "来源ElementIds", "来源UniqueIds", "问题/未计量原因" };
            foreach (string metric in new[] { "长度", "面积", "体积" })
            {
                heading.Add("原生" + metric + "(" + (metric == "长度" ? "m" : metric == "面积" ? "m2" : "m3") + ")");
                foreach (string suffix in new[] { "有效数", "缺失数", "无效数", "读取失败数", "不适用数", "合计溢出", "来源与状态" }) heading.Add(metric + suffix);
            }
            heading.AddRange(new[] { "计量工程标识", "计量工程名称", "计量工程修订号", "计量层标识", "计量层", "施工段标识", "施工段", "原始Element.LevelId", "原始Element.LevelName", "参照层UniqueId", "参照层解析来源", "工程设置问题" });
            CsvLine(csv, heading.ToArray());
            foreach (CivilScheduleRow row in rows)
            {
                List<string> fields = new List<string> {
                    row.Key, row.Label, row.KindName, row.LevelName, row.Count.ToString(CultureInfo.InvariantCulture), row.SupportedCount.ToString(CultureInfo.InvariantCulture),
                    (row.Count - row.SupportedCount).ToString(CultureInfo.InvariantCulture), String.Join("\n", row.Elements.Select(element => element.ElementId ?? String.Empty)),
                    String.Join("\n", row.Elements.Select(element => element.UniqueId ?? String.Empty)), row.Issues
                };
                AppendMetricCsv(fields, row, row.Length, "length");
                AppendMetricCsv(fields, row, row.Area, "area");
                AppendMetricCsv(fields, row, row.Volume, "volume");
                fields.AddRange(new[] { snapshot.ProjectId, snapshot.ProjectName, snapshot.ProjectRevision.ToString(CultureInfo.InvariantCulture),
                    Join(row, element => element.QuantityLevelId), row.QuantityLevelName, Join(row, element => element.SegmentId), row.SegmentName,
                    Join(row, element => element.RawLevelId), Join(row, element => element.RawLevelName), Join(row, element => element.LevelUniqueId),
                    Join(row, element => element.LevelSource), Join(row, element => String.Join("；", element.ProjectIssues ?? new List<string>())) });
                CsvLine(csv, fields.ToArray());
            }
            if (snapshot.Exclusions != null && snapshot.Exclusions.Count > 0)
            {
                CsvLine(csv); CsvLine(csv, "原始读取范围排除清单（不参与量表合计）");
                CsvLine(csv, "ElementId", "名称", "原因");
                foreach (CivilExclusion exclusion in snapshot.Exclusions)
                    if (exclusion != null) CsvLine(csv, exclusion.ElementId, exclusion.Name, exclusion.Reason);
            }
            return csv.ToString();
        }

        private static List<CivilElementRecord> Records(IEnumerable<CivilElementRecord> elements)
        {
            return elements == null ? new List<CivilElementRecord>() : elements.Where(element => element != null).ToList();
        }

        private static bool Eligible(CivilElementRecord element)
        {
            return element.Supported && SupportedKinds.Contains(element.KindCode ?? String.Empty) && String.IsNullOrWhiteSpace(element.ExclusionReason);
        }

        private static string KindCode(CivilElementRecord element)
        {
            return SupportedKinds.Contains(element.KindCode ?? String.Empty) ? element.KindCode.ToLowerInvariant() : "review";
        }

        private static string KindName(CivilElementRecord element)
        {
            if (!String.IsNullOrWhiteSpace(element.KindName)) return element.KindName;
            switch (KindCode(element))
            {
                case "wall": return "墙"; case "beam": return "梁"; case "structural-column": return "结构柱";
                case "architectural-column": return "建筑柱"; case "floor": return "板"; default: return "待核对";
            }
        }

        private static string ValidLevelId(string id) { return String.IsNullOrWhiteSpace(id) || id == "-1" ? String.Empty : id; }
        private static string LevelName(CivilElementRecord element) { return ValidLevelId(element.LevelId).Length == 0 || String.IsNullOrWhiteSpace(element.LevelName) ? "未指定楼层" : element.LevelName; }
        private static string QuantityLevelName(CivilElementRecord element) { return String.IsNullOrEmpty(element.QuantityLevelId) || String.IsNullOrWhiteSpace(element.QuantityLevelName) ? "未映射计量层" : element.QuantityLevelName; }
        private static string SegmentName(CivilElementRecord element) { return String.IsNullOrEmpty(element.SegmentId) || String.IsNullOrWhiteSpace(element.SegmentName) ? "未分配施工段" : element.SegmentName; }
        private static string Join(CivilScheduleRow row, Func<CivilElementRecord, string> select) { return String.Join("\n", row.Elements.Select(element => select(element) ?? String.Empty)); }
        private static string Part(string value) { value = value ?? String.Empty; return value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value; }
        private static string Mode(string value)
        {
            value = String.IsNullOrWhiteSpace(value) ? "element" : value;
            if (value != "element" && value != "kind" && value != "level" && value != "quantity-level" && value != "segment") throw new ArgumentException("Unknown civil quantity group mode.", "groupMode");
            return value;
        }

        private static CivilMetric Metric(CivilElementRecord element, string key) { return key == "length" ? element.Length : key == "area" ? element.Area : element.Volume; }
        private static string Unit(string key) { return key == "length" ? "m" : key == "area" ? "m2" : "m3"; }
        private static string MetricName(string key) { return key == "length" ? "原生长度" : key == "area" ? "原生面积" : "原生体积"; }
        private static string Status(CivilMetric metric, string key)
        {
            if (metric == null) return "missing";
            if (metric.Status == "available")
                return metric.Value.HasValue && !Double.IsNaN(metric.Value.Value) && !Double.IsInfinity(metric.Value.Value) && metric.Value.Value >= 0 && metric.Unit == Unit(key) ? "available" : "invalid";
            if (metric.Status == "missing" || metric.Status == "unavailable" || metric.Status == "not-applicable") return metric.Value.HasValue ? "invalid" : metric.Status;
            return "invalid";
        }

        private static List<string> ElementIssues(CivilElementRecord element)
        {
            List<string> issues = element.Issues == null ? new List<string>() : element.Issues.Where(issue => !String.IsNullOrWhiteSpace(issue)).ToList();
            if (element.ProjectIssues != null) issues.AddRange(element.ProjectIssues.Where(issue => !String.IsNullOrWhiteSpace(issue)));
            if (!Eligible(element)) issues.Add(!String.IsNullOrWhiteSpace(element.ExclusionReason) ? element.ExclusionReason : "未纳入本期受支持计量范围");
            if (String.IsNullOrWhiteSpace(element.ElementId) || String.IsNullOrWhiteSpace(element.UniqueId)) issues.Add("来源构件标识不完整");
            if (ValidLevelId(element.LevelId).Length == 0 || String.IsNullOrWhiteSpace(element.LevelName)) issues.Add("原始楼层未确定");
            if (Eligible(element)) foreach (string key in new[] { "length", "area", "volume" })
            {
                CivilMetric metric = Metric(element, key); string status = Status(metric, key);
                if (status == "missing") issues.Add(MetricName(key) + "缺失");
                if (status == "invalid") issues.Add(MetricName(key) + "无效");
                if (status == "unavailable") issues.Add(MetricName(key) + "读取失败");
                if (status == "available" && String.IsNullOrWhiteSpace(metric.Source)) issues.Add(MetricName(key) + "来源未记录");
            }
            return issues.Distinct(StringComparer.Ordinal).ToList();
        }

        private static string SearchText(CivilElementRecord element)
        {
            return String.Join("\n", new[] { element.ElementId, element.UniqueId, element.Name, element.Category, element.KindCode, KindName(element),
                element.Family, element.TypeName, element.LevelId, LevelName(element), element.RawLevelId, element.RawLevelName, element.LevelUniqueId, element.LevelSource,
                element.QuantityLevelId, element.QuantityLevelName, element.SegmentId, element.SegmentName, element.PhaseStatus, element.DesignOptionName, String.Join("；", ElementIssues(element)) });
        }

        private static CivilScheduleRow Row(string key, string label, List<CivilElementRecord> elements)
        {
            List<string> kinds = elements.Select(KindName).Distinct(StringComparer.Ordinal).ToList();
            List<string> levels = elements.Select(LevelName).Distinct(StringComparer.Ordinal).ToList();
            List<string> quantityLevels = elements.Select(QuantityLevelName).Distinct(StringComparer.Ordinal).ToList();
            List<string> segments = elements.Select(SegmentName).Distinct(StringComparer.Ordinal).ToList();
            return new CivilScheduleRow {
                Key = key, Label = label, Elements = new List<CivilElementRecord>(elements), Count = elements.Count, SupportedCount = elements.Count(Eligible),
                KindName = kinds.Count == 1 ? kinds[0] : kinds.Count > 1 ? "多个业务类别" : String.Empty,
                LevelName = levels.Count == 1 ? levels[0] : levels.Count > 1 ? "多个原始楼层" : String.Empty,
                QuantityLevelName = quantityLevels.Count == 1 ? quantityLevels[0] : quantityLevels.Count > 1 ? "多个计量层" : String.Empty,
                SegmentName = segments.Count == 1 ? segments[0] : segments.Count > 1 ? "多个施工段" : String.Empty,
                Issues = String.Join("；", elements.SelectMany(ElementIssues).Distinct(StringComparer.Ordinal)),
                Length = SummarizeMetric(elements, "length"), Area = SummarizeMetric(elements, "area"), Volume = SummarizeMetric(elements, "volume")
            };
        }

        private static CivilMetricSummary SummarizeMetric(IEnumerable<CivilElementRecord> elements, string key)
        {
            CivilMetricSummary result = new CivilMetricSummary(); double sum = 0, correction = 0;
            foreach (CivilElementRecord element in elements)
            {
                if (!Eligible(element)) continue;
                CivilMetric metric = Metric(element, key); string status = Status(metric, key);
                switch (status)
                {
                    case "missing": result.Missing++; continue; case "invalid": result.Invalid++; continue;
                    case "unavailable": result.Unavailable++; continue; case "not-applicable": result.NotApplicable++; continue;
                }
                result.Available++;
                double value = metric.Value.Value, next = sum + value;
                if (Double.IsInfinity(next) || Double.IsNaN(next)) { result.Overflow = true; continue; }
                correction += Math.Abs(sum) >= Math.Abs(value) ? (sum - next) + value : (value - next) + sum; sum = next;
            }
            double total = sum + correction;
            if (Double.IsNaN(total) || Double.IsInfinity(total)) result.Overflow = true;
            if (result.Available > 0 && !result.Overflow) result.Value = total == 0 ? 0 : total;
            return result;
        }

        private static void AppendMetricCsv(List<string> fields, CivilScheduleRow row, CivilMetricSummary summary, string key)
        {
            bool notApplicable = row.Count > 0 && (row.SupportedCount == 0 || (summary.Available == 0 && summary.Missing == 0 && summary.Invalid == 0 && summary.Unavailable == 0 && summary.NotApplicable > 0));
            fields.Add(summary.Value.HasValue ? Number(summary.Value.Value) : notApplicable ? "NA" : String.Empty);
            fields.Add(summary.Available.ToString(CultureInfo.InvariantCulture)); fields.Add(summary.Missing.ToString(CultureInfo.InvariantCulture));
            fields.Add(summary.Invalid.ToString(CultureInfo.InvariantCulture)); fields.Add(summary.Unavailable.ToString(CultureInfo.InvariantCulture));
            fields.Add(summary.NotApplicable.ToString(CultureInfo.InvariantCulture)); fields.Add(summary.Overflow ? "是" : String.Empty);
            fields.Add(String.Join("\n", row.Elements.Select(element => {
                CivilMetric metric = Metric(element, key);
                return "ElementId=" + (element.ElementId ?? String.Empty) + "; UniqueId=" + (element.UniqueId ?? String.Empty) + "; "
                    + (!Eligible(element) ? "NA; " + (!String.IsNullOrWhiteSpace(element.ExclusionReason) ? element.ExclusionReason : "未纳入本期计量")
                    : Status(metric, key) + "; unit=" + Unit(key) + "; source=" + (metric == null ? String.Empty : metric.Source ?? String.Empty));
            })));
        }

        private static string Number(double value) { return value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture); }
        private static void CsvLine(StringBuilder csv, params string[] fields)
        {
            for (int index = 0; index < fields.Length; index++)
            {
                if (index > 0) csv.Append(',');
                string value = fields[index] ?? String.Empty;
                int first = 0; while (first < value.Length && (Char.IsWhiteSpace(value[first]) || Char.IsControl(value[first]) || value[first] == '\ufeff')) first++;
                if ((value.Length > 0 && (value[0] == '\t' || value[0] == '\r' || value[0] == '\n')) || (first < value.Length && "=+-@".IndexOf(value[first]) >= 0)) value = "'" + value;
                csv.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
            }
            csv.Append("\r\n");
        }
    }
}
