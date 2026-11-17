// NO AutoCAD references in this file (linked into the engine test project).
namespace Retrace.Core;

/// <summary>One palette page as the recorder sees it at an instant: the window's handle, the palette's name and its set's title, whether it is open where it belongs (<see cref="Shown"/>: visible and not rolled up or
/// slid away) and the rectangle the picture of it would cover (null when none can be read).</summary>
public sealed record PaletteState(long Id, string Name, string Set, bool Shown, PxRect? Rect);

/// <summary>The palette that opened during a command: its name and the screen rectangle to box in the end picture.</summary>
public sealed record PaletteOpened(string Name, PxRect Rect);

/// <summary>What the end picture of a command shows about palettes: <see cref="Opened"/> when one became visible (box and caption), else null with <see cref="Reason"/> for retrace.log. Never a guessed rectangle.</summary>
public sealed record PaletteOpenDecision(PaletteOpened? Opened, string Reason);

public static class PaletteOpening
{
    /// <summary>Decides from the palettes at the command's start (<paramref name="before"/>) and now, when the end picture is taken (<paramref name="after"/>), and AutoCAD's window rectangle
    /// (<paramref name="main"/>, null when unknown). A palette counts as opened when it is shown now and was not shown at the start (it was closed, or not there at all). Several: the one that lies
    /// leftmost, then topmost, then first by name is boxed and named, the others only appear in the reason. A palette whose rectangle is unreadable, empty or not inside AutoCAD's window is never boxed.</summary>
    public static PaletteOpenDecision Decide(IReadOnlyList<PaletteState> before, IReadOnlyList<PaletteState> after, PxRect? main)
    {
        var wasShown = new HashSet<long>(before.Where(p => p.Shown).Select(p => p.Id));
        var fresh = after.Where(p => p.Shown && !wasShown.Contains(p.Id)).ToList();
        if (fresh.Count == 0)
        {
            var already = after.Where(p => p.Shown).Select(p => p.Name).ToList();
            var hidden = after.Where(p => !p.Shown).Select(p => p.Name).ToList();
            if (already.Count == 0 && hidden.Count == 0) return new(null, "no palette is open now");
            return new(null, "no palette became visible during the command" + (already.Count > 0 ? $"; already open: {Join(already)}" : "") + (hidden.Count > 0 ? $"; closed, collapsed or auto-hidden: {Join(hidden)}" : ""));
        }
        var usable = new List<PaletteOpened>();
        var skipped = new List<string>();
        foreach (var p in fresh)
        {
            if (p.Rect is not { Width: > 0, Height: > 0 } r) { skipped.Add($"{p.Name} (window rectangle unreadable)"); continue; }
            if (main is not { } m || !MarkGuard.RectInside(m, r)) { skipped.Add($"{p.Name} (outside AutoCAD's window)"); continue; }
            usable.Add(new PaletteOpened(p.Name, r));
        }
        if (usable.Count == 0) return new(null, "a palette became visible but is not marked: " + string.Join("; ", skipped));
        var first = usable.OrderBy(u => u.Rect.Left).ThenBy(u => u.Rect.Top).ThenBy(u => u.Name, StringComparer.Ordinal).First();
        var rest = usable.Where(u => !ReferenceEquals(u, first)).Select(u => u.Name).Concat(skipped).ToList();
        return new(first, $"palette {first.Name} opened" + (rest.Count > 0 ? $"; also visible, not marked: {Join(rest)}" : ""));
    }

    static string Join(IEnumerable<string> names) => string.Join(", ", names.Select(n => n.Length == 0 ? "(untitled)" : n));
}
