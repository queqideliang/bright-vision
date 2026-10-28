using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrightVision.Civil;
using BrightVision.Revit;

internal static class CivilQuantityUiFixture
{
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static T Field<T>(object value, string name)
    { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    private static object Property(object value, string name)
    { return value.GetType().GetProperty(name).GetValue(value, null); }
    private static void Pick(ComboBox picker, string id)
    { picker.SelectedItem = ((IEnumerable<CivilChoice>)picker.ItemsSource).First(c => c.Id == id); }
    private static List<object> Rows(DataGrid table)
    { return table.Items.Cast<object>().ToList(); }
    private static void Throws(Action action, string message)
    {
        bool failed = false;
        try { action(); } catch (InvalidOperationException) { failed = true; }
        Check(failed, message);
    }
    private static void RejectProject(Action action, string message)
    {
        bool failed = false;
        try { action(); } catch (InvalidDataException) { failed = true; }
        Check(failed, message);
    }
    private static CivilMetric Metric(string state, double? value, string unit, string source)
    { return new CivilMetric { Status = state, Value = value, Unit = unit, Source = source }; }
    private static CivilElementRecord Record(string id, string name, string kind, string kindName, string level, bool supported)
    {
        return new CivilElementRecord { ElementId = id, UniqueId = "unique-" + id, Name = name, KindCode = kind, KindName = kindName,
            Category = kindName, Family = "测试族", TypeName = "测试类型-" + id, LevelId = level,
            LevelName = level == "L1" ? "一层" : "二层", Supported = supported,
            RawLevelId = level, RawLevelName = level == "L1" ? "一层" : "二层",
            LevelUniqueId = "level-unique-" + level, LevelSource = "Element.LevelId",
            Length = Metric("available", 2, "m", "CURVE_ELEM_LENGTH"),
            Area = Metric("available", 3, "m2", "HOST_AREA_COMPUTED"),
            Volume = Metric("available", 4, "m3", "HOST_VOLUME_COMPUTED") };
    }
    private static CivilSnapshot Snapshot(CivilReadRequest request, long revision)
    {
        CivilElementRecord zero = Record("101", "=零值墙", "wall", "普通墙", "L1", true);
        zero.Length.Value = 0;
        CivilElementRecord absent = Record("102", "缺参墙", "wall", "普通墙", "L1", true);
        absent.Area = Metric("missing", null, "m2", "HOST_AREA_COMPUTED"); absent.Issues.Add("原生面积缺失");
        CivilElementRecord beam = Record("201", "结构梁", "beam", "结构梁", "L2", true);
        beam.Area = Metric("not-applicable", null, "m2", "HOST_AREA_COMPUTED");
        beam.RawLevelId = "-1"; beam.RawLevelName = String.Empty; beam.LevelSource = "INSTANCE_REFERENCE_LEVEL_PARAM";
        CivilElementRecord column = Record("301", "结构柱", "structural-column", "结构柱", "L2", true);
        column.Volume = Metric("invalid", null, "m3", "HOST_VOLUME_COMPUTED");
        CivilElementRecord brace = Record("401", "支撑", "brace", "结构支撑", "L1", false);
        brace.ExclusionReason = "支撑不纳入首期梁合计"; brace.Issues.Add("首期未支持的支撑");
        CivilSnapshot snapshot = new CivilSnapshot { ModelTitle = "UI离屏测试工程", DocumentScope = "fixture-root", SourcePath = "H:\\fixture\\UI-offscreen-test.rvt", CapturedAtUtc = "2026-10-28T12:00:00Z",
            ScopeLabel = request.Scope, PhaseLabel = request.PhaseId, DesignOptionLabel = request.DesignOptionId,
            CandidateCount = 8, LinkInstanceCount = 1, MissingLinkCount = 1, Revision = revision,
            Elements = new List<CivilElementRecord> { zero, absent, beam, column, brace } };
        snapshot.Levels.Add(new CivilLevelInfo { Id = "L1", UniqueId = "level-unique-L1", Name = "一层", ElevationMetres = 0 });
        snapshot.Levels.Add(new CivilLevelInfo { Id = "L2", UniqueId = "level-unique-L2", Name = "二层", ElevationMetres = 3.6 });
        snapshot.Exclusions.Add(new CivilExclusion { ElementId = "901", Name = "未加载链接", Reason = "链接未加载，根文档范围不包含其内容" });
        snapshot.Notices.Add("本次仅读取根文档；视图剖切不裁切参数量。");
        return snapshot;
    }
    private static void Render(Window window, string path, int width = 1180, int height = 800)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        DrawingVisual background = new DrawingVisual();
        using (DrawingContext drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(content);
        PngBitmapEncoder png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream file = File.Create(path)) png.Save(file);
    }

