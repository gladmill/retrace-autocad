// NO AutoCAD references in this file (linked into the engine test project).
namespace Retrace.Core;

/// <summary>How a watch for a palette that shows up after the command ended finished. <see cref="Retake"/>: the palette appeared in time, the end picture is taken again with the box. <see cref="ClickBox"/>: the person's
/// first input was a click inside the palette that had just appeared: the box goes on that click's picture. <see cref="ActedFirst"/>: the person's input (or a dialog, another command, the end of the recording) came first and no
/// palette had appeared. <see cref="TimedOut"/>: nothing appeared within <see cref="LatePaletteWatch.LimitMs"/>. <see cref="Hover"/>: the palette that appeared is an auto-hide set that slid out under the mouse at an edge of the
/// window (the person's doing, not the command's): no retake, no box.</summary>
public enum LateKind { Retake, ClickBox, ActedFirst, TimedOut, Hover }

/// <param name="Opened">the palette (Retake, ClickBox)</param>
/// <param name="ElapsedMs">ms from the command's end to this outcome</param>
/// <param name="Line">the retrace.log line for it</param>
public sealed record LateOutcome(LateKind Kind, PaletteOpened? Opened, long ElapsedMs, string Line);

/// <summary>
/// After the end picture of a ribbon / Quick Access Toolbar click's command found no opened palette, a palette that an add-in shows (or expands from a zero-size or hidden state) a moment later is caught here: the recorder reads the palettes
/// every <see cref="PollMs"/> and this decides, by the same per-set rules as <see cref="PaletteOpening.Decide"/> against the palettes at the click, which of these happens first: a palette appears (seen twice, <see cref="SettleMs"/> apart, at the same
/// place, so it has been painted), the person's next input arrives, another command starts, or <see cref="LimitMs"/> passes. A palette that slides out because the mouse is on its edge tab (an anchored set) is the person's doing, not the command's
/// (<see cref="LateKind.Hover"/>). Pure data; UI thread only. A palette that was already shown at the click is never "opened".
/// </summary>
public sealed class LatePaletteWatch
{
    /// <summary>How long after the command's end a palette may still appear and be caught. Measured on a report: an add-in's palette set was made visible between the command's end and the person's first click in it, 2.3 s later;
    /// 3 s covers that with some room and is the most a command that opens no palette at all makes the recorder look (one palette read each <see cref="PollMs"/>, about twenty in all). A constant, not a setting.</summary>
    public const long LimitMs = 3000;

    /// <summary>How often the palettes are read while watching.</summary>
    public const long PollMs = 150;

    /// <summary>A palette must be seen at the same place by two reads this far apart before the picture is retaken (its window has been painted by then).</summary>
    public const long SettleMs = 100;

    IReadOnlyList<PaletteState> _before = [];
    readonly List<PxRect> _bars = new();   // the edge bars of anchored sets seen before the click and during the watch
    bool _hover;                            // the cursor entered an edge bar during the watch
    long _endedAt;
    long _lastPoll;
    string? _seenName;
    PxRect _seenRect;
    long _seenAt;

    public bool Active { get; private set; }
    /// <summary>The event number of the end picture being watched.</summary>
    public int N { get; private set; }

    /// <summary>The command whose end picture <paramref name="n"/> showed no opened palette ended at <paramref name="endedMs"/>; <paramref name="before"/> is what was open at the click.</summary>
    public void Begin(int n, IReadOnlyList<PaletteState> before, long endedMs)
    {
        N = n; _before = before; _endedAt = endedMs; _lastPoll = endedMs; _seenName = null; _hover = false; Active = true;
        _bars.Clear(); AddBars(before);
    }

    public void Clear() { Active = false; _seenName = null; _before = []; _bars.Clear(); _hover = false; }

    void AddBars(IReadOnlyList<PaletteState> list)
    {
        foreach (var p in list) if (p.IsBar && p.Rect is { Width: > 0, Height: > 0 } r && !_bars.Contains(r)) _bars.Add(r);
    }

    /// <summary>Is it time to read the palettes again?</summary>
    public bool Due(long nowMs) => Active && nowMs - _lastPoll >= PollMs;

    /// <summary>The mouse moved to the screen point (<paramref name="x"/>, <paramref name="y"/>): remembers that it was over the edge bar of an anchored set (its tab slides the palette out).</summary>
    public void NoteCursor(int x, int y) { if (Active && !_hover && _bars.Any(b => b.Contains(x, y))) _hover = true; }

