// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrightVision.Update;

namespace BrightVision.Revit
{
    internal sealed class PluginUpdateWindow : Window
    {
        private readonly PluginUpdateClient client;
        private readonly string currentVersion;
        private readonly TextBlock status = new TextBlock();
        private readonly TextBlock detail = new TextBlock();
        private readonly CheckBox preview = new CheckBox { Content = "包含开发预览版本", IsChecked = true };
        private readonly Button check = new Button { Content = "重新检查" };
        private readonly Button download = new Button { Content = "下载更新", IsEnabled = false };
        private readonly Button install = new Button { Content = "打开安装器", IsEnabled = false };
        private readonly Button cancel = new Button { Content = "取消检查／下载", IsEnabled = false };
        private readonly ProgressBar progress = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100 };
        private PluginUpdateManifest manifest;
        private PluginInstallerDownload downloaded;
        private string latestVersion;
        private CancellationTokenSource operation;
        private bool busy, closed, installerOpened;

        internal PluginUpdateWindow(string currentVersion) : this(currentVersion, new PluginUpdateClient(), true) { }
        internal PluginUpdateWindow(string currentVersion, PluginUpdateClient client, bool autoCheck)
        {
            this.currentVersion = currentVersion; this.client = client;
            PluginUpdateCore.CompareVersions(currentVersion, currentVersion);
            Title = "崛亮视觉 · Bright Vision — 检查更新"; Width = 740; Height = 560; MinWidth = 620; MinHeight = 470;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White;
            var root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = "检查插件更新", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brushes.MidnightBlue });
            head.Children.Add(new TextBlock { Text = "当前版本  " + currentVersion + "   ·   崛亮视觉 / Bright Vision", Margin = new Thickness(0, 8, 0, 16), Foreground = Brushes.DimGray });
            head.Children.Add(preview); root.Children.Add(head);
            var body = new StackPanel { Margin = new Thickness(0, 20, 0, 12) };
            status.Text = "准备检查官方插件版本。"; status.FontSize = 18; status.FontWeight = FontWeights.SemiBold; status.TextWrapping = TextWrapping.Wrap;
            detail.Text = "只访问官方 GitHub 发布页，不上传模型或工程信息。"; detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 12, 0, 14); detail.LineHeight = 23;
            body.Children.Add(status); body.Children.Add(detail); body.Children.Add(progress);
            var page = new Button { Content = "打开官方发布页", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
            page.Click += delegate { try { PluginUpdateClient.OpenOfficialPage(latestVersion); } catch (Exception error) { MessageBox.Show(this, "未能打开浏览器：" + error.Message, "检查更新"); } }; body.Children.Add(page);
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            var bottom = new StackPanel();
            bottom.Children.Add(new TextBlock { Text = "安装更新需先保存工程并退出全部 Revit。安装完成后重开 Revit 生效。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 12) });
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (Button button in new[] { check, download, install, cancel }) { button.Margin = new Thickness(6, 4, 0, 0); button.Padding = new Thickness(12, 7, 12, 7); buttons.Children.Add(button); }
            var close = new Button { Content = "关闭", Margin = new Thickness(6, 4, 0, 0), Padding = new Thickness(12, 7, 12, 7) }; close.Click += delegate { Close(); }; buttons.Children.Add(close);
            bottom.Children.Add(buttons); Grid.SetRow(bottom, 2); root.Children.Add(bottom); Content = root;
            check.Click += async delegate { await CheckNowAsync(); };
            download.Click += async delegate { await DownloadNowAsync(); };
            cancel.Click += delegate { if (operation != null) operation.Cancel(); };
            preview.Click += delegate { manifest = null; downloaded = null; latestVersion = null; installerOpened = false; progress.Value = 0; progress.IsIndeterminate = false; RefreshButtons(); status.Text = "更新渠道已改变，请重新检查。"; detail.Text = "修改渠道后，先前的检查结果和下载入口已失效。"; };
            install.Click += delegate
            {
                if (busy || downloaded == null || installerOpened) return;
                if (MessageBox.Show(this, "即将打开已校验的官方安装器。\n\n请先保存工程；安装器打开后，退出全部 Revit，再在安装器中完成更新。\n\n现在打开安装器吗？", "安装更新", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
                try { PluginUpdateClient.LaunchInstaller(downloaded); installerOpened = true; status.Text = "安装器已打开"; detail.Text = "请保存并退出全部 Revit，在安装器中完成更新，再重开 Revit。当前会话仍在使用 " + currentVersion + "，尚不能视为已更新。"; RefreshButtons(); }
                catch (Exception error) { status.Text = "安装器未能打开"; detail.Text = Friendly(error); }
            };
            Closed += delegate { closed = true; if (operation != null) operation.Cancel(); else client.Dispose(); };
            if (autoCheck) Loaded += async delegate { await CheckNowAsync(); };
        }

        internal async Task CheckNowAsync()
        {
            if (busy || closed) return;
            manifest = null; downloaded = null; latestVersion = null; installerOpened = false;
            status.Text = "正在检查官方插件版本…"; detail.Text = "连接 GitHub，读取 Revit 插件发布记录。模型数据留在本机。"; progress.Value = 0; progress.IsIndeterminate = true;
            var source = new CancellationTokenSource(); operation = source; SetBusy(true);
            try
            {
                bool includePreview = preview.IsChecked == true;
                PluginUpdateCheck result = await Task.Run(delegate { return client.CheckAsync(currentVersion, includePreview, source.Token); });
                if (closed) return;
                if (result.Latest == null)
                {
                    status.Text = includePreview ? "尚无可用的公开插件版本" : "尚无稳定版";
                    detail.Text = includePreview ? "官方发布页还没有符合安装条件的插件版本，可稍后再检查。" : "可以勾选“包含开发预览版本”后重新检查。";
                }
                else
                {
                    latestVersion = result.Latest.Version; manifest = result.Available;
                    int comparison = PluginUpdateCore.CompareVersions(latestVersion, currentVersion);
                    string channel = result.Latest.Channel == "preview" ? "开发预览" : "稳定版";
                    if (comparison > 0) { status.Text = "发现新版本 " + latestVersion; detail.Text = channel + " · 官方安装包 " + Megabytes(manifest.Installer.Size) + " MB\n可先打开官方发布页阅读本版功能和已知限制，再下载更新。下载后将核对大小与 SHA-256。"; }
                    else if (comparison == 0) { status.Text = "当前已是所选渠道的最新版本"; detail.Text = "本机 " + currentVersion + " · 线上 " + latestVersion + "（" + channel + "）。"; }
                    else { status.Text = "本机版本比线上公开版更新"; detail.Text = "本机 " + currentVersion + " · 线上最新 " + latestVersion + "（" + channel + "）。当前无需降级；本机开发功能可能尚未公开发布。"; }
                }
            }
            catch (OperationCanceledException) { if (!closed) { status.Text = source.IsCancellationRequested ? "已取消检查" : "检查超时，尚未完成"; detail.Text = "可以重新检查，或打开官方发布页手动下载。"; } }
            catch (Exception error) { if (!closed) { status.Text = "检查未完成"; detail.Text = Friendly(error); } }
            finally { operation = null; source.Dispose(); if (closed) client.Dispose(); else { progress.IsIndeterminate = false; SetBusy(false); } }
        }

        internal async Task DownloadNowAsync()
        {
            if (busy || closed || manifest == null) return;
            PluginUpdateManifest selected = manifest; downloaded = null; installerOpened = false;
            status.Text = "正在下载 " + selected.Version + "…"; detail.Text = "正在下载官方安装包。可以取消；取消或校验失败不会运行安装程序。"; progress.IsIndeterminate = false; progress.Value = 0;
            var source = new CancellationTokenSource(); operation = source; SetBusy(true);
            var updates = new Progress<long>(delegate(long count) { if (!closed && busy && operation == source) { progress.Value = Math.Min(100, 100.0 * count / selected.Installer.Size); } });
            try
            {
                PluginInstallerDownload result = await Task.Run(delegate { return client.DownloadAsync(selected, updates, source.Token); });
                if (closed) return;
                downloaded = result; progress.Value = 100; status.Text = "下载并校验完成";
                detail.Text = "版本 " + selected.Version + " · 大小及 SHA-256 与官方记录一致。\n\n点击“打开安装器”继续。安装前请保存工程并退出全部 Revit。\n\n校验用于核对文件完整性，Windows／Revit 的发布者确认仍由你决定。";
            }
            catch (OperationCanceledException) { if (!closed) { status.Text = source.IsCancellationRequested ? "已取消下载" : "下载超时，尚未完成"; detail.Text = "可以重新下载。当前插件和工程均未改变。"; } }
            catch (Exception error) { if (!closed) { status.Text = "下载未完成"; detail.Text = Friendly(error); } }
            finally { operation = null; source.Dispose(); if (closed) client.Dispose(); else SetBusy(false); }
        }

        private static string Megabytes(long bytes) { return (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture); }
        private static string Friendly(Exception error)
        {
            while (error is AggregateException && error.InnerException != null || error is System.Reflection.TargetInvocationException && error.InnerException != null) error = error.InnerException;
            if (error is HttpRequestException || error is System.Net.WebException) return "未能连接官方更新服务。请检查网络后重试，或打开官方发布页手动下载。\n\n" + error.Message;
            return error.Message;
        }
        private void SetBusy(bool value) { busy = value; RefreshButtons(); }
        private void RefreshButtons() { check.IsEnabled = !busy; preview.IsEnabled = !busy; cancel.IsEnabled = busy; download.IsEnabled = !busy && manifest != null && downloaded == null; install.IsEnabled = !busy && downloaded != null && !installerOpened; }
    }
}
