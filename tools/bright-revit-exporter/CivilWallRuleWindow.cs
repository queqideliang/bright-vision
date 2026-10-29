// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrightVision.Civil;

namespace BrightVision.Revit
{
    // Owns a detached draft; neither cancellation nor validation touches the
    // caller's template. File selection/persistence belongs to the workbench.
    internal sealed class CivilWallRuleWindow : Window
    {
        private readonly CivilWallRuleTemplate initial;
        private readonly CivilWallRuleTemplate draft;
        private readonly string originalFingerprint;
        private readonly bool copiedBuiltin;
        private readonly TextBox templateName = new TextBox();
        private readonly TextBox description = new TextBox();
        private readonly TextBox templateId = new TextBox();
        private readonly TextBox revision = new TextBox();
        private readonly TextBox volumeMethod = new TextBox();
        private readonly TextBox areaMethod = new TextBox();
        private readonly ComboBox openingPolicy = new ComboBox();
        private readonly TextBox joinPolicy = new TextBox();
        private readonly TextBox absoluteTolerance = new TextBox();
        private readonly TextBox relativeTolerance = new TextBox();
        private readonly TextBlock message = new TextBlock();
        private readonly Button applyButton = new Button();
        private readonly Button cancelButton = new Button();
        private bool dialogActive;

        internal CivilWallRuleTemplate Result { get; private set; }

        internal CivilWallRuleWindow(CivilWallRuleTemplate initial)
        {
            this.initial = CivilWallRuleTemplates.Clone(initial ?? CivilWallRuleTemplates.Default());
            originalFingerprint = CivilWallRuleTemplates.Fingerprint(this.initial);
            copiedBuiltin = CivilWallRuleTemplates.IsBuiltIn(this.initial);
            draft = copiedBuiltin ? CivilWallRuleTemplates.Copy(this.initial, this.initial.Name) : CivilWallRuleTemplates.Clone(this.initial);

            Title = "BV · 墙体规则模板";
            Icon = Branding.Icon(32);
            Width = 800; Height = 650; MinWidth = 720; MinHeight = 560;
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24);
            ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12; Background = Brushes.White;

