// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BrightVision.Civil;

internal static class CivilWallJoinCoreFixture
{
    private static int checks;
    private static readonly List<string> cases = new List<string>();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Near(double actual, double expected, string message) { Check(!Double.IsNaN(actual) && !Double.IsInfinity(actual) && Math.Abs(actual - expected) < 1e-10, message); }
    private static int[] Cells(int first, int last) { return Enumerable.Range(first, last - first + 1).ToArray(); }

    // Exact finite disjoint atoms, not sampled Revit geometry. Set operations
    // implement an independent algebraic oracle without using kernel logic.
    private sealed class CellSolid
    {
        internal readonly HashSet<int> Atoms;
        internal int DisposeCalls;
        internal CellSolid(IEnumerable<int> atoms) { Atoms = new HashSet<int>(atoms); }
    }
    private sealed class Ops : ICivilWallJoinSolidOps
    {
        internal readonly List<CellSolid> Native = new List<CellSolid>(), Created = new List<CellSolid>();
        internal readonly Dictionary<int, double> Weights = new Dictionary<int, double>();
        internal int Calls, VolumeCalls, DifferenceCalls, ThrowAtCall, ThrowAtDispose, DisposeCalls, NoOpDifferenceAt;
        internal int VolumeFaultAt;
        internal double VolumeFaultValue;
        internal string Alias;
        internal bool ThrowMemory;
        internal CellSolid Source(IEnumerable<int> atoms) { var result = new CellSolid(atoms); Native.Add(result); return result; }
        private CellSolid Read(object solid) { var value = (CellSolid)solid; if (value.DisposeCalls > 0) throw new InvalidOperationException("Used disposed solid"); return value; }
        private void Call()
        {
            Calls++;
            if (Calls == ThrowAtCall) { if (ThrowMemory) throw new OutOfMemoryException("Controlled memory error"); throw new InvalidOperationException("Controlled geometry error"); }
        }
        private object Make(IEnumerable<int> atoms) { var result = new CellSolid(atoms); Created.Add(result); return result; }
        public object Clone(object solid)
        {
            Call();
            if (Alias == "borrowed") return solid;
            if (Alias == "owned" && Created.Count > 0) return Created[0];
            if (Alias == "null") return null;
            return Make(Read(solid).Atoms);
        }
        public object Union(object left, object right) { Call(); if (Alias == "union-left") return left; return Make(Read(left).Atoms.Union(Read(right).Atoms)); }
        public object Difference(object left, object right)
        {
            Call(); DifferenceCalls++;
            if (DifferenceCalls == NoOpDifferenceAt) return Make(Read(left).Atoms);
            return Make(Read(left).Atoms.Except(Read(right).Atoms));
        }
        public object Intersect(object left, object right) { Call(); return Make(Read(left).Atoms.Intersect(Read(right).Atoms)); }
        public double VolumeMetres3(object solid)
        {
            VolumeCalls++; if (VolumeCalls == VolumeFaultAt) return VolumeFaultValue;
            double sum = 0; foreach (int atom in Read(solid).Atoms) { double value; sum += Weights.TryGetValue(atom, out value) ? value : 1; } return sum;
        }
        public void Dispose(object solid)
        {
            var value = (CellSolid)solid; value.DisposeCalls++; DisposeCalls++;
            if (DisposeCalls == ThrowAtDispose) throw new InvalidOperationException("Controlled cleanup failure");
        }
    }
    private static CivilWallJoinNode Node(Ops ops, string id, int rank, int[] expected, int[] actual = null, bool requested = true, double? gross = null)
    {
        return new CivilWallJoinNode { UniqueId = "wall-" + id, ElementId = id, PriorityRank = rank, Requested = requested, Supported = true,
            ExpectedSolid = ops.Source(expected), ActualSolid = ops.Source(actual ?? expected), GrossVolumeMetres3 = gross ?? expected.Length };
    }
    private static CivilWallJoinInput Input(params CivilWallJoinNode[] nodes)
    { return new CivilWallJoinInput { ContextKey = "doc:phase:view:revision:closed-dependency-set", DependenciesComplete = true, Nodes = nodes.ToList() }; }
    private static void Ownership(Ops ops, string name)
    {
        Check(ops.Native.All(solid => solid.DisposeCalls == 0), name + ": borrowed native solids never disposed");
        Check(ops.Created.All(solid => solid.DisposeCalls == 1), name + ": every returned new solid disposed exactly once");
    }
    private static string NativeSnapshot(Ops ops) { return String.Join("|", ops.Native.Select(solid => String.Join(",", solid.Atoms.OrderBy(value => value)))); }
    private static Dictionary<string, double> IndependentOracle(CivilWallJoinInput input, Ops ops)
    {
        var result = input.Nodes.ToDictionary(node => node.UniqueId, node => 0.0);
        // For each disjoint atom choose its highest-priority owner. This does
        // not reproduce the kernel's sequence of residual differences.
        IEnumerable<int> universe = input.Nodes.SelectMany(node => ((CellSolid)node.ExpectedSolid).Atoms).Distinct();
        foreach (int atom in universe)
        {
            CivilWallJoinNode owner = input.Nodes.Where(node => ((CellSolid)node.ExpectedSolid).Atoms.Contains(atom)).OrderBy(node => node.PriorityRank.Value).First();
            double weight; result[owner.UniqueId] += ops.Weights.TryGetValue(atom, out weight) ? weight : 1;
        }
        return result;
    }
    private static CivilWallJoinComponentResult Success(string name, CivilWallJoinInput input, Ops ops, Action<string> checkpoint = null)
    {
        string native = NativeSnapshot(ops); var expected = IndependentOracle(input, ops);
        var result = CivilWallJoinCore.Calculate(input, ops, checkpoint);
        Check(result.Status == "calculated", name + ": status calculated, got " + result.Message);
        Check(result.MethodId == "BV.Wall.ComponentPriority/1" && result.ContextKey == input.ContextKey, name + ": method/source context retained");
        Check(result.Rows.Count == input.Nodes.Count && result.NodeCount == input.Nodes.Count, name + ": all dependencies retained including unrequested");
        Check(result.Rows.Select(row => row.PriorityRank).SequenceEqual(result.Rows.Select(row => row.PriorityRank).OrderBy(rank => rank)), name + ": explicit rank order");
        Near(result.ExpectedUnionVolumeMetres3.Value, expected.Values.Sum(), name + ": independent union volume");
        Near(result.SumResultVolumeMetres3.Value, expected.Values.Sum(), name + ": independent assigned sum");
        Near(result.AssignedUnionVolumeMetres3.Value, expected.Values.Sum(), name + ": assigned union");
        Near(result.SymmetricDifferenceMetres3.Value, 0, name + ": whole component native shape proof");
        Near(result.AllocationDifferenceMetres3.Value, 0, name + ": allocation shape conservation");
        Near(result.ResidualOverlapMetres3.Value, 0, name + ": no remaining overlap");
        foreach (CivilWallJoinRowResult row in result.Rows)
        {
            Near(row.ResultVolumeMetres3, expected[row.UniqueId], name + ": atom-owner oracle " + row.UniqueId);
            Near(row.ExpectedVolumeMetres3 - row.JoinDeductionVolumeMetres3, row.ResultVolumeMetres3, name + ": row conservation");
            Near(row.GrossVolumeMetres3 - row.OpeningDeductionVolumeMetres3 - row.JoinDeductionVolumeMetres3, row.ResultVolumeMetres3, name + ": opening and join deductions are distinct");
            Near(row.Steps.Sum(step => step.DeductionVolumeMetres3), row.JoinDeductionVolumeMetres3, name + ": incremental step sum");
            double previous = row.ExpectedVolumeMetres3;
            foreach (CivilWallJoinStep step in row.Steps)
            {
                Near(step.BeforeVolumeMetres3, previous, name + ": step starts at previous residual");
                Near(step.BeforeVolumeMetres3 - step.AfterVolumeMetres3, step.DeductionVolumeMetres3, name + ": signed residual step");
                Check(step.HigherPriorityRank < row.PriorityRank && input.Nodes.Any(node => node.UniqueId == step.HigherUniqueId && node.ElementId == step.HigherElementId && node.PriorityRank == step.HigherPriorityRank), name + ": step exact higher identity");
                previous = step.AfterVolumeMetres3;
            }
        }
        Check(result.Operations == ops.Calls && result.Operations <= input.MaximumOperations, name + ": actual operation budget count");
        Check(NativeSnapshot(ops) == native, name + ": original cell operands not mutated"); Ownership(ops, name); cases.Add(name); return result;
    }
    private static CivilWallJoinComponentResult Failure(string name, CivilWallJoinInput input, Ops ops, string status = "unsupported", Action<string> checkpoint = null)
    {
        string native = NativeSnapshot(ops); var result = CivilWallJoinCore.Calculate(input, ops, checkpoint);
        Check(result.Status == status, name + ": status " + status + ", got " + result.Status + ": " + result.Message);
        Check(result.Rows.Count == 0 && !String.IsNullOrWhiteSpace(result.Message), name + ": entire component refused with reason");
        Check(typeof(CivilWallJoinComponentResult).GetProperties().Where(property => property.PropertyType == typeof(double?)).All(property => property.GetValue(result, null) == null), name + ": no partial proof/quantity values retained");
        Check(result.MethodId == CivilWallJoinCore.MethodId, name + ": method still identified");
        Check(NativeSnapshot(ops) == native, name + ": borrowed operands unchanged"); Ownership(ops, name); cases.Add(name); return result;
    }
    private static Ops Two(out CivilWallJoinInput input)
    { var ops = new Ops(); input = Input(Node(ops,"A",1,Cells(0,3)),Node(ops,"B",2,Cells(2,5))); return ops; }

