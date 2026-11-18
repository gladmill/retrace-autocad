using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Retrace.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.AutoCad;

/// <summary>
/// AutoCAD's status bar (the bar below the command line) is WPF content in a host window under a <c>msctls_statusbar32</c> window; every item (a pane, a tray item; AutoCAD's own Grid / Snap / Ortho and an add-in's) is an element whose
/// DataContext is AutoCAD's item model (<c>Autodesk.AutoCAD.StatusBar.Pane</c> / <c>PopupPane</c>: Text, ToolTip, Name, RegistryKey, HelpID, IsSplitPane...). This reads the item under a screen point (a click), and every item the bar shows
/// now (a replay finding one again), from that WPF tree: nothing is hooked, nothing is guessed, and a click outside the bar costs one rectangle test. The item models are read by property name (reflection), so a release that renames
/// the type still works as long as the properties are there; when they are not, no item is found and the caller says so. UI thread only. See docs/recording-format.md, `statusbar_click`.
/// </summary>
static class StatusBarWatch
{
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr data);
    delegate bool EnumProc(IntPtr h, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>One item of the bar as the tree shows it now.</summary>
    public sealed class Item
    {
        public string Text = "", Tooltip = "", Key = "", Name = "", Kind = StatusBarClick.Pane;
        public bool Own, Visible, Enabled = true, Split, PopupType;
        public int Index = -1;
        public PxRect Rect;
        public IntPtr Host;
        public object? Model;
    }

    /// <summary>The status bar window (the <c>msctls_statusbar32</c> child of AutoCAD's main window), else the window the managed status bar reports, else zero.</summary>
    public static IntPtr BarWindow()
    {
        try
        {
            var main = AcApp.MainWindow.Handle;
            if (main != IntPtr.Zero) { var h = FindWindowEx(main, IntPtr.Zero, "msctls_statusbar32", null); if (h != IntPtr.Zero) return h; }
            if (AcApp.StatusBar?.Window is { } w && w.GetType().GetProperty("Handle")?.GetValue(w) is { } hv)
                return hv is IntPtr p ? p : new IntPtr(Convert.ToInt64(hv));
        }
        catch (Exception) { }
        return IntPtr.Zero;
    }

    /// <summary>The bar's screen rectangle, or null.</summary>
    public static PxRect? BarRect(IntPtr bar)
    {
        if (bar == IntPtr.Zero || !IsWindowVisible(bar) || !GetWindowRect(bar, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return null;
        return new PxRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Is the screen point on the status bar?</summary>
    public static bool IsOnBar(int x, int y) => BarRect(BarWindow()) is { } b && b.Contains(x, y);

    static IEnumerable<(IntPtr Host, HwndSource Source)> Sources(IntPtr bar, IntPtr hint)
    {
        var seen = new HashSet<IntPtr>();
        if (hint != IntPtr.Zero && HwndSource.FromHwnd(hint) is { } hs) { seen.Add(hint); yield return (hint, hs); }
        var kids = new List<IntPtr>();
        if (bar != IntPtr.Zero) EnumChildWindows(bar, (h, _) => { kids.Add(h); return true; }, IntPtr.Zero);
        foreach (var k in kids)
            if (seen.Add(k) && HwndSource.FromHwnd(k) is { } s) yield return (k, s);
    }

    static readonly Dictionary<Type, (PropertyInfo? Text, PropertyInfo? Tip, PropertyInfo? Name, PropertyInfo? Key, PropertyInfo? Help, PropertyInfo? Visible, PropertyInfo? Enabled, PropertyInfo? Split)> Props = new();

    static string Str(PropertyInfo? p, object o) { try { return p?.GetValue(o) is { } v ? (v as string ?? "") : ""; } catch (Exception) { return ""; } }
    static bool Bool(PropertyInfo? p, object o, bool otherwise) { try { return p?.GetValue(o) is bool b ? b : otherwise; } catch (Exception) { return otherwise; } }

    /// <summary>Is this DataContext an item of the status bar (the type lives in AutoCAD's status bar namespace and has the properties of an item)?</summary>
    static bool IsModel(object? dc)
    {
        if (dc == null) return false;
        var t = dc.GetType();
        // an item model of AutoCAD's status bar: an Autodesk type called Pane, PopupPane or TrayItem, wherever the bar window is (another release may build the bar differently)
        if (t.FullName is not { } n || !n.StartsWith("Autodesk.", StringComparison.Ordinal) || !(t.Name is "Pane" or "PopupPane" or "TrayItem")) return false;
        if (!Props.TryGetValue(t, out _)) Props[t] = (Prop(t, "Text"), Prop(t, "ToolTip"), Prop(t, "Name"), Prop(t, "RegistryKey"), Prop(t, "HelpID"), Prop(t, "IsVisible"), Prop(t, "IsEnabled"), Prop(t, "IsSplitPane"));
        return Props[t].Text != null || Props[t].Tip != null;
    }

    static PropertyInfo? Prop(Type t, string name) => t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    static Item? ItemOf(object dc, FrameworkElement outer, IntPtr host)
    {
        var p = Props[dc.GetType()];
        var key = Str(p.Key, dc); var help = Str(p.Help, dc);
        bool own = key.Length > 0 || help.Length > 0;
        var tip = Str(p.Tip, dc);
        var it = new Item
        {
            Text = Str(p.Text, dc).Trim(), Tooltip = own || tip.Contains("$(", StringComparison.Ordinal) ? "" : tip.Trim(), Key = key, Name = own ? Str(p.Name, dc) : "", Own = own,
            Visible = Bool(p.Visible, dc, true) && outer.IsVisible && outer.ActualWidth >= 1 && outer.ActualHeight >= 1, Enabled = Bool(p.Enabled, dc, true), Split = Bool(p.Split, dc, false),
            PopupType = dc.GetType().Name == "PopupPane", Host = host, Model = dc, Kind = dc.GetType().Name == "TrayItem" ? StatusBarClick.Tray : StatusBarClick.Pane,
        };
        if (Rect(outer) is { } r) it.Rect = r; else it.Visible = false;
        return it;
    }

    static PxRect? Rect(FrameworkElement fe)
    {
        try
        {
            if (PresentationSource.FromVisual(fe) == null) return null;
            var tl = fe.PointToScreen(new System.Windows.Point(0, 0));
            var br = fe.PointToScreen(new System.Windows.Point(fe.ActualWidth, fe.ActualHeight));
            return new PxRect((int)Math.Floor(tl.X), (int)Math.Floor(tl.Y), (int)Math.Ceiling(br.X), (int)Math.Ceiling(br.Y));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Every item the bar has now, shown or not, in the order the tree lists them, with kind and index worked out (<see cref="Number"/>).</summary>
    public static List<Item> ReadAll(IntPtr hint = default)
    {
        var items = new List<Item>();
        try
        {
            var bar = BarWindow();
            foreach (var (host, src) in Sources(bar, hint))
            {
                if (src.RootVisual is not DependencyObject root) continue;
                var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var queue = new Queue<DependencyObject>(); queue.Enqueue(root);
                for (int n = 0; queue.Count > 0 && n < 60000; n++)
                {
                    var v = queue.Dequeue();
                    if (v is FrameworkElement fe && IsModel(fe.DataContext) && fe.DataContext is { } dc && seen.Add(dc) && ItemOf(dc, fe, host) is { } it) items.Add(it);   // breadth first: the first element with a model is its outermost
                    if (v is Visual || v is System.Windows.Media.Media3D.Visual3D)
                        for (int i = 0, c = VisualTreeHelper.GetChildrenCount(v); i < c; i++) queue.Enqueue(VisualTreeHelper.GetChild(v, i));
                }
            }
            // an item the bar clips away (AutoCAD's window is narrow: the tree still lists it, with a rectangle outside the bar) is not shown
            if (BarRect(BarWindow()) is { } barBox)
                foreach (var it in items)
                    if (it.Visible && (it.Rect.Right > barBox.Right + 1 || it.Rect.Left < barBox.Left - 1 || it.Rect.Bottom > barBox.Bottom + 2 || it.Rect.Top < barBox.Top - 2)) it.Visible = false;
            Number(items);
        }
        catch (Exception ex) { Log.Error("status bar read", ex); }
        return items;
    }

    /// <summary>Kind and index of every item: a tray item has its own model type (`TrayItem`); else an item whose tool tip is a tray item's (and which no pane has the text and tool tip of) is a tray item, its index among the tray items; a pane's index is its place in the managed pane list (<c>StatusBar.Panes</c>), the k-th
    /// of the panes with the same text and tool tip for the k-th such item in the tree; -1 when the managed list has none (AutoCAD's own items need no index: they have a key).</summary>
    static void Number(List<Item> items)
    {
        var bar = AcApp.StatusBar;
        var panes = new List<(string Text, string Tip)>();
        var trays = new List<string>();
        try
        {
            if (bar != null)
            {
                foreach (Autodesk.AutoCAD.Windows.Pane p in bar.Panes) panes.Add(((p.Text ?? "").Trim(), (p.ToolTipText ?? "").Trim()));
                foreach (Autodesk.AutoCAD.Windows.TrayItem t in bar.TrayItems) trays.Add((t.ToolTipText ?? "").Trim());
            }
        }
        catch (Exception ex) { Log.Error("status bar lists", ex); }
        var used = new Dictionary<(string, string, bool), int>();
        foreach (var it in items.Where(x => !x.Own))
        {
            bool tray = it.Kind == StatusBarClick.Tray || (it.Text.Length == 0 && it.Tooltip.Length > 0 && trays.Contains(it.Tooltip) && !panes.Contains((it.Text, it.Tooltip)));
            it.Kind = tray ? StatusBarClick.Tray : StatusBarClick.Pane;
            var k = (it.Text, it.Tooltip, tray);
            used.TryGetValue(k, out var seen); used[k] = seen + 1;
            if (tray) { int at = -1, c = 0; for (int i = 0; i < trays.Count; i++) if (trays[i] == it.Tooltip && c++ == seen) { at = i; break; } it.Index = at; }
            else { int at = -1, c = 0; for (int i = 0; i < panes.Count; i++) if (panes[i].Text == it.Text && panes[i].Tip == it.Tooltip && c++ == seen) { at = i; break; } it.Index = at; }
        }
    }

    /// <summary>What a click at a screen point was on.</summary>
    public sealed record Hit(Item? Item, bool DropDownPart, PxRect? Bar, string? Problem);

    /// <summary>The status bar item under the screen point (the left or right button went down there), found by a hit test of the bar's WPF tree; <paramref name="hwnd"/> is the window the message went to. Item null with a reason when the point is on the bar and no item is under it.</summary>
    public static Hit At(int x, int y, IntPtr hwnd, bool onBar = true)
    {
        var bar = BarWindow();
        var barRect = BarRect(bar);
        if (onBar && (barRect is not { } br0 || !br0.Contains(x, y))) return new Hit(null, false, barRect, "the point is not on the status bar");
        var br = barRect ?? new PxRect(x, y, x + 1, y + 1);   // no bar window found: the item's own rectangle decides the picture (Crop needs a bar: the window picture is taken)
        try
        {
            foreach (var (host, src) in Sources(bar, hwnd))
            {
                if (src.RootVisual is not FrameworkElement root) continue;
                System.Windows.Point p;
                try { p = root.PointFromScreen(new System.Windows.Point(x, y)); } catch (Exception) { continue; }
                var hit = VisualTreeHelper.HitTest(root, p);
                if (hit?.VisualHit is not DependencyObject d) continue;
                bool arrow = false;
                for (DependencyObject? c = d; c != null; c = c is Visual || c is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(c) : LogicalTreeHelper.GetParent(c))
                {
                    if (c is FrameworkElement named && (named.Name is "PART_ToggleButton" or "mDropDownIndicator" or "mDropDownBadge")) arrow = true;
                    if (c is FrameworkElement fe && IsModel(fe.DataContext) && fe.DataContext is { } dc)
                    {
                        // the outermost element that has this model
                        FrameworkElement outer = fe;
                        for (DependencyObject? up = VisualTreeHelper.GetParent(fe); up != null; up = VisualTreeHelper.GetParent(up))
                            if (up is FrameworkElement uf && ReferenceEquals(uf.DataContext, dc)) outer = uf; else if (up is FrameworkElement) break;
                        var item = ItemOf(dc, outer, host);
                        if (item == null) return new Hit(null, false, br, "the item's rectangle cannot be read");
                        // number it among all the items (index and kind)
                        var all = ReadAll(host);
                        var same = all.FirstOrDefault(a => ReferenceEquals(a.Model, dc));
                        if (same != null) item = same;
                        return new Hit(item, arrow && item.Split, br, null);
                    }
                }
            }
        }
        catch (Exception ex) { Log.Error("status bar hit", ex); return new Hit(null, false, br, "the status bar could not be read: " + ex.Message); }
        return new Hit(null, false, br, "no status bar item is under the mouse (empty space of the bar, or the bar's content is not the kind Retrace reads); elements under the mouse: " + Ancestors(hwnd, x, y, 8));
    }

    /// <summary>Is the point in the bottom 80 px (scaled by the window's DPI) of AutoCAD's main window, where the status bar is?</summary>
    public static bool InLowStrip(int x, int y, IntPtr main)
    {
        try
        {
            if (main == IntPtr.Zero || !GetWindowRect(main, out var r)) return false;
            int dpi = (int)GetDpiForWindow(main); if (dpi <= 0) dpi = 96;
            int strip = 80 * dpi / 96;
            return x >= r.Left && x < r.Right && y >= r.Bottom - strip && y < r.Bottom;
        }
        catch (Exception) { return false; }
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);

    /// <summary>Is the window one of AutoCAD's application frame content (a WPF host whose parents are only the application frame, the status bar and the main frame)?</summary>
    public static bool InApplicationFrame(IntPtr hwnd)
    {
        try { return PaneClicks.IsApplicationFrame(ClassChain(hwnd)); } catch (Exception) { return false; }
    }

    static List<string> ClassChain(IntPtr hwnd)
    {
        var l = new List<string>();
        for (var h = hwnd; h != IntPtr.Zero && l.Count < 12; h = GetParent(h)) l.Add(DialogReader.ClassOf(h));
        return l;
    }

    /// <summary>Up to <paramref name="max"/> ancestors of the visual under the point in the WPF window the click went to, each as <c>ElementType[DataContextType.FullName]</c>.</summary>
    static string Ancestors(IntPtr hwnd, int x, int y, int max)
    {
        try
        {
            if (HwndSource.FromHwnd(hwnd) is not { RootVisual: FrameworkElement root }) return "(the window is not WPF)";
            var hit = VisualTreeHelper.HitTest(root, root.PointFromScreen(new System.Windows.Point(x, y)));
            if (hit?.VisualHit is not DependencyObject d) return "(nothing hit)";
            var parts = new List<string>();
            for (DependencyObject? c = d; c != null && parts.Count < max; c = c is Visual || c is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(c) : null)
                parts.Add(c.GetType().Name + "[" + ((c as FrameworkElement)?.DataContext?.GetType().FullName ?? "") + "]");
            return string.Join(" < ", parts);
        }
        catch (Exception ex) { return "(" + ex.Message + ")"; }
    }

    /// <summary>The text of the one `status bar probe:` line: how the bar was resolved (msctls / StatusBar.Window / none) with its class, rectangle, visibility and parent chain; the clicked window's class chain; the WPF ancestors at the point.</summary>
    public static string Probe(IntPtr hwnd, int x, int y)
    {
        string how = "none"; IntPtr bar = IntPtr.Zero;
        try
        {
            var main = AcApp.MainWindow.Handle;
            if (main != IntPtr.Zero) { bar = FindWindowEx(main, IntPtr.Zero, "msctls_statusbar32", null); if (bar != IntPtr.Zero) how = "msctls_statusbar32 child of the main frame"; }
            if (bar == IntPtr.Zero) { bar = BarWindow(); if (bar != IntPtr.Zero) how = "Application.StatusBar.Window"; }
        }
        catch (Exception) { }
        string barText = bar == IntPtr.Zero ? "no bar window" : $"class {DialogReader.ClassOf(bar)}, rect {(GetWindowRect(bar, out var r) ? $"{r.Left},{r.Top},{r.Right},{r.Bottom}" : "?")}, visible {IsWindowVisible(bar)}, parents {string.Join(" < ", ClassChain(GetParent(bar)))}";
        return $"bar resolved by {how} ({barText}); click at {x},{y} in {string.Join(" < ", ClassChain(hwnd))}; WPF ancestors: {Ancestors(hwnd, x, y, 8)}";
    }
}
