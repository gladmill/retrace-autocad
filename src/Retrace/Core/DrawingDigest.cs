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
    public static DrawingDigest Build(IEnumerable<(string Dxf, string Layer, string Key)> entities, IEnumerable<string> layers)
    {
        var d = new DrawingDigest { Version = CurrentVersion };
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
    /// rules, so it can no longer match) and to record the test again once the drawing is confirmed right.</summary>
    public static List<string> Compare(DrawingDigest expected, DrawingDigest actual)
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
        return diffs;
    }
}
