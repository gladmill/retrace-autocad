using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retrace.Core;

// NO AutoCAD references in this file: it is linked into the engine test project (tests/Retrace.Tests) and unit-tested there.

/// <summary>
/// A compact fingerprint of model space and the layer table: how many entities of each type / on each layer, the layer names,
/// and a hash over a canonical key per entity (type, layer, rounded extents, block name, length; text, blocks and dimensions: what
/// places them and their text instead of extents, see <see cref="PlacedKey"/>).
/// The same JSON shape is written to the session (`state` event, `mark.state`) and the replay result (`expected`, `actual`).
/// </summary>
public sealed class DrawingDigest
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("byType")] public SortedDictionary<string, int> ByType { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("byLayer")] public SortedDictionary<string, int> ByLayer { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("layers")] public List<string> Layers { get; set; } = new();
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    /// <summary>Key rules the hash was made with. Digests of the first rules have no "v" and read as 1 (every entity keyed on its extents).</summary>
    [JsonPropertyName("v")] public int Version { get; set; } = 1;

    /// <summary>Per-layer state, keyed by layer name (sorted, ordinal). Absent in digests made before schema 2 and when the layer table could not be read;
    /// its presence, not <see cref="Version"/>, tells a reader the digest carries layer state. Not part of <see cref="Hash"/>.
    /// Layers that come from an external reference (names like "XREF|WALLS") are left out: they change outside the test.</summary>
    [JsonIgnore] public SortedDictionary<string, LayerProps>? LayerState { get; set; }
    /// <summary>The current layer (CLAYER) name; absent in digests made before schema 2.</summary>
    [JsonIgnore] public string? CurrentLayer { get; set; }

    // JSON side of LayerState / CurrentLayer. Read as raw elements so a malformed value can never fail the whole digest: the setters go through
    // the tolerant reader (ReadLayerState / LayerProps.From), which skips what it cannot use.
    [JsonPropertyName("layerState")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? LayerStateJson
    {
        get => LayerState == null ? null : JsonSerializer.SerializeToElement(LayerState);
        set => LayerState = value is { } v ? ReadLayerState(v) : null;
    }

    [JsonPropertyName("currentLayer")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? CurrentLayerJson
    {
        get => CurrentLayer == null ? null : JsonSerializer.SerializeToElement(CurrentLayer);
        set => CurrentLayer = value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    }

    /// <summary>The one tolerant reader of a "layerState" value: null unless it is an object; a layer whose value is not an object is skipped,
    /// a property of the wrong type is left out. Never throws. Keys are ordinal-sorted.</summary>
    public static SortedDictionary<string, LayerProps>? ReadLayerState(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var d = new SortedDictionary<string, LayerProps>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object) d[p.Name] = LayerProps.From(p.Value);
        return d;
    }

    /// <summary>2: TEXT, ATTDEF, MTEXT, INSERT and dimensions are keyed by <see cref="PlacedKey"/>; every other key is unchanged from 1.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Coordinates and lengths are rounded to this many decimals before they enter a key.</summary>
    public const int Decimals = 4;

    /// <summary>Rounded number as text: invariant culture, no "-0".</summary>
    public static string Num(double d)
    {
        var r = Math.Round(d, Decimals, MidpointRounding.AwayFromZero);
        if (r == 0) r = 0; // -0 -> 0
        return r.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>Canonical key of one entity. <paramref name="extents"/> is null when the extents could not be read;
    /// <paramref name="extra"/> is the text, block name or length when the entity has one.</summary>
    public static string Key(string dxf, string layer, (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)? extents, string? extra)
    {
        var ext = extents is { } e
            ? $"{Num(e.MinX)},{Num(e.MinY)},{Num(e.MinZ)}-{Num(e.MaxX)},{Num(e.MaxY)},{Num(e.MaxZ)}"
            : "noext";
        return string.Join("|", dxf, layer, ext, extra ?? "");
    }

    /// <summary>Rounded point "x,y,z".</summary>
    public static string Pt(double x, double y, double z) => $"{Num(x)},{Num(y)},{Num(z)}";

    /// <summary>Canonical key of an entity whose extents include rendered text (v2: TEXT, ATTDEF, MTEXT, INSERT, DIMENSION):
    /// <paramref name="placement"/> is what places and sizes it (points, height, rotation, justification...), <paramref name="extra"/>
    /// its text, tag or block name and attributes. Not its extents: they depend on how AutoCAD renders the font, and the same TEXT
    /// can measure differently from one day to the next on the same PC.</summary>
    public static string PlacedKey(string dxf, string layer, string placement, string extra) => string.Join("|", dxf, layer, placement, extra);

    static readonly HashSet<string> PlacedTypes = new(StringComparer.Ordinal) { "TEXT", "MTEXT", "ATTDEF", "INSERT" };
    static bool IsPlaced(string dxf) => PlacedTypes.Contains(dxf) || dxf.EndsWith("DIMENSION", StringComparison.Ordinal);   // DIMENSION, ARC_DIMENSION, LARGE_RADIAL_DIMENSION

    /// <summary>Digest of the entity keys (with their dxf and layer) and the layer names.</summary>
    public static DrawingDigest Build(IEnumerable<(string Dxf, string Layer, string Key)> entities, IEnumerable<string> layers,
        IEnumerable<KeyValuePair<string, LayerProps>>? layerState = null, string? currentLayer = null)
    {
        var d = new DrawingDigest { Version = CurrentVersion };
        if (layerState != null)
        {
            d.LayerState = new SortedDictionary<string, LayerProps>(StringComparer.Ordinal);
            foreach (var kv in layerState) d.LayerState[kv.Key] = kv.Value;
        }
        d.CurrentLayer = currentLayer;
        var keys = new List<string>();
        foreach (var (dxf, layer, key) in entities)
        {
            d.Count++;
            d.ByType[dxf] = d.ByType.GetValueOrDefault(dxf) + 1;
            d.ByLayer[layer] = d.ByLayer.GetValueOrDefault(layer) + 1;
            keys.Add(key);
        }
        d.Layers = layers.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        d.Hash = ShortHash(keys);
        return d;
    }

    /// <summary>First 16 hex chars of SHA-256 over the keys, sorted ordinally and joined by newlines (order does not matter).</summary>
    public static string ShortHash(IEnumerable<string> keys)
    {
        var sorted = keys.ToList();
        sorted.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sorted)))).ToLowerInvariant()[..16];
    }

    public static DrawingDigest? FromJson(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        try { return e.Deserialize<DrawingDigest>(); }
        catch (JsonException) { return null; }
    }

    /// <summary>Lineweight text for <see cref="LayerProps.Lineweight"/>: millimetres with up to two decimals ("0.25", "0.3", "0") from AutoCAD's
    /// hundredths of a millimetre; the negative special values by name ("ByLayer", "ByBlock", "Default").</summary>
    public static string LineweightText(int hundredthsMm) => hundredthsMm switch
    {
        -1 => "ByLayer",
        -2 => "ByBlock",
        -3 => "Default",
        < 0 => hundredthsMm.ToString(CultureInfo.InvariantCulture),
        _ => (hundredthsMm / 100.0).ToString("0.##", CultureInfo.InvariantCulture),
    };

    /// <summary>"2 LINE, 1 CIRCLE; layers: 0, DIM" for the console and the markdown.</summary>
    public string Summary()
    {
        var types = ByType.Count == 0 ? "empty" : string.Join(", ", ByType.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Value} {t.Key}"));
        return $"{types}; layers: {(Layers.Count == 0 ? "-" : string.Join(", ", Layers))}";
    }

    /// <summary>Human-readable differences, empty when the two digests are equal. Each line starts with what differs:
    /// "LINE: expected 2, got 3", "layer DIM missing", "layer TMP unexpected", "layer 0: expected 2 entities, got 3".
    /// "geometry hash differs" is added only when counts and layers all agree (otherwise the counts already explain it); for an expected
    /// digest of version 1 that contains text, blocks or dimensions it says the checkpoint may be stale (their keys follow the old
    /// rules, so it can no longer match) and to record the test again once the drawing is confirmed right.
    /// Layer state (schema 2) is compared only when <paramref name="compareLayerState"/> is true and the expected digest has it. The caller passes
    /// false for a mark that HAS behavior checks (`Step.Checks != null`): the `layers` behavior owns layer state there. The current layer is never
    /// compared (a test fails on it only through an explicit `sysvars` check of `CLAYER`); the field is still read.</summary>
    public static List<string> Compare(DrawingDigest expected, DrawingDigest actual, bool compareLayerState = true)
    {
        var diffs = new List<string>();
        foreach (var t in expected.ByType.Keys.Union(actual.ByType.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            var ex = expected.ByType.GetValueOrDefault(t);
            var ac = actual.ByType.GetValueOrDefault(t);
            if (ex != ac) diffs.Add($"{t}: expected {ex}, got {ac}");
        }
        foreach (var l in expected.Layers.Except(actual.Layers, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)) diffs.Add($"layer {l} missing");
        foreach (var l in actual.Layers.Except(expected.Layers, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)) diffs.Add($"layer {l} unexpected");
        foreach (var l in expected.ByLayer.Keys.Union(actual.ByLayer.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            var ex = expected.ByLayer.GetValueOrDefault(l);
            var ac = actual.ByLayer.GetValueOrDefault(l);
            if (ex != ac) diffs.Add($"layer {l}: expected {ex} {(ex == 1 ? "entity" : "entities")}, got {ac}");
        }
        if (expected.Count != actual.Count && diffs.Count == 0) diffs.Add($"entity count: expected {expected.Count}, got {actual.Count}");
        if (diffs.Count == 0 && !string.Equals(expected.Hash, actual.Hash, StringComparison.OrdinalIgnoreCase))
            diffs.Add(expected.Version < CurrentVersion && expected.ByType.Keys.Any(IsPlaced)
                ? "geometry hash differs (checkpoint recorded with digest rules v1, which key text, blocks and dimensions differently, " +
                  "so it cannot match any more: check the drawing, then record the test again)"
                : "geometry hash differs");
        // layer state is appended last and does not count as "other diffs" for the hash line above: it is not part of the hash, so a
        // layer-state difference must never hide a geometry difference
        if (compareLayerState) CompareLayerState(expected, actual, diffs);
        return diffs;
    }

    /// <summary>Layer state differences, only when the expected digest has <see cref="LayerState"/> (older checkpoints are skipped silently).
    /// A layer missing on either side is already reported above, a property missing on either side is skipped.
    /// Lines: "layer DIM: expected frozen, got thawed", "layer DIM color: expected 1, got 7".</summary>
    static void CompareLayerState(DrawingDigest expected, DrawingDigest actual, List<string> diffs)
    {
        if (expected.LayerState != null && actual.LayerState != null)
        {
            foreach (var (name, ex) in expected.LayerState)
            {
                if (!actual.LayerState.TryGetValue(name, out var ac)) continue;
                Flag(diffs, name, ex.On, ac.On, "on", "off");
                Flag(diffs, name, ex.Frozen, ac.Frozen, "frozen", "thawed");
                Flag(diffs, name, ex.Locked, ac.Locked, "locked", "unlocked");
                Flag(diffs, name, ex.Plot, ac.Plot, "plottable", "not plottable");
                Text(diffs, name, "color", ex.Color, ac.Color);
                Text(diffs, name, "linetype", ex.Linetype, ac.Linetype);
                Text(diffs, name, "lineweight", ex.Lineweight, ac.Lineweight);
            }
        }
    }

    static void Flag(List<string> diffs, string layer, bool? ex, bool? ac, string whenTrue, string whenFalse)
    {
        if (ex is { } e && ac is { } a && e != a) diffs.Add($"layer {layer}: expected {(e ? whenTrue : whenFalse)}, got {(a ? whenTrue : whenFalse)}");
    }

    static void Text(List<string> diffs, string layer, string what, string? ex, string? ac)
    {
        if (ex != null && ac != null && !string.Equals(ex, ac, StringComparison.Ordinal)) diffs.Add($"layer {layer} {what}: expected {ex}, got {ac}");
    }
}

/// <summary>State of one layer in <see cref="DrawingDigest.LayerState"/>. Every property is optional: one that could not be read is left out
/// and is not compared.</summary>
public sealed class LayerProps
{
    /// <summary>Layer is on (not turned off).</summary>
    [JsonPropertyName("on")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? On { get; set; }
    [JsonPropertyName("frozen")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Frozen { get; set; }
    [JsonPropertyName("locked")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Locked { get; set; }
    /// <summary>ACI number ("7"), "rgb:r,g,b" for true colour, or "Book$Name" for a colour book colour.</summary>
    [JsonPropertyName("color")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Color { get; set; }
    /// <summary>Linetype name ("Continuous", "DASHED").</summary>
    [JsonPropertyName("linetype")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Linetype { get; set; }
    /// <summary>See <see cref="DrawingDigest.LineweightText"/>: millimetres ("0.25") or "ByLayer" / "ByBlock" / "Default".</summary>
    [JsonPropertyName("lineweight")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Lineweight { get; set; }
    /// <summary>Layer is plottable.</summary>
    [JsonPropertyName("plot")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Plot { get; set; }

    /// <summary>Tolerant read of one layer's object: a property of the wrong type is left out (null). Never throws.</summary>
    public static LayerProps From(JsonElement e)
    {
        static bool? B(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        static string? S(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new LayerProps { On = B(e, "on"), Frozen = B(e, "frozen"), Locked = B(e, "locked"), Plot = B(e, "plot"),
            Color = S(e, "color"), Linetype = S(e, "linetype"), Lineweight = S(e, "lineweight") };
    }
}
