using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>
/// Follows the modal dialogs of a recording and remembers which field values the user changed and which push buttons they pressed. The recorder tells it when a
/// dialog opened/was found/closed; it reads the fields (<see cref="DialogReader"/>) on request and keeps a <see cref="FieldTracker"/> per dialog. A thread message hook
/// (WH_GETMESSAGE) sees the presses of every message loop on the UI thread: it reads the dialog before the message is dispatched, so a button's handler has not run.
/// A dialog that a button of another dialog opens is not announced by AutoCAD; it is found by its window (<see cref="ModalAbove"/>). The bookkeeping that needs no window
/// is in Core (<see cref="DialogStack{T}"/>, <see cref="ClickTrail"/>, <see cref="DialogClickRules"/>). No AutoCAD API. Win32 reads run on the calling (UI) thread; UI
/// Automation reads run on a pool thread, one at a time.
/// </summary>
sealed partial class DialogWatch
{
    public const long Win32ThrottleMs = 150, UiaThrottleMs = 500;
    /// <summary>A button press waiting to be completed: the mouse button must come up on the same button (or Space on it). Nothing but what decides it is read until it is complete (<see cref="Commit"/>).</summary>
    public sealed record Pending(IntPtr Button, string Text, int Id, bool ByKey)
    {
        /// <summary>What the dialog looked like when the mouse button went down (a push button, a tree item): the click is written from this, even when it is completed later (<see cref="DetectNested"/>).</summary>
        public Snap? Snap { get; set; }
        /// <summary>The dialog read at the button-down without touching the tracker: used only when the press commits before its button-up (<see cref="DialogClickRules.UseDownSnapshot"/>).</summary>
        public Snap? Down { get; set; }
        /// <summary>A field of the dialog had the focus when the mouse went down on the button (its validation may open a message box as the focus leaves).</summary>
        public bool FieldFocusedAtDown { get; init; }
        /// <summary>dblclick: its button-up has been seen; the decision waits until that message has been dispatched.</summary>
        public bool UpSeen { get; set; }
        /// <summary>"click" or "dblclick" (a double-click on a tree item: <see cref="Button"/> is the tree, <see cref="Text"/> the item's text).</summary>
        public string Kind { get; init; } = DialogClickKinds.Click;
        public string Path { get; init; } = "";
        public string TreeKey { get; init; } = "";
        public string TreeLabel { get; init; } = "";
        public string Tab { get; init; } = "";
        /// <summary>dblclick: the item and whether it was open when the double-click came, to tell a double-click that only opened or closed a node.</summary>
        public IntPtr Item { get; init; }
        public bool WasExpanded { get; init; }
        /// <summary>A context menu choice instead of a button press (<see cref="CommitMenu"/>).</summary>
        public Retrace.Core.MenuChoice? Menu { get; init; }
    }

    /// <summary>The dialog read for a press: the scan, the static texts, and the fields changed so far (before the press' handler).</summary>
    public sealed record Snap(DialogReader.Win32Scan? Basis, List<DialogStatic> Statics, List<FieldChange> Changes, int Read);

    public sealed class Entry : IStackEntry
    {
        public IntPtr Hwnd;
        /// <summary>n of the modal_title event if there was one, else of modal_enter.</summary>
        public int ForN;
        public string? Title;
        public string Source = "win32";
        public readonly FieldTracker Tracker = new();
        public long LastReadMs = -100000;
        public Task? Uia;
        /// <summary>"ok" | "cancel" from the last OK/Cancel press, Enter, Esc or title-bar close seen in the dialog; null = not seen.</summary>
        public string? ClosedWith;
        /// <summary>Win32 reads that found no fields, and whether one ever did: a dialog's controls may not exist yet at the first read.</summary>
        public int Win32Empty; public bool FieldSeen;
        /// <summary>The last Win32 pass over the dialog (its push buttons, for the index of a button that closed the dialog before it could be read again).</summary>
        public DialogReader.Win32Scan? LastScan;
        /// <summary>The tab the dialog showed in the last read that found tab controls (<see cref="TabPath"/>); "" for a dialog without any. A dialog that is gone when it closes is last read before: this is the tab it was closed from.</summary>
        public string Showing = "";
        public bool Resolved => Hwnd != IntPtr.Zero;
        public long Window => (long)Hwnd;
        /// <summary>The dialog this one was opened from: the one under it when it opened right after a click on one of that dialog's buttons (<see cref="ClickTrail.MayOpenNested"/>), however it was found. Null: it is a dialog of its own.</summary>
        public Entry? Parent;
        /// <summary>Found by its window (<see cref="Tick"/>), not announced by <c>EnterModal</c>: AutoCAD does not raise that for a dialog a button of another one opens.</summary>
        public bool Synthetic { get; init; }
        public bool Adopted { get; set; }
        /// <summary>The static texts of the last read that asked for them (a press, the input that closes the dialog).</summary>
        public List<DialogStatic> Statics = new();
        /// <summary>A button was clicked and the baseline has not been moved past what its handler did yet.</summary>
        public readonly ClickSettle Settle = new();
        /// <summary>The last click, for the nested-dialog gate and the explicit close.</summary>
        public readonly ClickTrail Trail = new();
        /// <summary>The button press that went down and is not complete yet.</summary>
        public Pending? Press;
        /// <summary>The tick states of its trees with check boxes, kept between reads (<see cref="TreeTickCache"/>).</summary>
        public readonly TreeTickCache Ticks = new();
        /// <summary>The labels of trees whose check boxes were cut at <see cref="TreeChecks.MaxItems"/> items: `truncated` of the dialog's <c>dialog_values</c>.</summary>
        public readonly HashSet<string> Truncated = new();
        /// <summary>A WPF dialog (<see cref="WpfDialogs.IsWindow"/>), once known; its presses are in DialogWatch.Wpf.cs.</summary>
        public bool? Wpf;
    }

