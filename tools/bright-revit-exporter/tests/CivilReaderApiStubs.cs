using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Autodesk.Revit.DB
{
    internal enum BuiltInParameter { VIEW_PHASE, CURVE_ELEM_LENGTH, HOST_AREA_COMPUTED, HOST_VOLUME_COMPUTED,
        INSTANCE_REFERENCE_LEVEL_PARAM, FAMILY_LEVEL_PARAM, SCHEDULE_LEVEL_PARAM, FAMILY_BASE_LEVEL_PARAM, WALL_BASE_CONSTRAINT, LEVEL_PARAM }
    internal enum BuiltInCategory { OST_Walls=-1, OST_Floors=-2, OST_StructuralFraming=-3, OST_StructuralColumns=-4, OST_Columns=-5, OST_GenericModel=-6, OST_Other=-7, OST_CurtainWallPanels=-8, OST_CurtainWallMullions=-9 }
    internal enum StorageType { None, Double, Integer, ElementId }
    internal enum ElementOnPhaseStatus { New, Existing, Demolished, Temporary, Future, Past, None }
    internal enum WallKind { Basic, Curtain, Stacked }
    internal sealed class ElementId
    {
        internal static readonly ElementId InvalidElementId=new ElementId(-1);
        internal int IntegerValue { get; private set; }
        internal long Value { get { return IntegerValue; } }
        internal ElementId(int value) { IntegerValue=value; }
        public override bool Equals(object value) { ElementId other=value as ElementId;return other!=null&&other.IntegerValue==IntegerValue; }
        public override int GetHashCode() { return IntegerValue; }
    }
    internal sealed class Category { internal ElementId Id; internal string Name; }
    internal sealed class Parameter
    {
        internal bool HasValue=true;
        internal StorageType StorageType=StorageType.Double;
        internal double Number;
        internal ElementId Id=ElementId.InvalidElementId;
        internal Exception Error;
        internal int ReadCount;
        internal double AsDouble() { ReadCount++;if(Error!=null)throw Error;return Number; }
        internal ElementId AsElementId() { if(Error!=null)throw Error;return Id; }
    }
    internal class Element
    {
        internal Document Document;
        internal ElementId Id=new ElementId(0),LevelId=ElementId.InvalidElementId,TypeId=ElementId.InvalidElementId;
        internal string UniqueId="unique",Name="element";
        internal Category Category;
        internal DesignOption DesignOption;
        internal ElementOnPhaseStatus PhaseStatus=ElementOnPhaseStatus.New;
        internal BoundingBoxXYZ Box;
        internal readonly Dictionary<BuiltInParameter,Parameter> Values=new Dictionary<BuiltInParameter,Parameter>();
        internal virtual Parameter get_Parameter(BuiltInParameter key) { Parameter p;return Values.TryGetValue(key,out p)?p:null; }
        internal ElementId GetTypeId() { return TypeId; }
        internal ElementOnPhaseStatus GetPhaseStatus(ElementId phase) { return PhaseStatus; }
        internal BoundingBoxXYZ get_BoundingBox(View view) { return Box; }
    }
    internal class ElementType:Element { internal string FamilyName="family"; }
    internal sealed class WallType:ElementType { internal WallKind Kind=WallKind.Basic; }
    internal sealed class Wall:Element { internal bool IsStackedWall,IsStackedWallMember; internal WallType WallType=new WallType(); }
    internal sealed class Floor:Element { }
    internal sealed class FamilyInstance:Element { internal FamilyInstance SuperComponent; internal Autodesk.Revit.DB.Structure.StructuralType StructuralType; }
    internal sealed class Phase:Element { }
    internal sealed class Level:Element
    {
        internal double Elevation;
        internal double? ProjectElevationOverride;
        internal double ProjectElevation { get { return ProjectElevationOverride ?? Elevation; } }
    }
    internal sealed class DesignOption:Element { }
    internal sealed class RevitLinkInstance:Element { internal Document LinkedDocument; internal Document GetLinkDocument() { return LinkedDocument; } }
    internal class View:Element { internal bool IsTemplate,ValidForIteration=true; }
    internal sealed class ViewSheet:View { }
    internal sealed class ViewSchedule:View { }
    internal sealed class XYZ
    {
        internal readonly double X,Y,Z;
        internal XYZ(double x,double y,double z){X=x;Y=y;Z=z;}
    }
    internal sealed class Transform
    {
        internal double OffsetZ;
        internal XYZ OfPoint(XYZ value){return new XYZ(value.X,value.Y,value.Z+OffsetZ);}
    }
    internal sealed class BoundingBoxXYZ { internal XYZ Min=new XYZ(0,0,0),Max=new XYZ(1,1,1);internal Transform Transform=new Transform(); }
    internal sealed class Document
    {
        internal string ModelKey=Guid.NewGuid().ToString(),Title="Test project",PathName="H:\\fixture\\sample.rvt";
        internal bool IsValidObject=true,IsFamilyDocument;
        internal Element ProjectInformation=new Element{UniqueId="project-unique"};
        internal readonly List<Phase> Phases=new List<Phase>();
        internal readonly Dictionary<int,Element> Elements=new Dictionary<int,Element>();
        internal readonly Dictionary<int,List<Element>> ViewElements=new Dictionary<int,List<Element>>();
        internal Element GetElement(ElementId id) { Element e;return id!=null&&Elements.TryGetValue(id.IntegerValue,out e)?e:null; }
        internal T Add<T>(T element,int id) where T:Element
        { element.Document=this;element.Id=new ElementId(id);element.UniqueId="element-"+id;Elements[id]=element;return element; }
        public override bool Equals(object value){Document other=value as Document;return other!=null&&other.ModelKey==ModelKey;}
        public override int GetHashCode(){return ModelKey.GetHashCode();}
    }
    internal sealed class FilteredElementCollector:IEnumerable<Element>,IDisposable
    {
        private IEnumerable<Element> elements;
        internal FilteredElementCollector(Document document){elements=document.Elements.Values;}
        internal FilteredElementCollector(Document document,ElementId view){elements=document.ViewElements[view.IntegerValue];}
        internal FilteredElementCollector OfClass(Type type){elements=elements.Where(e=>type.IsInstanceOfType(e));return this;}
        internal FilteredElementCollector WhereElementIsNotElementType(){elements=elements.Where(e=>!(e is ElementType));return this;}
        internal IList<Element> ToElements(){return elements.ToList();}
        internal static bool IsViewValidForElementIteration(Document document,ElementId id){View view=document.GetElement(id) as View;return view!=null&&view.ValidForIteration;}
        public IEnumerator<Element> GetEnumerator(){return elements.GetEnumerator();}
        IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
        public void Dispose(){ }
    }
}
namespace Autodesk.Revit.DB.Structure { internal enum StructuralType { NonStructural,Beam,Brace,Column } }
namespace Autodesk.Revit.DB.Events
{
    internal sealed class DocumentChangedEventArgs:EventArgs
    {
        private readonly Autodesk.Revit.DB.Document document;
        internal DocumentChangedEventArgs(Autodesk.Revit.DB.Document document){this.document=document;}
        internal Autodesk.Revit.DB.Document GetDocument(){return document;}
    }
}
namespace Autodesk.Revit.ApplicationServices
{
    internal sealed class ControlledApplication
    {
        internal event EventHandler<Autodesk.Revit.DB.Events.DocumentChangedEventArgs> DocumentChanged;
        internal void Publish(Autodesk.Revit.DB.Document document){var handler=DocumentChanged;if(handler!=null)handler(this,new Autodesk.Revit.DB.Events.DocumentChangedEventArgs(document));}
    }
}
namespace Autodesk.Revit.UI.Selection
{
    internal sealed class Selection
    {
        internal readonly List<Autodesk.Revit.DB.ElementId> Ids=new List<Autodesk.Revit.DB.ElementId>();
        internal ICollection<Autodesk.Revit.DB.ElementId> GetElementIds(){return Ids;}
    }
}
namespace Autodesk.Revit.UI
{
    internal sealed class UIControlledApplication { internal readonly Autodesk.Revit.ApplicationServices.ControlledApplication ControlledApplication=new Autodesk.Revit.ApplicationServices.ControlledApplication(); }
    internal sealed class UIDocument
    {
        internal Autodesk.Revit.DB.Document Document;
        internal Autodesk.Revit.DB.View ActiveView;
        internal readonly Autodesk.Revit.UI.Selection.Selection Selection=new Autodesk.Revit.UI.Selection.Selection();
    }
}
