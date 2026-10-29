// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace BrightVision.Civil
{
    // Raw Revit coordinate evidence is explicitly labelled feet. All quantity
    // properties are SI. No native API object is retained in a sealed batch.
    internal sealed class CivilWallJoinWallEvidence
    {
        public CivilWallFact Fact { get; set; }
        public double[] OriginFeet { get; set; }
        public double[] Axis { get; set; }
        public double[] Normal { get; set; }
        public double[] GrossBoundsFeet { get; set; }
        public double[] ActualBoundsFeet { get; set; }
        public double NormalMinFeet { get; set; }
        public double? ExpectedVolumeMetres3 { get; set; }
        public double? ActualVolumeMetres3 { get; set; }
        public List<string> ExplicitWallJoinUniqueIds { get; set; }
        public string Issue { get; set; }
        public CivilWallJoinWallEvidence() { ExplicitWallJoinUniqueIds = new List<string>(); Issue = String.Empty; }
    }

    internal sealed class CivilWallJoinComponentFacts
    {
        public string ComponentId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public CivilWallJoinBinding Binding { get; set; }
        public List<CivilWallJoinParticipant> Participants { get; set; }
        public List<CivilWallJoinWallEvidence> Evidence { get; set; }
    }

    internal sealed class CivilWallJoinSnapshot
    {
        public CivilSnapshot NativeSnapshot { get; set; }
        public string GeometryViewUniqueId { get; set; }
        public string GeometryViewName { get; set; }
        public string PhaseId { get; set; }
        public string DesignOptionId { get; set; }
        public List<string> Notices { get; set; }
        public CivilWallRuleTemplate OpeningRule { get; set; }
        public List<CivilWallJoinComponentFacts> Components { get; set; }
    }

    internal sealed class CivilWallJoinCalculation
    {
        public string ComponentId { get; set; }
        public CivilWallJoinPlan Plan { get; set; }
        public CivilWallJoinComponentResult Result { get; set; }
    }

    internal sealed class CivilWallJoinBatch
    {
        public string BatchId { get; internal set; }
        public CivilWallJoinSnapshot Input { get; internal set; }
        public List<CivilWallJoinCalculation> Calculations { get; internal set; }
        public string InputFingerprint { get; internal set; }
        public string ResultFingerprint { get; internal set; }
    }

    internal static class CivilWallJoinSession
    {
        public const string ReportId = "BV.Wall.JoinReport/1";
        private sealed class Seal { internal string Id, Input, Results; }
        private static readonly ConditionalWeakTable<CivilWallJoinBatch, Seal> Seals = new ConditionalWeakTable<CivilWallJoinBatch, Seal>();

        internal static void ValidateReadFacts(CivilWallJoinSnapshot input)
        {
            if(input==null||input.Components==null)throw new InvalidDataException("读取组件事实缺失。");
            var probe = new CivilWallJoinBatch { Input = input, Calculations = input.Components.Select(facts => new CivilWallJoinCalculation {
                ComponentId = facts == null ? null : facts.ComponentId,
                Result = new CivilWallJoinComponentResult { ContextKey = facts == null ? null : facts.ComponentId, Status = "unsupported", Message = "只校验读取结构；尚未核算" }
            }).ToList() };
            ValidateValues(probe);
        }

        internal static CivilWallJoinBatch Create(CivilWallJoinSnapshot input, IEnumerable<CivilWallJoinCalculation> calculations)
        {
            if (calculations == null) throw new ArgumentNullException("calculations");
            var batch = new CivilWallJoinBatch { BatchId = Guid.NewGuid().ToString("N"), Input = input, Calculations = calculations.ToList() };
            ValidateValues(batch);
            batch.InputFingerprint = CivilWallBatchIntegrity.FactsFingerprint(input);
            batch.ResultFingerprint = CivilWallBatchIntegrity.FactsFingerprint(batch.Calculations);
            Seals.Add(batch, new Seal { Id = batch.BatchId, Input = batch.InputFingerprint, Results = batch.ResultFingerprint });
            return batch;
        }

        internal static void Validate(CivilWallJoinBatch batch)
        {
            Seal seal;
            if (batch == null || !Seals.TryGetValue(batch, out seal) || batch.BatchId != seal.Id || batch.InputFingerprint != seal.Input || batch.ResultFingerprint != seal.Results ||
                CivilWallBatchIntegrity.FactsFingerprint(batch.Input) != seal.Input || CivilWallBatchIntegrity.FactsFingerprint(batch.Calculations) != seal.Results)
                throw new InvalidDataException("交接批次、来源事实或方案已变化，请重新读取核算；旧结果不可导出或定位。");
            ValidateValues(batch);
        }

        private static void ValidateValues(CivilWallJoinBatch batch)
        {
            if (batch.Input == null || batch.Input.NativeSnapshot == null || batch.Input.Components == null || batch.Calculations == null ||
                batch.Input.NativeSnapshot.Elements == null || String.IsNullOrWhiteSpace(batch.Input.NativeSnapshot.DocumentScope) || String.IsNullOrWhiteSpace(batch.Input.GeometryViewUniqueId) ||
                String.IsNullOrWhiteSpace(batch.Input.PhaseId) || batch.Input.DesignOptionId != "-1")
                throw new InvalidDataException("交接批次缺少完整来源、校验视图或组件。");
            CivilWallRuleTemplates.Validate(batch.Input.OpeningRule);
            if (batch.Calculations.Count != batch.Input.Components.Count) throw new InvalidDataException("交接结果未完整覆盖已读取组件。");
            var components = new Dictionary<string, CivilWallJoinComponentFacts>(StringComparer.Ordinal);
            var allIds = new HashSet<string>(StringComparer.Ordinal); var allElementIds = new HashSet<string>(StringComparer.Ordinal); var requested = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (CivilWallJoinComponentFacts facts in batch.Input.Components)
            {
                if (facts == null || String.IsNullOrWhiteSpace(facts.ComponentId) || components.ContainsKey(facts.ComponentId) || facts.Participants == null || facts.Evidence == null)
                    throw new InvalidDataException("交接组件身份、参与墙或证据缺失／重复。");
                CivilWallJoinPlans.ValidateBinding(facts.Binding);
                if (facts.ComponentId != facts.Binding.GeometryDependencyFingerprint) throw new InvalidDataException("组件身份与几何依赖指纹不一致。");
                if (facts.Binding.DocumentScope != batch.Input.NativeSnapshot.DocumentScope || facts.Binding.SourceRevision != batch.Input.NativeSnapshot.Revision ||
                    !String.Equals(facts.Binding.SourcePath, batch.Input.NativeSnapshot.SourcePath ?? String.Empty, StringComparison.OrdinalIgnoreCase) || facts.Binding.GeometryViewUniqueId != batch.Input.GeometryViewUniqueId || facts.Binding.PhaseId != batch.Input.PhaseId)
                    throw new InvalidDataException("交接组件来源与本轮模型、修订或视图不一致。");
                var local = new HashSet<string>(StringComparer.Ordinal);
                foreach (CivilWallJoinParticipant wall in facts.Participants)
                {
                    if (wall == null || String.IsNullOrWhiteSpace(wall.ElementUniqueId) || String.IsNullOrWhiteSpace(wall.ElementId) || !local.Add(wall.ElementUniqueId) || !allIds.Add(wall.ElementUniqueId) || !allElementIds.Add(wall.ElementId))
                        throw new InvalidDataException("参与墙身份重复、缺失或跨组件重复。");
                    if (wall.Requested) requested.Add(wall.ElementUniqueId,wall.ElementId);
                }
                if (local.Count == 0 || !facts.Participants.Any(wall => wall.Requested)) throw new InvalidDataException("组件没有请求墙或参与墙。");
                if ((facts.Status != "ready" && facts.Status != "unsupported") || (facts.Status == "ready" && facts.Participants.Count > CivilWallJoinCore.MaximumNodes) ||
                    facts.Evidence.Count != facts.Participants.Count || facts.Evidence.Any(item => item == null || item.Fact == null) ||
                    facts.Evidence.Select(item => item.Fact.UniqueId).Distinct(StringComparer.Ordinal).Count() != facts.Participants.Count ||
                    facts.Evidence.Any(item => !facts.Participants.Any(wall => wall.ElementUniqueId == item.Fact.UniqueId && wall.ElementId == item.Fact.ElementId)))
                    throw new InvalidDataException("读取组件状态或原始几何证据覆盖不完整。");
                components.Add(facts.ComponentId, facts);
            }
            var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            if (batch.Input.NativeSnapshot.Elements.Any(source => source == null)) throw new InvalidDataException("原生来源记录缺失。");
            foreach (CivilElementRecord source in batch.Input.NativeSnapshot.Elements.Where(source => source.KindCode == "wall"))
                if (String.IsNullOrWhiteSpace(source.UniqueId) || !sourceIds.Add(source.UniqueId) || !requested.ContainsKey(source.UniqueId) || requested[source.UniqueId] != source.ElementId) throw new InvalidDataException("原生请求墙与交接请求覆盖或身份不一致。");
            if (!sourceIds.SetEquals(requested.Keys)) throw new InvalidDataException("交接组件漏掉原生请求墙，未静默出部分量。");
            var calculatedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilWallJoinCalculation calculation in batch.Calculations)
            {
                CivilWallJoinComponentFacts facts;
                if (calculation == null || !components.TryGetValue(calculation.ComponentId ?? String.Empty, out facts) || !calculatedIds.Add(calculation.ComponentId) || calculation.Result == null)
                    throw new InvalidDataException("结果不属于本次完整组件集合。");
                CivilWallJoinComponentResult result = calculation.Result;
                if (result.ContextKey != facts.ComponentId || result.MethodId != CivilWallJoinCore.MethodId || result.Rows == null)
                    throw new InvalidDataException("交接结果上下文或方法不匹配。");
                if (result.Status != "calculated")
                {
                    if (result.Status != "unsupported" || result.Rows.Count != 0 || result.ExpectedUnionVolumeMetres3.HasValue || result.ActualUnionVolumeMetres3.HasValue ||
                        result.SumResultVolumeMetres3.HasValue || result.AssignedUnionVolumeMetres3.HasValue || result.SymmetricDifferenceMetres3.HasValue || result.ResidualOverlapMetres3.HasValue || result.AllocationDifferenceMetres3.HasValue || result.ToleranceMetres3.HasValue)
                        throw new InvalidDataException("未支持组件必须保留空量值；失败或取消的整轮不得提交。");
                    continue;
                }
                CivilWallJoinPlans.ValidateFor(calculation.Plan, facts.Binding, facts.Participants.Select(wall => wall.ElementUniqueId));
                if (result.Operations < 1 || result.Operations > CivilWallJoinCore.MaximumOperationLimit) throw new InvalidDataException("交接证明操作数无效或超过预算。");
                if (facts.Status != "ready" || facts.Evidence.Count != facts.Participants.Count || facts.Evidence.Any(item => item == null || item.Fact == null) ||
                    facts.Evidence.Select(item => item.Fact.UniqueId).Distinct(StringComparer.Ordinal).Count() != facts.Participants.Count ||
                    !new HashSet<string>(facts.Evidence.Select(item => item.Fact.UniqueId),StringComparer.Ordinal).SetEquals(facts.Participants.Select(wall => wall.ElementUniqueId)))
                    throw new InvalidDataException("已通过结果缺少完整、唯一、已准备的组件事实。");
                double tolerance = Positive(result.ToleranceMetres3);
                double union = Quantity(result.ExpectedUnionVolumeMetres3), actual = Quantity(result.ActualUnionVolumeMetres3);
                if (tolerance > Math.Max(batch.Input.OpeningRule.AbsoluteToleranceMetres3, union * batch.Input.OpeningRule.RelativeTolerance) * (1 + 1e-12))
                    throw new InvalidDataException("交接组件证明容差比所选规则更宽。");
                Near(union, actual, tolerance); Near(union, Quantity(result.AssignedUnionVolumeMetres3), tolerance); Near(union, Quantity(result.SumResultVolumeMetres3), tolerance);
                if (Quantity(result.SymmetricDifferenceMetres3) > tolerance || Quantity(result.ResidualOverlapMetres3) > tolerance || Quantity(result.AllocationDifferenceMetres3) > tolerance)
                    throw new InvalidDataException("组件形状、分配并集或残量重叠证明未通过。");
                if (result.NodeCount != facts.Participants.Count || result.RequestedCount != facts.Participants.Count(wall => wall.Requested) || result.Rows.Count != facts.Participants.Count)
                    throw new InvalidDataException("交接组件结果覆盖发生变化。");
                var rows = result.Rows.ToDictionary(row => row.UniqueId, StringComparer.Ordinal); double total = 0;
                var priority = calculation.Plan.Priorities.ToDictionary(entry => entry.ElementUniqueId, entry => entry.Rank, StringComparer.Ordinal);
                foreach (CivilWallJoinParticipant participant in facts.Participants)
                {
                    CivilWallJoinRowResult row;
                    if (!rows.TryGetValue(participant.ElementUniqueId, out row) || row.ElementId != participant.ElementId || row.Requested != participant.Requested || row.PriorityRank != priority[row.UniqueId] || row.Steps == null)
                        throw new InvalidDataException("墙体结果与完整依赖身份或已确认优先序不一致。");
                    double gross = Quantity(row.GrossVolumeMetres3), opening = Quantity(row.OpeningDeductionVolumeMetres3), expected = Quantity(row.ExpectedVolumeMetres3);
                    double join = Quantity(row.JoinDeductionVolumeMetres3), remaining = Quantity(row.ResultVolumeMetres3); Quantity(row.ActualVolumeMetres3);
                    Near(gross - opening, expected, tolerance); Near(expected - join, remaining, tolerance);
                    Near(gross, Quantity(participant.GrossVolumeMetres3), tolerance); Near(opening, Quantity(participant.OpeningVolumeMetres3), tolerance);
                    CivilWallJoinWallEvidence evidence = facts.Evidence.Single(item => item.Fact != null && item.Fact.UniqueId == row.UniqueId);
                    if (evidence.Fact.ElementId != row.ElementId || evidence.Fact.Status != "component-prepared" || evidence.Fact.GeometryVerified || !String.IsNullOrEmpty(evidence.Issue))
                        throw new InvalidDataException("交接几何证据身份或准备状态失效。");
                    CivilWallFact wallFact = evidence.Fact;
                    if (wallFact.Inserts == null || wallFact.Inserts.Count > CivilWallCalculator.MaxInserts || wallFact.Inserts.Any(insert => insert == null ||
                        (insert.Status != "excluded" && (insert.Status != "recognized" || insert.Kind != "rect-opening" || insert.HostUniqueId != row.UniqueId ||
                        (insert.CutoutMethod != "native-opening" && insert.CutoutMethod != "family-rect-cutout") ||
                        (batch.Input.OpeningRule.OpeningPolicy == CivilWallRuleTemplates.NativeOpeningOnly && insert.CutoutMethod != "native-opening")))))
                        throw new InvalidDataException("交接事实的切洞提取方法不属于本轮所选规则。");
                    Near(Dimension(wallFact.ParameterLength) * Dimension(wallFact.ParameterHeight) * Dimension(wallFact.ParameterWidth), gross, tolerance);
                    Coordinates(evidence.OriginFeet,3); Coordinates(evidence.Axis,3); Coordinates(evidence.Normal,3); Coordinates(evidence.GrossBoundsFeet,6); Coordinates(evidence.ActualBoundsFeet,6);
                    if (!Finite(evidence.NormalMinFeet)) throw new InvalidDataException("交接参数基准坐标不是有限值。");
                    Near(expected, Quantity(evidence.ExpectedVolumeMetres3), tolerance); Near(row.ActualVolumeMetres3, Quantity(evidence.ActualVolumeMetres3), tolerance);
                    if (row.Steps.Count != row.PriorityRank - 1) throw new InvalidDataException("交接台账没有完整覆盖所有高优先级墙。");
                    double residual = expected, deductions = 0; int rank = 0;
                    foreach (CivilWallJoinStep step in row.Steps)
                    {
                        CivilWallJoinRowResult higher;
                        if (step == null || step.HigherPriorityRank != ++rank || !rows.TryGetValue(step.HigherUniqueId ?? String.Empty, out higher) || higher.PriorityRank != rank || higher.ElementId != step.HigherElementId)
                            throw new InvalidDataException("交接台账关联身份或顺序不完整。");
                        Quantity(step.BeforeVolumeMetres3); Quantity(step.AfterVolumeMetres3); Quantity(step.DeductionVolumeMetres3);
                        Near(step.BeforeVolumeMetres3, residual, tolerance); Near(step.BeforeVolumeMetres3 - step.AfterVolumeMetres3, step.DeductionVolumeMetres3, tolerance);
                        if (step.AfterVolumeMetres3 > step.BeforeVolumeMetres3 + tolerance) throw new InvalidDataException("扣减步骤增加了体积。");
                        residual = step.AfterVolumeMetres3; deductions = Add(deductions, step.DeductionVolumeMetres3);
                    }
                    Near(residual, remaining, tolerance); Near(deductions, join, tolerance); total = Add(total, remaining);
                }
                Near(total, union, tolerance);
            }
        }

        internal static string ToCsv(CivilWallJoinBatch batch, bool includeDependencies)
        {
            Validate(batch); var text = new StringBuilder(); CivilSnapshot native = batch.Input.NativeSnapshot;
            Csv(text, "产品", "崛亮视觉 · Bright Vision"); Csv(text, "报表格式", ReportId); Csv(text, "体积方法", CivilWallJoinCore.MethodId);
            Csv(text, "口径", "项目约定组件并集体积按明确优先顺序分配；高优先级保有交集；不改变原生量，不作为地区清单／定额量或交接后面积。");
            Csv(text, "批次", batch.BatchId); Csv(text, "事实指纹SHA256", batch.InputFingerprint); Csv(text, "结果指纹SHA256", batch.ResultFingerprint);
            Csv(text, "来源工程", native.DocumentScope); Csv(text, "来源文件", native.SourcePath); Csv(text, "来源修订", native.Revision); Csv(text, "原始计量范围", native.ScopeLabel);
            Csv(text, "阶段Id", batch.Input.PhaseId); Csv(text, "设计选项Id", batch.Input.DesignOptionId);
            Csv(text, "几何校验视图", batch.Input.GeometryViewUniqueId); Csv(text, "切洞规则", batch.Input.OpeningRule.TemplateId); Csv(text, "切洞规则指纹SHA256", CivilWallRuleTemplates.Fingerprint(batch.Input.OpeningRule));
            Csv(text, "行范围", includeDependencies ? "请求墙及全部范围外依赖" : "仅请求墙；组件证明仍包括全部范围外依赖");
            foreach(string notice in batch.Input.Notices ?? new List<string>()) Csv(text,"读取边界说明",notice);
            Csv(text); Csv(text, "组件汇总（完整组件的守恒量，不因仅导出请求墙而改变）");
            Csv(text, "组件Id", "状态", "请求墙数", "参与墙数", "方案", "方案修订", "洞后并集m3", "实际并集m3", "分配合计m3", "形状差集m3", "分配差集m3", "说明");
            foreach (CivilWallJoinCalculation item in batch.Calculations)
            {
                CivilWallJoinComponentFacts facts = batch.Input.Components.Single(value => value.ComponentId == item.ComponentId); CivilWallJoinComponentResult result = item.Result;
                Csv(text, item.ComponentId, result.Status, facts.Participants.Count(wall => wall.Requested), facts.Participants.Count, item.Plan == null ? null : item.Plan.Name, item.Plan == null ? (int?)null : item.Plan.Revision,
                    result.ExpectedUnionVolumeMetres3, result.ActualUnionVolumeMetres3, result.SumResultVolumeMetres3, result.SymmetricDifferenceMetres3, result.AllocationDifferenceMetres3, result.Message);
                Csv(text, "组件方案指纹SHA256", item.ComponentId, item.Plan == null ? null : CivilWallJoinPlans.Fingerprint(item.Plan));
                Csv(text, "组件几何指纹SHA256", item.ComponentId, facts.Binding.GeometryDependencyFingerprint);
            }
            Csv(text); Csv(text, "墙体体积明细（原生参数量单列；没有交接后面积推断）");
            Csv(text, "组件Id", "墙UniqueId", "墙ElementId", "名称", "范围角色", "优先顺序", "参数毛量m3", "已确认洞口扣量m3", "洞后基准m3", "交接扣量m3", "分配结果m3", "实际墙实体m3", "Revit原生体积m3", "状态");
            foreach (CivilWallJoinCalculation item in batch.Calculations)
            {
                CivilWallJoinComponentFacts facts = batch.Input.Components.Single(value => value.ComponentId == item.ComponentId);
                foreach (CivilWallJoinParticipant wall in facts.Participants.Where(wall => includeDependencies || wall.Requested))
                {
                    CivilWallJoinRowResult row = item.Result.Rows.SingleOrDefault(value => value.UniqueId == wall.ElementUniqueId);
                    Csv(text, item.ComponentId, wall.ElementUniqueId, wall.ElementId, wall.Name, wall.Requested ? "请求墙" : "范围外依赖", row == null ? (int?)null : row.PriorityRank,
                        row == null ? (double?)null : row.GrossVolumeMetres3, row == null ? (double?)null : row.OpeningDeductionVolumeMetres3, row == null ? (double?)null : row.ExpectedVolumeMetres3,
                        row == null ? (double?)null : row.JoinDeductionVolumeMetres3, row == null ? (double?)null : row.ResultVolumeMetres3, row == null ? (double?)null : row.ActualVolumeMetres3, wall.NativeVolumeMetres3, item.Result.Status);
                }
            }
            Csv(text); Csv(text, "交接逐条台账（在当前残量上扣除，体积变化为负号；零增量保留）");
            Csv(text, "组件Id", "墙UniqueId", "高优先墙UniqueId", "高优先墙ElementId", "高优先顺序", "扣前m3", "扣后m3", "体积变化m3");
            foreach (CivilWallJoinCalculation item in batch.Calculations) foreach (CivilWallJoinRowResult row in item.Result.Rows.Where(row => includeDependencies || row.Requested)) foreach (CivilWallJoinStep step in row.Steps)
                Csv(text, item.ComponentId, row.UniqueId, step.HigherUniqueId, step.HigherElementId, step.HigherPriorityRank, step.BeforeVolumeMetres3, step.AfterVolumeMetres3, -step.DeductionVolumeMetres3);
            return text.ToString();
        }
        private static double Quantity(double? value) { if (!value.HasValue || !Finite(value.Value) || value.Value < 0) throw new InvalidDataException("交接数量缺失、非有限或为负。"); return value.Value; }
        private static double Dimension(CivilMetric metric) { if(metric==null||metric.Status!="available"||metric.Unit!="m"||String.IsNullOrWhiteSpace(metric.Source))throw new InvalidDataException("参数毛量缺少明确米制来源。");return Positive(metric.Value); }
        private static void Coordinates(double[] values,int count) { if(values==null||values.Length!=count||values.Any(value=>!Finite(value)))throw new InvalidDataException("组件原生坐标证据缺失或非有限。"); }
        private static double Positive(double? value) { double number = Quantity(value); if (number <= 0) throw new InvalidDataException("交接证明容差无效。"); return number; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static double Add(double left, double right) { double total = left + right; if (!Finite(total)) throw new InvalidDataException("交接数量合计溢出。"); return total; }
        private static void Near(double left, double right, double tolerance) { if (!Finite(left) || !Finite(right) || Math.Abs(left - right) > tolerance) throw new InvalidDataException("交接基准、扣减、残量或组件合计不守恒。"); }
        private static void Csv(StringBuilder text, params object[] values)
        {
            text.Append(String.Join(",", values.Select(value => {
                string cell;
                if (value == null) cell = String.Empty;
                else if (value is double) { if (!Finite((double)value)) throw new InvalidDataException("交接报表存在非有限数值。"); cell = ((double)value).ToString("R", CultureInfo.InvariantCulture); }
                else if (value is int || value is long) cell = Convert.ToString(value, CultureInfo.InvariantCulture);
                else { cell = Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty; string probe = cell.TrimStart(); if ((cell.Length > 0 && "\t\r\n".IndexOf(cell[0]) >= 0) || (probe.Length > 0 && "=+-@".IndexOf(probe[0]) >= 0)) cell = "'" + cell; }
                return "\"" + cell.Replace("\"", "\"\"") + "\"";
            }).ToArray())).Append("\r\n");
        }
    }
}
