using System.Globalization;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// The parts of behavior capture that need no AutoCAD: entity ids with `#2`, `#3`... for repeats, the viewport order and key, xdata and Xrecord
/// groups as text, the named-object-dictionary filter, small formatters. `AutoCad/BehaviorCapture*.cs` calls these; see docs/recording-format.md.
/// </summary>
public static class BehaviorKeys
{
    /// <summary>`MS:` for model space.</summary>
    public const string ModelPrefix = "MS:";

    /// <summary>`PS:&lt;layout&gt;:` for a paper-space layout.</summary>
    public static string PaperPrefix(string layout) => "PS:" + layout + ":";

    static string I(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Entity ids for the keyed entities of ONE space, given their keys in creation order (the order of the owner block table record).
    /// The id is the prefix plus the key; the second and later entities with the same key get `#2`, `#3`... in that order. So a single edit
    /// does not renumber the others, and data swapped between identical entities is detected. Limit: if a replay creates identical entities in
    /// another order than the recording did, the numbering follows creation order.</summary>
    public static string[] EntityIds(string prefix, IReadOnlyList<string> keysInCreationOrder)
    {
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new string[keysInCreationOrder.Count];
        for (var i = 0; i < ids.Length; i++)
        {
            var key = keysInCreationOrder[i];
            var n = count[key] = count.GetValueOrDefault(key) + 1;
            ids[i] = n == 1 ? prefix + key : prefix + key + "#" + I(n);
        }
        return ids;
    }

    /// <summary>Viewport order inside a layout: centre Y descending, then centre X ascending, compared after rounding like every key
    /// (so 1e-9 of float noise does not reorder); ties keep the input order.</summary>
    public static List<T> ViewportOrder<T>(IEnumerable<(double CenterX, double CenterY, T Item)> viewports) =>
        viewports.Select((v, i) => (X: Rounded(v.CenterX), Y: Rounded(v.CenterY), v.Item, I: i))
            .OrderByDescending(v => v.Y).ThenBy(v => v.X).ThenBy(v => v.I).Select(v => v.Item).ToList();

    static double Rounded(double d) => Math.Round(d, DrawingDigest.Decimals, MidpointRounding.AwayFromZero);

    /// <summary>`&lt;layout&gt;|&lt;n&gt;` (n from 1).</summary>
    public static string ViewportKey(string layout, int n) => layout + "|" + I(n);

    /// <summary>`&lt;owner id&gt;|&lt;regapp&gt;`.</summary>
    public static string XDataKey(string ownerId, string regapp) => ownerId + "|" + regapp;

    /// <summary>`&lt;owner id&gt;|&lt;entry path&gt;`; the path is the dictionary entry names joined by `/`.</summary>
    public static string XDictKey(string ownerId, IEnumerable<string> path) => ownerId + "|" + string.Join("/", path.Select(EscapePathSegment));

    /// <summary>`\` and `/` in a dictionary entry name are written `\\` and `\/`, so nested A -> B and a top-level entry named `A/B` differ.</summary>
    public static string EscapePathSegment(string name) => name.Replace("\\", "\\\\").Replace("/", "\\/");

    /// <summary>`&lt;layout&gt;|&lt;insert id&gt;|&lt;TAG&gt;`; a second attribute with the same tag in one insert gets `#2`.</summary>
    public static string AttributeKey(string layout, string insertId, string tag, int repeat = 1) =>
        layout + "|" + insertId + "|" + tag + (repeat > 1 ? "#" + I(repeat) : "");

    /// <summary>One xdata / Xrecord group: `code:value`.</summary>
    public static string Group(int code, string value) => I(code) + ":" + value;

    /// <summary>Xdata groups (in order) split by registered application: a group with code 1001 starts the next application, whose name is its
    /// value; the 1001 group itself is not repeated in the list. Groups before the first 1001 are dropped (xdata always starts with one).
    /// An application that appears twice gets its groups joined.</summary>
    public static SortedDictionary<string, List<string>> SplitXData(IEnumerable<(int Code, string Value)> groups)
    {
        var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var (code, value) in groups)
        {
            if (code == 1001)
            {
                if (!result.TryGetValue(value, out current)) result[value] = current = new List<string>();
            }
            else current?.Add(Group(code, value));
        }
        return result;
    }

    /// <summary>The canonical value of a list of groups: fields `0`, `1`, `2`... each a group. A compare names the index that differs.</summary>
    public static string GroupsValue(IEnumerable<string> groups) =>
        CanonicalValue.Join(groups.Select((g, i) => (I(i), (string?)g)));

    /// <summary>The canonical value of an extension-dictionary entry: field `type` (the DXF name), then the data groups as fields `0`, `1`...
    /// (none for anything but an Xrecord).</summary>
    public static string EntryValue(string dxfName, IEnumerable<string> groups) =>
        CanonicalValue.Join(new[] { ("type", (string?)dxfName) }.Concat(groups.Select((g, i) => (I(i), (string?)g))));

