using System.Text.Json;

namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// Right-click context menus: what a recording keeps of one (the `context_menu` event), how a menu item text is compared, the walk of a menu path in a menu known only
// through a children function, the wording, and the bookkeeping of the messages of a Win32 popup menu (which item is selected, which one was chosen).

/// <summary>A context menu choice: a right-click on <see cref="Target"/> and the menu item picked. <see cref="Kind"/>: "win32" (TrackPopupMenu), "winforms" (ContextMenuStrip) or "wpf" (ContextMenu).</summary>
/// <param name="Target">the control as fields are keyed (`name:Tree`, `id:1002`, `label:Files#1`); "" = the window itself</param>
/// <param name="TargetLabel">the control's label as fields name it ("Catalog")</param>
/// <param name="Item">a tree item's path (` › `) or a list item's text that was right-clicked; "" = none</param>
/// <param name="Path">the menu items chosen from the top: ["Edit", "Rename"]</param>
/// <param name="Id">the command id of a Win32 menu item (0 = none or not known)</param>
/// <param name="Owner">a Win32 menu: the window that gets its WM_COMMAND, as a role: `window` (the dialog or palette itself) or `id:N` (a control of it); "" = not recorded</param>
/// <param name="TargetKind">the field kind of the target (tree, list, edit ...), "" when it is no field</param>
public sealed record MenuChoice(string Kind, string Target, string TargetLabel, string Item, IReadOnlyList<string> Path, int Id = 0, string TargetKind = "", string Owner = "")
{
    /// <summary>The menu path as one text: "Edit › Rename".</summary>
    public string PathText => string.Join(TreePath.Separator, Path);

    /// <summary>The last level of <see cref="Item"/>: what was right-clicked, "" when none.</summary>
    public string ItemLeaf => TreePath.Split(Item) is { Count: > 0 } l ? l[^1] : "";

    /// <summary>What the person does: "Right-click **Bolts** in **Catalog**, choose **Edit › Rename**" (<paramref name="bold"/> false: no markup; <paramref name="escape"/> cleans a text for markdown).</summary>
    public string Wording(bool bold = true, Func<string, string>? escape = null, string place = "the dialog")
    {
        string B(string s) { s = escape?.Invoke(s) ?? s; return bold ? $"**{s}**" : s; }
        var what = ItemLeaf.Length > 0 ? B(ItemLeaf) : TargetLabel.Length > 0 ? B(TargetLabel) : place;
        var where = ItemLeaf.Length > 0 && TargetLabel.Length > 0 ? $" in {B(TargetLabel)}" : "";
        return $"Right-click {what}{where}, choose {B(PathText)}";
    }

    /// <summary>The same as a phrase inside a sentence (the pause message, a step's sub-item): "right-click Bolts in Catalog, choose Edit › Rename".</summary>
    public string Phrase(bool bold = false, Func<string, string>? escape = null, string place = "the dialog") { var w = Wording(bold, escape, place); return char.ToLowerInvariant(w[0]) + w[1..]; }

    /// <summary>What a button would be called in lists of clicks: the menu path.</summary>
    public string ButtonText => PathText;

    /// <summary>The `context_menu` event's own properties (the recorder adds `for` / palette, fields, statics).</summary>
    public IEnumerable<(string, object?)> ToFields()
    {
        yield return ("kind", Kind);
        yield return ("target", Target.Length > 0 ? Target : null);
        yield return ("label", TargetLabel.Length > 0 ? TargetLabel : null);
        yield return ("targetKind", TargetKind.Length > 0 ? TargetKind : null);
        yield return ("item", Item.Length > 0 ? Item : null);
        yield return ("path", Path.ToList());
        yield return ("id", Id > 0 ? Id : null);
        yield return ("owner", Owner.Length > 0 ? Owner : null);
    }

    /// <summary>The choice a `context_menu` event holds; null when it has no menu path.</summary>
    public static MenuChoice? From(JsonElement e)
    {
        string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        if (!e.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.Array) return null;
        var path = p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList();
        if (path.Count == 0) return null;
        int id = e.TryGetProperty("id", out var iv) && iv.ValueKind == JsonValueKind.Number && iv.TryGetInt32(out var n) ? n : 0;
        var kind = S("kind");
        return new MenuChoice(kind.Length > 0 ? kind : "win32", S("target"), S("label"), S("item"), path, id, S("targetKind"), S("owner"));
    }
}

public static class MenuText
{
    /// <summary>A menu item's text as it is compared: mnemonic ampersands out ("&amp;Rename" is "Rename", "A &amp;&amp; B" is "A &amp; B"), the accelerator after a tab out ("Rename\tF2"),
    /// a trailing ellipsis out ("Properties..." and "Properties…" are "Properties"), trimmed.</summary>
    public static string Clean(string? text)
    {
        var t = Display(text);
        while (t.EndsWith("...", StringComparison.Ordinal) || t.EndsWith('\u2026')) t = t.EndsWith("...", StringComparison.Ordinal) ? t[..^3].TrimEnd() : t[..^1].TrimEnd();
        return t;
    }

