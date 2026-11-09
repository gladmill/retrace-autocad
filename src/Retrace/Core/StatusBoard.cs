using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Retrace.Engine.Recordings;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). The AutoCAD status board: Retrace writes one small JSON file
// (`retrace-<acad pid>.json`) that says what it is doing, so a person or a tool can see it from outside AutoCAD. The file contract is
// CONTRACT.md of the AcadStatus project (v1). This file is the pure part: the texts, the JSON, the folder and the atomic write.
// The hooks that collect the facts are AutoCad/StatusBoardHost.cs.

/// <summary>What Retrace is doing, as far as the board is concerned.</summary>
public enum StatusMode { Idle, Record, Replay, Suite, Tdoc }

/// <summary>The raw facts <see cref="StatusBoard.Compose"/> turns into a board entry. The host fills in what it knows and leaves the rest.</summary>
public sealed class StatusFacts
{
    public StatusMode Mode { get; init; }
    /// <summary>The test being recorded or replayed (its name, without the date in front of the folder).</summary>
    public string Test { get; init; } = "";
    /// <summary>Replay: the step being run, 1-based, and how many there are. Record: <see cref="RecordedSteps"/> is used instead.</summary>
    public int Step { get; init; }
    public int Steps { get; init; }
    /// <summary>The command (or title) of that step.</summary>
    public string StepText { get; init; } = "";
    /// <summary>Suite and Tdoc: the test being run is number <see cref="QueueIndex"/> of <see cref="QueueCount"/> (1-based).</summary>
    public int QueueIndex { get; init; }
    public int QueueCount { get; init; }
    /// <summary>Record: the steps recorded so far.</summary>
    public int RecordedSteps { get; init; }
    /// <summary>Something the run is waiting for that needs no person ("filling dialog Plot", "waiting for Next"); goes into the summary.</summary>
    public string? Note { get; init; }
    /// <summary>The run cannot go on without a person, and why. Makes the state needs-person.</summary>
    public string? NeedsPerson { get; init; }
    /// <summary>Idle: how the last run ended ("Last: smoke032 PASS (2/2 checkpoints)").</summary>
    public string? Last { get; init; }
    /// <summary>The last run ended in an ERROR the board wants seen (state error).</summary>
    public bool LastIsError { get; init; }
    /// <summary>The last run ended at a dialog that is still open: state needs-person, with the reason in <see cref="NeedsPerson"/>.</summary>
    public bool LastNeedsPerson { get; init; }
    /// <summary>"user", "suite"... or null when Retrace does not know how the work was started (then the field is left out).</summary>
    public string? Owner { get; init; }
}

/// <summary>Where a running replay is, as the replayer reports it to the host (the host copies it into <see cref="StatusFacts"/>).</summary>
public sealed record ReplayProgress(string Test, int Step, int Steps, string StepText, string? Note, string? NeedsPerson);

/// <summary>One board entry, without the parts that depend on the clock (<see cref="StatusBoard.Build"/> adds those). Equal entries are the same state:
/// <see cref="StatusWriter"/> does not rewrite the file for them except on the heartbeat.</summary>
public sealed record StatusEntry(string State, string Summary, string? Short, string? Owner, string Mode, string? Test, int Step, int Steps,
    string? StepText, int SuiteIndex, int SuiteCount, string? NeedsPerson);

public static class StatusBoard
{
    public const string Source = "retrace";
    public const int Version = 1;
    /// <summary>The file goes stale this many seconds after it was written; the host refreshes it every few seconds.</summary>
    public const int TtlSec = 15;
    public const int MaxSummary = 200, MaxShort = 18, MaxField = 60;

    public const string Running = "running", Idle = "idle", NeedsPerson = "needs-person", Error = "error", Stopped = "stopped";

    // ---- where the file goes ----------------------------------------------------------------------------------------------------------

