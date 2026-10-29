// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;

// Project-owned adapter stand-ins with an exact axis-aligned box-set oracle.
// This is not Autodesk geometry execution or evidence about its Boolean kernel.
namespace Autodesk.Revit.DB
{
    internal enum BuiltInParameter { VIEW_PHASE, CURVE_ELEM_LENGTH, WALL_USER_HEIGHT_PARAM, WALL_TOP_IS_ATTACHED, WALL_BOTTOM_IS_ATTACHED, WALL_HEIGHT_TYPE, WALL_BASE_CONSTRAINT, WALL_BASE_OFFSET,HOST_AREA_COMPUTED,HOST_VOLUME_COMPUTED }
    internal enum BuiltInCategory { OST_Doors=-1, OST_Windows=-2 }
    internal enum StorageType { None, Integer, Double, ElementId }
    internal enum ElementOnPhaseStatus { Existing,New,Demolished,Future }
    internal enum WallKind { Basic,Curtain,Stacked }
    internal enum BooleanOperationsType { Union,Difference,Intersect }
    internal sealed class ElementId
    {
        internal int IntegerValue;internal long Value {get{return IntegerValue;}}
        internal ElementId(int value){IntegerValue=value;}
        public override bool Equals(object other){return other is ElementId&&((ElementId)other).IntegerValue==IntegerValue;}
        public override int GetHashCode(){return IntegerValue;}
    }
    internal sealed class Parameter
    {
        internal bool HasValue=true;internal StorageType StorageType;internal double Number;internal int Integer;internal ElementId Id;
        internal double AsDouble(){return Number;}internal int AsInteger(){return Integer;}internal ElementId AsElementId(){return Id;}
    }
    internal sealed class Category{internal ElementId Id;}
    internal class Element
    {
        internal Document Document;internal ElementId Id;internal string UniqueId,Name="element";internal bool IsValidObject=true;
        internal Category Category;internal DesignOption DesignOption;internal ElementOnPhaseStatus PhaseStatus=ElementOnPhaseStatus.New;internal bool Phaseable=true;internal Exception PhaseError;
        internal readonly Dictionary<BuiltInParameter,Parameter> Parameters=new Dictionary<BuiltInParameter,Parameter>();
        internal readonly List<ElementId> Joined=new List<ElementId>();
        internal readonly List<ElementId> Dependents=new List<ElementId>();
#if !REVIT_2015
        public ICollection<ElementId> GetDependentElements(ElementFilter filter){return Dependents;}
#endif
        internal Parameter get_Parameter(BuiltInParameter key){Parameter p;return Parameters.TryGetValue(key,out p)?p:null;}
        internal ElementOnPhaseStatus GetPhaseStatus(ElementId phase){if(PhaseError!=null)throw PhaseError;return PhaseStatus;}
        internal bool HasPhases(){return Phaseable;}
    }
    internal sealed class DesignOption:Element{}
    internal class ElementFilter{}
    internal sealed class Sketch:Element{}
    internal sealed class Phase:Element{}
    internal sealed class Level:Element{internal double ProjectElevation;}
    internal class View:Element{internal bool IsTemplate,Iterable=true;}
    internal sealed class View3D:View{}
    internal sealed class ViewSheet:View{}
    internal sealed class ViewSchedule:View{}
    internal sealed class WallType:Element{internal WallKind Kind;internal double Width=1;}
    internal class Location{}
    internal sealed class LocationCurve:Location
    {
        internal Curve Curve;internal readonly List<Element>[] Joins={new List<Element>(),new List<Element>()};
        internal IEnumerable<Element> get_ElementsAtJoin(int end){return Joins[end];}
    }
    internal sealed class Wall:Element
    {
        internal bool IsStackedWall,IsStackedWallMember;internal WallType WallType;internal Location Location;
        internal readonly List<ElementId> Inserts=new List<ElementId>();internal GeometryElement Geometry;
        internal readonly List<ElementId> CuttingVoids=new List<ElementId>();
        internal Options LastOptions;internal Exception GeometryError;internal Action DuringGeometry;internal int GeometryReads,BoundsReads;
        internal BoundingBoxXYZ Box=new BoundingBoxXYZ{Min=new XYZ(0,2,0),Max=new XYZ(10,3,10)};
        internal BoundingBoxXYZ get_BoundingBox(View view){BoundsReads++;return Box;}
        internal ICollection<ElementId> FindInserts(bool a,bool b,bool c,bool d){if(!a||!b||!c||!d)throw new Exception("Incomplete insert request");return Inserts;}
        internal GeometryElement get_Geometry(Options options){GeometryReads++;LastOptions=options;if(DuringGeometry!=null)DuringGeometry();if(GeometryError!=null)throw GeometryError;return Geometry;}
    }
    internal sealed class Opening:Element{internal Element Host;internal bool IsRectBoundary=true;internal IList<XYZ> BoundaryRect;}
    internal sealed class FamilyInstance:Element{internal Element Host;}
    internal sealed class Document
    {
        internal string Title="wall fixture",PathName="H:\\fixture\\wall.rvt";internal bool IsValidObject=true,IsFamilyDocument;internal long Revision;
        internal Element ProjectInformation=new Element{UniqueId="project-wall-fixture"};internal readonly Dictionary<int,Element> Elements=new Dictionary<int,Element>();
        internal int GetElementCalls;
        internal Element GetElement(ElementId id){GetElementCalls++;Element e;return id!=null&&Elements.TryGetValue(id.IntegerValue,out e)?e:null;}
        internal Element GetElement(string uid){GetElementCalls++;return Elements.Values.FirstOrDefault(e=>e.UniqueId==uid);}
        internal T Add<T>(T e,int id)where T:Element{e.Document=this;e.Id=new ElementId(id);e.UniqueId="wall-e-"+id;Elements.Add(id,e);return e;}
    }
    internal sealed class FilteredElementCollector:IEnumerable<Element>,IDisposable
    {
        private IEnumerable<Element> values;internal FilteredElementCollector(Document d){values=d.Elements.Values;}
        internal FilteredElementCollector OfClass(Type type){values=values.Where(e=>type.IsInstanceOfType(e));return this;}
        internal static bool IsViewValidForElementIteration(Document d,ElementId id){return ((View)d.GetElement(id)).Iterable;}
        public IEnumerator<Element> GetEnumerator(){return values.GetEnumerator();}System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator(){return GetEnumerator();}public void Dispose(){}
    }
    internal static class JoinGeometryUtils{internal static ICollection<ElementId> GetJoinedElements(Document d,Element e){return e.Joined;}}
    internal static class InstanceVoidCutUtils{internal static ICollection<ElementId> GetCuttingVoidInstances(Element e){return((Wall)e).CuttingVoids;}}
    internal sealed class XYZ
    {
        internal readonly double X,Y,Z;internal static readonly XYZ BasisZ=new XYZ(0,0,1);
        internal XYZ(double x,double y,double z){X=x;Y=y;Z=z;}
        internal double DotProduct(XYZ b){return X*b.X+Y*b.Y+Z*b.Z;}
        public static XYZ operator-(XYZ a,XYZ b){return new XYZ(a.X-b.X,a.Y-b.Y,a.Z-b.Z);}
    }
    internal sealed class Transform
    {
        internal static Transform Identity{get{return new Transform();}}
        internal double OffsetX;internal XYZ OfPoint(XYZ p){return new XYZ(p.X+OffsetX,p.Y,p.Z);}
    }
    internal sealed class BoundingBoxXYZ:IDisposable
    {
        internal XYZ Min,Max;internal Transform Transform=new Transform();public void Dispose(){}
    }
    internal abstract class Curve
    {
        internal abstract XYZ GetEndPoint(int end);internal virtual IList<XYZ> Tessellate(){return new[]{GetEndPoint(0),GetEndPoint(1)};}
    }
    internal sealed class Arc:Curve{internal override XYZ GetEndPoint(int end){return new XYZ(end*10,0,0);}}
    internal sealed class Line:Curve
    {
        internal XYZ A,B;internal bool IsBound=true;internal double Length{get{XYZ v=B-A;return Math.Sqrt(v.DotProduct(v));}}
        internal XYZ Direction{get{XYZ v=B-A;return new XYZ(v.X/Length,v.Y/Length,v.Z/Length);}}
        internal static Line CreateBound(XYZ a,XYZ b){return new Line{A=a,B=b};}
        internal override XYZ GetEndPoint(int end){return end==0?A:B;}
    }
    internal class GeometryObject:IDisposable{internal int DisposeCalls;public void Dispose(){DisposeCalls++;}}
    internal sealed class GeometryInstance:GeometryObject{}
    internal sealed class Mesh:GeometryObject{}
    internal sealed class GeometryElement:List<GeometryObject>,IDisposable{public void Dispose(){}}
    internal sealed class Options:IDisposable{internal bool ComputeReferences,IncludeNonVisibleObjects;internal View View;public void Dispose(){}}
    internal sealed class CurveLoop:List<Curve>,IDisposable{internal void Append(Curve curve){Add(curve);}public void Dispose(){}}
    internal sealed class FaceArray{internal int Size=6;}
    internal sealed class Edge{internal Curve Curve;internal Curve AsCurve(){return Curve;}}
    internal sealed class BoxShape
    {
        internal double X0,X1,Y0,Y1,Z0,Z1;
        internal BoxShape(double x0,double x1,double y0,double y1,double z0,double z1){X0=x0;X1=x1;Y0=y0;Y1=y1;Z0=z0;Z1=z1;}
        internal double Volume{get{return(X1-X0)*(Y1-Y0)*(Z1-Z0);}}
        internal BoxShape Copy(){return new BoxShape(X0,X1,Y0,Y1,Z0,Z1);}
        internal BoxShape Intersect(BoxShape b){double x0=Math.Max(X0,b.X0),x1=Math.Min(X1,b.X1),y0=Math.Max(Y0,b.Y0),y1=Math.Min(Y1,b.Y1),z0=Math.Max(Z0,b.Z0),z1=Math.Min(Z1,b.Z1);return x1>x0&&y1>y0&&z1>z0?new BoxShape(x0,x1,y0,y1,z0,z1):null;}
        internal List<BoxShape> Subtract(BoxShape b)
        {
            BoxShape cut=Intersect(b);if(cut==null)return new List<BoxShape>{Copy()};var r=new List<BoxShape>();
            Add(r,X0,cut.X0,Y0,Y1,Z0,Z1);Add(r,cut.X1,X1,Y0,Y1,Z0,Z1);
            Add(r,cut.X0,cut.X1,Y0,cut.Y0,Z0,Z1);Add(r,cut.X0,cut.X1,cut.Y1,Y1,Z0,Z1);
            Add(r,cut.X0,cut.X1,cut.Y0,cut.Y1,Z0,cut.Z0);Add(r,cut.X0,cut.X1,cut.Y0,cut.Y1,cut.Z1,Z1);return r;
        }
        private static void Add(List<BoxShape> r,double x0,double x1,double y0,double y1,double z0,double z1){if(x1>x0&&y1>y0&&z1>z0)r.Add(new BoxShape(x0,x1,y0,y1,z0,z1));}
    }
    internal sealed class Solid:GeometryObject
    {
        internal readonly List<BoxShape> Boxes=new List<BoxShape>();
        internal double Volume{get{return Boxes.Sum(box=>box.Volume);}}
        internal FaceArray Faces{get{return new FaceArray{Size=Boxes.Count==0?0:6*Boxes.Count};}}
        internal IList<Edge> Edges
        {
            get{var r=new List<Edge>();foreach(BoxShape b in Boxes){var p=new[]{new XYZ(b.X0,b.Y0,b.Z0),new XYZ(b.X1,b.Y0,b.Z0),new XYZ(b.X1,b.Y1,b.Z0),new XYZ(b.X0,b.Y1,b.Z0),new XYZ(b.X0,b.Y0,b.Z1),new XYZ(b.X1,b.Y0,b.Z1),new XYZ(b.X1,b.Y1,b.Z1),new XYZ(b.X0,b.Y1,b.Z1)};foreach(int[] pair in new[]{new[]{0,1},new[]{1,2},new[]{2,3},new[]{3,0},new[]{4,5},new[]{5,6},new[]{6,7},new[]{7,4},new[]{0,4},new[]{1,5},new[]{2,6},new[]{3,7}})r.Add(new Edge{Curve=Line.CreateBound(p[pair[0]],p[pair[1]])});}return r;}
        }
        internal static Solid Shape(params BoxShape[] boxes){var result=new Solid();result.Boxes.AddRange(boxes.Select(box=>box.Copy()));return result;}
    }
    internal static class GeometryCreationUtilities
    {
        internal static Solid CreateExtrusionGeometry(IList<CurveLoop> loops,XYZ direction,double height)
        {
            IList<XYZ> points=loops[0].Select(c=>c.GetEndPoint(0)).ToList();
            return Solid.Shape(new BoxShape(points.Min(p=>p.X),points.Max(p=>p.X),points.Min(p=>p.Y),points.Max(p=>p.Y),points.Min(p=>p.Z),points.Min(p=>p.Z)+height));
        }
    }
    internal static class BooleanOperationsUtils
    {
        internal static bool Throw;
        internal static int Calls;internal static readonly List<Solid> Outputs=new List<Solid>();
        internal static Solid ExecuteBooleanOperation(Solid a,Solid b,BooleanOperationsType operation)
        {
            Calls++;
            if(Throw)throw new InvalidOperationException("Controlled Boolean failure");
            var r=new Solid();
            if(operation==BooleanOperationsType.Intersect){foreach(BoxShape x in a.Boxes)foreach(BoxShape y in b.Boxes){BoxShape overlap=x.Intersect(y);if(overlap!=null)r.Boxes.Add(overlap);}}
            else if(operation==BooleanOperationsType.Difference){r.Boxes.AddRange(a.Boxes.Select(box=>box.Copy()));foreach(BoxShape cut in b.Boxes){var next=new List<BoxShape>();foreach(BoxShape source in r.Boxes)next.AddRange(source.Subtract(cut));r.Boxes.Clear();r.Boxes.AddRange(next);}}
            else{r.Boxes.AddRange(a.Boxes.Select(box=>box.Copy()));foreach(BoxShape add in b.Boxes){var parts=new List<BoxShape>{add.Copy()};foreach(BoxShape old in r.Boxes){var next=new List<BoxShape>();foreach(BoxShape part in parts)next.AddRange(part.Subtract(old));parts=next;}r.Boxes.AddRange(parts);}}
            return Output(r);
        }
        private static Solid Output(Solid value){Outputs.Add(value);return value;}
    }
    internal static class SolidUtils
    {
        internal static bool ReturnInputAlias;internal static int SplitCount=1;
        internal static readonly List<Solid> Copies=new List<Solid>();
        internal static Solid Clone(Solid solid){if(ReturnInputAlias)return solid;var result=Solid.Shape(solid.Boxes.ToArray());Copies.Add(result);return result;}
        internal static IList<Solid> SplitVolumes(Solid solid){if(ReturnInputAlias)return new[]{solid};return Enumerable.Range(0,SplitCount).Select(i=>Clone(solid)).ToList();}
    }
}
namespace Autodesk.Revit.UI
{
    internal sealed class UIDocument
    {
        internal Autodesk.Revit.DB.Document Document;internal Autodesk.Revit.DB.View ActiveView;
    }
}
namespace BrightVision.Revit
{
    internal static class RevitCivilQuantityReader{internal static long Revision(Autodesk.Revit.DB.Document d){return d.Revision;}}
}
