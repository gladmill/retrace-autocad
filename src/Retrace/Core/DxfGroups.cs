namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// Reads the two persisted flags that behavior capture needs from the DXF groups of an object (what `entget` returns), because the managed
/// API gives them only through session state: `Viewport.On` reads false until the layout has been current in the running session, and the
/// layout's PSLTSCALE is loaded into the system variable only while the layout is current.
/// The groups are (code, value) pairs in order; values are numbers or text. See docs/recording-format.md, "Session independence".
/// </summary>
public static class DxfGroups
{
    /// <summary>VIEWPORT group 90, "turn viewport off".</summary>
    public const int ViewportOffBit = 0x20000;
    /// <summary>LAYOUT group 70 (the one after the `AcDbLayout` marker), "PSLTSCALE when this layout is current".</summary>
    public const int LayoutPsltscaleBit = 1;

    /// <summary>The viewport's group 90 (status flags): the first 90 after the `AcDbViewport` subclass marker. Null if absent.</summary>
    public static int? ViewportFlags(IEnumerable<(int Code, object? Value)> groups) => FlagAfter(groups, "AcDbViewport", 90);

    /// <summary>The layout's group 70 after the `AcDbLayout` marker (the first 70, in `AcDbPlotSettings`, is the plot flags). Null if absent.</summary>
    public static int? LayoutFlags(IEnumerable<(int Code, object? Value)> groups) => FlagAfter(groups, "AcDbLayout", 70);

    /// <summary>true = the viewport is turned off (its off bit is set).</summary>
    public static bool ViewportOff(int flags) => (flags & ViewportOffBit) != 0;

    /// <summary>The layout's own PSLTSCALE (0 or 1).</summary>
    public static bool LayoutPsltscale(int flags) => (flags & LayoutPsltscaleBit) != 0;

    static int? FlagAfter(IEnumerable<(int Code, object? Value)> groups, string subclass, int code)
    {
        var inside = false;
        foreach (var (c, v) in groups)
        {
            if (c == 100) { inside = v is string s && string.Equals(s, subclass, StringComparison.Ordinal); continue; }
            if (inside && c == code) return v switch
            {
                int i => i,
                short sh => sh,
                long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
                uint u when u <= int.MaxValue => (int)u,
                ushort us => us,
                _ => null,
            };
        }
        return null;
    }
}
