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

    static int Shift(int lo, int hi, int alo, int ahi)
    {
        if (hi - lo > ahi - alo) return lo < alo || lo >= ahi ? alo - lo : 0;
        if (lo < alo) return alo - lo;
        if (hi > ahi) return ahi - hi;
        return 0;
    }
}
