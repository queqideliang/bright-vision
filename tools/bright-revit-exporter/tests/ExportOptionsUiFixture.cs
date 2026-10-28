using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BrightVision.Revit;
public static class Fixture {
 static int checks;
 static void Check(bool value,string message) { checks++; if(!value) throw new Exception(message); }
 static object Field(object value,string name) { return value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(value); }
 [STAThread] public static void Main(string[] args) {
  var a=new View3D{Name="总体三维视图",Id=new ElementId(1),ViewType="ThreeD"};
  var b=new View3D{Name="机电协调视图",Id=new ElementId(2),ViewType="ThreeD"};
  var drawing=new View{Name="一层平面图",Id=new ElementId(3),ViewType="FloorPlan",CanBePrinted=true};
  var doc=new UIDocument{Document=new Document{Title="测试工程",PathName=""},ActiveView=b};
  var type=typeof(ExportOptionsDialog).GetNestedType("OptionsWindow",BindingFlags.NonPublic);
  var window=(Window)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{doc,new List<View3D>{a,b},new List<Autodesk.Revit.DB.View>{drawing}},CultureInfo.InvariantCulture);
  Check(window.Title=="BV 导出","title");
  Check(window.Icon!=null,"icon");
  Check(((TextBox)Field(window,"modelName")).Text=="测试工程","model name");
  Check(((ComboBox)Field(window,"viewPicker")).SelectedIndex==1,"current 3D preselected");
  foreach(string name in new[]{"textures","systems","spaces","disciplines","cad"}) Check(((CheckBox)Field(window,name)).IsChecked==true,"default "+name);
  Check(((CheckBox)Field(window,"grids")).IsChecked==false,"grids optional");
  Check(((CheckBox)Field(window,"drawings")).IsChecked==false,"2D optional");
  Check(!((Button)Field(window,"drawingSelect")).IsEnabled,"2D picker initially disabled");
  ((CheckBox)Field(window,"drawings")).IsChecked=true;
  Check(((Button)Field(window,"drawingSelect")).IsEnabled,"2D enable enables selection");
  ((CheckBox)Field(window,"drawings")).IsChecked=false;
  Check(!((Button)Field(window,"drawingSelect")).IsEnabled,"2D disable disables selection");
  var validate=typeof(ExportOptionsDialog).GetMethod("ValidFileName",BindingFlags.Static|BindingFlags.NonPublic);
  foreach(string name in new[]{"CON","aux.zip","LPT1","a/b","a.","","  "}) Check(!(bool)validate.Invoke(null,new object[]{name}),"reject "+name);
  Check((bool)validate.Invoke(null,new object[]{"体育馆_三维模型"}),"Chinese name");
  Check((bool)validate.Invoke(null,new object[]{"BV-model.2026"}),"dot in model name");
  var noDrawings=(Window)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{doc,new List<View3D>{a,b},new List<Autodesk.Revit.DB.View>()},CultureInfo.InvariantCulture);
  Check(!((CheckBox)Field(noDrawings,"drawings")).IsEnabled,"no drawings cannot enable 2D");
  var content=(FrameworkElement)window.Content;
  content.Measure(new Size(770,790)); content.Arrange(new Rect(0,0,770,790)); content.UpdateLayout();
  var bitmap=new RenderTargetBitmap(770,790,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
  var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(args[0]))encoder.Save(file);
  Console.WriteLine("Passed "+checks+" UI/validation assertions; rendered without showing a window.");
 }
}
