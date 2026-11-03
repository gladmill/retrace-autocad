using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Retrace.Engine.Recordings;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). The summary of a suite run (RETRACESUITE): one line per test, written as
// `<stamp>_suite.json` and `<stamp>_suite.md` in the results folder.

/// <summary>A checkpoint that did not match, as the suite summary shows it.</summary>
public sealed class SuiteCheckpoint
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("differences")] public List<string> Differences { get; set; } = new();
}

/// <summary>One test of a suite run.</summary>
public sealed class SuiteTest
{
    /// <summary>The verdicts of a replay, plus these two: the test ended where a person had to act, and the test never ran.</summary>
    public const string NeedsPerson = "NEEDS-PERSON", NotRun = "NOT-RUN";

    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("recording")] public string Recording { get; set; } = "";
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = NotRun;
    [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    /// <summary>The checkpoints that differed (and the ones whose behavior checks differed), with what differed.</summary>
    [JsonPropertyName("failingCheckpoints")] public List<SuiteCheckpoint> FailingCheckpoints { get; set; } = new();
    /// <summary>The per-test result file (`results\&lt;stamp&gt;_&lt;VERDICT&gt;.json` in the test's folder); "" when none was written.</summary>
    [JsonPropertyName("result")] public string Result { get; set; } = "";

    /// <summary>The line for a replay that ran. A replay that stopped at a step that needs a person (a dialog it cannot fill, a security prompt) is
    /// NEEDS-PERSON, not a plain INCOMPLETE.</summary>
    public static SuiteTest From(string recording, ReplayResult r, double seconds)
    {
        var verdict = r.Verdict;
        if (verdict == ReplayResult.Incomplete && r.Steps.Any(s => s.Status is "needs-person" or "blocked")) verdict = NeedsPerson;
        var t = new SuiteTest
        {
            Name = TestList.NameOf(TestFolders.NameOf(recording)), Recording = recording, Verdict = verdict, DurationSeconds = Math.Round(seconds, 1),
            Message = r.Message ?? "", Result = r.WrittenTo ?? "",
        };
        foreach (var c in r.Checkpoints)
        {
            var diffs = c.Differences.Concat(c.CheckDifferences ?? []).ToList();
            if (c.Pass == false) t.FailingCheckpoints.Add(new SuiteCheckpoint { Index = c.Index, Note = c.Note, Differences = diffs });
        }
        return t;
    }

    /// <summary>The line for a test that did not run (it could not start, or the suite stopped before it).</summary>
    public static SuiteTest Skipped(string recording, string why, double seconds = 0) => new()
    {
        Name = TestList.NameOf(TestFolders.NameOf(recording)), Recording = recording, Verdict = NotRun, Message = why, DurationSeconds = Math.Round(seconds, 1),
    };
}

/// <summary>A suite run: what ran, in order, and why it ended.</summary>
public sealed class SuiteResult
{
    public const string Pass = "PASS", Fail = "FAIL", Stopped = "STOPPED";

    [JsonPropertyName("stamp")] public string Stamp { get; set; } = "";
    [JsonPropertyName("startedUtc")] public string StartedUtc { get; set; } = "";
    [JsonPropertyName("finishedUtc")] public string FinishedUtc { get; set; } = "";
    [JsonPropertyName("coreVersion")] public string CoreVersion { get; set; } = "";
    /// <summary>PASS when every test passed; STOPPED when the suite ended early (see <see cref="StopReason"/>); else FAIL.</summary>
    [JsonPropertyName("verdict")] public string Verdict => StopReason.Length > 0 ? Stopped : Tests.Count > 0 && Tests.All(t => t.Verdict == ReplayResult.Pass) ? Pass : Fail;
    /// <summary>Why the suite ended before its last test; "" when it ran to the end.</summary>
    [JsonPropertyName("stopReason")] public string StopReason { get; set; } = "";
    [JsonPropertyName("stepTimeoutSeconds")] public int StepTimeoutSeconds { get; set; }
    [JsonPropertyName("counts")] public Dictionary<string, int> Counts => Tests.GroupBy(t => t.Verdict).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
    [JsonPropertyName("tests")] public List<SuiteTest> Tests { get; set; } = new();

    public const string FileSuffix = "_suite";


    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>"3 PASS, 1 FAIL, 1 ERROR" (counts of the verdicts that occur).</summary>
    public string CountsText() => string.Join(", ", Counts.Select(kv => $"{kv.Value} {kv.Key}"));

