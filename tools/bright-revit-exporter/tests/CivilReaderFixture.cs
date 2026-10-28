using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using BrightVision.Civil;
using BrightVision.Revit;

internal static class CivilReaderFixture
{
    private static int checks;
    private static void Check(bool condition,string reason){if(!condition)throw new Exception(reason);checks++;}
    private static Category Category(BuiltInCategory kind){return new Category{Id=new ElementId((int)kind),Name=kind.ToString()};}
    private static T Element<T>(Document doc,T element,int id,BuiltInCategory category) where T:Element
    {
        doc.Add(element,id);element.Category=Category(category);element.LevelId=new ElementId(1);
        element.Values[BuiltInParameter.CURVE_ELEM_LENGTH]=new Parameter{Number=10};
        element.Values[BuiltInParameter.HOST_AREA_COMPUTED]=new Parameter{Number=100};
        element.Values[BuiltInParameter.HOST_VOLUME_COMPUTED]=new Parameter{Number=0};
        element.Box=new BoundingBoxXYZ{Min=new XYZ(0,0,0),Max=new XYZ(1,1,8)};
        return element;
    }
    private static CivilReadRequest Request(string scope="project",string option="-1"){return new CivilReadRequest{Scope=scope,PhaseId="10",DesignOptionId=option};}
    private static Parameter LevelReference(ElementId id){return new Parameter{StorageType=StorageType.ElementId,Id=id};}
    private static CivilElementRecord ReadRecord(UIDocument ui,string elementId)
    {return RevitCivilQuantityReader.Read(ui,Request()).Elements.Single(row=>row.ElementId==elementId);}
    private static void Reject(Action action,string message){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Check(rejected,message);}
    private static int Main()
    {
        try
        {
            Document doc=new Document();
            Level firstLevel=doc.Add(new Level{Name="1F",Elevation=0},1);
            Level secondLevel=doc.Add(new Level{Name="2F",Elevation=-100,ProjectElevationOverride=10},2);
            Phase phase=doc.Add(new Phase{Name="Existing/New phase"},10);doc.Phases.Add(phase);
            Phase later=doc.Add(new Phase{Name="Later phase"},11);doc.Phases.Add(later);
            DesignOption optionA=doc.Add(new DesignOption{Name="Option A"},20),optionB=doc.Add(new DesignOption{Name="Option B"},21);
            View view=doc.Add(new View{Name="Model view"},30);
            view.Values[BuiltInParameter.VIEW_PHASE]=new Parameter{StorageType=StorageType.ElementId,Id=phase.Id};
            UIDocument ui=new UIDocument{Document=doc,ActiveView=view};
            Wall wall=Element(doc,new Wall{Name="wall"},100,BuiltInCategory.OST_Walls);
            Floor floor=Element(doc,new Floor{Name="floor"},101,BuiltInCategory.OST_Floors);
            FamilyInstance beam=Element(doc,new FamilyInstance{Name="beam",StructuralType=StructuralType.Beam},102,BuiltInCategory.OST_StructuralFraming);
            beam.LevelId=ElementId.InvalidElementId;
            beam.Values[BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM]=LevelReference(firstLevel.Id);
            FamilyInstance structuralColumn=Element(doc,new FamilyInstance{Name="structural column",StructuralType=StructuralType.Column},103,BuiltInCategory.OST_StructuralColumns);
            structuralColumn.PhaseStatus=ElementOnPhaseStatus.Existing;
            FamilyInstance architecturalColumn=Element(doc,new FamilyInstance{Name="architectural column"},104,BuiltInCategory.OST_Columns);
            Element(doc,new FamilyInstance{Name="brace",StructuralType=StructuralType.Brace},105,BuiltInCategory.OST_StructuralFraming);
            Element(doc,new Wall{Name="stacked",IsStackedWall=true},106,BuiltInCategory.OST_Walls);
            Element(doc,new Wall{Name="stack member",IsStackedWallMember=true},107,BuiltInCategory.OST_Walls);
            Wall curtain=Element(doc,new Wall{Name="curtain"},108,BuiltInCategory.OST_Walls);curtain.WallType.Kind=WallKind.Curtain;
            Element(doc,new FamilyInstance{Name="nested",SuperComponent=beam,StructuralType=StructuralType.Beam},109,BuiltInCategory.OST_StructuralFraming);
            Element(doc,new FamilyInstance{Name="generic"},110,BuiltInCategory.OST_GenericModel);
            Element(doc,new Wall{Name="demolished",PhaseStatus=ElementOnPhaseStatus.Demolished},111,BuiltInCategory.OST_Walls);
            Element(doc,new Wall{Name="temporary",PhaseStatus=ElementOnPhaseStatus.Temporary},112,BuiltInCategory.OST_Walls);
            Element(doc,new Wall{Name="future",PhaseStatus=ElementOnPhaseStatus.Future},113,BuiltInCategory.OST_Walls);
            Element(doc,new Wall{Name="past",PhaseStatus=ElementOnPhaseStatus.Past},114,BuiltInCategory.OST_Walls);
            Element(doc,new Wall{Name="none",PhaseStatus=ElementOnPhaseStatus.None},115,BuiltInCategory.OST_Walls);
            Wall selectedOption=Element(doc,new Wall{Name="option A",DesignOption=optionA},116,BuiltInCategory.OST_Walls);
            Element(doc,new Wall{Name="option B",DesignOption=optionB},117,BuiltInCategory.OST_Walls);
            Element(doc,new Element{Name="other discipline"},118,BuiltInCategory.OST_Other);
            Element(doc,new Wall{Name="separate stacked category",IsStackedWall=true},119,BuiltInCategory.OST_Other);
            doc.Add(new RevitLinkInstance{Name="missing link"},120);doc.Add(new RevitLinkInstance{Name="loaded link",LinkedDocument=new Document()},121);
            Element(doc,new FamilyInstance{Name="curtain panel"},122,BuiltInCategory.OST_CurtainWallPanels);
            Element(doc,new FamilyInstance{Name="curtain mullion"},123,BuiltInCategory.OST_CurtainWallMullions);
            Element(doc,new FamilyInstance{Name="uncertain structural role"},124,BuiltInCategory.OST_StructuralColumns);
            doc.ViewElements[30]=new System.Collections.Generic.List<Element>{wall,floor,beam,structuralColumn,architecturalColumn};

            Check(RevitCivilQuantityReader.DefaultRequest(ui).PhaseId=="10","View phase was ignored.");
            Check(RevitCivilQuantityReader.DefaultRequest(ui).Scope=="view","Iterable model view did not remain the default scope.");
            ViewSheet sheet=doc.Add(new ViewSheet{Name="Title sheet"},31);
            sheet.Values[BuiltInParameter.VIEW_PHASE]=LevelReference(phase.Id);
            ui.ActiveView=sheet;
            Check(RevitCivilQuantityReader.DefaultRequest(ui).Scope=="project"&&RevitCivilQuantityReader.DefaultRequest(ui).PhaseId=="10","Title sheet did not use project scope while retaining its explicit phase.");
            ui.ActiveView=doc.Add(new ViewSchedule{Name="Schedule"},32);
            Check(RevitCivilQuantityReader.DefaultRequest(ui).Scope=="project","Schedule became a default quantity view.");
            ui.ActiveView=view;view.IsTemplate=true;
            Check(RevitCivilQuantityReader.DefaultRequest(ui).Scope=="project","Template became a default quantity view.");
            view.IsTemplate=false;view.ValidForIteration=false;
            Check(RevitCivilQuantityReader.DefaultRequest(ui).Scope=="project","Non-iterable view became a default quantity view.");
            view.ValidForIteration=true;ui.ActiveView=null;
            Check(RevitCivilQuantityReader.DefaultRequest(ui).Scope=="project","No active view did not use project scope.");
            ui.ActiveView=view;
            view.Values.Clear();Check(RevitCivilQuantityReader.DefaultRequest(ui).PhaseId=="11","Last document phase fallback failed.");
            Check(RevitCivilQuantityReader.DefaultRequest(ui).DesignOptionId=="-1","Default silently included design alternatives.");
            Check(RevitCivilQuantityReader.GetPhases(doc).Count==2,"Phase choices were lost.");
            Check(RevitCivilQuantityReader.GetDesignOptions(doc).Count==3,"Design option choices must include the main model.");
            CivilSnapshot snapshot=RevitCivilQuantityReader.Read(ui,Request());
            Check(snapshot.SourcePath==doc.PathName,"Source path was not captured from the root document.");
            Check(snapshot.Levels.Count==2&&snapshot.Levels[0].Id=="1"&&snapshot.Levels[1].Id=="2","Native levels were not sorted by project elevation.");
            Check(snapshot.Levels[1].UniqueId==secondLevel.UniqueId&&Math.Abs(snapshot.Levels[1].ElevationMetres.Value-3.048)<1e-14,"Level identity or project-elevation SI metadata was replaced by display elevation.");
            Check(snapshot.Elements.Count(r=>r.Supported)==5,"Unsupported families were counted.");
            Check(structuralColumn.Values[BuiltInParameter.CURVE_ELEM_LENGTH].ReadCount==0&&structuralColumn.Values[BuiltInParameter.HOST_AREA_COMPUTED].ReadCount==0&&structuralColumn.Values[BuiltInParameter.HOST_VOLUME_COMPUTED].ReadCount==1,"Non-applicable column metrics were read as quantities.");
            Check(snapshot.Elements.Count(r=>r.KindCode=="review")==9,"Stacked/curtain/nested/generic records were not isolated for review.");
            Check(new[]{"119","122","123","124"}.All(id=>snapshot.Elements.Any(r=>r.ElementId==id&&r.KindCode=="review")),"Special parent categories, curtain children or unclear column roles were silently measured or lost.");
            Check(snapshot.Exclusions.Any(r=>r.ElementId=="105"),"Brace was counted as beam.");
            Check(new[]{"111","112","113","114","115"}.All(id=>snapshot.Exclusions.Any(r=>r.ElementId==id)),"Non-existing/new phases were included.");
            Check(new[]{"116","117"}.All(id=>snapshot.Exclusions.Any(r=>r.ElementId==id)),"Main-model mode included design option elements.");
            Check(snapshot.Exclusions.Any(r=>r.ElementId=="118"),"Other disciplines were not reported as excluded.");
            Check(snapshot.LinkInstanceCount==2&&snapshot.MissingLinkCount==1,"Root link inventory was incorrect.");
            CivilElementRecord wallRecord=snapshot.Elements.Single(r=>r.ElementId=="100");
            Check(Math.Abs(wallRecord.Length.Value.Value-3.048)<1e-14&&Math.Abs(wallRecord.Area.Value.Value-9.290304)<1e-13&&wallRecord.Volume.Value==0,"SI conversion or legal zero failed.");
            Check(wallRecord.LevelId=="1"&&wallRecord.LevelName=="1F"&&wallRecord.RawLevelId=="1"&&wallRecord.RawLevelName=="1F","Raw level assignment was replaced.");
            Check(wallRecord.LevelUniqueId==firstLevel.UniqueId&&wallRecord.LevelSource=="Element.LevelId","Raw level provenance was lost.");
            CivilElementRecord beamRecord=snapshot.Elements.Single(r=>r.ElementId=="102");
            Check(beamRecord.RawLevelId=="-1"&&beamRecord.RawLevelName==String.Empty&&beamRecord.LevelId=="1"&&beamRecord.LevelUniqueId==firstLevel.UniqueId&&beamRecord.LevelSource=="INSTANCE_REFERENCE_LEVEL_PARAM","Beam reference level did not resolve independently from its absent raw LevelId.");
            Check(beamRecord.Area.Status=="not-applicable"&&beamRecord.Area.Value==null,"Beam area was improperly summed.");
            Check(snapshot.Elements.Single(r=>r.ElementId=="101").Length.Status=="not-applicable","Floor length was improperly summed.");
            foreach(string id in new[]{"103","104"}){
                CivilElementRecord row=snapshot.Elements.Single(r=>r.ElementId==id);
                Check(row.Length.Status=="not-applicable"&&row.Area.Status=="not-applicable","Column length was presented as height.");
            }
            Check(snapshot.Elements.Single(r=>r.ElementId=="103").KindCode=="structural-column"&&snapshot.Elements.Single(r=>r.ElementId=="104").KindCode=="architectural-column","Column business categories were mixed.");
            Check(snapshot.Elements.Where(r=>!r.Supported).All(r=>r.Length.Status=="not-applicable"&&r.Volume.Value==null&&!String.IsNullOrEmpty(r.ExclusionReason)),"Review records carry quantities or no reason.");
            Check(RevitCivilQuantityReader.Read(ui,Request("project","20")).Elements.Any(r=>r.ElementId=="116"),"Confirmed option A was omitted.");
            Check(!RevitCivilQuantityReader.Read(ui,Request("project","20")).Elements.Any(r=>r.ElementId=="117"),"Another design option was mixed in.");
            Check(RevitCivilQuantityReader.Read(ui,Request("view")).Elements.Count==5,"View scope did not use view candidates.");
            doc.ViewElements[31]=new System.Collections.Generic.List<Element>();ui.ActiveView=sheet;
            Check(RevitCivilQuantityReader.Read(ui,Request("view")).Elements.Count==0,"Explicit iterable empty-sheet scope silently expanded to the whole project.");
            ui.ActiveView=view;
            ui.Selection.Ids.Add(wall.Id);ui.Selection.Ids.Add(wall.Id);ui.Selection.Ids.Add(selectedOption.Id);
            Check(RevitCivilQuantityReader.Read(ui,Request("selection")).Elements.Count==1,"Selection duplicated source or skipped option filtering.");
            ui.Selection.Ids.Clear();Check(RevitCivilQuantityReader.Read(ui,Request("selection")).CandidateCount==0,"Empty selection expanded to project scope.");
            Reject(()=>RevitCivilQuantityReader.Read(ui,new CivilReadRequest{Scope="project",PhaseId="-1"}),"Mixed/invalid phase was accepted.");
            Reject(()=>RevitCivilQuantityReader.Read(ui,Request("bad")),"Unknown scope was accepted.");
            Reject(()=>RevitCivilQuantityReader.Read(ui,Request("project","9999")),"Unknown design option was accepted.");
            view.IsTemplate=true;Reject(()=>RevitCivilQuantityReader.Read(ui,Request("view")),"Template view was iterable.");view.IsTemplate=false;
            doc.IsFamilyDocument=true;Reject(()=>RevitCivilQuantityReader.Read(ui,Request()),"Family document was treated as a project.");doc.IsFamilyDocument=false;
            doc.PathName=String.Empty;
            Check(RevitCivilQuantityReader.Read(ui,Request()).SourcePath==String.Empty,"Unsaved project gained an invented source path.");
            doc.PathName="H:\\fixture\\sample.rvt";

            // Reference-level selection is identity based and independent of
            // quantities, display names, bounding-box height and parameter order.
            secondLevel.Name=firstLevel.Name;
            beam.Values[BuiltInParameter.FAMILY_LEVEL_PARAM]=LevelReference(firstLevel.Id);
            beam.Values[BuiltInParameter.SCHEDULE_LEVEL_PARAM]=LevelReference(firstLevel.Id);
            CivilElementRecord same=ReadRecord(ui,"102");
            Check(same.LevelId=="1"&&same.LevelSource=="INSTANCE_REFERENCE_LEVEL_PARAM","Agreeing references did not retain the category-specific source priority.");
            beam.Values[BuiltInParameter.FAMILY_LEVEL_PARAM]=LevelReference(secondLevel.Id);
            CivilElementRecord conflict=ReadRecord(ui,"102");
            Check(conflict.LevelId=="-1"&&conflict.LevelUniqueId==String.Empty&&conflict.LevelSource==String.Empty&&conflict.Issues.Any(issue=>issue.Contains("参照楼层冲突")),"Conflicting references silently chose the first reference or merged same-named levels.");
            Check(conflict.RawLevelId=="-1"&&conflict.Length.Value==beamRecord.Length.Value&&conflict.Volume.Value==beamRecord.Volume.Value,"Reference conflict changed original identity or metric values.");
            beam.LevelId=firstLevel.Id;
            CivilElementRecord retained=ReadRecord(ui,"102");
            Check(retained.LevelId=="1"&&retained.RawLevelId=="1"&&retained.LevelSource=="Element.LevelId"&&retained.Issues.Any(issue=>issue.Contains("原生楼层冲突")&&issue.Contains("FAMILY_LEVEL_PARAM=2")),"A conflicting native reference replaced a valid raw LevelId without a warning.");
            beam.LevelId=ElementId.InvalidElementId;
            beam.Values.Remove(BuiltInParameter.FAMILY_LEVEL_PARAM);beam.Values.Remove(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            beam.Values[BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM].HasValue=false;
            Check(ReadRecord(ui,"102").LevelId=="-1","An unvalued reference became a level.");
            beam.Values[BuiltInParameter.FAMILY_LEVEL_PARAM]=LevelReference(secondLevel.Id);
            CivilElementRecord familyFallback=ReadRecord(ui,"102");
            Check(familyFallback.LevelId=="2"&&familyFallback.LevelSource=="FAMILY_LEVEL_PARAM","Beam family-level fallback was ignored when the primary parameter had no value.");
            beam.Values[BuiltInParameter.SCHEDULE_LEVEL_PARAM]=LevelReference(ElementId.InvalidElementId);
            Check(ReadRecord(ui,"102").LevelId=="2","An explicitly unassigned reference blocked an otherwise valid native reference.");
            beam.Values.Remove(BuiltInParameter.FAMILY_LEVEL_PARAM);
            beam.Values[BuiltInParameter.SCHEDULE_LEVEL_PARAM]=LevelReference(firstLevel.Id);
            Check(ReadRecord(ui,"102").LevelSource=="SCHEDULE_LEVEL_PARAM","Schedule-level native fallback was omitted.");
            beam.LevelId=floor.Id;
            CivilElementRecord nonLevelRaw=ReadRecord(ui,"102");
            Check(nonLevelRaw.RawLevelId=="101"&&nonLevelRaw.RawLevelName==String.Empty&&nonLevelRaw.LevelId=="1","Non-level raw identity was lost or accepted as a Level.");
            beam.LevelId=ElementId.InvalidElementId;
            beam.Values[BuiltInParameter.FAMILY_LEVEL_PARAM]=new Parameter{StorageType=StorageType.Integer};
            CivilElementRecord wrongStorage=ReadRecord(ui,"102");
            Check(wrongStorage.LevelId=="-1"&&wrongStorage.Issues.Any(issue=>issue.Contains("参数类型无效")),"A malformed competing native reference was silently ignored for fallback.");
            beam.Values[BuiltInParameter.FAMILY_LEVEL_PARAM]=LevelReference(floor.Id);
            Check(ReadRecord(ui,"102").LevelId=="-1"&&ReadRecord(ui,"102").Issues.Any(issue=>issue.Contains("未指向当前工程 Level")),"A native reference to another element type was treated as a Level.");
            beam.Values[BuiltInParameter.FAMILY_LEVEL_PARAM]=new Parameter{StorageType=StorageType.ElementId,Error=new InvalidOperationException("reference read failed")};
            Check(ReadRecord(ui,"102").LevelId=="-1"&&ReadRecord(ui,"102").Issues.Any(issue=>issue.Contains("参照层读取失败")),"Unreadable competing native references allowed an unverified fallback.");
            beam.Values[BuiltInParameter.FAMILY_LEVEL_PARAM].Error=new OperationCanceledException();
            bool levelCancelled=false;try{ReadRecord(ui,"102");}catch(OperationCanceledException){levelCancelled=true;}
            Check(levelCancelled,"Cancellation during reference-level reading was swallowed.");
            beam.Values[BuiltInParameter.FAMILY_LEVEL_PARAM].Error=new InvalidOperationException("reference read failed");
            beam.LevelId=firstLevel.Id;
            CivilElementRecord rawDuringError=ReadRecord(ui,"102");
            Check(rawDuringError.LevelId=="1"&&rawDuringError.LevelSource=="Element.LevelId"&&rawDuringError.Issues.Any(issue=>issue.Contains("参照层读取失败")),"Valid raw level was discarded when a corroborating parameter failed.");
            beam.LevelId=ElementId.InvalidElementId;
            beam.Values.Remove(BuiltInParameter.FAMILY_LEVEL_PARAM);beam.Values.Remove(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            beam.Values[BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM]=LevelReference(firstLevel.Id);

            floor.LevelId=ElementId.InvalidElementId;floor.Values[BuiltInParameter.LEVEL_PARAM]=LevelReference(secondLevel.Id);
            floor.Values[BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM]=LevelReference(firstLevel.Id);
            CivilElementRecord floorFallback=ReadRecord(ui,"101");
            Check(floorFallback.LevelId=="2"&&floorFallback.LevelSource=="LEVEL_PARAM","Floor used a family/beam reference instead of its level parameter.");
            floor.LevelId=firstLevel.Id;
            structuralColumn.LevelId=ElementId.InvalidElementId;structuralColumn.Values[BuiltInParameter.FAMILY_BASE_LEVEL_PARAM]=LevelReference(secondLevel.Id);
            Check(ReadRecord(ui,"103").LevelId=="2"&&ReadRecord(ui,"103").LevelSource=="FAMILY_BASE_LEVEL_PARAM","Structural-column base level was not resolved.");
            structuralColumn.LevelId=firstLevel.Id;
            architecturalColumn.LevelId=ElementId.InvalidElementId;architecturalColumn.Values[BuiltInParameter.FAMILY_LEVEL_PARAM]=LevelReference(secondLevel.Id);
            Check(ReadRecord(ui,"104").LevelId=="2"&&ReadRecord(ui,"104").LevelSource=="FAMILY_LEVEL_PARAM","Architectural-column native level was not resolved.");
            architecturalColumn.LevelId=firstLevel.Id;
            Element generic=doc.GetElement(new ElementId(110));generic.LevelId=ElementId.InvalidElementId;generic.Values[BuiltInParameter.SCHEDULE_LEVEL_PARAM]=LevelReference(firstLevel.Id);
            Check(ReadRecord(ui,"110").KindCode=="review"&&ReadRecord(ui,"110").LevelId=="-1","Review-only generic family received an unapproved category fallback.");

            wall.LevelId=ElementId.InvalidElementId;wall.Values[BuiltInParameter.WALL_BASE_CONSTRAINT]=LevelReference(secondLevel.Id);
            wall.Values[BuiltInParameter.LEVEL_PARAM]=LevelReference(firstLevel.Id);wall.Box.Min=new XYZ(0,0,9);wall.Box.Max=new XYZ(1,1,11);
            CivilElementRecord baseResolved=ReadRecord(ui,"100");
            Check(baseResolved.LevelId=="2"&&baseResolved.LevelSource=="WALL_BASE_CONSTRAINT","Wall base-constraint reference was not resolved by category.");
            Check(!baseResolved.Issues.Any(issue=>issue.Contains("跨层候选")),"Resolved native base plane was falsely counted as a crossed storey.");
            wall.Values.Remove(BuiltInParameter.WALL_BASE_CONSTRAINT);
            Check(ReadRecord(ui,"100").LevelId=="-1"&&ReadRecord(ui,"100").Issues.Any(issue=>issue.Contains("跨层候选")),"Cross-storey check used display elevation or inferred wall membership from an inapplicable parameter.");
            wall.Values.Remove(BuiltInParameter.LEVEL_PARAM);wall.Box.Min=new XYZ(0,0,0);
            secondLevel.Name="2F";
            wall.Box.Max=new XYZ(1,1,12);wall.LevelId=ElementId.InvalidElementId;wall.Values.Remove(BuiltInParameter.HOST_AREA_COMPUTED);
            wall.Values[BuiltInParameter.HOST_VOLUME_COMPUTED].Number=Double.NaN;
            wall.Values[BuiltInParameter.CURVE_ELEM_LENGTH].Error=new InvalidOperationException("host failure");
            CivilElementRecord bad=RevitCivilQuantityReader.Read(ui,Request("view")).Elements.Single(r=>r.ElementId=="100");
            Check(bad.Area.Status=="missing"&&bad.Area.Value==null&&bad.Volume.Status=="invalid"&&bad.Volume.Value==null&&bad.Length.Status=="unavailable","Missing/invalid/failed parameter became zero.");
            Check(bad.Issues.Any(i=>i.Contains("原始楼层"))&&bad.Issues.Any(i=>i.Contains("跨层候选")),"Raw-level or cross-layer candidate checks were omitted.");
            wall.Box.Max=new XYZ(1,1,8);wall.Box.Transform.OffsetZ=3;
            Check(RevitCivilQuantityReader.Read(ui,Request("view")).Elements.Single(r=>r.ElementId=="100").Issues.Any(i=>i.Contains("跨层候选")),"Bounding-box transform was ignored in level checks.");
            wall.Values[BuiltInParameter.CURVE_ELEM_LENGTH].Error=new OperationCanceledException();
            bool cancelled=false;try{RevitCivilQuantityReader.Read(ui,Request("view"));}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"Cancellation was swallowed.");
            wall.Values[BuiltInParameter.CURVE_ELEM_LENGTH].Error=null;
            Level basement=doc.Add(new Level{Name="Basement",Elevation=999,ProjectElevationOverride=-5},3);
            CivilSnapshot underground=RevitCivilQuantityReader.Read(ui,Request());
            Check(underground.Levels[0].Id=="3"&&Math.Abs(underground.Levels[0].ElevationMetres.Value+1.524)<1e-14,"A legitimate negative level project elevation was lost or treated as an invalid quantity.");
            basement.ProjectElevationOverride=Double.NaN;
            beam.Values[BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM]=LevelReference(basement.Id);
            CivilSnapshot invalidElevation=RevitCivilQuantityReader.Read(ui,Request());
            Check(invalidElevation.Levels.Single(level=>level.Id=="3").ElevationMetres==null&&invalidElevation.Notices.Any(notice=>notice.Contains("项目标高无效")),"A non-finite native level elevation became a numeric mapping candidate.");
            Check(invalidElevation.Elements.Single(row=>row.ElementId=="102").LevelId=="3"&&invalidElevation.Elements.Single(row=>row.ElementId=="102").Issues.Any(issue=>issue.Contains("项目标高无效，未检查跨层")),"Invalid elevation erased a native identity or silently passed cross-storey checking.");
            basement.ProjectElevationOverride=-5;
            beam.Values[BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM]=LevelReference(firstLevel.Id);
            UIControlledApplication application=new UIControlledApplication();
            RevitCivilQuantityReader.Attach(application);RevitCivilQuantityReader.Attach(application);
            Check(RevitCivilQuantityReader.Revision(doc)==0,"Revision did not start at zero.");
            application.ControlledApplication.Publish(doc);
            Check(RevitCivilQuantityReader.Revision(doc)==1,"Change event was missed or attached twice.");
            Document wrapper=new Document{ModelKey=doc.ModelKey};
            Check(RevitCivilQuantityReader.Revision(wrapper)==1,"Same native-document wrapper identity lost the revision.");
            Check(RevitCivilQuantityReader.Revision(new Document())==0,"Different documents shared revision state.");
            RevitCivilQuantityReader.Detach(application);application.ControlledApplication.Publish(doc);
            Check(RevitCivilQuantityReader.Revision(doc)==0,"Detach left a running event handler.");
            Console.WriteLine("PASS: "+checks+" civil reader assertions; isolated test API, no Revit host.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.ToString());return 1;}
    }
}
