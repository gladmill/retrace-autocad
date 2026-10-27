using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>
/// Follows the modal dialogs of a recording and remembers which field values the user changed. The recorder tells it when a dialog
/// opened/was found/closed; it reads the fields (<see cref="DialogReader"/>) on request and keeps a <see cref="FieldTracker"/> per dialog.
/// No AutoCAD API. Win32 reads run on the calling (UI) thread; UI Automation reads run on a pool thread, one at a time.
/// </summary>
sealed class DialogWatch
{
    public const long Win32ThrottleMs = 150, UiaThrottleMs = 500;

    public sealed class Entry
    {
        public IntPtr Hwnd;
        /// <summary>n of the modal_title event if there was one, else of modal_enter.</summary>
        public int ForN;
        public string? Title;
        public string Source = "win32";
        public readonly FieldTracker Tracker = new();
        public long LastReadMs = -100000;
        public Task? Uia;
        /// <summary>"ok" | "cancel" from the last OK/Cancel click, Enter or Esc seen in the dialog; null = not seen.</summary>
        public string? ClosedWith;
        /// <summary>Win32 reads that found no fields, and whether one ever did: a dialog's controls may not exist yet at the first read.</summary>
        public int Win32Empty; public bool Win32Seen;
        public bool Resolved => Hwnd != IntPtr.Zero;
    }

    public sealed record Result(int ForN, string? Title, string Source, List<FieldChange> Changes, int Read, string? ClosedWith);

    readonly List<Entry> _stack = new();
    /// <summary>The innermost resolved dialog; zero if none. Read from OnPreTranslate without a lock (a stale value is harmless).</summary>
    public volatile IntPtr Top;

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr h);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);

    public bool Enabled { get; set; }

    public void Clear() { _stack.Clear(); Top = IntPtr.Zero; Unhook(); }

    /// <summary>Recording time (ms since the session started), for throttling reads from the message hook.</summary>
    public Func<long> Clock { get; set; } = () => 0;

    /// <summary>OK or Enter in a tracked dialog, before the dialog handles it: (n of its modal_title/modal_enter, dialog window). The recorder
    /// takes the "after" picture here.</summary>
    public Action<int, IntPtr>? Confirmed { get; set; }

    // A thread message hook (WH_GETMESSAGE) while a dialog is tracked: it sees the clicks and keys of every message loop on the UI
    // thread. AutoCAD's PreTranslateMessage misses WinForms dialogs (ShowModalDialog pumps its own loop).
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    HookProc? _proc;   // kept alive while hooked
    IntPtr _hook;

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
            if (code >= 0 && wParam == (IntPtr)1 /* PM_REMOVE: the message is being handled */ && Top != IntPtr.Zero)
            {
                var m = Marshal.PtrToStructure<MSG>(lParam);
                if (m.message == 0x0201) OnInput(m.hwnd, Clock(), 0);                                                   // WM_LBUTTONDOWN
                else if (m.message == 0x0100 && (m.wParam == (IntPtr)0x0D || m.wParam == (IntPtr)0x1B)) OnInput(m.hwnd, Clock(), (int)m.wParam);   // Enter, Esc
            }
        }
        catch (Exception ex) { Log.Error("dialog hook", ex); }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>A modal dialog opened (the window is not known yet).</summary>
    public Entry? Push(int forN)
    {
        if (!Enabled) return null;
        var e = new Entry { ForN = forN };
        _stack.Add(e);
        return e;
    }

    /// <summary>The dialog window was found: take the initial snapshot.</summary>
    public void Resolve(Entry e, IntPtr hwnd, int forN, string? title, long nowMs)
    {
        if (!_stack.Contains(e)) return;
        e.Hwnd = hwnd; e.ForN = forN; e.Title = title;
        Read(e, nowMs, initial: true);
        UpdateTop();
    }

    void UpdateTop()
    {
        Top = IntPtr.Zero;
        for (int i = _stack.Count - 1; i >= 0; i--)
            if (_stack[i].Resolved) { Top = _stack[i].Hwnd; Hook(); return; }
        Unhook();
    }

    /// <summary>Timer tick: re-read the innermost dialog, at most every ~150 ms (UI Automation: 500 ms).</summary>
    public void Tick(long nowMs)
    {
        var e = Innermost();
        if (e == null) return;
        if (nowMs - e.LastReadMs >= (e.Source == "uia" ? UiaThrottleMs : Win32ThrottleMs)) Read(e, nowMs, initial: false);
    }

    /// <summary>A click, Enter or Esc (<paramref name="key"/>: 0 = click, 0x0D, 0x1B) aimed at <paramref name="hwnd"/>: if it is inside the
    /// innermost dialog, note whether it confirms or cancels, and read now (the values are about to be applied).</summary>
    public void OnInput(IntPtr hwnd, long nowMs, int key = 0)
    {
        var e = Innermost();
        if (e == null || (hwnd != e.Hwnd && !IsChild(e.Hwnd, hwnd))) return;
        e.ClosedWith = key switch { 0x1B => "cancel", 0x0D => "ok", _ => ButtonRole(hwnd) };
        if (key == 0x1B) return;
        if (!(e.Source == "uia" && nowMs - e.LastReadMs < 100)) Read(e, nowMs, initial: false);
        if (e.ClosedWith == "ok") Confirmed?.Invoke(e.ForN, e.Hwnd);
    }

    /// <summary>"ok" / "cancel" for the dialog's OK / Cancel button (control id IDOK/IDCANCEL, or its text), else null.</summary>
    static string? ButtonRole(IntPtr hwnd)
    {
        var id = GetDlgCtrlID(hwnd);
        if (id == 1) return "ok";
        if (id == 2) return "cancel";
        var sb = new System.Text.StringBuilder(64);
        GetWindowText(hwnd, sb, 64);
        return Retrace.Core.DialogButtons.Role(sb.ToString());
    }

    Entry? Innermost()
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
            if (_stack[i].Resolved) return _stack[i];
        return null;
    }

    void Read(Entry e, long nowMs, bool initial)
    {
        if (!IsWindow(e.Hwnd)) return;   // gone: keep the last snapshot
        e.LastReadMs = nowMs;
        if (e.Source == "win32")
        {
            var fields = DialogReader.ReadWin32(e.Hwnd);
            if (fields.Count > 0) { e.Win32Seen = true; e.Tracker.Observe(fields); return; }
            if (e.Win32Seen || ++e.Win32Empty < 4) return;   // controls not created yet (seen with Drawing Units): try again for ~0.5 s
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
    /// dialog was never found. The window that closed is the innermost one (dialogs nest), or the one that is no longer a window.</summary>
    public Result? Close(long nowMs)
    {
        if (_stack.Count == 0) return null;
        int idx = _stack.FindLastIndex(x => x.Resolved && !IsWindow(x.Hwnd));
        if (idx < 0) idx = _stack.Count - 1;
        var e = _stack[idx];
        _stack.RemoveAt(idx);
        UpdateTop();
        if (!e.Resolved) return null;
        Read(e, nowMs, initial: false);
        return new Result(e.ForN, e.Title, e.Source, e.Tracker.Changes(), e.Tracker.ReadCount, e.ClosedWith);
    }
}
