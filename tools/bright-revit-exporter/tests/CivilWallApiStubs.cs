using System;
using System.Collections.Generic;
using System.Linq;

// Adapter-only stand-ins. Their Boolean results are controlled test data,
// not a replacement geometry engine or evidence about Autodesk Boolean maths.
namespace Autodesk.Revit.DB
{
    internal enum BuiltInParameter { VIEW_PHASE, CURVE_ELEM_LENGTH, WALL_USER_HEIGHT_PARAM, WALL_TOP_IS_ATTACHED, WALL_BOTTOM_IS_ATTACHED, WALL_HEIGHT_TYPE, WALL_BASE_CONSTRAINT, WALL_BASE_OFFSET }
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
        internal Parameter get_Parameter(BuiltInParameter key){Parameter p;return Parameters.TryGetValue(key,out p)?p:null;}
        internal ElementOnPhaseStatus GetPhaseStatus(ElementId phase){if(PhaseError!=null)throw PhaseError;return PhaseStatus;}
        internal bool HasPhases(){return Phaseable;}
    }
    internal sealed class DesignOption:Element{}
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
        internal Options LastOptions;internal Exception GeometryError;internal Action DuringGeometry;
        internal BoundingBoxXYZ Box=new BoundingBoxXYZ{Min=new XYZ(0,2,0),Max=new XYZ(10,3,10)};
        internal BoundingBoxXYZ get_BoundingBox(View view){return Box;}
        internal ICollection<ElementId> FindInserts(bool a,bool b,bool c,bool d){if(!a||!b||!c||!d)throw new Exception("Incomplete insert request");return Inserts;}
        internal GeometryElement get_Geometry(Options options){LastOptions=options;if(DuringGeometry!=null)DuringGeometry();if(GeometryError!=null)throw GeometryError;return Geometry;}
    }
    internal sealed class Opening:Element{internal Element Host;internal bool IsRectBoundary=true;internal IList<XYZ> BoundaryRect;}
    internal sealed class FamilyInstance:Element{internal Element Host;}
    internal sealed class Document
    {
        internal string Title="wall fixture",PathName="H:\\fixture\\wall.rvt";internal bool IsValidObject=true,IsFamilyDocument;internal long Revision;
        internal Element ProjectInformation=new Element{UniqueId="project-wall-fixture"};internal readonly Dictionary<int,Element> Elements=new Dictionary<int,Element>();
        internal Element GetElement(ElementId id){Element e;return id!=null&&Elements.TryGetValue(id.IntegerValue,out e)?e:null;}
        internal Element GetElement(string uid){return Elements.Values.FirstOrDefault(e=>e.UniqueId==uid);}
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
    internal sealed class XYZ
    {
        internal readonly double X,Y,Z;internal static readonly XYZ BasisZ=new XYZ(0,0,1);
        internal XYZ(double x,double y,double z){X=x;Y=y;Z=z;}
        internal double DotProduct(XYZ b){return X*b.X+Y*b.Y+Z*b.Z;}
        public static XYZ operator-(XYZ a,XYZ b){return new XYZ(a.X-b.X,a.Y-b.Y,a.Z-b.Z);}
    }
    internal sealed class Transform
    {
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
    internal class GeometryObject:IDisposable{public void Dispose(){}}
    internal sealed class GeometryInstance:GeometryObject{}
    internal sealed class Mesh:GeometryObject{}
    internal sealed class GeometryElement:List<GeometryObject>,IDisposable{public void Dispose(){}}
    internal sealed class Options:IDisposable{internal bool ComputeReferences,IncludeNonVisibleObjects;internal View View;public void Dispose(){}}
    internal sealed class CurveLoop:List<Curve>,IDisposable{internal void Append(Curve curve){Add(curve);}public void Dispose(){}}
    internal sealed class FaceArray{internal int Size=6;}
    internal sealed class Edge{internal Curve Curve;internal Curve AsCurve(){return Curve;}}
    internal sealed class Solid:GeometryObject
    {
        internal string Tag="actual";internal double Volume=100;internal double X0,X1,N0,N1,Z0,Z1;internal FaceArray Faces=new FaceArray();internal readonly List<Edge> Edges=new List<Edge>();
        internal static Solid Actual(double volume,double minN,double maxN){var s=new Solid{Volume=volume};s.Edges.Add(new Edge{Curve=Line.CreateBound(new XYZ(0,minN,0),new XYZ(10,maxN,10))});return s;}
    }
    internal static class GeometryCreationUtilities
    {
        internal static Solid CreateExtrusionGeometry(IList<CurveLoop> loops,XYZ direction,double height)
        {
            IList<XYZ> points=loops[0].Select(c=>c.GetEndPoint(0)).ToList();
            return new Solid{Tag="constructed",Volume=(points.Max(p=>p.X)-points.Min(p=>p.X))*(points.Max(p=>p.Y)-points.Min(p=>p.Y))*height,X0=points.Min(p=>p.X),X1=points.Max(p=>p.X),N0=points.Min(p=>p.Y),N1=points.Max(p=>p.Y),Z0=points.Min(p=>p.Z),Z1=points.Min(p=>p.Z)+height};
        }
    }
    internal static class BooleanOperationsUtils
    {
        internal static bool Throw;internal static double ForcedShapeResidual,IntersectionVolume;
        internal static Solid ExecuteBooleanOperation(Solid a,Solid b,BooleanOperationsType operation)
        {
            if(Throw)throw new InvalidOperationException("Controlled Boolean failure");
            if(operation==BooleanOperationsType.Intersect)return new Solid{Tag="intersection",Volume=IntersectionVolume};
            double value=operation==BooleanOperationsType.Union?a.Volume+b.Volume:Math.Max(0,a.Volume-b.Volume);
            if(operation==BooleanOperationsType.Difference&&a.Tag=="constructed"&&b.Tag=="constructed")
                value=Math.Max(0,a.Volume-Math.Max(0,Math.Min(a.X1,b.X1)-Math.Max(a.X0,b.X0))*Math.Max(0,Math.Min(a.N1,b.N1)-Math.Max(a.N0,b.N0))*Math.Max(0,Math.Min(a.Z1,b.Z1)-Math.Max(a.Z0,b.Z0)));
            if(operation==BooleanOperationsType.Difference&&(a.Tag=="actual"||b.Tag=="actual"))value+=ForcedShapeResidual/2;
            return new Solid{Tag=operation==BooleanOperationsType.Union?"actual":"constructed",Volume=value,X0=a.X0,X1=a.X1,N0=a.N0,N1=a.N1,Z0=a.Z0,Z1=a.Z1};
        }
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