            Grid root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            StackPanel heading = new StackPanel();
            DockPanel title = new DockPanel();
            title.Children.Add(new Image { Source = Branding.Icon(32), Width = 36, Height = 36, Margin = new Thickness(0, 0, 10, 0) });
            StackPanel brand = new StackPanel();
            brand.Children.Add(new TextBlock { Text = "墙体规则模板", FontSize = 20, FontWeight = FontWeights.SemiBold });
            brand.Children.Add(new TextBlock { Text = "崛亮视觉 · Bright Vision", FontSize = 11, Foreground = Brushes.DimGray });
            title.Children.Add(brand); heading.Children.Add(title);
            heading.Children.Add(new TextBlock {
                Text = "项目约定默认模板，并非地区清单、定额或模板抹灰规则。",
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 10)
            });
            root.Children.Add(heading);

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            Grid form = new Grid(); form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) }); form.ColumnDefinitions.Add(new ColumnDefinition()); scroll.Content = form;

            Configure(templateName, false); templateName.Text = draft.Name;
            AddRow(form, "模板名称", templateName, 32);
            Configure(description, false); description.Text = draft.Description; description.AcceptsReturn = true; description.TextWrapping = TextWrapping.Wrap; description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            AddRow(form, "模板说明", description, 58);
            Configure(templateId, true); templateId.Text = draft.TemplateId; templateId.ToolTip = "模板唯一标识；内置方案编辑后使用新的自有模板标识。";
            Configure(revision, true); revision.Text = draft.Revision.ToString(CultureInfo.InvariantCulture);
            Grid identity = new Grid(); identity.ColumnDefinitions.Add(new ColumnDefinition()); identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            identity.Children.Add(templateId);
            StackPanel version = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
            version.Children.Add(new TextBlock { Text = "修订 ", VerticalAlignment = VerticalAlignment.Center }); revision.Width = 65; version.Children.Add(revision); Grid.SetColumn(version, 1); identity.Children.Add(version);
            AddRow(form, "标识 / 格式 v1", identity, 28);

            Configure(volumeMethod, true); volumeMethod.Text = draft.MethodId;
            Configure(areaMethod, true); areaMethod.Text = draft.AreaMethodId;
            StackPanel methods = new StackPanel(); methods.Children.Add(volumeMethod); methods.Children.Add(areaMethod);
            AddRow(form, "体积 / 投影面积方法", methods, 48);

            openingPolicy.Margin = new Thickness(0); openingPolicy.VerticalContentAlignment = VerticalAlignment.Center;
            openingPolicy.Items.Add(new ComboBoxItem { Content = "已验证的真实矩形门窗切洞 + 原生 Opening", Tag = CivilWallRuleTemplates.VerifiedRectCutout });
            openingPolicy.Items.Add(new ComboBoxItem { Content = "仅原生矩形 Opening", Tag = CivilWallRuleTemplates.NativeOpeningOnly });
            foreach (ComboBoxItem item in openingPolicy.Items) if ((string)item.Tag == draft.OpeningPolicy) openingPolicy.SelectedItem = item;
            AddRow(form, "洞口策略", openingPolicy, 32);

            Configure(joinPolicy, true); joinPolicy.Text = "拒绝重叠（reject-overlap）";
            joinPolicy.ToolTip = "发现未能解释的相交或重叠时，保留问题并拒绝给出最终数量；当前不提供交接优先级分配。";
            AddRow(form, "交接处理", joinPolicy, 28);

            Configure(absoluteTolerance, false); absoluteTolerance.Text = draft.AbsoluteToleranceMetres3.ToString("R", CultureInfo.InvariantCulture);
            Configure(relativeTolerance, false); relativeTolerance.Text = draft.RelativeTolerance.ToString("R", CultureInfo.InvariantCulture);
            absoluteTolerance.ToolTip = "绝对体积容差，单位 m³；正有限值，不大于 1e-8。";
            relativeTolerance.ToolTip = "相对体积容差，无量纲；正有限值，不大于 1e-8。";
            Grid tolerance = new Grid(); tolerance.ColumnDefinitions.Add(new ColumnDefinition()); tolerance.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) }); tolerance.ColumnDefinitions.Add(new ColumnDefinition());
            tolerance.Children.Add(absoluteTolerance);
            TextBlock relativeLabel = new TextBlock { Text = "相对", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }; Grid.SetColumn(relativeLabel, 1); tolerance.Children.Add(relativeLabel);
            Grid.SetColumn(relativeTolerance, 2); tolerance.Children.Add(relativeTolerance);
            AddRow(form, "绝对容差（m³）", tolerance, 32);

            TextBlock scope = new TextBlock {
                Text = "容差可用科学计数法（例如 1e-9），上限均为 1e-8。只支持已证明的直线矩形墙与矩形实际切洞；不以门窗标称尺寸代替切洞，也不分配重叠构件的扣量优先级。",
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 6)
            };
            AddRow(form, "适用范围", scope, Double.NaN);

            message.TextWrapping = TextWrapping.Wrap; message.Foreground = Brushes.Firebrick; message.MinHeight = 18; message.MaxHeight = 58; message.Margin = new Thickness(0, 7, 0, 5);
            Grid.SetRow(message, 2); root.Children.Add(message);
            DockPanel footer = new DockPanel();
            footer.Children.Add(new TextBlock {
                Text = copiedBuiltin ? "内置方案将复制为自有模板，修订从 1 开始。" : "更改后提高修订号；取消不改变原模板。",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 440
            });
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            cancelButton.Content = "取消"; cancelButton.IsCancel = true; cancelButton.Padding = new Thickness(20, 7, 20, 7); cancelButton.Margin = new Thickness(8, 0, 8, 0);
            cancelButton.Click += delegate { Result = null; if (IsVisible) Close(); };
            applyButton.Content = "应用模板"; applyButton.IsDefault = true; applyButton.Padding = new Thickness(20, 7, 20, 7);
            applyButton.Click += delegate {
                try
                {
                    CivilWallRuleTemplate candidate = BuildTemplate();
                    Result = candidate; message.Text = String.Empty;
                    if (dialogActive) DialogResult = true;
                    else if (IsVisible) Close();
                }
                catch (Exception error)
                {
                    if (error is OutOfMemoryException) throw;
                    Result = null; message.Text = error.Message;
                }
            };
            buttons.Children.Add(cancelButton); buttons.Children.Add(applyButton); DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
            Grid.SetRow(footer, 3); root.Children.Add(footer);

            templateName.TextChanged += delegate { PreviewRevision(); };
            description.TextChanged += delegate { PreviewRevision(); };
            absoluteTolerance.TextChanged += delegate { PreviewRevision(); };
            relativeTolerance.TextChanged += delegate { PreviewRevision(); };
            openingPolicy.SelectionChanged += delegate { PreviewRevision(); };
        }

        // This flag is set only for the modal call made on this dialog type.
        // Offscreen BuildTemplate/Click tests never set Window.DialogResult.
        public new bool? ShowDialog()
        {
            dialogActive = true;
            try { return base.ShowDialog(); }
            finally { dialogActive = false; }
        }

        internal CivilWallRuleTemplate BuildTemplate()
        {
            CivilWallRuleTemplate candidate = CivilWallRuleTemplates.Clone(draft);
            candidate.Name = templateName.Text.Trim(); candidate.Description = description.Text;
            candidate.MethodId = volumeMethod.Text; candidate.AreaMethodId = areaMethod.Text;
            ComboBoxItem selected = openingPolicy.SelectedItem as ComboBoxItem;
            if (selected == null || !(selected.Tag is string)) throw new InvalidDataException("请选择已实现的洞口策略。");
            candidate.OpeningPolicy = (string)selected.Tag;
            candidate.AbsoluteToleranceMetres3 = ParseTolerance(absoluteTolerance.Text, "绝对体积容差");
            candidate.RelativeTolerance = ParseTolerance(relativeTolerance.Text, "相对体积容差");
            CivilWallRuleTemplates.Validate(candidate);
            if (!copiedBuiltin && CivilWallRuleTemplates.Fingerprint(candidate) != originalFingerprint)
            {
                if (initial.Revision == Int32.MaxValue) throw new InvalidDataException("模板修订号已达上限，请复制为新模板后再编辑。");
                candidate.Revision = initial.Revision + 1;
            }
            CivilWallRuleTemplates.Validate(candidate);
            return candidate;
        }

        private void PreviewRevision()
        {
            try { revision.Text = BuildTemplate().Revision.ToString(CultureInfo.InvariantCulture); }
            catch (Exception error)
            {
                if (error is OutOfMemoryException) throw;
                revision.Text = "待校验";
            }
        }

        private static double ParseTolerance(string text, string label)
        {
            double value;
            if (!Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0)
                throw new InvalidDataException(label + "请输入正有限数值；支持 1e-9 形式，使用小数点。");
            return value;
        }

        private static void Configure(TextBox box, bool readOnly)
        {
            box.IsReadOnly = readOnly; box.Padding = new Thickness(6, 3, 6, 3); box.VerticalContentAlignment = VerticalAlignment.Center;
            if (readOnly) { box.Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)); box.Foreground = Brushes.DimGray; }
        }

        private static void AddRow(Grid form, string text, FrameworkElement control, double height)
        {
            int row = form.RowDefinitions.Count;
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 10, 4) };
            Grid.SetRow(label, row); form.Children.Add(label);
            if (!Double.IsNaN(height)) control.Height = height;
            control.Margin = new Thickness(0, 0, 0, 4); Grid.SetColumn(control, 1); Grid.SetRow(control, row); form.Children.Add(control);
        }
    }
}
