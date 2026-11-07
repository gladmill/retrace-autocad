namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The check boxes of a tree (TVS_CHECKBOXES, also WinForms TreeView.CheckBoxes), as dialog fields, and a double-click on a tree item.
//
// Model: one field of kind "treecheck" PER ITEM, not one field per tree. The value is "on" / "off", the key is the tree's own key plus "|check:" plus the item's
// path (`id:1001|check:Parts › Fasteners`), the label is the tree's label, " ¦ ", the path. Only a tick the person changed is a change, so a recording holds just those items
// and the 200-character cap on a value never bites (a tree with hundreds of ticked items as one joined value would). The reader lists the items that exist while it
// reads (the nodes of a collapsed lazily filled node are listed once they exist); an item seen for the first time takes the state it has then as its baseline.
// A recording from before this holds no such field, so nothing about older recordings changes.

public static class TreeChecks
{
    public const string Kind = "treecheck";

    /// <summary>Between a tree's key and an item's path in a key.</summary>
    public const string KeyMark = "|check:";

    /// <summary>Between a tree's label and an item's path in a label.</summary>
    public const string LabelSep = " ¦ ";

    /// <summary>At most this many items of one tree are listed per read (a runaway tree must not make every read slow).</summary>
    public const int MaxItems = 1500;

    public static bool Is(string? kind) => kind == Kind;

    public static string KeyOf(string treeKey, string path) => treeKey + KeyMark + path;
    public static string LabelOf(string treeLabel, string path) => treeLabel + LabelSep + path;

    /// <summary>The tree's key and the item's path of a treecheck key; false for any other key.</summary>
    public static bool TryParseKey(string? key, out string treeKey, out string path)
    {
        treeKey = path = "";
        var at = key?.IndexOf(KeyMark, StringComparison.Ordinal) ?? -1;
        if (key == null || at < 0) return false;
        treeKey = key[..at]; path = key[(at + KeyMark.Length)..];
        return true;
    }

    /// <summary>The tree's label and the item's path of a treecheck label (the whole label as the tree when it has no separator).</summary>
    public static (string Tree, string Path) SplitLabel(string? label)
    {
        label ??= "";
        var at = label.IndexOf(LabelSep, StringComparison.Ordinal);
        return at < 0 ? (label, "") : (label[..at], label[(at + LabelSep.Length)..]);
    }

    /// <summary>The path of a recorded treecheck change: from its key, else from its label.</summary>
    public static string PathOf(FieldChange c) => TryParseKey(c.Key, out _, out var p) ? p : SplitLabel(c.Label).Path;

    /// <summary>The recorded change as a change of the tree it is in (key, label, tab): what the matcher looks the live tree up by.</summary>
    public static FieldChange TreeOf(FieldChange c)
    {
        var treeKey = TryParseKey(c.Key, out var k, out _) ? k : c.Key;
        return new FieldChange(treeKey, SplitLabel(c.Label).Tree, "tree", "", "", c.Tab);
    }

    /// <summary>The state image of an item (TVIS_STATEIMAGEMASK >> 12) as a value: 2 = "on", 1 = "off"; null for an item with no check box (0, or a state image of another use).</summary>
    public static string? ValueOf(int stateImage) => stateImage switch { 2 => "on", 1 => "off", _ => null };

    /// <summary>The items of a tree with their check state, depth first, each as the path of item texts from the top ("Parts › Fasteners"): <paramref name="state"/> gives an item's value
    /// (null = it has no check box and is left out, its children are still walked). Two items with the same path are listed once (the first). At most <see cref="MaxItems"/> items and
    /// <see cref="TreePath.MaxDepth"/> levels; <paramref name="truncated"/> says the cap was hit. <paramref name="children"/> is called with <paramref name="root"/> for the top level.</summary>
    public static List<(string Path, string Value)> Collect<T>(T root, Func<T, IEnumerable<T>> children, Func<T, string> text, Func<T, string?> state, out bool truncated)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool cut = false;
        void Walk(T parent, List<string> trail)
        {
            foreach (var item in children(parent))
            {
                if (list.Count >= MaxItems) { cut = true; return; }
                trail.Add(text(item));
                var path = TreePath.Join(trail);
                if (state(item) is { } v && seen.Add(path)) list.Add((path, v));
                if (trail.Count < TreePath.MaxDepth) Walk(item, trail);
                trail.RemoveAt(trail.Count - 1);
                if (cut) return;
            }
        }
        Walk(root, new List<string>());
        truncated = cut;
        return list;
    }

    public static List<(string Path, string Value)> Collect<T>(T root, Func<T, IEnumerable<T>> children, Func<T, string> text, Func<T, string?> state) =>
        Collect(root, children, text, state, out _);

    /// <summary>How many levels deep a path is.</summary>
    public static int Depth(string path) => TreePath.Split(path).Count;

    /// <summary>The indexes of <paramref name="values"/> in the order to apply them: the ticks go deepest first (a leaf before its parent, so a parent that ticks its children as it is ticked has the
    /// last word and a child ticked after it cannot undo it), and keep the slots the ticks had among the other values, which stay where they were.</summary>
    public static List<int> OrderForApply(IReadOnlyList<FieldChange> values, IEnumerable<int> indexes)
    {
        var order = indexes.ToList();
        var slots = order.Select((v, at) => (v, at)).Where(x => Is(values[x.v].Kind)).Select(x => x.at).ToList();
        var ticks = slots.Select(at => order[at]).OrderByDescending(i => Depth(PathOf(values[i]))).ToList();   // stable
        for (int k = 0; k < slots.Count; k++) order[slots[k]] = ticks[k];
        return order;
    }

    /// <summary>The words of a tick: "Tick `A › B` in **Layers**" with the callers' own markup (<paramref name="tree"/> and <paramref name="path"/> format the two parts); null when it says nothing.</summary>
    public static string? Sentence(string after, string treeLabel, string path, Func<string, string> tree, Func<string, string> pathText) =>
        after == "on" ? $"Tick {pathText(path)} in {tree(treeLabel)}" : after == "off" ? $"Untick {pathText(path)} in {tree(treeLabel)}" : null;

    /// <summary>"Tick A › B in Layers" for the pause message (plain text, no markup).</summary>
    public static string? Describe(FieldChange c) => Sentence(c.After, SplitLabel(c.Label).Tree, PathOf(c), t => t, p => p);
}

