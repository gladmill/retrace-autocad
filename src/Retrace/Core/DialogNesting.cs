using System.Text.Json;

namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// What the two readers of a recording (the replay plan, Core/Plan.cs, and the test script, Engine/Recordings/RecordingSession.cs) do alike with a nested dialog and the
// fields of a dialog event: one implementation.

public static class DialogNesting
{
    /// <summary>A dialog with <c>parent</c> belongs to its outer dialog's latest click: it is added to <paramref name="clickNested"/> (the dialogs that click opened, in order) when it opened within
    /// <see cref="DialogClickRules.NestedWithinMs"/> of the click, or of the close of the dialog of that click before it (a sequence: Clear > "Remove all?" > "3 items removed"). Returns
    /// false when it does not belong to the click (no click near enough): the dialog stays a step of its own, as before.</summary>
    public static bool Attach<T>(IList<T> clickNested, long clickMs, T nested, Func<T, long> openOf, Func<T, long> closeOf)
    {
        long? prevEnd = clickNested.Count > 0 && closeOf(clickNested[^1]) is >= 0 and var end ? end : null;
        if (!DialogClickRules.NestedBelongsTo(clickMs, prevEnd, openOf(nested))) return false;
        clickNested.Add(nested);
        return true;
    }

    /// <summary>A <c>modal_alias</c> event: the dialog event <paramref name="dupN"/> was announced (<c>EnterModal</c>) for a dialog the recorder already tracked as <paramref name="ofN"/>
    /// (found by its window). From now on that number means that dialog. Returns the step built for the duplicate, which the reader drops (null: nothing to drop).</summary>
    public static T? Alias<T>(Dictionary<long, T> byN, long dupN, long ofN) where T : class
    {
        if (!byN.TryGetValue(dupN, out var dup) || !byN.TryGetValue(ofN, out var target)) return null;
        byN[dupN] = target;
        return ReferenceEquals(dup, target) ? null : dup;
    }

    /// <summary>The changed fields of a <c>dialog_values</c> / <c>dialog_click</c> event (its <c>fields</c> array; each with its <c>tab</c> when the dialog has tabs, else ""); anything that is not an array of objects gives none.</summary>
    public static List<FieldChange> FieldsOf(JsonElement fields)
    {
        var list = new List<FieldChange>();
        if (fields.ValueKind != JsonValueKind.Array) return list;
        string S(JsonElement f, string k) => f.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        foreach (var f in fields.EnumerateArray())
            if (f.ValueKind == JsonValueKind.Object) list.Add(new FieldChange(S(f, "key"), S(f, "label"), S(f, "kind"), S(f, "before"), S(f, "after"), S(f, "tab")));
        return list;
    }

    /// <summary>The same for an event: its <c>fields</c> property, or none.</summary>
    public static List<FieldChange> FieldsOfEvent(JsonElement e) => e.TryGetProperty("fields", out var f) ? FieldsOf(f) : new List<FieldChange>();
}
