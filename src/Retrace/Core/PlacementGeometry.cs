namespace Retrace.Core;

// NO AutoCAD, WPF or Win32 references in this file (linked into the engine test project). All numbers are device pixels in virtual-screen
// coordinates (a monitor left of the primary one has negative X), as GetWindowRect / GetMonitorInfo give them. The thin Win32 part is AutoCad/WindowPlacement.cs.

/// <summary>A rectangle in device pixels (Right and Bottom are exclusive, as in Win32 RECT).</summary>
public readonly record struct PxBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => Math.Max(0, (long)Width) * Math.Max(0, (long)Height);
}

/// <summary>Where a Retrace window goes: centred on its owner, clamped into a work area, or in the lower right corner (progress notices).</summary>
public static class PlacementGeometry
{
    /// <summary>The gap between a corner window and the edges of the work area, in device pixels at 100 % (see <see cref="Scale"/>).</summary>
    public const int CornerMargin = 16;

    /// <summary>The work area that holds <paramref name="owner"/> best: the one with the largest overlap, else the one nearest its centre. Null when <paramref name="works"/> is empty.</summary>
    public static PxBox? PickWork(PxBox owner, IReadOnlyList<PxBox> works)
    {
        if (works.Count == 0) return null;
        PxBox? best = null; long bestArea = -1;
        foreach (var w in works)
        {
            var a = Intersect(owner, w).Area;
            if (a > bestArea) { bestArea = a; best = w; }
        }
        if (bestArea > 0) return best;
        // no overlap at all (the owner is off every monitor): the nearest centre
        long cx = ((long)owner.Left + owner.Right) / 2, cy = ((long)owner.Top + owner.Bottom) / 2;
        return works.OrderBy(w => Dist2(cx, cy, ((long)w.Left + w.Right) / 2, ((long)w.Top + w.Bottom) / 2)).First();
    }

    static long Dist2(long ax, long ay, long bx, long by) => (ax - bx) * (ax - bx) + (ay - by) * (ay - by);

    static PxBox Intersect(PxBox a, PxBox b)
    {
        var l = Math.Max(a.Left, b.Left); var t = Math.Max(a.Top, b.Top);
        var r = Math.Min(a.Right, b.Right); var bt = Math.Min(a.Bottom, b.Bottom);
        return r <= l || bt <= t ? new PxBox(0, 0, 0, 0) : new PxBox(l, t, r, bt);
    }

    /// <summary>The top left corner (X, Y) of a window of <paramref name="width"/> x <paramref name="height"/> centred on <paramref name="owner"/>, then moved so it lies inside <paramref name="work"/>
    /// (a window larger than the work area sits at its top left).</summary>
    public static (int X, int Y) CenterOn(PxBox owner, int width, int height, PxBox work)
    {
        var x = owner.Left + (owner.Width - width) / 2;
        var y = owner.Top + (owner.Height - height) / 2;
        return Clamp(x, y, width, height, work);
    }

    /// <summary>The corner a progress notice opens in: the lower right of <paramref name="work"/>, <paramref name="margin"/> from the right and bottom edges.</summary>
    public static (int X, int Y) LowerRight(PxBox work, int width, int height, int margin) => Clamp(work.Right - width - margin, work.Bottom - height - margin, width, height, work);

    /// <summary>The top left corner moved the least so the window lies inside <paramref name="work"/>; a window wider or taller than the work area is aligned to its left / top edge.</summary>
    public static (int X, int Y) Clamp(int x, int y, int width, int height, PxBox work)
    {
        if (x + width > work.Right) x = work.Right - width;
        if (y + height > work.Bottom) y = work.Bottom - height;
        if (x < work.Left) x = work.Left;
        if (y < work.Top) y = work.Top;
        return (x, y);
    }

    /// <summary>The size capped to <paramref name="work"/> in each dimension on its own (a window wider than the work area but not taller is only made narrower).</summary>
    public static (int Width, int Height) FitSize(int width, int height, PxBox work) => (Math.Min(width, Math.Max(1, work.Width)), Math.Min(height, Math.Max(1, work.Height)));

    /// <summary>A corner notice that changed height (its text grew or shrank): the bottom edge stays <paramref name="margin"/> above the bottom of <paramref name="work"/>, so the buttons at the bottom never slide under the taskbar.
    /// X is kept (a window that was moved aside stays aside) but moved into the work area; the height is capped to the work area less the margins. Returns the top left corner and the height to use.</summary>
    public static (int X, int Y, int Height) KeepBottom(PxBox current, PxBox work, int margin)
    {
        var h = Math.Min(current.Height, Math.Max(1, work.Height - 2 * margin));
        var (x, _) = Clamp(current.Left, work.Top, current.Width, h, work);
        return (x, work.Bottom - margin - h, h);
    }

    /// <summary>Device pixels for a length in device-independent units (1/96 inch) at <paramref name="dpi"/> (96 = 100 %), rounded to the nearest pixel. A dpi of 0 or less reads as 96.</summary>
    public static int Scale(double dips, int dpi) => (int)Math.Round(dips * (dpi <= 0 ? 96 : dpi) / 96.0, MidpointRounding.AwayFromZero);

    /// <summary>The owner rectangle to centre on: the AutoCAD (or parent) window's rectangle, or, when that window is minimized or has no usable size, the work area of the monitor it was on.</summary>
    public static PxBox OwnerOrWork(PxBox? ownerRect, bool ownerMinimized, PxBox work) => ownerRect is { IsEmpty: false } r && !ownerMinimized ? r : work;
}
