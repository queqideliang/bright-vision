// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BrightVision.Civil
{
    internal sealed class CivilWallReportData
    {
        public CivilWallCalculationBatch Batch { get; internal set; }
        public CivilSnapshot Snapshot { get; internal set; }
        public List<CivilWallReportRow> Rows { get; internal set; }
        internal string DocumentScope, SourcePath, SnapshotFingerprint;
        internal long SourceRevision;
    }

    internal sealed class CivilWallReportRow
    {
        internal CivilWallReportData Owner;
        public CivilWallResult Result { get; internal set; }
        public CivilElementRecord Source { get; internal set; }
        public string LevelName { get { return Label(Source.LevelName, "未指定原生楼层"); } }
        public string LevelUniqueId { get { return Source.LevelUniqueId ?? String.Empty; } }
        public string QuantityLevelId { get { return Source.QuantityLevelId ?? String.Empty; } }
        public string QuantityLevelName { get { return QuantityLevelId.Length == 0 ? "未映射计量层" : Label(Source.QuantityLevelName, "未命名计量层"); } }
        public string SegmentId { get { return Source.SegmentId ?? String.Empty; } }
        public string SegmentName { get { return SegmentId.Length == 0 ? "未分配施工段" : Label(Source.SegmentName, "未命名施工段"); } }
        public string TypeUniqueId { get { return Result.Fact.TypeUniqueId ?? String.Empty; } }
        public string TypeName { get { return Label(Result.Fact.TypeName, Label(Source.TypeName, "未命名类型")); } }
        private static string Label(string value, string fallback) { return String.IsNullOrWhiteSpace(value) ? fallback : value; }
    }

    internal sealed class CivilWallReportGroup
    {
        public string Key { get; internal set; }
        public string Name { get; internal set; }
        public List<CivilWallReportRow> Rows { get; internal set; }
        public int WallCount { get; internal set; }
        public int CalculatedCount { get; internal set; }
        public int UnsupportedCount { get; internal set; }
        public int FailedCount { get; internal set; }
        public double? BasisAreaMetres2 { get; internal set; }
        public double? OpeningAreaMetres2 { get; internal set; }
        public double? ResultAreaMetres2 { get; internal set; }
        public double? ResultVolumeMetres3 { get; internal set; }
    }

    // Reporting annotations never split geometry or enter a quantity formula.
    // The legacy calculator CSV remains v1; this explicitly identified report
    // adds projection area, project attribution and coverage-aware group totals.
    internal static class CivilWallReport
    {
        public const string SchemaId = "BV.Wall.Report/2";
        public const string UnassignedId = "__unassigned__";
        public const int GroupColumnCount = 11;
        public const int WallColumnCount = 38;
        public const int LedgerColumnCount = 14;

        public static CivilWallReportData Create(CivilWallCalculationBatch batch, CivilProjectSettings project = null)
        {
            CivilWallBatchIntegrity.Validate(batch);
            if (batch == null || batch.Input == null || batch.Input.NativeSnapshot == null || batch.Input.Walls == null || batch.Results == null)
                throw new InvalidDataException("缺少完整的墙体计算批次。");
            CivilSnapshot native = batch.Input.NativeSnapshot;
            if (String.IsNullOrWhiteSpace(native.DocumentScope) || native.Elements == null)
                throw new InvalidDataException("缺少原生来源文档或构件集合。");
            ValidateSources(native.Elements);
            if (batch.Input.Walls.Count != batch.Results.Count)
                throw new InvalidDataException("墙体事实与结果覆盖数不一致；未生成不完整报表。");
            var facts = new Dictionary<string, CivilWallFact>(StringComparer.Ordinal);
            var factIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilWallFact fact in batch.Input.Walls)
            {
                if (fact == null || String.IsNullOrWhiteSpace(fact.UniqueId) || String.IsNullOrWhiteSpace(fact.ElementId) || facts.ContainsKey(fact.UniqueId) || !factIds.Add(fact.ElementId))
                    throw new InvalidDataException("墙体事实的来源身份缺失或重复。");
                facts.Add(fact.UniqueId, fact);
            }
            List<CivilElementRecord> nativeWalls = native.Elements.Where(source => source.KindCode == "wall").ToList();
            if (nativeWalls.Count != facts.Count || nativeWalls.Any(source => !facts.ContainsKey(source.UniqueId) || facts[source.UniqueId].ElementId != source.ElementId))
                throw new InvalidDataException("墙体事实未完整覆盖本次原生墙来源；未静默漏算未支持或读取失败的墙。");
            CivilSnapshot applied = native;
            if (project != null)
            {
                CivilProjectSettings copy = CivilProject.Clone(project);
                if (copy.QuantityLevels.Any(level => level.Id == UnassignedId) || copy.Segments.Any(segment => segment.Id == UnassignedId))
                    throw new InvalidDataException("工程归属标识与报表保留值 __unassigned__ 冲突，请改用其他标识。");
                applied = CivilProject.Apply(native, copy);
            }
            var sources = applied.Elements.ToDictionary(source => source.UniqueId, StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var data = new CivilWallReportData { Batch = batch, Snapshot = applied, Rows = new List<CivilWallReportRow>(),
                DocumentScope = native.DocumentScope, SourcePath = native.SourcePath, SourceRevision = native.Revision };
            foreach (CivilWallResult result in batch.Results)
            {
                CivilWallFact fact;
                if (result == null || result.Fact == null || !facts.TryGetValue(result.Fact.UniqueId ?? String.Empty, out fact) ||
                    !Object.ReferenceEquals(fact, result.Fact) || !used.Add(fact.UniqueId))
                    throw new InvalidDataException("计算结果不属于本次完整墙体事实集合。");
                CivilElementRecord source;
                if (!sources.TryGetValue(fact.UniqueId, out source) || source.ElementId != fact.ElementId || source.KindCode != "wall")
                    throw new InvalidDataException("墙体结果无法按 UniqueId、ElementId 与原生墙来源严格关联。");
                var row = new CivilWallReportRow { Result = result, Source = source, Owner = data };
                ValidateRow(row);
                data.Rows.Add(row);
            }
            data.SnapshotFingerprint = CivilWallBatchIntegrity.SnapshotFingerprint(applied);
            return data;
        }

        public static List<CivilWallReportRow> Filter(CivilWallReportData data, string query, string status, string quantityLevelId, string segmentId)
        {
            ValidateData(data);
            query = (query ?? String.Empty).Trim();
            status = status ?? String.Empty;
            if (status.Length > 0 && status != "calculated" && status != "unsupported" && status != "failed")
                throw new InvalidDataException("未知墙体状态筛选。");
            return data.Rows.Where(row => (status.Length == 0 || row.Result.Status == status) &&
                AttributionMatches(row.QuantityLevelId, quantityLevelId) && AttributionMatches(row.SegmentId, segmentId) &&
                (query.Length == 0 || Search(row).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        public static List<CivilWallReportGroup> Group(IEnumerable<CivilWallReportRow> rows, string dimension)
        {
            Dimension(dimension);
            List<CivilWallReportRow> list = UniqueRows(rows);
            return GroupCore(list, dimension);
        }
        private static List<CivilWallReportGroup> GroupCore(List<CivilWallReportRow> list, string dimension)
        {
            var grouped = new Dictionary<string, CivilWallReportGroup>(StringComparer.Ordinal);
            foreach (CivilWallReportRow row in list)
            {
                ValidateRow(row);
                string identity, name;
                GroupIdentity(row, dimension, out identity, out name);
                string key = dimension + "|" + identity;
                CivilWallReportGroup group;
                if (!grouped.TryGetValue(key, out group))
                {
                    group = new CivilWallReportGroup { Key = key, Name = name, Rows = new List<CivilWallReportRow>() };
                    grouped.Add(key, group);
                }
                group.Rows.Add(row);
            }
            foreach (CivilWallReportGroup group in grouped.Values) FillTotals(group);
            return grouped.Values.OrderBy(group => group.Name, StringComparer.Ordinal).ThenBy(group => group.Key, StringComparer.Ordinal).ToList();
        }

        public static string ToCsv(CivilWallReportData data, IEnumerable<CivilWallReportRow> selected, string groupBy)
        {
            if (data == null) throw new ArgumentNullException("data");
            Dimension(groupBy);
            // Materialize caller enumeration before validating the snapshot.
            // The remainder invokes only private helpers over owned DTOs.
            List<CivilWallReportRow> rows = UniqueRows(selected ?? data.Rows, false);
            ValidateData(data);
            var allowed = new HashSet<CivilWallReportRow>(data.Rows);
            if (rows.Any(row => !allowed.Contains(row))) throw new InvalidDataException("导出行不属于本次报表；未混入其他批次或重复来源。");
            List<CivilWallReportGroup> groups = GroupCore(rows, groupBy);
            var total = new CivilWallReportGroup { Key = "total", Name = "当前范围合计", Rows = rows }; FillTotals(total);
            CivilSnapshot native = data.Snapshot; CivilWallSnapshot input = data.Batch.Input;
            var text = new StringBuilder();
            Csv(text, "产品", "崛亮视觉 · Bright Vision"); Csv(text, "报表格式", SchemaId);
            Csv(text, "体积方法", data.Batch.RuleTemplate.MethodId); Csv(text, "面积方法", data.Batch.RuleTemplate.AreaMethodId);
            Csv(text, "计算批次", data.Batch.BatchId); Csv(text, "规则模板Id", data.Batch.RuleTemplate.TemplateId);
            Csv(text, "规则模板", data.Batch.RuleTemplate.Name); Csv(text, "规则修订", data.Batch.RuleTemplate.Revision);
            Csv(text, "规则指纹SHA256", data.Batch.RuleFingerprint); Csv(text, "事实指纹SHA256", data.Batch.InputFingerprint);
            Csv(text, "切洞策略", data.Batch.RuleTemplate.OpeningPolicy); Csv(text, "交接策略", data.Batch.RuleTemplate.JoinPolicy);
            Csv(text, "面积口径", "单侧墙轴向投影：L×H 减矩形洞口并集；不是抹灰、模板、外露表面积、建筑面积或地区清单／定额面积。");
            Csv(text, "体积口径", "独立矩形基准扣明确洞口并集；原生面积与体积单列，不进入扣减公式。");
            Csv(text, "工程归属边界", "计量层和施工段仅为整构件报表归属，不执行几何切层或切段。");
            Csv(text, "模型", native.ModelTitle); Csv(text, "来源文档标识", native.DocumentScope); Csv(text, "来源路径", native.SourcePath);
            Csv(text, "读取时间UTC", native.CapturedAtUtc); Csv(text, "模型修订", native.Revision); Csv(text, "范围", native.ScopeLabel);
            Csv(text, "阶段", native.PhaseLabel); Csv(text, "设计选项", native.DesignOptionLabel);
            Csv(text, "几何校验视图UniqueId", input.GeometryViewUniqueId); Csv(text, "几何校验视图", input.GeometryViewName); Csv(text, "几何校验阶段Id", input.GeometryPhaseId);
            Csv(text, "计量工程Id", native.ProjectId); Csv(text, "计量工程", native.ProjectName); Csv(text, "计量工程修订", native.ProjectRevision);
            Csv(text, "分组维度", groupBy); Csv(text, "当前墙数", rows.Count); Csv(text, "纳入试算", total.CalculatedCount); Csv(text, "未纳入", total.UnsupportedCount + total.FailedCount);
            Csv(text, "计量层未映射墙数", rows.Count(row => row.QuantityLevelId.Length == 0)); Csv(text, "施工段未分配墙数", rows.Count(row => row.SegmentId.Length == 0));
            Csv(text, "链接实例数（未纳入墙体试算）", native.LinkInstanceCount); Csv(text, "未加载链接数", native.MissingLinkCount);
            foreach (string notice in native.ProjectNotices ?? new List<string>()) Csv(text, "工程提示", notice);
            Csv(text); Csv(text, "分组汇总：仅通过项参与数值合计；无通过项时数值为空，合法零保留");
            Csv(text, "分组维度", "分组身份", "分组名称", "墙数", "通过", "未支持", "失败", "基准单侧投影面积m2", "洞口并集面积m2", "净单侧投影面积m2", "项目试算体积m3");
            foreach (CivilWallReportGroup group in groups) GroupCsv(text, groupBy, group);
            GroupCsv(text, "total", total);
            Csv(text); Csv(text, "墙明细：保留未纳入项，原生量不参与项目方法计算");
            Csv(text, "墙ElementId", "墙UniqueId", "名称", "类型UniqueId", "类型", "状态", "原生参照楼层UniqueId", "原生参照楼层", "参照楼层来源", "原始楼层Id", "原始楼层", "计量层Id", "计量层", "施工段Id", "施工段",
                "基准单侧投影面积m2", "洞口并集面积m2", "净单侧投影面积m2", "矩形基准体积m3", "扣减体积m3", "项目试算体积m3", "Revit原生面积m2", "原生面积状态", "原生面积单位", "原生面积来源", "Revit原生体积m3", "原生体积状态", "原生体积单位", "原生体积来源",
                "实际实体体积m3", "双向差集体积m3", "几何容差m3", "墙间检查完整", "重叠候选数", "相交墙ElementIds", "体积方法", "面积方法", "状态说明");
            foreach (CivilWallReportRow row in rows)
            {
                CivilWallResult result = row.Result; CivilWallFact fact = result.Fact; CivilElementRecord source = row.Source;
                Csv(text, fact.ElementId, fact.UniqueId, fact.Name, row.TypeUniqueId, row.TypeName, result.Status, row.LevelUniqueId, row.LevelName, source.LevelSource, source.RawLevelId, source.RawLevelName, row.QuantityLevelId, row.QuantityLevelName, row.SegmentId, row.SegmentName,
                    result.BasisAreaMetres2, result.OpeningUnionAreaMetres2, result.ResultAreaMetres2, result.BasisVolumeMetres3, result.DeductionVolumeMetres3, result.ResultVolumeMetres3,
                    MetricValue(source.Area, "m2"), MetricStatus(source.Area), MetricUnit(source.Area), MetricSource(source.Area), MetricValue(source.Volume, "m3"), MetricStatus(source.Volume), MetricUnit(source.Volume), MetricSource(source.Volume),
                    fact.GeometryVolumeMetres3, fact.SymmetricDifferenceMetres3, fact.GeometryToleranceMetres3, fact.InterwallOverlapCheckComplete ? "yes" : "no", fact.OverlapCandidateCount,
                    String.Join("\n", fact.IntersectingWallElementIds ?? new List<string>()), CivilWallCalculator.RuleId, CivilWallCalculator.AreaRuleId, result.Message);
            }
            Csv(text); Csv(text, "逐条台账：带符号面积与体积变化；洞口并集去重后再扣减");
            Csv(text, "墙ElementId", "墙UniqueId", "步骤", "关联ElementId", "关联UniqueId", "说明", "面积变化m2", "面积公式", "新增并集面积m2", "体积变化m3", "体积公式", "来源", "体积方法", "面积方法");
            foreach (CivilWallReportRow row in rows) foreach (CivilWallLedgerEntry entry in row.Result.Ledger)
                Csv(text, row.Result.Fact.ElementId, row.Result.Fact.UniqueId, entry.Kind, entry.ElementId, entry.UniqueId, entry.Description, entry.DeltaAreaMetres2, entry.AreaFormula,
                    entry.AreaMetres2, entry.DeltaVolumeMetres3, entry.Formula, entry.Source, CivilWallCalculator.RuleId, CivilWallCalculator.AreaRuleId);
            return text.ToString();
        }

        private static void ValidateSources(IEnumerable<CivilElementRecord> sources)
        {
            var uids = new HashSet<string>(StringComparer.Ordinal); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilElementRecord source in sources)
                if (source == null || String.IsNullOrWhiteSpace(source.UniqueId) || String.IsNullOrWhiteSpace(source.ElementId) || !uids.Add(source.UniqueId) || !ids.Add(source.ElementId))
                    throw new InvalidDataException("原生快照来源身份缺失或重复，未生成墙体归属报表。");
        }
        internal static void Validate(CivilWallReportData data) { ValidateData(data); }
        private static void ValidateData(CivilWallReportData data)
        {
            if (data == null || data.Batch == null || data.Batch.Input == null || data.Batch.Input.NativeSnapshot == null || data.Snapshot == null || data.Rows == null)
                throw new InvalidDataException("墙体报表数据不完整。");
            CivilWallBatchIntegrity.Validate(data.Batch);
            if (CivilWallBatchIntegrity.SnapshotFingerprint(data.Snapshot) != data.SnapshotFingerprint)
                throw new InvalidDataException("报表工程归属或原生参照量在生成后已变化，请重新生成报表。");
            CivilSnapshot original = data.Batch.Input.NativeSnapshot;
            if (original.DocumentScope != data.DocumentScope || original.SourcePath != data.SourcePath || original.Revision != data.SourceRevision ||
                data.Snapshot.DocumentScope != data.DocumentScope || data.Snapshot.SourcePath != data.SourcePath || data.Snapshot.Revision != data.SourceRevision)
                throw new InvalidDataException("报表来源文档或修订已改变，请重新读取。");
            if (data.Batch.Results == null || data.Batch.Results.Count != data.Rows.Count || data.Snapshot.Elements == null ||
                data.Snapshot.Elements.Count(source => source != null && source.KindCode == "wall") != data.Rows.Count)
                throw new InvalidDataException("报表来源集合或计算覆盖数已变化，请重新读取。");
            var results = new HashSet<CivilWallResult>(data.Batch.Results);
            var sources = new HashSet<CivilElementRecord>(data.Snapshot.Elements);
            foreach (CivilWallReportRow row in UniqueRows(data.Rows, false))
            {
                if (!Object.ReferenceEquals(row.Owner, data) || !results.Contains(row.Result) || !sources.Contains(row.Source))
                    throw new InvalidDataException("报表行已被外来来源或其他计算批次替换。");
                ValidateRow(row);
            }
        }
        private static List<CivilWallReportRow> UniqueRows(IEnumerable<CivilWallReportRow> rows, bool validateOwner = true)
        {
            if (rows == null) throw new ArgumentNullException("rows");
            List<CivilWallReportRow> result = rows.ToList(); var identities = new HashSet<string>(StringComparer.Ordinal);
            CivilWallReportData owner = result.Count == 0 ? null : result[0] == null ? null : result[0].Owner;
            if (validateOwner && result.Count > 0)
            {
                ValidateData(owner);
                var owned = new HashSet<CivilWallReportRow>(owner.Rows);
                if (result.Any(row => !owned.Contains(row))) throw new InvalidDataException("分组行不属于其声明的原始报表。");
            }
            var elementIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilWallReportRow row in result)
            {
                if (row == null || row.Owner == null || !Object.ReferenceEquals(row.Owner, owner) || row.Result == null || row.Result.Fact == null ||
                    String.IsNullOrWhiteSpace(row.Result.Fact.UniqueId) || !identities.Add(row.Result.Fact.UniqueId) ||
                    String.IsNullOrWhiteSpace(row.Result.Fact.ElementId) || !elementIds.Add(row.Result.Fact.ElementId))
                    throw new InvalidDataException("报表行缺少来源或重复；未重复合计同一面墙。");
            }
            return result;
        }
        private static void ValidateRow(CivilWallReportRow row)
        {
            if (row == null || row.Source == null || row.Result == null || row.Result.Fact == null || row.Source.KindCode != "wall" ||
                row.Source.UniqueId != row.Result.Fact.UniqueId || row.Source.ElementId != row.Result.Fact.ElementId || String.IsNullOrWhiteSpace(row.Source.ElementId))
                throw new InvalidDataException("墙体报表来源关联已失效。");
            CivilWallResult result = row.Result;
            if (result.Status != "calculated" && result.Status != "unsupported" && result.Status != "failed") throw new InvalidDataException("未知墙体结果状态。");
            if (result.Ledger == null) throw new InvalidDataException("缺少墙体台账集合。");
            if (result.Status == "calculated")
            {
                PositiveOrZero(result.BasisAreaMetres2); PositiveOrZero(result.OpeningUnionAreaMetres2); PositiveOrZero(result.ResultAreaMetres2);
                PositiveOrZero(result.BasisVolumeMetres3); PositiveOrZero(result.DeductionVolumeMetres3); PositiveOrZero(result.ResultVolumeMetres3);
                if (result.Ledger.Any(entry => entry == null || !Finite(entry.DeltaAreaMetres2) || !Finite(entry.DeltaVolumeMetres3)))
                    throw new InvalidDataException("台账包含缺失或非有限的变化值。");
            }
            else if (result.BasisAreaMetres2.HasValue || result.OpeningUnionAreaMetres2.HasValue || result.ResultAreaMetres2.HasValue ||
                result.BasisVolumeMetres3.HasValue || result.DeductionVolumeMetres3.HasValue || result.ResultVolumeMetres3.HasValue || result.Ledger.Count != 0)
                throw new InvalidDataException("未通过的墙体不能保留试算数值或台账。");
        }
        private static void FillTotals(CivilWallReportGroup group)
        {
            group.WallCount = group.Rows.Count; group.CalculatedCount = group.Rows.Count(row => row.Result.Status == "calculated");
            group.UnsupportedCount = group.Rows.Count(row => row.Result.Status == "unsupported"); group.FailedCount = group.Rows.Count(row => row.Result.Status == "failed");
            List<CivilWallReportRow> passed = group.Rows.Where(row => row.Result.Status == "calculated").ToList();
            group.BasisAreaMetres2 = Sum(passed, row => row.Result.BasisAreaMetres2); group.OpeningAreaMetres2 = Sum(passed, row => row.Result.OpeningUnionAreaMetres2);
            group.ResultAreaMetres2 = Sum(passed, row => row.Result.ResultAreaMetres2); group.ResultVolumeMetres3 = Sum(passed, row => row.Result.ResultVolumeMetres3);
        }
        private static double? Sum(List<CivilWallReportRow> rows, Func<CivilWallReportRow, double?> value)
        {
            if (rows.Count == 0) return null;
            double total = 0;
            foreach (CivilWallReportRow row in rows) { double? item = value(row); PositiveOrZero(item); total += item.Value; if (!Finite(total)) throw new InvalidDataException("墙体报表合计溢出，未输出不完整数值。"); }
            return total;
        }
        private static void Dimension(string dimension)
        {
            if (dimension != "level" && dimension != "quantity-level" && dimension != "segment" && dimension != "type") throw new InvalidDataException("不支持的墙体报表分组维度。");
        }
        private static void GroupIdentity(CivilWallReportRow row, string dimension, out string identity, out string name)
        {
            if (dimension == "level")
            {
                string level = row.Source.LevelId ?? String.Empty;
                identity = row.LevelUniqueId.Length > 0 ? "uid:" + row.LevelUniqueId : level.Length > 0 && level != "-1" ? "id:" + level : "missing";
                name = identity == "missing" ? "未指定原生楼层" : row.LevelName;
            }
            else if (dimension == "quantity-level") { identity = row.QuantityLevelId.Length == 0 ? "missing" : "id:" + row.QuantityLevelId; name = row.QuantityLevelName; }
            else if (dimension == "segment") { identity = row.SegmentId.Length == 0 ? "missing" : "id:" + row.SegmentId; name = row.SegmentName; }
            else { identity = row.TypeUniqueId.Length == 0 ? "missing" : "uid:" + row.TypeUniqueId; name = row.TypeUniqueId.Length == 0 ? "未提供类型标识" : row.TypeName; }
        }
        private static bool AttributionMatches(string value, string filter) { return String.IsNullOrEmpty(filter) || (filter == UnassignedId ? value.Length == 0 : value == filter); }
        private static string Search(CivilWallReportRow row)
        {
            return String.Join(" ", new[] { row.Result.Fact.Name, row.Result.Fact.ElementId, row.Result.Fact.UniqueId, row.TypeName, row.LevelName, row.QuantityLevelName, row.SegmentName, row.Result.Message });
        }
        private static void PositiveOrZero(double? value) { if (!value.HasValue || !Finite(value.Value) || value.Value < 0) throw new InvalidDataException("通过项缺少有效的有限非负墙体量值。"); }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static object MetricValue(CivilMetric metric, string unit) { return metric != null && metric.Status == "available" && metric.Unit == unit && metric.Value.HasValue && Finite(metric.Value.Value) ? (object)metric.Value.Value : null; }
        private static string MetricStatus(CivilMetric metric) { return metric == null ? "missing" : metric.Status; }
        private static string MetricUnit(CivilMetric metric) { return metric == null ? String.Empty : metric.Unit; }
        private static string MetricSource(CivilMetric metric) { return metric == null ? String.Empty : metric.Source; }
        private static void GroupCsv(StringBuilder text, string dimension, CivilWallReportGroup group)
        {
            Csv(text, dimension, group.Key, group.Name, group.WallCount, group.CalculatedCount, group.UnsupportedCount, group.FailedCount,
                group.BasisAreaMetres2, group.OpeningAreaMetres2, group.ResultAreaMetres2, group.ResultVolumeMetres3);
        }
        private static void Csv(StringBuilder text, params object[] values)
        {
            text.Append(String.Join(",", values.Select(value => {
                if (value == null) return "\"\"";
                string cell;
                if (value is double) { if (!Finite((double)value)) throw new InvalidDataException("报表包含非有限数值。"); cell = ((double)value).ToString("R", CultureInfo.InvariantCulture); }
                else if (value is int || value is long) cell = Convert.ToString(value, CultureInfo.InvariantCulture);
                else {
                    cell = Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty; string probe = cell.TrimStart();
                    if ((cell.Length > 0 && (cell[0] == '\t' || cell[0] == '\r' || cell[0] == '\n')) || (probe.Length > 0 && "=+-@".IndexOf(probe[0]) >= 0)) cell = "'" + cell;
                }
                return "\"" + cell.Replace("\"", "\"\"") + "\"";
            }).ToArray())).Append("\r\n");
        }
    }
}
