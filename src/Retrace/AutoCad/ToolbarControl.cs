using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>One visible button of a toolbar, as the TBBUTTON says: its position, command id, whether it is enabled and checked (<c>fsState</c>), and its style byte (<c>fsStyle</c>, BTNS_*: <see cref="ToolbarPress"/>). No text:
/// the name of a button is read only when recording (<see cref="ToolbarControl.Label"/>).</summary>
readonly record struct ToolbarButton(int Index, int Id, bool Enabled, bool Checked, int Style);

/// <summary>
/// A toolbar (<c>ToolbarWindow32</c>, an MFC CToolBar) of a dialog or frame of this thread. Every function first checks the window class: the TB_ messages are in the private WM_USER range and are sent to nothing but a
/// window of that class. Its buttons are not windows: they are read with the TB_ messages (all pointers are to buffers of this call, so only a toolbar of the calling thread, whose messages are handled inside the call), a
/// click is recognised by the hit test of the mouse-down, and the name of a button is its text or, for an icon-only button, the tooltip the owner gives (the TTN_NEEDTEXT notification the toolbar's own tooltip would send; asked
/// only while recording, once per button). A replay finds a button by its command id and presses it by posting the WM_COMMAND the toolbar itself sends (command id, notification 0, the toolbar as the control) to the
/// toolbar's parent, as the control does (a toolbar's notify window can differ from its parent, and nothing reads it: see TODO.md). TB_PRESSBUTTON only changes how the button looks and runs no command, so it is not used. The rules of what may be pressed this way are in <see cref="ToolbarPress"/>.
/// </summary>
static class ToolbarControl
{
    const uint WM_COMMAND = 0x0111, TB_GETEXTENDEDSTYLE = 0x0455;
    const uint TB_CHECKBUTTON = 0x0402, TB_GETBUTTON = 0x0417, TB_BUTTONCOUNT = 0x0418, TB_COMMANDTOINDEX = 0x0419, TB_GETITEMRECT = 0x041D, TB_HITTEST = 0x0445, TB_GETBUTTONTEXTW = 0x044B;
    const byte TBSTATE_CHECKED = 0x01, TBSTATE_ENABLED = 0x04, TBSTATE_HIDDEN = 0x08;
    const int TTN_NEEDTEXTW = -530;
    const byte BTNS_SEP = 0x01;

