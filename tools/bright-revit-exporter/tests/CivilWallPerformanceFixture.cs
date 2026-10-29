// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using BrightVision.Civil;

internal static class CivilWallPerformanceFixture
{
    [DataContract]
    private sealed class Measurement
    {
        [DataMember] public int Size;
        [DataMember] public int Iteration;
        [DataMember] public bool Warmup;
        [DataMember] public string Stage;
        [DataMember] public double Milliseconds;
        [DataMember] public double ProcessorMilliseconds;
        [DataMember] public long ManagedHeapBeforeBytes;
        [DataMember] public long ManagedHeapAfterBytes;
        [DataMember] public long ManagedHeapDeltaBytes;
        [DataMember] public long PrivateMemoryBeforeBytes;
        [DataMember] public long PrivateMemoryAfterBytes;
        [DataMember] public long PrivateMemoryDeltaBytes;
        [DataMember] public long WorkingSetBeforeBytes;
        [DataMember] public long WorkingSetAfterBytes;
        [DataMember] public long WorkingSetPeakSinceProcessStartBytes;
        [DataMember] public int Gen0Collections;
        [DataMember] public int Gen1Collections;
        [DataMember] public int Gen2Collections;
    }
    [DataContract]
    private sealed class Summary
    {
        [DataMember] public int Size;
        [DataMember] public string Stage;
        [DataMember] public int Samples;
        [DataMember] public double MedianMilliseconds;
        [DataMember] public double MinMilliseconds;
        [DataMember] public double MaxMilliseconds;
        [DataMember] public double MedianProcessorMilliseconds;
        [DataMember] public double MedianManagedHeapDeltaBytes;
        [DataMember] public double MedianPrivateMemoryDeltaBytes;
        [DataMember] public double MedianGen0Collections;
        [DataMember] public double MedianGen1Collections;
        [DataMember] public double MedianGen2Collections;
    }
    [DataContract]
    private sealed class Report
    {
        [DataMember] public string Result;
        [DataMember] public string Scope;
        [DataMember] public string RuntimeVersion;
        [DataMember] public string RuntimeAssembly;
        [DataMember] public string OperatingSystem;
        [DataMember] public bool Is64BitProcess;
        [DataMember] public int LogicalProcessors;
        [DataMember] public int WarmupIterationsPerSize;
        [DataMember] public int MeasuredIterationsPerSize;
        [DataMember] public int[] Sizes;
        [DataMember] public string MemoryMeasurement;
        [DataMember] public string TimingBoundary;
        [DataMember] public string HashGuardState;
        [DataMember] public bool RevitStarted;
        [DataMember] public bool NativeDesktopInput;
        [DataMember] public bool ChildFixtureExecutable;
        [DataMember] public bool ForcedGarbageCollection;
        [DataMember] public List<Measurement> Measurements;
        [DataMember] public List<Summary> Summaries;
    }

