using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace Retrace.AutoCad;

/// <summary>
/// A WPF palette, in process, from its visual tree: its fields (the same kinds and labels <see cref="DialogReader"/> gives a dialog), its push
/// buttons, the button under a click, and for replay setting a field and pressing a button the way WPF does it (bindings updated, commands
/// run). UI Automation would deadlock here: these windows belong to the UI thread that is asking. UI thread only.
/// </summary>
static class WpfPalette
{
    const int MaxElements = 3000;

    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);

    /// <summary>What one pass over the visual tree found: the fields, the element behind each, and the enabled push buttons (text, element).</summary>
    internal sealed class Scan
    {
        public List<DialogField> Fields = new();
        public List<FrameworkElement> Elements = new();
        public List<(ButtonBase Button, string Text)> Buttons = new();
    }

    /// <summary>The first WPF host window (HwndWrapper) under <paramref name="page"/>, or zero.</summary>
    public static IntPtr Host(IntPtr page)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(page, (h, _) =>
        {
            if (!DialogReader.ClassOf(h).StartsWith("HwndWrapper", StringComparison.Ordinal)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The fields of the WPF content under <paramref name="page"/>; empty if there is none. Never throws.</summary>
    public static List<DialogField> Read(IntPtr page) => ScanPage(page).Fields;

    /// <summary>Fields and buttons of the WPF content under <paramref name="page"/>. <paramref name="hiddenToo"/>: also while the palette is
    /// hidden (auto-hide), going by each element's own Visibility instead of whether it is on screen. Never throws.</summary>
    internal static Scan ScanPage(IntPtr page, bool hiddenToo = false, bool disabledToo = false)
    {
        var scan = new Scan();
        var raw = new List<RawControl>();
        var rawEl = new List<FrameworkElement>();
        try
        {
            var host = Host(page);
            if (host == IntPtr.Zero || HwndSource.FromHwnd(host)?.RootVisual is not DependencyObject root) return scan;
            int seen = 0;
            Walk(root, raw, rawEl, scan.Buttons, hiddenToo, disabledToo, ref seen);
        }
        catch (Exception ex) { Log.Error("palette fields (wpf)", ex); }
        foreach (var (f, idx) in DialogFieldsCore.BuildMapped(raw)) { scan.Fields.Add(f); scan.Elements.Add(rawEl[idx[0]]); }
        return scan;
    }

    static void Walk(DependencyObject d, List<RawControl> raw, List<FrameworkElement> rawEl, List<(ButtonBase, string)> buttons, bool hiddenToo, bool disabledToo, ref int seen)
    {
        if (++seen > MaxElements) { if (seen == MaxElements + 1) Log.Info($"palette fields (wpf): stopped after {MaxElements} elements; later fields are not recorded"); return; }
        if (d is UIElement u && (hiddenToo ? u.Visibility != Visibility.Visible : !u.IsVisible)) return;
        if (Field(d) is { } f) { raw.Add(f); rawEl.Add((FrameworkElement)d); if (d is ComboBox or ListBox) return; }   // their own parts are part of them
        if (d is ButtonBase b and not ToggleButton && (disabledToo || b.IsEnabled)) buttons.Add((b, ButtonText(b)));
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), raw, rawEl, buttons, hiddenToo, disabledToo, ref seen);
    }

    static RawControl? Field(DependencyObject d)
    {
        string kind, value;
        switch (d)
        {
            case TextBox t when !t.IsReadOnly && t.TemplatedParent is not ComboBox:
                kind = "edit"; value = t.Text; break;
            case CheckBox c:
                kind = "check"; value = c.IsChecked switch { true => "on", false => "off", _ => "mixed" }; break;
            case RadioButton r:
                kind = "radio1"; value = r.IsChecked == true ? "on" : "off"; break;
            case ComboBox c:
                kind = "combo"; value = c.IsEditable ? c.Text : c.SelectedItem == null ? "" : ItemText(c, c.SelectedItem); break;
            case ListBox l when l.SelectionMode == SelectionMode.Single:
                kind = "list"; value = l.SelectedItem == null ? "" : ItemText(l, l.SelectedItem); break;
            case Slider s:
                kind = "slider"; value = s.Value.ToString(CultureInfo.InvariantCulture); break;
            default:
                return null;
        }
        var e = (FrameworkElement)d;
        var label = DialogFieldsCore.CleanLabel(LabelOf(e));
        // x:Name stays the same between runs: the key; the label is what a person reads
        return new RawControl(0, label.Length > 0 ? label : "(unnamed)", kind, value, Name: string.IsNullOrEmpty(e.Name) ? null : e.Name);
    }

    /// <summary>What an item of a list or combo shows: a ComboBoxItem / ListBoxItem's content, the text its container shows (a data template),
    /// else the item's own text.</summary>
    static string ItemText(ItemsControl c, object item)
    {
        if (item is ContentControl cc && Content(cc.Content) is { } own) return own;
        if (c.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject box && FirstText(box) is { Length: > 0 } shown) return shown;
        // no container (a drop-down never opened, an item scrolled out of a virtualizing list): show the item through its template off screen
        if (c.ItemTemplate != null || c.ItemTemplateSelector != null)
        {
            try
            {
                var cp = new ContentPresenter { Content = item, ContentTemplate = c.ItemTemplate, ContentTemplateSelector = c.ItemTemplateSelector };
                cp.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (FirstText(cp) is { Length: > 0 } t) return t;
            }
            catch (Exception ex) { Log.Error("palette item text (wpf)", ex); }
        }
        if (!string.IsNullOrEmpty(c.DisplayMemberPath) && item.GetType().GetProperty(c.DisplayMemberPath)?.GetValue(item) is { } member) return member.ToString() ?? "";
        return item.ToString() ?? "";
    }

    static string? FirstText(DependencyObject d)
    {
        if (d is TextBlock tb && tb.Text.Length > 0) return tb.Text;
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++) if (FirstText(VisualTreeHelper.GetChild(d, i)) is { } t) return t;
        return null;
    }

    /// <summary>What a person reads next to the control: its automation name, its own text (check / radio), a Label that targets it, else the
    /// Label or TextBlock just before it in its panel, else its x:Name.</summary>
    static string? LabelOf(FrameworkElement e)
    {
        var name = AutomationProperties.GetName(e);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (e is ToggleButton t && Content(t.Content) is { Length: > 0 } own) return own;
        if (e.Parent is Panel p)
        {
            foreach (var c in p.Children.OfType<Label>()) if (ReferenceEquals(c.Target, e)) return Content(c.Content);
            int i = p.Children.IndexOf(e);
            for (int j = i - 1; j >= 0; j--)
            {
                var prev = p.Children[j];
                if (prev is Label l) return Content(l.Content);
                if (prev is TextBlock tb) return tb.Text;
                if (prev is Control) break;   // another field: no label of ours above it
            }
        }
        return e.Name;
    }

    static string? Content(object? content) => content switch
    {
        string s => s,
        TextBlock tb => tb.Text,
        AccessText at => at.Text,
        null => null,
        _ => content.ToString(),
    };

    static string ButtonText(ButtonBase b)
    {
        var text = DialogFieldsCore.CleanLabel(AutomationProperties.GetName(b) is { Length: > 0 } n ? n : Content(b.Content) ?? b.Name);
        return text.Length > 0 ? text : "(button)";
    }

    /// <summary>The text of the push button under a click at <paramref name="lParam"/> (client coordinates of <paramref name="hwnd"/>), or null
    /// if <paramref name="hwnd"/> is not WPF or the click is not on a push button (check boxes and radios are fields).</summary>
    public static string? ButtonAt(IntPtr hwnd, IntPtr lParam)
    {
        long lp = lParam.ToInt64();
        return HitButton(hwnd, new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)), out var b) ? ButtonText(b!) : null;
    }

    /// <summary>Which of the page's enabled push buttons with the same text the one under the click is (1 = the first).</summary>
    public static int ButtonIndexAt(IntPtr page, IntPtr hwnd, IntPtr lParam)
    {
        long lp = lParam.ToInt64();
        if (!HitButton(hwnd, new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)), out var b)) return 1;
        var all = ScanPage(page).Buttons;
        var at = all.FindIndex(x => ReferenceEquals(x.Button, b));
        if (at < 0) return 1;
        var text = all[at].Text;
        return all.Take(at + 1).Count(x => x.Text == text);
    }

    static bool HitButton(IntPtr hwnd, Point devicePx, out ButtonBase? button)
    {
        button = null;
        try
        {
            if (HwndSource.FromHwnd(hwnd) is not { RootVisual: UIElement root } src) return false;
            var pt = src.CompositionTarget?.TransformFromDevice.Transform(devicePx) ?? devicePx;
            for (var d = root.InputHitTest(pt) as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            {
                if (d is ToggleButton) return false;
                if (d is ButtonBase b) { if (!b.IsEnabled) return false; button = b; return true; }
                if (d is not Visual and not System.Windows.Media.Media3D.Visual3D) break;
            }
        }
        catch (Exception ex) { Log.Error("palette click (wpf)", ex); }
        return false;
    }

    /// <summary>The element under a point on the screen inside <paramref name="hwnd"/> (a WPF host), walked up to the nearest field or button;
    /// null if none.</summary>
    internal static FrameworkElement? ElementAt(IntPtr hwnd, Point screenPx)
    {
        try
        {
            if (HwndSource.FromHwnd(hwnd) is not { RootVisual: UIElement root } src) return null;
            var pt = root.PointFromScreen(screenPx);
            for (var d = root.InputHitTest(pt) as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            {
                if (d is FrameworkElement fe && (Field(fe) != null || fe is ButtonBase)) return fe;
                if (d is not Visual and not System.Windows.Media.Media3D.Visual3D) break;
            }
        }
        catch (Exception ex) { Log.Error("palette element at (wpf)", ex); }
        return null;
    }

    /// <summary>true if <paramref name="hwnd"/> hosts a WPF popup (a combo box's drop-down, a context menu, a tooltip).</summary>
    internal static bool IsPopup(IntPtr hwnd)
    {
        try { return HwndSource.FromHwnd(hwnd)?.RootVisual?.GetType().Name == "PopupRoot"; }
        catch (Exception) { return false; }
    }

    /// <summary>Screen rectangle of an element, for distances to the cursor; null if it is not on screen.</summary>
    internal static Rect? ScreenRect(FrameworkElement e)
    {
        try
        {
            if (!e.IsVisible || PresentationSource.FromVisual(e) == null) return null;
            var a = e.PointToScreen(new Point(0, 0));
            var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
            return new Rect(a, b);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Sets one recorded change on a WPF field the way a person would leave it: the property set and its binding pushed to the source at
    /// once (a TextBox only updates its source when it loses focus). Null when done, else why not.</summary>
    internal static string? Apply(FrameworkElement e, DialogField live, FieldChange c)
    {
        try
        {
            switch (e)
            {
                case TextBox t:
                    t.Text = c.After;
                    BindingOperations.GetBindingExpression(t, TextBox.TextProperty)?.UpdateSource();
                    return null;
                case CheckBox cb:
                {
                    bool? want = c.After switch { "on" => true, "off" => false, _ => null };
                    // toggled the way a click does (its Command and Click handlers run), at most a full three-state cycle; set directly if that fails
                    if (UIElementAutomationPeer.CreatePeerForElement(cb)?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle && cb.IsEnabled)
                        for (int i = 0; i < 3 && cb.IsChecked != want; i++) toggle.Toggle();
                    if (cb.IsChecked != want) cb.IsChecked = want;
                    return null;
                }
                case RadioButton rb:
                    if (c.After == "on") rb.IsChecked = true;
                    return null;
                case ComboBox combo:
                {
                    var want = FillMatcher.Expected(c);
                    foreach (var item in combo.Items)
                        if (FillMatcher.ValueMatches("combo", want, ItemText(combo, item))) { combo.SelectedItem = item; return null; }
                    if (!combo.IsEditable) return $"no entry \"{want}\" in the list";
                    combo.Text = want;
                    BindingOperations.GetBindingExpression(combo, ComboBox.TextProperty)?.UpdateSource();
                    return null;
                }
                case ListBox list:
                {
                    var want = FillMatcher.Expected(c);
                    foreach (var item in list.Items)
                        if (FillMatcher.ValueMatches("list", want, ItemText(list, item))) { list.SelectedItem = item; list.ScrollIntoView(item); return null; }
                    return $"no entry \"{want}\" in the list";
                }
                case Slider s:
                    if (!double.TryParse(c.After, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return $"\"{c.After}\" is not a number";
                    s.Value = v;
                    return null;
                default:
                    return $"cannot set a {live.Kind}";
            }
        }
        catch (Exception ex) { Log.Error("palette fill (wpf)", ex); return ex.Message; }
    }

    /// <summary>Presses a WPF button the way a click does (its automation peer: Click handlers and its Command run). Works while the palette is
    /// hidden. False if it could not.</summary>
    internal static bool Press(ButtonBase b)
    {
        try
        {
            if (!b.IsEnabled) return false;   // a CanExecute requery may have disabled it since the scan
            if (UIElementAutomationPeer.CreatePeerForElement(b)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) { invoke.Invoke(); return true; }
            b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // no peer: at least its Click handlers
            return true;
        }
        catch (Exception ex) { Log.Error("palette press (wpf)", ex); return false; }
    }
}