/// <summary>Applies the recorded ticks of one tree so that the tree ends as the recording left it, whatever the tree does by itself (a native tree ticks the children of a ticked parent, a program may
/// tick others). Rule: each tick is set (<c>set</c>: null = done, else why not) and the tree read again (<c>read</c>); a recorded item applied before that is not in its recorded state any more is set again
/// (once); an item the recording has no change for that differs from the state it had before the first tick is put back (a person is told when that fails). Ticks are best applied deepest first
/// (<see cref="TreeChecks.OrderForApply"/>). One applier per tree and per segment: its baseline is the tree before its first tick.</summary>
public sealed class TreeCheckApplier
{
    readonly Dictionary<string, bool> _recorded = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> _applied = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<List<(string Path, string Value)>> _read;
    readonly Func<string, bool, string?> _set;
    Dictionary<string, string>? _baseline;

    public TreeCheckApplier(IEnumerable<(string Path, bool On)> recorded, Func<List<(string Path, string Value)>> read, Func<string, bool, string?> set)
    {
        foreach (var (p, on) in recorded) _recorded[p] = on;
        _read = read; _set = set;
    }

    static string Want(bool on) => on ? "on" : "off";

    /// <summary>The items the applier put back or set again beyond the ones it was asked for, in order, for the log.</summary>
    public List<string> Corrections { get; } = new();

    /// <summary>Sets one recorded tick and reconciles the rest: one read of the tree after the set (a second only when something had to be put back, which is then the end state). Null = done, else why not.</summary>
    public string? Apply(string path, bool on)
    {
        _baseline ??= _read().ToDictionary(x => x.Path, x => x.Value, StringComparer.OrdinalIgnoreCase);
        if (_set(path, on) is { } why) return why;
        _applied[path] = on;
        var now = _read();
        bool changed = false;
        foreach (var (p, v) in now)
        {
            if (_applied.TryGetValue(p, out var wanted))
            {
                if (v == Want(wanted)) continue;
                if (_set(p, wanted) is { } e1) return $"{p}: {e1}";
                Corrections.Add(p); changed = true;
            }
            else if (!_recorded.ContainsKey(p) && _baseline.TryGetValue(p, out var before) && v != before)
            {
                if (_set(p, before == "on") is { } e2) return $"{p} was ticked as a side effect of {path} and could not be put back: {e2}";
                Corrections.Add(p); changed = true;
            }
        }
        if (changed) now = _read();   // the end state: every item applied so far is as recorded, every other one as it was
        foreach (var (p, v) in now)
        {
            if (_applied.TryGetValue(p, out var wanted) && v != Want(wanted)) return $"{p} is {v} after {path} was set; the recording has it {Want(wanted)}";
            if (!_applied.ContainsKey(p) && !_recorded.ContainsKey(p) && _baseline.TryGetValue(p, out var before) && v != before) return $"{p} changed to {v} when {path} was set (it was {before})";
        }
        return null;
    }
}

public static class TreeClickRules
{
    /// <summary>A double-click on a tree item is an action of the test unless all it did was open or close a node: the item has children, its expanded state flipped, and nothing else
    /// happened (no field changed, no window opened, the dialog is still there). A double-click on an item without children, or one that changed something, is recorded.</summary>
    public static bool IsAction(bool hasChildren, bool expandedFlipped, bool fieldsChanged, bool windowOpened, bool dialogGone) =>
        windowOpened || dialogGone || fieldsChanged || !hasChildren || !expandedFlipped;
}
