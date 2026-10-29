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
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using CivilSnapshotComparison = BrightVision.CivilWallHostAcceptance.CivilSnapshotComparison;

[assembly: AssemblyVersion("1.0.0.0")]
namespace BrightVision.CivilWallPhase3HostAcceptance
{
    // An independent, explicitly configured one-run SDK driver. Preparing or
    // compiling this DLL does not register it, open a document or start Revit.
    public sealed class App : IExternalApplication
    {
        private const string ConfigurationEnvironment = "BV_CIVIL_WALL_PHASE3_CONFIG";
        private const string NonceEnvironment = "BV_CIVIL_WALL_PHASE3_RUN_ID";
        private const double FeetPerMetre = 1.0 / 0.3048;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16777216 };
        private readonly List<string> warnings = new List<string>();
        private UIControlledApplication host;
        private Config config;
        private string boundary;
        private int checks;

        public Result OnStartup(UIControlledApplication application)
        {
            string file = Environment.GetEnvironmentVariable(ConfigurationEnvironment);
            string nonce = Environment.GetEnvironmentVariable(NonceEnvironment); Guid identifier;
            if (String.IsNullOrEmpty(file) || !Guid.TryParseExact(nonce,"N",out identifier) || identifier == Guid.Empty) return Result.Succeeded;
            try
            {
                DirectoryInfo cursor = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory;
                while (cursor != null && !File.Exists(Path.Combine(cursor.FullName,"tools","bright-revit-exporter","CivilWallFacts.cs"))) cursor = cursor.Parent;
                if (cursor == null) return Result.Succeeded;
                boundary = Path.Combine(cursor.FullName,"test","revit-civil-acceptance","phase3-1.9");
                file = Child(file); Config candidate = json.Deserialize<Config>(File.ReadAllText(file,Encoding.UTF8));
                if (candidate == null || candidate.runId != nonce || Path.GetFileName(file) != "config.json" || Path.GetFileName(Path.GetDirectoryName(file)) != "run-"+nonce) return Result.Succeeded;
                candidate.modelPath=Child(candidate.modelPath);candidate.outputDirectory=Child(candidate.outputDirectory);
                if (!SamePath(candidate.outputDirectory,Path.Combine(Path.GetDirectoryName(file),"output")) || Path.GetFileName(candidate.modelPath)!="acceptance-model.rvt" || !File.Exists(candidate.modelPath)) return Result.Succeeded;
                if (!Sha(candidate.expectedDriverSha256) || !Sha(candidate.expectedModelSha256) || !Sha(candidate.expectedExporterSha256) || !Sha(candidate.productionManifestSha256) || !Sha(candidate.expectedRevitApiSha256) || !Sha(candidate.expectedRevitApiUiSha256) || !File.Exists(candidate.exporterPath) || !File.Exists(candidate.productionManifestPath)) return Result.Succeeded;
                if (Hash(Assembly.GetExecutingAssembly().Location)!=candidate.expectedDriverSha256 || Hash(candidate.productionManifestPath)!=candidate.productionManifestSha256 || candidate.expectedPackageVersion!="1.9.0" || candidate.productionSourceCount<1) return Result.Succeeded;
                config=candidate;host=application;
                Environment.SetEnvironmentVariable(ConfigurationEnvironment,null);Environment.SetEnvironmentVariable(NonceEnvironment,null);
                host.Idling+=OnIdling;Status("waiting-for-exact-dedicated-model");
            }
            catch { if(host!=null)host.Idling-=OnIdling;host=null;config=null; }
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication application){if(host!=null)host.Idling-=OnIdling;return Result.Succeeded;}
        private void OnIdling(object sender,IdlingEventArgs args)
        {
            var application=sender as UIApplication;
            if(application==null||application.ActiveUIDocument==null||config==null||!SamePath(application.ActiveUIDocument.Document.PathName,config.modelPath))return;
            host.Idling-=OnIdling;Run(application);
        }

