namespace Retrace.Core;

/// <summary>The values of <see cref="RetraceSettings.PostTestZoom"/> (setting <c>postTestZoom</c>): what the view of model space does when a replayed test has run its last step.</summary>
public static class PostTestZoomModes
{
    /// <summary>Leave the view as it is (the default).</summary>
    public const string None = "none";
    /// <summary>Zoom the model-space viewport to the drawing extents.</summary>
    public const string Extents = "extents";
    /// <summary>Zoom to the most recently created model-space object (the newest handle created during the test).</summary>
    public const string LastEntity = "lastEntity";

    public static readonly string[] All = { None, Extents, LastEntity };

    /// <summary>The canonical value for a text from a settings file or the command line: matched without case, around spaces and quotes; "1" / "2" and a few spellings
    /// ("last", "lastobject", "zoomextents") also work. Anything else, or null, is <see cref="None"/>.</summary>
    public static string Normalize(string? text) => Normalize(text, out _);

    /// <summary>True when the text is one of the spellings.</summary>
    public static bool IsKnown(string? text) { Normalize(text, out var k); return k; }

    /// <summary>As <see cref="Normalize(string?)"/>; <paramref name="known"/> is false when the text is none of the spellings (it then reads as none).</summary>
    public static string Normalize(string? text, out bool known)
    {
        var t = (text ?? "").Trim().Trim('"').Trim().Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
        known = true;
        return t switch
        {
            "extents" or "extent" or "zoomextents" or "e" or "1" => Extents,
            "lastentity" or "last" or "lastobject" or "lastcreated" or "l" or "2" => LastEntity,
            "" or "none" or "n" or "no" or "off" or "0" => None,
            _ => Unknown(out known),
        };
        static string Unknown(out bool known) { known = false; return None; }
    }
}

/// <summary>The view window for the post-test zoom, in the view's own (DCS) coordinates.</summary>
public readonly record struct ZoomWindow(double CenterX, double CenterY, double Width, double Height)
{
    /// <summary>The window that shows the box (<paramref name="minX"/>..<paramref name="maxY"/>, in view coordinates) with <paramref name="margin"/> (a fraction of the box's
    /// larger side, added on every side) around it, at least <paramref name="minSize"/> wide and high, and with the viewport's shape (<paramref name="aspect"/> = width / height).
    /// Null when the box is not usable: a coordinate that is not a finite number, a reversed box (the extents of an empty drawing), or a non-positive aspect.</summary>
    public static ZoomWindow? Fit(double minX, double minY, double maxX, double maxY, double aspect, double margin, double minSize)
    {
        if (!Ok(minX) || !Ok(minY) || !Ok(maxX) || !Ok(maxY) || !Ok(aspect) || !(aspect > 0)) return null;
        if (maxX < minX || maxY < minY) return null;
        if (!Ok(margin) || margin < 0) margin = 0;
        if (!Ok(minSize) || minSize <= 0) minSize = 1e-6;
        double w = maxX - minX, h = maxY - minY;
        var pad = Math.Max(w, h) * margin;
        w += 2 * pad; h += 2 * pad;
        if (w < minSize) w = minSize;
        if (h < minSize) h = minSize;
        // the viewport's shape: grow the short side so the whole box stays visible
        if (w / h < aspect) w = h * aspect; else h = w / aspect;
        return new ZoomWindow((minX + maxX) / 2, (minY + maxY) / 2, w, h);
    }

    static bool Ok(double d) => !double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e99;

    /// <summary>Like <see cref="Fit"/> for the zoom to an object, with a minimum size that does not make the view shrink on every zoom to the same point-like object. The minimum is
    /// <paramref name="pointFraction"/> of the current view's height (the ordinary rule), except when <paramref name="memory"/> says the current view is the one the rule made last (same height) and the
    /// target is effectively a point at that scale: the same box as the remembered one (the same object zoomed again), or one whose larger side plus padding is at most 1 % of the remembered minimum.
    /// Then the remembered minimum (not the view height, which differs from it in a view that is not wider than high) is used again, so the window is the same. When the minimum decided the size,
    /// (minimum, resulting height, box) is remembered. <paramref name="log"/> gets a line when the target would have qualified but the view's height is not the remembered one.</summary>
    public static ZoomWindow? FitStable(double minX, double minY, double maxX, double maxY, double curWidth, double curHeight, double margin, double pointFraction, PointZoomMemory memory, Action<string>? log = null)
    {
        if (!Ok(curWidth) || !Ok(curHeight) || !(curWidth > 0) || !(curHeight > 0)) return null;
        var mg = Ok(margin) && margin > 0 ? margin : 0;
        var pad = Math.Max(maxX - minX, maxY - minY) * mg;
        var minSize = curHeight * pointFraction;
        if (memory.Qualifies(minX, minY, maxX, maxY, Math.Max(maxX - minX, maxY - minY) + 2 * pad))
        {
            if (memory.ViewMatches(curHeight)) minSize = memory.Minimum;
            else log?.Invoke($"point zoom memory: view height {curHeight:0.########}, remembered {memory.Height:0.########}: not the remembered view");
        }
        if (Fit(minX, minY, maxX, maxY, curWidth / curHeight, margin, minSize) is not { } w) return null;
        if (maxX - minX + 2 * pad < minSize && maxY - minY + 2 * pad < minSize) memory.Remember(minSize, w.Height, minX, minY, maxX, maxY);   // the minimum size gave the window
        return w;
    }
}

/// <summary>What the minimum-size rule (a point-like target) made last: the minimum that was used, the view height that came out (what tells "this view is the one the rule made"; it differs from the
/// minimum in a view that is not wider than high) and the target's box. See <see cref="ZoomWindow.FitStable"/>. Per process; lost on a restart (then the first zoom is 5 % of the view it finds, once).</summary>
public sealed class PointZoomMemory
{
    double _min, _height, _x0, _y0, _x1, _y1;
    public const double Tolerance = 1e-6;        // relative: the view's height after a round trip through the view table
    public const double SmallFraction = 0.01;    // "a point at that scale": padded larger side at most 1 % of the remembered minimum
    public const double SameBoxFraction = 1e-4;  // the same object: every coordinate within this fraction of the remembered minimum

    public double Minimum => _min;
    public double Height => _height;
    public bool IsSet => _min > 0;

    public bool ViewMatches(double curHeight) => _min > 0 && Math.Abs(curHeight - _height) <= Tolerance * _height;

    bool SameBox(double x0, double y0, double x1, double y1)
    {
        var t = SameBoxFraction * _min;
        return Math.Abs(x0 - _x0) <= t && Math.Abs(y0 - _y0) <= t && Math.Abs(x1 - _x1) <= t && Math.Abs(y1 - _y1) <= t;
    }

    /// <summary>True when something is remembered and the target is the remembered object's box or a point at the remembered minimum's scale.</summary>
    public bool Qualifies(double x0, double y0, double x1, double y1, double paddedLargerSide) =>
        _min > 0 && (SameBox(x0, y0, x1, y1) || paddedLargerSide <= SmallFraction * _min);

    public void Remember(double minimum, double height, double x0, double y0, double x1, double y1) { _min = minimum; _height = height; _x0 = x0; _y0 = y0; _x1 = x1; _y1 = y1; }
    public void Forget() { _min = 0; _height = 0; }
}
