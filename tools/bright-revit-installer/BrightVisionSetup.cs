using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("BV Revit Exporter Setup")]
[assembly: AssemblyProduct("BV / bright vision")]
[assembly: AssemblyCompany("崛亮视觉")]
[assembly: AssemblyCopyright("Copyright 2026 崛亮视觉 (Bright Vision)")]
[assembly: AssemblyDescription("Installs the Revit export plugin for the current Windows user")]
[assembly: AssemblyVersion("1.11.0.0")]
[assembly: AssemblyFileVersion("1.11.0.0")]
[assembly: AssemblyInformationalVersion("1.11.0")]

internal sealed class SetupOptions
{
    internal string Action;
    internal string[] Versions;
    internal string AddinsRoot;
    internal string CacheRoot;
    internal string PreviewOutput;
    internal string Runtime;
    internal bool ReplaceLegacy = true;

    internal static SetupOptions Parse(string[] args)
    {
        SetupOptions value = new SetupOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (i + 1 == args.Length) throw new ArgumentException("Missing value: " + option);
            string argument = args[++i];
            switch (option)
            {
                case "--action":
                    if (Array.IndexOf(new[] { "Detect", "Validate", "Plan", "Install", "Uninstall" }, argument) < 0)
                        throw new ArgumentException("Unknown action: " + argument);
                    value.Action = argument;
                    break;
                case "--versions":
                    value.Versions = argument.Split(',');
                    foreach (string year in value.Versions)
                    {
                        int number;
                        if (!Int32.TryParse(year, out number) || number < 2015 || number > 2026 || year.Length != 4)
                            throw new ArgumentException("Unsupported Revit version: " + year);
                    }
                    break;
                case "--addins-root": value.AddinsRoot = Path.GetFullPath(argument); break;
                case "--cache-root": value.CacheRoot = Path.GetFullPath(argument); break;
                case "--render-preview": value.PreviewOutput = Path.GetFullPath(argument); break;
                case "--runtime":
                    if (argument != "net8" && argument != "net10") throw new ArgumentException("Unknown runtime.");
                    value.Runtime = argument;
                    break;
                case "--replace-legacy":
                    if (!Boolean.TryParse(argument, out value.ReplaceLegacy)) throw new ArgumentException("Expected true or false for --replace-legacy.");
                    break;
                default: throw new ArgumentException("Unknown option: " + option);
            }
        }
        return value;
    }
}

internal sealed class SetupEngine
{
    private readonly string moduleRoot;
    private readonly SetupOptions options;

    internal SetupEngine(SetupOptions options)
    {
        this.options = options;
        string basePath = options.CacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bright.Vision", "SetupCache");
        // Every run owns a new folder. Only embedded entries are written there.
        moduleRoot = Path.Combine(Path.GetFullPath(basePath), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(moduleRoot);
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BrightVision.Module.zip"))
        using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            string prefix = moduleRoot + Path.DirectorySeparatorChar;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                string destination = Path.GetFullPath(Path.Combine(moduleRoot, name));
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid embedded archive path.");
                if (String.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using (Stream input = entry.Open())
                using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
            }
        }
        if (!File.Exists(Path.Combine(moduleRoot, "Invoke-BrightVisionSetup.ps1")))
            throw new InvalidDataException("Embedded installer module is missing.");
    }

    private static string Literal(string value) { return "'" + value.Replace("'", "''") + "'"; }

    internal Dictionary<string, object> Run(string action, string[] years)
    {
        return Run(action, years, options.ReplaceLegacy);
    }

    internal Dictionary<string, object> Run(string action, string[] years, bool replaceLegacy)
    {
        string command = "& " + Literal(Path.Combine(moduleRoot, "Invoke-BrightVisionSetup.ps1")) + " -Action " + Literal(action);
        if (years != null && years.Length > 0)
        {
            List<string> quoted = new List<string>();
            foreach (string year in years) quoted.Add(Literal(year));
            command += " -RevitVersions @(" + String.Join(",", quoted.ToArray()) + ")";
        }
        if (options.AddinsRoot != null) command += " -AddinsRoot " + Literal(options.AddinsRoot);
        if (options.Runtime != null) command += " -Runtime " + Literal(options.Runtime);
        command += " -ReplaceLegacy:$" + (replaceLegacy ? "true" : "false");
        ProcessStartInfo start = new ProcessStartInfo();
        start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        start.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using (Process process = Process.Start(start))
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            string text = stdout.Result.Trim();
            if (String.IsNullOrEmpty(text)) throw new InvalidOperationException(stderr.Result.Trim());
            Dictionary<string, object> response;
            try { response = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text); }
            catch { throw new InvalidOperationException("安装模块未返回有效结果。\r\n" + text + "\r\n" + stderr.Result); }
            if (process.ExitCode != 0 || !(bool)response["success"])
                throw new InvalidOperationException(response.ContainsKey("message") ? (string)response["message"] : stderr.Result);
            return response;
        }
    }
}

