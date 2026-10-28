using System.Runtime.InteropServices;
using System.Text;
using Retrace.Engine.Recordings;

namespace Retrace.AutoCad;

/// <summary>
/// AutoCAD's native palettes (no .NET content; AutoCAD Electrical's Project Manager is the one this was built on): the standard Windows
/// controls in them, read while recording and driven on replay. Recording: a click on a tree item (<c>SysTreeView32</c>: its path of item
/// texts, click / double-click / right-click), a click on a push button (<c>Button</c>: id, position, tool tip), and the entry picked in a
/// drop-down list (<c>ComboBox</c>: watched after the person clicks it, written once the list is closed and the text changed). Replay
/// finds the control again by kind, position and id and acts on it in process: tree items are selected and given the mouse messages
/// sent (not posted) to the tree at the item, which is what made the Project Manager open a drawing (seen 2026-10-28, R25.1, outside any
/// command; posted messages and a WM_NOTIFY NM_DBLCLK did not); buttons get BM_CLICK; drop-downs CB_SETCURSEL plus the CBN_SELENDOK and
/// CBN_SELCHANGE notifications. UI thread only.
/// </summary>
static class NativePalette
{
    /// <summary>A palette with native content: its window (the palette, its tab in the set), its name and the set's title.</summary>
    public sealed record Spot(IntPtr Page, string Palette, string Set);

    /// <summary>One native action, ready to write: the <c>native_click</c> / <c>native_select</c> fields.</summary>
    public sealed record Input(Spot Where, string Control, string Action, string[]? Path, int Index, int Id, string? Label, string? Value, int Entry = 0);

