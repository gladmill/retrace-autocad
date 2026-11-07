using System.Runtime.InteropServices;
using System.Text;
using Retrace.Core;
using static Retrace.AutoCad.Win32;

namespace Retrace.AutoCad;

/// <summary>
/// Records the context menus of a recording: a right-click (WM_RBUTTONDOWN, the Menu key, Shift+F10) on a control of a tracked dialog or a palette with .NET content, and the menu item
/// the person then chooses. Three kinds of menu, each seen where it can be seen before the item's handler runs (the nested-dialog gate and the dialog's baseline need that):
/// <list type="bullet">
/// <item>Win32 popup menu (TrackPopupMenu): while a right-click is armed, a WH_CALLWNDPROC hook sees WM_ENTERMENULOOP / WM_EXITMENULOOP, WM_MENUSELECT (the highlighted item and its menu) and the posted
/// WM_COMMAND says the item was chosen; <see cref="MenuTracker"/> keeps the path. A menu closed without a choice posts no command and records nothing; a menu shown with TPM_RETURNCMD posts none either (not recorded).</item>
/// <item>WinForms ContextMenuStrip: the button-up on the drop-down window (a ToolStripDropDown, asked through reflection like <see cref="WinFormsControls"/>) names the item under it, when the strip belongs
/// to the control that was right-clicked.</item>
/// <item>WPF ContextMenu: a class handler for <c>MenuItem.Click</c> runs before the item's own handlers.</item>
/// </list>
/// A right-click is armed for what is under it (the item of a tree or list, worked out before the program reacts) and is dropped when the menu session ends without a choice, or when no menu
/// opened within about a second: a choice from a menu that the armed right-click did not open is not credited to it. The control's field key is worked out when the choice is made (one scan, not one per
/// right-click). UI thread only; the hooks are on the UI thread's message loops, so they also see the loops of modal dialogs. Nothing here throws into AutoCAD.
/// </summary>
sealed class MenuWatch
{
    /// <summary>The right-click a menu choice belongs to: the window it went to, where (Spot null = a tracked dialog), the tree or list item under it, and the strip that belongs to the control (WinForms).</summary>
    public sealed record Armed(IntPtr Hwnd, IntPtr Container, PaletteWatch.Spot? Spot, string Item, long AtMs)
    {
        public ShotService.Picture? Picture { get; init; }
        /// <summary>The control's field, worked out when the choice is made.</summary>
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public string Kind { get; init; } = "";
        /// <summary>A list view: what the menu opened on (null = not a list view, or it could not be read).</summary>
        public MenuListView.Opened? ListView { get; init; }
        /// <summary>A list view: the right-click went to the column header.</summary>
        public bool OnHeader { get; init; }
    }

    /// <summary>A menu item was chosen: the choice and where it was right-clicked (Spot null = a tracked dialog).</summary>
    public Action<MenuChoice, Armed>? Chosen { get; set; }
    /// <summary>A picture of the palette as it is now (taken at the right-click, before a menu covers it), or null.</summary>
    public Func<PaletteWatch.Spot, ShotService.Picture?>? PaletteSnap { get; set; }

    public Func<long> Clock { get; set; } = () => 0;
    /// <summary>The innermost tracked dialog's window (zero = none).</summary>
    public Func<IntPtr> DialogTop { get; set; } = () => IntPtr.Zero;
    public bool Enabled { get; set; }

