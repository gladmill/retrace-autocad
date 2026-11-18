using System.Runtime.InteropServices;
using System.Windows.Interop;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>
/// Reads the palette pages of AutoCAD's process as they are now, for the recorder's "this command opened a palette" mark (<see cref="PaletteOpening"/> decides). A page is a window titled with the palette's name
/// whose parent is the palette set (AfxWnd) and whose grandparent is a control bar (AfxControlBar), the same chain <see cref="PaletteFill"/> looks for. The ribbon (hosted the same way) and Retrace's own guide are left out.
/// Read-only: nothing is moved, shown or clicked. UI thread only.
/// </summary>
static class PaletteSnapshot
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumProc(IntPtr h, IntPtr data);

    /// <summary>Every palette page of this process: shown or not, with the rectangle its picture would cover (null when it is not shown).</summary>
    public static List<PaletteState> Read()
    {
        var list = new List<PaletteState>();
        try
        {
            uint me = (uint)Environment.ProcessId;
            var ribbon = RibbonHost();
            EnumWindows((top, _) =>
            {
                GetWindowThreadProcessId(top, out var pid);
                if (pid != me) return true;
                EnumChildWindows(top, (h, _) =>
                {
                    if (!DialogReader.ClassOf(h).StartsWith("AfxWnd", StringComparison.Ordinal)) return true;
                    var name = Native.TitleOf(h);
                    if (name.Length == 0) return true;
                    var set = DialogReader.GetParent(h);
                    if (!DialogReader.ClassOf(set).StartsWith("AfxWnd", StringComparison.Ordinal) || !DialogReader.ClassOf(DialogReader.GetParent(set)).StartsWith("AfxControlBar", StringComparison.Ordinal)) return true;
                    if (ribbon != IntPtr.Zero && (ribbon == h || IsChild(h, ribbon))) return true;
                    var setTitle = Native.TitleOf(set);
                    if (setTitle == Retrace.Ui.GuidePalette.SetTitle) return true;
                    bool shown = PaletteReveal.IsShown(h);
                    PxRect? rect = shown && PaletteReveal.PictureRect(h) is { } r ? new PxRect(r.Left, r.Top, r.Right, r.Bottom) : null;
                    list.Add(new PaletteState(h.ToInt64(), name, setTitle, shown, rect, shown ? null : RolledStrip(h), Diag: shown ? "" : WhyNotShown(h)));
                    return true;
                }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("palette snapshot", ex); }
        list.AddRange(AnchorBars());
        return list;
    }

    static string R(RECT r) => $"{r.Left},{r.Top},{r.Right},{r.Bottom}";

    /// <summary>Why a page failed <see cref="PaletteReveal.IsShown"/>, in one line: the page's visibility and rectangle, its root window's class, visibility and rectangle, and the first test that failed.</summary>
    static string WhyNotShown(IntPtr page)
    {
        try
        {
            var root = GetAncestor(page, 2);
            bool pv = IsWindowVisible(page); GetWindowRect(page, out var pr);
            if (root == IntPtr.Zero) return $"page visible={pv} rect {R(pr)}; no root window; failed: no root";
            bool rv = IsWindowVisible(root); GetWindowRect(root, out var rr);
            int rw = rr.Right - rr.Left, rh = rr.Bottom - rr.Top;
            string why = !pv ? "the page is not visible" : rw <= 60 || rh <= 60 ? "the root window is 60 px or less (a rolled-up strip, or hidden)" : pr.Left < rr.Left - 2 || pr.Top < rr.Top - 2 || pr.Left >= rr.Right || pr.Top >= rr.Bottom ? "the page's top-left corner is outside its root window" : "unknown";
            return $"page visible={pv} rect {R(pr)}; root {DialogReader.ClassOf(root)} visible={rv} rect {R(rr)}; failed: {why}";
        }
        catch (Exception ex) { return "(" + ex.Message + ")"; }
    }

    /// <summary>A floating palette set in Auto-hide state with the mouse away is rolled up: its frame (the page's top-level window) stays on screen as a title strip (about 22 px wide) and the pages are moved off. The strip's rectangle, read from
    /// that window (never guessed); null when the frame is hidden (closed), is AutoCAD's own main window (a docked set), or is a normal size (an open set whose tab is not in front).</summary>
    static PxRect? RolledStrip(IntPtr page)
    {
        try
        {
            var root = GetAncestor(page, 2 /* GA_ROOT */);
            if (root == IntPtr.Zero || root == page || DialogReader.ClassOf(root).StartsWith("AfxMDIFrame", StringComparison.Ordinal) || !IsWindowVisible(root) || !GetWindowRect(root, out var r)) return null;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            return w > 0 && h > 0 && (w <= 60 || h <= 60) ? new PxRect(r.Left, r.Top, r.Right, r.Bottom) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The bars at the edge of AutoCAD's window that hold the title strips of docked palette sets in Auto-hide state ("anchored"): a visible control bar, a direct child of the main frame, thin (40 px or less) and long (100 px or more), holding one dialog window. Found only
    /// while at least one set is anchored (the bar is hidden otherwise). The tab of a set is drawn inside it, not a window of its own.</summary>
    static List<PaletteState> AnchorBars()
    {
        var bars = new List<PaletteState>();
        try
        {
            var main = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle;
            if (main == IntPtr.Zero) return bars;
            for (var h = FindWindowEx(main, IntPtr.Zero, null, null); h != IntPtr.Zero; h = FindWindowEx(main, h, null, null))   // direct children only
            {
                if (!DialogReader.ClassOf(h).StartsWith("AfxControlBar", StringComparison.Ordinal) || !IsWindowVisible(h) || !GetWindowRect(h, out var r)) continue;
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (Math.Min(w, ht) > 40 || Math.Max(w, ht) < 100 || Math.Min(w, ht) <= 0) continue;
                if (FindWindowEx(h, IntPtr.Zero, "#32770", null) != IntPtr.Zero) bars.Add(new PaletteState(h.ToInt64(), "(auto-hide bar)", "", false, new PxRect(r.Left, r.Top, r.Right, r.Bottom), IsBar: true));
            }
        }
        catch (Exception ex) { Log.Error("auto-hide bars", ex); }
        return bars;
    }

    static IntPtr RibbonHost()
    {
        try { return Autodesk.Windows.ComponentManager.Ribbon is { } r && System.Windows.PresentationSource.FromVisual(r) is HwndSource src ? src.Handle : IntPtr.Zero; }
        catch (Exception) { return IntPtr.Zero; }
    }
}
