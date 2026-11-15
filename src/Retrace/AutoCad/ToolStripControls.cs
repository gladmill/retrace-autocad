using System.Collections;
using System.Drawing;
using System.Reflection;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>One item of a WinForms <c>ToolStrip</c>, read through reflection (Retrace does not reference WinForms). <c>Button</c>: an item a person presses (not a separator, not hidden, not a label or a hosted text box / combo, which
/// have their own windows or no action).</summary>
sealed record TsItem(object Obj, string Name, string Text, string Tooltip, string Accessible, bool Enabled, bool Available, bool Separator, bool Label, bool Host, bool Overflow, bool DropDown, bool Split, Rectangle Bounds, bool OnOverflow = false)
{
    public bool Button => !Separator && Available && !Label && !Host && !Overflow;
    public ToolCandidate Candidate => new(Name, Text, Tooltip, Accessible, Enabled);
}

/// <summary>
/// A WinForms <c>ToolStrip</c> of a palette, asked in process through reflection. A ToolStrip is one window; its items (<c>ToolStripButton</c>, drop-down and split buttons, separators) have no window of their own, so a click on one is a
/// WM_LBUTTONDOWN on the strip, and which item it was comes from <c>GetItemAt</c>. A drop-down's menu is a window of its own (<c>ToolStripDropDown</c>, a top-level popup): the item chosen there is found the same way and its
/// owner chain leads back to the strip. UI thread only.
/// </summary>
static class ToolStripControls
{
    const string NS = "System.Windows.Forms.";

    static bool Is(object? o, string type) => o != null && WinFormsControls.IsA(o.GetType(), NS + type);

    /// <summary>The ToolStrip (also a MenuStrip or StatusStrip, never a drop-down) behind a window handle, or null.</summary>
    public static object? StripOf(IntPtr h) => WinFormsControls.Control(h) is { } c && Is(c, "ToolStrip") && !Is(c, "ToolStripDropDown") ? c : null;

    /// <summary>The ToolStripDropDown (a drop-down menu, the overflow list too) behind a window handle, or null.</summary>
    public static object? DropDownOf(IntPtr h) => WinFormsControls.Control(h) is { } c && Is(c, "ToolStripDropDown") ? c : null;

    static T? Get<T>(object o, string prop)
    {
        try { return o.GetType().GetProperty(prop, BindingFlags.Instance | BindingFlags.Public)?.GetValue(o) is T v ? v : default; }
        catch (Exception) { return default; }
    }

    public static IntPtr HandleOf(object control) => Get<IntPtr>(control, "Handle");

    static TsItem Read(object item)
    {
        bool sep = Is(item, "ToolStripSeparator");
        bool label = Is(item, "ToolStripLabel") && Get<bool>(item, "IsLink") == false;
        bool host = Is(item, "ToolStripControlHost");
        bool dropItem = Is(item, "ToolStripDropDownItem");
        // by type, not by the items it has now: a drop-down filled in DropDownOpening has none at the button-down. A menu item is a drop-down when it has items now (its type says nothing).
        bool hasDrop = dropItem && (Is(item, "ToolStripDropDownButton") || Is(item, "ToolStripSplitButton") || Get<bool>(item, "HasDropDownItems"));
        return new TsItem(item, Get<string>(item, "Name") ?? "", Get<string>(item, "Text") ?? "", Get<string>(item, "ToolTipText") ?? "", Get<string>(item, "AccessibleName") ?? "",
            Get<bool>(item, "Enabled"), Get<bool>(item, "Available"), sep, label, host, Is(item, "ToolStripOverflowButton"), hasDrop, Is(item, "ToolStripSplitButton"), Get<Rectangle>(item, "Bounds"), Get<bool>(item, "IsOnOverflow"));
    }

    /// <summary>Every item of the strip in order, separators included.</summary>
    public static List<TsItem> Items(object strip)
    {
        var list = new List<TsItem>();
        try
        {
            if (Get<IEnumerable>(strip, "Items") is { } items)
                foreach (var it in items) { if (it != null) list.Add(Read(it)); if (list.Count >= 400) break; }
        }
        catch (Exception ex) { Log.Error("toolstrip items", ex); }
        return list;
    }

    /// <summary>The buttons (see <see cref="TsItem.Button"/>) in order: the list a position, a count and a name are counted in.</summary>
    public static List<TsItem> Buttons(object strip) => Items(strip).Where(i => i.Button).ToList();

    /// <summary>The item under a client point of the strip (its own GetItemAt; the overflow chevron too), or null for the gap between items.</summary>
    public static TsItem? ItemAt(object strip, int x, int y)
    {
        try
        {
            var m = strip.GetType().GetMethod("GetItemAt", new[] { typeof(int), typeof(int) });
            return m?.Invoke(strip, new object[] { x, y }) is { } it ? Read(it) : null;
        }
        catch (Exception ex) { Log.Error("toolstrip item at", ex); return null; }
    }

    /// <summary>The place of a ToolStrip among the ToolStrips of a page (1 = the first) and its `Name`.</summary>
    public static List<(IntPtr H, object Strip)> StripsOf(IntPtr page, bool hidden) =>
        PaletteClickDetect.Children(page, hidden).Select(h => (H: h, Strip: StripOf(h))).Where(t => t.Strip != null).Select(t => (t.H, t.Strip!)).ToList();

    public static string NameOf(object control) => Get<string>(control, "Name") ?? "";

    /// <summary>The click point of the item's part that a replay clicks, on the screen: the item's middle; a split button's arrow part when <paramref name="arrow"/>.</summary>
    public static (int X, int Y, ScreenRect Rect)? ScreenOf(IntPtr client, TsItem item, bool arrow)
    {
        var b = item.Bounds;
        if (item.Split)
        {
            // ButtonBounds / DropDownButtonBounds are relative to the item, not to the strip
            var part = Get<Rectangle>(item.Obj, arrow ? "DropDownButtonBounds" : "ButtonBounds");
            if (part.Width <= 0 || part.Height <= 0) return null;
            b = new Rectangle(item.Bounds.Left + part.Left, item.Bounds.Top + part.Top, part.Width, part.Height);
        }
        if (b.Width <= 0 || b.Height <= 0) return null;
        var p1 = new Win32.POINT { X = b.Left, Y = b.Top }; var p2 = new Win32.POINT { X = b.Right, Y = b.Bottom };
        if (!Win32.ClientToScreen(client, ref p1) || !Win32.ClientToScreen(client, ref p2)) return null;
        return ((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2, new ScreenRect(p1.X, p1.Y, p2.X, p2.Y));
    }

    /// <summary>The rectangle for a picture's frame: <paramref name="rect"/> only when it lies inside the strip's client area (screen); a rectangle outside it (a stale item rectangle, a strip nested in a container that clips it) is dropped.</summary>
    public static ScreenRect? InsideStrip(IntPtr strip, ScreenRect rect)
    {
        if (!Win32.GetClientRect(strip, out var c)) return null;
        var p1 = new Win32.POINT { X = c.Left, Y = c.Top }; var p2 = new Win32.POINT { X = c.Right, Y = c.Bottom };
        if (!Win32.ClientToScreen(strip, ref p1) || !Win32.ClientToScreen(strip, ref p2)) return null;
        return Retrace.Core.MarkGuard.ItemInStrip(new Retrace.Core.PxRect(p1.X, p1.Y, p2.X, p2.Y), new Retrace.Core.PxRect(rect.Left, rect.Top, rect.Right, rect.Bottom)) ? rect : null;
    }

    /// <summary>The whole item (a split button's two parts together) on the screen, for a picture's frame.</summary>
    public static ScreenRect? WholeItemRect(IntPtr client, TsItem item)
    {
        var b = item.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return null;
        var p1 = new Win32.POINT { X = b.Left, Y = b.Top }; var p2 = new Win32.POINT { X = b.Right, Y = b.Bottom };
        return Win32.ClientToScreen(client, ref p1) && Win32.ClientToScreen(client, ref p2) ? new ScreenRect(p1.X, p1.Y, p2.X, p2.Y) : null;
    }

    /// <summary>Is the client point on the arrow part of a split button?</summary>
    public static bool OnArrow(TsItem item, int x, int y) =>
        item.Split && Get<Rectangle>(item.Obj, "DropDownButtonBounds") is { Width: > 0 } d && new Rectangle(item.Bounds.Left + d.Left, item.Bounds.Top + d.Top, d.Width, d.Height).Contains(x, y);

    /// <summary>Replay, just before the click: is the screen point on THIS item of the strip (and on its arrow part when <paramref name="arrow"/>, its button part otherwise)? A point that is on the strip but on another icon must never be clicked.</summary>
    public static string? PointIsOn(IntPtr strip, object item, int sx, int sy, bool arrow)
    {
        var ctl = StripOf(strip);
        if (ctl == null) return "the toolbar is gone";
        var p = new Win32.POINT { X = sx, Y = sy };
        if (!Win32.ScreenToClient(strip, ref p)) return "the point is not on the toolbar";
        var at = ItemAt(ctl, p.X, p.Y);
        if (at == null || !ReferenceEquals(at.Obj, item)) return "the click point is on another toolbar item or on none";
        if (at.Split && OnArrow(at, p.X, p.Y) != arrow) return "the click point is on the wrong part of the split button";
        return null;
    }

    /// <summary>Replay, just before a menu click: is the screen point on THAT menu item of the drop-down window (not another item, not the padding)?</summary>
    public static string? MenuPointIsOn(IntPtr ddWindow, object item, int sx, int sy)
    {
        var dd = DropDownOf(ddWindow);
        if (dd == null) return "the drop-down is gone";
        var p = new Win32.POINT { X = sx, Y = sy };
        if (!Win32.ScreenToClient(ddWindow, ref p)) return "the point is not on the drop-down";
        try
        {
            var at = dd.GetType().GetMethod("GetItemAt", new[] { typeof(int), typeof(int) })?.Invoke(dd, new object[] { p.X, p.Y });
            return ReferenceEquals(at, item) ? null : "the click point is on another menu item or on none";
        }
        catch (Exception) { return "the menu item under the point could not be read"; }
    }

    /// <summary>Is the drop-down of this item showing?</summary>
    public static bool IsOpen(object item) => Get<object>(item, "DropDown") is { } dd && Get<bool>(dd, "Visible");

    /// <summary>Closes the drop-down of an item (and its sub menus), so a retry starts clean.</summary>
    public static void Close(object item)
    {
        try { item.GetType().GetMethod("HideDropDown", Type.EmptyTypes)?.Invoke(item, null); }
        catch (Exception ex) { Log.Error("toolstrip close drop-down", ex); }
    }

    /// <summary>Is this window the drop-down menu of a ToolStrip item (a click in it is work in the palette, not leaving it)?</summary>
    public static bool IsDropDownWindow(IntPtr h) => DialogReader.ClassOf(h).StartsWith("WindowsForms", StringComparison.Ordinal) && DropDownOf(h) != null;

    // ---- drop-down menus ---------------------------------------------------------------------------------------------------------------

    /// <summary>The name of a menu item as a path is made of: its text (mnemonic removed), else its tool tip, else its `Name`; "(item)" when none.</summary>
    public static string MenuName(object item)
    {
        var n = ButtonNames.Clean(Get<string>(item, "Text"));
        if (n.Length == 0) n = ButtonNames.Clean(Get<string>(item, "ToolTipText"));
        if (n.Length == 0) n = ButtonNames.Clean(Get<string>(item, "Name"));
        return n.Length > 0 ? n : "(item)";
    }

    /// <summary>A click at a point of a drop-down window: the strip the menu belongs to (its window), the strip-level item that opened it, and the path of item texts from that item's menu down to the item clicked. Null when the
    /// click is no choice (no item, a separator, a disabled item, a sub menu that only opens) or the chain does not end at a strip (the overflow list). <paramref name="why"/> says which, for the log.</summary>
    public sealed record Choice(IntPtr Strip, object Root, List<string> Path, object Leaf);

    public static Choice? DropDownChoice(IntPtr hwnd, int x, int y, out string why)
    {
        why = "";
        var dd = DropDownOf(hwnd);
        if (dd == null) { why = "not a drop-down"; return null; }
        object? leaf;
        try { leaf = dd.GetType().GetMethod("GetItemAt", new[] { typeof(int), typeof(int) })?.Invoke(dd, new object[] { x, y }); }
        catch (Exception) { leaf = null; }
        if (leaf == null) { why = "no item under the click"; return null; }
        if (Is(leaf, "ToolStripSeparator")) { why = "a separator"; return null; }
        if (!Get<bool>(leaf, "Enabled")) { why = "a disabled item"; return null; }
        if (Is(leaf, "ToolStripDropDownItem") && Get<bool>(leaf, "HasDropDownItems")) { why = "a sub menu (it only opens)"; return null; }
        var path = new List<string> { MenuName(leaf) };
        var owner = Get<object>(leaf, "OwnerItem");
        for (int i = 0; owner != null && i < 12; i++)
        {
            var next = Get<object>(owner, "OwnerItem");
            if (next == null) break;
            path.Insert(0, MenuName(owner));
            owner = next;
        }
        if (owner == null) { why = Is(dd, "ToolStripOverflow") ? "the overflow list (not followed)" : "a menu with no owner item"; return null; }
        var strip = Get<object>(owner, "Owner");
        if (strip == null || !Is(strip, "ToolStrip") || Is(strip, "ToolStripDropDown")) { why = "the menu is not owned by a strip"; return null; }
        return new Choice(HandleOf(strip), owner, path, leaf);
    }

    /// <summary>Replay: the menu item with this text in the open drop-down of <paramref name="parent"/> (a strip item or a menu item): the drop-down's window, the screen point and rectangle. Null with the reason when the menu is
    /// not showing yet, has no such item, or the item is disabled.</summary>
    public static (IntPtr Window, int X, int Y, ScreenRect Rect, object Item)? MenuItemTarget(object parent, string text, out string why)
    {
        why = "";
        var dd = Get<object>(parent, "DropDown");
        if (dd == null) { why = "the item has no drop-down"; return null; }
        if (!Get<bool>(dd, "Visible")) { why = "the drop-down is not showing"; return null; }
        var h = HandleOf(dd);
        if (h == IntPtr.Zero) { why = "the drop-down has no window"; return null; }
        if (Get<IEnumerable>(parent, "DropDownItems") is not { } items) { why = "the item has no menu items"; return null; }
        foreach (var it in items)
        {
            if (it == null || Is(it, "ToolStripSeparator") || !string.Equals(MenuName(it), text, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Get<bool>(it, "Enabled")) { why = $"the menu item \"{text}\" is disabled"; return null; }
            var b = Get<Rectangle>(it, "Bounds");
            var p1 = new Win32.POINT { X = b.Left, Y = b.Top }; var p2 = new Win32.POINT { X = b.Right, Y = b.Bottom };
            if (b.Width <= 0 || !Win32.ClientToScreen(h, ref p1) || !Win32.ClientToScreen(h, ref p2)) { why = $"the menu item \"{text}\" is not in view"; return null; }
            return (h, (p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2, new ScreenRect(p1.X, p1.Y, p2.X, p2.Y), it);
        }
        why = $"no menu item \"{text}\" in the drop-down";
        return null;
    }
}
