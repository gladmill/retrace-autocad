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
}

/// <summary>One replayed step. `status`: "ok", "failed" (a dialog step that did not happen as recorded) or "needs-person" (unattended run stopped here).</summary>
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

    static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

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
            var path = Path.Combine(dir, stem + ".json");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem}_{i}.json");
            WriteAtomic(path, json);
            WriteAtomic(Path.Combine(dir, TestFolders.LatestResult), json);
            return path;
        }
        catch (Exception ex) { onError?.Invoke(ex); return null; }
    }
}
