// What the evidence workbook adds to the sign-off sheet: the tester's (or an automated run's) pictures per row, results and notes, and the lines that say who or what made them.
// NO AutoCAD references in this file (linked into the engine test project).
using System.Globalization;
using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Recordings;

/// <summary>One row of the evidence workbook. <paramref name="Pictures"/>: full paths of PNG files, in the order they stack in the Evidence cell. <paramref name="Result"/>: Pass, Fail, Blocked or Not run
/// (null = left as "Not run", for the tester). <paramref name="Notes"/>: written into the Notes cell.</summary>
public sealed record EvidenceRow(IReadOnlyList<string> Pictures, string? Result = null, string? Notes = null);

/// <summary>The evidence of a test, keyed by the sheet row's label ("3", "3.2"). <see cref="Tester"/> and <see cref="Date"/> go into the sign-off cells; <see cref="InfoLines"/> are extra lines of the Info sheet
/// (an automated run says so there: when, with which Retrace and AutoCAD).</summary>
public sealed class EvidenceData
{
    public Dictionary<string, EvidenceRow> Rows { get; } = new(StringComparer.Ordinal);
    public string Tester { get; set; } = "";
    public string Date { get; set; } = "";
    public List<(string Label, string Value)> InfoLines { get; } = [];
    /// <summary>The sheet's own note over the Evidence column (I3); null = <see cref="ExportXlsx.EvidenceFilledHint"/>.</summary>
    public string? Hint { get; set; }
}

/// <summary>Where the pictures of a test's evidence live and how they are named: `&lt;test folder&gt;\evidence\step-&lt;row&gt;_&lt;yyyyMMdd-HHmmss-fff&gt;.png`. A picture taken away is moved to `evidence\removed\`, never deleted.
/// An automated run keeps its pictures in `evidence\auto_&lt;yyyyMMdd-HHmmss&gt;\step-&lt;row&gt;.png`, apart from the tester's.</summary>
public static class EvidenceFiles
{
    public const string Dir = "evidence", RemovedDir = "removed", WorkbookPrefix = "steps-evidence_";