    private static void Run()
    {
        CivilWallJoinInput input; Ops ops; CivilWallJoinComponentResult result;
        ops = new Ops(); input = Input(Node(ops,"one",1,Cells(0,3))); Success("single-wall", input, ops);
        ops = Two(out input); result = Success("two-overlapping-walls", input, ops); Near(result.Rows[1].JoinDeductionVolumeMetres3,2,"Two-wall intersection is deducted once");
        ops = new Ops(); input = Input(Node(ops,"A",1,Cells(0,3)),Node(ops,"B",2,Cells(2,5)),Node(ops,"C",3,Cells(3,6))); result = Success("three-wall-shared-intersection",input,ops);
        Near(result.Rows[2].JoinDeductionVolumeMetres3,3,"Triple intersection union, not pair sum four"); Near(result.Rows[2].Steps[0].DeductionVolumeMetres3,1,"First higher contributor");Near(result.Rows[2].Steps[1].DeductionVolumeMetres3,2,"Second contributor sees current residual");
        ops = new Ops(); input = Input(Node(ops,"A",1,Cells(0,3)),Node(ops,"B",2,Cells(2,5)),Node(ops,"C",3,Cells(4,6),gross:4));result=Success("hole-overlaps-wall-junction",input,ops);
        Near(result.Rows[2].OpeningDeductionVolumeMetres3,1,"Hole deducted independently"); Near(result.Rows[2].JoinDeductionVolumeMetres3,2,"Hole already removed before intersection allocation");
        ops = new Ops(); input = Input(Node(ops,"A",1,Cells(0,3)),Node(ops,"B",2,Cells(2,5),Cells(4,5))); result=Success("native-join-ownership-differs-but-union-matches",input,ops);Near(result.Rows[1].ActualVolumeMetres3,2,"Native assigned quantity retained only as reference");
        ops = Two(out input); input.Nodes.Reverse(); result=Success("input-order-does-not-choose-business-priority",input,ops);Check(result.Rows[0].UniqueId=="wall-A","Explicit rank wins independent of enumeration order");
        ops = Two(out input); input.Nodes[0].PriorityRank=2;input.Nodes[1].PriorityRank=1;result=Success("swap-priority-changes-allocation",input,ops);Near(result.Rows.Single(row=>row.UniqueId=="wall-A").ResultVolumeMetres3,2,"Lower wall changes only after explicit rank swap");
        ops = Two(out input); input.Nodes[0].Requested=false; result=Success("unrequested-neighbor-retains-priority",input,ops);Check(result.RequestedCount==1&&!result.Rows[0].Requested,"Selection does not remove higher dependency");
        ops = new Ops(); input=Input(Node(ops,"A",1,Cells(0,3)),Node(ops,"B",2,Cells(0,3)));result=Success("duplicate-geometry-different-wall-identities",input,ops);Near(result.Rows[1].ResultVolumeMetres3,0,"Fully allocated duplicate is legal zero");
        ops = new Ops(); input=Input(Node(ops,"empty",1,new int[0],gross:4));result=Success("fully-cut-wall-has-legal-zero-volume",input,ops);Near(result.Rows[0].OpeningDeductionVolumeMetres3,4,"Complete opening volume retained");
        ops = new Ops(); input=Input(Node(ops,"A",1,Cells(0,3)),Node(ops,"B",2,Cells(10,13)));Success("disjoint-supporting-nodes",input,ops);
        ops = new Ops(); input=Input(Node(ops,"A",1,new[]{0,1},new[]{1,2}));Failure("equal-volume-different-shape-is-refused",input,ops);
        ops = Two(out input); ((CellSolid)input.Nodes[1].ActualSolid).Atoms.Remove(5);Failure("missing-native-part-refuses-whole-component",input,ops);
        ops = Two(out input);input.DependenciesComplete=false;Failure("incomplete-dependency-closure",input,ops);Check(ops.Calls==0,"Incomplete closure invokes no geometry");
        ops = Two(out input);input.ContextKey="";Failure("missing-source-context",input,ops);
        ops = Two(out input);input.Nodes[1].Supported=false;input.Nodes[1].Issue="unverified native shape";Failure("unsupported-unrequested-neighbor",input,ops);
        ops = Two(out input);input.Nodes[0].PriorityRank=null;Failure("missing-explicit-rank",input,ops);
        ops = Two(out input);input.Nodes[1].PriorityRank=1;Failure("duplicate-rank",input,ops);
        ops = Two(out input);input.Nodes[1].PriorityRank=0;Failure("nonpositive-rank",input,ops);
        ops = Two(out input);input.Nodes[1].UniqueId=input.Nodes[0].UniqueId;Failure("duplicate-unique-id",input,ops);
        ops = Two(out input);input.Nodes[1].ElementId=input.Nodes[0].ElementId;Failure("duplicate-element-id",input,ops);
        ops = Two(out input);input.Nodes[1]=null;Failure("null-node",input,ops);
        ops = Two(out input);input.Nodes.ForEach(node=>node.Requested=false);Failure("no-requested-wall",input,ops);
        ops = Two(out input);input.Nodes[1].ActualSolid=null;Failure("missing-actual-shape",input,ops);
        ops = Two(out input);input.Nodes[1].GrossVolumeMetres3=0;Failure("zero-undefined-gross-volume",input,ops);
        ops = Two(out input);input.Nodes[1].GrossVolumeMetres3=Double.NaN;Failure("nan-gross-volume",input,ops);
        ops = Two(out input);input.Nodes[1].GrossVolumeMetres3=Double.PositiveInfinity;Failure("infinite-gross-volume",input,ops);
        ops = Two(out input);input.Nodes[1].GrossVolumeMetres3=1;Failure("opening-cut-solid-larger-than-gross",input,ops);
        ops = Two(out input);input.AbsoluteToleranceMetres3=1e-7;Failure("absolute-tolerance-cannot-be-expanded",input,ops);
        ops = Two(out input);input.RelativeTolerance=Double.NaN;Failure("nan-relative-tolerance",input,ops);
        ops = Two(out input);input.RelativeTolerance=0;Failure("zero-relative-tolerance",input,ops);
        ops = Two(out input);input.MaximumOperations=0;Failure("invalid-operation-budget",input,ops);
        ops = Two(out input);input.MaximumOperations=20001;Failure("operation-budget-above-global-cap",input,ops);
        ops = Two(out input);input.MaximumOperations=1;result=Failure("operation-budget-exhaustion-is-atomic",input,ops);Check(result.Operations==1&&ops.Calls==1,"Budget checks before invoking extra provider operation");
        ops = new Ops();input=Input(Enumerable.Range(1,65).Select(index=>Node(ops,index.ToString(),index,new[]{index})).ToArray());Failure("component-node-budget-does-not-truncate",input,ops);Check(ops.Calls==0,"Oversized component does not enter CSG");
        foreach(string alias in new[]{"borrowed","owned","null","union-left"}){ops=Two(out input);ops.Alias=alias;Failure("provider-alias-"+alias,input,ops,"failed");}
        foreach(double invalid in new[]{Double.NaN,Double.PositiveInfinity,-1.0}){ops=Two(out input);ops.VolumeFaultAt=1;ops.VolumeFaultValue=invalid;Failure("invalid-provider-volume-"+invalid.ToString(CultureInfo.InvariantCulture),input,ops);}
        ops=Two(out input);ops.ThrowAtCall=3;Failure("native-operation-exception-atomic-failure",input,ops,"failed");
        ops=Two(out input);ops.NoOpDifferenceAt=4;Failure("provider-difference-not-removing-higher-priority-intersection",input,ops);
        ops=Two(out input);ops.ThrowAtDispose=1;Failure("dispose-failure-refuses-result-and-attempts-every-owned-solid",input,ops,"failed");
        foreach(int cancelAt in new[]{1,2,10,20}){ops=Two(out input);int calls=0;Failure("cancel-checkpoint-"+cancelAt,input,ops,"cancelled",delegate{if(++calls==cancelAt)throw new OperationCanceledException();});}
        ops=Two(out input);Failure("cancel-during-residual-stage-clears-completed-first-row",input,ops,"cancelled",delegate(string stage){if(stage=="按明确优先顺序分配交接剩余实体")throw new OperationCanceledException();});
        ops=Two(out input);Failure("cancel-at-final-checkpoint-still-does-not-commit",input,ops,"cancelled",delegate(string stage){if(stage=="交接组件提交前核验")throw new OperationCanceledException();});
        ops=Two(out input);string frozenKey=input.ContextKey;int frozenCalls=0;var originalNodes=input.Nodes.ToList();result=CivilWallJoinCore.Calculate(input,ops,delegate{if(++frozenCalls==1){input.ContextKey="changed externally";input.DependenciesComplete=false;input.MaximumOperations=1;input.Nodes[0].PriorityRank=99;input.Nodes.Clear();}});
        Check(result.Status=="calculated"&&result.ContextKey==frozenKey&&result.Rows[0].PriorityRank==1&&result.Rows.Count==2,"Caller metadata mutations do not modify the in-flight frozen input");Ownership(ops,"frozen-input");cases.Add("freeze-caller-metadata-before-checkpoints");
        ops=new Ops();ops.Weights[0]=1;ops.Weights[1]=2e-7;input=Input(Enumerable.Range(1,64).Select(index=>Node(ops,index.ToString(),index,new[]{0},new[]{0,1},gross:1)).ToArray());Failure("64-overlapping-walls-do-not-expand-component-shape-tolerance",input,ops);
        ops=Two(out input);input.Nodes[0].GrossVolumeMetres3=Double.MaxValue;input.Nodes[1].GrossVolumeMetres3=Double.MaxValue;Failure("summed-gross-overflow-is-refused",input,ops);
        ops=Two(out input);ops.ThrowAtCall=3;ops.ThrowMemory=true;bool memory=false;try{CivilWallJoinCore.Calculate(input,ops);}catch(OutOfMemoryException){memory=true;}Check(memory,"Memory failure is not disguised as unsupported geometry");Ownership(ops,"memory-cleanup");cases.Add("out-of-memory-propagates-after-owned-cleanup");
        // An independent per-atom ownership oracle validates varied triple
        // intersections, hole subsets and priorities, rather than final sums.
        var random=new Random(5291);
        for(int sample=0;sample<30;sample++)
        {
            ops=new Ops();var nodes=new List<CivilWallJoinNode>();var ranks=Enumerable.Range(1,5).OrderBy(value=>random.Next()).ToArray();
            for(int index=0;index<5;index++){int start=random.Next(0,9),length=random.Next(2,7);int[] atoms=Enumerable.Range(start,length).Where(value=>random.Next(0,4)!=0).ToArray();nodes.Add(Node(ops,index.ToString(),ranks[index],atoms,gross:length));}
            input=Input(nodes.OrderBy(node=>random.Next()).ToArray());Success("random-independent-atom-ownership-"+sample,input,ops);
        }
    }
    private static int Main(string[] args)
    {
        Run();string output=args[0];Directory.CreateDirectory(output);
        File.WriteAllLines(Path.Combine(output,"cases.txt"),cases,new UTF8Encoding(false));
        string names=String.Join(",",cases.Select(name=>"\""+name.Replace("\\","\\\\").Replace("\"","\\\"")+"\""));
        File.WriteAllText(Path.Combine(output,"report.json"),"{\"result\":\"PASS\",\"assertions\":"+checks+",\"cases\":["+names+"],\"scope\":\"Pure project-owned CSG kernel using exact finite disjoint weighted atoms and an independent per-atom priority-owner oracle. No Revit, native CSG or UI execution.\",\"revitStarted\":false,\"nativeDesktopInput\":false}",new UTF8Encoding(false));
        Console.WriteLine("PASS "+checks+" join-component assertions across "+cases.Count+" cases.");return 0;
    }
}
