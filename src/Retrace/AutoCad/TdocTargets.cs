using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Retrace.AutoCad;

/// <summary>A rectangle on the screen in device pixels; Right and Bottom are exclusive, like a RECT.</summary>
readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public ScreenRect Moved(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
}

/// <summary>A control that gets a numbered box in the Tdoc picture of a dialog or palette. <see cref="Handle"/> (a Win32 control) is read again when the
/// picture is taken, because the window may have been moved; without one (UI Automation) <see cref="Rect"/> is where it was. <see cref="At"/>: where the dialog's window was when <see cref="Rect"/> was read
/// (null = when the <see cref="DialogShot"/> was made). <see cref="Arrow"/>: an arrow ends on this box (the item a person clicked in a tree or a list).</summary>
sealed record DialogBox(IntPtr Handle, ScreenRect Rect, string Text, ScreenRect? At = null, bool Arrow = false);

/// <summary>What a dialog shows when it is filled in and about to be closed: the controls that were filled and the button that closes it.</summary>
/// <param name="DialogAt">where the dialog's window was when the rectangles were read (null = not known: they are taken as read before the picture's own move onto the screen)</param>
sealed record DialogShot(IntPtr Dialog, List<DialogBox> Filled, DialogBox? Close, ScreenRect? DialogAt = null);

/// <summary>Where things are on the screen, for the boxes of a Tdoc picture. Every failure is logged and means "no box".</summary>
static class TdocTargets
{
    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>The screen rectangle of a ribbon item, read from the ribbon's own WPF tree (UI thread only; milliseconds, where UI Automation
    /// took over 6 s on AutoCAD 2026's ribbon). The item's visual is the outermost shown element whose DataContext is the RibbonItem. When the
    /// button does not show because a narrow window collapsed its panel into one button, that panel's rectangle is returned with
    /// <c>Panel</c> true: the reader opens the panel first. Null when neither shows (another tab, a slide-out) or on any failure.</summary>
    public static (ScreenRect Rect, bool Panel)? RibbonItemRect(string itemId)
    {
        try
        {
            var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
            if (ribbon == null || string.IsNullOrEmpty(itemId)) return null;
            var item = ribbon.FindItem(itemId, false, true);
            if (item == null) return null;
            if (ShownRect(ribbon, item) is { } r) return (r, false);
            foreach (var tab in ribbon.Tabs)
            {
                if (!tab.IsActive) continue;
                foreach (var panel in tab.Panels)
                    if (Holds(panel.Source?.Items, item, 0) && ShownRect(ribbon, panel) is { } pr) return (pr, true);
            }
        }
        catch (Exception ex) { Log.Error("tdoc ribbon item rect", ex); }
        return null;
    }

    /// <summary>The screen rectangle of the clicked ribbon item <paramref name="item"/> itself (the object the click ran, not a lookup by its id, which a custom or duplicated id can miss), wherever it shows in this process: the
    /// ribbon, and also an open drop-down, split-button menu or slide-out, which are windows of their own (every WPF presentation source is searched). Of several shown copies the one that holds
    /// <paramref name="near"/> (the mouse-down point) wins. UI thread; null when it shows nowhere or on any failure.</summary>
    public static ScreenRect? ItemRectAnywhere(object item, (int X, int Y)? near)
    {
        try
        {
            ScreenRect? first = null;
            var ribbonSource = Autodesk.Windows.ComponentManager.Ribbon is { } rb ? System.Windows.PresentationSource.FromVisual(rb) : null;
            foreach (System.Windows.PresentationSource src in System.Windows.PresentationSource.CurrentSources)
            {
                try
                {
                    // a source owned by another dispatcher thread cannot be read from here; only the ribbon's own window and pop-ups (drop-down menus, slide-outs) can hold a ribbon item
                    if (!src.CheckAccess()) continue;
                    if (src.RootVisual is not System.Windows.FrameworkElement root || !root.IsVisible) continue;
                    if (!ReferenceEquals(src, ribbonSource) && root.GetType().Name != "PopupRoot") continue;
                    // a drop-down menu that has closed (it closes before the item's click is reported) keeps its visual tree for a while: only a source whose window shows counts
                    if (src is System.Windows.Interop.HwndSource hs && !IsWindowVisible(hs.Handle)) continue;
                    if (ShownRect(root, item) is not { } r) continue;
                    if (near is { } p && p.X >= r.Left - 2 && p.X < r.Right + 2 && p.Y >= r.Top - 2 && p.Y < r.Bottom + 2) return r;
                    first ??= r;
                }
                catch (Exception ex) { Log.Error("ribbon item rect (one source)", ex); }
            }
            return first;
        }
        catch (Exception ex) { Log.Error("ribbon item rect (any source)", ex); return null; }
    }

