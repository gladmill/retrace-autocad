namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>When a green frame or arrow may be drawn on a picture. A mark that is not provably in the picture and on what was clicked is left out:
/// a missing mark is better than one that points at the wrong place.</summary>
public static class MarkGuard
{
    /// <summary>How far (px) a rectangle may stick out of the picture, or a click point lie outside its rectangle, and still count as inside: rounding between coordinate spaces and the frame's own edge.</summary>
    public const int Slack = 2;

    /// <summary>The rectangle lies inside the picture's rectangle (both in the same space, Right and Bottom exclusive) and has a size.</summary>
    public static bool RectInside(PxRect picture, PxRect rect, int slack = Slack) =>
        rect.Width > 0 && rect.Height > 0 && picture.Width > 0 && picture.Height > 0 &&
        rect.Left >= picture.Left - slack && rect.Top >= picture.Top - slack && rect.Right <= picture.Right + slack && rect.Bottom <= picture.Bottom + slack;

    /// <summary>The part of <paramref name="rect"/> that lies in the picture (Right and Bottom exclusive), or null when nothing does.</summary>
    public static PxRect? Clip(PxRect picture, PxRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || picture.Width <= 0 || picture.Height <= 0) return null;
        var c = new PxRect(Math.Max(rect.Left, picture.Left), Math.Max(rect.Top, picture.Top), Math.Min(rect.Right, picture.Right), Math.Min(rect.Bottom, picture.Bottom));
        return c.Width > 0 && c.Height > 0 ? c : null;
    }

    /// <summary>Smallest visible part (px) that counts as "sizable" whatever share of the rectangle it is.</summary>
    public const int MinVisibleWidth = 24, MinVisibleHeight = 8;

    /// <summary>The part of the rectangle to draw: the rectangle clipped to the picture, when that part is sizable (at least a quarter of the rectangle's area, or at least
    /// <see cref="MinVisibleWidth"/> x <see cref="MinVisibleHeight"/>: a long label, a list row or a floating palette that sticks out of the picture keeps its frame). A rectangle that lies
    /// (almost) entirely outside gives null.</summary>
    public static PxRect? Visible(PxRect picture, PxRect rect)
    {
        if (Clip(picture, rect) is not { } c) return null;
        long area = (long)rect.Width * rect.Height, part = (long)c.Width * c.Height;
        return part * 4 >= area || (c.Width >= MinVisibleWidth && c.Height >= MinVisibleHeight) ? c : null;
    }

    /// <summary>How far (px) left or right of a row's rectangle a click on the same row still counts as on it: a tree item's rectangle is its label, and its icon, state box and indent lie to the left of it
    /// (a click on them selects the item); a report-view list item's click may be in a column beside the label. The row's own vertical range stays strict.</summary>
    public const int RowReach = 48;

    /// <summary>Frame and arrow are allowed: the rectangle (if any) is visible in the picture (<see cref="Visible"/>; what sticks out is clipped away), and when a click point is known it lies inside the picture and, with a
    /// rectangle, inside the visible part of it (<paramref name="reach"/> px more to each side). Without a rectangle only the point is judged (the arrow alone). Nothing known: false.</summary>
    public static bool Accept(PxRect picture, PxRect? rect, (int X, int Y)? click, int slack = Slack, int reach = 0)
    {
        if (rect is null && click is null) return false;
        PxRect? shown = null;
        if (rect is { } r && (shown = Visible(picture, r)) is null) return false;
        if (click is { } c)
        {
            if (!picture.Contains(c.X, c.Y)) return false;
            if (shown is { } v && !(c.X >= v.Left - slack - reach && c.X < v.Right + slack + reach && c.Y >= v.Top - slack && c.Y < v.Bottom + slack)) return false;
        }
        return true;
    }

    /// <summary>A tool strip item's rectangle is usable only when it lies inside the strip's client area (a stale item rectangle, or a strip scrolled or clipped by its container, gives one that does not).</summary>
    public static bool ItemInStrip(PxRect strip, PxRect item) => RectInside(strip, item, 0);
}
