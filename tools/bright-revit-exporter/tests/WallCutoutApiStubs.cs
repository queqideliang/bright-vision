// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

// Project-owned contract stand-ins only. No Autodesk binary or native geometry
// engine is loaded by this fixture, and no Boolean operation is simulated.
namespace Autodesk.Revit.DB
{
    internal enum BuiltInCategory { OST_Doors = -2000014, OST_Windows = -2000016 }
    internal sealed class ElementId
    {
        internal readonly long Value;
        internal int IntegerValue { get { return checked((int)Value); } }
        internal ElementId(long value) { Value = value; }
        public override bool Equals(object value) { return value is ElementId && ((ElementId)value).Value == Value; }
        public override int GetHashCode() { return Value.GetHashCode(); }
    }
    internal sealed class Category { internal ElementId Id; }
    internal class Element
    {
        internal Document Document;
        internal ElementId Id;
        internal string UniqueId, Name = "fixture element";
        internal bool IsValidObject = true;
        internal Category Category;
    }
    internal sealed class Wall : Element { }
    internal sealed class FamilyInstance : Element
    {
        internal Element Host;
        internal static int NominalReads;
        internal double NominalWidthValue = 1234, NominalHeightValue = 0.25;
        internal double NominalWidth { get { NominalReads++; throw new Exception("Nominal width must never be read."); } }
        internal double NominalHeight { get { NominalReads++; throw new Exception("Nominal height must never be read."); } }
        internal object get_Parameter(object value) { NominalReads++; throw new Exception("Nominal parameter must never be read."); }
    }
    internal sealed class Document
    {
        internal bool IsValidObject = true, IsFamilyDocument;
        internal readonly Dictionary<long, Element> Elements = new Dictionary<long, Element>();
        internal Element GetElement(ElementId id) { Element item; return id != null && Elements.TryGetValue(id.Value, out item) ? item : null; }
        internal T Add<T>(T item, long id) where T : Element
        {
            item.Document = this; item.Id = new ElementId(id); item.UniqueId = "fixture-" + id; Elements.Add(id, item); return item;
        }
    }
    internal sealed class XYZ
    {
        internal readonly double X, Y, Z;
        internal XYZ(double x, double y, double z) { X = x; Y = y; Z = z; }
        internal double DotProduct(XYZ value) { return X * value.X + Y * value.Y + Z * value.Z; }
        public static XYZ operator -(XYZ a, XYZ b) { return new XYZ(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    }
    internal abstract class Curve : IDisposable
    {
        internal static int DisposeCalls, TessellateCalls;
        internal abstract XYZ GetEndPoint(int index);
        internal virtual IList<XYZ> Tessellate() { TessellateCalls++; throw new Exception("Tessellation is not cut geometry."); }
        public void Dispose() { DisposeCalls++; }
    }
    internal sealed class Line : Curve
    {
        internal XYZ A, B;
        internal bool IsBound = true;
        internal static Line CreateBound(XYZ a, XYZ b) { return new Line { A = a, B = b }; }
        internal override XYZ GetEndPoint(int index) { return index == 0 ? A : B; }
    }
    internal sealed class Arc : Curve
    {
        internal override XYZ GetEndPoint(int index) { return new XYZ(index, 0, 0); }
    }
    internal sealed class CurveLoop : List<Curve>, IDisposable
    {
        internal int DisposeCalls;
        public void Dispose() { DisposeCalls++; }
    }
}
