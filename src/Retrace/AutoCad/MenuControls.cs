using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>
/// WinForms ContextMenuStrip, asked in process through reflection (Retrace does not reference WinForms; AutoCAD has it loaded), like <see cref="WinFormsControls"/>:
/// the drop-down window, the item under a point, an item's menu path, and (for replay) the menu of a control, a path walked in it and an item clicked. UI thread only.
/// </summary>
static class WinFormsMenus
{
    static bool IsA(Type? t, string fullName)
    {
        for (; t != null; t = t.BaseType) if (t.FullName == fullName) return true;
        return false;
    }

    static object? Get(object o, string name) { try { return o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o); } catch (Exception) { return null; } }

    /// <summary>Is <paramref name="h"/> the window of a ToolStripDropDown (a ContextMenuStrip or a submenu of one)?</summary>
    public static bool IsDropDown(IntPtr h) => h != IntPtr.Zero && WinFormsControls.Control(h) is { } c && IsA(c.GetType(), "System.Windows.Forms.ToolStripDropDown");

    /// <summary>The control a drop-down menu was opened for (the root drop-down's SourceControl), or zero.</summary>
    public static IntPtr SourceOf(IntPtr dropDown)
    {
        try
        {
            var dd = WinFormsControls.Control(dropDown);
            for (int i = 0; dd != null && i < 20; i++)
            {
                if (Get(dd, "OwnerItem") is { } owner) { dd = Get(owner, "Owner"); continue; }
                return Get(dd, "SourceControl") is { } src && Get(src, "Handle") is IntPtr p ? p : IntPtr.Zero;
            }
        }
        catch (Exception ex) { Log.Error("menu source (winforms)", ex); }
        return IntPtr.Zero;
    }

    /// <summary>The menu item under (x, y) of the drop-down window <paramref name="dropDown"/>, as its menu path from the top; null for nothing, a separator, a disabled item or one that opens a submenu.</summary>
    public static (List<string> Path, IntPtr Source)? ItemUnder(IntPtr dropDown, int x, int y)
    {
        try
        {
            var dd = WinFormsControls.Control(dropDown);
            if (dd == null) return null;
            var item = dd.GetType().GetMethod("GetItemAt", new[] { typeof(int), typeof(int) })?.Invoke(dd, new object[] { x, y });
            if (item == null || IsA(item.GetType(), "System.Windows.Forms.ToolStripSeparator")) return null;
            if (Get(item, "Enabled") is false) return null;
            if (Get(item, "HasDropDownItems") is true) return null;
            var path = PathOf(item);
            return path.Count == 0 ? null : (path, SourceOf(dropDown));
        }
        catch (Exception ex) { Log.Error("menu item (winforms)", ex); return null; }
    }

    static List<string> PathOf(object item)
    {
        var path = new List<string>();
        for (var it = item; it != null && path.Count < 20; it = Get(it, "OwnerItem")) path.Insert(0, MenuText.Display(Get(it, "Text") as string));
        return path;
    }

    // ---- replay ----------------------------------------------------------------------------------------------------------------

    /// <summary>The ContextMenuStrip that belongs to the control <paramref name="control"/>: its own, else its parents'. For a tree, <paramref name="item"/> (a path) is looked for first: the node's own menu.</summary>
    public static object? StripOf(IntPtr control, string item)
    {
        try
        {
            var c = WinFormsControls.Control(control);
            if (c == null) return null;
            if (item.Length > 0 && IsA(c.GetType(), "System.Windows.Forms.TreeView") && FindNode(c, item) is { } node && Get(node, "ContextMenuStrip") is { } nodeMenu) return nodeMenu;
            for (int i = 0; c != null && i < 20; i++, c = Get(c, "Parent"))
                if (Get(c, "ContextMenuStrip") is { } strip) return strip;
        }
        catch (Exception ex) { Log.Error("menu strip (winforms)", ex); }
        return null;
    }

    /// <summary>The TreeNode of a WinForms TreeView at <paramref name="path"/> (item texts from the top, trimmed, case-insensitive).</summary>
    static object? FindNode(object tree, string path)
    {
        object? nodes = Get(tree, "Nodes"), found = null;
        foreach (var level in TreePath.Split(path))
        {
            found = null;
            if (nodes is System.Collections.IEnumerable list)
                foreach (var n in list) if (TreePath.SameText(Get(n!, "Text") as string, level)) { found = n; break; }
            if (found == null) return null;
            nodes = Get(found, "Nodes");
        }
        return found;
    }

    public static bool IsVisible(object strip) => Get(strip, "Visible") is true;

    /// <summary>The items of a menu (the strip, or the drop-down of an item): non-separator ones, as objects. Opening the strip is the caller's business; a submenu's items are listed without opening it.</summary>
    public static IEnumerable<object> Items(object? parentItem, object strip)
    {
        object? collection = parentItem == null ? Get(strip, "Items") : Get(parentItem, "DropDownItems");
        if (collection is System.Collections.IEnumerable list)
            foreach (var i in list)
                if (i != null && !IsA(i.GetType(), "System.Windows.Forms.ToolStripSeparator")) yield return i;
    }

    public static string TextOf(object item) => Get(item, "Text") as string ?? "";
    public static bool EnabledOf(object item) => Get(item, "Enabled") is not false && Get(item, "Available") is not false;

    /// <summary>Walks the menu path in <paramref name="strip"/>; the leaf item or why not.</summary>
    public static (object? Leaf, string? Problem) Find(object strip, IReadOnlyList<string> path)
    {
        var found = MenuPath.Find<object>(path, null, p => Items(p, strip), TextOf, EnabledOf);
        return found.Problem != null ? (null, found.Problem) : (found.Items[^1], null);
    }

    /// <summary>Shows the strip for <paramref name="control"/> at a client point (<c>Show(Control, Point)</c>: the strip's SourceControl is set, as by a right-click; its Opening handlers run). False when it cannot.</summary>
    public static bool Show(object strip, IntPtr control, int clientX, int clientY)
    {
        try
        {
            if (WinFormsControls.Control(control) is not { } c) return false;
            var show = strip.GetType().GetMethods().FirstOrDefault(m => m.Name == "Show" && m.GetParameters() is { Length: 2 } ps && ps[0].ParameterType.Name == "Control" && ps[1].ParameterType == typeof(System.Drawing.Point));
            if (show == null) return false;
            show.Invoke(strip, new object[] { c, new System.Drawing.Point(clientX, clientY) });
            return true;
        }
        catch (Exception ex) { Log.Error("menu show (winforms)", ex.InnerException ?? ex); return false; }
    }

    /// <summary>Clicks the item the way a click does (its Click handlers run); false when it failed.</summary>
    public static bool Click(object item)
    {
        try
        {
            item.GetType().GetMethod("PerformClick", Type.EmptyTypes)!.Invoke(item, null);
            return true;
        }
        catch (Exception ex) { Log.Error("menu click (winforms)", ex.InnerException ?? ex); return false; }
    }

    public static void Close(object strip)
    {
        try { strip.GetType().GetMethod("Close", Type.EmptyTypes)?.Invoke(strip, null); }
        catch (Exception ex) { Log.Error("menu close (winforms)", ex.InnerException ?? ex); }
    }
}

