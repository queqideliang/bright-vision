using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using BrightVision.Export;

namespace BrightVision.Revit
{
    internal static class RevitQuantityReader
    {
        internal static Dictionary<string, object> Capture(Element element, string elementId, string documentScope, string linkInstanceScope)
        {
            if (element == null) throw new ArgumentNullException("element");
            return QuantitySnapshot.Create(element.UniqueId, elementId, documentScope, linkInstanceScope,
                Read(element, BuiltInParameter.CURVE_ELEM_LENGTH, 1),
                Read(element, BuiltInParameter.HOST_AREA_COMPUTED, 2),
                Read(element, BuiltInParameter.HOST_VOLUME_COMPUTED, 3));
        }

        private static QuantityMetric Read(Element element, BuiltInParameter builtIn, int dimension)
        {
            try
            {
                // Fixed instance built-ins only: no localized-name lookup,
                // type-parameter fallback, or displayed/triangulated geometry.
                Parameter parameter = element.get_Parameter(builtIn);
                if (parameter == null || !parameter.HasValue) return QuantitySnapshot.Missing();
                if (parameter.StorageType != StorageType.Double) return QuantitySnapshot.Invalid();
                return QuantitySnapshot.FromInternalValue(parameter.AsDouble(), dimension);
            }
            catch (Exception error)
            {
                // Host parameter failures should leave an explicit unavailable
                // cell, while cancellation and process exhaustion remain fatal.
                if (error is OperationCanceledException || error is OutOfMemoryException) throw;
                return QuantitySnapshot.Unavailable();
            }
        }
    }
}
