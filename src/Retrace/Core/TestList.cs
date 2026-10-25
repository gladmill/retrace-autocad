using Retrace.Engine.Recordings;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). What the "Open test" window lists: the test folders of the
// Tests root, newest first, each with its last verdict and which files exist. Reads files, so call it off the UI thread.

/// <summary>One test folder as the "Open test" window shows it.</summary>
public sealed record TestEntry(
    string Folder, string Name, DateTime Started, string? Verdict,
    bool HasSteps, bool HasDocx, bool HasXlsx, bool HasShots, bool HasResults, bool HasLatest, bool DocxStale, bool XlsxStale)
{
    public string Recording => Path.Combine(Folder, TestFolders.RecordingFile);
    public string Steps => Path.Combine(Folder, TestFolders.StepsFile);
    public string Docx => Path.Combine(Folder, TestList.DocxFile);
    public string Xlsx => Path.Combine(Folder, TestList.XlsxFile);
    public string Shots => Path.Combine(Folder, TestFolders.ScreenshotsDir);
    public string Results => Path.Combine(Folder, TestFolders.ResultsDir);
    public string Latest => Path.Combine(Results, TestFolders.LatestResult);

    /// <summary>`dtest — 2026-10-10 02:43 — INCOMPLETE` (no verdict part when the test was never run).</summary>
    public string Label => TestList.Label(Name, Started, Verdict);
    public override string ToString() => Label;
}

public static class TestList
{
    public const string DocxFile = "steps.docx";
    public const string XlsxFile = "steps.xlsx";

    static readonly Regex DatePrefix = Rx.Create(@"^\d{4}-\d{2}-\d{2}_\d{4}_", RegexOptions.CultureInvariant);

    /// <summary>`2026-10-10_0243_dtest` -> `dtest` (the folder name without the date the recorder put in front; a clash suffix `_2` stays).</summary>
    public static string NameOf(string folderName)
    {
        var n = DatePrefix.Replace(folderName, "").Trim();
        return n.Length == 0 ? folderName : n;
    }

    public static string Label(string name, DateTime started, string? verdict) =>
        $"{name} — {started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}" + (string.IsNullOrEmpty(verdict) ? "" : $" — {verdict}");

    /// <summary>`verdict` of `results\latest.json`, or null if there is none or it cannot be read.</summary>
    public static string? ReadVerdict(string testFolder)
    {
        try
        {
            var f = Path.Combine(testFolder, TestFolders.ResultsDir, TestFolders.LatestResult);
            if (!File.Exists(f)) return null;
            using var d = JsonDocument.Parse(File.ReadAllText(f));
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>True if <paramref name="file"/> is missing or older than the script it was made from.</summary>
    public static bool IsStale(string steps, string file)
    {
        try
        {
            if (!File.Exists(file)) return true;
            if (!File.Exists(steps)) return false;
            return File.GetLastWriteTimeUtc(steps) > File.GetLastWriteTimeUtc(file).AddSeconds(2);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Reads one test folder (null if it has no recording.jsonl).</summary>
    public static TestEntry? Inspect(string folder)
    {
        try
        {
            var rec = Path.Combine(folder, TestFolders.RecordingFile);
            if (!File.Exists(rec)) return null;
            var steps = Path.Combine(folder, TestFolders.StepsFile);
            var docx = Path.Combine(folder, DocxFile);
            var xlsx = Path.Combine(folder, XlsxFile);
            var results = Path.Combine(folder, TestFolders.ResultsDir);
            bool hasD = File.Exists(docx), hasX = File.Exists(xlsx);
            return new TestEntry(folder, NameOf(Path.GetFileName(folder.TrimEnd('\\', '/'))), TestFolders.StartedOf(rec), ReadVerdict(folder),
                File.Exists(steps), hasD, hasX, Directory.Exists(Path.Combine(folder, TestFolders.ScreenshotsDir)), Directory.Exists(results),
                File.Exists(Path.Combine(results, TestFolders.LatestResult)), hasD && IsStale(steps, docx), hasX && IsStale(steps, xlsx));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Every test folder under <paramref name="root"/> (one level down; staging folders starting with '.' skipped), newest first,
    /// ties by folder name.</summary>
    public static List<TestEntry> Read(string root)
    {
        var list = new List<TestEntry>();
        try
        {
            if (!Directory.Exists(root)) return list;
            foreach (var d in Directory.EnumerateDirectories(root))
                if (!Path.GetFileName(d).StartsWith('.') && Inspect(d) is { } e) list.Add(e);
        }
        catch (Exception) { }
        return Order(list);
    }

    public static List<TestEntry> Order(IEnumerable<TestEntry> entries) =>
        entries.OrderByDescending(e => e.Started).ThenBy(e => e.Folder, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Entries whose label (name, date, verdict) contains every word of <paramref name="text"/>, ignoring case.</summary>
    public static List<TestEntry> Filter(IEnumerable<TestEntry> entries, string? text)
    {
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Where(e => words.All(w => e.Label.Contains(w, StringComparison.CurrentCultureIgnoreCase) || Path.GetFileName(e.Folder).Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }
}