    /// <summary>A push button (not OK / Cancel) pressed in a dialog: <paramref name="Changes"/> are the fields changed since the dialog opened or since the click
    /// before, read before the button's handler ran; <paramref name="Index"/> says which of the enabled buttons with that text it is (1 = the first). <paramref name="Tab"/>: the tab the button sits on, or
    /// the one showing for a button outside the tabs (""/none in a dialog without tabs).</summary>
    public sealed record Click(int ForN, string Button, int Id, int Index, List<FieldChange> Changes, int Read, List<DialogStatic> Statics, string Tab = "",
        string Kind = DialogClickKinds.Click, string Tree = "", string Path = "", string TreeLabel = "", bool OnDown = false, Retrace.Core.MenuChoice? Menu = null);

    /// <summary>ParentN: the <c>for</c> of the dialog this one was opened from (null: it was opened with no other tracked dialog open). Statics: the static texts of the last read
    /// that asked for them. ClosedBy: with <c>closedWith</c> "click", the button whose click closed the dialog. CloseTab: the tab showing when it closed (null: it has no tabs).</summary>
    public sealed record Result(int ForN, string? Title, string Source, List<FieldChange> Changes, int Read, string? ClosedWith, int? ParentN = null, List<DialogStatic>? Statics = null, string? ClosedBy = null, string? CloseTab = null, List<string>? Truncated = null);

    readonly DialogStack<Entry> _stack = new();
    readonly HashSet<IntPtr> _notNested = new();   // windows that opened over a dialog but could not be recorded: looked at once
    /// <summary>The innermost resolved dialog; zero if none. Read from OnPreTranslate without a lock (a stale value is harmless).</summary>
    public volatile IntPtr Top;

    public bool Enabled { get; set; }

    public void Clear() { _stack.Clear(); _notNested.Clear(); Top = IntPtr.Zero; Unhook(); WpfUntap(); }

    /// <summary>Recording time (ms since the session started), for throttling reads from the message hook.</summary>
    public Func<long> Clock { get; set; } = () => 0;

    /// <summary>OK, or Enter / Space / a mnemonic that presses it, in a tracked dialog, before the dialog handles it: (n of its modal_title/modal_enter, dialog window). The recorder
    /// takes the "after" picture here.</summary>
    public Action<int, IntPtr>? Confirmed { get; set; }

    /// <summary>A dialog opened over the innermost tracked one, right after a button click, without <c>EnterModal</c> (see <see cref="ModalAbove"/>): the recorder writes its
    /// <c>modal_enter</c> and returns its event number (the <c>for</c> of its <c>dialog_values</c>); 0 = not recorded, and the window is left alone.
    /// <see cref="Entry.Title"/> and <see cref="Entry.Hwnd"/> are set.</summary>
    public Func<Entry, int>? NestedOpened { get; set; }

    /// <summary>Such a dialog closed (its window is gone): its result, to be written like any dialog's.</summary>
    public Action<Result>? NestedClosed { get; set; }

    /// <summary>AutoCAD's main window: never a dialog.</summary>
    public IntPtr Main { get; set; }

    /// <summary>A push button that is not OK or Cancel was pressed in a tracked Win32 dialog (a click that went down and up on it, Space, Enter on it or as the default button, its
    /// mnemonic), before its handler runs.</summary>
    public Action<Click>? Clicked { get; set; }

    // A thread message hook (WH_GETMESSAGE) while a dialog is tracked: it sees the presses and keys of every message loop on the UI
    // thread. AutoCAD's PreTranslateMessage misses WinForms dialogs (ShowModalDialog pumps its own loop). A WM_COMMAND / BN_CLICKED hook (WH_CALLWNDPROC) would see
    // a click, Space and a native dialog's Enter and mnemonics, but not WinForms' own Enter on its AcceptButton and its mnemonics (PerformClick calls OnClick with no
    // message), so the presses are read from the input instead.
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    HookProc? _proc;   // kept alive while hooked
    IntPtr _hook;

    const uint WM_NCLBUTTONDOWN = 0x00A1, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_SYSKEYDOWN = 0x0104, WM_SYSCOMMAND = 0x0112,
        WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_MOUSEWHEEL = 0x020A;
    const long HTCLOSE = 20, SC_CLOSE = 0xF060, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_MENU = 0x12, VK_F4 = 0x73;

    static bool IsInput(uint message) => message is WM_NCLBUTTONDOWN or WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSCOMMAND or WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK
        or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_MOUSEWHEEL;