    /// <summary>One line: `suite FAIL: 3 PASS, 1 FAIL, 1 ERROR (5 tests)`; a stopped suite also says why.</summary>
    public string Summary() => $"suite {Verdict}: {(Tests.Count == 0 ? "no tests" : CountsText())} ({Tests.Count} test{(Tests.Count == 1 ? "" : "s")})"
        + (StopReason.Length > 0 ? ". Stopped: " + StopReason : "");

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Retrace suite {Stamp}: {Verdict}");
        sb.AppendLine();
        sb.AppendLine(Summary() + ".");
        sb.AppendLine();
        sb.AppendLine($"Started {StartedUtc}, finished {FinishedUtc}, Retrace {CoreVersion}, step timeout {StepTimeoutSeconds} s.");
        sb.AppendLine();
        sb.AppendLine("| # | Test | Verdict | Seconds | What differed | Result |");
        sb.AppendLine("|---|---|---|---|---|---|");
        for (var i = 0; i < Tests.Count; i++)
        {
            var t = Tests[i];
            sb.Append($"| {i + 1} | {Cell(t.Name)} | {t.Verdict} | {t.DurationSeconds.ToString("0.#", CultureInfo.InvariantCulture)} | {Cell(What(t))} | {Cell(t.Result)} |").AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The failing checkpoints with their differences, else the message that explains the verdict.</summary>
    static string What(SuiteTest t)
    {
        if (t.FailingCheckpoints.Count > 0)
            return string.Join(" / ", t.FailingCheckpoints.Select(c => $"checkpoint {c.Index}{(c.Note.Length > 0 ? " \"" + c.Note + "\"" : "")}: {string.Join("; ", c.Differences)}"));
        return t.Verdict == ReplayResult.Pass ? "" : t.Message;
    }

    static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    /// <summary>Lists what the suite did not get to. Returns true when it added anything. A test that was started (<paramref name="inFlight"/>) but has no result is INCOMPLETE when its replay had begun
    /// (<paramref name="inFlightRan"/>), else NOT-RUN; every other test of <paramref name="recordings"/> without a line is NOT-RUN. If anything is missing and
    /// <see cref="StopReason"/> is empty, it is set: a suite never ends as PASS with tests missing.</summary>
    public bool AddMissing(IReadOnlyList<string> recordings, string? inFlight, bool inFlightRan)
    {
        var added = false;
        bool Has(string r) => Tests.Any(t => string.Equals(t.Recording, r, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(inFlight) && !Has(inFlight))
        {
            var t = SuiteTest.Skipped(inFlight, inFlightRan ? "the suite ended while this test ran" : "the suite ended before this test started");
            if (inFlightRan) t.Verdict = ReplayResult.Incomplete;
            Tests.Add(t);
            added = true;
        }
        foreach (var r in recordings)
            if (!Has(r)) { Tests.Add(SuiteTest.Skipped(r, "the suite stopped before this test")); added = true; }
        if (added && StopReason.Length == 0) StopReason = "the suite ended before every test ran";
        return added;
    }

    /// <summary>Writes the .json and the .md (each through a temp file) and returns the .json path. A suite in the same second gets `_suite_2`...</summary>
    public string Write(string resultsDir)
    {
        var stamp = Stamp.Length > 0 ? Stamp : TestFolders.Stamp(DateTime.Now);
        Stamp = stamp;
        var json = UniquePath.Find(i => UniquePath.Numbered(resultsDir, stamp + FileSuffix, ".json", i));
        AtomicFile.WriteText(Path.ChangeExtension(json, ".md"), ToMarkdown());
        AtomicFile.WriteText(json, ToJson());
        return json;
    }

    /// <summary>The newest `*_suite*.json` of the results folder (by write time, then name), or null.</summary>
    public static string? LatestJson(string resultsDir)
    {
        try
        {
            if (!Directory.Exists(resultsDir)) return null;
            return Directory.EnumerateFiles(resultsDir, "*" + FileSuffix + "*.json").Where(f => IsSuiteFile(Path.GetFileName(f)))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ThenByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    /// <summary>`<stamp>_suite.json` or `<stamp>_suite_<n>.json`.</summary>
    static bool IsSuiteFile(string name)
    {
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = name[..^5];
        if (stem.EndsWith(FileSuffix, StringComparison.Ordinal)) return true;
        var k = stem.LastIndexOf(FileSuffix + "_", StringComparison.Ordinal);
        return k > 0 && stem[(k + FileSuffix.Length + 1)..] is { Length: > 0 } n && n.All(char.IsDigit);
    }

    /// <summary>Reads a suite file; null if it is missing or not one.</summary>
    public static SuiteResult? Read(string jsonPath)
    {
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = d.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tests", out _)) return null;
            return JsonSerializer.Deserialize<SuiteResult>(root.GetRawText());
        }
        catch (Exception) { return null; }
    }

    /// <summary>For the Open test window: the newest suite's one-line summary and its .md file; null when no suite ran yet.</summary>
    public static (string Summary, string Md)? Latest(string resultsDir)
    {
        var json = LatestJson(resultsDir);
        if (json == null || Read(json) is not { } r) return null;
        return ($"{r.Stamp}: {r.Summary()}", Path.ChangeExtension(json, ".md"));
    }
}
