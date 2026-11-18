// NO AutoCAD references in this file (linked into the engine test project).
namespace Retrace.Core;

/// <summary>One palette page as the recorder sees it at an instant: the window's handle, the palette's name and its set's title, whether it is open where it belongs (<see cref="Shown"/>: visible and not rolled up or
/// slid away) and the rectangle the picture of it would cover (null when none can be read).</summary>
/// <param name="Strip">a floating palette set that is rolled up (Auto-hide on, the mouse away): its frame is on screen as a title strip, its pages are moved off; this is the strip's rectangle (null when the set is not in that state)</param>
/// <param name="Diag">for a page that is not shown: its visibility and rectangle, its root window's class, visibility and rectangle and why the geometric test failed (one line for retrace.log when no box is drawn)</param>
/// <param name="IsBar">not a palette: a bar at the edge of AutoCAD's window that holds the title strips of docked palette sets in Auto-hide state (<see cref="Rect"/> is its rectangle; one entry per such bar)</param>
public sealed record PaletteState(long Id, string Name, string Set, bool Shown, PxRect? Rect, PxRect? Strip = null, bool IsBar = false, string Diag = "");

/// <summary>The palette that opened during a command: its name and the screen rectangle to box in the end picture. <paramref name="State"/>: "" = open (expanded), "rolled" = Auto-hide, a floating set rolled up to its title strip (the box is the strip),
/// "anchored" = Auto-hide, a docked set anchored to the edge of AutoCAD's window (the box is the edge bar that holds its tab); the caption says so.</summary>
public sealed record PaletteOpened(string Name, PxRect Rect, string State = "");

/// <summary>What the end picture of a command shows about palettes: <see cref="Opened"/> when one became visible (box and caption), else null with <see cref="Reason"/> for retrace.log. Never a guessed rectangle.</summary>
public sealed record PaletteOpenDecision(PaletteOpened? Opened, string Reason);

