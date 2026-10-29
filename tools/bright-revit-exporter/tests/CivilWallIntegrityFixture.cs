// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BrightVision.Civil;

internal static class CivilWallIntegrityFixture
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Near(double? actual, double expected, string message) { Check(actual.HasValue && Math.Abs(actual.Value - expected) <= 1e-9, message); }
    private static void Refuses(Action action, string message)
    {
        bool refused = false; try { action(); } catch (InvalidDataException) { refused = true; } catch (ArgumentException) { refused = true; }
        Check(refused, message);
    }
    private static CivilMetric Metric(double value, string unit, string source) { return new CivilMetric { Value = value, Unit = unit, Source = source, Status = "available" }; }
    private static CivilWallInsert Hole(string id, double x0, double x1, double z0, double z1)
    {
        return new CivilWallInsert { ElementId = "opening-" + id, UniqueId = "opening-uid-" + id, Name = "明确矩形洞口" + id, HostUniqueId = "wall-uid",
            Kind = "rect-opening", Status = "recognized", Source = "Opening.BoundaryRect; confirmed host local rectangle", RectX0 = x0, RectX1 = x1, RectZ0 = z0, RectZ1 = z1 };
    }
    private static CivilWallSnapshot Input(int openings = 2)
    {
        var wall = new CivilWallFact { ElementId = "10", UniqueId = "wall-uid", Name = "墙", TypeUniqueId = "type-uid", TypeName = "200mm", CurveKind = "Line",
            BaseElevationSource = "Level.ProjectElevation + WALL_BASE_OFFSET", BaseElevationMetres = 0, TopConstraintId = "-1", AttachedTop = false, AttachedBottom = false,
            ParameterLength = Metric(10,"m","CURVE_ELEM_LENGTH"), ParameterHeight = Metric(3,"m","WALL_USER_HEIGHT_PARAM"), ParameterWidth = Metric(.2,"m","WallType.Width"),
            NativeLength = Metric(10,"m","CURVE_ELEM_LENGTH"), NativeArea = Metric(123,"m2","HOST_AREA_COMPUTED"), NativeVolume = Metric(456,"m3","HOST_VOLUME_COMPUTED"),
            GeometryVolumeMetres3 = openings == 0 ? 6 : openings == 1 ? 5.6 : openings == 2 ? 5.4 : 0,
            GeometryVerified = true, InterwallOverlapCheckComplete = true, OverlapCandidateCount = 0, SymmetricDifferenceMetres3 = 0, GeometryToleranceMetres3 = 1e-8, Status = "verified" };
        if (openings == 1 || openings == 2) wall.Inserts.Add(Hole("A", 1, 3, 1, 2));
        if (openings == 2) wall.Inserts.Add(Hole("B", 2, 4, 1, 2));
        if (openings == 3) wall.Inserts.Add(Hole("full", 0, 10, 0, 3));
        var native = new CivilSnapshot { ModelTitle = "完整性夹具", DocumentScope = "integrity-document", SourcePath = "H:\\fixture\\integrity.rvt", Revision = 7,
            CapturedAtUtc = "2026-10-29T00:00:00Z", ScopeLabel = "project", PhaseLabel = "new", DesignOptionLabel = "main", CandidateCount = 1 };
        native.Elements.Add(new CivilElementRecord { ElementId = wall.ElementId, UniqueId = wall.UniqueId, Name = wall.Name, KindCode = "wall", KindName = "墙", Category = "墙", TypeName = wall.TypeName,
            Supported = true, LevelId = "101", LevelUniqueId = "level-uid", LevelName = "Level 1", LevelSource = "WALL_BASE_CONSTRAINT", RawLevelId = "101", RawLevelName = "Level 1",
            Length = wall.NativeLength, Area = wall.NativeArea, Volume = wall.NativeVolume });
        return new CivilWallSnapshot { NativeSnapshot = native, RuleTemplate = CivilWallRuleTemplates.Default(), Walls = new List<CivilWallFact> { wall },
            GeometryViewUniqueId = "view-uid", GeometryViewName = "3D", GeometryPhaseId = "phase-id" };
    }
    private static CivilWallCalculationBatch Batch(int openings = 2) { return CivilWallCalculator.Calculate(Input(openings)); }
    private static CivilWallCalculationBatch Unsealed(int openings = 2)
    {
        CivilWallSnapshot input = Input(openings);
        return new CivilWallCalculationBatch { Input = input, Results = new List<CivilWallResult> { CivilWallCalculator.CalculateWall(input.Walls[0]) } };
    }
    private static void Mutation(Action<CivilWallCalculationBatch> change, string label)
    {
        CivilWallCalculationBatch batch = Batch(); CivilWallReportData report = CivilWallReport.Create(batch); change(batch);
        Refuses(delegate { CivilWallBatchIntegrity.Validate(batch); }, label + " direct integrity");
        Refuses(delegate { CivilWallReport.Create(batch); }, label + " report creation");
        Refuses(delegate { CivilWallReport.Filter(report,"","","",""); }, label + " filtering");
        Refuses(delegate { CivilWallReport.Group(report.Rows,"type"); }, label + " direct grouping follows owner integrity");
        Refuses(delegate { CivilWallReport.ToCsv(report,null,"type"); }, label + " report CSV");
        Refuses(delegate { CivilWallCalculator.ToCsv(batch,batch.Results); }, label + " legacy CSV");
    }
    private static void SealRefuses(Action<CivilWallResult> change, string label)
    {
        CivilWallCalculationBatch batch = Unsealed(); change(batch.Results[0]);
        Refuses(delegate { CivilWallBatchIntegrity.Seal(batch); }, label);
        Check(String.IsNullOrEmpty(batch.BatchId), label + " never commits an ID");
        Refuses(delegate { CivilWallBatchIntegrity.Validate(batch); }, label + " remains unusable");
    }
    private static CivilProjectSettings Project(CivilWallCalculationBatch batch)
    {
        var project = CivilProject.Create(batch.Input.NativeSnapshot,"完整性工程归属");
        project.SchemaVersion = 2; project.WallRuleTemplate = CivilWallRuleTemplates.Clone(batch.RuleTemplate); CivilProject.Touch(project);
        project.QuantityLevels.Add(new CivilQuantityLevel { Id = "Q1", Name = "计量层" });
        project.LevelMappings.Add(new CivilLevelMapping { SourceLevelUniqueId = "level-uid", QuantityLevelId = "Q1" });
        project.Segments.Add(new CivilSegment { Id = "S1", Name = "施工段" });
        project.ElementSegments.Add(new CivilElementSegment { ElementUniqueId = "wall-uid", SegmentId = "S1" });
        return project;
    }
    private static void AppliedMutation(Action<CivilWallReportData> change, string label)
    {
        CivilWallCalculationBatch batch = Batch(); CivilProjectSettings project = Project(batch); CivilWallReportData report = CivilWallReport.Create(batch,project);
        Check(!Object.ReferenceEquals(report.Snapshot,batch.Input.NativeSnapshot), label + " uses an applied snapshot");
        change(report); CivilWallBatchIntegrity.Validate(batch);
        Refuses(delegate { CivilWallReport.Filter(report,"","","",""); }, label + " applied snapshot filtering");
        Refuses(delegate { CivilWallReport.Group(report.Rows,"type"); }, label + " applied snapshot grouping");
        Refuses(delegate { CivilWallReport.ToCsv(report,null,"type"); }, label + " applied snapshot CSV");
        Check(CivilWallReport.Create(batch,project).Rows.Count == 1, label + " did not change the original facts or project");
    }
    private static IEnumerable<CivilWallReportRow> YieldThenChange(IEnumerable<CivilWallReportRow> rows, Action change, bool changeBeforeFirst)
    {
        if (changeBeforeFirst) change();
        foreach (CivilWallReportRow row in rows) yield return row;
        if (!changeBeforeFirst) change();
    }

    private static int Main(string[] args)
    {
        string output = args[0];
        foreach (int openings in new[] { 0, 1, 2, 3 })
        {
            CivilWallCalculationBatch batch = Batch(openings); CivilWallResult row = batch.Results.Single(); CivilWallBatchIntegrity.Validate(batch);
            double area = openings == 0 ? 30 : openings == 1 ? 28 : openings == 2 ? 27 : 0;
            double volume = openings == 0 ? 6 : openings == 1 ? 5.6 : openings == 2 ? 5.4 : 0;
            Check(row.Status == "calculated", "Supported fixture calculates"); Near(row.ResultAreaMetres2,area,"Independent projected area"); Near(row.ResultVolumeMetres3,volume,"Independent volume");
            Near(row.Ledger.Sum(entry => entry.DeltaAreaMetres2),area,"Signed area ledger conserves"); Near(row.Ledger.Sum(entry => entry.DeltaVolumeMetres3),volume,"Signed volume ledger conserves");
            Check(row.Fact.NativeArea.Value == 123 && row.Fact.NativeVolume.Value == 456, "Native facts remain separate and are not subtracted again");
            Guid id; Check(Guid.TryParseExact(batch.BatchId,"N",out id) && id != Guid.Empty, "Calculation automatically seals a nonempty batch identity");
            Check(batch.RuleFingerprint.Length == 64 && batch.InputFingerprint.Length == 64 && batch.ResultFingerprint.Length == 64, "All three canonical digests are present");
            Check(!Object.ReferenceEquals(batch.RuleTemplate,batch.Input.RuleTemplate) && batch.RuleFingerprint == CivilWallRuleTemplates.Fingerprint(batch.RuleTemplate), "Effective rule is cloned and fingerprinted");
            Check(CivilWallReport.Create(batch).Rows.Count == 1 && CivilWallReport.Group(CivilWallReport.Create(batch).Rows,"type").Single().CalculatedCount == 1, "Unchanged sealed batch is reportable");
        }
        CivilWallSnapshot emptyInput = Input(); emptyInput.Walls.Clear(); emptyInput.NativeSnapshot.Elements.Clear(); emptyInput.NativeSnapshot.CandidateCount = 0;
        CivilWallCalculationBatch emptyBatch = CivilWallCalculator.Calculate(emptyInput); CivilWallBatchIntegrity.Validate(emptyBatch);
        Check(CivilWallReport.Create(emptyBatch).Rows.Count == 0 && CivilWallReport.ToCsv(CivilWallReport.Create(emptyBatch),null,"type").Length > 0, "A completed empty range remains a valid zero-coverage report");
        foreach (string state in new[] { "unsupported", "failed" })
        {
            CivilWallSnapshot refusedInput = Input(); refusedInput.Walls[0].Status = state; refusedInput.Walls[0].GeometryVerified = false; refusedInput.NativeSnapshot.Elements[0].Supported = false;
            CivilWallCalculationBatch refusedBatch = CivilWallCalculator.Calculate(refusedInput); CivilWallBatchIntegrity.Validate(refusedBatch);
            Check(refusedBatch.Results[0].Status == state && !refusedBatch.Results[0].ResultVolumeMetres3.HasValue && refusedBatch.Results[0].Ledger.Count == 0, "Refused source retains status without a fake zero quantity");
            Check(CivilWallReport.Filter(CivilWallReport.Create(refusedBatch),"",state,"","").Count == 1, "Refused source is still covered by its report");
        }
        CivilWallSnapshot defaultInput = Input(); defaultInput.RuleTemplate = null; CivilWallCalculationBatch defaultBatch = CivilWallCalculator.Calculate(defaultInput);
        Check(defaultBatch.Input.RuleTemplate == null && defaultBatch.RuleFingerprint == CivilWallRuleTemplates.Fingerprint(CivilWallRuleTemplates.Default()), "Absent input rule resolves to a cloned default without mutating input");
        string batchId = defaultBatch.BatchId, inputHash = defaultBatch.InputFingerprint, resultHash = defaultBatch.ResultFingerprint;
        Refuses(delegate { CivilWallBatchIntegrity.Seal(defaultBatch); }, "Already sealed batch cannot be sealed again");
        Check(defaultBatch.BatchId == batchId && defaultBatch.InputFingerprint == inputHash && defaultBatch.ResultFingerprint == resultHash, "Reseal failure does not replace its original seal");
        defaultBatch.BatchId = null;
        Refuses(delegate { CivilWallBatchIntegrity.Seal(defaultBatch); }, "Clearing public seal fields cannot permit resealing the same batch");
        defaultBatch.BatchId = batchId; CivilWallBatchIntegrity.Validate(defaultBatch);
        Check(CivilWallReport.Create(defaultBatch).Rows.Count == 1, "Restoring the original batch ID retains its private original seal");
        var borrowed = new CivilWallCalculationBatch { Input = defaultBatch.Input, Results = defaultBatch.Results, RuleTemplate = defaultBatch.RuleTemplate,
            BatchId = defaultBatch.BatchId, RuleFingerprint = defaultBatch.RuleFingerprint, InputFingerprint = defaultBatch.InputFingerprint, ResultFingerprint = defaultBatch.ResultFingerprint };
        Refuses(delegate { CivilWallBatchIntegrity.Validate(borrowed); }, "A new batch object cannot borrow another object's public ID and hashes");
        Refuses(delegate { CivilWallReport.Create(borrowed); }, "Borrowed public seal fields cannot create a report");
        Refuses(delegate { CivilWallCalculator.ToCsv(borrowed,borrowed.Results); }, "Borrowed public seal fields cannot export legacy CSV");
        CivilWallCalculationBatch unsealed = Unsealed();
        Refuses(delegate { CivilWallBatchIntegrity.Validate(unsealed); }, "Hand-built unsealed batch cannot validate");
        Refuses(delegate { CivilWallReport.Create(unsealed); }, "Hand-built unsealed batch cannot create a report");
        Refuses(delegate { CivilWallCalculator.ToCsv(unsealed,unsealed.Results); }, "Hand-built unsealed batch cannot export legacy CSV");
        Mutation(value => value.Input.Walls[0].ParameterLength.Value = 11, "Finite parameter length change");
        Mutation(value => value.Input.Walls[0].ParameterHeight.Source += " modified", "Parameter source change");
        Mutation(value => value.Input.Walls[0].Inserts[0].RectX1 = 3.1, "Finite actual opening boundary change");
        Mutation(value => value.Input.Walls[0].Inserts[0].HostUniqueId = "another-wall", "Opening host change");
        Mutation(value => value.Input.Walls[0].Inserts[0].Source += " changed", "Opening evidence source change");
        Mutation(value => value.Input.Walls[0].Inserts[0].UniqueId += " changed", "Opening identity change");
        Mutation(value => value.Input.Walls[0].Inserts.Reverse(), "Opening evidence sequence change");
        Mutation(value => value.Input.Walls[0].GeometryVolumeMetres3 = 5.41, "Finite actual geometry change");
        Mutation(value => value.Input.Walls[0].SymmetricDifferenceMetres3 = 1e-9, "Geometry proof change");
        Mutation(value => value.Input.Walls[0].GeometryToleranceMetres3 = 1e-9, "Geometry tolerance change");
        Mutation(value => value.Input.Walls[0].JoinedElementIds.Add("21"), "Join dependency change");
        Mutation(value => value.Input.Walls[0].EndpointJoinedElementIds.Add("22"), "Endpoint dependency change");
        Mutation(value => value.Input.Walls[0].IntersectingWallElementIds.Add("23"), "Intersection dependency change");
        Mutation(value => value.Input.Walls[0].OverlapCandidateCount = 1, "Candidate coverage count change");
        Mutation(value => value.Input.Walls[0].Issues.Add("new issue"), "Evidence issue list change");
        Mutation(value => value.Input.NativeSnapshot.DocumentScope += " changed", "Native document identity change");
        Mutation(value => value.Input.NativeSnapshot.SourcePath = "H:\\fixture\\copied.rvt", "Native source path change");
        Mutation(value => value.Input.NativeSnapshot.Revision++, "Native model revision change");
        Mutation(value => value.Input.NativeSnapshot.Elements[0].UniqueId += " changed", "Native source element identity change");
        Mutation(value => value.Input.NativeSnapshot.Elements[0].Volume.Value = 457, "Finite original native quantity change");
        Mutation(value => value.Input.NativeSnapshot.Elements[0].Area.Source += " changed", "Native quantity source change");
        Mutation(value => value.Input.NativeSnapshot.Notices.Add("new source notice"), "Native source notice change");
        Mutation(value => value.Input.GeometryViewUniqueId += " changed", "Geometry view identity change");
        Mutation(value => value.Input.GeometryPhaseId += " changed", "Geometry phase change");
        Mutation(value => value.Input.RuleTemplate.Name += " changed", "Source rule name change");
        Mutation(value => value.Input.RuleTemplate.Revision++, "Source rule revision change");
        Mutation(value => value.Input.RuleTemplate.OpeningPolicy = CivilWallRuleTemplates.NativeOpeningOnly, "Source rule opening policy change");
        Mutation(value => value.Input.RuleTemplate.AbsoluteToleranceMetres3 = 1e-9, "Source rule absolute tolerance change");
        Mutation(value => value.Input.RuleTemplate.RelativeTolerance = 1e-9, "Source rule relative tolerance change");
        Mutation(value => value.RuleTemplate.Name += " changed", "Frozen effective rule name change");
        Mutation(value => value.RuleTemplate.Revision++, "Frozen effective rule revision change");
        Mutation(value => value.RuleTemplate.OpeningPolicy = CivilWallRuleTemplates.NativeOpeningOnly, "Frozen effective opening policy change");
        Mutation(value => value.RuleTemplate.AbsoluteToleranceMetres3 = 1e-9, "Frozen effective absolute tolerance change");
        Mutation(value => value.RuleTemplate.RelativeTolerance = 1e-9, "Frozen effective relative tolerance change");
        Mutation(value => value.RuleFingerprint = new String('0',64), "Rule digest replacement");
        Mutation(value => value.InputFingerprint = new String('0',64), "Input digest replacement");
        Mutation(value => value.ResultFingerprint = new String('0',64), "Result digest replacement");
        Mutation(value => value.BatchId = Guid.NewGuid().ToString("N"), "Batch identity replacement");
        Mutation(value => value.Results[0].ResultVolumeMetres3 = 5.5, "Finite result volume change");
        Mutation(value => value.Results[0].ResultAreaMetres2 = 27.1, "Finite result area change");
        Mutation(value => value.Results[0].Ledger[1].DeltaVolumeMetres3 = -.3, "Finite signed ledger volume change");
        Mutation(value => value.Results[0].Ledger[1].DeltaAreaMetres2 = -1.9, "Finite signed ledger area change");
        Mutation(value => value.Results[0].Ledger[1].AreaMetres2 = 1.9, "Legacy incremental area change");
        Mutation(value => value.Results[0].Ledger[1].ElementId += " changed", "Ledger associated element ID change");
        Mutation(value => value.Results[0].Ledger[1].UniqueId += " changed", "Ledger associated UID change");
        Mutation(value => value.Results[0].Ledger[1].Formula += " changed", "Ledger volume formula change");
        Mutation(value => value.Results[0].Ledger[1].AreaFormula += " changed", "Ledger area formula change");
        Mutation(value => value.Results[0].Ledger[1].Source += " changed", "Ledger evidence source change");
        Mutation(value => value.Results[0].Message += " changed", "Result status explanation change");

        CivilWallCalculationBatch restored = Batch(); CivilWallReportData restoredReport = CivilWallReport.Create(restored);
        double previous = restored.Input.Walls[0].ParameterLength.Value.Value; restored.Input.Walls[0].ParameterLength.Value = 11;
        Refuses(delegate { CivilWallBatchIntegrity.Validate(restored); }, "Changed parameter first invalidates");
        restored.Input.Walls[0].ParameterLength.Value = previous; CivilWallBatchIntegrity.Validate(restored);
        Check(CivilWallReport.Filter(restoredReport,"","","","").Count == 1, "Restoring the exact original value restores validity");
        restored.Input.Walls[0].ParameterLength = Metric(10,"m","CURVE_ELEM_LENGTH"); CivilWallBatchIntegrity.Validate(restored);
        Check(CivilWallReport.Create(restored).Rows.Count == 1, "Equivalent metric values in a new DTO retain value-based integrity");
        restored.RuleTemplate = CivilWallRuleTemplates.Clone(restored.RuleTemplate); CivilWallBatchIntegrity.Validate(restored);
        Check(CivilWallReport.ToCsv(restoredReport,null,"type").Length > 0, "Equivalent cloned rule retains integrity");
        var forgedRow = new CivilWallReportRow { Owner = restoredReport, Source = restoredReport.Rows[0].Source, Result = CivilWallCalculator.CalculateWall(restored.Input.Walls[0]) };
        forgedRow.Result.ResultVolumeMetres3 = 123;
        Refuses(delegate { CivilWallReport.Group(new[] { forgedRow },"type"); }, "A forged row cannot borrow a valid owner to bypass its sealed result set");
        CivilWallCalculationBatch lazyBatch = Batch(); CivilWallReportData lazyReport = CivilWallReport.Create(lazyBatch); bool lazyFactRan = false;
        IEnumerable<CivilWallReportRow> lazyFacts = YieldThenChange(lazyReport.Rows,delegate { lazyFactRan = true; lazyBatch.Input.Walls[0].ParameterLength.Value = 11; },true);
        Check(!lazyFactRan,"Selected fact mutation is deferred until enumeration");
        Refuses(delegate { CivilWallReport.ToCsv(lazyReport,lazyFacts,"type"); },"Fact mutation inside selected enumeration cannot bypass the single final validation");
        Check(lazyFactRan,"CSV actually materialized the external enumerator before detecting the fact change");
        CivilWallCalculationBatch lazyAppliedBatch = Batch(); CivilWallReportData lazyApplied = CivilWallReport.Create(lazyAppliedBatch,Project(lazyAppliedBatch)); bool lazyAppliedRan = false;
        IEnumerable<CivilWallReportRow> lazySnapshot = YieldThenChange(lazyApplied.Rows,delegate { lazyAppliedRan = true; lazyApplied.Snapshot.Elements[0].Volume = Metric(457,"m3","HOST_VOLUME_COMPUTED"); },false);
        Check(!lazyAppliedRan,"Selected applied-snapshot mutation is deferred until the final MoveNext");
        Refuses(delegate { CivilWallReport.ToCsv(lazyApplied,lazySnapshot,"type"); },"Applied snapshot mutation after the last yielded row cannot bypass validation");
        CivilWallBatchIntegrity.Validate(lazyAppliedBatch);
        Check(lazyAppliedRan && lazyAppliedBatch.Input.NativeSnapshot.Elements[0].Volume.Value == 456,"Final enumeration callback changed only the applied snapshot, not sealed native facts");
        CivilWallCalculationBatch lazyForgedBatch = Batch(); CivilWallReportData lazyForgedReport = CivilWallReport.Create(lazyForgedBatch); bool lazyForgedRan = false;
        var lazyForgedRow = new CivilWallReportRow { Owner = lazyForgedReport, Source = lazyForgedReport.Rows[0].Source, Result = CivilWallCalculator.CalculateWall(lazyForgedBatch.Input.Walls[0]) };
        IEnumerable<CivilWallReportRow> lazyForgery = YieldThenChange(new[] { lazyForgedRow },delegate { lazyForgedRan = true; },true);
        Refuses(delegate { CivilWallReport.ToCsv(lazyForgedReport,lazyForgery,"type"); },"An external enumeration cannot return a forged same-owner row even with numerically matching results");
        Check(lazyForgedRan,"Forged-row enumeration was actually consumed before its ownership was refused");
        AppliedMutation(value => value.Snapshot.Elements[0].Volume = Metric(457,"m3","HOST_VOLUME_COMPUTED"), "Finite applied native-volume reference replacement");
        AppliedMutation(value => value.Snapshot.Elements[0].Area = Metric(124,"m2","HOST_AREA_COMPUTED"), "Finite applied native-area reference replacement");
        AppliedMutation(value => value.Snapshot.Elements[0].Length = Metric(11,"m","CURVE_ELEM_LENGTH"), "Finite applied native-length reference replacement");
        AppliedMutation(value => value.Snapshot.Elements[0].QuantityLevelId = "Q2", "Applied quantity-level identity change");
        AppliedMutation(value => value.Snapshot.Elements[0].QuantityLevelName += " changed", "Applied quantity-level label change");
        AppliedMutation(value => value.Snapshot.Elements[0].SegmentId = "S2", "Applied segment identity change");
        AppliedMutation(value => value.Snapshot.Elements[0].SegmentName += " changed", "Applied segment label change");
        AppliedMutation(value => value.Snapshot.Elements[0].ProjectIssues.Add("changed attribution issue"), "Applied attribution issue change");
        AppliedMutation(value => value.Snapshot.Elements[0].Name += " changed", "Applied source label change");
        AppliedMutation(value => value.Snapshot.ProjectRevision++, "Applied project revision change");
        AppliedMutation(value => value.Snapshot.ProjectName += " changed", "Applied project name change");
        AppliedMutation(value => value.Snapshot.ProjectNotices.Add("changed project notice"), "Applied project notice change");
        CivilWallCalculationBatch appliedRestoredBatch = Batch(); CivilWallReportData appliedRestored = CivilWallReport.Create(appliedRestoredBatch,Project(appliedRestoredBatch));
        appliedRestored.Snapshot.Elements[0].Volume = Metric(457,"m3","HOST_VOLUME_COMPUTED");
        Refuses(delegate { CivilWallReport.Filter(appliedRestored,"","","",""); }, "Applied finite quantity change first invalidates");
        appliedRestored.Snapshot.Elements[0].Volume = Metric(456,"m3","HOST_VOLUME_COMPUTED");
        Check(CivilWallReport.Filter(appliedRestored,"","","","").Count == 1, "Equivalent restored applied quantity values restore report validity");

        SealRefuses(row => row.Ledger[0].Kind = "deduction", "Wrong basis step kind rejected at seal");
        SealRefuses(row => row.Ledger[0].UniqueId = "wrong-wall", "Unrelated basis UID rejected at seal");
        SealRefuses(row => row.Ledger[0].DeltaVolumeMetres3 = 5, "Basis ledger not equal to basis volume rejected at seal");
        SealRefuses(row => row.Ledger[1].DeltaVolumeMetres3 = .4, "Positive deduction volume rejected at seal");
        SealRefuses(row => row.Ledger[1].DeltaAreaMetres2 = 2, "Positive deduction area rejected at seal");
        SealRefuses(row => row.Ledger[1].Source = "", "Missing evidence source rejected at seal");
        SealRefuses(row => row.Ledger[1].ElementId = "", "Missing related ID rejected at seal");
        SealRefuses(row => row.Ledger[1].UniqueId = "", "Missing related UID rejected at seal");
        SealRefuses(row => row.Ledger[1].Formula = "", "Missing volume formula rejected at seal");
        SealRefuses(row => row.Ledger[1].AreaFormula = "", "Missing area formula rejected at seal");
        SealRefuses(row => row.Ledger[1].AreaMetres2 = Double.NaN, "Nonfinite legacy ledger area rejected at seal");
        SealRefuses(row => row.Ledger[1].AreaMetres2 = -2, "Negative legacy ledger area rejected at seal");
        SealRefuses(row => row.Ledger[1].AreaMetres2 = 1, "Legacy and signed ledger area mismatch rejected at seal");
        SealRefuses(row => row.ResultVolumeMetres3 = 5.5, "Result equation mismatch rejected at seal");
        SealRefuses(row => row.ResultAreaMetres2 = 28, "Projection result equation mismatch rejected at seal");
        SealRefuses(row => row.Ledger.Clear(), "Missing basis ledger rejected at seal");
        SealRefuses(row => {
            row.BasisVolumeMetres3 = row.ResultVolumeMetres3 = Double.MaxValue; row.DeductionVolumeMetres3 = 0;
            row.Ledger[0].DeltaVolumeMetres3 = Double.MaxValue; row.Ledger[1].DeltaVolumeMetres3 = -Double.MaxValue; row.Ledger[2].DeltaVolumeMetres3 = -Double.MaxValue;
            row.Ledger.Add(new CivilWallLedgerEntry { Kind = "deduction", ElementId = "third", UniqueId = "third-uid", Source = "overflow fixture", Formula = "0", AreaFormula = "0", AreaMetres2 = 0, DeltaAreaMetres2 = 0, DeltaVolumeMetres3 = -Double.MaxValue });
        }, "Finite ledger values whose running sum overflows are rejected at seal");
        CivilWallCalculationBatch unsupported = Unsealed(); unsupported.Results[0].Status = "unsupported";
        Refuses(delegate { CivilWallBatchIntegrity.Seal(unsupported); }, "Unsupported row cannot retain fake quantities or ledger");
        Check(String.IsNullOrEmpty(unsupported.BatchId), "Failed unsupported seal has no batch ID");
        File.WriteAllText(Path.Combine(output,"report.json"), "{\"result\":\"PASS\",\"assertions\":" + checks.ToString(CultureInfo.InvariantCulture) + ",\"scope\":\"Own managed wall batch sealing, immutable source/rule/result values, ledger conservation and all report routes; synthetic facts only\",\"testedReportRoutes\":[\"Create\",\"Filter\",\"Group\",\"ReportCsv\",\"LegacyCsv\"],\"revitStarted\":false,\"nativeDesktopInput\":false,\"childFixtureExecutable\":false}", new UTF8Encoding(false));
        Console.WriteLine("PASS " + checks.ToString(CultureInfo.InvariantCulture) + " wall integrity assertions."); return 0;
    }
}
