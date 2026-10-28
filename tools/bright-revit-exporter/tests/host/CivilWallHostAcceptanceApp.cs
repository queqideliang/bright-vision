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

[assembly: AssemblyVersion("1.0.0.0")]
namespace BrightVision.CivilWallHostAcceptance
{
    // Explicit one-run SDK fixture on an exact dedicated sample copy. Geometry
    // and view/type/wall changes exist only inside a required rollback group.
    public sealed class App : IExternalApplication
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16777216 };
        private readonly List<string> fixtureFailures = new List<string>();
        private Config config;
        private UIControlledApplication host;
        private string boundary;
        private int checks;
        private const double FeetPerMetre = 1.0 / 0.3048;

        public Result OnStartup(UIControlledApplication application)
        {
            string file = Environment.GetEnvironmentVariable("BV_CIVIL_WALL_ACCEPTANCE_CONFIG");
            string nonce = Environment.GetEnvironmentVariable("BV_CIVIL_WALL_ACCEPTANCE_RUN_ID");
            Guid guid;
            if (String.IsNullOrEmpty(file) || !Guid.TryParseExact(nonce, "N", out guid) || guid == Guid.Empty) return Result.Succeeded;
            try
            {
                DirectoryInfo cursor = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory;
                while (cursor != null && !File.Exists(Path.Combine(cursor.FullName, "tools", "bright-revit-exporter", "CivilWallFacts.cs"))) cursor = cursor.Parent;
                if (cursor == null) return Result.Succeeded;
                boundary = Path.Combine(cursor.FullName, "test", "revit-civil-acceptance", "phase3-20261028");
                file = Child(file);
                Config candidate = json.Deserialize<Config>(File.ReadAllText(file, Encoding.UTF8));
                if (candidate == null || candidate.runId != nonce || Path.GetFileName(file) != "config.json" ||
                    Path.GetFileName(Path.GetDirectoryName(file)) != "run-" + nonce) return Result.Succeeded;
                candidate.modelPath = Child(candidate.modelPath); candidate.outputDirectory = Child(candidate.outputDirectory);
                if (!SamePath(candidate.outputDirectory, Path.Combine(Path.GetDirectoryName(file), "output")) ||
                    Path.GetFileName(candidate.modelPath) != "rst_basic_sample_project.rvt" || !File.Exists(candidate.modelPath) || !File.Exists(candidate.exporterPath)) return Result.Succeeded;
                if (!Sha(candidate.expectedDriverSha256) || !Sha(candidate.expectedModelSha256) || !Sha(candidate.expectedExporterSha256) ||
                    Hash(Assembly.GetExecutingAssembly().Location) != candidate.expectedDriverSha256.ToLowerInvariant()) return Result.Succeeded;
                config = candidate; host = application;
                Environment.SetEnvironmentVariable("BV_CIVIL_WALL_ACCEPTANCE_CONFIG", null);
                Environment.SetEnvironmentVariable("BV_CIVIL_WALL_ACCEPTANCE_RUN_ID", null);
                host.Idling += OnIdling; Status("waiting-for-dedicated-model");
            }
            catch { config = null; }
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication application) { if (host != null) host.Idling -= OnIdling; return Result.Succeeded; }
        private void OnIdling(object sender, IdlingEventArgs args)
        {
            UIApplication application = sender as UIApplication;
            if (application == null || application.ActiveUIDocument == null || config == null) return;
            host.Idling -= OnIdling; Run(application);
        }

        private void Run(UIApplication application)
        {
            Document document = null; UIDocument ui = application.ActiveUIDocument;
            ICollection<ElementId> selection = null;
            TransactionGroup group = null;
            bool rolledBack = false;
            string originalView = null;
            string modelHash = null;
            int elementCount = 0;
            bool modified = false;
            List<string> createdUniqueIds = new List<string>();
            Stopwatch timer = Stopwatch.StartNew();
            Dictionary<string, object> report = Fields("result", "FAIL", "runId", config.runId, "processId", Process.GetCurrentProcess().Id,
                "revitYear", application.Application.VersionNumber, "revitBuild", application.Application.VersionBuild,
                "sourceSaved", false, "activeViewWritten", false, "fixtureRollbackRequired", true,
                "ribbonClickVerified", false, "nativeWallWindowVerified", false,
                "driver", Fields("path", Assembly.GetExecutingAssembly().Location, "sha256", Hash(Assembly.GetExecutingAssembly().Location)));
            try
            {
                Require(application.Application.VersionNumber == "2020", "This test driver targets Revit 2020 only.");
                Require(ui != null && SamePath(ui.Document.PathName, config.modelPath), "Refusing to modify any document except the exact dedicated sample copy.");
                document = ui.Document;
                foreach (Document open in application.Application.Documents) Require(open.IsLinked || open.Equals(document), "Another user project is open; fixture refused.");
                Require(!document.IsFamilyDocument && document.IsValidObject, "Dedicated project is invalid.");
                modelHash = Hash(config.modelPath); Require(modelHash == config.expectedModelSha256.ToLowerInvariant(), "Dedicated model hash mismatch.");
                Require(Hash(config.exporterPath) == config.expectedExporterSha256.ToLowerInvariant(), "Installed exporter hash mismatch.");
                Assembly production = Assembly.LoadFrom(config.exporterPath);
                Require(production.GetName().Version == new Version(1, 7, 0, 0) && SamePath(production.Location, config.exporterPath), "Expected exactly installed BV 1.7.0.0.");
                report["plugin"] = Fields("path", production.Location, "version", production.GetName().Version.ToString(), "sha256", Hash(production.Location));
                selection = ui.Selection.GetElementIds(); originalView = Id(ui.ActiveView.Id); elementCount = Count(document); modified = document.IsModified;
                report["source"] = Fields("path", document.PathName, "sha256Before", modelHash, "bytes", new FileInfo(config.modelPath).Length, "isModifiedBefore", modified, "elementCountBefore", elementCount);
                Type nativeReader = production.GetType("BrightVision.Revit.RevitCivilQuantityReader", true);
                Type wallReader = production.GetType("BrightVision.Revit.RevitCivilWallReader", true);
                Type calculator = production.GetType("BrightVision.Civil.CivilWallCalculator", true);
                Phase phase = document.Phases.Cast<Phase>().Last();
                Level level = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().First();
                WallType prototype = new FilteredElementCollector(document).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Basic).OrderBy(t => t.Name.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) < 0 ? 1 : 0).First();
                ViewFamilyType viewFamily = new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                Wall wall = null; WallType fixtureType = null; View3D geometryView = null;
                group = new TransactionGroup(document, "BV disposable wall acceptance " + config.runId); group.Start();
                Status("creating-rollback-fixture");
                Mutate(document, "BV fixture basic wall and explicit geometry view", delegate {
                    fixtureType = (WallType)prototype.Duplicate("BV fixture 200mm " + config.runId);
                    CompoundStructure compound = CompoundStructure.CreateSimpleCompoundStructure(new List<CompoundStructureLayer> {
                        new CompoundStructureLayer(0.2 * FeetPerMetre, MaterialFunctionAssignment.Structure, ElementId.InvalidElementId)
                    });
                    fixtureType.SetCompoundStructure(compound);
                    geometryView = View3D.CreateIsometric(document, viewFamily.Id); geometryView.Name = "BV fixture geometry " + config.runId;
                    geometryView.IsSectionBoxActive = false; geometryView.DetailLevel = ViewDetailLevel.Fine;
                    geometryView.get_Parameter(BuiltInParameter.VIEW_PHASE).Set(phase.Id);
                    XYZ a = new XYZ(5000, 5000, level.ProjectElevation), b = new XYZ(5000 + 10 * FeetPerMetre, 5000, level.ProjectElevation);
                    wall = Wall.Create(document, Line.CreateBound(a, b), fixtureType.Id, level.Id, 3 * FeetPerMetre, 0, false, false);
                    wall.CreatedPhaseId = phase.Id;
                    WallUtils.DisallowWallJoinAtEnd(wall, 0); WallUtils.DisallowWallJoinAtEnd(wall, 1);
                });
                createdUniqueIds.Add(wall.UniqueId); createdUniqueIds.Add(fixtureType.UniqueId); createdUniqueIds.Add(geometryView.UniqueId);
                Require(Math.Abs(fixtureType.Width * 0.3048 - 0.2) < 1e-10, "Fixture type must be independently defined as 0.2m.");
                Require(geometryView.get_Parameter(BuiltInParameter.VIEW_PHASE).AsElementId().Equals(phase.Id), "Fixture geometry view phase mismatch.");
                List<Dictionary<string, object>> cases = new List<Dictionary<string, object>>();
                object noHoleFact = CheckCase("no-opening", ui, wall, phase, geometryView, nativeReader, wallReader, calculator, 6, 0, cases);
                Opening first = null;
                Mutate(document, "BV fixture rectangular native opening", delegate {
                    first = NewOpening(document, wall, level.ProjectElevation, 2, 4, 1, 2); createdUniqueIds.Add(first.UniqueId);
                });
                CheckCase("one-opening", ui, wall, phase, geometryView, nativeReader, wallReader, calculator, 5.6, 2, cases);
                Opening second = null;
                report["overlappingNativeOpeningCreated"] = false;
                try
                {
                    Mutate(document, "BV fixture overlapping native opening", delegate { second = NewOpening(document, wall, level.ProjectElevation, 3, 5, 1, 2); });
                    createdUniqueIds.Add(second.UniqueId);
                    report["overlappingNativeOpeningCreated"] = true;
                }
                catch (FixtureCreationUnavailable error)
                {
                    report["overlappingNativeOpeningCreated"] = false; report["overlapCreationLimitation"] = error.Message;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException error)
                {
                    report["overlappingNativeOpeningCreated"] = false; report["overlapCreationLimitation"] = error.Message;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException error)
                {
                    report["overlappingNativeOpeningCreated"] = false; report["overlapCreationLimitation"] = error.Message;
                }
                if ((bool)report["overlappingNativeOpeningCreated"])
                    CheckCase("overlapping-openings", ui, wall, phase, geometryView, nativeReader, wallReader, calculator, 5.4, 3, cases);
                Wall profileWall = null;
                Mutate(document, "BV fixture nonrectangular wall profile", delegate {
                    double x = 5000, y = 5100, z = level.ProjectElevation;
                    XYZ a = new XYZ(x, y, z), b = new XYZ(x + 10 * FeetPerMetre, y, z);
                    XYZ c = new XYZ(x + 10 * FeetPerMetre, y, z + 2 * FeetPerMetre), d = new XYZ(x, y, z + 3 * FeetPerMetre);
                    profileWall = Wall.Create(document, new List<Curve> { Line.CreateBound(a, b), Line.CreateBound(b, c), Line.CreateBound(c, d), Line.CreateBound(d, a) }, fixtureType.Id, level.Id, false);
                    profileWall.CreatedPhaseId = phase.Id;
                    WallUtils.DisallowWallJoinAtEnd(profileWall, 0); WallUtils.DisallowWallJoinAtEnd(profileWall, 1);
                });
                createdUniqueIds.Add(profileWall.UniqueId);
                report["nonrectangularWallUniqueId"] = profileWall.UniqueId;
                ui.Selection.SetElementIds(new List<ElementId> { profileWall.Id });
                object profileRequest = Call(nativeReader, "DefaultRequest", ui); Set(profileRequest, "Scope", "selection"); Set(profileRequest, "PhaseId", Id(phase.Id)); Set(profileRequest, "DesignOptionId", "-1");
                object profileNative = Call(nativeReader, "Read", ui, profileRequest);
                object profileFacts = Call(wallReader, "Read", ui, profileNative, profileRequest, geometryView);
                IList profileRows = (IList)Get(profileFacts, "Walls"); Require(profileRows.Count == 1 && !(bool)Get(profileRows[0], "GeometryVerified"), "Nonrectangular native wall must not pass rectangle proof.");
                object profileBatch = Call(calculator, "Calculate", profileFacts), profileResult = ((IList)Get(profileBatch, "Results"))[0];
                Require(Get(profileResult, "ResultVolumeMetres3") == null && (string)Get(profileResult, "Status") != "calculated", "Nonrectangular wall emitted a result quantity.");
                Save("nonrect-profile-facts.json", profileFacts); Save("nonrect-profile-calculation.json", profileBatch);
                report["nonrectangularNativeWallRefusalVerified"] = true;
                // An explicitly unjoined physical overlap must be rejected,
                // even though each wall separately has a valid prism proof.
                Wall overlapWall = null;
                Mutate(document, "BV fixture unjoined physical wall overlap", delegate {
                    double y = 5000 + 0.1 * FeetPerMetre;
                    overlapWall = Wall.Create(document, Line.CreateBound(new XYZ(5000, y, level.ProjectElevation), new XYZ(5000 + 10 * FeetPerMetre, y, level.ProjectElevation)), fixtureType.Id, level.Id, 3 * FeetPerMetre, 0, false, false);
                    overlapWall.CreatedPhaseId = phase.Id;
                    WallUtils.DisallowWallJoinAtEnd(overlapWall, 0); WallUtils.DisallowWallJoinAtEnd(overlapWall, 1);
                    if (JoinGeometryUtils.AreElementsJoined(document, wall, overlapWall)) JoinGeometryUtils.UnjoinGeometry(document, wall, overlapWall);
                });
                createdUniqueIds.Add(overlapWall.UniqueId);
                Require(!JoinGeometryUtils.GetJoinedElements(document, wall).Contains(overlapWall.Id), "Overlap fixture must not use an explicit geometry join.");
                Require(!((LocationCurve)wall.Location).get_ElementsAtJoin(0).Cast<Element>().Concat(((LocationCurve)wall.Location).get_ElementsAtJoin(1).Cast<Element>()).Any(e => e.Id.Equals(overlapWall.Id)), "Overlap fixture must not use endpoint joins.");
                ui.Selection.SetElementIds(new List<ElementId> { wall.Id });
                object overlapRequest = Call(nativeReader, "DefaultRequest", ui); Set(overlapRequest, "Scope", "selection"); Set(overlapRequest, "PhaseId", Id(phase.Id)); Set(overlapRequest, "DesignOptionId", "-1");
                object overlapNative = Call(nativeReader, "Read", ui, overlapRequest);
                object overlapFacts = Call(wallReader, "Read", ui, overlapNative, overlapRequest, geometryView);
                object overlappingFact = ((IList)Get(overlapFacts, "Walls"))[0];
                Require((bool)Get(overlappingFact, "InterwallOverlapCheckComplete") && !(bool)Get(overlappingFact, "GeometryVerified"), "Physical overlap was not rejected after a complete independent-wall check.");
                Require(((IList)Get(overlappingFact, "IntersectingWallElementIds")).Cast<string>().Contains(Id(overlapWall.Id)), "Rejected physical overlap did not identify the other native wall.");
                object overlapBatch = Call(calculator, "Calculate", overlapFacts), overlapResult = ((IList)Get(overlapBatch, "Results"))[0];
                Require(Get(overlapResult, "ResultVolumeMetres3") == null && (string)Get(overlapResult, "Status") != "calculated", "Unjoined overlapping wall produced a quantity.");
                double independentRawOverlap;
                using (Options overlapOptions = new Options())
                {
                    overlapOptions.View = geometryView;
                    using (GeometryElement a = wall.get_Geometry(overlapOptions)) using (GeometryElement b = overlapWall.get_Geometry(overlapOptions))
                    using (Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(a.OfType<Solid>().Single(s => s.Volume > 1e-12), b.OfType<Solid>().Single(s => s.Volume > 1e-12), BooleanOperationsType.Intersect))
                        independentRawOverlap = intersection.Volume;
                }
                double independentOverlap = independentRawOverlap * Math.Pow(0.3048, 3);
                Near(independentOverlap, ((bool)report["overlappingNativeOpeningCreated"] ? 5.4 : 5.6) / 2, "Independent SDK wall intersection must be half the original remaining volume.");
                Save("unjoined-overlap-facts.json", overlapFacts); Save("unjoined-overlap-calculation.json", overlapBatch);
                report["unjoinedPhysicalOverlapWallRefusalVerified"] = true; report["overlapWallElementId"] = Id(overlapWall.Id); report["overlapWallUniqueId"] = overlapWall.UniqueId;
                report["interwallOverlapIndependentRawFeet3"] = independentRawOverlap; report["interwallOverlapIndependentVolumeMetres3"] = independentOverlap;
                // These are explicit pure-domain refusals on controlled DTOs,
                // not a claim that a real family void was read in this sample.
                object unknown = CopyFact(noHoleFact);
                IList inserts = NewList(Get(unknown, "Inserts"));
                object insert = Activator.CreateInstance(inserts.GetType().GetGenericArguments()[0], true);
                Set(insert, "ElementId", "controlled"); Set(insert, "UniqueId", "controlled-unknown-void"); Set(insert, "HostUniqueId", wall.UniqueId);
                Set(insert, "Kind", "door"); Set(insert, "Status", "unsupported"); Set(insert, "Source", "Controlled domain negative, not runtime family geometry");
                inserts.Add(insert); Set(unknown, "Inserts", inserts);
                object refused = Call(calculator, "CalculateWall", unknown);
                Require((string)Get(refused, "Status") == "unsupported" && Get(refused, "ResultVolumeMetres3") == null, "Unknown family void facts must not produce a quantity.");
                object invalidProof = CopyFact(noHoleFact); Set(invalidProof, "SymmetricDifferenceMetres3", 1.0);
                refused = Call(calculator, "CalculateWall", invalidProof);
                Require((string)Get(refused, "Status") == "unsupported" && Get(refused, "ResultVolumeMetres3") == null, "Invalid geometry proof must not produce a quantity.");
                report["controlledUnknownVoidDomainRefusal"] = true; report["controlledInvalidProofDomainRefusal"] = true;
                report["runtimeFamilyVoidReaderVerified"] = false;
                report["cases"] = cases; report["fixtureWarnings"] = fixtureFailures;
                Require(Id(ui.ActiveView.Id) == originalView, "Test must not activate the geometry view.");
                Require(group.RollBack() == TransactionStatus.RolledBack, "Fixture transaction group did not roll back."); rolledBack = true;
                Require(createdUniqueIds.All(uid => document.GetElement(uid) == null), "A created fixture element remains after rollback.");
                Require(Count(document) == elementCount, "Document element count differs after rollback.");
                Require(Hash(config.modelPath) == modelHash, "Source RVT disk bytes changed.");
                report["fixtureCreatedUniqueIds"] = createdUniqueIds; report["fixtureRollbackVerified"] = true;
                report["liveRevitWallSdkVerified"] = true; report["result"] = "PASS";
            }
            catch (Exception error) { report["error"] = error.ToString(); }
            finally
            {
                if (group != null)
                {
                    if (!rolledBack) try { rolledBack = group.RollBack() == TransactionStatus.RolledBack; } catch (Exception error) { report["rollbackError"] = error.ToString(); report["result"] = "FAIL"; }
                    try { group.Dispose(); } catch (Exception error) { report["transactionGroupDisposeError"] = error.ToString(); report["result"] = "FAIL"; }
                }
                report["fixtureRolledBack"] = rolledBack;
                if (ui != null && selection != null)
                {
                    try { ui.Selection.SetElementIds(selection); report["selectionRestored"] = new HashSet<string>(ui.Selection.GetElementIds().Select(Id)).SetEquals(selection.Select(Id)); }
                    catch (Exception error) { report["selectionRestoreError"] = error.ToString(); report["result"] = "FAIL"; }
                }
                if (document != null && document.IsValidObject)
                {
                    try
                    {
                        report["activeViewUnchanged"] = originalView == Id(ui.ActiveView.Id);
                        report["isModifiedAfter"] = document.IsModified; report["elementCountAfter"] = Count(document);
                    }
                    catch (Exception error) { report["finalDocumentInspectionError"] = error.ToString(); report["result"] = "FAIL"; }
                }
                if (modelHash != null)
                {
                    try { string finalHash = Hash(config.modelPath); report["sourceSha256After"] = finalHash; report["sourceHashUnchanged"] = finalHash == modelHash; }
                    catch (Exception error) { report["finalSourceHashError"] = error.ToString(); report["result"] = "FAIL"; }
                }
                report["assertions"] = checks; report["elapsedSeconds"] = timer.Elapsed.TotalSeconds; report["finishedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                if (!rolledBack || (report.ContainsKey("activeViewUnchanged") && !(bool)report["activeViewUnchanged"]) ||
                    (report.ContainsKey("sourceHashUnchanged") && !(bool)report["sourceHashUnchanged"]) ||
                    (report.ContainsKey("selectionRestored") && !(bool)report["selectionRestored"])) report["result"] = "FAIL";
                try
                {
                    report["artifacts"] = Directory.GetFiles(config.outputDirectory).Where(path => Path.GetFileName(path) != "civil-wall-host-report.json" && Path.GetFileName(path) != "civil-wall-host-status.json")
                        .Select(path => Fields("name", Path.GetFileName(path), "bytes", new FileInfo(path).Length, "sha256", Hash(path))).ToList();
                }
                catch (Exception error) { report["artifactInventoryError"] = error.ToString(); report["result"] = "FAIL"; }
                SaveFinalReport(application, report);
            }
        }

        private object CheckCase(string name, UIDocument ui, Wall wall, Phase phase, View3D view, Type nativeReader, Type wallReader, Type calculator, double expectedVolume, double expectedOpeningArea, IList<Dictionary<string, object>> cases)
        {
            Status("reading-" + name); ui.Selection.SetElementIds(new List<ElementId> { wall.Id });
            object request = Call(nativeReader, "DefaultRequest", ui); Set(request, "Scope", "selection"); Set(request, "PhaseId", Id(phase.Id)); Set(request, "DesignOptionId", "-1");
            object native = Call(nativeReader, "Read", ui, request); string before = json.Serialize(native);
            IList nativeRows = (IList)Get(native, "Elements"); Require(nativeRows.Count == 1 && (string)Get(nativeRows[0], "UniqueId") == wall.UniqueId, "Fixture native reader selection is not exact.");
            object facts = Call(wallReader, "Read", ui, native, request, view);
            IList walls = (IList)Get(facts, "Walls"); Require(walls.Count == 1, "Fixture wall source was lost or repeated.");
            object fact = walls[0];
            Require((bool)Get(fact, "GeometryVerified") && (string)Get(fact, "Status") == "verified", "Native Boolean shape verification failed: " + json.Serialize(Get(fact, "Issues")));
            Require((bool)Get(fact, "InterwallOverlapCheckComplete") && ((IList)Get(fact, "IntersectingWallElementIds")).Count == 0, "Accepted wall lacks a complete non-overlap check.");
            Near(Convert.ToDouble(Get(Get(fact, "ParameterLength"), "Value"), CultureInfo.InvariantCulture), 10, "Independent fixture length");
            Near(Convert.ToDouble(Get(Get(fact, "ParameterHeight"), "Value"), CultureInfo.InvariantCulture), 3, "Independent fixture height");
            Near(Convert.ToDouble(Get(Get(fact, "ParameterWidth"), "Value"), CultureInfo.InvariantCulture), 0.2, "Independent fixture width");
            Require((string)Get(facts, "GeometryViewUniqueId") == view.UniqueId && (string)Get(facts, "GeometryPhaseId") == Id(phase.Id), "Explicit geometry context lost.");
            double actualGeometry = Convert.ToDouble(Get(fact, "GeometryVolumeMetres3"), CultureInfo.InvariantCulture);
            Near(actualGeometry, expectedVolume, "Actual geometry volume differs from independent rectangular fixture");
            double difference = Convert.ToDouble(Get(fact, "SymmetricDifferenceMetres3"), CultureInfo.InvariantCulture), tolerance = Convert.ToDouble(Get(fact, "GeometryToleranceMetres3"), CultureInfo.InvariantCulture);
            Require(difference >= 0 && difference <= tolerance && tolerance <= 1e-6, "Recorded geometry proof/tolerance invalid.");
            object batch = Call(calculator, "Calculate", facts); IList results = (IList)Get(batch, "Results"); Require(results.Count == 1, "Fixture calculation count differs."); object result = results[0];
            Require((string)Get(result, "Status") == "calculated", "Verified fixture did not produce its project-method result.");
            Near(Convert.ToDouble(Get(result, "BasisVolumeMetres3"), CultureInfo.InvariantCulture), 6, "Parameter-prism basis must remain 6m3, not native volume.");
            Near(Convert.ToDouble(Get(result, "OpeningUnionAreaMetres2"), CultureInfo.InvariantCulture), expectedOpeningArea, "Opening union area differs from independent fixture");
            Near(Convert.ToDouble(Get(result, "ResultVolumeMetres3"), CultureInfo.InvariantCulture), expectedVolume, "Project method result differs from independent fixture");
            double ledger = ((IList)Get(result, "Ledger")).Cast<object>().Sum(entry => Convert.ToDouble(Get(entry, "DeltaVolumeMetres3"), CultureInfo.InvariantCulture));
            Near(ledger, expectedVolume, "Signed ledger is not conserved.");
            double nativeRaw = wall.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED).AsDouble();
            Near(Convert.ToDouble(Get(Get(fact, "NativeVolume"), "Value"), CultureInfo.InvariantCulture), nativeRaw * Math.Pow(0.3048, 3), "Native quantity must remain separately recorded.");
            Require(json.Serialize(native) == before, "Wall facts or calculator mutated native source snapshot.");
            Save(name + "-native.json", native); Save(name + "-facts.json", facts); Save(name + "-calculation.json", batch);
            string csv = (string)Call(calculator, "ToCsv", batch, Get(batch, "Results")); SaveText(name + ".csv", csv);
            cases.Add(Fields("name", name, "wallUniqueId", wall.UniqueId, "expectedBasisMetres3", 6.0, "expectedOpeningUnionAreaMetres2", expectedOpeningArea,
                "expectedResultMetres3", expectedVolume, "geometryVolumeMetres3", actualGeometry, "symmetricDifferenceMetres3", difference, "geometryToleranceMetres3", tolerance,
                "nativeRawVolumeFeet3", nativeRaw, "nativeVolumeMetres3", nativeRaw * Math.Pow(0.3048, 3), "nativeIsNotSubtractionBasis", true,
                "openingCount", ((IList)Get(fact, "Inserts")).Count, "ledgerCount", ((IList)Get(result, "Ledger")).Count, "nativeSnapshotUnchanged", true));
            return fact;
        }

        private Opening NewOpening(Document document, Wall wall, double baseZ, double x0, double x1, double z0, double z1)
        { return document.Create.NewOpening(wall, new XYZ(5000 + x0 * FeetPerMetre, 5000, baseZ + z0 * FeetPerMetre), new XYZ(5000 + x1 * FeetPerMetre, 5000, baseZ + z1 * FeetPerMetre)); }
        private void Mutate(Document document, string name, Action action)
        {
            using (Transaction transaction = new Transaction(document, name))
            {
                transaction.Start(); transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new FixtureFailures(fixtureFailures)).SetClearAfterRollback(true));
                try { action(); if (transaction.Commit() != TransactionStatus.Committed) throw new FixtureCreationUnavailable("Disposable fixture transaction rolled back: " + String.Join("; ", fixtureFailures)); }
                catch { if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack(); throw; }
            }
        }
        private sealed class FixtureFailures : IFailuresPreprocessor
        {
            private readonly IList<string> messages; internal FixtureFailures(IList<string> messages) { this.messages = messages; }
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
                {
                    messages.Add(failure.GetDescriptionText());
                    if (failure.GetSeverity() != FailureSeverity.Warning) return FailureProcessingResult.ProceedWithRollBack;
                    accessor.DeleteWarning(failure);
                }
                return FailureProcessingResult.Continue;
            }
        }
        private sealed class FixtureCreationUnavailable : Exception { internal FixtureCreationUnavailable(string text) : base(text) { } }
        private static object CopyFact(object fact)
        { object copy = Activator.CreateInstance(fact.GetType(), true); foreach (PropertyInfo property in fact.GetType().GetProperties()) if (property.CanWrite) property.SetValue(copy, property.GetValue(fact, null), null); return copy; }
        private static IList NewList(object list) { return (IList)Activator.CreateInstance(list.GetType()); }
        private static int Count(Document document)
        {
            using (FilteredElementCollector collector = new FilteredElementCollector(document))
                return collector.WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true))).ToElementIds().Count;
        }
        private static object Get(object value, string name) { return value.GetType().GetProperty(name).GetValue(value, null); }
        private static void Set(object value, string name, object data) { value.GetType().GetProperty(name).SetValue(value, data, null); }
        private static object Call(Type type, string name, params object[] values)
        { return type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Single(method => method.Name == name && method.GetParameters().Length == values.Length).Invoke(null, values); }
        private void Near(double actual, double expected, string label) { Require(!Double.IsNaN(actual) && !Double.IsInfinity(actual) && Math.Abs(actual - expected) <= Math.Max(1e-7, Math.Abs(expected) * 1e-7), label + ": " + actual.ToString("R", CultureInfo.InvariantCulture) + " != " + expected.ToString("R", CultureInfo.InvariantCulture)); }
        private void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        private static string Id(ElementId id) { return id == null ? "-1" : id.IntegerValue.ToString(CultureInfo.InvariantCulture); }
        private static bool SamePath(string a, string b) { return String.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        private static bool Sha(string value) { return value != null && value.Length == 64 && value.All(c => "0123456789abcdefABCDEF".IndexOf(c) >= 0); }
        private static string Hash(string file) { using (SHA256 sha = SHA256.Create()) using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private string Child(string file)
        {
            string path = Path.GetFullPath(file), root = Path.GetFullPath(boundary).TrimEnd('\\') + "\\";
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Wall acceptance path leaves phase boundary.");
            for (string cursor = path; cursor != null && cursor.Length >= root.Length - 1; cursor = Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Wall acceptance path is a reparse point.");
            return path;
        }
        private static Dictionary<string, object> Fields(params object[] values) { var result = new Dictionary<string, object>(); for (int index = 0; index < values.Length; index += 2) result.Add((string)values[index], values[index + 1]); return result; }
        private void SaveFinalReport(UIApplication application, Dictionary<string, object> report)
        {
            bool saved = false;
            try { Save("civil-wall-host-report.json", report); saved = true; }
            catch (Exception error)
            {
                report["reportWriteError"] = error.ToString(); report["result"] = "FAIL";
                try { Save("civil-wall-host-write-error.json", report); }
                catch (Exception fallback) { JournalFailure(application, error.ToString() + "\nFallback report write: " + fallback); }
            }
            try { Status(saved && (string)report["result"] == "PASS" ? "completed" : "failed"); }
            catch (Exception error)
            {
                report["statusWriteError"] = error.ToString(); report["result"] = "FAIL";
                try { Save("civil-wall-host-report.json", report); }
                catch (Exception fallback) { JournalFailure(application, error.ToString() + "\nFinal report write: " + fallback); }
            }
        }
        private static void JournalFailure(UIApplication application, string error)
        {
            try { application.Application.WriteJournalComment("BV wall SDK final evidence failed: " + error, false); }
            catch { /* No further desktop or document actions if both evidence paths and journaling fail. */ }
        }
        private void Status(string phase) { Save("civil-wall-host-status.json", Fields("phase", phase, "runId", config.runId, "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))); }
        private void Save(string name, object data) { SaveText(name, json.Serialize(data)); }
        private void SaveText(string name, string text)
        {
            Directory.CreateDirectory(config.outputDirectory); string path = Child(Path.Combine(config.outputDirectory, name));
            if (Encoding.UTF8.GetByteCount(text) > 16777216) throw new InvalidDataException("Wall acceptance evidence exceeds 16 MiB.");
            File.WriteAllText(path, text, new UTF8Encoding(name.EndsWith(".csv", StringComparison.Ordinal)));
        }
        private sealed class Config
        {
            public string runId { get; set; } public string modelPath { get; set; } public string outputDirectory { get; set; }
            public string exporterPath { get; set; } public string expectedExporterSha256 { get; set; }
            public string expectedModelSha256 { get; set; } public string expectedDriverSha256 { get; set; }
        }
    }
}
