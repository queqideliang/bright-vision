// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
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
    internal sealed class CivilWallWindow : Window
    {
        private readonly ComboBox scope = new ComboBox(), phase = new ComboBox(), option = new ComboBox(), geometryView = new ComboBox(), statusFilter = new ComboBox();
        private readonly ComboBox quantityLevelPicker = new ComboBox(), segmentPicker = new ComboBox(), groupPicker = new ComboBox();
        private readonly ComboBox rulePicker = new ComboBox();
        private readonly TextBox search = new TextBox();
        private readonly TextBlock status = new TextBlock(), summary = new TextBlock(), details = new TextBlock(), projectStatus = new TextBlock();
        private readonly TextBlock ruleStatus = new TextBlock();
        private readonly DataGrid walls = new DataGrid(), ledger = new DataGrid(), groups = new DataGrid();
        private readonly TabControl tabs = new TabControl();
        private readonly Button csvButton = new Button(), focusButton = new Button(), refreshButton = new Button();
        private readonly Button projectButton = new Button(), loadProjectButton = new Button(), saveProjectButton = new Button();
        private readonly Button cancelReadButton = new Button(), editRuleButton = new Button(), loadRuleButton = new Button(), saveRuleButton = new Button();
        private readonly Func<CivilReadRequest, string, CivilWallSnapshot> read;
        private readonly CivilWallRuleReader readWithRules;
        private readonly Func<long> revision;
        private readonly Func<string, bool> confirmDiscard;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly System.Diagnostics.Stopwatch progressClock = new System.Diagnostics.Stopwatch();
        private long lastInputPumpMilliseconds;
        private bool initializing = true, stale = true, reading, projectDirty, reportError;
        private bool cancellationRequested, pumpingInput, closeAfterRead;
        private string successfulKey, projectPath;
        private CivilProjectSettings currentProject;
        private CivilWallRuleTemplate selectedRule = CivilWallRuleTemplates.Default();
        private List<CivilWallReportRow> filtered = new List<CivilWallReportRow>();
        internal CivilWallCalculationBatch LastBatch { get; private set; }
        internal CivilWallReportData LastReport { get; private set; }
        internal IList<CivilElementRecord> SelectedFocusElements { get; private set; }
        internal CivilWallRuleTemplate SelectedRuleTemplate { get { return CivilWallRuleTemplates.Clone(selectedRule); } }

        internal CivilWallWindow(CivilReadRequest initial, IList<CivilChoice> phases, IList<CivilChoice> options, IList<CivilChoice> views,
            string selectedView, Func<CivilReadRequest, string, CivilWallSnapshot> read, Func<long> revision, Func<string, bool> confirmDiscard = null, CivilWallRuleReader readWithRules = null)
        {
            if (initial == null || (read == null && readWithRules == null) || revision == null) throw new ArgumentNullException("initial/read/revision");
            this.read = read; this.readWithRules = readWithRules; this.revision = revision;
            this.confirmDiscard = confirmDiscard ?? (message => MessageBox.Show(this, message, "BV · 未保存的工程设置", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            SelectedFocusElements = new List<CivilElementRecord>();
            Title = "BV · 墙体试算与台账 · 崛亮视觉"; Icon = Branding.Icon(32);
            Width = 1320; Height = 900; MinWidth = 1080; MinHeight = 720; MaxHeight = Math.Max(720, SystemParameters.WorkArea.Height - 24);
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Background = Brushes.White; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12;
            Grid root = new Grid();
            for (int row = 0; row < 3; row++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = root;
            Border header = new Border { Background = ColorBrush(17,35,55), Padding = new Thickness(20,12,20,12) };
            StackPanel title = new StackPanel();
            title.Children.Add(new TextBlock { Text = "墙体面积、体积与逐条台账", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            title.Children.Add(new TextBlock { Text = "崛亮视觉 · Bright Vision  |  " + CivilWallCalculator.RuleId + " · " + CivilWallCalculator.AreaRuleId + " · 项目约定", Foreground = ColorBrush(99,225,198), Margin = new Thickness(0,4,0,0) });
            header.Child = title; root.Children.Add(header);
            StackPanel controls = new StackPanel { Margin = new Thickness(20,10,20,6) };
            WrapPanel request = new WrapPanel();
            Choices(scope, new[] { Choice("view","当前活动视图"),Choice("selection","当前选择"),Choice("project","当前工程（根文档）") }, initial.Scope);
            Choices(phase, phases, initial.PhaseId); Choices(option, options, initial.DesignOptionId); Choices(geometryView, views, selectedView);
            request.Children.Add(Field("计量范围",scope,135)); request.Children.Add(Field("阶段",phase,150)); request.Children.Add(Field("设计选项",option,135)); request.Children.Add(Field("几何校验视图（不切换活动视图）",geometryView,245));
            refreshButton.Content = "读取并试算"; refreshButton.Padding = new Thickness(16,7,16,7); refreshButton.Margin = new Thickness(5,20,0,0);
            refreshButton.Click += delegate { RefreshCalculation(); }; request.Children.Add(refreshButton);
            cancelReadButton.Content = "取消本轮"; cancelReadButton.Padding = new Thickness(12,7,12,7); cancelReadButton.Margin = new Thickness(7,20,0,0);
            cancelReadButton.Click += delegate { RequestCancel(); }; request.Children.Add(cancelReadButton); controls.Children.Add(request);
            WrapPanel rules = new WrapPanel { Margin = new Thickness(0,7,0,0) };
            rulePicker.DisplayMemberPath = "Name"; rulePicker.SelectedValuePath = "TemplateId"; rulePicker.MinHeight = 29; rulePicker.Width = 252; rulePicker.Margin = new Thickness(0,0,8,0);
            ConfigureRulePicker(); rules.Children.Add(rulePicker);
            ProjectButton(rules, editRuleButton, "编辑自有模板…", EditRule);
            ProjectButton(rules, loadRuleButton, "导入 BV 规则 JSON…", OpenRuleDialog);
            ProjectButton(rules, saveRuleButton, "导出 BV 规则 JSON…", SaveRuleDialog); controls.Children.Add(rules);
            ruleStatus.TextWrapping = TextWrapping.Wrap; ruleStatus.Foreground = Brushes.DimGray; ruleStatus.Margin = new Thickness(0,4,0,0); controls.Children.Add(ruleStatus);
            WrapPanel projectBar = new WrapPanel { Margin = new Thickness(0,7,0,0) };
            ProjectButton(projectBar, projectButton, "计量工程设置…", EditProject);
            ProjectButton(projectBar, loadProjectButton, "打开工程 JSON…", OpenProjectDialog);
            ProjectButton(projectBar, saveProjectButton, "保存工程 JSON…", SaveProjectDialog);
            projectStatus.VerticalAlignment = VerticalAlignment.Center; projectStatus.TextWrapping = TextWrapping.Wrap; projectStatus.MaxWidth = 430; projectStatus.Margin = new Thickness(8,3,0,3); projectBar.Children.Add(projectStatus); controls.Children.Add(projectBar);
            controls.Children.Add(new TextBlock { Text = "面积为墙轴向单侧投影基准扣洞口并集，不是抹灰／模板／地区清单量。归属设置不切分几何、不改原生量；门窗名义尺寸不直接扣减。", TextWrapping = TextWrapping.Wrap, Foreground = ColorBrush(130,88,25), Margin = new Thickness(0,7,0,0) });
            status.Text = "请选择范围、阶段和同阶段三维校验视图。"; status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0,5,0,0); controls.Children.Add(status);
            Grid.SetRow(controls,1); root.Children.Add(controls);
            StackPanel filter = new StackPanel { Margin = new Thickness(20,0,20,7) };
            WrapPanel filters = new WrapPanel(); search.Padding = new Thickness(6); search.MinHeight = 30;
            filters.Children.Add(Field("搜索墙名 / 类型 / 来源 ID",search,210));
            Choices(statusFilter,new[] { Choice("","全部状态"),Choice("calculated","已通过试算"),Choice("unsupported","未支持"),Choice("failed","读取或几何失败") }, "");
            filters.Children.Add(Field("状态",statusFilter,135)); ConfigureProjectFilters();
            filters.Children.Add(Field("计量层",quantityLevelPicker,190)); filters.Children.Add(Field("施工段",segmentPicker,175));
            Choices(groupPicker,new[] { Choice("quantity-level","计量层"),Choice("segment","施工段"),Choice("level","原生楼层"),Choice("type","墙类型") },"quantity-level");
            filters.Children.Add(Field("汇总分组 / CSV",groupPicker,145)); filter.Children.Add(filters);
            summary.Margin = new Thickness(0,6,0,0); summary.FontWeight = FontWeights.SemiBold; summary.TextWrapping = TextWrapping.Wrap; filter.Children.Add(summary);
            Grid.SetRow(filter,2); root.Children.Add(filter);
            ConfigureGrid(walls); ConfigureGrid(ledger); ConfigureGrid(groups);
            Column(walls,"墙 / 类型","Name",180); Column(walls,"ID","ElementId",75); Column(walls,"状态","State",95);
            Column(walls,"基准投影 m²","BasisArea",110); Column(walls,"扣洞并集 m²","OpeningArea",110); Column(walls,"净投影 m²","ResultArea",110);
            Column(walls,"基准 m³","Basis",100); Column(walls,"扣减 m³","Deduction",100); Column(walls,"试算 m³","Result",100);
            Column(walls,"计量层","QuantityLevelName",135); Column(walls,"施工段","SegmentName",125); Column(walls,"原生楼层","LevelName",125);
            Column(walls,"Revit原生 m³","Native",110); Column(walls,"实际实体 m³","Geometry",110); Column(walls,"说明","Message",360);
            Grid wallTab = new Grid { Margin = new Thickness(8) }; wallTab.RowDefinitions.Add(new RowDefinition()); wallTab.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.65,GridUnitType.Star) });
            walls.Margin = new Thickness(0,0,0,6); walls.SelectionChanged += delegate { ShowLedger(); }; wallTab.Children.Add(walls);
            Grid ledgerPanel = new Grid(); ledgerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); ledgerPanel.RowDefinitions.Add(new RowDefinition());
            details.Text = "选择一面墙查看台账，或切换到分组汇总核查整组。未支持项不产生试算量。"; details.TextWrapping = TextWrapping.Wrap; details.Margin = new Thickness(0,1,0,5); ledgerPanel.Children.Add(details);
            Column(ledger,"步骤","Description",300); Column(ledger,"关联ID","ElementId",75); Column(ledger,"面积变化 m²","DeltaAreaMetres2",110);
            Column(ledger,"体积变化 m³","DeltaVolumeMetres3",110); Column(ledger,"面积公式","AreaFormula",225); Column(ledger,"体积公式","Formula",225); Column(ledger,"来源","Source",320);
            ledger.SelectionChanged += delegate { UpdateButtons(); }; Grid.SetRow(ledger,1); ledgerPanel.Children.Add(ledger); Grid.SetRow(ledgerPanel,1); wallTab.Children.Add(ledgerPanel);
            tabs.Items.Add(new TabItem { Header = "墙明细与台账", Content = wallTab });
            Column(groups,"分组","Name",210); Column(groups,"墙数","WallCount",70); Column(groups,"通过","CalculatedCount",70); Column(groups,"未支持","UnsupportedCount",75); Column(groups,"失败","FailedCount",70);
            Column(groups,"基准投影 m²","BasisAreaMetres2",125); Column(groups,"扣洞并集 m²","OpeningAreaMetres2",125); Column(groups,"净投影 m²","ResultAreaMetres2",125); Column(groups,"试算 m³","ResultVolumeMetres3",125);
            groups.Margin = new Thickness(8); groups.SelectionChanged += delegate { UpdateButtons(); }; tabs.Items.Add(new TabItem { Header = "分组汇总（选择一组可定位全部源墙）", Content = groups });
            tabs.Margin = new Thickness(20,0,20,0); tabs.SelectionChanged += delegate { UpdateButtons(); }; Grid.SetRow(tabs,3); root.Children.Add(tabs);
            DockPanel footer = new DockPanel { Margin = new Thickness(20,10,20,12) }; StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            csvButton.Content = "导出当前筛选与汇总 CSV…"; csvButton.Padding = new Thickness(12,6,12,6); csvButton.Click += SaveCsv;
            focusButton.Content = "定位选中墙／洞口／分组并关闭"; focusButton.Padding = new Thickness(12,6,12,6); focusButton.Margin = new Thickness(8,0,8,0); focusButton.Click += delegate { if (PrepareFocus()) Close(); };
            Button close = new Button { Content = "关闭", IsCancel = true, Padding = new Thickness(16,6,16,6) }; close.Click += delegate { SelectedFocusElements = new List<CivilElementRecord>(); Close(); };
            buttons.Children.Add(csvButton); buttons.Children.Add(focusButton); buttons.Children.Add(close); DockPanel.SetDock(buttons,Dock.Right); footer.Children.Add(buttons);
            footer.Children.Add(new TextBlock { Text = "只读 RVT · 归属不是几何切量", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray }); Grid.SetRow(footer,4); root.Children.Add(footer);
            scope.SelectionChanged += RequestChanged; phase.SelectionChanged += RequestChanged; option.SelectionChanged += RequestChanged; geometryView.SelectionChanged += RequestChanged;
            rulePicker.SelectionChanged += delegate {
                if (initializing) return;
                CivilWallRuleTemplate value = rulePicker.SelectedItem as CivilWallRuleTemplate;
                if (value == null) return;
                try { SetRuleTemplate(value); } catch (Exception error) { ProjectError(error); ConfigureRulePicker(); }
            };
            search.TextChanged += delegate { ApplyFilters(); }; statusFilter.SelectionChanged += delegate { ApplyFilters(); };
            quantityLevelPicker.SelectionChanged += delegate { ApplyFilters(); }; segmentPicker.SelectionChanged += delegate { ApplyFilters(); }; groupPicker.SelectionChanged += delegate { ApplyFilters(); };
            timer.Interval = TimeSpan.FromMilliseconds(800); timer.Tick += delegate { PollSourceFreshness(); }; Loaded += delegate { timer.Start(); }; Closed += delegate { timer.Stop(); };
            Closing += delegate(object sender,System.ComponentModel.CancelEventArgs args) {
                if (reading) { closeAfterRead = true; RequestCancel(); args.Cancel = true; return; }
                if (!CanDiscardProjectChanges("计量工程设置尚未保存到 JSON。是否放弃未保存的设置并关闭？")) { args.Cancel = true; SelectedFocusElements = new List<CivilElementRecord>(); }
            };
            initializing = false; UpdateButtons();
        }

        internal void RefreshCalculation()
        {
            if (reading) return;
            reading = true; cancellationRequested = false; stale = true; progressClock.Restart();lastInputPumpMilliseconds=-50;SelectedFocusElements = new List<CivilElementRecord>(); UpdateButtons(); status.Text = "正在读取墙体事实和校验实体；可在几何操作边界取消…";
            try
            {
                CivilReadRequest request = CurrentRequest(); string view = Id(geometryView); string requestedKey = Key(); long sourceRevision = revision();
                CivilWallRuleTemplate rule = CivilWallRuleTemplates.Clone(selectedRule); string fingerprint = CivilWallRuleTemplates.Fingerprint(rule);
                if (String.IsNullOrEmpty(view)) throw new InvalidOperationException("请选择明确的几何校验三维视图。");
                var control = new CivilWallReadControl { IsCancellationRequested = delegate { return cancellationRequested; }, Progress = ReadProgress };
                control.Checkpoint("读取准备", 0, 0);
                CivilWallSnapshot facts = readWithRules == null ? read(request,view) : readWithRules(request,view,CivilWallRuleTemplates.Clone(rule),control);
                if (facts == null || facts.NativeSnapshot == null) throw new InvalidDataException("读取未返回完整墙体快照。");
                if (readWithRules == null) facts.RuleTemplate = CivilWallRuleTemplates.Clone(rule);
                if (facts.RuleTemplate == null || CivilWallRuleTemplates.Fingerprint(facts.RuleTemplate) != fingerprint)
                    throw new InvalidDataException("读取事实没有绑定本轮选定规则；未提交结果。");
                control.Checkpoint("确定性试算", 0, facts.Walls == null ? 0 : facts.Walls.Count);
                CivilWallCalculationBatch batch = CivilWallCalculator.Calculate(facts);
                CivilWallReportData report = CivilWallReport.Create(batch,currentProject);
                FilteredView next = BuildView(report,currentProject);
                control.Checkpoint("提交前校验", facts.Walls.Count, facts.Walls.Count);
                CivilWallBatchIntegrity.Validate(batch);
                if (requestedKey != Key() || sourceRevision != revision() || facts.NativeSnapshot.Revision != sourceRevision || batch.RuleFingerprint != fingerprint)
                    throw new InvalidOperationException("读取期间模型、条件或规则发生变化；本轮结果未提交，请重新读取。");
                // All source, rule, report, filter, grouping and finite-total
                // checks succeeded before replacing the completed pair.
                LastBatch = batch; LastReport = report; successfulKey = requestedKey; stale = false; reportError = false;
                ConfigureProjectFilters(); PublishView(next);
                status.Text = facts.NativeSnapshot.ModelTitle + " · " + facts.NativeSnapshot.ScopeLabel + " · " + facts.NativeSnapshot.PhaseLabel + " · 几何视图：" + ((CivilChoice)geometryView.SelectedItem).Label;
            }
            catch (OperationCanceledException) { status.Text = "本轮已取消，没有提交半批结果。" + (LastBatch == null ? "" : "旧完成结果已过期，请重新读取。"); }
            catch (Exception error) { if (error is OutOfMemoryException) throw; status.Text = "本轮未完成：" + error.Message + (LastBatch == null ? "" : "；旧结果已过期。"); }
            finally
            {
                reading = false; UpdateButtons();
                if (closeAfterRead) { closeAfterRead = false; if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(Close)); }
            }
        }
        internal void RequestCancel()
        {
            if (!reading) return;
            cancellationRequested = true; SelectedFocusElements = new List<CivilElementRecord>();
            status.Text = "已请求取消；正在进行的单次几何操作结束后停止，不提交半批结果。";
        }
        private void ReadProgress(string stage, int completed, int total)
        {
            if (!reading) return;
            status.Text = cancellationRequested ? "已请求取消，等待当前几何操作结束…" : stage + (total > 0 ? " · " + completed + " / " + total : "") + "；可在操作边界取消。";
            if (!IsVisible || pumpingInput || progressClock.ElapsedMilliseconds-lastInputPumpMilliseconds<50) return;
            lastInputPumpMilliseconds=progressClock.ElapsedMilliseconds;
            // The modal ExternalCommand stays on its original Revit API
            // thread. The bounded frame drains queued input/render work
            // at an explicit adapter checkpoint. Every other action is locked
            // and its helper refuses reentry; no API work runs on Task.Run.
            pumpingInput = true;
            try
            {
                var frame = new DispatcherFrame();
                Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate { frame.Continue = false; }));
                Dispatcher.PushFrame(frame);
            }
            finally { pumpingInput = false; }
        }
        internal void ApplyFilters()
        {
            if (initializing || reading || LastReport == null) return;
            SelectedFocusElements = new List<CivilElementRecord>();
            try
            {
                FilteredView next = BuildView(LastReport,currentProject);
                bool recovered = reportError; PublishView(next); reportError = false;
                if(recovered)status.Text="当前筛选已恢复，可导出和定位；几何读取条件未改变。";
            }
            catch(Exception error)
            {
                if(error is OutOfMemoryException)throw;
                reportError=true;status.Text="当前筛选汇总未完成："+error.Message+"；旧表仅供查看，当前范围不可导出或定位。请缩小筛选范围或重新读取。";
            }
            UpdateButtons();
        }
        private FilteredView BuildView(CivilWallReportData report,CivilProjectSettings profile)
        {
            string quantity = Id(quantityLevelPicker), segment = Id(segmentPicker);
            if (quantity != "" && quantity != CivilWallReport.UnassignedId && (profile == null || !profile.QuantityLevels.Any(level => level.Id == quantity))) quantity = "";
            if (segment != "" && segment != CivilWallReport.UnassignedId && (profile == null || !profile.Segments.Any(item => item.Id == segment))) segment = "";
            List<CivilWallReportRow> next = CivilWallReport.Filter(report,search.Text.Trim(),Id(statusFilter),quantity,segment);
            List<CivilWallReportGroup> nextGroups = CivilWallReport.Group(next,Id(groupPicker));
            List<CivilWallReportRow> calculated = next.Where(row => row.Result.Status == "calculated").ToList();
            double area,volume; Totals(calculated,out area,out volume);
            return new FilteredView { Rows = next, Groups = nextGroups, Walls = next.Select(row => new WallRow(row)).ToList(),
                Summary = "当前 " + next.Count + " 面墙 · 通过 " + calculated.Count + " · 未纳入 " + (next.Count-calculated.Count) + " · 净投影面积 " + (calculated.Count == 0 ? "—" : Total(area,"m²")) + " · 试算体积 " + (calculated.Count == 0 ? "—" : Total(volume,"m³")) + "（仅通过项；原生量未替代）" };
        }
        private void PublishView(FilteredView next)
        {
            filtered = next.Rows; walls.ItemsSource = next.Walls; groups.ItemsSource = next.Groups; summary.Text = next.Summary; ShowLedger();
        }
        private static void Totals(List<CivilWallReportRow> rows,out double area,out double volume)
        {
            area=rows.Sum(row=>row.Result.ResultAreaMetres2.Value);volume=rows.Sum(row=>row.Result.ResultVolumeMetres3.Value);
            if(Double.IsNaN(area)||Double.IsInfinity(area)||Double.IsNaN(volume)||Double.IsInfinity(volume))throw new InvalidDataException("通过项的筛选合计溢出，未输出非有限数量。");
        }
        private void ValidateProjectView(CivilWallReportData report,CivilProjectSettings profile)
        {
            BuildView(report,profile);
        }
        internal bool CheckFreshness()
        { return Freshness(true); }
        internal bool PollSourceFreshness()
        { return Freshness(false); }
        private bool Freshness(bool verifyReport)
        {
            if (reading) { UpdateButtons(); return false; }
            if (!stale && LastBatch != null)
            {
                try {
                    if (Key()!=successfulKey || revision()!=LastBatch.Input.NativeSnapshot.Revision || LastBatch.RuleFingerprint!=CivilWallRuleTemplates.Fingerprint(selectedRule)) { stale=true; status.Text="模型、规则或读取条件已变化，旧结果过期，请重新读取。"; }
                    else if (verifyReport) CivilWallReport.Validate(LastReport);
                }
                catch (Exception error) { if (error is OutOfMemoryException) throw; stale=true; status.Text="无法确认来源状态："+error.Message; }
            }
            if (stale) SelectedFocusElements = new List<CivilElementRecord>();
            UpdateButtons(); return !stale && !reportError && LastBatch != null && LastReport != null;
        }
        internal string BuildCurrentCsv() { if (!CheckFreshness()) throw new InvalidOperationException("旧结果不可导出，请重新读取。"); return CivilWallReport.ToCsv(LastReport,filtered,Id(groupPicker)); }
        internal void ExportCsv(string destination)
        {
            string content=BuildCurrentCsv(); destination=Path.GetFullPath(destination);
            if (!destination.EndsWith(".csv",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 CSV 文件。");
            string temp=Path.Combine(Path.GetDirectoryName(destination),".bv-wall-"+Guid.NewGuid().ToString("N")+".tmp");
            try { using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) using(var writer=new StreamWriter(file,new UTF8Encoding(true))) writer.Write(content);
                if(File.Exists(destination)) File.Replace(temp,destination,null); else File.Move(temp,destination); }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        internal bool PrepareFocus()
        {
            SelectedFocusElements = new List<CivilElementRecord>(); if(!CheckFreshness()) return false;
            List<CivilElementRecord> selected = FocusCandidates(); if(selected.Count==0) return false; SelectedFocusElements=selected; return true;
        }
        private List<CivilElementRecord> FocusCandidates()
        {
            if(tabs.SelectedIndex==1) { CivilWallReportGroup group=groups.SelectedItem as CivilWallReportGroup; return group==null?new List<CivilElementRecord>():UniqueSources(group.Rows.Select(row=>row.Source)); }
            CivilWallLedgerEntry entry=ledger.SelectedItem as CivilWallLedgerEntry; WallRow wall=walls.SelectedItem as WallRow;
            if(entry!=null && !String.IsNullOrEmpty(entry.ElementId) && !String.IsNullOrEmpty(entry.UniqueId)) return new List<CivilElementRecord>{new CivilElementRecord{ElementId=entry.ElementId,UniqueId=entry.UniqueId}};
            return wall==null?new List<CivilElementRecord>():new List<CivilElementRecord>{wall.Record.Source};
        }
        private List<CivilElementRecord> ProjectCandidates()
        {
            if(tabs.SelectedIndex==1) { CivilWallReportGroup group=groups.SelectedItem as CivilWallReportGroup; return group==null?new List<CivilElementRecord>():UniqueSources(group.Rows.Select(row=>row.Source)); }
            WallRow wall=walls.SelectedItem as WallRow; return wall==null?new List<CivilElementRecord>():new List<CivilElementRecord>{wall.Record.Source};
        }
        private static List<CivilElementRecord> UniqueSources(IEnumerable<CivilElementRecord> source) { return source.Where(item=>item!=null&&!String.IsNullOrEmpty(item.UniqueId)&&!String.IsNullOrEmpty(item.ElementId)).GroupBy(item=>item.UniqueId+"|"+item.ElementId,StringComparer.Ordinal).Select(group=>group.First()).ToList(); }
        internal void SetProject(CivilProjectSettings profile)
        {
            EnsureIdle();
            if(!CheckFreshness())throw new InvalidOperationException("请先读取并试算，再设置计量工程。");
            CivilProjectSettings candidate=CivilProject.Clone(profile); CivilWallReportData applied=CivilWallReport.Create(LastBatch,candidate);FilteredView next=BuildView(applied,candidate);
            CivilWallRuleTemplate rule=candidate.SchemaVersion==2?CivilWallRuleTemplates.Clone(candidate.WallRuleTemplate):CivilWallRuleTemplates.Clone(selectedRule);
            bool ruleChanged=CivilWallRuleTemplates.Fingerprint(rule)!=LastBatch.RuleFingerprint;
            if(currentProject==null||currentProject.ProjectId!=candidate.ProjectId)projectPath=null;
            currentProject=candidate;selectedRule=rule;projectDirty=true;ConfigureRulePicker();ConfigureProjectFilters();
            if(ruleChanged){stale=true;SelectedFocusElements=new List<CivilElementRecord>();status.Text="工程规则与旧批次不同；已载入规则与归属，旧结果过期，必须重新读取试算。";}
            else{LastReport=applied;PublishView(next);status.Text=candidate.SchemaVersion==1?"格式 1 工程未保存墙体规则；明确沿用当前已选模板，保存时将写入格式 2。":"已应用同一规则下的计量工程归属。";}
            UpdateButtons();
        }
        internal void SaveProject(string path)
        {
            EnsureIdle();
            if(!CheckFreshness()||currentProject==null)throw new InvalidOperationException("请先读取并设置计量工程。");
            CivilProjectSettings candidate=CivilProject.Clone(currentProject);CivilReadRequest request=CurrentRequest();
            bool changed=candidate.PreferredScope!=request.Scope||candidate.PhaseId!=request.PhaseId||candidate.DesignOptionId!=request.DesignOptionId||candidate.SchemaVersion!=2||candidate.WallRuleTemplate==null||CivilWallRuleTemplates.Fingerprint(candidate.WallRuleTemplate)!=CivilWallRuleTemplates.Fingerprint(selectedRule);
            candidate.PreferredScope=request.Scope;candidate.PhaseId=request.PhaseId;candidate.DesignOptionId=request.DesignOptionId;candidate.SchemaVersion=2;candidate.WallRuleTemplate=CivilWallRuleTemplates.Clone(selectedRule);if(changed)CivilProject.Touch(candidate);
            CivilWallReportData applied=CivilWallReport.Create(LastBatch,candidate);FilteredView next=BuildView(applied,candidate);string destination=Path.GetFullPath(path);
            if(!destination.EndsWith(".bvproject.json",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("请选择 .bvproject.json 工程文件。");
            CivilProjectStore.Save(destination,candidate);currentProject=candidate;LastReport=applied;projectPath=destination;projectDirty=false;PublishView(next);UpdateButtons();
        }
        internal void LoadProject(string path)
        {
            EnsureIdle();
            if(!CheckFreshness())throw new InvalidOperationException("请先读取并试算，再打开计量工程。");
            CivilProjectSettings candidate=CivilProjectStore.Load(path);CivilWallReportData applied=CivilWallReport.Create(LastBatch,candidate);FilteredView next=BuildView(applied,candidate);string destination=Path.GetFullPath(path);
            CivilWallRuleTemplate rule=candidate.SchemaVersion==2?CivilWallRuleTemplates.Clone(candidate.WallRuleTemplate):CivilWallRuleTemplates.Clone(selectedRule);
            bool ruleChanged=CivilWallRuleTemplates.Fingerprint(rule)!=LastBatch.RuleFingerprint;
            currentProject=candidate;selectedRule=rule;projectPath=destination;projectDirty=false;ConfigureRulePicker();ConfigureProjectFilters();
            if(ruleChanged){stale=true;SelectedFocusElements=new List<CivilElementRecord>();status.Text="已打开工程与其规则模板；规则不同于旧批次，旧结果过期，须重新读取，未用新标签替换旧数量。";}
            else{LastReport=applied;PublishView(next);status.Text=candidate.SchemaVersion==1?"已打开格式 1 工程；文件没有墙体规则，明确沿用当前已选模板，保存时迁移为格式 2。":"已打开工程设置及相同规则；来源已核对，读取范围／阶段／设计选项／校验视图保持当前选择。";}
            UpdateButtons();
        }
        internal void SetRuleTemplate(CivilWallRuleTemplate value)
        {
            EnsureIdle();CivilWallRuleTemplate candidate=CivilWallRuleTemplates.Clone(value);
            if(CivilWallRuleTemplates.Fingerprint(candidate)==CivilWallRuleTemplates.Fingerprint(selectedRule)){ConfigureRulePicker();return;}
            CivilProjectSettings profile=currentProject==null?null:CivilProject.Clone(currentProject);
            if(profile!=null){profile.SchemaVersion=2;profile.WallRuleTemplate=CivilWallRuleTemplates.Clone(candidate);CivilProject.Touch(profile);}
            selectedRule=candidate;if(profile!=null){currentProject=profile;projectDirty=true;}
            stale=true;SelectedFocusElements=new List<CivilElementRecord>();ConfigureRulePicker();status.Text="规则模板已变化，旧结果过期；请重新读取试算。";UpdateButtons();
        }
        internal void LoadRuleTemplate(string path){EnsureIdle();CivilWallRuleTemplate candidate=CivilWallRuleTemplates.Load(path);SetRuleTemplate(candidate);}
        internal void SaveRuleTemplate(string path){EnsureIdle();CivilWallRuleTemplates.Save(path,CivilWallRuleTemplates.Clone(selectedRule));}
        private void EnsureIdle(){if(reading)throw new InvalidOperationException("正在读取试算；请先取消或等待完成，再修改规则或工程设置。");}
        private void ConfigureRulePicker()
        {
            bool previous=initializing;initializing=true;
            try{var values=CivilWallRuleTemplates.Catalog().ToList();if(!values.Any(value=>CivilWallRuleTemplates.Fingerprint(value)==CivilWallRuleTemplates.Fingerprint(selectedRule)))values.Add(CivilWallRuleTemplates.Clone(selectedRule));rulePicker.ItemsSource=values;rulePicker.SelectedItem=values.First(value=>CivilWallRuleTemplates.Fingerprint(value)==CivilWallRuleTemplates.Fingerprint(selectedRule));}
            finally{initializing=previous;}
        }
        private void EditRule()
        {
            if(reading)return;
            try{var dialog=new CivilWallRuleWindow(selectedRule){Owner=this};if(dialog.ShowDialog()==true)SetRuleTemplate(dialog.Result);}catch(Exception error){RuleError(error);}
        }
        private void OpenRuleDialog()
        {
            if(reading)return;var dialog=new OpenFileDialog{Title="导入 BV 自有墙体规则",Filter="BV 规则 JSON (*.json)|*.json",CheckFileExists=true};if(dialog.ShowDialog(this)!=true)return;
            try{LoadRuleTemplate(dialog.FileName);}catch(Exception error){RuleError(error);}
        }
        private void SaveRuleDialog()
        {
            if(reading)return;var dialog=new SaveFileDialog{Title="导出 BV 自有墙体规则",Filter="BV 规则 JSON (*.json)|*.json",DefaultExt=".json",AddExtension=true,OverwritePrompt=true,FileName="BV-墙体规则.bvwallrule.json"};if(dialog.ShowDialog(this)!=true)return;
            try{SaveRuleTemplate(dialog.FileName);status.Text="已导出当前 BV 自有规则模板："+dialog.FileName;}catch(Exception error){RuleError(error);}
        }
        private void RuleError(Exception error){if(error is OutOfMemoryException)throw error;status.Text="规则模板操作未完成："+error.Message;}
        internal bool CanDiscardProjectChanges(string message) { return !projectDirty || confirmDiscard(message); }
        private void ConfigureProjectFilters()
        {
            bool previous=initializing;initializing=true;
            try { string oldLevel=Id(quantityLevelPicker),oldSegment=Id(segmentPicker);
                var levels=new List<CivilChoice>{Choice("","全部计量层"),Choice("__unassigned__","未映射计量层")};var segments=new List<CivilChoice>{Choice("","全部施工段"),Choice("__unassigned__","未分配施工段")};
                if(currentProject!=null){levels.AddRange(currentProject.QuantityLevels.Select(level=>Choice(level.Id,level.Name)));segments.AddRange(currentProject.Segments.Select(segment=>Choice(segment.Id,segment.Name)));}
                Choices(quantityLevelPicker,levels,levels.Any(choice=>choice.Id==oldLevel)?oldLevel:"");Choices(segmentPicker,segments,segments.Any(choice=>choice.Id==oldSegment)?oldSegment:"");
            } finally {initializing=previous;}
        }
        private void EditProject()
        {
            if(!CheckFreshness())return;
            try { var dialog=new CivilProjectWindow(LastBatch.Input.NativeSnapshot,currentProject,ProjectCandidates()){Owner=this};if(dialog.ShowDialog()==true)SetProject(dialog.Result); }catch(Exception error){ProjectError(error);}
        }
        private void OpenProjectDialog()
        {
            if(!CheckFreshness()||!CanDiscardProjectChanges("当前计量工程尚未保存。是否放弃并打开其他工程设置？"))return;
            var dialog=new OpenFileDialog{Title="打开计量工程",Filter="BV 计量工程 (*.bvproject.json)|*.bvproject.json|JSON 文件 (*.json)|*.json",CheckFileExists=true};if(dialog.ShowDialog(this)!=true)return;
            try{LoadProject(dialog.FileName);}catch(Exception error){ProjectError(error);}
        }
        private void SaveProjectDialog()
        {
            if(!CheckFreshness()||currentProject==null)return;
            var dialog=new SaveFileDialog{Title="保存计量工程",Filter="BV 计量工程 (*.bvproject.json)|*.bvproject.json",DefaultExt=".bvproject.json",AddExtension=true,OverwritePrompt=true,FileName=String.IsNullOrEmpty(projectPath)?"BV-墙体计量工程.bvproject.json":Path.GetFileName(projectPath)};
            if(!String.IsNullOrEmpty(projectPath))dialog.InitialDirectory=Path.GetDirectoryName(projectPath);if(dialog.ShowDialog(this)!=true)return;
            try{SaveProject(dialog.FileName);status.Text="计量工程已保存；未写入 RVT："+dialog.FileName;}catch(Exception error){ProjectError(error);}
        }
        private void ProjectError(Exception error){if(error is OutOfMemoryException)throw error;status.Text="工程设置操作未完成："+error.Message;}
        private void ShowLedger()
        {
            WallRow row=walls.SelectedItem as WallRow;ledger.ItemsSource=row==null?new List<CivilWallLedgerEntry>():row.Record.Result.Ledger;
            details.Text=row==null?"选择一面墙查看台账；未支持项不输出试算量。":row.Record.Result.Message+"\n来源："+row.Record.Result.Fact.UniqueId+" · 双向差集 "+Number(row.Record.Result.Fact.SymmetricDifferenceMetres3)+" m³ · 容差 "+Number(row.Record.Result.Fact.GeometryToleranceMetres3)+" m³";UpdateButtons();
        }
        private void SaveCsv(object sender,RoutedEventArgs args)
        {
            if(!CheckFreshness())return;var dialog=new SaveFileDialog{Title="导出墙体面积、体积、归属与汇总",Filter="CSV 表格 (*.csv)|*.csv",DefaultExt=".csv",AddExtension=true,OverwritePrompt=true,FileName="BV-墙体试算台账.csv"};if(dialog.ShowDialog(this)!=true)return;
            try{ExportCsv(dialog.FileName);status.Text="已保存："+dialog.FileName;}catch(Exception error){if(error is OutOfMemoryException)throw;status.Text="未保存："+error.Message;}
        }
        private void RequestChanged(object sender,SelectionChangedEventArgs args){if(!initializing){stale=true;SelectedFocusElements=new List<CivilElementRecord>();status.Text="读取条件已变化，请重新读取。";UpdateButtons();}}
        private void UpdateButtons()
        {
            bool valid=!reading&&!stale&&!reportError&&LastBatch!=null&&LastReport!=null;
            refreshButton.IsEnabled=!reading;cancelReadButton.IsEnabled=reading;csvButton.IsEnabled=valid;focusButton.IsEnabled=valid&&FocusCandidates().Count>0;
            projectButton.IsEnabled=valid;loadProjectButton.IsEnabled=valid;saveProjectButton.IsEnabled=valid&&currentProject!=null;
            foreach(Control control in new Control[]{scope,phase,option,geometryView,rulePicker,editRuleButton,loadRuleButton,saveRuleButton,search,statusFilter,quantityLevelPicker,segmentPicker,groupPicker,tabs})control.IsEnabled=!reading;
            projectStatus.Text=currentProject==null?"尚未创建计量工程":currentProject.Name+" · 修订 "+currentProject.Revision+(projectDirty?" · 未保存":"");
            string fingerprint=CivilWallRuleTemplates.Fingerprint(selectedRule);
            ruleStatus.Text="项目约定模板："+selectedRule.Name+" · r"+selectedRule.Revision+" · "+(selectedRule.OpeningPolicy==CivilWallRuleTemplates.VerifiedRectCutout?"真实矩形门窗 + Opening":"仅原生矩形 Opening")+" · 拒绝重叠 · 指纹 "+fingerprint.Substring(0,16)+(stale?" · 旧结果过期／尚未计算":"");
            ruleStatus.ToolTip="模板标识："+selectedRule.TemplateId+"\n规则指纹："+fingerprint+"\n交接策略："+selectedRule.JoinPolicy+"；当前不支持优先级扣量。";
        }
        private CivilReadRequest CurrentRequest(){if(scope.SelectedItem==null||phase.SelectedItem==null||option.SelectedItem==null)throw new InvalidOperationException("请明确选择范围、阶段与设计选项。");return new CivilReadRequest{Scope=Id(scope),PhaseId=Id(phase),DesignOptionId=Id(option)};}
        private string Key(){CivilReadRequest r=CurrentRequest();return r.Scope+"\n"+r.PhaseId+"\n"+r.DesignOptionId+"\n"+Id(geometryView)+"\n"+CivilWallRuleTemplates.Fingerprint(selectedRule);}
        private static void ProjectButton(Panel panel,Button button,string label,Action action){button.Content=label;button.Padding=new Thickness(10,5,10,5);button.Margin=new Thickness(0,0,7,0);button.Click+=delegate{action();};panel.Children.Add(button);}
        private static CivilChoice Choice(string id,string label){return new CivilChoice{Id=id,Label=label};}
        private static string Id(ComboBox box){var c=box.SelectedItem as CivilChoice;return c==null?"":c.Id;}
        private static void Choices(ComboBox box,IEnumerable<CivilChoice> choices,string selected){var list=(choices??new CivilChoice[0]).ToList();box.ItemsSource=list;box.DisplayMemberPath="Label";box.SelectedValuePath="Id";box.SelectedItem=list.FirstOrDefault(c=>c.Id==selected);box.MinHeight=30;}
        private static FrameworkElement Field(string label,FrameworkElement control,double width){var panel=new StackPanel{Width=width,Margin=new Thickness(0,0,10,0)};panel.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,0,0,5),Foreground=Brushes.DimGray});panel.Children.Add(control);return panel;}
        private static Brush ColorBrush(byte r,byte g,byte b){return new SolidColorBrush(Color.FromRgb(r,g,b));}
        private static void ConfigureGrid(DataGrid grid){grid.AutoGenerateColumns=false;grid.IsReadOnly=true;grid.CanUserAddRows=false;grid.CanUserDeleteRows=false;grid.SelectionMode=DataGridSelectionMode.Single;grid.HeadersVisibility=DataGridHeadersVisibility.Column;grid.RowHeaderWidth=0;grid.MinRowHeight=29;grid.AlternatingRowBackground=ColorBrush(247,250,252);}
        private static void Column(DataGrid grid,string header,string binding,double width){var value=new Binding(binding);if(binding.EndsWith("Metres2",StringComparison.Ordinal)||binding.EndsWith("Metres3",StringComparison.Ordinal)){value.StringFormat="0.######";value.TargetNullValue="—";}grid.Columns.Add(new DataGridTextColumn{Header=header,Binding=value,Width=new DataGridLength(width)});}
        private static string Number(double? value){return value.HasValue?value.Value.ToString(Math.Abs(value.Value)>=1e12?"0.########E+0":"0.########",CultureInfo.CurrentCulture):"—";}
        private static string Total(double value,string unit){return Double.IsInfinity(value)||Double.IsNaN(value)?"合计溢出":value.ToString(Math.Abs(value)>=1e12?"0.###E+0":"0.###",CultureInfo.CurrentCulture)+" "+unit;}
        private sealed class FilteredView
        {
            internal List<CivilWallReportRow> Rows;
            internal List<CivilWallReportGroup> Groups;
            internal List<WallRow> Walls;
            internal string Summary;
        }
        private sealed class WallRow
        {
            internal readonly CivilWallReportRow Record;internal WallRow(CivilWallReportRow source){Record=source;}
            public string Name{get{return Record.Result.Fact.Name+" · "+Record.TypeName;}}public string ElementId{get{return Record.Source.ElementId;}}
            public string State{get{return Record.Result.Status=="calculated"?"通过试算":Record.Result.Status=="failed"?"读取／几何失败":"未支持";}}
            public string BasisArea{get{return Number(Record.Result.BasisAreaMetres2);}}public string OpeningArea{get{return Number(Record.Result.OpeningUnionAreaMetres2);}}public string ResultArea{get{return Number(Record.Result.ResultAreaMetres2);}}
            public string Basis{get{return Number(Record.Result.BasisVolumeMetres3);}}public string Deduction{get{return Number(Record.Result.DeductionVolumeMetres3);}}public string Result{get{return Number(Record.Result.ResultVolumeMetres3);}}
            public string LevelName{get{return Record.LevelName;}}public string QuantityLevelName{get{return Record.QuantityLevelName;}}public string SegmentName{get{return Record.SegmentName;}}
            public string Native{get{return Record.Result.Fact.NativeVolume!=null&&Record.Result.Fact.NativeVolume.Status=="available"?Number(Record.Result.Fact.NativeVolume.Value):"—";}}public string Geometry{get{return Number(Record.Result.Fact.GeometryVolumeMetres3);}}public string Message{get{return Record.Result.Message;}}
        }
    }
}
