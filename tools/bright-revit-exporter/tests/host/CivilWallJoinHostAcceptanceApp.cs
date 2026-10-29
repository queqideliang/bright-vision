// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using CivilSnapshotComparison = BrightVision.CivilWallHostAcceptance.CivilSnapshotComparison;

[assembly: AssemblyVersion("1.0.0.0")]
namespace BrightVision.CivilWallJoinHostAcceptance
{
    // Test only. This DLL is deliberately separate from both the installed
    // product and the frozen 1.9 test driver. All model edits are rolled back.
    public sealed class App : IExternalApplication
    {
        private const double F = 1 / .3048;
        private const string ConfigurationEnvironment = "BV_CIVIL_WALL_JOIN_CONFIG", NonceEnvironment = "BV_CIVIL_WALL_JOIN_RUN_ID";
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16777216 };
        private readonly List<string> warnings = new List<string>(), created = new List<string>();
        private readonly List<Dictionary<string,object>> cases = new List<Dictionary<string,object>>();
        private UIControlledApplication host; private Config config; private string boundary; private int checks;
        private Assembly production; private Type nativeReader, joinReader, core, plans, session, rules;

        public Result OnStartup(UIControlledApplication application)
        {
            string file=Environment.GetEnvironmentVariable(ConfigurationEnvironment),nonce=Environment.GetEnvironmentVariable(NonceEnvironment);Guid id;
            if(String.IsNullOrEmpty(file)||!Guid.TryParseExact(nonce,"N",out id)||id==Guid.Empty)return Result.Succeeded;
            try
            {
                DirectoryInfo cursor=new FileInfo(Assembly.GetExecutingAssembly().Location).Directory;
                while(cursor!=null&&!File.Exists(Path.Combine(cursor.FullName,"tools","bright-revit-exporter","CivilWallJoinCore.cs")))cursor=cursor.Parent;
                if(cursor==null)return Result.Succeeded;
                boundary=Path.Combine(cursor.FullName,"test","revit-civil-acceptance","phase3-1.10-joins");file=Child(file);
                Config candidate=json.Deserialize<Config>(File.ReadAllText(file,Encoding.UTF8));
                if(candidate==null||candidate.runId!=nonce||Path.GetFileName(file)!="config.json"||Path.GetFileName(Path.GetDirectoryName(file))!="run-"+nonce)return Result.Succeeded;
                candidate.modelPath=Child(candidate.modelPath);candidate.outputDirectory=Child(candidate.outputDirectory);
                if(!Same(candidate.outputDirectory,Path.Combine(Path.GetDirectoryName(file),"output"))||Path.GetFileName(candidate.modelPath)!="acceptance-model.rvt")return Result.Succeeded;
                if(candidate.expectedPackageVersion!="1.10.0"||candidate.productionSourceCount<1||Hash(Assembly.GetExecutingAssembly().Location)!=candidate.expectedDriverSha256||Hash(candidate.productionManifestPath)!=candidate.productionManifestSha256)return Result.Succeeded;
                config=candidate;host=application;Environment.SetEnvironmentVariable(ConfigurationEnvironment,null);Environment.SetEnvironmentVariable(NonceEnvironment,null);
                host.Idling+=OnIdling;Status("waiting-for-exact-dedicated-model");
            }
            catch{if(host!=null)host.Idling-=OnIdling;host=null;config=null;}
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication application){if(host!=null)host.Idling-=OnIdling;return Result.Succeeded;}
        private void OnIdling(object sender,IdlingEventArgs args)
        {
            var app=sender as UIApplication;if(config==null||app==null||app.ActiveUIDocument==null||!Same(app.ActiveUIDocument.Document.PathName,config.modelPath))return;
            host.Idling-=OnIdling;Run(app);
        }

