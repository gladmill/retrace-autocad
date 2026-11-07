using Retrace.Engine.Recordings;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retrace.Core;

// NO AutoCAD references in this file either (linked into the engine test project).

public sealed class CheckpointResult
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    /// <summary>true = matched, false = differed, null = the session recorded no state for this checkpoint (not verified).</summary>
    [JsonPropertyName("pass")] public bool? Pass { get; set; }
    [JsonPropertyName("differences")] public List<string> Differences { get; set; } = new();
    [JsonPropertyName("expected")] public DrawingDigest? Expected { get; set; }
    [JsonPropertyName("actual")] public DrawingDigest? Actual { get; set; }

    /// <summary>Behavior checks (schema 3): "auto" or "manual"; left out when the checkpoint recorded no checks.</summary>
    [JsonPropertyName("checkMode")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CheckMode { get; set; }
    /// <summary>Behavior ids that were verified; left out when the checkpoint recorded no checks.</summary>
    [JsonPropertyName("checkedBehaviors")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? CheckedBehaviors { get; set; }
    /// <summary>What differed in the behavior checks (see <see cref="BehaviorChecks.Compare"/>); left out when the checkpoint recorded no checks, empty when they matched.</summary>
    [JsonPropertyName("checkDifferences")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? CheckDifferences { get; set; }

    /// <summary>Verifies <paramref name="checks"/> against what the drawing holds now and records it here. Does nothing when there are no checks (null).
    /// A difference makes <see cref="Pass"/> false (also when the digest matched). Matching NON-EMPTY checks leave Pass as the digest compare set it, and
    /// make it true when it is null (the mark has no digest but has checks: the checks verify it). EMPTY checks verify nothing: they record the mode and
    /// leave Pass alone (a null Pass stays "not verified"); <paramref name="actual"/> may then be an empty snapshot. The Replayer must call this for
    /// every mark that has checks, also when `Step.State` is null.</summary>
    public void ApplyChecks(BehaviorChecks? checks, BehaviorSnapshot actual)
    {
        if (checks == null) return;
        CheckMode = checks.Mode;
        CheckedBehaviors = checks.Behaviors.Keys.ToList();
        CheckDifferences = BehaviorChecks.Compare(checks, actual);
        if (CheckDifferences.Count > 0) Pass = false;
        else if (checks.Behaviors.Count > 0) Pass ??= true;
    }
}

/// <summary>One segment of a dialog the replay did (see <see cref="DialogSegments"/>): the values it set and the button it pressed. In a results file as `segments[]`.</summary>
public sealed class SegmentResult
{
    public const string Ok = "ok", Failed = "failed", NotRun = "notRun", Person = "person";

    /// <summary>The button pressed after the values; left out for the closing segment (OK or Cancel).</summary>
    [JsonPropertyName("button")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Button { get; set; }
    /// <summary>"ok" (its values went in and the button was pressed), "failed", "notRun" (an earlier segment stopped the replay), or "person" (the replay gave up here and a person did this
    /// segment and the rest by hand; `message` on the first one says why).</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = NotRun;
    [JsonPropertyName("message")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Message { get; set; }
    /// <summary>What happened to each recorded value of this segment; left out when it had none.</summary>
    [JsonPropertyName("fields")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<FieldFillResult>? Fields { get; set; }
    /// <summary>The static texts the dialog showed just before the button was pressed (the closing segment: just before the close); left out when it read none.</summary>
    [JsonPropertyName("statics")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<DialogStatic>? Statics { get; set; }
    /// <summary>The dialogs the button opened and the replay did; left out when none.</summary>
    [JsonPropertyName("nested")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<NestedDialogResult>? Nested { get; set; }
}

/// <summary>A dialog a button opened, as the replay did it.</summary>
public sealed class NestedDialogResult
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("segments")] public List<SegmentResult> Segments { get; set; } = new();
}

/// <summary>One replayed step. `status`: "ok", "failed" (a dialog step that did not happen as recorded), "needs-person" (unattended run stopped here), "blocked" (a security prompt) or "timed-out" (a suite run's step timeout).</summary>
public sealed class StepResult
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "ok";
    /// <summary>A person did something here (filled in a dialog) while the replay waited. Only RETRACERUN (interactive) waits.</summary>
    [JsonPropertyName("waitedForPerson")] public bool WaitedForPerson { get; set; }
    [JsonPropertyName("message")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Message { get; set; }
    /// <summary>The replay filled the dialog in and closed it itself (setting `autoFillDialogs`); left out when false.</summary>
    [JsonPropertyName("autoFilled")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AutoFilled { get; set; }
    /// <summary>What happened to each recorded value the replay tried to fill in; left out when it did not try.</summary>
    [JsonPropertyName("fields")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<FieldFillResult>? Fields { get; set; }
    /// <summary>A Win32 dialog the replay did itself, one entry per recorded click and a last one for the close, with the static texts it showed before each button
    /// and before the close. Left out for a dialog filled through UI Automation and when the replay did not try.</summary>
    [JsonPropertyName("segments")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<SegmentResult>? Segments { get; set; }
}

/// <summary>The result of a replay, written at its end: `results/&lt;stamp&gt;_&lt;VERDICT&gt;.json` + `results/latest.json` in the test folder. See docs/recording-format.md.</summary>
public sealed class ReplayResult
{
    public const string Pass = "PASS", Fail = "FAIL", Incomplete = "INCOMPLETE", Error = "ERROR";

    [JsonPropertyName("session")] public string Session { get; set; } = "";
    [JsonPropertyName("startedUtc")] public string StartedUtc { get; set; } = "";
    [JsonPropertyName("finishedUtc")] public string FinishedUtc { get; set; } = "";
    [JsonPropertyName("coreVersion")] public string CoreVersion { get; set; } = "";
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = Incomplete;
    [JsonPropertyName("stepsTotal")] public int StepsTotal { get; set; }
    [JsonPropertyName("stepsRun")] public int StepsRun { get; set; }
    /// <summary>Did the drawing at the start match the session's initial state? Left out when the session has none.</summary>
    [JsonPropertyName("startStateMatches")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? StartStateMatches { get; set; }
    /// <summary>true for RETRACERUN (waits for a person at dialogs), false for RETRACEREPLAY (stops with INCOMPLETE there).</summary>
    [JsonPropertyName("interactive")] public bool Interactive { get; set; }
    [JsonPropertyName("steps")] public List<StepResult> Steps { get; set; } = new();
    [JsonPropertyName("checkpoints")] public List<CheckpointResult> Checkpoints { get; set; } = new();
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    /// <summary>The history file <see cref="Write"/> made (not stored in the file); a suite summary links to it.</summary>
    [JsonIgnore] public string? WrittenTo { get; set; }

    /// <summary>ERROR beats FAIL beats INCOMPLETE beats PASS. PASS needs a completed run and at least one checkpoint, every one verified and matching.</summary>
    public static string ComputeVerdict(bool completed, bool errored, IReadOnlyList<CheckpointResult> checkpoints, int failedSteps = 0)
    {
        if (errored) return Error;
        if (failedSteps > 0 || checkpoints.Any(c => c.Pass == false)) return Fail;
        if (!completed) return Incomplete;
        if (checkpoints.Count == 0 || checkpoints.Any(c => c.Pass == null)) return Incomplete;
        return Pass;
    }

    /// <summary>"RETRACEREPLAY: PASS 3/3 checkpoints (session x)"; otherwise the message is appended.</summary>
    public string Summary()
    {
        var passed = Checkpoints.Count(c => c.Pass == true);
        var unverified = Checkpoints.Count(c => c.Pass == null);
        return $"RETRACEREPLAY: {Verdict} {passed}/{Checkpoints.Count} checkpoints{(unverified > 0 ? $", {unverified} unverified" : "")} (session {Session})"
            + (Verdict is Pass || string.IsNullOrEmpty(Message) ? "" : ": " + Message);
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Recording in a test folder: the `results` folder next to it.</summary>
    public static string ResultsDir(string sessionPath) => Path.Combine(Path.GetDirectoryName(sessionPath) ?? "", TestFolders.ResultsDir);

    /// <summary>`2026-10-10_03-15-02_PASS.json`: one file per run.</summary>
    public static string HistoryName(DateTime finished, string verdict) => $"{TestFolders.Stamp(finished)}_{verdict}.json";

    /// <summary>The newest result of a recording: `results/latest.json` in its test folder; null if there is none.</summary>
    public static string? LatestFor(string sessionPath)
    {
        var p = Path.Combine(ResultsDir(sessionPath), TestFolders.LatestResult);
        return File.Exists(p) ? p : null;
    }

    static void WriteAtomic(string path, string json) => AtomicFile.WriteText(path, json);

    /// <summary>Writes the result (temp file, then move, so a poller never reads half of it): `results/&lt;stamp&gt;_&lt;VERDICT&gt;.json` (earlier runs stay)
    /// and a copy as `results/latest.json`. Returns the new run's file, or null if nothing worked (the error goes to <paramref name="onError"/>).</summary>
    public string? Write(string sessionPath, Action<Exception>? onError = null)
    {
        var json = ToJson();
        try
        {
            var dir = ResultsDir(sessionPath);
            Directory.CreateDirectory(dir);
            var stem = Path.GetFileNameWithoutExtension(HistoryName(DateTime.Now, Verdict));
            var path = UniquePath.Find(i => UniquePath.Numbered(dir, stem, ".json", i));
            WriteAtomic(path, json);
            WriteAtomic(Path.Combine(dir, TestFolders.LatestResult), json);
            WrittenTo = path;
            return path;
        }
        catch (Exception ex) { onError?.Invoke(ex); return null; }
    }
}
