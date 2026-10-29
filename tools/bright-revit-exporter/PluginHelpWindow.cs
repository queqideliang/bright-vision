// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

namespace BrightVision.Revit
{
    // Self-contained help: no Revit API, network or installed guide-file dependency.
    internal static class PluginHelpWindow
    {
        internal static void Show() { Show(IntPtr.Zero); }

        internal static void Show(IntPtr ownerHandle)
        {
            Window window = CreateWindow();
            if (ownerHandle != IntPtr.Zero)
            {
                new WindowInteropHelper(window).Owner = ownerHandle;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            window.ShowDialog();
        }

        // Allows the production document and every section to be checked offscreen.
        internal static Window CreateWindow() { return new HelpDialog(); }

        // Same text as USER_GUIDE.md and test/插件功能入门.md.
        internal const string GuideMarkdown = @"# BV 插件功能介绍与入门指南

## 先选哪个功能

打开 Revit 工程后，进入 BV 页签 → bright vision 面板。按钮中的两行文字在下文合写为一个名称。第一次使用，建议先用少量已知构件核对，再扩大范围。

- 想把模型交给别人离线查看：选“本地导出 模型 ZIP”，生成独立模型包。

- 想核对 Revit 已有的墙、梁、柱、板参数量：选“土建原始量工作台”，查看明细、问题和 CSV。

- 想试算一面普通直墙的单侧投影面积、体积和洞口扣减：选“墙体试算与台账”，核查规则、实际切洞和逐条台账。

- 想按项目约定把相交墙的共享体积分配给指定墙：选“墙体交接 体积核算”，先读取完整组件，再确认优先顺序。

原始参数量、项目几何试算量、地区清单／定额量是不同口径。本插件尚不是完整土建算量产品；不能把模型包、统计合计或几何试算直接当作地区结算工程量。

## 导出模型 ZIP

入口：BV → bright vision → 本地导出 模型 ZIP。

1. 打开工程，点击入口，在“模型名称”填写名称，在“三维视图”选择想交付的视图。导出以所选三维视图的可见范围为准；请先检查隐藏构件、视图范围和链接加载情况。

2. 核对“导出内容”。几何信息和构件信息始终包含；可选贴图纹理、系统树、空间树、专业树、CAD 几何、轴网等。CAD 几何导出可见网格，不打包原始 DWG。

3. 如需附带二维 DWG，勾选“二维视图（DWG）”，再点“选择二维视图…”手动选择。当前查看器暂不显示这些 DWG，它们只是 ZIP 附件。

4. 在“导出文件夹”填写路径或点“浏览…”，检查“保存为”路径，再点“导出 ZIP”。完成后查看保存位置、构件与几何数、源 RVT 和 ZIP 体积，以及导出提示。缺失贴图、未加载链接等提示需要单独核对。

5. 将生成的 ZIP 交给 Bright Vision 查看器打开。ZIP 是离线模型包，不是可继续编辑的 RVT，也不等于已完成工程量规则计算。

## 原始量工作台

入口：BV → bright vision → 土建原始量工作台。

1. 在“读取范围”选“当前视图”“当前选择”或“当前工程（根文档）”。使用当前选择时，先在 Revit 选好构件再打开工作台。当前视图只限制纳入哪些构件，视图剖切不会把整构件原生量裁成可见部分。

2. 核对“阶段（现存 / 新建）”与“设计选项”，点击“刷新原始量”。改变读取条件或模型后，要重新刷新；过期结果不能继续导出或定位。

3. 在“原始量表”查看墙、梁、柱、板等支持对象的原生长度、面积和体积。用搜索、业务类别、原生参照楼层或“仅问题”缩小范围；“表格方式”可选构件明细、按类别、按原生楼层、按计量层或按施工段汇总。

4. 切换“模型检查”，查看排除原因、缺失参数和楼层等问题。NA、空白或读取失败不等于零；汇总时同时核对支持对象数、有效参数覆盖和问题数，不能只读合计。

5. 点击“导出当前量表 CSV…”保存当前筛选与分组。需要回模型核查时，选择明细或问题行，再点“定位选中构件并关闭”。

此处保留 Revit 原生参数口径，不重新执行地区扣减，不是清单／定额净量、模板量、抹灰量或建筑面积。原生参照楼层也不等于按几何切层后的数量。

## 计量工程设置

用途：把整构件归入计量层与施工段，组织报表；设置另存为 .bvproject.json，不写入 RVT，也不切分构件几何。

1. 先在原始量工作台刷新数据，再点“计量工程设置…”。在“计量层与映射”可点“按原生楼层补齐计量层和映射”快速起步，也可新增或修改计量层，选原生楼层和目标计量层后点“映射选中楼层”。参考标高用米，可留空；它不是裁切平面。

2. 分施工段前，先回量表选择构件，再进“计量工程设置…”→“施工段与构件归属”。新增施工段、选中该段，点“分配到左侧选中施工段”。选中汇总行会包含该行全部来源构件；每个构件仅归一个施工段。

3. 点“应用设置”返回，检查计量层、施工段及未映射／未分配项，再点工作台“保存工程…”。下次在同一来源模型点“打开工程…”加载。取消设置窗口会放弃本窗口草稿。

4. 墙体试算窗口也提供“计量工程设置…”“打开工程 JSON…”“保存工程 JSON…”，用于同一类工程归属。工程文件只恢复配置，不替代重新读取；墙体工程还保存规则信息，载入后须核对所选模板。

工程设置持久化要求已保存的本地或 UNC 路径 RVT，并绑定来源身份与路径。换模型、另存路径或拿到别人的工程 JSON 时，不能按同名楼层或构件强行套用。未保存、云模型或 Revit Server 的工程设置存取尚不支持。

## 单墙试算与洞口规则

入口：BV → bright vision → 墙体试算与台账。初次建议选一面尺寸已知、未与邻墙重叠、顶部未连接的普通基本直墙，在 Revit 中选中后，以“当前选择”试算。

1. 核对“计量范围”“阶段”，在“设计选项”选择“主模型”，再选择明确的“几何校验视图（不切换活动视图）”。校验三维视图必须与所选阶段一致；读取不会把它切换成当前活动视图。

2. 默认模板“BV 项目几何核对”接受已验证的真实矩形门窗切洞和原生矩形 Opening。“BV 原生矩形 Opening 核对”只接受原生矩形 Opening；普通门窗不会因此被当作无洞墙。

3. 需要自有约定时，点“编辑自有模板…”；内置方案会复制为自有模板。可编辑名称、说明、已实现的切洞策略和容差，再点“应用模板”。用“导入 BV 规则 JSON…”或“导出 BV 规则 JSON…”交换本插件格式的规则，不执行任意脚本，也不直接加载第三方算量规则库。

4. 点“读取并试算”。只有独立基准和实际实体校验通过才出量：单侧轴向投影面积从长度 × 高度扣洞口并集，体积从独立矩形墙基准扣洞口并集；重叠洞口只扣新增并集部分。

5. 门窗扣量依据真实切洞证据，不用标称宽高猜测。未知 void、非矩形切洞、几何证明不足或重叠交接会显示未支持原因；不能删掉拒绝条件后把结果当正确量。墙体交接应使用独立的交接入口。

该面积不是墙体双面面积、表面面积、模板、抹灰或建筑面积；Revit 原生体积只作参照，不能当毛量再次扣洞。修改规则后旧结果过期，要重新读取。

## 墙体台账与工程报表

1. 完成“读取并试算”后，在“墙明细与台账”选择一面墙。先看状态与说明，再对照基准投影、扣洞并集、净投影、基准体积、扣减、试算体积，以及 Revit 原生量和实际实体量。

2. 下方逐条台账给出步骤、关联 ID、面积／体积变化、公式与来源。需要检查洞口时，选对应台账行；用“定位选中墙／洞口／分组并关闭”返回 Revit 核对实际对象。

3. 按状态、搜索、计量层和施工段筛选，用“汇总分组 / CSV”选计量层、施工段、原生楼层或墙类型，再看“分组汇总（选择一组可定位全部源墙）”。这里是整构件归属汇总，跨层／跨段墙不会被几何拆分。

4. 点击“导出当前筛选与汇总 CSV…”。同一 CSV 包含规则和来源信息、当前分组、墙明细与逐条台账，可用表格软件复核。它是现有 CSV 工程报表，尚不是完整地区计量计算书，也没有因此提供 XLSX／PDF 计算书。

合计只包含通过项，并保留未支持／失败数量。没有通过项时合计为空；只有明确算得的零才是零。模型、阶段、校验视图或规则改变后，须重新读取，旧报表不能继续导出或定位。

## 墙体交接与优先顺序

入口：BV → bright vision → 墙体交接 体积核算。此功能只分配体积，使用项目明确确认的顺序；不是地区交接扣减规则。

1. 选择“请求范围”“阶段”，在“设计选项”选择“主模型”，再选“同阶段三维校验视图（不切换视图）”及“前置切洞规则”，点“读取完整交接组件”。建议先用两面已知相交的普通直墙核对。

2. 上表每行是一个完整交接组件。选择组件，核对参与墙、请求墙和说明。范围外邻墙也会读入，因为它们可能拥有或影响交叠体积；只选其中一面墙不能忽略其他依赖。

3. 每个多墙组件都要点“确认／修改选中组件顺序…”。在顺序窗口选墙，用“上移 ↑”“下移 ↓”调整；数字越小越优先，优先级 1 保留共享交叠体积。名称或 ElementId 不代表业务优先级。

4. 核对完整参与列表，包括范围外依赖墙，勾选“我已核对完整组件（包括范围外依赖墙）的优先顺序，确认优先级 1 保留交叠体积。”，再点“确认并应用顺序”。修改顺序后需要再次确认；单墙组件采用唯一顺序。

5. 完成各多墙组件确认后，点“按确认顺序核算”。后续墙只扣当前剩余实体中已归给更高优先墙的部分，避免三墙公共交集重复扣量。完整组件形状与体积守恒均须通过，未知几何或依赖不全不会出量。

6. 对照“完整组件分配合计”与“其中请求墙”合计。它们范围不同；不能把请求墙合计当作整个组件。当前交接只处理体积，交接扣量除以墙厚不能冒充面积量。

## 交接台账、CSV 与方案

1. 核算后在组件中选择墙，核对参数毛量、扣洞、洞后、交接扣量、分配结果、实际实体和原生参照。下方显示每条高优先墙的关联 ID、优先级、扣前、扣后与本步扣量。

2. 默认勾选“明细与 CSV 包含范围外依赖墙”，方便复核完整组件。取消只改变明细与 CSV 的展示范围，不改变依赖读取、顺序或体积分配。

3. 点“导出交接体积及台账 CSV…”保存组件、墙与逐步扣量报表。选择可信批次中的墙后，可点“定位选中墙并关闭”回 Revit 核查。未支持组件结果为空且说明原因，不记成零。

4. 选中组件，点“保存交接方案…”保存 .bvwalljoins.json。它保存来源绑定和确认顺序，不保存可直接复用的数量，也不替代 .bvproject.json 的计量层／施工段归属。

5. 再次使用时，先读取组件，选中对应组件，再点“打开交接方案…”。只有来源、参与墙和几何依赖一致时才可沿用顺序；仍要点“按确认顺序核算”。模型、阶段、规则、视图或顺序变化后旧结果过期，不能继续导出或定位。

## 支持边界与常见问题

为什么有墙没有结果？当前几何试算仅支持有限的普通基本直墙情形，要求水平直线轴、直立、固定且可核对的高度／厚度及可靠几何证据。弧墙、斜墙、幕墙、叠层墙、顶部约束／附着、未知编辑轮廓、变截面、未知切洞等仍可能被拒绝。先读状态和原因；未支持不表示工程里没有这部分量。

墙梁柱板都能算交接吗？当前交接入口限定同根文档、同阶段、主模型下的有限墙体组件。墙梁柱板交接、材料分层、链接计量、跨层／跨段几何切量及地区清单／定额规则仍未完整实现。

为什么按钮灰了或提示过期？先检查是否已完成本轮读取、校验与顺序确认，再核对模型和条件有无变化。按提示重新读取／核算；失败或取消不会提交半轮结果。已有旧结果也不能冒充当前结果。

读取很久怎么办？墙体试算和交接窗口提供“取消本轮”。取消在操作检查点生效，当前单次原生几何运算需要先返回；取消后不提交部分合计，需要重新读取。模型 ZIP 导出期间可按 Esc 取消该导出命令。

计量层和施工段会裁量吗？不会。它们只改变整构件的报表归属；即使设置参考标高，跨层墙仍不是按该平面裁开的墙量。

有没有 Revit AI 助手？这里没有在 Revit 内执行任意自然语言指令的 AI。Bright Vision 查看器的“模型助手”是本地有限规则查询，可查概览、类别、楼层、所选属性、缺失项和原始量，并给出定位依据；它不上传模型，不使用大语言模型，也不判断地区规范或自动完成全量算量。

建议的第一次核对：先用“土建原始量工作台”导出几条原始量，再对一面已知尺寸的独立直墙做试算并检查洞口台账；最后再用两面相交墙确认顺序并比较交接扣量。保留 CSV、规则 JSON、工程 JSON 和交接方案时，分别注明各自用途与来源模型。

## 检查更新与安装新版

首次获得此入口，需要先安装一次 BV 1.11 并重开 Revit。旧版 1.10 没有“检查更新”按钮，不能通过旧版本中不存在的入口升级。

1. 进入 BV → bright vision → 检查更新。窗口显示当前插件版本；默认勾选“包含开发预览版本”，表示同时查看开发预览与稳定发布。开发预览可能仍有待完成功能与验证边界，请先读发布说明；取消勾选后只检查稳定版，不代表自动更换已安装版本。

2. 点“重新检查”。检查与下载只通过匿名 GET 访问官方 GitHub，不上传模型或工程信息。窗口会区分发现新版、当前已是最新、本机比线上公开版更新或尚无稳定版；本机较新时不提供降级。网络异常表示检查未完成，不能当作当前已是最新。

3. 发现新版后点“下载更新”。下载结束会核对文件大小和 SHA-256，只有通过后才可点“打开安装器”。“下载并校验完成”表示文件校验完成，插件仍未更新。切换渠道会使先前检查与下载入口失效，要重新检查；“取消检查／下载”可取消本轮操作。

4. 点击“打开安装器”，按窗口提示确认。随后保存工程并退出全部 Revit，在安装器中完成安装，再重开 Revit 使新版生效。“安装器已打开”仍不等于更新完成；当前 Revit 会话继续使用旧版，需安装并重开后再核对版本。

5. Windows 或 Revit 若出现发布者提示，请本人核对来源、文件与提示后决定。SHA-256 校验只核对文件完整性，不代替发布者确认，也不会自动同意提示或替你安装。
";

        private sealed class HelpTopic
        {
            internal readonly string Title;
            internal readonly List<string> Paragraphs = new List<string>();
            internal HelpTopic(string title) { Title = title; }
            public override string ToString() { return Title; }
        }

        private static List<HelpTopic> Topics()
        {
            var topics = new List<HelpTopic>();
            HelpTopic topic = null;
            foreach (string raw in GuideMarkdown.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string paragraph = raw.Trim();
                if (paragraph.StartsWith("## ", StringComparison.Ordinal))
                {
                    topic = new HelpTopic(paragraph.Substring(3));
                    topics.Add(topic);
                }
                else if (topic != null) topic.Paragraphs.Add(paragraph);
            }
            return topics;
        }

        private sealed class HelpDialog : Window
        {
            private readonly ListBox navigation = new ListBox();
            private readonly FlowDocumentScrollViewer reader = new FlowDocumentScrollViewer();

            internal HelpDialog()
            {
                Title = "BV · 功能介绍与入门指南 · 崛亮视觉";
                Icon = Branding.Icon(32);
                Width = Math.Min(1100, Math.Max(640, SystemParameters.WorkArea.Width - 24));
                Height = Math.Min(790, Math.Max(440, SystemParameters.WorkArea.Height - 24));
                MinWidth = Math.Min(780, Width); MinHeight = Math.Min(540, Height);
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ShowInTaskbar = false;
                Background = Brushes.White;
                Foreground = ColorBrush(30, 47, 65);
                FontFamily = new FontFamily("Microsoft YaHei UI");
                FontSize = 13;

                var root = new Grid { Background = Brushes.White };
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition());
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Content = root;

                var header = new Border { Background = ColorBrush(17, 35, 55), Padding = new Thickness(20, 14, 20, 14) };
                var headerContent = new DockPanel();
                headerContent.Children.Add(new Image { Source = Branding.Icon(32), Width = 38, Height = 38,
                    Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
                var heading = new StackPanel();
                heading.Children.Add(new TextBlock { Text = "功能介绍与入门指南", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
                heading.Children.Add(new TextBlock { Text = "崛亮视觉 · Bright Vision  |  选择左侧主题，按实际按钮完成第一次核对", Foreground = ColorBrush(99, 225, 198),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                headerContent.Children.Add(heading); header.Child = headerContent; root.Children.Add(header);

                var body = new Grid { Margin = new Thickness(16, 16, 16, 0) };
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(202) });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                navigation.ItemsSource = Topics();
                navigation.BorderBrush = ColorBrush(222, 229, 237);
                navigation.Background = ColorBrush(247, 249, 252);
                navigation.Padding = new Thickness(6);
                navigation.Margin = new Thickness(0, 0, 16, 0);
                navigation.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
                navigation.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
                var itemStyle = new Style(typeof(ListBoxItem));
                itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 10, 6, 10)));
                itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
                navigation.ItemContainerStyle = itemStyle;
                navigation.SelectionChanged += delegate { ShowTopic(); };
                body.Children.Add(navigation);

                reader.IsToolBarVisible = false;
                reader.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                reader.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                reader.IsSelectionEnabled = true;
                Grid.SetColumn(reader, 1); body.Children.Add(reader);
                Grid.SetRow(body, 1); root.Children.Add(body);

                var footer = new Border { Padding = new Thickness(20, 12, 20, 12), Margin = new Thickness(0, 14, 0, 0),
                    Background = ColorBrush(245, 247, 250), BorderBrush = ColorBrush(222, 229, 237), BorderThickness = new Thickness(0, 1, 0, 0) };
                var actions = new DockPanel();
                var close = new Button { Content = "关闭", IsCancel = true, Padding = new Thickness(24, 7, 24, 7), Margin = new Thickness(12, 0, 0, 0) };
                close.Click += delegate { Close(); };
                DockPanel.SetDock(close, Dock.Right); actions.Children.Add(close);
                actions.Children.Add(new TextBlock { Text = "离线内置说明 · 正文可选择并复制 · 原始量与项目试算须分别核对口径", TextWrapping = TextWrapping.Wrap,
                    Foreground = ColorBrush(92, 108, 126), VerticalAlignment = VerticalAlignment.Center });
                footer.Child = actions; Grid.SetRow(footer, 2); root.Children.Add(footer);
                navigation.SelectedIndex = 0;
            }

            private void ShowTopic()
            {
                HelpTopic topic = navigation.SelectedItem as HelpTopic;
                if (topic == null) return;
                var document = new FlowDocument { PagePadding = new Thickness(4, 0, 18, 8), ColumnWidth = 2000,
                    FontFamily = FontFamily, FontSize = 14, LineHeight = 24, Foreground = Foreground };
                document.Blocks.Add(new Paragraph(new Run(topic.Title)) { FontSize = 22, FontWeight = FontWeights.SemiBold,
                    LineHeight = 31, Margin = new Thickness(0, 0, 0, 18) });
                foreach (string text in topic.Paragraphs)
                {
                    bool bullet = text.StartsWith("- ", StringComparison.Ordinal);
                    document.Blocks.Add(new Paragraph(new Run(bullet ? "• " + text.Substring(2) : text)) {
                        Margin = new Thickness(bullet ? 10 : 0, 0, 0, 13), TextAlignment = TextAlignment.Left });
                }
                reader.Document = document;
            }
        }

        private static SolidColorBrush ColorBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }
    }
}