    /// <summary>`ACAD_STATUS_DIR` when it is set, else `%ProgramData%\Gladmill\acad-status` (<paramref name="commonAppData"/> is %ProgramData%).</summary>
    public static string Folder(Func<string, string?> getEnv, string commonAppData)
    {
        var env = (getEnv("ACAD_STATUS_DIR") ?? "").Trim().Trim('"');
        if (env.Length > 0) return env;
        return Path.Combine(string.IsNullOrWhiteSpace(commonAppData) ? @"C:\ProgramData" : commonAppData, "Gladmill", "acad-status");
    }

    /// <summary>`retrace-4812.json`: the AutoCAD process id is the instance, so two AutoCAD sessions never share a file.</summary>
    public static string FileName(int pid) => $"{Source}-{pid}.json";

    /// <summary>"Retrace 0.6.5": the version without the build metadata (`+commit`) the SDK may append.</summary>
    public static string WriterName(string? version)
    {
        var v = (version ?? "").Trim();
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        return v.Length == 0 ? "Retrace" : "Retrace " + v;
    }

    // ---- text ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>One line of at most <paramref name="max"/> characters: line breaks, tabs and other control characters become spaces, runs of spaces
    /// collapse, and a longer text is cut and ends in "...".</summary>
    public static string Cap(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 8));
        var space = true;   // leading spaces are dropped
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) { if (!space) sb.Append(' '); space = true; continue; }
            sb.Append(c); space = false;
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        if (sb.Length <= max) return sb.ToString();
        var keep = max > 3 ? max - 3 : max;
        if (keep > 0 && char.IsHighSurrogate(sb[keep - 1])) keep--;   // never cut a surrogate pair in half
        return sb.ToString(0, keep) + (max > 3 ? "..." : "");
    }

    static string Nz(string? s) => s ?? "";

    /// <summary>"smoke040, step 4/5 (BREAK)"-style tail of a step: " (BREAK)" or "".</summary>
    static string Paren(string? stepText) => string.IsNullOrWhiteSpace(stepText) ? "" : $" ({Cap(stepText, MaxField)})";

    // ---- the entry ----------------------------------------------------------------------------------------------------------------------

    /// <summary>The board entry for what Retrace is doing. Summary and short follow the contract: at most 200 and 18 characters, one line.</summary>
    public static StatusEntry Compose(StatusFacts f)
    {
        var owner = string.IsNullOrWhiteSpace(f.Owner) ? null : Cap(f.Owner, MaxField);
        var test = Cap(f.Test, MaxField);
        var stepText = Cap(f.StepText, MaxField);
        var person = string.IsNullOrWhiteSpace(f.NeedsPerson) ? null : Cap(f.NeedsPerson, MaxSummary - 60);
        string head, mode, shortText;
        int step = 0, steps = 0, qi = 0, qc = 0;
        switch (f.Mode)
        {
            case StatusMode.Record:
            {
                var n = Math.Max(0, f.RecordedSteps);
                shortText = $"Rec {n}";
                head = $"Recording {(test.Length > 0 ? test : "a test")}: {n} step{(n == 1 ? "" : "s")}";
                mode = "record"; step = n;
                break;
            }
            case StatusMode.Replay:
            {
                step = Math.Max(1, f.Step); steps = Math.Max(step, f.Steps);
                shortText = $"Step {step}/{steps}";
                head = $"Replay {(test.Length > 0 ? test : "a test")}: step {step}/{steps}{Paren(stepText)}";
                mode = "replay";
                break;
            }
            case StatusMode.Suite:
            case StatusMode.Tdoc:
            {
                var label = f.Mode == StatusMode.Suite ? "Suite" : "Tdoc";
                qc = Math.Max(1, f.QueueCount); qi = Math.Clamp(f.QueueIndex, 1, qc);
                shortText = $"{label} {qi}/{qc}";
                head = $"{label} {qi}/{qc}";
                if (test.Length > 0)
                {
                    head += ": " + test;
                    if (f.Steps > 0) { step = Math.Clamp(f.Step, 1, f.Steps); steps = f.Steps; head += $", step {step}/{steps}{Paren(stepText)}"; }
                }
                else head += ": between tests";
                mode = f.Mode == StatusMode.Suite ? "suite" : "tdoc";
                break;
            }
            default:
                return ComposeIdle(f, owner, person);
        }
        string state, summary;
        if (person != null)
        {
            state = NeedsPerson; shortText = "Needs person";
            summary = $"{head} needs a person: {person}";
        }
        else
        {
            state = Running;
            summary = string.IsNullOrWhiteSpace(f.Note) ? head : $"{head}, {Cap(f.Note, 80)}";
        }
        return new StatusEntry(state, Cap(summary, MaxSummary), Cap(shortText, MaxShort), owner, mode, test.Length > 0 ? test : null, step, steps,
            stepText.Length > 0 ? stepText : null, qi, qc, person);
    }

    static StatusEntry ComposeIdle(StatusFacts f, string? owner, string? person)
    {
        var last = Cap(f.Last, MaxSummary);
        if (f.LastNeedsPerson)
            return new StatusEntry(NeedsPerson, last.Length > 0 ? last : "A dialog needs a person", "Needs person", owner, "idle", null, 0, 0, null, 0, 0, person);
        if (f.LastIsError)
            return new StatusEntry(Error, last.Length > 0 ? last : "The last run ended in an error", "Test ERROR", owner, "idle", null, 0, 0, null, 0, 0, null);
        return new StatusEntry(Idle, last.Length > 0 ? last : "Idle", null, null, "idle", null, 0, 0, null, 0, 0, null);
    }

    /// <summary>The entry written when Retrace unloads (or the setting is turned off): the file stays, saying the writer is gone.</summary>
    public static StatusEntry StoppedEntry(string text = "Retrace unloaded") => new(Stopped, Cap(text, MaxSummary), null, null, "idle", null, 0, 0, null, 0, 0, null);

    // ---- how a run ended ----------------------------------------------------------------------------------------------------------

    /// <summary>"Last: smoke032 PASS (2/2 checkpoints)"; a run that is not a PASS adds why: "Last: smoke032 FAIL (1/2 checkpoints): step 3 failed: ...".</summary>
    public static string LastReplay(ReplayResult r)
    {
        var test = TestList.NameOf(Nz(r.Session));
        var total = r.Checkpoints.Count;
        var text = $"Last: {(test.Length > 0 ? test : "test")} {r.Verdict}" + (total > 0 ? $" ({r.Checkpoints.Count(c => c.Pass == true)}/{total} checkpoints)" : "");
        return r.Verdict == ReplayResult.Pass || string.IsNullOrWhiteSpace(r.Message) ? text : text + ": " + r.Message;
    }

    /// <summary>Why the run stopped, for <see cref="StatusFacts.NeedsPerson"/>: the message of the step that needs a person, else the run's message.</summary>
    public static string PersonReason(ReplayResult r)
    {
        var step = r.Steps.LastOrDefault(s => s.Status is "needs-person" or "blocked");
        var why = !string.IsNullOrWhiteSpace(step?.Message) ? step!.Message! : r.Message;
        return string.IsNullOrWhiteSpace(why) ? "a step needs a person" : why;
    }

    /// <summary>"Last suite PASS: 2 PASS (2 tests)": the counts of a finished suite, in the suite summary's order.</summary>
    public static string LastSuite(SuiteResult r) => "Last " + r.Summary();

    /// <summary>"Last Tdoc: 3 tests (1 FAIL, 2 PASS), 24 pictures"; a stopped run says so. The counts are in the suite summary's order
    /// (<see cref="SuiteResult.CountsText(IEnumerable{string})"/>).</summary>
    public static string LastTdoc(IReadOnlyList<string> verdicts, int pictures, bool stopped)
    {
        var counts = SuiteResult.CountsText(verdicts);
        return $"Last Tdoc: {verdicts.Count} test{(verdicts.Count == 1 ? "" : "s")}" + (counts.Length > 0 ? $" ({counts})" : "")
            + $", {pictures} picture{(pictures == 1 ? "" : "s")}" + (stopped ? ", stopped" : "");
    }

    /// <summary>How a run ended, as the idle entry shows it: the "Last: ..." line, whether it ended in an ERROR (state error), and whether it stopped at a dialog
    /// that is still open (state needs-person, with <see cref="Reason"/>, until the dialog closes).</summary>
    public sealed record StatusEnd(string Last, bool IsError, bool NeedsPerson, string? Reason);

    /// <summary>A replay ended. <paramref name="stoppedForPerson"/>: the replayer stopped because it needs a person (a dialog it cannot fill, a security prompt, a
    /// palette it cannot drive); <paramref name="dialogOpen"/>: a dialog or security prompt is open now. Needs-person only when both hold; an ERROR stays an error.</summary>
    public static StatusEnd EndOfReplay(ReplayResult r, bool stoppedForPerson, bool dialogOpen)
    {
        var error = r.Verdict == ReplayResult.Error;
        var person = !error && stoppedForPerson && dialogOpen;
        return new StatusEnd(LastReplay(r), error, person, person ? PersonReason(r) : null);
    }

    /// <summary>A suite ended. A suite with ERROR tests ends in state error. A suite the gate stopped because a dialog or security prompt stayed open
    /// (<paramref name="blockedByDialog"/>) stays needs-person while that dialog is open (<paramref name="dialogOpen"/>), with the stop reason.</summary>
    public static StatusEnd EndOfSuite(SuiteResult r, bool blockedByDialog, bool dialogOpen)
    {
        var person = blockedByDialog && dialogOpen;
        return new StatusEnd(LastSuite(r), r.Tests.Any(t => t.Verdict == ReplayResult.Error), person, person ? (r.StopReason.Length > 0 ? r.StopReason : "a dialog is open") : null);
    }

    /// <summary>The verdicts of a Tdoc run with NOT-RUN added for each of the <paramref name="planned"/> tests it never got to (it was stopped, or could not go on), as the
    /// suite lists the tests it did not reach.</summary>
    public static IReadOnlyList<string> PadNotRun(IReadOnlyList<string> verdicts, int planned)
    {
        if (verdicts.Count >= planned) return verdicts;
        return verdicts.Concat(Enumerable.Repeat(SuiteTest.NotRun, planned - verdicts.Count)).ToList();
    }

    /// <summary>A Tdoc run ended; a run with an ERROR test ends in state error.</summary>
    public static StatusEnd EndOfTdoc(IReadOnlyList<string> verdicts, int pictures, bool stopped) =>
        new(LastTdoc(verdicts, pictures, stopped), verdicts.Contains(ReplayResult.Error), false, null);

    // ---- recording ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Whether a recorded event is one more step of the test a replay will show ("Rec 12"), by the replay plan's rule (<see cref="Plan.Build"/>): a ribbon click,
    /// a ribbon tab switch (<paramref name="tabId"/>; not Retrace's own tab), a command at the top level (not nested in another command, and not the command a ribbon button or
    /// palette click already counted), a LISP call, a checkpoint, a palette step (its values, or a click; <see cref="StepTally"/> joins the two the plan joins), a click or
    /// selection in a native palette, a drawing opened outside any command or LISP (<paramref name="path"/> is its full path). The recorder asks only for events that are the
    /// person's (not a drawing's start-up LISP). Approximate where the plan looks at more than one event: its merging of a double-click into the click before it, of a native
    /// palette's own window, and of dialogs (not counted here) are left out, so the number is about the replay's step count (an Auto replay also leaves out the tab steps).</summary>
    public static bool CountsAsStep(string type, string? origin, int commandDepth, int lispDepth = 0, string? path = null, string? tabId = null) => type switch
    {
        "ribbon" or "lisp_start" or "mark" or "palette_values" or "palette_click" or "native_click" or "native_select" => true,
        "tab" => !Plan.IsOwnTab(tabId),
        "cmd_start" => commandDepth == 0 && origin is not ("ribbon" or "palette"),
        "doc_created" => commandDepth == 0 && lispDepth == 0 && path != null && DrawingOpen.IsWindowsFullPath(path),
        _ => false,
    };

    /// <summary>`error` entry for when the board itself failed to work out what to say: the file keeps being written (so it does not go stale and read as AutoCAD hung) and says
    /// what failed. The type of the exception only: its message could hold a path or a name.</summary>
    public static StatusEntry ErrorEntry(Exception ex) =>
        new(Error, Cap("status board error: " + ex.GetType().Name, MaxSummary), "Board error", null, "idle", null, 0, 0, null, 0, 0, null);

    // ---- the JSON -----------------------------------------------------------------------------------------------------------------------

    static readonly JsonWriterOptions WriterOptions = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The file's text (UTF-8 when written, no BOM). <paramref name="since"/> is when this state began; null leaves the field out.
    /// <paramref name="pidStart"/> is when process <paramref name="pid"/> started (`pidStart`, UTC): Windows reuses process ids, and a reader that finds the pid alive with
    /// another start time knows it is not the writer. Null (the start time could not be read) leaves the field out.</summary>
    public static string Build(StatusEntry e, int pid, string writer, DateTime nowUtc, DateTime? since, int ttlSec = TtlSec, DateTime? pidStart = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("source", Source);
            w.WriteString("writer", Cap(writer, MaxField));
            w.WriteNumber("pid", pid);
            if (pidStart != null) w.WriteString("pidStart", Utc(pidStart.Value));
            w.WriteString("updated", Utc(nowUtc));
            w.WriteNumber("ttlSec", ttlSec);
            w.WriteString("state", e.State);
            if (!string.IsNullOrEmpty(e.Short)) w.WriteString("short", Cap(e.Short, MaxShort));
            w.WriteString("summary", Cap(e.Summary, MaxSummary));
            if (!string.IsNullOrEmpty(e.Owner)) w.WriteString("owner", Cap(e.Owner, MaxField));
            if (since != null) w.WriteString("since", Utc(since.Value));
            w.WriteStartObject("detail");
            w.WriteString("mode", e.Mode);
            if (!string.IsNullOrEmpty(e.Test)) w.WriteString("test", e.Test);
            if (e.Step > 0 || e.Mode == "record") w.WriteNumber("step", e.Step);
            if (e.Steps > 0) w.WriteNumber("steps", e.Steps);
            if (!string.IsNullOrEmpty(e.StepText)) w.WriteString("stepText", e.StepText);
            if (e.SuiteCount > 0)
            {
                w.WriteStartObject("suite");
                w.WriteNumber("index", e.SuiteIndex);
                w.WriteNumber("count", e.SuiteCount);
                w.WriteEndObject();
            }
            if (!string.IsNullOrEmpty(e.NeedsPerson)) w.WriteString("needsPerson", e.NeedsPerson);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return new UTF8Encoding(false).GetString(ms.ToArray());
    }

    /// <summary>Writes the file the way the contract asks: `&lt;file&gt;.tmp-&lt;pid&gt;` in the same folder, then moved over the file (replace), so a reader
    /// never sees half a file. Creates the folder unless <paramref name="createFolder"/> is false (the writer knows it is there). Throws on failure; <see cref="StatusWriter"/> catches.</summary>
    public static void WriteAtomic(string folder, string fileName, string json, int pid, bool createFolder = true)
    {
        if (createFolder) Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        var tmp = path + ".tmp-" + pid.ToString(CultureInfo.InvariantCulture);
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }
}