    /// <summary>Whether the clicked ribbon item <paramref name="item"/> was clicked in the Quick Access Toolbar: "qat" when the visual that holds the mouse-down point (<paramref name="near"/>) has a Quick Access Toolbar
    /// among its ancestors; null for the ribbon and whenever it cannot be told (no mouse-down point and the item shows in both, a visual tree that cannot be read). An item shown in both the toolbar and a tab: the copy under the
    /// mouse-down point wins. Without a point, the only place the item shows decides. <paramref name="why"/>: what decided, for retrace.log. UI thread; read-only.</summary>
    public static string? BarOf(object item, (int X, int Y)? near, out string why) => BarOf(item, near, out why, out _);

    /// <summary><see cref="BarOf(object, ValueTuple{int,int}?, out string)"/>; <paramref name="button"/>: for a Quick Access Toolbar click, the rectangle of the toolbar's button under the mouse-down point (its copy of the item; the ribbon's own copy is somewhere else), else null.</summary>
    public static string? BarOf(object item, (int X, int Y)? near, out string why, out ScreenRect? button)
    {
        why = ""; button = null;
        if (near is { } pt && HitChain(pt, out var hitChain, out var hitQat, out var hitButton, out var hitCopy)) { if (hitQat && !Matches(hitCopy, item, out var matchWhy)) { why = $"not a toolbar click: the toolbar button under the mouse-down point is not the executed item ({matchWhy}); {hitChain}"; return null; } if (hitQat) button = hitButton; why = (hitQat ? "in the Quick Access Toolbar" : "on the ribbon") + $" (under the mouse-down point: {hitChain})"; return hitQat ? "qat" : null; }
        try
        {
            var found = new List<(ScreenRect Rect, bool Qat, string Chain)>();
            var ribbonSource = Autodesk.Windows.ComponentManager.Ribbon is { } rb ? System.Windows.PresentationSource.FromVisual(rb) : null;
            foreach (System.Windows.PresentationSource src in System.Windows.PresentationSource.CurrentSources)
            {
                try
                {
                    if (!src.CheckAccess()) continue;
                    if (src.RootVisual is not System.Windows.FrameworkElement root || !root.IsVisible) continue;
                    if (!ReferenceEquals(src, ribbonSource) && root.GetType().Name != "PopupRoot") continue;
                    if (src is System.Windows.Interop.HwndSource hs && !IsWindowVisible(hs.Handle)) continue;
                    foreach (var fe in ShownVisuals(root, item))
                    {
                    var tl = fe.PointToScreen(new System.Windows.Point(0, 0));
                    var br = fe.PointToScreen(new System.Windows.Point(fe.ActualWidth, fe.ActualHeight));
                    var chain = new List<string>();
                    bool qat = false;
                    for (System.Windows.DependencyObject? v = fe; v != null && chain.Count < 40; v = v is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(v) : null)
                    {
                        var name = v.GetType().Name;
                        chain.Add(name);
                        if (name.Contains("QuickAccess", StringComparison.OrdinalIgnoreCase)) qat = true;
                    }
                    found.Add((new ScreenRect((int)Math.Floor(tl.X), (int)Math.Floor(tl.Y), (int)Math.Ceiling(br.X), (int)Math.Ceiling(br.Y)), qat, string.Join("<", chain.Take(8))));
                    }
                }
                catch (Exception ex) { Log.Error("ribbon bar (one source)", ex); }
            }
            if (found.Count == 0) { why = "the item shows nowhere"; return null; }
            (ScreenRect Rect, bool Qat, string Chain)? pick = null;
            if (near is { } p)
                foreach (var f in found)
                    if (p.X >= f.Rect.Left - 2 && p.X < f.Rect.Right + 2 && p.Y >= f.Rect.Top - 2 && p.Y < f.Rect.Bottom + 2) { pick = f; break; }
            if (pick == null && found.Select(f => f.Qat).Distinct().Count() == 1) pick = found[0];
            if (pick is not { } c) { why = $"the item shows in the toolbar and on the ribbon and no mouse-down point tells which ({found.Count} copies)"; return null; }
            why = (c.Qat ? "in the Quick Access Toolbar" : "on the ribbon") + $" ({found.Count} shown, ancestors {c.Chain})";
            return c.Qat ? "qat" : null;
        }
        catch (Exception ex) { Log.Error("ribbon bar", ex); why = "failed"; return null; }
    }

