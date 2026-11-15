using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>
/// One coordinate space for pictures. AutoCAD is system DPI aware: when its monitor's scale differs from the scale it started with (the scale was changed after
/// sign-in, or the window is on a second monitor with another scale) Windows scales its window by bitmap, and every coordinate AutoCAD's own thread reads
/// (GetWindowRect, ClientToScreen, mouse messages, WinForms bounds) is in that scaled, "logical" space, while the screen itself, DWM's frame bounds and BitBlt
/// are physical pixels. Retrace keeps every rectangle and click point in the logical space (the space SetCursorPos and the clicks use) and converts only at the
/// capture: <see cref="ToPhysical"/> for what is copied from the screen, <see cref="ToLogical"/> for what DWM reports. A picture is then the logical rectangle
/// at physical resolution, and the frames and arrows map by ratio (<see cref="ShotService.Picture.SrcWidth"/>). Without scaling both are the identity.
/// </summary>
static class ScreenSpace
{
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    static readonly IntPtr PerMonitorV2 = new(-4);

    /// <summary>The window whose monitor decides the scale of a point when the caller has no window of its own: AutoCAD's main window, set when a recording or replay starts.</summary>
    public static IntPtr Reference { get; set; }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);

    /// <summary>The window's rectangle as this thread reads it (logical) and as the screen has it (physical: read under a per-monitor aware thread context, in which Windows
    /// answers in the screen's own pixels for every window); null when it cannot be read or is empty. The two give the window's own scale and origin offset, which is exact for the
    /// monitor it is on (a point on another monitor with another scale is converted with that monitor's window).</summary>
    static (RECT Logical, RECT Physical)? Both(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !GetWindowRect(hwnd, out var l) || l.Right <= l.Left || l.Bottom <= l.Top) return null;
        var old = SetThreadDpiAwarenessContext(PerMonitorV2);
        try { return GetWindowRect(hwnd, out var p) && p.Right > p.Left && p.Bottom > p.Top ? (l, p) : null; }
        finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }

    static (int X, int Y) Map(int x, int y, RECT from, RECT to) =>
        Retrace.Core.RectMap.Map(x, y, new Retrace.Core.PxRect(from.Left, from.Top, from.Right, from.Bottom), new Retrace.Core.PxRect(to.Left, to.Top, to.Right, to.Bottom));

    static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>A logical point as a physical screen pixel, by the scale of <paramref name="hwnd"/> (the reference window when none): unchanged when nothing is virtualised.</summary>
    public static (int X, int Y) ToPhysical(IntPtr hwnd, int x, int y)
    {
        try { if (Both(hwnd != IntPtr.Zero ? hwnd : Reference) is { } w && !Same(w.Logical, w.Physical)) return Map(x, y, w.Logical, w.Physical); }
        catch (Exception ex) { Log.Error("logical to physical", ex); }
        return (x, y);
    }

    /// <summary>A physical screen pixel as a logical point, by the scale of <paramref name="hwnd"/> (the reference window when none): unchanged when nothing is virtualised.</summary>
    public static (int X, int Y) ToLogical(IntPtr hwnd, int x, int y)
    {
        try { if (Both(hwnd != IntPtr.Zero ? hwnd : Reference) is { } w && !Same(w.Logical, w.Physical)) return Map(x, y, w.Physical, w.Logical); }
        catch (Exception ex) { Log.Error("physical to logical", ex); }
        return (x, y);
    }

    /// <summary>Makes this thread read physical coordinates until disposed (for a tool that reads geometry and captures in one go).</summary>
    public static IDisposable Physical() => new Scope(SetThreadDpiAwarenessContext(PerMonitorV2));

    sealed class Scope(IntPtr old) : IDisposable
    {
        public void Dispose() { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }
}
