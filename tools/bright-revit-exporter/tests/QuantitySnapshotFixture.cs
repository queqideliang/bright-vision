using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using BrightVision.Export;
using BrightVision.Revit;

internal static class QuantitySnapshotFixture
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static Dictionary<string, object> Metric(Dictionary<string, object> snapshot, string key)
    { return (Dictionary<string, object>)((Dictionary<string, object>)snapshot["metrics"])[key]; }
    private static Dictionary<string, object> Capture(Element element)
    { return RevitQuantityReader.Capture(element, "123", "document-a", "link-placement-a"); }
    private static void Status(Dictionary<string, object> metric, string expected)
    { Check((string)metric["status"] == expected && metric["value"] == null, "Incorrect missing/error state: " + expected); }

    private static int Main()
    {
        try
        {
            double[] expected = { 0.3048, 0.09290304, 0.028316846592 };
            for (int power = 1; power <= 3; power++)
            {
                QuantityMetric value = QuantitySnapshot.FromInternalValue(1, power);
                Check(value.Status == "available" && Math.Abs(value.Value.Value - expected[power - 1]) < 1e-15, "Dimension power was not preserved.");
                QuantityMetric zero = QuantitySnapshot.FromInternalValue(0, power);
                Check(zero.Status == "available" && zero.Value == 0, "Zero became a missing quantity.");
                foreach (double invalid in new double[] { -0.1, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
                {
                    QuantityMetric rejected = QuantitySnapshot.FromInternalValue(invalid, power);
                    Check(rejected.Status == "invalid" && !rejected.Value.HasValue, "Invalid value was treated as a quantity.");
                }
            }
            bool dimensionRejected = false;
            try { QuantitySnapshot.FromInternalValue(1, 4); } catch (ArgumentOutOfRangeException) { dimensionRejected = true; }
            Check(dimensionRejected, "Unknown quantity dimension was accepted.");

            Element element = new Element();
            Parameter length = new Parameter { Number = 10 };
            Parameter area = new Parameter { Number = 100 };
            Parameter volume = new Parameter { Number = 0 };
            element.Values.Add(BuiltInParameter.CURVE_ELEM_LENGTH, length);
            element.Values.Add(BuiltInParameter.HOST_AREA_COMPUTED, area);
            element.Values.Add(BuiltInParameter.HOST_VOLUME_COMPUTED, volume);
            Dictionary<string, object> snapshot = Capture(element);
            Check((int)snapshot["schemaVersion"] == 1 && (string)snapshot["source"] == "revit-parameters", "Snapshot contract identity failed.");
            Dictionary<string, object> source = (Dictionary<string, object>)snapshot["sourceElement"];
            Check((string)source["uniqueId"] == element.UniqueId && (string)source["elementId"] == "123" && (string)source["documentScope"] == "document-a" && (string)source["linkInstanceScope"] == "link-placement-a", "Source identity was changed.");
            Check(element.Reads.Count == 3 && element.Reads[0] == BuiltInParameter.CURVE_ELEM_LENGTH && element.Reads[1] == BuiltInParameter.HOST_AREA_COMPUTED && element.Reads[2] == BuiltInParameter.HOST_VOLUME_COMPUTED, "Reader did not request exactly the fixed instance built-ins.");
            Check(length.ValueReads == 1 && area.ValueReads == 1 && volume.ValueReads == 1, "Reader repeated or omitted numeric parameter reads.");
            Check((string)Metric(snapshot, "length")["unit"] == "m" && (string)Metric(snapshot, "area")["unit"] == "m2" && (string)Metric(snapshot, "volume")["unit"] == "m3", "SI unit declarations failed.");
            Check((string)Metric(snapshot, "length")["parameter"] == "CURVE_ELEM_LENGTH" && (string)Metric(snapshot, "area")["parameter"] == "HOST_AREA_COMPUTED" && (string)Metric(snapshot, "volume")["parameter"] == "HOST_VOLUME_COMPUTED", "Built-in provenance was lost.");
            Check(Math.Abs((double)Metric(snapshot, "length")["value"] - 3.048) < 1e-14 && Math.Abs((double)Metric(snapshot, "area")["value"] - 9.290304) < 1e-13 && (double)Metric(snapshot, "volume")["value"] == 0, "Reader converted parameters incorrectly.");

            Element absent = new Element();
            Dictionary<string, object> missing = Capture(absent);
            foreach (string key in new string[] { "length", "area", "volume" }) Status(Metric(missing, key), "missing");
            Parameter noValue = new Parameter { Present = false, Number = 900 };
            absent.Values.Add(BuiltInParameter.CURVE_ELEM_LENGTH, noValue);
            Status(Metric(Capture(absent), "length"), "missing");
            Check(noValue.ValueReads == 0, "A parameter without value was still read.");
            absent.Values[BuiltInParameter.CURVE_ELEM_LENGTH] = new Parameter { Type = StorageType.Integer, Number = 10 };
            Status(Metric(Capture(absent), "length"), "invalid");
            absent.Values[BuiltInParameter.CURVE_ELEM_LENGTH] = new Parameter { Error = new InvalidOperationException("host read failed") };
            Status(Metric(Capture(absent), "length"), "unavailable");
            absent.Error = new InvalidOperationException("lookup failed");
            Dictionary<string, object> failedLookup = Capture(absent);
            foreach (string key in new string[] { "length", "area", "volume" }) Status(Metric(failedLookup, key), "unavailable");
            absent.Error = new OperationCanceledException();
            bool cancelled = false; try { Capture(absent); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancellation was hidden as a missing quantity.");
            absent.Error = new OutOfMemoryException();
            bool exhausted = false; try { Capture(absent); } catch (OutOfMemoryException) { exhausted = true; }
            Check(exhausted, "Process exhaustion was hidden as a missing quantity.");
            string json = Json.Encode(missing);
            Check(json.Contains("\"value\":null") && !json.Contains("\"value\":0"), "Missing quantity serialized as zero.");
            Console.WriteLine("PASS: " + checks + " quantity assertions; production reader with isolated API stubs, no Revit host.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
    }
}