public static class PaletteOpening
{
    /// <summary>Decides from the palettes at the command's start (<paramref name="before"/>) and now, when the end picture is taken (<paramref name="after"/>), and AutoCAD's window rectangle
    /// (<paramref name="main"/>, null when unknown). A palette counts as opened when it is shown now and was not shown at the start (it was closed, or not there at all). Several: the one that lies
    /// leftmost, then topmost, then first by name is boxed and named, the others only appear in the reason. A palette whose rectangle is unreadable, empty or not inside AutoCAD's window is never boxed.</summary>
    public static PaletteOpenDecision Decide(IReadOnlyList<PaletteState> before, IReadOnlyList<PaletteState> after, PxRect? main)
    {
        var pages = after.Where(p => !p.IsBar).ToList();
        // there before: on screen as an open palette or a rolled-up strip. A palette that was rolled up before and now shows expanded (the mouse is on it) was already there: it is not "opened".
        var there = new HashSet<long>(before.Where(p => !p.IsBar && (p.Shown || p.Strip != null)).Select(p => p.Id));
        var thereSets = new HashSet<string>(before.Where(p => !p.IsBar && p.Strip != null).Select(p => p.Set), StringComparer.Ordinal);
        var fresh = pages.Where(p => p.Shown && !there.Contains(p.Id) && !(thereSets.Contains(p.Set) && p.Set.Length > 0)).ToList();
        var usable = new List<PaletteOpened>();
        var skipped = new List<string>();
        foreach (var p in fresh)
        {
            if (p.Rect is not { Width: > 0, Height: > 0 } r) { skipped.Add($"{p.Name} (window rectangle unreadable)"); continue; }
            if (main is not { } m || !MarkGuard.RectInside(m, r)) { skipped.Add($"{p.Name} (outside AutoCAD's window)"); continue; }
            usable.Add(new PaletteOpened(p.Name, r));
        }
        // Auto-hide, floating: the set is rolled up to its title strip (its pages are moved off the screen, so no page is "shown"); the strip is what is on screen. One entry per set, named by the set's title.
        // "there" is judged per set: a set with any page shown or rolled up before the command was already there (a set that was expanded at the click and rolled up at the end is not opened; a set that existed hidden is a candidate)
        var thereKeys = new HashSet<string>(before.Where(p => !p.IsBar && (p.Shown || p.Strip != null)).Select(Key), StringComparer.Ordinal);
        foreach (var grp in pages.Where(p => !p.Shown && p.Strip != null && !thereKeys.Contains(Key(p))).GroupBy(Key, StringComparer.Ordinal))
        {
            var strip = grp.First().Strip!.Value;
            var name = grp.Key;
            if (strip.Width <= 0 || strip.Height <= 0) { skipped.Add($"{name} (rolled up, strip rectangle empty)"); continue; }
            if (main is not { } m2 || !MarkGuard.RectInside(m2, strip)) { skipped.Add($"{name} (rolled up, strip outside AutoCAD's window)"); continue; }
            usable.Add(new PaletteOpened(name, strip, "rolled"));
        }
        // Auto-hide, docked: the set is anchored to an edge bar; its tab is drawn inside that bar (not a window of its own). A bar that was not there before and one palette set that was not shown before name the opened one; anything less certain is no box.
        string? anchoredWhy = null;
        var barsBefore = new HashSet<long>(before.Where(p => p.IsBar).Select(p => p.Id));
        var newBars = after.Where(p => p.IsBar && !barsBefore.Contains(p.Id)).ToList();
        if (newBars.Count > 0 && usable.Count == 0)
        {
            // a candidate: no page of the set was on screen before, and a page is new or changed (its geometry line differs: AutoCAD's own sets that stayed closed look the same before and after)
            var beforeDiag = before.Where(p => !p.IsBar).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Diag);
            var cand = pages.Where(p => !p.Shown && p.Strip == null && !thereKeys.Contains(Key(p)) && (!beforeDiag.TryGetValue(p.Id, out var was) || was != p.Diag)).Select(Key).Distinct(StringComparer.Ordinal).ToList();
            if (newBars.Count == 1 && cand.Count == 1 && newBars[0].Rect is { Width: > 0, Height: > 0 } br && main is { } m3 && MarkGuard.RectInside(m3, br)) usable.Add(new PaletteOpened(cand[0], br, "anchored"));
            else anchoredWhy = $"a bar of docked auto-hidden palettes appeared at the edge of AutoCAD's window, but {(cand.Count == 1 ? "its rectangle is unreadable or outside the window" : cand.Count == 0 ? "no palette that was not there before can be told apart" : $"{cand.Count} palette sets that were not there before could be it (" + Join(cand) + ")")}";
        }
        if (usable.Count == 0)
        {
            if (fresh.Count == 0 && anchoredWhy == null && skipped.Count == 0)
            {
                var already = pages.Where(p => p.Shown || p.Strip != null).Select(p => p.Name).ToList();
                var hidden = pages.Where(p => !p.Shown && p.Strip == null).Select(p => p.Name).ToList();
                if (already.Count == 0 && hidden.Count == 0) return new(null, "no palette is open now");
                return new(null, "no palette became visible during the command" + (already.Count > 0 ? $"; already open: {Join(already)}" : "") + (hidden.Count > 0 ? $"; closed, collapsed or auto-hidden: {Join(hidden)}" : ""));
            }
            return new(null, (anchoredWhy ?? "a palette became visible") + (anchoredWhy == null || skipped.Count > 0 ? (anchoredWhy == null ? " but is not marked: " : "; ") + string.Join("; ", skipped) : ""));
        }
        var first = usable.OrderBy(u => u.Rect.Left).ThenBy(u => u.Rect.Top).ThenBy(u => u.Name, StringComparer.Ordinal).First();
        var rest = usable.Where(u => !ReferenceEquals(u, first)).Select(u => u.Name).Concat(skipped).ToList();
        var how = first.State switch { "rolled" => $" (rolled up: strip {first.Rect.Left},{first.Rect.Top} {first.Rect.Width}x{first.Rect.Height} boxed)", "anchored" => $" (anchored, Auto-hide: edge bar {first.Rect.Left},{first.Rect.Top} {first.Rect.Width}x{first.Rect.Height} boxed)", _ => $" (expanded: {first.Rect.Left},{first.Rect.Top} {first.Rect.Width}x{first.Rect.Height} boxed)" };
        return new(first, $"palette {first.Name} opened{how}" + (rest.Count > 0 ? $"; also visible, not marked: {Join(rest)}" : ""));
    }

    static string Key(PaletteState p) => p.Set.Length > 0 ? p.Set : p.Name;

    static string Join(IEnumerable<string> names) => string.Join(", ", names.Select(n => n.Length == 0 ? "(untitled)" : n));
}
