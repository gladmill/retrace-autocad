namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).
public static class TdocGeometry
{
    /// <summary>How far to move the rectangle (<paramref name="l"/>, <paramref name="t"/>, <paramref name="r"/>, <paramref name="b"/>) to lie inside
    /// the area (<paramref name="al"/>, <paramref name="at"/>, <paramref name="ar"/>, <paramref name="ab"/>), the least that does it. A rectangle
    /// that does not touch the area at all is centred in it; one larger than the area keeps its top-left corner in it. (0, 0) when it lies inside already.</summary>
    public static (int Dx, int Dy) MoveInto(int l, int t, int r, int b, int al, int at, int ar, int ab)
    {
        int w = r - l, h = b - t, aw = ar - al, ah = ab - at;
        if (w <= 0 || h <= 0 || aw <= 0 || ah <= 0) return (0, 0);
        if (r <= al || l >= ar || b <= at || t >= ab) return (al + (aw - w) / 2 - l, at + (ah - h) / 2 - t);
        return (Shift(l, r, al, ar), Shift(t, b, at, ab));
    }

    /// <summary>Whether a rectangle can be the control an arrow points at: it has a size, lies in the dialog's client area (<paramref name="cl"/>, <paramref name="ct"/>, <paramref name="cr"/>,
    /// <paramref name="cb"/>: not in its title bar or border, so not above the client area's top either) and is not (nearly) all of that area, which would be the dialog itself. Any other rectangle
    /// inside the client area is a control, however wide or tall (a full-width button, a tall list).</summary>
    public static bool IsControlOf(int l, int t, int r, int b, int cl, int ct, int cr, int cb)
    {
        int w = r - l, h = b - t, cw = cr - cl, ch = cb - ct;
        if (w <= 0 || h <= 0 || cw <= 0 || ch <= 0) return false;
        int cx = (l + r) / 2, cy = (t + b) / 2;
        if (cx < cl || cx >= cr || cy < ct || cy >= cb) return false;
        if (t < ct) return false;                       // reaches into the title bar: the dialog (or its caption), not a control
        return !(w * 100 >= cw * 95 && h * 100 >= ch * 95);   // (nearly) the whole client area
    }

    static int Shift(int lo, int hi, int alo, int ahi)
    {
        if (hi - lo > ahi - alo) return lo < alo || lo >= ahi ? alo - lo : 0;
        if (lo < alo) return alo - lo;
        if (hi > ahi) return ahi - hi;
        return 0;
    }
}
