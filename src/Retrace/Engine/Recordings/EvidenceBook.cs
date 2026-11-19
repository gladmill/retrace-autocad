// Makes the evidence workbook of a test: from the tester's pictures (the Evidence window) or from an automated replay. The original steps.xlsx is never opened for writing.
// NO AutoCAD references in this file (linked into the engine test project).
using System.Globalization;
using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Recordings;

/// <summary>What the tester said in the Evidence window for one row of the sheet.</summary>
public sealed record ManualRow(string Result, string Notes);

/// <summary>One replayed step as the evidence needs it. <paramref name="T"/>: the step's time in the recording (ms), which names the sheet step it belongs to. <paramref name="Status"/>: ok, failed, needs-person, unverified (a checkpoint with no verdict),
/// stopped (a person pressed Stop here) or "" when the replay never got to it. <paramref name="Waited"/>: the replay waited for a person at this step. <paramref name="Picture"/>: the file of the picture taken after it, if any.</summary>
public sealed record ReplayStepInfo(int Index, long T, string Status, string? Message, string? Picture, bool Waited = false, string Kind = "", string? Opened = null, string? Filled = null, string? Ended = null, string? Failed = null, bool OpenedNoWindow = false);

/// <summary>What the workbook shows for one numbered step of steps.md: Result (Pass, Fail, Blocked, Not run), the Notes text for a failure, the picture.</summary>
/// <paramref name="Rows"/>: the evidence of each row of the step by row number (0 = the step's own row), see <see cref="EvidenceRows"/>; null = the picture goes on the step's own row only.
public sealed record StepOutcome(string Result, string? Notes, string? Picture, IReadOnlyDictionary<int, RowEvidence>? Rows = null);

public static class EvidenceBook
{
    /// <summary>The numbered step (1-based) a replay step with time <paramref name="t"/> belongs to: the last numbered step that began at or before it (a dialog a command opened belongs to the command).
    /// 0 when there are no numbered steps; a time before the first one belongs to the first.</summary>
    public static int StepOf(long t, IReadOnlyList<long> numberedT)
    {
        if (numberedT.Count == 0) return 0;
        int n = 1;
        for (int i = 0; i < numberedT.Count; i++) if (numberedT[i] <= t) n = i + 1;
        return n;
    }

    /// <summary>The outcome of every numbered step the replay touched. Fail when one of its replay steps failed (Notes carry the message); Pass when all of them ran ok; Blocked when one needs a person; the others Not run.
    /// The picture is the one after the last replay step of the step that has one.</summary>
    public static Dictionary<int, StepOutcome> Outcomes(IReadOnlyList<long> numberedT, IReadOnlyList<ReplayStepInfo> steps, IReadOnlyList<IReadOnlyList<Emit.WordingBullet>>? bullets = null, IReadOnlyList<int>? rowCounts = null, IReadOnlyList<IReadOnlyList<bool>>? subRowsFilled = null)
    {
        var by = new SortedDictionary<int, List<ReplayStepInfo>>();
        foreach (var s in steps)
        {
            int n = StepOf(s.T, numberedT);
            if (n == 0) continue;
            if (!by.TryGetValue(n, out var l)) by[n] = l = [];
            l.Add(s);
        }
        var map = new Dictionary<int, StepOutcome>();
        foreach (var (n, list) in by)
        {
            var failed = list.Where(s => s.Status == "failed").ToList();
            string result = failed.Count > 0 ? "Fail"
                : list.Any(s => s.Status == "needs-person") ? "Blocked"
                : list.All(s => s.Status == "ok") ? "Pass" : "Not run";
            var lines = new List<string>();
            foreach (var f in failed) lines.Add(string.IsNullOrWhiteSpace(f.Message) ? "The step failed." : "The step failed: " + Scrub(f.Message));
            foreach (var f in list.Where(s => s.Status == "needs-person")) lines.Add(string.IsNullOrWhiteSpace(f.Message) ? "The replay needed a person here." : Scrub(f.Message));
            foreach (var f in list.Where(s => s.Status is "unverified" or "stopped")) lines.Add(Scrub(f.Message));
            if (list.Any(s => s.Waited)) lines.Add("A person finished a dialog here (the replay waited for them).");
            string? notes = lines.Count == 0 ? null : string.Join("\n", lines.Where(x => x.Length > 0));
            IReadOnlyDictionary<int, RowEvidence>? rows = null;
            if (bullets != null && rowCounts != null && n <= bullets.Count && n <= rowCounts.Count && rowCounts[n - 1] > 0)
                rows = EvidenceRows.Place(n.ToString(CultureInfo.InvariantCulture), list, bullets[n - 1], rowCounts[n - 1],
                    by.TryGetValue(n + 1, out var nextList) && nextList.OrderBy(x => x.Index).FirstOrDefault(x => x.Kind != "tab") is { Kind: "dialog" } nd ? nd.Opened : null,
                    subRowsFilled != null && n <= subRowsFilled.Count ? subRowsFilled[n - 1] : null);
            map[n] = new StepOutcome(result, notes, list.LastOrDefault(s => s.Picture != null)?.Picture, rows);
        }
        return map;
    }