    /// <summary>The text as the recording keeps it and the steps show it: <see cref="Clean"/> but with the ellipsis ("Properties...").</summary>
    public static string Display(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = text;
        int tab = t.IndexOf('\t');
        if (tab >= 0) t = t[..tab];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == '&') { if (i + 1 < t.Length && t[i + 1] == '&') { sb.Append('&'); i++; } continue; }
            sb.Append(t[i]);
        }
        return sb.ToString().Trim();
    }

    public static bool Same(string? a, string? b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where a menu path led (like <see cref="TreeFound{T}"/>): the item at each level when <see cref="Problem"/> is null.</summary>
public sealed record MenuFound<T>(IReadOnlyList<T> Items, string? Problem);

public static class MenuPath
{
    /// <summary>Walks <paramref name="path"/> down a menu: at each level the first enabled child whose text is the level's text (else the first with that text, which then is a problem when
    /// it is the last one: a disabled item cannot be chosen). <paramref name="children"/> lists the items of a submenu (the null/default parent = the top level) and may open the
    /// submenu first. Separators are expected to be left out by the caller (empty text never matches).</summary>
    public static MenuFound<T> Find<T>(IReadOnlyList<string> path, T? root, Func<T?, IEnumerable<T>> children, Func<T, string> text, Func<T, bool> enabled) where T : class
    {
        var found = new List<T>();
        T? parent = root;
        for (int d = 0; d < path.Count; d++)
        {
            T? hit = null, disabled = null;
            foreach (var item in children(parent))
            {
                if (path[d].Length == 0 || !MenuText.Same(text(item), path[d])) continue;
                if (enabled(item)) { hit = item; break; }
                disabled ??= item;
            }
            if (hit == null && disabled == null)
                return new MenuFound<T>(found, $"no menu item {path[d]}" + (d > 0 ? $" under {string.Join(TreePath.Separator, path.Take(d))}" : ""));
            if (hit == null) return new MenuFound<T>(found, $"the menu item {string.Join(TreePath.Separator, path.Take(d + 1))} is disabled");
            found.Add(hit);
            parent = hit;
        }
        return new MenuFound<T>(found, null);
    }
}

/// <summary>Follows the messages of a Win32 popup menu (TrackPopupMenu): WM_MENUSELECT says which item is highlighted (and in which menu), WM_COMMAND with its id says it was chosen.
/// The path is the highlighted item of each menu level. A menu closed without a choice (Esc, a click elsewhere) ends with no WM_COMMAND and records nothing. No window code here.</summary>
public sealed class MenuTracker
{
    /// <summary>A WM_COMMAND with the id of the highlighted item counts when it follows the close of the menu this soon (the command is posted after the menu loop ended).</summary>
    public const long ChoiceWithinMs = 2000;

    readonly List<(long Menu, string Text)> _levels = new();
    int _leafId = -1;
    bool _leafPopup, _closed;
    long _closedMs;

    /// <summary>true once a menu session is going (an item was highlighted since the last choice or reset).</summary>
    public bool Active => _levels.Count > 0;

    /// <summary>WM_MENUSELECT: <paramref name="menu"/> is the menu the item is in, <paramref name="text"/> its text (cleaned), <paramref name="id"/> its command id (for a submenu: -1), <paramref name="popup"/> an item that opens a submenu.</summary>
    public void Select(long menu, string text, int id, bool popup)
    {
        if (_closed) Reset();
        int at = _levels.FindIndex(l => l.Menu == menu);
        if (at >= 0) _levels.RemoveRange(at, _levels.Count - at);
        _levels.Add((menu, text));
        _leafId = popup ? -1 : id;
        _leafPopup = popup;
    }

    /// <summary>The menu closed (WM_MENUSELECT with the close flags): the selection is kept for a moment, because the command of a click follows it.</summary>
    public void Closed(long nowMs) { _closed = true; _closedMs = nowMs; }

    /// <summary>WM_COMMAND with <paramref name="id"/>: the menu path when that is the highlighted item (not a submenu) of a menu that is open or closed a moment ago; the session ends.</summary>
    public IReadOnlyList<string>? Chosen(int id, long nowMs)
    {
        if (_levels.Count == 0 || _leafPopup || _leafId < 0 || id != _leafId) return null;
        if (_closed && nowMs - _closedMs > ChoiceWithinMs) { Reset(); return null; }
        var path = _levels.Select(l => l.Text).ToList();
        Reset();
        return path;
    }

    /// <summary>The id of the highlighted item (for the event), -1 = none.</summary>
    public int LeafId => _leafId;

    public void Reset() { _levels.Clear(); _leafId = -1; _leafPopup = false; _closed = false; }
}
