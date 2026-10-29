// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BrightVision.Revit
{
    public sealed class PluginSupportAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication application, CategorySet categories) { return true; }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class PluginUpdateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                Version version = Assembly.GetExecutingAssembly().GetName().Version;
                string current = version.Major + "." + version.Minor + "." + version.Build;
                var window = new PluginUpdateWindow(current);
                IntPtr owner = Process.GetCurrentProcess().MainWindowHandle;
                if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
                window.ShowDialog(); return Result.Succeeded;
            }
            catch (Exception error) { message = error.Message; TaskDialog.Show("Bright Vision · 检查更新", "未能打开更新窗口：\n" + error.Message); return Result.Failed; }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class PluginHelpCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try { PluginHelpWindow.Show(Process.GetCurrentProcess().MainWindowHandle); return Result.Succeeded; }
            catch (Exception error) { message = error.Message; TaskDialog.Show("Bright Vision · 功能介绍", "未能打开使用指南：\n" + error.Message); return Result.Failed; }
        }
    }
}
