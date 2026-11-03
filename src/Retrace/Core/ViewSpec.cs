using System.Globalization;
using System.Text.Json;

namespace Retrace.Core;

/// <summary>The view of the active viewport when a point was clicked or an object was picked: what the replay shows again before it types the point.
/// Pure data and arithmetic (no AutoCAD); the recorder fills it from the Editor's current view (<c>AutoCad/ViewControl.cs</c>), the replay sets it with the
/// Editor / ViewTableRecord API. Stored as `view` in the value of an `entity` / `nestedEntity` answer and of a `selection` answer that has `clicks`
/// (docs/recording-format.md, "Selections").</summary>
/// <param name="Space">`model` (the Model tab), `paper` (a layout, no viewport made current) or `viewport` (inside a floating viewport of a layout)</param>
/// <param name="Layout">the layout tab (CTAB)</param>
/// <param name="Viewport">CVPORT, the number of the current viewport (1 in paper space)</param>
/// <param name="Center">view centre in DCS (the ViewTableRecord's CenterPoint)</param>
/// <param name="Height">height of the view in drawing units</param>
/// <param name="Width">width of the view in drawing units (height times the window's aspect ratio when it was recorded)</param>
/// <param name="Direction">view direction, a vector in WCS (from the target to the camera)</param>
/// <param name="Target">target point, WCS</param>
/// <param name="Twist">view twist, radians</param>
public sealed record ViewSpec(string Space, string Layout, int Viewport, double[] Center, double Height, double Width, double[] Direction, double[] Target, double Twist)
{
    /// <summary>The `view` object as it is written in a recording.</summary>
    public Dictionary<string, object?> ToJson() => new()
    {
        ["space"] = Space,
        ["layout"] = Layout,
        ["vp"] = Viewport,
        ["center"] = Center.Select(Round).ToArray(),
        ["height"] = Round(Height),
        ["width"] = Round(Width),
        ["dir"] = Direction.Select(Round).ToArray(),
        ["target"] = Target.Select(Round).ToArray(),
        ["twist"] = Round(Twist),
    };

    static double Round(double v) => Math.Round(v, 9);

    /// <summary>Reads the `view` property of a recorded value. Null when it is missing or malformed (an old recording, or a view that could not be read).</summary>
    public static ViewSpec? FromValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("view", out var v) || v.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var space = Text(v, "space");
            if (space is not ("model" or "paper" or "viewport")) return null;
            var center = Nums(v, "center", 2); var dir = Nums(v, "dir", 3); var target = Nums(v, "target", 3);
            if (center == null || dir == null || target == null) return null;
            if (!Num(v, "height", out var h) || !Num(v, "width", out var w) || !(h > 0) || !(w > 0)) return null;
            Num(v, "twist", out var tw);
            int vp = v.TryGetProperty("vp", out var vpe) && vpe.ValueKind == JsonValueKind.Number && vpe.TryGetInt32(out var n) ? n : 0;
            if (Math.Abs(Len(dir)) < 1e-9) return null;
            return new ViewSpec(space, Text(v, "layout") ?? "", vp, center, h, w, dir, target, tw);
        }
        catch (Exception) { return null; }   // a malformed view is no view: the replay falls back to what it did before
    }

    /// <summary>A copy of a recorded value without its `view` (the view of that answer turned out stale: see `view_stale`).</summary>
    public static JsonElement WithoutView(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("view", out _)) return value.Clone();
        var node = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!.AsObject();
        node.Remove("view");
        return JsonSerializer.SerializeToElement(node);
    }

    static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Num(JsonElement e, string key, out double d)
    {
        d = 0;
        return e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d) && double.IsFinite(d);
    }
    /// <summary>The numbers of an array property (at least <paramref name="min"/>, all finite); null when missing or malformed. Shared with <see cref="PickReplay"/>.</summary>
    public static double[]? Nums(JsonElement e, string key, int min)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var l = new List<double>();
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out var d) || !double.IsFinite(d)) return null;
            l.Add(d);
        }
        return l.Count >= min ? l.ToArray() : null;
    }
    static double Len(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

    /// <summary>The size of the view to show in a window whose width / height is <paramref name="windowAspect"/>, so that the recorded area (Width x Height) is
    /// fully visible: the recorded height when the window is at least as wide as the recorded one, else the recorded width. The centre stays.</summary>
    public (double Width, double Height) Fit(double windowAspect)
    {
        if (!(windowAspect > 0) || !double.IsFinite(windowAspect)) return (Width, Height);
        double recorded = Width / Height;
        return windowAspect >= recorded ? (Height * windowAspect, Height) : (Width, Width / windowAspect);
    }

    /// <summary>Whether two views show the same thing (what one replayed step needs: a point typed in the first and one in the second need one view).</summary>
    public bool Same(ViewSpec o)
    {
        if (Space != o.Space || !string.Equals(Layout, o.Layout, StringComparison.Ordinal) || Viewport != o.Viewport) return false;
        double scale = Math.Max(Height, o.Height);
        bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;
        return Near(Height, o.Height, scale * 1e-6) && Near(Width, o.Width, scale * 1e-6)
            && Near(Center[0], o.Center[0], scale * 1e-6) && Near(Center[1], o.Center[1], scale * 1e-6)
            && Near(Twist, o.Twist, 1e-9)
            && Near(Target[0], o.Target[0], scale * 1e-6) && Near(Target[1], o.Target[1], scale * 1e-6) && Near(Target[2], o.Target[2], scale * 1e-6)
            && Near(Direction[0], o.Direction[0], 1e-9) && Near(Direction[1], o.Direction[1], 1e-9) && Near(Direction[2], o.Direction[2], 1e-9);
    }

    /// <summary>Whether the view looks straight down the Z axis of a frame (the plan view of a UCS, or of the WCS): only then a typed 2D point means one place
    /// on the screen. <paramref name="zAxis"/> is that frame's Z axis in WCS.</summary>
    public bool IsPlanOf(double[] zAxis)
    {
        double a = Len(Direction), b = Len(zAxis);
        if (a < 1e-12 || b < 1e-12) return false;
        double dot = (Direction[0] * zAxis[0] + Direction[1] * zAxis[1] + Direction[2] * zAxis[2]) / (a * b);
        return Math.Abs(Math.Abs(dot) - 1) < 1e-6;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Space} {Layout} vp{Viewport} centre {Center[0]:0.###},{Center[1]:0.###} height {Height:0.###}");
}
