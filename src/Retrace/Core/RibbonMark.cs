namespace Retrace.Core;

// NO AutoCAD, WPF or System.Drawing references in this file (linked into the engine test project).

public enum RibbonMarkKind { None, Box, Point }

/// <summary>What the picture of a ribbon click gets: a green box on the button and an arrow ending on it (<see cref="RibbonMarkKind.Box"/>), an arrow at the mouse-down point alone (<see cref="RibbonMarkKind.Point"/>), or
/// nothing (<see cref="RibbonMarkKind.None"/>); <see cref="Reason"/> says why, for retrace.log.</summary>
public readonly record struct RibbonMarkChoice(RibbonMarkKind Kind, PxRect Box, int X, int Y, string Reason);

/// <summary>The one decision for the marks of a ribbon click's picture. The button's rectangle is read at the click itself (the item the click ran, wherever it shows: the ribbon, a drop-down, a slide-out), the mouse-down point
/// is the click as the person made it. A box is only used when it holds the mouse-down point (a stale or duplicate rectangle elsewhere on the screen is not the button that was clicked) and lies in the picture (a rectangle the picture
/// does not show is unusable, so the next candidate is tried); when the rectangle cannot be found the arrow goes to the mouse-down point; with neither, nothing is drawn: never a guessed spot.</summary>
public static class RibbonMark
{
    /// <summary>How long (ms) after a mouse-down in a ribbon window a click still counts as made with that press (a button runs on the release; a keytip activation has no press of its own, and the last press may have been in the drawing).</summary>
    public const int DownFreshMs = 1000;
    /// <summary>How far (px) outside a button's rectangle its mouse-down point may lie: the frame's edge and rounding between coordinate spaces.</summary>
    public const int Reach = 4;

    /// <param name="item">the clicked item's rectangle, where it shows now (null: not found)</param>
    /// <param name="parent">asked only when the item's own rectangle is not used: the rectangle of the button that holds the item (a split or drop-down button), null when none shows</param>
    /// <param name="down">the screen point of the mouse-down that made the click, null when there was none just before it</param>
    /// <param name="picture">the screen rectangle the picture shows, when known: a rectangle or point outside it cannot be marked</param>
    public static RibbonMarkChoice Choose(PxRect? item, Func<PxRect?>? parent, (int X, int Y)? down, PxRect? picture = null)
    {
        string why = item == null ? "the button's rectangle was not found (not shown in the ribbon or any open menu)" : "";
        if (item is { } r)
        {
            if (!Usable(r)) why = "the button's rectangle is empty";
            else if (picture is { } pic && MarkGuard.Visible(pic, r) is null) why = "the button's rectangle is not inside the picture";
            else if (down is { } d && !Holds(r, d)) why = "the button's rectangle does not hold the mouse-down point";
            else return new(RibbonMarkKind.Box, r, 0, 0, "the button's rectangle");
        }
        // the button that holds the item (a split or drop-down button, whose menu has closed by the time the click is reported) is shown in the picture; it is the item's own container, found from the item, so
        // it needs no mouse-down point to hold (the click was on the menu item below it)
        if (parent?.Invoke() is { } p)
        {
            if (!Usable(p)) why += "; the container's rectangle is empty";
            else if (picture is { } pic && MarkGuard.Visible(pic, p) is null) why += "; the container's rectangle is not inside the picture";
            else return new(RibbonMarkKind.Box, p, 0, 0, "the rectangle of the button that holds the item");
        }
        if (down is { } pt && (picture is not { } pc || pc.Contains(pt.X, pt.Y))) return new(RibbonMarkKind.Point, default, pt.X, pt.Y, why + "; the arrow is at the mouse-down point");
        if (down != null) why += "; the mouse-down point is outside the picture";
        return new(RibbonMarkKind.None, default, 0, 0, (why.Length == 0 ? "no rectangle" : why) + ", and no usable mouse-down point (keyboard, code, or the last press was not in the ribbon); no mark");
    }

    static bool Usable(PxRect r) => r.Width > 0 && r.Height > 0;
    static bool Holds(PxRect r, (int X, int Y) d) => d.X >= r.Left - Reach && d.X < r.Right + Reach && d.Y >= r.Top - Reach && d.Y < r.Bottom + Reach;

    /// <summary>The mouse-down point (<paramref name="x"/>, <paramref name="y"/>) of the press whose message time was <paramref name="downMsgTime"/>, as a point for a click reported at <paramref name="nowMs"/> (both the system's
    /// millisecond clock, which wraps); null when there was no press (0), it is too old, or it was not in a ribbon window (<paramref name="inRibbonWindow"/>: a click in the drawing, then a button run by its keytip, is no press on that button).</summary>
    public static (int X, int Y)? FreshDown(int x, int y, uint downMsgTime, uint nowMs, bool inRibbonWindow) =>
        inRibbonWindow && downMsgTime != 0 && unchecked(nowMs - downMsgTime) <= (uint)DownFreshMs ? (x, y) : null;

    /// <summary>A ribbon item that may stand for the click as the button that holds a clicked item: a button, split button or drop-down, not the row or panel layout item a plain button sits in (its rectangle would be the whole row).</summary>
    public static bool IsButtonLike(string? typeName) =>
        !string.IsNullOrEmpty(typeName) && !typeName.Contains("RowPanel", StringComparison.Ordinal) && !typeName.Contains("Break", StringComparison.Ordinal)
        && !typeName.Contains("Separator", StringComparison.Ordinal) && !typeName.Contains("FoldPanel", StringComparison.Ordinal) && !typeName.Contains("PanelSource", StringComparison.Ordinal);
}
