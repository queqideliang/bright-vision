using System;
using System.Collections.Generic;

namespace Autodesk.Revit.DB
{
    // Separate test-only types. No Autodesk assembly is loaded by the fixture.
    internal enum BuiltInParameter { CURVE_ELEM_LENGTH, HOST_AREA_COMPUTED, HOST_VOLUME_COMPUTED }
    internal enum StorageType { None, Integer, Double, String, ElementId }
    internal sealed class Parameter
    {
        internal bool Present = true;
        internal StorageType Type = StorageType.Double;
        internal double Number;
        internal Exception Error;
        internal int ValueReads;
        internal bool HasValue { get { return Present; } }
        internal StorageType StorageType { get { return Type; } }
        internal double AsDouble() { ValueReads++; if (Error != null) throw Error; return Number; }
    }
    internal sealed class Element
    {
        internal string UniqueId = "fixture-unique-id";
        internal readonly Dictionary<BuiltInParameter, Parameter> Values = new Dictionary<BuiltInParameter, Parameter>();
        internal readonly List<BuiltInParameter> Reads = new List<BuiltInParameter>();
        internal Exception Error;
        internal Parameter get_Parameter(BuiltInParameter key)
        {
            Reads.Add(key); if (Error != null) throw Error;
            Parameter value; return Values.TryGetValue(key, out value) ? value : null;
        }
    }
}
