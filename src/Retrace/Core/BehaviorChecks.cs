using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>What one checkpoint verifies for one behavior.</summary>
public sealed class BehaviorCheck
{
    public const string SourceAuto = "auto", SourceManual = "manual", ScopeItems = "items", ScopeAll = "all";

    /// <summary>"auto" (picked from what changed) or "manual" (picked by a person or the settings).</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = SourceAuto;
    /// <summary>"items": only the listed items must match (a null value = must be absent). "all": the behavior's whole item map must equal
    /// <see cref="Items"/> exactly (missing, unexpected and changed items all fail).</summary>
    [JsonPropertyName("scope")] public string Scope { get; set; } = ScopeItems;
    /// <summary>item key -> canonical value, or null for "must be absent". Ordinal-sorted.</summary>
    [JsonPropertyName("items")] public SortedDictionary<string, string?> Items { get; set; } = new(StringComparer.Ordinal);

    public BehaviorCheck Clone() => new() { Source = Source, Scope = Scope, Items = new SortedDictionary<string, string?>(Items, StringComparer.Ordinal) };
}

/// <summary>
/// The `checks` of a `mark` line: which behaviors the checkpoint verifies and the expected data. Built by <see cref="AutoPick"/> or
/// <see cref="ManualPick"/> when recording, changed per checkpoint by <see cref="WithManual"/> / <see cref="Without"/>, compared at replay with
/// <see cref="Compare"/>. Immutable by convention: the With... methods return a copy.
/// </summary>
public sealed class BehaviorChecks
{
    public const string ModeAuto = "auto", ModeManual = "manual";

    [JsonPropertyName("mode")] public string Mode { get; set; } = ModeAuto;
    [JsonPropertyName("behaviors")] public SortedDictionary<string, BehaviorCheck> Behaviors { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore] public bool IsEmpty => Behaviors.Count == 0;

    public BehaviorChecks Clone()
    {
        var c = new BehaviorChecks { Mode = Mode };
        foreach (var (id, b) in Behaviors) c.Behaviors[id] = b.Clone();
        return c;
    }

    // ---- picking (recording time) ----

    /// <summary>Auto-detect: every behavior captured in both snapshots whose items changed gets `source auto, scope items`; items = added and
    /// changed with the current value, removed with null. <paramref name="previous"/> is the previous checkpoint's snapshot, or the start
    /// snapshot for the first one; null (none captured) gives no checks. Mode is "auto".
    /// `appearance` is picked differently: an added item (a new or moved entity) and a changed item are kept with their own fields only (the
    /// resolved `r...` fields, which are the layer's and belong to `layers`, are left out); a changed item whose difference is only in the resolved
    /// fields is dropped, and so is a removed item (the digest and `paperspace` own what an entity is and where). So a layer color change adds no
    /// `appearance` item, a new red hatch does, and a MOVE adds small items for the moved entities. More than <see cref="MaxAutoAppearanceItems"/>
    /// items leave `appearance` out of that checkpoint (reported to <paramref name="log"/>).</summary>
    public static BehaviorChecks AutoPick(BehaviorSnapshot? previous, BehaviorSnapshot current, Action<string>? log = null)
    {
        var checks = new BehaviorChecks { Mode = ModeAuto };
        if (previous == null) return checks;
        foreach (var (id, diff) in BehaviorSnapshot.Diff(previous, current))
        {
            if (diff.IsEmpty) continue;
            var cur = current.Behaviors[id];
            var check = new BehaviorCheck { Source = BehaviorCheck.SourceAuto, Scope = BehaviorCheck.ScopeItems };
            if (id == BehaviorCatalog.Appearance)
            {
                var prev = previous.Behaviors[id];
                foreach (var k in diff.Added) check.Items[k] = BehaviorKeys.OwnLook(cur[k]);
                foreach (var k in diff.Changed)
                    if (OwnLookChanged(prev[k], cur[k])) check.Items[k] = BehaviorKeys.OwnLook(cur[k]);
                if (check.Items.Count == 0) continue;
                if (check.Items.Count > MaxAutoAppearanceItems)
                {
                    // a mass edit (a big MOVE, a drawing opened mid-recording) would store thousands of items in the recording: not checked at this checkpoint
                    log?.Invoke($"appearance: {check.Items.Count} items over the {MaxAutoAppearanceItems} limit, not checked at this checkpoint");
                    continue;
                }
            }
            else
            {
                foreach (var k in diff.Added.Concat(diff.Changed)) check.Items[k] = cur[k];
                foreach (var k in diff.Removed) check.Items[k] = null;
            }
            checks.Behaviors[id] = check;
        }
        return checks;
    }

    /// <summary>Most `appearance` items an auto pick stores for one checkpoint; above it the behavior is left out of that checkpoint's checks.</summary>
    public const int MaxAutoAppearanceItems = 2000;