    void Hook()
    {
        if (_hook != IntPtr.Zero) return;
        _proc ??= OnHook;
        _hook = SetWindowsHookEx(3 /* WH_GETMESSAGE */, _proc, IntPtr.Zero, GetCurrentThreadId());
        if (_hook == IntPtr.Zero) Log.Error("dialog hook", new InvalidOperationException("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error()));
    }

    void Unhook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // only the messages that are input are read (the hook sees every message of the thread: WM_MOUSEMOVE, WM_TIMER, WM_PAINT ...): the type first, the whole MSG for those
            if (code >= 0 && wParam == (IntPtr)1 /* PM_REMOVE: the message is being handled */ && Top != IntPtr.Zero && IsInput((uint)Marshal.ReadInt32(lParam, IntPtr.Size)))
                OnMessage(Marshal.PtrToStructure<Native.Msg>(lParam), Clock());
        }
        catch (Exception ex) { Log.Error("dialog hook", ex); }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>The innermost tracked dialog whose window is known, or null (for <see cref="MenuWatch"/>).</summary>
    public Entry? Innermost => _stack.Innermost() is { Resolved: true } e ? e : null;

    /// <summary>A context menu item was chosen in the innermost tracked dialog (<see cref="MenuWatch"/>, before the item's handler runs): written as a click, with the menu in place of the button, so
    /// the dialog's baseline, the nested-dialog gate and the explicit close work as for a button.</summary>
    public void CommitMenu(Retrace.Core.MenuChoice menu, long now)
    {
        if (Innermost is not { } e) return;
        e.Press = null;
        Commit(e, new Pending(IntPtr.Zero, menu.ButtonText, menu.Id, true) { Menu = menu }, now);
    }

    /// <summary>A modal dialog opened (the window is not known yet). It is opened from the dialog under it only when it follows a click on that dialog's button, the same gate a dialog found by its
    /// window passes (<see cref="ClickTrail.MayOpenNested"/>); else it is a dialog of its own, as it was before nested dialogs were recorded.</summary>
    public Entry? Push(int forN)
    {
        if (!Enabled) return null;
        var top = _stack.Top;
        // a button that acts as the mouse goes down opens it before the button comes up: that press is the click this dialog belongs to
        if (top is { Resolved: true, Press: { ByKey: false } pend } && Native.Exists(top.Hwnd) && !Native.IsEnabled(top.Hwnd)) CommitOnDown(top, pend, Native.ForegroundOfThisProcess(), Clock());
        var parent = top is { Resolved: true } && top.Trail.MayOpenNested(Clock()) ? top : null;
        parent?.Trail.NestedOpened();
        var e = new Entry { ForN = forN, Parent = parent };
        _stack.Push(e);
        return e;
    }

    /// <summary>The window of the announced dialog <paramref name="e"/> has been found. When a nested-dialog entry (found by its window, before AutoCAD's late <c>EnterModal</c> came) tracks that
    /// window already, <paramref name="e"/> is merged into it: the entry leaves the stack (it must not block the search for the next nested dialog), the nested entry takes the
    /// announcement over, and its <c>for</c> is returned: the recorder then writes a <c>modal_alias</c> (the announcement's <c>modal_enter</c> is that dialog's) and no title or picture, and the
    /// <c>LeaveModal</c> that follows is <see cref="LeaveAdopted"/>. Null: it is another window, <see cref="Resolve"/> it.</summary>
    public int? MergeInto(Entry e, IntPtr hwnd)
    {
        if (!_stack.Contains(e) || _stack.MergeInto(e, (long)hwnd) is not { } into) return null;
        UpdateTop();
        return into.ForN;
    }

    /// <summary>The dialog window was found: take the initial snapshot.</summary>
    public void Resolve(Entry e, IntPtr hwnd, int forN, string? title, long nowMs)
    {
        if (!_stack.Contains(e)) return;
        e.Hwnd = hwnd; e.ForN = forN; e.Title = title;
        Read(e, nowMs);
        UpdateTop();
    }

    /// <summary>The <c>LeaveModal</c> of a dialog <see cref="MergeInto"/> took over: its result, or null when its window went away earlier and it was reported then.</summary>
    public Result? LeaveAdopted(long nowMs) => _stack.InnermostAdopted() is { } e ? CloseAt(e, nowMs) : null;

    void UpdateTop()
    {
        WpfSync();
        Top = IntPtr.Zero;
        if (_stack.Innermost() is { } e) { Top = e.Hwnd; Hook(); return; }
        Unhook();
    }

    /// <summary>Timer tick: re-read the innermost dialog, at most every ~150 ms (UI Automation: 500 ms).</summary>
    public void Tick(long nowMs)
    {
        ReapNested(nowMs);
        ResolveDoubles(nowMs);
        DetectNested(nowMs);
        var e = _stack.Innermost();
        if (e == null) return;
        if (nowMs - e.LastReadMs >= (e.Source is "uia" or "wpf" ? UiaThrottleMs : Win32ThrottleMs)) Read(e, nowMs);
    }

    /// <summary>The innermost tracked dialog is disabled and a modal window has opened over it that nothing announced: it is a nested dialog, opened from that one, when it
    /// follows a click on one of its buttons or the nested dialog of that click before it (<see cref="ClickTrail.MayOpenNested"/>). Any other window (a validation message box after OK, Help,
    /// a dialog from a double-click in a list) is no step of the test and is left alone, as before.</summary>
    void DetectNested(long nowMs)
    {
        var open = NestedOpened;
        var top = _stack.Innermost();
        if (open == null || top == null || _stack.Looking || !Native.Exists(top.Hwnd)) return;   // a dialog AutoCAD announced is still being looked for: it is that one
        // a press whose button-up has not come: a button that acts as the mouse goes down (or a double-click on a tree item) opened a window, and the button-up went to that window. The press
        // is the click now (written from what the dialog looked like when it went down), so the gate below opens for the window
        if (top.Press is { ByKey: false } pending && WindowOver(top) is var over0 && over0 != IntPtr.Zero)
        {
            CommitOnDown(top, pending, over0, nowMs);
            if (_notNested.Contains(over0)) return;
        }
        // the gate first, which is cheap: with no click before it (or other input since) no window over the dialog is a nested dialog, so no window is looked for or read at all (a validation box
        // over a dialog costs nothing per tick)
        if (!top.Trail.MayOpenNested(nowMs)) return;
        var w = ModalAbove.Find(top.Hwnd, Main, IntPtr.Zero, h => _notNested.Contains(h) || _stack.Has((long)h));
        if (w == IntPtr.Zero) return;
        var e = new Entry { Hwnd = w, Parent = top, Synthetic = true, Title = Native.TitleOf(w) };
        int n = open(e);
        if (n <= 0) { _notNested.Add(w); return; }
        e.ForN = n;
        top.Trail.NestedOpened();
        _stack.Push(e);
        Read(e, nowMs);
        UpdateTop();
    }

    /// <summary>The modal window open over the dialog of <paramref name="e"/> that no entry tracks and that was not looked at before, or zero (see <see cref="ModalAbove"/>).</summary>
    IntPtr WindowOver(Entry e) => ModalAbove.Find(e.Hwnd, Main, IntPtr.Zero, h => _notNested.Contains(h) || _stack.Has((long)h));

    /// <summary>Nested dialogs whose window is gone have closed (nothing raises <c>LeaveModal</c> for them): their results go out, innermost first.</summary>
    void ReapNested(long nowMs)
    {
        foreach (var e in _stack.ReapGone(w => Native.Exists((IntPtr)w)))
        {
            var r = Finish(e, nowMs);
            UpdateTop();
            if (r != null) NestedClosed?.Invoke(r);
        }
        if (_notNested.Count > 0) _notNested.RemoveWhere(h => !Native.Exists(h));
    }

    // ---- presses ------------------------------------------------------------------------------------------------------------

    /// <summary>One message the hook sees, before it is dispatched.</summary>
    void OnMessage(Native.Msg m, long now)
    {
        ReapNested(now);   // a nested dialog that has just closed must not take the input meant for the dialog under it
        ResolveDoubles(now);   // the button-up of a double-click on a tree item has been dispatched by the time the next message comes
        var e = _stack.Innermost();
        if (e == null) return;
        // the button comes up on a press that went down before: it counts even when that press has opened a dialog over this one by now (a button that acts when it goes down)
        if (m.message == WM_LBUTTONUP) { Up(m, now); e.Ticks.Invalidate(m.hwnd); return; }
        if (!Native.IsEnabled(e.Hwnd)) return;   // a modal window is over it: nothing a person does reaches it (a message posted to it is not a press)
        if (IsWpf(e) && WpfMessage(e, m)) return;   // a WPF dialog has no child windows: its buttons are found in its visual tree
        switch (m.message)
        {
            case WM_LBUTTONDOWN or WM_LBUTTONDBLCLK when Native.IsInside(e.Hwnd, m.hwnd): Down(e, m, now); break;
            case WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_MOUSEWHEEL when Native.IsInside(e.Hwnd, m.hwnd): Touch(e); break;
            // the title bar's close box, and the system command it posts: the dialog is cancelled
            case WM_NCLBUTTONDOWN when (long)m.wParam == HTCLOSE && Native.IsInside(e.Hwnd, m.hwnd): Cancelled(e); break;
            case WM_SYSCOMMAND when ((long)m.wParam & 0xFFF0) == SC_CLOSE && Native.IsInside(e.Hwnd, m.hwnd): Cancelled(e); break;
            case WM_KEYDOWN when Native.IsInside(e.Hwnd, m.hwnd): KeyDown(e, m, now); break;
            case WM_KEYUP when (long)m.wParam == VK_SPACE: SpaceUp(e, m, now); break;
            case WM_SYSKEYDOWN when Native.IsInside(e.Hwnd, m.hwnd): SysKeyDown(e, m, now); break;
        }
        e.Ticks.Invalidate(m.hwnd);   // a tree that got input shows a changed tick once the message is dispatched
    }

    /// <summary>Any input aimed at the dialog (not the click itself): moves the baseline past what the last button's handler did, read now, before this input is dispatched (the person's
    /// typing since is not in it), and ends the window in which a new window counts as that click's nested dialog.</summary>
    void Touch(Entry e)
    {
        SettleIfPending(e, null);
        e.Trail.Input();
    }

    /// <summary>The baseline moves past what the last button's handler did, once: from <paramref name="scan"/> when the caller has just read the dialog, else from a read of its own.</summary>
    void SettleIfPending(Entry e, DialogReader.Win32Scan? scan)
    {
        if (!e.Settle.OnInput() || !Native.Exists(e.Hwnd)) return;
        var fields = IsWpf(e) ? WpfDialogs.Read(e.Hwnd).Fields : (scan ?? DialogReader.Scan(e.Hwnd, ticks: e.Ticks)).Fields;
        if (fields.Count == 0) return;   // no Win32 fields (a WPF dialog, read through UI Automation): nothing to move
        e.FieldSeen = true; e.Tracker.Observe(fields); e.Tracker.Rebase();
    }

    void Cancelled(Entry e) { Touch(e); e.Press = null; e.ClosedWith = "cancel"; }

    /// <summary>The mouse button went down in the dialog. A push button that is not OK, Cancel or Help is a click once the button comes up on it (<see cref="Up"/>): only what decides that is read now
    /// (its text, its role), the dialog is read when the click is complete (<see cref="Commit"/>), which is still before the button's handler runs. Anything else is read now, as it is about to be applied.</summary>
    void Down(Entry e, Native.Msg m, long now)
    {
        var hwnd = m.hwnd;
        e.Press = null;
        if (m.message == WM_LBUTTONDBLCLK && TreeDoubleClick(e, m, now)) return;
        var text = DialogReader.PushButtonText(hwnd);
        var role = ButtonRole(hwnd);
        e.ClosedWith = role;
        if (text != null && role == null && !Retrace.Core.DialogButtons.IsHelp(text))
        {
            e.Trail.Input();
            // read at the button-up as ever (a focused field's Leave handler that reformats on this mouse-down has run by then); the read made now is only the fallback for a press that commits before it
            e.Press = new Pending(hwnd, text, DialogReader.GetDlgCtrlID(hwnd), false) { Down = PeekSnap(e, now), FieldFocusedAtDown = FieldFocused(e, hwnd) };
            return;
        }
        Touch(e);
        ReadAtInput(e, now);
        if (role == "ok") Confirmed?.Invoke(e.ForN, e.Hwnd);
    }

    /// <summary>The mouse button came up: the click is complete when it is on the button the press went down on and inside it; released elsewhere, it is dropped.</summary>
    void Up(Native.Msg m, long now)
    {
        var e = _stack.Items.LastOrDefault(x => x.Press is { ByKey: false });
        var p = e?.Press;
        if (e == null || p == null) return;
        e.Press = null;
        // the button-up may have gone to a window the press opened over the dialog (a button that acts as the mouse goes down): decided here, not by a later tick
        bool disabled = Native.Exists(e.Hwnd) && !Native.IsEnabled(e.Hwnd);
        var window = disabled ? WindowOver(e) : IntPtr.Zero;
        bool over = window != IntPtr.Zero;
        if (p.Kind == DialogClickKinds.DoubleClick)
        {
            if (over) Commit(e, p, now);
            else if (m.hwnd == p.Button) { p.UpSeen = true; e.Press = p; }   // what the double-click did is looked at once this button-up has been dispatched (ResolveDoubles)
            return;
        }
        bool landed = false;
        if (Native.ClientSize(m.hwnd, out var w, out var h))
        {
            int x = (short)((long)m.lParam & 0xFFFF), y = (short)(((long)m.lParam >> 16) & 0xFFFF);
            landed = DialogClickRules.MouseUpCompletes((long)p.Button, (long)m.hwnd, x, y, w, h);
        }
        if (!DialogClickRules.UpCommits(landed, disabled, over)) return;
        if (!landed || over) CommitOnDown(e, p, window, now); else Commit(e, p, now);
    }

    /// <summary>A double-click on a tree item whose button-up has been dispatched is looked at now: it is a click of the test unless all it did was open or close a node.</summary>
    void ResolveDoubles(long now)
    {
        foreach (var e in _stack.Items.ToList())
            if (e.Press is { Kind: DialogClickKinds.DoubleClick, UpSeen: true } p) { e.Press = null; TreeDoubleClickUp(e, p, now); }
    }

    void KeyDown(Entry e, Native.Msg m, long now)
    {
        long vk = (long)m.wParam;
        if (vk is 0x10 or 0x11 or VK_MENU or (>= 0xA0 and <= 0xA5)) return;   // a modifier alone is no input
        if (vk == VK_ESCAPE) { Touch(e); e.Press = null; e.ClosedWith = "cancel"; return; }
        // a press by key reads the dialog itself (Commit) and settles the baseline with that read; any other key settles here
        if (vk == VK_RETURN) { e.Trail.Input(); EnterPressed(e, m, now); return; }
        if (vk == VK_SPACE) { if ((((long)m.lParam >> 30) & 1) == 0) { e.Trail.Input(); SpaceDown(e, m.hwnd, now); } return; }
        // a letter or digit with no Alt is a mnemonic, as the dialog manager (and WinForms) take it, when the focused control does not take characters; Ctrl+letter and Win+letter are shortcuts
        if (IsLetterOrDigit(vk) && DialogClickRules.LetterIsMnemonic(DialogReader.DialogCode(m.hwnd, m with { message = WM_CHAR, wParam = (IntPtr)char.ToLowerInvariant((char)vk), lParam = (IntPtr)1 }), Native.CtrlDown(), Native.WinDown()))
        { e.Trail.Input(); MnemonicPressed(e, (char)vk, now); return; }
        Touch(e);
    }

    void SysKeyDown(Entry e, Native.Msg m, long now)
    {
        long vk = (long)m.wParam;
        if (vk == VK_MENU) return;
        if (vk == VK_F4) { Touch(e); e.Press = null; e.ClosedWith = "cancel"; return; }   // Alt+F4 closes the window
        e.Trail.Input();
        if (IsLetterOrDigit(vk)) MnemonicPressed(e, (char)vk, now); else SettleIfPending(e, null);
    }

    static bool IsLetterOrDigit(long vk) => vk is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A);