    private static CivilMetric Metric(double value, string unit, string source) { return new CivilMetric { Status = "available", Value = value, Unit = unit, Source = source }; }
    private static CivilWallSnapshot Input(int count)
    {
        var input = new CivilWallSnapshot { NativeSnapshot = new CivilSnapshot { DocumentScope = "performance-synthetic-document", SourcePath = "H:\\fixture\\performance.rvt",
            Revision = 1, ModelTitle = "Synthetic independent unjoined walls", ScopeLabel = "project", PhaseLabel = "new", DesignOptionLabel = "main", CandidateCount = count, CapturedAtUtc = "2026-10-29T00:00:00Z" },
            RuleTemplate = CivilWallRuleTemplates.Default(), GeometryViewUniqueId = "synthetic-view", GeometryViewName = "Synthetic 3D", GeometryPhaseId = "synthetic-phase" };
        for (int index = 0; index < count; index++)
        {
            string id = (index + 1).ToString(CultureInfo.InvariantCulture), uid = "synthetic-wall-" + id;
            var length = Metric(10,"m","CURVE_ELEM_LENGTH"); var area = Metric(30,"m2","HOST_AREA_COMPUTED"); var volume = Metric(6,"m3","HOST_VOLUME_COMPUTED");
            var fact = new CivilWallFact { ElementId = id, UniqueId = uid, Name = "Synthetic wall " + id, TypeUniqueId = "type-" + (index % 20).ToString(CultureInfo.InvariantCulture), TypeName = "Synthetic 200mm " + (index % 20).ToString(CultureInfo.InvariantCulture), CurveKind = "Line",
                BaseElevationSource = "Level.ProjectElevation + WALL_BASE_OFFSET", BaseElevationMetres = 0, TopConstraintId = "-1", AttachedTop = false, AttachedBottom = false,
                ParameterLength = Metric(10,"m","CURVE_ELEM_LENGTH"), ParameterHeight = Metric(3,"m","WALL_USER_HEIGHT_PARAM"), ParameterWidth = Metric(.2,"m","WallType.Width"),
                NativeLength = length, NativeArea = area, NativeVolume = volume, GeometryVolumeMetres3 = 6, GeometryVerified = true, InterwallOverlapCheckComplete = true,
                OverlapCandidateCount = 0, SymmetricDifferenceMetres3 = 0, GeometryToleranceMetres3 = 1e-8, Status = "verified" };
            input.Walls.Add(fact);
            string level = "level-" + (index % 10).ToString(CultureInfo.InvariantCulture);
            input.NativeSnapshot.Elements.Add(new CivilElementRecord { ElementId = id, UniqueId = uid, Name = fact.Name, Category = "Walls", KindCode = "wall", KindName = "Wall", TypeName = fact.TypeName,
                Supported = true, LevelId = level, LevelUniqueId = level, LevelName = level, RawLevelId = level, RawLevelName = level, LevelSource = "WALL_BASE_CONSTRAINT", Length = length, Area = area, Volume = volume });
        }
        return input;
    }
    private static T Measure<T>(int size, int iteration, bool warmup, string stage, Func<T> action, IList<Measurement> measurements)
    {
        using (Process process = Process.GetCurrentProcess())
        {
            process.Refresh(); long managed = GC.GetTotalMemory(false), privateBytes = process.PrivateMemorySize64, working = process.WorkingSet64;
            TimeSpan processor = process.TotalProcessorTime; int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            Stopwatch elapsed = Stopwatch.StartNew(); T value = action(); elapsed.Stop();
            long managedAfter = GC.GetTotalMemory(false); process.Refresh();
            measurements.Add(new Measurement { Size = size, Iteration = iteration, Warmup = warmup, Stage = stage, Milliseconds = elapsed.Elapsed.TotalMilliseconds,
                ProcessorMilliseconds = (process.TotalProcessorTime - processor).TotalMilliseconds,
                ManagedHeapBeforeBytes = managed, ManagedHeapAfterBytes = managedAfter, ManagedHeapDeltaBytes = managedAfter - managed,
                PrivateMemoryBeforeBytes = privateBytes, PrivateMemoryAfterBytes = process.PrivateMemorySize64, PrivateMemoryDeltaBytes = process.PrivateMemorySize64 - privateBytes,
                WorkingSetBeforeBytes = working, WorkingSetAfterBytes = process.WorkingSet64, WorkingSetPeakSinceProcessStartBytes = process.PeakWorkingSet64,
                Gen0Collections = GC.CollectionCount(0) - gen0, Gen1Collections = GC.CollectionCount(1) - gen1, Gen2Collections = GC.CollectionCount(2) - gen2 });
            return value;
        }
    }
    private static void Trial(int size, int iteration, bool warmup, IList<Measurement> measurements)
    {
        CivilWallSnapshot input = Input(size);
        CivilWallCalculationBatch batch = Measure(size,iteration,warmup,"Calculate",() => CivilWallCalculator.Calculate(input),measurements);
        CivilWallReportData report = Measure(size,iteration,warmup,"Report.Create",() => CivilWallReport.Create(batch),measurements);
        List<CivilWallReportRow> rows = Measure(size,iteration,warmup,"Report.Filter",() => CivilWallReport.Filter(report,"","calculated","",""),measurements);
        List<CivilWallReportGroup> groups = Measure(size,iteration,warmup,"Report.Group",() => CivilWallReport.Group(rows,"type"),measurements);
        Measure(size,iteration,warmup,"Freshness.IntegrityValidateOnly",() => { CivilWallBatchIntegrity.Validate(batch); return batch.BatchId; },measurements);
        string csv = Measure(size,iteration,warmup,"Report.ToCsv",() => CivilWallReport.ToCsv(report,rows,"type"),measurements);
        if (batch.Results.Count != size || batch.Results.Any(row => row.Status != "calculated") || report.Rows.Count != size || rows.Count != size ||
            groups.Sum(group => group.WallCount) != size || groups.Sum(group => group.ResultVolumeMetres3.Value) != size * 6.0 ||
            groups.Sum(group => group.ResultAreaMetres2.Value) != size * 30.0 || csv.Length == 0)
            throw new InvalidDataException("Synthetic performance inputs did not produce their independent count/area/volume baseline.");
        string stages = String.Join(" ",measurements.Where(item => item.Size == size && item.Iteration == iteration && item.Warmup == warmup).Select(item => item.Stage + "=" + item.Milliseconds.ToString("0.0",CultureInfo.InvariantCulture) + "ms"));
        Console.WriteLine("PERF size=" + size.ToString(CultureInfo.InvariantCulture) + " " + (warmup ? "warmup" : "sample") + "=" + iteration.ToString(CultureInfo.InvariantCulture) + " " + stages);
    }
    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray(); int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : sorted[middle - 1] / 2 + sorted[middle] / 2;
    }
    private static int Main(string[] args)
    {
        string output = args[0]; int[] sizes = args[1].Split(',').Select(value => Int32.Parse(value,CultureInfo.InvariantCulture)).Distinct().OrderBy(value => value).ToArray();
        int iterations = Int32.Parse(args[2],CultureInfo.InvariantCulture), warmup = Int32.Parse(args[3],CultureInfo.InvariantCulture);
        if (sizes.Length == 0 || sizes.Any(value => value != 100 && value != 1000 && value != 10000) || iterations < 3 || iterations > 9 || warmup < 1 || warmup > 3)
            throw new ArgumentException("Use the bounded requested synthetic wall sizes and at least three measured samples.");
        var measurements = new List<Measurement>();
        foreach (int size in sizes)
        {
            for (int index = 1; index <= warmup; index++) Trial(size,index,true,measurements);
            for (int index = 1; index <= iterations; index++) Trial(size,index,false,measurements);
        }
        var summaries = measurements.Where(item => !item.Warmup).GroupBy(item => item.Size.ToString(CultureInfo.InvariantCulture) + ":" + item.Stage).Select(group => new Summary {
            Size = group.First().Size, Stage = group.First().Stage, Samples = group.Count(), MedianMilliseconds = Median(group.Select(item => item.Milliseconds)),
            MinMilliseconds = group.Min(item => item.Milliseconds), MaxMilliseconds = group.Max(item => item.Milliseconds), MedianProcessorMilliseconds = Median(group.Select(item => item.ProcessorMilliseconds)),
            MedianManagedHeapDeltaBytes = Median(group.Select(item => (double)item.ManagedHeapDeltaBytes)), MedianPrivateMemoryDeltaBytes = Median(group.Select(item => (double)item.PrivateMemoryDeltaBytes)),
            MedianGen0Collections = Median(group.Select(item => (double)item.Gen0Collections)), MedianGen1Collections = Median(group.Select(item => (double)item.Gen1Collections)), MedianGen2Collections = Median(group.Select(item => (double)item.Gen2Collections))
        }).OrderBy(item => item.Size).ThenBy(item => item.Stage,StringComparer.Ordinal).ToList();
        var report = new Report { Result = "PASS", Scope = "Synthetic SI facts/native records for independent unjoined no-opening walls; own managed calculation/report/integrity only. No Revit API, native geometry, model I/O, WPF, desktop, export or customer-model speed measurement.",
            RuntimeVersion = Environment.Version.ToString(), RuntimeAssembly = typeof(Object).Assembly.FullName, OperatingSystem = Environment.OSVersion.VersionString, Is64BitProcess = Environment.Is64BitProcess, LogicalProcessors = Environment.ProcessorCount,
            WarmupIterationsPerSize = warmup, MeasuredIterationsPerSize = iterations, Sizes = sizes,
            MemoryMeasurement = "Before/after GC.GetTotalMemory(false), process private bytes and working set are approximate retained/live heap observations, not cumulative allocations or stage peak. Deltas can be negative after GC. Working-set peak is the process lifetime high-water mark. GC counts are recorded; no forced GC.",
            TimingBoundary = "Input construction, memory sampling, assertions and console output excluded from each stage stopwatch. Each trial uses fresh facts; warmup samples are preserved but excluded from medians. Freshness measures Integrity.Validate only, not the UI timer/key/document checks. Shared-machine timings are diagnostic, not a performance SLA.",
            HashGuardState = "All production mutation checks remain enabled; no prevalidated paths, cached fingerprints or skipped guards.", RevitStarted = false, NativeDesktopInput = false, ChildFixtureExecutable = false, ForcedGarbageCollection = false,
            Measurements = measurements, Summaries = summaries };
        using (var file = new FileStream(Path.Combine(output,"report.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            new DataContractJsonSerializer(typeof(Report)).WriteObject(file,report);
        Console.WriteLine("PASS synthetic wall performance diagnostic: " + summaries.Count.ToString(CultureInfo.InvariantCulture) + " stage medians, " + measurements.Count.ToString(CultureInfo.InvariantCulture) + " raw samples including warmup.");
        return 0;
    }
}