    const uint WM_COMMAND = 0x0111, WM_MENUSELECT = 0x011F, WM_ENTERMENULOOP = 0x0211, WM_EXITMENULOOP = 0x0212, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_RBUTTONDOWN = 0x0204, WM_LBUTTONUP = 0x0202;
    const int WH_GETMESSAGE = 3, WH_CALLWNDPROC = 4;
    /// <summary>A right-click that opened no menu within this long is forgotten.</summary>
    const long OpenWithinMs = 1000;
    /// <summary>After a Win32 menu loop ended, the command of a choice is posted within this long.</summary>
    const long CommandWithinMs = 2000;
    const uint MF_POPUP = 0x10, MF_SYSMENU = 0x2000, MF_BYPOSITION = 0x400;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuStringW(IntPtr menu, uint item, StringBuilder text, int max, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    HookProc? _getProc, _callProc;
    IntPtr _getHook, _callHook;
    Armed? _armed;
    readonly MenuTracker _tracker = new();
    bool _loop, _strip;     // a Win32 menu loop is running; a ContextMenuStrip of the right-clicked control has been seen open
    IntPtr _stripWnd;       // its drop-down window
    long _loopEndMs = -1;

    // ---- on / off ------------------------------------------------------------------------------------------------------------

    /// <summary>Recording started: the message hook that sees the right-clicks, and the WPF class handler.</summary>
    public void Start()
    {
        Stop();
        if (!Enabled) return;
        _getProc ??= OnGet;
        _getHook = SetWindowsHookEx(WH_GETMESSAGE, _getProc, IntPtr.Zero, GetCurrentThreadId());
        if (_getHook == IntPtr.Zero) Log.Error("menu hook", new InvalidOperationException("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error()));
        WpfMenus.Listen(this);
    }

    public void Stop()
    {
        if (_getHook != IntPtr.Zero) { UnhookWindowsHookEx(_getHook); _getHook = IntPtr.Zero; }
        Disarm();
        WpfMenus.Mute(this);
    }

    /// <summary>The right-click is forgotten (and the hook on sent messages removed).</summary>
    void Disarm()
    {
        _armed = null; _loop = false; _strip = false; _stripWnd = IntPtr.Zero; _loopEndMs = -1;
        _tracker.Reset();
        if (_callHook != IntPtr.Zero) { UnhookWindowsHookEx(_callHook); _callHook = IntPtr.Zero; }
    }

    /// <summary>Timer tick: a right-click that opened no menu in time, a menu that closed with no choice, ends its arming.</summary>
    public void Tick(long nowMs)
    {
        var a = _armed;
        if (a == null) return;
        try
        {
            // a ContextMenuStrip (or a submenu of one) that belongs to the control right-clicked and became visible is the menu this right-click opened, whatever way the program showed it
            if (!_strip && !_loop) FindStrip(a);
            bool seen = _loop || _strip || _loopEndMs >= 0;
            if (!seen && nowMs - a.AtMs > OpenWithinMs) Disarm();                                  // no menu opened
            else if (_strip && !IsWindowVisible(_stripWnd)) Disarm();                              // the strip closed, with no choice (a choice is made before it closes)
            else if (_loopEndMs >= 0 && nowMs - _loopEndMs > CommandWithinMs) Disarm();          // the loop ended, no command followed
        }
        catch (Exception ex) { Log.Error("menu tick", ex); }
    }

    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    void FindStrip(Armed a)
    {
        IntPtr found = IntPtr.Zero;
        EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
        {
            if (!IsWindowVisible(h) || !WinFormsMenus.IsDropDown(h)) return true;
            var src = WinFormsMenus.SourceOf(h);
            if (src != IntPtr.Zero && (Native.IsInside(src, a.Hwnd) || Native.IsInside(a.Hwnd, src))) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (found != IntPtr.Zero) { _strip = true; _stripWnd = found; }
    }

    void StartCalls()
    {
        if (_callHook != IntPtr.Zero) return;
        _callProc ??= OnCall;
        _callHook = SetWindowsHookEx(WH_CALLWNDPROC, _callProc, IntPtr.Zero, GetCurrentThreadId());
    }

    // ---- the posted messages ---------------------------------------------------------------------------------------------------

    IntPtr OnGet(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && wParam == (IntPtr)1)
            {
                var message = (uint)Marshal.ReadInt32(lParam, IntPtr.Size);
                if (message is WM_RBUTTONDOWN or WM_KEYDOWN or WM_SYSKEYDOWN or WM_COMMAND or WM_LBUTTONUP) OnPosted(Marshal.PtrToStructure<Native.Msg>(lParam), Clock());
            }
        }
        catch (Exception ex) { Log.Error("menu hook", ex); }
        return CallNextHookEx(_getHook, code, wParam, lParam);
    }

    void OnPosted(Native.Msg m, long now)
    {
        switch (m.message)
        {
            case WM_RBUTTONDOWN:
                Arm(m.hwnd, (short)((long)m.lParam & 0xFFFF), (short)(((long)m.lParam >> 16) & 0xFFFF), now);
                break;
            case WM_KEYDOWN or WM_SYSKEYDOWN when MenuKeys.Opens(m.message, (long)m.wParam, GetKeyState(0x10) < 0, GetKeyState(0x12) < 0):
                Arm(GetFocus() is var f && f != IntPtr.Zero ? f : m.hwnd, null, null, now);
                break;
            case WM_COMMAND when _armed != null && _tracker.Active && ((long)m.wParam >> 16) == 0:   // HIWORD 0: a menu item (1 is an accelerator, others notifications of a control)
                if (_tracker.Chosen((int)((long)m.wParam & 0xFFFF), now) is { } path) Offer(new MenuChoice("win32", "", "", "", path.ToList(), (int)((long)m.wParam & 0xFFFF), "", OwnerRole(m.hwnd)), now, IntPtr.Zero);
                break;
            case WM_LBUTTONUP when WinFormsMenus.IsDropDown(m.hwnd):
                // only the menu of a ContextMenuStrip (it has a source control): the drop-down of a menu bar is no context menu
                if (WinFormsMenus.ItemUnder(m.hwnd, (short)((long)m.lParam & 0xFFFF), (short)(((long)m.lParam >> 16) & 0xFFFF)) is { } pick && pick.Source != IntPtr.Zero)
                    Offer(new MenuChoice("winforms", "", "", "", pick.Path), now, pick.Source);
                break;
        }
    }

    // ---- the sent messages -----------------------------------------------------------------------------------------------------

    IntPtr OnCall(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                // CWPSTRUCT: lParam, wParam, message, hwnd
                var message = (uint)Marshal.ReadInt32(lParam, 2 * IntPtr.Size);
                switch (message)
                {
                    case WM_ENTERMENULOOP: _loop = true; _loopEndMs = -1; break;
                    case WM_EXITMENULOOP: if (_loop) { _loop = false; _loopEndMs = Clock(); } break;
                    case WM_MENUSELECT: OnMenuSelect(Marshal.ReadIntPtr(lParam, IntPtr.Size), Marshal.ReadIntPtr(lParam, 0), Clock()); break;
                }
            }
        }
        catch (Exception ex) { Log.Error("menu hook (sent)", ex); }
        return CallNextHookEx(_callHook, code, wParam, lParam);
    }

