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
        private readonly TextBox search = new TextBox();
        private readonly TextBlock status = new TextBlock(), summary = new TextBlock(), details = new TextBlock();
        private readonly DataGrid walls = new DataGrid(), ledger = new DataGrid();
        private readonly Button csvButton = new Button(), focusButton = new Button(), refreshButton = new Button();
        private readonly Func<CivilReadRequest, string, CivilWallSnapshot> read;
        private readonly Func<long> revision;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private bool initializing = true, stale = true, reading;
        private string successfulKey;
        private List<CivilWallResult> filtered = new List<CivilWallResult>();
        internal CivilWallCalculationBatch LastBatch { get; private set; }
        internal IList<CivilElementRecord> SelectedFocusElements { get; private set; }

        internal CivilWallWindow(CivilReadRequest initial, IList<CivilChoice> phases, IList<CivilChoice> options, IList<CivilChoice> views,
            string selectedView, Func<CivilReadRequest, string, CivilWallSnapshot> read, Func<long> revision)
        {
            if (initial == null || read == null || revision == null) throw new ArgumentNullException("initial/read/revision");
            this.read = read; this.revision = revision; SelectedFocusElements = new List<CivilElementRecord>();
            Title = "BV · 墙体试算与台账 · 崛亮视觉"; Icon = Branding.Icon(32);
            Width = 1320; Height = 900; MinWidth = 1080; MinHeight = 720; MaxHeight = Math.Max(720, SystemParameters.WorkArea.Height - 24);
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Background = Brushes.White; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12;
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.65, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = root;
            Border header = new Border { Background = ColorBrush(17,35,55), Padding = new Thickness(20,14,20,14) };
            StackPanel title = new StackPanel();
            title.Children.Add(new TextBlock { Text = "墙体试算与逐条台账", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            title.Children.Add(new TextBlock { Text = "崛亮视觉 · Bright Vision  |  " + CivilWallCalculator.RuleId + " · 项目约定", Foreground = ColorBrush(99,225,198), Margin = new Thickness(0,5,0,0) });
            header.Child = title; root.Children.Add(header);
            StackPanel controls = new StackPanel { Margin = new Thickness(20,12,20,8) };
            WrapPanel request = new WrapPanel();
            Choices(scope, new[] { Choice("view","当前活动视图"),Choice("selection","当前选择"),Choice("project","当前工程（根文档）") }, initial.Scope);
            Choices(phase, phases, initial.PhaseId); Choices(option, options, initial.DesignOptionId); Choices(geometryView, views, selectedView);
            request.Children.Add(Field("计量范围",scope,150)); request.Children.Add(Field("阶段",phase,200)); request.Children.Add(Field("设计选项",option,160)); request.Children.Add(Field("几何校验视图（不切换活动视图）",geometryView,285));
            refreshButton.Content = "读取并试算"; refreshButton.Padding = new Thickness(16,7,16,7); refreshButton.Margin = new Thickness(5,20,0,0);
            refreshButton.Click += delegate { RefreshCalculation(); }; request.Children.Add(refreshButton); controls.Children.Add(request);
            controls.Children.Add(new TextBlock { Text = "矩形基准扣明确矩形洞口后，须通过实际实体双向差集及墙间重叠检查。原生量单列；门窗名义尺寸不直接扣减。仅部分主模型普通直墙，不含梁柱板／链接交接和地区规则。", TextWrapping = TextWrapping.Wrap, Foreground = ColorBrush(130,88,25), Margin = new Thickness(0,10,0,0) });
            status.Text = "请选择范围、阶段和同阶段的三维校验视图。"; status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0,7,0,0); controls.Children.Add(status);
            Grid.SetRow(controls,1); root.Children.Add(controls);
            StackPanel filter = new StackPanel { Margin = new Thickness(20,0,20,8) };
            WrapPanel filters = new WrapPanel(); search.Padding = new Thickness(7); search.MinHeight = 30;
            filters.Children.Add(Field("搜索墙名 / 类型 / 来源 ID",search,440));
            Choices(statusFilter,new[] { Choice("","全部状态"),Choice("calculated","已通过试算"),Choice("unsupported","未支持"),Choice("failed","读取或几何失败") }, "");
            filters.Children.Add(Field("状态",statusFilter,200)); filter.Children.Add(filters);
            summary.Margin = new Thickness(0,8,0,0); summary.FontWeight = FontWeights.SemiBold; summary.TextWrapping = TextWrapping.Wrap; filter.Children.Add(summary);
            Grid.SetRow(filter,2); root.Children.Add(filter);
            ConfigureGrid(walls); ConfigureGrid(ledger);
            Column(walls,"墙 / 类型","Name",185); Column(walls,"ID","ElementId",85); Column(walls,"状态","State",105);
            Column(walls,"矩形基准 m³","Basis",110); Column(walls,"洞口并集 m²","OpeningArea",110); Column(walls,"扣减 m³","Deduction",100);
            Column(walls,"项目试算 m³","Result",115); Column(walls,"Revit原生 m³","Native",115); Column(walls,"实际实体 m³","Geometry",115); Column(walls,"说明","Message",380);
            walls.Margin = new Thickness(20,0,20,8); walls.SelectionChanged += delegate { ShowLedger(); }; Grid.SetRow(walls,3); root.Children.Add(walls);
            Grid ledgerPanel = new Grid { Margin = new Thickness(20,0,20,0) };
            ledgerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); ledgerPanel.RowDefinitions.Add(new RowDefinition());
            details.Text = "选择一面墙，查看基准与逐条洞口并集扣减。未支持项不会产生试算量。"; details.TextWrapping = TextWrapping.Wrap; details.Margin = new Thickness(0,2,0,8); ledgerPanel.Children.Add(details);
            Column(ledger,"步骤","Description",320); Column(ledger,"关联ID","ElementId",85); Column(ledger,"新增并集 m²","AreaMetres2",105);
            Column(ledger,"体积变化 m³","DeltaVolumeMetres3",115); Column(ledger,"公式","Formula",250); Column(ledger,"来源","Source",350);
            ledger.SelectionChanged += delegate { UpdateButtons(); }; Grid.SetRow(ledger,1); ledgerPanel.Children.Add(ledger); Grid.SetRow(ledgerPanel,4); root.Children.Add(ledgerPanel);
            DockPanel footer = new DockPanel { Margin = new Thickness(20,12,20,14) };
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            csvButton.Content = "导出当前试算及台账 CSV…"; csvButton.Padding = new Thickness(12,6,12,6); csvButton.Click += SaveCsv;
            focusButton.Content = "定位墙／洞口并关闭"; focusButton.Padding = new Thickness(12,6,12,6); focusButton.Margin = new Thickness(8,0,8,0);
            focusButton.Click += delegate { if (PrepareFocus()) Close(); };
            Button close = new Button { Content = "关闭", IsCancel = true, Padding = new Thickness(16,6,16,6) }; close.Click += delegate { Close(); };
            buttons.Children.Add(csvButton); buttons.Children.Add(focusButton); buttons.Children.Add(close); DockPanel.SetDock(buttons,Dock.Right); footer.Children.Add(buttons);
            footer.Children.Add(new TextBlock { Text = "只读 RVT · 非地区清单／定额量", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
            Grid.SetRow(footer,5); root.Children.Add(footer);
            scope.SelectionChanged += RequestChanged; phase.SelectionChanged += RequestChanged; option.SelectionChanged += RequestChanged; geometryView.SelectionChanged += RequestChanged;
            search.TextChanged += delegate { ApplyFilters(); }; statusFilter.SelectionChanged += delegate { ApplyFilters(); };
            timer.Interval = TimeSpan.FromMilliseconds(800); timer.Tick += delegate { CheckFreshness(); };
            Loaded += delegate { timer.Start(); }; Closed += delegate { timer.Stop(); }; initializing = false; UpdateButtons();
        }

        internal void RefreshCalculation()
        {
            if (reading) return;
            reading = true; stale = true; refreshButton.IsEnabled = false; UpdateButtons();
            status.Text = "正在读取墙体事实和校验实体…";
            try
            {
                CivilReadRequest request = CurrentRequest(); string view = Id(geometryView);
                if (String.IsNullOrEmpty(view)) throw new InvalidOperationException("请选择明确的几何校验三维视图。");
                CivilWallSnapshot facts = read(request,view);
                CivilWallCalculationBatch batch = CivilWallCalculator.Calculate(facts);
                LastBatch = batch; successfulKey = Key(); stale = revision() != facts.NativeSnapshot.Revision;
                status.Text = stale ? "读取期间模型发生变化，请重新读取。" : facts.NativeSnapshot.ModelTitle + " · " + facts.NativeSnapshot.ScopeLabel + " · " + facts.NativeSnapshot.PhaseLabel + " · 几何视图：" + ((CivilChoice)geometryView.SelectedItem).Label;
                ApplyFilters();
            }
            catch (Exception error) { if (error is OutOfMemoryException) throw; status.Text = "本轮未完成：" + error.Message + (LastBatch == null ? "" : "；旧结果已过期。"); }
            finally { reading = false; refreshButton.IsEnabled = true; UpdateButtons(); }
        }
        internal void ApplyFilters()
        {
            if (initializing || LastBatch == null) return;
            string query = search.Text.Trim(), state = Id(statusFilter);
            filtered = LastBatch.Results.Where(r => (state.Length == 0 || r.Status == state) && (query.Length == 0 ||
                String.Join(" ",new[] {r.Fact.Name,r.Fact.TypeName,r.Fact.ElementId,r.Fact.UniqueId,r.Message}).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0)).ToList();
            walls.ItemsSource = filtered.Select(r => new WallRow(r)).ToList();
            List<CivilWallResult> calculated = filtered.Where(r => r.Status == "calculated").ToList();
            double total = calculated.Sum(r => r.ResultVolumeMetres3.Value);
            summary.Text = "当前 " + filtered.Count + " 面墙 · 通过 " + calculated.Count + " · 未纳入 " + (filtered.Count-calculated.Count) + " · 项目试算体积 " + (Double.IsInfinity(total) || Double.IsNaN(total) ? "合计溢出" : total.ToString("0.###",CultureInfo.CurrentCulture)+" m³") + "（仅通过项，原生量未替代）";
            ShowLedger(); UpdateButtons();
        }
        internal bool CheckFreshness()
        {
            if (!stale && LastBatch != null)
            {
                try { if (Key()!=successfulKey || revision()!=LastBatch.Input.NativeSnapshot.Revision) { stale=true; status.Text="模型或读取条件已变化，旧结果过期，请重新读取。"; } }
                catch (Exception error) { if (error is OutOfMemoryException) throw; stale=true; status.Text="无法确认来源状态："+error.Message; }
            }
            UpdateButtons(); return !stale && LastBatch != null;
        }
        internal string BuildCurrentCsv() { if (!CheckFreshness()) throw new InvalidOperationException("旧结果不可导出，请重新读取。"); return CivilWallCalculator.ToCsv(LastBatch,filtered); }
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
            if(!CheckFreshness()) return false;
            CivilWallLedgerEntry entry=ledger.SelectedItem as CivilWallLedgerEntry;
            WallRow row=walls.SelectedItem as WallRow;
            string id=entry==null?(row==null?"":row.Source.Fact.ElementId):entry.ElementId;
            string uid=entry==null?(row==null?"":row.Source.Fact.UniqueId):entry.UniqueId;
            if(String.IsNullOrEmpty(id)||String.IsNullOrEmpty(uid)) return false;
            SelectedFocusElements=new List<CivilElementRecord> {new CivilElementRecord {ElementId=id,UniqueId=uid}}; return true;
        }
        private void ShowLedger()
        {
            WallRow row=walls.SelectedItem as WallRow; ledger.ItemsSource=row==null?new List<CivilWallLedgerEntry>():row.Source.Ledger;
            details.Text=row==null?"选择一面墙查看台账；未支持项不输出试算量。":row.Source.Message + "\n来源："+row.Source.Fact.UniqueId+" · 双向差集 "+Number(row.Source.Fact.SymmetricDifferenceMetres3)+" m³ · 容差 "+Number(row.Source.Fact.GeometryToleranceMetres3)+" m³";
            UpdateButtons();
        }
        private void SaveCsv(object sender,RoutedEventArgs args)
        {
            if(!CheckFreshness())return;
            var dialog=new SaveFileDialog {Title="导出墙体试算与台账",Filter="CSV 表格 (*.csv)|*.csv",DefaultExt=".csv",AddExtension=true,OverwritePrompt=true,FileName="BV-墙体试算台账.csv"};
            if(dialog.ShowDialog(this)!=true)return;
            try{ExportCsv(dialog.FileName);status.Text="已保存："+dialog.FileName;}catch(Exception error){if(error is OutOfMemoryException)throw;status.Text="未保存："+error.Message;}
        }
        private void RequestChanged(object sender,SelectionChangedEventArgs args){if(!initializing){stale=true;status.Text="读取条件已变化，请重新读取。";UpdateButtons();}}
        private void UpdateButtons(){bool valid=!reading&&!stale&&LastBatch!=null;csvButton.IsEnabled=valid;focusButton.IsEnabled=valid&&(walls.SelectedItem!=null||ledger.SelectedItem!=null);}
        private CivilReadRequest CurrentRequest(){if(scope.SelectedItem==null||phase.SelectedItem==null||option.SelectedItem==null)throw new InvalidOperationException("请明确选择范围、阶段与设计选项。");return new CivilReadRequest {Scope=Id(scope),PhaseId=Id(phase),DesignOptionId=Id(option)};}
        private string Key(){CivilReadRequest r=CurrentRequest();return r.Scope+"\n"+r.PhaseId+"\n"+r.DesignOptionId+"\n"+Id(geometryView);}
        private static CivilChoice Choice(string id,string label){return new CivilChoice {Id=id,Label=label};}
        private static string Id(ComboBox box){var c=box.SelectedItem as CivilChoice;return c==null?"":c.Id;}
        private static void Choices(ComboBox box,IEnumerable<CivilChoice> choices,string selected){var list=(choices??new CivilChoice[0]).ToList();box.ItemsSource=list;box.DisplayMemberPath="Label";box.SelectedValuePath="Id";box.SelectedItem=list.FirstOrDefault(c=>c.Id==selected);box.MinHeight=30;}
        private static FrameworkElement Field(string label,FrameworkElement control,double width){var panel=new StackPanel {Width=width,Margin=new Thickness(0,0,10,0)};panel.Children.Add(new TextBlock {Text=label,Margin=new Thickness(0,0,0,5),Foreground=Brushes.DimGray});panel.Children.Add(control);return panel;}
        private static Brush ColorBrush(byte r,byte g,byte b){return new SolidColorBrush(Color.FromRgb(r,g,b));}
        private static void ConfigureGrid(DataGrid grid){grid.AutoGenerateColumns=false;grid.IsReadOnly=true;grid.CanUserAddRows=false;grid.CanUserDeleteRows=false;grid.SelectionMode=DataGridSelectionMode.Single;grid.HeadersVisibility=DataGridHeadersVisibility.Column;grid.RowHeaderWidth=0;grid.MinRowHeight=29;grid.AlternatingRowBackground=ColorBrush(247,250,252);}
        private static void Column(DataGrid grid,string header,string binding,double width){var value=new Binding(binding);if(binding=="AreaMetres2"||binding=="DeltaVolumeMetres3")value.StringFormat="0.######";grid.Columns.Add(new DataGridTextColumn {Header=header,Binding=value,Width=new DataGridLength(width)});}
        private static string Number(double? value){return value.HasValue?value.Value.ToString("0.########",CultureInfo.CurrentCulture):"—";}
        private sealed class WallRow
        {
            internal readonly CivilWallResult Source; internal WallRow(CivilWallResult source){Source=source;}
            public string Name{get{return Source.Fact.Name+" · "+Source.Fact.TypeName;}}public string ElementId{get{return Source.Fact.ElementId;}}
            public string State{get{return Source.Status=="calculated"?"通过试算":Source.Status=="failed"?"读取／几何失败":"未支持";}}
            public string Basis{get{return Number(Source.BasisVolumeMetres3);}}public string OpeningArea{get{return Number(Source.OpeningUnionAreaMetres2);}}
            public string Deduction{get{return Number(Source.DeductionVolumeMetres3);}}public string Result{get{return Number(Source.ResultVolumeMetres3);}}
            public string Native{get{return Source.Fact.NativeVolume!=null&&Source.Fact.NativeVolume.Status=="available"?Number(Source.Fact.NativeVolume.Value):"—";}}
            public string Geometry{get{return Number(Source.Fact.GeometryVolumeMetres3);}}public string Message{get{return Source.Message;}}
        }
    }
}
