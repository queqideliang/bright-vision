// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BrightVision.Civil;

internal static class CivilWallJoinSessionFixture
{
    private static int checks;
    private static readonly List<string> cases = new List<string>();
    private static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
    private static void Near(double actual,double expected,string name){Check(!Double.IsNaN(actual)&&!Double.IsInfinity(actual)&&Math.Abs(actual-expected)<1e-10,name);}
    private static bool Refuses(Action action){try{action();return false;}catch(OutOfMemoryException){throw;}catch{return true;}}
    private static CivilMetric Metric(double value,string unit,string source){return new CivilMetric{Value=value,Unit=unit,Source=source,Status="available"};}
    private sealed class Cell
    {
        internal HashSet<int> Atoms;internal bool Disposed;
        internal Cell(IEnumerable<int> atoms){Atoms=new HashSet<int>(atoms);}
    }
    private sealed class Ops:ICivilWallJoinSolidOps
    {
        internal readonly List<Cell> Created=new List<Cell>();
        private Cell Read(object value){var cell=(Cell)value;if(cell.Disposed)throw new InvalidOperationException("Disposed cell");return cell;}
        private object New(IEnumerable<int> atoms){var cell=new Cell(atoms);Created.Add(cell);return cell;}
        public object Clone(object value){return New(Read(value).Atoms);}
        public object Union(object left,object right){return New(Read(left).Atoms.Union(Read(right).Atoms));}
        public object Difference(object left,object right){return New(Read(left).Atoms.Except(Read(right).Atoms));}
        public object Intersect(object left,object right){return New(Read(left).Atoms.Intersect(Read(right).Atoms));}
        public double VolumeMetres3(object value){return Read(value).Atoms.Count;}
        public void Dispose(object value){var cell=Read(value);cell.Disposed=true;}
    }
    private sealed class Fixture
    {
        internal CivilWallJoinSnapshot Snapshot;
        internal readonly List<CivilWallJoinCalculation> Calculations=new List<CivilWallJoinCalculation>();
        internal readonly List<Ops> Providers=new List<Ops>();
        internal readonly List<CivilWallJoinInput> Inputs=new List<CivilWallJoinInput>();
        internal Fixture(string output,int walls=2,bool firstRequested=true,bool unsupported=false)
        {
            Snapshot=new CivilWallJoinSnapshot{NativeSnapshot=new CivilSnapshot{DocumentScope="doc-fixture",SourcePath=Path.Combine(output,"fixture.rvt"),Revision=7,ModelTitle="中文算量测试",ScopeLabel="当前选择",PhaseLabel="新建",DesignOptionLabel="主模型"},
                PhaseId="phase-P",DesignOptionId="-1",GeometryViewUniqueId="view-fixture",GeometryViewName="只读几何校验",OpeningRule=CivilWallRuleTemplates.Default(),Components=new List<CivilWallJoinComponentFacts>()};
            AddComponent('a',walls,firstRequested,unsupported);
        }
        internal void AddComponent(char key,int count,bool firstRequested,bool unsupported)
        {
            string componentId=new string(key,64);
            var binding=new CivilWallJoinBinding{DocumentScope=Snapshot.NativeSnapshot.DocumentScope,SourcePath=Snapshot.NativeSnapshot.SourcePath,SourceRevision=Snapshot.NativeSnapshot.Revision,
                PhaseId=Snapshot.PhaseId,GeometryViewUniqueId=Snapshot.GeometryViewUniqueId,GeometryDependencyFingerprint=componentId};
            var facts=new CivilWallJoinComponentFacts{ComponentId=componentId,Status=unsupported?"unsupported":"ready",Message=unsupported?"明确未支持的邻墙":"",
                Binding=binding,Participants=new List<CivilWallJoinParticipant>(),Evidence=new List<CivilWallJoinWallEvidence>()};
            var core=new CivilWallJoinInput{ContextKey=componentId,DependenciesComplete=true};var ops=new Ops();Providers.Add(ops);Inputs.Add(core);
            for(int index=0;index<count;index++)
            {
                int start=index==0?0:index==1?2:3;int[] expected=Enumerable.Range(start,4).ToArray();int[] actual=index==1?new[]{4,5}:index==2?new[]{6}:expected;
                bool requested=index>0||firstRequested;string id=key+"-"+(index+1);string uid="wall-"+id;
                var fact=new CivilWallFact{ElementId=id,UniqueId=uid,Name="墙"+id,TypeUniqueId="type-basic",TypeName="普通直墙",CurveKind="Line",Status="component-prepared",GeometryVerified=false,InterwallOverlapCheckComplete=true,
                    ParameterLength=Metric(4,"m","LocationCurve.Curve.Length"),ParameterHeight=Metric(1,"m","WALL_USER_HEIGHT_PARAM"),ParameterWidth=Metric(1,"m","WallType.Width"),
                    NativeVolume=Metric(actual.Length,"m3","HOST_VOLUME_COMPUTED"),NativeArea=Metric(4,"m2","HOST_AREA_COMPUTED"),NativeLength=Metric(4,"m","CURVE_ELEM_LENGTH"),
                    GeometryVolumeMetres3=actual.Length,BaseElevationMetres=0,BaseElevationSource="Level.ProjectElevation + WALL_BASE_OFFSET",SymmetricDifferenceMetres3=4-actual.Length,GeometryToleranceMetres3=4e-8};
                var participant=new CivilWallJoinParticipant{ElementId=id,ElementUniqueId=uid,Name=fact.Name,Requested=requested,GrossVolumeMetres3=4,OpeningVolumeMetres3=0,NativeVolumeMetres3=actual.Length};
                var evidence=new CivilWallJoinWallEvidence{Fact=fact,OriginFeet=new[]{start/.3048,0.0,0.0},Axis=new[]{1.0,0.0,0.0},Normal=new[]{0.0,1.0,0.0},NormalMinFeet=0,
                    GrossBoundsFeet=new[]{start/.3048,(start+4)/.3048,0.0,1/.3048,0.0,1/.3048},ActualBoundsFeet=new[]{start/.3048,(start+4)/.3048,0.0,1/.3048,0.0,1/.3048},ExpectedVolumeMetres3=4,ActualVolumeMetres3=actual.Length};
                facts.Participants.Add(participant);facts.Evidence.Add(evidence);
                core.Nodes.Add(new CivilWallJoinNode{ElementId=id,UniqueId=uid,Requested=requested,Supported=!unsupported,PriorityRank=index+1,GrossVolumeMetres3=4,ExpectedSolid=new Cell(expected),ActualSolid=new Cell(actual)});
                if(requested)Snapshot.NativeSnapshot.Elements.Add(new CivilElementRecord{ElementId=id,UniqueId=uid,KindCode="wall",Name=fact.Name,TypeName=fact.TypeName,Volume=Metric(actual.Length,"m3","HOST_VOLUME_COMPUTED")});
            }
            var result=CivilWallJoinCore.Calculate(core,ops);
            Check(result.Status==(unsupported?"unsupported":"calculated"),"Fixture uses actual core calculation, not handcrafted successful results");
            Check(ops.Created.All(cell=>cell.Disposed),"Fixture core native stand-ins released before plain session seal");
            var plan=unsupported?null:CivilWallJoinPlans.Create(binding,core.Nodes.OrderBy(node=>node.PriorityRank.Value).Select(node=>node.UniqueId),"明确优先序方案"+key);
            Snapshot.Components.Add(facts);Calculations.Add(new CivilWallJoinCalculation{ComponentId=componentId,Plan=plan,Result=result});
        }
        internal void Recalculate(int index,IEnumerable<string> order=null)
        {
            var input=Inputs[index];var ids=(order??input.Nodes.OrderBy(node=>node.PriorityRank.Value).Select(node=>node.UniqueId)).ToList();
            for(int rank=0;rank<ids.Count;rank++)input.Nodes.Single(node=>node.UniqueId==ids[rank]).PriorityRank=rank+1;
            Calculations[index].Plan=CivilWallJoinPlans.Create(Snapshot.Components[index].Binding,ids,"重新确认的交接顺序");
            Calculations[index].Result=CivilWallJoinCore.Calculate(input,Providers[index]);
            Check(Calculations[index].Result.Status=="calculated","Recomputed session result comes from actual core with explicit changed order/shape");
            Check(Providers[index].Created.All(cell=>cell.Disposed),"Recomputed opaque operands leave no live core output");
        }
        internal CivilWallJoinBatch Seal(){return CivilWallJoinSession.Create(Snapshot,Calculations);}
    }
    private static List<List<string>> Csv(string text)
    {
        var rows=new List<List<string>>();var row=new List<string>();var field=new StringBuilder();bool quoted=false;
        for(int index=0;index<text.Length;index++)
        {
            char c=text[index];
            if(c=='"'){if(quoted&&index+1<text.Length&&text[index+1]=='"'){field.Append('"');index++;}else quoted=!quoted;}
            else if(c==','&&!quoted){row.Add(field.ToString());field.Clear();}
            else if((c=='\r'||c=='\n')&&!quoted){if(c=='\r'&&index+1<text.Length&&text[index+1]=='\n')index++;row.Add(field.ToString());field.Clear();rows.Add(row);row=new List<string>();}
            else field.Append(c);
        }
        Check(!quoted,"CSV quotes close");if(field.Length>0||row.Count>0){row.Add(field.ToString());rows.Add(row);}return rows;
    }
    private static List<List<string>> Section(List<List<string>> rows,int count)
    {
        int start=rows.FindIndex(row=>row.Count==count&&row[0]=="组件Id");Check(start>=0,"CSV contains exact "+count+" column section");
        return rows.Skip(start+1).TakeWhile(row=>row.Count==count).ToList();
    }
    private static void Valid(string name,Fixture fixture)
    {
        var batch=fixture.Seal();CivilWallJoinSession.Validate(batch);Check(batch.BatchId.Length==32&&batch.InputFingerprint.Length==64&&batch.ResultFingerprint.Length==64,name+": sealed identities");
        string text=CivilWallJoinSession.ToCsv(batch,true);Check(text.Contains("崛亮视觉")&&text.Contains("BV.Wall.JoinReport/1")&&text.Contains(CivilWallJoinCore.MethodId),name+": product/report/method");
        var rows=Csv(text);var wallRows=Section(rows,14);var ledger=Section(rows,8);
        Check(wallRows.Count==fixture.Snapshot.Components.Sum(component=>component.Participants.Count),name+": all dependency rows in CSV");
        foreach(var calculation in fixture.Calculations)
        {
            foreach(var result in calculation.Result.Rows)
            {
                var wall=wallRows.Single(row=>row[1]==result.UniqueId);Near(Double.Parse(wall[10],CultureInfo.InvariantCulture),result.ResultVolumeMetres3,name+": CSV result");
                Near(Double.Parse(wall[9],CultureInfo.InvariantCulture),result.JoinDeductionVolumeMetres3,name+": CSV join deduction");
                var steps=ledger.Where(row=>row[1]==result.UniqueId).ToList();Check(steps.Count==result.Steps.Count,name+": all signed ledger steps");
                Near(steps.Sum(row=>Double.Parse(row[7],CultureInfo.InvariantCulture)),-result.JoinDeductionVolumeMetres3,name+": signed CSV ledger conservation");
            }
        }
        cases.Add(name);
    }
    private static void Preseal(string name,string output,Action<Fixture> mutate)
    {
        var fixture=new Fixture(output);mutate(fixture);Check(Refuses(delegate{fixture.Seal();}),name+": malformed input must not be blessed by a new seal");cases.Add(name);
    }
    private static void Tamper(string name,string output,Action<CivilWallJoinBatch> mutate)
    {
        var fixture=new Fixture(output);var batch=fixture.Seal();string originalInput=batch.InputFingerprint,originalResult=batch.ResultFingerprint;mutate(batch);
        Check(Refuses(delegate{CivilWallJoinSession.Validate(batch);}),name+": Validate refuses");Check(Refuses(delegate{CivilWallJoinSession.ToCsv(batch,true);}),name+": complete CSV refuses");
        Check(Refuses(delegate{CivilWallJoinSession.ToCsv(batch,false);}),name+": requested-only CSV refuses");
        Check(batch.InputFingerprint==originalInput&&batch.ResultFingerprint==originalResult,name+": failed validation does not re-seal facts");cases.Add(name);
    }
    private static void Run(string output)
    {
        Valid("paired-walls-core-through-session",new Fixture(output));Valid("three-walls-shared-intersection-session",new Fixture(output,3));
        Valid("unrequested-dependency-session",new Fixture(output,2,false));Valid("unsupported-component-keeps-coverage-with-null-quantities",new Fixture(output,2,true,true));
        var mixed=new Fixture(output);mixed.AddComponent('b',2,true,true);Valid("mixed-calculated-unsupported-components",mixed);
        var swapped=new Fixture(output,3);swapped.Recalculate(0,new[]{"wall-a-3","wall-a-2","wall-a-1"});Valid("actual-core-three-wall-priority-swap-session",swapped);
        Near(swapped.Calculations[0].Result.Rows.Single(row=>row.UniqueId=="wall-a-1").ResultVolumeMetres3,2,"Priority swap changes allocated A volume without changing native actual body");
        var hole=new Fixture(output);((Cell)hole.Inputs[0].Nodes[1].ExpectedSolid).Atoms.Remove(3);hole.Snapshot.Components[0].Evidence[1].ExpectedVolumeMetres3=3;hole.Snapshot.Components[0].Participants[1].OpeningVolumeMetres3=1;
        hole.Snapshot.Components[0].Evidence[1].Fact.Inserts.Add(new CivilWallInsert{ElementId="opening",UniqueId="opening",HostUniqueId="wall-a-2",Kind="rect-opening",Status="recognized",CutoutMethod="native-opening",RectX0=1,RectX1=2,RectZ0=0,RectZ1=1,Source="Opening.BoundaryRect"});
        hole.Recalculate(0);Valid("opening-cut-before-junction-allocation-session",hole);Near(hole.Calculations[0].Result.Rows[1].OpeningDeductionVolumeMetres3,1,"Opening remains separate");Near(hole.Calculations[0].Result.Rows[1].JoinDeductionVolumeMetres3,1,"Already opened atom not deducted again at junction");
        var zero=new Fixture(output,1);((Cell)zero.Inputs[0].Nodes[0].ExpectedSolid).Atoms.Clear();((Cell)zero.Inputs[0].Nodes[0].ActualSolid).Atoms.Clear();zero.Snapshot.Components[0].Evidence[0].ExpectedVolumeMetres3=0;zero.Snapshot.Components[0].Evidence[0].ActualVolumeMetres3=0;
        zero.Snapshot.Components[0].Participants[0].OpeningVolumeMetres3=4;zero.Snapshot.Components[0].Participants[0].NativeVolumeMetres3=0;zero.Snapshot.Components[0].Evidence[0].Fact.GeometryVolumeMetres3=0;zero.Snapshot.Components[0].Evidence[0].Fact.NativeVolume.Value=0;zero.Snapshot.NativeSnapshot.Elements[0].Volume.Value=0;
        zero.Snapshot.Components[0].Evidence[0].Fact.Inserts.Add(new CivilWallInsert{ElementId="all-opening",UniqueId="all-opening",HostUniqueId="wall-a-1",Kind="rect-opening",Status="recognized",CutoutMethod="native-opening",RectX0=0,RectX1=4,RectZ0=0,RectZ1=1,Source="Opening.BoundaryRect"});
        zero.Recalculate(0);Valid("calculated-zero-is-not-empty-session",zero);Check(Section(Csv(CivilWallJoinSession.ToCsv(zero.Seal(),true)),14).Single()[10]=="0","Legal zero prints numeric zero, unsupported remains empty");
        var unsupported=new Fixture(output,2,true,true);var unsupportedBatch=unsupported.Seal();var unsupportedRows=Section(Csv(CivilWallJoinSession.ToCsv(unsupportedBatch,true)),14);
        Check(unsupportedRows.All(row=>row[5]==""&&Enumerable.Range(6,6).All(index=>row[index]=="")&&row[13]=="unsupported"),"Unsupported CSV rows preserve empty quantities, not fabricated zeros");
        var dependency=new Fixture(output,2,false);var dependencyBatch=dependency.Seal();var all=Csv(CivilWallJoinSession.ToCsv(dependencyBatch,true));var requested=Csv(CivilWallJoinSession.ToCsv(dependencyBatch,false));
        Check(Section(all,14).Count==2&&Section(requested,14).Count==1,"Requested-only wall rows do not silently retain dependency rows");
        var fullSummary=all.Single(row=>row.Count==12&&row[0]==new string('a',64));var partSummary=requested.Single(row=>row.Count==12&&row[0]==new string('a',64));Check(fullSummary.SequenceEqual(partSummary),"Requested-only CSV retains the full component proof and conservation total");
        Check(Section(requested,8).Single()[2]=="wall-a-1","Requested wall ledger retains its unrequested higher-priority association");
        var hostile=new Fixture(output);hostile.Snapshot.Components[0].Participants[0].Name="=SUM(1,2)\n中文";hostile.Calculations[0].Plan.Name="+公式方案";hostile.Snapshot.NativeSnapshot.ScopeLabel="@范围";
        var hostileBatch=hostile.Seal();var hostileCsv=Csv(CivilWallJoinSession.ToCsv(hostileBatch,true));Check(Section(hostileCsv,14)[0][3]=="'=SUM(1,2)\n中文","CSV handles Unicode, embedded comma/newline and formula guard");Check(hostileCsv.Any(row=>row.Any(cell=>cell=="'+公式方案"))&&hostileCsv.Any(row=>row.Any(cell=>cell=="'@范围")),"Formula guard covers plan and native source metadata");

        Tamper("post-seal-fact-dimension",output,b=>b.Input.Components[0].Evidence[0].Fact.ParameterLength.Value=4.1);
        Tamper("post-seal-raw-source-volume",output,b=>b.Input.NativeSnapshot.Elements[0].Volume.Value=999);
        Tamper("post-seal-source-revision",output,b=>b.Input.NativeSnapshot.Revision++);
        Tamper("post-seal-source-scope",output,b=>b.Input.NativeSnapshot.DocumentScope="foreign");
        Tamper("post-seal-phase",output,b=>b.Input.PhaseId="other-phase");
        Tamper("post-seal-design-option",output,b=>b.Input.DesignOptionId="linked-option");
        Tamper("post-seal-result-quantity",output,b=>b.Calculations[0].Result.Rows[1].ResultVolumeMetres3+=.1);
        Tamper("post-seal-result-ledger",output,b=>b.Calculations[0].Result.Rows[1].Steps[0].DeductionVolumeMetres3+=.1);
        Tamper("post-seal-plan-rank",output,b=>b.Calculations[0].Plan.Priorities[0].Rank=2);
        Tamper("post-seal-plan-name",output,b=>b.Calculations[0].Plan.Name="renamed");
        Tamper("post-seal-binding-path",output,b=>b.Input.Components[0].Binding.SourcePath=Path.Combine(output,"other.rvt"));
        Tamper("post-seal-binding-fingerprint",output,b=>b.Input.Components[0].Binding.GeometryDependencyFingerprint=new string('f',64));
        Tamper("post-seal-requested-role",output,b=>b.Input.Components[0].Participants[0].Requested=false);
        Tamper("post-seal-wall-identity",output,b=>b.Input.Components[0].Participants[0].ElementId="wrong");
        Tamper("post-seal-batch-id",output,b=>b.BatchId=Guid.NewGuid().ToString("N"));
        Tamper("post-seal-native-list",output,b=>b.Input.NativeSnapshot.Elements.RemoveAt(0));
        Tamper("post-seal-cutout-policy",output,b=>b.Input.OpeningRule=CivilWallRuleTemplates.LegacyExplicitOpeningOnly());
        var restored=new Fixture(output);var restoredBatch=restored.Seal();double originalLength=restoredBatch.Input.Components[0].Evidence[0].Fact.ParameterLength.Value.Value;restoredBatch.Input.Components[0].Evidence[0].Fact.ParameterLength.Value=99;
        Check(Refuses(delegate{CivilWallJoinSession.Validate(restoredBatch);}),"Finite mutation first refuses");restoredBatch.Input.Components[0].Evidence[0].Fact.ParameterLength.Value=originalLength;CivilWallJoinSession.Validate(restoredBatch);Check(CivilWallJoinSession.ToCsv(restoredBatch,true).Contains(restoredBatch.BatchId),"Restoring exact original facts revalidates original seal without replacing identity");cases.Add("restore-exact-original-facts");

        Preseal("phase-binding-must-match-batch-request",output,f=>{f.Snapshot.Components[0].Binding.PhaseId="wrong";f.Calculations[0].Plan.Binding.PhaseId="wrong";});
        Preseal("native-id-must-match-request-participant",output,f=>f.Snapshot.NativeSnapshot.Elements[0].ElementId="different-source-id");
        Preseal("native-wall-coverage-cannot-drop-request",output,f=>f.Snapshot.NativeSnapshot.Elements.RemoveAt(0));
        Preseal("extra-native-wall-cannot-be-silently-skipped",output,f=>f.Snapshot.NativeSnapshot.Elements.Add(new CivilElementRecord{UniqueId="uncovered",ElementId="uncovered",KindCode="wall"}));
        Preseal("null-native-record",output,f=>f.Snapshot.NativeSnapshot.Elements.Add(null));
        Preseal("duplicate-native-request",output,f=>f.Snapshot.NativeSnapshot.Elements.Add(f.Snapshot.NativeSnapshot.Elements[0]));
        Preseal("source-scope-forgery",output,f=>f.Snapshot.NativeSnapshot.DocumentScope="other");
        Preseal("source-path-forgery",output,f=>f.Snapshot.NativeSnapshot.SourcePath=Path.Combine(output,"other.rvt"));
        Preseal("source-revision-forgery",output,f=>f.Snapshot.NativeSnapshot.Revision++);
        Preseal("wrong-view-binding",output,f=>f.Snapshot.GeometryViewUniqueId="different-view");
        Preseal("unimplemented-design-option",output,f=>f.Snapshot.DesignOptionId="other");
        Preseal("missing-phase",output,f=>f.Snapshot.PhaseId="");
        Preseal("duplicate-participant-element-id",output,f=>f.Snapshot.Components[0].Participants[1].ElementId=f.Snapshot.Components[0].Participants[0].ElementId);
        Preseal("missing-evidence",output,f=>f.Snapshot.Components[0].Evidence.RemoveAt(0));
        Preseal("duplicate-evidence",output,f=>f.Snapshot.Components[0].Evidence[1]=f.Snapshot.Components[0].Evidence[0]);
        Preseal("foreign-extra-evidence",output,f=>f.Snapshot.Components[0].Evidence.Add(new CivilWallJoinWallEvidence{Fact=new CivilWallFact{UniqueId="foreign",ElementId="foreign"}}));
        Preseal("null-evidence-fact",output,f=>f.Snapshot.Components[0].Evidence[0].Fact=null);
        Preseal("evidence-element-id-forgery",output,f=>f.Snapshot.Components[0].Evidence[0].Fact.ElementId="foreign");
        Preseal("unsupported-facts-cannot-claim-calculated",output,f=>f.Snapshot.Components[0].Status="unsupported");
        Preseal("legacy-single-wall-proof-cannot-replace-component-prepared",output,f=>{f.Snapshot.Components[0].Evidence[0].Fact.GeometryVerified=true;f.Snapshot.Components[0].Evidence[0].Fact.Status="verified";});
        Preseal("evidence-retains-unresolved-issue",output,f=>f.Snapshot.Components[0].Evidence[0].Issue="unknown source");
        Preseal("nonfinite-world-coordinate",output,f=>f.Snapshot.Components[0].Evidence[0].GrossBoundsFeet[0]=Double.NaN);
        Preseal("wrong-coordinate-dimension",output,f=>f.Snapshot.Components[0].Evidence[0].OriginFeet=new[]{0.0,1.0});
        Preseal("nonfinite-normal-offset",output,f=>f.Snapshot.Components[0].Evidence[0].NormalMinFeet=Double.PositiveInfinity);
        Preseal("failed-component-does-not-commit",output,f=>f.Calculations[0].Result.Status="failed");
        Preseal("cancelled-component-does-not-commit",output,f=>f.Calculations[0].Result.Status="cancelled");
        Preseal("incomplete-component-calculation-coverage",output,f=>f.Calculations.Clear());
        Preseal("duplicate-component-calculation",output,f=>f.Calculations.Add(f.Calculations[0]));
        Preseal("foreign-component-result",output,f=>f.Calculations[0].ComponentId="foreign");
        Preseal("unimplemented-component-method",output,f=>f.Calculations[0].Result.MethodId="unimplemented");
        Preseal("unimplemented-opening-method",output,f=>f.Snapshot.OpeningRule.MethodId=CivilWallJoinCore.MethodId);
        Preseal("nonconserving-result-row",output,f=>f.Calculations[0].Result.Rows[1].ResultVolumeMetres3+=.1);
        Preseal("nonconserving-ledger-step",output,f=>f.Calculations[0].Result.Rows[1].Steps[0].DeductionVolumeMetres3+=.1);
        Preseal("foreign-step-association",output,f=>f.Calculations[0].Result.Rows[1].Steps[0].HigherUniqueId="foreign");
        Preseal("incomplete-ledger-coverage",output,f=>f.Calculations[0].Result.Rows[1].Steps.Clear());
        Preseal("shape-proof-above-tolerance",output,f=>f.Calculations[0].Result.SymmetricDifferenceMetres3=1);
        Preseal("residual-overlap-proof-above-tolerance",output,f=>f.Calculations[0].Result.ResidualOverlapMetres3=1);
        Preseal("tolerance-cannot-be-expanded",output,f=>f.Calculations[0].Result.ToleranceMetres3=1);
        Preseal("nan-result-quantity",output,f=>f.Calculations[0].Result.Rows[0].GrossVolumeMetres3=Double.NaN);
        Preseal("negative-result-quantity",output,f=>f.Calculations[0].Result.Rows[0].OpeningDeductionVolumeMetres3=-1);

        // These are semantic source/proof gates rather than hash-format tests.
        Preseal("legacy-policy-cannot-use-family-cutout-proof",output,f=>{f.Snapshot.OpeningRule=CivilWallRuleTemplates.LegacyExplicitOpeningOnly();f.Snapshot.Components[0].Evidence[0].Fact.Inserts.Add(new CivilWallInsert{ElementId="door",UniqueId="door",HostUniqueId="wall-a-1",Kind="rect-opening",Status="recognized",CutoutMethod="family-rect-cutout",RectX0=1,RectX1=2,RectZ0=0,RectZ1=1,Source="official actual family cutout"});});
        Preseal("negative-operation-count",output,f=>f.Calculations[0].Result.Operations=-1);
        Preseal("operation-count-above-core-cap",output,f=>f.Calculations[0].Result.Operations=20001);
        Preseal("context-id-must-be-geometry-bound",output,f=>{f.Snapshot.Components[0].ComponentId="arbitrary-component";f.Calculations[0].ComponentId="arbitrary-component";f.Calculations[0].Result.ContextKey="arbitrary-component";});
        Preseal("missing-parameter-source",output,f=>f.Snapshot.Components[0].Evidence[0].Fact.ParameterLength.Source="");
        Preseal("parameter-gross-must-match-core-row",output,f=>f.Snapshot.Components[0].Evidence[0].Fact.ParameterWidth.Value=2);
        Preseal("nonfinite-parameter-dimension",output,f=>f.Snapshot.Components[0].Evidence[0].Fact.ParameterHeight.Value=Double.NaN);
        Preseal("wrong-parameter-unit",output,f=>f.Snapshot.Components[0].Evidence[0].Fact.ParameterLength.Unit="ft");
        Preseal("unavailable-parameter-dimension",output,f=>f.Snapshot.Components[0].Evidence[0].Fact.ParameterLength.Status="missing");
        Preseal("missing-opening-facts",output,f=>f.Snapshot.Components[0].Evidence[0].Fact.Inserts=null);
        Preseal("unknown-insert-method",output,f=>f.Snapshot.Components[0].Evidence[0].Fact.Inserts.Add(new CivilWallInsert{UniqueId="void",ElementId="void",HostUniqueId="wall-a-1",Status="recognized",Kind="rect-opening",CutoutMethod="unknown"}));
        Preseal("unsupported-insert-proof",output,f=>f.Snapshot.Components[0].Evidence[0].Fact.Inserts.Add(new CivilWallInsert{UniqueId="void",ElementId="void",HostUniqueId="wall-a-1",Status="unsupported",Kind="other"}));
    }
    private static int Main(string[] args)
    {
        string output=args[0];Directory.CreateDirectory(output);Run(output);File.WriteAllLines(Path.Combine(output,"cases.txt"),cases,new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output,"report.json"),"{\"result\":\"PASS\",\"assertions\":"+checks+",\"caseCount\":"+cases.Count+",\"scope\":\"Actual project-owned core calculation with exact atom operands, then production session sealing and CSV. No Revit, native CSG, window or desktop input.\",\"revitStarted\":false,\"nativeDesktopInput\":false}",new UTF8Encoding(false));Console.WriteLine("PASS "+checks+" wall join-session assertions across "+cases.Count+" cases.");return 0;
    }
}