    /// <summary>Is the toolbar button's item (<paramref name="copy"/>) the executed <paramref name="item"/>? <see cref="QatMatch"/>: ids, else macros, else texts. <paramref name="why"/> names both for retrace.log.</summary>
    static bool Matches(Autodesk.Windows.RibbonItem? copy, object item, out string why)
    {
        var it = item as Autodesk.Windows.RibbonItem;
        static string? Macro(Autodesk.Windows.RibbonItem? i) => (i as Autodesk.Windows.RibbonCommandItem)?.CommandParameter as string;
        static string? Txt(Autodesk.Windows.RibbonItem? i) => i == null ? null : (string.IsNullOrWhiteSpace(i.Text) ? i.Name : i.Text);
        why = $"toolbar button {copy?.GetType().Name ?? "none"} id '{copy?.Id}' text '{Txt(copy)}', item {it?.GetType().Name ?? "none"} id '{it?.Id}' text '{Txt(it)}'";
        if (it == null) return false;
        // some toolbar items are drawn through the toolbar's overflow host (its item is the host's own, id "Id_QuickAccessToolBarOverflowButton"): then the toolbar's list must hold the executed item
        bool host = copy == null || (copy.Id?.Contains("QuickAccessToolBar", StringComparison.OrdinalIgnoreCase) ?? false);
        bool holds = host && QatHolds(it, out var info);
        if (host) { QatHolds(it, out var info2); why += $"; the button is the toolbar's host, the toolbar {(holds ? "holds" : "does not hold")} the item ({info2})"; }
        return Retrace.Core.QatMatch.Accept(host, holds, !host && Retrace.Core.QatMatch.Same(copy!.Id, Macro(copy), Txt(copy), it.Id, Macro(it), Txt(it)));
    }

    /// <summary>Does the Quick Access Toolbar's own list of items hold <paramref name="item"/> (the same object, or one with the same id)? <paramref name="info"/> says how many it looked at, for retrace.log.</summary>
    static bool QatHolds(Autodesk.Windows.RibbonItem item, out string info)
    {
        info = "list unreadable";
        try
        {
            var qat = Autodesk.Windows.ComponentManager.QuickAccessToolBar;
            if (qat?.GetType().GetProperty("Items")?.GetValue(qat) is not System.Collections.IEnumerable items) { info = $"no Items on {qat?.GetType().Name ?? "null"}"; return false; }
            int n = 0; bool found = false;
            foreach (var it in items)
            {
                n++;
                if (ReferenceEquals(it, item) || (it is Autodesk.Windows.RibbonItem ri && !string.IsNullOrEmpty(item.Id) && ri.Id == item.Id)) found = true;
            }
            info = $"{n} items in {qat.GetType().Name}";
            return found;
        }
        catch (Exception ex) { Log.Error("QAT items", ex); info = "failed"; return false; }
    }

