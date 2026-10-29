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
    public sealed class CivilWallJoinCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument ui = data.Application.ActiveUIDocument; Document document = ui == null ? null : ui.Document;
            if(document==null||!document.IsValidObject||document.IsFamilyDocument){TaskDialog.Show("BV · 墙体交接","请先打开工程模型。");return Result.Cancelled;}
            try
            {
                CivilReadRequest initial = RevitCivilQuantityReader.DefaultRequest(ui);
                var views = new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>().Where(view => !view.IsTemplate).OrderBy(view => view.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
                View3D active = ui.ActiveView as View3D;
                var window = new CivilWallJoinWindow(initial,RevitCivilQuantityReader.GetPhases(document),RevitCivilQuantityReader.GetDesignOptions(document),views.Select(view => new CivilChoice { Id = view.UniqueId, Label = view.Name }).ToList(),active == null || active.IsTemplate ? String.Empty : active.UniqueId,
                    delegate(CivilReadRequest request,string viewId,CivilWallRuleTemplate rule,CivilWallReadControl control)
                    {
                        Require(data.Application,document); View3D view = document.GetElement(viewId) as View3D;
                        if(view==null||!view.IsValidObject||view.IsTemplate)throw new InvalidOperationException("几何校验视图失效，请重新打开交接工作台。");
                        control.Checkpoint("读取请求范围原生参数",0,0); Require(data.Application,document);
                        CivilSnapshot native = RevitCivilQuantityReader.Read(ui,request); control.Checkpoint("读取完整墙体交接依赖",0,0); Require(data.Application,document);
                        RevitWallJoinReadBundle result = RevitWallJoinReader.Read(ui,native,request,view,rule,control);
                        try { Require(data.Application,document); return result; } catch { result.Dispose(); throw; }
                    },delegate { return document.IsValidObject ? RevitCivilQuantityReader.Revision(document) : -1; });
                IntPtr owner = Process.GetCurrentProcess().MainWindowHandle; if(owner!=IntPtr.Zero)new WindowInteropHelper(window).Owner=owner;
                window.ShowDialog();
                if(window.SelectedFocusElements.Count>0)
                {
                    Require(data.Application,document); CivilWallJoinSession.Validate(window.LastBatch);
                    if(window.LastBatch.Input.NativeSnapshot.Revision!=RevitCivilQuantityReader.Revision(document))throw new InvalidOperationException("模型修订变化，请重新读取交接组件后定位。");
                    IList<ElementId> ids=CivilQuantityCommand.ResolveFocus(document,window.SelectedFocusElements);ui.Selection.SetElementIds(ids);
                    try { ui.ShowElements(ids); } catch(Autodesk.Revit.Exceptions.InvalidOperationException) { TaskDialog.Show("BV · 墙体交接","来源构件已选中，当前视图不能缩放到该对象。"); }
                }
                return Result.Succeeded;
            }
            catch(OperationCanceledException){return Result.Cancelled;}
            catch(Exception error){message=error.Message;TaskDialog.Show("BV · 墙体交接","本轮未完成：\n"+error.Message);return Result.Failed;}
        }
        private static void Require(UIApplication application,Document captured)
        { if(!captured.IsValidObject||application.ActiveUIDocument==null||!application.ActiveUIDocument.Document.Equals(captured))throw new InvalidOperationException("来源工程已变化，请重新打开交接工作台。"); }
    }
}
