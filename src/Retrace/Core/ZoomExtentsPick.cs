namespace Retrace.Core;

/// <summary>Which rule gave the box a zoom uses for one object.</summary>
public enum ZoomExtentsRule
{
    /// <summary>The object's own GeometricExtents.</summary>
    Object,
    /// <summary>Entity.Bounds, because GeometricExtents was point-like, tiny, degenerate, reversed or threw.</summary>
    Bounds,
    /// <summary>The union of the pieces of a temporary Explode, same reason.</summary>
    Exploded,
    /// <summary>Nothing better than a point-like box was found: the zoom uses the minimum window size.</summary>
    Point,
    /// <summary>No usable box at all.</summary>
    None,
}

/// <summary>What a temporary explode gave: the united extents of the pieces, how many pieces were used of how many returned, and a reason when the result must not be used (stopped at the time budget,
/// a piece that lives in the database).</summary>
public readonly record struct ExplodeResult(ZoomBox? Union, int Used, int Total, string? Problem)
{
    public static ExplodeResult None => new(null, 0, 0, null);
}

/// <summary>The extents of one object for the zoom, with a fall-back for an object whose GeometricExtents is useless or too small to say (a custom entity may report a point, a tiny box, a reversed box or throw).
/// Pure: AutoCAD supplies the three readers.</summary>
public static class ZoomExtentsPick
{
    public const double PointSize = 1e-6;     // a box whose every side is at most this is point-like
    public const double ClearlyLarger = 2.0;  // a fall-back replaces a small (not point-like) geometric box only when its larger side is at least this many times bigger and it contains that box

    public readonly record struct Result(ZoomBox? Box, ZoomExtentsRule Rule, string Seen);

    public static bool PointLike(ZoomBox b) => b.MaxX - b.MinX <= PointSize && b.MaxY - b.MinY <= PointSize && b.MaxZ - b.MinZ <= PointSize;

    public static double Larger(ZoomBox b) => Math.Max(b.MaxX - b.MinX, Math.Max(b.MaxY - b.MinY, b.MaxZ - b.MinZ));

    static bool Contains(ZoomBox o, ZoomBox i) => o.MinX <= i.MinX && o.MinY <= i.MinY && o.MinZ <= i.MinZ && o.MaxX >= i.MaxX && o.MaxY >= i.MaxY && o.MaxZ >= i.MaxZ;

    static string Fmt(ZoomBox b) => $"{b.MinX:0.###},{b.MinY:0.###},{b.MinZ:0.###} to {b.MaxX:0.###},{b.MaxY:0.###},{b.MaxZ:0.###}";

    /// <summary>Picks the box. <paramref name="geometric"/> is GeometricExtents, <paramref name="bounds"/> Entity.Bounds (null when there is none), <paramref name="exploded"/> a temporary explode (the reader
    /// stops itself at the time budget and says so in <see cref="ExplodeResult.Problem"/>). Each reader may throw; the exception is noted in <see cref="Result.Seen"/>, which says what each one gave, for the log.
    /// A usable geometric box larger than <paramref name="smallSide"/> (the size under which the minimum-size rule would decide the window) is used as it is and nothing else is asked. Otherwise, when
    /// <paramref name="tryFallbacks"/> is set, Bounds then the explode are asked; a result is used only when it is usable and not point-like and, when the geometric box is usable (small but not a point), it contains that
    /// box and is at least <see cref="ClearlyLarger"/> times larger. Without a better box: the geometric one (<see cref="ZoomExtentsRule.Object"/>, or <see cref="ZoomExtentsRule.Point"/> when it is point-like), else
    /// the first point-like fall-back, else none.</summary>
    public static Result Pick(Func<ZoomBox?> geometric, Func<ZoomBox?> bounds, Func<ExplodeResult> exploded, double maxSpan, double smallSide, bool tryFallbacks)
    {
        var seen = new List<string>();
        string Seen() => string.Join("; ", seen);
        ZoomBox? g = null; ZoomBox? pointBox = null;
        try
        {
            if (geometric() is { } x)
            {
                if (x.Usable(maxSpan)) { g = x; seen.Add("GeometricExtents " + Fmt(x)); } else seen.Add("GeometricExtents unusable " + Fmt(x));
            }
            else seen.Add("GeometricExtents none");
        }
        catch (Exception ex) { seen.Add("GeometricExtents threw " + ex.GetType().Name + ": " + ex.Message); }
        bool gPoint = g is { } gb0 && PointLike(gb0);
        if (g is { } gb && !gPoint && Larger(gb) > smallSide) return new Result(gb, ZoomExtentsRule.Object, Seen());
        Result Fallback()
        {
            if (g is { } o) return new Result(o, gPoint ? ZoomExtentsRule.Point : ZoomExtentsRule.Object, Seen());
            return pointBox is { } p ? new Result(p, ZoomExtentsRule.Point, Seen()) : new Result(null, ZoomExtentsRule.None, Seen());
        }
        if (!tryFallbacks) return Fallback();

        bool Accept(string name, ZoomBox x)
        {
            if (!x.Usable(maxSpan)) { seen.Add(name + " unusable " + Fmt(x)); return false; }
            seen.Add(name + " " + Fmt(x));
            if (PointLike(x)) { pointBox ??= x; return false; }
            if (g is { } o && !gPoint && !(Contains(x, o) && Larger(x) >= ClearlyLarger * Larger(o))) { seen.Add(name + " not clearly larger than GeometricExtents: kept"); return false; }
            return true;
        }
        try
        {
            if (bounds() is { } b) { if (Accept("Bounds", b)) return new Result(b, ZoomExtentsRule.Bounds, Seen()); }
            else seen.Add("Bounds none");
        }
        catch (Exception ex) { seen.Add("Bounds threw " + ex.GetType().Name + ": " + ex.Message); }
        try
        {
            var r = exploded();
            if (r.Problem != null) seen.Add("explode " + r.Problem);
            else if (r.Union is { } u) { if (Accept($"explode ({r.Used} pieces)", u)) return new Result(u, ZoomExtentsRule.Exploded, Seen()); }
            else seen.Add($"explode ({r.Total} pieces) none");
        }
        catch (Exception ex) { seen.Add("explode threw " + ex.GetType().Name + ": " + ex.Message); }
        return Fallback();
    }
}