    /// <summary>Enter: when the focused control keeps it (<see cref="DialogClickRules.EnterGoesToControl"/>, asked of the control as the dialog manager does) it is no press. Else it presses the
    /// push button that has the focus, else the dialog's default button (a WinForms AcceptButton, DM_GETDEFID, BS_DEFPUSHBUTTON). With none found it is taken as OK, as before.</summary>
    void EnterPressed(Entry e, Native.Msg m, long now)
    {
        e.Press = null;
        if (DialogClickRules.EnterGoesToControl(DialogReader.DialogCode(m.hwnd, m))) { SettleIfPending(e, null); e.ClosedWith = null; return; }
        var button = DialogReader.PushButtonText(m.hwnd) != null ? m.hwnd : DialogReader.DefaultButtonOf(e.Hwnd);
        if (button == IntPtr.Zero)
        {
            SettleIfPending(e, null);
            e.ClosedWith = "ok";
            ReadAtInput(e, now);
            Confirmed?.Invoke(e.ForN, e.Hwnd);
            return;
        }
        ButtonPressed(e, button, now);
    }

    /// <summary>Space went down on the focused control: a push button is pressed, and it is a click when Space comes up on it (<see cref="SpaceUp"/>).</summary>
    void SpaceDown(Entry e, IntPtr focus, long now)
    {
        var text = DialogReader.PushButtonText(focus);
        if (text == null) { SettleIfPending(e, null); return; }   // Space presses buttons only; a check box is a field
        var role = ButtonRole(focus);
        e.ClosedWith = role;
        e.Press = null;
        if (role == null && !Retrace.Core.DialogButtons.IsHelp(text)) { e.Press = new Pending(focus, text, DialogReader.GetDlgCtrlID(focus), true); return; }
        SettleIfPending(e, null);
        ReadAtInput(e, now);
        if (role == "ok") Confirmed?.Invoke(e.ForN, e.Hwnd);
    }