internal sealed class VersionChoice
{
    internal string Year;
    internal bool Detected;
    internal bool Managed;
    internal bool HasPayload;
    internal string Runtime;
    internal int LegacyCount;
    internal bool BridgeMigration;
    public override string ToString()
    {
        string runtime = Runtime == "netframework" ? ".NET Framework" : Runtime == "net8" ? ".NET 8" : Runtime == "net10" ? ".NET 10" : "运行时未确定";
        return "Revit " + Year + "　" + runtime + (Detected ? "　已检测" : "　未检测到主程序") + (Managed ? "　· 已注册插件" : "") + (HasPayload ? "" : "　· 无匹配插件") + (BridgeMigration ? "　· 将迁移桥接" : "") + (LegacyCount > 0 ? "　· 旧入口 " + LegacyCount : "");
    }
}

internal sealed class SetupForm : Form
{
    private readonly SetupEngine engine;
    private readonly CheckedListBox versions = new CheckedListBox();
    private readonly TextBox status = new TextBox();
    private readonly Button install = new Button();
    private readonly Button uninstall = new Button();
    private readonly Button refresh = new Button();
    private readonly CheckBox replaceLegacy = new CheckBox();
    private bool busy;

    private static string FriendlyError(string message)
    {
        if (message.StartsWith("Close all Revit processes", StringComparison.Ordinal))
            return "请先保存模型并关闭所有 Revit 窗口，再安装或卸载插件。";
        if (message.StartsWith("Another Bright.Vision registration", StringComparison.Ordinal) || message.StartsWith("Unmanaged file conflict", StringComparison.Ordinal))
            return "检测到其他方式安装的同一导出插件或同名文件，未覆盖。请先使用原安装方式卸载，再重试。\r\n" + message;
        return message;
    }

    private static string StatusLabel(string value)
    {
        switch (value)
        {
            case "Installed": return "已安装";
            case "Upgraded": return "已升级，文件已校验";
            case "AlreadyInstalled": return "已安装，文件一致";
            case "Uninstalled": return "已卸载";
            case "PreservedModified": return "已保留用户修改过的文件";
            case "NotManaged": return "未由本安装器管理";
            case "Skipped": return "已跳过";
            default: return value;
        }
    }

    internal SetupForm(SetupEngine engine)
    {
        this.engine = engine;
        Text = "BV · 崛亮视觉 · Bright Vision · Revit 插件安装 1.11.0";
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(246, 248, 251);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 575);
        MinimumSize = new Size(720, 610);
        AutoScaleMode = AutoScaleMode.Dpi;
        Label title = new Label { Text = "BV · bright vision", Font = new Font("Segoe UI", 26F, FontStyle.Bold),
            ForeColor = Color.FromArgb(22, 43, 70), AutoSize = true, Location = new Point(28, 22) };
        Label detail = new Label { Text = "Revit 模型导出插件\r\n自动识别 Revit 2015–2026，按实际 .NET 运行时匹配插件。", AutoSize = true, Location = new Point(32, 84) };
        versions.SetBounds(32, 145, 656, 160);
        versions.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        versions.CheckOnClick = true;
        versions.BorderStyle = BorderStyle.FixedSingle;
        versions.ItemCheck += delegate { if (IsHandleCreated) BeginInvoke(new Action(UpdateButtons)); };
        replaceLegacy.Text = "替换原版导出入口（可恢复；已有桥接自动迁移）";
        replaceLegacy.Checked = engine != null; replaceLegacy.AutoSize = true; replaceLegacy.Location = new Point(32, 315);
        install.Text = "安装插件"; install.SetBounds(32, 355, 152, 42);
        install.BackColor = Color.FromArgb(32, 91, 184); install.ForeColor = Color.White;
        install.FlatStyle = FlatStyle.Flat;
        uninstall.Text = "卸载插件"; uninstall.SetBounds(198, 355, 152, 42);
        refresh.Text = "重新检测"; refresh.SetBounds(536, 355, 152, 42);
        refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        status.SetBounds(32, 417, 656, 124);
        status.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        status.Multiline = true; status.ReadOnly = true; status.ScrollBars = ScrollBars.Vertical;
        status.BackColor = Color.White; status.BorderStyle = BorderStyle.FixedSingle;
        Controls.AddRange(new Control[] { title, detail, versions, replaceLegacy, install, uninstall, refresh, status });
        AcceptButton = install;
        install.Click += async delegate { await Apply("Install"); };
        uninstall.Click += async delegate { await Apply("Uninstall"); };
        refresh.Click += async delegate { await Detect(false); };
        Shown += async delegate { await Detect(false); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool detected = false, managed = false, invalidInstall = false;
        foreach (VersionChoice item in versions.CheckedItems)
        { detected |= item.Detected; managed |= item.Managed; invalidInstall |= !item.Detected || !item.HasPayload; }
        install.Enabled = !busy && detected && !invalidInstall;
        uninstall.Enabled = !busy && managed;
        refresh.Enabled = !busy;
        versions.Enabled = !busy;
        replaceLegacy.Enabled = !busy;
    }