    /// <summary>The workbook data of an automated run: every row of step N gets N's result; the picture and the failure note go on the step's own row (label "N").</summary>
    public static EvidenceData FromOutcomes(StepsDoc doc, IReadOnlyDictionary<int, StepOutcome> outcomes, IEnumerable<(string Label, string Value)> info, string tester, string date)
    {
        var ev = new EvidenceData { Tester = tester, Date = date };
        foreach (var line in info) ev.InfoLines.Add(line);
        foreach (var row in doc.Rows)
        {
            var main = row.Label.Split('.')[0];
            if (!int.TryParse(main, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || !outcomes.TryGetValue(n, out var o)) continue;
            bool own = row.Label == main;
            if (o.Rows == null) { ev.Rows[row.Label] = new EvidenceRow(own && o.Picture != null ? [o.Picture] : [], o.Result, own ? o.Notes : null); continue; }
            int idx = own ? 0 : int.TryParse(row.Label[(main.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : -1;
            o.Rows.TryGetValue(idx, out var re);
            var notes = string.Join("\n", new[] { own ? o.Notes : null, re?.Note }.Where(x => !string.IsNullOrEmpty(x)));
            ev.Rows[row.Label] = new EvidenceRow(re?.Picture != null ? [re.Picture] : [], o.Result, notes.Length == 0 ? null : notes);
        }
        return ev;
    }

    /// <summary>The workbook data of a tester's walk: the pictures of <c>evidence\</c> by row, plus the result and notes the tester gave.</summary>
    public static EvidenceData FromTester(StepsDoc doc, string testFolder, IReadOnlyDictionary<string, ManualRow> rows, string tester, string date)
    {
        var ev = new EvidenceData { Tester = tester, Date = date };
        var files = EvidenceFiles.List(testFolder);
        foreach (var row in doc.Rows)
        {
            files.TryGetValue(row.Label, out var pics);
            rows.TryGetValue(row.Label, out var said);
            var result = said != null && said.Result is "Pass" or "Fail" or "Blocked" ? said.Result : null;   // "Not run" and nothing: the tester's to set in Excel
            var notes = said?.Notes is { Length: > 0 } nt ? nt : null;
            if (pics != null && pics.Count > EvidenceFiles.MaxPicturesPerRow)
            {
                notes = (notes == null ? "" : notes + "\n") + $"{pics.Count - EvidenceFiles.MaxPicturesPerRow} more picture(s) are in the evidence folder (at most {EvidenceFiles.MaxPicturesPerRow} go into a row).";
                pics = pics.Take(EvidenceFiles.MaxPicturesPerRow).ToList();
            }
            if ((pics?.Count ?? 0) > 0 || result != null || notes != null) ev.Rows[row.Label] = new EvidenceRow(pics ?? [], result, notes);
        }
        return ev;
    }

    /// <summary>Writes <paramref name="ev"/> as a new workbook beside steps.xlsx (a name that does not exist yet; through a temp file). Returns its path.</summary>
    public static string Write(StepsDoc doc, string testFolder, EvidenceData ev, DateTime now, Func<string, int, (byte[] Png, int Width, int Height)?>? rescale = null)
    {
        var target = EvidenceFiles.WorkbookPath(testFolder, now);
        var tmp = Path.Combine(testFolder, "." + Path.GetFileName(target) + ".tmp");
        try
        {
            ExportXlsx.Write(doc, testFolder, tmp, rescale, ev);
            File.Move(tmp, target);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        return target;
    }

    static readonly Regex Profile = Rx.Create(@"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\r\n""<>|*?]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A path under a Windows user profile (<c>C:\Users\name</c>, also with forward slashes) is written as <c>%USERPROFILE%</c>: no user name ends up in an evidence workbook. Nothing else is changed: a folder outside a profile
    /// (another drive, a network share) is written as it is.</summary>
    public static string ScrubPaths(string? text) => Profile.Replace(text ?? "", "%USERPROFILE%");

    /// <summary>A message for the workbook: <see cref="ScrubPaths"/>, trimmed, at most 400 characters.</summary>
    public static string Scrub(string? text)
    {
        var t = ScrubPaths(DocText.Guard(text, "evidence workbook")).Trim();
        return t.Length > 400 ? t[..400] + "..." : t;
    }

    /// <summary>The "automated run" lines of the Info sheet. No user name and no computer name: the date and AutoCAD's version (the product's name and version are not in a document).</summary>
    public static List<(string Label, string Value)> AutomatedInfo(DateTime now, string autocadVersion, string verdict, int run, int total, bool anyWaited = false) =>
    [
        ("Run", $"Automated run on AutoCAD {autocadVersion}, {now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}. "
            + (anyWaited ? "The recording was replayed and a picture taken after each step; a person finished some dialogs it could not fill in (see Notes)." : "Nobody clicked anything: the recording was replayed and a picture taken after each step.")),
        ("Verdict", $"{verdict} ({run} of {total} replay steps run)"),
    ];
}