    static IntPtr Send(IntPtr h, uint msg, IntPtr w, IntPtr l) => Native.SendTimed(h, msg, w, l);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] static extern bool Post(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    /// <summary>Is this window a toolbar of this thread? (the one test every TB_ message is behind)</summary>
    public static bool Is(IntPtr h) => h != IntPtr.Zero && Native.OwnedByThisThread(h) && NativeParts.IsToolbarClass(DialogReader.ClassOf(h));

    /// <summary>The visible buttons (no separators or hidden ones), from the TBBUTTON of each: state and style come with it. Empty when the window is no toolbar of this thread.</summary>
    public static List<ToolbarButton> Buttons(IntPtr tb)
    {
        var list = new List<ToolbarButton>();
        try
        {
            if (!Is(tb)) return list;
            int n = (int)Send(tb, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < Math.Min(n, 200); i++)
                if (ReadButton(tb, i) is { } b) list.Add(b);
        }
        catch (Exception ex) { Log.Error("toolbar buttons", ex); }
        return list;
    }

    /// <summary>The button at a position (TB_GETBUTTON), or null for a separator, a hidden one or a position that is none.</summary>
    static ToolbarButton? ReadButton(IntPtr tb, int index)
    {
        var buf = Marshal.AllocHGlobal(64);
        try
        {
            for (int z = 0; z < 64; z += 8) Marshal.WriteInt64(buf, z, 0);
            if (Send(tb, TB_GETBUTTON, (IntPtr)index, buf) == IntPtr.Zero) return null;
            int id = Marshal.ReadInt32(buf, 4);
            byte state = Marshal.ReadByte(buf, 8), style = Marshal.ReadByte(buf, 9);
            if (((style & BTNS_SEP) != 0 && id == 0) || (state & TBSTATE_HIDDEN) != 0) return null;
            return new ToolbarButton(index, id, (state & TBSTATE_ENABLED) != 0, (state & TBSTATE_CHECKED) != 0, style);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The visible button with that command id (TB_COMMANDTOINDEX, then its TBBUTTON), or null.</summary>
    public static ToolbarButton? Find(IntPtr tb, int id)
    {
        if (!Is(tb)) return null;
        int index = (int)Send(tb, TB_COMMANDTOINDEX, (IntPtr)id, IntPtr.Zero);
        return index < 0 ? null : ReadButton(tb, index) is { } b && b.Id == id ? b : null;
    }

    /// <summary>The button's own text ("" for an icon-only button). Read only when recording.</summary>
    static string TextOf(IntPtr tb, int id)
    {
        int len = (int)Send(tb, TB_GETBUTTONTEXTW, (IntPtr)id, IntPtr.Zero);
        if (len <= 0 || len > 500) return "";
        var buf = Marshal.AllocHGlobal((len + 2) * 2);
        try
        {
            Send(tb, TB_GETBUTTONTEXTW, (IntPtr)id, buf);
            return Marshal.PtrToStringUni(buf, len);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The button's rectangle in client coordinates (TB_GETITEMRECT by position), or null.</summary>
    static (int L, int T, int R, int B)? ItemRect(IntPtr tb, int index)
    {
        var p = Marshal.AllocHGlobal(16);
        try
        {
            for (int i = 0; i < 16; i += 4) Marshal.WriteInt32(p, i, 0);
            if (Send(tb, TB_GETITEMRECT, (IntPtr)index, p) == IntPtr.Zero) return null;
            return (Marshal.ReadInt32(p, 0), Marshal.ReadInt32(p, 4), Marshal.ReadInt32(p, 8), Marshal.ReadInt32(p, 12));
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>The index of the button under the client point, or -1 (a separator, the gap, a hidden button). TB_HITTEST answers 0 or more for a button and a negative number (the nearest item) for a miss, and 0 is also
    /// what a point beside the first button can give: a result of 0 or more counts only when the point lies inside that button's rectangle (TB_GETITEMRECT).</summary>
    public static int HitTest(IntPtr tb, int x, int y)
    {
        if (!Is(tb)) return -1;
        var p = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(p, 0, x); Marshal.WriteInt32(p, 4, y);
            int index = (int)Send(tb, TB_HITTEST, IntPtr.Zero, p);
            if (index < 0 || ItemRect(tb, index) is not { } r) return -1;
            return ToolbarLabel.Inside(x, y, r.L, r.T, r.R, r.B) ? index : -1;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>The button's rectangle on the screen, for a picture's box (a Tdoc picture boxes the button, not the whole toolbar); null when it cannot be read.</summary>
    public static (int L, int T, int R, int B)? ItemScreenRect(IntPtr tb, int index)
    {
        if (!Is(tb) || ItemRect(tb, index) is not { } r) return null;
        var a = new POINT { X = r.L, Y = r.T }; var b = new POINT { X = r.R, Y = r.B };
        return ClientToScreen(tb, ref a) && ClientToScreen(tb, ref b) ? (a.X, a.Y, b.X, b.Y) : null;
    }

    /// <summary>The toolbar's extended style (TB_GETEXTENDEDSTYLE), 0 for a window that is no toolbar of this thread. Sent to a verified <c>ToolbarWindow32</c> only.</summary>
    public static int ExtendedStyle(IntPtr tb) => Is(tb) ? (int)Send(tb, TB_GETEXTENDEDSTYLE, IntPtr.Zero, IntPtr.Zero) : 0;

    /// <summary>Is the check button checked now (its TBSTATE_CHECKED)?</summary>
    public static bool IsChecked(IntPtr tb, int id) => Find(tb, id) is { Checked: true };

    /// <summary>Puts a check button in a state as the toolbar does for a click (TB_CHECKBUTTON; for a button of a check group the toolbar unchecks the others itself).</summary>
    public static void SetChecked(IntPtr tb, int id, bool check)
    {
        if (Is(tb)) Send(tb, TB_CHECKBUTTON, (IntPtr)id, (IntPtr)(check ? 1 : 0));
    }

    /// <summary>The window the toolbar's WM_COMMAND is for: its parent, as the control itself sends it.</summary>
    public static IntPtr OwnerOf(IntPtr tb) => DialogReader.GetParent(tb);

    // the tooltips found, per toolbar window and command id: only a text is kept (an owner that has none now may have one later), and an entry of a window that is gone is dropped (a handle can be given to a new window)
    static readonly Dictionary<(IntPtr Toolbar, int Id), string> Tips = new();
    static IntPtr _asker;

    /// <summary>The window a tooltip request is sent from: a window of Retrace's own, hidden (message-only), made once. MFC's handler of TTN_NEEDTEXT puts the window the notification names in <c>hwndFrom</c> on top (SetWindowPos
    /// HWND_TOP), a thing that must happen to Retrace's window and never to the program's toolbar or tooltip control.</summary>
    static IntPtr Asker()
    {
        if (_asker != IntPtr.Zero && Native.Exists(_asker)) return _asker;
        _asker = CreateWindowEx(0, "Static", "Retrace tooltip request", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return _asker;
    }

    /// <summary>The tooltip of a button, asked of the owner as the toolbar's tooltip would (TTN_NEEDTEXTW with the command id) from Retrace's own hidden window (<see cref="Asker"/>); "" when it gives none. For a recording only,
    /// once per button: a text is kept, an empty answer is asked again at the next press.</summary>
    public static string Tooltip(IntPtr tb, int id)
    {
        foreach (var gone in Tips.Keys.Where(k => !Native.Exists(k.Toolbar)).ToList()) Tips.Remove(gone);
        if (Tips.TryGetValue((tb, id), out var known)) return known;
        if (!Is(tb)) return "";
        string tip = "";
        var owner = OwnerOf(tb);
        var from = Asker();
        if (owner == IntPtr.Zero || from == IntPtr.Zero) return "";
        try
        {
            // NMTTDISPINFOW: NMHDR (24 bytes), lpszText, szText[80] (at 32), hinst (at 192)
            Native.SendNotify(owner, from, id, TTN_NEEDTEXTW, size: 256, after: buf =>
            {
                var text = Marshal.PtrToStringUni(buf + 32) ?? "";
                if (text.Length == 0 && Marshal.ReadIntPtr(buf, 24) is var p && p.ToInt64() > 0xFFFF && Marshal.ReadIntPtr(buf, 192) == IntPtr.Zero) text = Marshal.PtrToStringUni(p) ?? "";
                tip = text;
            });
        }
        catch (Exception ex) { Log.Error("toolbar tooltip", ex); }
        if (tip.Length > 0) { if (Tips.Count > 500) Tips.Clear(); Tips[(tb, id)] = tip; }
        return tip;
    }

    /// <summary>The name of a button, for a recording, steps.md and messages (never to find a button): its text, else its tooltip (<see cref="ToolbarLabel.Choose"/>). Only while recording.</summary>
    public static string Label(IntPtr tb, int id)
    {
        var own = TextOf(tb, id);
        return ToolbarLabel.Choose(own, own.Trim().Length > 0 ? null : Tooltip(tb, id), id);
    }

    /// <summary>Presses a button the way the toolbar does: posts WM_COMMAND (command id, notification 0, the toolbar as the control) to its owner. Null = posted, else why not; <paramref name="disabled"/>: the reason is that the
    /// button is disabled, which may be the program's update of its state that has not come yet (the caller tries again until the controls time out). The caller has checked <see cref="ToolbarPress.StopFor"/>.</summary>
    public static string? Press(IntPtr tb, int id, out bool disabled)
    {
        disabled = false;
        if (!Is(tb)) return "the window is no toolbar";
        var owner = OwnerOf(tb);
        if (owner == IntPtr.Zero) return "the toolbar has no owner window";
        if (Find(tb, id) is not { } b) return "the toolbar has no such button";
        if (!b.Enabled) { disabled = true; return "the toolbar button is disabled"; }
        return Post(owner, WM_COMMAND, (IntPtr)(id & 0xFFFF), tb) ? null : "could not post the command to the toolbar's owner";
    }
}
