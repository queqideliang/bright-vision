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

internal static class CivilWallReaderFixture
{
    private static int assertions;
    private sealed class Fixture
    {
        internal readonly Document Document=new Document();internal readonly UIDocument Ui;internal readonly CivilSnapshot Snapshot;
        internal readonly CivilReadRequest Request=new CivilReadRequest{Scope="project",PhaseId="1",DesignOptionId="-1"};
        internal readonly Wall Wall;internal readonly View3D View;internal readonly CivilElementRecord Source;
        internal Fixture()
        {
            Document.Add(new Phase(),1);var level=Document.Add(new Level(),2);View=Document.Add(new View3D(),3);
            View.Parameters[BuiltInParameter.VIEW_PHASE]=IdParameter(1);
            Wall=Document.Add(new Wall{WallType=Document.Add(new WallType{Kind=WallKind.Basic,Name="test basic"},11),Location=new LocationCurve{Curve=Line.CreateBound(new XYZ(0,0,0),new XYZ(10,0,0))},Geometry=new GeometryElement{Solid.Actual(100,2,3)}},10);
            Wall.Parameters[BuiltInParameter.CURVE_ELEM_LENGTH]=DoubleParameter(10);Wall.Parameters[BuiltInParameter.WALL_USER_HEIGHT_PARAM]=DoubleParameter(10);
            Wall.Parameters[BuiltInParameter.WALL_BASE_OFFSET]=DoubleParameter(0);Wall.Parameters[BuiltInParameter.WALL_BASE_CONSTRAINT]=IdParameter(level.Id.IntegerValue);
            Wall.Parameters[BuiltInParameter.WALL_HEIGHT_TYPE]=IdParameter(-1);
            Wall.Parameters[BuiltInParameter.WALL_TOP_IS_ATTACHED]=IntegerParameter(0);Wall.Parameters[BuiltInParameter.WALL_BOTTOM_IS_ATTACHED]=IntegerParameter(0);
            Ui=new UIDocument{Document=Document,ActiveView=View};
            Source=new CivilElementRecord{ElementId="10",UniqueId=Wall.UniqueId,Supported=true,KindCode="wall",Volume=new CivilMetric{Status="available",Value=999,Unit="m3",Source="HOST_VOLUME_COMPUTED"}};
            Snapshot=new CivilSnapshot{DocumentScope=PackageWriter.Hash(Document.ProjectInformation.UniqueId).Substring(0,24),SourcePath=Document.PathName,Revision=0};Snapshot.Elements.Add(Source);
        }
        internal CivilWallFact Read(){return RevitCivilWallReader.Read(Ui,Snapshot,Request,View).Walls.Single();}
        internal Opening Opening(int id){var o=Document.Add(new Opening{Host=Wall,BoundaryRect=new[]{new XYZ(2,2.5,3),new XYZ(4,2.5,5)}},id);Wall.Inserts.Add(o.Id);return o;}
    }
    private static Parameter DoubleParameter(double value){return new Parameter{StorageType=StorageType.Double,Number=value};}
    private static Parameter IntegerParameter(int value){return new Parameter{StorageType=StorageType.Integer,Integer=value};}
    private static Parameter IdParameter(int value){return new Parameter{StorageType=StorageType.ElementId,Id=new ElementId(value)};}
    private static void Require(bool value,string message){if(!value)throw new Exception(message);assertions++;}
    private static void Near(double actual,double expected,string message){Require(Math.Abs(actual-expected)<=1e-9,message);}
    private static void Unsupported(Action<Fixture> edit,string name)
    {
        var f=new Fixture();edit(f);var fact=f.Read();Require(!fact.GeometryVerified&&fact.Status=="unsupported"&&fact.Issues.Count>0,name);
    }
    private static void Reject(Action<Fixture> edit,string name)
    {
        var f=new Fixture();edit(f);try{f.Read();throw new Exception("Expected rejection: "+name);}catch(InvalidOperationException){assertions++;}
    }
    private static FamilyInstance Door(Fixture f,int id)
    {
        var door=f.Document.Add(new FamilyInstance{Host=f.Wall,Category=new Category{Id=new ElementId((int)BuiltInCategory.OST_Doors)}},id);
        f.Wall.Inserts.Add(door.Id);return door;
    }
    private static string NativeCsv(Fixture f){return CivilSchedule.ToCsv(f.Snapshot,f.Snapshot.Elements,"element");}
    private static void CancelAt(Fixture f,string stage,int completed,string name)
    {
        string before=NativeCsv(f);var oldComplete=new CivilWallSnapshot{NativeSnapshot=new CivilSnapshot{ModelTitle="previous complete batch"}};
        CivilWallSnapshot committed=oldComplete,candidate=null;bool cancelled=false,sawStage=false;
        var control=new CivilWallReadControl{
            IsCancellationRequested=delegate{return cancelled;},
            Progress=delegate(string current,int done,int total){if(current==stage&&done==completed){sawStage=true;cancelled=true;}}
        };
        try{candidate=RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View,CivilWallRuleTemplates.Default(),control);committed=candidate;throw new Exception("Cancellation not propagated: "+name);}
        catch(OperationCanceledException){assertions++;}
        Require(sawStage&&candidate==null&&Object.ReferenceEquals(committed,oldComplete),name+" returns no partial snapshot to replace last complete batch");
        Require(NativeCsv(f)==before,name+" retains all native source fields");
    }
    private static int Main()
    {
        var f=new Fixture();var fact=f.Read();
        Require(fact.GeometryVerified&&fact.Status=="verified","Known no-opening fake-CSG adapter path");
        Near(fact.ParameterLength.Value.Value,3.048,"Length SI");Near(fact.ParameterHeight.Value.Value,3.048,"Height SI");Near(fact.ParameterWidth.Value.Value,0.3048,"Type width SI");
        Near(fact.GeometryVolumeMetres3.Value,2.8316846592,"Geometry SI");Near(fact.SymmetricDifferenceMetres3.Value,0,"Both difference branches");
        Require(fact.GeometryToleranceMetres3.Value>0&&fact.GeometryToleranceMetres3.Value<=1e-6,"Recorded tight tolerance");
        Require(fact.InterwallOverlapCheckComplete&&fact.OverlapCandidateCount==0&&fact.IntersectingWallElementIds.Count==0,"No-candidate independence proof complete");
        Require(Object.ReferenceEquals(fact.NativeVolume,f.Source.Volume)&&f.Source.Volume.Value==999,"Native metric is separate and unchanged");
        Require(f.Source.ProjectIssues.Count==0&&f.Source.Issues.Count==0,"Source annotations untouched");
        Require(Object.ReferenceEquals(f.Ui.ActiveView,f.View)&&Object.ReferenceEquals(f.Wall.LastOptions.View,f.View),"Explicit view used without ActiveView write");
        Require(fact.BaseElevationSource=="Level.ProjectElevation + WALL_BASE_OFFSET","Base provenance");
        f=new Fixture();var opening=f.Opening(20);f.Wall.Geometry=new GeometryElement{Solid.Actual(96,2,3)};fact=f.Read();
        Require(fact.GeometryVerified&&fact.Inserts.Count==1,"Explicit rectangular opening recognized");
        var item=fact.Inserts[0];Require(item.Kind=="rect-opening"&&item.Status=="recognized"&&item.HostUniqueId==f.Wall.UniqueId,"Host and kind");
        Near(item.RectX0.Value,0.6096,"Opening axis lower");Near(item.RectX1.Value,1.2192,"Opening axis upper");Near(item.RectZ0.Value,0.9144,"Opening base relative lower");
        Require(item.Source.Contains("BoundaryRect"),"Opening provenance");
        Require(RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View).GeometryViewUniqueId==f.View.UniqueId,"Explicit geometry view identity recorded");
        f=new Fixture();opening=f.Opening(20);opening.Phaseable=false;opening.PhaseStatus=ElementOnPhaseStatus.Demolished;f.Wall.Geometry=new GeometryElement{Solid.Actual(96,2,3)};fact=f.Read();
        Require(fact.GeometryVerified&&fact.Inserts[0].Source.Contains("HasPhases=false"),"Nonphaseable native opening uses confirmed host lifecycle explicitly");
        f=new Fixture();f.Opening(20);f.Wall.Inserts.Add(new ElementId(20));f.Wall.Geometry=new GeometryElement{Solid.Actual(96,2,3)};
        Require(f.Read().Inserts.Count==1,"Repeated insert reference deduplicated");
        Unsupported(x=>x.Source.Supported=false,"Unsupported source classification");
        Unsupported(x=>x.Wall.WallType.Kind=WallKind.Curtain,"Curtain wall");
        Unsupported(x=>x.Wall.IsStackedWallMember=true,"Stacked member");
        Unsupported(x=>x.Wall.Parameters[BuiltInParameter.WALL_TOP_IS_ATTACHED]=IntegerParameter(1),"Top attachment");
        Unsupported(x=>x.Wall.Parameters.Remove(BuiltInParameter.WALL_BOTTOM_IS_ATTACHED),"Unknown bottom attachment");
        Unsupported(x=>x.Wall.Parameters[BuiltInParameter.WALL_HEIGHT_TYPE]=IdParameter(2),"Connected top");
        Unsupported(x=>x.Wall.Parameters[BuiltInParameter.WALL_USER_HEIGHT_PARAM]=DoubleParameter(0),"Zero height");
        Unsupported(x=>x.Wall.Parameters[BuiltInParameter.CURVE_ELEM_LENGTH]=DoubleParameter(Double.NaN),"Nonfinite length");
        Unsupported(x=>x.Wall.Parameters[BuiltInParameter.CURVE_ELEM_LENGTH].StorageType=StorageType.Integer,"Wrong dimensional storage");
        Unsupported(x=>x.Wall.Parameters[BuiltInParameter.WALL_BASE_CONSTRAINT]=IdParameter(-1),"Missing base");
        Unsupported(x=>((LocationCurve)x.Wall.Location).Curve=new Arc(),"Arc wall");
        Unsupported(x=>((LocationCurve)x.Wall.Location).Curve=Line.CreateBound(new XYZ(0,0,0),new XYZ(10,0,1)),"Non-horizontal axis");
        Unsupported(x=>x.Wall.Parameters[BuiltInParameter.CURVE_ELEM_LENGTH]=DoubleParameter(9),"Parameter axis discrepancy");
        Unsupported(x=>x.Wall.Joined.Add(new ElementId(99)),"Explicit geometry join");
        Unsupported(x=>((LocationCurve)x.Wall.Location).Joins[0].Add(x.Document.Add(new Element(),98)),"Endpoint join");
        Unsupported(x=>x.Request.DesignOptionId="11","Other design option request");
        Unsupported(x=>x.Wall.DesignOption=new DesignOption(),"Source wall option rejected");
        Unsupported(x=>x.Wall.PhaseStatus=ElementOnPhaseStatus.Demolished,"Source wall wrong phase");
        Unsupported(x=>x.View.Parameters[BuiltInParameter.VIEW_PHASE]=IdParameter(2),"Other view phase");
        Unsupported(x=>x.View.IsTemplate=true,"Template view");
        Unsupported(x=>x.View.Iterable=false,"Non-iterable view");
        Unsupported(x=>x.Wall.Geometry=null,"No geometry");
        Unsupported(x=>x.Wall.Geometry=new GeometryElement{new GeometryInstance()},"Unknown geometry instance branch");
        Unsupported(x=>x.Wall.Geometry.Add(new Mesh()),"Mesh physical branch not silently ignored");
        Unsupported(x=>x.Wall.Geometry=new GeometryElement{Solid.Actual(100,2,4)},"Variable thickness envelope");
        Unsupported(x=>x.Wall.Geometry=new GeometryElement{Solid.Actual(90,2,3)},"Edited shape differs");
        Unsupported(x=>x.Opening(20).IsRectBoundary=false,"Nonrect opening");
        Unsupported(x=>x.Opening(20).Host=x.Document.Add(new Element(),30),"Other host");
        Unsupported(x=>x.Opening(20).BoundaryRect=new[]{new XYZ(2,2,3),new XYZ(4,3,5)},"Nonplanar rect diagonal");
        Unsupported(x=>x.Opening(20).BoundaryRect=new[]{new XYZ(2,2,3),new XYZ(2,2,5)},"Degenerate rect");
        Unsupported(x=>{var door=x.Document.Add(new FamilyInstance{Host=x.Wall,Category=new Category{Id=new ElementId(-1)}},20);x.Wall.Inserts.Add(door.Id);},"Unknown actual door void");
        Unsupported(x=>{for(int i=0;i<201;i++)x.Wall.Inserts.Add(new ElementId(100+i));},"Insert cap fails without truncating");
        f=new Fixture();f.Opening(20).PhaseStatus=ElementOnPhaseStatus.Future;fact=f.Read();Require(fact.GeometryVerified&&fact.Inserts[0].Status=="excluded","Future opening excluded without subtracting");
        f=new Fixture();BooleanOperationsUtils.ForcedShapeResidual=1;fact=f.Read();Require(!fact.GeometryVerified&&fact.SymmetricDifferenceMetres3>fact.GeometryToleranceMetres3,"Same-volume shape mismatch rejects via Boolean results");BooleanOperationsUtils.ForcedShapeResidual=0;
        f=new Fixture();BooleanOperationsUtils.Throw=true;fact=f.Read();Require(!fact.GeometryVerified&&fact.Status=="failed","Boolean exception never produces quantity");BooleanOperationsUtils.Throw=false;
        f=new Fixture();f.Wall.GeometryError=new OperationCanceledException();try{f.Read();throw new Exception("Cancellation swallowed");}catch(OperationCanceledException){assertions++;}
        Reject(x=>x.Snapshot.SourcePath="H:\\other.rvt","Other source path");Reject(x=>x.Snapshot.DocumentScope="foreign","Other document scope");Reject(x=>x.Document.Revision=2,"Stale snapshot");
        Reject(x=>x.Wall.DuringGeometry=delegate{x.Document.Revision++;},"Revision changes mid-read");
        f=new Fixture();View foreign=new Document().Add(new View3D(),99);try{RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,foreign);throw new Exception("Foreign view accepted");}catch(InvalidOperationException){assertions++;}
        f=new Fixture();var sheet=f.Document.Add(new ViewSheet(),40);f.Ui.ActiveView=sheet;fact=RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View).Walls.Single();Require(fact.GeometryVerified&&Object.ReferenceEquals(f.Ui.ActiveView,sheet),"Independent explicit model view works from title sheet");
        fact=RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request).Walls.Single();Require(!fact.GeometryVerified,"Three-argument default does not use title-sheet geometry");
        f=new Fixture();((Level)f.Document.GetElement(new ElementId(2))).ProjectElevation=-3;f.Wall.Parameters[BuiltInParameter.WALL_BASE_OFFSET]=DoubleParameter(0.5);fact=f.Read();Near(fact.BaseElevationMetres.Value,-2.5*0.3048,"Negative project base plus explicit bottom offset");
        f=new Fixture();f.Document.Elements.Remove(10);fact=f.Read();Require(fact.Status=="failed"&&!fact.GeometryVerified&&fact.UniqueId==f.Source.UniqueId,"Missing native wall source remains explicit failed record");
        f=new Fixture();var other=OtherWall(f,60);fact=f.Read();Require(fact.GeometryVerified&&fact.InterwallOverlapCheckComplete&&fact.OverlapCandidateCount==1,"AABB candidate alone never establishes physical overlap");
        f=new Fixture();other=OtherWall(f,60);BooleanOperationsUtils.IntersectionVolume=1;fact=f.Read();Require(!fact.GeometryVerified&&fact.Status=="unsupported"&&fact.InterwallOverlapCheckComplete&&fact.IntersectingWallElementIds.SequenceEqual(new[]{"60"}),"Actual intersection result rejects unjoined overlapping wall");BooleanOperationsUtils.IntersectionVolume=0;
        f=new Fixture();other=OtherWall(f,60);other.Geometry=null;fact=f.Read();Require(!fact.GeometryVerified&&!fact.InterwallOverlapCheckComplete&&fact.OverlapCandidateCount==1,"Unreadable candidate solid does not certify independence");
        f=new Fixture();other=OtherWall(f,60);other.PhaseError=new InvalidOperationException();fact=f.Read();Require(!fact.GeometryVerified&&!fact.InterwallOverlapCheckComplete,"Ambiguous candidate phase rejected");
        f=new Fixture();other=OtherWall(f,60);other.Box=null;fact=f.Read();Require(!fact.GeometryVerified&&!fact.InterwallOverlapCheckComplete,"Candidate without locatable bounds rejected");
        f=new Fixture();other=OtherWall(f,60);other.Geometry.Add(new Mesh());fact=f.Read();Require(!fact.GeometryVerified&&!fact.InterwallOverlapCheckComplete,"Candidate mesh branch not silently ignored");
        f=new Fixture();other=OtherWall(f,60);other.Box.Min=new XYZ(100,2,0);other.Box.Max=new XYZ(110,3,10);other.Geometry=null;fact=f.Read();Require(fact.GeometryVerified&&fact.OverlapCandidateCount==0,"Disjoint bounds avoid unnecessary geometry reads");
        f=new Fixture();other=OtherWall(f,60);other.Box.Min=new XYZ(100,2,0);other.Box.Max=new XYZ(110,3,10);other.Box.Transform.OffsetX=-100;fact=f.Read();Require(fact.GeometryVerified&&fact.OverlapCandidateCount==1,"Bounding box transform applied before coarse pairing");
        f=new Fixture();other=OtherWall(f,60);other.PhaseStatus=ElementOnPhaseStatus.Future;other.Geometry=null;fact=f.Read();Require(fact.GeometryVerified&&fact.OverlapCandidateCount==0,"Future wall excluded from confirmed phase check");
        f=new Fixture();other=OtherWall(f,60);other.DesignOption=new DesignOption();other.Geometry=null;fact=f.Read();Require(fact.GeometryVerified&&fact.OverlapCandidateCount==0,"Other design option excluded from main model check");
        f=new Fixture();for(int i=0;i<65;i++)OtherWall(f,100+i);fact=f.Read();Require(!fact.GeometryVerified&&!fact.InterwallOverlapCheckComplete&&fact.OverlapCandidateCount==65,"Candidate cap fails without truncation");
        f=new Fixture();OtherWall(f,60);OtherWall(f,61);BooleanOperationsUtils.IntersectionVolume=1;fact=f.Read();Require(!fact.GeometryVerified&&fact.InterwallOverlapCheckComplete&&fact.IntersectingWallElementIds.SequenceEqual(new[]{"60","61"}),"All intersecting source IDs retained, not only first pair");BooleanOperationsUtils.IntersectionVolume=0;
        f=new Fixture();for(int i=0;i<20000;i++)OtherWall(f,100+i);fact=f.Read();Require(!fact.GeometryVerified&&!fact.InterwallOverlapCheckComplete&&fact.Issues.Any(i=>i.Contains("20,000")),"Catalog cap rejects rather than silently losing walls");
        Require(assertions==79,"Original 79 adapter assertions remain intact before 1.9 additions");
        f=new Fixture();var template=CivilWallRuleTemplates.Default();string fingerprint=CivilWallRuleTemplates.Fingerprint(template),nativeBefore=NativeCsv(f);
        var withRule=RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View,template,null);
        Require(withRule.Walls.Single().GeometryVerified,"Explicit default rule retains supported native-opening proof path");
        Require(!Object.ReferenceEquals(template,withRule.RuleTemplate)&&!Object.ReferenceEquals(template.Priorities,withRule.RuleTemplate.Priorities),"Reader freezes an independent template and priority collection");
        Require(CivilWallRuleTemplates.Fingerprint(template)==fingerprint&&CivilWallRuleTemplates.Fingerprint(withRule.RuleTemplate)==fingerprint,"Reader preserves original and captured rule identity");
        Require(NativeCsv(f)==nativeBefore,"Explicit rule read leaves native source CSV unchanged");
        withRule.RuleTemplate.Name="changed captured clone";Require(template.Name!="changed captured clone","Captured rule object cannot mutate caller template");
        f=new Fixture();template=CivilWallRuleTemplates.Default();string originalName=template.Name;
        var freezeControl=new CivilWallReadControl{Progress=delegate(string stage,int completed,int total){template.Name="caller changed after start";}};
        withRule=RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View,template,freezeControl);
        Require(withRule.RuleTemplate.Name==originalName,"Rule snapshot is frozen before callbacks can change caller settings");
        f=new Fixture();Door(f,20);var legacy=CivilWallRuleTemplates.LegacyExplicitOpeningOnly();nativeBefore=NativeCsv(f);
        fact=RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View,legacy,null).Walls.Single();
        Require(!fact.GeometryVerified&&fact.Status=="unsupported"&&fact.Inserts.Single().Issues.Any(issue=>issue.Contains("本规则未接纳")),"Native-opening-only rule refuses door families before IFC provider");
        Require(NativeCsv(f)==nativeBefore&&CivilWallRuleTemplates.Fingerprint(legacy)==CivilWallRuleTemplates.Fingerprint(CivilWallRuleTemplates.LegacyExplicitOpeningOnly()),"Rejected door preserves source and selected legacy rule");
        f=new Fixture();Door(f,20);fact=f.Read();
        Require(!fact.GeometryVerified&&fact.Status=="unsupported"&&fact.Inserts.Single().Issues.Any(issue=>issue.Contains("RevitAPI 宿主")),"Default actual-cutout policy requires official host API and has no test global override");
        f=new Fixture();Door(f,20).PhaseStatus=ElementOnPhaseStatus.Future;fact=f.Read();
        Require(fact.GeometryVerified&&fact.Inserts.Single().Status=="excluded","Future door is excluded before official actual-cutout API access");
        f=new Fixture();Door(f,20).DesignOption=new DesignOption();fact=f.Read();
        Require(fact.GeometryVerified&&fact.Inserts.Single().Status=="excluded","Other-option door is excluded before official actual-cutout API access");
        f=new Fixture();f.Opening(20);f.Wall.Geometry=new GeometryElement{Solid.Actual(96,2,3)};
        fact=RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View,CivilWallRuleTemplates.LegacyExplicitOpeningOnly(),null).Walls.Single();
        Require(fact.GeometryVerified&&fact.Inserts.Single().Source.Contains("BoundaryRect"),"Native-opening-only rule still accepts proven native rectangular Opening");
        f=new Fixture();var strict=CivilWallRuleTemplates.Copy(CivilWallRuleTemplates.Default(),"strict project tolerance");strict.AbsoluteToleranceMetres3=1e-11;strict.RelativeTolerance=1e-11;
        try{
            BooleanOperationsUtils.ForcedShapeResidual=1e-7;
            var normalFact=RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View,CivilWallRuleTemplates.Default(),null).Walls.Single();
            var strictFact=RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View,strict,null).Walls.Single();
            Require(normalFact.GeometryVerified&&!strictFact.GeometryVerified,"Tightened absolute/relative rule tolerances reject the same controlled residual");
            Require(strictFact.GeometryToleranceMetres3<normalFact.GeometryToleranceMetres3&&strictFact.SymmetricDifferenceMetres3>strictFact.GeometryToleranceMetres3,"Selected tolerance and residual evidence are retained in adapter facts");
        }finally{BooleanOperationsUtils.ForcedShapeResidual=0;}
        f=new Fixture();template=CivilWallRuleTemplates.Default();template.JoinPolicy="not-implemented";int progressCalls=0;
        try{RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View,template,new CivilWallReadControl{Progress=delegate{progressCalls++;}});throw new Exception("Unknown rule accepted");}
        catch(InvalidDataException){assertions++;}
        Require(progressCalls==0&&f.Document.GetElementCalls==0&&f.Wall.GeometryReads==0&&f.Wall.BoundsReads==0,"Invalid rule is rejected before callbacks or any native element/geometry reads");
        f=new Fixture();var second=OtherWall(f,60);f.Snapshot.Elements.Add(new CivilElementRecord{KindCode="wall",ElementId="60",UniqueId=second.UniqueId,Supported=true});
        CancelAt(f,"读取墙体与切洞",1,"second-wall-cancellation");Require(f.Wall.GeometryReads==1&&second.GeometryReads==1,"Second-wall cancellation occurs after first wall and its independence proof, before second wall read");
        f=new Fixture();f.Opening(20);f.Opening(21);CancelAt(f,"读取宿主切洞",1,"second-insert-cancellation");Require(f.Wall.GeometryReads==0,"Insert cancellation precedes wall Boolean proof");
        f=new Fixture();for(int index=0;index<33;index++)OtherWall(f,100+index);CancelAt(f,"读取同阶段墙体依赖目录",32,"dependency-catalog-cancellation");Require(f.Wall.GeometryReads==0,"Directory cancellation precedes wall geometry reads");
        f=new Fixture();OtherWall(f,60);CancelAt(f,"核对相邻墙体",0,"neighbor-cancellation");Require(f.Wall.GeometryReads==1,"Neighbor cancellation occurs after source wall shape proof");
        f=new Fixture();f.Opening(20);CancelAt(f,"扣除已确认切洞实体",0,"opening-boolean-boundary-cancellation");
        f=new Fixture();CancelAt(f,"完成墙体事实",1,"completion-checkpoint-cancellation");
        f=new Fixture();nativeBefore=NativeCsv(f);var revisionChange=new CivilWallReadControl{Progress=delegate(string stage,int done,int total){if(stage=="完成墙体事实")f.Document.Revision++;}};
        try{RevitCivilWallReader.Read(f.Ui,f.Snapshot,f.Request,f.View,CivilWallRuleTemplates.Default(),revisionChange);throw new Exception("Final progress callback changed model revision without rejection");}
        catch(InvalidOperationException){assertions++;}
        Require(NativeCsv(f)==nativeBefore,"Final callback revision change does not mutate the native source snapshot");
        f=new Fixture();BooleanOperationsUtils.Calls=0;BooleanOperationsUtils.Outputs.Clear();CancelAt(f,"实体布尔操作前",0,"before-native-boolean-cancellation");
        Require(BooleanOperationsUtils.Calls==0&&BooleanOperationsUtils.Outputs.Count==0,"Pre-Boolean cancellation prevents the native operation from starting");
        f=new Fixture();BooleanOperationsUtils.Calls=0;BooleanOperationsUtils.Outputs.Clear();CancelAt(f,"实体布尔操作后",1,"after-native-boolean-cancellation");
        Require(BooleanOperationsUtils.Calls==1&&BooleanOperationsUtils.Outputs.Count==1,"Post-Boolean cancellation happens after one complete operation, without claiming interruption inside native CSG");
        Require(BooleanOperationsUtils.Outputs.Single().DisposeCalls==1,"New Boolean output is owned before post-operation cancellation and disposed exactly once");
        Require(((Solid)f.Wall.Geometry[0]).DisposeCalls==0,"Cancellation does not individually dispose borrowed source Solid");
        Console.WriteLine("PASS "+assertions+" wall reader adapter assertions; fake controlled Boolean results only, not Autodesk geometry validation.");return assertions;
    }
    private static Wall OtherWall(Fixture f,int id){return f.Document.Add(new Wall{WallType=f.Wall.WallType,Location=new LocationCurve{Curve=Line.CreateBound(new XYZ(0,0,0),new XYZ(10,0,0))},Geometry=new GeometryElement{Solid.Actual(100,2,3)}},id);}
}
