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

    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);

    /// <summary>Bringing a palette that is not the front tab of its (open) set to the front, by clicking its tab (see <see cref="Retrace.Core.PaletteTabs"/>,
    /// <see cref="Retrace.Core.TabSearch"/>). Made only when another page of the same set is in front, with the pages' order known. Only the set window's rectangle and the
    /// front page's rectangle are used (the page being looked for may have none), read again for each click: the set can move, re-dock or reopen. When the search fails,
    /// the tab that was in front is put back by the same kind of search.</summary>
    public sealed class TabSession
    {
        readonly IntPtr _set;
        readonly List<IntPtr> _pages;   // the palettes that have a tab, in the order they were added
        readonly Retrace.Core.TabSearch _search;
        readonly int _start;
        bool _restoring;
        (int Offset, POINT Screen)? _last;   // the click made and not yet looked at

        TabSession(IntPtr set, List<IntPtr> pages, int target, int front)
        {
            _set = set; _pages = pages; _start = front;
            _search = new Retrace.Core.TabSearch(pages.Count, target, front);
        }

        /// <summary>Clicks made so far, in both searches.</summary>
        public int Clicks { get; private set; }

        /// <summary>The session, or null when there is no positive evidence that <paramref name="page"/> is a back tab of an open set: some other page of the same set is
        /// in front, and the pages' order is known.</summary>
        public static TabSession? Begin(IntPtr page)
        {
            var set = DialogReader.GetParent(page);
            if (set == IntPtr.Zero || !SetIsOpen(page)) return null;
            var setTitle = Native.TitleOf(set);
            var ids = new List<(IntPtr H, int Id)>();
            foreach (var h in Native.ChildrenOf(set))
                if (DialogReader.ClassOf(h).StartsWith("AfxWnd", StringComparison.Ordinal) && Native.TitleOf(h).Length > 0 && PaletteFill.IsPage(h, setTitle)) ids.Add((h, DialogReader.GetDlgCtrlID(h)));
            int target = Retrace.Core.PaletteTabs.IndexOf(DialogReader.GetDlgCtrlID(page), ids.Select(p => p.Id).ToList());
            if (target < 0) return null;
            var pages = ids.OrderBy(p => p.Id).Select(p => p.H).ToList();
            var front = pages.Select((h, i) => (h, i)).Where(p => p.i != target && IsShown(p.h)).ToList();
            if (front.Count != 1) return null;
            return new TabSession(set, pages, target, front[0].i);
        }

        /// <summary>The page in front now (its index in the set), -1 when none or more than one shows.</summary>
        int Shown()
        {
            var shown = _pages.Select((h, i) => (h, i)).Where(p => IsShown(p.h)).ToList();
            return shown.Count == 1 ? shown[0].i : -1;
        }

        /// <summary>Where the mouse may rest to keep a rolled-up set open: on the page in front (never on the page looked for, which may have no rectangle).</summary>
        public POINT? CursorSpot() => _pages.FirstOrDefault(IsShown) is var h && h != IntPtr.Zero ? Inside(h) : null;

        /// <summary>Where the click was, on the screen; null before any click.</summary>
        public POINT? LastClick => _last?.Screen;

        /// <summary>Looks at what the last click did, then makes the next one: for the palette, and once the clicks for it are used up, for the tab that was in front. false when
        /// there is nothing more to try: then the palette did not come to the front and the original tab is back, or could not be put back (logged).</summary>
        public bool Click()
        {
            if (_last is { } l) _search.Observe(l.Offset, Shown());
            _last = null;
            if (!_restoring)
            {
                if (Post(_search.Next)) return true;
                _restoring = true;
                if (Shown() != _start) _search.Retarget(_start);
            }
            if (Shown() == _start) return false;
            if (Post(_search.Next)) return true;
            Log.Info("replay: could not put the tab that was in front back");
            return false;
        }

        /// <summary>Makes the click <paramref name="next"/> gives for the strip as it is now; false when there is none, or no strip. The tab strip lies in the set window's
        /// non-client part (its client area is only the pages), so the strip and the offsets are measured in the set window's own rectangle; the click is posted in client
        /// coordinates and never outside that rectangle.</summary>
        bool Post(Func<int, int?> next)
        {
            var front = _pages.FirstOrDefault(IsShown);
            if (front == IntPtr.Zero) return false;
            RECT w = Rect(_set), f = Rect(front);
            if (Retrace.Core.PaletteTabs.StripX(w.Width, f.Left - w.Left, f.Right - w.Left, (int)Win32.GetDpiForWindow(_set)) is not { } x) return false;
            if (next(w.Height) is not { } off || x < 0 || x >= w.Width || off < 0 || off >= w.Height) return false;
            var screen = new POINT { X = w.Left + x, Y = w.Top + off };
            var client = screen;
            if (!ScreenToClient(_set, ref client)) return false;
            Native.PostClick(_set, client.X, client.Y);
            _last = (off, screen);
            Clicks++;
            return true;
        }
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