    void OnMenuSelect(IntPtr wParam, IntPtr hmenu, long now)
    {
        uint low = (uint)((long)wParam & 0xFFFF), flags = (uint)(((long)wParam >> 16) & 0xFFFF);
        if (flags == 0xFFFF && hmenu == IntPtr.Zero) { _tracker.Closed(now); return; }   // the menu is closed
        if ((flags & MF_SYSMENU) != 0 || hmenu == IntPtr.Zero) return;
        bool popup = (flags & MF_POPUP) != 0;
        var text = new StringBuilder(256);
        GetMenuStringW(hmenu, low, text, text.Capacity, popup ? MF_BYPOSITION : 0);
        _tracker.Select((long)hmenu, MenuText.Display(text.ToString()), popup ? -1 : (int)low, popup);
    }

    // ---- where a window is -----------------------------------------------------------------------------------------------------

    /// <summary>Where <paramref name="hwnd"/> is: in the innermost tracked dialog (Spot null), or in a plugin palette (Spot); (zero, null) for anywhere else.</summary>
    (IntPtr Container, PaletteWatch.Spot? Spot) Where(IntPtr hwnd)
    {
        var top = DialogTop();
        if (top != IntPtr.Zero && Native.IsInside(top, hwnd)) return (top, null);
        if (PaletteWatch.Locate(hwnd) is { } s) return (s.Page, s);
        return (IntPtr.Zero, null);
    }

