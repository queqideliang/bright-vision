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
    internal delegate RevitWallJoinReadBundle CivilWallJoinReadDelegate(CivilReadRequest request, string viewId, CivilWallRuleTemplate rule, CivilWallReadControl control);

    internal sealed class CivilWallJoinWindow : Window
    {
        private readonly ComboBox scope = new ComboBox(), phase = new ComboBox(), option = new ComboBox(), view = new ComboBox(), rules = new ComboBox();
        private readonly DataGrid components = new DataGrid(), walls = new DataGrid(), steps = new DataGrid();
        private readonly TextBlock status = new TextBlock(), summary = new TextBlock(), explanation = new TextBlock();
        private readonly Button readButton = new Button(), calculateButton = new Button(), cancelButton = new Button(), editButton = new Button(), loadButton = new Button(), savePlanButton = new Button(), csvButton = new Button(), focusButton = new Button();
        private readonly CheckBox dependencies = new CheckBox { Content = "明细与 CSV 包含范围外依赖墙", IsChecked = true };
        private readonly CivilWallJoinReadDelegate reader;
        private readonly Func<long> revision;
        private readonly Dictionary<string, CivilWallJoinPlan> plans = new Dictionary<string, CivilWallJoinPlan>(StringComparer.Ordinal);
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly System.Diagnostics.Stopwatch progressClock = new System.Diagnostics.Stopwatch();
        private RevitWallJoinReadBundle bundle;
        private CivilWallJoinSnapshot snapshot;
        private string readKey, readFingerprint;
        private bool reading, cancelled, stale = true, calculating, initializing = true, pumping, closeWhenDone;
        private long lastPump;
        internal CivilWallJoinBatch LastBatch { get; private set; }
        internal IList<CivilElementRecord> SelectedFocusElements { get; private set; }

        internal CivilWallJoinWindow(CivilReadRequest initial, IList<CivilChoice> phases, IList<CivilChoice> options, IList<CivilChoice> views, string initialView, CivilWallJoinReadDelegate reader, Func<long> revision)
        {
            if (initial == null || reader == null || revision == null) throw new ArgumentNullException("initial/reader/revision");
            this.reader = reader; this.revision = revision; SelectedFocusElements = new List<CivilElementRecord>();
            Title = "BV · 墙体交接核算 · 崛亮视觉"; Icon = Branding.Icon(32); Width = 1340; Height = 900; MinWidth = 1080; MinHeight = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12; ShowInTaskbar = false;
            Grid root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = root;
            var header = new Border { Background = new SolidColorBrush(Color.FromRgb(17,35,55)), Padding = new Thickness(20,12,20,12) };
            var heading = new StackPanel(); heading.Children.Add(new TextBlock { Text = "墙体交接体积与逐条扣量", FontSize = 22, Foreground = Brushes.White });
            heading.Children.Add(new TextBlock { Text = "崛亮视觉 · Bright Vision  |  " + CivilWallJoinCore.MethodId + " · 项目约定", Foreground = Brushes.Aquamarine, Margin = new Thickness(0,4,0,0) }); header.Child = heading; root.Children.Add(header);
            var controls = new StackPanel { Margin = new Thickness(20,10,20,8) }; var request = new WrapPanel();
            Choices(scope, new[] { Choice("view","当前活动视图"),Choice("selection","当前选择"),Choice("project","当前工程根文档") }, initial.Scope);
            Choices(phase, phases, initial.PhaseId); Choices(option, options, initial.DesignOptionId); Choices(view, views, initialView);
            request.Children.Add(Field("请求范围",scope,145)); request.Children.Add(Field("阶段",phase,145)); request.Children.Add(Field("设计选项",option,145)); request.Children.Add(Field("同阶段三维校验视图（不切换视图）",view,235));
            rules.ItemsSource = CivilWallRuleTemplates.Catalog(); rules.DisplayMemberPath = "Name"; rules.SelectedIndex = 0; request.Children.Add(Field("前置切洞规则",rules,220)); controls.Children.Add(request);
            var actions = new WrapPanel { Margin = new Thickness(0,8,0,6) }; Add(actions,readButton,"读取完整交接组件",ReadComponents); Add(actions,editButton,"确认／修改选中组件顺序…",EditPlan); Add(actions,loadButton,"打开交接方案…",OpenPlan); Add(actions,savePlanButton,"保存交接方案…",SavePlanDialog); Add(actions,calculateButton,"按确认顺序核算",Calculate); Add(actions,cancelButton,"取消本轮",RequestCancel); controls.Children.Add(actions);
            controls.Children.Add(new TextBlock { Text = "优先级 1 保有交集；范围外邻墙参与扣重，导出范围不改变分配。此处仅核算体积；交接体积除厚度不是面积。单墙采用唯一顺序，多墙必须明确确认。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SaddleBrown });
            status.Text = "先读取组件，再逐个确认多墙组件的优先顺序。未知几何和不完整依赖不会出量。"; status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0,6,0,0); controls.Children.Add(status);
            summary.FontWeight = FontWeights.SemiBold; summary.TextWrapping = TextWrapping.Wrap; summary.Margin = new Thickness(0,5,0,0); controls.Children.Add(summary); Grid.SetRow(controls,1); root.Children.Add(controls);
            var tables = new Grid { Margin = new Thickness(20,0,20,0) }; tables.RowDefinitions.Add(new RowDefinition { Height = new GridLength(.8,GridUnitType.Star) }); tables.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); tables.RowDefinitions.Add(new RowDefinition()); tables.RowDefinitions.Add(new RowDefinition { Height = new GridLength(.65,GridUnitType.Star) });
            Configure(components); Column(components,"组件（几何指纹）","Id",190); Column(components,"参与墙","Count",70); Column(components,"请求墙","Requested",70); Column(components,"顺序方案","Plan",190); Column(components,"状态","Status",95); Column(components,"完整组件分配合计 m³","Volume",175); Column(components,"说明","Message",420); components.SelectionChanged += delegate { ShowComponent(); }; tables.Children.Add(components);
            explanation.TextWrapping = TextWrapping.Wrap; explanation.Margin = new Thickness(0,5,0,5); Grid.SetRow(explanation,1); tables.Children.Add(explanation);
            Configure(walls); Column(walls,"墙 / 类型","Name",220); Column(walls,"ID","ElementId",75); Column(walls,"角色","Role",105); Column(walls,"顺序","Rank",65); Column(walls,"参数毛量 m³","Gross",115); Column(walls,"扣洞 m³","Opening",105); Column(walls,"洞后 m³","Expected",105); Column(walls,"交接扣量 m³","Join",115); Column(walls,"分配结果 m³","Result",120); Column(walls,"实际实体 m³","Actual",120); Column(walls,"原生参照 m³","Native",120); walls.SelectionChanged += delegate { ShowSteps(); }; walls.Margin = new Thickness(0,0,0,6); Grid.SetRow(walls,2); tables.Children.Add(walls);
            Configure(steps); Column(steps,"高优先墙UniqueId","HigherUniqueId",320); Column(steps,"关联ID","HigherElementId",85); Column(steps,"优先级","HigherPriorityRank",75); Column(steps,"扣前 m³","BeforeVolumeMetres3",140); Column(steps,"扣后 m³","AfterVolumeMetres3",140); Column(steps,"本步扣量 m³","DeductionVolumeMetres3",150); Grid.SetRow(steps,3); tables.Children.Add(steps); Grid.SetRow(tables,2); root.Children.Add(tables);
            var footer = new DockPanel { Margin = new Thickness(20,10,20,12) }; var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Add(buttons,csvButton,"导出交接体积及台账 CSV…",SaveCsv); Add(buttons,focusButton,"定位选中墙并关闭",delegate { if (PrepareFocus()) Close(); }); var close = new Button(); Add(buttons,close,"关闭",Close); DockPanel.SetDock(buttons,Dock.Right); footer.Children.Add(buttons); footer.Children.Add(dependencies); Grid.SetRow(footer,3); root.Children.Add(footer);
            foreach (ComboBox box in new[] { scope,phase,option,view,rules }) box.SelectionChanged += delegate { if (!initializing) Invalidate("读取条件或切洞规则已变化，请重新读取组件。"); };
            dependencies.Checked += delegate { if (!initializing) ShowComponent(); }; dependencies.Unchecked += delegate { if (!initializing) ShowComponent(); };
            timer.Interval = TimeSpan.FromMilliseconds(800); timer.Tick += delegate { SourceFresh(); }; Loaded += delegate { timer.Start(); };
            Closing += delegate(object sender,System.ComponentModel.CancelEventArgs args) { if (reading || calculating) { RequestCancel(); closeWhenDone = true; args.Cancel = true; } };
            Closed += delegate { timer.Stop(); if (bundle != null) { bundle.Dispose(); bundle = null; } };
            initializing = false; UpdateButtons();
        }

        internal void ReadComponents()
        {
            if (reading || calculating) return; reading = true; cancelled = false; Invalidate("正在读取依赖目录与实体…"); progressClock.Restart(); lastPump = -50;
            RevitWallJoinReadBundle candidate = null;
            try
            {
                string key = Key(); long before = revision(); var control = Control();
                candidate = reader(CurrentRequest(),Id(view),SelectedRule(),control); control.Checkpoint("提交组件读取前核对",0,0);
                if (candidate == null || candidate.NativeSnapshot == null || before != revision() || candidate.NativeSnapshot.Revision != before || key != Key() ||
                    candidate.GeometryViewUniqueId != Id(view) || candidate.GeometryPhaseId != CurrentRequest().PhaseId ||
                    CivilWallRuleTemplates.Fingerprint(candidate.RuleTemplate) != CivilWallRuleTemplates.Fingerprint(SelectedRule())) throw new InvalidOperationException("读取期间来源、条件或返回的规则／视图不一致，未替换已读取组件。");
                CivilWallJoinSnapshot next = Capture(candidate); next.PhaseId = CurrentRequest().PhaseId; next.DesignOptionId = CurrentRequest().DesignOptionId;
                CivilWallJoinSession.ValidateReadFacts(next); string fingerprint = CivilWallBatchIntegrity.FactsFingerprint(next);
                var nextPlans = new Dictionary<string,CivilWallJoinPlan>(StringComparer.Ordinal);
                foreach (RevitWallJoinPreparedComponent component in candidate.Components.Where(Ready)) if (component.Participants.Count == 1)
                    nextPlans[component.ComponentId] = CivilWallJoinPlans.Create(component.Binding,component.Participants.Select(wall => wall.ElementUniqueId),"单墙唯一顺序");
                candidate.Components.Select(item => new ComponentRow(item,nextPlans.ContainsKey(item.ComponentId)?nextPlans[item.ComponentId]:null,null)).ToList();
                if (bundle != null) bundle.Dispose(); bundle = candidate; candidate = null; snapshot = next; readKey = key; readFingerprint = fingerprint; plans.Clear(); LastBatch = null; stale = true;
                foreach (var item in nextPlans) plans.Add(item.Key,item.Value);
                PublishComponents(); status.Text = "组件已读取；多墙组件需明确确认优先顺序，才可核算。范围外邻墙仍参与。";
            }
            catch (OperationCanceledException) { status.Text = "读取已取消，没有提交部分组件；旧结果已过期。"; }
            catch (Exception error) { if (error is OutOfMemoryException) throw; status.Text = "读取未完成：" + error.Message; }
            finally { if (candidate != null) candidate.Dispose(); reading = false; FinishOperation(); }
        }

        internal void SetPlan(string componentId, CivilWallJoinPlan plan)
        {
            EnsureIdle(); if (!ReadFresh()) throw new InvalidOperationException("请先重新读取组件。");
            RevitWallJoinPreparedComponent component = bundle.Components.Single(item => item.ComponentId == componentId);
            if (!Ready(component)) throw new InvalidOperationException("该组件未具备完整几何证据，不能配置后出量。");
            CivilWallJoinPlans.ValidateFor(plan,component.Binding,component.Participants.Select(wall => wall.ElementUniqueId)); plans[componentId] = CivilWallJoinPlans.Clone(plan);
            Invalidate("交接顺序已改变，旧计算结果过期；请重新核算。"); PublishComponents();
        }

        internal void Calculate()
        {
            EnsureIdle(); if (!ReadFresh()) throw new InvalidOperationException("请先重新读取组件。");
            foreach (RevitWallJoinPreparedComponent component in bundle.Components.Where(Ready))
            {
                CivilWallJoinPlan plan; if (!plans.TryGetValue(component.ComponentId,out plan)) throw new InvalidOperationException("还有多墙组件未确认顺序，请选择组件并确认。");
                CivilWallJoinPlans.ValidateFor(plan,component.Binding,component.Participants.Select(wall => wall.ElementUniqueId));
            }
            calculating = true; cancelled = false; Invalidate("正在按已确认顺序核算…"); progressClock.Restart(); lastPump = -50;
            try
            {
                string key = Key(); long before = revision(); var control = Control(); var calculations = new List<CivilWallJoinCalculation>();
                foreach (RevitWallJoinPreparedComponent component in bundle.Components)
                {
                    control.Checkpoint("核算交接组件",calculations.Count,bundle.Components.Count);
                    CivilWallJoinPlan plan = null; CivilWallJoinComponentResult result;
                    if (!Ready(component)) result = new CivilWallJoinComponentResult { ContextKey = component.ComponentId, Status = "unsupported", Message = component.Message };
                    else
                    {
                        plan = CivilWallJoinPlans.Clone(plans[component.ComponentId]); var ranks = plan.Priorities.ToDictionary(entry => entry.ElementUniqueId,entry => entry.Rank,StringComparer.Ordinal);
                        foreach (CivilWallJoinNode node in component.Input.Nodes) node.PriorityRank = ranks[node.UniqueId];
                        result = CivilWallJoinCore.Calculate(component.Input,bundle.Operations,stage => control.Checkpoint(stage,calculations.Count,bundle.Components.Count));
                        if (result.Status == "cancelled") throw new OperationCanceledException();
                        if (result.Status == "failed") throw new InvalidOperationException(result.Message);
                    }
                    calculations.Add(new CivilWallJoinCalculation { ComponentId = component.ComponentId, Plan = plan, Result = result });
                }
                CivilWallJoinBatch candidate = CivilWallJoinSession.Create(snapshot,calculations); SummaryFor(candidate); control.Checkpoint("提交交接结果前核对",calculations.Count,calculations.Count);
                if (before != revision() || key != Key() || !ReadFresh()) throw new InvalidOperationException("核算期间来源、事实或条件变化，未提交结果。");
                CivilWallJoinSession.Validate(candidate); LastBatch = candidate; stale = false; PublishComponents();
                int passed = calculations.Count(item => item.Result.Status == "calculated"); status.Text = "核算完成：" + passed + " 个组件通过，" + (calculations.Count-passed) + " 个组件未支持。未支持项为空，不计入合计。";
            }
            catch (OperationCanceledException) { status.Text = "核算已取消，未提交部分结果；旧结果过期。"; }
            catch (Exception error) { if (error is OutOfMemoryException) throw; status.Text = "核算未完成：" + error.Message; }
            finally { calculating = false; FinishOperation(); }
        }
        internal void RequestCancel() { if (reading || calculating) { cancelled = true; status.Text = "已请求取消，当前单次原生操作结束后停止，不提交部分结果。"; } }
        private CivilWallReadControl Control() { return new CivilWallReadControl { IsCancellationRequested = () => cancelled, Progress = Progress }; }
        private void Progress(string stage,int done,int total)
        {
            status.Text = stage + (total > 0 ? " · " + done + "/" + total : "");
            if (!IsVisible || pumping || progressClock.ElapsedMilliseconds-lastPump < 50) return;
            lastPump = progressClock.ElapsedMilliseconds; pumping = true;
            try { var frame = new DispatcherFrame(); Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate { frame.Continue = false; })); Dispatcher.PushFrame(frame); }
            finally { pumping = false; }
        }
        private bool SourceFresh()
        {
            if (reading || calculating) return false;
            try { if (bundle != null && (readKey != Key() || revision() != bundle.NativeSnapshot.Revision)) Invalidate("来源修订或条件已变化，请重新读取组件。");
                return bundle != null && readKey == Key() && revision() == bundle.NativeSnapshot.Revision; }
            catch(Exception error) { if(error is OutOfMemoryException)throw;Invalidate("来源条件不完整，请重新读取组件。");return false; }
        }
        private bool ReadFresh()
        {
            // During an operation the caller still needs the same synchronous
            // source/fact test; SourceFresh intentionally skips timer work.
            try { return bundle != null && snapshot != null && !bundle.IsDisposed && readKey == Key() && revision() == bundle.NativeSnapshot.Revision && CivilWallBatchIntegrity.FactsFingerprint(snapshot) == readFingerprint; }
            catch(Exception error) { if(error is OutOfMemoryException)throw;return false; }
        }
        internal bool CheckFreshness()
        {
            if (reading || calculating || !ReadFresh() || LastBatch == null || stale) { if (!reading && !calculating) Invalidate("旧结果不可导出或定位，请重新读取／核算。"); return false; }
            try {
                CivilWallJoinSession.Validate(LastBatch);
                foreach (CivilWallJoinCalculation calculation in LastBatch.Calculations.Where(item=>item.Plan!=null))
                {
                    CivilWallJoinPlan current; if(!plans.TryGetValue(calculation.ComponentId,out current))throw new InvalidDataException("当前已确认顺序缺失，请重新核算。");
                    CivilWallJoinComponentFacts facts = LastBatch.Input.Components.Single(item=>item.ComponentId==calculation.ComponentId);
                    CivilWallJoinPlans.ValidateFor(current,facts.Binding,facts.Participants.Select(item=>item.ElementUniqueId));
                    if(CivilWallJoinPlans.Fingerprint(current)!=CivilWallJoinPlans.Fingerprint(calculation.Plan))throw new InvalidDataException("当前交接方案已变化，旧结果不可导出或定位。");
                }
                return true;
            }
            catch (Exception error) { if (error is OutOfMemoryException) throw; Invalidate(error.Message); return false; }
        }
        internal string BuildCurrentCsv() { if (!CheckFreshness()) throw new InvalidOperationException("旧交接结果不可导出。"); return CivilWallJoinSession.ToCsv(LastBatch,dependencies.IsChecked == true); }
        internal bool PrepareFocus()
        {
            SelectedFocusElements = new List<CivilElementRecord>(); if (!CheckFreshness()) return false;
            WallRow selected = walls.SelectedItem as WallRow; if (selected == null) return false;
            SelectedFocusElements.Add(new CivilElementRecord { UniqueId = selected.Participant.ElementUniqueId, ElementId = selected.Participant.ElementId }); return true;
        }
        private void SaveCsv()
        {
            if (!CheckFreshness()) return; var dialog = new SaveFileDialog { Title = "导出交接体积与台账", Filter = "CSV (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true, FileName = "BV-墙体交接台账.csv" };
            if (dialog.ShowDialog(this) != true) return;
            try { string text = BuildCurrentCsv(); string path = Path.GetFullPath(dialog.FileName), temp = Path.Combine(Path.GetDirectoryName(path),".bv-join-"+Guid.NewGuid().ToString("N")+".tmp");
                try { using(var stream = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) using(var writer = new StreamWriter(stream,new UTF8Encoding(true))) writer.Write(text); if (File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path); }
                finally { if (File.Exists(temp)) File.Delete(temp); } status.Text = "交接报表已保存：" + path;
            } catch (Exception error) { if (error is OutOfMemoryException) throw; status.Text = "未保存：" + error.Message; }
        }
        private void EditPlan()
        {
            try { var component = SelectedComponent(); CivilWallJoinPlan current; plans.TryGetValue(component.ComponentId,out current); var dialog = new CivilWallJoinPlanWindow(component.Binding,component.Participants,current) { Owner = this }; if (dialog.ShowDialog() == true) SetPlan(component.ComponentId,dialog.Result); }
            catch (Exception error) { if (error is OutOfMemoryException) throw; status.Text = error.Message; }
        }
        private void OpenPlan()
        {
            try { var component = SelectedComponent(); var dialog = new OpenFileDialog { Title = "打开该组件的交接方案", Filter = "交接方案 (*.bvwalljoins.json)|*.bvwalljoins.json|JSON (*.json)|*.json", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) {
                    CivilWallJoinPlan saved = CivilWallJoinPlans.Load(dialog.FileName);
                    CivilWallJoinPlan adopted = CivilWallJoinPlans.AdoptForUnchangedGeometry(saved,component.Binding,component.Participants.Select(wall=>wall.ElementUniqueId));
                    SetPlan(component.ComponentId,adopted);
                    if(saved.Binding.SourceRevision!=adopted.Binding.SourceRevision)status.Text="几何依赖未变，已沿用保存的顺序至 r"+adopted.Revision+"；请重新核算，未复用旧数量。";
                } }
            catch (Exception error) { if (error is OutOfMemoryException) throw; status.Text = "方案未载入：" + error.Message; }
        }
        private void SavePlanDialog()
        {
            try { var component = SelectedComponent(); CivilWallJoinPlan plan; if (!plans.TryGetValue(component.ComponentId,out plan)) throw new InvalidOperationException("请先确认该组件的顺序。"); var dialog = new SaveFileDialog { Title = "保存来源绑定的交接方案", Filter = "交接方案 (*.bvwalljoins.json)|*.bvwalljoins.json", DefaultExt = ".bvwalljoins.json", AddExtension = true, FileName = "BV-交接方案.bvwalljoins.json" }; if (dialog.ShowDialog(this) == true) CivilWallJoinPlans.Save(dialog.FileName,plan); }
            catch (Exception error) { if (error is OutOfMemoryException) throw; status.Text = "方案未保存：" + error.Message; }
        }
        private RevitWallJoinPreparedComponent SelectedComponent()
        {
            EnsureIdle(); if (!ReadFresh()) throw new InvalidOperationException("组件来源已变化，请重新读取。"); var row = components.SelectedItem as ComponentRow; if (row == null) throw new InvalidOperationException("请选择一个组件。"); return bundle.Components.Single(item => item.ComponentId == row.FullId);
        }
        private void PublishComponents()
        {
            string previous = components.SelectedItem is ComponentRow ? ((ComponentRow)components.SelectedItem).FullId : null;
            var rows = bundle.Components.Select(item => new ComponentRow(item,plans.ContainsKey(item.ComponentId)?plans[item.ComponentId]:null,LastBatch==null?null:LastBatch.Calculations.SingleOrDefault(value=>value.ComponentId==item.ComponentId))).ToList(); components.ItemsSource = rows;
            components.SelectedItem = rows.FirstOrDefault(row => row.FullId == previous) ?? rows.FirstOrDefault(); ShowComponent();
            if (LastBatch != null && !stale)
                summary.Text = SummaryFor(LastBatch);
            else summary.Text = "已读取 " + rows.Count + " 个组件；请核查范围外依赖与未支持原因。";
            UpdateButtons();
        }
        private static string SummaryFor(CivilWallJoinBatch batch)
        {
            double total = 0, requested = 0; int passed = 0;
            foreach(var component in batch.Calculations.Where(item => item.Result.Status == "calculated"))
            {
                passed++; foreach(var row in component.Result.Rows) { total += row.ResultVolumeMetres3; if(row.Requested)requested += row.ResultVolumeMetres3; }
            }
            if(Double.IsInfinity(total)||Double.IsNaN(total)||Double.IsInfinity(requested)||Double.IsNaN(requested))throw new InvalidDataException("交接汇总溢出，未提交不完整结果。");
            if(passed==0)return "没有通过组件，合计 —；未支持量为空，不当作零。";
            return "完整组件分配合计 " + total.ToString("0.######",CultureInfo.CurrentCulture) + " m³ · 其中请求墙 " + requested.ToString("0.######",CultureInfo.CurrentCulture) + " m³ · 仅通过组件，范围外墙不重复合计";
        }
        private void ShowComponent()
        {
            var selected = components.SelectedItem as ComponentRow; if (bundle == null || selected == null) { walls.ItemsSource = null; steps.ItemsSource = null; return; }
            var component = bundle.Components.Single(item => item.ComponentId == selected.FullId); CivilWallJoinCalculation calculated = LastBatch == null ? null : LastBatch.Calculations.SingleOrDefault(item => item.ComponentId == component.ComponentId);
            CivilWallJoinPlan plan; plans.TryGetValue(component.ComponentId,out plan);
            walls.ItemsSource = component.Participants.Where(item => dependencies.IsChecked == true || item.Requested).Select(item => new WallRow(item,plan,calculated==null?null:calculated.Result.Rows.SingleOrDefault(row=>row.UniqueId==item.ElementUniqueId))).ToList();
            explanation.Text = component.Message + " 依赖指纹：" + component.Binding.GeometryDependencyFingerprint + (stale ? " · 旧结果过期／尚未核算" : ""); ShowSteps(); UpdateButtons();
        }
        private void ShowSteps() { var row = walls.SelectedItem as WallRow; steps.ItemsSource = row == null || row.ResultRow == null ? new List<CivilWallJoinStep>() : row.ResultRow.Steps; UpdateButtons(); }
        private static CivilWallJoinSnapshot Capture(RevitWallJoinReadBundle data)
        {
            return new CivilWallJoinSnapshot { NativeSnapshot = data.NativeSnapshot, GeometryViewUniqueId = data.GeometryViewUniqueId, GeometryViewName = data.GeometryViewName, OpeningRule = CivilWallRuleTemplates.Clone(data.RuleTemplate), Notices = data.Notices == null ? new List<string>() : data.Notices.ToList(),
                Components = data.Components.Select(component => new CivilWallJoinComponentFacts { ComponentId = component.ComponentId, Binding = CivilWallJoinPlans.CloneBinding(component.Binding), Status = component.Status, Message = component.Message, Participants = component.Participants, Evidence = component.Evidence }).ToList() };
        }
        private static bool Ready(RevitWallJoinPreparedComponent component) { return component != null && component.Status == "ready" && component.Input != null && component.Input.Nodes != null && component.Input.DependenciesComplete && component.Input.Nodes.Count > 0 && component.Input.Nodes.All(node => node != null && node.Supported); }
        private void Invalidate(string message) { stale = true; SelectedFocusElements = new List<CivilElementRecord>(); status.Text = message; UpdateButtons(); }
        private void EnsureIdle() { if (reading || calculating) throw new InvalidOperationException("正在读取或核算，请先取消或等待完成。"); }
        private void FinishOperation() { UpdateButtons(); if (closeWhenDone) { closeWhenDone = false; if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(Close)); } }
        private void UpdateButtons()
        {
            bool idle = !reading && !calculating; readButton.IsEnabled = idle; cancelButton.IsEnabled = !idle; calculateButton.IsEnabled = idle && bundle != null;
            bool selected = components.SelectedItem != null; editButton.IsEnabled = loadButton.IsEnabled = savePlanButton.IsEnabled = idle && selected; csvButton.IsEnabled = idle && !stale && LastBatch != null; focusButton.IsEnabled = csvButton.IsEnabled && walls.SelectedItem != null;
            foreach(Control control in new Control[] { scope,phase,option,view,rules,components,walls,dependencies }) control.IsEnabled = idle;
        }
        private CivilReadRequest CurrentRequest() { if (scope.SelectedItem == null || phase.SelectedItem == null || option.SelectedItem == null || view.SelectedItem == null) throw new InvalidOperationException("请明确范围、阶段、选项与几何视图。"); return new CivilReadRequest { Scope = Id(scope),PhaseId = Id(phase),DesignOptionId = Id(option) }; }
        private CivilWallRuleTemplate SelectedRule() { var rule = rules.SelectedItem as CivilWallRuleTemplate; if(rule==null)throw new InvalidOperationException("请选择明确的切洞规则。"); return CivilWallRuleTemplates.Clone(rule); }
        private string Key() { var request = CurrentRequest(); return request.Scope+"\n"+request.PhaseId+"\n"+request.DesignOptionId+"\n"+Id(view)+"\n"+CivilWallRuleTemplates.Fingerprint(SelectedRule()); }
        private static string Id(ComboBox box) { return box.SelectedItem is CivilChoice ? ((CivilChoice)box.SelectedItem).Id : String.Empty; }
        private static CivilChoice Choice(string id,string name) { return new CivilChoice { Id = id, Label = name }; }
        private static void Choices(ComboBox box,IEnumerable<CivilChoice> items,string selected) { var values=items.ToList();box.ItemsSource=values;box.DisplayMemberPath="Label";box.SelectedItem=values.FirstOrDefault(value=>value.Id==selected); }
        private static FrameworkElement Field(string name,Control control,double width) { var panel=new StackPanel { Width=width,Margin=new Thickness(0,0,10,0) };panel.Children.Add(new TextBlock { Text=name,Margin=new Thickness(0,0,0,5) });control.MinHeight=29;panel.Children.Add(control);return panel; }
        private void Add(Panel panel,Button button,string label,Action action) { button.Content=label;button.Padding=new Thickness(11,6,11,6);button.Margin=new Thickness(0,0,7,0);button.Click+=delegate { try { action(); } catch(Exception error) { if(error is OutOfMemoryException)throw; status.Text = "本轮未完成：" + error.Message; } };panel.Children.Add(button); }
        private static void Configure(DataGrid grid) { grid.AutoGenerateColumns=false;grid.IsReadOnly=true;grid.CanUserAddRows=false;grid.CanUserDeleteRows=false;grid.SelectionMode=DataGridSelectionMode.Single;grid.HeadersVisibility=DataGridHeadersVisibility.Column;grid.MinRowHeight=28;grid.RowHeaderWidth=0; }
        private static void Column(DataGrid grid,string title,string path,double width) { grid.Columns.Add(new DataGridTextColumn { Header=title,Binding=new Binding(path) { TargetNullValue="—" },Width=new DataGridLength(width) }); }
        private static string Number(double? value) { return value.HasValue?value.Value.ToString("0.######",CultureInfo.CurrentCulture):"—"; }
        private sealed class ComponentRow
        {
            internal string FullId; public string Id {get{return FullId.Substring(0,Math.Min(18,FullId.Length));}} public int Count {get;private set;} public int Requested {get;private set;} public string Plan {get;private set;} public string Status {get;private set;} public string Message {get;private set;} public string Volume {get;private set;}
            internal ComponentRow(RevitWallJoinPreparedComponent component,CivilWallJoinPlan plan,CivilWallJoinCalculation result) { FullId=component.ComponentId;Count=component.Participants.Count;Requested=component.Participants.Count(wall=>wall.Requested);Plan=plan==null?"尚未确认顺序":plan.Name+" · r"+plan.Revision;Status=result==null?component.Status:result.Result.Status;Message=result==null?component.Message:result.Result.Message;Volume=result==null?"—":Number(result.Result.SumResultVolumeMetres3); }
        }
        private sealed class WallRow
        {
            internal CivilWallJoinParticipant Participant; internal CivilWallJoinRowResult ResultRow;
            public string Name {get{return Participant.Name;}} public string ElementId {get{return Participant.ElementId;}} public string Role {get{return Participant.Requested?"请求墙":"范围外依赖";}} public int? Rank {get;private set;}
            public string Gross {get{return Number(ResultRow==null?Participant.GrossVolumeMetres3:ResultRow.GrossVolumeMetres3);}} public string Opening {get{return Number(ResultRow==null?Participant.OpeningVolumeMetres3:ResultRow.OpeningDeductionVolumeMetres3);}} public string Expected {get{return Number(ResultRow==null?(double?)null:ResultRow.ExpectedVolumeMetres3);}} public string Join {get{return Number(ResultRow==null?(double?)null:ResultRow.JoinDeductionVolumeMetres3);}} public string Result {get{return Number(ResultRow==null?(double?)null:ResultRow.ResultVolumeMetres3);}} public string Actual {get{return Number(ResultRow==null?(double?)null:ResultRow.ActualVolumeMetres3);}} public string Native {get{return Number(Participant.NativeVolumeMetres3);}}
            internal WallRow(CivilWallJoinParticipant participant,CivilWallJoinPlan plan,CivilWallJoinRowResult result) { Participant=participant;ResultRow=result;if(plan!=null){var rank=plan.Priorities.SingleOrDefault(item=>item.ElementUniqueId==participant.ElementUniqueId);Rank=rank==null?(int?)null:rank.Rank;} }
        }
    }
}