    void SpaceUp(Entry e, Native.Msg m, long now)
    {
        var p = e.Press;
        if (p == null || !p.ByKey) return;
        e.Press = null;
        if (m.hwnd == p.Button) Commit(e, p, now);
    }

    /// <summary>A mnemonic (Alt+letter, or the letter alone): it presses the push button that alone has that letter underlined.</summary>
    void MnemonicPressed(Entry e, char letter, long now)
    {
        var button = DialogReader.MnemonicButton(e.Hwnd, letter);
        if (button != IntPtr.Zero) ButtonPressed(e, button, now); else SettleIfPending(e, null);
    }

    /// <summary>A push button was pressed by the keyboard: OK and Cancel set how the dialog closes, any other (not Help) is a click, written at once.</summary>
    void ButtonPressed(Entry e, IntPtr button, long now)
    {
        if (!Native.IsEnabled(button)) { SettleIfPending(e, null); e.ClosedWith = null; return; }   // Enter or the mnemonic of a disabled button does nothing
        var role = ButtonRole(button);
        e.ClosedWith = role;
        var text = DialogReader.PushButtonText(button);
        if (role == null)
        {
            if (text != null && !Retrace.Core.DialogButtons.IsHelp(text)) Commit(e, new Pending(button, text, DialogReader.GetDlgCtrlID(button), true), now);
            else SettleIfPending(e, null);
            return;
        }
        SettleIfPending(e, null);
        ReadAtInput(e, now);
        if (role == "ok") Confirmed?.Invoke(e.ForN, e.Hwnd);
    }

