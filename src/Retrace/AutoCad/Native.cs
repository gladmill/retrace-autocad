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
