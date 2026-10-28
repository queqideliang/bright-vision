using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BrightVision.Civil;
using Microsoft.Win32;

namespace BrightVision.Revit
{
    // A modal, read-only presentation of native-parameter facts. All Revit API
    // access is supplied by the external command's read delegate. Focus is
    // returned as source records only after this modal window has closed.
    internal sealed class CivilQuantityWindow : Window
    {
        private readonly Func<CivilReadRequest, CivilSnapshot> read;
        private readonly Func<long> getRevision;
        private readonly ComboBox scopePicker = new ComboBox();
        private readonly ComboBox phasePicker = new ComboBox();
        private readonly ComboBox optionPicker = new ComboBox();
        private readonly ComboBox kindPicker = new ComboBox();
        private readonly ComboBox levelPicker = new ComboBox();
        private readonly ComboBox quantityLevelPicker = new ComboBox();
        private readonly ComboBox segmentPicker = new ComboBox();
        private readonly ComboBox groupPicker = new ComboBox();
        private readonly TextBox search = new TextBox();
        private readonly CheckBox issuesOnly = new CheckBox();
        private readonly Button refreshButton = new Button();
        private readonly Button csvButton = new Button();
        private readonly Button focusButton = new Button();
        private readonly Button projectButton = new Button();
        private readonly Button loadProjectButton = new Button();
        private readonly Button saveProjectButton = new Button();
        private readonly TextBlock projectStatus = new TextBlock();
        private readonly TextBlock status = new TextBlock();
        private readonly TextBlock summary = new TextBlock();
        private readonly TextBlock scopeSummary = new TextBlock();
        private readonly TextBlock totals = new TextBlock();
        private readonly TextBlock notices = new TextBlock();
        private readonly TabControl tabs = new TabControl();
        private readonly DataGrid quantities = new DataGrid();
        private readonly DataGrid problems = new DataGrid();
        private readonly DispatcherTimer revisionTimer = new DispatcherTimer();
        private List<CivilElementRecord> filteredElements = new List<CivilElementRecord>();
        private List<ProblemRow> problemRows = new List<ProblemRow>();
        private bool initializing = true;
        private bool refreshing;
        private bool initialLoadAttempted;
        private bool stale = true;
        private string successfulRequest;
        private CivilSnapshot nativeSnapshot;
        private CivilProjectSettings currentProject;
        private string projectPath;
        private bool projectDirty;

        public IList<CivilElementRecord> SelectedFocusElements { get; private set; }
        public CivilSnapshot LastSnapshot { get; private set; }
        internal CivilProjectSettings CurrentProject { get { return currentProject == null ? null : CivilProject.Clone(currentProject); } }