    private static void TestProjectWorkflow(CivilQuantityWindow window, string output)
    {
        Field<TabControl>(window, "tabs").SelectedIndex = 0;
        Pick(Field<ComboBox>(window, "groupPicker"), "element");
        CivilSnapshot native = Field<CivilSnapshot>(window, "nativeSnapshot");
        string originalCsv = CivilSchedule.ToCsv(native, native.Elements, "element");
        CivilProjectSettings initial = CivilProject.Create(native, "离屏计量工程");
        List<CivilElementRecord> walls = native.Elements.Where(element => element.KindCode == "wall").ToList();
        CivilProjectWindow abandoned = new CivilProjectWindow(native, initial, walls);
        Check(!abandoned.IsVisible && abandoned.Result == null && abandoned.Icon != null, "Constructing settings must stay offscreen with no implicit accepted result.");
        string discardedLevel = abandoned.SetLevel(null, "取消的计量层", -2);
        abandoned.MapLevels(new List<string> { "level-unique-L1" }, discardedLevel);
        string discardedSegment = abandoned.SetSegment(null, "取消的施工段");
        abandoned.AssignSelected(discardedSegment);
        Field<TextBox>(abandoned, "projectName").Text = "取消的新名称";
        abandoned.Close();
        Check(abandoned.Result == null && initial.Name == "离屏计量工程" && initial.Revision == 1 && initial.QuantityLevels.Count == 0 && initial.LevelMappings.Count == 0 && initial.Segments.Count == 0 && initial.ElementSegments.Count == 0, "Closing uncommitted settings must isolate every draft change from the caller's profile.");
        Check(CivilSchedule.ToCsv(native, native.Elements, "element") == originalCsv, "Cancelling settings must preserve all native records and values.");

        CivilProjectWindow settings = new CivilProjectWindow(native, initial, walls);
        settings.ImportNativeLevels(); settings.ImportNativeLevels();
        CivilProjectSettings imported = settings.CompleteSettings();
        Check(imported.QuantityLevels.Count == 2 && imported.LevelMappings.Count == 2, "Repeated native-level import must not duplicate levels or mappings.");
        Check(imported.LevelMappings.Select(mapping => mapping.SourceLevelUniqueId).OrderBy(value => value).SequenceEqual(new[] { "level-unique-L1", "level-unique-L2" }), "Imported mappings must use native level UniqueIds rather than display names or short IDs.");
        Check(initial.QuantityLevels.Count == 0 && initial.LevelMappings.Count == 0, "Accepting a draft must still not mutate the caller's original profile.");

        string quantityLevel = settings.SetLevel(null, "合并计量层", -1.25);
        Field<TextBox>(settings, "levelName").Text = "联合计量层";
        Field<TextBox>(settings, "elevation").Text = "-1.5";
        settings.SetLevel(quantityLevel, "联合计量层", -1.5);
        settings.MapLevels(new List<string> { "level-unique-L1", "level-unique-L2", "level-unique-L1" }, quantityLevel);
        string segment = settings.SetSegment(null, "A区");
        Field<TextBox>(settings, "segmentName").Text = "A施工区";
        settings.SetSegment(segment, "A施工区"); settings.AssignSelected(segment);
        Field<TextBox>(settings, "projectName").Text = "UI计量工程";
        Throws(delegate { settings.MapLevels(new List<string> { "level-unique-L1", "foreign-level" }, imported.QuantityLevels[0].Id); }, "An invalid later mapping source must reject the whole edit.");
        Throws(delegate { settings.SetLevel(null, "无效标高", Double.NaN); }, "Settings must reject non-finite quantity-level elevation.");
        CivilProjectSettings accepted = settings.CompleteSettings();
        Check(accepted.Name == "UI计量工程" && accepted.QuantityLevels.Single(level => level.Id == quantityLevel).ElevationMetres == -1.5, "Edited project metadata or legal negative reference elevation was lost.");
        Check(accepted.QuantityLevels.Count == 3 && accepted.Segments.Count == 1 && accepted.Segments[0].Name == "A施工区", "Updating existing settings must preserve IDs rather than append duplicate records.");
        Check(accepted.LevelMappings.Count == 2 && accepted.LevelMappings.All(mapping => mapping.QuantityLevelId == quantityLevel), "A failed batch mapping partially changed an earlier valid mapping or failed to merge native levels.");
        Check(accepted.ElementSegments.Count == 2 && accepted.ElementSegments.All(assignment => assignment.SegmentId == segment && walls.Any(wall => wall.UniqueId == assignment.ElementUniqueId)), "Segment assignment must use only selected source element identities.");
        Check(imported.LevelMappings.All(mapping => mapping.QuantityLevelId != quantityLevel) && imported.ElementSegments.Count == 0, "A previously returned settings result must remain isolated from later draft edits.");
        Render(settings, Path.Combine(output, "BV-civil-project-settings.png"), 980, 710);
        ((Grid)settings.Content).Children.OfType<TabControl>().Single().SelectedIndex = 1;
        Render(settings, Path.Combine(output, "BV-civil-project-segments.png"), 980, 710);

        int revision = accepted.Revision;
        window.SetProject(accepted);
        Check(window.CurrentProject.ProjectId == accepted.ProjectId && window.LastSnapshot.ProjectName == "UI计量工程" && window.LastSnapshot.ProjectRevision == revision, "Applying settings must attach explicit project metadata to the presentation snapshot.");
        Check(!Object.ReferenceEquals(native, window.LastSnapshot) && Object.ReferenceEquals(native, Field<CivilSnapshot>(window, "nativeSnapshot")), "Project annotations must use a separate snapshot while preserving the captured native baseline.");
        Check(CivilSchedule.ToCsv(native, native.Elements, "element") == originalCsv && native.Elements.All(element => String.IsNullOrEmpty(element.QuantityLevelId) && String.IsNullOrEmpty(element.SegmentId)), "Applying settings must not modify native metric values, layer identities or annotations.");
        CivilElementRecord annotatedBeam = window.LastSnapshot.Elements.Single(element => element.ElementId == "201");
        Check(annotatedBeam.RawLevelId == "-1" && annotatedBeam.LevelId == "L2" && annotatedBeam.LevelUniqueId == "level-unique-L2" && annotatedBeam.LevelSource == "INSTANCE_REFERENCE_LEVEL_PARAM" && annotatedBeam.QuantityLevelId == quantityLevel, "Business-layer mapping must use the resolved native UniqueId while retaining absent raw LevelId provenance.");
        DataGrid table = Field<DataGrid>(window, "quantities");
        Check(((string)Property(Rows(table).Single(row => (string)Property(row, "ElementId") == "201"), "Detail")).Contains("INSTANCE_REFERENCE_LEVEL_PARAM"), "The visible row detail must disclose native level resolution provenance.");
        accepted.Name = "外部修改不得影响工作台";
        CivilProjectSettings exposed = window.CurrentProject; exposed.Name = "返回副本修改"; exposed.LevelMappings.Clear();
        Check(window.CurrentProject.Name == "UI计量工程" && window.CurrentProject.LevelMappings.Count == 2, "Working project state must not alias either input profiles or returned profiles.");

        Pick(Field<ComboBox>(window, "quantityLevelPicker"), quantityLevel);
        Check(table.Items.Count == 5, "Business-level filtering must retain all source records mapped from both native levels.");
        Pick(Field<ComboBox>(window, "segmentPicker"), segment);
        Check(table.Items.Count == 2 && Rows(table).All(row => (string)Property(row, "KindName") == "普通墙"), "Segment filtering must select exactly assigned records.");
        string segmentCsv = window.BuildCurrentCsv();
        Check(segmentCsv.Contains("101") && segmentCsv.Contains("102") && !segmentCsv.Contains("201") && segmentCsv.Contains("A施工区") && segmentCsv.Contains("联合计量层"), "Project CSV must follow the current layer/segment filter and retain assignment labels.");
        Pick(Field<ComboBox>(window, "segmentPicker"), "__unassigned__");
        Check(table.Items.Count == 3, "The unassigned segment filter must remain explicit rather than include assigned records.");
        Pick(Field<ComboBox>(window, "segmentPicker"), "");
        Pick(Field<ComboBox>(window, "quantityLevelPicker"), "__unassigned__");
        Check(table.Items.Count == 0, "The unmapped quantity-level filter must not include mapped records.");
        Pick(Field<ComboBox>(window, "quantityLevelPicker"), "");
        Pick(Field<ComboBox>(window, "groupPicker"), "quantity-level");
        Check(table.Items.Count == 4 && Rows(table).All(row => (string)Property(row, "QuantityLevelName") == "联合计量层"), "Quantity-level grouping must retain business kinds without multiplying source quantities.");
        object wallGroup = Rows(table).Single(row => (string)Property(row, "KindName") == "普通墙");
        table.SelectedItem = wallGroup;
        Check(window.PrepareFocus() && window.SelectedFocusElements.Count == 2 && window.SelectedFocusElements.All(element => walls.Any(wall => wall.UniqueId == element.UniqueId)), "Focus after project grouping must retain original source identities.");
        Pick(Field<ComboBox>(window, "groupPicker"), "segment");
        Check(table.Items.Count == 4 && Rows(table).Any(row => (string)Property(row, "SegmentName") == "未分配施工段"), "Segment grouping must retain explicitly unassigned source records.");
        Render(window, Path.Combine(output, "BV-civil-project-quantities.png"));

        CivilProjectSettings foreign = window.CurrentProject; foreign.SourcePath = "H:\\fixture\\other-model.rvt";
        CivilSnapshot previous = window.LastSnapshot;
        RejectProject(delegate { window.SetProject(foreign); }, "A foreign model's profile must not apply to current native records.");
        Check(Object.ReferenceEquals(previous, window.LastSnapshot) && window.CurrentProject.Name == "UI计量工程", "Rejected source binding must leave the visible snapshot and project intact.");
        string projectPath = Path.Combine(output, "civil-ui-project.json");
        window.SaveProject(projectPath);
        CivilProjectSettings saved = CivilProjectStore.Load(projectPath);
        Check(saved.ProjectId == window.CurrentProject.ProjectId && saved.Name == "UI计量工程" && saved.SourcePath == native.SourcePath && saved.ElementSegments.Count == 2, "Saved JSON must round-trip source binding and assignments.");
        Check(saved.PreferredScope == "project" && saved.PhaseId == "p2" && saved.DesignOptionId == "o1", "Project persistence must record the explicit current read choices without changing them.");
        window.LoadProject(projectPath);
        Check(window.CurrentProject.ProjectId == saved.ProjectId && window.LastSnapshot.Elements.Count(element => element.SegmentId == segment) == 2 && window.CheckFreshness(), "Loading current-source settings must restore annotations without invalidating native data.");
        Check(!Field<bool>(window, "projectDirty"), "A completed project load must not remain marked as an unsaved edit.");
        string corruptPath = Path.Combine(output, "civil-ui-corrupt.json"); File.WriteAllText(corruptPath, "{broken", Encoding.UTF8);
        previous = window.LastSnapshot;
        RejectProject(delegate { window.LoadProject(corruptPath); }, "A damaged project file must fail explicitly.");
        Check(Object.ReferenceEquals(previous, window.LastSnapshot) && window.CurrentProject.ProjectId == saved.ProjectId && !Field<bool>(window, "projectDirty"), "A failed load must preserve the last accepted project and exact snapshot.");
        string fullCsv = window.BuildCurrentCsv();
        Check(fullCsv.Contains("参照层解析来源") && fullCsv.Contains("INSTANCE_REFERENCE_LEVEL_PARAM") && fullCsv.Contains("原始Element.LevelId"), "CSV must preserve source-level provenance alongside project annotations.");
        Check(CivilSchedule.ToCsv(native, native.Elements, "element") == originalCsv, "Saving and reloading project settings must preserve the native baseline byte-for-byte at the domain CSV boundary.");
        Check(!window.IsVisible && !settings.IsVisible, "All project workflows and renders must remain offscreen.");
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // Reflection does not honor Main's STAThread attribute. Own an STA
        // worker explicitly; never create or launch a temporary executable.
        Exception failure = null;
        Thread worker = new Thread(delegate() {
            try { Run(args); }
            catch (Exception error) { failure = error; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start(); worker.Join();
        if (failure != null) throw new InvalidOperationException("Civil WPF fixture failed.", failure);
    }

    private static void Run(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Expected a workspace artifact output directory.");
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        long revision = 5; int reads = 0; bool failRead = false; CivilReadRequest lastRequest = null;
        List<CivilChoice> phases = new List<CivilChoice> { new CivilChoice { Id = "p1", Label = "现状" }, new CivilChoice { Id = "p2", Label = "新建" } };
        List<CivilChoice> options = new List<CivilChoice> { new CivilChoice { Id = "-1", Label = "主模型" }, new CivilChoice { Id = "o1", Label = "方案一" } };
        Func<CivilReadRequest, CivilSnapshot> reader = delegate(CivilReadRequest request) {
            reads++; lastRequest = request; if (failRead) throw new InvalidOperationException("模拟原生读取失败"); return Snapshot(request, revision);
        };
        CivilQuantityWindow window = new CivilQuantityWindow(new CivilReadRequest { Scope = "view", PhaseId = "p1", DesignOptionId = "-1" }, phases, options, reader, delegate { return revision; });
        Check(!window.IsVisible && reads == 0, "Construction must neither show a window nor read a Revit document.");
        Check(window.Title.Contains("原始量") && window.Icon != null, "Raw-quantity title and BV icon are required.");
        Check(!Field<Button>(window, "csvButton").IsEnabled && !Field<Button>(window, "focusButton").IsEnabled, "Unread data must not be exported or focused.");
        window.RefreshSnapshot();
        Check(reads == 1 && window.LastSnapshot != null, "Explicit refresh must obtain a snapshot.");
        Check(lastRequest.Scope == "view" && lastRequest.PhaseId == "p1" && lastRequest.DesignOptionId == "-1", "Initial explicit request must reach the reader unchanged.");
        Check(window.CheckFreshness() && Field<Button>(window, "csvButton").IsEnabled, "Completed current snapshot must allow CSV export.");
        DataGrid quantities = Field<DataGrid>(window, "quantities"); DataGrid problems = Field<DataGrid>(window, "problems");
        Check(quantities.Items.Count == 5, "All raw and unsupported records must remain inspectable.");
        object zeroRow = Rows(quantities).First(r => (string)Property(r, "ElementId") == "101");
        object missingRow = Rows(quantities).First(r => (string)Property(r, "ElementId") == "102");
        Check((string)Property(zeroRow, "LengthText") == "0", "A true native zero must remain visible as zero.");
        Check((string)Property(missingRow, "AreaText") == "缺失", "Missing area must be explicit rather than presented as zero or NA.");
        Check((string)Property(Rows(quantities).First(r => (string)Property(r, "ElementId") == "201"), "AreaText") == "NA", "A non-applicable metric must remain visibly distinct from missing and zero.");
        Check((string)Property(Rows(quantities).First(r => (string)Property(r, "ElementId") == "401"), "VolumeText") == "NA", "An unsupported record must not display an included quantity.");
        Check((string)Property(Rows(quantities).First(r => (string)Property(r, "ElementId") == "301"), "VolumeText") == "无效", "An invalid native metric must be visibly distinct from zero, missing, and not applicable.");
        Check(problems.Items.Cast<object>().Any(r => (string)Property(r, "ElementId") == "301" && ((string)Property(r, "Message")).Contains("无效")), "The issue tab must include domain-detected invalid metrics even when record.Issues is empty.");
        Check(((string)Property(missingRow, "Detail")).Contains("缺失 1"), "Coverage must disclose missing measurements.");
        Check(Field<TextBlock>(window, "notices").Text.Contains("未加载链接：1"), "Unloaded links must remain explicit.");

        Field<TextBox>(window, "search").Text = "测试类型-201";
        Check(quantities.Items.Count == 1 && (string)Property(quantities.Items[0], "ElementId") == "201", "Type search must filter the actual schedule.");
        Check(problems.Items.Cast<object>().Any(r => (string)Property(r, "ElementId") == "901"), "A local quantity filter must not hide scope exclusions.");
        Field<TextBox>(window, "search").Text = "";
        Pick(Field<ComboBox>(window, "kindPicker"), "wall");
        Check(quantities.Items.Count == 2, "Kind filter must retain the two walls only.");
        string wallCsv = window.BuildCurrentCsv();
        Check(wallCsv.Contains("101") && wallCsv.Contains("102") && !wallCsv.Contains("201"), "CSV must use current filtered elements, not the full snapshot.");
        Pick(Field<ComboBox>(window, "kindPicker"), ""); Pick(Field<ComboBox>(window, "levelPicker"), "L2");
        Check(quantities.Items.Count == 2, "Original-level filter must use stable level IDs.");
        Pick(Field<ComboBox>(window, "levelPicker"), ""); Field<CheckBox>(window, "issuesOnly").IsChecked = true;
        Check(quantities.Items.Count == 3, "Issues-only filter must expose missing, invalid, and unsupported records.");
        Field<CheckBox>(window, "issuesOnly").IsChecked = false; Pick(Field<ComboBox>(window, "groupPicker"), "kind");
        Check(quantities.Items.Count == 4, "Kind grouping must retain a distinct unsupported category.");
        object wallGroup = Rows(quantities).First(r => (string)Property(r, "KindName") == "普通墙");
        quantities.SelectedItem = wallGroup;
        Check(Field<Button>(window, "focusButton").IsEnabled, "Selecting a source-backed quantity group must enable the actual focus action.");
        Check(window.PrepareFocus(), "A selected group must produce focus candidates.");
        Check(window.SelectedFocusElements.Count == 2 && window.SelectedFocusElements.All(e => e.UniqueId.StartsWith("unique-")), "Group focus must return all original source records with stable IDs.");
        Check(!window.IsVisible, "Preparing focus must not perform desktop input or show a window.");

        Pick(Field<ComboBox>(window, "phasePicker"), "p2");
        Check(!Field<Button>(window, "csvButton").IsEnabled && !Field<Button>(window, "focusButton").IsEnabled, "Changed phase must stale both actions immediately.");
        Throws(delegate { window.BuildCurrentCsv(); }, "A stale phase must not export data from the previous phase.");
        string preservedCsv = Path.Combine(output, "existing-civil.csv");
        File.WriteAllText(preservedCsv, "Existing approved CSV must survive a failed export.", Encoding.UTF8);
        byte[] preservedBytes = File.ReadAllBytes(preservedCsv);
        Throws(delegate { window.ExportCsv(preservedCsv); }, "A stale CSV generation must fail before replacing an existing file.");
        Check(preservedBytes.SequenceEqual(File.ReadAllBytes(preservedCsv)), "A failed CSV generation must preserve the exact existing file bytes.");
        window.RefreshSnapshot();
        Check(lastRequest.PhaseId == "p2" && window.CheckFreshness(), "Refresh must use the newly selected phase.");
        Pick(Field<ComboBox>(window, "optionPicker"), "o1");
        Check(!window.CheckFreshness(), "Changed design option must stale the existing result.");
        window.RefreshSnapshot(); Check(lastRequest.DesignOptionId == "o1", "Explicit design option must reach the reader.");
        Pick(Field<ComboBox>(window, "scopePicker"), "selection"); window.RefreshSnapshot();
        Check(lastRequest.Scope == "selection", "Selection scope must reach the reader.");
        Pick(Field<ComboBox>(window, "scopePicker"), "project"); window.RefreshSnapshot();
        Check(lastRequest.Scope == "project", "Project scope must reach the reader.");
        revision++;
        Check(!window.CheckFreshness(), "Model revision changes must stale a current result.");
        Throws(delegate { window.BuildCurrentCsv(); }, "Outdated model results must not export as current.");
        window.RefreshSnapshot(); Check(window.CheckFreshness(), "Successful reread must recover from a changed model revision.");
        CivilSnapshot previous = window.LastSnapshot; failRead = true; window.RefreshSnapshot();
        Check(Object.ReferenceEquals(previous, window.LastSnapshot), "A failed refresh must not replace a completed snapshot with partial data.");
        Check(!Field<Button>(window, "csvButton").IsEnabled && Field<TextBlock>(window, "status").Text.Contains("模拟原生读取失败"), "A failed refresh must expose failure and block CSV.");
        failRead = false; window.RefreshSnapshot(); Check(window.CheckFreshness(), "A later successful refresh must recover from a read failure.");

        Pick(Field<ComboBox>(window, "groupPicker"), "element");
        string csvPath = Path.Combine(output, "civil-ui-current.csv"); window.ExportCsv(csvPath);
        byte[] bytes = File.ReadAllBytes(csvPath);
        Check(bytes.Length > 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf, "CSV file must include the UTF-8 BOM.");
        Check(File.ReadAllText(csvPath, Encoding.UTF8) == window.BuildCurrentCsv(), "File content must equal the domain's current-screen CSV result.");
        window.ExportCsv(preservedCsv);
        Check(File.ReadAllText(preservedCsv, Encoding.UTF8) == window.BuildCurrentCsv(), "A completed export must atomically replace a previously existing CSV.");
        Check(Directory.GetFiles(output, ".*.bright-civil-*.tmp").Length == 0, "Completed and refused CSV exports must leave no owned temporary files.");
        Render(window, Path.Combine(output, "BV-civil-quantities.png"));
        Field<TabControl>(window, "tabs").SelectedIndex = 1;
        object excludedProblem = Rows(problems).First(r => (string)Property(r, "ElementId") == "901");
        problems.SelectedItem = excludedProblem;
        Check(!window.PrepareFocus(), "A scope exclusion without a stable source identity must not invent a focus target.");
        object actualProblem = Rows(problems).First(r => (string)Property(r, "ElementId") == "102");
        problems.SelectedItem = actualProblem;
        Check(Field<Button>(window, "focusButton").IsEnabled, "Selecting a source-backed issue must enable the actual focus action.");
        Check(window.PrepareFocus() && window.SelectedFocusElements.Count == 1 && window.SelectedFocusElements[0].ElementId == "102", "A record-backed problem must return the correct source element for focus.");
        Render(window, Path.Combine(output, "BV-civil-problems.png"));

        TestProjectWorkflow(window, output);

        int beforeInvalid = reads;
        CivilQuantityWindow unspecified = new CivilQuantityWindow(new CivilReadRequest { Scope = "project", PhaseId = "unknown", DesignOptionId = "-1" }, phases, options, reader, delegate { return revision; });
        unspecified.RefreshSnapshot();
        Check(reads == beforeInvalid && !Field<Button>(unspecified, "csvButton").IsEnabled, "An unavailable phase must require explicit selection rather than silently choose one.");
        Check(!window.IsVisible && !unspecified.IsVisible, "All fixture windows must remain offscreen.");
        File.WriteAllText(Path.Combine(output, "civil-ui-assertions.json"), "{\"status\":\"PASS\",\"assertions\":" + checks.ToString(CultureInfo.InvariantCulture) + ",\"scope\":\"Production domain and WPF rendered offscreen; no shown window, Revit API, or desktop input.\"}", new UTF8Encoding(false));
        Console.WriteLine("Passed " + checks + " civil quantity UI assertions; rendered actual WPF content offscreen, with no shown window or Revit API access.");
    }
}
