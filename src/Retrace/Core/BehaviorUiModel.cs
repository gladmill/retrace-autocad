namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).
// The logic behind the behavior-check UI (Settings window, Checkpoints window), kept out of the WPF code so the tests reach it.

/// <summary>One line of the behavior list in a window.</summary>
/// <param name="Id">catalog id (or the id from a newer recording)</param>
/// <param name="Ticked">shown checked</param>
/// <param name="Enabled">the user can change it</param>
/// <param name="Source">Checkpoints window: "auto" / "manual" for a verified behavior, null when not verified</param>
/// <param name="Tooltip">why it is disabled, or null</param>
/// <param name="Recordable">Settings window: an opt-in behavior, which has a separate "Record" tick</param>
/// <param name="Recorded">Settings window: that Record tick is set</param>
public sealed record BehaviorRow(string Id, string Label, string Description, bool Ticked, bool Enabled, string? Source = null, string? Tooltip = null,
    bool Recordable = false, bool Recorded = false);

/// <summary>The Settings window's "Behavior checks" group.</summary>
public static class BehaviorSettingsModel
{
    public const string AutoExplanation = "Retrace works out what changed at each checkpoint and verifies exactly that. Turn this off to pick the behaviors yourself. Entity appearance and Paper-space entities are large, so they are recorded only when Record is ticked for them: tick it before recording.";
    public const string ManualExplanation = "Every checkpoint verifies the ticked behaviors in full. Model-space entities are always verified.";
    public const string ModelSpaceLabel = "Model-space entities";
    public const string ModelSpaceDescription = "The objects in model space: always verified.";

    /// <summary>Whether the "check" tick of a row can be edited: only when auto-detect is off. The one predicate for the window and <see cref="Rows"/>.
    /// (The "Record" tick of an opt-in row is editable in both modes.)</summary>
    public static bool CheckEnabled(bool autoDetect) => !autoDetect;

    /// <summary>Every catalog behavior, in catalog order. Auto-detect on: all shown disabled (ticks still show the stored list, so turning
    /// auto-detect off again brings back the choice). Off: enabled, ticked = in <paramref name="checkBehaviors"/> (ids match without case).</summary>
    public static List<BehaviorRow> Rows(bool autoDetect, IEnumerable<string>? checkBehaviors, IEnumerable<string>? recordBehaviors = null)
    {
        var recorded = new HashSet<string>(new RetraceSettings { RecordBehaviors = (recordBehaviors ?? Enumerable.Empty<string>()).ToList() }.EffectiveRecordBehaviors(), StringComparer.Ordinal);
        var ticked = new HashSet<string>(new RetraceSettings { CheckBehaviors = (checkBehaviors ?? Enumerable.Empty<string>()).ToList() }.EffectiveCheckBehaviors(), StringComparer.Ordinal);
        return BehaviorCatalog.All.Select(b => new BehaviorRow(b.Id, b.Label, b.Description, ticked.Contains(b.Id), CheckEnabled(autoDetect), null, null,
            BehaviorCatalog.IsOptIn(b.Id), recorded.Contains(b.Id))).ToList();
    }

    /// <summary>The setting value from the ticks: the catalog ids that are ticked, in catalog order, then every id in <paramref name="existing"/>
    /// that is not a catalog id (from a newer build or edited by hand) exactly as it was, so saving the window does not lose it.</summary>
    public static List<string> FromTicks(IEnumerable<string> tickedIds, IEnumerable<string>? existing = null)
    {
        var set = new HashSet<string>(tickedIds, StringComparer.OrdinalIgnoreCase);
        var result = BehaviorCatalog.All.Where(b => set.Contains(b.Id)).Select(b => b.Id).ToList();
        result.AddRange((existing ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id) && BehaviorCatalog.Find(id.Trim(), true) == null));
        return result;
    }

    public static string Explanation(bool autoDetect) => autoDetect ? AutoExplanation : ManualExplanation;
}