    private async Task Detect(bool preserveMessage)
    {
        busy = true; UpdateButtons();
        if (!preserveMessage) status.Text = "正在检查插件包并识别 Revit…";
        try
        {
            Dictionary<string, object> reply = await Task.Run(() => engine.Run("Detect", null));
            LoadDetection(reply, preserveMessage);
        }
        catch (Exception error) { status.Text = FriendlyError(error.Message); }
        finally { busy = false; UpdateButtons(); }
    }

    private void LoadDetection(Dictionary<string, object> reply, bool preserveMessage)
    {
        versions.Items.Clear();
        foreach (Dictionary<string, object> row in (IEnumerable)reply["results"])
        {
            VersionChoice item = new VersionChoice { Year = (string)row["RevitVersion"], Detected = (bool)row["Detected"], Managed = (bool)row["Managed"], HasPayload = (bool)row["HasPayload"], Runtime = (string)row["Runtime"], LegacyCount=Convert.ToInt32(row["LegacyUserCount"])+Convert.ToInt32(row["LegacyMachineCount"]), BridgeMigration=(bool)row["BridgeMigrationRequired"] };
            versions.Items.Add(item, (item.Detected && item.HasPayload) || item.Managed);
        }
        if (!preserveMessage) status.Text = versions.Items.Count == 0 ?
            "未检测到 Revit 2015–2026。安装 Revit 后可重新检测。" :
            "已勾选检测到且有匹配插件的版本。安装前请关闭 Revit。\r\n独立导出无需原引擎；旧入口及桥接会备份。重启后进入 BV → bright vision。";
        UpdateButtons();
    }

    internal void RenderPreview(string output)
    {
        LoadDetection(engine.Run("Detect", null), false);
        // Render this program's own controls offscreen; no desktop capture/input.
        CreateControl();
        using (Bitmap bitmap = new Bitmap(ClientSize.Width, ClientSize.Height))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(BackColor);
            foreach (Control control in Controls)
                control.DrawToBitmap(bitmap, control.Bounds);
            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private async Task Apply(string action)
    {
        List<string> years = new List<string>();
        foreach (VersionChoice item in versions.CheckedItems) years.Add(item.Year);
        bool replace = replaceLegacy.Checked;
        busy = true; UpdateButtons();
        status.Text = action == "Install" ? "正在预检并安装插件…" : "正在卸载本安装器管理的插件…";
        try
        {
            Dictionary<string, object> reply = await Task.Run(() => engine.Run(action, years.ToArray(), replace));
            List<string> messages = new List<string>();
            foreach (Dictionary<string, object> row in (IEnumerable)reply["results"])
            {
                messages.Add("Revit " + row["RevitVersion"] + "：" + StatusLabel((string)row["Status"]) + "\r\n" + row["Destination"]);
                if (action == "Install" && Convert.ToInt32(row["LegacyUserBackedUp"]) + Convert.ToInt32(row["LegacyMachineMasked"]) > 0)
                    messages.Add("旧入口已在当前用户停用，可卸载恢复；机器目录与原 DLL 未改动。");
                if (action == "Install" && (bool)row["BridgeBackedUp"])
                    messages.Add("已将原桥接的注册、DLL 与所有权记录移出插件目录并备份；当前使用独立导出器。");
            }
            foreach (object warning in (IEnumerable)reply["warnings"]) messages.Add(warning.ToString());
            if (action == "Install") messages.Add("注册完成。请重启 Revit，在 BV 页签 → bright vision 面板使用本地导出。");
            status.Text = String.Join("\r\n", messages.ToArray());
        }
        catch (Exception error) { status.Text = "操作未完成：\r\n" + FriendlyError(error.Message); }
        finally { busy = false; }
        await Detect(true);
    }
}

internal static class Program
{
    private static void WriteJson(object value)
    {
        // A winexe has no console window. Write UTF-8 to an inherited pipe
        // directly; Console.OutputEncoding can throw without a console handle.
        using (StreamWriter writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
            writer.WriteLine(new JavaScriptSerializer().Serialize(value));
    }

    [STAThread]
    private static int Main(string[] args)
    {
        bool commandLine = Array.IndexOf(args, "--action") >= 0 || Array.IndexOf(args, "--render-preview") >= 0;
        try
        {
            SetupOptions options = SetupOptions.Parse(args);
            SetupEngine engine = new SetupEngine(options);
            if (options.Action != null)
            {
                WriteJson(engine.Run(options.Action, options.Versions));
                return 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (options.PreviewOutput != null)
            {
                using (SetupForm form = new SetupForm(engine)) form.RenderPreview(options.PreviewOutput);
                WriteJson(new { success = true, preview = options.PreviewOutput });
                return 0;
            }
            Application.Run(new SetupForm(engine));
            return 0;
        }
        catch (Exception error)
        {
            if (commandLine)
            {
                WriteJson(new { success = false, product = "Bright.Vision", message = error.Message });
            }
            else MessageBox.Show(error.Message, "Bright.Vision 安装未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
