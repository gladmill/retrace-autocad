using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;
using Retrace.Core;
using static Retrace.AutoCad.DialogReader;

namespace Retrace.AutoCad;

/// <summary>
/// RETRACERUN: does one recorded context menu choice (<see cref="MenuChoice"/>) in a dialog or a palette. A small state machine the caller ticks (UI thread; every tick of a dialog, every
/// retry of a palette step): find the control, select the tree or list item the menu was opened on, open the menu the way the program expects, pick the item by its text path, check the menu
/// closed. Win32 popup menu (TrackPopupMenu): WM_CONTEXTMENU is posted to the control (the program shows its menu from its own message loop), the menu window's HMENU is walked by text,
/// then the menu is ended (EndMenu) and the item's command id is posted to the menu's owner as WM_COMMAND (the path is checked first; the recorded id is the fallback when the items have
/// no text). WinForms ContextMenuStrip: WM_CONTEXTMENU is posted (the control shows its own strip, its Opening handlers run), the item is found by text and clicked through
/// <c>PerformClick</c> in a queued call (an item may open a modal form: never inside the tick). WPF ContextMenu: opened on the element, the item raised through its automation peer.
/// A menu never stays open: a failure closes it (EndMenu / Close / IsOpen) before the reason is returned. Nothing here throws.
/// </summary>
sealed class MenuReplay
{
    public enum Status { Pending, Done, Failed }

    /// <summary>Why the last <see cref="Step"/> was not done (Pending: what is awaited; Failed: the reason).</summary>
    public string? Message { get; private set; }
    /// <summary>After <see cref="Status.Done"/>: a problem found later (the click failed in its queued call, the menu stayed open); the caller reads it on its next ticks.</summary>
    public string? LateProblem { get; private set; }
    /// <summary>The choice has been made (the command posted, the click queued): what the item did to the dialog is the program's work now, so the values set before it must not be read back again.</summary>
    public bool Committed => _stage is Stage.Closing or Stage.Verifying;
    /// <summary>After <see cref="Status.Done"/>: a Win32 menu's command was posted and the replay saw nothing change (no field, no static text, no window): it may not have run where the program expected it.</summary>
    public bool Unverified { get; private set; }

    readonly MenuChoice _menu;
    readonly IntPtr _container;
    readonly bool _hidden;
    enum Stage { Find, Open, Wait, Closing, Verifying, Finished }
    Stage _stage = Stage.Find;
    DateTime _t0 = DateTime.MinValue, _stageAt;
    IntPtr _control;
    System.Windows.FrameworkElement? _wpfTarget;
    (int X, int Y) _point;
    object? _strip;
    ContextMenu? _wpfMenu;
    IntPtr _menuWindow, _owner;
    int _commandId;

    static readonly TimeSpan Overall = TimeSpan.FromSeconds(8), OpenWait = TimeSpan.FromMilliseconds(1500), CloseWait = TimeSpan.FromSeconds(2);