    /// <summary>The WPF visual under a screen point, in any window of this process that shows it (the toolbar is not necessarily in the ribbon's window): its ancestors' type names, and whether one of them is a Quick Access Toolbar.
    /// false when no WPF visual is under the point.</summary>
    static bool HitChain((int X, int Y) p, out string chain, out bool qat, out ScreenRect? button, out Autodesk.Windows.RibbonItem? copy)
    {
        chain = ""; qat = false; button = null; copy = null;
        try
        {
            foreach (System.Windows.PresentationSource src in System.Windows.PresentationSource.CurrentSources)
            {
                try
                {
                    if (!src.CheckAccess() || src is not System.Windows.Interop.HwndSource hs || !IsWindowVisible(hs.Handle) || src.RootVisual is not System.Windows.FrameworkElement root || !root.IsVisible) continue;
                    if (!GetWindowRect(hs.Handle, out var wr) || p.X < wr.Left || p.X >= wr.Right || p.Y < wr.Top || p.Y >= wr.Bottom) continue;
                    var local = root.PointFromScreen(new System.Windows.Point(p.X, p.Y));
                    if (System.Windows.Media.VisualTreeHelper.HitTest(root, local)?.VisualHit is not { } hit) continue;
                    var names = new List<string>();
                    for (System.Windows.DependencyObject? v = hit; v != null && names.Count < 40; v = v is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(v) : null)
                    {
                        var name = v.GetType().Name;
                        var dcName = v is System.Windows.FrameworkElement { DataContext: { } dc } ? dc.GetType().Name : "";
                        names.Add(dcName.Length > 0 ? $"{name}[{dcName}]" : name);
                        // the toolbar's own panel carries the toolbar's source as its DataContext (QuickAccessToolBarSource); its buttons are copies of the ribbon items, not the items
                        if (name.Contains("QuickAccess", StringComparison.OrdinalIgnoreCase) || dcName.Contains("QuickAccess", StringComparison.OrdinalIgnoreCase)) qat = true;
                    }
                    // the button the point is on: the nearest RibbonItemControl above the hit visual
                    for (System.Windows.DependencyObject? v = hit; v != null && button == null; v = v is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(v) : null)
                        if (v is System.Windows.FrameworkElement { ActualWidth: >= 1, ActualHeight: >= 1 } fe && fe.GetType().Name == "RibbonItemControl")
                        {
                            var tl = fe.PointToScreen(new System.Windows.Point(0, 0)); var br = fe.PointToScreen(new System.Windows.Point(fe.ActualWidth, fe.ActualHeight));
                            button = new ScreenRect((int)Math.Floor(tl.X), (int)Math.Floor(tl.Y), (int)Math.Ceiling(br.X), (int)Math.Ceiling(br.Y));
                            copy = fe.DataContext as Autodesk.Windows.RibbonItem;
                        }
                    chain = string.Join("<", names.Take(10));
                    return true;
                }
                catch (Exception ex) { Log.Error("ribbon bar hit (one source)", ex); }
            }
        }
        catch (Exception ex) { Log.Error("ribbon bar hit", ex); }
        return false;
    }