    /// <summary>The palettes were read at <paramref name="nowMs"/> (<paramref name="after"/>); <paramref name="cursor"/> is where the mouse is; null while the watch goes on.</summary>
    public LateOutcome? Poll(IReadOnlyList<PaletteState> after, PxRect? main, long nowMs, (int X, int Y)? cursor = null)
    {
        if (!Active) return null;
        _lastPoll = nowMs;
        long ms = nowMs - _endedAt;
        AddBars(after);
        if (cursor is { } c0) NoteCursor(c0.X, c0.Y);
        var d = PaletteOpening.Decide(_before, after, main);
        if (d.Opened is { } o)
        {
            if (IsHover(o, cursor)) return Finish(new LateOutcome(LateKind.Hover, null, ms, HoverLine(o, ms)));
            if (_seenName == o.Name && _seenRect == o.Rect && nowMs - _seenAt >= SettleMs) return Finish(new LateOutcome(LateKind.Retake, o, ms, Appeared(o, ms) + "picture retaken with the box"));
            if (_seenName != o.Name || _seenRect != o.Rect) { _seenName = o.Name; _seenRect = o.Rect; _seenAt = nowMs; }
        }
        else _seenName = null;
        if (ms >= LimitMs) return Finish(new LateOutcome(LateKind.TimedOut, null, ms, $"end picture {N}: nothing appeared within {ms} ms after the command ended"));
        return null;
    }

    /// <summary>The person's input arrived at <paramref name="nowMs"/> (a mouse button-down at <paramref name="x"/>, <paramref name="y"/>, or anything else: null, null); <paramref name="after"/> is read just now. The screen is no longer the state right after
    /// the command, so there is no retake: a click inside a palette that appeared gets the box on its own picture, anything else ends the watch.</summary>
    public LateOutcome Input(IReadOnlyList<PaletteState> after, PxRect? main, long nowMs, int? x, int? y)
    {
        long ms = nowMs - _endedAt;
        AddBars(after);
        var d = PaletteOpening.Decide(_before, after, main);
        (int X, int Y)? cursor = x is { } cx && y is { } cy ? (cx, cy) : null;
        if (d.Opened is { } o && IsHover(o, cursor, insideRule: false)) return Finish(new LateOutcome(LateKind.Hover, null, ms, HoverLine(o, ms)));
        if (d.Opened is { } o2 && x is { } px && y is { } py && o2.Rect.Contains(px, py))
            return Finish(new LateOutcome(LateKind.ClickBox, o2, ms, Appeared(o2, ms) + "the person acted first: box drawn on the next click's picture"));
        var what = x != null ? "a click" : "an input";
        return Finish(new LateOutcome(LateKind.ActedFirst, null, ms, d.Opened is { } late
            ? $"end picture {N}: palette {late.Name} appeared {ms} ms after the command ended, but the person acted first ({what} outside it): no retake, no box"
            : $"end picture {N}: the person acted first ({what}, {ms} ms after the command ended): no palette had appeared"));
    }

    /// <summary>Something else ended the watch (another command started, a dialog opened, the recording stopped): <paramref name="why"/> for the log.</summary>
    public LateOutcome Interrupted(string why, long nowMs)
    {
        long ms = nowMs - _endedAt;
        return Finish(new LateOutcome(LateKind.ActedFirst, null, ms, $"end picture {N}: palette watch stopped {ms} ms after the command ended: {why}"));
    }

    /// <summary>A palette that slid out under the mouse: the cursor is on an edge bar, or inside the new palette while its set was anchored (or had a zero-size root, the state of a retracted anchored set) at the click, or the cursor was
    /// on an edge bar at some point during the watch. A palette an add-in shows by itself leaves the mouse where the click was (the toolbar), outside of it.</summary>
    bool IsHover(PaletteOpened o, (int X, int Y)? cursor, bool insideRule = true)
    {
        if (_hover) return true;
        if (cursor is not { } c) return false;
        if (_bars.Any(b => b.Contains(c.X, c.Y))) return true;
        return insideRule && o.Rect.Contains(c.X, c.Y) && WasAnchored(o);   // (a person's click inside the palette is the click-box case, not a hover: the click itself is inside by definition)
    }

    bool WasAnchored(PaletteOpened o) =>
        _before.Any(p => p.IsBar) ||
        _before.Any(p => !p.IsBar && (p.Name == o.Name || p.Set == o.Name) && p.Diag.Contains("60 px or less", StringComparison.Ordinal));

    string HoverLine(PaletteOpened o, long ms) => $"end picture {N}: palette {(o.Name.Length == 0 ? "(untitled)" : o.Name)} showed {ms} ms after the command ended with the mouse on the edge of the window (an auto-hide set slid out under it): no retake, no box";

    string Appeared(PaletteOpened o, long ms) => $"end picture {N}: palette {(o.Name.Length == 0 ? "(untitled)" : o.Name)} appeared {ms} ms after the command ended: ";

    LateOutcome Finish(LateOutcome o) { Clear(); return o; }
}
