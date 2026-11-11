using System.Runtime.InteropServices;
using System.Windows.Threading;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>
/// The green click arrow of a replay (setting <c>showClickArrows</c>): a top-most, click-through, non-activating layered window that points at the screen point Retrace is about to click
/// or at the middle of the control it fills. It has no owner and no parent, never takes the focus, never receives a click (<c>WS_EX_TRANSPARENT</c>, <c>HTTRANSPARENT</c>, <c>MA_NOACTIVATE</c>), is not in
/// Alt-Tab (<c>WS_EX_TOOLWINDOW</c>) and goes away by itself after <see cref="ClickArrow.ShowMs"/>, or when the next arrow comes. Nothing here waits, so a step is never delayed; every failure is
/// logged once and then ignored. The geometry and the pixels are Core (<see cref="ClickArrow"/>, <see cref="Annotate.DrawArrow"/>). UI thread only; every call is a no-op while the setting is off or no
/// replay runs. <see cref="HideNow"/> runs before every screen capture, so a picture never holds the live arrow (the pictures get their own arrow drawn into the image).
/// </summary>
static class ClickArrows
{
    static bool _active, _failed;
    static IntPtr _hwnd;
    static DispatcherTimer? _timer;
    static readonly WndProc Proc = WindowProc;

    /// <summary>A replay starts: arrows are shown for it when the setting is on.</summary>
    public static void Begin(bool on) { _active = on; if (!on) HideNow(); }

    /// <summary>The replay ended: nothing is shown any more.</summary>
    public static void End() { _active = false; HideNow(); }

    /// <summary>An arrow at the middle of the window <paramref name="h"/> (a control Retrace clicks or fills).</summary>
    public static void AtWindow(IntPtr h)
    {
        if (!_active || h == IntPtr.Zero) return;
        try { if (IsWindowVisible(h) && GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top) At((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2); }   // a hidden or rolled-up palette is not shown: nothing to point at
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>An arrow at the point (<paramref name="cx"/>, <paramref name="cy"/>) in the client coordinates of the window <paramref name="h"/> (the point a mouse message is sent at).</summary>
    public static void AtClient(IntPtr h, int cx, int cy)
    {
        if (!_active || h == IntPtr.Zero) return;
        try { var p = new POINT { X = cx, Y = cy }; if (ClientToScreen(h, ref p)) At(p.X, p.Y); }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>An arrow at the middle of a screen rectangle.</summary>
    public static void AtRect(int left, int top, int right, int bottom)
    {
        if (!_active || right <= left || bottom <= top) return;
        At((left + right) / 2, (top + bottom) / 2);
    }

    /// <summary>An arrow whose head is on the screen point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static void At(int x, int y)
    {
        if (!_active) return;
        try
        {
            var monitors = Monitors();
            if (ClickArrow.MonitorOf(x, y, monitors) is not { } m || !m.Contains(x, y)) return;   // off every monitor (a window parked off the screen): no arrow
            if (ClickArrow.ForScreen(x, y, monitors) is not { } a) return;
            Show(a);
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Removes the arrow now (before a capture, at the end of a replay). The screen is flushed so that a capture right after sees it gone.</summary>
    public static void HideNow()
    {
        try
        {
            _timer?.Stop();
            if (_hwnd != IntPtr.Zero && IsWindowVisible(_hwnd))
            {
                ShowWindow(_hwnd, 0);
                DwmFlush();
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    static void Fail(Exception ex)
    {
        if (_failed) return;
        _failed = true;
        Log.Error("click arrow", ex);
    }

    static void Show(ArrowLine a)
    {
        const int Margin = 12;
        int left = Math.Min(a.StartX, a.TipX) - Margin, top = Math.Min(a.StartY, a.TipY) - Margin;
        int w = Math.Abs(a.StartX - a.TipX) + 2 * Margin + 1, h = Math.Abs(a.StartY - a.TipY) + 2 * Margin + 1;
        var px = new byte[w * h * 4];
        Annotate.DrawArrow(px, w, h, new ArrowLine(a.StartX - left, a.StartY - top, a.TipX - left, a.TipY - top));

        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) _hwnd = CreateOverlay();
        if (_hwnd == IntPtr.Zero) return;

        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        IntPtr dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            var bmi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            dib = CreateDIBSection(screen, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return;
            Marshal.Copy(px, 0, bits, px.Length);
            old = SelectObject(mem, dib);
            // top-most, without activation, at the arrow's place (SWP_SHOWWINDOW); the image goes in with UpdateLayeredWindow
            SetWindowPos(_hwnd, new IntPtr(-1), left, top, w, h, 0x0010 | 0x0040);   // SWP_NOACTIVATE | SWP_SHOWWINDOW
            var dst = new POINT { X = left, Y = top };
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };   // AC_SRC_OVER, AC_SRC_ALPHA
            UpdateLayeredWindow(_hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2);   // ULW_ALPHA
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ClickArrow.ShowMs) };
            _timer.Tick += (_, _) => HideNow();
        }
        _timer.Stop();
        _timer.Start();
    }

    static IntPtr CreateOverlay()
    {
        var cls = new WNDCLASS { lpfnWndProc = Proc, hInstance = GetModuleHandle(null), lpszClassName = "RetraceClickArrow" };
        RegisterClass(ref cls);   // a second registration (a reload) fails and the class from the first one is used
        // WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW; a popup with no parent and no owner
        return CreateWindowEx(0x80000 | 0x20 | 0x8 | 0x08000000 | 0x80, "RetraceClickArrow", "", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
    }

    static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0084: return new IntPtr(-1);   // WM_NCHITTEST: HTTRANSPARENT
            case 0x0021: return new IntPtr(3);    // WM_MOUSEACTIVATE: MA_NOACTIVATE
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    static List<PxRect> Monitors()
    {
        var list = new List<PxRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr d) => { list.Add(new PxRect(r.Left, r.Top, r.Right, r.Bottom)); return true; }, IntPtr.Zero);
        return list;
    }

    // ---- Win32 -------------------------------------------------------------------------------------------------------

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClass(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr sourceDc, ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum proc, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
}
