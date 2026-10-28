using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BrightVision.Civil;

internal static class CivilWallCalculationFixture
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Near(double? actual, double expected, string message) { Check(actual.HasValue && Math.Abs(actual.Value - expected) < 1e-9, message); }
    private static CivilMetric Length(double value, string source) { return new CivilMetric { Status = "available", Unit = "m", Value = value, Source = source }; }
    private static CivilWallFact Wall()
    {
        return new CivilWallFact { ElementId = "10", UniqueId = "wall-uid", TypeUniqueId = "type-uid", Name = "wall", Status = "verified", GeometryVerified = true, InterwallOverlapCheckComplete = true,
            ParameterLength = Length(10, "LocationCurve.Curve.Length"), ParameterHeight = Length(3, "WALL_USER_HEIGHT_PARAM"), ParameterWidth = Length(0.2, "WallType.Width"),
            GeometryVolumeMetres3 = 6, SymmetricDifferenceMetres3 = 0, GeometryToleranceMetres3 = 1e-8,
            NativeVolume = new CivilMetric { Status = "available", Unit = "m3", Value = 6, Source = "HOST_VOLUME_COMPUTED" } };
    }
    private static CivilWallInsert Hole(string id, double x0, double x1, double z0, double z1)
    {
        return new CivilWallInsert { ElementId = id, UniqueId = id, HostUniqueId = "wall-uid", Kind = "rect-opening", Status = "recognized", Source = "Opening.BoundaryRect",
            RectX0 = x0, RectX1 = x1, RectZ0 = z0, RectZ1 = z1 };
    }
    private static CivilWallResult Calculate(CivilWallFact wall) { return CivilWallCalculator.CalculateWall(wall); }
    private static void Refused(CivilWallFact wall, string reason)
    {
        CivilWallResult result = Calculate(wall);
        Check(result.Status != "calculated" && !result.ResultVolumeMetres3.HasValue && result.Ledger.Count == 0, reason);
    }
    private static double IndependentArea(List<CivilWallInsert> holes)
    {
        // Independent exact cell coverage on all boundary coordinates. The
        // production sweep/merged-interval method is not called or copied.
        double[] xs = holes.SelectMany(h => new[] { Math.Max(0, Math.Min(10, h.RectX0.Value)), Math.Max(0, Math.Min(10, h.RectX1.Value)) }).Concat(new[] { 0.0, 10.0 }).Distinct().OrderBy(v => v).ToArray();
        double[] zs = holes.SelectMany(h => new[] { Math.Max(0, Math.Min(3, h.RectZ0.Value)), Math.Max(0, Math.Min(3, h.RectZ1.Value)) }).Concat(new[] { 0.0, 3.0 }).Distinct().OrderBy(v => v).ToArray();
        double area = 0;
        for (int x = 1; x < xs.Length; x++) for (int z = 1; z < zs.Length; z++)
        {
            double midX = (xs[x - 1] + xs[x]) / 2, midZ = (zs[z - 1] + zs[z]) / 2;
            if (holes.Any(h => midX > h.RectX0 && midX < h.RectX1 && midZ > h.RectZ0 && midZ < h.RectZ1))
                area += (xs[x] - xs[x - 1]) * (zs[z] - zs[z - 1]);
        }
        return area;
    }
    private static int Main(string[] args)
    {
        string output = args[0]; Directory.CreateDirectory(output);
        CivilWallFact wall = Wall(); CivilWallResult result = Calculate(wall);
        Check(result.Status == "calculated", "An independently verified simple wall must calculate."); Near(result.ResultVolumeMetres3, 6, "No-opening basis.");
        Check(result.Ledger.Count == 1 && result.Ledger[0].Kind == "basis", "No invented deduction.");
        wall.Inserts.Add(Hole("A", 1, 3, 0.5, 1.5)); wall.GeometryVolumeMetres3 = 5.6; wall.NativeVolume.Value = 123;
        result = Calculate(wall); Near(result.OpeningUnionAreaMetres2, 2, "One opening area."); Near(result.ResultVolumeMetres3, 5.6, "Native 123 is never the subtraction basis.");
        Near(wall.NativeVolume.Value, 123, "Native metric must remain unchanged."); Check(result.Ledger.Count == 2, "One base plus one opening entry.");
        wall.Inserts.Add(Hole("B", 2, 4, 0.5, 1.5)); wall.GeometryVolumeMetres3 = 5.4;
        result = Calculate(wall); Near(result.OpeningUnionAreaMetres2, 3, "Overlap union, not a sum."); Near(result.ResultVolumeMetres3, 5.4, "Overlapping openings deducted once.");
        Near(result.Ledger[2].AreaMetres2, 1, "Second opening only contributes the new area."); Near(result.Ledger.Sum(e => e.DeltaVolumeMetres3), 5.4, "Ledger conserves final volume.");
        wall.Inserts.Add(Hole("C", 1, 3, 0.5, 1.5)); result = Calculate(wall);
        Near(result.Ledger[3].DeltaVolumeMetres3, 0, "Coincident different opening has no duplicate deduction.");
        wall.Inserts.Reverse(); result = Calculate(wall); Check(result.Ledger[1].UniqueId == "A" && result.Ledger[3].UniqueId == "C", "Stable identity order.");
        wall = Wall(); wall.Inserts.Add(Hole("edge", -1, 1, -1, 1)); wall.GeometryVolumeMetres3 = 5.8;
        result = Calculate(wall); Near(result.OpeningUnionAreaMetres2, 1, "Opening clips to wall X/Z bounds.");
        wall.Inserts.Add(Hole("outside", 11, 12, 0, 1)); result = Calculate(wall); Near(result.ResultVolumeMetres3, 5.8, "Outside rectangle contributes zero.");
        wall = Wall(); wall.Inserts.Add(Hole("all", -1, 11, -1, 4)); wall.GeometryVolumeMetres3 = 0;
        result = Calculate(wall); Near(result.ResultVolumeMetres3, 0, "Full-domain clipping preserves legitimate zero.");
        wall = Wall(); wall.GeometryVerified = false; Refused(wall, "No geometry proof means no output.");
        wall = Wall(); wall.InterwallOverlapCheckComplete = false; Refused(wall, "Incomplete wall intersection evidence means no output.");
        wall = Wall(); wall.IntersectingWallElementIds.Add("other-wall"); Refused(wall, "Physically overlapping different walls cannot both count.");
        wall = Wall(); wall.OverlapCandidateCount = 65; Refused(wall, "A completed proof cannot claim an out-of-range candidate count.");
        wall = Wall(); wall.OverlapCandidateCount = -1; Refused(wall, "Negative overlap candidate counts refused.");
        wall = Wall(); wall.SymmetricDifferenceMetres3 = 0.01; Refused(wall, "Geometry mismatch refused.");
        wall = Wall(); wall.GeometryToleranceMetres3 = 1; Refused(wall, "Arbitrary tolerance refused.");
        wall = Wall(); wall.ParameterWidth.Value = Double.NaN; Refused(wall, "NaN refused.");
        wall = Wall(); wall.ParameterLength.Source = ""; Refused(wall, "Unproven input source refused.");
        wall = Wall(); wall.ParameterHeight.Unit = "ft"; Refused(wall, "Dimension mismatch refused.");
        wall = Wall(); wall.ParameterWidth.Value = Double.MaxValue; Refused(wall, "Overflow refused.");
        wall = Wall(); wall.Inserts.Add(new CivilWallInsert { Kind = "door", Status = "unsupported" }); Refused(wall, "Nominal door sizes are not cut proof.");
        wall = Wall(); wall.Inserts.Add(Hole("a", 0, 1, 0, 1)); wall.Inserts[0].HostUniqueId = "other"; Refused(wall, "Foreign host refused.");
        wall = Wall(); wall.Inserts.Add(Hole("a", 0, 1, 0, 1)); wall.Inserts.Add(Hole("a", 2, 3, 0, 1)); Refused(wall, "Duplicate insert identity refused.");
        wall = Wall(); wall.Inserts.Add(Hole("a", 0, 0, 0, 1)); Refused(wall, "Degenerate rectangle refused.");
        wall = Wall(); wall.GeometryVolumeMetres3 = 5; Refused(wall, "Geometry proof inconsistent with domain rectangle arithmetic refused.");
        wall = Wall(); wall.Status = "failed"; Check(Calculate(wall).Status == "failed", "Read failure remains distinct.");
        wall = Wall(); wall.Inserts.Add(new CivilWallInsert { Status = "excluded" }); Near(Calculate(wall).ResultVolumeMetres3, 6, "Explicitly excluded insert not subtracted.");
        var random = new Random(572091);
        for (int sample = 0; sample < 60; sample++)
        {
            wall = Wall();
            for (int n = 0; n < 8; n++) { double x = random.Next(-4, 22) / 2.0, z = random.Next(-2, 8) / 2.0; wall.Inserts.Add(Hole("H" + n, x, x + random.Next(1, 7) / 2.0, z, z + random.Next(1, 5) / 2.0)); }
            double area = IndependentArea(wall.Inserts); wall.GeometryVolumeMetres3 = 6 - area * 0.2;
            result = Calculate(wall); Check(result.Status == "calculated", "Generated exact-cell oracle should agree.");
            Near(result.OpeningUnionAreaMetres2, area, "Independent 2D cell coverage matches rectangle sweep."); Near(result.Ledger.Sum(e => e.DeltaVolumeMetres3), 6 - area * 0.2, "Generated ledger conservation.");
        }
        wall = Wall(); wall.Name = "=CMD(\"unsafe\")"; var snapshot = new CivilWallSnapshot { NativeSnapshot = new CivilSnapshot { ModelTitle = "@input", DocumentScope = "doc", SourcePath = "H:\\sample.rvt" }, Walls = new List<CivilWallFact> { wall } };
        var batch = CivilWallCalculator.Calculate(snapshot); string csv = CivilWallCalculator.ToCsv(batch, null);
        Check(csv.Contains("\"'=CMD(\"\"unsafe\"\")\""), "CSV formula text escaped and quoted."); Check(csv.Contains("\"'@input\""), "Metadata formula text protected.");
        Check(csv.Contains(CivilWallCalculator.RuleId) && csv.Contains("HOST_VOLUME_COMPUTED") && !batch.Results[0].Ledger.Any(e => e.Formula.Contains("HOST_VOLUME_COMPUTED")), "Native source is preserved without entering the calculation formula.");
        Check(!csv.Contains("NaN") && !csv.Contains("Infinity"), "No nonfinite numeric CSV values.");
        File.WriteAllText(Path.Combine(output, "wall-calculation.csv"), csv, new System.Text.UTF8Encoding(true));
        bool rejected = false; snapshot.Walls.Add(wall); try { CivilWallCalculator.Calculate(snapshot); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Repeated wall source refuses whole batch.");
        File.WriteAllText(Path.Combine(output, "report.json"), "{\"result\":\"PASS\",\"assertions\":" + checks + ",\"scope\":\"Pure project-owned DLL; independent rectangle cell oracle; no Revit or UI\"}");
        Console.WriteLine("PASS " + checks + " wall calculation assertions."); return 0;
    }
}