        private void Run(UIApplication app)
        {
            UIDocument ui=app.ActiveUIDocument;Document document=null;TransactionGroup group=null;bool rolledBack=false,attached=false;
            ICollection<ElementId> selection=null;string activeView=null,modelHash=null;int originalCount=0;Stopwatch timer=Stopwatch.StartNew();
            var report=Fields("schemaVersion",1,"result","FAIL","runId",config.runId,"processId",Process.GetCurrentProcess().Id,"revitYear",app.Application.VersionNumber,"revitBuild",app.Application.VersionBuild,
                "expectedPackageVersion",config.expectedPackageVersion,"productionSourceCount",config.productionSourceCount,"productionManifestPath",config.productionManifestPath,"productionManifestSha256",config.productionManifestSha256,
                "sourceSaved",false,"activeViewWritten",false,"fixtureRollbackRequired",true,"ribbonClickVerified",false,"nativeWindowVerified",false);
            try
            {
                Require(app.Application.VersionNumber=="2020","Driver targets exact local Revit 2020 API.");
                Require(Same(typeof(Document).Assembly.Location,config.expectedRevitApiPath)&&Hash(typeof(Document).Assembly.Location)==config.expectedRevitApiSha256,"Actual RevitAPI path/hash differs.");
                Require(Same(typeof(UIApplication).Assembly.Location,config.expectedRevitApiUiPath)&&Hash(typeof(UIApplication).Assembly.Location)==config.expectedRevitApiUiSha256,"Actual RevitAPIUI path/hash differs.");
                report["hostApi"]=Fields("RevitAPI",Fields("path",typeof(Document).Assembly.Location,"sha256",Hash(typeof(Document).Assembly.Location)),"RevitAPIUI",Fields("path",typeof(UIApplication).Assembly.Location,"sha256",Hash(typeof(UIApplication).Assembly.Location)));
                Require(ui!=null&&Same(ui.Document.PathName,config.modelPath),"Only dedicated model copy may be tested.");document=ui.Document;
                foreach(Document open in app.Application.Documents)Require(open.IsLinked||open.Equals(document),"Another user document is open; fixture refused.");
                Require(document.IsValidObject&&!document.IsFamilyDocument,"Dedicated copy is not a valid project.");
                modelHash=Hash(config.modelPath);Require(modelHash==config.expectedModelSha256&&Hash(config.originalModelPath)==config.expectedOriginalModelSha256,"Prepared sample identity changed.");
                Require(Hash(config.exporterPath)==config.expectedExporterSha256&&Hash(config.productionManifestPath)==config.productionManifestSha256,"Prepared production identity changed.");
                production=Assembly.LoadFile(Path.GetFullPath(config.exporterPath));
                Require(Same(production.Location,config.exporterPath)&&production.GetName().Version.ToString()==config.expectedAssemblyVersion&&Hash(production.Location)==config.expectedExporterSha256,"Production Assembly.LoadFile identity differs.");
                report["plugin"]=Fields("path",production.Location,"version",production.GetName().Version.ToString(),"sha256",Hash(production.Location),"loadMethod","Assembly.LoadFile");
                report["driver"]=Fields("path",Assembly.GetExecutingAssembly().Location,"sha256",Hash(Assembly.GetExecutingAssembly().Location));
                nativeReader=production.GetType("BrightVision.Revit.RevitCivilQuantityReader",true);joinReader=production.GetType("BrightVision.Revit.RevitWallJoinReader",true);
                core=production.GetType("BrightVision.Civil.CivilWallJoinCore",true);plans=production.GetType("BrightVision.Civil.CivilWallJoinPlans",true);session=production.GetType("BrightVision.Civil.CivilWallJoinSession",true);rules=production.GetType("BrightVision.Civil.CivilWallRuleTemplates",true);
                Call(nativeReader,"Attach",host);attached=true;
                selection=ui.Selection.GetElementIds();activeView=Id(ui.ActiveView.Id);originalCount=Count(document);
                report["source"]=Fields("path",document.PathName,"sha256Before",modelHash,"elementCountBefore",originalCount,"isModifiedBefore",document.IsModified);
                Phase phase=document.Phases.Cast<Phase>().Last();Level level=new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().First();
                WallType prototype=new FilteredElementCollector(document).OfClass(typeof(WallType)).Cast<WallType>().First(candidateType=>candidateType.Kind==WallKind.Basic);
                ViewFamilyType viewType=new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(candidateType=>candidateType.ViewFamily==ViewFamily.ThreeDimensional);
                WallType type=null;View3D view=null;
                group=new TransactionGroup(document,"BV 1.10 disposable join SDK "+config.runId);Require(group.Start()==TransactionStatus.Started,"Could not start rollback group.");
                Mutate(document,"BV independent 200mm type and geometry view",delegate{
                    type=(WallType)prototype.Duplicate("BV 1.10 200mm "+config.runId);type.SetCompoundStructure(CompoundStructure.CreateSimpleCompoundStructure(new List<CompoundStructureLayer>{new CompoundStructureLayer(.2*F,MaterialFunctionAssignment.Structure,ElementId.InvalidElementId)}));
                    view=View3D.CreateIsometric(document,viewType.Id);view.Name="BV join geometry "+config.runId;view.DetailLevel=ViewDetailLevel.Fine;view.IsSectionBoxActive=false;view.get_Parameter(BuiltInParameter.VIEW_PHASE).Set(phase.Id);
                    created.Add(type.UniqueId);created.Add(view.UniqueId);
                });
                Near(type.Width*.3048,.2,"Independent thickness must be 0.2m.");
                double z=level.ProjectElevation;

                Scene(document,"cross",delegate{
                    Wall a=null,b=null;XYZ origin=new XYZ(5000,5000,z);
                    Mutate(document,"Unjoined perpendicular walls",delegate{a=Wall(document,type,level,phase,origin,0,0,10,0,false);b=Wall(document,type,level,phase,origin,5,-5,5,5,false);});
                    var geometry=new[]{Analytic(a,true,1,new[]{0.0,10,-.1,.1,0,3}),Analytic(b,true,2,new[]{4.9,5.1,-5,5,0,3})};
                    var unjoined=NormalizeUnjoined(document,new[]{a,b},view,origin,geometry);
                    var first=CheckCase("unjoined-cross-A-first",ui,new[]{a,b},new[]{a,b},phase,view,origin,geometry,true,11.88,new[]{6.0,5.88},null,null,unjoined);
                    var swapped=new[]{Analytic(a,true,2,new[]{0.0,10,-.1,.1,0,3}),Analytic(b,true,1,new[]{4.9,5.1,-5,5,0,3})};
                    CheckCase("unjoined-cross-B-first",ui,new[]{a,b},new[]{b,a},phase,view,origin,swapped,true,11.88,new[]{6.0,5.88},null,null,unjoined);
                    var onlyGeometry=new[]{Analytic(a,true,1,new[]{0.0,10,-.1,.1,0,3}),Analytic(b,false,2,new[]{4.9,5.1,-5,5,0,3})};
                    var only=CheckCase("selection-only-A",ui,new[]{a},new[]{a,b},phase,view,origin,onlyGeometry,true,11.88,new[]{6.0,5.88},first.Plan,null,unjoined);
                    var both=CheckCase("selection-both-A-B",ui,new[]{a,b},new[]{a,b},phase,view,origin,geometry,true,11.88,new[]{6.0,5.88},first.Plan,null,unjoined);
                    Require(first.ComponentId==only.ComponentId&&only.ComponentId==both.ComponentId&&first.PlanFingerprint==only.PlanFingerprint&&only.PlanFingerprint==both.PlanFingerprint,"Selection changed complete geometry or explicit source-bound plan identity.");
                    Near(first.Rows[a.UniqueId],only.Rows[a.UniqueId],"Selecting only A must not change A allocation.");Near(only.Rows[a.UniqueId],both.Rows[a.UniqueId],"A allocation must stay equal after selecting A+B.");
                    report["selectionInvariance"]=Fields("result","PASS","componentId",first.ComponentId,"planFingerprint",first.PlanFingerprint,"wallUniqueId",a.UniqueId,"volume",only.Rows[a.UniqueId],"cases",new[]{"unjoined-cross-A-first","selection-only-A","selection-both-A-B"});
                    CheckCancel(ui,a,phase,view);
                });
                Scene(document,"T",delegate{
                    Wall a=null,b=null;XYZ origin=new XYZ(5000,5400,z);Mutate(document,"Unjoined T walls",delegate{a=Wall(document,type,level,phase,origin,0,0,10,0,false);b=Wall(document,type,level,phase,origin,5,-5,5,0,false);});
                    var geometry=new[]{Analytic(a,true,1,new[]{0.0,10,-.1,.1,0,3}),Analytic(b,true,2,new[]{4.9,5.1,-5,0,0,3})};var unjoined=NormalizeUnjoined(document,new[]{a,b},view,origin,geometry);
                    CheckCase("unjoined-T-A-first",ui,new[]{a,b},new[]{a,b},phase,view,origin,geometry,true,8.94,new[]{6.0,2.94},null,null,unjoined);
                });
                Scene(document,"triple",delegate{
                    Wall a=null,b=null,c=null;XYZ origin=new XYZ(5000,5800,z);Mutate(document,"Three walls with shared intersection",delegate{a=Wall(document,type,level,phase,origin,0,0,10,0,false);c=Wall(document,type,level,phase,origin,4,0,6,0,false);b=Wall(document,type,level,phase,origin,5,-5,5,5,false);});
                    var geometry=new[]{Analytic(a,true,1,new[]{0.0,10,-.1,.1,0,3}),Analytic(c,true,2,new[]{4.0,6,-.1,.1,0,3}),Analytic(b,true,3,new[]{4.9,5.1,-5,5,0,3})};var unjoined=NormalizeUnjoined(document,new[]{a,b,c},view,origin,geometry);
                    var outcome=CheckCase("triple-shared-intersection",ui,new[]{a,b,c},new[]{a,c,b},phase,view,origin,geometry,true,11.88,new[]{6.0,0,5.88},null,null,unjoined);
                    Near(outcome.Rows[c.UniqueId],0,"Duplicate segment has legal zero allocation.");
                });
                Scene(document,"opening-cross",delegate{
                    Wall a=null,b=null;XYZ origin=new XYZ(5000,6200,z);Mutate(document,"Cross with a full-thickness native Opening",delegate{a=Wall(document,type,level,phase,origin,0,0,10,0,false);b=Wall(document,type,level,phase,origin,5,-5,5,5,false);Opening opening=document.Create.NewOpening(a,Point(origin,4,0,1),Point(origin,6,0,2));created.Add(opening.UniqueId);});
                    var ga=Analytic(a,true,1,new[]{0.0,10,-.1,.1,0,3});ga["cuts"]=new[]{new[]{4.0,6,-.1,.1,1,2}};
                    var geometry=new[]{ga,Analytic(b,true,2,new[]{4.9,5.1,-5,5,0,3})};var unjoined=NormalizeUnjoined(document,new[]{a,b},view,origin,geometry);
                    CheckCase("opening-cross",ui,new[]{a,b},new[]{a,b},phase,view,origin,geometry,true,11.52,new[]{5.6,5.92},null,null,unjoined);
                });
                Scene(document,"native-joined-cross",delegate{
                    Wall a=null,b=null;XYZ origin=new XYZ(5000,6600,z);
                    // Revit regenerates at transaction commit. Do not ask its
                    // join service to operate on newly created stale geometry.
                    Mutate(document,"Commit independent walls before native geometry join",delegate{a=Wall(document,type,level,phase,origin,0,0,10,0,false);b=Wall(document,type,level,phase,origin,5,-5,5,5,false);});
                    var geometry=new[]{Analytic(a,true,1,new[]{0.0,10,-.1,.1,0,3}),Analytic(b,true,2,new[]{4.9,5.1,-5,5,0,3})};
                    var unjoined=NormalizeUnjoined(document,new[]{a,b},view,origin,geometry);
                    var setup=Fields("method","committed-walls-before-explicit-native-geometry-join","creationTransactionCommitted",true,"geometryRegeneratedBeforeJoin",false,"areElementsJoined",false,"firstUniqueId",a.UniqueId,"secondUniqueId",b.UniqueId,"beforeUnjoinProof",unjoined,"before",null,"after",null);
                    report["nativeJoinSetup"]=setup;
                    Mutate(document,"Explicit native geometry join of regenerated walls",delegate{
                        document.Regenerate();setup["geometryRegeneratedBeforeJoin"]=true;
                        Dictionary<string,object> before=RawShapeProof(new[]{a,b},view,origin,geometry);setup["before"]=before;
                        Near(Number(before["expectedUnionMetres3"]),11.88,"Committed native-join fixture expected union.");Near(Number(before["actualUnionMetres3"]),11.88,"Committed native-join fixture actual union.");
                        Require(Number(before["symmetricDifferenceMetres3"])<=1e-8&&((List<Dictionary<string,object>>)before["perWall"]).All(item=>Number(item["symmetricDifferenceMetres3"])<=1e-8),"Native-join fixture must start as complete independent box walls.");
                        Require(!JoinGeometryUtils.AreElementsJoined(document,a,b),"Independent native-join fixture was already joined.");JoinGeometryUtils.JoinGeometry(document,a,b);document.Regenerate();
                    });
                    setup["areElementsJoined"]=JoinGeometryUtils.AreElementsJoined(document,a,b);
                    setup["joinedFirstElementIds"]=JoinGeometryUtils.GetJoinedElements(document,a).Select(Id).OrderBy(value=>value,StringComparer.Ordinal).ToArray();
                    setup["joinedSecondElementIds"]=JoinGeometryUtils.GetJoinedElements(document,b).Select(Id).OrderBy(value=>value,StringComparer.Ordinal).ToArray();
                    Require((bool)setup["areElementsJoined"]&&((string[])setup["joinedFirstElementIds"]).Contains(Id(b.Id))&&((string[])setup["joinedSecondElementIds"]).Contains(Id(a.Id)),"Native geometry join must have reciprocal explicit association.");
                    Dictionary<string,object> after=RawShapeProof(new[]{a,b},view,origin,geometry);setup["after"]=after;
                    Near(Number(after["expectedUnionMetres3"]),11.88,"Native-join expected union is fixed analytically.");Near(Number(after["actualUnionMetres3"]),11.88,"Native geometry join must preserve complete fixture union.");
                    var afterWalls=(List<Dictionary<string,object>>)after["perWall"];
                    Require(Number(after["symmetricDifferenceMetres3"])<=1e-8&&afterWalls.All(item=>Number(item["actualMinusExpectedMetres3"])<=1e-8)&&afterWalls.Any(item=>Number(item["expectedMinusActualMetres3"])>1e-8),"Native join must actually trim a wall, preserve component union, and introduce no outside-gross extension.");
                    var outcome=CheckCase("native-joined-cross",ui,new[]{a,b},new[]{a,b},phase,view,origin,geometry,true,11.88,new[]{6.0,5.88},null);outcome.Record["nativeJoinSetupProof"]=setup;
                });
                Scene(document,"L-extension",delegate{
                    Wall a=null,b=null;XYZ origin=new XYZ(5000,7000,z);Mutate(document,"Natural endpoint L join",delegate{a=Wall(document,type,level,phase,origin,0,0,5,0,true);b=Wall(document,type,level,phase,origin,5,0,5,5,true);document.Regenerate();if(!JoinGeometryUtils.AreElementsJoined(document,a,b))JoinGeometryUtils.JoinGeometry(document,a,b);});
                    var geometry=new[]{Analytic(a,true,1,new[]{0.0,5,-.1,.1,0,3}),Analytic(b,true,2,new[]{4.9,5.1,0,5,0,3})};
                    Dictionary<string,object> proof=RawShapeProof(new[]{a,b},view,origin,geometry);
                    Require(Convert.ToDouble(proof["symmetricDifferenceMetres3"],CultureInfo.InvariantCulture)>1e-8,"L fixture did not produce an actual outside-gross shape extension; refusal expectation was not proved.");
                    CheckCase("native-L-end-extension-refused",ui,new[]{a,b},new[]{a,b},phase,view,origin,geometry,false,5.97,new double[0],null,proof);
                });
                Scene(document,"profile",delegate{
                    Wall wall=null;XYZ origin=new XYZ(5000,7400,z);var points=new[]{Point(origin,0,0,0),Point(origin,4,0,0),Point(origin,4,0,3),Point(origin,2,0,4),Point(origin,0,0,3)};
                    Mutate(document,"Nonrectangular actual profile wall with explicit project base",delegate{
                        var curves=new List<Curve>();for(int index=0;index<points.Length;index++)curves.Add(Line.CreateBound(points[index],points[(index+1)%points.Length]));
                        wall=Autodesk.Revit.DB.Wall.Create(document,curves,type.Id,level.Id,false,new XYZ(0,-1,0));wall.CreatedPhaseId=phase.Id;
                        Parameter reference=wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);if(reference!=null&&!reference.IsReadOnly)reference.Set(0);
                        Parameter baseConstraint=wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT),baseOffset=wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                        Require(baseConstraint!=null&&baseConstraint.StorageType==StorageType.ElementId&&baseConstraint.AsElementId().Equals(level.Id),"Profile wall base constraint must match the explicitly supplied level.");
                        Require(baseOffset!=null&&baseOffset.StorageType==StorageType.Double&&baseOffset.HasValue&&!baseOffset.IsReadOnly,"Profile wall requires an editable explicit base offset.");
                        double offsetBefore=baseOffset.AsDouble(),desiredOffset=origin.Z-level.ProjectElevation;
                        // The profile overload may choose an offset using the
                        // level's displayed/shared Elevation. Our prescribed
                        // world coordinates use ProjectElevation throughout.
                        // This correction uses declared inputs and parameters,
                        // never a measured edge/apex or a reconstructed origin.
                        var construction=Fields("method","explicit-project-origin-base-constraint-and-offset","inputOriginFeet",new[]{origin.X,origin.Y,origin.Z},"inputProfilePointsFeet",points.Select(point=>new[]{point.X,point.Y,point.Z}).ToArray(),"levelUniqueId",level.UniqueId,"levelElementId",Id(level.Id),"levelElevationFeet",level.Elevation,"levelProjectElevationFeet",level.ProjectElevation,"wallBaseConstraintIdBefore",Id(baseConstraint.AsElementId()),"wallBaseOffsetFeetBefore",offsetBefore,"requestedBaseOffsetFeet",desiredOffset,"expectedBaseProjectElevationFeet",origin.Z,"nativeGeometryUsedToChooseOffset",false);
                        report["profileConstruction"]=construction;baseOffset.Set(desiredOffset);document.Regenerate();
                        construction["wallBaseConstraintIdAfter"]=Id(baseConstraint.AsElementId());construction["wallBaseOffsetFeetAfter"]=baseOffset.AsDouble();
                        Require(baseConstraint.AsElementId().Equals(level.Id),"Explicit profile base constraint changed.");Near(baseOffset.AsDouble(),desiredOffset,"Profile base offset must follow the prescribed project origin.");
                        WallUtils.DisallowWallJoinAtEnd(wall,0);WallUtils.DisallowWallJoinAtEnd(wall,1);document.Regenerate();created.Add(wall.UniqueId);
                    });
                    var profileProof=RawProfileProof(wall,view,origin,report);
                    var outcome=CheckCase("unknown-profile-refused",ui,new[]{wall},new[]{wall},phase,view,origin,new[]{Analytic(wall,true,1,new[]{0.0,4,-.1,.1,0,4})},false,Double.NaN,new double[0],null);
                    outcome.Record["actualNonrectangularProfileMetres"]=new[]{new[]{0.0,0,0},new[]{4.0,0,0},new[]{4.0,0,3},new[]{2.0,0,4},new[]{0.0,0,3}};
                    outcome.Record["actualProfileProof"]=profileProof;
                });
                Require(Id(ui.ActiveView.Id)==activeView,"Geometry view was activated.");
                Require(group.RollBack()==TransactionStatus.RolledBack,"Fixture transaction group rollback failed.");rolledBack=true;
                Require(created.All(uid=>document.GetElement(uid)==null)&&Count(document)==originalCount,"Temporary fixture objects survived rollback.");
                report["result"]="PASS";
            }
            catch(Exception error){report["error"]=Unwrap(error).ToString();}
            finally
            {
                if(group!=null){if(!rolledBack)try{rolledBack=group.RollBack()==TransactionStatus.RolledBack;}catch(Exception error){report["rollbackError"]=error.ToString();}try{group.Dispose();}catch(Exception error){report["disposeError"]=error.ToString();report["result"]="FAIL";}}
                report["fixtureRolledBack"]=rolledBack;report["fixtureCreatedUniqueIds"]=created;report["cases"]=cases;
                if(ui!=null&&selection!=null)try{ui.Selection.SetElementIds(selection);report["selectionRestored"]=new HashSet<string>(ui.Selection.GetElementIds().Select(Id)).SetEquals(selection.Select(Id));}catch(Exception error){report["selectionRestoreError"]=error.ToString();report["result"]="FAIL";}
                if(document!=null&&document.IsValidObject)try{report["activeViewUnchanged"]=Id(ui.ActiveView.Id)==activeView;report["elementCountAfter"]=Count(document);report["isModifiedAfter"]=document.IsModified;}catch(Exception error){report["finalDocumentError"]=error.ToString();report["result"]="FAIL";}
                if(modelHash!=null)try{report["sourceSha256After"]=Hash(config.modelPath);report["sourceHashUnchanged"]=(string)report["sourceSha256After"]==modelHash;}catch{report["sourceHashUnchanged"]=false;}
                try{report["originalSourceHashUnchanged"]=Hash(config.originalModelPath)==config.expectedOriginalModelSha256;}catch{report["originalSourceHashUnchanged"]=false;}
                if(attached)try{Call(nativeReader,"Detach",host);report["revisionTrackingDetached"]=true;}catch(Exception error){report["detachError"]=error.ToString();report["result"]="FAIL";}
                if(!rolledBack||False(report,"selectionRestored")||False(report,"activeViewUnchanged")||False(report,"sourceHashUnchanged")||False(report,"originalSourceHashUnchanged")||(report.ContainsKey("elementCountAfter")&&(int)report["elementCountAfter"]!=originalCount))report["result"]="FAIL";
                string[] required={"unjoined-cross-A-first","unjoined-cross-B-first","selection-only-A","selection-both-A-B","unjoined-T-A-first","triple-shared-intersection","opening-cross","native-joined-cross","native-L-end-extension-refused","unknown-profile-refused"};
                bool all=required.All(name=>cases.Any(item=>(string)item["name"]==name&&(string)item["result"]=="PASS"));
                report["readerCancellationVerified"]=cancellationVerified;
                if(!all||!cancellationVerified)report["result"]="FAIL";
                report["requiredCases"]=required;report["liveRevit110JoinSdkVerified"]=(string)report["result"]=="PASS"&&all&&rolledBack;report["assertions"]=checks;report["elapsedSeconds"]=timer.Elapsed.TotalSeconds;report["finishedUtc"]=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture);report["fixtureWarnings"]=warnings;
                try{report["artifacts"]=Directory.GetFiles(config.outputDirectory).Where(path=>!Path.GetFileName(path).StartsWith("civil-wall-join-host-",StringComparison.Ordinal)).Select(path=>Fields("name",Path.GetFileName(path),"bytes",new FileInfo(path).Length,"sha256",Hash(path))).ToList();Save("civil-wall-join-host-report.json",report);Status((string)report["result"]=="PASS"?"completed":"failed");}catch(Exception error){try{app.Application.WriteJournalComment("BV join evidence write failed: "+error,false);}catch{}}
            }
        }

        private sealed class Outcome { internal object Plan;internal string PlanFingerprint,ComponentId;internal Dictionary<string,double> Rows;internal Dictionary<string,object> Record; }
        private Outcome CheckCase(string name,UIDocument ui,Wall[] selection,Wall[] priority,Phase phase,View3D view,XYZ origin,Dictionary<string,object>[] geometry,bool expectedCalculated,double expectedUnion,double[] expectedVolumes,object reusePlan,Dictionary<string,object> independentProof=null,Dictionary<string,object> unjoinedSetupProof=null)
        {
            Status("reading-"+name);ui.Selection.SetElementIds(selection.Select(wall=>wall.Id).ToList());
            object request=Call(nativeReader,"DefaultRequest",ui);Set(request,"Scope","selection");Set(request,"PhaseId",Id(phase.Id));Set(request,"DesignOptionId","-1");
            object native=Call(nativeReader,"Read",ui,request);string before=json.Serialize(native);SaveText(name+"-native-before.json",before);
            object rule=Call(rules,"Default"),bundle=null;object snapshot=null,batch=null;IList calculations=List("BrightVision.Civil.CivilWallJoinCalculation");
            var resultRows=new Dictionary<string,double>();object usedPlan=null;string componentId=null,planFingerprint=null;
            try
            {
                bundle=Call(joinReader,"Read",ui,native,request,view,rule,null);string after=json.Serialize(native);SaveText(name+"-native-after-reader.json",after);SameNative(before,after,"Reader changed original source snapshot.");
                IList components=(IList)Get(bundle,"Components");Require(components.Count==1,"Expected exactly one complete connected fixture component.");object component=components[0];componentId=(string)Get(component,"ComponentId");
                IList participants=(IList)Get(component,"Participants");Require(participants.Count==priority.Length,"Range-external dependency closure was not complete.");
                var expectedIds=new HashSet<string>(priority.Select(wall=>wall.UniqueId));Require(expectedIds.SetEquals(participants.Cast<object>().Select(wall=>(string)Get(wall,"ElementUniqueId"))),"Prepared participant identity differs from actual fixture walls.");
                snapshot=Capture(bundle,request);Call(session,"ValidateReadFacts",snapshot);
                object result;
                if((string)Get(component,"Status")=="ready")
                {
                    usedPlan=reusePlan==null?Call(plans,"Create",Get(component,"Binding"),priority.Select(wall=>wall.UniqueId).ToList(),"SDK explicit order "+name):Call(plans,"Clone",reusePlan);
                    Call(plans,"ValidateFor",usedPlan,Get(component,"Binding"),priority.Select(wall=>wall.UniqueId).ToList());
                    var ranks=((IList)Get(usedPlan,"Priorities")).Cast<object>().ToDictionary(item=>(string)Get(item,"ElementUniqueId"),item=>(int)Get(item,"Rank"));
                    foreach(object node in (IList)Get(Get(component,"Input"),"Nodes"))Set(node,"PriorityRank",ranks[(string)Get(node,"UniqueId")]);
                    result=Call(core,"Calculate",Get(component,"Input"),Get(bundle,"Operations"),null);
                    if((string)Get(result,"Status")=="failed"||(string)Get(result,"Status")=="cancelled")throw new InvalidOperationException((string)Get(result,"Message"));
                    planFingerprint=(string)Call(plans,"Fingerprint",usedPlan);
                }
                else{result=New("BrightVision.Civil.CivilWallJoinComponentResult");Set(result,"Status","unsupported");Set(result,"Message",Get(component,"Message"));Set(result,"ContextKey",componentId);}
                object calculation=New("BrightVision.Civil.CivilWallJoinCalculation");Set(calculation,"ComponentId",componentId);Set(calculation,"Plan",usedPlan);Set(calculation,"Result",result);calculations.Add(calculation);
                batch=Call(session,"Create",snapshot,calculations);Call(session,"Validate",batch);
                string afterCalc=json.Serialize(native);SaveText(name+"-native-after-calculation.json",afterCalc);SameNative(before,afterCalc,"Core or session changed original native facts.");
                if(expectedCalculated)
                {
                    Require((string)Get(result,"Status")=="calculated","Expected supported actual component failed: "+json.Serialize(Get(component,"Message"))+" / "+Get(result,"Message"));
                    foreach(object evidence in (IList)Get(component,"Evidence"))Near(Number(Get(evidence,"NormalMinFeet"))*.3048,-.1,"Centered wall transverse reference must be -0.1m, not a guessed key reference.");
                    Near(Number(Get(result,"ExpectedUnionVolumeMetres3")),expectedUnion,"Manual box-union expectation.");Near(Number(Get(result,"SumResultVolumeMetres3")),expectedUnion,"Manual component allocated sum.");
                    foreach(object row in (IList)Get(result,"Rows"))resultRows.Add((string)Get(row,"UniqueId"),Number(Get(row,"ResultVolumeMetres3")));
                    for(int index=0;index<priority.Length;index++)Near(resultRows[priority[index].UniqueId],expectedVolumes[index],"Explicit manual row allocation "+index);
                    if(name=="triple-shared-intersection")
                    {
                        object last=((IList)Get(result,"Rows")).Cast<object>().Single(row=>(string)Get(row,"UniqueId")==priority[2].UniqueId);IList steps=(IList)Get(last,"Steps");
                        Require(steps.Count==2,"Triple intersection ledger coverage.");Near(Number(Get(steps[0],"DeductionVolumeMetres3")),.12,"First triple deduction.");Near(Number(Get(steps[1],"DeductionVolumeMetres3")),0,"Shared triple intersection must not be deducted again.");
                    }
                }
                else Require((string)Get(result,"Status")=="unsupported"&&((IList)Get(result,"Rows")).Count==0&&Get(result,"ExpectedUnionVolumeMetres3")==null&&Get(result,"SumResultVolumeMetres3")==null,"Unsupported geometry emitted a volume or partial rows.");
                string csv=(string)Call(session,"ToCsv",batch,true),requestedCsv=(string)Call(session,"ToCsv",batch,false);SaveText(name+"-join-report.csv",csv);SaveText(name+"-requested-report.csv",requestedCsv);
                Require(csv.Contains("BV.Wall.JoinReport/1")&&csv.Contains("BV.Wall.ComponentPriority/1")&&csv.Contains((string)Get(batch,"InputFingerprint"))&&csv.Contains((string)Get(batch,"ResultFingerprint")),"JoinReport omitted source/result identity.");
                SameNative(before,json.Serialize(native),"CSV changed native source facts.");
                Save(name+"-snapshot.json",snapshot);Save(name+"-calculations.json",calculations);
                Save(name+"-native-comparison.json",Fields("result","PASS","rawSerializedBytesEqual",true,"readerDifferencePath",null,"calculationDifferencePath",null));
                var record=Fields("name",name,"result","PASS","expectedCalculated",expectedCalculated,"manualExpectedUnionMetres3",Double.IsNaN(expectedUnion)?null:(object)expectedUnion,
                    "manualExpectedRows",expectedCalculated?priority.Select((wall,index)=>Fields("uid",wall.UniqueId,"volume",expectedVolumes[index])).ToList():null,
                    "componentId",componentId,"bindingFingerprint",Get(Get(component,"Binding"),"GeometryDependencyFingerprint"),"planFingerprint",planFingerprint,"batchId",Get(batch,"BatchId"),"inputFingerprint",Get(batch,"InputFingerprint"),"resultFingerprint",Get(batch,"ResultFingerprint"),
                    "analytic",Fields("unit","metres-relative-to-origin-feet","originFeet",new[]{origin.X,origin.Y,origin.Z},"walls",geometry),"componentResults",new[]{result},"selectionUniqueIds",selection.Select(wall=>wall.UniqueId).ToArray(),
                    "nativeSnapshotUnchanged",true,"independentNativeShapeProof",independentProof,"unjoinedSetupProof",unjoinedSetupProof);
                cases.Add(record);return new Outcome{Plan=usedPlan,PlanFingerprint=planFingerprint,ComponentId=componentId,Rows=resultRows,Record=record};
            }
            finally{if(bundle!=null){((IDisposable)bundle).Dispose();Require((bool)Get(bundle,"IsDisposed"),"Native read bundle did not dispose its prepared operands.");}}
        }
        private object Capture(object bundle,object request)
        {
            object snapshot=New("BrightVision.Civil.CivilWallJoinSnapshot");Set(snapshot,"NativeSnapshot",Get(bundle,"NativeSnapshot"));Set(snapshot,"PhaseId",Get(request,"PhaseId"));Set(snapshot,"DesignOptionId",Get(request,"DesignOptionId"));
            Set(snapshot,"GeometryViewUniqueId",Get(bundle,"GeometryViewUniqueId"));Set(snapshot,"GeometryViewName",Get(bundle,"GeometryViewName"));Set(snapshot,"OpeningRule",Call(rules,"Clone",Get(bundle,"RuleTemplate")));Set(snapshot,"Notices",Get(bundle,"Notices"));
            IList facts=List("BrightVision.Civil.CivilWallJoinComponentFacts");foreach(object component in (IList)Get(bundle,"Components")){object item=New("BrightVision.Civil.CivilWallJoinComponentFacts");Set(item,"ComponentId",Get(component,"ComponentId"));Set(item,"Binding",Call(plans,"CloneBinding",Get(component,"Binding")));Set(item,"Status",Get(component,"Status"));Set(item,"Message",Get(component,"Message"));Set(item,"Participants",Get(component,"Participants"));Set(item,"Evidence",Get(component,"Evidence"));facts.Add(item);}Set(snapshot,"Components",facts);return snapshot;
        }
        private void CheckCancel(UIDocument ui,Wall wall,Phase phase,View3D view)
        {
            ui.Selection.SetElementIds(new List<ElementId>{wall.Id});object request=Call(nativeReader,"DefaultRequest",ui);Set(request,"Scope","selection");Set(request,"PhaseId",Id(phase.Id));Set(request,"DesignOptionId","-1");object native=Call(nativeReader,"Read",ui,request);string before=json.Serialize(native);SaveText("cancel-reader-native-before.json",before);
            object control=New("BrightVision.Civil.CivilWallReadControl");control.GetType().GetField("IsCancellationRequested",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(control,new Func<bool>(()=>true));bool cancelled=false;
            try{object unexpected=Call(joinReader,"Read",ui,native,request,view,Call(rules,"Default"),control);if(unexpected!=null)((IDisposable)unexpected).Dispose();}
            catch(Exception error){cancelled=error is OperationCanceledException||error.GetType().FullName=="Autodesk.Revit.Exceptions.OperationCanceledException";if(!cancelled)throw;}
            Require(cancelled,"Reader did not propagate requested cancellation.");string after=json.Serialize(native);SaveText("cancel-reader-native-after.json",after);SameNative(before,after,"Cancellation changed native source facts.");
            Save("reader-cancellation.json",Fields("result","PASS","cancelled",true,"batchCommitted",false,"nativeSnapshotUnchanged",true));
            cancellationVerified=true;
        }
        private bool cancellationVerified;
        private Dictionary<string,object> NormalizeUnjoined(Document document,Wall[] walls,View3D view,XYZ origin,Dictionary<string,object>[] analytic)
        {
            // DisallowWallJoinAtEnd controls endpoint cleanup only. Creation
            // commit can still establish a mid-wall native geometry join.
            // Remove each actual native association after that commit, then
            // independently prove the declared unjoined fixture's full shape.
            var removed=new List<Dictionary<string,object>>();
            Mutate(document,"Remove automatic native geometry joins after creation commit",delegate{
                foreach(Wall wall in walls){WallUtils.DisallowWallJoinAtEnd(wall,0);WallUtils.DisallowWallJoinAtEnd(wall,1);}
                document.Regenerate();
                for(int first=0;first<walls.Length;first++)for(int second=first+1;second<walls.Length;second++)
                {
                    if(!JoinGeometryUtils.AreElementsJoined(document,walls[first],walls[second]))continue;
                    JoinGeometryUtils.UnjoinGeometry(document,walls[first],walls[second]);
                    Require(!JoinGeometryUtils.AreElementsJoined(document,walls[first],walls[second]),"Native automatic geometry relation survived explicit UnjoinGeometry.");
                    removed.Add(Fields("firstUniqueId",walls[first].UniqueId,"secondUniqueId",walls[second].UniqueId,"areElementsJoinedBefore",true,"areElementsJoinedAfter",false));
                }
                document.Regenerate();
            });
            for(int first=0;first<walls.Length;first++)for(int second=first+1;second<walls.Length;second++)Require(!JoinGeometryUtils.AreElementsJoined(document,walls[first],walls[second]),"A native wall geometry association reappeared after unjoin regeneration/commit.");
            var associations=new List<Dictionary<string,object>>();
            foreach(Wall wall in walls)
            {
                string[] geometry=JoinGeometryUtils.GetJoinedElements(document,wall).Select(Id).Distinct(StringComparer.Ordinal).OrderBy(value=>value,StringComparer.Ordinal).ToArray();
                var endpointIds=new HashSet<string>(StringComparer.Ordinal);LocationCurve curve=wall.Location as LocationCurve;
                if(curve!=null)for(int end=0;end<2;end++)foreach(Element joined in curve.get_ElementsAtJoin(end))if(joined!=null&&!joined.Id.Equals(wall.Id))endpointIds.Add(Id(joined.Id));
                Require(geometry.Length==0&&endpointIds.Count==0,"Declared unjoined fixture has an actual geometry or endpoint association after explicit cleanup.");
                associations.Add(Fields("uid",wall.UniqueId,"id",Id(wall.Id),"joinedElementIds",geometry,"endpointJoinedElementIds",endpointIds.OrderBy(value=>value,StringComparer.Ordinal).ToArray()));
            }
            Dictionary<string,object> shape=RawShapeProof(walls,view,origin,analytic);
            Require(Number(shape["symmetricDifferenceMetres3"])<=1e-8&&((List<Dictionary<string,object>>)shape["perWall"]).All(item=>Number(item["symmetricDifferenceMetres3"])<=1e-8),"Declared unjoined fixture must have E=A for every wall, including actual opening cutouts.");
            return Fields("method","explicit-native-unjoin-after-creation-commit","creationTransactionCommitted",true,"geometryRegeneratedAfterUnjoin",true,"unjoinedPairs",removed,"wallAssociations",associations,"shape",shape);
        }
        private Dictionary<string,object> RawShapeProof(Wall[] walls,View3D view,XYZ origin,Dictionary<string,object>[] analytic)
        {
            var owned=new List<Solid>();try
            {
                Solid expected=null,actual=null;var perWall=new List<Dictionary<string,object>>();
                Require(walls.Length==analytic.Length&&walls.Select(wall=>wall.UniqueId).Distinct(StringComparer.Ordinal).Count()==walls.Length,"Independent manual shape coverage must be exact.");
                foreach(Wall wall in walls)
                {
                    Dictionary<string,object> item=analytic.Single(value=>(string)value["uid"]==wall.UniqueId);Require((string)item["id"]==Id(wall.Id),"Independent raw shape helper requires exact wall identity.");
                    Solid expectedWall=Own(Box(origin,(double[])item["box"]),owned),actualWall=null;
                    foreach(double[] cut in (double[][])item["cuts"]){Solid cutter=Own(Box(origin,cut),owned);expectedWall=Own(BooleanOperationsUtils.ExecuteBooleanOperation(expectedWall,cutter,BooleanOperationsType.Difference),owned);}
                    using(var options=new Options()){options.View=view;using(GeometryElement geometry=wall.get_Geometry(options)){Require(geometry!=null,"Independent actual shape absent.");foreach(GeometryObject native in geometry){Require(!(native is Mesh)&&!(native is GeometryInstance),"Independent shape contains unsupported branch.");Solid part=native as Solid;if(part==null||part.Faces.Size==0||part.Volume<1e-12)continue;part=Own(SolidUtils.Clone(part),owned);actualWall=actualWall==null?part:Own(BooleanOperationsUtils.ExecuteBooleanOperation(actualWall,part,BooleanOperationsType.Union),owned);}}}
                    Require(actualWall!=null,"Independent raw shape requires a positive actual wall solid.");
                    Solid wallMissing=Own(BooleanOperationsUtils.ExecuteBooleanOperation(expectedWall,actualWall,BooleanOperationsType.Difference),owned),wallExtra=Own(BooleanOperationsUtils.ExecuteBooleanOperation(actualWall,expectedWall,BooleanOperationsType.Difference),owned);
                    perWall.Add(Fields("uid",wall.UniqueId,"id",Id(wall.Id),"expectedVolumeMetres3",expectedWall.Volume*Math.Pow(.3048,3),"actualVolumeMetres3",actualWall.Volume*Math.Pow(.3048,3),"expectedMinusActualMetres3",wallMissing.Volume*Math.Pow(.3048,3),"actualMinusExpectedMetres3",wallExtra.Volume*Math.Pow(.3048,3),"symmetricDifferenceMetres3",(wallMissing.Volume+wallExtra.Volume)*Math.Pow(.3048,3)));
                    expected=expected==null?expectedWall:Own(BooleanOperationsUtils.ExecuteBooleanOperation(expected,expectedWall,BooleanOperationsType.Union),owned);actual=actual==null?actualWall:Own(BooleanOperationsUtils.ExecuteBooleanOperation(actual,actualWall,BooleanOperationsType.Union),owned);
                }
                Require(expected!=null&&actual!=null,"Independent shape proof needs positive native solids.");Solid missing=Own(BooleanOperationsUtils.ExecuteBooleanOperation(expected,actual,BooleanOperationsType.Difference),owned),extra=Own(BooleanOperationsUtils.ExecuteBooleanOperation(actual,expected,BooleanOperationsType.Difference),owned);
                return Fields("method","independent-manual-box-union-vs-raw-native-solid-union","expectedUnionMetres3",expected.Volume*Math.Pow(.3048,3),"actualUnionMetres3",actual.Volume*Math.Pow(.3048,3),"symmetricDifferenceMetres3",(missing.Volume+extra.Volume)*Math.Pow(.3048,3),"perWall",perWall);
            }finally{for(int index=owned.Count-1;index>=0;index--)owned[index].Dispose();}
        }
        private Dictionary<string,object> RawProfileProof(Wall wall,View3D view,XYZ origin,Dictionary<string,object> report)
        {
            var vertices=new List<double[]>();double volume=0;
            using(var options=new Options())
            {
                options.View=view;
                using(GeometryElement geometry=wall.get_Geometry(options))
                {
                    Require(geometry!=null,"Actual profile wall geometry missing.");
                    foreach(GeometryObject item in geometry)
                    {
                        Require(!(item is Mesh)&&!(item is GeometryInstance),"Actual profile wall geometry not independently measurable.");
                        Solid solid=item as Solid;if(solid==null||solid.Faces.Size==0||solid.Volume<1e-12)continue;
                        volume+=solid.Volume*Math.Pow(.3048,3);
                        foreach(Edge edge in solid.Edges)
                        {
                            // Measure the actual topological edge at its
                            // normalized boundaries, rather than the endpoint
                            // representation of its converted underlying curve.
                            for(int end=0;end<2;end++){XYZ point=edge.Evaluate(end);vertices.Add(new[]{(point.X-origin.X)*.3048,(point.Y-origin.Y)*.3048,(point.Z-origin.Z)*.3048});}
                        }
                    }
                }
            }
            var proof=Fields("method","raw-native-edge-endpoints-and-volume","unit","metres-relative-to-origin-feet","vertices",vertices,"nativeVolumeMetres3",volume,"manualShoelaceAreaMetres2",14.0,"thicknessMetres",.2,"endpointMeasurement","Edge.Evaluate normalized 0/1");
            proof["construction"]=report["profileConstruction"];
            // Preserve the measured coordinates even if a strict fixture gate
            // fails. Failure evidence must not disappear before serialization.
            report["profileProofDiagnostic"]=proof;
            Near(volume,2.8,"Actual five-edge profile must have shoelace area14m2 times0.2m thickness.");
            foreach(double[] expected in new[]{new[]{0.0,0},new[]{4.0,0},new[]{4.0,3},new[]{2.0,4},new[]{0.0,3}})Require(vertices.Any(point=>Math.Abs(point[0]-expected[0])<1e-6&&Math.Abs(point[2]-expected[1])<1e-6),"Actual native profile is missing an independently prescribed gable vertex "+expected[0]+","+expected[1]+".");
            Require(vertices.Count>0,"Actual profile edge set is empty.");Near(vertices.Max(point=>point[1])-vertices.Min(point=>point[1]),.2,"Actual native profile thickness remains fixed.");
            return proof;
        }
        private static Solid Own(Solid value,IList<Solid> owned){if(value==null)throw new InvalidOperationException("Independent CSG returned null.");owned.Add(value);return value;}
        private static Solid Box(XYZ origin,double[] box)
        {using(var loop=new CurveLoop()){XYZ a=Point(origin,box[0],box[2],box[4]),b=Point(origin,box[1],box[2],box[4]),c=Point(origin,box[1],box[3],box[4]),d=Point(origin,box[0],box[3],box[4]);loop.Append(Line.CreateBound(a,b));loop.Append(Line.CreateBound(b,c));loop.Append(Line.CreateBound(c,d));loop.Append(Line.CreateBound(d,a));return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop>{loop},XYZ.BasisZ,(box[5]-box[4])*F);}}
        private Dictionary<string,object> Analytic(Wall wall,bool requested,int rank,double[] box){return Fields("uid",wall.UniqueId,"id",Id(wall.Id),"requested",requested,"rank",rank,"box",box,"cuts",new double[0][]);}
        private Wall Wall(Document document,WallType type,Level level,Phase phase,XYZ origin,double x0,double y0,double x1,double y1,bool joins)
        {
            Wall wall=Autodesk.Revit.DB.Wall.Create(document,Line.CreateBound(Point(origin,x0,y0,0),Point(origin,x1,y1,0)),type.Id,level.Id,3*F,0,false,false);wall.CreatedPhaseId=phase.Id;
            Parameter reference=wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);if(reference!=null&&!reference.IsReadOnly)reference.Set(0);
            if(joins){WallUtils.AllowWallJoinAtEnd(wall,0);WallUtils.AllowWallJoinAtEnd(wall,1);}else{WallUtils.DisallowWallJoinAtEnd(wall,0);WallUtils.DisallowWallJoinAtEnd(wall,1);}created.Add(wall.UniqueId);return wall;
        }
        private void Scene(Document document,string name,Action action){using(var scene=new TransactionGroup(document,"BV join scene "+name)){Require(scene.Start()==TransactionStatus.Started,"Cannot start scenario group.");try{action();}finally{Require(scene.RollBack()==TransactionStatus.RolledBack,"Scenario rollback failed.");}}}
        private void Mutate(Document document,string name,Action action){using(var transaction=new Transaction(document,name)){Require(transaction.Start()==TransactionStatus.Started,"Cannot start fixture mutation.");transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new Failures(warnings)).SetClearAfterRollback(true));try{action();Require(transaction.Commit()==TransactionStatus.Committed,"Fixture mutation rolled back.");}catch{if(transaction.GetStatus()==TransactionStatus.Started)transaction.RollBack();throw;}}}
        private sealed class Failures:IFailuresPreprocessor{private readonly IList<string> messages;internal Failures(IList<string> messages){this.messages=messages;}public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor){foreach(var failure in accessor.GetFailureMessages()){messages.Add(failure.GetDescriptionText());if(failure.GetSeverity()!=FailureSeverity.Warning)return FailureProcessingResult.ProceedWithRollBack;accessor.DeleteWarning(failure);}return FailureProcessingResult.Continue;}}
        private static XYZ Point(XYZ origin,double x,double y,double z){return new XYZ(origin.X+x*F,origin.Y+y*F,origin.Z+z*F);}
        private object New(string type){return Activator.CreateInstance(production.GetType(type,true),true);}
        private IList List(string type){return(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(production.GetType(type,true)));}
        private static object Get(object value,string property){return value.GetType().GetProperty(property,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(value,null);}
        private static void Set(object value,string property,object data){value.GetType().GetProperty(property,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(value,data,null);}
        private static object Call(Type type,string name,params object[] arguments){try{return type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).Single(method=>method.Name==name&&method.GetParameters().Length==arguments.Length).Invoke(null,arguments);}catch(TargetInvocationException error){throw Unwrap(error);}}
        private static Exception Unwrap(Exception error){while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;return error;}
        private static int Count(Document document){using(var collector=new FilteredElementCollector(document))return collector.WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(false),new ElementIsElementTypeFilter(true))).ToElementIds().Count;}
        private void SameNative(string before,string after,string message){Require(before==after&&CivilSnapshotComparison.CompareJson(json,before,after)==null,message);}
        private static double Number(object value){return Convert.ToDouble(value,CultureInfo.InvariantCulture);}
        private void Near(double actual,double expected,string message){Require(!Double.IsNaN(actual)&&!Double.IsInfinity(actual)&&Math.Abs(actual-expected)<=Math.Max(1e-7,Math.Abs(expected)*1e-7),message);}
        private void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);checks++;}
        private static string Id(ElementId id){return id.IntegerValue.ToString(CultureInfo.InvariantCulture);}
        private static bool False(IDictionary<string,object> item,string key){return item.ContainsKey(key)&&item[key] is bool&&!(bool)item[key];}
        private static bool Same(string a,string b){return !String.IsNullOrEmpty(a)&&!String.IsNullOrEmpty(b)&&String.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);}
        private static string Hash(string file){using(var hash=SHA256.Create())using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        private string Child(string path){path=Path.GetFullPath(path);string root=Path.GetFullPath(boundary).TrimEnd('\\')+"\\";if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Evidence path leaves new join run boundary.");for(string cursor=path;cursor!=null&&cursor.Length>=root.Length-1;cursor=Path.GetDirectoryName(cursor))if((File.Exists(cursor)||Directory.Exists(cursor))&&(File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Reparse evidence path refused.");return path;}
        private static Dictionary<string,object> Fields(params object[] data){var result=new Dictionary<string,object>();for(int index=0;index<data.Length;index+=2)result.Add((string)data[index],data[index+1]);return result;}
        private void Save(string name,object data){SaveText(name,json.Serialize(data));}
        private void SaveText(string name,string text){if(Encoding.UTF8.GetByteCount(text)>16777216)throw new InvalidDataException("Evidence exceeds 16MiB.");File.WriteAllText(Child(Path.Combine(config.outputDirectory,name)),text,new UTF8Encoding(name.EndsWith(".csv",StringComparison.Ordinal)));}
        private void Status(string phase){Save("civil-wall-join-host-status.json",Fields("phase",phase,"runId",config.runId,"utc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)));}
        private sealed class Config
        {
            public string runId{get;set;}public string modelPath{get;set;}public string outputDirectory{get;set;}public string originalModelPath{get;set;}public string expectedOriginalModelSha256{get;set;}
            public string exporterPath{get;set;}public string expectedExporterSha256{get;set;}public string expectedAssemblyVersion{get;set;}public string expectedPackageVersion{get;set;}public int productionSourceCount{get;set;}
            public string productionManifestPath{get;set;}public string productionManifestSha256{get;set;}public string expectedModelSha256{get;set;}public string expectedDriverSha256{get;set;}
            public string expectedRevitApiPath{get;set;}public string expectedRevitApiSha256{get;set;}public string expectedRevitApiUiPath{get;set;}public string expectedRevitApiUiSha256{get;set;}
        }
    }
}