    /// <summary>Do two `appearance` values differ in a field other than the resolved ones (`rcolor`, `rlinetype`, `rlineweight`, `rtransparency`)?
    /// A value that is not canonical counts as different.</summary>
    static bool OwnLookChanged(string before, string after)
    {
        if (!CanonicalValue.TryParse(before, out var b) || !CanonicalValue.TryParse(after, out var a) || a == null || b == null) return true;
        static Dictionary<string, string> Own(List<(string Name, string Value)> f) =>
            f.Where(x => !BehaviorKeys.IsResolvedField(x.Name)).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        var ob = Own(b); var oa = Own(a);
        return ob.Count != oa.Count || ob.Any(kv => !oa.TryGetValue(kv.Key, out var v) || v != kv.Value);
    }

    /// <summary>Manual: each chosen behavior id (duplicates and unknown ids ignored) that the current snapshot captured gets
    /// `source manual, scope all` with the snapshot's full item map. A chosen behavior the snapshot could not read is left out. Mode is "manual".</summary>
    public static BehaviorChecks ManualPick(IEnumerable<string> chosen, BehaviorSnapshot current)
    {
        var checks = new BehaviorChecks { Mode = ModeManual };
        foreach (var id in chosen)
            if (BehaviorCatalog.IsKnown(id) && !checks.Behaviors.ContainsKey(id) && current.Behaviors.TryGetValue(id, out var items))
                checks.Behaviors[id] = FullCheck(items);
        return checks;
    }

    static BehaviorCheck FullCheck(IEnumerable<KeyValuePair<string, string>> items)
    {
        var c = new BehaviorCheck { Source = BehaviorCheck.SourceManual, Scope = BehaviorCheck.ScopeAll };
        foreach (var kv in items) c.Items[kv.Key] = kv.Value;
        return c;
    }

    // ---- per-checkpoint override (Checkpoints window) ----

    /// <summary>A copy with <paramref name="behaviorId"/> verified as manual / all from <paramref name="stored"/> (the checkpoint's stored snapshot,
    /// `snapshots/mark-n.json`), replacing any check it had. If the snapshot never captured that behavior, the copy is unchanged.</summary>
    public BehaviorChecks WithManual(string behaviorId, BehaviorSnapshot stored)
    {
        var c = Clone();
        if (stored.Behaviors.TryGetValue(behaviorId, out var items)) c.Behaviors[behaviorId] = FullCheck(items);
        return c;
    }

    /// <summary>A copy without the check for <paramref name="behaviorId"/>.</summary>
    public BehaviorChecks Without(string behaviorId)
    {
        var c = Clone();
        c.Behaviors.Remove(behaviorId);
        return c;
    }

    // ---- JSON ----

    static readonly JsonSerializerOptions Json = BehaviorSnapshot.JsonOptions;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Tolerant read of a `checks` value: null unless it is an object. A behavior that is not an object is skipped; an unknown scope or
    /// source falls back to "items" / "manual"; items that are neither string nor null are skipped. Never throws.</summary>
    public static BehaviorChecks? FromJson(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var c = new BehaviorChecks
        {
            Mode = e.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == ModeManual ? ModeManual : ModeAuto,
        };
        if (e.TryGetProperty("behaviors", out var bs) && bs.ValueKind == JsonValueKind.Object)
            foreach (var b in bs.EnumerateObject())
            {
                if (b.Value.ValueKind != JsonValueKind.Object) continue;
                var check = new BehaviorCheck
                {
                    Source = Str(b.Value, "source") == BehaviorCheck.SourceManual ? BehaviorCheck.SourceManual : BehaviorCheck.SourceAuto,
                    Scope = Str(b.Value, "scope") == BehaviorCheck.ScopeAll ? BehaviorCheck.ScopeAll : BehaviorCheck.ScopeItems,
                };
                if (b.Value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                    foreach (var it in items.EnumerateObject())
                    {
                        if (it.Value.ValueKind == JsonValueKind.String) check.Items[it.Name] = it.Value.GetString() ?? "";
                        else if (it.Value.ValueKind == JsonValueKind.Null) check.Items[it.Name] = null;
                    }
                c.Behaviors[b.Name] = check;
            }
        return c;
    }

    public static BehaviorChecks? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { using var d = JsonDocument.Parse(json); return FromJson(d.RootElement); }
        catch (JsonException) { return null; }
    }

    static string? Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- replay compare ----