    /// <summary>The press is a click: the dialog is read (fields and buttons in one pass, then the static texts), the click goes out, and the baseline moves to the values read, so the dialog's
    /// close holds only what changes after this click. The read is still before the button's handler: the hook sees the button-up (or the key-up, or the Enter or mnemonic key) before it is
    /// dispatched, and the handler runs from that message. The baseline settles with that read when the click before it has not been settled yet. A button that acts as it goes down and closes the dialog
    /// (Text Style's Close) has no window left to read: the last read before it stands.</summary>
    void Commit(Entry e, Pending p, long now, bool onDown = false)
    {
        var snap = DialogClickRules.UseDownSnapshot(onDown, p.Down != null) ? p.Down! : p.Snap ?? TakeSnap(e, now);
        var basis = snap.Basis;
        var dbl = p.Kind == DialogClickKinds.DoubleClick;
        var click = new Click(e.ForN, p.Text, p.Id, dbl || basis == null || p.Menu != null ? 1 : DialogReader.ButtonIndex(basis, p.Button), snap.Changes, snap.Read, snap.Statics,
            p.Menu != null ? (basis != null && basis.Tabs.Count > 0 ? basis.Showing : "") : dbl ? p.Tab : basis?.TabOf(p.Button) ?? "", p.Kind, p.TreeKey, p.Path, p.TreeLabel, onDown && !dbl, p.Menu);
        e.Tracker.Rebase();
        e.Settle.Clicked(now);
        e.Trail.Clicked(p.Text, now);
        Clicked?.Invoke(click);
    }

    /// <summary>The dialog as it is now, read for a press that may commit before its button-up, without changing the tracker, the settle state or the cache of the click before: the fields changed so far would
    /// be those of <see cref="Tracker"/> with this read as the latest.</summary>
    Snap? PeekSnap(Entry e, long now)
    {
        if (!Native.Exists(e.Hwnd)) return null;
        var scan = DialogReader.Scan(e.Hwnd, ticks: e.Ticks);
        var statics = DialogReader.ReadStatics(e.Hwnd);
        return new Snap(scan, statics.Count > 0 ? statics : e.Statics, e.Tracker.ChangesWith(scan.Fields), e.Tracker.ReadCount);
    }

