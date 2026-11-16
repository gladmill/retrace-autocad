namespace Retrace.Core;

// NO AutoCAD, WPF or System.Drawing references in this file (linked into the engine test project).

/// <summary>The values of <see cref="RetraceSettings.ScreenshotMode"/> (setting <c>screenshotMode</c>): how the pictures of a document are cropped.</summary>
public static class ScreenshotModes
{
    /// <summary>The pictures as they always were (the default): the ribbon click shows the whole window, a palette click the palette's frame.</summary>
    public const string Classic = "classic";
    /// <summary>Smart zoom: a ribbon click shows the button, its panel and the tab row; a palette click the whole palette with some surroundings; every picture is a 1:1 crop.</summary>
    public const string Smart = "smart";

    public static readonly string[] All = { Classic, Smart };

    /// <summary>The canonical value for a text from a settings file or the command line: matched without case, around spaces and quotes; "1" and a few spellings ("smartzoom", "zoom") also work.
    /// Anything else, or null, is <see cref="Classic"/>.</summary>
    public static string Normalize(string? text) => Normalize(text, out _);

    /// <summary>As <see cref="Normalize(string?)"/>; <paramref name="known"/> is false when the text is none of the spellings (it then reads as classic).</summary>
    public static string Normalize(string? text, out bool known)
    {
        var t = (text ?? "").Trim().Trim('"').Trim().Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
        known = true;
        return t switch
        {
            "smart" or "smartzoom" or "zoom" or "s" or "1" => Smart,
            "" or "classic" or "c" or "asisnow" or "off" or "0" or "no" or "none" => Classic,
            _ => Unknown(out known),
        };
        static string Unknown(out bool known) { known = false; return Classic; }
    }
}

/// <summary>What a picture is of, for the crop decision of smart zoom.</summary>
public enum PictureKind { Ribbon, Palette, Dialog, Drawing }

/// <summary>The rectangles known for a picture, all in the process's logical screen space (Right and Bottom exclusive). Missing ones are null.</summary>
/// <param name="Window">AutoCAD's main window (the picture of the classic mode); never null</param>
/// <param name="Item">the ribbon item that was clicked (or, inside a drop-down, the button that holds it), when it shows</param>
/// <param name="Panel">the ribbon panel that holds it</param>
/// <param name="TabHeaders">the headers of the tabs the ribbon shows, in any order</param>
/// <param name="ActiveTab">the header of the active tab, one of <paramref name="TabHeaders"/></param>
/// <param name="Ribbon">the whole ribbon (tab row and panels, full width): the fallback when the rest is not known</param>
/// <param name="Palette">the palette as it shows (its frame, tabs and page)</param>
/// <param name="PaletteMonitor">the work area of the monitor the palette is on: a palette outside the main window is padded only inside it</param>
/// <param name="Snap">every ribbon rectangle whose text a crop edge should not cut through: the tab headers and the panels of the active tab</param>
public sealed record CropRects(PxRect Window, PxRect? Item = null, PxRect? Panel = null, IReadOnlyList<PxRect>? TabHeaders = null, PxRect? ActiveTab = null, PxRect? Ribbon = null, PxRect? Palette = null,
    PxRect? PaletteMonitor = null, IReadOnlyList<PxRect>? Snap = null);

/// <summary>The crop a smart-zoom picture is taken of, and why.</summary>
/// <param name="Rect">the logical screen rectangle to copy 1:1</param>
/// <param name="Reason">"ribbon", "ribbon-whole" (a rectangle was missing: the whole ribbon), "palette", or "window" (the window as it is: dialog, drawing, nothing known)</param>
public readonly record struct CropDecision(PxRect Rect, string Reason)
{
    public bool IsWindow => Reason == "window";
}

/// <summary>Smart zoom (setting <c>screenshotMode</c> = smart): which rectangle of the screen a picture shows. The pictures of one document then show text and buttons at the same size, because each is a
/// 1:1 copy of a rectangle, never scaled, and a small crop is padded with the real surroundings up to a common minimum size. Pure: the capture reads the rectangles, this decides, the capture copies.
/// <list type="bullet">
/// <item>Ribbon click: the clicked item, its panel, the active tab's header and <see cref="NeighbourTabs"/> tab headers on each side of it (the reader finds the tab in a long row), plus <see cref="Margin"/> around them. When the
/// item is far to the right of the tab's name the crop is as wide as that takes. A rectangle that is missing (item inside a drop-down that is not shown, tab headers not found) gives the whole ribbon, full width; never a guess.</item>
/// <item>Palette click: the whole palette plus <see cref="Margin"/> around it.</item>
/// <item>Dialog and drawing pictures: the window, as before.</item>
/// </list>
/// Every crop is at least <see cref="MinWidth"/> x <see cref="MinHeight"/> (padded evenly on every side that has room) and stays inside the window (a floating palette outside it: inside the union of both).</summary>
public static class SmartCrop
{
    /// <summary>The surroundings added around what must be shown, logical px.</summary>
    public const int Margin = 24;
    /// <summary>The smallest a crop is, logical px: a small crop is padded with what is around it, not left a tiny picture.</summary>
    public const int MinWidth = 600, MinHeight = 360;
    /// <summary>How many tab headers on each side of the active tab are shown.</summary>
    public const int NeighbourTabs = 2;

