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
/// <param name="NoItem">a list view's menu was opened on no item (empty space, or nothing selected): replay clears the selection and opens it on empty space. False in a recording from before this was written, which then right-clicks the item or the middle as before.</param>
/// <param name="Selection">a list view: the texts of the selected items when the menu opened (the focused or clicked one is <see cref="Item"/>); replay selects them again. Null = not recorded.</param>
/// <param name="ItemIndex">a list view: the row of <see cref="Item"/> (-1 = none or not recorded); used when the text is blank; a text that is not found (or has fewer rows than <see cref="Occurrence"/>) fails the step, never falls back to this row</param>
/// <param name="Occurrence">a list view: which row with the text <see cref="Item"/> it is, counted from 1 (0 = not recorded)</param>
/// <param name="SelectionOccurrences">a list view: for each entry of <see cref="Selection"/>, which row with that text it is (from 1), parallel to it; null = not recorded</param>
/// <param name="SelectionRows">a list view: for each entry of <see cref="Selection"/>, its row, parallel to it (used for blank texts); null = not recorded</param>
/// <param name="SelectionTruncated">a list view: more items were selected than are recorded; replay does not act on a partial selection and a person takes over</param>
/// <param name="OnHeader">a list view: the menu was opened on the column header (no item; the selection is not touched)</param>
public sealed record MenuChoice(string Kind, string Target, string TargetLabel, string Item, IReadOnlyList<string> Path, int Id = 0, string TargetKind = "", string Owner = "", bool NoItem = false, IReadOnlyList<string>? Selection = null,
    int ItemIndex = -1, int Occurrence = 0, bool SelectionTruncated = false, bool OnHeader = false, IReadOnlyList<int>? SelectionOccurrences = null, IReadOnlyList<int>? SelectionRows = null)
{
    /// <summary>A list view (TargetKind `listview`) is not a tree: its item text is one row's text, never split on the tree separator.</summary>
    bool IsListView => TargetKind == "listview";
    /// <summary>The menu path as one text: "Edit › Rename".</summary>
    public string PathText => string.Join(TreePath.Separator, Path);

    /// <summary>The last level of <see cref="Item"/>: what was right-clicked, "" when none.</summary>
    public string ItemLeaf => IsListView ? Item : TreePath.Split(Item) is { Count: > 0 } l ? l[^1] : "";

    /// <summary>What the person does: "Right-click **Bolts** in **Catalog**, choose **Edit › Rename**" (<paramref name="bold"/> false: no markup; <paramref name="escape"/> cleans a text for markdown).</summary>
    public string Wording(bool bold = true, Func<string, string>? escape = null, string place = "the dialog")
    {
        string B(string s) { s = escape?.Invoke(s) ?? s; return bold ? $"**{s}**" : s; }
        var listName = TargetLabel.Length > 0 ? B(TargetLabel) : IsListView && Target.Length > 0 ? "the list" : "";   // a list with a key but no label is still "the list", never "the dialog"
        var what = OnHeader && listName.Length > 0 ? $"the header of {listName}" : NoItem && listName.Length > 0 ? $"empty space in {listName}" : ItemLeaf.Length > 0 ? B(ItemLeaf) : listName.Length > 0 ? listName : place;
        var where = ItemLeaf.Length > 0 && TargetLabel.Length > 0 ? $" in {B(TargetLabel)}" : "";
        var prefix = SelectionPrefix(B);
        return $"{prefix}{(prefix.Length > 0 ? "right-click" : "Right-click")} {what}{where}, choose {B(PathText)}";
    }

    /// <summary>"With Hammer, Wrench selected, " when a list view had several items selected (at most 5 named), "" otherwise.</summary>
    string SelectionPrefix(Func<string, string> bold)
    {
        if (SelectionTruncated) return "With many items selected, ";
        if (Selection is not { Count: > 1 } sel) return "";
        var names = string.Join(", ", sel.Take(5).Select(bold)) + (sel.Count > 5 ? $" and {sel.Count - 5} more" : "");
        return $"With {names} selected, ";
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
        yield return ("noItem", NoItem ? true : null);
        yield return ("selection", Selection is { Count: > 0 } ? Selection.ToList() : null);
        yield return ("selectionOccurrences", Selection is { Count: > 0 } && SelectionOccurrences is { Count: > 0 } ? SelectionOccurrences.ToList() : null);
        yield return ("selectionRows", Selection is { Count: > 0 } && SelectionRows is { Count: > 0 } ? SelectionRows.ToList() : null);
        yield return ("selectionTruncated", SelectionTruncated ? true : null);
        yield return ("itemIndex", ItemIndex >= 0 ? ItemIndex : null);
        yield return ("occurrence", Occurrence > 0 ? Occurrence : null);
        yield return ("onHeader", OnHeader ? true : null);
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
        bool noItem = e.TryGetProperty("noItem", out var ni) && ni.ValueKind == JsonValueKind.True;
        List<string>? selection = e.TryGetProperty("selection", out var sel) && sel.ValueKind == JsonValueKind.Array
            ? sel.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList() : null;
        bool B(string k) => e.TryGetProperty(k, out var b) && b.ValueKind == JsonValueKind.True;
        int N(string k, int dflt) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n2) ? n2 : dflt;
        List<int>? occurrences = e.TryGetProperty("selectionOccurrences", out var so) && so.ValueKind == JsonValueKind.Array
            ? so.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n3) ? n3 : 0).ToList() : null;
        List<int>? rows = e.TryGetProperty("selectionRows", out var sr) && sr.ValueKind == JsonValueKind.Array
            ? sr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n4) ? n4 : -1).ToList() : null;
        return new MenuChoice(kind.Length > 0 ? kind : "win32", S("target"), S("label"), S("item"), path, id, S("targetKind"), S("owner"), noItem, selection, N("itemIndex", -1), N("occurrence", 0), B("selectionTruncated"), B("onHeader"), occurrences, rows);
    }
}

