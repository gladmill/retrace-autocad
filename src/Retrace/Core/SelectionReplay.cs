using System.Text.Json;

namespace Retrace.Core;

/// <summary>How the answers of a selection are replayed. Pure logic (no AutoCAD), used by <see cref="Plan"/>.
/// What AutoCAD raises for a built-in command's selection (seen recording ERASE and -ATTEDIT in AutoCAD 2026): every window corner, pick and
/// selection keyword the person gives is a `point` / `corner` prompt of its own (`Window` comes as a Keyword result of a point prompt),
/// and the finished selection is one more `selection` prompt, with handles. That result is the echo of the points already recorded, and the
/// Enter that ends the selection may be recorded as a `selection` with status Error. Replaying the handles after the points selects the same
/// objects a second time (or fails: handles differ between drawings, and a nested selection such as the attributes of -ATTEDIT refuses them).</summary>
public static class SelectionReplay
{
    /// <summary>true for an answer that built a selection: a window / crossing corner, or a point or keyword given at a "Select objects:" prompt.</summary>
    public static bool IsSelectionInput(Answer a)
    {
        if (a.Kind == "corner") return true;
        if (a.Kind != "point") return false;
        var m = a.Message ?? "";
        return m.Contains("select", StringComparison.OrdinalIgnoreCase) || m.Contains("corner", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The answers to type, in order: the recording's answers without the selection results that only echo points typed just before,
    /// and with the Enter that ended a selection (a `selection` result with status Error or None that a key press led to) as a plain Enter.
    /// A selection result with nothing replayable (an empty selection, no input behind it) is dropped, so it does not stop the typing after it.
    /// A selection result that stands alone (no selection input before it) is kept: it is typed from its handles, the last resort.</summary>
    public static List<Answer> Effective(IReadOnlyList<Answer> answers)
    {
        var result = new List<Answer>();
        bool typedSelectionInput = false;   // selection points / keywords since the last answer that is not selection input
        foreach (var a in answers)
        {
            if (a.Kind != "selection")
            {
                typedSelectionInput = IsSelectionInput(a);
                result.Add(a);
                continue;
            }
            if (a.Status == "OK")
            {
                if (typedSelectionInput) { typedSelectionInput = false; continue; }   // echo of the points above; a second result after it is its own
                result.Add(a);
                continue;
            }
            if (a.Status == "Keyword")   // a keyword chosen at the selection prompt: typed as it was
            {
                result.Add(a);
                typedSelectionInput = false;
                continue;
            }
            // Error / None / Cancel: the Enter that ended the selection, when a key press or click led to it and no Enter was recorded for it
            bool enterAlreadyThere = result.Count > 0 && (result[^1].Status == "None" || ClosesSelection(result[^1]));
            if (a.Status is "Error" or "None" && a.Input == true && !enterAlreadyThere)
                result.Add(a with { Status = "None", Value = null, Keyword = null });
            else if (a.Status == "Cancel") result.Add(a);
            typedSelectionInput = false;
        }
        return result;
    }

    /// <summary>true for a selection result whose typed text already ends with the Enter that closes the selection (an empty last line).</summary>
    static bool ClosesSelection(Answer a) =>
        a is { Kind: "selection", Status: "OK", Value: { } v } && TypedLines(v) is { Count: > 0 } lines && lines[^1].Length == 0;

    /// <summary>The text the person typed for this selection (`typed` of the recorded value), as lines; null if the recording has none.</summary>
    public static List<string>? TypedLines(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("typed", out var t) && t.ValueKind == JsonValueKind.String ? TypedInput.Lines(t.GetString()) : null;

    /// <summary>Last resort: handles as `(handent "..")`, one line each, then the Enter that ends the selection. A handle that
    /// <paramref name="exists"/> says is missing in the drawing, or that holds another kind of object than recorded, is left out (typing a
    /// (handent) that is nil makes AutoCAD ask again). Null when no handle is left.</summary>
    public static List<string>? HandleLines(JsonElement value, Func<string, string?, bool>? exists = null)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("handles", out var hs) || hs.ValueKind != JsonValueKind.Array) return null;
        string?[] dxf = value.TryGetProperty("dxf", out var ds) && ds.ValueKind == JsonValueKind.Array
            ? ds.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToArray() : Array.Empty<string?>();
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (var h in hs.EnumerateArray())
        {
            var handle = h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            var kind = i < dxf.Length ? dxf[i] : null;
            i++;
            if (string.IsNullOrEmpty(handle) || !handle.All(Uri.IsHexDigit) || !seen.Add(handle)) continue;   // "?" and repeats (attributes of one insert)
            if (exists != null && !exists(handle, kind)) continue;
            lines.Add($"(handent \"{handle}\")");
        }
        if (lines.Count == 0) return null;
        lines.Add("");
        return lines;
    }
}