    public MenuReplay(MenuChoice menu, IntPtr container, bool hidden = false) { _menu = menu; _container = container; _hidden = hidden; }

    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] static extern bool Post(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool EndMenu();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] static extern uint GetMenuItemID(IntPtr menu, int pos);
    [DllImport("user32.dll")] static extern IntPtr GetSubMenu(IntPtr menu, int pos);
    [DllImport("user32.dll")] static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuStringW(IntPtr menu, uint item, StringBuilder text, int max, uint flags);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const uint WM_CONTEXTMENU = 0x007B, WM_COMMAND = 0x0111, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_KEYDOWN = 0x0100, MN_GETHMENU = 0x01E1, GW_OWNER = 4,
        MF_BYPOSITION = 0x400, MF_GRAYED = 1, MF_DISABLED = 2, MF_SEPARATOR = 0x800, LB_FINDSTRINGEXACT = 0x01A2, LB_GETITEMRECT = 0x0198, SMTO_ABORTIFHUNG = 2;

    /// <summary>Does one step. Call again on the next tick while it is <see cref="Status.Pending"/>.</summary>
    public Status Step(DateTime now, Win32Scan? scan = null)
    {
        try { return StepCore(now, scan); }
        catch (Exception ex) { Log.Error("menu replay", ex); return Fail("the context menu failed: " + ex.Message); }
    }

    Status Pending(string why) { Message = why; return Status.Pending; }

    Status Fail(string why)
    {
        CloseAll();
        _stage = Stage.Finished;
        Message = why;
        return Status.Failed;
    }

    Status StepCore(DateTime now, Win32Scan? scan)
    {
        if (_t0 == DateTime.MinValue) _t0 = now;
        if (_stage == Stage.Finished) return Message == null ? Status.Done : Status.Failed;
        if (now - _t0 > Overall && _stage != Stage.Closing) return Fail($"the context menu ({_menu.PathText}) did not work in time");
        switch (_stage)
        {
            case Stage.Find: return FindTarget(now, scan);
            case Stage.Open: return Open(now);
            case Stage.Wait: return _menu.Kind switch { "winforms" => WaitStrip(now), "wpf" => WaitWpf(now), _ => WaitWin32(now) };
            case Stage.Closing: return Closing(now);
            case Stage.Verifying: return Verifying(now);
        }
        return Status.Done;
    }

    // ---- 1: the control, and the item the menu is opened on ---------------------------------------------------------------------

    Status FindTarget(DateTime now, Win32Scan? scan)
    {
        if (_menu.Kind == "wpf")
        {
            _wpfTarget = WpfMenus.TargetOf(_container, _menu, out var problem);
            if (_wpfTarget == null) return Pending(problem ?? "the control is not there yet");
            if (WpfMenus.MenuOf(_wpfTarget) == null) return Fail("the control has no context menu");
            _stage = Stage.Open;
            return Pending("opening the menu");
        }
        scan ??= Scan(_container, _hidden);
        IntPtr control = _container;
        if (_menu.Target.Length > 0)
        {
            var change = new FieldChange(_menu.Target, _menu.TargetLabel, _menu.TargetKind, "", _menu.Item, "");   // as a tree or list value: the item to select
            var map = FillMatcher.Match(new[] { change }, scan.Fields.Select(LiveField.Of).ToList());
            if (map[0] < 0 || scan.Handles[map[0]].Count == 0) return Pending($"no control \"{_menu.TargetLabel}\" for the menu");
            control = scan.Handles[map[0]][0];
            // the tree or list item is not selected here: the right-click is sent at the item, as a person's is, and the program selects it or not as it always does (the selection the recording
            // made before the click is a value the dialog sets first)
        }
        _control = control;
        _point = PointOf(control);
        _stage = Stage.Open;
        return Pending("opening the menu");
    }

    /// <summary>Where the menu is opened: the middle of the item the menu belongs to (tree, list), else the middle of the control; screen coordinates.</summary>
    (int, int) PointOf(IntPtr control)
    {
        var p = new POINT();
        bool have = false;
        try
        {
            if (_menu.Item.Length > 0 && _menu.TargetKind == "tree" && Native.OwnedByThisThread(control))
            {
                var found = TreeControl.Find(control, TreePath.Split(_menu.Item));
                if (found.Problem == null && TreeControl.TextRect(control, found.Items[^1]) is { } r) { p = new POINT { X = (r.L + r.R) / 2, Y = (r.T + r.B) / 2 }; have = true; }
            }
            else if (_menu.Item.Length > 0 && _menu.TargetKind == "list" && Native.OwnedByThisThread(control))
            {
                var buf = Marshal.StringToHGlobalUni(_menu.ItemLeaf.Length > 0 ? _menu.Item : "");
                try
                {
                    Native.SendNum(control, LB_FINDSTRINGEXACT, (IntPtr)(-1), buf, SMTO_ABORTIFHUNG, 1000, out var idx);
                    if (idx.ToInt64() >= 0)
                    {
                        var rect = Marshal.AllocHGlobal(16);
                        try
                        {
                            Native.SendNum(control, LB_GETITEMRECT, idx, rect, SMTO_ABORTIFHUNG, 1000, out _);
                            p = new POINT { X = (Marshal.ReadInt32(rect, 0) + Marshal.ReadInt32(rect, 8)) / 2, Y = (Marshal.ReadInt32(rect, 4) + Marshal.ReadInt32(rect, 12)) / 2 };
                            have = true;
                        }
                        finally { Marshal.FreeHGlobal(rect); }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            if (!have && GetClientRect(control, out var c)) p = new POINT { X = Math.Max(1, (c.Right - c.Left) / 2), Y = Math.Max(1, (c.Bottom - c.Top) / 2) };
        }
        catch (Exception ex) { Log.Error("menu point", ex); }
        ClientToScreen(control, ref p);
        return (p.X, p.Y);
    }

    // ---- 2: open the menu ------------------------------------------------------------------------------------------------------

    Status Open(DateTime now)
    {
        _stageAt = now;
        if (_menu.Kind == "wpf")
        {
            _wpfMenu = WpfMenus.MenuOf(_wpfTarget!);
            if (!WpfMenus.RaiseOpening(_wpfTarget!)) WpfMenus.OpenDirect(_wpfMenu!, _wpfTarget!);
            _stage = Stage.Wait;
            return Pending("waiting for the menu");
        }
        if (_menu.Kind == "winforms")
        {
            _strip = WinFormsMenus.StripOf(_control, _menu.Item);
            if (_strip == null) return Fail("the control has no ContextMenuStrip");
        }
        _attempt = 0;
        TryOpen();
        _stage = Stage.Wait;
        return Pending("waiting for the menu");
    }

    int _attempt;
    bool _wpfDirect;

    /// <summary>One way of asking the program for its menu, posted so that it shows it from its own message loop. A Win32 popup menu: WM_CONTEXTMENU first (what a right-click leads to), then the mouse messages. A WinForms
    /// control: the mouse messages first (a TreeView only shows a menu for the right-click it saw itself), then WM_CONTEXTMENU, then the strip is shown where the item is. false = no way left.</summary>
    bool TryOpen()
    {
        bool winforms = _menu.Kind == "winforms";
        int ways = winforms ? 3 : 2;
        if (_attempt >= ways) return false;
        bool mouse = winforms ? _attempt == 0 : _attempt == 1;
        if (winforms && _attempt == 2)
        {
            var at = new POINT { X = _point.X, Y = _point.Y };
            ScreenToClient(_control, ref at);
            WinFormsMenus.Show(_strip!, _control, at.X, at.Y);
            _attempt++;
            return true;
        }
        if (mouse)
        {
            var client = new POINT { X = _point.X, Y = _point.Y };
            ScreenToClient(_control, ref client);
            var l = (IntPtr)(((client.Y & 0xFFFF) << 16) | (client.X & 0xFFFF));
            Post(_control, WM_RBUTTONDOWN, (IntPtr)2, l);
            Post(_control, WM_RBUTTONUP, IntPtr.Zero, l);
        }
        else Post(_control, WM_CONTEXTMENU, _control, (IntPtr)(((_point.Y & 0xFFFF) << 16) | (_point.X & 0xFFFF)));
        _attempt++;
        return true;
    }

    // ---- 3: the menu is open: pick the item -------------------------------------------------------------------------------------

    Status WaitWin32(DateTime now)
    {
        var window = MenuWindow();
        if (window == IntPtr.Zero)
        {
            if (now - _stageAt > OpenWait)
            {
                if (!TryOpen()) return Fail("the context menu did not open");
                _stageAt = now;
            }
            return Pending("waiting for the menu");
        }
        Native.SendNum(window, MN_GETHMENU, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out var hmenu);
        if (hmenu == IntPtr.Zero) return Pending("waiting for the menu items");
        var (id, problem) = FindWin32Item(hmenu);
        if (problem != null) return Fail(problem);
        _commandId = id;
        _menuWindow = window;
        _owner = OwnerOf(window);
        if (_owner == IntPtr.Zero) return Fail("the window that gets the menu's command (" + (_menu.Owner.Length > 0 ? _menu.Owner : "the menu's owner") + ") was not found");
        _before = Fingerprint();
        // ends the menu (nothing is chosen), then the command is delivered to the owner as the menu would have
        EndMenu();
        Post(_owner, WM_COMMAND, (IntPtr)(id & 0xFFFF), IntPtr.Zero);
        _stage = Stage.Closing;
        _stageAt = now;
        return Pending("closing the menu");
    }

    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);

    string? _before;

    /// <summary>The window that gets the command: the recorded owner (`window` = the dialog or palette, `id:N` = its control), else the menu window's own owner (older recordings); zero when it is not there.</summary>
    IntPtr OwnerOf(IntPtr menuWindow)
    {
        var role = _menu.Owner;
        if (role == "window") return _container;
        if (role.StartsWith("id:", StringComparison.Ordinal) && int.TryParse(role.AsSpan(3), out var id)) return GetDlgItem(_container, id);
        return GetWindow(menuWindow, GW_OWNER);
    }
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr h, int id);

    /// <summary>What the window shows now (field values, static texts): a command that ran in it changes this.</summary>
    string Fingerprint()
    {
        try
        {
            var fields = Scan(_container, _hidden).Fields.Select(f => f.Key + "=" + f.Value);
            var statics = ReadStatics(_container).Select(x => x.Text);
            return string.Join("|", fields) + "##" + string.Join("|", statics);
        }
        catch (Exception) { return ""; }
    }

    /// <summary>The first visible popup menu window (class #32768) of this process, or zero.</summary>
    static IntPtr MenuWindow()
    {
        var found = IntPtr.Zero;
        uint me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid == me && IsWindowVisible(h) && Native.ClassOf(h) == "#32768") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Walks the path in the menu by text; the command id of the leaf, or why not. The recorded id is the fallback when the path's texts are empty (owner-drawn items).</summary>
    (int Id, string? Problem) FindWin32Item(IntPtr hmenu)
    {
        var path = _menu.Path;
        var current = hmenu;
        int leaf = -1;
        for (int d = 0; d < path.Count; d++)
        {
            int hit = -1, disabled = -1;
            int count = Math.Min(GetMenuItemCount(current), 500);
            for (int i = 0; i < count; i++)
            {
                var flags = GetMenuState(current, (uint)i, MF_BYPOSITION);
                if (flags == 0xFFFFFFFF || (flags & MF_SEPARATOR) != 0) continue;
                var text = new StringBuilder(256);
                GetMenuStringW(current, (uint)i, text, text.Capacity, MF_BYPOSITION);
                if (path[d].Length == 0 || !MenuText.Same(text.ToString(), path[d])) continue;
                if ((flags & (MF_GRAYED | MF_DISABLED)) != 0) { if (disabled < 0) disabled = i; continue; }
                hit = i; break;
            }
            if (hit < 0)
            {
                if (d == path.Count - 1 && _menu.Id > 0 && GetMenuState(current, (uint)_menu.Id, 0) != 0xFFFFFFFF && path[d].Length == 0) { leaf = _menu.Id; break; }
                return (0, disabled >= 0 ? $"the menu item {string.Join(TreePath.Separator, path.Take(d + 1))} is disabled"
                                          : $"no menu item {path[d]}" + (d > 0 ? $" under {string.Join(TreePath.Separator, path.Take(d))}" : ""));
            }
            if (d < path.Count - 1)
            {
                current = GetSubMenu(current, hit);
                if (current == IntPtr.Zero) return (0, $"the menu item {path[d]} has no submenu");
            }
            else leaf = (int)GetMenuItemID(current, hit);
        }
        return leaf is > 0 and < 0xFFFF ? (leaf, null) : (0, $"the menu item {_menu.PathText} has no command id");
    }

    Status WaitStrip(DateTime now)
    {
        var strip = _strip!;
        if (!WinFormsMenus.IsVisible(strip))
        {
            if (now - _stageAt > OpenWait)
            {
                if (!TryOpen()) return Fail("the context menu did not open");
                _stageAt = now;
            }
            return Pending("waiting for the menu");
        }
        var (leaf, problem) = WinFormsMenus.Find(strip, _menu.Path);
        if (problem != null) return Fail(problem);
        WinFormsMenus.Close(strip);   // the drop-down closes as the item is clicked; its handler may open a form and must not run with it open
        var item = leaf!;
        // queued: the item may open a modal form, and a modal loop inside the tick would run the tick again
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (!WinFormsMenus.Click(item)) LateProblem = $"could not click {_menu.PathText}";
            if (WinFormsMenus.IsVisible(strip)) { WinFormsMenus.Close(strip); if (WinFormsMenus.IsVisible(strip)) LateProblem ??= "the context menu stayed open"; }
        });
        _stage = Stage.Finished;
        Message = null;
        return Status.Done;
    }

    Status WaitWpf(DateTime now)
    {
        var cm = _wpfMenu!;
        if (!cm.IsOpen)
        {
            if (now - _stageAt <= OpenWait) return Pending("waiting for the menu");
            if (_wpfDirect) return Fail("the context menu did not open");
            _wpfDirect = true;   // ContextMenuOpening did not open it: opened directly (its Opening handlers did not run)
            WpfMenus.OpenDirect(cm, _wpfTarget!);
            _stageAt = now;
            return Pending("waiting for the menu");
        }
        var (leaf, problem) = WpfMenus.Find(cm, _menu.Path);
        if (problem != null) return Fail(problem);
        cm.IsOpen = false;
        var item = leaf!;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (!WpfMenus.Click(item)) LateProblem = $"could not click {_menu.PathText}";
            if (cm.IsOpen) { cm.IsOpen = false; if (cm.IsOpen) LateProblem ??= "the context menu stayed open"; }
        });
        _stage = Stage.Finished;
        Message = null;
        return Status.Done;
    }

    // ---- 4: the menu must be gone -----------------------------------------------------------------------------------------------

    Status Closing(DateTime now)
    {
        if (MenuWindow() == IntPtr.Zero) { _stage = Stage.Verifying; _stageAt = now; return Pending("checking that the command ran"); }
        if (now - _stageAt > CloseWait)
        {
            EndMenu();
            if (_menuWindow != IntPtr.Zero) Post(_menuWindow, WM_KEYDOWN, (IntPtr)0x1B, IntPtr.Zero);
            if (now - _stageAt > CloseWait + CloseWait) return Fail("the context menu stayed open");
        }
        return Pending("closing the menu");
    }

    Status Verifying(DateTime now)
    {
        // the command ran where the program expected it when something shows it: a field or text changed, or a window opened over this one
        if (!Native.IsEnabled(_container) || (_before != null && Fingerprint() != _before)) { _stage = Stage.Finished; Message = null; return Status.Done; }
        if (now - _stageAt < VerifyWait) return Pending("checking that the command ran");
        Unverified = true;
        _stage = Stage.Finished; Message = null;
        return Status.Done;
    }

    static readonly TimeSpan VerifyWait = TimeSpan.FromMilliseconds(1200);

    /// <summary>Closes whatever is open (a failure, or the replay ends): never leaves a menu open.</summary>
    public void CloseAll()
    {
        try
        {
            if (MenuWindow() != IntPtr.Zero) { EndMenu(); }
            if (_strip != null && WinFormsMenus.IsVisible(_strip)) WinFormsMenus.Close(_strip);
            if (_wpfMenu is { IsOpen: true } cm) cm.IsOpen = false;
        }
        catch (Exception ex) { Log.Error("menu close", ex); }
    }
}
