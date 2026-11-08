using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>
/// A tab control (<c>SysTabControl32</c>: a native one, an MFC property sheet's, a WinForms TabControl), read and driven with TCM messages. The one place for the TCM
/// constants, the TCITEM interop, the tab texts and rectangles and the switch: the tab context of a field in a dialog or palette (<see cref="TabPath"/>) is read through it
/// (<see cref="DialogReader"/>), <see cref="DialogAutoFiller"/> and <see cref="PaletteFill"/> switch with <see cref="Select"/>, and <see cref="NativePalette"/> clicks and records tabs.
/// UI thread only. The messages that carry an item or a rectangle pass pointers to memory of this call, so only for a control of the calling thread
/// (<see cref="OwnedByThisThread"/>, as for <see cref="TreeControl"/>). Nothing here throws.
/// </summary>
static class TabControl32
{
    internal const uint TCM_FIRST = 0x1300, TCM_GETITEMCOUNT = TCM_FIRST + 4, TCM_GETITEMRECT = TCM_FIRST + 10, TCM_GETCURSEL = TCM_FIRST + 11, TCM_SETCURSEL = TCM_FIRST + 12,
        TCM_HITTEST = TCM_FIRST + 13, TCM_ADJUSTRECT = TCM_FIRST + 40, TCM_GETITEMW = TCM_FIRST + 60;
    const uint TCIF_TEXT = 1, SMTO_ABORTIFHUNG = 2, TimeoutMs = 2000, WM_NOTIFY = 0x004E;
    const int TCN_FIRST = -550, TCN_SELCHANGE = TCN_FIRST - 1, TCN_SELCHANGING = TCN_FIRST - 2, TextChars = 256, MaxTabs = 200;
    // property sheet messages (WM_USER + n): the current page, and show page n (runs the pages' PSN_KILLACTIVE / PSN_SETACTIVE)
    const uint PSM_SETCURSEL = 0x0400 + 101, PSM_GETTABCONTROL = 0x0400 + 116, PSM_GETCURRENTPAGEHWND = 0x0400 + 118;

