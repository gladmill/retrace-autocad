using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>
/// Whether a palette is open on screen, and how a person would open it. A floating palette set with auto roll-up shrinks to its title strip
/// when the mouse leaves it (its windows stay "visible" but move off to a corner); a docked one with auto-hide becomes a tab at the side and
/// its windows are hidden. Both open when the mouse rests on them. The replay uses this to show the palette it drives (setting showDrivenUi);
/// the recorder only uses <see cref="IsShown"/> and <see cref="PictureRect"/>, it never moves the mouse. UI thread only.
/// </summary>
static class PaletteReveal
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    const uint GA_ROOT = 2, GW_OWNER = 4;

    static RECT Rect(IntPtr h) { GetWindowRect(h, out var r); return r; }

    /// <summary>A window's screen rectangle (device pixels).</summary>
    public static RECT RectOf(IntPtr h) => Rect(h);

    /// <summary>A palette window lies where it belongs: its top-left corner inside the window that holds it (a page larger than its frame
    /// scrolls, so only the corner counts), and that window is more than a title strip (a rolled-up palette set is a strip about 22 px wide,
    /// with the palette moved off to the screen corner).</summary>
    static bool Inside(RECT w, RECT frame) =>
        w.Width > 0 && w.Height > 0 && frame.Width > 60 && frame.Height > 60
        && w.Left >= frame.Left - 2 && w.Top >= frame.Top - 2 && w.Left < frame.Right && w.Top < frame.Bottom;

    /// <summary>The palette's control bar: the window around the palette set (its parent's parent).</summary>
    static IntPtr Bar(IntPtr page) => DialogReader.GetParent(DialogReader.GetParent(page));

    /// <summary>true when the palette is open where it belongs: visible, and lying inside the window that holds it (a rolled-up palette's windows
    /// are moved off to the screen corner, outside its title strip).</summary>
    public static bool IsShown(IntPtr page)
    {
        if (page == IntPtr.Zero || !IsWindowVisible(page)) return false;
        return Inside(Rect(page), Rect(GetAncestor(page, GA_ROOT)));
    }

    /// <summary>true when the palette set around the palette is open (not rolled up, not slid away), whether or not this palette is its active tab:
    /// hovering would change nothing then.</summary>
    public static bool SetIsOpen(IntPtr page)
    {
        var set = DialogReader.GetParent(page);
        if (set == IntPtr.Zero || !IsWindowVisible(set)) return false;
        return Inside(Rect(set), Rect(GetAncestor(set, GA_ROOT)));
    }

    /// <summary>What a picture of the palette should cover while it is shown: its control bar (title, tabs and page), else the page.</summary>
    public static RECT? PictureRect(IntPtr page)
    {
        if (!IsShown(page)) return null;
        var bar = Bar(page);
        var r = bar != IntPtr.Zero && IsWindowVisible(bar) ? Rect(bar) : Rect(page);
        return r.Width > 0 && r.Height > 0 ? r : null;
    }

    /// <summary>Where to rest the mouse to open it, best first: the title strip of a rolled-up floating palette set; else the tab of an
    /// auto-hidden one, which sits just outside its slide-out frame on the side it is docked to.</summary>
    public static List<POINT> HoverPoints(IntPtr page)
    {
        var list = new List<POINT>();
        var frame = GetAncestor(page, GA_ROOT);
        var f = Rect(frame);
        if (IsWindowVisible(frame) && f.Width > 0 && f.Height > 0) list.Add(new POINT { X = f.Left + f.Width / 2, Y = f.Top + Math.Min(f.Height / 2, 200) });
        // auto-hidden: the frame is hidden; its tab is a strip about 20 px wide next to it, from its top down
        var owner = GetWindow(frame, GW_OWNER);
        var main = Rect(owner != IntPtr.Zero ? owner : frame);
        bool left = f.Left - main.Left < main.Right - f.Right;
        int x = left ? f.Left - 12 : f.Right + 12;
        foreach (var dy in new[] { 60, 150, 250 }) list.Add(new POINT { X = x, Y = f.Top + dy });
        return list;
    }

    /// <summary>The middle of the palette page, to keep the mouse on while the palette is being driven (it rolls up again when the mouse leaves).</summary>
    public static POINT Inside(IntPtr page) { var p = Rect(page); return new POINT { X = p.Left + p.Width / 2, Y = p.Top + Math.Min(p.Height / 2, 120) }; }

    /// <summary>The screen rectangle of the palette as shown (for keeping Retrace's own replay window off it).</summary>
    public static RECT Screen(IntPtr page) => PictureRect(page) ?? Rect(page);
}
