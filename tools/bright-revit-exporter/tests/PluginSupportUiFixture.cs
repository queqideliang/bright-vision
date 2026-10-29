// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrightVision.Export;
using BrightVision.Revit;
using BrightVision.Update;

internal static class PluginSupportUiFixture
{
    private static int checks;
    private static bool optionalWorkspaceTutorialPresent;
    private static readonly List<string> cases = new List<string>();
    private static readonly List<string> layouts = new List<string>();
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    // Deliberately incomplete, non-executable bytes: not a PE or an installer.
    private static readonly byte[] Payload = { 0x4d, 0x5a, 0x42, 0x56, 0x55, 0x49, 0x00, 0x01 };

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); checks++; }
    private static T Field<T>(object owner, string name)
    { return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner); }
    private static string Text(PluginUpdateWindow window, string name) { return Field<TextBlock>(window, name).Text; }
    private static Button Button(PluginUpdateWindow window, string name) { return Field<Button>(window, name); }
    private static void Click(Button value) { value.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent, value)); }
    private static void Channel(PluginUpdateWindow window, bool includePreview)
    {
        var control = Field<CheckBox>(window, "preview");
        control.IsChecked = includePreview;
        control.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, control));
    }
    private static string Normalize(string value) { return value.Replace("\r\n", "\n").Trim(); }
    private static Dictionary<string, object> O(params object[] values)
    { var result = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) result.Add((string)values[i], values[i + 1]); return result; }
    private static byte[] Bytes(object value) { return Utf8.GetBytes(Json.Encode(value)); }
    private static string Hash(byte[] value)
    { using (var hash = SHA256.Create()) return String.Concat(hash.ComputeHash(value).Select(b => b.ToString("x2", CultureInfo.InvariantCulture))); }
    private static string AssetUrl(string version, string name)
    { return PluginUpdateCore.ReleasesPageUrl + "/download/revit-v" + version + "/" + name; }

    private sealed class FakeTransport : IPluginUpdateTransport
    {
        private readonly string version;
        private readonly byte[] manifest, releases;
        private readonly List<string> requested = new List<string>();
        internal bool FailNetwork, CorruptDownload;
        internal int Disposals;
        internal TaskCompletionSource<bool> Gate;
        internal FakeTransport(string version, bool preview)
        {
            this.version = version;
            string name = "BV-" + version + "-Setup.exe", tag = "revit-v" + version;
            manifest = Bytes(O("schemaVersion", 2, "productId", PluginUpdateCore.ProductId, "publisher", PluginUpdateCore.Publisher,
                "channel", preview ? "preview" : "stable", "version", version,
                "release", O("owner", PluginUpdateCore.Owner, "repository", PluginUpdateCore.Repository, "tag", tag,
                    "url", PluginUpdateCore.ReleasesPageUrl + "/tag/" + tag),
                "installer", O("fileName", name, "size", Payload.Length, "sha256", Hash(Payload), "url", AssetUrl(version, name))));
            releases = Bytes(new object[] { O("id", 120, "tag_name", tag, "draft", false, "prerelease", preview,
                "html_url", PluginUpdateCore.ReleasesPageUrl + "/tag/" + tag,
                "assets", new object[] { Asset("update.json", manifest), Asset(name, Payload) }) });
        }
        private Dictionary<string, object> Asset(string name, byte[] bytes)
        { return O("name", name, "state", "uploaded", "size", bytes.Length, "digest", "sha256:" + Hash(bytes), "browser_download_url", AssetUrl(version, name)); }
        internal string[] Requests { get { lock (requested) return requested.ToArray(); } }
        public async Task<PluginHttpRead> OpenAsync(Uri address, CancellationToken token)
        {
            lock (requested) requested.Add(address.AbsoluteUri);
            // Exercise the real asynchronous continuation path without network I/O.
            await Task.Delay(1, token).ConfigureAwait(false);
            TaskCompletionSource<bool> gate = Gate;
            if (gate != null)
                using (token.Register(delegate { gate.TrySetCanceled(); })) await gate.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (FailNetwork) throw new HttpRequestException("Injected offline fixture response; no network request was made.");
            byte[] bytes;
            if (address.AbsoluteUri == PluginUpdateCore.ApiUrl(1)) bytes = releases;
            else if (address.AbsoluteUri == AssetUrl(version, "update.json")) bytes = manifest;
            else if (address.AbsoluteUri == AssetUrl(version, "BV-" + version + "-Setup.exe"))
            { bytes = (byte[])Payload.Clone(); if (CorruptDownload) bytes[bytes.Length - 1] ^= 0x10; }
            else throw new InvalidOperationException("Unexpected fake transport address: " + address);
            return new PluginHttpRead { StatusCode = 200, ContentLength = bytes.Length, Body = new MemoryStream(bytes, false) };
        }
        public void Dispose() { Interlocked.Increment(ref Disposals); }
    }

    private static PluginUpdateWindow Window(string output, string name, string current, FakeTransport transport)
    { return new PluginUpdateWindow(current, new PluginUpdateClient(transport, Path.Combine(output, "cache-" + name)), false); }

    // A bounded Dispatcher frame lets await resume on the STA; never block on an
    // incomplete task with Result/Wait, and do not create or show native windows.
    private static void Complete(Task task)
    {
        var watch = Stopwatch.StartNew();
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += delegate { if (task.IsCompleted || watch.ElapsedMilliseconds > 10000) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
        if (!task.IsCompleted) throw new TimeoutException("Support UI fixture async operation exceeded ten seconds.");
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject value) where T : DependencyObject
    {
        T typed = value as T; if (typed != null) yield return typed;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
            foreach (T child in Visuals<T>(VisualTreeHelper.GetChild(value, i))) yield return child;
    }
    private static bool Inside(FrameworkElement value, FrameworkElement root, double width, double height)
    {
        Point point = value.TranslatePoint(new Point(), root);
        return value.ActualWidth > 0 && value.ActualHeight > 0 && point.X >= -.1 && point.Y >= -.1 &&
            point.X + value.ActualWidth <= width + .1 && point.Y + value.ActualHeight <= height + .1;
    }
    private static void Render(Window window, string output, int width, int height, string name)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        if (!(window is PluginUpdateWindow))
        {
            var navigation = Field<ListBox>(window, "navigation");
            navigation.ScrollIntoView(navigation.SelectedItem); root.UpdateLayout();
        }
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew)) encoder.Save(stream);
        Check(!window.IsVisible && !window.IsLoaded, "Render remains offscreen: " + name);
        Check(Visuals<Button>(root).Where(button => button.Content.ToString() == "关闭").All(button => Inside(button, root, width, height)), "Close is reachable: " + name);
        var update = window as PluginUpdateWindow;
        if (update != null)
            Check(new[] { "check", "download", "install", "cancel" }.All(field => Inside(Button(update, field), root, width, height)), "All update actions stay inside the client: " + name);
        else Check(Field<FlowDocumentScrollViewer>(window, "reader").ActualWidth > 450, "Help text remains readable: " + name);
        layouts.Add(name);
    }

    private static void Help(string output, string module)
    {
        string guide = Normalize(PluginHelpWindow.GuideMarkdown);
        string userGuide = Normalize(File.ReadAllText(Path.Combine(module, "USER_GUIDE.md"), Utf8));
        string workspaceTutorial = Path.Combine(module, "..", "..", "test", "插件功能入门.md");
        optionalWorkspaceTutorialPresent = File.Exists(workspaceTutorial);
        Check(guide == userGuide && (!optionalWorkspaceTutorialPresent || guide == Normalize(File.ReadAllText(workspaceTutorial, Utf8))),
            "Required USER_GUIDE and any optional workspace tutorial equal the embedded guide");
        Window window = PluginHelpWindow.CreateWindow(); var list = Field<ListBox>(window, "navigation"); var reader = Field<FlowDocumentScrollViewer>(window, "reader");
        Check(window.Icon != null && window.Title.Contains("崛亮视觉") && reader.IsSelectionEnabled && !reader.IsToolBarVisible, "Branded help uses a selectable readonly document");
        string[] sections = guide.Split(new[] { "\n## " }, StringSplitOptions.None).Skip(1).ToArray();
        Check(sections.Length == 10 && list.Items.Count == 10 && list.SelectedIndex == 0, "All ten help topics are available with an initial topic");
        for (int index = 0; index < sections.Length; index++)
        {
            list.SelectedIndex = index;
            string actual = Normalize(new TextRange(reader.Document.ContentStart, reader.Document.ContentEnd).Text);
            string[] paragraphs = sections[index].Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            Check(paragraphs.All(paragraph => actual.Contains(paragraph.Trim().StartsWith("- ") ? "• " + paragraph.Trim().Substring(2) : paragraph.Trim())) &&
                reader.Document.Blocks.OfType<Paragraph>().All(p => p.Inlines.All(inline => inline is Run)), "Selected help topic renders its entire inert text: " + list.SelectedItem);
        }
        list.SelectedIndex = 9; list.ScrollIntoView(list.SelectedItem); Render(window, output, 1100, 790, "BV-help-update-1100.png");
        Render(window, output, 780, 540, "BV-help-update-780.png");
        window.Close(); cases.Add("Ten offline help topics, Markdown parity and two update-guide layouts");
    }

    private static void NewVersionAndDownload(string output)
    {
        var transport = new FakeTransport("1.12.0", true); var window = Window(output, "complete", "1.11.0", transport);
        Check(transport.Requests.Length == 0 && !Button(window, "download").IsEnabled && !Button(window, "install").IsEnabled, "Injected window does not auto-check or offer unverified actions");
        transport.Gate = new TaskCompletionSource<bool>(); Task pending = window.CheckNowAsync();
        Check(!Button(window, "check").IsEnabled && !Button(window, "download").IsEnabled && Button(window, "cancel").IsEnabled && !Field<CheckBox>(window, "preview").IsEnabled, "Checking disables competing actions and enables cancellation");
        transport.Gate.TrySetResult(true); Complete(pending); transport.Gate = null;
        Check(Text(window, "status") == "发现新版本 1.12.0" && Text(window, "detail").Contains("SHA-256") && Button(window, "download").IsEnabled && !Button(window, "install").IsEnabled, "New version offers download only after manifest validation");
        Render(window, output, 740, 560, "BV-update-new-740.png"); Render(window, output, 620, 470, "BV-update-new-620.png");
        transport.Gate = new TaskCompletionSource<bool>(); pending = window.DownloadNowAsync();
        Check(!Button(window, "download").IsEnabled && !Button(window, "install").IsEnabled && Button(window, "cancel").IsEnabled, "Downloading never enables an incomplete installer");
        transport.Gate.TrySetResult(true); Complete(pending); transport.Gate = null;
        var received = Field<PluginInstallerDownload>(window, "downloaded");
        Check(Text(window, "status") == "下载并校验完成" && Field<ProgressBar>(window, "progress").Value == 100 && Button(window, "install").IsEnabled && !Button(window, "download").IsEnabled, "Verified download enables the explicit installer action");
        Check(received != null && File.ReadAllBytes(received.Path).SequenceEqual(Payload) && !Field<bool>(window, "installerOpened") && Text(window, "detail").Contains("发布者确认仍由你决定"), "Synthetic bytes are verified, never launched, and do not replace publisher approval");
        PluginUpdateClient.VerifyDownloaded(received);
        Render(window, output, 620, 470, "BV-update-verified-620.png");
        Channel(window, false);
        Check(Field<PluginUpdateManifest>(window, "manifest") == null && Field<PluginInstallerDownload>(window, "downloaded") == null && Field<string>(window, "latestVersion") == null && Field<ProgressBar>(window, "progress").Value == 0 &&
            !Button(window, "download").IsEnabled && !Button(window, "install").IsEnabled && Text(window, "status").Contains("重新检查"), "Channel changes invalidate check and download results");
        Check(transport.Requests.Length == 3, "Only the fake release, manifest and payload were read");
        window.Close(); Check(transport.Disposals == 1, "Closing idle update UI disposes its injected transport");
        cases.Add("New release, busy states, validated synthetic download, channel invalidation and update layouts");
    }

    private static void VersionStates(string output)
    {
        foreach (string current in new[] { "1.12.0", "1.13.0" })
        {
            var transport = new FakeTransport("1.12.0", true); var window = Window(output, current, current, transport); Complete(window.CheckNowAsync());
            string expected = current == "1.12.0" ? "当前已是所选渠道的最新版本" : "本机版本比线上公开版更新";
            Check(Text(window, "status") == expected && Text(window, "detail").Contains(current) && Text(window, "detail").Contains("1.12.0") &&
                !Button(window, "download").IsEnabled && !Button(window, "install").IsEnabled, "Equal or newer local version is accurately identified without downgrade: " + current);
            Check(transport.Requests.SequenceEqual(new[] { PluginUpdateCore.ApiUrl(1) }), "Current local versions do not request an installer or manifest"); window.Close();
        }
        var previewOnly = new FakeTransport("1.12.0", true); var stable = Window(output, "stable", "1.11.0", previewOnly); Channel(stable, false); Complete(stable.CheckNowAsync());
        Check(Text(stable, "status") == "尚无稳定版" && Text(stable, "detail").Contains("包含开发预览版本") && !Button(stable, "download").IsEnabled && !Button(stable, "install").IsEnabled, "Preview-only metadata does not claim a stable release");
        Check(previewOnly.Requests.Length == 1, "Empty stable channel does not download preview metadata"); stable.Close();
        cases.Add("Current version, newer local version and no stable release");
    }

    private static void FailuresAndCancel(string output)
    {
        var offline = new FakeTransport("1.12.0", true) { FailNetwork = true }; var window = Window(output, "offline", "1.11.0", offline); Complete(window.CheckNowAsync());
        Check(Text(window, "status") == "检查未完成" && Text(window, "detail").Contains("未能连接官方更新服务") && Button(window, "check").IsEnabled && !Button(window, "download").IsEnabled && !Button(window, "install").IsEnabled, "Network error stays an incomplete check with a retry");
        Render(window, output, 620, 470, "BV-update-offline-620.png"); window.Close();
        var corrupt = new FakeTransport("1.12.0", true) { CorruptDownload = true }; window = Window(output, "corrupt", "1.11.0", corrupt); Complete(window.CheckNowAsync()); Complete(window.DownloadNowAsync());
        Check(Text(window, "status") == "下载未完成" && Text(window, "detail").Contains("SHA-256") && Field<PluginInstallerDownload>(window, "downloaded") == null && !Button(window, "install").IsEnabled && Button(window, "download").IsEnabled, "Checksum failure cannot enable installation and permits retry");
        Check(!Directory.EnumerateFiles(Path.Combine(output, "cache-corrupt"), "*", SearchOption.AllDirectories).Any(), "Failed validation leaves no partial or completed payload"); window.Close();
        var cancellable = new FakeTransport("1.12.0", true) { Gate = new TaskCompletionSource<bool>() }; window = Window(output, "cancel", "1.11.0", cancellable);
        Task pending = window.CheckNowAsync(); Click(Button(window, "cancel")); Complete(pending);
        Check(Text(window, "status") == "已取消检查" && Button(window, "check").IsEnabled && !Button(window, "cancel").IsEnabled && !Button(window, "install").IsEnabled, "Cancellation returns to idle without a completed update result"); window.Close();
        cases.Add("Offline service, corrupt synthetic payload and canceled check");
    }

    private static void Run(string output, string module)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Help(output, module); NewVersionAndDownload(output); VersionStates(output); FailuresAndCancel(output);
        File.WriteAllText(Path.Combine(output, "report.json"), Json.Encode(O("result", "PASS", "assertions", checks,
            "cases", cases, "layouts", layouts, "scope", "Production help and update WPF plus real update core/client with injected asynchronous byte transport; no Autodesk, network, browser, publisher dialog, installer execution or native desktop input",
            "syntheticPayloadBytes", Payload.Length, "requiredUserGuideValidated", true, "optionalWorkspaceTutorialPresent", optionalWorkspaceTutorialPresent,
            "uiShown", false, "nativeDesktopInput", false, "networkUsed", false,
            "revitStarted", false, "browserOpened", false, "publisherDialogSimulated", false, "installerStarted", false, "childFixtureExecutable", false)), Utf8);
    }

    private static int Main(string[] args)
    {
        Exception failure = null;
        var thread = new Thread(delegate() {
            try { Run(args[0], args[1]); } catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(30000)) throw new TimeoutException("Support UI fixture did not leave its STA within thirty seconds.");
        if (failure != null) throw new InvalidOperationException("Support UI fixture failed after " + checks + " assertions.", failure);
        return 0;
    }
}