    /// <summary>Does a field of the dialog (an edit, a combo box) have the focus, and is it not <paramref name="button"/>?</summary>
    static bool FieldFocused(Entry e, IntPtr button)
    {
        var f = GetFocus();
        if (f == IntPtr.Zero || f == button || !Native.IsInside(e.Hwnd, f)) return false;
        var seg = DialogReader.Segment(DialogReader.ClassOf(f));
        return seg.Contains("edit") || seg.StartsWith("combobox");
    }

    /// <summary>A window opened over the dialog of <paramref name="e"/> while its press is pending: the press is that button's click with its nested dialog (<c>onDown</c>), unless the window is a message box a
    /// focused field's validation opened as the mouse went down (<see cref="DialogClickRules.OnDownWindowIsNested"/>): then the press is dropped and the window is left alone, as before mouse-down buttons were recorded.</summary>
    void CommitOnDown(Entry e, Pending p, IntPtr window, long now)
    {
        e.Press = null;
        if (window != IntPtr.Zero && !DialogClickRules.OnDownWindowIsNested(p.FieldFocusedAtDown, Native.OnlyTextAndButtons(window))) { _notNested.Add(window); return; }
        Commit(e, p, now, onDown: true);
    }

    /// <summary>The dialog as it is just before a press' handler runs: read (fields and buttons in one pass, then the static texts), the baseline settled with that read when the click before it has not
    /// been settled yet, and the fields changed so far. A button that acts as it goes down and closes the dialog (Text Style's Close) has no window left to read: the last read before it stands.</summary>
    Snap TakeSnap(Entry e, long now)
    {
        var scan = Native.Exists(e.Hwnd) ? DialogReader.Scan(e.Hwnd, ticks: e.Ticks) : null;
        var statics = e.Statics;
        if (scan != null)
        {
            e.LastReadMs = now; e.LastScan = scan;
            foreach (var l in scan.Truncated) e.Truncated.Add(l);
            if (scan.Tabs.Count > 0) e.Showing = scan.Showing;
            if (scan.Fields.Count > 0) { e.FieldSeen = true; e.Tracker.Observe(scan.Fields); }
            SettleIfPending(e, scan);
            statics = DialogReader.ReadStatics(e.Hwnd);
            if (statics.Count > 0) e.Statics = statics;
        }
        return new Snap(scan ?? e.LastScan, statics, e.Tracker.Changes(), e.Tracker.ReadCount);
    }

    /// <summary>A double-click on an item of a tree of the dialog (the second press of a double-click lands as WM_LBUTTONDBLCLK): a pending press, written when it did something
    /// (<see cref="TreeDoubleClickUp"/>) or when a window opened over the dialog (<see cref="DetectNested"/>). The first press of it selected the item, so the dialog read now holds that selection.
    /// False when it is no item of a tree field (the press is then an input like any other).</summary>
    bool TreeDoubleClick(Entry e, Native.Msg m, long now)
    {
        var tree = m.hwnd;
        var item = TreeControl.ItemAt(tree, m.lParam);
        if (item == IntPtr.Zero) return false;   // the +/- button, the indent, the check box, empty space, no tree
        e.Trail.Input();   // (TakeSnap settles the baseline itself)
        e.ClosedWith = null;
        var snap = TakeSnap(e, now);
        var at = -1;
        for (int i = 0; snap.Basis != null && i < snap.Basis.Fields.Count; i++)
            if (snap.Basis.Fields[i].Kind == "tree" && snap.Basis.Handles[i].Contains(tree)) { at = i; break; }
        if (at < 0 || snap.Basis == null) return false;
        var field = snap.Basis.Fields[at];
        e.Press = new Pending(tree, TreeControl.Text(tree, item).Trim(), DialogReader.GetDlgCtrlID(tree), false)
        {
            Snap = snap, Kind = DialogClickKinds.DoubleClick, Path = TreePath.Join(TreeControl.TextsTo(tree, item)), TreeKey = field.Key, TreeLabel = field.Label,
            Tab = field.Tab.Length > 0 ? field.Tab : snap.Basis.Showing, Item = item, WasExpanded = (TreeControl.State(tree, item, TreeControl.TVIS_EXPANDED) & TreeControl.TVIS_EXPANDED) != 0,
        };
        return true;
    }

    /// <summary>The button came up after a double-click on a tree item. It is a click of the test unless it only opened or closed a node (<see cref="TreeClickRules.IsAction"/>): then nothing is written and
    /// the input still ends the window in which a new window counts as the click's nested dialog.</summary>
    void TreeDoubleClickUp(Entry e, Pending p, long now)
    {
        bool gone = !Native.Exists(e.Hwnd);
        bool changed = false, flipped = false, children = false;
        if (!gone)
        {
            var fresh = DialogReader.Scan(e.Hwnd, ticks: e.Ticks).Fields;
            changed = p.Snap?.Basis != null && DialogFieldsCore.Diff(p.Snap.Basis.Fields, fresh).Count > 0;
            flipped = ((TreeControl.State(p.Button, p.Item, TreeControl.TVIS_EXPANDED) & TreeControl.TVIS_EXPANDED) != 0) != p.WasExpanded;
            children = TreeControl.HasChildren(p.Button, p.Item);
        }
        if (TreeClickRules.IsAction(children, flipped, changed, windowOpened: false, gone)) Commit(e, p, now);
    }

