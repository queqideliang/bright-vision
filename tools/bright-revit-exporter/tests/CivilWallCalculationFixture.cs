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
        Check(result.Status != "calculated" && !result.ResultVolumeMetres3.HasValue &&
            !result.BasisAreaMetres2.HasValue && !result.ResultAreaMetres2.HasValue &&
            !result.OpeningUnionAreaMetres2.HasValue && result.Ledger.Count == 0, reason);
    }
    private static void Area(CivilWallResult result, double expected, string message)
    {
        Near(result.ResultAreaMetres2, expected, message);
        Near(result.Ledger.Sum(entry => entry.DeltaAreaMetres2), expected, message + " Signed area ledger conservation.");
    }
    private static List<List<string>> CsvRows(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new System.Text.StringBuilder(); bool quoted = false;
        for (int at = 0; at < text.Length; at++)
        {
            char c = text[at];
            if (c == '"')
            {
                if (quoted && at + 1 < text.Length && text[at + 1] == '"') { cell.Append('"'); at++; }
                else quoted = !quoted;
            }
            else if (!quoted && (c == ',' || c == '\r' || c == '\n'))
            {
                row.Add(cell.ToString()); cell.Length = 0;
                if (c != ',') { rows.Add(row); row = new List<string>(); if (c == '\r' && at + 1 < text.Length && text[at + 1] == '\n') at++; }
            }
            else cell.Append(c);
        }
        if (quoted) throw new Exception("Unterminated CSV quoted field.");
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
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
        Near(result.BasisAreaMetres2, 30, "Single-sided projected basis is length times height, without two-side or thickness factors.");
        Area(result, 30, "No-opening projected area.");
        Check(CivilWallCalculator.AreaRuleId == "BV.Wall.AxisProjection/1" && CivilWallCalculator.RuleId == "BV.Wall.RectSolid/1", "Area and original volume methods have separate stable identifiers.");
        Check(result.Ledger[0].AreaFormula == "10 × 3", "Basis ledger retains an independent area formula.");
        Check(result.Ledger.Count == 1 && result.Ledger[0].Kind == "basis", "No invented deduction.");
        wall.Inserts.Add(Hole("A", 1, 3, 0.5, 1.5)); wall.GeometryVolumeMetres3 = 5.6; wall.NativeVolume.Value = 123;
        result = Calculate(wall); Near(result.OpeningUnionAreaMetres2, 2, "One opening area."); Near(result.ResultVolumeMetres3, 5.6, "Native 123 is never the subtraction basis.");
        Area(result, 28, "One opening projected area."); Near(result.Ledger[1].DeltaAreaMetres2, -2, "Opening ledger area is a signed deduction.");
        Check(result.Ledger[1].AreaFormula == "−(2 − 0)" && result.Ledger[1].Formula == "(2 − 0) × 0.2", "Area and volume formulas retain their own dimensions.");
        Near(wall.NativeVolume.Value, 123, "Native metric must remain unchanged."); Check(result.Ledger.Count == 2, "One base plus one opening entry.");
        wall.Inserts.Add(Hole("B", 2, 4, 0.5, 1.5)); wall.GeometryVolumeMetres3 = 5.4;
        result = Calculate(wall); Near(result.OpeningUnionAreaMetres2, 3, "Overlap union, not a sum."); Near(result.ResultVolumeMetres3, 5.4, "Overlapping openings deducted once.");
        Area(result, 27, "Two overlapping openings projected area."); Near(result.Ledger[2].DeltaAreaMetres2, -1, "Overlapping opening only deducts new projected area.");
        Near(result.Ledger[2].AreaMetres2, 1, "Second opening only contributes the new area."); Near(result.Ledger.Sum(e => e.DeltaVolumeMetres3), 5.4, "Ledger conserves final volume.");
        wall.Inserts.Add(Hole("C", 1, 3, 0.5, 1.5)); result = Calculate(wall);
        Near(result.Ledger[3].DeltaVolumeMetres3, 0, "Coincident different opening has no duplicate deduction.");
        Area(result, 27, "Coincident opening does not reduce net area twice."); Near(result.Ledger[3].DeltaAreaMetres2, 0, "Coincident opening has zero incremental signed area.");
        string areaLedgerOrder = String.Join("|", result.Ledger.Select(entry => entry.UniqueId + ":" + entry.DeltaAreaMetres2.ToString("R", CultureInfo.InvariantCulture) + ":" + entry.AreaFormula));
        wall.Inserts.Reverse(); result = Calculate(wall); Check(result.Ledger[1].UniqueId == "A" && result.Ledger[3].UniqueId == "C", "Stable identity order.");
        Check(areaLedgerOrder == String.Join("|", result.Ledger.Select(entry => entry.UniqueId + ":" + entry.DeltaAreaMetres2.ToString("R", CultureInfo.InvariantCulture) + ":" + entry.AreaFormula)), "Area ledger and formulas do not depend on insert input order.");
        Area(result, 27, "Reordered openings retain the same projected area.");
        CivilMetric nativeArea = new CivilMetric { Status = "available", Unit = "m2", Value = 98765, Source = "HOST_AREA_COMPUTED" };
        wall.NativeArea = nativeArea; result = Calculate(wall); Area(result, 27, "Native area does not enter project area subtraction.");
        Check(Object.ReferenceEquals(wall.NativeArea, nativeArea) && nativeArea.Status == "available" && nativeArea.Source == "HOST_AREA_COMPUTED" && nativeArea.Value == 98765, "Original native area reference, status, source and value stay unchanged.");
        nativeArea.Status = "missing"; nativeArea.Value = null; result = Calculate(wall); Area(result, 27, "Missing native area never becomes a zero or alternate computation basis.");
        wall = Wall(); wall.Inserts.Add(Hole("edge", -1, 1, -1, 1)); wall.GeometryVolumeMetres3 = 5.8;
        result = Calculate(wall); Near(result.OpeningUnionAreaMetres2, 1, "Opening clips to wall X/Z bounds.");
        Area(result, 29, "Only the in-domain projected part of a partial opening is deducted.");
        wall.Inserts.Add(Hole("outside", 11, 12, 0, 1)); result = Calculate(wall); Near(result.ResultVolumeMetres3, 5.8, "Outside rectangle contributes zero.");
        Area(result, 29, "Outside opening has no projected area deduction."); Near(result.Ledger.Single(entry => entry.UniqueId == "outside").DeltaAreaMetres2, 0, "Outside opening remains a zero-change source ledger entry.");
        wall = Wall(); wall.Inserts.Add(Hole("all", -1, 11, -1, 4)); wall.GeometryVolumeMetres3 = 0;
        result = Calculate(wall); Near(result.ResultVolumeMetres3, 0, "Full-domain clipping preserves legitimate zero.");
        Area(result, 0, "Full-domain clipping produces an available legitimate zero projected area."); Near(result.BasisAreaMetres2, 30, "Fully removed wall retains its projected basis.");
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
        wall = Wall(); wall.ParameterLength.Value = Double.MaxValue; Refused(wall, "Projected area overflow refuses both area and volume results.");
        wall = Wall(); wall.Inserts.Add(Hole("bad-point", 0, Double.NaN, 0, 1)); Refused(wall, "Nonfinite opening coordinate refuses an area result and clears the partially assembled ledger.");
        wall = Wall(); wall.Inserts.Add(Hole("bad-point", 0, 1, Double.NegativeInfinity, 1)); Refused(wall, "Infinite opening coordinate is not a clipped finite area.");
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
            Area(result, 30 - area, "Independent covered cells give the net projected area.");
            Check(result.Ledger.All(entry => !Double.IsNaN(entry.DeltaAreaMetres2) && !Double.IsInfinity(entry.DeltaAreaMetres2) && !String.IsNullOrWhiteSpace(entry.AreaFormula)), "Every projected area change is finite and retains its area formula.");
        }
        wall = Wall(); wall.Name = "=CMD(\"unsafe\")"; var snapshot = new CivilWallSnapshot { NativeSnapshot = new CivilSnapshot { ModelTitle = "@input", DocumentScope = "doc", SourcePath = "H:\\sample.rvt" }, Walls = new List<CivilWallFact> { wall } };
        var batch = CivilWallCalculator.Calculate(snapshot); string csv = CivilWallCalculator.ToCsv(batch, null);
        Check(csv.Contains("\"'=CMD(\"\"unsafe\"\")\""), "CSV formula text escaped and quoted."); Check(csv.Contains("\"'@input\""), "Metadata formula text protected.");
        Check(csv.Contains(CivilWallCalculator.RuleId) && csv.Contains("HOST_VOLUME_COMPUTED") && !batch.Results[0].Ledger.Any(e => e.Formula.Contains("HOST_VOLUME_COMPUTED")), "Native source is preserved without entering the calculation formula.");
        Check(!csv.Contains("NaN") && !csv.Contains("Infinity"), "No nonfinite numeric CSV values.");
        List<List<string>> legacyRows = CsvRows(csv);
        Check(legacyRows.Single(row => row.Count > 0 && row[0] == "墙ElementId" && row.Contains("状态")).Count == 25, "Legacy summary CSV remains a 25-column contract.");
        Check(legacyRows.Single(row => row.Count > 0 && row[0] == "墙ElementId" && row.Contains("步骤")).Count == 11, "Legacy ledger CSV remains an 11-column contract.");
        File.WriteAllText(Path.Combine(output, "wall-calculation.csv"), csv, new System.Text.UTF8Encoding(true));
        bool rejected = false; snapshot.Walls.Add(wall); try { CivilWallCalculator.Calculate(snapshot); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Repeated wall source refuses whole batch.");
        File.WriteAllText(Path.Combine(output, "report.json"), "{\"result\":\"PASS\",\"assertions\":" + checks + ",\"scope\":\"Pure project-owned DLL; independent rectangle cell oracle; no Revit or UI\"}");
        Console.WriteLine("PASS " + checks + " wall calculation assertions."); return 0;
    }
}
