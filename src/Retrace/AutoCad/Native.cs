using System.Runtime.InteropServices;
using System.Text;

namespace Retrace.AutoCad;

static class Native
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>The foreground window if it belongs to this process (AutoCAD's), else zero.</summary>
    public static IntPtr ForegroundOfThisProcess()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return IntPtr.Zero;
        GetWindowThreadProcessId(h, out var pid);
        return pid == (uint)Environment.ProcessId ? h : IntPtr.Zero;
    }

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    /// <summary>The foreground window of any program (zero when none).</summary>
    public static IntPtr ForegroundWindow() => GetForegroundWindow();
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    /// <summary>The character a WM_KEYDOWN types (what <see cref="Retrace.Core.TypedInput"/> keeps of the command line); null for keys that
    /// type nothing or are pressed with Ctrl / Alt. UI thread only. See <see cref="Retrace.Core.TypedInput.CharOf"/> for the layout it assumes.</summary>
    public static char? TypedChar(uint vk)
    {
        if (GetKeyState(0x11) < 0 || GetKeyState(0x12) < 0) return null;
        return Retrace.Core.TypedInput.CharOf(vk, GetKeyState(0x10) < 0, (GetKeyState(0x14) & 1) != 0);
    }

    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr h);
    /// <summary>h is the window <paramref name="parent"/> or one of its children.</summary>
    public static bool IsInside(IntPtr parent, IntPtr h) => parent != IntPtr.Zero && h != IntPtr.Zero && (parent == h || IsChild(parent, h));
    public static bool ShiftOrCtrlDown() => GetKeyState(0x10) < 0 || GetKeyState(0x11) < 0;
    /// <summary>Is a Ctrl key down? (Ctrl+letter is a shortcut, not a mnemonic.)</summary>
    public static bool CtrlDown() => GetKeyState(0x11) < 0;
    /// <summary>Is a Windows key down? (Win+letter is a shortcut of Windows.)</summary>
    public static bool WinDown() => GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0;

    /// <summary>Does the window belong to the calling thread? The only trees and tab controls that are read or driven (messages that carry pointers).</summary>
    public static bool OwnedByThisThread(IntPtr h) => h != IntPtr.Zero && GetWindowThreadProcessId(h, out _) == GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    internal static extern IntPtr SendNum(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);

    public const uint SMTO_ABORTIFHUNG = 2, WM_NOTIFY = 0x004E, SendTimeoutMs = 2000;

    /// <summary>A message sent with SendMessageTimeout and SMTO_ABORTIFHUNG, as every read and set of a control that may belong to another thread is: a hung window costs the timeout, never the caller. The result (0 when it timed out).</summary>
    public static IntPtr SendTimed(IntPtr h, uint msg, IntPtr w, IntPtr l, uint timeoutMs = SendTimeoutMs)
    {
        SendNum(h, msg, w, l, SMTO_ABORTIFHUNG, timeoutMs, out var r);
        return r;
    }

    /// <summary>WM_NOTIFY from <paramref name="from"/> to <paramref name="to"/> with <paramref name="code"/>, as a control sends it: an NMHDR (window, id, code) in a zeroed buffer of <paramref name="size"/> bytes, followed by
    /// <paramref name="data"/> (the notification's own fields). <paramref name="after"/> sees the buffer once the message is handled (a reply the receiver wrote into it). Sent with a timeout (<see cref="SendTimed"/>). The one
    /// helper for the tab control, the other common controls and the tooltip query.</summary>
    public static IntPtr SendNotify(IntPtr to, IntPtr from, int id, int code, ReadOnlySpan<byte> data = default, int size = 96, Action<IntPtr>? after = null)
    {
        size = Math.Max(size, 24 + data.Length);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteIntPtr(buf, 0, from); Marshal.WriteIntPtr(buf, 8, (IntPtr)id); Marshal.WriteInt32(buf, 16, code);
            for (int i = 0; i < data.Length; i++) Marshal.WriteByte(buf, 24 + i, data[i]);
            var r = SendTimed(to, WM_NOTIFY, (IntPtr)id, buf);
            after?.Invoke(buf);
            return r;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The window <paramref name="x"/> itself or its ancestor that is a direct child of <paramref name="parent"/>; zero when <paramref name="x"/> is not under it.</summary>
    public static IntPtr AncestorUnder(IntPtr x, IntPtr parent)
    {
        for (int n = 0; n < 40 && x != IntPtr.Zero; n++, x = GetParent(x)) if (GetParent(x) == parent) return x;
        return IntPtr.Zero;
    }

    public static bool Exists(IntPtr h) => h != IntPtr.Zero && IsWindow(h);

    /// <summary>Is the window visible (itself and its parents)?</summary>
    public static bool IsVisible(IntPtr h) => IsWindowVisible(h);
    /// <summary>Does the window have a title bar? (Tooltips, drop-down lists and other pop-ups have none.)</summary>
    public static bool HasCaption(IntPtr h) => (Win32.GetWindowLongPtr(h, Win32.GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION;
    const long WS_CAPTION = 0x00C00000;

    /// <summary>A message as a thread message hook (WH_GETMESSAGE) sees it.</summary>
    [StructLayout(LayoutKind.Sequential)] public struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    /// <summary>Is the window enabled? A window under a modal one is not.</summary>
    public static bool IsEnabled(IntPtr h) => IsWindowEnabled(h);

    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    /// <summary>The size of the window's client area; false when there is none.</summary>
    public static bool ClientSize(IntPtr h, out int width, out int height)
    {
        var ok = GetClientRect(h, out var r);
        width = r.Right - r.Left; height = r.Bottom - r.Top;
        return ok;
    }

    public static string TitleOf(IntPtr h) { var t = new StringBuilder(256); GetWindowText(h, t, t.Capacity); return t.ToString(); }

    public static string ClassOf(IntPtr h) { var c = new StringBuilder(128); GetClassName(h, c, c.Capacity); return c.ToString(); }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);

    /// <summary>A progress window: a standard dialog (#32770) with no visible, enabled push button, such as AutoCAD Electrical's "Updating..."
    /// while it switches projects. It closes by itself; nothing in it can be answered.</summary>
    public static bool IsProgressWindow(IntPtr h)
    {
        if (!Exists(h) || ClassOf(h) != "#32770") return false;
        bool button = false;
        EnumChildWindows(h, (c, _) =>
        {
            if (ClassOf(c) == "Button" && IsWindowVisible(c) && IsWindowEnabled(c)) { button = true; return false; }
            return true;
        }, IntPtr.Zero);
        return !button;
    }
    /// <summary>A message box: every visible child window is a text (Static) or a button. A dialog with any other control (an edit box, a list
    /// view, a tree, a grid) is not, whatever a field scanner makes of it.</summary>
    public static bool OnlyTextAndButtons(IntPtr h)
    {
        if (!Exists(h)) return false;
        bool other = false, any = false;
        EnumChildWindows(h, (c, _) =>
        {
            if (!IsWindowVisible(c)) return true;
            any = true;
            var cls = ClassOf(c);
            if (cls != "Static" && cls != "Button") { other = true; return false; }
            return true;
        }, IntPtr.Zero);
        return any && !other;
    }
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// <summary>The title of an open AutoCAD security prompt (<see cref="SecurityPrompt"/>) on this (AutoCAD's UI) thread, else null.
    /// The prompt is a task dialog (#32770) that AutoCAD shows on its main thread; call this from that thread (a timer tick).</summary>
    public static string? OpenSecurityPrompt()
    {
        string? found = null;
        try
        {
            EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
            {
                if (!IsWindowVisible(h) || ClassOf(h) != "#32770") return true;
                var t = TitleOf(h);
                if (!SecurityPrompt.IsTitle(t)) return true;
                found = t; return false;
            }, IntPtr.Zero);
        }
        catch { return null; }
        return found;
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Brings a window of this process to the front (restores it when minimized). The thread input is attached to the foreground
    /// window's thread for the call, which is what lets a window take the foreground from another program. UI thread.</summary>
    public static void Front(IntPtr h)
    {
        try
        {
            if (!Exists(h)) return;
            if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
            var fg = GetForegroundWindow();
            if (fg == h) return;
            uint me = GetCurrentThreadId(), other = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            bool attached = other != 0 && other != me && AttachThreadInput(me, other, true);
            try { BringWindowToTop(h); SetForegroundWindow(h); }
            finally { if (attached) AttachThreadInput(me, other, false); }
        }
        catch (Exception ex) { Log.Error("window to front", ex); }
    }

    /// <summary>Moves a window that lies partly or wholly off the screen (a second monitor that is gone, a saved position) onto the screen, the
    /// least that does it. Returns how far it moved, (0, 0) when it was fine. UI thread.</summary>
    public static (int Dx, int Dy) MoveOntoScreen(IntPtr h)
    {
        try
        {
            if (!Exists(h) || !GetWindowRect(h, out var r)) return (0, 0);
            int sx = GetSystemMetrics(76), sy = GetSystemMetrics(77), sw = GetSystemMetrics(78), sh = GetSystemMetrics(79);   // the virtual screen
            var (dx, dy) = Retrace.Core.TdocGeometry.MoveInto(r.Left, r.Top, r.Right, r.Bottom, sx, sy, sx + sw, sy + sh);
            if (dx == 0 && dy == 0) return (0, 0);
            SetWindowPos(h, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0, 0x0001 | 0x0004 | 0x0010);   // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
            return (dx, dy);
        }
        catch (Exception ex) { Log.Error("window onto screen", ex); return (0, 0); }
    }

    /// <summary>The foreground window belongs to this process and has a child window whose text starts with <paramref name="prefix"/> (how Retrace's own verdict box is told from a
    /// dialog of anybody else's: its message starts with "Retrace test:"). The text read is the first 255 characters.</summary>
    public static bool ForegroundDialogStartsWith(string prefix)
    {
        try
        {
            var h = ForegroundOfThisProcess();
            if (h == IntPtr.Zero) return false;
            var found = false;
            EnumChildWindows(h, (c, _) => { if (TitleOf(c).StartsWith(prefix, StringComparison.Ordinal)) { found = true; return false; } return true; }, IntPtr.Zero);
            return found;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Title and window class of the current foreground window (null on failure).</summary>
    public static (string Title, string Class)? Foreground()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            var t = new StringBuilder(256);
            var c = new StringBuilder(128);
            GetWindowText(h, t, t.Capacity);
            GetClassName(h, c, c.Capacity);
            return (t.ToString(), c.ToString());
        }
        catch { return null; }
    }

    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);

    /// <summary>The top-level windows of the UI thread (AutoCAD's), front first, as <see cref="Retrace.Core.WindowFacts"/>. UI thread only. Includes the windows an owner owns (EnumThreadWindows lists every top-level window).</summary>
    public static List<Retrace.Core.WindowFacts> WindowsOfThisThread() => WindowsOfThread(GetCurrentThreadId());

    /// <summary>The visible top-level windows of <paramref name="thread"/>, front first (the one enumeration the dialog-window rules share, see <see cref="ModalAbove.Find"/>).</summary>
    public static List<Retrace.Core.WindowFacts> WindowsOfThread(uint thread)
    {
        var list = new List<Retrace.Core.WindowFacts>();
        try
        {
            EnumThreadWindows(thread, (h, _) =>
            {
                try { if (IsWindowVisible(h)) list.Add(FactsOf(h)); }   // cheap exit: most top-level windows of AutoCAD are hidden
                catch (Exception) { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("windows of the UI thread", ex); }
        return list;
    }

    /// <summary>What <see cref="Retrace.Core.ModalWindows"/> needs to know about one window.</summary>
    public static Retrace.Core.WindowFacts FactsOf(IntPtr h)
    {
        long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64();
        bool transparent = (ex & 0x80000) != 0 && GetLayeredWindowAttributes(h, out var colorKey, out var alpha, out var fl) && (fl & 2) != 0 && alpha == 0;   // WS_EX_LAYERED with LWA_ALPHA 0
        GetWindowRect(h, out var r);
        return new Retrace.Core.WindowFacts(h.ToInt64(), IsWindowVisible(h), IsWindowEnabled(h), HasCaption(h), (ex & 0x80) != 0, transparent, r.Right - r.Left, r.Bottom - r.Top, ClassOf(h), ModalAbove.IsRetraceWindow(h));
    }

    /// <summary>The foreground window when it is one of this process's, is no AutoCAD main window or <paramref name="replayWindow"/>, and is really shown (visible, not fully transparent, not empty: an add-in's form that is never seen is in front yet no window). Else zero.</summary>
    public static IntPtr ShownForegroundOfThisProcess(IntPtr mainWindow, IntPtr replayWindow = default)
    {
        var h = ForegroundOfThisProcess();
        if (h == IntPtr.Zero || h == mainWindow || h == replayWindow) return IntPtr.Zero;
        try { return Retrace.Core.ModalWindows.IsShown(FactsOf(h)) ? h : IntPtr.Zero; } catch (Exception) { return IntPtr.Zero; }
    }

    /// <summary>The new dialog window in front among this process's windows (not AutoCAD's main window, not <paramref name="replayWindow"/>, not one of <paramref name="before"/>), zero when there is none. Never looks at the foreground window: another program may be in front.</summary>
    public static IntPtr DialogWindow(IntPtr mainWindow, IntPtr replayWindow = default, IReadOnlyCollection<long>? before = null) =>
        (IntPtr)Retrace.Core.ModalWindows.Topmost(WindowsOfThisThread(), mainWindow.ToInt64(), replayWindow.ToInt64(), before);

    /// <summary>The handles of the visible windows now: the modal state that starts now has a dialog only among the windows that are not in it.</summary>
    public static HashSet<long> VisibleHandles() => WindowsOfThisThread().Select(w => w.Handle).ToHashSet();
}
