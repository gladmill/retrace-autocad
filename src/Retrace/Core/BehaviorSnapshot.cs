using System.Text.Json;
using System.Text.Json.Serialization;
using Retrace.Engine.Recordings;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// What the drawing holds for each behavior at one moment: behavior id -> (item key -> canonical value), everything ordinal-sorted.
/// A behavior that is not in the snapshot was not captured (could not be read): that is different from "captured, no items".
/// Item keys are opaque strings to Core (entity keys are the digest keys with `#2`, `#3`... for duplicates, never handles).
/// </summary>
public sealed class BehaviorSnapshot
{
    public const int FormatVersion = 1;

    [JsonPropertyName("v")] public int Version { get; set; } = FormatVersion;
    [JsonPropertyName("behaviors")] public SortedDictionary<string, SortedDictionary<string, string>> Behaviors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Sets (replaces) the items of one behavior, marking it as captured. Capture code calls this once per behavior.</summary>
    public void Set(string behaviorId, IEnumerable<KeyValuePair<string, string>> items)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in items) d[kv.Key] = kv.Value;
        Behaviors[behaviorId] = d;
    }

    public bool Has(string behaviorId) => Behaviors.ContainsKey(behaviorId);

    public SortedDictionary<string, string>? Items(string behaviorId) => Behaviors.GetValueOrDefault(behaviorId);

    /// <summary>The one set of JSON options for snapshots and checks (compact, readable characters unescaped).</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Tolerant read: null unless the text is a JSON object; a behavior that is not an object and an item that is not a string are skipped.
    /// Never throws.</summary>
    public static BehaviorSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { using var d = JsonDocument.Parse(json); return FromJson(d.RootElement); }
        catch (JsonException) { return null; }
    }

    public static BehaviorSnapshot? FromJson(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var s = new BehaviorSnapshot { Version = e.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : FormatVersion };
        if (e.TryGetProperty("behaviors", out var bs) && bs.ValueKind == JsonValueKind.Object)
            foreach (var b in bs.EnumerateObject())
            {
                if (b.Value.ValueKind != JsonValueKind.Object) continue;
                var items = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var it in b.Value.EnumerateObject())
                    if (it.Value.ValueKind == JsonValueKind.String) items[it.Name] = it.Value.GetString() ?? "";
                s.Behaviors[b.Name] = items;
            }
        return s;
    }

    // ---- files beside the recording: snapshots\start.json, snapshots\mark-<n>.json ----

    public static string Dir(string testFolder) => Path.Combine(testFolder, TestFolders.SnapshotsDir);
    public static string StartPath(string testFolder) => Path.Combine(Dir(testFolder), "start.json");
    /// <param name="n">the `n` of the `mark` event</param>
    public static string MarkPath(string testFolder, int n) => Path.Combine(Dir(testFolder), $"mark-{n}.json");

    /// <summary>Writes the snapshot (temp file, then move). Returns false and the error instead of throwing.</summary>
    public bool Write(string path, out string error)
    {
        try
        {
            AtomicFile.WriteText(path, ToJson());
            error = "";
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>Reads a snapshot file; null when it is missing or broken. Never throws.</summary>
    public static BehaviorSnapshot? Read(string path)
    {
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null; }
        catch (Exception) { return null; }
    }

    // ---- diff ----

    /// <summary>Item keys that differ between two snapshots, per behavior captured in BOTH. A behavior missing on either side is not in the result
    /// (it was not captured, which is not "everything removed").</summary>
    public static SortedDictionary<string, BehaviorDiff> Diff(BehaviorSnapshot previous, BehaviorSnapshot current)
    {
        var result = new SortedDictionary<string, BehaviorDiff>(StringComparer.Ordinal);
        foreach (var (id, cur) in current.Behaviors)
        {
            if (!previous.Behaviors.TryGetValue(id, out var prev)) continue;
            var d = new BehaviorDiff();
            foreach (var (k, v) in cur)
            {
                if (!prev.TryGetValue(k, out var pv)) d.Added.Add(k);
                else if (!string.Equals(pv, v, StringComparison.Ordinal)) d.Changed.Add(k);
            }
            foreach (var k in prev.Keys) if (!cur.ContainsKey(k)) d.Removed.Add(k);
            result[id] = d;
        }
        return result;
    }
}

/// <summary>Item keys of one behavior that were added, removed or changed (each list ordinal-sorted).</summary>
public sealed class BehaviorDiff
{
    public List<string> Added { get; } = new();
    public List<string> Removed { get; } = new();
    public List<string> Changed { get; } = new();
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}