/// <summary>Edits to the system-variable lists (settings `sysvarsAdd` / `sysvarsRemove`) as the "Edit system variables" dialog makes them.
/// The effective list is <see cref="SysVarDefaults.Default"/> + add - remove, without case. Every edit keeps the two lists minimal:
/// a name is in `add` only if it is not a default, in `remove` only if it is.</summary>
public static class SysvarListEdit
{
    /// <summary>A valid name: letters, digits, `_` and `$`, 1 to 64 characters. Returns it in upper case, or null with <paramref name="error"/> set.</summary>
    public static string? CleanName(string? text, out string error)
    {
        var t = (text ?? "").Trim().ToUpperInvariant();
        error = "";
        if (t.Length == 0) { error = "Type a system variable name."; return null; }
        if (t.Length > 64 || !t.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '$'))
        { error = "A system variable name has letters, digits, _ and $ only."; return null; }
        return t;
    }

    static bool IsDefault(string name) => SysVarDefaults.Default.Contains(name, StringComparer.OrdinalIgnoreCase);
    static bool In(List<string> l, string name) => l.Any(x => string.Equals(x?.Trim(), name, StringComparison.OrdinalIgnoreCase));
    static void Drop(List<string> l, string name) => l.RemoveAll(x => string.Equals(x?.Trim(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Puts a name on the effective list. False (with <paramref name="message"/>) when the name is invalid or already on it.</summary>
    public static bool Add(List<string> add, List<string> remove, string? text, out string message)
    {
        var name = CleanName(text, out message);
        if (name == null) return false;
        if (SysVarDefaults.Effective(add, remove).Contains(name, StringComparer.OrdinalIgnoreCase)) { message = name + " is already on the list."; return false; }
        Drop(remove, name);
        if (!IsDefault(name) && !In(add, name)) add.Add(name);
        message = "";
        return true;
    }

    /// <summary>Takes a name off the effective list. False when it is not on it.</summary>
    public static bool Remove(List<string> add, List<string> remove, string? text)
    {
        var name = (text ?? "").Trim();
        if (!SysVarDefaults.Effective(add, remove).Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        Drop(add, name);
        if (IsDefault(name) && !In(remove, name)) remove.Add(name.ToUpperInvariant());
        return true;
    }

    /// <summary>Back to the default list.</summary>
    public static void Reset(List<string> add, List<string> remove) { add.Clear(); remove.Clear(); }

    /// <summary>"31 variables (2 added, 1 removed)".</summary>
    public static string Summary(IEnumerable<string>? add, IEnumerable<string>? remove)
    {
        var n = SysVarDefaults.Effective(add, remove).Count;
        var a = (add ?? Enumerable.Empty<string>()).Count(s => !string.IsNullOrWhiteSpace(s) && !IsDefault(s.Trim()));
        var r = (remove ?? Enumerable.Empty<string>()).Count(s => !string.IsNullOrWhiteSpace(s) && IsDefault(s.Trim()));
        var extra = new List<string>();
        if (a > 0) extra.Add($"{a} added");
        if (r > 0) extra.Add($"{r} removed");
        return $"{n} variable{(n == 1 ? "" : "s")}" + (extra.Count > 0 ? $" ({string.Join(", ", extra)})" : " (the default list)");
    }
}

/// <summary>What the user changed in one checkpoint's `checks`: behavior id -> the new check, or null = take it out. Only touched behaviors are
/// listed; every other behavior of the mark line keeps its JSON exactly as it was.</summary>
public sealed class CheckEdits : Dictionary<string, BehaviorCheck?> { }

/// <summary>The Checkpoints window's per-checkpoint behavior list and what ticking does. One editor per checkpoint: it remembers the check each
/// behavior had when the mark was read (or last saved), so re-ticking a behavior that was taken off restores that exact check; a behavior that
/// was not verified is added as manual / all from the stored snapshot, which is read lazily on first need and cached.</summary>
public sealed class CheckpointChecksEditor
{
    public const string NoChecksText = "No behavior checks (older recording).";
    public const string NoSnapshotTip = "This recording has no stored data for this behavior at this checkpoint (older recording, or it could not be read when recording), so it cannot be added.";
    public const string NotRecordedTip = "Not ticked under Record in Settings when this was recorded, so this checkpoint has no stored data for it and it cannot be added. (Otherwise: older recording, or it could not be read when recording.)";

    readonly string _mode;
    Dictionary<string, BehaviorCheck> _baseline;           // as on disk
    readonly Dictionary<string, BehaviorCheck> _current;   // as edited; a missing key = not verified
    readonly Lazy<BehaviorSnapshot?> _snapshot;

    /// <summary>Set by <see cref="Tick"/> / <see cref="MarkSaved"/>: whether the edits differ from what is on disk. No serializing.</summary>
    public bool Dirty { get; private set; }

    /// <param name="loadSnapshot">reads the checkpoint's stored snapshot (null when missing); called at most once, when first needed</param>
    public CheckpointChecksEditor(BehaviorChecks original, Func<BehaviorSnapshot?> loadSnapshot)
    {
        _mode = original.Mode;
        _baseline = new Dictionary<string, BehaviorCheck>(original.Behaviors, StringComparer.Ordinal);
        _current = new Dictionary<string, BehaviorCheck>(_baseline, StringComparer.Ordinal);
        _snapshot = new Lazy<BehaviorSnapshot?>(loadSnapshot);
    }

    public bool SnapshotLoaded => _snapshot.IsValueCreated;
    public BehaviorSnapshot? Snapshot => _snapshot.Value;

    /// <summary>The checks as edited (for the summary line).</summary>
    public BehaviorChecks Current()
    {
        var c = new BehaviorChecks { Mode = _mode };
        foreach (var kv in _current) c.Behaviors[kv.Key] = kv.Value;
        return c;
    }

    public string Summary() => Summary(Current());

    /// <summary>"Layer state (auto), System variables (manual)", or "none besides the entities".</summary>
    public static string Summary(BehaviorChecks checks) =>
        checks.IsEmpty ? "none besides the entities"
        : string.Join(", ", checks.Behaviors.Select(kv => $"{BehaviorCatalog.LabelOf(kv.Key)} ({kv.Value.Source})"));

    /// <summary>One row per catalog behavior, then the ids this build does not know that the mark holds (listed for good, so an untick can be
    /// undone). An unverified behavior is enabled when the mark originally had it or the stored snapshot holds it; this reads the snapshot.</summary>
    public List<BehaviorRow> Rows()
    {
        var rows = new List<BehaviorRow>();
        var snap = Snapshot;
        foreach (var b in BehaviorCatalog.All)
        {
            if (_current.TryGetValue(b.Id, out var c)) { rows.Add(new BehaviorRow(b.Id, b.Label, b.Description, true, true, c.Source)); continue; }
            var can = _baseline.ContainsKey(b.Id) || (snap != null && snap.Has(b.Id));
            // the Record hint only when there is a snapshot and it lacks the behavior; no snapshot file at all is the general tip
            var notRecorded = BehaviorCatalog.IsOptIn(b.Id) && snap != null && !snap.Has(b.Id);
            rows.Add(new BehaviorRow(b.Id, b.Label, b.Description, false, can, null, can ? null : notRecorded ? NotRecordedTip : NoSnapshotTip));
        }
        foreach (var id in UnknownIds())
        {
            _current.TryGetValue(id, out var c);
            rows.Add(new BehaviorRow(id, id, "Not known to this version of Retrace; it is kept and not verified here.", c != null, true, c?.Source));
        }
        return rows;
    }

    /// <summary>The ids on this mark that this build does not know, ordinal order (the rows for them never go away).</summary>
    public List<string> UnknownIds() =>
        _baseline.Keys.Concat(_current.Keys).Where(id => !BehaviorCatalog.IsKnown(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>One tick. Unticking takes the behavior off. Ticking restores the check the mark originally had, else adds the behavior as
    /// manual / all from the stored snapshot. False when ticking was not possible (no stored data); nothing changes then.</summary>
    public bool Tick(string behaviorId, bool ticked)
    {
        if (!ticked) _current.Remove(behaviorId);
        else if (!_current.ContainsKey(behaviorId))
        {
            if (_baseline.TryGetValue(behaviorId, out var original)) _current[behaviorId] = original;
            else
            {
                var snap = Snapshot;
                if (snap == null || !snap.Has(behaviorId)) return false;
                _current[behaviorId] = new BehaviorChecks().WithManual(behaviorId, snap).Behaviors[behaviorId];
            }
        }
        Dirty = Edits().Count > 0;
        return true;
    }

    /// <summary>The behaviors whose check differs from disk (reference comparison: a restored original is no edit).</summary>
    public CheckEdits Edits()
    {
        var e = new CheckEdits();
        foreach (var id in _baseline.Keys.Concat(_current.Keys).Distinct(StringComparer.Ordinal))
        {
            _baseline.TryGetValue(id, out var b);
            _current.TryGetValue(id, out var c);
            if (!ReferenceEquals(b, c)) e[id] = c;
        }
        return e;
    }

    /// <summary>The edits that were sent are on disk now: they become the baseline.</summary>
    public void MarkSaved(CheckEdits sent)
    {
        _baseline = new Dictionary<string, BehaviorCheck>(_baseline, StringComparer.Ordinal);
        foreach (var (id, c) in sent) { if (c == null) _baseline.Remove(id); else _baseline[id] = c; }
        Dirty = Edits().Count > 0;
    }
}