    /// <summary>The window a Win32 menu's command went to (the one passed to TrackPopupMenu): `window` for the dialog or palette itself, `id:N` for a control of it, "" when it is neither.</summary>
    string OwnerRole(IntPtr owner)
    {
        var a = _armed;
        if (a == null || owner == IntPtr.Zero) return "";
        if (owner == a.Container) return "window";
        return Native.IsInside(a.Container, owner) ? "id:" + DialogReader.GetDlgCtrlID(owner) : "";
    }

    // ---- the right-click -------------------------------------------------------------------------------------------------------

    /// <summary>A right-click (or the key) on <paramref name="hwnd"/>: where, in a tracked dialog or a palette, and what is under it (a tree or list item; the control's field is looked up at the choice).
    /// Anything else forgets the last one.</summary>
    void Arm(IntPtr hwnd, int? x, int? y, long now)
    {
        Disarm();
        if (!Enabled || hwnd == IntPtr.Zero) return;
        try
        {
            var (container, spot) = Where(hwnd);
            if (container == IntPtr.Zero) return;
            // a right-click on a list view's header belongs to the list view; it opens on no item and leaves the selection alone
            bool header = false;
            var headerOwner = MenuListView.OwnerOfHeader(hwnd);
            if (headerOwner != IntPtr.Zero) { hwnd = headerOwner; header = true; }
            var segment = DialogReader.Segment(DialogReader.ClassOf(hwnd));
            string item = "";
            MenuListView.Opened? listView = null;
            if (segment == "systreeview32") item = MenuTargets.TreeItem(hwnd, hwnd, x, y);
            else if (segment == "listbox") item = MenuTargets.ListItem(hwnd, hwnd, x, y);
            else if (segment == "syslistview32" && !header) { listView = MenuListView.Read(hwnd, x, y); item = listView.Item; }
            _armed = new Armed(hwnd, container, spot, item, now)
            {
                ListView = listView, OnHeader = header,
                Picture = spot != null ? PaletteSnap?.Invoke(spot) : null,
            };
            StartCalls();
        }
        catch (Exception ex) { Log.Error("menu arm", ex); Disarm(); }
    }

    // ---- the choices -----------------------------------------------------------------------------------------------------------

    /// <summary>A menu item was chosen. It is credited to the armed right-click when that opened the menu: a WinForms menu whose source control is the one right-clicked; a Win32 menu (the armed right-click is
    /// dropped as soon as no menu loop followed it). <paramref name="source"/>: the control a WinForms menu was opened for.</summary>
    void Offer(MenuChoice choice, long now, IntPtr source)
    {
        var a = _armed;
        Disarm();
        if (a == null) return;
        if (source != IntPtr.Zero && !Native.IsInside(source, a.Hwnd) && !Native.IsInside(a.Hwnd, source)) return;   // a menu of another control
        // the control's field, now (one scan)
        var scan = DialogReader.Scan(a.Container);
        int at = -1;
        for (var h = a.Hwnd; at < 0 && h != IntPtr.Zero && h != a.Container; h = DialogReader.GetParent(h))
            at = scan.Handles.FindIndex(l => l.Contains(h));
        string key = at >= 0 ? scan.Fields[at].Key : "", label = at >= 0 ? scan.Fields[at].Label : "", kind = at >= 0 ? scan.Fields[at].Kind : "";
        // a list view is no field (its value is not recorded), but the menu belongs to it: keep the control and the item as a target of its own
        if (at < 0 && MenuListView.Is(a.Hwnd) && Native.OwnedByThisThread(a.Hwnd)) { (key, label) = MenuListView.KeyOf(a.Hwnd, a.Container); kind = MenuListView.Kind; }
        var lv = kind == MenuListView.Kind ? a.ListView : null;
        if (kind == MenuListView.Kind && !a.OnHeader && lv is not { Known: true }) { Log.Info("menu on a list view: the list could not be read, no item is recorded"); lv = null; }
        Chosen?.Invoke(choice with
        {
            Target = key, TargetLabel = label, TargetKind = kind, Item = a.Item,
            NoItem = lv is { NoItem: true }, Selection = lv is { Selection.Count: > 0 } ? lv.Selection : null, SelectionOccurrences = lv is { Selection.Count: > 0 } ? lv.SelectionOccurrences : null, SelectionRows = lv is { Selection.Count: > 0 } ? lv.SelectionRows : null, ItemIndex = lv?.Index ?? -1, Occurrence = lv?.Occurrence ?? 0,
            SelectionTruncated = lv is { Truncated: true }, OnHeader = kind == MenuListView.Kind && a.OnHeader,
        }, a with { Key = key, Label = label, Kind = kind });
    }

