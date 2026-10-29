// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BrightVision.Civil
{
    internal sealed class CivilWallLedgerEntry
    {
        public string Kind { get; set; }
        public string ElementId { get; set; }
        public string UniqueId { get; set; }
        public string Description { get; set; }
        public string Formula { get; set; }
        public double AreaMetres2 { get; set; }
        public double DeltaAreaMetres2 { get; set; }
        public string AreaFormula { get; set; }
        public double DeltaVolumeMetres3 { get; set; }
        public string Source { get; set; }
    }

    internal sealed class CivilWallResult
    {
        public CivilWallFact Fact { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public double? BasisAreaMetres2 { get; set; }
        public double? ResultAreaMetres2 { get; set; }
        public double? BasisVolumeMetres3 { get; set; }
        public double? OpeningUnionAreaMetres2 { get; set; }
        public double? DeductionVolumeMetres3 { get; set; }
        public double? ResultVolumeMetres3 { get; set; }
        public List<CivilWallLedgerEntry> Ledger { get; set; }
        public CivilWallResult() { Status = "unsupported"; Message = String.Empty; Ledger = new List<CivilWallLedgerEntry>(); }
    }

    internal sealed class CivilWallCalculationBatch
    {
        public CivilWallSnapshot Input { get; set; }
        public List<CivilWallResult> Results { get; set; }
        public CivilWallRuleTemplate RuleTemplate { get; internal set; }
        public string RuleFingerprint { get; internal set; }
        public string InputFingerprint { get; internal set; }
        public string ResultFingerprint { get; internal set; }
        public string BatchId { get; internal set; }
        public CivilWallCalculationBatch() { Results = new List<CivilWallResult>(); }
    }

    // One deterministic project method, not a regional bill or quota rule.
    // Native quantities never enter the subtraction formula. Geometry proof is
    // supplied by the Revit adapter and checked again for numeric consistency.
    internal static class CivilWallCalculator
    {
        public const string RuleId = "BV.Wall.RectSolid/1";
        // One side in the wall axis/vertical plane. This is not exposed-face,
        // formwork, finish, building area or a regional bill/quota measurement.
        public const string AreaRuleId = "BV.Wall.AxisProjection/1";
        public const int MaxInserts = 200;

        public static CivilWallCalculationBatch Calculate(CivilWallSnapshot input)
        {
            if (input == null || input.NativeSnapshot == null || input.Walls == null) throw new InvalidDataException("缺少墙体读取快照。");
            CivilWallRuleTemplate rule = CivilWallRuleTemplates.Clone(input.RuleTemplate ?? CivilWallRuleTemplates.Default());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var batch = new CivilWallCalculationBatch { Input = input };
            foreach (CivilWallFact fact in input.Walls)
            {
                if (fact == null || String.IsNullOrWhiteSpace(fact.UniqueId) || !seen.Add(fact.UniqueId))
                    throw new InvalidDataException("墙体来源身份缺失或重复；本轮结果未生成。");
                batch.Results.Add(CalculateWithRule(fact, rule));
            }
            CivilWallBatchIntegrity.Seal(batch);
            return batch;
        }

        public static CivilWallResult CalculateWall(CivilWallFact fact)
        { return CalculateWithRule(fact, CivilWallRuleTemplates.Default()); }

        private static CivilWallResult CalculateWithRule(CivilWallFact fact, CivilWallRuleTemplate rule)
        {
            if (fact == null) throw new ArgumentNullException("fact");
            var result = new CivilWallResult { Fact = fact };
            try
            {
                if (fact.Status != "verified" || !fact.GeometryVerified)
                    throw new UnsupportedWall(String.Join("；", fact.Issues ?? new List<string>()) + "；未通过矩形实体一致性校验。");
                if (!fact.InterwallOverlapCheckComplete || fact.IntersectingWallElementIds == null || fact.IntersectingWallElementIds.Count > 0 || fact.OverlapCandidateCount < 0 || fact.OverlapCandidateCount > 64)
                    throw new UnsupportedWall("同阶段主模型墙体的实体重叠检查未完成或存在正体积交集，未将重复／重叠墙相加。");
                double length = Dimension(fact.ParameterLength, "轴线长度"), height = Dimension(fact.ParameterHeight, "未连接高度"), width = Dimension(fact.ParameterWidth, "类型厚度");
                double faceArea = Finite(length * height), basis = Finite(faceArea * width);
                if (!fact.GeometryVolumeMetres3.HasValue || !FiniteNonNegative(fact.GeometryVolumeMetres3.Value) ||
                    !fact.SymmetricDifferenceMetres3.HasValue || !FiniteNonNegative(fact.SymmetricDifferenceMetres3.Value) ||
                    !fact.GeometryToleranceMetres3.HasValue || !FinitePositive(fact.GeometryToleranceMetres3.Value))
                    throw new UnsupportedWall("缺少有效的实体体积、双向差集或容差证据。");
                double tolerance = fact.GeometryToleranceMetres3.Value;
                double ruleTolerance = Math.Max(rule.AbsoluteToleranceMetres3, basis * rule.RelativeTolerance);
                if (tolerance > ruleTolerance && tolerance - ruleTolerance > ruleTolerance * 1e-12)
                    throw new UnsupportedWall("实体证据的容差比所选模板允许值更宽，未使用旧规则证明新规则结果。");
                // The adapter cannot silently declare an arbitrarily large
                // geometry tolerance and turn a different wall into a match.
                if (tolerance > Math.Max(1e-6, basis * 1e-7) || fact.SymmetricDifferenceMetres3.Value > tolerance)
                    throw new UnsupportedWall("实体一致性校验超出允许容差。");
                if (fact.Inserts == null || fact.Inserts.Count > MaxInserts) throw new UnsupportedWall("宿主插入信息缺失或超过本期 200 个上限。");
                var inserts = fact.Inserts.Where(i => i != null && i.Status != "excluded").ToList();
                if (fact.Inserts.Any(i => i == null)) throw new UnsupportedWall("宿主插入记录缺失。");
                if (inserts.Any(i => i.Status != "recognized" || i.Kind != "rect-opening"))
                    throw new UnsupportedWall("存在未确认的门窗、洞口或其他切洞，不能按名义尺寸扣减。");
                if (inserts.Any(i => i.CutoutMethod != "native-opening" && i.CutoutMethod != "family-rect-cutout"))
                    throw new UnsupportedWall("实际切洞的提取方法未知，未将其作为本方案的扣减依据。");
                if (rule.OpeningPolicy == CivilWallRuleTemplates.NativeOpeningOnly && inserts.Any(i => i.CutoutMethod != "native-opening"))
                    throw new UnsupportedWall("当前模板只接受原生矩形 Opening；门窗族实际切洞不纳入此方案。");
                var identities = new HashSet<string>(StringComparer.Ordinal);
                var rectangles = new List<Rect>();
                result.Ledger.Add(new CivilWallLedgerEntry {
                    Kind = "basis", ElementId = fact.ElementId, UniqueId = fact.UniqueId,
                    Description = "几何校验通过的矩形基准体积", AreaMetres2 = faceArea, DeltaVolumeMetres3 = basis,
                    DeltaAreaMetres2 = faceArea, AreaFormula = F(length) + " × " + F(height),
                    Formula = F(length) + " × " + F(height) + " × " + F(width),
                    Source = fact.ParameterLength.Source + "; " + fact.ParameterHeight.Source + "; " + fact.ParameterWidth.Source
                });
                double union = 0;
                foreach (CivilWallInsert insert in inserts.OrderBy(i => i.UniqueId, StringComparer.Ordinal))
                {
                    if (String.IsNullOrWhiteSpace(insert.UniqueId) || !identities.Add(insert.UniqueId) || insert.HostUniqueId != fact.UniqueId)
                        throw new UnsupportedWall("洞口身份重复、缺失或宿主不匹配。");
                    double x0 = Coordinate(insert.RectX0), x1 = Coordinate(insert.RectX1), z0 = Coordinate(insert.RectZ0), z1 = Coordinate(insert.RectZ1);
                    if (x1 <= x0 || z1 <= z0) throw new UnsupportedWall("矩形洞口边界方向或尺寸无效。");
                    var clipped = new Rect { X0 = Math.Max(0, x0), X1 = Math.Min(length, x1), Z0 = Math.Max(0, z0), Z1 = Math.Min(height, z1) };
                    if (clipped.X1 > clipped.X0 && clipped.Z1 > clipped.Z0) rectangles.Add(clipped);
                    double next = UnionArea(rectangles), incremental = Math.Max(0, next - union);
                    result.Ledger.Add(new CivilWallLedgerEntry {
                        Kind = "deduction", ElementId = insert.ElementId, UniqueId = insert.UniqueId,
                        Description = "明确矩形洞口：裁剪到墙域后的新增并集面积（重叠不重复扣）",
                        AreaMetres2 = incremental, DeltaVolumeMetres3 = -Finite(incremental * width),
                        DeltaAreaMetres2 = -incremental, AreaFormula = "−(" + F(next) + " − " + F(union) + ")",
                        Formula = "(" + F(next) + " − " + F(union) + ") × " + F(width), Source = insert.Source
                    });
                    union = next;
                }
                double deduction = Finite(union * width), remaining = Math.Max(0, Finite(basis - deduction));
                if (union > faceArea + 1e-10 || Math.Abs(remaining - fact.GeometryVolumeMetres3.Value) > tolerance)
                    throw new UnsupportedWall("独立矩形并集结果与实际实体体积不一致；不输出试算量。");
                result.BasisAreaMetres2 = faceArea; result.ResultAreaMetres2 = Math.Max(0, Finite(faceArea - union));
                result.BasisVolumeMetres3 = basis; result.OpeningUnionAreaMetres2 = union;
                result.DeductionVolumeMetres3 = deduction; result.ResultVolumeMetres3 = remaining;
                result.Status = "calculated"; result.Message = "项目约定矩形墙试算；不是地区清单／定额量。原生量单列，未参与再次扣减。";
            }
            catch (UnsupportedWall error)
            {
                result.Status = fact.Status == "failed" ? "failed" : "unsupported";
                result.Message = error.Message; result.Ledger.Clear();
                result.BasisAreaMetres2 = result.ResultAreaMetres2 = null;
                result.BasisVolumeMetres3 = result.OpeningUnionAreaMetres2 = result.DeductionVolumeMetres3 = result.ResultVolumeMetres3 = null;
            }
            return result;
        }

        private static double Dimension(CivilMetric metric, string label)
        {
            if (metric == null || metric.Status != "available" || metric.Unit != "m" || String.IsNullOrWhiteSpace(metric.Source) ||
                !metric.Value.HasValue || !FinitePositive(metric.Value.Value)) throw new UnsupportedWall(label + "缺少明确来源的有效米制值。");
            return metric.Value.Value;
        }
        private static double Coordinate(double? value) { if (!value.HasValue || !FiniteNumber(value.Value)) throw new UnsupportedWall("洞口坐标不可用。"); return value.Value; }
        private static double Finite(double value) { if (!FiniteNumber(value)) throw new UnsupportedWall("计算数值溢出，未输出结果。"); return value; }
        private static bool FiniteNumber(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static bool FiniteNonNegative(double value) { return FiniteNumber(value) && value >= 0; }
        private static bool FinitePositive(double value) { return FiniteNumber(value) && value > 0; }
        private static string F(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private sealed class UnsupportedWall : Exception { internal UnsupportedWall(string message) : base(message) { } }
        private sealed class Rect { internal double X0, X1, Z0, Z1; }

        // Exact axis-aligned rectangle union in the wall's local X/Z plane.
        // No mesh, raster sampling or opening-area sum is used as a substitute.
        private static double UnionArea(List<Rect> rectangles)
        {
            double[] xs = rectangles.SelectMany(r => new[] { r.X0, r.X1 }).Distinct().OrderBy(x => x).ToArray();
            double area = 0;
            for (int i = 1; i < xs.Length; i++)
            {
                double left = xs[i - 1], right = xs[i];
                var intervals = rectangles.Where(r => r.X0 < right && r.X1 > left).OrderBy(r => r.Z0).ThenBy(r => r.Z1).ToList();
                double total = 0, start = 0, end = 0; bool active = false;
                foreach (Rect interval in intervals)
                {
                    if (!active) { start = interval.Z0; end = interval.Z1; active = true; }
                    else if (interval.Z0 <= end) end = Math.Max(end, interval.Z1);
                    else { total = Finite(total + end - start); start = interval.Z0; end = interval.Z1; }
                }
                if (active) total = Finite(total + end - start);
                area = Finite(area + Finite((right - left) * total));
            }
            return area;
        }

        public static string ToCsv(CivilWallCalculationBatch batch, IEnumerable<CivilWallResult> selected)
        {
            CivilWallBatchIntegrity.Validate(batch);
            if (batch == null || batch.Input == null || batch.Input.NativeSnapshot == null) throw new ArgumentNullException("batch");
            List<CivilWallResult> rows = (selected ?? batch.Results).ToList();
            if (rows.Any(row => !batch.Results.Contains(row))) throw new InvalidDataException("报表记录不属于本次计算批次。");
            var text = new StringBuilder(); CivilSnapshot native = batch.Input.NativeSnapshot;
            Csv(text, "产品", "崛亮视觉 · Bright Vision"); Csv(text, "方法", RuleId);
            Csv(text, "口径", "矩形基准体积减明确矩形洞口并集；项目约定，不是地区清单／定额。原生量单列，不再扣减。");
            Csv(text, "模型", native.ModelTitle); Csv(text, "来源文档标识", native.DocumentScope); Csv(text, "来源路径", native.SourcePath);
            Csv(text, "读取时间UTC", native.CapturedAtUtc); Csv(text, "模型修订", native.Revision); Csv(text, "范围", native.ScopeLabel);
            Csv(text, "几何校验视图UniqueId", batch.Input.GeometryViewUniqueId); Csv(text, "几何校验视图", batch.Input.GeometryViewName); Csv(text, "几何校验阶段Id", batch.Input.GeometryPhaseId);
            Csv(text, "阶段", native.PhaseLabel); Csv(text, "设计选项", native.DesignOptionLabel); Csv(text, "当前墙数", rows.Count);
            Csv(text, "链接实例数（未纳入墙体试算）", native.LinkInstanceCount); Csv(text, "未加载链接数", native.MissingLinkCount);
            Csv(text, "边界", "仅部分主模型普通墙；同墙矩形洞口并集去重，同阶段墙体正体积交集拒绝；梁柱板、链接与地区规则增扣未实现。");
            Csv(text, "纳入试算", rows.Count(r => r.Status == "calculated")); Csv(text, "未纳入", rows.Count(r => r.Status != "calculated")); Csv(text);
            Csv(text, "墙ElementId", "墙UniqueId", "名称", "类型UniqueId", "状态", "长度m", "高度m", "厚度m", "基准体积m3", "洞口并集面积m2", "扣减体积m3", "项目试算体积m3", "Revit原生体积m3", "原生体积状态", "原生体积来源", "基底标高m", "基底来源", "实际实体体积m3", "双向差集体积m3", "几何容差m3", "墙间检查完整", "重叠候选数", "相交墙ElementIds", "方法", "状态说明");
            foreach (CivilWallResult row in rows)
            {
                CivilWallFact f = row.Fact;
                Csv(text, f.ElementId, f.UniqueId, f.Name, f.TypeUniqueId, row.Status, Value(f.ParameterLength), Value(f.ParameterHeight), Value(f.ParameterWidth),
                    row.BasisVolumeMetres3, row.OpeningUnionAreaMetres2, row.DeductionVolumeMetres3, row.ResultVolumeMetres3,
                    Value(f.NativeVolume), f.NativeVolume == null ? "missing" : f.NativeVolume.Status, f.NativeVolume == null ? "" : f.NativeVolume.Source, f.BaseElevationMetres, f.BaseElevationSource,
                    f.GeometryVolumeMetres3, f.SymmetricDifferenceMetres3, f.GeometryToleranceMetres3, f.InterwallOverlapCheckComplete ? "yes" : "no", f.OverlapCandidateCount,
                    String.Join("\n", f.IntersectingWallElementIds ?? new List<string>()), RuleId, row.Message);
            }
            Csv(text); Csv(text, "逐条台账：数值仅包含通过校验的墙体；来源与公式保留");
            Csv(text, "墙ElementId", "墙UniqueId", "步骤", "关联ElementId", "关联UniqueId", "说明", "新增并集面积m2", "体积变化m3", "公式", "来源", "方法");
            foreach (CivilWallResult row in rows) foreach (CivilWallLedgerEntry entry in row.Ledger)
                Csv(text, row.Fact.ElementId, row.Fact.UniqueId, entry.Kind, entry.ElementId, entry.UniqueId, entry.Description, entry.AreaMetres2, entry.DeltaVolumeMetres3, entry.Formula, entry.Source, RuleId);
            return text.ToString();
        }
        private static object Value(CivilMetric metric) { return metric != null && metric.Status == "available" && metric.Value.HasValue && FiniteNumber(metric.Value.Value) ? (object)metric.Value : null; }
        private static void Csv(StringBuilder text, params object[] values)
        {
            text.Append(String.Join(",", values.Select(value => {
                if (value == null) return "\"\"";
                string cell;
                if (value is double) { if (!FiniteNumber((double)value)) return "\"\""; cell = ((double)value).ToString("R", CultureInfo.InvariantCulture); }
                else if (value is int || value is long) cell = Convert.ToString(value, CultureInfo.InvariantCulture);
                else {
                    cell = Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
                    string probe = cell.TrimStart();
                    if ((cell.Length > 0 && (cell[0] == '\t' || cell[0] == '\r' || cell[0] == '\n')) ||
                        (probe.Length > 0 && "=+-@".IndexOf(probe[0]) >= 0)) cell = "'" + cell;
                }
                return "\"" + cell.Replace("\"", "\"\"") + "\"";
            }).ToArray())).Append("\r\n");
        }
    }
}
