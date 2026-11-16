using System.Windows;
using System.Windows.Media;
using Autodesk.Windows;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>
/// Smart zoom (setting <c>screenshotMode</c> = smart): reads the rectangles a picture's crop is decided from (<see cref="SmartCrop"/>, pure) and copies the crop 1:1. Everything is in the process's logical
/// screen space (ScreenSpace); the capture converts. A rectangle that cannot be read is left out and <see cref="SmartCrop"/> falls back (the whole ribbon, the window); nothing is guessed. UI thread only.
/// </summary>
static class SmartShots
{
    /// <summary>The ribbon rectangles of a click on item <paramref name="itemId"/> (<paramref name="parentId"/>: the button that holds it, tried when the item itself does not show, e.g. inside a drop-down), as the
    /// ribbon's WPF tree shows them now: the item, its panel, the headers of the tabs the ribbon shows and the active tab's, the whole ribbon, and the rectangles a crop edge should not cut through. Null when there is no ribbon or no window.</summary>
    public static CropRects? ReadRibbon(string? itemId, string? parentId, RibbonPanel? panel, IntPtr main, bool workArea)
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null || ShotService.WindowRect(main, workArea) is not { } window) return null;
            var item = ItemRect(itemId, parentId);
            if (panel == null && !string.IsNullOrEmpty(itemId) && ribbon.FindItem(itemId, false, true) is { } found)   // the panel is not known (the training document): the active tab's panel that holds the item
                panel = ribbon.ActiveTab?.Panels.FirstOrDefault(p => TdocTargets.Holds(p.Source?.Items, found, 0));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (panelRect, headers, active, snap) = Walk(ribbon, panel);
            if (sw.ElapsedMilliseconds > 30) Log.Info($"smart ribbon: reading the ribbon's tree took {sw.ElapsedMilliseconds} ms");
            PxRect? bar = null;
            if (PresentationSource.FromVisual(ribbon) != null && ribbon.ActualWidth >= 1 && ribbon.ActualHeight >= 1)
            {
                var tl = ribbon.PointToScreen(new Point(0, 0));
                var br = ribbon.PointToScreen(new Point(ribbon.ActualWidth, ribbon.ActualHeight));
                bar = new PxRect((int)Math.Floor(tl.X), (int)Math.Floor(tl.Y), (int)Math.Ceiling(br.X), (int)Math.Ceiling(br.Y));
            }
            return new CropRects(window, item, panelRect, headers, active, bar, Snap: snap);
        }
        catch (Exception ex) { Log.Error("smart ribbon rectangles", ex); return null; }
    }

    /// <summary>The rectangle a ribbon click is framed on and cropped around: the item itself when it shows; else (an item inside a drop-down) the button that holds it, when that shows; else, for the crop only, the
    /// item's panel (<see cref="TdocTargets.RibbonItemRect"/> answers it for an item that is not shown). Null when none is known.</summary>
    public static PxRect? ItemRect(string? itemId, string? parentId, bool markOnly = false)
    {
        var hit = string.IsNullOrEmpty(itemId) ? null : TdocTargets.RibbonItemRect(itemId!);
        if (hit is { Panel: false } own) return Px(own.Rect);
        if (!string.IsNullOrEmpty(parentId) && TdocTargets.RibbonItemRect(parentId!) is { Panel: false } up) return Px(up.Rect);
        return !markOnly && hit is { } panelHit ? Px(panelHit.Rect) : null;
    }

    static PxRect Px(ScreenRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>One walk over the ribbon's visual tree (not into what is not shown): the panel's rectangle (the outermost shown element whose DataContext is the panel), the header of each shown tab and which one is the
    /// active tab's, and every panel of the active tab. The tab's content shares the tab's DataContext, so a tab's header is the smallest shown element with that DataContext that lies above the panel row (its bottom is not below the
    /// panel's top) or whose class name contains "Header"; a tab without one gets none (the crop then falls back to the whole ribbon).</summary>
    static (PxRect? Panel, List<PxRect> Headers, PxRect? Active, List<PxRect> Snap) Walk(RibbonControl ribbon, RibbonPanel? panel)
    {
        PxRect? panelRect = null;
        var cands = new Dictionary<RibbonTab, List<(PxRect R, bool Named)>>();
        var panels = new Dictionary<RibbonPanel, PxRect>();
        var activeTab = ribbon.ActiveTab;
        var tabs = new HashSet<RibbonTab>(ribbon.Tabs.Where(t => t.IsVisible));
        var queue = new Queue<DependencyObject>(); queue.Enqueue(ribbon);
        for (int seen = 0; queue.Count > 0 && seen < 50000; seen++)
        {
            var v = queue.Dequeue();
            if (v is FrameworkElement fe)
            {
                if (!fe.IsVisible) continue;   // nothing below what is not shown is shown
                if (fe.ActualWidth >= 1 && fe.ActualHeight >= 1)
                {
                    if (panel != null && panelRect == null && ReferenceEquals(fe.DataContext, panel) && ScreenOf(fe) is { } pr) panelRect = pr;
                    if (fe.DataContext is RibbonPanel rp && !panels.ContainsKey(rp) && activeTab != null && activeTab.Panels.Contains(rp) && ScreenOf(fe) is { } rr) panels[rp] = rr;
                    else if (fe.DataContext is RibbonTab t && tabs.Contains(t) && ScreenOf(fe) is { } r)
                    {
                        if (!cands.TryGetValue(t, out var l)) cands[t] = l = new();
                        l.Add((r, fe.GetType().Name.Contains("Header", StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
            if (v is Visual || v is System.Windows.Media.Media3D.Visual3D)
                for (int i = 0, n = VisualTreeHelper.GetChildrenCount(v); i < n; i++) queue.Enqueue(VisualTreeHelper.GetChild(v, i));
        }
        // the panel row's top: the panel's own, else the top of the active tab's panels
        int? rowTop = panelRect?.Top ?? (panels.Count > 0 ? panels.Values.Min(x => x.Top) : null);
        var headers = new List<PxRect>();
        PxRect? active = null;
        foreach (var (t, list) in cands)
        {
            var ok = list.Where(c => c.Named || (rowTop is { } top && c.R.Bottom <= top)).OrderByDescending(c => c.Named).ThenBy(c => (long)c.R.Width * c.R.Height).ToList();
            if (ok.Count == 0) continue;
            headers.Add(ok[0].R);
            if (ReferenceEquals(t, activeTab)) active = ok[0].R;
        }
        var snap = new List<PxRect>(headers);
        snap.AddRange(panels.Values);
        return (panelRect, headers, active, snap);
    }

    static PxRect? ScreenOf(FrameworkElement fe)
    {
        if (PresentationSource.FromVisual(fe) == null) return null;
        var tl = fe.PointToScreen(new Point(0, 0));
        var br = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
        return new PxRect((int)Math.Floor(tl.X), (int)Math.Floor(tl.Y), (int)Math.Ceiling(br.X), (int)Math.Ceiling(br.Y));
    }

    /// <summary>The picture of the smart ribbon crop for a click now, or null (then the caller takes the window as in the classic mode). The picture is a 1:1 copy of the crop.</summary>
    public static ShotService.Picture? RibbonPicture(string? itemId, string? parentId, RibbonPanel? panel, IntPtr main, bool workArea = true)
    {
        if (main == IntPtr.Zero || ReadRibbon(itemId, parentId, panel, main, workArea) is not { } rects) return null;
        var d = SmartCrop.Decide(PictureKind.Ribbon, rects);
        Log.Info($"smart ribbon picture: {d.Reason} {d.Rect.Left},{d.Rect.Top} {d.Rect.Width}x{d.Rect.Height} (item {Fmt(rects.Item)}, panel {Fmt(rects.Panel)}, active tab {Fmt(rects.ActiveTab)}, {rects.TabHeaders?.Count ?? 0} tab headers)");
        if (d.IsWindow) return null;
        return ShotService.CaptureRect(d.Rect.Left, d.Rect.Top, d.Rect.Right, d.Rect.Bottom, main);
    }

    /// <summary>The screen area of a palette's picture: its frame as it shows (<see cref="PaletteReveal.PictureRect"/>) and, in smart mode, that plus its surroundings (<see cref="SmartCrop"/>); null when it does not show.</summary>
    public static (int L, int T, int R, int B)? PaletteArea(IntPtr page, bool smart)
    {
        if (PaletteReveal.PictureRect(page) is not { } r) return null;
        if (!smart || ShotService.WindowRect(ScreenSpace.Reference, true) is not { } window) return (r.Left, r.Top, r.Right, r.Bottom);
        var d = SmartCrop.Decide(PictureKind.Palette, new CropRects(window, Palette: new PxRect(r.Left, r.Top, r.Right, r.Bottom), PaletteMonitor: ShotService.WorkAreaOf(page)));
        return (d.Rect.Left, d.Rect.Top, d.Rect.Right, d.Rect.Bottom);
    }

    static string Fmt(PxRect? r) => r is { } x ? $"{x.Left},{x.Top} {x.Width}x{x.Height}" : "none";
}
