namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The pure parts of reading a dialog's field values: label matching, radio grouping, keys, before/after diff.

public readonly record struct Box(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>One field of a dialog. Kind: edit | check | radio | combo | list | slider | tree | listview. Value: the text; check "on"/"off"/"mixed"; for a radio group the text of the checked radio;
/// for a tree the texts of the selected item and its parents from the top level down, joined with " › " ("" when nothing is selected: see <see cref="TreePath"/>).</summary>
/// <remarks>Tab: the tab the field sits on (<see cref="TabPath"/>: "Lines", "Fit › Text placement"), "" for a field on no tab and in every dialog without tab controls. In a dialog with tabs the
/// key names the tab too (<c>tab:Lines|id:1001</c>), so two pages that reuse a control id are two fields.</remarks>
public sealed record DialogField(string Key, string Label, string Kind, string Value, string Tab = "", IReadOnlyList<ListItem>? Items = null);

/// <summary>A control as the reader saw it, before grouping and keying.
/// Kind "radio" = a Win32 radio button (value "on"/"off"; consecutive radios form a group), "radio1" = a stand-alone radio (UI Automation).
/// Name: the control's own name where the toolkit has one (WinForms <c>Control.Name</c>): stable between runs, unlike WinForms window ids.
/// Tab: the tab it sits on, "" for none.</summary>
public sealed record RawControl(int Id, string Label, string Kind, string Value, bool GroupStart = false, long Parent = 0, string? Name = null, string Tab = "", IReadOnlyList<ListItem>? Items = null);

/// <summary>One field that changed. Tab: the tab it is on ("" = none or not recorded: a recording from before tabs was read).</summary>
public sealed record FieldChange(string Key, string Label, string Kind, string Before, string After, string Tab = "", IReadOnlyList<ListItem>? Items = null);

public static class DialogFieldsCore
{
    public const int MaxValueLength = 200;

    /// <summary>Trees and list views are numbered, keyed and matched among their own sort, so a recording from before they were read keys every other field as it did: 1 tree, 2 list view, 0 the rest.
    /// The one place for <see cref="BuildMapped"/>, <see cref="Qualify"/> and <see cref="FillMatcher.Match"/>.</summary>
    public static int SortOf(string kind) => kind == "tree" ? 1 : kind == ListSelection.Kind ? 2 : 0;
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

    /// <summary>One change in plain words for the pause message: "Set Rows to 5", "Clear Name", "Tick Snap", "Untick Snap", "Choose Metric", "Select A › B in Parts";
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
            case "treecheck":
                return TreeChecks.Describe(c);
            case "tree":
                return c.After.Length == 0 ? $"Clear the selection in {c.Label}" : $"Select {c.After} in {c.Label}";
            case ListSelection.Kind:
                return ListSelection.Sentence(c.After, c.Label, x => x, x => x, c.Items);
            default:
                return c.After.Length == 0 ? $"Clear {c.Label}" : $"Set {c.Label} to {c.After}";
        }
    }

    /// <summary>Labels that occur more than once get the title of the smallest group box around the control: "Length › Precision".
    /// <paramref name="rects"/> parallels <paramref name="raw"/>; a label with no surrounding group box stays as it is.</summary>
    public static List<RawControl> Qualify(IReadOnlyList<RawControl> raw, IReadOnlyList<Box> rects, IReadOnlyList<(Box Rect, string Text)> groups)
    {
        // a tree is counted among trees only: a dialog's other fields are qualified (and so keyed) exactly as before trees were read
        static string Class(RawControl r) => SortOf(r.Kind).ToString() + ":";
        var dup = raw.GroupBy(r => Class(r) + r.Label, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<RawControl>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            string? group = null;
            if (dup.Contains(Class(r) + r.Label) && i < rects.Count)
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
        var items = new List<(int Id, string Label, string Kind, string Value, List<int> Raw, string? Name, string Tab, IReadOnlyList<ListItem>? Sel)>();
        var group = new List<RawControl>();
        var groupIdx = new List<int>();
        void Flush()
        {
            if (group.Count == 0) return;
            var sel = group.FirstOrDefault(r => r.Value == "on");
            items.Add((group[0].Id, string.Join(" / ", group.Select(r => r.Label)), "radio", sel?.Label ?? "", new List<int>(groupIdx), null, group[0].Tab, null));
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
            items.Add((r.Id, r.Label, r.Kind == "radio1" ? "radio" : r.Kind, r.Value, new List<int> { ri }, r.Name, r.Tab, r.Items));
        }
        Flush();

        // Ids, names and occurrence numbers are counted among trees and among the other fields apart: a tree takes no number of another
        // field's label, id or name, so a recording made before trees were read keys its other fields as it did. They are counted per tab too (a dialog
        // without tabs has the one tab "", so nothing changes there): two pages that reuse id 1001 are two fields, `tab:Lines|id:1001` and `tab:Text|id:1001`
        // (a list view is a class of its own too: a recording from before list views were read keys everything else as it did)
        var idCount = new Dictionary<(int, string, int), int>();
        foreach (var i in items) idCount[(SortOf(i.Kind), i.Tab, i.Id)] = idCount.GetValueOrDefault((SortOf(i.Kind), i.Tab, i.Id)) + 1;
        var nameCount = new Dictionary<(int, string, string), int>();
        foreach (var i in items) if (!string.IsNullOrEmpty(i.Name)) nameCount[(SortOf(i.Kind), i.Tab, i.Name)] = nameCount.GetValueOrDefault((SortOf(i.Kind), i.Tab, i.Name)) + 1;
        var labelSeen = new Dictionary<(int, string, string), int>();
        var result = new List<(DialogField, IReadOnlyList<int>)>();
        foreach (var i in items)
        {
            int sort = SortOf(i.Kind);
            var occ = labelSeen[(sort, i.Tab, i.Label)] = labelSeen.GetValueOrDefault((sort, i.Tab, i.Label)) + 1;
            var key = !string.IsNullOrEmpty(i.Name) && nameCount[(sort, i.Tab, i.Name)] == 1 ? $"name:{i.Name}"
                    : i.Id is not (0 or -1 or 0xFFFF) && idCount[(sort, i.Tab, i.Id)] == 1 ? $"id:{i.Id}" : $"label:{i.Label}#{occ}";
            // a list view's value is the set of selected items: never cut (the items are kept beside it)
            result.Add((new DialogField(TabPath.Qualify(key, i.Tab), i.Label, i.Kind, i.Kind == ListSelection.Kind ? i.Value : Clip(i.Value), i.Tab, i.Sel), i.Raw));
        }
        // A list view is numbered among list views only, so its key can equal the key of another field (an unlabelled control with id 0 next to an edit with the same label): the list view is then told
        // by its kind, so the tracker and the matcher keep two fields. Only list views are touched; a tree sharing a key with another field is as it always was, and a key that did not collide is unchanged.
        var others = result.Where(r => r.Item1.Kind != ListSelection.Kind).Select(r => r.Item1.Key).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int n = 0; n < result.Count; n++)
        {
            var (f, idx) = result[n];
            if (f.Kind != ListSelection.Kind) continue;
            var k = f.Key;
            if (others.Contains(k) || !seen.Add(k)) { k += "~" + ListSelection.Kind; seen.Add(k); result[n] = (f with { Key = k }, idx); }
        }
        return result;
    }

    /// <summary>Same value? A list view's selections are compared by their items (the text of the value is for display); anything else by its text.</summary>
    static bool SameValue(DialogField a, DialogField b) =>
        a.Kind == ListSelection.Kind && a.Items != null && b.Items != null
            ? ListSelection.Key(a.Items) == ListSelection.Key(b.Items)   // text and occurrence; the row only of a blank item: a sorted or inserted row is no change
            : string.Equals(a.Value, b.Value, StringComparison.Ordinal);

    /// <summary>Fields whose value differs between the two snapshots (matched by key; fields only in one of them are not changes).</summary>
    public static List<FieldChange> Diff(IReadOnlyList<DialogField> before, IReadOnlyList<DialogField> after)
    {
        var b = new Dictionary<string, DialogField>();
        foreach (var f in before) b[f.Key] = f;
        var changes = new List<FieldChange>();
        foreach (var f in after)
            if (b.TryGetValue(f.Key, out var o) && !SameValue(o, f))
                changes.Add(new FieldChange(f.Key, f.Label, f.Kind, o.Value, f.Value, f.Tab, f.Items));
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

    /// <summary><see cref="Changes"/> as they would be if <paramref name="latest"/> had been observed now, without observing it (nothing is changed: a read that is kept only for the case it is needed).</summary>
    public List<FieldChange> ChangesWith(IEnumerable<DialogField> latest)
    {
        lock (_gate)
        {
            var last = new Dictionary<string, DialogField>(_last);
            foreach (var f in latest) if (_first.ContainsKey(f.Key)) last[f.Key] = f;
            return DialogFieldsCore.Diff(_order.Select(k => _first[k]).ToList(), _order.Select(k => last[k]).ToList());
        }
    }

    /// <summary>Moves the baseline to the values seen last: <see cref="Changes"/> is empty until a value changes again. A dialog button that was
    /// clicked takes the changes made so far with it; what the dialog is changed to afterwards counts from here. Fields seen later still get
    /// their first-seen value as baseline.</summary>
    public void Rebase()
    {
        lock (_gate)
            foreach (var k in _order) _first[k] = _last[k];
    }
}