    /// <summary>A read for an input that is about to act (OK, Cancel, Enter): the fields, and the static texts of the dialog as it is now.</summary>
    void ReadAtInput(Entry e, long now)
    {
        if (e.Source == "uia" && now - e.LastReadMs < 100) return;
        Read(e, now, statics: true);
    }

    /// <summary>"ok" / "cancel" for the dialog's OK / Cancel button (control id IDOK/IDCANCEL, or its text), else null.</summary>
    static string? ButtonRole(IntPtr hwnd) => DialogReader.GetDlgCtrlID(hwnd) switch { 1 => "ok", 2 => "cancel", _ => Retrace.Core.DialogButtons.Role(Native.TitleOf(hwnd)) };

    /// <summary>One read of the dialog: the static texts when asked for (they are Win32 texts, whatever the source of the fields), then the fields. A dialog with no Win32 field after a few reads is
    /// read through UI Automation (<see cref="DialogClickRules.FlipToUia"/>); that is only how its fields are read: its push buttons are still tracked.</summary>
    void Read(Entry e, long nowMs, bool statics = false)
    {
        if (!Native.Exists(e.Hwnd)) return;   // gone: keep the last snapshot
        e.LastReadMs = nowMs;
        if (IsWpf(e)) { WpfRead(e, nowMs); return; }   // in process, on this thread: no UI Automation
        if (statics && DialogReader.ReadStatics(e.Hwnd) is { Count: > 0 } st) e.Statics = st;
        if (e.Source == "win32")
        {
            var scan = DialogReader.Scan(e.Hwnd, ticks: e.Ticks);
            foreach (var l in scan.Truncated) e.Truncated.Add(l);
            e.LastScan = scan;
            if (scan.Tabs.Count > 0) e.Showing = scan.Showing;
            var fields = scan.Fields;
            if (fields.Count > 0)
            {
                e.FieldSeen = true; e.Tracker.Observe(fields);
                // the first read after a click, when no input has come first: what the button's handler did to the dialog is not the person's change
                if (e.Settle.OnRead(nowMs)) e.Tracker.Rebase();
                return;
            }
            if (!DialogClickRules.FlipToUia(e.FieldSeen, ++e.Win32Empty)) return;   // controls not created yet (seen with Drawing Units): try again for ~0.5 s
            e.Source = "uia";   // still no child windows with fields: probably WPF
        }
        var t = e.Uia;
        if (t != null && !t.IsCompleted) return;   // one UI Automation read at a time
        var h = e.Hwnd;
        e.Uia = Task.Run(() =>
        {
            try { e.Tracker.Observe(DialogReader.ReadUia(h)); }
            catch (Exception ex) { Log.Error("dialog fields (uia task)", ex); }
        });
    }

    /// <summary>A modal dialog closed (LeaveModal). Takes the last read if the window still exists and returns the result, or null if the
    /// dialog was never found. The window that closed is the innermost announced one that is no longer a window (dialogs nest), else the innermost announced one.</summary>
    public Result? Close(long nowMs)
    {
        ReapNested(nowMs);   // a nested dialog that closed before this one: its result goes out first
        return _stack.PickForLeave(w => Native.Exists((IntPtr)w)) is { } e ? CloseAt(e, nowMs) : null;
    }

    Result? CloseAt(Entry e, long nowMs)
    {
        _stack.Remove(e);
        UpdateTop();
        return Finish(e, nowMs);
    }

    /// <summary>The entry has left the stack: its result. <c>closedWith</c> is what OK / Cancel / Enter / Esc / the title bar said; when it said nothing and the dialog went away after its last
    /// click with no other input to it since, it is "click" and <see cref="Result.ClosedBy"/> names that button (<see cref="ClickTrail.Final"/>).</summary>
    Result? Finish(Entry e, long nowMs)
    {
        // the dialog under it: what this dialog's OK did to it (Text Style > New... adds and selects the style, changes the font fields) is its handler's work, not the person's edit there, and the
        // next nested dialog of that click may open now
        if (e.Parent is { } parent && _stack.Contains(parent)) { parent.Trail.NestedClosed(nowMs); if (e.Resolved) parent.Settle.Rearm(nowMs); }
        if (!e.Resolved) return null;
        // a button that closes the dialog as soon as it is pressed (Text Style's Close acts on the button-down: the dialog is gone before the button comes up): that press was its click
        if (e.Press is { ByKey: false } pressed) { e.Press = null; Commit(e, pressed, nowMs, onDown: true); }
        e.Press = null;
        e.Ticks.ForceFresh = true;   // the last read of the dialog: the tick states are not the cached ones
        Read(e, nowMs, statics: true);
        var (closedWith, by) = e.Trail.Final(e.ClosedWith, nowMs);
        return new Result(e.ForN, e.Title, e.Source, e.Tracker.Changes(), e.Tracker.ReadCount, closedWith, e.Parent?.ForN, e.Statics, by, e.Showing.Length > 0 ? e.Showing : null,
            e.Truncated.Count > 0 ? e.Truncated.ToList() : null);
    }
}
