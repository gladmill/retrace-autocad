using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>The screen rectangle of what a person clicked (a tree item's label, a list view or list box item, a push button), for the green frame and arrow on the pictures of the test document.
/// Read before the click is dispatched, in the message hook; null when it cannot be read: a picture with no rectangle gets no frame and no arrow (never a guessed spot). UI thread.</summary>
static class ClickRects
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    const uint LVM_GETITEMRECT = 0x100E, LB_ITEMFROMPOINT = 0x01A9, LB_GETITEMRECT = 0x0198, SMTO_ABORTIFHUNG = 2, TimeoutMs = 300;

    static ScreenRect? ToScreen(IntPtr h, int l, int t, int r, int b)
    {
        if (r <= l || b <= t) return null;
        var tl = new POINT { X = l, Y = t }; var br = new POINT { X = r, Y = b };
        return ClientToScreen(h, ref tl) && ClientToScreen(h, ref br) ? new ScreenRect(tl.X, tl.Y, br.X, br.Y) : null;
    }

    /// <summary>A tree item's label.</summary>
    public static ScreenRect? TreeItem(IntPtr tree, IntPtr item) =>
        TreeControl.TextRect(tree, item) is { } r ? ToScreen(tree, r.L, r.T, r.R, r.B) : null;

    /// <summary>A list view item's bounds (icon and label), whatever the view mode.</summary>
    public static ScreenRect? ListViewItem(IntPtr list, int index)
    {
        if (index < 0) return null;
        var buf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(buf, 0, 0); Marshal.WriteInt64(buf, 8, 0);
            Marshal.WriteInt32(buf, 0, 0);   // LVIR_BOUNDS
            Native.SendNum(list, LVM_GETITEMRECT, (IntPtr)index, buf, SMTO_ABORTIFHUNG, TimeoutMs, out var ok);
            return ok == IntPtr.Zero ? null : ToScreen(list, Marshal.ReadInt32(buf, 0), Marshal.ReadInt32(buf, 4), Marshal.ReadInt32(buf, 8), Marshal.ReadInt32(buf, 12));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The list box item under a client point: its index, or -1 (empty space).</summary>
    public static int ListBoxIndexAt(IntPtr list, int x, int y)
    {
        Native.SendNum(list, LB_ITEMFROMPOINT, IntPtr.Zero, Native.MakeLParam(x, y), SMTO_ABORTIFHUNG, TimeoutMs, out var r);
        long v = (long)r;
        return ((v >> 16) & 0xFFFF) != 0 ? -1 : (int)(v & 0xFFFF);
    }

    public static ScreenRect? ListBoxItem(IntPtr list, int index)
    {
        if (index < 0) return null;
        var buf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(buf, 0, 0); Marshal.WriteInt64(buf, 8, 0);
            Native.SendNum(list, LB_GETITEMRECT, (IntPtr)index, buf, SMTO_ABORTIFHUNG, TimeoutMs, out var ok);
            return (long)ok == -1 ? null : ToScreen(list, Marshal.ReadInt32(buf, 0), Marshal.ReadInt32(buf, 4), Marshal.ReadInt32(buf, 8), Marshal.ReadInt32(buf, 12));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The text of a list view item: "" when it cannot be read.</summary>
    public static string ListViewText(IntPtr list, int index) => MenuListView.ItemText(list, index);

    /// <summary>What a mouse message at the client point <paramref name="lParam"/> of <paramref name="h"/> clicked: the tree item, the list view or list box item under it, else the window itself (a button, a picture, a control).</summary>
    public static ScreenRect? Of(IntPtr h, IntPtr lParam, bool pictureBox = false)
    {
        try
        {
            int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
            switch (DialogReader.Segment(DialogReader.ClassOf(h)))
            {
                case "systreeview32":
                    return TreeControl.ItemAt(h, lParam) is var it && it != IntPtr.Zero ? TreeItem(h, it) : null;
                case "syslistview32":
                    return Native.OwnedByThisThread(h) ? ListViewItem(h, MenuListView.HitTest(h, x, y)) : null;
                case "listbox":
                    return Native.OwnedByThisThread(h) ? ListBoxItem(h, ListBoxIndexAt(h, x, y)) : null;
                default:
                    // a push button (or, when the caller knows it is one, a picture box) is its own target; a panel, a label, a WPF palette's host window is not: the whole palette is no place to point at
                    return pictureBox || DialogReader.PushButtonText(h) != null ? Window(h) : null;
            }
        }
        catch (Exception ex) { Log.Error("click rect", ex); return null; }
    }

    /// <summary>A window's own rectangle (a push button).</summary>
    public static ScreenRect? Window(IntPtr h) => h != IntPtr.Zero && GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top ? new ScreenRect(r.Left, r.Top, r.Right, r.Bottom) : null;
}
