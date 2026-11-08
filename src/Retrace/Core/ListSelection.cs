using System.Text.Json;

namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).

/// <summary>One selected item of a list view: its first-column text, its row, and which row with that text it is (from 1). The row is a hint (the list may be sorted differently on replay);
/// the text and the occurrence decide (<see cref="ListViewRows.Resolve"/>).</summary>
public sealed record ListItem(string Text, int Row, int Occurrence);

/// <summary>
/// A list view (SysListView32, a WinForms ListView) as a dialog field: kind <c>listview</c>. Its value is the selected items, in row order, as text
/// ("Bolts", or "Bolts; Nuts"; the second row with the same text reads "Bolts (2nd)"). The recording also keeps <c>items</c> (text, row, occurrence) so the replay selects exactly the
/// recorded rows. The pure rules are here: the text of a selection, parsing it back (a hand-written <c>after</c> without <c>items</c>), the rows to select on replay, the view mode names.
/// </summary>
public static class ListSelection
{
    public const string Kind = "listview";
    public const string Separator = "; ";
    /// <summary>More selected items than this are not recorded (replay would have to hand such a list to a person).</summary>
    public const int MaxItems = 100;

    static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => n + "th" };

    /// <summary>The text of one item the way a value shows it: "Bolts", "Bolts (2nd)".</summary>
    public static string Label(ListItem i)
    {
        // a blank row is "(blank)", so it differs from "nothing selected"; the occurrence suffix is the same as for any text
        var text = i.Text.Trim().Length == 0 ? BlankLabel : i.Text;
        return i.Occurrence > 1 ? $"{text} ({Ordinal(i.Occurrence)})" : text;
    }

    public const string BlankLabel = "(blank)";

    /// <summary>The value of a list view whose selection could not be read (more than <see cref="MaxItems"/> selected, a row past the first rows Retrace looks at, an error): never a selection of an earlier read.
    /// Replay does not select anything for it and stops for a person ("the selection could not be recorded").</summary>
    public const string NotRecorded = "(selection not recorded)";

    public static bool IsNotRecorded(string? value) => value == NotRecorded;

    /// <summary>What two selections are compared by: text (trimmed, case folded) and occurrence per item, a blank item by its row, sorted. The value text is for display only (an item text may hold "; "
    /// or "(2nd)"); the row of a texted item is left out because a list may be sorted differently on replay.</summary>
    public static string Key(IEnumerable<ListItem> items) =>
        string.Join("\u001f", items.Select(i => i.Text.Trim().Length == 0 ? "\u0001" + i.Row : i.Text.Trim().ToLowerInvariant() + "\u0002" + i.Occurrence).Distinct().OrderBy(x => x, StringComparer.Ordinal));   // a set: two items that are one row count once

    /// <summary>The value of a selection: the items' labels in row order joined with "; ". "" for no selection.</summary>
    public static string Format(IEnumerable<ListItem> items) => string.Join(Separator, items.OrderBy(i => i.Row).Select(Label));

    /// <summary>The selected rows of a list whose first-column texts are <paramref name="texts"/> as items (occurrence counted from the top).</summary>
    public static List<ListItem> FromRows(IReadOnlyList<string> texts, IEnumerable<int> rows) =>
        rows.Where(r => r >= 0 && r < texts.Count).OrderBy(r => r).Select(r => new ListItem(texts[r], r, ListViewRows.OccurrenceOf(texts, r))).ToList();

    /// <summary>The names in a value, one per selected item ("Bolts", "Nuts (2nd)" kept as written): for wording.</summary>
    public static List<string> Names(string value) =>
        string.IsNullOrWhiteSpace(value) ? new List<string>() : value.Split(Separator, StringSplitOptions.None).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>The items of a recorded field: its <c>items</c>, else (a hand-written recording) its <c>after</c> text split on "; " with "(2nd)" read as the occurrence.</summary>
    public static IReadOnlyList<ListItem> ItemsOf(FieldChange c) => c.Items is { } items ? items : Parse(c.After);

    /// <summary>The items of a hand-written value (no `items` in the recording): the texts as written, "(blank)" is the text "(blank)", the row of an item is not known (-1). A blank row can only be recorded with its `items`.</summary>
    public static List<ListItem> Parse(string value)
    {
        var list = new List<ListItem>();
        int row = -1;
        foreach (var name in Names(value))
        {
            var text = name; int occ = 1;
            var open = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0 && name.EndsWith(')'))
            {
                var inner = name[(open + 2)..^1];
                var digits = new string(inner.TakeWhile(char.IsDigit).ToArray());
                if (digits.Length > 0 && int.TryParse(digits, out var n) && n > 1 && inner[digits.Length..] is "st" or "nd" or "rd" or "th") { text = name[..open]; occ = n; }
            }
            list.Add(new ListItem(text, row, occ));
        }
        return list;
    }

    /// <summary>The rows to select for the recorded <paramref name="items"/> in a list with these texts: every item looked up first (text and occurrence, the row only as a fallback for a blank text),
    /// null with the reason when one is not there (nothing is selected then; never silently another row).</summary>
    public static List<int>? Plan(IReadOnlyList<string> texts, IReadOnlyList<ListItem> items, out string? problem) => Plan(texts, items, out problem, null);

    /// <param name="warnings">gets a line for an item that resolves to a row an earlier item took (it is selected once, but the recording had two)</param>
    public static List<int>? Plan(IReadOnlyList<string> texts, IReadOnlyList<ListItem> items, out string? problem, List<string>? warnings)
    {
        problem = null;
        var rows = new List<int>();
        foreach (var item in items)
        {
            if (item.Text.Trim().Length == 0 && item.Row < 0) { problem = "a blank item has no row in the recording (it was written by hand without items)"; return null; }
            var row = ListViewRows.Resolve(texts, item.Text, item.Occurrence, item.Row, out problem);
            if (row < 0) { problem ??= $"no item \"{item.Text}\" in the list"; return null; }
            if (rows.Contains(row)) warnings?.Add($"\"{Label(item)}\" is row {row} again: the recording had two items that are one row here");
            else rows.Add(row);
        }
        rows.Sort();
        return rows;
    }

    /// <summary>The rows to change to reach <paramref name="want"/> from the rows selected now: (deselect, select). A row selected already stays untouched (no needless notification).</summary>
    public static (List<int> Deselect, List<int> Select) Changes(IEnumerable<int> selectedNow, IReadOnlyList<int> want)
    {
        var now = selectedNow.ToHashSet();
        return (now.Where(r => !want.Contains(r)).OrderBy(r => r).ToList(), want.Where(r => !now.Contains(r)).ToList());
    }

    /// <summary>The <c>items</c> of a field object (an array of {text,row,occurrence}); null when it has none.</summary>
    public static List<ListItem>? ItemsFromJson(JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<ListItem>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string text = e.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            int row = e.TryGetProperty("row", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var rv) ? rv : -1;
            int occ = e.TryGetProperty("occurrence", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var ov) ? ov : 1;
            list.Add(new ListItem(text, row, occ));
        }
        return list;
    }

    /// <summary>The recording's JSON for the items (<c>[{"text":"Bolts","row":2,"occurrence":1}]</c>).</summary>
    public static List<Dictionary<string, object?>> ToJson(IEnumerable<ListItem> items) =>
        items.Select(i => new Dictionary<string, object?> { ["text"] = i.Text, ["row"] = i.Row, ["occurrence"] = i.Occurrence }).ToList();

    /// <summary>"Select **Bolts** in **Products**" / "Select **Bolts**, **Nuts** in **Products**" (<paramref name="item"/> and <paramref name="list"/> format the parts).</summary>
    public static string Sentence(string after, string listLabel, Func<string, string> item, Func<string, string> list, IReadOnlyList<ListItem>? items = null) =>
        IsNotRecorded(after) ? $"Select the items in {list(listLabel)} yourself (the selection could not be recorded)" :
        (items != null ? items.OrderBy(i => i.Row).Select(Label).ToList() : Names(after)) is { Count: > 0 } names
            ? $"Select {string.Join(", ", names.Select(item))} in {list(listLabel)}"
            : $"Clear the selection in {list(listLabel)}";
}