        private void Run(UIApplication application)
        {
            UIDocument ui=application.ActiveUIDocument;Document document=null;TransactionGroup group=null;bool rolledBack=false,trackingAttached=false;
            ICollection<ElementId> selection=null;string activeView=null,modelHash=null;int elementCount=0;bool modified=false;Type nativeReader=null;
            var created=new List<string>();var cases=new List<Dictionary<string,object>>();var familyAttempts=new List<Dictionary<string,object>>();var blocked=new List<Dictionary<string,object>>();
            Stopwatch elapsed=Stopwatch.StartNew();
            var report=Fields("schemaVersion",1,"result","FAIL","runId",config.runId,"processId",Process.GetCurrentProcess().Id,
                "revitYear",application.Application.VersionNumber,"revitBuild",application.Application.VersionBuild,"expectedPackageVersion",config.expectedPackageVersion,
                "productionSourceCount",config.productionSourceCount,"productionManifestPath",config.productionManifestPath,"productionManifestSha256",config.productionManifestSha256,
                "sourceSaved",false,"activeViewWritten",false,"fixtureRollbackRequired",true,"ribbonClickVerified",false,"nativeWindowVerified",false,
                "driver",Fields("path",Assembly.GetExecutingAssembly().Location));
            try
            {
                report["driver"]=Fields("path",Assembly.GetExecutingAssembly().Location,"sha256",Hash(Assembly.GetExecutingAssembly().Location));
                Require(application.Application.VersionNumber=="2020","This independent SDK driver only targets Revit 2020.");
                Assembly actualApi=typeof(Document).Assembly,actualUiApi=typeof(UIApplication).Assembly;
                Require(SamePath(actualApi.Location,config.expectedRevitApiPath)&&Hash(actualApi.Location)==config.expectedRevitApiSha256,"Actual RevitAPI path/hash differs from the exact driver reference host.");
                Require(SamePath(actualUiApi.Location,config.expectedRevitApiUiPath)&&Hash(actualUiApi.Location)==config.expectedRevitApiUiSha256,"Actual RevitAPIUI path/hash differs from the exact driver reference host.");
                report["hostApi"]=Fields("RevitAPI",Fields("path",actualApi.Location,"sha256",Hash(actualApi.Location)),"RevitAPIUI",Fields("path",actualUiApi.Location,"sha256",Hash(actualUiApi.Location)));
                Require(ui!=null&&SamePath(ui.Document.PathName,config.modelPath),"Only the exact prepared model copy may be used.");document=ui.Document;
                foreach(Document open in application.Application.Documents)Require(open.IsLinked||open.Equals(document),"Another project/family is open; disposable fixture refused.");
                Require(document.IsValidObject&&!document.IsFamilyDocument,"Prepared document is not a valid project.");
                modelHash=Hash(config.modelPath);Require(modelHash==config.expectedModelSha256,"Prepared model bytes changed.");
                Require(Hash(config.exporterPath)==config.expectedExporterSha256&&Hash(config.productionManifestPath)==config.productionManifestSha256,"Prepared production identity changed.");
                // LoadFile avoids binding this test to an installed older same-
                // named assembly. Never run the production App/Ribbon startup.
                Assembly production=Assembly.LoadFile(Path.GetFullPath(config.exporterPath));
                Require(SamePath(production.Location,config.exporterPath)&&production.GetName().Version.ToString()==config.expectedAssemblyVersion&&Hash(production.Location)==config.expectedExporterSha256,"Isolated production assembly identity does not match the new manifest payload.");
                report["plugin"]=Fields("path",production.Location,"version",production.GetName().Version.ToString(),"sha256",Hash(production.Location),"loadMethod","Assembly.LoadFile");
                nativeReader=production.GetType("BrightVision.Revit.RevitCivilQuantityReader",true);Call(nativeReader,"Attach",host);trackingAttached=true;
                report["revisionTrackingAttached"]=true;
                Type wallReader=production.GetType("BrightVision.Revit.RevitCivilWallReader",true),calculator=production.GetType("BrightVision.Civil.CivilWallCalculator",true),rules=production.GetType("BrightVision.Civil.CivilWallRuleTemplates",true),reportType=production.GetType("BrightVision.Civil.CivilWallReport",true);
                object defaultRule=Call(rules,"Default"),legacyRule=Call(rules,"LegacyExplicitOpeningOnly");
                Require((string)Get(defaultRule,"OpeningPolicy")=="verified-rect-cutout"&&(string)Get(legacyRule,"OpeningPolicy")=="native-opening-only","Expected the explicit default and legacy policy contracts.");
                selection=ui.Selection.GetElementIds();activeView=Id(ui.ActiveView.Id);elementCount=Count(document);modified=document.IsModified;
                report["source"]=Fields("path",document.PathName,"sha256Before",modelHash,"elementCountBefore",elementCount,"isModifiedBefore",modified);
                Phase phase=document.Phases.Cast<Phase>().Last();Level level=new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().First();
                WallType prototype=new FilteredElementCollector(document).OfClass(typeof(WallType)).Cast<WallType>().First(type=>type.Kind==WallKind.Basic);
                ViewFamilyType viewType=new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(type=>type.ViewFamily==ViewFamily.ThreeDimensional);
                WallType fixtureType=null;View3D view=null;Wall wall=null;
                group=new TransactionGroup(document,"BV 1.9 disposable SDK acceptance "+config.runId);Require(group.Start()==TransactionStatus.Started,"Could not start required rollback group.");
                Mutate(document,"BV independent small wall fixture",delegate{
                    fixtureType=(WallType)prototype.Duplicate("BV 1.9 fixture 200mm "+config.runId);
                    fixtureType.SetCompoundStructure(CompoundStructure.CreateSimpleCompoundStructure(new List<CompoundStructureLayer>{new CompoundStructureLayer(.2*FeetPerMetre,MaterialFunctionAssignment.Structure,ElementId.InvalidElementId)}));
                    view=View3D.CreateIsometric(document,viewType.Id);view.Name="BV 1.9 fixture geometry "+config.runId;view.DetailLevel=ViewDetailLevel.Fine;view.IsSectionBoxActive=false;view.get_Parameter(BuiltInParameter.VIEW_PHASE).Set(phase.Id);
                    wall=CreateWall(document,fixtureType,level,phase,5000);created.Add(fixtureType.UniqueId);created.Add(view.UniqueId);created.Add(wall.UniqueId);
                });
                Near(fixtureType.Width*.3048,.2,"Independently constructed uniform wall thickness.");
                CheckCase("default-no-opening",ui,wall,phase,view,nativeReader,wallReader,calculator,rules,reportType,defaultRule,true,6,0,cases);
                CheckCase("legacy-no-opening",ui,wall,phase,view,nativeReader,wallReader,calculator,rules,reportType,legacyRule,true,6,0,cases);
                Mutate(document,"BV actual native rectangular Opening",delegate{Opening opening=document.Create.NewOpening(wall,new XYZ(5000+2*FeetPerMetre,5000,level.ProjectElevation+FeetPerMetre),new XYZ(5000+4*FeetPerMetre,5000,level.ProjectElevation+2*FeetPerMetre));created.Add(opening.UniqueId);});
                CheckCase("default-native-opening",ui,wall,phase,view,nativeReader,wallReader,calculator,rules,reportType,defaultRule,true,5.6,2,cases);
                CheckCase("legacy-native-opening",ui,wall,phase,view,nativeReader,wallReader,calculator,rules,reportType,legacyRule,true,5.6,2,cases);

                int location=0;
                foreach(BuiltInCategory category in new[]{BuiltInCategory.OST_Doors,BuiltInCategory.OST_Windows})
                {
                    string name=category==BuiltInCategory.OST_Doors?"door":"window";bool accepted=false;
                    List<FamilySymbol> symbols=new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                        .Where(symbol=>symbol.Category!=null&&symbol.Category.Id.IntegerValue==(int)category&&symbol.Family.FamilyPlacementType==FamilyPlacementType.OneLevelBasedHosted).OrderBy(symbol=>symbol.Id.IntegerValue).Take(8).ToList();
                    foreach(FamilySymbol symbol in symbols)
                    {
                        Wall hostWall=null;FamilyInstance instance=null;location++;
                        try
                        {
                            double y=5300+location*100;
                            Mutate(document,"BV actual hosted "+name+" trial",delegate{
                                hostWall=CreateWall(document,fixtureType,level,phase,y);if(!symbol.IsActive)symbol.Activate();document.Regenerate();
                                instance=document.Create.NewFamilyInstance(new XYZ(5000+5*FeetPerMetre,y,level.ProjectElevation+(category==BuiltInCategory.OST_Windows?FeetPerMetre:0)),symbol,hostWall,level,StructuralType.NonStructural);
                                instance.CreatedPhaseId=phase.Id;
                            });
                            created.Add(hostWall.UniqueId);created.Add(instance.UniqueId);
                            object trial=Read(ui,hostWall,phase,view,nativeReader,wallReader,defaultRule);object fact=((IList)Get(trial,"Walls"))[0];
                            if(!(bool)Get(fact,"GeometryVerified"))
                            {
                                familyAttempts.Add(Fields("category",name,"symbolElementId",Id(symbol.Id),"symbolUniqueId",symbol.UniqueId,"instanceUniqueId",instance.UniqueId,"result","blocked","issues",Get(fact,"Issues"),"inserts",Get(fact,"Inserts")));continue;
                            }
                            IList inserts=(IList)Get(fact,"Inserts");
                            Require(inserts.Cast<object>().Any(insert=>(string)Get(insert,"UniqueId")==instance.UniqueId&&(string)Get(insert,"HostUniqueId")==hostWall.UniqueId&&(string)Get(insert,"Kind")=="rect-opening"&&(string)Get(insert,"Status")=="recognized"&&(string)Get(insert,"CutoutMethod")=="family-rect-cutout"&&((string)Get(insert,"Source")).Contains("GetInstanceCutoutFromWall")),"Accepted family does not retain exact host/category/method identity and actual official IFC cutout provenance.");
                            double actualVolume=RawVolume(hostWall,view);Require(actualVolume>0&&actualVolume<6,"Hosted family must make a real positive-volume cut in the independent wall solid.");
                            double openingArea=(6-actualVolume)/.2;
                            CheckCase("default-actual-"+name,ui,hostWall,phase,view,nativeReader,wallReader,calculator,rules,reportType,defaultRule,true,actualVolume,openingArea,cases);
                            CheckCase("legacy-refuses-actual-"+name,ui,hostWall,phase,view,nativeReader,wallReader,calculator,rules,reportType,legacyRule,false,0,0,cases);
                            familyAttempts.Add(Fields("category",name,"symbolElementId",Id(symbol.Id),"symbolUniqueId",symbol.UniqueId,"instanceUniqueId",instance.UniqueId,"hostUniqueId",hostWall.UniqueId,"result","PASS","independentActualSolidMetres3",actualVolume,"expectedSource","Independent raw native wall Solid.Volume, uniform 0.2m wall and verified full-thickness rectangular cut; no nominal family dimensions read"));
                            accepted=true;break;
                        }
                        catch(FixtureUnavailable error){familyAttempts.Add(Fields("category",name,"symbolUniqueId",symbol.UniqueId,"result","blocked","reason",error.Message));}
                        catch(Autodesk.Revit.Exceptions.ArgumentException error){familyAttempts.Add(Fields("category",name,"symbolUniqueId",symbol.UniqueId,"result","blocked","reason",error.Message));}
                        catch(Autodesk.Revit.Exceptions.InvalidOperationException error){familyAttempts.Add(Fields("category",name,"symbolUniqueId",symbol.UniqueId,"result","blocked","reason",error.Message));}
                    }
                    if(!accepted)blocked.Add(Fields("case","actual-"+name+"-cutout","reason",symbols.Count==0?"Dedicated official sample contains no loaded one-level hosted family of this category; no guessed or downloaded family used.":"None of at most eight loaded native symbols produced a supported actual rectangular cutout; see attempts. No nominal dimensions substituted."));
                }
                report["cases"]=cases;report["familyAttempts"]=familyAttempts;report["blockedCases"]=blocked;
                Require(Id(ui.ActiveView.Id)==activeView,"Geometry view must not be activated.");
                Require(group.RollBack()==TransactionStatus.RolledBack,"Required fixture rollback failed.");rolledBack=true;
                Require(created.All(uid=>document.GetElement(uid)==null),"Created fixture object survived rollback.");Require(Count(document)==elementCount,"Element count changed after rollback.");
                report["fixtureCreatedUniqueIds"]=created;report["fixtureRollbackVerified"]=true;
                report["result"]=blocked.Count==0?"PASS":"PARTIAL";
            }
            catch(Exception error){report["error"]=Unwrap(error).ToString();}
            finally
            {
                if(group!=null){if(!rolledBack)try{rolledBack=group.RollBack()==TransactionStatus.RolledBack;}catch(Exception error){report["rollbackError"]=error.ToString();report["result"]="FAIL";}try{group.Dispose();}catch(Exception error){report["disposeError"]=error.ToString();report["result"]="FAIL";}}
                report["fixtureRolledBack"]=rolledBack;
                if(ui!=null&&selection!=null)try{ui.Selection.SetElementIds(selection);report["selectionRestored"]=new HashSet<string>(ui.Selection.GetElementIds().Select(Id)).SetEquals(selection.Select(Id));}catch(Exception error){report["selectionRestoreError"]=error.ToString();report["result"]="FAIL";}
                if(document!=null&&document.IsValidObject)try{report["activeViewUnchanged"]=Id(ui.ActiveView.Id)==activeView;report["elementCountAfter"]=Count(document);report["isModifiedAfter"]=document.IsModified;}catch(Exception error){report["finalDocumentError"]=error.ToString();report["result"]="FAIL";}
                if(modelHash!=null)try{report["sourceSha256After"]=Hash(config.modelPath);report["sourceHashUnchanged"]=(string)report["sourceSha256After"]==modelHash;}catch(Exception error){report["sourceHashError"]=error.ToString();report["result"]="FAIL";}
                if(!String.IsNullOrEmpty(config.originalModelPath))try{report["originalSourceHashUnchanged"]=Hash(config.originalModelPath)==config.expectedOriginalModelSha256;}catch(Exception error){report["originalSourceHashError"]=error.ToString();report["result"]="FAIL";}
                if(trackingAttached&&nativeReader!=null)try{Call(nativeReader,"Detach",host);report["revisionTrackingDetached"]=true;}catch(Exception error){report["trackingDetachError"]=Unwrap(error).ToString();report["result"]="FAIL";}
                if(!rolledBack||False(report,"activeViewUnchanged")||False(report,"selectionRestored")||False(report,"sourceHashUnchanged")||False(report,"originalSourceHashUnchanged")||report.ContainsKey("elementCountAfter")&&(int)report["elementCountAfter"]!=elementCount)report["result"]="FAIL";
                string[] required={"default-no-opening","legacy-no-opening","default-native-opening","legacy-native-opening","default-actual-door","legacy-refuses-actual-door","default-actual-window","legacy-refuses-actual-window"};
                bool allCasesPassed=required.All(name=>cases.Any(item=>(string)item["name"]==name&&(string)item["result"]=="PASS"));
                report["cases"]=cases;report["familyAttempts"]=familyAttempts;report["blockedCases"]=blocked;report["requiredCases"]=required;report["requiredCasesPassed"]=allCasesPassed;
                report["liveRevit19WallSdkVerified"]=(string)report["result"]=="PASS"&&allCasesPassed&&rolledBack;
                report["assertions"]=checks;report["elapsedSeconds"]=elapsed.Elapsed.TotalSeconds;report["finishedUtc"]=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture);report["fixtureWarnings"]=warnings;
                try{report["artifacts"]=Directory.GetFiles(config.outputDirectory).Where(path=>!Path.GetFileName(path).StartsWith("civil-wall-phase3-host-",StringComparison.Ordinal)).Select(path=>Fields("name",Path.GetFileName(path),"bytes",new FileInfo(path).Length,"sha256",Hash(path))).ToList();Save("civil-wall-phase3-host-report.json",report);Status((string)report["result"]=="PASS"?"completed":(string)report["result"]=="PARTIAL"?"completed-with-blocked-cases":"failed");}
                catch(Exception error){try{application.Application.WriteJournalComment("BV 1.9 SDK evidence write failed: "+error,false);}catch{}}
            }
        }

        private void CheckCase(string name,UIDocument ui,Wall wall,Phase phase,View3D view,Type nativeReader,Type wallReader,Type calculator,Type rules,Type reportType,object rule,bool accepted,double expectedVolume,double expectedOpeningArea,IList<Dictionary<string,object>> cases)
        {
            Status("reading-"+name);ui.Selection.SetElementIds(new List<ElementId>{wall.Id});
            object request=Call(nativeReader,"DefaultRequest",ui);Set(request,"Scope","selection");Set(request,"PhaseId",Id(phase.Id));Set(request,"DesignOptionId","-1");
            object native=Call(nativeReader,"Read",ui,request);string before=json.Serialize(native);SaveText(name+"-native-before.json",before);
            object facts=Call(wallReader,"Read",ui,native,request,view,rule,null);string afterReader=json.Serialize(native);SaveText(name+"-native-after-reader.json",afterReader);
            Require(before==afterReader&&CivilSnapshotComparison.CompareJson(json,before,afterReader)==null,"Reader changed native source snapshot.");
            IList factRows=(IList)Get(facts,"Walls");Require(factRows.Count==1&&(string)Get(factRows[0],"UniqueId")==wall.UniqueId,"Selected native wall identity lost or duplicated.");
            object fact=factRows[0],batch=Call(calculator,"Calculate",facts),data=Call(reportType,"Create",batch,null);IList results=(IList)Get(batch,"Results");Require(results.Count==1,"Calculation coverage changed.");object result=results[0];
            string afterCalculation=json.Serialize(native);SaveText(name+"-native-after-calculation.json",afterCalculation);
            Require(before==afterCalculation&&CivilSnapshotComparison.CompareJson(json,before,afterCalculation)==null,"Calculation/report changed native source snapshot.");
            string fingerprint=(string)Call(rules,"Fingerprint",rule);Require((string)Get(batch,"RuleFingerprint")==fingerprint&&((string)Get(batch,"BatchId")).Length==32,"Completed batch does not bind the exact rule fingerprint and batch identity.");
            string csv=(string)Call(reportType,"ToCsv",data,Get(data,"Rows"),"type");SaveText(name+"-report2.csv",csv);
            Require(csv.Contains("BV.Wall.Report/2")&&csv.Contains(fingerprint)&&csv.Contains((string)Get(rule,"OpeningPolicy"))&&csv.Contains((string)Get(rule,"TemplateId")),"Report/2 omitted exact rule metadata.");
            Require(before==json.Serialize(native),"CSV/report export mutated native facts.");
            if(accepted)
            {
                Require((bool)Get(fact,"GeometryVerified")&&(string)Get(result,"Status")=="calculated","Expected actual wall/solid proof failed: "+json.Serialize(Get(fact,"Issues")));
                Near(Number(Get(result,"BasisVolumeMetres3")),6,"Independent rectangular basis.");Near(Number(Get(result,"OpeningUnionAreaMetres2")),expectedOpeningArea,"Independent opening area.");Near(Number(Get(result,"ResultVolumeMetres3")),expectedVolume,"Expected actual solid volume.");Near(Number(Get(result,"ResultAreaMetres2")),30-expectedOpeningArea,"Expected single-sided projection area.");
                Near(((IList)Get(result,"Ledger")).Cast<object>().Sum(entry=>Number(Get(entry,"DeltaVolumeMetres3"))),expectedVolume,"Volume ledger conservation.");
                Near(((IList)Get(result,"Ledger")).Cast<object>().Sum(entry=>Number(Get(entry,"DeltaAreaMetres2"))),30-expectedOpeningArea,"Area ledger conservation.");
            }
            else Require((string)Get(result,"Status")!="calculated"&&Get(result,"ResultVolumeMetres3")==null&&Get(result,"ResultAreaMetres2")==null&&((IList)Get(result,"Ledger")).Count==0,"Legacy policy emitted quantities for an excluded actual family cutout.");
            Save(name+"-facts.json",facts);Save(name+"-calculation.json",batch);
            Save(name+"-native-comparison.json",Fields("result","PASS","method","exact-structural-json-v1","rawSerializedBytesEqual",true,"readerDifferencePath",null,"calculationDifferencePath",null,"beforeFile",name+"-native-before.json","afterReaderFile",name+"-native-after-reader.json","afterCalculationFile",name+"-native-after-calculation.json"));
            cases.Add(Fields("name",name,"result","PASS","expectedCalculated",accepted,"wallUniqueId",wall.UniqueId,"ruleTemplateId",Get(rule,"TemplateId"),"ruleFingerprint",fingerprint,"batchId",Get(batch,"BatchId"),"openingPolicy",Get(rule,"OpeningPolicy"),"nativeSnapshotUnchanged",true,"nativeRawBytesUnchanged",true,"actualResultVolumeMetres3",Get(result,"ResultVolumeMetres3"),"actualResultAreaMetres2",Get(result,"ResultAreaMetres2")));
        }
        private static object Read(UIDocument ui,Wall wall,Phase phase,View view,Type nativeReader,Type wallReader,object rule)
        {ui.Selection.SetElementIds(new List<ElementId>{wall.Id});object request=Call(nativeReader,"DefaultRequest",ui);Set(request,"Scope","selection");Set(request,"PhaseId",Id(phase.Id));Set(request,"DesignOptionId","-1");object native=Call(nativeReader,"Read",ui,request);return Call(wallReader,"Read",ui,native,request,view,rule,null);}
        private static Wall CreateWall(Document document,WallType type,Level level,Phase phase,double y)
        {Wall wall=Wall.Create(document,Line.CreateBound(new XYZ(5000,y,level.ProjectElevation),new XYZ(5000+10*FeetPerMetre,y,level.ProjectElevation)),type.Id,level.Id,3*FeetPerMetre,0,false,false);wall.CreatedPhaseId=phase.Id;WallUtils.DisallowWallJoinAtEnd(wall,0);WallUtils.DisallowWallJoinAtEnd(wall,1);return wall;}
        private static double RawVolume(Wall wall,View view)
        {using(var options=new Options()){options.View=view;using(GeometryElement geometry=wall.get_Geometry(options)){List<Solid> solids=geometry.OfType<Solid>().Where(solid=>solid.Faces.Size>0&&solid.Volume>1e-12).ToList();if(solids.Count!=1)throw new FixtureUnavailable("Independent wall volume fixture requires exactly one positive native solid.");return solids[0].Volume*Math.Pow(.3048,3);}}}
        private void Mutate(Document document,string name,Action action)
        {using(var transaction=new Transaction(document,name)){transaction.Start();transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new Failures(warnings)).SetClearAfterRollback(true));try{action();if(transaction.Commit()!=TransactionStatus.Committed)throw new FixtureUnavailable("Fixture transaction rolled back: "+String.Join("; ",warnings));}catch{if(transaction.GetStatus()==TransactionStatus.Started)transaction.RollBack();throw;}}}
        private sealed class Failures:IFailuresPreprocessor
        {private readonly IList<string> messages;internal Failures(IList<string> messages){this.messages=messages;}public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor){foreach(FailureMessageAccessor failure in accessor.GetFailureMessages()){messages.Add(failure.GetDescriptionText());if(failure.GetSeverity()!=FailureSeverity.Warning)return FailureProcessingResult.ProceedWithRollBack;accessor.DeleteWarning(failure);}return FailureProcessingResult.Continue;}}
        private sealed class FixtureUnavailable:Exception{internal FixtureUnavailable(string text):base(text){}}
        private static int Count(Document document){using(var collector=new FilteredElementCollector(document))return collector.WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(false),new ElementIsElementTypeFilter(true))).ToElementIds().Count;}
        private static object Get(object value,string name){return value.GetType().GetProperty(name).GetValue(value,null);}
        private static void Set(object value,string name,object data){value.GetType().GetProperty(name).SetValue(value,data,null);}
        private static object Call(Type type,string name,params object[] arguments){try{return type.GetMethods(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Single(method=>method.Name==name&&method.GetParameters().Length==arguments.Length).Invoke(null,arguments);}catch(TargetInvocationException error){throw Unwrap(error);}}
        private static Exception Unwrap(Exception error){while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;return error;}
        private static double Number(object value){return Convert.ToDouble(value,CultureInfo.InvariantCulture);}
        private void Near(double actual,double expected,string label){Require(!Double.IsNaN(actual)&&!Double.IsInfinity(actual)&&Math.Abs(actual-expected)<=Math.Max(1e-7,Math.Abs(expected)*1e-7),label);}
        private void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);checks++;}
        private static string Id(ElementId id){return id==null?"-1":id.IntegerValue.ToString(CultureInfo.InvariantCulture);}
        private static bool False(IDictionary<string,object> values,string key){return values.ContainsKey(key)&&values[key] is bool&&!(bool)values[key];}
        private static bool SamePath(string left,string right){return !String.IsNullOrEmpty(left)&&!String.IsNullOrEmpty(right)&&String.Equals(Path.GetFullPath(left),Path.GetFullPath(right),StringComparison.OrdinalIgnoreCase);}
        private static bool Sha(string value){return value!=null&&value.Length==64&&value.All(character=>"0123456789abcdef".IndexOf(character)>=0);}
        private static string Hash(string file){using(SHA256 hash=SHA256.Create())using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        private string Child(string file){string path=Path.GetFullPath(file),root=Path.GetFullPath(boundary).TrimEnd('\\')+"\\";if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("SDK evidence path leaves the new 1.9 boundary.");for(string cursor=path;cursor!=null&&cursor.Length>=root.Length-1;cursor=Path.GetDirectoryName(cursor))if((File.Exists(cursor)||Directory.Exists(cursor))&&(File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("SDK evidence path is a reparse point.");return path;}
        private static Dictionary<string,object> Fields(params object[] values){var result=new Dictionary<string,object>();for(int index=0;index<values.Length;index+=2)result.Add((string)values[index],values[index+1]);return result;}
        private void Save(string name,object value){SaveText(name,json.Serialize(value));}
        private void SaveText(string name,string text){Directory.CreateDirectory(config.outputDirectory);if(Encoding.UTF8.GetByteCount(text)>16777216)throw new InvalidDataException("SDK evidence exceeds 16 MiB.");File.WriteAllText(Child(Path.Combine(config.outputDirectory,name)),text,new UTF8Encoding(name.EndsWith(".csv",StringComparison.Ordinal)));}
        private void Status(string phase){Save("civil-wall-phase3-host-status.json",Fields("phase",phase,"runId",config.runId,"utc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)));}
        private sealed class Config
        {
            public string runId{get;set;}public string modelPath{get;set;}public string outputDirectory{get;set;}
            public string originalModelPath{get;set;}public string expectedOriginalModelSha256{get;set;}
            public string exporterPath{get;set;}public string expectedExporterSha256{get;set;}public string expectedAssemblyVersion{get;set;}
            public string expectedModelSha256{get;set;}public string expectedDriverSha256{get;set;}
            public string productionManifestPath{get;set;}public string productionManifestSha256{get;set;}public string expectedPackageVersion{get;set;}public int productionSourceCount{get;set;}
            public string expectedRevitApiPath{get;set;}public string expectedRevitApiSha256{get;set;}public string expectedRevitApiUiPath{get;set;}public string expectedRevitApiUiSha256{get;set;}
        }
    }
}