    /// <summary>A WPF menu item was clicked (from <see cref="WpfMenus"/>): <paramref name="source"/> is the window the menu belongs to.</summary>
    internal void WpfChosen(IReadOnlyList<string> path, IntPtr source, string key, string label, string kind, string item)
    {
        if (!Enabled) return;
        var now = Clock();
        var (container, spot) = Where(source);
        if (container == IntPtr.Zero) return;
        Disarm();
        var a = new Armed(source, container, spot, item, now) { Key = key, Label = label, Kind = kind, Picture = spot != null ? PaletteSnap?.Invoke(spot) : null };
        Chosen?.Invoke(new MenuChoice("wpf", key, label, item, path.ToList(), 0, kind), a);
    }
}

/// <summary>What was under a right-click in a tree or a list box.</summary>
static class MenuTargets
{
    const uint TVM_HITTEST = 0x1111, LB_ITEMFROMPOINT = 0x01A9, LB_GETTEXT = 0x0189, LB_GETTEXTLEN = 0x018A, LB_GETCURSEL = 0x0188;
    const uint SMTO_ABORTIFHUNG = 2;

    /// <summary>The path of the tree item under the click ((x, y) client coordinates of <paramref name="hit"/>, the window the click went to); with no point (the Menu key) the selected item.</summary>
    public static string TreeItem(IntPtr tree, IntPtr hit, int? x, int? y)
    {
        try
        {
            if (!Native.OwnedByThisThread(tree)) return "";
            if (x == null || y == null) return TreeControl.SelectedPath(tree);
            var p = new POINT { X = x.Value, Y = y.Value };
            if (hit != tree) { ClientToScreen(hit, ref p); ScreenToClient(tree, ref p); }
            var buf = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(buf, 0, p.X); Marshal.WriteInt32(buf, 4, p.Y); Marshal.WriteInt64(buf, 8, 0); Marshal.WriteIntPtr(buf, 16, IntPtr.Zero);
                Native.SendNum(tree, TVM_HITTEST, IntPtr.Zero, buf, SMTO_ABORTIFHUNG, 1000, out var item);
                if (item == IntPtr.Zero) return "";
                return TreePath.Join(TreeControl.TextsTo(tree, item));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { Log.Error("menu target (tree)", ex); return ""; }
    }

    /// <summary>The text of the list box item under the click; with no point (the Menu key) the selected one.</summary>
    public static string ListItem(IntPtr list, IntPtr hit, int? x, int? y)
    {
        try
        {
            if (!Native.OwnedByThisThread(list)) return "";
            int index;
            if (x == null || y == null)
            {
                Native.SendNum(list, LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out var cur);
                index = (int)cur.ToInt64();
            }
            else
            {
                var p = new POINT { X = x.Value, Y = y.Value };
                if (hit != list) { ClientToScreen(hit, ref p); ScreenToClient(list, ref p); }
                Native.SendNum(list, LB_ITEMFROMPOINT, IntPtr.Zero, (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)), SMTO_ABORTIFHUNG, 1000, out var r);
                long v = r.ToInt64();
                if ((v >> 16) != 0) return "";   // outside the client area
                index = (int)(v & 0xFFFF);
            }
            if (index < 0) return "";
            Native.SendNum(list, LB_GETTEXTLEN, (IntPtr)index, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out var len);
            if (len.ToInt64() <= 0 || len.ToInt64() > 1000) return "";
            var buf = Marshal.AllocHGlobal(((int)len + 2) * 2);
            try
            {
                Native.SendNum(list, LB_GETTEXT, (IntPtr)index, buf, SMTO_ABORTIFHUNG, 1000, out _);
                return Marshal.PtrToStringUni(buf) ?? "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { Log.Error("menu target (list)", ex); return ""; }
    }
}
