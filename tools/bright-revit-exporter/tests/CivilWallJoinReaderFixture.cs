// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BrightVision.Civil;
using BrightVision.Export;
using BrightVision.Revit;

internal static class CivilWallJoinReaderFixture
{
    private const double M=.3048,M3=M*M*M;
    private static int checks;
    private static readonly List<string> cases=new List<string>();
    private static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    private static void Near(double actual,double expected,string message){Check(Math.Abs(actual-expected)<1e-9,message);}
    private static bool Refuses(Action action){try{action();return false;}catch(OperationCanceledException){return true;}catch(InvalidOperationException){return true;}}
    private sealed class Fixture
    {
        internal Document Document=new Document();internal Phase Phase;internal View3D View;internal UIDocument Ui;internal readonly List<Wall> Walls=new List<Wall>();private int serial=1000;
        internal Fixture(){Phase=Document.Add(new Phase{Name="New"},1);View=Document.Add(new View3D{Name="Geometry"},2);View.Parameters[BuiltInParameter.VIEW_PHASE]=new Parameter{StorageType=StorageType.ElementId,Id=Phase.Id};Ui=new UIDocument{Document=Document,ActiveView=View};Document.Add(new Level{ProjectElevation=0},3);}
        internal Wall Wall(double x0,double x1,double y0,double y1,bool vertical=false)
        {
            var actual=Solid.Shape(new BoxShape(x0,x1,y0,y1,0,3));var wall=Document.Add(new Wall{Name="Wall "+serial,Geometry=new GeometryElement{actual}},serial++);
            WallType type=Document.Add(new WallType{Name="Basic",Kind=WallKind.Basic,Width=vertical?x1-x0:y1-y0},serial++);wall.WallType=type;
            wall.Location=new LocationCurve{Curve=vertical?Line.CreateBound(new XYZ((x0+x1)/2,y0,0),new XYZ((x0+x1)/2,y1,0)):Line.CreateBound(new XYZ(x0,(y0+y1)/2,0),new XYZ(x1,(y0+y1)/2,0))};
            wall.Parameters[BuiltInParameter.CURVE_ELEM_LENGTH]=P(vertical?y1-y0:x1-x0);wall.Parameters[BuiltInParameter.WALL_USER_HEIGHT_PARAM]=P(3);wall.Parameters[BuiltInParameter.WALL_BASE_OFFSET]=P(0);
            wall.Parameters[BuiltInParameter.WALL_TOP_IS_ATTACHED]=new Parameter{StorageType=StorageType.Integer,Integer=0};wall.Parameters[BuiltInParameter.WALL_BOTTOM_IS_ATTACHED]=new Parameter{StorageType=StorageType.Integer,Integer=0};
            wall.Parameters[BuiltInParameter.WALL_HEIGHT_TYPE]=new Parameter{StorageType=StorageType.ElementId,Id=new ElementId(-1)};wall.Parameters[BuiltInParameter.WALL_BASE_CONSTRAINT]=new Parameter{StorageType=StorageType.ElementId,Id=new ElementId(3)};
            wall.Parameters[BuiltInParameter.HOST_VOLUME_COMPUTED]=P(actual.Volume);wall.Parameters[BuiltInParameter.HOST_AREA_COMPUTED]=P((vertical?y1-y0:x1-x0)*3);
            wall.Box=new BoundingBoxXYZ{Min=new XYZ(x0,y0,0),Max=new XYZ(x1,y1,3)};Walls.Add(wall);return wall;
        }
        internal CivilSnapshot Snapshot(params Wall[] requested)
        {var snapshot=new CivilSnapshot{ModelTitle=Document.Title,DocumentScope=PackageWriter.Hash(Document.ProjectInformation.UniqueId).Substring(0,24),SourcePath=Document.PathName,Revision=Document.Revision,ScopeLabel="selection"};foreach(Wall wall in requested)snapshot.Elements.Add(new CivilElementRecord{UniqueId=wall.UniqueId,ElementId=wall.Id.IntegerValue.ToString(),Name=wall.Name,KindCode="wall",Supported=true,Length=Metric(wall,BuiltInParameter.CURVE_ELEM_LENGTH,"m",M),Area=Metric(wall,BuiltInParameter.HOST_AREA_COMPUTED,"m2",M*M),Volume=Metric(wall,BuiltInParameter.HOST_VOLUME_COMPUTED,"m3",M3)});return snapshot;}
        internal RevitWallJoinReadBundle Read(Wall[] requested,CivilWallReadControl control=null,CivilWallRuleTemplate rule=null)
        {return RevitWallJoinReader.Read(Ui,Snapshot(requested),new CivilReadRequest{Scope="selection",PhaseId="1",DesignOptionId="-1"},View,rule??CivilWallRuleTemplates.Default(),control);}
        internal Opening Hole(Wall wall,double x0,double x1,double z0,double z1)
        {var opening=Document.Add(new Opening{Host=wall,BoundaryRect=new[]{new XYZ(x0,(wall.Box.Min.Y+wall.Box.Max.Y)/2,z0),new XYZ(x1,(wall.Box.Min.Y+wall.Box.Max.Y)/2,z1)}},serial++);wall.Inserts.Add(opening.Id);Solid cut=Solid.Shape(new BoxShape(x0,x1,wall.Box.Min.Y-1,wall.Box.Max.Y+1,z0,z1));wall.Geometry=new GeometryElement{BooleanOperationsUtils.ExecuteBooleanOperation((Solid)wall.Geometry[0],cut,BooleanOperationsType.Difference)};return opening;}
        internal void Join(Wall a,Wall b){a.Joined.Add(b.Id);b.Joined.Add(a.Id);}
    }
    private static Parameter P(double value){return new Parameter{StorageType=StorageType.Double,Number=value};}
    private static CivilMetric Metric(Wall wall,BuiltInParameter key,string unit,double factor){return new CivilMetric{Status="available",Value=wall.Parameters[key].Number*factor,Unit=unit,Source=key.ToString()};}
    private static CivilWallJoinComponentResult Calculate(RevitWallJoinReadBundle bundle,params Wall[] priority)
    {var component=bundle.Components.Single();foreach(CivilWallJoinNode node in component.Input.Nodes)node.PriorityRank=Array.FindIndex(priority,wall=>wall.UniqueId==node.UniqueId)+1;return CivilWallJoinCore.Calculate(component.Input,bundle.Operations);}
    private static void DisposeExactlyOnce(RevitWallJoinReadBundle bundle,IEnumerable<Solid> source)
    {Solid[] owned=bundle.Owned.ToArray();Check(owned.Length>0,"Read owns native copies and constructed operands");bundle.Dispose();Check(bundle.IsDisposed&&bundle.Owned.Count==0,"Bundle lifetime closes deterministically");Check(owned.All(solid=>solid.DisposeCalls==1),"Every owned native copy is disposed exactly once");Check(source.All(solid=>solid.DisposeCalls==0),"Borrowed document solids are not individually disposed");bundle.Dispose();Check(owned.All(solid=>solid.DisposeCalls==1),"Repeated bundle disposal is idempotent");}
    private static void Run()
    {
        var f=new Fixture();Wall a=f.Wall(0,10,0,1),b=f.Wall(4,5,-4,6,true);Solid sourceA=(Solid)a.Geometry[0],sourceB=(Solid)b.Geometry[0];View original=f.Ui.ActiveView;
        var bundle=f.Read(new[]{a});Check(bundle.Components.Count==1&&bundle.Components[0].Status=="ready","Two unjoined crossing walls form one ready component");var component=bundle.Components[0];
        Check(component.Participants.Count==2&&component.Participants.Count(p=>p.Requested)==1,"Unrequested overlapping neighbor remains in full dependency component");
        Check(component.Evidence.All(e=>e.Fact.Status=="component-prepared"&&!e.Fact.GeometryVerified),"Native preparation does not pretend legacy per-wall proof or completed component calculation");
        Check(component.Input.Nodes.All(n=>!n.PriorityRank.HasValue),"Adapter never assigns business priority from element IDs");
        Check(bundle.Owned.Count==4,"Completed read retains only each component's E/A operands, not transient Boolean or unrelated candidate geometry");
        Check(component.Participants.All(p=>p.GrossVolumeMetres3.HasValue&&p.OpeningVolumeMetres3==0),"Known zero opening deduction remains a legal zero");
        string fingerprint=component.Binding.GeometryDependencyFingerprint;var result=Calculate(bundle,a,b);Check(result.Status=="calculated","Prepared exact crossing component passes independent core");Near(result.SumResultVolumeMetres3.Value,57*M3,"Cross component union volume 57 cubic feet");Near(result.Rows.Single(r=>r.UniqueId==a.UniqueId).ResultVolumeMetres3,30*M3,"Higher-priority wall owns intersection");Near(result.Rows.Single(r=>r.UniqueId==b.UniqueId).ResultVolumeMetres3,27*M3,"Lower-priority wall deducts three cubic feet once");Check(Object.ReferenceEquals(f.Ui.ActiveView,original),"No active view is changed");DisposeExactlyOnce(bundle,new[]{sourceA,sourceB});
        using(var all=f.Read(new[]{b,a})){Check(all.Components[0].Binding.GeometryDependencyFingerprint==fingerprint,"Component identity is independent of selection scope and source order");Check(all.Components[0].Participants.All(p=>p.Requested),"Selection changes only requested report flags");var swapped=Calculate(all,b,a);Near(swapped.Rows.Single(r=>r.UniqueId==b.UniqueId).ResultVolumeMetres3,30*M3,"Explicit priority swap changes ownership");Near(swapped.Rows.Single(r=>r.UniqueId==a.UniqueId).ResultVolumeMetres3,27*M3,"Same complete union survives priority swap");}
        f.Document.Revision=5;using(var reopened=f.Read(new[]{a})){Check(reopened.Components[0].Binding.GeometryDependencyFingerprint==fingerprint,"Volatile process revision does not change unchanged component geometry identity");Check(reopened.Components[0].Binding.SourceRevision==5,"Live binding still records and guards the current native revision");}
        f.Document.Revision=6;a.Parameters[BuiltInParameter.CURVE_ELEM_LENGTH].Number=11;((LocationCurve)a.Location).Curve=Line.CreateBound(new XYZ(0,.5,0),new XYZ(11,.5,0));a.Geometry=new GeometryElement{Solid.Shape(new BoxShape(0,11,0,1,0,3))};a.Box.Max=new XYZ(11,1,3);a.Parameters[BuiltInParameter.HOST_VOLUME_COMPUTED].Number=33;a.Parameters[BuiltInParameter.HOST_AREA_COMPUTED].Number=33;
        using(var changed=f.Read(new[]{a})){Check(changed.Components[0].Binding.GeometryDependencyFingerprint!=fingerprint,"Changed actual/basis geometry invalidates a persistent component plan");Check(changed.Components[0].Binding.SourceRevision==6,"Geometry-changed binding also retains current revision");}
        cases.Add("unjoined-cross/closure/scope-invariance/priority/ownership");

        f=new Fixture();a=f.Wall(0,10,0,1);b=f.Wall(4,5,-4,.5,true);using(var tee=f.Read(new[]{a})){Check(tee.Components[0].Participants.Count==2,"T junction includes its unrequested wall");var teeResult=Calculate(tee,a,b);Check(teeResult.Status=="calculated","T junction is proved from full shapes");Near(teeResult.SumResultVolumeMetres3.Value,42*M3,"T junction union has independent 42-cubic-foot expectation");Near(teeResult.Rows.Single(r=>r.UniqueId==b.UniqueId).JoinDeductionVolumeMetres3,1.5*M3,"T junction deducts only 1.5-cubic-foot shared region");}
        f=new Fixture();a=f.Wall(0,10,0,1);b=f.Wall(4,5,-4,6,true);Wall triple=f.Wall(4,5,0,1);using(var three=f.Read(new[]{a})){Check(three.Components[0].Participants.Count==3,"Triple shared region has complete recursive closure");var tripleResult=Calculate(three,triple,b,a);Check(tripleResult.Status=="calculated","Triple component proves and allocates");Near(tripleResult.SumResultVolumeMetres3.Value,57*M3,"Triple shared region does not inflate component union");var last=tripleResult.Rows.Single(r=>r.UniqueId==a.UniqueId);Near(last.Steps[0].DeductionVolumeMetres3,3*M3,"First high-priority shared owner consumes intersection");Near(last.Steps[1].DeductionVolumeMetres3,0,"Second high-priority overlap already consumed is not deducted again");}
        cases.Add("T-junction/triple-shared-region-once");

        f=new Fixture();a=f.Wall(0,10,0,1);b=f.Wall(10,20,0,1);Wall c=f.Wall(100,110,0,1);
        using(var separated=f.Read(new[]{a,c})){Check(separated.Components.Count==2&&separated.Components.All(x=>x.Participants.Count==1),"BBox touching and remote walls do not become positive-volume dependencies");Check(!separated.Components.SelectMany(x=>x.Participants).Any(p=>p.ElementUniqueId==b.UniqueId),"Unrequested merely touching wall is excluded after exact intersection");}
        cases.Add("touching-bounds-are-candidates-only");

        f=new Fixture();a=f.Wall(0,10,0,1);b=f.Wall(4,5,-4,6,true);f.Join(a,b);b.Geometry=new GeometryElement{BooleanOperationsUtils.ExecuteBooleanOperation((Solid)b.Geometry[0],(Solid)a.Geometry[0],BooleanOperationsType.Difference)};
        using(var joined=f.Read(new[]{b})){
#if REVIT_2015
            Check(joined.Components[0].Status=="unsupported","Legacy API without sketch/dependent provenance refuses per-wall native differences");Check(joined.Components[0].Evidence.Single(e=>e.Fact.UniqueId==b.UniqueId).Fact.SymmetricDifferenceMetres3>0,"Legacy unproved difference remains visible");Check(Calculate(joined,b,a).Rows.Count==0,"Legacy missing provenance never becomes calculated quantity");
#else
            Check(joined.Components[0].Status=="ready","Explicitly trimmed wall missing region is proved inside joined neighbor");Check(joined.Components[0].Evidence.Single(e=>e.Fact.UniqueId==b.UniqueId).Fact.SymmetricDifferenceMetres3>0,"Per-wall native difference is retained instead of rewritten to zero");Check(Calculate(joined,b,a).Status=="calculated","Component union proves explicit join before project priority allocation");
#endif
        }
        f.Document.Add(new Sketch(),50000);b.Dependents.Add(new ElementId(50000));using(var profile=f.Read(new[]{b})){Check(profile.Components[0].Status=="unsupported","Native profile sketch cannot hide inside a valid neighboring union");Check(Calculate(profile,b,a).Rows.Count==0,"Unsupported profile invalidates whole component");}
        cases.Add("explicit-join/missing-region-proof/edited-profile-refusal");

        f=new Fixture();a=f.Wall(0,10,0,1);b=f.Wall(4,5,-4,6,true);f.Join(a,b);a.Geometry=new GeometryElement{Solid.Shape(new BoxShape(0,7,0,1,0,3),new BoxShape(8,10,0,1,0,3))};using(var badMissing=f.Read(new[]{a})){Check(badMissing.Components[0].Status=="unsupported","Missing native shape outside explicit neighbor gross cannot be explained away by any join");}
        f=new Fixture();a=f.Wall(0,10,0,1);b=f.Wall(100,110,0,1);((LocationCurve)b.Location).Curve=new Arc();b.Joined.Add(a.Id);using(var incoming=f.Read(new[]{a})){Check(incoming.Components[0].Participants.Count==2&&incoming.Components[0].Status=="unsupported","Far unsupported wall with incoming-only explicit join is retained and refuses entire component");Check(incoming.Components[0].Evidence.Single(e=>e.Fact.UniqueId==a.UniqueId).ExplicitWallJoinUniqueIds.Contains(b.UniqueId),"Directory edges are symmetrized before bbox filtering");}
        f=new Fixture();a=f.Wall(0,10,0,1);b=f.Wall(100,110,0,1);((LocationCurve)b.Location).Curve=new Arc();using(var farUnknown=f.Read(new[]{a})){Check(farUnknown.Components[0].Status=="ready"&&farUnknown.Components[0].Participants.Count==1,"Unrelated bounded arc wall is explicitly excluded rather than globally poisoning a supported component");Check(farUnknown.ExcludedUnknownGrossEnvelopeCount==1&&farUnknown.Notices.Any(n=>n.Contains("未知")),"Unknown-gross exclusion scope and count are recorded");}
        cases.Add("missing-cut-outside-join/incoming-unsupported-link/unrelated-arc-scope");

        f=new Fixture();a=f.Wall(0,10,0,1);Opening hole=f.Hole(a,1,3,1,2);using(var opening=f.Read(new[]{a})){Check(opening.Components[0].Status=="ready","Known native rectangle cutout is retained in join preparation");Near(opening.Components[0].Participants[0].OpeningVolumeMetres3.Value,2*M3,"Opening participant quantity is deduction, not remaining solid");Near(Calculate(opening,a).Rows[0].ResultVolumeMetres3,28*M3,"Rectangle opening is deducted once before joins");}
        a.CuttingVoids.Add(new ElementId(60000));using(var unknown=f.Read(new[]{a})){Check(unknown.Components[0].Status=="unsupported"&&unknown.Components[0].Message.Contains("空心族"),"Unconfirmed unattached void is not hidden by union proof");Check(!unknown.Components[0].Participants[0].OpeningVolumeMetres3.HasValue,"Unproved cutout does not get fabricated zero");}
        cases.Add("confirmed-opening/unknown-void-rejection");

        f=new Fixture();a=f.Wall(0,10,0,1);Element external=f.Document.Add(new Element(),70000);a.Joined.Add(external.Id);using(var unsupported=f.Read(new[]{a})){Check(unsupported.Components[0].Status=="unsupported","External non-wall explicit join refuses calculation");}
        f=new Fixture();a=f.Wall(0,10,0,1);a.Geometry=new GeometryElement{Solid.Shape(new BoxShape(0,11,0,1,0,3))};a.Box.Max=new XYZ(11,1,3);using(var extended=f.Read(new[]{a})){Check(extended.Components[0].Status=="unsupported","Native end extension outside independent gross prism is refused");}
        f=new Fixture();a=f.Wall(0,10,0,1);a.Geometry=new GeometryElement{Solid.Shape(new BoxShape(0,4,0,1,0,3),new BoxShape(5,10,0,1,0,3))};using(var missing=f.Read(new[]{a})){Check(missing.Components[0].Status=="unsupported","Unjoined missing shape cannot be accepted from another wall");}
        cases.Add("non-wall-join/end-extension/unjoined-shape-guard");

        f=new Fixture();a=f.Wall(0,10,0,1);b=f.Wall(100,110,0,1);b.Box=null;Check(Refuses(delegate{using(var read=f.Read(new[]{a})){};}),"Missing remote bounds fail complete directory instead of excluding a possible dependency");
        f=new Fixture();a=f.Wall(0,10,0,1);bool cancel=false;int copies=SolidUtils.Copies.Count;sourceA=(Solid)a.Geometry[0];
        Check(Refuses(delegate{using(var read=f.Read(new[]{a},new CivilWallReadControl{IsCancellationRequested=()=>cancel,Progress=delegate(string stage,int done,int total){if(stage=="交接原生实体操作后")cancel=true;}})){};}),"Cancellation at native operation boundary returns no bundle");Check(SolidUtils.Copies.Skip(copies).All(s=>s.DisposeCalls==1)&&sourceA.DisposeCalls==0,"Cancellation releases the just-cloned owned native solid, not its borrowed source");
        f=new Fixture();a=f.Wall(0,10,0,1);copies=SolidUtils.Copies.Count;sourceA=(Solid)a.Geometry[0];Check(Refuses(delegate{using(var read=f.Read(new[]{a},new CivilWallReadControl{Progress=delegate(string stage,int done,int total){if(stage=="完成交接依赖读取")f.Document.Revision++;}})){};}),"Final progress callback cannot return a stale revision");Check(SolidUtils.Copies.Skip(copies).All(s=>s.DisposeCalls==1)&&sourceA.DisposeCalls==0,"Revision failure releases prepared copies");
        cases.Add("directory-failure/cancel/final-revision-atomicity");

        f=new Fixture();a=f.Wall(0,10,0,1);sourceA=(Solid)a.Geometry[0];SolidUtils.ReturnInputAlias=true;try{using(var alias=f.Read(new[]{a})){Check(alias.Components[0].Status=="unsupported","Aliasing native clone is rejected");}}finally{SolidUtils.ReturnInputAlias=false;}Check(sourceA.DisposeCalls==0,"Rejected clone alias never enters owned disposal list");
        f=new Fixture();a=f.Wall(0,10,0,1);for(int i=0;i<65;i++)f.Wall(0,10,0,1);Check(Refuses(delegate{using(var read=f.Read(new[]{a})){};}),"Candidate cap never truncates overlaps then calculates");
        f=new Fixture();a=f.Wall(0,10,0,1);for(int i=1;i<65;i++)f.Wall(i*9,i*9+10,0,1);Check(Refuses(delegate{using(var read=f.Read(new[]{a})){};}),"Recursive component cap rejects a long chain rather than returning a selected prefix");
        cases.Add("alias-safety/candidate-and-component-limits");
#if REVIT_2015
        f=new Fixture();a=f.Wall(0,10,0,1);sourceA=(Solid)a.Geometry[0];int splitStart=SolidUtils.Copies.Count;SolidUtils.SplitCount=2;try{using(var split=f.Read(new[]{a})){Check(split.Components[0].Status=="unsupported","Legacy split-copy refuses multiple outputs instead of discarding all but the first");}}finally{SolidUtils.SplitCount=1;}Check(sourceA.DisposeCalls==0&&SolidUtils.Copies.Skip(splitStart).All(s=>s.DisposeCalls==1),"Refused legacy split copies are all disposed without disposing the input");cases.Add("legacy-split-copy-provenance-and-multiple-volume-refusal");
#endif
    }
    private static int Main(string[] args)
    {
        Run();File.WriteAllLines(Path.Combine(args[0],"cases.txt"),cases);Console.WriteLine("PASS "+checks+" wall join reader assertions; project-owned exact axis-aligned box-set API stand-ins only.");return checks;
    }
}
