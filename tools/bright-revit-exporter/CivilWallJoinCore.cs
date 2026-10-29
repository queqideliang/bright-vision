// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace BrightVision.Civil
{
    // Opaque operands are used only during calculation. The adapter retains
    // ownership of every input. Operations must produce independent solids.
    internal interface ICivilWallJoinSolidOps
    {
        object Clone(object solid);
        object Union(object left, object right);
        object Difference(object left, object right);
        object Intersect(object left, object right);
        double VolumeMetres3(object solid);
        void Dispose(object solid);
    }

    internal sealed class CivilWallJoinNode
    {
        public string UniqueId { get; set; }
        public string ElementId { get; set; }
        public bool Requested { get; set; }
        public bool Supported { get; set; }
        public string Issue { get; set; }
        public int? PriorityRank { get; set; }
        public double GrossVolumeMetres3 { get; set; }
        public object ExpectedSolid { get; set; }
        public object ActualSolid { get; set; }
        public CivilWallJoinNode() { UniqueId = ElementId = Issue = String.Empty; }
    }

    internal sealed class CivilWallJoinInput
    {
        public string ContextKey { get; set; }
        public bool DependenciesComplete { get; set; }
        public double AbsoluteToleranceMetres3 { get; set; }
        public double RelativeTolerance { get; set; }
        public int MaximumOperations { get; set; }
        public List<CivilWallJoinNode> Nodes { get; set; }
        public CivilWallJoinInput()
        {
            ContextKey = String.Empty; Nodes = new List<CivilWallJoinNode>();
            AbsoluteToleranceMetres3 = RelativeTolerance = 1e-8; MaximumOperations = 20000;
        }
    }

    internal sealed class CivilWallJoinStep
    {
        public string HigherUniqueId { get; set; }
        public string HigherElementId { get; set; }
        public int HigherPriorityRank { get; set; }
        public double BeforeVolumeMetres3 { get; set; }
        public double AfterVolumeMetres3 { get; set; }
        public double DeductionVolumeMetres3 { get; set; }
    }

    internal sealed class CivilWallJoinRowResult
    {
        public string UniqueId { get; set; }
        public string ElementId { get; set; }
        public bool Requested { get; set; }
        public int PriorityRank { get; set; }
        public double GrossVolumeMetres3 { get; set; }
        public double OpeningDeductionVolumeMetres3 { get; set; }
        public double ExpectedVolumeMetres3 { get; set; }
        public double ActualVolumeMetres3 { get; set; }
        public double JoinDeductionVolumeMetres3 { get; set; }
        public double ResultVolumeMetres3 { get; set; }
        public List<CivilWallJoinStep> Steps { get; set; }
        public CivilWallJoinRowResult() { UniqueId = ElementId = String.Empty; Steps = new List<CivilWallJoinStep>(); }
    }

    internal sealed class CivilWallJoinComponentResult
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public string ContextKey { get; set; }
        public string MethodId { get; set; }
        public int Operations { get; set; }
        public int NodeCount { get; set; }
        public int RequestedCount { get; set; }
        public double? ToleranceMetres3 { get; set; }
        public double? ExpectedUnionVolumeMetres3 { get; set; }
        public double? ActualUnionVolumeMetres3 { get; set; }
        public double? SymmetricDifferenceMetres3 { get; set; }
        public double? AssignedUnionVolumeMetres3 { get; set; }
        public double? AllocationDifferenceMetres3 { get; set; }
        public double? ResidualOverlapMetres3 { get; set; }
        public double? SumResultVolumeMetres3 { get; set; }
        public List<CivilWallJoinRowResult> Rows { get; set; }
        public CivilWallJoinComponentResult()
        {
            Status = "unsupported"; Message = ContextKey = String.Empty;
            MethodId = CivilWallJoinCore.MethodId; Rows = new List<CivilWallJoinRowResult>();
        }
    }

    // Project-owned volume allocation, not a regional measurement rule. A
    // complete dependency component is proved as a shape before any allocation.
    // Every deduction is from the current residual, so a triple intersection
    // cannot be deducted once for each original pair.
    internal static class CivilWallJoinCore
    {
        public const string MethodId = "BV.Wall.ComponentPriority/1";
        public const int MaximumNodes = 64;
        public const int MaximumOperationLimit = 20000;

        public static CivilWallJoinComponentResult Calculate(CivilWallJoinInput input, ICivilWallJoinSolidOps ops, Action<string> checkpoint = null)
        {
            if (input == null) throw new ArgumentNullException("input");
            if (ops == null) throw new ArgumentNullException("ops");
            var result = new CivilWallJoinComponentResult { ContextKey = input.ContextKey ?? String.Empty };
            var scope = new SolidScope(ops, input.MaximumOperations, checkpoint);
            try
            {
                Check(input);
                input = Freeze(input);
                List<CivilWallJoinNode> ordered = input.Nodes.OrderBy(node => node.PriorityRank.Value).ToList();
                result.NodeCount = ordered.Count; result.RequestedCount = ordered.Count(node => node.Requested);
                double grossSum = 0;
                foreach (CivilWallJoinNode node in ordered)
                {
                    scope.Borrow(node.ExpectedSolid); scope.Borrow(node.ActualSolid);
                    grossSum = Add(grossSum, node.GrossVolumeMetres3);
                }
                var expected = new List<object>(); var actual = new List<object>();
                var expectedVolumes = new List<double>(); var actualVolumes = new List<double>();
                foreach (CivilWallJoinNode node in ordered)
                {
                    scope.Checkpoint("读取交接参与墙的已确认实体");
                    object e = scope.Clone(node.ExpectedSolid), a = scope.Clone(node.ActualSolid);
                    double volume = scope.Volume(e), native = scope.Volume(a);
                    if (volume - node.GrossVolumeMetres3 > RowTolerance(input, node))
                        throw new Unsupported("洞后实体体积超过明确毛量，未按原生净量冒充毛量。");
                    expected.Add(e); actual.Add(a); expectedVolumes.Add(volume); actualVolumes.Add(native);
                }
                object expectedUnion = scope.UnionAll(expected), actualUnion = scope.UnionAll(actual);
                result.ExpectedUnionVolumeMetres3 = scope.Volume(expectedUnion);
                result.ActualUnionVolumeMetres3 = scope.Volume(actualUnion);
                // Duplicating overlapping walls must not enlarge the allowed
                // shape error. The component scale is its union, not the sum
                // of every (potentially duplicate) gross prism.
                double tolerance = Math.Max(input.AbsoluteToleranceMetres3, Finite(result.ExpectedUnionVolumeMetres3.Value * input.RelativeTolerance));
                result.ToleranceMetres3 = tolerance;
                double difference = SymmetricDifference(scope, expectedUnion, actualUnion);
                result.SymmetricDifferenceMetres3 = difference;
                if (difference > tolerance)
                    throw new Unsupported("洞后毛实体并集与实际实体并集形状不一致；未猜测端部延伸、未知切割或缺失依赖。");

                var residuals = new List<object>();
                for (int index = 0; index < ordered.Count; index++)
                {
                    CivilWallJoinNode node = ordered[index]; double rowTolerance = RowTolerance(input, node);
                    object residual = scope.Clone(expected[index]); double remaining = scope.Volume(residual), deduction = 0;
                    var row = new CivilWallJoinRowResult { UniqueId = node.UniqueId, ElementId = node.ElementId, Requested = node.Requested,
                        PriorityRank = node.PriorityRank.Value, GrossVolumeMetres3 = node.GrossVolumeMetres3,
                        ExpectedVolumeMetres3 = expectedVolumes[index], ActualVolumeMetres3 = actualVolumes[index],
                        OpeningDeductionVolumeMetres3 = Math.Max(0, node.GrossVolumeMetres3 - expectedVolumes[index]) };
                    for (int higher = 0; higher < index; higher++)
                    {
                        scope.Checkpoint("按明确优先顺序分配交接剩余实体");
                        object next = scope.Difference(residual, expected[higher]); double after = scope.Volume(next);
                        if (after - remaining > rowTolerance) throw new Unsupported("交接差集产生了正向体积增加，未提交组件结果。");
                        double delta = Math.Max(0, remaining - after);
                        row.Steps.Add(new CivilWallJoinStep { HigherUniqueId = ordered[higher].UniqueId, HigherElementId = ordered[higher].ElementId,
                            HigherPriorityRank = ordered[higher].PriorityRank.Value, BeforeVolumeMetres3 = remaining, AfterVolumeMetres3 = after, DeductionVolumeMetres3 = delta });
                        deduction = Add(deduction, delta); remaining = after; residual = next;
                    }
                    Near(expectedVolumes[index] - deduction, remaining, rowTolerance, "逐墙洞后基准、交接扣减和剩余量不守恒。");
                    if (scope.Volume(scope.Difference(residual, expected[index])) > rowTolerance)
                        throw new Unsupported("分配后实体超出本墙洞后毛实体。");
                    for (int higher = 0; higher < index; higher++)
                        if (scope.Volume(scope.Intersect(residual, expected[higher])) > rowTolerance)
                            throw new Unsupported("低优先墙仍占有高优先墙应保有的交集。");
                    row.JoinDeductionVolumeMetres3 = deduction; row.ResultVolumeMetres3 = remaining;
                    residuals.Add(residual); result.Rows.Add(row);
                }

                double overlapSum = 0;
                for (int left = 0; left < residuals.Count; left++)
                    for (int right = left + 1; right < residuals.Count; right++)
                    {
                        double overlap = scope.Volume(scope.Intersect(residuals[left], residuals[right]));
                        if (overlap > tolerance) throw new Unsupported("分配后的墙体仍存在正体积重叠。");
                        overlapSum = Add(overlapSum, overlap);
                    }
                if (overlapSum > tolerance) throw new Unsupported("分配后残余交集累计超过组件容差。");
                result.ResidualOverlapMetres3 = overlapSum;
                object assignedUnion = scope.UnionAll(residuals);
                double allocationDifference = SymmetricDifference(scope, assignedUnion, expectedUnion);
                result.AllocationDifferenceMetres3 = allocationDifference;
                if (allocationDifference > tolerance) throw new Unsupported("分配前后的实体并集不一致。");
                double sum = 0; foreach (CivilWallJoinRowResult row in result.Rows) sum = Add(sum, row.ResultVolumeMetres3);
                double assignedVolume = scope.Volume(assignedUnion);
                Near(sum, result.ExpectedUnionVolumeMetres3.Value, tolerance, "各墙剩余体积之和与组件并集不守恒。");
                Near(assignedVolume, result.ExpectedUnionVolumeMetres3.Value, tolerance, "分配后并集体积与分配前不守恒。");
                result.SumResultVolumeMetres3 = sum; result.AssignedUnionVolumeMetres3 = assignedVolume;
                scope.Checkpoint("交接组件提交前核验");
                result.Status = "calculated"; result.Message = "项目约定交接体积分配；优先序明确，洞口与交接均按剩余实体去重，原生量仅作对照。";
            }
            catch (OperationCanceledException) { Clear(result, "cancelled", "交接组件计算已取消，没有提交部分结果。"); }
            catch (Unsupported error) { Clear(result, "unsupported", error.Message); }
            catch (Exception error)
            {
                if (error is OutOfMemoryException) throw;
                if (error.GetType().FullName == "Autodesk.Revit.Exceptions.OperationCanceledException")
                    Clear(result, "cancelled", "原生交接运算已取消，没有提交部分结果。");
                else Clear(result, "failed", "交接几何运算未完成：" + error.GetType().Name + ": " + error.Message);
            }
            finally
            {
                result.Operations = scope.Operations;
                string cleanupError = scope.ReleaseAll();
                if (cleanupError != null) Clear(result, "failed", "交接实体释放未完成：" + cleanupError);
            }
            return result;
        }

        private static void Check(CivilWallJoinInput input)
        {
            if (String.IsNullOrWhiteSpace(input.ContextKey) || input.ContextKey.Length > 4096) throw new Unsupported("缺少明确来源上下文。");
            if (!input.DependenciesComplete) throw new Unsupported("交接依赖闭包未完整读取，不能只按当前选中墙分配交集。");
            if (input.Nodes == null || input.Nodes.Count < 1 || input.Nodes.Count > MaximumNodes) throw new Unsupported("交接组件必须包含 1 至 64 面墙；未截断后继续计算。");
            if (input.MaximumOperations < 1 || input.MaximumOperations > MaximumOperationLimit) throw new Unsupported("交接组件几何运算预算无效。");
            if (!Positive(input.AbsoluteToleranceMetres3) || input.AbsoluteToleranceMetres3 > 1e-8 || !Positive(input.RelativeTolerance) || input.RelativeTolerance > 1e-8)
                throw new Unsupported("交接组件容差无效或比已实现口径更宽。");
            var uids = new HashSet<string>(StringComparer.Ordinal); var ids = new HashSet<string>(StringComparer.Ordinal); var ranks = new HashSet<int>();
            foreach (CivilWallJoinNode node in input.Nodes)
            {
                if (node == null || String.IsNullOrWhiteSpace(node.UniqueId) || node.UniqueId.Length > 1024 || String.IsNullOrWhiteSpace(node.ElementId) || node.ElementId.Length > 256 || !uids.Add(node.UniqueId) || !ids.Add(node.ElementId))
                    throw new Unsupported("交接参与墙身份缺失或重复。");
                if (!node.Supported) throw new Unsupported("交接组件包含未获支持的参与墙：" + node.ElementId + " " + node.Issue);
                if (!node.PriorityRank.HasValue || node.PriorityRank.Value < 1 || !ranks.Add(node.PriorityRank.Value)) throw new Unsupported("组件内每面墙都必须有唯一的正整数优先序，包括未选中的依赖墙。");
                if (!Positive(node.GrossVolumeMetres3) || node.ExpectedSolid == null || node.ActualSolid == null) throw new Unsupported("交接参与墙缺少明确毛量或已准备的几何证据。");
            }
            if (!input.Nodes.Any(node => node.Requested)) throw new Unsupported("组件内没有本次请求的墙。");
        }

        private static CivilWallJoinInput Freeze(CivilWallJoinInput input)
        {
            return new CivilWallJoinInput { ContextKey = input.ContextKey, DependenciesComplete = input.DependenciesComplete,
                AbsoluteToleranceMetres3 = input.AbsoluteToleranceMetres3, RelativeTolerance = input.RelativeTolerance, MaximumOperations = input.MaximumOperations,
                Nodes = input.Nodes == null ? null : input.Nodes.Select(node => node == null ? null : new CivilWallJoinNode {
                    UniqueId = node.UniqueId, ElementId = node.ElementId, Requested = node.Requested, Supported = node.Supported,
                    Issue = node.Issue, PriorityRank = node.PriorityRank, GrossVolumeMetres3 = node.GrossVolumeMetres3,
                    ExpectedSolid = node.ExpectedSolid, ActualSolid = node.ActualSolid }).ToList() };
        }

        private static void Clear(CivilWallJoinComponentResult result, string status, string message)
        {
            result.Status = status; result.Message = message; result.Rows.Clear();
            result.ToleranceMetres3 = result.ExpectedUnionVolumeMetres3 = result.ActualUnionVolumeMetres3 = result.SymmetricDifferenceMetres3 = null;
            result.AssignedUnionVolumeMetres3 = result.AllocationDifferenceMetres3 = result.ResidualOverlapMetres3 = result.SumResultVolumeMetres3 = null;
        }
        private static double SymmetricDifference(SolidScope scope, object left, object right)
        { return Add(scope.Volume(scope.Difference(left, right)), scope.Volume(scope.Difference(right, left))); }
        private static double RowTolerance(CivilWallJoinInput input, CivilWallJoinNode node)
        { return Math.Max(input.AbsoluteToleranceMetres3, Finite(node.GrossVolumeMetres3 * input.RelativeTolerance)); }
        private static bool Positive(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value > 0; }
        private static double Finite(double value) { if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new Unsupported("交接量值溢出或为非有限值。"); return value; }
        private static double Add(double left, double right) { return Finite(left + right); }
        private static void Near(double actual, double expected, double tolerance, string message)
        { if (Math.Abs(Finite(actual - expected)) > tolerance) throw new Unsupported(message); }
        private sealed class Unsupported : Exception { internal Unsupported(string message) : base(message) { } }
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object left, object right) { return Object.ReferenceEquals(left, right); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }
        private sealed class SolidScope
        {
            private readonly ICivilWallJoinSolidOps ops;
            private readonly int maximum;
            private readonly Action<string> checkpoint;
            private readonly HashSet<object> borrowed = new HashSet<object>(ReferenceComparer.Instance), owned = new HashSet<object>(ReferenceComparer.Instance);
            private readonly List<object> order = new List<object>();
            internal int Operations { get; private set; }
            internal SolidScope(ICivilWallJoinSolidOps ops, int maximum, Action<string> checkpoint) { this.ops = ops; this.maximum = maximum; this.checkpoint = checkpoint; }
            internal void Borrow(object solid) { borrowed.Add(solid); }
            internal void Checkpoint(string stage) { if (checkpoint != null) checkpoint(stage); }
            private void Before()
            {
                Checkpoint("交接实体操作前");
                if (Operations >= maximum) throw new Unsupported("交接组件超过几何运算预算，没有提交半个组件。");
                Operations++;
            }
            private object Own(object solid)
            {
                if (solid == null) throw new InvalidOperationException("几何操作未返回实体。");
                if (borrowed.Contains(solid) || !owned.Add(solid)) throw new InvalidOperationException("几何操作返回借用对象或已拥有的别名，拒绝释放或重用原生输入。");
                order.Add(solid); Checkpoint("交接实体操作后"); return solid;
            }
            internal object Clone(object solid) { Before(); return Own(ops.Clone(solid)); }
            internal object Union(object left, object right) { Before(); return Own(ops.Union(left, right)); }
            internal object Difference(object left, object right) { Before(); return Own(ops.Difference(left, right)); }
            internal object Intersect(object left, object right) { Before(); return Own(ops.Intersect(left, right)); }
            internal double Volume(object solid)
            {
                double value = Finite(ops.VolumeMetres3(solid));
                if (value < 0) throw new Unsupported("几何实体体积为负。"); return value;
            }
            internal object UnionAll(IList<object> solids)
            {
                object result = Clone(solids[0]);
                for (int index = 1; index < solids.Count; index++) result = Union(result, solids[index]);
                return result;
            }
            internal string ReleaseAll()
            {
                string failure = null;
                for (int index = order.Count - 1; index >= 0; index--)
                    try { ops.Dispose(order[index]); }
                    catch (Exception error) { if (error is OutOfMemoryException) throw; if (failure == null) failure = error.GetType().Name + ": " + error.Message; }
                return failure;
            }
        }
    }
}
