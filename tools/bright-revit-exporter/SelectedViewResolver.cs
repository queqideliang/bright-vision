using System;
using System.IO;
using Autodesk.Revit.DB;

namespace BrightVision.Revit
{
    internal static class SelectedViewResolver
    {
        internal static View3D Resolve(Document document, View3D selectedView)
        {
            if (document == null || !document.IsValidObject)
                throw new InvalidDataException("当前工程已关闭或失效，请重新打开工程后导出。");
            if (selectedView == null || !selectedView.IsValidObject)
                throw new InvalidDataException("所选三维视图已失效，请重新选择视图。");

            // Revit can return different managed wrappers for one native document.
            // Document.Equals compares the underlying open document; != does not.
            Document source = selectedView.Document;
            if (source == null || !source.IsValidObject || !source.Equals(document))
                throw new InvalidDataException("所选视图不属于当前工程，请重新选择当前工程的三维视图。");

            // Re-resolve the selection against the captured document, rather than
            // keeping a potentially stale UI element wrapper. A document check is
            // still required because copied RVT files may share element UniqueIds.
            string uniqueId = selectedView.UniqueId;
            if (String.IsNullOrEmpty(uniqueId))
                throw new InvalidDataException("所选三维视图没有有效标识，请重新选择视图。");
            View3D resolved = document.GetElement(uniqueId) as View3D;
            if (resolved == null || !resolved.IsValidObject)
                throw new InvalidDataException("所选三维视图不存在或已删除，请重新选择视图。");
            if (resolved.IsTemplate)
                throw new InvalidDataException("视图模板不能导出，请选择普通三维视图。");
            return resolved;
        }
    }
}