    /// <summary>The replay entry point: readable difference lines between what the checkpoint expects and what the drawing holds now; empty = pass.
    /// <paramref name="actual"/> must be captured for (at least) the behaviors in <paramref name="expected"/>; a behavior it lacks gives one line
    /// "&lt;label&gt;: could not be read". A behavior id this build does not know is skipped. Lines: `sysvar CLAYER: expected DIM, got 0`, `layer DIM frozen: expected 1, got 0`,
    /// `xdata &lt;id&gt;|ACAD missing`, `symbols STYLE:Notes unexpected`, `layout Sheet1 count: expected 4, got 5`. For canonical values
    /// (<see cref="BehaviorInfo.Canonical"/>) the differing fields are named; other values are shown whole. Ordered by behavior id, then item key.</summary>
    public static List<string> Compare(BehaviorChecks expected, BehaviorSnapshot actual)
    {
        var lines = new List<string>();
        foreach (var (id, check) in expected.Behaviors)
        {
            var info = BehaviorCatalog.Find(id);
            if (info == null) continue;   // an id from a newer build: not understood here, so not verified (and never a failure)
            if (!actual.Behaviors.TryGetValue(id, out var act)) { lines.Add($"{BehaviorCatalog.LabelOf(id)}: could not be read"); continue; }
            var noun = info.Noun;
            var canonical = info.Canonical;
            // Fields that older recordings do not have, or have with another meaning (see FieldRules): only these are tolerated, nothing else.
            var rules = FieldRules.For(id);
            foreach (var (key, want) in check.Items)
            {
                var present = act.TryGetValue(key, out var got);
                // the sysvar list is the user's and can change after recording: a variable that is no longer read (or unknown here) is not verified
                if (id == BehaviorCatalog.SysVars && !present) continue;
                if (want == null) { if (present) lines.Add($"{noun} {key} unexpected"); }
                else if (!present) lines.Add($"{noun} {key} missing");
                else if (!string.Equals(want, got, StringComparison.Ordinal)) AddValueDiff(lines, noun, key, want, got!, canonical, rules);
            }
            if (check.Scope == BehaviorCheck.ScopeAll && id != BehaviorCatalog.SysVars)   // sysvars: names that were not recorded are ignored
                foreach (var key in act.Keys)
                    if (!check.Items.ContainsKey(key)) lines.Add($"{noun} {key} unexpected");
        }
        return lines;
    }

    /// <summary>The only tolerance of <see cref="Compare"/> for older recordings, by behavior. <c>Dropped</c>: a field a recording may still hold that
    /// is no longer read (viewports `on`: it read false until the layout had been current in the session, so its recorded value means nothing now);
    /// it is ignored. <c>New</c>: a field older recordings do not have (layouts `psltscale`, viewports `off`): skipped when the recording lacks it;
    /// when the recording has it and this capture does not (entget unavailable), the line is `could not be read`, never a quiet pass.
    /// Every other field is strict, including `expected (none)` lines.</summary>
    sealed class FieldRules(string[] dropped, string[] added, string[]? unrecorded = null)
    {
        /// <summary><c>Unrecorded</c>: a field an auto pick leaves out on purpose (appearance `r...`: the layer's values). Skipped when the recording lacks
        /// it; when the recording has it (a manual pick) it is compared like any other field.</summary>
        public readonly string[] Dropped = dropped, New = added, Unrecorded = unrecorded ?? Array.Empty<string>();
        public bool IsUnrecorded(string field) => Array.IndexOf(Unrecorded, field) >= 0;
        static readonly FieldRules None = new(Array.Empty<string>(), Array.Empty<string>());
        public static FieldRules For(string behaviorId) => behaviorId switch
        {
            BehaviorCatalog.Layouts => new(Array.Empty<string>(), new[] { "psltscale" }),
            BehaviorCatalog.Viewports => new(new[] { "on" }, new[] { "off" }),
            BehaviorCatalog.Appearance => new(Array.Empty<string>(), Array.Empty<string>(), new[] { "rcolor", "rlinetype", "rlineweight", "rtransparency" }),
            _ => None,
        };
        public bool IsDropped(string field) => Array.IndexOf(Dropped, field) >= 0;
        public bool IsNew(string field) => Array.IndexOf(New, field) >= 0;
    }

    static void AddValueDiff(List<string> lines, string noun, string key, string want, string got, bool canonical, FieldRules rules)
    {
        if (canonical && CanonicalValue.TryParse(want, out var wf) && CanonicalValue.TryParse(got, out var gf) && wf != null && gf != null)
        {
            var before = lines.Count;
            var tolerated = false;
            var gotBy = gf.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            var wantBy = wf.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            foreach (var (name, w) in wf.DistinctBy(f => f.Name))
            {
                if (rules.IsDropped(name)) { tolerated = true; continue; }
                if (!gotBy.TryGetValue(name, out var g))
                    lines.Add(rules.IsNew(name) ? $"{noun} {key} {name}: could not be read" : $"{noun} {key} {name}: expected {w}, got (none)");
                else if (g != w) lines.Add($"{noun} {key} {name}: expected {w}, got {g}");
            }
            foreach (var (name, g) in gf.DistinctBy(f => f.Name))
            {
                if (wantBy.ContainsKey(name)) continue;
                if (rules.IsNew(name) || rules.IsUnrecorded(name)) tolerated = true;   // an older recording, or left out by the auto pick: not checked
                else lines.Add($"{noun} {key} {name}: expected (none), got {g}");
            }
            if (lines.Count > before) return;
            if (tolerated) return;   // the values differ only by fields that are tolerated: not a difference
        }
        lines.Add($"{noun} {key}: expected {want}, got {got}");
    }
}