/// <summary>
/// WPF ContextMenu, in process like <see cref="WpfPalette"/>. Recording: a class handler for <c>MenuItem.Click</c> (class handlers run before the item's own, so the choice is written before
/// the item's code runs) reports a click on an item of a ContextMenu; replay: find the element (and the list or tree item) the menu belongs to, open its menu, walk the path, and raise the
/// item's click through its automation peer. UI thread only.
/// </summary>
static class WpfMenus
{
    static MenuWatch? _watch;
    static bool _registered;

    public static void Listen(MenuWatch w)
    {
        _watch = w;
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent, new RoutedEventHandler(OnClick));
    }

    public static void Mute(MenuWatch w) { if (ReferenceEquals(_watch, w)) _watch = null; }

    /// <summary>A menu item's text: its Header (a string, a TextBlock, an AccessText), the mnemonic underscore out.</summary>
    public static string HeaderText(MenuItem m)
    {
        var t = m.Header switch { string s => s, TextBlock tb => tb.Text, AccessText at => at.Text, null => "", var o => o.ToString() ?? "" };
        // the same text rules as a WPF button (Core/WpfPresses): the first single "_" is the access key, "__" a literal underscore, an "&" is just a character
        return WpfPresses.StripAccess(t);
    }

    static void OnClick(object sender, RoutedEventArgs e)
    {
        var w = _watch;
        if (w == null || sender is not MenuItem mi || mi.HasItems) return;
        try
        {
            var path = new List<string>();
            ContextMenu? cm = null;
            DependencyObject? cur = mi;
            for (int i = 0; cur != null && i < 20; i++)
            {
                if (cur is MenuItem m) path.Insert(0, HeaderText(m));
                var owner = ItemsControl.ItemsControlFromItemContainer(cur);
                if (owner is ContextMenu c) { cm = c; break; }
                cur = owner;
            }
            if (cm == null || cm.PlacementTarget is not FrameworkElement target) return;
            var hwnd = (PresentationSource.FromVisual(target) as HwndSource)?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;
            string key = "", label = "", kind = "", item = "";
            if (PaletteWatch.Locate(hwnd) is { } spot)
            {
                var scan = WpfPalette.ScanPage(spot.Page);
                for (DependencyObject? d = target; d != null && key.Length == 0; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
                {
                    int at = d is FrameworkElement fe ? scan.Elements.IndexOf(fe) : -1;
                    if (at >= 0) { key = scan.Fields[at].Key; label = scan.Fields[at].Label; kind = scan.Fields[at].Kind; }
                }
            }
            item = ItemPathOf(target);
            w.WpfChosen(path, hwnd, key, label, kind, item);
        }
        catch (Exception ex) { Log.Error("menu click (wpf)", ex); }
    }

    /// <summary>The list item or tree item the element is in (its path of header texts for a tree), "" for none.</summary>
    static string ItemPathOf(DependencyObject target)
    {
        for (DependencyObject? d = target; d != null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem tvi)
            {
                var texts = new List<string>();
                for (DependencyObject? t = tvi; t != null; t = ItemsControl.ItemsControlFromItemContainer(t) is { } o && o is not TreeView ? o : null)
                    texts.Insert(0, WpfPalette.FirstText(t) ?? "");
                return TreePath.Join(texts);
            }
            if (d is ListBoxItem lbi) return WpfPalette.FirstText(lbi) ?? "";
        }
        return "";
    }

    // ---- replay ----------------------------------------------------------------------------------------------------------------

    /// <summary>The element a recorded menu belongs to: the scan's field with that key, or the item container (list item / tree item) with the recorded text under it. Null when not found.</summary>
    public static FrameworkElement? TargetOf(IntPtr page, MenuChoice menu, out string? problem)
    {
        problem = null;
        var scan = WpfPalette.ScanPage(page);
        FrameworkElement? el = null;
        if (menu.Target.Length > 0)
        {
            var map = FillMatcher.Match(new[] { new FieldChange(menu.Target, menu.TargetLabel, menu.TargetKind, "", "", "") }, scan.Fields.Select(LiveField.Of).ToList());
            if (map[0] >= 0) el = scan.Elements[map[0]];
            else { problem = $"no field \"{menu.TargetLabel}\" in the palette"; return null; }
        }
        if (menu.Item.Length > 0)
        {
            var root = el ?? (HwndSource.FromHwnd(WpfPalette.Host(page))?.RootVisual as FrameworkElement);
            var container = root == null ? null : FindItem(root, TreePath.Split(menu.Item));
            if (container == null) { problem = $"no item {menu.ItemLeaf} in {menu.TargetLabel}"; return null; }
            return container;
        }
        if (el == null) problem = "the palette has no field the menu belongs to";
        return el;
    }

    static FrameworkElement? FindItem(DependencyObject root, IReadOnlyList<string> levels)
    {
        // the first list or tree item whose text is the last level (and, for a tree, whose parents' texts are the levels above)
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        int seen = 0;
        while (stack.Count > 0 && seen++ < 5000)
        {
            var d = stack.Pop();
            if (d is TreeViewItem tvi && TreePath.SameText(HeaderOrText(tvi), levels[^1]) && ParentsMatch(tvi, levels)) return tvi;
            if (d is ListBoxItem lbi && levels.Count == 1 && TreePath.SameText(WpfPalette.FirstText(lbi), levels[0])) return lbi;
            for (int i = VisualTreeHelper.GetChildrenCount(d) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    static string? HeaderOrText(TreeViewItem t) => t.Header as string ?? WpfPalette.FirstText(t);

    static bool ParentsMatch(TreeViewItem t, IReadOnlyList<string> levels)
    {
        var cur = t;
        for (int i = levels.Count - 2; i >= 0; i--)
        {
            if (ItemsControl.ItemsControlFromItemContainer(cur) is not TreeViewItem p || !TreePath.SameText(HeaderOrText(p), levels[i])) return false;
            cur = p;
        }
        return true;
    }

    /// <summary>The ContextMenu of the element, else of an ancestor.</summary>
    public static ContextMenu? MenuOf(FrameworkElement el)
    {
        for (DependencyObject? d = el; d != null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement fe && fe.ContextMenu is { } cm) return cm;
        return null;
    }

    /// <summary>Selects the list or tree item the menu was recorded on, as the right-click's mouse-down does (nothing for another element).</summary>
    public static void Select(FrameworkElement target)
    {
        try
        {
            if (target is ListBoxItem lbi) lbi.IsSelected = true;
            else if (target is TreeViewItem tvi) tvi.IsSelected = true;
        }
        catch (Exception ex) { Log.Error("menu select (wpf)", ex); }
    }

    /// <summary>Opens the menu on <paramref name="target"/> the way a right-click does: the element gets <c>ContextMenuOpening</c> (its handlers run, then ContextMenuService opens the menu on it),
    /// through the event arguments' internal constructor; when that cannot be done, or the menu does not open, <see cref="OpenDirect"/> sets <c>IsOpen</c> (no Opening handler runs).</summary>
    public static bool RaiseOpening(FrameworkElement target)
    {
        try
        {
            var p = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
            var args = Activator.CreateInstance(typeof(ContextMenuEventArgs), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new object[] { target, true, p.X, p.Y }, null) as ContextMenuEventArgs;
            if (args == null) return false;
            args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            target.RaiseEvent(args);
            Log.Info("context menu (wpf): ContextMenuOpening raised on " + target.GetType().Name);
            return true;
        }
        catch (Exception ex) { Log.Error("menu opening (wpf)", ex.InnerException ?? ex); return false; }
    }

    public static void OpenDirect(ContextMenu cm, FrameworkElement target)
    {
        Log.Info("context menu (wpf): opened directly (IsOpen), no ContextMenuOpening");
        cm.PlacementTarget = target;
        cm.IsOpen = true;
        cm.UpdateLayout();
    }

    public static IEnumerable<MenuItem> Items(ItemsControl parent)
    {
        foreach (var item in parent.Items)
        {
            var box = item as MenuItem ?? parent.ItemContainerGenerator.ContainerFromItem(item) as MenuItem;
            if (box != null) yield return box;
        }
    }

    public static (MenuItem? Leaf, string? Problem) Find(ContextMenu cm, IReadOnlyList<string> path)
    {
        var found = MenuPath.Find<MenuItem>(path, null, p => Items(p == null ? (ItemsControl)cm : p), HeaderText, m => m.IsEnabled);
        return found.Problem != null ? (null, found.Problem) : (found.Items[^1], null);
    }

    /// <summary>Raises the item's click through its automation peer (the Click handlers and the Command run); false when it has no peer.</summary>
    public static bool Click(MenuItem item)
    {
        try
        {
            if (System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(item) is System.Windows.Automation.Peers.MenuItemAutomationPeer peer
                && peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) is System.Windows.Automation.Provider.IInvokeProvider invoke)
            {
                invoke.Invoke();
                return true;
            }
        }
        catch (Exception ex) { Log.Error("menu click (wpf)", ex); }
        return false;
    }
}
