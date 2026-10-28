using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrightVision.Civil;

namespace BrightVision.Revit
{
    // Edits an isolated copy. Cancel never changes the caller's settings or RVT.
    internal sealed class CivilProjectWindow : Window
    {
        private readonly CivilSnapshot snapshot;
        private readonly List<CivilElementRecord> selected;
        private readonly TextBox projectName = new TextBox();
        private readonly ListBox levelList = new ListBox();
        private readonly ListBox nativeLevels = new ListBox();
        private readonly ComboBox mappingTarget = new ComboBox();
        private readonly TextBox levelName = new TextBox();
        private readonly TextBox elevation = new TextBox();
        private readonly ListBox segmentList = new ListBox();
        private readonly TextBox segmentName = new TextBox();
        private readonly TextBlock message = new TextBlock();
        private readonly TextBlock assignments = new TextBlock();
        private readonly CivilProjectSettings draft;
        internal CivilProjectSettings Result { get; private set; }

        internal CivilProjectWindow(CivilSnapshot snapshot, CivilProjectSettings profile, IList<CivilElementRecord> selectedElements)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            this.snapshot = snapshot;
            draft = CivilProject.Clone(profile ?? CivilProject.Create(snapshot, snapshot.ModelTitle));
            selected = (selectedElements ?? new List<CivilElementRecord>()).ToList();
            Title = "BV · 计量工程设置";
            Icon = Branding.Icon(32); Width = 980; Height = 710; MinWidth = 840; MinHeight = 590;
            MaxHeight = Math.Max(590, SystemParameters.WorkArea.Height - 24);
            ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12; Background = Brushes.White;
            Grid root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            StackPanel introduction = new StackPanel();
            introduction.Children.Add(new TextBlock { Text = "计量工程设置", FontSize = 20, FontWeight = FontWeights.SemiBold });
            introduction.Children.Add(new TextBlock { Text = "归属设置保存在独立工程 JSON；不写入 RVT，不切分几何，不改变原生长度、面积或体积。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12), Foreground = Brushes.DimGray });
            root.Children.Add(introduction);
            projectName.Text = draft.Name; projectName.Padding = new Thickness(6); projectName.Margin = new Thickness(0, 0, 0, 12);
            projectName.ToolTip = "计量工程名称"; Grid.SetRow(projectName, 1); root.Children.Add(projectName);
            TabControl tabs = new TabControl(); Grid.SetRow(tabs, 2); root.Children.Add(tabs);
            Grid levels = new Grid { Margin = new Thickness(12) };
            levels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star) });
            levels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            StackPanel editLevel = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
            editLevel.Children.Add(Label("计量层（业务归属，不是几何裁切平面）"));
            levelList.DisplayMemberPath = "Name"; levelList.Height = 155; levelList.SelectionChanged += delegate {
                CivilQuantityLevel level = levelList.SelectedItem as CivilQuantityLevel;
                levelName.Text = level == null ? "" : level.Name;
                elevation.Text = level == null || !level.ElevationMetres.HasValue ? "" : level.ElevationMetres.Value.ToString("R", CultureInfo.CurrentCulture);
            }; editLevel.Children.Add(levelList);
            editLevel.Children.Add(Label("名称")); editLevel.Children.Add(levelName);
            editLevel.Children.Add(Label("参考标高（米，可留空）")); editLevel.Children.Add(elevation);
            WrapPanel levelButtons = new WrapPanel();
            levelButtons.Children.Add(Action("新增", delegate { SetLevel(null, levelName.Text, ParseElevation()); }));
            levelButtons.Children.Add(Action("修改选中", delegate {
                CivilQuantityLevel item = levelList.SelectedItem as CivilQuantityLevel;
                if (item == null) throw new InvalidOperationException("请先选择要修改的计量层。");
                SetLevel(item.Id, levelName.Text, ParseElevation());
            }));
            levelButtons.Children.Add(Action("删除选中", delegate {
                CivilQuantityLevel item = levelList.SelectedItem as CivilQuantityLevel;
                if (item == null) return;
                draft.LevelMappings.RemoveAll(m => m.QuantityLevelId == item.Id); draft.QuantityLevels.Remove(item); RefreshLists();
            })); editLevel.Children.Add(levelButtons);
            editLevel.Children.Add(Action("按原生楼层补齐计量层和映射", ImportNativeLevels));
            levels.Children.Add(editLevel);
            StackPanel mapping = new StackPanel(); Grid.SetColumn(mapping, 1); levels.Children.Add(mapping);
            mapping.Children.Add(Label("原生楼层 → 计量层（支持 Ctrl / Shift 多选）"));
            nativeLevels.SelectionMode = SelectionMode.Extended; nativeLevels.DisplayMemberPath = "Label"; nativeLevels.Height = 225; mapping.Children.Add(nativeLevels);
            mapping.Children.Add(Label("目标计量层")); mappingTarget.DisplayMemberPath = "Name"; mappingTarget.MinHeight = 30; mapping.Children.Add(mappingTarget);
            WrapPanel mapButtons = new WrapPanel();
            mapButtons.Children.Add(Action("映射选中楼层", delegate {
                CivilQuantityLevel target = mappingTarget.SelectedItem as CivilQuantityLevel;
                if (target == null) throw new InvalidOperationException("请先选择目标计量层。");
                MapLevels(nativeLevels.SelectedItems.Cast<NativeLevelRow>().Select(l => l.Id).ToList(), target.Id);
            }));
            mapButtons.Children.Add(Action("清除选中映射", delegate { MapLevels(nativeLevels.SelectedItems.Cast<NativeLevelRow>().Select(l => l.Id).ToList(), ""); })); mapping.Children.Add(mapButtons);
            mapping.Children.Add(new TextBlock { Text = "无明确原生参照楼层的构件保持未映射。一个原生楼层只归属一个计量层，可将多个原生楼层合并到同一计量层。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0) });
            tabs.Items.Add(new TabItem { Header = "计量层与映射", Content = levels });
            Grid segments = new Grid { Margin = new Thickness(12) };
            segments.ColumnDefinitions.Add(new ColumnDefinition()); segments.ColumnDefinitions.Add(new ColumnDefinition());
            StackPanel segmentEditor = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
            segmentEditor.Children.Add(Label("施工段")); segmentList.DisplayMemberPath = "Name"; segmentList.Height = 220;
            segmentList.SelectionChanged += delegate { CivilSegment item = segmentList.SelectedItem as CivilSegment; segmentName.Text = item == null ? "" : item.Name; };
            segmentEditor.Children.Add(segmentList); segmentEditor.Children.Add(Label("施工段名称")); segmentEditor.Children.Add(segmentName);
            WrapPanel segmentButtons = new WrapPanel();
            segmentButtons.Children.Add(Action("新增", delegate { SetSegment(null, segmentName.Text); }));
            segmentButtons.Children.Add(Action("修改选中", delegate {
                CivilSegment item = segmentList.SelectedItem as CivilSegment; if (item == null) throw new InvalidOperationException("请先选择施工段。"); SetSegment(item.Id, segmentName.Text);
            }));
            segmentButtons.Children.Add(Action("删除选中", delegate {
                CivilSegment item = segmentList.SelectedItem as CivilSegment; if (item == null) return;
                draft.ElementSegments.RemoveAll(a => a.SegmentId == item.Id); draft.Segments.Remove(item); RefreshLists();
            })); segmentEditor.Children.Add(segmentButtons); segments.Children.Add(segmentEditor);
            StackPanel assign = new StackPanel(); Grid.SetColumn(assign, 1); segments.Children.Add(assign);
            assign.Children.Add(Label("按工作台选中的构件分配"));
            assign.Children.Add(new TextBlock { Text = "本次选中 " + selected.Count + " 个来源构件。选中汇总行时包含该行的全部构件。更换选中范围请取消本窗口，返回量表选择。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
            assign.Children.Add(Action("分配到左侧选中施工段", delegate {
                CivilSegment item = segmentList.SelectedItem as CivilSegment; if (item == null) throw new InvalidOperationException("请先选择施工段。"); AssignSelected(item.Id);
            }));
            assign.Children.Add(Action("清除本次选中构件的施工段", delegate { AssignSelected(""); }));
            assignments.TextWrapping = TextWrapping.Wrap; assignments.Margin = new Thickness(0, 15, 0, 0); assign.Children.Add(assignments);
            assign.Children.Add(new TextBlock { Text = "每个构件仅归属一个施工段。分配只改变报表归属；不拆分跨层、跨段构件。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 15, 0, 0) });
            tabs.Items.Add(new TabItem { Header = "施工段与构件归属", Content = segments });
            message.TextWrapping = TextWrapping.Wrap; message.Foreground = Brushes.Firebrick; message.Margin = new Thickness(0, 10, 0, 8); Grid.SetRow(message, 3); root.Children.Add(message);
            StackPanel footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(0, 0, 8, 0) }; footer.Children.Add(cancel);
            footer.Children.Add(Action("应用设置", delegate { CompleteSettings(); DialogResult = true; }));
            Grid.SetRow(footer, 4); root.Children.Add(footer); RefreshLists();
        }

        internal void ImportNativeLevels()
        {
            foreach (CivilLevelInfo native in snapshot.Levels.Where(l => !String.IsNullOrEmpty(l.UniqueId)))
            {
                if (draft.LevelMappings.Any(m => m.SourceLevelUniqueId == native.UniqueId)) continue;
                CivilQuantityLevel level = new CivilQuantityLevel { Id = Guid.NewGuid().ToString("N"), Name = native.Name, ElevationMetres = native.ElevationMetres };
                draft.QuantityLevels.Add(level); draft.LevelMappings.Add(new CivilLevelMapping { SourceLevelUniqueId = native.UniqueId, QuantityLevelId = level.Id });
            }
            RefreshLists();
        }
        internal string SetLevel(string id, string name, double? elevationMetres)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("请填写计量层名称。");
            if (elevationMetres.HasValue && (Double.IsNaN(elevationMetres.Value) || Double.IsInfinity(elevationMetres.Value))) throw new InvalidOperationException("参考标高必须为有限数值。");
            CivilQuantityLevel item = draft.QuantityLevels.FirstOrDefault(l => l.Id == id);
            if (item == null) { item = new CivilQuantityLevel { Id = Guid.NewGuid().ToString("N") }; draft.QuantityLevels.Add(item); }
            item.Name = name.Trim(); item.ElevationMetres = elevationMetres; RefreshLists(); levelList.SelectedItem = item; return item.Id;
        }
        internal void MapLevels(IList<string> sourceIds, string targetId)
        {
            if (sourceIds.Count == 0) throw new InvalidOperationException("请先选择原生楼层。");
            if (!String.IsNullOrEmpty(targetId) && !draft.QuantityLevels.Any(l => l.Id == targetId)) throw new InvalidOperationException("目标计量层不存在。");
            List<string> ids = sourceIds.Distinct(StringComparer.Ordinal).ToList();
            if (ids.Any(id => !snapshot.Levels.Any(l => l.UniqueId == id))) throw new InvalidOperationException("来源楼层不存在。");
            foreach (string id in ids)
            {
                draft.LevelMappings.RemoveAll(m => m.SourceLevelUniqueId == id);
                if (!String.IsNullOrEmpty(targetId)) draft.LevelMappings.Add(new CivilLevelMapping { SourceLevelUniqueId = id, QuantityLevelId = targetId });
            }
            RefreshLists();
        }
        internal string SetSegment(string id, string name)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("请填写施工段名称。");
            CivilSegment item = draft.Segments.FirstOrDefault(s => s.Id == id);
            if (item == null) { item = new CivilSegment { Id = Guid.NewGuid().ToString("N") }; draft.Segments.Add(item); }
            item.Name = name.Trim(); RefreshLists(); segmentList.SelectedItem = item; return item.Id;
        }
        internal void AssignSelected(string segmentId)
        {
            if (selected.Count == 0) throw new InvalidOperationException("请先在量表中选择需要分配的构件。");
            CivilProject.AssignSegment(draft, selected, segmentId); RefreshLists();
        }
        internal CivilProjectSettings CompleteSettings()
        {
            draft.Name = projectName.Text.Trim(); CivilProject.Validate(draft); CivilProject.Touch(draft);
            // Identity is checked before this dialog can return a result.
            CivilProject.Apply(snapshot, draft); Result = CivilProject.Clone(draft); return Result;
        }
        private void RefreshLists()
        {
            levelList.ItemsSource = draft.QuantityLevels.ToList(); mappingTarget.ItemsSource = draft.QuantityLevels.ToList();
            segmentList.ItemsSource = draft.Segments.ToList();
            nativeLevels.ItemsSource = snapshot.Levels.Select(l => new NativeLevelRow { Id = l.UniqueId, Label = l.Name + "  (" + (l.ElevationMetres.HasValue ? l.ElevationMetres.Value.ToString("0.###", CultureInfo.CurrentCulture) + " m" : "标高不可读") + ") → " + MappingName(l.UniqueId) }).ToList();
            assignments.Text = "工程中已保存 " + draft.ElementSegments.Count + " 条构件归属；当前草稿有 " + draft.QuantityLevels.Count + " 个计量层、" + draft.Segments.Count + " 个施工段。";
        }
        private string MappingName(string sourceId)
        {
            CivilLevelMapping mapping = draft.LevelMappings.FirstOrDefault(m => m.SourceLevelUniqueId == sourceId);
            CivilQuantityLevel target = mapping == null ? null : draft.QuantityLevels.FirstOrDefault(l => l.Id == mapping.QuantityLevelId);
            return target == null ? "未映射" : target.Name;
        }
        private double? ParseElevation()
        {
            if (String.IsNullOrWhiteSpace(elevation.Text)) return null;
            double value; if (!Double.TryParse(elevation.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) throw new InvalidOperationException("参考标高请输入米制数值。"); return value;
        }
        private Button Action(string text, System.Action action)
        {
            Button button = new Button { Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 6, 7, 0) };
            button.Click += delegate { try { action(); message.Text = ""; } catch (Exception error) { if (error is OutOfMemoryException) throw; message.Text = error.Message; } }; return button;
        }
        private static TextBlock Label(string text) { return new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 5), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap }; }
        private sealed class NativeLevelRow { public string Id { get; set; } public string Label { get; set; } }
    }
}
