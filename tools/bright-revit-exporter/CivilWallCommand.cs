// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BrightVision.Civil;

namespace BrightVision.Revit
{
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class CivilWallCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument ui=commandData.Application.ActiveUIDocument; Document document=ui==null?null:ui.Document;
            if(document==null||!document.IsValidObject||document.IsFamilyDocument){TaskDialog.Show("崛亮视觉 · 墙体试算","请先打开工程模型。");return Result.Cancelled;}
            try
            {
                CivilReadRequest initial=RevitCivilQuantityReader.DefaultRequest(ui);
                List<View3D> views=new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>().Where(v=>!v.IsTemplate).OrderBy(v=>v.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
                View3D active=ui.ActiveView as View3D;
                string selected=active!=null&&!active.IsTemplate?active.UniqueId:"";
                // A model view is an explicit user choice when starting on a
                // sheet. Never change ActiveView to perform geometry reads.
                var window=new CivilWallWindow(initial,RevitCivilQuantityReader.GetPhases(document),RevitCivilQuantityReader.GetDesignOptions(document),views.Select(v=>new CivilChoice {Id=v.UniqueId,Label=v.Name}).ToList(),selected,
                    null,
                    delegate{return document.IsValidObject?RevitCivilQuantityReader.Revision(document):-1;}, null,
                    delegate(CivilReadRequest request,string viewId,CivilWallRuleTemplate rule,CivilWallReadControl control){RequireDocument(commandData.Application,document);View3D view=document.GetElement(viewId) as View3D;
                        if(view==null||view.IsTemplate||!view.IsValidObject)throw new InvalidOperationException("几何校验视图失效，请重新打开工作台。");
                        if(control!=null)control.Checkpoint("读取原生构件参数",0,0);
                        RequireDocument(commandData.Application,document);
                        CivilSnapshot native=RevitCivilQuantityReader.Read(ui,request);
                        if(control!=null)control.Checkpoint("原生参数已读取",native.Elements.Count,native.Elements.Count);
                        RequireDocument(commandData.Application,document);
                        return RevitCivilWallReader.Read(ui,native,request,view,rule,control);});
                IntPtr owner=Process.GetCurrentProcess().MainWindowHandle;if(owner!=IntPtr.Zero)new WindowInteropHelper(window).Owner=owner;
                window.ShowDialog();
                if(window.SelectedFocusElements.Count>0){RequireDocument(commandData.Application,document);
                    if(window.LastBatch==null||window.LastBatch.Input.NativeSnapshot.Revision!=RevitCivilQuantityReader.Revision(document))throw new InvalidOperationException("模型已变化，请重新试算后再定位。");
                    CivilWallBatchIntegrity.Validate(window.LastBatch);
                    IList<ElementId> ids=CivilQuantityCommand.ResolveFocus(document,window.SelectedFocusElements);ui.Selection.SetElementIds(ids);
                    try{ui.ShowElements(ids);}catch(Autodesk.Revit.Exceptions.InvalidOperationException){TaskDialog.Show("BV · 墙体试算","关联构件已选中，当前视图暂不能缩放到该对象。");}}
                return Result.Succeeded;
            }
            catch(Exception error){if(error is OperationCanceledException)return Result.Cancelled;message=error.Message;TaskDialog.Show("崛亮视觉 · 墙体试算","本轮未完成：\n"+error.Message);return Result.Failed;}
        }
        private static void RequireDocument(UIApplication app,Document captured){UIDocument current=app.ActiveUIDocument;if(!captured.IsValidObject||current==null||!current.Document.Equals(captured))throw new InvalidOperationException("来源工程已变化，请关闭窗口后重新打开。");}
    }
}
