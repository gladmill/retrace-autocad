using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// The one place that decides where a Retrace window opens (the geometry is <see cref="PlacementGeometry"/>, unit-tested; this is the Win32 part).
/// <see cref="MainInterface"/>: a main interface (Settings, Open a test, Map, Training doc, Checkpoints, the test script, the prompt window) is owned by the AutoCAD main window
/// (so it is always in front of AutoCAD and its palettes, never behind them) and opened centred on that window, on the monitor AutoCAD is on; a child dialog (Sysvar,
/// Confirm) centres on its parent window. A window larger than that monitor's work area is made smaller. The size of a resizable window is remembered (<see cref="WindowSizes"/>), the position never.
/// <see cref="Progress"/>: a progress notice (the replay window, also used by Tdoc and suites) opens in the lower right corner of AutoCAD's monitor work area, topmost and without taking the focus;
/// when its text grows, its bottom edge stays where it was (the buttons never slide under the taskbar).
/// All of it works in device pixels from the window's real rectangle, so a monitor left of the primary one (negative coordinates) is right; a move to a monitor with another scale
/// resizes the window, so it is placed again with the size it has then (checked in the unit tests with the pure geometry; only one 100 % monitor was available to test on).
/// Never throws; a failure leaves the window where WPF put it. A docked palette (the Guide) is not placed: it stays where the user docked it.
/// </summary>
internal static class WindowPlacement
{
    /// <summary>Own the window by AutoCAD's main window and centre it on that window (or on its parent window, <see cref="Window.Owner"/>) when it opens. Call from the constructor, after the size is set.</summary>
    public static void MainInterface(Window w)
    {
        try
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            OwnByMain(w);
            RestoreSize(w);
            Hook(w, Kind.Center);
        }
        catch (Exception ex) { Log.Error("window placement", ex); }
    }

    /// <summary>A progress notice: owned by AutoCAD's main window, topmost, never activated, in the lower right corner of its monitor's work area. Call from the constructor.</summary>
    public static void Progress(Window w)
    {
        try
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Topmost = true;
            w.ShowActivated = false;
            OwnByMain(w);
            Hook(w, Kind.Corner);
            // after the layout pass: at SizeChanged the window handle still has the old size
            w.SizeChanged += (_, _) => w.Dispatcher.BeginInvoke(new Action(() => KeepBottom(w)), System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex) { Log.Error("window placement", ex); }
    }

    enum Kind { Center, Corner }

    static void OwnByMain(Window w)
    {
        if (w.Owner != null) return;   // a child dialog already has its parent
        var main = MainHandle();
        if (main != IntPtr.Zero) new WindowInteropHelper(w).Owner = main;
    }

    // ---- remembered size (resizable windows only) ----

    static string SizeKey(Window w) => w.GetType() == typeof(Window) ? "Window:" + w.Title : w.GetType().Name;

    static bool Resizable(Window w) => w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip && w.SizeToContent == SizeToContent.Manual;

    static void RestoreSize(Window w)
    {
        if (!Resizable(w)) return;
        if (WindowSizes.Read(SizeKey(w)) is { } s)
        {
            w.Width = Math.Max(s.Width, w.MinWidth);
            w.Height = Math.Max(s.Height, w.MinHeight);
        }
        w.Closing += (_, _) =>
        {
            try { var b = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds; WindowSizes.Write(SizeKey(w), b.Width, b.Height); }
            catch (Exception) { }
        };
    }

    // ---- placing ----

    static void Hook(Window w, Kind kind)
    {
        w.SourceInitialized += (_, _) => Apply(w, kind, first: true);
        w.Loaded += (_, _) => Apply(w, kind, first: false);
    }

    // Placed at SourceInitialized (the window has its handle and size, is not shown yet) and again at Loaded: WPF moves a window whose Left / Top were never set to the system's default
    // place after SourceInitialized (seen: a window opened 100 pixels off the centre and another 80 pixels too high), so the second pass is not optional.
    static void Apply(Window w, Kind kind, bool first)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            var parent = w.Owner != null ? new WindowInteropHelper(w.Owner).Handle : IntPtr.Zero;
            var corner = kind == Kind.Corner;
            for (var pass = 0; pass < 3; pass++)   // a move to a monitor with another scale resizes the window: place it again with the size it has now
            {
                if (!GetWindowRect(h, out var me)) return;
                var ctx = Context(parent);
                if (ctx is not { } c) return;
                var margin = corner ? PlacementGeometry.Scale(PlacementGeometry.CornerMargin, DpiOf(h)) : 0;
                var room = corner ? new PxBox(c.Work.Left, c.Work.Top + margin, c.Work.Right, c.Work.Bottom - margin) : c.Work;
                var (fw, fh) = PlacementGeometry.FitSize(me.Right - me.Left, me.Bottom - me.Top, room);
                var scale = DpiOf(h) / 96.0;
                if (scale > 0)   // later growth (SizeToContent, a longer text) is capped too
                {
                    try { w.MaxWidth = Math.Min(w.MaxWidth, Math.Max(1, room.Width / scale)); w.MaxHeight = Math.Min(w.MaxHeight, Math.Max(1, room.Height / scale)); } catch (Exception) { }
                }
                var at = corner ? PlacementGeometry.LowerRight(c.Work, fw, fh, margin) : PlacementGeometry.CenterOn(c.Owner, fw, fh, c.Work);
                var resize = fw != me.Right - me.Left || fh != me.Bottom - me.Top;
                if (resize || me.Left != at.X || me.Top != at.Y)
                {
                    // WPF's own Left / Top first (device pixels to its units), so it does not move the window back to where it thinks it is
                    if (first && scale > 0) { try { w.Left = at.X / scale; w.Top = at.Y / scale; } catch (Exception) { } }
                    SetWindowPos(h, IntPtr.Zero, at.X, at.Y, fw, fh, SWP_NOZORDER | SWP_NOACTIVATE | (resize ? 0u : SWP_NOSIZE));
                }
                if (!first && pass == 0) Log.Info($"window placement: {(corner ? "lower right" : "centred")} {fw}x{fh} on {c.Describe} work {c.Work.Left},{c.Work.Top},{c.Work.Right},{c.Work.Bottom} -> {at.X},{at.Y}");
                if (!GetWindowRect(h, out var after) || (after.Right - after.Left == fw && after.Bottom - after.Top == fh)) break;
            }
        }
        catch (Exception ex) { Log.Error("window placement", ex); }
    }

    static bool _keeping;

    /// <summary>A progress notice that changed height: the bottom edge stays where it was.</summary>
    static void KeepBottom(Window w)
    {
        if (_keeping) return;
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero || !w.IsLoaded || !GetWindowRect(h, out var me)) return;
            var ctx = Context(IntPtr.Zero);
            if (ctx is not { } c) return;
            var cur = new PxBox(me.Left, me.Top, me.Right, me.Bottom);
            var work = PlacementGeometry.PickWork(cur, WorkAreas()) ?? c.Work;
            var margin = PlacementGeometry.Scale(PlacementGeometry.CornerMargin, DpiOf(h));
            var (x, y, height) = PlacementGeometry.KeepBottom(cur, work, margin);
            if (x == me.Left && y == me.Top && height == cur.Height) return;
            _keeping = true;
            try { SetWindowPos(h, IntPtr.Zero, x, y, cur.Width, height, SWP_NOZORDER | SWP_NOACTIVATE); }
            finally { _keeping = false; }
        }
        catch (Exception ex) { Log.Error("window placement", ex); }
    }

    readonly record struct Where(PxBox Owner, PxBox Work, string Describe);

    /// <summary>The rectangle to centre on and the work area of its monitor: the parent window or AutoCAD's main window; a minimized AutoCAD is no rectangle, its monitor's work area is used.</summary>
    static Where? Context(IntPtr parent)
    {
        var anchor = parent != IntPtr.Zero ? parent : MainHandle();
        var works = WorkAreas();
        if (works.Count == 0) return null;
        if (anchor != IntPtr.Zero && IsIconic(anchor))
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var wk = GetMonitorInfo(MonitorFromWindow(anchor, 2 /* MONITOR_DEFAULTTONEAREST */), ref mi) ? new PxBox(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom) : works[0];
            return new Where(wk, wk, "minimized window's monitor");
        }
        PxBox? rect = null;
        if (anchor != IntPtr.Zero && GetWindowRect(anchor, out var r)) rect = new PxBox(r.Left, r.Top, r.Right, r.Bottom);
        var work = PlacementGeometry.PickWork(rect ?? works[0], works) ?? works[0];
        return new Where(PlacementGeometry.OwnerOrWork(rect, false, work), work, rect is { } b ? $"{b.Left},{b.Top},{b.Right},{b.Bottom}" : "none");
    }

    static IntPtr MainHandle()
    {
        try { return AcApp.MainWindow.Handle; } catch (Exception) { return IntPtr.Zero; }
    }

    static int DpiOf(IntPtr h)
    {
        try { if (h != IntPtr.Zero) { var d = GetDpiForWindow(h); if (d > 0) return (int)d; } } catch (Exception) { }
        return 96;
    }

    static List<PxBox> WorkAreas()
    {
        var l = new List<PxBox>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(m, ref mi)) l.Add(new PxBox(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom));
            return true;
        }, IntPtr.Zero);
        return l;
    }

    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
}
