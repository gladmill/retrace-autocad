namespace Retrace.Core;

/// <summary>
/// Where the tabs of a palette set are, for bringing a palette that is not the front tab to the front (setting showDrivenUi). AutoCAD draws the tabs itself on one
/// edge of the set (no window per tab, nothing in UI Automation): a strip beside the pages, one tab per palette in the order the palettes were added, from the
/// top. Only what is reliable is used: the set's own rectangle and the rectangle of the page that is in front (a page that is not has no usable rectangle:
/// a palette never shown has none at all). Pure geometry; the clicking is <c>AutoCad/PaletteReveal</c>.
/// </summary>
public static class PaletteTabs
{
    /// <summary>The width (px at 100% DPI) a tab strip is taken to have: a side of the front page narrower or wider than this is not one.</summary>
    public const int MinStrip = 12, MaxStrip = 48;

    /// <summary>The x to click, measured from the left of the set window: the middle of the strip beside the front page. A side is the tab strip only when its width is
    /// plausible for one (<see cref="MinStrip"/> to <see cref="MaxStrip"/> px, scaled by <paramref name="dpi"/>); null when neither side is, or both are (the
    /// side cannot be told: no click is made).</summary>
    public static int? StripX(int setWidth, int frontLeft, int frontRight, int dpi = 96)
    {
        double scale = Math.Max(dpi, 96) / 96.0;
        bool Plausible(int w) => w >= MinStrip * scale && w <= MaxStrip * scale;
        int left = frontLeft, right = setWidth - frontRight;
        bool l = Plausible(left), r = Plausible(right);
        if (l == r) return null;
        return r ? (frontRight + setWidth) / 2 : frontLeft / 2;
    }

    /// <summary>The position of a page among the palettes of its set (0 = added first), from the dialog-control ids of the set's pages (AutoCAD numbers them in the order they were added); -1 when the ids do not tell (a page missing, equal ids, fewer than two pages).</summary>
    public static int IndexOf(int pageId, IReadOnlyCollection<int> pageIds)
    {
        var sorted = pageIds.OrderBy(i => i).ToList();
        if (sorted.Count < 2 || sorted.Distinct().Count() != sorted.Count) return -1;
        return sorted.IndexOf(pageId);
    }
}

/// <summary>
/// A guided search for the tab of one palette: the pages lie along the strip in order, so after every click, which page came to the front says where the
/// click landed, and the next click is aimed from that. At most one click per page of the set. Positions are offsets from the top of the strip (the set can
/// move between clicks); the caller adds the strip's top again. Nothing here touches a window.
/// </summary>
public sealed class TabSearch
{
    readonly int _count;
    int _target;
    readonly List<int> _tried = new();
    readonly Dictionary<int, int> _at = new();   // page index -> the offset that showed it (a click that changed the front page)
    int _shown;

    /// <param name="count">pages in the set</param>
    /// <param name="target">the page to bring to the front (0-based, in the order added)</param>
    /// <param name="front">the page in front now</param>
    public TabSearch(int count, int target, int front) { _count = count; _target = target; _shown = front; Start = front; }

    /// <summary>The page that was in front when the search began.</summary>
    public int Start { get; }

    /// <summary>Clicks made so far.</summary>
    public int Clicks => _tried.Count;

    /// <summary>The offset of a click that is known to show <paramref name="page"/> (null: never seen).</summary>
    public int? PointOf(int page) => _at.TryGetValue(page, out var y) ? y : null;

    /// <summary>The offset to click next for a strip <paramref name="height"/> px tall, or null when the clicks are used up (one per page).</summary>
    public int? Next(int height)
    {
        if (_tried.Count >= _count || height < 20) return null;
        double pitch = (double)height / _count;
        if (_at.TryGetValue(_target, out var known) && _tried.All(t => Math.Abs(t - known) >= 6)) return Math.Clamp(known, 4, height - 4);   // a click already showed it
        double est = _at.Count switch
        {
            0 => (_target + 0.5) * pitch,
            1 => _at.First().Value + (_target - _at.First().Key) * pitch,   // the direction is a guess until a second tab has been seen
            _ => Interpolate(),
        };
        est = Math.Clamp(est, 4, height - 4);
        // never the same spot twice (a miss shows nothing new): nudge by a third of a tab, up and down
        for (int k = 0; k < 2 * _count + 1; k++)
        {
            int off = (k + 1) / 2 * (k % 2 == 1 ? 1 : -1);
            int y = (int)Math.Clamp(est + off * pitch / 3, 4, height - 4);
            if (_tried.All(t => Math.Abs(t - y) >= 6)) return y;
        }
        return null;
    }

    /// <summary>Starts again for another page (the one that was in front, to put it back): the clicks are counted afresh (one per page again) and what was learned about the tabs is kept.</summary>
    public void Retarget(int page) { _target = page; _tried.Clear(); }

    /// <summary>What the click at <paramref name="offset"/> (the one <see cref="Next"/> gave) did: <paramref name="shown"/> is the page in front now, -1 when none could be told.</summary>
    public void Observe(int offset, int shown)
    {
        _tried.Add(offset);
        if (shown >= 0 && shown != _shown) _at[shown] = offset;   // a change tells where that tab is; no change tells nothing
        if (shown >= 0) _shown = shown;
    }

    double Interpolate()
    {
        // the two seen tabs nearest the target: a line through them gives the pitch and the direction
        var two = _at.OrderBy(p => Math.Abs(p.Key - _target)).Take(2).ToList();
        var (a, b) = (two[0], two[1]);
        double slope = (double)(b.Value - a.Value) / (b.Key - a.Key);
        return a.Value + (_target - a.Key) * slope;
    }
}
