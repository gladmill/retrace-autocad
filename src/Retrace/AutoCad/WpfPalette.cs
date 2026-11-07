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
using Retrace.Core;

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
        DependencyObject? _root;
        bool _hiddenToo;
        internal void SetRoot(DependencyObject root, bool hiddenToo) { _root = root; _hiddenToo = hiddenToo; }

        /// <summary>The parts of the window as it is at the time of the call, in a pruned pass of its own over the scan's root (capped at <see cref="MaxElements"/>; it does not look at what the scan saw): the tab controls, trees,
        /// grids and lists Retrace does not follow, and with <paramref name="statics"/> the static texts too. Call it when the answer is wanted: a press, the close, a replay segment's end, about once a second while recording
        /// (parts only), and from replay ticks only at a segment's end or for a field that is not found; never in a palette's scan or a plain replay poll. Never throws.</summary>
        public Parts ReadParts(bool statics = true)
        {
            var ctx = new WalkContext(_root, _hiddenToo, false, fields: false, texts: statics, parts: true);
            try { if (_root != null) Walk(_root, ctx); }
            catch (Exception ex) { Log.Error("dialog parts (wpf)", ex); }
            return new Parts(DialogStatics.Build(ctx.Texts, stripMnemonics: false), ctx.Unsupported);   // "&" is literal in WPF
        }
    }

    /// <summary>The static texts (the visible TextBlocks and Labels that are not part of a control, and read-only text boxes, in tree order, control id 0 (WPF has none), "&amp;" kept) and the tab controls, trees, grids and
    /// lists with a selection that is no field (<see cref="WpfPresses.UnsupportedNote"/>).</summary>
    internal sealed record Parts(List<DialogStatic> Statics, List<WpfPresses.Unsupported> Unsupported);

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
    internal static Scan ScanPage(IntPtr page, bool hiddenToo = false, bool disabledToo = false) => ScanHost(Host(page), hiddenToo, disabledToo);

    /// <summary>Fields and buttons of the WPF window <paramref name="host"/> itself (a dialog's HwndWrapper, or the one <see cref="Host"/> found). Never throws.</summary>
    internal static Scan ScanHost(IntPtr host, bool hiddenToo = false, bool disabledToo = false)
    {
        var scan = new Scan();
        WalkContext? ctx = null;
        try
        {
            if (host == IntPtr.Zero || HwndSource.FromHwnd(host)?.RootVisual is not DependencyObject root) return scan;
            scan.SetRoot(root, hiddenToo);
            ctx = new WalkContext(root, hiddenToo, disabledToo, fields: true, texts: false, parts: false);   // the root lets LogCap say it once per window
            Walk(root, ctx);
        }
        catch (Exception ex) { Log.Error("palette fields (wpf)", ex); }
        if (ctx == null) return scan;
        scan.Buttons = ctx.Buttons;
        foreach (var (f, idx) in DialogFieldsCore.BuildMapped(ctx.Raw)) { scan.Fields.Add(f); scan.Elements.Add(ctx.RawEl[idx[0]]); }
        return scan;
    }

    /// <summary>What one walk over the visual tree looks for (fields and buttons, static texts, parts that are not followed: any mix of the three) and carries: the findings, the options and the element count. A walk
    /// for texts or parts only is pruned below controls that own their texts; one for fields goes through everything but the parts of a combo or list box.</summary>
    sealed class WalkContext
    {
        public readonly bool HiddenToo, DisabledToo, Fields, WantTexts, WantParts;
        public readonly List<RawControl> Raw = new();
        public readonly List<FrameworkElement> RawEl = new();
        public List<(ButtonBase Button, string Text)> Buttons = new();
        public readonly List<(int, string?)> Texts = new();
        public readonly List<WpfPresses.Unsupported> Unsupported = new();
        /// <summary>The tab page the walk is in (the selected pages' headers of the tab controls above; parts walks only): the page of an unnamed part.</summary>
        public string Page = "";
        public readonly DependencyObject? Root;
        public int Seen;
        public WalkContext(DependencyObject? root, bool hiddenToo, bool disabledToo, bool fields, bool texts, bool parts) { Root = root; HiddenToo = hiddenToo; DisabledToo = disabledToo; Fields = fields; WantTexts = texts; WantParts = parts; }
    }

    static void Walk(DependencyObject d, WalkContext x)
    {
        if (++x.Seen > MaxElements) { if (x.Seen == MaxElements + 1) LogCap(x); return; }
        if (d is UIElement u && (x.HiddenToo ? u.Visibility != Visibility.Visible : !u.IsVisible)) return;
        if (x.WantParts && UnsupportedKind(d) is { } kind)
            x.Unsupported.Add(new WpfPresses.Unsupported(kind, (d is FrameworkElement { Name.Length: > 0 } fe ? fe.Name : AutomationProperties.GetName(d)) ?? "", x.Page));
        if (x.Fields)
        {
            if (Field(d) is { } f) { x.Raw.Add(f); x.RawEl.Add((FrameworkElement)d); if (d is ComboBox or ListBox) return; }   // their own parts are part of them
            if (d is ButtonBase b && IsPushButton(b) && (x.DisabledToo || b.IsEnabled)) x.Buttons.Add((b, ButtonText(b)));
        }
        else
        {
            if (x.WantTexts && d is TextBlock { Text.Length: > 0 } tb) { x.Texts.Add((0, tb.Text)); return; }
            if (x.WantTexts && d is TextBox { IsReadOnly: true } ro) { x.Texts.Add((0, ro.Text)); return; }   // its template's texts (a placeholder) are not the window's
            if (d is Control c && OwnsTexts(c.GetType())) return;   // a button, an item, a combo box: its texts are its own, and there is nothing of ours below
        }
        var page = x.Page;
        if (x.WantParts && d is TabControl tabs) x.Page = page + ">" + PageName(tabs);
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), x);
        x.Page = page;
    }

    /// <summary>The selected page of a tab control by its header text (a string or a TextBlock, <see cref="TabPath.Clean"/>), else by its index (a non-string header, a bound control).</summary>
    static string PageName(TabControl tabs)
    {
        var text = tabs.SelectedItem switch
        {
            TabItem { Header: string s } => s,
            TabItem { Header: TextBlock tb } => tb.Text,
            _ => null,
        };
        return TabPath.Clean(text) is { Length: > 0 } clean ? clean : "#" + tabs.SelectedIndex;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, HashSet<string>> CapLogged = new();
    /// <summary>The walk stopped at <see cref="MaxElements"/>: said once per window and kind of walk, whoever asked.</summary>
    static void LogCap(WalkContext x)
    {
        var kind = x.Fields ? "fields" : "parts/statics";
        if (x.Root != null && !CapLogged.GetOrCreateValue(x.Root).Add(kind)) return;
        Log.Info($"palette {kind} (wpf): stopped after {MaxElements} elements; later ones are not read");
    }

    /// <summary>The type names of <paramref name="o"/>, most derived first (one helper for the rules that go by type name).</summary>
    static List<string> Chain(object o) => Chain(o.GetType());

    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> OwnsCache = new();
    /// <summary>Does a control of this type own the texts inside it (<see cref="DialogStatics.WpfOwnsItsTexts"/>)? Cached per type: a scan asks for every control.</summary>
    static bool OwnsTexts(Type t)
    {
        return OwnsCache.GetOrAdd(t, type => DialogStatics.WpfOwnsItsTexts(Chain(type)));
    }
    static List<string> Chain(Type t) { var l = new List<string>(); for (; t != null && t != typeof(object); t = t.BaseType!) l.Add(t.Name); return l; }

    /// <summary>A visible part of the window that has a selection or pages Retrace does not follow and that is no field: a tab control ("tabs"), a tree ("tree"), a data grid ("grid"), any other list or item
    /// control with a selection that is not a field (a multi-select list box or list view: "list"). Null for anything else (a single-select list box or a combo box is a field).</summary>
    static string? UnsupportedKind(DependencyObject d) => d switch
    {
        TabControl => "tabs",
        TreeView => "tree",
        DataGrid => "grid",
        ListBox { SelectionMode: SelectionMode.Single } => null,   // a field, a list view too (Field() reads any single-select ListBox)
        System.Windows.Controls.Primitives.Selector and not ComboBox => "list",
        _ => null,
    };

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

    internal static string? FirstText(DependencyObject d)
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

    /// <summary>The text a push button shows: its automation name, else its content, else its x:Name. Only a string Content or an AccessText has an access key, so only those lose their underscore marker
    /// (<see cref="WpfPresses.StripAccess"/>: "_Add" is "Add"); an automation name, an x:Name and a TextBlock's text are shown as they are. A recording from before compares through <see cref="WpfPresses.SameText"/>.
    /// One text for palettes and dialogs.</summary>
    internal static string ButtonText(ButtonBase b)
    {
        string text;
        if (AutomationProperties.GetName(b) is { Length: > 0 } n) text = DialogFieldsCore.CleanLabel(n);
        else text = b.Content switch
        {
            string s => WpfPresses.StripAccess(s),
            AccessText at => WpfPresses.StripAccess(at.Text),
            null => DialogFieldsCore.CleanLabel(b.Name),
            var other => DialogFieldsCore.CleanLabel(Content(other)),
        };
        return text.Length > 0 ? text : "(button)";
    }

    /// <summary>Is <paramref name="b"/> a real push button (<see cref="WpfPresses.IsPushButton"/>): not a toggle, repeat button, column header or a part of a scroll bar, slider, combo box or date picker?</summary>
    internal static bool IsPushButton(ButtonBase b)
    {
        var parents = new List<IReadOnlyList<string>>();
        for (var p = b.TemplatedParent; p != null && parents.Count < 8; p = (p as FrameworkElement)?.TemplatedParent) parents.Add(Chain(p));
        return WpfPresses.IsPushButton(Chain(b), parents);
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
            // the hit may be a Run (a content element over text), which is no visual: its parent is a logical one
            for (var d = root.InputHitTest(pt) as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                if (d is ButtonBase b) { if (!IsPushButton(b) || !b.IsEnabled) return false; button = b; return true; }
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
