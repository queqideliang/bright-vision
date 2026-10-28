using System;
using System.Collections;
using System.Collections.Generic;
namespace Autodesk.Revit.DB {
 public sealed class ElementId { public int Value; public ElementId(int value) { Value=value; } public override bool Equals(object obj) { var id=obj as ElementId; return id!=null && id.Value==Value; } public override int GetHashCode() { return Value; } }
 public class View { public string Name {get;set;} public ElementId Id {get;set;} public bool IsTemplate {get;set;} public bool CanBePrinted {get;set;} public string ViewType {get;set;} }
 public class View3D:View {}
 public class Document { public string Title {get;set;} public string PathName {get;set;} public List<View> Views=new List<View>(); }
 public sealed class FilteredElementCollector:IEnumerable { Document document; public FilteredElementCollector(Document value){document=value;} public FilteredElementCollector OfClass(Type value){return this;} public IEnumerator GetEnumerator(){return document.Views.GetEnumerator();} }
}
namespace Autodesk.Revit.UI {
 public class UIDocument { public Autodesk.Revit.DB.Document Document {get;set;} public Autodesk.Revit.DB.View ActiveView {get;set;} }
 public static class TaskDialog { public static void Show(string title,string message) { throw new InvalidOperationException("Unexpected dialog in fixture: "+message); } }
}
