// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BrightVision.Civil;

namespace BrightVision.Revit
{
    // The displayed list is not a business default. A result exists only after
    // explicit confirmation and Apply; borrowed context/participants are cloned.
    internal sealed class CivilWallJoinPlanWindow : Window
    {
        private readonly CivilWallJoinBinding binding;
        private readonly CivilWallJoinPlan initial;
        private readonly CivilWallJoinPlan draft;
        private readonly string initialFingerprint;
        private readonly List<ParticipantRow> rows;
        private readonly DataGrid participants = new DataGrid();
        private readonly TextBox planName = new TextBox();
        private readonly TextBlock message = new TextBlock(), revisionLabel = new TextBlock();
        private readonly CheckBox confirmOrder = new CheckBox();
        private readonly Button upButton = new Button(), downButton = new Button(), applyButton = new Button(), cancelButton = new Button();
        private bool modalActive;

        internal CivilWallJoinPlan Result { get; private set; }

        internal CivilWallJoinPlanWindow(CivilWallJoinBinding binding, IList<CivilWallJoinParticipant> componentParticipants, CivilWallJoinPlan initial = null)
        {
            this.binding = CivilWallJoinPlans.CloneBinding(binding);
            if (componentParticipants == null || componentParticipants.Count < 1 || componentParticipants.Count > CivilWallJoinPlans.MaximumParticipants)
                throw new InvalidDataException("编辑顺序需要完整组件的 1 至 64 面参与墙。");
            var uidSet = new HashSet<string>(StringComparer.Ordinal); var idSet = new HashSet<string>(StringComparer.Ordinal);
            rows = new List<ParticipantRow>();
            foreach (CivilWallJoinParticipant source in componentParticipants)
            {
                if (source == null || !Identity(source.ElementUniqueId) || !Identity(source.ElementId) || !uidSet.Add(source.ElementUniqueId) || !idSet.Add(source.ElementId))
                    throw new InvalidDataException("参与墙来源身份缺失或重复。");
                CheckMetric(source.GrossVolumeMetres3); CheckMetric(source.OpeningVolumeMetres3); CheckMetric(source.NativeVolumeMetres3);
                rows.Add(new ParticipantRow(new CivilWallJoinParticipant { ElementUniqueId = source.ElementUniqueId, ElementId = source.ElementId, Name = source.Name ?? String.Empty,
                    Requested = source.Requested, GrossVolumeMetres3 = source.GrossVolumeMetres3, OpeningVolumeMetres3 = source.OpeningVolumeMetres3, NativeVolumeMetres3 = source.NativeVolumeMetres3 }));
            }
            if (initial != null)
            {
                CivilWallJoinPlans.ValidateFor(initial, this.binding, uidSet); this.initial = CivilWallJoinPlans.Clone(initial);
                initialFingerprint = CivilWallJoinPlans.Fingerprint(this.initial);
                var ranks = this.initial.Priorities.ToDictionary(entry => entry.ElementUniqueId, entry => entry.Rank, StringComparer.Ordinal);
                rows = rows.OrderBy(row => ranks[row.UniqueId]).ToList();
            }
            draft = this.initial == null ? CivilWallJoinPlans.Create(this.binding, rows.Select(row => row.UniqueId), "墙体组件交接顺序") : CivilWallJoinPlans.Clone(this.initial);
            Title = "BV · 墙体交接优先顺序 · 崛亮视觉"; Icon = Branding.Icon(32);
            Width = 1180; Height = 720; MinWidth = 920; MinHeight = 580; MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24);
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Background = Brushes.White; FontSize = 12; FontFamily = new FontFamily("Microsoft YaHei UI");
            var root = new Grid { Margin = new Thickness(20,16,20,16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = root;
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = "明确交接体积的归属顺序", FontSize = 22, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock { Text = "崛亮视觉 · Bright Vision  |  " + CivilWallJoinPlans.MethodId, Foreground = Brushes.DimGray, Margin = new Thickness(0,4,0,8) });
            header.Children.Add(new TextBlock { Text = "优先级 1 保留共享交叠体积，后续墙仅扣除已分配部分。范围外依赖墙也必须参与排序；此处编辑顺序，实际扣量由完整组件计算和守恒校验决定。", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(130,88,25)), Margin = new Thickness(0,0,0,8) });
            header.Children.Add(new TextBlock { Text = "工程 " + this.binding.DocumentScope + " · 阶段 " + this.binding.PhaseId + " · 校验视图 " + this.binding.GeometryViewUniqueId + " · 模型修订 " + this.binding.SourceRevision.ToString(CultureInfo.InvariantCulture), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
            header.Children.Add(new TextBlock { Text = "依赖几何指纹 " + this.binding.GeometryDependencyFingerprint, FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, ToolTip = this.binding.SourcePath });
            var naming = new DockPanel { Margin = new Thickness(0,10,0,10) };
            naming.Children.Add(new TextBlock { Text = "方案名称", Width = 70, VerticalAlignment = VerticalAlignment.Center });
            revisionLabel.Margin = new Thickness(10,0,0,0); revisionLabel.VerticalAlignment = VerticalAlignment.Center; revisionLabel.Foreground = Brushes.DimGray; DockPanel.SetDock(revisionLabel,Dock.Right); naming.Children.Add(revisionLabel);
            planName.Text = draft.Name; planName.Padding = new Thickness(6,5,6,5); naming.Children.Add(planName); header.Children.Add(naming); root.Children.Add(header);
            var center = new Grid(); center.ColumnDefinitions.Add(new ColumnDefinition()); center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) }); Grid.SetRow(center,1); root.Children.Add(center);
            participants.AutoGenerateColumns = false; participants.IsReadOnly = true; participants.CanUserAddRows = false; participants.CanUserDeleteRows = false;
            participants.CanUserSortColumns = false; participants.CanUserReorderColumns = false; participants.SelectionMode = DataGridSelectionMode.Single; participants.HeadersVisibility = DataGridHeadersVisibility.Column;
            participants.RowHeaderWidth = 0; participants.MinRowHeight = 30; participants.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(247,250,252));
            Column("优先级","Rank",65); Column("范围","Scope",115); Column("墙名称","Name",190); Column("ElementId","ElementId",90); Column("UniqueId","UniqueId",270);
            Column("独立基准 m³","Gross",110); Column("已知扣洞 m³","Opening",110); Column("Revit 参照 m³","Native",115);
            center.Children.Add(participants);
            var moves = new StackPanel { Margin = new Thickness(12,0,0,0), VerticalAlignment = VerticalAlignment.Top };
            upButton.Content = "上移 ↑"; upButton.Padding = new Thickness(10,8,10,8); upButton.Margin = new Thickness(0,0,0,8); upButton.Click += delegate { MoveSelected(-1); }; moves.Children.Add(upButton);
            downButton.Content = "下移 ↓"; downButton.Padding = new Thickness(10,8,10,8); downButton.Click += delegate { MoveSelected(1); }; moves.Children.Add(downButton);
            moves.Children.Add(new TextBlock { Text = "数字越小，\n优先保留\n交叠体积。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0,12,0,0) });
            Grid.SetColumn(moves,1); center.Children.Add(moves);
            var confirmation = new StackPanel { Margin = new Thickness(0,12,0,8) };
            confirmOrder.Content = new TextBlock { Text = "我已核对完整组件（包括范围外依赖墙）的优先顺序，确认优先级 1 保留交叠体积。", TextWrapping = TextWrapping.Wrap };
            confirmOrder.VerticalContentAlignment = VerticalAlignment.Center; confirmOrder.MinHeight = 32; confirmation.Children.Add(confirmOrder);
            message.Text = "当前展示顺序尚未确认，未生成可用于计算的方案。"; message.TextWrapping = TextWrapping.Wrap; message.Foreground = Brushes.Firebrick; message.MinHeight = 20; message.Margin = new Thickness(0,6,0,0); confirmation.Children.Add(message);
            Grid.SetRow(confirmation,2); root.Children.Add(confirmation);
            var footer = new DockPanel(); footer.Children.Add(new TextBlock { Text = "只保存来源绑定和顺序，不写回 RVT。", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            cancelButton.Content = "取消"; cancelButton.IsCancel = true; cancelButton.Padding = new Thickness(18,7,18,7); cancelButton.Margin = new Thickness(10,0,10,0);
            cancelButton.Click += delegate { Result = null; if (IsVisible) Close(); };
            applyButton.Content = "确认并应用顺序"; applyButton.IsDefault = true; applyButton.Padding = new Thickness(18,7,18,7);
            applyButton.Click += delegate {
                try { Result = BuildPlan(); message.Text = String.Empty; if (modalActive) DialogResult = true; else if (IsVisible) Close(); }
                catch (Exception error) { if (error is OutOfMemoryException) throw; Result = null; message.Text = error.Message; }
            };
            buttons.Children.Add(cancelButton); buttons.Children.Add(applyButton); DockPanel.SetDock(buttons,Dock.Right); footer.Children.Add(buttons); Grid.SetRow(footer,3); root.Children.Add(footer);
            participants.SelectionChanged += delegate { UpdateControls(); };
            confirmOrder.Checked += delegate { message.Text = String.Empty; UpdateControls(); }; confirmOrder.Unchecked += delegate { UpdateControls(); };
            planName.TextChanged += delegate { ResetConfirmation("方案内容已变化，请重新核对并确认。"); };
            PublishRows(0); UpdateControls();
        }

        public new bool? ShowDialog() { modalActive = true; try { return base.ShowDialog(); } finally { modalActive = false; } }
        internal CivilWallJoinPlan BuildPlan()
        {
            if (confirmOrder.IsChecked != true) throw new InvalidDataException("请先明确确认完整组件的优先顺序。");
            var candidate = CivilWallJoinPlans.Clone(draft); candidate.Name = planName.Text;
            candidate.Priorities = rows.Select((row,index)=>new CivilWallJoinPriority { ElementUniqueId = row.UniqueId, Rank = index+1 }).ToList();
            CivilWallJoinPlans.ValidateFor(candidate,binding,rows.Select(row=>row.UniqueId));
            if (initial != null && CivilWallJoinPlans.Fingerprint(candidate) != initialFingerprint)
            {
                if (initial.Revision == Int32.MaxValue) throw new InvalidDataException("方案修订已达上限，请另存为新的交接方案。");
                candidate.Revision = initial.Revision + 1;
            }
            CivilWallJoinPlans.Validate(candidate); return candidate;
        }
        internal void MoveSelected(int direction)
        {
            if (direction != -1 && direction != 1) throw new ArgumentOutOfRangeException("direction");
            int index = participants.SelectedIndex, target = index + direction;
            if (index < 0 || target < 0 || target >= rows.Count) return;
            ParticipantRow current = rows[index]; rows.RemoveAt(index); rows.Insert(target,current);
            ResetConfirmation("顺序已变化，请重新核对并确认。"); PublishRows(target);
        }
        private void PublishRows(int selected)
        { for (int index=0;index<rows.Count;index++) rows[index].Rank=index+1; participants.ItemsSource=null;participants.ItemsSource=rows;participants.SelectedIndex=selected;UpdateControls(); }
        private void ResetConfirmation(string text) { confirmOrder.IsChecked=false;Result=null;message.Text=text;UpdateControls(); }
        private void UpdateControls()
        {
            upButton.IsEnabled=participants.SelectedIndex>0;downButton.IsEnabled=participants.SelectedIndex>=0&&participants.SelectedIndex<rows.Count-1;
            applyButton.IsEnabled=confirmOrder.IsChecked==true;
            revisionLabel.Text=initial==null?"新方案 · 修订 1":"当前修订 "+initial.Revision.ToString(CultureInfo.InvariantCulture)+"；更改后升版";
        }
        private void Column(string header,string property,double width) { participants.Columns.Add(new DataGridTextColumn { Header=header,Binding=new Binding(property),Width=width }); }
        private static bool Identity(string value) { return !String.IsNullOrWhiteSpace(value)&&value.Length<=1024&&!value.Any(Char.IsControl); }
        private static void CheckMetric(double? value) { if(value.HasValue&&(Double.IsNaN(value.Value)||Double.IsInfinity(value.Value)||value.Value<0))throw new InvalidDataException("参与墙的显示参照量须为有限非负数或明确空值。"); }
        private sealed class ParticipantRow
        {
            private readonly CivilWallJoinParticipant source;
            internal ParticipantRow(CivilWallJoinParticipant source) { this.source=source; }
            public int Rank { get; internal set; }
            public string UniqueId { get { return source.ElementUniqueId; } }
            public string ElementId { get { return source.ElementId; } }
            public string Name { get { return String.IsNullOrWhiteSpace(source.Name)?"未命名墙":source.Name; } }
            public string Scope { get { return source.Requested?"计量范围内":"依赖墙（范围外）"; } }
            public string Gross { get { return Number(source.GrossVolumeMetres3); } }
            public string Opening { get { return Number(source.OpeningVolumeMetres3); } }
            public string Native { get { return Number(source.NativeVolumeMetres3); } }
            private static string Number(double? value) { return value.HasValue?value.Value.ToString("0.########",CultureInfo.CurrentCulture):"—"; }
        }
    }
}
