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
        private readonly TextBox search = new TextBox();
        private readonly TextBlock status = new TextBlock(), summary = new TextBlock(), details = new TextBlock(), projectStatus = new TextBlock();
        private readonly DataGrid walls = new DataGrid(), ledger = new DataGrid(), groups = new DataGrid();
        private readonly TabControl tabs = new TabControl();
        private readonly Button csvButton = new Button(), focusButton = new Button(), refreshButton = new Button();
        private readonly Button projectButton = new Button(), loadProjectButton = new Button(), saveProjectButton = new Button();
        private readonly Func<CivilReadRequest, string, CivilWallSnapshot> read;
        private readonly Func<long> revision;
        private readonly Func<string, bool> confirmDiscard;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private bool initializing = true, stale = true, reading, projectDirty, reportError;
        private string successfulKey, projectPath;
        private CivilProjectSettings currentProject;
        private List<CivilWallReportRow> filtered = new List<CivilWallReportRow>();
        internal CivilWallCalculationBatch LastBatch { get; private set; }
        internal CivilWallReportData LastReport { get; private set; }
        internal IList<CivilElementRecord> SelectedFocusElements { get; private set; }

        internal CivilWallWindow(CivilReadRequest initial, IList<CivilChoice> phases, IList<CivilChoice> options, IList<CivilChoice> views,
            string selectedView, Func<CivilReadRequest, string, CivilWallSnapshot> read, Func<long> revision, Func<string, bool> confirmDiscard = null)
        {
            if (initial == null || read == null || revision == null) throw new ArgumentNullException("initial/read/revision");
            this.read = read; this.revision = revision;
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
            refreshButton.Click += delegate { RefreshCalculation(); }; request.Children.Add(refreshButton); controls.Children.Add(request);
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
            search.TextChanged += delegate { ApplyFilters(); }; statusFilter.SelectionChanged += delegate { ApplyFilters(); };
            quantityLevelPicker.SelectionChanged += delegate { ApplyFilters(); }; segmentPicker.SelectionChanged += delegate { ApplyFilters(); }; groupPicker.SelectionChanged += delegate { ApplyFilters(); };
            timer.Interval = TimeSpan.FromMilliseconds(800); timer.Tick += delegate { CheckFreshness(); }; Loaded += delegate { timer.Start(); }; Closed += delegate { timer.Stop(); };
            Closing += delegate(object sender,System.ComponentModel.CancelEventArgs args) { if (!CanDiscardProjectChanges("计量工程设置尚未保存到 JSON。是否放弃未保存的设置并关闭？")) { args.Cancel = true; SelectedFocusElements = new List<CivilElementRecord>(); } };
            initializing = false; UpdateButtons();
        }

        internal void RefreshCalculation()
        {
            if (reading) return;
            reading = true; stale = true; SelectedFocusElements = new List<CivilElementRecord>(); UpdateButtons(); status.Text = "正在读取墙体事实和校验实体…";
            try
            {
                CivilReadRequest request = CurrentRequest(); string view = Id(geometryView);
                if (String.IsNullOrEmpty(view)) throw new InvalidOperationException("请选择明确的几何校验三维视图。");
                CivilWallSnapshot facts = read(request,view); CivilWallCalculationBatch batch = CivilWallCalculator.Calculate(facts);
                CivilWallReportData report = CivilWallReport.Create(batch,currentProject);
                LastBatch = batch; LastReport = report; successfulKey = Key(); stale = revision() != facts.NativeSnapshot.Revision;
                status.Text = stale ? "读取期间模型发生变化，请重新读取。" : facts.NativeSnapshot.ModelTitle + " · " + facts.NativeSnapshot.ScopeLabel + " · " + facts.NativeSnapshot.PhaseLabel + " · 几何视图：" + ((CivilChoice)geometryView.SelectedItem).Label;
                ConfigureProjectFilters(); ApplyFilters();
            }
            catch (Exception error) { if (error is OutOfMemoryException) throw; status.Text = "本轮未完成：" + error.Message + (LastBatch == null ? "" : "；旧结果已过期。"); }
            finally { reading = false; UpdateButtons(); }
        }
        internal void ApplyFilters()
        {
            if (initializing || LastReport == null) return;
            SelectedFocusElements = new List<CivilElementRecord>();
            try
            {
                List<CivilWallReportRow> next = CivilWallReport.Filter(LastReport,search.Text.Trim(),Id(statusFilter),Id(quantityLevelPicker),Id(segmentPicker));
                List<CivilWallReportGroup> nextGroups = CivilWallReport.Group(next,Id(groupPicker));
                List<CivilWallReportRow> calculated = next.Where(row => row.Result.Status == "calculated").ToList();
                double area, volume; Totals(calculated,out area,out volume);
                List<WallRow> nextWalls = next.Select(row => new WallRow(row)).ToList();
                string text = "当前 " + next.Count + " 面墙 · 通过 " + calculated.Count + " · 未纳入 " + (next.Count-calculated.Count) + " · 净投影面积 " + (calculated.Count==0?"—":Total(area,"m²")) + " · 试算体积 " + (calculated.Count==0?"—":Total(volume,"m³")) + "（仅通过项；原生量未替代）";
                bool recovered = reportError; filtered = next; walls.ItemsSource = nextWalls; groups.ItemsSource = nextGroups; summary.Text = text; reportError = false;
                if(recovered)status.Text="当前筛选已恢复，可导出和定位；几何读取条件未改变。";
                ShowLedger();
            }
            catch(Exception error)
            {
                if(error is OutOfMemoryException)throw;
                reportError=true;status.Text="当前筛选汇总未完成："+error.Message+"；旧表仅供查看，当前范围不可导出或定位。请缩小筛选范围或重新读取。";
            }
            UpdateButtons();
        }
        private static void Totals(List<CivilWallReportRow> rows,out double area,out double volume)
        {
            area=rows.Sum(row=>row.Result.ResultAreaMetres2.Value);volume=rows.Sum(row=>row.Result.ResultVolumeMetres3.Value);
            if(Double.IsNaN(area)||Double.IsInfinity(area)||Double.IsNaN(volume)||Double.IsInfinity(volume))throw new InvalidDataException("通过项的筛选合计溢出，未输出非有限数量。");
        }
        private void ValidateProjectView(CivilWallReportData report,CivilProjectSettings profile)
        {
            string quantity=Id(quantityLevelPicker),segment=Id(segmentPicker);
            if(quantity!=""&&quantity!="__unassigned__"&&!profile.QuantityLevels.Any(level=>level.Id==quantity))quantity="";
            if(segment!=""&&segment!="__unassigned__"&&!profile.Segments.Any(item=>item.Id==segment))segment="";
            List<CivilWallReportRow> rows=CivilWallReport.Filter(report,search.Text.Trim(),Id(statusFilter),quantity,segment);
            CivilWallReport.Group(rows,Id(groupPicker));double area,volume;Totals(rows.Where(row=>row.Result.Status=="calculated").ToList(),out area,out volume);
        }
        internal bool CheckFreshness()
        {
            if (!stale && LastBatch != null)
            {
                try { if (Key()!=successfulKey || revision()!=LastBatch.Input.NativeSnapshot.Revision) { stale=true; status.Text="模型或读取条件已变化，旧结果过期，请重新读取。"; } }
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
            if(!CheckFreshness())throw new InvalidOperationException("请先读取并试算，再设置计量工程。");
            CivilProjectSettings candidate=CivilProject.Clone(profile); CivilWallReportData applied=CivilWallReport.Create(LastBatch,candidate);ValidateProjectView(applied,candidate);
            if(currentProject==null||currentProject.ProjectId!=candidate.ProjectId)projectPath=null;
            currentProject=candidate;LastReport=applied;projectDirty=true;ConfigureProjectFilters();ApplyFilters();UpdateButtons();
        }
        internal void SaveProject(string path)
        {
            if(!CheckFreshness()||currentProject==null)throw new InvalidOperationException("请先读取并设置计量工程。");
            CivilProjectSettings candidate=CivilProject.Clone(currentProject);CivilReadRequest request=CurrentRequest();
            if(candidate.PreferredScope!=request.Scope||candidate.PhaseId!=request.PhaseId||candidate.DesignOptionId!=request.DesignOptionId){candidate.PreferredScope=request.Scope;candidate.PhaseId=request.PhaseId;candidate.DesignOptionId=request.DesignOptionId;CivilProject.Touch(candidate);}
            CivilWallReportData applied=CivilWallReport.Create(LastBatch,candidate);ValidateProjectView(applied,candidate);string destination=Path.GetFullPath(path);
            if(!destination.EndsWith(".bvproject.json",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("请选择 .bvproject.json 工程文件。");
            CivilProjectStore.Save(destination,candidate);currentProject=candidate;LastReport=applied;projectPath=destination;projectDirty=false;ApplyFilters();UpdateButtons();
        }
        internal void LoadProject(string path)
        {
            if(!CheckFreshness())throw new InvalidOperationException("请先读取并试算，再打开计量工程。");
            CivilProjectSettings candidate=CivilProjectStore.Load(path);CivilWallReportData applied=CivilWallReport.Create(LastBatch,candidate);ValidateProjectView(applied,candidate);string destination=Path.GetFullPath(path);
            currentProject=candidate;LastReport=applied;projectPath=destination;projectDirty=false;ConfigureProjectFilters();ApplyFilters();UpdateButtons();
            status.Text="已打开工程设置；模型来源已核对，读取范围／阶段／设计选项／校验视图继续使用当前选择。";
        }
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
        private void UpdateButtons(){bool valid=!reading&&!stale&&!reportError&&LastBatch!=null&&LastReport!=null;refreshButton.IsEnabled=!reading;csvButton.IsEnabled=valid;focusButton.IsEnabled=valid&&FocusCandidates().Count>0;projectButton.IsEnabled=valid;loadProjectButton.IsEnabled=valid;saveProjectButton.IsEnabled=valid&&currentProject!=null;projectStatus.Text=currentProject==null?"尚未创建计量工程":currentProject.Name+" · 修订 "+currentProject.Revision+(projectDirty?" · 未保存":"");}
        private CivilReadRequest CurrentRequest(){if(scope.SelectedItem==null||phase.SelectedItem==null||option.SelectedItem==null)throw new InvalidOperationException("请明确选择范围、阶段与设计选项。");return new CivilReadRequest{Scope=Id(scope),PhaseId=Id(phase),DesignOptionId=Id(option)};}
        private string Key(){CivilReadRequest r=CurrentRequest();return r.Scope+"\n"+r.PhaseId+"\n"+r.DesignOptionId+"\n"+Id(geometryView);}
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