    const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_COMMAND = 0x0111;
    const uint TV_FIRST = 0x1100, TVM_EXPAND = TV_FIRST + 2, TVM_GETITEMRECT = TV_FIRST + 4, TVM_GETNEXTITEM = TV_FIRST + 10, TVM_SELECTITEM = TV_FIRST + 11,
        TVM_HITTEST = TV_FIRST + 17, TVM_ENSUREVISIBLE = TV_FIRST + 20, TVM_GETITEMW = TV_FIRST + 62;
    const int TVGN_ROOT = 0, TVGN_NEXT = 1, TVGN_PARENT = 3, TVGN_CHILD = 4, TVGN_CARET = 9, TVE_EXPAND = 2;
    const uint TVHT_ONITEM = 0x46;   // icon, label or state icon (not the +/- button, not the indent)
    const int WS_CHILD = 0x40000000;
    const uint LB_ITEMFROMPOINT = 0x01A9, TVIF_STATE = 8, TVIS_BOLD = 0x10, CBS_HASSTRINGS = 0x0200;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    const uint BM_CLICK = 0x00F5, CB_GETCURSEL = 0x0147, CB_GETLBTEXT = 0x0148, CB_GETLBTEXTLEN = 0x0149, CB_SETCURSEL = 0x014E,
        CB_GETDROPPEDSTATE = 0x0157, CB_FINDSTRINGEXACT = 0x0158;
    const int CBN_SELCHANGE = 1, CBN_SELENDOK = 9;
    const uint TTM_GETTEXTW = 0x0438, TTM_GETTOOLCOUNT = 0x040D, TTM_ENUMTOOLSW = 0x043A;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct TVHITTESTINFO { public POINT pt; public uint flags; public IntPtr hItem; }
    [StructLayout(LayoutKind.Sequential)] struct TVITEMW { public uint mask; public IntPtr hItem; public uint state, stateMask; public IntPtr pszText; public int cchTextMax, iImage, iSelectedImage, cChildren; public IntPtr lParam; }
    [StructLayout(LayoutKind.Sequential)] struct TOOLINFOW { public uint cbSize, uFlags; public IntPtr hwnd, uId; public RECT rect; public IntPtr hinst, lpszText, lParam, lpReserved; }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, EnumProc cb, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr Send(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] static extern IntPtr SendText(IntPtr h, uint m, IntPtr w, string l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendHit(IntPtr h, uint m, IntPtr w, ref TVHITTESTINFO l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendItem(IntPtr h, uint m, IntPtr w, ref TVITEMW l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendTool(IntPtr h, uint m, IntPtr w, ref TOOLINFOW l);

    // ---- where input landed -------------------------------------------------------------------------------------------------

    /// <summary>The native palette <paramref name="h"/> is in, or null: the same chain as <see cref="PaletteWatch.Locate"/> (palette AfxWnd,
    /// set AfxWnd, AfxControlBar), but with no WinForms or WPF window on the way up. Those palettes are <see cref="PaletteWatch"/>'s; the
    /// ribbon and Retrace's guide are .NET content too.</summary>
    public static Spot? Locate(IntPtr h)
    {
        for (int i = 0; i < 40 && h != IntPtr.Zero; i++, h = GetParent(h))
        {
            // GetParent of a pop-up window is its owner: a dialog the palette opened is not in the palette
            if ((GetWindowLong(h, -16) & WS_CHILD) == 0) return null;
            var cls = Native.ClassOf(h);
            if (cls.StartsWith("WindowsForms", StringComparison.Ordinal) || cls.StartsWith("HwndWrapper", StringComparison.Ordinal)) return null;
            if (!cls.StartsWith("AfxWnd", StringComparison.Ordinal)) continue;
            var set = GetParent(h);
            if (!Native.ClassOf(set).StartsWith("AfxWnd", StringComparison.Ordinal) || !Native.ClassOf(GetParent(set)).StartsWith("AfxControlBar", StringComparison.Ordinal)) continue;
            var name = Native.TitleOf(h);
            if (name.Length == 0) continue;
            return new Spot(h, name, Native.TitleOf(set));
        }
        return null;
    }

    /// <summary>The visible controls of window class <paramref name="cls"/> under <paramref name="page"/>, in window order; all of them when
    /// none is visible (an auto-hidden or rolled-up palette that replay drives hidden).</summary>
    static List<IntPtr> Controls(IntPtr page, string cls)
    {
        var all = new List<IntPtr>();
        EnumChildWindows(page, (c, _) => { if (Native.ClassOf(c) == cls) all.Add(c); return true; }, IntPtr.Zero);
        var shown = all.Where(IsWindowVisible).ToList();
        return shown.Count > 0 ? shown : all;
    }

    /// <summary>An owner-drawn list without CBS_HASSTRINGS: its entries hold data, not text (AutoCAD Electrical's project list).</summary>
    static bool HoldsNoText(IntPtr combo)
    {
        var style = GetWindowLong(combo, -16);
        return (style & 0x30 /* CBS_OWNERDRAWFIXED | CBS_OWNERDRAWVARIABLE */) != 0 && (style & CBS_HASSTRINGS) == 0;
    }

    // ---- recording ----------------------------------------------------------------------------------------------------------

    sealed class WatchedCombo { public required IntPtr Combo; public required Spot Where; public int Index, Id, Sel; }
    static WatchedCombo? _combo;
    // native_select events written without their entry's text (an owner-drawn list): named once the palette has settled
    static readonly List<(long N, long Since, Spot Where)> _unnamed = new();

    public static void Clear() { _combo = null; _unnamed.Clear(); }

    /// <summary>The recorder wrote <paramref name="a"/> as event <paramref name="n"/>: an entry picked without its text gets named later (<see cref="Tick"/>).</summary>
    public static void Written(Input a, long n, long nowMs)
    {
        if (a.Control == "combo" && a.Value == null && n > 0) _unnamed.Add((n, nowMs, a.Where));
    }

    /// <summary>A click aimed at <paramref name="hwnd"/> (PreTranslateMessage, before the control handles it). Returns what to record, or null:
    /// a click on a tree item (not its +/- button), on an enabled push button; a click on a drop-down starts watching it (see <see cref="Tick"/>).</summary>
    public static Input? OnClick(IntPtr hwnd, uint message, IntPtr lParam)
    {
        var cls = Native.ClassOf(hwnd);
        // an entry clicked in the open list of the drop-down being watched (the list is a top-level window): recorded now, before the palette
        // reacts (AutoCAD Electrical switches the active project, shows "Updating..." and runs AEMTRELOAD), so that belongs to this step
        if (cls == "ComboLBox" && _combo is { } w && message == WM_LBUTTONDOWN && Native.Exists(w.Combo) && ListOf(w.Combo) == hwnd)
        {
            var at = (long)Send(hwnd, LB_ITEMFROMPOINT, IntPtr.Zero, lParam);
            if (((at >> 16) & 0xFFFF) != 0) return null;   // below the last entry
            var entry = (int)(at & 0xFFFF);
            if (entry == w.Sel) return null;   // the entry already shown: nothing changes
            w.Sel = entry;
            return new Input(w.Where, "combo", "select", null, w.Index, w.Id, null, EntryText(w.Combo, entry), entry + 1);
        }
        if (cls is not ("SysTreeView32" or "Button" or "ComboBox" or "Edit")) return null;
        var spot = Locate(hwnd);
        if (spot == null) return null;
        if (cls is "ComboBox" or "Edit")
        {
            var combo = cls == "Edit" ? GetParent(hwnd) : hwnd;   // the edit box of a drop-down combo
            if (Native.ClassOf(combo) != "ComboBox" || message == WM_RBUTTONDOWN) return null;
            var all = Controls(spot.Page, "ComboBox");
            _combo = new WatchedCombo { Combo = combo, Where = spot, Index = all.IndexOf(combo) + 1, Id = GetDlgCtrlID(combo), Sel = Selected(combo) };
            return null;
        }
        if (cls == "Button")
        {
            if (message != WM_LBUTTONDOWN || !IsWindowEnabled(hwnd)) return null;
            var all = Controls(spot.Page, "Button");
            var label = Native.TitleOf(hwnd).Replace("&", "").Trim();
            if (label.Length == 0) label = ToolTip(hwnd) ?? "";
            return new Input(spot, "button", "click", null, all.IndexOf(hwnd) + 1, GetDlgCtrlID(hwnd), label.Length > 0 ? label : null, null);
        }
        var action = message switch { WM_LBUTTONDOWN => "click", WM_LBUTTONDBLCLK => "dblclick", WM_RBUTTONDOWN => "rclick", _ => null };
        if (action == null) return null;
        var hit = new TVHITTESTINFO { pt = new POINT { X = (short)((long)lParam & 0xFFFF), Y = (short)(((long)lParam >> 16) & 0xFFFF) } };
        var item = SendHit(hwnd, TVM_HITTEST, IntPtr.Zero, ref hit);
        if (item == IntPtr.Zero || (hit.flags & TVHT_ONITEM) == 0) return null;   // the +/- button or empty space: expanding is not a step
        var path = new List<string>();
        for (var it = item; it != IntPtr.Zero && path.Count < 50; it = Send(hwnd, TVM_GETNEXTITEM, (IntPtr)TVGN_PARENT, it)) path.Insert(0, ItemText(hwnd, it));
        var trees = Controls(spot.Page, "SysTreeView32");
        return new Input(spot, "tree", action, path.ToArray(), trees.IndexOf(hwnd) + 1, GetDlgCtrlID(hwnd), null, null);
    }

    /// <summary>Timer tick while recording (and at a checkpoint or Stop, <paramref name="force"/>). Select: an entry picked in the watched
    /// drop-down some other way than a click in its list (the keyboard), once the list is closed. Value: the text of an entry picked from a
    /// list that holds none, once no modal window is open and 700 ms have passed: the palette's tree shows the active item in bold (AutoCAD
    /// Electrical's active project), else nothing.</summary>
    public static (Input? Select, (long For, string Value)? Value) Tick(bool modalOpen, long nowMs, bool force = false)
    {
        (long, string)? value = null;
        // the oldest one first; a later pick names its own entry on a later tick
        if (_unnamed.Count > 0 && !modalOpen && (force || nowMs - _unnamed[0].Since >= 700))
        {
            var u = _unnamed[0];
            _unnamed.RemoveAt(0);
            // a pick made after this one may have changed the active item already: then this entry stays unnamed (replayed by position)
            var text = _unnamed.Count == 0 && Native.Exists(u.Where.Page) ? BoldItem(u.Where.Page) : null;
            if (text != null) value = (u.N, text);
        }
        var w = _combo;
        if (w == null || !Native.Exists(w.Combo)) { _combo = null; return (null, value); }
        if (Send(w.Combo, CB_GETDROPPEDSTATE, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero) return (null, value);
        // watched only while the person works in it (it has the focus): a change the palette makes itself is not their pick
        if (GetFocus() != w.Combo) { _combo = null; return (null, value); }
        var sel = Selected(w.Combo);
        if (sel == w.Sel || sel < 0) return (null, value);
        w.Sel = sel;
        return (new Input(w.Where, "combo", "select", null, w.Index, w.Id, null, EntryText(w.Combo, sel), sel + 1), value);
    }

    [DllImport("user32.dll")] static extern IntPtr GetFocus();

    /// <summary>The window of a combo box's drop-down list, or zero.</summary>
    static IntPtr ListOf(IntPtr combo)
    {
        var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
        return GetComboBoxInfo(combo, ref info) ? info.hwndList : IntPtr.Zero;
    }

    static int Selected(IntPtr combo) => (int)Send(combo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);

    /// <summary>The text of entry <paramref name="i"/>, or null when the list holds none (owner-drawn without CBS_HASSTRINGS: CB_GETLBTEXT
    /// would return the entry's data, not text).</summary>
    static string? EntryText(IntPtr combo, int i)
    {
        if (HoldsNoText(combo)) return null;
        var len = (int)Send(combo, CB_GETLBTEXTLEN, (IntPtr)i, IntPtr.Zero);
        if (len <= 0 || len > 4096) return null;
        var buf = Marshal.AllocHGlobal((len + 1) * 2);
        try { Send(combo, CB_GETLBTEXT, (IntPtr)i, buf); return Marshal.PtrToStringUni(buf); }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The bold top-level item of the palette's first tree (AutoCAD Electrical: the active project), or null.</summary>
    static string? BoldItem(IntPtr page)
    {
        var tree = Controls(page, "SysTreeView32").FirstOrDefault();
        if (tree == IntPtr.Zero) return null;
        for (var it = Send(tree, TVM_GETNEXTITEM, (IntPtr)TVGN_ROOT, IntPtr.Zero); it != IntPtr.Zero; it = Send(tree, TVM_GETNEXTITEM, (IntPtr)TVGN_NEXT, it))
        {
            var item = new TVITEMW { mask = TVIF_STATE, hItem = it, stateMask = TVIS_BOLD };
            SendItem(tree, TVM_GETITEMW, IntPtr.Zero, ref item);
            if ((item.state & TVIS_BOLD) != 0) return ItemText(tree, it);
        }
        return null;
    }

    static string ItemText(IntPtr tree, IntPtr item)
    {
        var buf = Marshal.AllocHGlobal(1026);
        try
        {
            var it = new TVITEMW { mask = 1 /* TVIF_TEXT */, hItem = item, pszText = buf, cchTextMax = 512 };
            SendItem(tree, TVM_GETITEMW, IntPtr.Zero, ref it);
            return Marshal.PtrToStringUni(it.pszText == IntPtr.Zero ? buf : it.pszText) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The tool tip of a button with no text (the Project Manager's icon buttons), from the tool tip controls of this thread: the tool
    /// registered for that window, or for a rectangle of its parent around it. Null when there is none.</summary>
    static string? ToolTip(IntPtr button)
    {
        string? found = null;
        var parent = GetParent(button);
        GetWindowRect(button, out var br);
        EnumThreadWindows(GetCurrentThreadId(), (top, _) =>
        {
            if (Native.ClassOf(top) != "tooltips_class32") return true;
            var count = (int)Send(top, TTM_GETTOOLCOUNT, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < count && i < 500 && found == null; i++)
            {
                var ti = new TOOLINFOW { cbSize = (uint)Marshal.SizeOf<TOOLINFOW>() };
                if (SendTool(top, TTM_ENUMTOOLSW, (IntPtr)i, ref ti) == IntPtr.Zero) continue;
                bool idIsWindow = (ti.uFlags & 1) != 0;   // TTF_IDISHWND
                bool match = idIsWindow ? ti.uId == button
                    : ti.hwnd == parent && Contains(parent, ti.rect, br);
                if (!match) continue;
                var buf = Marshal.AllocHGlobal(1026);
                try
                {
                    ti.lpszText = buf;
                    Marshal.WriteInt16(buf, 0);
                    SendTool(top, TTM_GETTEXTW, (IntPtr)512, ref ti);
                    var t = Marshal.PtrToStringUni(buf)?.Trim();
                    if (!string.IsNullOrEmpty(t)) found = t.Split('\n')[0].Trim();
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return found == null;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>true if the client rectangle <paramref name="r"/> of <paramref name="parent"/> contains the middle of the screen rectangle <paramref name="screen"/>.</summary>
    static bool Contains(IntPtr parent, RECT r, RECT screen)
    {
        var mid = new POINT { X = (screen.L + screen.R) / 2, Y = (screen.T + screen.B) / 2 };
        var tl = new POINT { X = r.L, Y = r.T }; ClientToScreen(parent, ref tl);
        var br = new POINT { X = r.R, Y = r.B }; ClientToScreen(parent, ref br);
        return mid.X >= tl.X && mid.X < br.X && mid.Y >= tl.Y && mid.Y < br.Y;
    }

    // ---- replay -------------------------------------------------------------------------------------------------------------

    /// <summary>Does the step's native action: null when it is done or under way, else what is wrong (the caller retries for a while: the
    /// palette may still be opening, a button may still be disabled, the project may still be switching). The action itself runs from the
    /// dispatcher right after the caller's tick (<see cref="Later"/>): what it opens (a modal dialog, "Updating...") then runs with the
    /// replay's ticks going on, which wait for it, fill it or stop for a person, as after any other step. <paramref name="cursor"/>: a
    /// screen point to rest the mouse on first (a tree item), or null.</summary>
    public static string? Run(Retrace.Core.Step s, Action<int, int>? cursor)
    {
        var a = s.Native!;
        var page = PaletteFill.FindPage(s);
        if (page == IntPtr.Zero) return $"the {s.DialogTitle} palette is not open";
        var cls = a.Control switch { "tree" => "SysTreeView32", "button" => "Button", _ => "ComboBox" };
        var all = Controls(page, cls);
        // by id where the recording has one and it is unique, else by position
        var byId = a.Id != 0 ? all.Where(c => GetDlgCtrlID(c) == a.Id).ToList() : new List<IntPtr>();
        var ctl = byId.Count == 1 ? byId[0] : a.Index <= all.Count ? all[a.Index - 1] : IntPtr.Zero;
        if (ctl == IntPtr.Zero) return $"no {a.Control} number {a.Index} in the {s.DialogTitle} palette";
        switch (a.Control)
        {
            case "button":
                if (!IsWindowEnabled(ctl)) return $"the {a.Target} button is disabled";
                Later("button", () => Send(ctl, BM_CLICK, IntPtr.Zero, IntPtr.Zero));
                return null;
            case "combo":
                return Combo(page, ctl, a);
            default:
                return Tree(ctl, a, cursor);
        }
    }

    /// <summary>Runs <paramref name="act"/> from the dispatcher after the current tick: a native control handles the message inside the call
    /// and may run a modal loop there.</summary>
    static void Later(string what, Action act) =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal,
            () => { try { act(); } catch (Exception ex) { Log.Error("native palette " + what, ex); } });

    static IntPtr _pickedIn; static int _pickedEntry = -1; static DateTime _pickedAt;   // the last pick sent, so a retry does not pick again at once

    /// <summary>Picks the entry: by its text when the list holds text, else by its position. An entry named by the palette's active item
    /// (AutoCAD Electrical's project list) is done when that item is bold, whatever the list shows; else when the list shows the entry. A
    /// retry while the palette is still switching does not pick again; after 5 s it does.</summary>
    static string? Combo(IntPtr page, IntPtr combo, NativeAction a)
    {
        int i = !HoldsNoText(combo) ? (int)SendText(combo, CB_FINDSTRINGEXACT, (IntPtr)(-1), a.Value ?? "") : a.Entry - 1;
        if (i < 0 || i >= (int)Send(combo, 0x0146 /* CB_GETCOUNT */, IntPtr.Zero, IntPtr.Zero)) return $"the drop-down list has no entry {a.Target}";
        bool named = HoldsNoText(combo) && a.Value != null;
        bool Done() => named ? string.Equals(BoldItem(page), a.Value, StringComparison.OrdinalIgnoreCase) : Selected(combo) == i;
        if (Done()) return null;
        if (_pickedIn != combo || _pickedEntry != i || DateTime.Now - _pickedAt > TimeSpan.FromSeconds(5))
        {
            _pickedIn = combo; _pickedEntry = i; _pickedAt = DateTime.Now;
            Later("drop-down", () =>
            {
                var before = Selected(combo);
                // as a person does it: open the list and click the entry in it. AutoCAD Electrical's project list ignores CB_SETCURSEL with the
                // CBN_SELENDOK / CBN_SELCHANGE notifications (seen 2026-10-28: the list showed the entry, the active project stayed)
                var how = ClickEntry(combo, i) ? "list click" : "notifications";
                if (how == "notifications")
                {
                    Send(combo, CB_SETCURSEL, (IntPtr)i, IntPtr.Zero);
                    var id = GetDlgCtrlID(combo);
                    Send(GetParent(combo), WM_COMMAND, (IntPtr)((CBN_SELENDOK << 16) | (id & 0xFFFF)), combo);
                    Send(GetParent(combo), WM_COMMAND, (IntPtr)((CBN_SELCHANGE << 16) | (id & 0xFFFF)), combo);
                }
                Log.Info($"native combo ({how}): entry {before} -> {i} (now {Selected(combo)}), active item {BoldItem(page) ?? "none"}");
            });
            return $"picking {a.Target}";   // checked on the next try
        }
        return named ? $"{a.Value} is not active yet (the palette shows {BoldItem(page) ?? "nothing"} as active)" : $"the drop-down list did not take {a.Target}";
    }

    [StructLayout(LayoutKind.Sequential)] struct COMBOBOXINFO { public int cbSize; public RECT rcItem, rcButton; public int stateButton; public IntPtr hwndCombo, hwndItem, hwndList; }
    [DllImport("user32.dll")] static extern bool GetComboBoxInfo(IntPtr h, ref COMBOBOXINFO i);

    /// <summary>Opens the drop-down list and sends a click to entry <paramref name="i"/> in it (the list closes and the combo box tells its
    /// parent, as after a person's click). False when the list could not be found or the entry is not in view.</summary>
    static bool ClickEntry(IntPtr combo, int i)
    {
        var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
        if (!GetComboBoxInfo(combo, ref info) || info.hwndList == IntPtr.Zero) return false;
        Send(combo, 0x014F /* CB_SHOWDROPDOWN */, (IntPtr)1, IntPtr.Zero);
        var list = info.hwndList;
        Send(list, 0x0197 /* LB_SETTOPINDEX */, (IntPtr)i, IntPtr.Zero);
        var buf = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        RECT r;
        try
        {
            if ((long)Send(list, 0x0198 /* LB_GETITEMRECT */, (IntPtr)i, buf) == -1) { Send(combo, 0x014F, IntPtr.Zero, IntPtr.Zero); return false; }
            r = Marshal.PtrToStructure<RECT>(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
        var lp = (IntPtr)((((r.T + r.B) / 2) << 16) | (((r.L + r.R) / 2) & 0xFFFF));
        Send(list, 0x0200 /* WM_MOUSEMOVE */, IntPtr.Zero, lp);
        Send(list, WM_LBUTTONDOWN, (IntPtr)1, lp);
        Send(list, WM_LBUTTONUP, IntPtr.Zero, lp);
        if (IsWindowVisible(list)) Send(combo, 0x014F, IntPtr.Zero, IntPtr.Zero);   // still open: close it
        return true;
    }

    static string? Tree(IntPtr tree, NativeAction a, Action<int, int>? cursor)
    {
        var item = IntPtr.Zero;
        var level = Send(tree, TVM_GETNEXTITEM, (IntPtr)TVGN_ROOT, IntPtr.Zero);
        for (int d = 0; d < a.Path.Count; d++)
        {
            if (d > 0)
            {
                Send(tree, TVM_EXPAND, (IntPtr)TVE_EXPAND, item);
                level = Send(tree, TVM_GETNEXTITEM, (IntPtr)TVGN_CHILD, item);
            }
            item = IntPtr.Zero;
            for (var it = level; it != IntPtr.Zero; it = Send(tree, TVM_GETNEXTITEM, (IntPtr)TVGN_NEXT, it))
                if (string.Equals(ItemText(tree, it), a.Path[d], StringComparison.OrdinalIgnoreCase)) { item = it; break; }
            if (item == IntPtr.Zero) return $"no item {a.Path[d]}" + (d > 0 ? $" under {string.Join(" › ", a.Path.Take(d))}" : "") + " in the tree";
        }
        Send(tree, TVM_SELECTITEM, (IntPtr)TVGN_CARET, item);
        if (a.Action == "rclick") return null;   // the context menu itself is not replayed; the command picked from it follows as typed
        Send(tree, TVM_ENSUREVISIBLE, IntPtr.Zero, item);
        var r = ItemRect(tree, item);
        var pt = new POINT { X = Math.Min(r.L + 8, (r.L + r.R) / 2), Y = (r.T + r.B) / 2 };
        var screen = pt; ClientToScreen(tree, ref screen);
        cursor?.Invoke(screen.X, screen.Y);   // Electrical reads where the mouse is when it handles the click
        var lp = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
        Later("tree", () =>
        {
            Send(tree, WM_LBUTTONDOWN, (IntPtr)1, lp);
            Send(tree, WM_LBUTTONUP, IntPtr.Zero, lp);
            if (a.Action == "dblclick")
            {
                Send(tree, WM_LBUTTONDBLCLK, (IntPtr)1, lp);
                Send(tree, WM_LBUTTONUP, IntPtr.Zero, lp);
            }
        });
        return null;
    }

    /// <summary>The label rectangle of a tree item (TVM_GETITEMRECT takes the item handle in the rectangle it fills).</summary>
    static RECT ItemRect(IntPtr tree, IntPtr item)
    {
        var buf = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.WriteIntPtr(buf, item);
            Send(tree, TVM_GETITEMRECT, (IntPtr)1, buf);
            return Marshal.PtrToStructure<RECT>(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