    /// <summary>Every outermost visible element under <paramref name="root"/> whose DataContext is <paramref name="data"/> (a button shown in the toolbar and on a tab is two); what lies inside a match is not searched.</summary>
    static List<System.Windows.FrameworkElement> ShownVisuals(System.Windows.FrameworkElement root, object data)
    {
        var all = new List<System.Windows.FrameworkElement>();
        var queue = new Queue<System.Windows.DependencyObject>(); queue.Enqueue(root);
        for (int seen = 0; queue.Count > 0 && seen < 50000; seen++)
        {
            var v = queue.Dequeue();
            if (v is System.Windows.FrameworkElement fe && ReferenceEquals(fe.DataContext, data) && fe.IsVisible && fe.ActualWidth >= 1 && fe.ActualHeight >= 1
                && System.Windows.PresentationSource.FromVisual(fe) != null)
            { all.Add(fe); continue; }
            if (v is System.Windows.Media.Visual || v is System.Windows.Media.Media3D.Visual3D)
                for (int i = 0, n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(v); i < n; i++) queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(v, i));
        }
        return all;
    }

    /// <summary>Whether a ribbon item collection holds <paramref name="target"/>, also inside rows, split buttons and other item lists.</summary>
    internal static bool Holds(System.Collections.IEnumerable? items, object target, int depth)
    {
        if (items == null || depth > 6) return false;
        foreach (var it in items)
        {
            if (ReferenceEquals(it, target)) return true;
            if (it?.GetType().GetProperty("Items")?.GetValue(it) is System.Collections.IEnumerable sub && Holds(sub, target, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>The screen rectangle (device pixels) of the outermost visible element under <paramref name="root"/> that shows <paramref name="data"/>.</summary>
    static ScreenRect? ShownRect(System.Windows.FrameworkElement root, object data)
    {
        var queue = new Queue<System.Windows.DependencyObject>(); queue.Enqueue(root);
        for (int seen = 0; queue.Count > 0 && seen < 50000; seen++)
        {
            var v = queue.Dequeue();
            if (v is System.Windows.FrameworkElement fe && ReferenceEquals(fe.DataContext, data) && fe.IsVisible && fe.ActualWidth >= 1 && fe.ActualHeight >= 1
                && System.Windows.PresentationSource.FromVisual(fe) != null)
            {
                var tl = fe.PointToScreen(new System.Windows.Point(0, 0));
                var br = fe.PointToScreen(new System.Windows.Point(fe.ActualWidth, fe.ActualHeight));
                return new ScreenRect((int)Math.Floor(tl.X), (int)Math.Floor(tl.Y), (int)Math.Ceiling(br.X), (int)Math.Ceiling(br.Y));
            }
            if (v is System.Windows.Media.Visual || v is System.Windows.Media.Media3D.Visual3D)
                for (int i = 0, n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(v); i < n; i++) queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(v, i));
        }
        return null;
    }

    /// <summary>The UI Automation rectangle of an element as a screen rectangle; null when it has none.</summary>
    public static ScreenRect? Of(AutomationElement el)
    {
        try
        {
            var r = el.Current.BoundingRectangle;
            return r.IsEmpty || r.Width < 1 || r.Height < 1 ? null : new ScreenRect((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));
        }
        catch (Exception) { return null; }
    }

    /// <summary>A window's rectangle on the screen; null when it is not visible. UI thread.</summary>
    public static ScreenRect? Of(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindowVisible(h) || !GetWindowRect(h, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return null;
        return new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);

    /// <summary>A window's client area on the screen (without its caption and borders); null when it is not visible. UI thread.</summary>
    public static ScreenRect? ClientOf(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindowVisible(h) || !GetClientRect(h, out var c)) return null;
        var p = new POINT { X = c.Left, Y = c.Top };
        if (!ClientToScreen(h, ref p) || c.Right <= c.Left || c.Bottom <= c.Top) return null;
        return new ScreenRect(p.X, p.Y, p.X + (c.Right - c.Left), p.Y + (c.Bottom - c.Top));
    }

    /// <summary>The box of a dialog or palette field that was filled. A tree selection, a list view selection of one item and a list box selection get the box on the recorded item itself (the tree item's label, the list
    /// item's icon and label, the list box row) with an arrow ending on it, found from the recorded value as the replay finds it (<see cref="PaletteClickFill.TreeItemRect"/>, <see cref="MenuListView.RowRect"/>, the row of the list box
    /// with that text), never from what is selected now: a palette's picture is taken before its step runs. Nothing is opened, selected or scrolled for it; an item that is not in view gets no box of its own. Every other field, and a
    /// tree or list whose item cannot be found (not in view, a collapsed parent, several items selected, the control is of another thread), gets the box of the whole control and no arrow: never a guessed point. UI thread.</summary>
    /// <param name="at">where the dialog's window is now (the box has its own place when it is an item, so it moves with the dialog)</param>
    public static DialogBox FieldBox(Retrace.Core.FieldChange v, IntPtr control, ScreenRect? at)
    {
        var text = TdocPlan.FieldCallout(v);
        try
        {
            ScreenRect? item = null;
            if (v.Kind == "tree" && v.After.Length > 0) item = PaletteClickFill.TreeItemRect(control, v.After);
            else if (v.Kind == Retrace.Core.ListSelection.Kind && Native.OwnedByThisThread(control) && Retrace.Core.ListSelection.ItemsOf(v) is { Count: 1 } items
                && MenuListView.AllTexts(control) is { } texts && Retrace.Core.ListSelection.Plan(texts, items, out _) is { Count: 1 } rows
                && MenuListView.RowRect(control, rows[0], scroll: false) is { } r) item = new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);
            else if (v.Kind == "list" && v.After.Length > 0) item = ListBoxRowRect(control, v.After);
            if (item is { } ir && Of(control) is { } cr && ir.Left >= cr.Left - 2 && ir.Top >= cr.Top - 2 && ir.Right <= cr.Right + 2 && ir.Bottom <= cr.Bottom + 2)
                return new DialogBox(IntPtr.Zero, ir, text, at, Arrow: true);
        }
        catch (Exception ex) { Log.Error("tdoc item box", ex); }
        return new DialogBox(control, default, text);
    }

    /// <summary>The rectangle (screen) of the first row of a list box whose text is <paramref name="value"/> (LB_GETTEXT, LB_GETITEMRECT); null when there is none or it is not in view.</summary>
    static ScreenRect? ListBoxRowRect(IntPtr h, string value)
    {
        const uint LB_GETCOUNT = 0x018B, LB_GETTEXT = 0x0189, LB_GETTEXTLEN = 0x018A, LB_GETITEMRECT = 0x0198;
        if (!Native.OwnedByThisThread(h)) return null;
        Native.SendNum(h, LB_GETCOUNT, IntPtr.Zero, IntPtr.Zero, 2, 1000, out var count);
        int row = -1;
        for (int i = 0; i < Math.Min((long)count, 2000L); i++)
        {
            Native.SendNum(h, LB_GETTEXTLEN, (IntPtr)i, IntPtr.Zero, 2, 1000, out var len);
            if ((long)len < 0 || (long)len > 1000) continue;
            var tb = Marshal.AllocHGlobal(((int)len + 1) * 2);
            try
            {
                Native.SendNum(h, LB_GETTEXT, (IntPtr)i, tb, 2, 1000, out var got);
                if ((long)got >= 0 && string.Equals(Marshal.PtrToStringUni(tb, (int)(long)got).Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase)) { row = i; break; }
            }
            finally { Marshal.FreeHGlobal(tb); }
        }
        if (row < 0) return null;
        var buf = Marshal.AllocHGlobal(16);
        try
        {
            for (int k = 0; k < 4; k++) Marshal.WriteInt32(buf, k * 4, 0);
            Native.SendNum(h, LB_GETITEMRECT, (IntPtr)row, buf, 2, 1000, out var ok);
            if ((long)ok == -1) return null;   // LB_ERR (LB_GETITEMRECT answers 0 when it filled the rectangle)
            var tl = new POINT { X = Marshal.ReadInt32(buf, 0), Y = Marshal.ReadInt32(buf, 4) };
            var br = new POINT { X = Marshal.ReadInt32(buf, 8), Y = Marshal.ReadInt32(buf, 12) };
            if (br.X - tl.X < 2 || br.Y - tl.Y < 2 || !GetClientRect(h, out var c)) return null;
            if (tl.Y < 0 || br.Y > c.Bottom + 1) return null;   // scrolled out of view
            ClientToScreen(h, ref tl); ClientToScreen(h, ref br);
            return new ScreenRect(tl.X, tl.Y, br.X, br.Y);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The rectangle of a box: the control's window now when it has one, else where it was seen, moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public static ScreenRect? Of(DialogBox b, int dx = 0, int dy = 0) => b.Handle != IntPtr.Zero ? Of(b.Handle) : b.Rect.Moved(dx, dy);

    /// <summary>A WPF window of this process whose content is AutoCAD's command line (AutoCAD 2026: an untitled HwndWrapper owned by the main window,
    /// holding a CommandLineControl). UI thread only: it reads the WPF visual tree.</summary>
    static bool HostsCommandLine(IntPtr h)
    {
        try
        {
            if (!Native.ClassOf(h).StartsWith("HwndWrapper", StringComparison.Ordinal)) return false;
            if (System.Windows.Interop.HwndSource.FromHwnd(h)?.RootVisual is not System.Windows.DependencyObject root) return false;
            var queue = new Queue<(System.Windows.DependencyObject, int)>(); queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (v, depth) = queue.Dequeue();
                if (v.GetType().Name.Contains("CommandLine", StringComparison.Ordinal)) return true;
                if (depth >= 6 || v is not System.Windows.Media.Visual) continue;
                for (int i = 0, n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(v); i < n; i++) queue.Enqueue((System.Windows.Media.VisualTreeHelper.GetChild(v, i), depth + 1));
            }
        }
        catch (Exception ex) { Log.Error("tdoc command line host", ex); }
        return false;
    }

    /// <summary>AutoCAD's command line: the biggest visible window of this process, docked or floating, that holds AutoCAD's command line
    /// control (<see cref="HostsCommandLine"/>) or is titled "Command Line" (or has "CmdLine" in its class). Null when there is none. UI thread.</summary>
    public static ScreenRect? CommandLineRect(IntPtr main)
    {
        ScreenRect? best = null;
        long bestArea = 0;
        bool Consider(IntPtr h)
        {
            if (!IsWindowVisible(h)) return true;
            var title = Native.TitleOf(h);
            if (!title.Equals("Command Line", StringComparison.OrdinalIgnoreCase) && Native.ClassOf(h).IndexOf("CmdLine", StringComparison.OrdinalIgnoreCase) < 0
                && !HostsCommandLine(h)) return true;
            if (Of(h) is { } r && (long)(r.Right - r.Left) * (r.Bottom - r.Top) is var area && area > bestArea) { best = r; bestArea = area; }
            return true;
        }
        try
        {
            uint me = (uint)Environment.ProcessId;
            EnumChildWindows(main, (h, _) => Consider(h), IntPtr.Zero);
            EnumWindows((top, _) =>
            {
                GetWindowThreadProcessId(top, out var pid);
                if (pid != me) return true;
                Consider(top);
                EnumChildWindows(top, (h, _) => Consider(h), IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("tdoc command line rect", ex); }
        return best;
    }
}
