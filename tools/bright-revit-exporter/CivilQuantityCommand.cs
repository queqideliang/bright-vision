using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BrightVision.Civil;

namespace BrightVision.Revit
{
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class CivilQuantityCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument ui = commandData.Application.ActiveUIDocument;
            Document document = ui == null ? null : ui.Document;
            if (document == null || !document.IsValidObject || document.IsFamilyDocument)
            {
                TaskDialog.Show("BV 土建原始量", "请先打开工程模型，再使用原始量工作台。");
                return Result.Cancelled;
            }
            try
            {
                CivilQuantityWindow window = new CivilQuantityWindow(
                    RevitCivilQuantityReader.DefaultRequest(ui),
                    RevitCivilQuantityReader.GetPhases(document),
                    RevitCivilQuantityReader.GetDesignOptions(document),
                    delegate(CivilReadRequest request)
                    {
                        RequireCurrentDocument(commandData.Application, document);
                        return RevitCivilQuantityReader.Read(ui, request);
                    },
                    delegate { return document.IsValidObject ? RevitCivilQuantityReader.Revision(document) : -1; });
                IntPtr owner = Process.GetCurrentProcess().MainWindowHandle;
                if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
                window.ShowDialog();
                if (window.SelectedFocusElements != null && window.SelectedFocusElements.Count > 0)
                {
                    RequireCurrentDocument(commandData.Application, document);
                    if (window.LastSnapshot == null || window.LastSnapshot.Revision != RevitCivilQuantityReader.Revision(document))
                        throw new InvalidOperationException("模型已变化，请重新读取量表后再定位。");
                    IList<ElementId> ids = ResolveFocus(document, window.SelectedFocusElements);
                    ui.Selection.SetElementIds(ids);
                    try { ui.ShowElements(ids); }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                    {
                        TaskDialog.Show("BV 土建原始量", "构件已选中，当前视图暂时无法缩放到所选对象。请在适用的模型视图中查看选择。");
                    }
                }
                return Result.Succeeded;
            }
            catch (Exception error)
            {
                if (error is OperationCanceledException) return Result.Cancelled;
                message = error.Message;
                TaskDialog.Show("BV 土建原始量", "工作台操作未完成：\n" + error.Message);
                return Result.Failed;
            }
        }

        private static void RequireCurrentDocument(UIApplication application, Document captured)
        {
            UIDocument current = application.ActiveUIDocument;
            if (!captured.IsValidObject || current == null || !current.Document.Equals(captured))
                throw new InvalidOperationException("当前工程已变化，请关闭工作台后重新打开。");
        }

        // Resolve identities again after the modal window closes; never select
        // another element merely because an integer ID was reused.
        internal static IList<ElementId> ResolveFocus(Document document, IList<CivilElementRecord> records)
        {
            if (records == null || records.Count == 0) throw new InvalidOperationException("请先选择量表行。");
            if (records.Count > 5000) throw new InvalidOperationException("一次最多定位 5,000 个构件，请先缩小筛选范围。");
            List<ElementId> result = new List<ElementId>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CivilElementRecord record in records)
            {
                if (record == null || String.IsNullOrEmpty(record.UniqueId))
                    throw new InvalidOperationException("所选记录缺少来源身份，请重新读取。");
                Element element = document.GetElement(record.UniqueId);
                if (element == null || !element.IsValidObject || !String.Equals(Id(element.Id), record.ElementId, StringComparison.Ordinal))
                    throw new InvalidOperationException("所选构件已删除或身份变化，请重新读取。");
                if (seen.Add(record.UniqueId)) result.Add(element.Id);
            }
            return result;
        }

        private static string Id(ElementId id)
        {
#if REVIT_2024_OR_GREATER
            return id.Value.ToString(CultureInfo.InvariantCulture);
#else
            return id.IntegerValue.ToString(CultureInfo.InvariantCulture);
#endif
        }
    }
}
