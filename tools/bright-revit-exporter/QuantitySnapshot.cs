using System;
using System.Collections.Generic;

namespace BrightVision.Export
{
    // Pure unit/status logic, kept independent of the Revit host so the data
    // contract and dimension powers can be verified without loading Revit.
    internal sealed class QuantityMetric
    {
        internal readonly string Status;
        internal readonly double? Value;
        internal QuantityMetric(string status, double? value) { Status = status; Value = value; }
    }

    internal static class QuantitySnapshot
    {
        private const double MetresPerFoot = 0.3048;

        internal static QuantityMetric Missing() { return new QuantityMetric("missing", null); }
        internal static QuantityMetric Invalid() { return new QuantityMetric("invalid", null); }
        internal static QuantityMetric Unavailable() { return new QuantityMetric("unavailable", null); }

        internal static QuantityMetric FromInternalValue(double value, int dimension)
        {
            if (dimension < 1 || dimension > 3) throw new ArgumentOutOfRangeException("dimension");
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0) return Invalid();
            double scale = MetresPerFoot;
            for (int power = 1; power < dimension; power++) scale *= MetresPerFoot;
            double converted = value * scale;
            if (Double.IsNaN(converted) || Double.IsInfinity(converted)) return Invalid();
            return new QuantityMetric("available", converted == 0 ? 0 : converted);
        }

        internal static Dictionary<string, object> Create(string uniqueId, string elementId, string documentScope, string linkInstanceScope,
            QuantityMetric length, QuantityMetric area, QuantityMetric volume)
        {
            return PackageWriter.Fields("schemaVersion", 1, "source", "revit-parameters",
                "sourceElement", PackageWriter.Fields("uniqueId", uniqueId, "elementId", elementId,
                    "documentScope", documentScope, "linkInstanceScope", linkInstanceScope),
                "metrics", PackageWriter.Fields("length", Metric(length, "m", "CURVE_ELEM_LENGTH"),
                    "area", Metric(area, "m2", "HOST_AREA_COMPUTED"), "volume", Metric(volume, "m3", "HOST_VOLUME_COMPUTED")));
        }

        private static Dictionary<string, object> Metric(QuantityMetric metric, string unit, string parameter)
        {
            if (metric == null) throw new ArgumentNullException("metric");
            return PackageWriter.Fields("status", metric.Status, "value", metric.Value.HasValue ? (object)metric.Value.Value : null,
                "unit", unit, "parameter", parameter);
        }
    }
}
