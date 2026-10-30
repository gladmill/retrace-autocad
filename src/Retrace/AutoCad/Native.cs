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

    public static bool Exists(IntPtr h) => h != IntPtr.Zero && IsWindow(h);

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
}
