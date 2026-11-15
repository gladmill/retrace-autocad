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
}
