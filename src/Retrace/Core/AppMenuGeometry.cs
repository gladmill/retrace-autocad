namespace Retrace.Core;

// NO Win32, WPF or AutoCAD references in this file (linked into the engine test project).

/// <summary>Is a menu pop-up the application menu (the big A): it opens at the top-left corner of AutoCAD's window, tall. A ribbon drop-down or a toolbar's customize menu opens under its own button, further right or down; the workspace switcher
/// opens at the bottom. The pop-up's corner is measured from the main window's own corner and the limits scale with the window's DPI, so a window that is moved, smaller, maximized (its frame starts a few pixels outside the screen) or on another
/// monitor gives the same answer.</summary>
public static class AppMenuGeometry
{
    public const int MaxLeft = 80, MaxTop = 60, MinHeight = 200;

    /// <param name="left">the pop-up's left edge minus the main window's left edge, device pixels</param>
    /// <param name="top">the pop-up's top edge minus the main window's top edge</param>
    /// <param name="height">the pop-up's height</param>
    /// <param name="scale">the window's DPI divided by 96 (1.0 at 100 %)</param>
    public static bool IsAppMenu(int left, int top, int height, double scale)
    {
        if (scale <= 0) scale = 1;
        return left >= -16 * scale && left <= MaxLeft * scale && top >= -16 * scale && top <= MaxTop * scale && height > MinHeight * scale;
    }
}
