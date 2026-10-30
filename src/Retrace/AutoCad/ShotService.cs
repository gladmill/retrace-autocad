using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>
/// Takes the screenshots of a recording. Event handlers only call <see cref="Request"/> (data work: no AutoCAD API, no capture).
/// <see cref="Pump"/> runs from a dispatcher timer and from Application.Idle, after the UI has had time to paint, and does the
/// BitBlt there; PNG encoding and the `shot` event happen on a pool thread. Every failure goes to retrace.log and is otherwise ignored.
/// </summary>
sealed class ShotService
{
    /// <summary>Pixels of a capture: 32-bit BGRA, top-down.</summary>
    public sealed record Picture(byte[] Pixels, int Width, int Height);

    readonly ShotScheduler _sched = new();
    readonly List<Task> _tasks = new();
    string _folder = "";
    string _sessionPath = "";
    bool _enabled;
    IntPtr _main;
    Func<bool> _modalOpen = () => false;
    Action<int, string, string> _onShot = (_, _, _) => { };

    public bool Enabled => _enabled;

    /// <param name="sessionPath">the recording's path; pictures go to `screenshots\` next to a recording.jsonl</param>
    /// <param name="onShot">called on a pool thread after the file is written: (n, relative file path, "window" | "dialog")</param>
    public void Begin(string sessionPath, bool enabled, IntPtr mainWindow, Func<bool> modalOpen, Action<int, string, string> onShot)
    {
        _sched.Clear();
        _tasks.Clear();
        _afterTaken.Clear();
        _sessionPath = sessionPath;
        _folder = Path.Combine(Path.GetDirectoryName(sessionPath) ?? "", ShotNaming.FolderFor(sessionPath));
        _enabled = enabled;
        _main = mainWindow;
        _modalOpen = modalOpen;
        _onShot = onShot;
    }

    public void SetMainWindow(IntPtr h) { if (_main == IntPtr.Zero) _main = h; }
    public bool NeedsMainWindow => _enabled && _main == IntPtr.Zero;

    public void Request(ShotRequest? r)
    {
        if (_enabled && r != null) _sched.Enqueue(r);
    }

    /// <summary>Fulfils what is due. <paramref name="force"/>: everything pending now (recording is stopping).</summary>
    public void Pump(long nowMs, bool force = false)
    {
        if (!_enabled || _sched.Pending == 0) return;
        List<ShotRequest> ready;
        try { ready = _sched.TakeReady(nowMs, force); }
        catch (Exception ex) { Log.Error("shot schedule", ex); return; }
        foreach (var r in ready)
        {
            try { Capture(r); }
            catch (Exception ex) { Log.Error($"shot {r.N} {r.Kind}", ex); }
        }
    }

    void Capture(ShotRequest r)
    {
        IntPtr hwnd;
        string what;
        if (r.Target == ShotTarget.Dialog)
        {
            if (!_modalOpen()) return;                       // the dialog is already gone: the screen shows something else
            hwnd = Native.ForegroundOfThisProcess();
            if (hwnd == IntPtr.Zero || hwnd == _main) return; // no dialog window of AutoCAD in front
            what = "dialog";
        }
        else
        {
            hwnd = _main;
            if (hwnd == IntPtr.Zero || Win.IsIconic(hwnd) || !Win.IsWindowVisible(hwnd)) return;
            what = "window";
        }
        Grab(r.N, r.Kind, hwnd, what);
    }

    readonly HashSet<int> _afterTaken = new();

    /// <summary>The "after" picture of dialog <paramref name="n"/> (its modal_title / modal_enter event), taken now: the person just pressed OK or
    /// Enter and the dialog still shows the values. Another OK (a validation message sent them back) overwrites the file; one `shot` event.</summary>
    public void DialogAfter(int n, IntPtr hwnd)
    {
        if (!_enabled || n <= 0 || hwnd == IntPtr.Zero || !Win.IsWindowVisible(hwnd)) return;
        try { Grab(n, "dialog-after", hwnd, "dialog-after", announce: _afterTaken.Add(n)); }
        catch (Exception ex) { Log.Error($"shot {n} dialog-after", ex); }
    }

    /// <summary>A picture of a screen rectangle (device pixels), taken now; null if it is empty or the capture fails. UI thread.</summary>
    public static Picture? CaptureRect(int left, int top, int right, int bottom)
    {
        var r = new Win.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        if (r.Width <= 0 || r.Height <= 0) return null;
        try { return new Picture(Win.CopyScreen(r), r.Width, r.Height); }
        catch (Exception ex) { Log.Error("capture rect", ex); return null; }
    }

