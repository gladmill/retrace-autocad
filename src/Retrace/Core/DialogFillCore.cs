using System.Globalization;
using System.Text.Json.Serialization;

namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The pure parts of filling a dialog from a recording: matching recorded fields to live controls, comparing values,
// choosing the close button, deciding whether to auto-fill at all, and the message for a person when it cannot.

/// <summary>A field of the live dialog as the reader sees it now (Key, Label, Kind as in <see cref="DialogField"/>).</summary>
public sealed record LiveField(string Key, string Label, string Kind);

/// <summary>What happened to one recorded value. Status: applied | notFound | failed. In a results file as `fields[]`.</summary>
public sealed class FieldFillResult
{
    public const string Applied = "applied", NotFound = "notFound", Failed = "failed";

    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = NotFound;
    [JsonPropertyName("message")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Message { get; set; }

    public static FieldFillResult Ok(string label, string? message = null) => new() { Label = label, Status = Applied, Message = message };
    public static FieldFillResult Missing(string label, string? message = null) => new() { Label = label, Status = NotFound, Message = message ?? "no matching control in the dialog" };
    public static FieldFillResult Fail(string label, string message) => new() { Label = label, Status = Failed, Message = message };
}

/// <summary>How the replay may handle a dialog by itself.</summary>
public enum AutoMode { None, Ok, Cancel }

public static class FillMatcher
{
    /// <summary>Where a window numeric id stops being a plain dialog control id: WinForms ids are handle-derived and differ per run.</summary>
    public const int StableIdLimit = 65536;

    /// <summary>Can the replay handle this dialog step alone? "cancel": just press Cancel. "ok"/null: needs a recording of the values
    /// (`valuesKnown`), and either some changed values or at least one field read ("nothing changed" is only known if something was read).
    /// A null `closedWith` with changed values is taken as OK.</summary>
    public static AutoMode Decide(bool valuesKnown, int changed, int read, string? closedWith)
    {
        if (closedWith == "cancel") return AutoMode.Cancel;
        if (!valuesKnown) return AutoMode.None;
        if (changed > 0) return AutoMode.Ok;
        return closedWith == "ok" && read > 0 ? AutoMode.Ok : AutoMode.None;
    }

