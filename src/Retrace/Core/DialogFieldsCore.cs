namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The pure parts of reading a dialog's field values: label matching, radio grouping, keys, before/after diff.

public readonly record struct Box(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>One field of a dialog. Kind: edit | check | radio | combo | list | slider. Value: the text; check "on"/"off"/"mixed"; for a radio group the text of the checked radio.</summary>
public sealed record DialogField(string Key, string Label, string Kind, string Value);

/// <summary>A control as the reader saw it, before grouping and keying.
/// Kind "radio" = a Win32 radio button (value "on"/"off"; consecutive radios form a group), "radio1" = a stand-alone radio (UI Automation).</summary>
public sealed record RawControl(int Id, string Label, string Kind, string Value, bool GroupStart = false, long Parent = 0);

public sealed record FieldChange(string Key, string Label, string Kind, string Before, string After);

public static class DialogFieldsCore
{
    public const int MaxValueLength = 200;
    /// <summary>A label directly above its control may be at most this far above it (pixels).</summary>
    public const int AboveReach = 30;
    /// <summary>A label left of its control may be at most this far from it (pixels).</summary>
    public const int LeftReach = 400;

    public static string Clip(string? s)
    {
        s ??= "";
        return s.Length <= MaxValueLength ? s : s[..MaxValueLength];
    }

    /// <summary>Removes mnemonic ampersands ("&amp;&amp;" stays one "&amp;") and a trailing ':'; trims.</summary>
    public static string CleanLabel(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '&') { if (i + 1 < s.Length && s[i + 1] == '&') { sb.Append('&'); i++; } continue; }
            sb.Append(s[i]);
        }
        var t = sb.ToString().Trim();
        while (t.EndsWith(':')) t = t[..^1].TrimEnd();
        return t.Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>The label of a control from the static texts around it: the nearest one left of it on the same row (vertical overlap),
    /// else the nearest directly above it (within <see cref="AboveReach"/> px and overlapping it horizontally). Null if none.</summary>
    public static string? FindLabel(Box control, IReadOnlyList<(Box Rect, string Text)> statics)
    {
        string? best = null;
        int bestDist = int.MaxValue;
        foreach (var (r, text) in statics)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            int overlap = Math.Min(r.Bottom, control.Bottom) - Math.Max(r.Top, control.Top);
            if (overlap <= 0 || r.Right > control.Left + 2) continue;
            int dist = control.Left - r.Right;
            if (dist > LeftReach) continue;
            if (dist < bestDist) { bestDist = dist; best = text; }
        }
        if (best != null) return CleanLabel(best) is { Length: > 0 } l ? l : null;
        bestDist = int.MaxValue;
        foreach (var (r, text) in statics)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            int gap = control.Top - r.Bottom;
            if (gap < -2 || gap > AboveReach) continue;
            if (r.Right < control.Left || r.Left > control.Right) continue;
            if (gap < bestDist) { bestDist = gap; best = text; }
        }
        return best == null ? null : CleanLabel(best) is { Length: > 0 } l2 ? l2 : null;
    }

    /// <summary>One change in plain words for the pause message: "Set Rows to 5", "Clear Name", "Tick Snap", "Untick Snap", "Choose Metric";
    /// null when it says nothing (a radio that became "off").</summary>
    public static string? Describe(FieldChange c)
    {
        switch (c.Kind)
        {
            case "check":
                return c.After == "on" ? $"Tick {c.Label}" : c.After == "off" ? $"Untick {c.Label}" : $"Set {c.Label} to {c.After}";
            case "radio":
                var chosen = c.After == "on" ? c.Label : c.After is "off" or "" ? null : c.After;
                return chosen == null ? null : $"Choose {chosen}";
            default:
                return c.After.Length == 0 ? $"Clear {c.Label}" : $"Set {c.Label} to {c.After}";
        }
    }

    /// <summary>Labels that occur more than once get the title of the smallest group box around the control: "Length › Precision".
    /// <paramref name="rects"/> parallels <paramref name="raw"/>; a label with no surrounding group box stays as it is.</summary>
    public static List<RawControl> Qualify(IReadOnlyList<RawControl> raw, IReadOnlyList<Box> rects, IReadOnlyList<(Box Rect, string Text)> groups)
    {
        var dup = raw.GroupBy(r => r.Label, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<RawControl>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            string? group = null;
            if (dup.Contains(r.Label) && i < rects.Count)
            {
                var c = rects[i];
                group = groups.Where(g => g.Rect.Left <= c.Left && g.Rect.Top <= c.Top && g.Rect.Right >= c.Right && g.Rect.Bottom >= c.Bottom)
                    .OrderBy(g => (long)(g.Rect.Right - g.Rect.Left) * (g.Rect.Bottom - g.Rect.Top)).Select(g => g.Text).FirstOrDefault();
            }
            list.Add(group == null ? r : r with { Label = $"{group} › {r.Label}" });
        }
        return list;
    }

    /// <summary>Groups consecutive Win32 radios (a group ends at a WS_GROUP radio, a different parent, or any other control) into one
    /// "radio" field whose value is the text of the checked radio ("" if none), and builds the keys.</summary>
    public static List<DialogField> Build(IReadOnlyList<RawControl> raw) => BuildMapped(raw).Select(x => x.Field).ToList();

    /// <summary>Like <see cref="Build"/>, and says which raw controls (indexes into <paramref name="raw"/>) make up each field
    /// (one, or all radios of a group). The filler uses it to find the live control behind a field.</summary>
    public static List<(DialogField Field, IReadOnlyList<int> Raw)> BuildMapped(IReadOnlyList<RawControl> raw)
    {
        var items = new List<(int Id, string Label, string Kind, string Value, List<int> Raw)>();
        var group = new List<RawControl>();
        var groupIdx = new List<int>();
        void Flush()
        {
            if (group.Count == 0) return;
            var sel = group.FirstOrDefault(r => r.Value == "on");
            items.Add((group[0].Id, string.Join(" / ", group.Select(r => r.Label)), "radio", sel?.Label ?? "", new List<int>(groupIdx)));
            group.Clear(); groupIdx.Clear();
        }
        for (int ri = 0; ri < raw.Count; ri++)
        {
            var r = raw[ri];
            if (r.Kind == "radio")
            {
                if (group.Count > 0 && (r.GroupStart || r.Parent != group[0].Parent)) Flush();
                group.Add(r); groupIdx.Add(ri);
                continue;
            }
            Flush();
            items.Add((r.Id, r.Label, r.Kind == "radio1" ? "radio" : r.Kind, r.Value, new List<int> { ri }));
        }
        Flush();

        var idCount = new Dictionary<int, int>();
        foreach (var i in items) idCount[i.Id] = idCount.GetValueOrDefault(i.Id) + 1;
        var labelSeen = new Dictionary<string, int>();
        var result = new List<(DialogField, IReadOnlyList<int>)>();
        foreach (var i in items)
        {
            var occ = labelSeen[i.Label] = labelSeen.GetValueOrDefault(i.Label) + 1;
            var key = i.Id is not (0 or -1 or 0xFFFF) && idCount[i.Id] == 1 ? $"id:{i.Id}" : $"label:{i.Label}#{occ}";
            result.Add((new DialogField(key, i.Label, i.Kind, Clip(i.Value)), i.Raw));
        }
        return result;
    }

    /// <summary>Fields whose value differs between the two snapshots (matched by key; fields only in one of them are not changes).</summary>
    public static List<FieldChange> Diff(IReadOnlyList<DialogField> before, IReadOnlyList<DialogField> after)
    {
        var b = new Dictionary<string, DialogField>();
        foreach (var f in before) b[f.Key] = f;
        var changes = new List<FieldChange>();
        foreach (var f in after)
            if (b.TryGetValue(f.Key, out var o) && !string.Equals(o.Value, f.Value, StringComparison.Ordinal))
                changes.Add(new FieldChange(f.Key, f.Label, f.Kind, o.Value, f.Value));
        return changes;
    }
}

/// <summary>Remembers, per field key, the first value ever read (the baseline) and the latest one. A field that only becomes visible
/// later (another tab page) gets its first-seen value as baseline. Thread safe: UI Automation reads arrive from a pool thread.</summary>
public sealed class FieldTracker
{
    readonly object _gate = new();
    readonly List<string> _order = new();
    readonly Dictionary<string, DialogField> _first = new(), _last = new();

    /// <summary>Number of fields seen so far.</summary>
    public int ReadCount { get { lock (_gate) return _order.Count; } }

    public void Observe(IEnumerable<DialogField> fields)
    {
        lock (_gate)
            foreach (var f in fields)
            {
                if (!_first.ContainsKey(f.Key)) { _first[f.Key] = f; _order.Add(f.Key); }
                _last[f.Key] = f;
            }
    }

    public List<FieldChange> Changes()
    {
        lock (_gate)
            return DialogFieldsCore.Diff(_order.Select(k => _first[k]).ToList(), _order.Select(k => _last[k]).ToList());
    }
}
