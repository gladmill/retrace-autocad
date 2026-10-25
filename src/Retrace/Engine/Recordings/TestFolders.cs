// The on-disk layout of recorded tests: one folder per recording (see docs/output-layout.md).
using System.Globalization;

namespace Retrace.Engine.Recordings;

/// <summary>Names and discovery of test folders: `&lt;tests folder&gt;\&lt;yyyy-MM-dd_HHmm&gt;_&lt;name&gt;\{recording.jsonl, steps.md, screenshots\, results\}`.
/// Only test folders (and an explicit recording.jsonl path) are read.  The one implementation, used by the engine and by the plugin (Core).</summary>
public static class TestFolders
{
    public const string RecordingFile = "recording.jsonl";
    public const string StepsFile = "steps.md";
    public const string ScreenshotsDir = "screenshots";
    public const string UnusedDir = "unused";
    public const string ResultsDir = "results";
    public const string LatestResult = "latest.json";

    /// <summary>True for `.../recording.jsonl`: a recording inside a test folder.</summary>
    public static bool IsRecording(string path) => string.Equals(Path.GetFileName(path), RecordingFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>The test folder's name part: the recording's name with characters Windows forbids in a folder name replaced by '-'.</summary>
    public static string CleanName(string? name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in (name ?? "").Trim()) sb.Append(c < ' ' || "<>:\"/\\|?*".Contains(c) ? '-' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length > 80) s = s[..80].TrimEnd('.', ' ');
        return s.Length == 0 ? "test" : s;
    }

    /// <summary>`2026-10-10_0243_dtest`.</summary>
    public static string FolderName(string? name, DateTime started) =>
        $"{started.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture)}_{CleanName(name)}";

    /// <summary>A folder path under <paramref name="root"/> that does not exist yet: the name, else "name_2", "name_3"...</summary>
    public static string UniqueFolder(string root, string folderName)
    {
        var p = Path.Combine(root, folderName);
        for (int i = 2; Directory.Exists(p) || File.Exists(p); i++) p = Path.Combine(root, $"{folderName}_{i}");
        return p;
    }

    /// <summary>The header's `started` (ISO 8601, as the recorder wrote it) as the clock time written there, else null.</summary>
    public static DateTime? ParseStarted(string? started) =>
        DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.DateTime : null;

    /// <summary>`yyyy-MM-dd_HH-mm-ss`: a result file's stamp.</summary>
    public static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    static bool Skip(string dir) => Path.GetFileName(dir).StartsWith('.');   // staging folders

    /// <summary>The recording's name: its test folder's name.</summary>
    public static string NameOf(string recordingPath) =>
        new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(recordingPath)) ?? "").Name;

    /// <summary>`steps.md` next to a recording.jsonl.</summary>
    public static string ScriptPath(string recordingPath) => Path.Combine(Path.GetDirectoryName(recordingPath) ?? "", StepsFile);

    /// <summary>When the recording started: the header's `started`, else the file's time.</summary>
    public static DateTime StartedOf(string recordingPath)
    {
        try
        {
            using var fs = new FileStream(recordingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);   // a recording in progress is open for writing
            using var sr = new StreamReader(fs);
            var first = sr.ReadLine();
            if (first != null)
            {
                using var d = System.Text.Json.JsonDocument.Parse(first);
                if (d.RootElement.TryGetProperty("started", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
                    && ParseStarted(s.GetString()) is { } t) return t;
            }
        }
        catch (Exception) { }
        try { return File.GetLastWriteTime(recordingPath); } catch (Exception) { return DateTime.MinValue; }
    }

    /// <summary>`root\*\recording.jsonl` (one level down), newest first: by the header's `started`, then the file's time, then path. Never throws.</summary>
    public static List<string> FindRecordings(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateDirectories(root).Where(d => !Skip(d)).Select(d => Path.Combine(d, RecordingFile)).Where(File.Exists)
                .OrderByDescending(StartedOf).ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>The recording that started last, or null.</summary>
    public static string? Newest(string root) => FindRecordings(root).FirstOrDefault();

    /// <summary>Every recording of a folder: its test folders. If <paramref name="root"/> is itself a test folder, its recording.</summary>
    public static List<string> FindAll(string root)
    {
        var self = Path.Combine(root, RecordingFile);
        return File.Exists(self) ? [self] : FindRecordings(root);
    }
}
