using System.Text.Json;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>The Retrace settings in force when a recording started, written into its header as one `settings` object, so a recording that is sent on shows exactly how the test was made.
/// Additive: a recording without it (made before) is read as before, and a replay always uses the current settings, never the recorded ones.</summary>
public static class SettingsHeader
{
    public const string Field = "settings";

    /// <summary>Settings that are kept out of the header: the five folder and program paths (they name a person's folders and machine; the header carries the template path already, which is the one
    /// path a replay needs). Every other setting is in the header. A test checks that a new setting is either written or listed here.</summary>
    public static readonly string[] LeftOut = { "sourceFolder", "docsFolder", "testsFolder", "capturesFolder", "editorPath" };

    /// <summary>The settings as the header stores them, keyed as in settings.json (values normalized, lists cleaned as the recorder and replay read them).</summary>
    public static Dictionary<string, object?> Snapshot(RetraceSettings s) => new()
    {
        ["screenshots"] = s.Screenshots,
        ["screenshotMode"] = s.ScreenshotMode,
        ["showClickArrows"] = s.ShowClickArrows,
        ["testDocArrows"] = s.TestDocArrows,
        ["postTestZoom"] = s.PostTestZoom,
        ["recordZoom"] = s.RecordZoom,
        ["promptStyle"] = s.UiPrompts ? PromptStyles.Ui : PromptStyles.CommandLine,
        ["dialogValues"] = s.DialogValues,
        ["autoFillDialogs"] = s.AutoFillDialogs,
        ["showDrivenUi"] = s.ShowDrivenUi,
        ["reviewCheckpoints"] = s.ReviewCheckpoints,
        ["autoDetectBehavior"] = s.AutoDetectBehavior,
        ["checkModelSpace"] = s.CheckModelSpace,
        ["checkBehaviors"] = s.EffectiveCheckBehaviors(),
        ["recordBehaviors"] = s.EffectiveRecordBehaviors(),
        ["sysvarsAdd"] = Names(s.SysvarsAdd),
        ["sysvarsRemove"] = Names(s.SysvarsRemove),
        ["basicCheck"] = s.BasicCheck,
        ["basicCheckItems"] = s.EffectiveBasicCheckItems(),
        ["exportDocx"] = s.ExportDocx,
        ["exportXlsx"] = s.ExportXlsx,
        ["tdocBringToFront"] = s.TdocBringToFront,
        ["tdocAnnotate"] = s.TdocAnnotate,
        ["tdocStepDelayMs"] = s.TdocStepDelayMs,
        ["suiteStepTimeoutSeconds"] = s.SuiteStepTimeoutSeconds,
        ["statusBoard"] = s.StatusBoard,
        ["ribbonTabPosition"] = s.RibbonTabPosition,
        ["recordStartTab"] = s.RecordStartTab,
        ["checkpointKey"] = s.CheckpointChord(out _)?.Format() ?? "None",   // as the shortcut is used: an invalid value falls back to the default, off is "None"
        ["loadedCustomization"] = s.LoadedCustomization,
        ["captureRibbon"] = s.CaptureRibbon,
        ["captureAllWorkspaces"] = s.CaptureAllWorkspaces,
    };

    /// <summary>System variable names as the behavior reads them: trimmed, no blanks, no duplicates (compared without case, first spelling wins).</summary>
    static List<string> Names(IEnumerable<string>? names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (names ?? Enumerable.Empty<string>()).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0 && seen.Add(n)).ToList();
    }

    /// <summary>The `settings` object of the header event as name / value texts in file order ("true", "smart", "modelspace, layers"), or null when the header has none (older recordings).</summary>
    public static List<(string Name, string Value)>? FromHeader(IReadOnlyDictionary<string, JsonElement> header)
    {
        if (!header.TryGetValue(Field, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        var l = new List<(string, string)>();
        foreach (var p in o.EnumerateObject()) l.Add((p.Name, Text(p.Value)));
        return l;
    }

    /// <summary>The screenshot mode a recording was made with: its header's `screenshotMode`; a header without it, or no `settings` object at all (made before the key existed), was made with classic
    /// (never the current default).</summary>
    public static string ModeOf(List<(string Name, string Value)>? recorded) =>
        recorded?.FirstOrDefault(r => r.Name == "screenshotMode") is { Name: not null } m ? ScreenshotModes.Normalize(m.Value) : ScreenshotModes.Classic;

    /// <summary>As <see cref="ModeOf(List{ValueTuple{string,string}})"/>, from a recording's header fields.</summary>
    public static string ModeOf(IReadOnlyDictionary<string, JsonElement>? header) => ModeOf(header == null ? null : FromHeader(header));

    static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Array => v.GetArrayLength() == 0 ? "none" : string.Join(", ", v.EnumerateArray().Select(Text)),
        _ => v.ToString(),
    };

    /// <summary>The compact line of the test document: only what is not at its default, e.g. "Settings: screenshotMode smart, recordZoom extents, basicCheck true". Null when the recording has no
    /// settings object; "Settings: all at their defaults" when nothing differs.</summary>
    public static string? Line(List<(string Name, string Value)>? recorded)
    {
        if (recorded == null) return null;
        var dflt = new Dictionary<string, string>();
        foreach (var (k, v) in Snapshot(new RetraceSettings())) dflt[k] = Text(JsonSerializer.SerializeToElement(v));
        // a settings object without the screenshot mode (made before it existed) was made with classic, which is not the default now
        if (!recorded.Any(r => r.Name == "screenshotMode")) recorded = new List<(string Name, string Value)>(recorded) { ("screenshotMode", ScreenshotModes.Classic) };
        var diff = recorded.Where(r => !dflt.TryGetValue(r.Name, out var d) || d != r.Value).Select(r => $"{r.Name} {(r.Value.Length == 0 ? "(empty)" : r.Value)}").ToList();
        return diff.Count == 0 ? "Settings: all at their defaults" : "Settings (not default): " + string.Join(", ", diff);
    }
}