/// <summary>The keys that open a context menu. F10 is a system key: Windows sends it as WM_SYSKEYDOWN even without Alt, so Shift+F10 never arrives as WM_KEYDOWN (found in the VM test of 2026-11-07).</summary>
public static class MenuKeys
{
    const uint WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const long VK_APPS = 0x5D, VK_F10 = 0x79;

    /// <summary>Does this key message open the control's context menu: the Menu key (WM_KEYDOWN), or Shift+F10 with Alt up (WM_SYSKEYDOWN: Windows sends F10 as a system key even without Alt, so Shift+F10 never arrives as WM_KEYDOWN). Alt held: never (Alt+Menu is no context menu).</summary>
    public static bool Opens(uint message, long vk, bool shiftDown, bool altDown) =>
        !altDown && ((message == WM_KEYDOWN && vk == VK_APPS) || (message == WM_SYSKEYDOWN && vk == VK_F10 && shiftDown));
}

/// <summary>The rows of a list view that a recorded menu names: one comparison of row texts (trimmed, ordinal, ignoring case) for the recorder (which row with this text is it) and for replay (which row is that).</summary>
public static class ListViewRows
{
    public static bool Same(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Which row with the text of row <paramref name="index"/> that row is, counted from 1.</summary>
    public static int OccurrenceOf(IReadOnlyList<string> texts, int index)
    {
        int n = 1;
        for (int i = 0; i < index && i < texts.Count; i++) if (Same(texts[i], texts[index])) n++;
        return n;
    }

    static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => n + "th" };

    /// <summary>The row of a recorded item: the <paramref name="occurrence"/>th row with the text. A blank text is the recorded row (when that row is blank). No occurrence recorded (an older recording): the recorded row while it
    /// still has the text, else the first row with it. Fewer rows with the text than the recording used: -1 and the reason (never silently another row). -1 with a reason too when there is none.</summary>
    public static int Resolve(IReadOnlyList<string> texts, string text, int occurrence, int index, out string? problem)
    {
        problem = null;
        if (text.Trim().Length == 0)
        {
            if (index >= 0 && index < texts.Count && texts[index].Trim().Length == 0) return index;
            problem = $"no blank row {index} in the list";
            return -1;
        }
        if (occurrence <= 0 && index >= 0 && index < texts.Count && Same(texts[index], text)) return index;
        int n = 0, first = -1;
        for (int i = 0; i < texts.Count; i++)
            if (Same(texts[i], text)) { if (first < 0) first = i; if (occurrence > 0 && ++n == occurrence) return i; else if (occurrence <= 0) return first; }
        if (n == 0) problem = $"no item \"{text}\" in the list";
        else problem = $"only {n} row{(n == 1 ? "" : "s")} \"{text}\", the recording used the {Ordinal(occurrence)}";
        return -1;
    }

    /// <summary>The rows to select for a recorded menu: the item's row, then every other recorded selection entry resolved the same way (text and occurrence; a blank text by its row), each entry to its own row and never a second
    /// row for one entry. Null with the reason when the item or an entry is not in the list. An older recording (no occurrences) takes the first row with the text that is not taken yet.</summary>
    public static (int At, List<int> Rows)? Plan(IReadOnlyList<string> texts, MenuChoice menu, out string? problem)
    {
        int at = Resolve(texts, menu.Item, menu.Occurrence, menu.ItemIndex, out problem);
        if (at < 0) { problem ??= $"no row {menu.ItemIndex} in the list"; return null; }
        var rows = new List<int> { at };
        bool itemEntryUsed = false;
        var selection = menu.Selection ?? (IReadOnlyList<string>)Array.Empty<string>();
        for (int k = 0; k < selection.Count; k++)
        {
            int occ = menu.SelectionOccurrences is { } o && k < o.Count ? o[k] : 0, recordedRow = menu.SelectionRows is { } sr && k < sr.Count ? sr[k] : -1, row = -1;
            bool blank = selection[k].Trim().Length == 0;
            if (occ > 0 || blank) { row = Resolve(texts, selection[k], occ, recordedRow, out problem); if (row < 0) return null; }
            else
            {
                if (!itemEntryUsed && Same(selection[k], menu.Item)) { itemEntryUsed = true; continue; }   // the item's own entry
                for (int i = 0; i < texts.Count && row < 0; i++) if (!rows.Contains(i) && Same(texts[i], selection[k])) row = i;
                if (row < 0) { problem = $"no item \"{selection[k]}\" in the list"; return null; }
            }
            if (!rows.Contains(row)) rows.Add(row);
        }
        problem = null;
        return (at, rows);
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