    /// <summary>A picture of a window's visible frame and where its top-left corner is on the screen; null if the window is gone, hidden,
    /// minimized or empty, or the capture fails. UI thread.</summary>
    /// <param name="workArea">only the part on the monitor's work area: a maximized main window's frame reaches under the task bar</param>
    public static (Picture Pic, int Left, int Top)? CaptureWindow(IntPtr hwnd, bool workArea = false)
    {
        try
        {
            if (hwnd == IntPtr.Zero || Win.IsIconic(hwnd) || !Win.IsWindowVisible(hwnd)) return null;
            var b = Win.Bounds(hwnd);
            if (workArea && Win.WorkArea(hwnd) is { } wa)
                b = new Win.RECT { Left = Math.Max(b.Left, wa.Left), Top = Math.Max(b.Top, wa.Top), Right = Math.Min(b.Right, wa.Right), Bottom = Math.Min(b.Bottom, wa.Bottom) };
            return Capture(b) is { } p ? (p, b.Left, b.Top) : null;
        }
        catch (Exception ex) { Log.Error("capture window", ex); return null; }
    }

    static Picture? Capture(Win.RECT r) => r.Width <= 0 || r.Height <= 0 ? null : new Picture(Win.CopyScreen(r), r.Width, r.Height);

    /// <summary>Writes a picture taken earlier as the `shot` of event <paramref name="n"/> (what: "palette").</summary>
    public void Write(int n, string kind, Picture p, string what)
    {
        if (!_enabled || n <= 0) return;
        var path = Path.Combine(_folder, ShotNaming.FileName(n, kind));
        var rel = ShotNaming.RelativePathFor(_sessionPath, n, kind);
        var onShot = _onShot;
        var t = Task.Run(() =>
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); Png.Write(path, p.Width, p.Height, p.Pixels); onShot(n, rel, what); }
            catch (Exception ex) { Log.Error($"shot {n} write", ex); }
        });
        lock (_tasks) { _tasks.RemoveAll(x => x.IsCompleted); _tasks.Add(t); }
    }

    void Grab(int n, string kind, IntPtr hwnd, string what, bool announce = true)
    {
        var b = Win.Bounds(hwnd);
        if (b.Width <= 0 || b.Height <= 0) return;
        var px = Win.CopyScreen(b);                          // the only part that must run on the UI thread
        var path = Path.Combine(_folder, ShotNaming.FileName(n, kind));
        var rel = ShotNaming.RelativePathFor(_sessionPath, n, kind);
        int w = b.Width, h = b.Height;
        var onShot = _onShot;
        var t = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var (nw, nh) = what == "window" ? ShotImage.FitSize(w, h) : (w, h);
                var data = nw == w && nh == h ? px : ShotImage.Scale(px, w, h, nw, nh);
                Png.Write(path, nw, nh, data);
                if (announce) onShot(n, rel, what);
            }
            catch (Exception ex) { Log.Error($"shot {n} write", ex); }
        });
        lock (_tasks) { _tasks.RemoveAll(x => x.IsCompleted); _tasks.Add(t); }
    }

    /// <summary>Waits (bounded) for pictures still being encoded, so their `shot` events land before session_end.</summary>
    public void Drain(int timeoutMs = 5000)
    {
        Task[] all;
        lock (_tasks) all = _tasks.ToArray();
        try { Task.WaitAll(all, timeoutMs); } catch (Exception ex) { Log.Error("shot drain", ex); }
    }

    public void End()
    {
        _sched.Clear();
        _enabled = false;
        _onShot = (_, _, _) => { };
    }

    // ---- Win32 -------------------------------------------------------------------------------------------------------

    static class Win
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        /// <summary>The work area (the screen without the task bar) of the monitor the window is mostly on; null if it cannot be read.</summary>
        public static RECT? WorkArea(IntPtr hwnd)
        {
            var m = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            return m != IntPtr.Zero && GetMonitorInfo(m, ref mi) && mi.rcWork.Width > 0 ? mi.rcWork : null;
        }

        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        const uint SRCCOPY = 0x00CC0020;   // no CAPTUREBLT: it makes the cursor flicker on every capture

        /// <summary>Visible frame (DWM extended frame bounds: without the invisible resize border); GetWindowRect if DWM fails.</summary>
        public static RECT Bounds(IntPtr hwnd)
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0) return r;
            GetWindowRect(hwnd, out r);
            return r;
        }

        /// <summary>BitBlt from the screen DC; 32-bit BGRA, top-down.</summary>
        public static byte[] CopyScreen(RECT r)
        {
            int w = r.Width, h = r.Height;
            var screen = GetDC(IntPtr.Zero);
            var mem = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var dib = CreateDIBSection(screen, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            try
            {
                if (dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
                var old = SelectObject(mem, dib);
                if (!BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, SRCCOPY)) throw new InvalidOperationException("BitBlt failed");
                var px = new byte[w * h * 4];
                Marshal.Copy(bits, px, 0, px.Length);
                SelectObject(mem, old);
                return px;
            }
            finally
            {
                if (dib != IntPtr.Zero) DeleteObject(dib);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
