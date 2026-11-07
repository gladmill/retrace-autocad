namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The pure parts of tab context: a field sits on a page of a tab control, and in a dialog with tabs each field (and each button pressed) carries the
// tab it was on as a path of tab texts ("Lines", or "Fit › Text placement" for a tab control inside a page of another one, outermost first). The path
// qualifies a field's key (`tab:Lines|id:1001`), so two pages that reuse a control id are two fields; and it says what a replay must show before it
// sets a value or presses a button (TabPlan). The Win32 side (TabControl32, DialogReader) only reads the controls and hands the numbers over.

public static class TabPath
{
    /// <summary>Between the tab texts of two nested tab controls, as the paths of a tree are shown.</summary>
    public const string Separator = " › ";

    /// <summary>The start of a key that names its tab: <c>tab:Lines|id:1001</c>.</summary>
    public const string KeyPrefix = "tab:";

    // what a key looks like after its tab: the three forms DialogFieldsCore.BuildMapped makes
    static readonly string[] KeyForms = { "|id:", "|name:", "|label:" };

    /// <summary>A tab's text as it is compared and shown: mnemonic ampersands and a trailing ':' taken out, trimmed.</summary>
    public static string Clean(string? tabText) => DialogFieldsCore.CleanLabel(tabText);

    /// <summary>The path of the tabs whose texts are <paramref name="tabs"/>, outermost first; "" for none (a control that sits in no tab).</summary>
    public static string Join(IEnumerable<string?> tabs) => string.Join(Separator, tabs.Select(Clean).Where(t => t.Length > 0));

    /// <summary>The levels of a path, trimmed; none for "" or white space.</summary>
    public static List<string> Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new List<string>();
        return path.Split(Separator).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>Are two tab texts the same? Trimmed, mnemonics and a trailing ':' ignored, case-insensitive.</summary>
    public static bool SameText(string? a, string? b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Are two paths the same, level by level? Two empty paths are.</summary>
    public static bool Same(string? a, string? b)
    {
        var x = Split(a);
        var y = Split(b);
        if (x.Count != y.Count) return false;
        for (int i = 0; i < x.Count; i++) if (!SameText(x[i], y[i])) return false;
        return true;
    }

    // ---- keys ----------------------------------------------------------------------------------------------------------

    /// <summary>The key of a field on a tab: <c>tab:Lines|id:1001</c>. A field that sits on no tab (and every field of a dialog without tabs) keeps its key as it was.</summary>
    public static string Qualify(string key, string? tab)
    {
        var path = Join(Split(tab));
        return path.Length == 0 ? key : KeyPrefix + path + "|" + key;
    }

    static int FormAt(string key)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return -1;
        int best = -1;
        foreach (var form in KeyForms)
        {
            var at = key.IndexOf(form, KeyPrefix.Length, StringComparison.Ordinal);
            if (at >= 0 && (best < 0 || at < best)) best = at;
        }
        return best;
    }

    /// <summary>The key without its tab: <c>id:1001</c> for <c>tab:Lines|id:1001</c>; a key that names no tab is returned as it is.</summary>
    public static string Unqualified(string key) => FormAt(key) is var at and >= 0 ? key[(at + 1)..] : key;

    /// <summary>The tab a key names ("Lines"), or "" when it names none.</summary>
    public static string TabOf(string key) => FormAt(key) is var at and >= 0 ? key[KeyPrefix.Length..at] : "";
}