    [StructLayout(LayoutKind.Sequential)]
    struct TCITEMW { public uint mask, dwState, dwStateMask; public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct TCHITTESTINFO { public POINT pt; public uint flags; }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendItem(IntPtr h, uint msg, IntPtr w, ref TCITEMW l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendHit(IntPtr h, uint msg, IntPtr w, ref TCHITTESTINFO l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendRect(IntPtr h, uint msg, IntPtr w, ref RECT l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);


    static long Send(IntPtr h, uint msg, long w = 0, long l = 0)
    {
        Native.SendNum(h, msg, (IntPtr)w, (IntPtr)l, SMTO_ABORTIFHUNG, TimeoutMs, out var r);
        return r.ToInt64();
    }

    public static int Count(IntPtr tab) => (int)Math.Min(Send(tab, TCM_GETITEMCOUNT), MaxTabs);

    /// <summary>The index of the selected tab, -1 for none.</summary>
    public static int Selected(IntPtr tab) => (int)Send(tab, TCM_GETCURSEL);

    /// <summary>The text of tab <paramref name="index"/> as shown (mnemonics and a trailing ':' taken out); "" when it has none.</summary>
    public static string Text(IntPtr tab, int index)
    {
        var buf = Marshal.AllocHGlobal((TextChars + 1) * 2);
        try
        {
            Marshal.WriteInt16(buf, 0);
            var it = new TCITEMW { mask = TCIF_TEXT, pszText = buf, cchTextMax = TextChars };
            if (SendItem(tab, TCM_GETITEMW, (IntPtr)index, ref it, SMTO_ABORTIFHUNG, TimeoutMs, out var ok) == IntPtr.Zero || ok == IntPtr.Zero) return "";
            return TabPath.Clean(Marshal.PtrToStringUni(it.pszText == IntPtr.Zero ? buf : it.pszText));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The texts of all tabs, in tab order.</summary>
    public static List<string> Texts(IntPtr tab)
    {
        var list = new List<string>();
        for (int i = 0, n = Count(tab); i < n; i++) list.Add(Text(tab, i));
        return list;
    }

    /// <summary>The area that tab pages take, in screen coordinates (TCM_ADJUSTRECT on the client area); null when the control did not answer.</summary>
    public static (int L, int T, int R, int B)? DisplayRect(IntPtr tab)
    {
        if (!GetClientRect(tab, out var r)) return null;
        if (SendRect(tab, TCM_ADJUSTRECT, IntPtr.Zero, ref r, SMTO_ABORTIFHUNG, TimeoutMs, out _) == IntPtr.Zero && r.R <= r.L) return null;
        var tl = new POINT { X = r.L, Y = r.T }; ClientToScreen(tab, ref tl);
        var br = new POINT { X = r.R, Y = r.B }; ClientToScreen(tab, ref br);
        return (tl.X, tl.Y, br.X, br.Y);
    }

    /// <summary>The rectangle of tab <paramref name="index"/> in the control's client coordinates; null when it has none (scrolled out of a multi-row control, or no answer).</summary>
    public static (int L, int T, int R, int B)? ItemRect(IntPtr tab, int index)
    {
        var r = new RECT();
        if (SendRect(tab, TCM_GETITEMRECT, (IntPtr)index, ref r, SMTO_ABORTIFHUNG, TimeoutMs, out var ok) == IntPtr.Zero || ok == IntPtr.Zero || r.R <= r.L) return null;
        return (r.L, r.T, r.R, r.B);
    }

    /// <summary>The rectangle of tab <paramref name="index"/> on the screen; null when it has none.</summary>
    public static (int L, int T, int R, int B)? ItemScreenRect(IntPtr tab, int index)
    {
        if (ItemRect(tab, index) is not { } r) return null;
        var tl = new POINT { X = r.L, Y = r.T }; ClientToScreen(tab, ref tl);
        var br = new POINT { X = r.R, Y = r.B }; ClientToScreen(tab, ref br);
        return (tl.X, tl.Y, br.X, br.Y);
    }

    /// <summary>The tab under the client point (<paramref name="x"/>, <paramref name="y"/>), -1 when the point is on none.</summary>
    public static int HitTest(IntPtr tab, int x, int y)
    {
        var hit = new TCHITTESTINFO { pt = new POINT { X = x, Y = y } };
        return SendHit(tab, TCM_HITTEST, IntPtr.Zero, ref hit, SMTO_ABORTIFHUNG, TimeoutMs, out var r) == IntPtr.Zero ? -1 : (int)r.ToInt64();
    }

    /// <summary>The index of the tab with <paramref name="text"/> (<see cref="TabPath.SameText"/>), -1 for none.</summary>
    public static int Find(IntPtr tab, string text)
    {
        for (int i = 0, n = Count(tab); i < n; i++) if (TabPath.SameText(Text(tab, i), text)) return i;
        return -1;
    }

    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

    /// <summary>The window that holds the tab control when it looks like a property sheet by its structure alone (no message is sent): a <c>#32770</c> whose own pages, child <c>#32770</c> windows,
    /// sit beside the tab control. Zero otherwise. AutoCAD's tabbed dialogs have that shape too; <see cref="Select"/> asks the window before it trusts it.</summary>
    static IntPtr SheetByStructure(IntPtr tab)
    {
        var sheet = GetParent(tab);
        if (sheet == IntPtr.Zero || Native.ClassOf(sheet) != "#32770") return IntPtr.Zero;
        int n = 0;
        for (var c = GetWindow(sheet, 5 /* GW_CHILD */); c != IntPtr.Zero && n < 200; c = GetWindow(c, 2 /* GW_HWNDNEXT */), n++)
            if (c != tab && Native.ClassOf(c) == "#32770") return sheet;
        return IntPtr.Zero;
    }

    /// <summary>How a tab control is switched, for the log: "WinForms", "property sheet (by structure)" or "tab control". Reads classes only: nothing is sent to the dialog while it is recorded.</summary>
    public static string KindOf(IntPtr tab)
    {
        if (WinFormsControls.IsTabControl(tab)) return "WinForms";
        return SheetByStructure(tab) != IntPtr.Zero ? "property sheet (by structure)" : "tab control";
    }

    /// <summary>The property sheet whose tab control this is: shaped like one (<see cref="SheetByStructure"/>) and, asked on replay, answering PSM_GETCURRENTPAGEHWND with one of its pages and
    /// PSM_GETTABCONTROL with this tab control. Zero for a dialog that merely has a tab control (AutoCAD's own tabbed dialogs, DIMSTYLE's, are such: they show their pages from TCN_SELCHANGE).</summary>
    static IntPtr SheetOf(IntPtr tab)
    {
        var sheet = SheetByStructure(tab);
        if (sheet == IntPtr.Zero) return IntPtr.Zero;
        Native.SendNum(sheet, PSM_GETCURRENTPAGEHWND, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, TimeoutMs, out var page);
        if (page == IntPtr.Zero || !Native.IsInside(sheet, page)) return IntPtr.Zero;
        Native.SendNum(sheet, PSM_GETTABCONTROL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, TimeoutMs, out var control);
        return control == tab ? sheet : IntPtr.Zero;
    }

    /// <summary>Shows tab <paramref name="index"/>, the way the control's owner expects it (on this thread, inside this call: what the tab's pages do when they are left and entered runs now).
    /// WinForms TabControl: its <c>SelectedIndex</c>, through WinForms. A property sheet (MFC and comctl32): PSM_SETCURSEL to the sheet, which runs the pages' activate and kill-active logic;
    /// a page may refuse to be left (invalid input), and then the tab stays. Any other tab control: TCN_SELCHANGING to the owner (it may refuse), TCM_SETCURSEL, TCN_SELCHANGE. Checked
    /// afterwards: null when the tab is showing, else why it is not.</summary>
    public static string? Select(IntPtr tab, int index)
    {
        try
        {
            if (!Native.OwnedByThisThread(tab)) return "the tab control belongs to another thread";
            if (index < 0 || index >= Count(tab)) return $"the tab control has no tab {index + 1}";
            var from = Selected(tab);
            if (from == index) return null;
            var shows = Text(tab, from);
            string how;
            if (WinFormsControls.TrySelectTab(tab, index)) how = "WinForms";
            else if (SheetOf(tab) is var sheet && sheet != IntPtr.Zero)
            {
                how = "PSM_SETCURSEL";
                Native.SendNum(sheet, PSM_SETCURSEL, (IntPtr)index, IntPtr.Zero, SMTO_ABORTIFHUNG, TimeoutMs, out _);
                if (Selected(tab) != index)
                    return $"the {(shows.Length > 0 ? shows : "current")} page did not let go (it refused to be left, or the sheet did not take PSM_SETCURSEL)";
            }
            else
            {
                how = "TCM_SETCURSEL";
                var parent = GetParent(tab);
                if (parent != IntPtr.Zero && Notify(parent, tab, TCN_SELCHANGING) != 0) return $"the dialog does not let go of the {(shows.Length > 0 ? shows : "current")} tab (TCN_SELCHANGING refused)";
                Send(tab, TCM_SETCURSEL, index);
                if (parent != IntPtr.Zero) Notify(parent, tab, TCN_SELCHANGE);
            }
            var now = Selected(tab);
            if (now != index) return $"the tab did not change by {how} (it shows {(now >= 0 ? Text(tab, now) : "none")})";
            Log.Info($"tab: {shows} -> {Text(tab, index)} by {how}");
            return null;
        }
        catch (Exception ex) { Log.Error("tab select", ex); return ex.Message; }
    }

    /// <summary>WM_NOTIFY from the tab control to its parent with <paramref name="code"/>, as the control sends it; the parent's answer (TCN_SELCHANGING: non-zero = stay).</summary>
    static long Notify(IntPtr parent, IntPtr tab, int code) => Native.SendNotify(parent, tab, GetDlgCtrlID(tab), code, size: 24).ToInt64();
}
