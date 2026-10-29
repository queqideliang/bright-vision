// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrightVision.Civil;
using BrightVision.Revit;

internal static class CivilWallRuleUiFixture
{
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
    private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner); }
    private static void Refuses(Action action, string message)
    {
        bool refused = false;
        try { action(); }
        catch (InvalidDataException) { refused = true; }
        catch (ArgumentException) { refused = true; }
        Check(refused, message);
    }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void Select(CivilWallRuleWindow window, string policy)
    {
        ComboBox box = Field<ComboBox>(window, "openingPolicy");
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == policy);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Fits(FrameworkElement element, FrameworkElement root, int width, int height, string label)
    {
        Point point = element.TranslatePoint(new Point(0, 0), root);
        Check(element.ActualWidth > 20 && element.ActualHeight >= 18 && point.X >= 0 && point.Y >= 0 &&
            point.X + element.ActualWidth <= width + .1 && point.Y + element.ActualHeight <= height + .1, label + " remains inside the offscreen client area.");
    }
    private static void Render(CivilWallRuleWindow window, string output, string name, int width, int height)
    {
        FrameworkElement root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write)) encoder.Save(file);
        Fits(Field<Button>(window, "applyButton"), root, width, height, "Apply button");
        Fits(Field<Button>(window, "cancelButton"), root, width, height, "Cancel button");
        Check(!window.IsVisible, "Offscreen rendering must never show the editor.");
    }
    private static int Main(string[] args)
    {
        if (args == null || args.Length != 1) throw new ArgumentException("A new test output directory is required.");
        Exception failure = null;
        var thread = new Thread(delegate() { try { Run(args[0]); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new InvalidOperationException("Wall rule editor fixture failed.", failure);
        return 0;
    }
    private static void Run(string output)
    {
        CivilWallRuleTemplate builtin = CivilWallRuleTemplates.Default();
        string builtinBefore = CivilWallRuleTemplates.Fingerprint(builtin);
        var editor = new CivilWallRuleWindow(builtin);
        Check(!editor.IsVisible && editor.Result == null && editor.Icon != null, "Constructing an editor neither displays UI nor accepts a result.");
        Check(editor.Width <= 800 && editor.Height <= 650, "Default window fits a 1080-high desktop without requiring fullscreen.");
        CivilWallRuleTemplate detached = editor.BuildTemplate();
        Guid copiedId;
        Check(Guid.TryParse(detached.TemplateId, out copiedId) && detached.TemplateId != builtin.TemplateId && detached.Revision == 1, "An internal default is copied to an independent template identity at revision one.");
        Check(editor.Result == null && editor.BuildTemplate().TemplateId == detached.TemplateId, "Building is repeatable for this draft and does not imply modal acceptance.");
        Check(!Object.ReferenceEquals(detached, builtin) && !Object.ReferenceEquals(detached.Priorities, builtin.Priorities), "The editor and returned data do not share mutable state with the input.");
        Check(CivilWallRuleTemplates.Fingerprint(builtin) == builtinBefore, "Constructing and building a copy leave the internal default unchanged.");

        foreach (string name in new[] { "templateId", "revision", "volumeMethod", "areaMethod", "joinPolicy" })
            Check(Field<TextBox>(editor, name).IsReadOnly, "Unsupported/identity field is read-only: " + name);
        Check(Field<ComboBox>(editor, "openingPolicy").Items.Count == 2 && !Field<ComboBox>(editor, "openingPolicy").IsEditable, "Only the two implemented opening policies are available.");
        Check(Field<TextBox>(editor, "joinPolicy").Text.Contains("reject-overlap"), "Overlap refusal is explicit and offers no priority controls.");

        Field<TextBox>(editor, "templateName").Text = "崛亮视觉 · 一期墙体核对";
        Field<TextBox>(editor, "description").Text = "项目约定：真实矩形洞口\r\n复核后使用，不是地区清单或定额。";
        Field<TextBox>(editor, "absoluteTolerance").Text = "2.5e-9";
        Field<TextBox>(editor, "relativeTolerance").Text = "5e-9";
        Select(editor, CivilWallRuleTemplates.NativeOpeningOnly);
        CivilWallRuleTemplate edited = editor.BuildTemplate();
        Check(edited.Name == "崛亮视觉 · 一期墙体核对" && edited.Description.Contains("\r\n"), "Chinese names and multiline project notes survive the real controls.");
        Check(edited.AbsoluteToleranceMetres3 == 2.5e-9 && edited.RelativeTolerance == 5e-9 && edited.OpeningPolicy == CivilWallRuleTemplates.NativeOpeningOnly, "Scientific tolerances and selected opening policy reach the built template.");
        Check(edited.MethodId == "BV.Wall.RectSolid/1" && edited.AreaMethodId == "BV.Wall.AxisProjection/1" && edited.JoinPolicy == "reject-overlap" && edited.Priorities.Count == 0, "Only the implemented geometry methods and overlap policy can be built.");
        Render(editor, output, "BV-wall-rule-editor-client-780x610.png", 780, 610);
        var visibleLabels = Descendants((DependencyObject)editor.Content).OfType<TextBlock>().Select(block => block.Text).ToList();
        Check(visibleLabels.Any(text => text.Contains("崛亮视觉")) && visibleLabels.Any(text => text.Contains("并非地区清单")), "Company attribution and project-convention scope are visible in the actual content.");

        CivilWallRuleTemplates.Save(Path.Combine(output, "chinese-edited.bvwallrule.json"), edited);
        CivilWallRuleTemplate reopened = CivilWallRuleTemplates.Load(Path.Combine(output, "chinese-edited.bvwallrule.json"));
        Check(CivilWallRuleTemplates.Fingerprint(edited) == CivilWallRuleTemplates.Fingerprint(reopened), "The actual built template round-trips through the production JSON store.");

        string validName = Field<TextBox>(editor, "templateName").Text;
        Field<TextBox>(editor, "templateName").Text = "   ";
        Refuses(delegate { editor.BuildTemplate(); }, "Whitespace-only name must be rejected.");
        Click(Field<Button>(editor, "applyButton"));
        Check(editor.Result == null && !String.IsNullOrEmpty(Field<TextBlock>(editor, "message").Text) && !editor.IsVisible, "Invalid confirmation remains offscreen and writes an inline error without accepting a result.");
        Render(editor, output, "BV-wall-rule-editor-validation-client-780x610.png", 780, 610);
        Field<TextBox>(editor, "templateName").Text = validName;

        foreach (string invalid in new[] { "", "NaN", "Infinity", "0", "-1e-9", "1e-7", "1e309", "invalid", "1e-400" })
        {
            Field<TextBox>(editor, "absoluteTolerance").Text = invalid;
            Refuses(delegate { editor.BuildTemplate(); }, "Reject invalid or relaxed absolute tolerance: " + invalid);
        }
        Field<TextBox>(editor, "absoluteTolerance").Text = "1e-8";
        foreach (string invalid in new[] { "NaN", "-Infinity", "0", "1.00001e-8" })
        {
            Field<TextBox>(editor, "relativeTolerance").Text = invalid;
            Refuses(delegate { editor.BuildTemplate(); }, "Reject invalid or relaxed relative tolerance: " + invalid);
        }
        Field<TextBox>(editor, "relativeTolerance").Text = "1e-8";
        Field<ComboBox>(editor, "openingPolicy").SelectedIndex = -1;
        Refuses(delegate { editor.BuildTemplate(); }, "A missing opening policy must not silently become the default.");
        var unsupported = new ComboBoxItem { Content = "未实现策略", Tag = "unsupported-nominal-door-size" };
        Field<ComboBox>(editor, "openingPolicy").Items.Add(unsupported); Field<ComboBox>(editor, "openingPolicy").SelectedItem = unsupported;
        Refuses(delegate { editor.BuildTemplate(); }, "An unsupported strategy injected into the normal picker is rejected by domain validation.");
        Select(editor, CivilWallRuleTemplates.VerifiedRectCutout);
        Check(editor.BuildTemplate().AbsoluteToleranceMetres3 == 1e-8, "Global tolerance boundary remains accepted after validation errors.");
        Click(Field<Button>(editor, "applyButton"));
        Check(editor.Result != null && editor.DialogResult == null && String.IsNullOrEmpty(Field<TextBlock>(editor, "message").Text), "Valid offscreen confirmation accepts data without setting Window.DialogResult or showing a window.");
        Check(CivilWallRuleTemplates.Fingerprint(builtin) == builtinBefore, "Neither successful nor failed confirmation mutates the internal default.");
        Click(Field<Button>(editor, "cancelButton"));
        Check(editor.Result == null && CivilWallRuleTemplates.Fingerprint(builtin) == builtinBefore, "Cancel discards only the local result and preserves the original.");
        editor.Close();

        var custom = CivilWallRuleTemplates.Copy(builtin, "我方项目模板"); custom.Revision = 7;
        custom.AbsoluteToleranceMetres3 = 1e-10; custom.RelativeTolerance = 1e-10;
        string customBefore = CivilWallRuleTemplates.Fingerprint(custom);
        var customEditor = new CivilWallRuleWindow(custom);
        Check(CivilWallRuleTemplates.Fingerprint(customEditor.BuildTemplate()) == customBefore, "Unchanged custom template keeps identity, revision and content fingerprint.");
        Field<TextBox>(customEditor, "description").Text += "\n本工程补充说明。";
        CivilWallRuleTemplate revised = customEditor.BuildTemplate();
        Check(revised.TemplateId == custom.TemplateId && revised.Revision == 8 && revised.Description != custom.Description, "A change to a loaded custom template increments revision under the same identity.");
        Check(customEditor.BuildTemplate().Revision == 8, "Repeated builds never keep incrementing a single edit.");
        Field<TextBox>(customEditor, "absoluteTolerance").Text = "1e-9";
        Check(customEditor.BuildTemplate().AbsoluteToleranceMetres3 == 1e-9 && customEditor.BuildTemplate().Revision == 8, "A previously tight template may adjust within the unchanged engine maximum.");
        Field<TextBox>(customEditor, "description").Text = custom.Description;
        Field<TextBox>(customEditor, "absoluteTolerance").Text = "1e-10";
        Check(CivilWallRuleTemplates.Fingerprint(customEditor.BuildTemplate()) == customBefore, "Reverting all edits restores the original revision.");
        Field<TextBox>(customEditor, "templateName").Text = "不保存的变更";
        Click(Field<Button>(customEditor, "cancelButton")); customEditor.Close();
        Check(CivilWallRuleTemplates.Fingerprint(custom) == customBefore, "Cancel after editing a loaded custom template does not modify the caller.");

        var ceiling = CivilWallRuleTemplates.Copy(builtin, "最高修订"); ceiling.Revision = Int32.MaxValue;
        var ceilingEditor = new CivilWallRuleWindow(ceiling);
        Check(ceilingEditor.BuildTemplate().Revision == Int32.MaxValue, "An unchanged maximum revision remains readable.");
        Field<TextBox>(ceilingEditor, "description").Text += "变更";
        Refuses(delegate { ceilingEditor.BuildTemplate(); }, "Revision overflow must reject the edit.");
        Click(Field<Button>(ceilingEditor, "applyButton"));
        Check(ceilingEditor.Result == null && Field<TextBlock>(ceilingEditor, "message").Text.Contains("修订号"), "Revision overflow remains an inline validation failure.");
        ceilingEditor.Close();

        var legacy = CivilWallRuleTemplates.LegacyExplicitOpeningOnly();
        var legacyEditor = new CivilWallRuleWindow(legacy);
        Check(legacyEditor.BuildTemplate().TemplateId != legacy.TemplateId && legacyEditor.BuildTemplate().OpeningPolicy == CivilWallRuleTemplates.NativeOpeningOnly, "Legacy internal template also copies with its explicit opening restriction.");
        legacyEditor.Close();
        var lookalike = CivilWallRuleTemplates.Copy(builtin, "名称不是内置身份"); lookalike.TemplateId = "BV.Custom.ProjectTemplate";
        var lookalikeEditor = new CivilWallRuleWindow(lookalike);
        Check(lookalikeEditor.BuildTemplate().TemplateId == lookalike.TemplateId, "A user template starting with BV is not confused with the two exact internal identities.");
        lookalikeEditor.Close();

        CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var cultureEditor = new CivilWallRuleWindow(custom);
            Field<TextBox>(cultureEditor, "absoluteTolerance").Text = "2.5e-9";
            Check(cultureEditor.BuildTemplate().AbsoluteToleranceMetres3 == 2.5e-9, "Scientific notation uses the documented decimal point independently of system locale.");
            cultureEditor.Close();
        }
        finally { Thread.CurrentThread.CurrentCulture = previousCulture; }

        File.WriteAllText(Path.Combine(output, "report.json"), "{\"result\":\"PASS\",\"assertions\":" + checks.ToString(CultureInfo.InvariantCulture) + ",\"scope\":\"Production BV-owned rule template editor, controls and JSON store; offscreen WPF only\",\"uiShown\":false,\"nativeDesktopInput\":false,\"revitStarted\":false,\"templateScope\":\"BV project conventions, no commercial template content\"}");
        Console.WriteLine("PASS " + checks + " rule template UI assertions.");
    }
}
