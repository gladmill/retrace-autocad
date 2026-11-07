namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The pure parts of a tree field: the selected item as a path of item texts from the top level down ("Parts › Fasteners › Bolts"),
// comparing two paths, and finding a path in a tree that is only known through a children function (the Win32 reader and filler hand it
// the TVM messages; the tests hand it a dictionary).

/// <summary>Where a path led. <see cref="Items"/> holds the item found at each level (all of them when <see cref="Problem"/> is null, else the levels
/// before the one that was not found); <see cref="Problem"/> says in a sentence what was not found.</summary>
public sealed record TreeFound<T>(IReadOnlyList<T> Items, string? Problem);

public static class TreePath
{
    /// <summary>Between the texts of two levels: as the paths of a native palette's tree click are shown.</summary>
    public const string Separator = " › ";

    /// <summary>More levels than this are not followed (a loop in a broken tree would never end).</summary>
    public const int MaxDepth = 64;

    /// <summary>The path of the item whose texts from the top level down are <paramref name="texts"/>; "" for no texts (nothing is selected).</summary>
    public static string Join(IEnumerable<string?> texts) => string.Join(Separator, texts.Select(t => (t ?? "").Trim()));

    /// <summary>The levels of a path, trimmed; none for "" or white space.</summary>
    public static List<string> Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new List<string>();
        return path.Split(Separator).Select(s => s.Trim()).ToList();
    }

    /// <summary>Are two item texts the same? Trimmed, case-insensitive.</summary>
    public static bool SameText(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Are two paths the same, level by level (trimmed, case-insensitive)? Two empty paths are.</summary>
    public static bool Same(string? a, string? b)
    {
        var x = Split(a);
        var y = Split(b);
        if (x.Count != y.Count) return false;
        for (int i = 0; i < x.Count; i++) if (!SameText(x[i], y[i])) return false;
        return true;
    }

    /// <summary>Walks <paramref name="path"/> down from the top: at each level the first child of the previous item whose text is the level's text.
    /// <paramref name="expand"/> is called on an item before its children are listed (a tree that fills a node when it is opened); it is not called
    /// for <paramref name="root"/>, the stand-in for "the tree itself" whose children are the top-level items.</summary>
    public static TreeFound<T> Find<T>(IReadOnlyList<string> path, T root, Func<T, IEnumerable<T>> children, Func<T, string> text, Action<T>? expand = null)
    {
        var found = new List<T>();
        var parent = root;
        for (int d = 0; d < path.Count && d < MaxDepth; d++)
        {
            if (d > 0) expand?.Invoke(parent);
            bool hit = false;
            foreach (var item in children(parent))
                if (SameText(text(item), path[d])) { found.Add(item); parent = item; hit = true; break; }
            if (!hit) return new TreeFound<T>(found, $"no item {path[d]}" + (d > 0 ? $" under {Join(path.Take(d))}" : "") + " in the tree");
        }
        return new TreeFound<T>(found, path.Count > MaxDepth ? $"the path is more than {MaxDepth} levels deep" : null);
    }
}
