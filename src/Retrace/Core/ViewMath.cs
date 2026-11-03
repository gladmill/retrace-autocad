namespace Retrace.Core;

/// <summary>The plane of a view and what lies on it: the arithmetic the pick check needs, without AutoCAD (the AutoCAD side gives it points and extents).
/// A view looks along its direction; the screen plane is perpendicular to it. Distances for a pick are measured in that plane, so geometry at another height than the picked
/// point (a plan view of elevated objects) is not rejected.</summary>
public static class ViewMath
{
    /// <summary>Two unit vectors that span the screen plane of a view with this direction: the arbitrary axis algorithm AutoCAD uses for the DCS (X first, then Y).</summary>
    public static (double[] X, double[] Y) PlaneBasis(double[] dir)
    {
        double len = Math.Sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
        double[] n = { dir[0] / len, dir[1] / len, dir[2] / len };
        double[] x = Math.Abs(n[0]) < 1.0 / 64 && Math.Abs(n[1]) < 1.0 / 64
            ? Cross(new double[] { 0, 1, 0 }, n)
            : Cross(new double[] { 0, 0, 1 }, n);
        Normalize(x);
        var y = Cross(n, x);
        Normalize(y);
        return (x, y);
    }

    /// <summary>The coordinates of a point in the screen plane of a view: its offset from <paramref name="origin"/> along the plane's X and Y.</summary>
    public static (double X, double Y) Project(double[] p, double[] origin, (double[] X, double[] Y) basis)
    {
        double a = p[0] - origin[0], b = p[1] - origin[1], c = p[2] - origin[2];
        return (a * basis.X[0] + b * basis.X[1] + c * basis.X[2], a * basis.Y[0] + b * basis.Y[1] + c * basis.Y[2]);
    }

    /// <summary>Distance in the screen plane from a point to the box of <paramref name="min"/> / <paramref name="max"/> (world corners, projected: the box of its eight corners in the plane,
    /// which contains the object, so a distance above the pick box means the object is out of reach). 0 inside.</summary>
    public static double BoxDistance(double[] min, double[] max, double[] p, double[] origin, (double[] X, double[] Y) basis)
    {
        double lx = double.MaxValue, hx = double.MinValue, ly = double.MaxValue, hy = double.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var c = new[] { (i & 1) == 0 ? min[0] : max[0], (i & 2) == 0 ? min[1] : max[1], (i & 4) == 0 ? min[2] : max[2] };
            var (x, y) = Project(c, origin, basis);
            lx = Math.Min(lx, x); hx = Math.Max(hx, x); ly = Math.Min(ly, y); hy = Math.Max(hy, y);
        }
        var (px, py) = Project(p, origin, basis);
        double dx = Math.Max(Math.Max(lx - px, 0), px - hx), dy = Math.Max(Math.Max(ly - py, 0), py - hy);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The length of the part of <paramref name="v"/> in the screen plane (the component along the view direction removed).</summary>
    public static double PlaneLength(double[] v, double[] dir)
    {
        double d2 = dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2];
        double t = (v[0] * dir[0] + v[1] * dir[1] + v[2] * dir[2]) / d2;
        double a = v[0] - t * dir[0], b = v[1] - t * dir[1], c = v[2] - t * dir[2];
        return Math.Sqrt(a * a + b * b + c * c);
    }

    /// <summary>Whether a world point is inside the area the view shows in a window of <paramref name="windowAspect"/> (<see cref="ViewSpec.Fit"/>). The view's own axes (the DCS) are the
    /// plane axes turned by the twist: a point at (x, y) in the plane is at (x cos t - y sin t, x sin t + y cos t) in the DCS. The sign was checked in AutoCAD 2026 with a view twisted by PLAN in a UCS
    /// rotated 30 degrees (twist 330 degrees, the clicked points inside the recorded window); the view centre is in these axes, relative to the target.</summary>
    public static bool InView(ViewSpec view, double windowAspect, double[] p)
    {
        var (w, h) = view.Fit(windowAspect);
        var (x, y) = Project(p, view.Target, PlaneBasis(view.Direction));
        double c = Math.Cos(view.Twist), s = Math.Sin(view.Twist);
        double vx = x * c - y * s, vy = x * s + y * c;
        return Math.Abs(vx - view.Center[0]) <= w / 2 && Math.Abs(vy - view.Center[1]) <= h / 2;
    }

    static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    static void Normalize(double[] v) { double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); v[0] /= l; v[1] /= l; v[2] /= l; }
}
