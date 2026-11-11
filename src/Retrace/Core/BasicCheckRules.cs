using System.Text.Json;
using Retrace.Engine.Recordings;
using System.Text.Json.Serialization;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// The basic check: counts taken in the drawing AT THE END of a test (model-space entities, layers, paper-space layouts) and compared with the same counts
/// at the end of the recording. Independent of checkpoints. See docs/recording-format.md ("Basic check").
/// </summary>
public static class BasicCheckRules
{
    public const string ModelSpace = "modelspace", Layers = "layers", Layouts = "layouts";
    public static readonly string[] AllItems = { ModelSpace, Layers, Layouts };

    /// <summary>The event the recorder writes right before `session_end`.</summary>
    public const string EndCountsEvent = "end_counts";

    /// <summary>Where the expected counts came from (`source` in the result).</summary>
    public const string FromEndCounts = "end_counts", FromLastCheckpoint = "lastCheckpoint", FromNothing = "none";

    /// <summary>Known item ids from <paramref name="items"/>, matched without case, no duplicates, in the fixed order modelspace, layers, layouts.</summary>
    public static List<string> Clean(IEnumerable<string?>? items)
    {
        var set = new HashSet<string>((items ?? Array.Empty<string?>()).Select(x => (x ?? "").Trim().ToLowerInvariant()), StringComparer.Ordinal);
        return AllItems.Where(set.Contains).ToList();
    }

    public static string Label(string item) => item switch { ModelSpace => "model space", Layers => "layers", Layouts => "layouts", _ => item };

    /// <summary>The three counts of a drawing; null = not known / could not be read.</summary>
    public sealed record Counts(int? ModelSpace, int? Layers, int? Layouts)
    {
        public int? Of(string item) => item switch { BasicCheckRules.ModelSpace => ModelSpace, BasicCheckRules.Layers => Layers, BasicCheckRules.Layouts => Layouts, _ => null };
    }

    /// <summary>A layer name that comes from an external reference ("XREF|WALLS"): not counted.</summary>
    public static bool IsXrefLayer(string name) => name.Contains('|');

    /// <summary>The note on a basic check that could not use the last checkpoint of an older recording.</summary>
    public const string StepsAfterLastCheckpoint = "recorded before end counts existed; steps after the last checkpoint";

    /// <summary>Events that can change the drawing (what a person or a command did), after the last checkpoint: the plugin's own commands and ribbon tab do not count.</summary>
    static bool ChangesDrawing(JsonElement e) => Str(e, "type") switch
    {
        "cmd_start" => !OwnCommands.IsOwnCommand(Str(e, "command")),
        "ribbon" => !OwnCommands.IsOwnTab(Str(e, "tabId")) && !OwnCommands.IsOwnCommand(Str(e, "id")),
        "lisp_start" or "palette_click" or "palette_values" or "native_click" or "native_select" or "native_value" or "dialog_click" or "dialog_values" or "context_menu" or "doc_created" => true,
        _ => false,
    };

    /// <summary>The expected counts of a recording, where they came from and, when something is not known, why. The last `end_counts` event wins. A recording without
    /// one (made before it existed) falls back to the last `mark` that holds a state: its entity `count` and its `layers` (xref layers left out), layouts not known, but
    /// only when nothing that can change the drawing follows that mark (<see cref="ChangesDrawing"/>); else the counts are not known and the note says why. No `end_counts`
    /// and no mark with a state: nothing is known (source "none").</summary>
    public static (Counts Counts, string Source, string? Note) ExpectedFrom(IEnumerable<JsonElement> events)
    {
        Counts? end = null; Counts? mark = null; bool changedAfterMark = false;
        foreach (var e in events)
        {
            var type = Str(e, "type");
            if (type == EndCountsEvent) end = new Counts(Int(e, "modelspace"), Int(e, "layers"), Int(e, "layouts"));
            else if (type == "mark")
            {
                changedAfterMark = false;
                mark = null;
                if (e.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object)
                {
                    int? n = Int(st, "count");
                    int? l = st.TryGetProperty("layers", out var la) && la.ValueKind == JsonValueKind.Array
                        ? la.EnumerateArray().Count(x => x.ValueKind == JsonValueKind.String && !IsXrefLayer(x.GetString() ?? "")) : null;
                    if (n != null || l != null) mark = new Counts(n, l, null);
                }
            }
            else if (ChangesDrawing(e)) changedAfterMark = true;
        }
        if (end != null) return (end, FromEndCounts, null);
        if (mark != null && changedAfterMark) return (new Counts(null, null, null), FromNothing, StepsAfterLastCheckpoint);
        if (mark != null) return (mark, FromLastCheckpoint, null);
        return (new Counts(null, null, null), FromNothing, null);
    }

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= 0 ? i : null;

    /// <summary>Compares each chosen item. An item is verified only when both sides are known; the others have `pass` null ("not verified").</summary>
    public static BasicCheckResult Evaluate(IEnumerable<string> items, Counts expected, Counts? actual, string source, string? note = null)
    {
        var r = new BasicCheckResult { Source = source, Note = note };
        foreach (var item in Clean(items))
        {
            var ex = expected.Of(item); var ac = actual?.Of(item);
            r.Items.Add(new BasicCheckItem { Item = item, Expected = ex, Actual = ac, Pass = ex != null && ac != null ? ex == ac : null });
        }
        r.Pass = r.Items.Any(i => i.Pass == false) ? false : r.Items.Any(i => i.Pass == true) ? true : null;
        return r;
    }
}

public sealed class BasicCheckItem
{
    [JsonPropertyName("item")] public string Item { get; set; } = "";
    /// <summary>The count at the end of the recording; left out when not known.</summary>
    [JsonPropertyName("expected")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Expected { get; set; }
    /// <summary>The count at the end of the test; left out when it could not be read.</summary>
    [JsonPropertyName("actual")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Actual { get; set; }
    /// <summary>true = equal, false = different, null = not verified (one side is not known).</summary>
    [JsonPropertyName("pass")] public bool? Pass { get; set; }
}

/// <summary>`basicCheck` in the result file.</summary>
public sealed class BasicCheckResult
{
    [JsonPropertyName("items")] public List<BasicCheckItem> Items { get; set; } = new();
    /// <summary>false when any verified item differs, true when at least one was verified and all verified ones match, null when nothing was verified.</summary>
    [JsonPropertyName("pass")] public bool? Pass { get; set; }
    /// <summary>Where the expected counts came from: `end_counts` (the recording's own event), `lastCheckpoint` (older recording) or `none`.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = BasicCheckRules.FromNothing;
    /// <summary>Why nothing could be counted (the test closed its drawing, the drawing could not be read); left out otherwise.</summary>
    [JsonPropertyName("note")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; set; }

    /// <summary>`Basic check: model space 12 = 12, layers 5 ≠ 3, layouts not verified` (recorded, then replay).</summary>
    public string Line()
    {
        var parts = Items.Select(i => i.Pass switch
        {
            true => $"{BasicCheckRules.Label(i.Item)} {i.Expected} = {i.Actual}",
            false => $"{BasicCheckRules.Label(i.Item)} {i.Expected} ≠ {i.Actual}",
            _ => $"{BasicCheckRules.Label(i.Item)} not verified",
        });
        return "Basic check: " + (Items.Count == 0 ? "no items chosen" : string.Join(", ", parts)) + (Note != null ? $" ({Note})" : "");
    }
}
