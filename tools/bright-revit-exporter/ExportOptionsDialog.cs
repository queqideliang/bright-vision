using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Forms = System.Windows.Forms;
using RevitView = Autodesk.Revit.DB.View;
using Grid = System.Windows.Controls.Grid;
using Panel = System.Windows.Controls.Panel;
using UIDocument = Autodesk.Revit.UI.UIDocument;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace BrightVision.Revit
{
    internal static class ExportOptionsDialog
    {
        internal static ExportOptions Show(UIDocument document)
        {
            List<View3D> views = new List<View3D>();
            List<RevitView> views2D = new List<RevitView>();
            foreach (RevitView view in new FilteredElementCollector(document.Document).OfClass(typeof(RevitView)))
            {
                if (view.IsTemplate) continue;
                View3D view3D = view as View3D;
                if (view3D != null) views.Add(view3D);
                else if (CanExport2D(view)) views2D.Add(view);
            }
            views.Sort(delegate(View3D a, View3D b) { return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name); });
            views2D.Sort(delegate(RevitView a, RevitView b) { return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name); });
            if (views.Count == 0)
            {
                TaskDialog.Show("BV 导出", "工程中没有可导出的非模板三维视图。请先创建三维视图。");
                return null;
            }
            OptionsWindow window = new OptionsWindow(document, views, views2D);
            try
            {
                IntPtr owner = Process.GetCurrentProcess().MainWindowHandle;
                if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
            }
            catch (InvalidOperationException) { }
            return window.ShowDialog() == true ? window.Options : null;
        }

        private static bool CanExport2D(RevitView view)
        {
            if (!view.CanBePrinted) return false;
            // Explicitly limit this list to model/drafting views accepted by
            // the DWG workflow. Sheets/schedules/templates are not selected
            // implicitly, and adding a new view type requires an export check.
            string type = view.ViewType.ToString();
            return type == "FloorPlan" || type == "CeilingPlan" || type == "Elevation" || type == "Section"
                || type == "Detail" || type == "EngineeringPlan" || type == "AreaPlan" || type == "DraftingView";
        }

        private sealed class OptionsWindow : Window
        {
            private readonly TextBox modelName = new TextBox();
            private readonly TextBox outputFolder = new TextBox();
            private readonly ComboBox viewPicker = new ComboBox();
            private readonly CheckBox textures = new CheckBox();
            private readonly CheckBox systems = new CheckBox();
            private readonly CheckBox spaces = new CheckBox();
            private readonly CheckBox disciplines = new CheckBox();
            private readonly CheckBox cad = new CheckBox();
            private readonly CheckBox grids = new CheckBox();
            private readonly CheckBox drawings = new CheckBox();
            private readonly TextBlock drawingSummary = new TextBlock();
            private readonly TextBlock targetSummary = new TextBlock();
            private readonly Button drawingSelect = new Button();
            private readonly List<RevitView> available2D;
            private readonly List<ElementId> selected2D = new List<ElementId>();
            private readonly List<View3D> available3D;
            internal ExportOptions Options { get; private set; }

            internal OptionsWindow(UIDocument document, List<View3D> views, List<RevitView> views2D)
            {
                available3D = views; available2D = views2D;
                Title = "BV 导出"; Icon = Branding.Icon(32);
                Width = 770; Height = 830; MinWidth = 650; MinHeight = 530;
                MaxHeight = Math.Max(530, SystemParameters.WorkArea.Height - 24);
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                Background = Brushes.White;
                Foreground = ColorBrush(32, 47, 67);
                FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12;
                ShowInTaskbar = false;

                Grid layout = new Grid { Background = Brushes.White };
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(76) });
                layout.RowDefinitions.Add(new RowDefinition());
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(66) });
                Content = layout;

                Border header = new Border { Background = ColorBrush(17, 35, 55), Padding = new Thickness(22, 12, 22, 12) };
                StackPanel headerRow = new StackPanel { Orientation = Orientation.Horizontal };
                headerRow.Children.Add(new System.Windows.Controls.Image { Source = Branding.Icon(32), Width = 38, Height = 38, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
                StackPanel headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                headerText.Children.Add(new TextBlock { Text = "BV · 模型导出", Foreground = Brushes.White, FontSize = 19, FontWeight = FontWeights.SemiBold });
                headerText.Children.Add(new TextBlock { Text = "bright vision", Foreground = ColorBrush(77, 221, 190), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
                headerRow.Children.Add(headerText); header.Child = headerRow; layout.Children.Add(header);

                StackPanel body = new StackPanel { Margin = new Thickness(22, 16, 22, 16) };
                ScrollViewer scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                Grid.SetRow(scroll, 1); layout.Children.Add(scroll);

                body.Children.Add(Label("模型名称"));
                modelName.Text = SafeInitialName(document.Document.Title);
                StyleInput(modelName); body.Children.Add(modelName);
                body.Children.Add(Label("三维视图", 14));
                viewPicker.MinHeight = 30;
                int selected = 0;
                for (int i = 0; i < views.Count; i++)
                {
                    viewPicker.Items.Add(views[i].Name);
                    if (views[i].Id.Equals(document.ActiveView.Id)) selected = i;
                }
                viewPicker.SelectedIndex = selected;
                body.Children.Add(viewPicker);
                body.Children.Add(new TextBlock { Text = "按所选视图的可见范围导出，不修改当前视图。", Foreground = ColorBrush(102, 117, 134), Margin = new Thickness(0, 5, 0, 0) });
                body.Children.Add(Label("导出内容", 18));
                Border optionBorder = new Border { BorderBrush = ColorBrush(216, 224, 232), BorderThickness = new Thickness(1) };
                StackPanel rows = new StackPanel(); optionBorder.Child = rows; body.Children.Add(optionBorder);
                AddOption(rows, new CheckBox(), "几何信息", "所选视图中可见构件的三角网格", true, false);
                AddOption(rows, new CheckBox(), "构件信息", "构件标识、实例与类型参数、楼层关系", true, false);
                AddOption(rows, textures, "贴图纹理", "本地漫反射贴图；缺失贴图会列入导出提示", true, true);
                AddOption(rows, systems, "系统树", "按 Revit 机电系统及其构件成员组织", true, true);
                AddOption(rows, spaces, "空间树", "房间 / 空间及 Revit 已提供的构件归属", true, true);
                AddOption(rows, disciplines, "专业树", "根据构件类别整理专业分组", true, true);
                AddOption(rows, cad, "CAD 几何", "所选视图中可见的 CAD 网格；不打包原 DWG", true, true);
                AddOption(rows, grids, "轴网", "轴线名称与曲线数据", false, true);
                AddOption(rows, drawings, "二维视图（DWG）", "仅打包手动选择的视图；查看器暂不显示 DWG", false, views2D.Count > 0);
                DockPanel drawingRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                drawingSelect.Content = "选择二维视图…"; drawingSelect.Padding = new Thickness(12, 5, 12, 5); drawingSelect.IsEnabled = false;
                drawingSelect.Click += SelectDrawings;
                DockPanel.SetDock(drawingSelect, Dock.Right); drawingRow.Children.Add(drawingSelect);
                drawingSummary.Text = views2D.Count == 0 ? "当前工程没有可导出的二维视图。" : "尚未选择二维视图";
                drawingSummary.VerticalAlignment = VerticalAlignment.Center; drawingSummary.Foreground = ColorBrush(102, 117, 134);
                drawingRow.Children.Add(drawingSummary); body.Children.Add(drawingRow);
                drawings.Checked += delegate { drawingSelect.IsEnabled = true; };
                drawings.Unchecked += delegate { drawingSelect.IsEnabled = false; };

                body.Children.Add(Label("导出文件夹", 18));
                Grid folderRow = new Grid(); folderRow.ColumnDefinitions.Add(new ColumnDefinition()); folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                outputFolder.Text = InitialDirectory(document.Document.PathName);
                StyleInput(outputFolder); folderRow.Children.Add(outputFolder);
                Button browse = new Button { Content = "浏览…", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
                browse.Click += BrowseFolder; Grid.SetColumn(browse, 1); folderRow.Children.Add(browse); body.Children.Add(folderRow);
                targetSummary.TextWrapping = TextWrapping.Wrap; targetSummary.Foreground = ColorBrush(102, 117, 134); targetSummary.Margin = new Thickness(0, 7, 0, 0);
                body.Children.Add(targetSummary);
                modelName.TextChanged += delegate { UpdateTarget(); }; outputFolder.TextChanged += delegate { UpdateTarget(); }; UpdateTarget();

                Border footer = new Border { Background = ColorBrush(245, 247, 250), BorderBrush = ColorBrush(225, 231, 237), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(22, 13, 22, 13) };
                DockPanel footerRow = new DockPanel(); footer.Child = footerRow; Grid.SetRow(footer, 2); layout.Children.Add(footer);
                StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
                Button cancel = new Button { Content = "取消", Width = 82, Height = 32, IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
                Button export = new Button { Content = "导出 ZIP", Width = 100, Height = 32, IsDefault = true, Background = ColorBrush(36, 104, 184), Foreground = Brushes.White, BorderBrush = ColorBrush(36, 104, 184) };
                export.Click += Confirm; buttons.Children.Add(cancel); buttons.Children.Add(export); DockPanel.SetDock(buttons, Dock.Right); footerRow.Children.Add(buttons);
                footerRow.Children.Add(new TextBlock { Text = "完成后显示源 RVT 与导出 ZIP 体积", VerticalAlignment = VerticalAlignment.Center, Foreground = ColorBrush(102, 117, 134) });
            }

            private void Confirm(object sender, RoutedEventArgs e)
            {
                string name = modelName.Text.Trim();
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                if (!ValidFileName(name)) { ShowError("请输入有效的模型名称，不要包含路径或文件名保留字符。"); return; }
                string directory;
                if (!Path.IsPathRooted(outputFolder.Text.Trim())) { ShowError("请输入完整的导出文件夹路径，或点击浏览选择文件夹。"); return; }
                try { directory = Path.GetFullPath(outputFolder.Text.Trim()); }
                catch (Exception error) { if (!(error is ArgumentException) && !(error is NotSupportedException) && !(error is PathTooLongException)) throw; ShowError("导出文件夹路径无效。"); return; }
                if (!Directory.Exists(directory)) { ShowError("导出文件夹不存在，请选择或创建一个文件夹。"); return; }
                if (viewPicker.SelectedIndex < 0 || viewPicker.SelectedIndex >= available3D.Count) { ShowError("请选择要导出的三维视图。"); return; }
                if (drawings.IsChecked == true && selected2D.Count == 0) { ShowError("已勾选二维视图，请先选择要打包的视图。"); return; }
                string output = Path.Combine(directory, name + ".zip");
                if (File.Exists(output) && MessageBox.Show(this, "目标文件已存在，导出成功后将替换：\n" + output, "BV 导出", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                Options = new ExportOptions { ModelName = name, OutputPath = output, View = available3D[viewPicker.SelectedIndex],
                    IncludeTextures = textures.IsChecked == true, IncludeSystemTree = systems.IsChecked == true,
                    IncludeSpaceTree = spaces.IsChecked == true, IncludeDisciplineTree = disciplines.IsChecked == true,
                    IncludeCadLinks = cad.IsChecked == true, IncludeGrids = grids.IsChecked == true, Include2DViews = drawings.IsChecked == true,
                    Views2D = new List<ElementId>(selected2D) };
                DialogResult = true;
            }

            private void ShowError(string message) { MessageBox.Show(this, message, "BV 导出", MessageBoxButton.OK, MessageBoxImage.Information); }
            private void BrowseFolder(object sender, RoutedEventArgs e)
            {
                using (Forms.FolderBrowserDialog dialog = new Forms.FolderBrowserDialog())
                {
                    dialog.Description = "选择 BV 模型 ZIP 的保存文件夹";
                    if (Directory.Exists(outputFolder.Text)) dialog.SelectedPath = outputFolder.Text;
                    dialog.ShowNewFolderButton = true;
                    if (dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK) outputFolder.Text = dialog.SelectedPath;
                }
            }
            private void UpdateTarget() { targetSummary.Text = "保存为：" + outputFolder.Text.TrimEnd('\\', '/') + Path.DirectorySeparatorChar + modelName.Text + (modelName.Text.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "" : ".zip"); }

            private void SelectDrawings(object sender, RoutedEventArgs e)
            {
                Window picker = new Window { Title = "BV · 选择二维视图", Icon = Branding.Icon(32), Owner = this, Width = 530, Height = Math.Min(570, MaxHeight), MinHeight = 300,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = FontFamily, FontSize = 12, Background = Brushes.White };
                Grid grid = new Grid { Margin = new Thickness(16) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.Children.Add(new TextBlock { Text = "选择需要随 ZIP 打包的 DWG 视图", Margin = new Thickness(0, 0, 0, 10) });
                StackPanel list = new StackPanel(); List<CheckBox> checks = new List<CheckBox>();
                foreach (RevitView view in available2D)
                {
                    CheckBox check = new CheckBox { Content = view.Name + "  (" + view.ViewType + ")", IsChecked = selected2D.Contains(view.Id), Margin = new Thickness(4, 5, 4, 5) };
                    checks.Add(check); list.Children.Add(check);
                }
                ScrollViewer scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
                StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
                Button cancel = new Button { Content = "取消", Width = 80, Height = 30, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
                Button accept = new Button { Content = "确定", Width = 80, Height = 30, IsDefault = true };
                accept.Click += delegate { picker.DialogResult = true; }; actions.Children.Add(cancel); actions.Children.Add(accept); Grid.SetRow(actions, 2); grid.Children.Add(actions); picker.Content = grid;
                if (picker.ShowDialog() == true)
                {
                    selected2D.Clear();
                    for (int i = 0; i < checks.Count; i++) if (checks[i].IsChecked == true) selected2D.Add(available2D[i].Id);
                    drawingSummary.Text = "已选择 " + selected2D.Count + " 个二维视图";
                }
            }
        }

        private sealed class DialogOwner : Forms.IWin32Window
        {
            private readonly IntPtr handle;
            internal DialogOwner(IntPtr value) { handle = value; }
            public IntPtr Handle { get { return handle; } }
        }

        private static Brush ColorBrush(byte r, byte g, byte b) { return new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)); }
        private static TextBlock Label(string text) { return Label(text, 0); }
        private static TextBlock Label(string text, int top) { return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 7) }; }
        private static void StyleInput(TextBox input) { input.MinHeight = 30; input.Padding = new Thickness(6, 4, 6, 4); input.VerticalContentAlignment = VerticalAlignment.Center; }
        private static void AddOption(Panel rows, CheckBox check, string title, string description, bool enabledByDefault, bool editable)
        {
            Grid row = new Grid { Margin = new Thickness(10, 0, 10, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(174) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            check.Content = title; check.IsChecked = enabledByDefault; check.IsEnabled = editable; check.VerticalAlignment = VerticalAlignment.Center; check.Margin = new Thickness(0, 8, 8, 8);
            row.Children.Add(check);
            TextBlock note = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = ColorBrush(102, 117, 134), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
            Grid.SetColumn(note, 1); row.Children.Add(note); rows.Children.Add(new Border { Child = row, BorderBrush = ColorBrush(232, 237, 242), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
        private static string InitialDirectory(string path)
        {
            try { if (!String.IsNullOrWhiteSpace(path) && File.Exists(path)) { string folder = Path.GetDirectoryName(path); if (Directory.Exists(folder)) return folder; } }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        private static string SafeInitialName(string title)
        {
            string name = String.IsNullOrWhiteSpace(title) ? "模型" : title;
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            name = name.Trim().TrimEnd('.');
            return ValidFileName(name) ? name : "模型";
        }
        private static bool ValidFileName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 128 || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return false;
            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '0' && stem[3] <= '9') return false;
            return true;
        }
    }
}