    /// <summary>Is a named-object-dictionary entry AutoCAD's own (or noise)? Names starting `ACAD_` (group, layout, materials, styles, scale list,
    /// plot settings...), `AcDb` (variable dictionary, placeholders), `AEC_` and `ASDK_` are skipped, compared without case. Everything else,
    /// such as an add-in's own dictionary, is kept.</summary>
    public static bool IsBuiltInNodEntry(string name) =>
        name.StartsWith("ACAD_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AcDb", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("AEC_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ASDK_", StringComparison.OrdinalIgnoreCase);

    /// <summary>`TYPE:n,TYPE:n` sorted by type name (ordinal); empty for no entities.</summary>
    public static string TypeCounts(IEnumerable<string> dxfNames) =>
        string.Join(",", dxfNames.GroupBy(t => t, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key + ":" + I(g.Count())));

    /// <summary>Booleans are `1` / `0`.</summary>
    public static string Bool(bool b) => b ? "1" : "0";

    /// <summary>Rounded 2D point `x,y`.</summary>
    public static string Pt2(double x, double y) => DrawingDigest.Num(x) + "," + DrawingDigest.Num(y);

    /// <summary>Names sorted ordinally and joined by `,` (frozen layers of a viewport).</summary>
    public static string SortedList(IEnumerable<string> names) => string.Join(",", names.OrderBy(n => n, StringComparer.Ordinal));

    // ---- appearance (behavior `appearance`) ----

    public const string ByLayer = "ByLayer", ByBlock = "ByBlock";

    /// <summary>The entity key without its layer: `dxf|rest` for a key made by <c>DrawingDigest.Key / PlacedKey</c> (`dxf|layer|rest`). The
    /// `appearance` behavior names an entity with this, so a layer change is a changed field (`layer: expected A, got B`) and not an entity
    /// that vanished and another that appeared. The dxf part is whatever the key starts with (a DXF name has no `|`), so a block-reference
    /// subclass keyed as INSERT works too. A key whose second part is not `layer` is returned unchanged.</summary>
    public static string LayerlessKey(string key, string layer)
    {
        var bar = key.IndexOf('|');
        if (bar < 0) return key;
        var rest = key.AsSpan(bar + 1);
        return rest.StartsWith(layer, StringComparison.Ordinal) && rest.Length > layer.Length && rest[layer.Length] == '|'
            ? string.Concat(key.AsSpan(0, bar + 1), rest[(layer.Length + 1)..])
            : key;
    }

    /// <summary>The look of one entity: its own values, and what its layer gives (used only for the properties set to ByLayer).</summary>
    public sealed record Look(string Layer, string Color, string Linetype, string LtScale, string Lineweight, string Transparency, string? PlotStyle,
        string LayerColor, string LayerLinetype, string LayerLineweight, string LayerTransparency);

    /// <summary>The canonical value of an `appearance` item, fields in this order: `layer`, `color`, `linetype`, `ltscale`, `lineweight`,
    /// `transparency`, `plotstyle` (only for a named plot style), then `rcolor`, `rlinetype`, `rlineweight`, `rtransparency` (the layer's value,
    /// only for a property that is ByLayer, so an explicit value is not repeated). ByBlock is not resolved: it depends on the insert.</summary>
    public static string AppearanceValue(Look l)
    {
        var f = new List<(string, string?)>
        {
            ("layer", l.Layer), ("color", l.Color), ("linetype", l.Linetype), ("ltscale", l.LtScale), ("lineweight", l.Lineweight),
            ("transparency", l.Transparency),
        };
        if (!string.IsNullOrEmpty(l.PlotStyle)) f.Add(("plotstyle", l.PlotStyle));
        if (l.Color == ByLayer) f.Add(("rcolor", l.LayerColor));
        if (l.Linetype == ByLayer) f.Add(("rlinetype", l.LayerLinetype));
        if (l.Lineweight == ByLayer) f.Add(("rlineweight", l.LayerLineweight));
        if (l.Transparency == ByLayer) f.Add(("rtransparency", l.LayerTransparency));
        return CanonicalValue.Join(f);
    }

    /// <summary>The resolved fields of an `appearance` value: what the layer gives, owned by the `layers` behavior.</summary>
    public static bool IsResolvedField(string name) => name is "rcolor" or "rlinetype" or "rlineweight" or "rtransparency";

    /// <summary>An `appearance` value without its resolved (`r...`) fields: the entity's own look. Text that is not a canonical value is returned unchanged.</summary>
    public static string OwnLook(string value) =>
        CanonicalValue.TryParse(value, out var f) && f != null ? CanonicalValue.Join(f.Where(x => !IsResolvedField(x.Name)).Select(x => (x.Name, (string?)x.Value))) : value;

    /// <summary>A plot style name worth recording: not empty and not an AutoCAD default (`ByLayer`, `ByColor`); null otherwise.</summary>
    public static string? NamedPlotStyle(string? name) =>
        string.IsNullOrEmpty(name) || name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) || name.Equals("ByColor", StringComparison.OrdinalIgnoreCase) ? null : name;

    /// <summary>The Transparency text used by the layers behavior and `appearance`: the alpha (0-255), `ByBlock` or `ByLayer`.</summary>
    public static string TransparencyText(bool byAlpha, byte alpha, bool byBlock) =>
        byAlpha ? alpha.ToString(CultureInfo.InvariantCulture) : byBlock ? ByBlock : ByLayer;

    /// <summary>Most items `appearance` or `paperspace` will hold; a drawing with more leaves the behavior out ("could not be read", logged once)
    /// so a snapshot stays a few tens of megabytes.</summary>
    public const int MaxEntityItems = 100_000;
}