    static bool SameLabel(string a, string b) => string.Equals(DialogFieldsCore.CleanLabel(a), DialogFieldsCore.CleanLabel(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>"Length › Precision" and "Precision": one side carries the group box the other lacks (recorded through UI Automation, or by a
    /// recorder from before group prefixes). Used only when no label matches exactly.</summary>
    static bool SameLeaf(string a, string b)
    {
        static string Leaf(string s) { var c = DialogFieldsCore.CleanLabel(s); var i = c.LastIndexOf(" › ", StringComparison.Ordinal); return i < 0 ? c : c[(i + 3)..]; }
        return string.Equals(Leaf(a), Leaf(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`id:1004` gives 1004, else null.</summary>
    public static int? IdOf(string key) => key.StartsWith("id:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>`label:Rows#2` gives 2 (the occurrence among fields with that label), else null.</summary>
    public static int? OccurrenceOf(string key)
    {
        if (!key.StartsWith("label:", StringComparison.Ordinal)) return null;
        var h = key.LastIndexOf('#');
        return h > 0 && int.TryParse(key.AsSpan(h + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : null;
    }

    /// <summary>For each recorded change the index of its live field, or -1. WinForms control ids are not stable between runs, so the label comes
    /// first (case-insensitive, mnemonics and trailing ':' ignored): with a `label:L#n` key the n-th live field with that label; with an `id:` key the
    /// live field with that label (the one with the same id if several). Only when no label matches, and the id is a stock dialog id (below 65536),
    /// the live field with the same id. The kind must agree; a live field is used once.</summary>
    public static int[] Match(IReadOnlyList<FieldChange> recorded, IReadOnlyList<LiveField> live)
    {
        var result = new int[recorded.Count];
        var used = new HashSet<int>();
        for (int i = 0; i < recorded.Count; i++)
        {
            var r = recorded[i];
            result[i] = -1;
            // a control's own name (WinForms) is stable between runs: it decides before any label
            if (r.Key.StartsWith("name:", StringComparison.Ordinal))
            {
                int byName = -1;
                for (int j = 0; j < live.Count; j++) if (!used.Contains(j) && live[j].Key == r.Key && live[j].Kind == r.Kind) { byName = j; break; }
                if (byName >= 0) { used.Add(byName); result[i] = byName; continue; }
            }
            var same = new List<int>();   // live fields with the label, in dialog order
            for (int j = 0; j < live.Count; j++) if (SameLabel(live[j].Label, r.Label)) same.Add(j);
            if (same.Count == 0) for (int j = 0; j < live.Count; j++) if (SameLeaf(live[j].Label, r.Label)) same.Add(j);
            int pick = -1;
            if (same.Count > 0)
            {
                if (OccurrenceOf(r.Key) is { } n) { if (n <= same.Count) pick = same[n - 1]; }
                else
                {
                    var open = same.Where(j => !used.Contains(j) && live[j].Kind == r.Kind).ToList();
                    pick = open.FirstOrDefault(j => live[j].Key == r.Key, -1);
                    if (pick < 0 && open.Count > 0) pick = open[0];
                }
                if (pick >= 0 && (used.Contains(pick) || live[pick].Kind != r.Kind)) pick = -1;
            }
            else if (IdOf(r.Key) is { } id && id > 0 && id < StableIdLimit)
            {
                for (int j = 0; j < live.Count; j++)
                    if (!used.Contains(j) && live[j].Key == r.Key && live[j].Kind == r.Kind) { pick = j; break; }
            }
            if (pick >= 0) { used.Add(pick); result[i] = pick; }
        }
        return result;
    }

    /// <summary>The value a field has when the change is done: the `after` text; a radio recorded per button ("on") means that button's label.</summary>
    public static string Expected(FieldChange c) => c.Kind == "radio" && c.After == "on" ? c.Label : c.After;

    /// <summary>A radio group recorded as "none selected" cannot be put back to that: nothing to do.</summary>
    /// <summary>Does the live value show the recorded change? A radio button read on its own (WinForms, UI Automation) reads "on"/"off", not the
    /// text of the chosen button.</summary>
    public static bool Satisfied(FieldChange c, string? current) =>
        current != null && (c.Kind == "radio" && c.After == "on" && current is "on" or "off" ? current == "on" : ValueMatches(c.Kind, Expected(c), current));

    public static bool IsNoOp(FieldChange c) => c.Kind == "radio" && c.After is "" or "off";

    static string LastPart(string s) { var i = s.LastIndexOf(" › ", StringComparison.Ordinal); return i < 0 ? s : s[(i + 3)..]; }

    /// <summary>Does the live value equal the expected one? Trimmed; numbers compare as numbers ("20" equals "20.00"); text ignores case;
    /// a radio's text may carry its group prefix ("Length › Metric").</summary>
    public static bool ValueMatches(string kind, string expected, string actual)
    {
        var a = (expected ?? "").Trim();
        var b = (actual ?? "").Trim();
        if (kind == "radio") { a = DialogFieldsCore.CleanLabel(LastPart(a)); b = DialogFieldsCore.CleanLabel(LastPart(b)); }
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (kind is "edit" or "combo" or "list" or "slider" &&
            double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
        return false;
    }

    /// <summary>Index of the button to press: OK (id 1, else text "OK") or Cancel (id 2, else text "Cancel"); -1 if there is none.
    /// A text match is a fallback because WinForms ids are not the stock ones.</summary>
    public static int PickButton(IReadOnlyList<(int Id, string Text)> buttons, bool ok)
    {
        int id = ok ? 1 : 2;
        string text = ok ? "OK" : "Cancel";
        for (int i = 0; i < buttons.Count; i++) if (buttons[i].Id == id) return i;
        for (int i = 0; i < buttons.Count; i++) if (string.Equals(DialogFieldsCore.CleanLabel(buttons[i].Text), text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public static bool TitleMatches(string? recorded, string? actual) =>
        string.IsNullOrWhiteSpace(recorded) || string.Equals((recorded ?? "").Trim(), (actual ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The pause message when the replay could not (fully) fill the dialog itself:
    /// "A dialog opens: X. Filled 2 of 3 values; set Spacing to 20 yourself, then close it with OK (see f.md, step 4)."
    /// <paramref name="results"/> parallels <paramref name="values"/> (shorter or empty: the rest counts as not done). <paramref name="note"/> is put first.</summary>
    public static string FallbackMessage(string? title, string? scriptFile, int? stepNumber, IReadOnlyList<FieldChange>? values,
        IReadOnlyList<FieldFillResult> results, string? closedWith, string? note)
    {
        var t = string.IsNullOrWhiteSpace(title) ? "(untitled dialog)" : title;
        var see = scriptFile != null && stepNumber != null ? $" (see {scriptFile}, step {stepNumber})" : "";
        var close = closedWith == "cancel" ? "Cancel" : "OK";
        var sb = new System.Text.StringBuilder($"A dialog opens: {t}. ");
        if (!string.IsNullOrWhiteSpace(note)) sb.Append(note!.Trim().TrimEnd('.')).Append(". ");
        var vals = closedWith == "cancel" || values == null ? new List<FieldChange>() : values.ToList();
        if (vals.Count == 0) { sb.Append($"Close it with {close}{see}."); return sb.ToString(); }
        int done = 0;
        var left = new List<string>();
        for (int i = 0; i < vals.Count; i++)
        {
            if (i < results.Count && results[i].Status == FieldFillResult.Applied) { done++; continue; }
            var d = DialogFieldsCore.Describe(vals[i]);
            if (d != null) left.Add(char.ToLowerInvariant(d[0]) + d[1..]);
        }
        if (left.Count == 0) sb.Append($"Filled all {vals.Count} values; close it with {close}{see}.");
        else sb.Append($"Filled {done} of {vals.Count} values; {string.Join(", ", left)} yourself, then close it with {close}{see}.");
        return sb.ToString();
    }
}