    public static CropDecision Decide(PictureKind kind, CropRects r)
    {
        var window = r.Window;
        switch (kind)
        {
            case PictureKind.Ribbon:
            {
                var tabs = TabsToShow(r.TabHeaders, r.ActiveTab);
                if (Valid(r.Item) && Valid(r.Panel) && tabs != null)
                {
                    var need = Union(Union(r.Item!.Value, r.Panel!.Value), tabs.Value);
                    return new CropDecision(SnapEdges(Fit(need, window), r.Snap, window), "ribbon");
                }
                if (Valid(r.Ribbon)) return new CropDecision(Fit(r.Ribbon!.Value, window), "ribbon-whole");
                return new CropDecision(window, "window");
            }
            case PictureKind.Palette:
            {
                if (!Valid(r.Palette)) return new CropDecision(window, "window");
                var pal = r.Palette!.Value;
                // inside the main window: padded inside it; a floating palette outside it: inside its own monitor's work area (else the union of both)
                bool inside = pal.Left >= window.Left && pal.Top >= window.Top && pal.Right <= window.Right && pal.Bottom <= window.Bottom;
                return new CropDecision(Fit(pal, inside ? window : Valid(r.PaletteMonitor) ? r.PaletteMonitor!.Value : window), "palette");
            }
            default:
                return new CropDecision(window, "window");
        }
    }

    static bool Valid(PxRect? r) => r is { Width: > 0, Height: > 0 };

    /// <summary>The union of the active tab's header and the <see cref="NeighbourTabs"/> headers on each side of it, in left-to-right order; null when the active tab's header is not among the headers
    /// (the row cannot be trusted).</summary>
    public static PxRect? TabsToShow(IReadOnlyList<PxRect>? headers, PxRect? active)
    {
        if (headers == null || !Valid(active)) return null;
        var row = headers.Where(h => h.Width > 0 && h.Height > 0).OrderBy(h => h.Left).ToList();
        int at = row.IndexOf(active!.Value);
        if (at < 0) return null;
        var from = Math.Max(0, at - NeighbourTabs);
        var to = Math.Min(row.Count - 1, at + NeighbourTabs);
        var u = row[from];
        for (int i = from + 1; i <= to; i++) u = Union(u, row[i]);
        return u;
    }

    public static PxRect Union(PxRect a, PxRect b) => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    /// <summary>The crop for <paramref name="need"/>: it plus <see cref="Margin"/> on every side, padded up to the minimum size, inside the window (or the union of the window and the needed rectangle).</summary>
    public static PxRect Fit(PxRect need, PxRect window)
    {
        var bounds = Union(window, need);
        int l = need.Left - Margin, t = need.Top - Margin, rr = need.Right + Margin, b = need.Bottom + Margin;
        int w = Math.Min(Math.Max(rr - l, MinWidth), bounds.Width), h = Math.Min(Math.Max(b - t, MinHeight), bounds.Height);
        // padded evenly around what is shown, then slid back inside the bounds (the side without room gives its share to the other)
        int cx = (l + rr) / 2, cy = (t + b) / 2;
        int left = Slide(cx - w / 2, w, bounds.Left, bounds.Right), top = Slide(cy - h / 2, h, bounds.Top, bounds.Bottom);
        return new PxRect(left, top, left + w, top + h);
    }

    /// <summary>How far (px) a crop edge may be moved outward to take a whole tab header or panel in.</summary>
    public const int SnapMax = 250;

    /// <summary>A crop's left or right edge that cuts through a known ribbon rectangle (a neighbouring tab's name, a panel's label) moves outward to take that rectangle whole, when that is at most
    /// <see cref="SnapMax"/> px and stays inside <paramref name="bounds"/>; only rectangles that overlap the crop vertically count.</summary>
    public static PxRect SnapEdges(PxRect crop, IReadOnlyList<PxRect>? snap, PxRect bounds)
    {
        if (snap == null) return crop;
        int left = crop.Left, right = crop.Right;
        for (int pass = 0; pass < 4; pass++)
        {
            bool moved = false;
            foreach (var s in snap)
            {
                if (s.Width <= 0 || s.Height <= 0 || s.Bottom <= crop.Top || s.Top >= crop.Bottom) continue;
                if (s.Left < left && s.Right > left && left - s.Left <= SnapMax && s.Left >= bounds.Left) { left = s.Left; moved = true; }
                if (s.Left < right && s.Right > right && s.Right - right <= SnapMax && s.Right <= bounds.Right) { right = s.Right; moved = true; }
            }
            if (!moved) break;
        }
        return new PxRect(left, crop.Top, right, crop.Bottom);
    }

    static int Slide(int start, int size, int min, int max) => Math.Max(min, Math.Min(start, max - size));
}