    static readonly Regex Name = Rx.Create(@"^step-(?<row>\d+(?:\.\d+)?)_(?<stamp>\d{8}-\d{6}-\d{3})\.png$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string FolderOf(string testFolder) => Path.Combine(testFolder, Dir);

    /// <summary>The file name of a picture of row <paramref name="row"/> taken at <paramref name="now"/>.</summary>
    public static string FileName(string row, DateTime now) => $"step-{row}_{now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.png";

    /// <summary>A path for a new picture of the row; a name that exists already (two pictures in one millisecond) is moved on by a millisecond, so the name stays one this class lists.</summary>
    public static string NewPath(string testFolder, string row, DateTime now)
    {
        var dir = FolderOf(testFolder);
        var p = Path.Combine(dir, FileName(row, now));
        for (int i = 1; File.Exists(p) && i < 1000; i++) p = Path.Combine(dir, FileName(row, now.AddMilliseconds(i)));
        return p;
    }

    /// <summary>The most pictures of one row the workbook takes (a stack of more would need columns beyond what is sensible); the rest stay in the evidence folder.</summary>
    public const int MaxPicturesPerRow = 60;

    const string StepsStampFile = ".steps-sha";

    /// <summary>The steps of a steps.md: from its "## Steps" heading to the "## Result" one. The intro above (title, recorded line, how-to lines) is not what pictures depend on, so a change there is no change of the steps. A text with no such heading counts as a whole.</summary>
    public static string StepsPart(string? stepsText)
    {
        var text = stepsText ?? "";
        int a = text.IndexOf("## Steps", StringComparison.Ordinal);
        if (a < 0) return text;
        int b = text.IndexOf("\n## Result", a, StringComparison.Ordinal);
        return b < 0 ? text[a..] : text[a..b];
    }

    /// <summary>A fingerprint of the steps of a steps.md, kept beside the pictures: a picture belongs to a row by its number, so when the steps change (a test recorded again into the same folder, steps.md edited) the pictures may sit on other rows.</summary>
    public static string StepsFingerprint(string stepsText) => Hash(StepsPart(stepsText));

    static string Hash(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? ""))).Substring(0, 16);
    }

    /// <summary>The "Files in this folder" section an older version wrote between the intro and "## Steps" (the stamp of its time is the fingerprint of the whole text with it in).</summary>
    const string OldFilesSection = "## Files in this folder\n\n| File | What it is for |\n|---|---|\n| `steps.md` | This page: the steps to follow by hand. It is written again each time the recording is processed, so do not edit it. |\n| `recording.jsonl` | The recording itself, one event per line. This is what **Run test** replays. |\n| `screenshots` | Pictures of the screen, named after the step that shows them. `unused` holds the ones this page does not show. |\n| `results` | One file per automatic run, named by date and verdict (`2026-10-10_03-15-02_PASS.json`). `latest.json` is a copy of the newest. |\n\nTo run this test automatically: in AutoCAD open the **" + "Retr" + "ace** tab, click **Run test** and pick `recording.jsonl` in this folder.\n\n";

    /// <summary>The fingerprints an existing stamp may hold for this text: the steps part (what is written now), the whole text (the first version), and the whole text with the older versions' Files section put back before "## Steps"
    /// (what a stamp made before the section was dropped holds; the text read now no longer has it). So a stamp made earlier stays valid while the steps are the same.</summary>
    static IEnumerable<string> AcceptedFingerprints(string stepsText)
    {
        var text = stepsText ?? "";
        yield return StepsFingerprint(text);
        yield return Hash(text);
        int a = text.IndexOf("## Steps", StringComparison.Ordinal);
        if (a >= 0 && !text.Contains("## Files in this folder", StringComparison.Ordinal)) yield return Hash(text[..a] + OldFilesSection + text[a..]);
    }

    /// <summary>false when pictures exist whose steps.md differs from the one they were taken against. Only reads: no pictures, or no fingerprint, is true.</summary>
    public static bool StepsUnchanged(string testFolder, string stepsText)
    {
        var stamp = Path.Combine(FolderOf(testFolder), StepsStampFile);
        try
        {
            if (!File.Exists(stamp) || List(testFolder).Count == 0) return true;
            return AcceptedFingerprints(stepsText).Contains(File.ReadAllText(stamp).Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Writes the fingerprint after a picture was taken: when there is none yet, or when this is the only picture of the test (the earlier ones were all removed: the stamp starts again).</summary>
    public static void StampSteps(string testFolder, string stepsText)
    {
        try
        {
            var dir = FolderOf(testFolder);
            Directory.CreateDirectory(dir);
            var stamp = Path.Combine(dir, StepsStampFile);
            if (!File.Exists(stamp) || List(testFolder).Values.Sum(l => l.Count) <= 1) File.WriteAllText(stamp, StepsFingerprint(stepsText));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The row a picture file belongs to ("3.2"), null when its name is not one of ours.</summary>
    public static string? RowOf(string file)
    {
        var m = Name.Match(Path.GetFileName(file));
        return m.Success ? m.Groups["row"].Value : null;
    }

    /// <summary>The tester's pictures of a test (not the removed ones, not an automated run's): row -> files, oldest first.</summary>
    public static Dictionary<string, List<string>> List(string testFolder)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var dir = FolderOf(testFolder);
        if (!Directory.Exists(dir)) return map;
        foreach (var f in Directory.EnumerateFiles(dir, "step-*.png").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
            if (RowOf(f) is { } row) { if (!map.TryGetValue(row, out var l)) map[row] = l = []; l.Add(f); }
        return map;
    }

    /// <summary>Moves a picture to `evidence\removed\` (the file keeps its name; a clash gets a number). Returns the new path.</summary>
    public static string Remove(string file)
    {
        var removed = Path.Combine(Path.GetDirectoryName(file) ?? "", RemovedDir);
        Directory.CreateDirectory(removed);
        var target = Path.Combine(removed, Path.GetFileName(file));
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(removed, Path.GetFileNameWithoutExtension(file) + $"_{i}.png");
        File.Move(file, target);
        return target;
    }

    /// <summary>The path of a new workbook beside steps.xlsx: `steps-evidence_20261117-101500.xlsx`; never one that exists.</summary>
    public static string WorkbookPath(string testFolder, DateTime now)
    {
        var stem = Path.Combine(testFolder, WorkbookPrefix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var p = stem + ".xlsx";
        for (int i = 2; File.Exists(p); i++) p = stem + $"_{i}.xlsx";
        return p;
    }
}

/// <summary>How the pictures of one row sit in its Evidence cell: stacked top to bottom, each sized by the sheet's rule (420 px wide, at most 380 px high, never enlarged), a 4 px inset above and between them. The row
/// may not be taller than Excel's 409 pt: when the stack is taller the pictures all shrink by the same factor, down to half size; with more pictures than that holds, the rest go to the next column (Evidence 2, ...).</summary>
public static class EvidenceLayout
{
    public const long Inset = 38100;                        // 4 px in EMU
    public const double MaxRowPoints = 409, MinScale = 0.5;
    const double EmuPerPoint = 12700;

    /// <summary>One picture: which column group (0 = Evidence, 1 = Evidence 2 ...), its size and its top offset in the row, in EMU.</summary>
    public readonly record struct Placed(int Group, long Cx, long Cy, long Top);

    /// <summary>The layout of <paramref name="sizes"/> (pixel sizes, in stacking order) and the height in points each group needs.</summary>
    public static (List<Placed> Pictures, List<double> GroupHeights) Stack(IReadOnlyList<(int W, int H)> sizes)
    {
        long limit = (long)(MaxRowPoints * EmuPerPoint);
        var thumbs = sizes.Select(s => ExportXlsx.Thumb(s.W, s.H)).ToList();
        var placed = new List<Placed>(); var heights = new List<double>();
        int i = 0, group = 0;
        while (i < thumbs.Count)
        {
            // as many pictures as fit this column at half size at the least
            int end = i + 1;
            while (end < thumbs.Count && Scale(thumbs, i, end + 1, limit) >= MinScale) end++;
            double k = Scale(thumbs, i, end, limit);
            long top = Inset;
            for (int j = i; j < end; j++)
            {
                long cx = Math.Max(1, (long)(thumbs[j].Cx * k)), cy = Math.Max(1, (long)(thumbs[j].Cy * k));
                placed.Add(new Placed(group, cx, cy, top));
                top += cy + Inset;
            }
            heights.Add(top / EmuPerPoint);   // the inset after the last picture is the bottom padding
            i = end; group++;
        }
        return (placed, heights);
    }

    /// <summary>The factor (at most 1) that makes thumbs[from..to) stack into a row of at most the limit: insets above, between and below.</summary>
    static double Scale(List<(long Cx, long Cy)> thumbs, int from, int to, long limit)
    {
        long sum = 0;
        for (int j = from; j < to; j++) sum += thumbs[j].Cy;
        long room = limit - Inset * (to - from + 1);
        return sum <= room ? 1.0 : Math.Max(0.0, room / (double)sum);
    }
}