        internal CivilQuantityWindow(CivilReadRequest initialRequest, IList<CivilChoice> phases,
            IList<CivilChoice> options, Func<CivilReadRequest, CivilSnapshot> read, Func<long> getRevision)
        {
            if (initialRequest == null) throw new ArgumentNullException("initialRequest");
            if (read == null) throw new ArgumentNullException("read");
            if (getRevision == null) throw new ArgumentNullException("getRevision");
            this.read = read;
            this.getRevision = getRevision;
            SelectedFocusElements = new List<CivilElementRecord>();
            Title = "BV · 土建原始量工作台";
            Icon = Branding.Icon(32);
            Width = 1300; Height = 900; MinWidth = 1050; MinHeight = 720;
            MaxHeight = Math.Max(620, SystemParameters.WorkArea.Height - 24);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = Brushes.White;
            Foreground = Brush(30, 47, 65);
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 12;

            Grid layout = new Grid { Background = Brushes.White };
            foreach (GridLength height in new[] { new GridLength(80), GridLength.Auto, GridLength.Auto,
                GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
                layout.RowDefinitions.Add(new RowDefinition { Height = height });
            Content = layout;

            Border header = new Border { Background = Brush(17, 35, 55), Padding = new Thickness(20, 12, 20, 12) };
            DockPanel headerContent = new DockPanel();
            headerContent.Children.Add(new Image { Source = Branding.Icon(32), Width = 36, Height = 36,
                Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            StackPanel heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(new TextBlock { Text = "土建原始量工作台", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            heading.Children.Add(new TextBlock { Text = "bright vision  ·  Revit 原生参数量", Foreground = Brush(99, 225, 198), Margin = new Thickness(0, 3, 0, 0) });
            headerContent.Children.Add(heading); header.Child = headerContent; layout.Children.Add(header);

            Grid request = new Grid { Margin = new Thickness(20, 14, 20, 0) };
            foreach (double width in new[] { 150.0, 230.0, 270.0 }) request.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            request.ColumnDefinitions.Add(new ColumnDefinition());
            ConfigureChoice(scopePicker, new List<CivilChoice> {
                new CivilChoice { Id = "view", Label = "当前视图" },
                new CivilChoice { Id = "selection", Label = "当前选择" },
                new CivilChoice { Id = "project", Label = "当前工程（根文档）" }
            }, initialRequest.Scope);
            ConfigureChoice(phasePicker, phases ?? new List<CivilChoice>(), initialRequest.PhaseId);
            ConfigureChoice(optionPicker, options ?? new List<CivilChoice>(), initialRequest.DesignOptionId);
            request.Children.Add(Labeled("读取范围", scopePicker));
            FrameworkElement phaseControl = Labeled("阶段（现存 / 新建）", phasePicker); Grid.SetColumn(phaseControl, 1); request.Children.Add(phaseControl);
            FrameworkElement optionControl = Labeled("设计选项", optionPicker); Grid.SetColumn(optionControl, 2); request.Children.Add(optionControl);
            refreshButton.Content = "刷新原始量"; refreshButton.Padding = new Thickness(16, 7, 16, 7);
            refreshButton.HorizontalAlignment = HorizontalAlignment.Right; refreshButton.VerticalAlignment = VerticalAlignment.Bottom;
            refreshButton.Background = Brush(37, 102, 181); refreshButton.Foreground = Brushes.White; refreshButton.BorderBrush = refreshButton.Background;
            refreshButton.Click += delegate { RefreshSnapshot(); };
            Grid.SetColumn(refreshButton, 3); request.Children.Add(refreshButton); Grid.SetRow(request, 1); layout.Children.Add(request);

            StackPanel context = new StackPanel { Margin = new Thickness(20, 10, 20, 0) };
            context.Children.Add(new TextBlock { Text = "原始量保留 Revit 参数口径，不是清单 / 定额净量、模板量或建筑面积；视图剖切不会裁算整构件量。",
                Foreground = Brush(112, 77, 23), TextWrapping = TextWrapping.Wrap });
            scopeSummary.Text = "范围、阶段与设计选项须明确选择后读取。";
            scopeSummary.TextWrapping = TextWrapping.Wrap; scopeSummary.Foreground = Brush(92, 108, 126); scopeSummary.Margin = new Thickness(0, 5, 0, 0);
            context.Children.Add(scopeSummary);
            status.Text = "尚未读取数据。"; status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 5, 0, 0); context.Children.Add(status);
            WrapPanel projectBar = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
            projectButton.Content = "计量工程设置…"; projectButton.Padding = new Thickness(10, 5, 10, 5);
            projectButton.Click += delegate { EditProject(); }; projectBar.Children.Add(projectButton);
            loadProjectButton.Content = "打开工程…"; loadProjectButton.Padding = new Thickness(10, 5, 10, 5); loadProjectButton.Margin = new Thickness(7, 0, 0, 0);
            loadProjectButton.Click += delegate { OpenProjectDialog(); }; projectBar.Children.Add(loadProjectButton);
            saveProjectButton.Content = "保存工程…"; saveProjectButton.Padding = new Thickness(10, 5, 10, 5); saveProjectButton.Margin = new Thickness(7, 0, 0, 0);
            saveProjectButton.Click += delegate { SaveProjectDialog(); }; projectBar.Children.Add(saveProjectButton);
            projectStatus.Margin = new Thickness(10, 7, 0, 0); projectStatus.Foreground = Brush(76, 96, 116); projectBar.Children.Add(projectStatus);
            context.Children.Add(projectBar);
            Grid.SetRow(context, 2); layout.Children.Add(context);

            Grid filters = new Grid { Margin = new Thickness(20, 13, 20, 12) };
            filters.ColumnDefinitions.Add(new ColumnDefinition());
            foreach (double width in new[] { 160.0, 180.0, 130.0, 100.0 }) filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            search.MinHeight = 31; search.Padding = new Thickness(8, 5, 8, 5);
            search.ToolTip = "搜索类别、类型、名称、原始楼层或构件 ID";
            filters.Children.Add(Labeled("搜索名称 / 类型 / 楼层 / ID", search));
            ConfigureChoice(kindPicker, new List<CivilChoice> { new CivilChoice { Id = "", Label = "全部类别" } }, "");
            ConfigureChoice(levelPicker, new List<CivilChoice> { new CivilChoice { Id = "", Label = "全部原始楼层" } }, "");
            ConfigureChoice(groupPicker, new List<CivilChoice> {
                new CivilChoice { Id = "element", Label = "构件明细" }, new CivilChoice { Id = "kind", Label = "按类别汇总" }, new CivilChoice { Id = "level", Label = "按原生楼层" },
                new CivilChoice { Id = "quantity-level", Label = "按计量层汇总" }, new CivilChoice { Id = "segment", Label = "按施工段汇总" }
            }, "element");
            FrameworkElement kindControl = Labeled("业务类别", kindPicker); Grid.SetColumn(kindControl, 1); filters.Children.Add(kindControl);
            FrameworkElement levelControl = Labeled("原生参照楼层", levelPicker); Grid.SetColumn(levelControl, 2); filters.Children.Add(levelControl);
            FrameworkElement groupControl = Labeled("表格方式", groupPicker); Grid.SetColumn(groupControl, 3); filters.Children.Add(groupControl);
            issuesOnly.Content = "仅问题"; issuesOnly.VerticalAlignment = VerticalAlignment.Bottom; issuesOnly.Margin = new Thickness(10, 0, 0, 8);
            Grid.SetColumn(issuesOnly, 4); filters.Children.Add(issuesOnly);
            filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WrapPanel projectFilters = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            ConfigureChoice(quantityLevelPicker, new List<CivilChoice> { new CivilChoice { Id = "", Label = "全部计量层" } }, "");
            ConfigureChoice(segmentPicker, new List<CivilChoice> { new CivilChoice { Id = "", Label = "全部施工段" } }, "");
            quantityLevelPicker.Width = 220; segmentPicker.Width = 220;
            projectFilters.Children.Add(Labeled("计量层", quantityLevelPicker)); projectFilters.Children.Add(Labeled("施工段", segmentPicker));
            Grid.SetRow(projectFilters, 1); Grid.SetColumnSpan(projectFilters, 5); filters.Children.Add(projectFilters);
            Grid.SetRow(filters, 3); layout.Children.Add(filters);

            Grid tables = new Grid { Margin = new Thickness(20, 0, 20, 0) };
            tables.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tables.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tables.RowDefinitions.Add(new RowDefinition());
            summary.FontWeight = FontWeights.SemiBold; summary.Margin = new Thickness(0, 0, 0, 6); tables.Children.Add(summary);
            totals.TextWrapping = TextWrapping.Wrap; totals.Foreground = Brush(74, 94, 115); totals.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(totals, 1); tables.Children.Add(totals);
            ConfigureGrid(quantities); ConfigureGrid(problems);
            AddColumn(quantities, "构件 / 分组", "Label", 185);
            AddColumn(quantities, "构件 ID", "ElementId", 80);
            AddColumn(quantities, "业务类别", "KindName", 95);
            AddColumn(quantities, "原生参照楼层", "LevelName", 105);
            AddColumn(quantities, "计量层", "QuantityLevelName", 105);
            AddColumn(quantities, "施工段", "SegmentName", 105);
            AddColumn(quantities, "对象 / 纳入", "CountText", 80);
            AddColumn(quantities, "原生长度 m", "LengthText", 100);
            AddColumn(quantities, "原生面积 m²", "AreaText", 100);
            AddColumn(quantities, "原生体积 m³", "VolumeText", 100);
            AddColumn(quantities, "状态 / 问题", "IssueText", 245);
            Style rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Detail")));
            quantities.RowStyle = rowStyle;
            quantities.SelectionChanged += delegate { UpdateActions(); };
            AddColumn(problems, "来源", "Origin", 105);
            AddColumn(problems, "构件 ID", "ElementId", 95);
            AddColumn(problems, "构件", "Name", 200);
            AddColumn(problems, "问题 / 排除原因", "Message", 650);
            Style problemStyle = new Style(typeof(DataGridRow));
            problemStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Message")));
            problems.RowStyle = problemStyle;
            problems.SelectionChanged += delegate { UpdateActions(); };
            tabs.Items.Add(new TabItem { Header = "原始量表", Content = quantities });
            Grid issuePanel = new Grid(); issuePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); issuePanel.RowDefinitions.Add(new RowDefinition());
            notices.TextWrapping = TextWrapping.Wrap; notices.Foreground = Brush(108, 86, 46); notices.Margin = new Thickness(8);
            issuePanel.Children.Add(notices); Grid.SetRow(problems, 1); issuePanel.Children.Add(problems);
            tabs.Items.Add(new TabItem { Header = "模型检查", Content = issuePanel });
            tabs.SelectionChanged += delegate { UpdateActions(); };
            Grid.SetRow(tabs, 2); tables.Children.Add(tabs); Grid.SetRow(tables, 4); layout.Children.Add(tables);

            Border footer = new Border { Background = Brush(245, 247, 250), Padding = new Thickness(20, 12, 20, 12), Margin = new Thickness(0, 12, 0, 0),
                BorderBrush = Brush(222, 229, 237), BorderThickness = new Thickness(0, 1, 0, 0) };
            DockPanel actions = new DockPanel();
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            csvButton.Content = "导出当前量表 CSV…"; csvButton.Padding = new Thickness(12, 6, 12, 6); csvButton.Margin = new Thickness(0, 0, 8, 0); csvButton.Click += SaveCsv;
            focusButton.Content = "定位选中构件并关闭"; focusButton.Padding = new Thickness(12, 6, 12, 6); focusButton.Margin = new Thickness(0, 0, 8, 0);
            focusButton.Click += delegate { if (PrepareFocus()) Close(); };
            Button close = new Button { Content = "关闭", Padding = new Thickness(16, 6, 16, 6), IsCancel = true }; close.Click += delegate { Close(); };
            buttons.Children.Add(csvButton); buttons.Children.Add(focusButton); buttons.Children.Add(close); DockPanel.SetDock(buttons, Dock.Right); actions.Children.Add(buttons);
            actions.Children.Add(new TextBlock { Text = "只读模型 · 工程设置另存 JSON", Foreground = Brush(102, 118, 135), VerticalAlignment = VerticalAlignment.Center });
            footer.Child = actions; Grid.SetRow(footer, 5); layout.Children.Add(footer);

            scopePicker.SelectionChanged += RequestChanged; phasePicker.SelectionChanged += RequestChanged; optionPicker.SelectionChanged += RequestChanged;
            search.TextChanged += delegate { ApplyFilters(); };
            kindPicker.SelectionChanged += delegate { ApplyFilters(); }; levelPicker.SelectionChanged += delegate { ApplyFilters(); }; groupPicker.SelectionChanged += delegate { ApplyFilters(); };
            quantityLevelPicker.SelectionChanged += delegate { ApplyFilters(); }; segmentPicker.SelectionChanged += delegate { ApplyFilters(); };
            issuesOnly.Checked += delegate { ApplyFilters(); }; issuesOnly.Unchecked += delegate { ApplyFilters(); };
            revisionTimer.Interval = TimeSpan.FromMilliseconds(800); revisionTimer.Tick += delegate { CheckFreshness(); };
            Loaded += delegate { if (!initialLoadAttempted) RefreshSnapshot(); revisionTimer.Start(); };
            Closed += delegate { revisionTimer.Stop(); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args) {
                if (projectDirty && MessageBox.Show(this, "计量工程设置尚未保存到 JSON。是否放弃本次未保存的设置并关闭？", "BV · 未保存的工程设置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) args.Cancel = true;
            };
            initializing = false;
            UpdateActions();
        }

        internal void RefreshSnapshot()
        {
            if (refreshing) return;
            initialLoadAttempted = true;
            refreshing = true; stale = true; UpdateActions(); refreshButton.IsEnabled = false;
            status.Text = "正在读取原生参数…"; status.Foreground = Brush(76, 96, 116);
            try
            {
                CivilReadRequest request = CurrentRequest();
                CivilSnapshot snapshot = read(request);
                if (snapshot == null) throw new InvalidDataException("未取得完整的读取结果。");
                CivilSnapshot applied = currentProject == null ? snapshot : CivilProject.Apply(snapshot, currentProject);
                nativeSnapshot = snapshot; LastSnapshot = applied;
                successfulRequest = RequestKey(request);
                initializing = true;
                ConfigureChoice(kindPicker, new[] { new CivilChoice { Id = "", Label = "全部类别" } }.Concat(snapshot.Elements
                    .GroupBy(e => e.KindCode ?? "", StringComparer.Ordinal).Where(g => g.Key.Length > 0)
                    .Select(g => new CivilChoice { Id = g.Key, Label = g.First().KindName }).OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)).ToList(), "");
                ConfigureChoice(levelPicker, new[] { new CivilChoice { Id = "", Label = "全部原生楼层" } }.Concat(snapshot.Elements
                    .Where(e => !String.IsNullOrEmpty(e.LevelId)).GroupBy(e => e.LevelId, StringComparer.Ordinal)
                    .Select(g => new CivilChoice { Id = g.Key, Label = String.IsNullOrEmpty(g.First().LevelName) ? "楼层 " + g.Key : g.First().LevelName })
                    .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)).ToList(), "");
                initializing = false;
                scopeSummary.Text = snapshot.ModelTitle + "  ·  " + snapshot.ScopeLabel + "  ·  阶段：" + snapshot.PhaseLabel + "  ·  设计选项：" + snapshot.DesignOptionLabel;
                stale = getRevision() != snapshot.Revision;
                status.Text = stale ? "读取期间模型已变化，请重新刷新。" : "读取时间：" + snapshot.CapturedAtUtc + "  ·  原始量仅包含当前列出的受支持对象。";
                status.Foreground = stale ? Brush(163, 96, 25) : Brush(39, 111, 89);
                ConfigureProjectFilters();
                ApplyFilters();
            }
            catch (Exception error)
            {
                if (error is OutOfMemoryException) throw;
                stale = true;
                status.Text = "刷新未完成：" + error.Message + (LastSnapshot == null ? "" : "  原有数据已过期，请刷新后再导出或定位。");
                status.Foreground = Brush(164, 58, 48);
            }
            finally
            {
                initializing = false; refreshing = false; refreshButton.IsEnabled = true; UpdateActions();
            }
        }

        internal void ApplyFilters()
        {
            if (initializing || LastSnapshot == null) return;
            filteredElements = CivilSchedule.FilterProject(LastSnapshot.Elements, search.Text, ChoiceId(kindPicker), ChoiceId(levelPicker), issuesOnly.IsChecked == true, ProjectFilterId(quantityLevelPicker), ProjectFilterId(segmentPicker));
            if (ChoiceId(quantityLevelPicker) == "__unassigned__") filteredElements = filteredElements.Where(e => String.IsNullOrEmpty(e.QuantityLevelId)).ToList();
            if (ChoiceId(segmentPicker) == "__unassigned__") filteredElements = filteredElements.Where(e => String.IsNullOrEmpty(e.SegmentId)).ToList();
            List<CivilScheduleRow> rows = CivilSchedule.BuildRows(filteredElements, GroupMode);
            quantities.ItemsSource = rows.Select(r => new QuantityRow(r)).ToList();
            CivilScheduleRow total = CivilSchedule.Summarize(filteredElements);
            summary.Text = "当前筛选：" + total.Count + " 个对象，纳入原始量 " + total.SupportedCount + " 个  ·  候选 " + LastSnapshot.CandidateCount
                + "  ·  排除 " + LastSnapshot.Exclusions.Count + "  ·  根文档外链接 " + LastSnapshot.LinkInstanceCount;
            totals.Text = "原生长度 " + MetricText(total.Length) + " m    原生面积 " + MetricText(total.Area) + " m²    原生体积 " + MetricText(total.Volume)
                + " m³    （NA 不适用；* 仅汇总有效项；悬停行查看覆盖和参数来源）";
            problemRows = new List<ProblemRow>();
            foreach (CivilScheduleRow checkedRow in CivilSchedule.BuildRows(filteredElements, "element"))
            {
                CivilElementRecord element = checkedRow.Elements[0];
                if (!String.IsNullOrEmpty(checkedRow.Issues))
                    problemRows.Add(new ProblemRow("构件检查", element.ElementId, element.Name, checkedRow.Issues, element));
            }
            // Scope exclusions are always visible: filtering the quantity table
            // must not hide a missing link or an explicitly excluded input.
            foreach (CivilExclusion exclusion in LastSnapshot.Exclusions)
            {
                CivilElementRecord record = LastSnapshot.Elements.FirstOrDefault(e => e.ElementId == exclusion.ElementId);
                problemRows.Add(new ProblemRow("范围排除", exclusion.ElementId, exclusion.Name, exclusion.Reason, record));
            }
            problems.ItemsSource = problemRows;
            notices.Text = (LastSnapshot.Notices.Count == 0 ? "本次读取无额外范围提示。" : String.Join("\n", LastSnapshot.Notices.ToArray()))
                + (LastSnapshot.MissingLinkCount > 0 ? "\n未加载链接：" + LastSnapshot.MissingLinkCount + "；其内容未计入。" : "")
                + (LastSnapshot.ProjectNotices.Count == 0 ? "" : "\n" + String.Join("\n", LastSnapshot.ProjectNotices.ToArray()))
                + "\n范围排除项始终保留；仅有提示或无可解析来源身份的行不能定位。";
            ((TabItem)tabs.Items[1]).Header = "模型检查（" + problemRows.Count + "）";
            UpdateActions();
        }

        internal bool CheckFreshness()
        {
            if (LastSnapshot == null || stale) { UpdateActions(); return false; }
            try
            {
                if (successfulRequest != RequestKey(CurrentRequest()) || getRevision() != LastSnapshot.Revision)
                    MarkStale("范围或模型已变化，当前数据已过期，请刷新后再导出或定位。");
            }
            catch (Exception error)
            {
                if (error is OutOfMemoryException) throw;
                MarkStale("无法确认当前工程状态，请刷新后重试：" + error.Message);
            }
            UpdateActions(); return !stale;
        }

        internal string BuildCurrentCsv()
        {
            if (!CheckFreshness()) throw new InvalidOperationException("数据已过期或尚未读取，请先刷新。");
            return CivilSchedule.ToCsv(LastSnapshot, filteredElements, GroupMode);
        }

        internal void ExportCsv(string path)
        {
            string csv = BuildCurrentCsv();
            string destination = Path.GetFullPath(path);
            string temporary = Path.Combine(Path.GetDirectoryName(destination), "." + Path.GetFileName(destination) + ".bright-civil-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(true))) writer.Write(csv);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                // Only this invocation's unpredictable temporary file may be
                // removed. The destination is never truncated or deleted.
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal bool PrepareFocus()
        {
            if (!CheckFreshness()) return false;
            List<CivilElementRecord> selected = FocusCandidates();
            if (selected.Count == 0) return false;
            SelectedFocusElements = selected;
            return true;
        }

        private List<CivilElementRecord> FocusCandidates()
        {
            List<CivilElementRecord> selected = new List<CivilElementRecord>();
            if (tabs.SelectedIndex == 1)
            {
                foreach (ProblemRow row in problems.SelectedItems) if (row.Element != null) selected.Add(row.Element);
            }
            else foreach (QuantityRow row in quantities.SelectedItems) selected.AddRange(row.Source.Elements);
            return selected.Where(e => !String.IsNullOrEmpty(e.UniqueId)).GroupBy(e => e.UniqueId + "|" + e.ElementId, StringComparer.Ordinal).Select(g => g.First()).ToList();
        }

        internal void SetProject(CivilProjectSettings profile)
        {
            if (!CheckFreshness()) throw new InvalidOperationException("请先刷新原始量，再设置计量工程。");
            CivilProjectSettings candidate = CivilProject.Clone(profile);
            CivilSnapshot applied = CivilProject.Apply(nativeSnapshot, candidate);
            if (currentProject == null || currentProject.ProjectId != candidate.ProjectId) projectPath = null;
            currentProject = candidate; LastSnapshot = applied; projectDirty = true;
            ConfigureProjectFilters(); ApplyFilters(); UpdateActions();
        }

        internal void SaveProject(string path)
        {
            if (!CheckFreshness() || currentProject == null) throw new InvalidOperationException("请先读取并设置计量工程。");
            CivilProjectSettings candidate = CivilProject.Clone(currentProject);
            CivilReadRequest request = CurrentRequest();
            if (candidate.PreferredScope != request.Scope || candidate.PhaseId != request.PhaseId || candidate.DesignOptionId != request.DesignOptionId)
            {
                candidate.PreferredScope = request.Scope; candidate.PhaseId = request.PhaseId; candidate.DesignOptionId = request.DesignOptionId;
                CivilProject.Touch(candidate);
            }
            string destination = Path.GetFullPath(path);
            if (!destination.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("工程设置请保存为 JSON 文件。");
            CivilProjectStore.Save(destination, candidate);
            currentProject = candidate; LastSnapshot = CivilProject.Apply(nativeSnapshot, candidate);
            projectPath = destination; projectDirty = false; ApplyFilters(); UpdateActions();
        }

        internal void LoadProject(string path)
        {
            if (!CheckFreshness()) throw new InvalidOperationException("请先刷新原始量，再打开工程设置。");
            CivilProjectSettings candidate = CivilProjectStore.Load(path);
            CivilSnapshot applied = CivilProject.Apply(nativeSnapshot, candidate);
            currentProject = candidate; LastSnapshot = applied; projectPath = Path.GetFullPath(path); projectDirty = false;
            ConfigureProjectFilters(); ApplyFilters(); UpdateActions();
            status.Text = "已打开工程设置；来源模型身份已核对。读取范围、阶段和设计选项仍使用工作台当前选择。";
        }

        private void ConfigureProjectFilters()
        {
            bool previous = initializing; initializing = true;
            try
            {
                string oldLevel = ChoiceId(quantityLevelPicker), oldSegment = ChoiceId(segmentPicker);
                List<CivilChoice> levels = new List<CivilChoice> { new CivilChoice { Id = "", Label = "全部计量层" }, new CivilChoice { Id = "__unassigned__", Label = "未映射计量层" } };
                List<CivilChoice> segments = new List<CivilChoice> { new CivilChoice { Id = "", Label = "全部施工段" }, new CivilChoice { Id = "__unassigned__", Label = "未分配施工段" } };
                if (currentProject != null)
                {
                    levels.AddRange(currentProject.QuantityLevels.Select(l => new CivilChoice { Id = l.Id, Label = l.Name }));
                    segments.AddRange(currentProject.Segments.Select(s => new CivilChoice { Id = s.Id, Label = s.Name }));
                }
                ConfigureChoice(quantityLevelPicker, levels, levels.Any(c => c.Id == oldLevel) ? oldLevel : "");
                ConfigureChoice(segmentPicker, segments, segments.Any(c => c.Id == oldSegment) ? oldSegment : "");
            }
            finally { initializing = previous; }
        }

        private static string ProjectFilterId(ComboBox picker) { string id = ChoiceId(picker); return id == "__unassigned__" ? "" : id; }

        private void EditProject()
        {
            if (!CheckFreshness()) return;
            try
            {
                CivilProjectWindow dialog = new CivilProjectWindow(nativeSnapshot, currentProject, FocusCandidates()) { Owner = this };
                if (dialog.ShowDialog() == true) SetProject(dialog.Result);
            }
            catch (Exception error) { ShowProjectError(error); }
        }

        private void OpenProjectDialog()
        {
            if (!CheckFreshness()) return;
            if (projectDirty && MessageBox.Show(this, "当前工程设置尚未保存。是否放弃并打开其他工程设置？", "BV · 打开工程", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            OpenFileDialog dialog = new OpenFileDialog { Title = "打开计量工程设置", Filter = "BV 计量工程 (*.bvproject.json)|*.bvproject.json|JSON 文件 (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            try { LoadProject(dialog.FileName); } catch (Exception error) { ShowProjectError(error); }
        }

        private void SaveProjectDialog()
        {
            if (!CheckFreshness() || currentProject == null) return;
            SaveFileDialog dialog = new SaveFileDialog { Title = "保存计量工程设置", Filter = "BV 计量工程 (*.bvproject.json)|*.bvproject.json", DefaultExt = ".bvproject.json", AddExtension = true, OverwritePrompt = true,
                FileName = String.IsNullOrEmpty(projectPath) ? "BV-计量工程.bvproject.json" : Path.GetFileName(projectPath) };
            if (!String.IsNullOrEmpty(projectPath)) dialog.InitialDirectory = Path.GetDirectoryName(projectPath);
            if (dialog.ShowDialog(this) != true) return;
            try { SaveProject(dialog.FileName); status.Text = "计量工程已保存：" + dialog.FileName + "；未写入 RVT。"; } catch (Exception error) { ShowProjectError(error); }
        }

        private void ShowProjectError(Exception error)
        {
            if (error is OutOfMemoryException) throw error;
            status.Text = "工程设置操作未完成：" + error.Message; status.Foreground = Brush(164, 58, 48);
        }

        private void SaveCsv(object sender, RoutedEventArgs args)
        {
            if (!CheckFreshness()) return;
            SaveFileDialog dialog = new SaveFileDialog { Title = "导出当前筛选的原始量表", Filter = "CSV 表格 (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true,
                OverwritePrompt = true, FileName = "BV-土建原始量.csv" };
            if (dialog.ShowDialog(this) != true) return;
            try { ExportCsv(dialog.FileName); status.Text = "CSV 已保存：" + dialog.FileName + "  ·  内容与当前筛选及分组一致。"; status.Foreground = Brush(39, 111, 89); }
            catch (Exception error)
            {
                if (error is OutOfMemoryException) throw;
                status.Text = "CSV 未保存：" + error.Message; status.Foreground = Brush(164, 58, 48);
            }
        }

        private void RequestChanged(object sender, SelectionChangedEventArgs args)
        { if (!initializing) MarkStale("读取条件已变化，请点击“刷新原始量”。"); }
        private void MarkStale(string message)
        { stale = true; status.Text = message; status.Foreground = Brush(163, 96, 25); UpdateActions(); }
        private void UpdateActions()
        {
            bool valid = !refreshing && !stale && LastSnapshot != null;
            csvButton.IsEnabled = valid;
            focusButton.IsEnabled = valid && FocusCandidates().Count > 0;
            projectButton.IsEnabled = valid; loadProjectButton.IsEnabled = valid;
            saveProjectButton.IsEnabled = valid && currentProject != null;
            projectStatus.Text = currentProject == null ? "尚未创建计量工程" : currentProject.Name + " · 修订 " + currentProject.Revision + (projectDirty ? " · 未保存" : "");
        }
        private CivilReadRequest CurrentRequest()
        {
            if (scopePicker.SelectedItem == null || phasePicker.SelectedItem == null || optionPicker.SelectedItem == null)
                throw new InvalidOperationException("请明确选择读取范围、阶段与设计选项。");
            return new CivilReadRequest { Scope = ChoiceId(scopePicker), PhaseId = ChoiceId(phasePicker), DesignOptionId = ChoiceId(optionPicker) };
        }
        private string GroupMode { get { return ChoiceId(groupPicker); } }
        private static string RequestKey(CivilReadRequest request)
        { return request.Scope + "\n" + request.PhaseId + "\n" + request.DesignOptionId; }
        private static string ChoiceId(ComboBox picker)
        { CivilChoice choice = picker.SelectedItem as CivilChoice; return choice == null ? "" : choice.Id; }
        private static void ConfigureChoice(ComboBox picker, IList<CivilChoice> choices, string selected)
        {
            picker.DisplayMemberPath = "Label"; picker.SelectedValuePath = "Id"; picker.ItemsSource = choices;
            picker.SelectedItem = choices.FirstOrDefault(c => c.Id == selected);
            picker.MinHeight = 31; picker.Padding = new Thickness(5, 3, 5, 3);
        }
        private static FrameworkElement Labeled(string text, FrameworkElement control)
        {
            StackPanel panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            panel.Children.Add(new TextBlock { Text = text, Foreground = Brush(85, 103, 123), Margin = new Thickness(0, 0, 0, 5) });
            panel.Children.Add(control); return panel;
        }
        private static void ConfigureGrid(DataGrid grid)
        {
            grid.AutoGenerateColumns = false; grid.IsReadOnly = true; grid.CanUserAddRows = false; grid.CanUserDeleteRows = false;
            grid.SelectionMode = DataGridSelectionMode.Extended; grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            grid.HeadersVisibility = DataGridHeadersVisibility.Column; grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            grid.BorderBrush = Brush(220, 228, 237); grid.HorizontalGridLinesBrush = Brush(234, 239, 244);
            grid.RowHeaderWidth = 0; grid.MinRowHeight = 30; grid.AlternatingRowBackground = Brush(248, 250, 252);
        }
        private static void AddColumn(DataGrid grid, string header, string path, double width)
        { grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = new DataGridLength(width) }); }
        private static Brush Brush(byte r, byte g, byte b)
        { return new SolidColorBrush(Color.FromRgb(r, g, b)); }
        private static string MetricText(CivilMetricSummary value)
        {
            if (value.Overflow) return "溢出";
            int failures = value.Missing + value.Invalid + value.Unavailable;
            if (value.Value.HasValue) return value.Value.Value.ToString("0.###", CultureInfo.CurrentCulture) + (failures > 0 ? " *" : "");
            if (value.NotApplicable > 0 && failures == 0) return "NA";
            if (value.Missing > 0 && value.Invalid == 0 && value.Unavailable == 0) return "缺失";
            if (value.Invalid > 0 && value.Missing == 0 && value.Unavailable == 0) return "无效";
            if (value.Unavailable > 0 && value.Missing == 0 && value.Invalid == 0) return "不可读";
            return failures > 0 ? "无有效量" : "—";
        }
        private static string Coverage(string name, CivilMetricSummary value)
        {
            return name + "：有效 " + value.Available + " / 缺失 " + value.Missing + " / 无效 " + value.Invalid
                + " / 不可读 " + value.Unavailable + " / 不适用 " + value.NotApplicable + (value.Overflow ? " / 汇总溢出" : "");
        }

        private sealed class QuantityRow
        {
            internal readonly CivilScheduleRow Source;
            internal QuantityRow(CivilScheduleRow row) { Source = row; }
            public string Label { get { return Source.Label; } }
            public string ElementId { get { return Source.Elements.Count == 1 ? Source.Elements[0].ElementId : "分组"; } }
            public string KindName { get { return Source.KindName; } }
            public string LevelName { get { return Source.LevelName; } }
            public string QuantityLevelName { get { return Source.QuantityLevelName; } }
            public string SegmentName { get { return Source.SegmentName; } }
            public string CountText { get { return Source.Count + " / " + Source.SupportedCount; } }
            public string LengthText { get { return Source.SupportedCount == 0 ? "NA" : MetricText(Source.Length); } }
            public string AreaText { get { return Source.SupportedCount == 0 ? "NA" : MetricText(Source.Area); } }
            public string VolumeText { get { return Source.SupportedCount == 0 ? "NA" : MetricText(Source.Volume); } }
            public string IssueText { get { return String.IsNullOrEmpty(Source.Issues) ? "已读取原始参数" : Source.Issues; } }
            public string Detail
            {
                get
                {
                    string sources = String.Join("；", Source.Elements.SelectMany(e => new[] { e.Length.Source, e.Area.Source, e.Volume.Source }).Where(s => !String.IsNullOrEmpty(s)).Distinct().ToArray());
                    return Coverage("长度 m", Source.Length) + "\n" + Coverage("面积 m²", Source.Area) + "\n" + Coverage("体积 m³", Source.Volume)
                        + "\n参数来源：" + sources + "\n原生楼层来源：" + String.Join("；", Source.Elements.Select(e => e.LevelSource).Distinct().ToArray())
                        + "\nElement.LevelId 原值：" + String.Join("；", Source.Elements.Select(e => e.RawLevelId + " " + e.RawLevelName).Distinct().ToArray()) + "\n" + Source.Issues;
                }
            }
        }
        private sealed class ProblemRow
        {
            internal readonly CivilElementRecord Element;
            internal ProblemRow(string origin, string id, string name, string message, CivilElementRecord element)
            { Origin = origin; ElementId = id; Name = name; Message = message; Element = element; }
            public string Origin { get; private set; }
            public string ElementId { get; private set; }
            public string Name { get; private set; }
            public string Message { get; private set; }
        }
    }
}
