namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>How a click on a palette tree item reads in the test document, the training document (Tdoc) and the step lists: the action and the item first
/// ("Click **Child**", "Expand **Parent**"), then where it is ("under Parent label, in the Tree label tree"). Only the wording: the recording keeps the full path and label, and a replay
/// finds the item by those.</summary>
public static class TreeWording
{
    static readonly System.Text.RegularExpressions.Regex IdOnly = Retrace.Engine.Util.Rx.Create(@"^#\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A tree's label as the words use it: a bracketed part at the end ("(Filter - 'abc')") and a colon at the end are dropped ("Tree label: (Filter - 'abc')" gives "Tree label").
    /// Text that is only brackets or only a colon is kept as it is, so a label never turns into nothing because of this.</summary>
    public static string Label(string? label)
    {
        var t = (label ?? "").Trim();
        if (IdOnly.IsMatch(t)) return "";   // a tree with no label of its own is recorded as "#1012" (its id): the words say "the tree"
        var cut = t;
        if (cut.EndsWith(')'))
        {
            int depth = 0, open = -1;
            for (int i = cut.Length - 1; i >= 0; i--)
            {
                if (cut[i] == ')') depth++;
                else if (cut[i] == '(' && --depth == 0) { open = i; break; }
            }
            if (open > 0) cut = cut[..open].TrimEnd();
        }
        cut = cut.TrimEnd(':', ' ');
        return cut.Length > 0 ? cut : t;
    }

    /// <summary>The item (last level) and the path of its parents ("" for a top-level item) of a tree path.</summary>
    public static (string Item, string Parents) Split(string? path)
    {
        var levels = TreePath.Split(path);
        if (levels.Count == 0) return ("", "");
        return (levels[^1], TreePath.Join(levels.Take(levels.Count - 1)));
    }

    /// <summary>"Click **Child** under Parent, in the Tree label tree" / "Expand **A** in the tree". <paramref name="verb"/> is click, expand or collapse; <paramref name="item"/> marks up the item,
    /// <paramref name="text"/> the parent path and the tree label (the caller's escaping).</summary>
    public static string Words(string verb, string path, string? treeLabel, Func<string, string> item, Func<string, string> text, bool capital = true)
    {
        var (name, parents) = Split(path);
        var v = capital ? char.ToUpperInvariant(verb[0]) + verb[1..] : verb;
        var tree = Label(treeLabel);
        var where = tree.Length > 0 ? $"in the {text(tree)} tree" : "in the tree";
        return $"{v} {item(name.Length > 0 ? name : PaletteClickKinds.BlankItem)}" + (parents.Length > 0 ? $" under {text(parents)}, " : " ") + where;
    }
}
