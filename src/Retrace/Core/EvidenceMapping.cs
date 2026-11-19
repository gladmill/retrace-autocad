// NO AutoCAD references in this file (linked into the engine test project). How a finished replay becomes the outcome of each numbered step of steps.md for the evidence workbook.
using Retrace.Engine.Recordings;

namespace Retrace.Core;

/// <summary>How the replay ended: it ran to the end, an error ended it, it stopped for a person (a dialog it could not fill, a security prompt), or a person pressed Stop.</summary>
public enum EvidenceEnd { Completed, Error, Person, Stopped }

public static class EvidenceMapping
{
    public const string NotVerified = "Replay ran every step; nothing verified the result (verdict INCOMPLETE): a person must check.";

    /// <summary>The outcome of every numbered step of the script. <paramref name="numberedT"/>: the time of each numbered step of steps.md; <paramref name="pictures"/>: the file taken after each replay step (null = none);
    /// <paramref name="endStep"/>: the replay step (0-based) that was being run when the replay ended early (-1 = none) and <paramref name="end"/> how it ended. Rules:
    ///  - a replay step's status is its result line's (ok, failed, timed-out = Fail, needs-person and blocked = Blocked); a checkpoint step is paired with the n-th checkpoint result: failed when it failed, not verified when it has no verdict;
    ///  - the step the replay ended on takes the end: an error is its failure, a stop for a person its block, a Stop pressed by a person leaves it Not run;
    ///  - a numbered step the replay has no step of (a drawing switch, a script chain's commands) takes the result of the step before it when the replay ran to the end, so a PASS replay has no Not run row;
    ///  - the overall result never reads Pass unless the verdict is PASS: a failed basic check makes the last step Fail (its differences in Notes), a completed run with nothing verified leaves the last step Not run.</summary>
    public static Dictionary<int, StepOutcome> Outcomes(IReadOnlyList<Step> steps, ReplayResult r, IReadOnlyList<string?> pictures, IReadOnlyList<long> numberedT, int endStep, EvidenceEnd end,
        IReadOnlyList<StepShots?>? shots = null, IReadOnlyList<IReadOnlyList<Engine.Emit.WordingBullet>>? bullets = null, IReadOnlyList<int>? rowCounts = null, IReadOnlyList<IReadOnlyList<bool>>? subRowsFilled = null)
    {
        var infos = new List<ReplayStepInfo>();
        int mark = 0;
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var res = r.Steps.LastOrDefault(x => x.Index == i + 1);
            string status = res == null ? "" : res.Status switch { "ok" => "ok", "failed" or "timed-out" => "failed", "" => "", _ => "needs-person" };
            string? message = EvidenceBook.Scrub(res?.Message);
            if (s.Kind == "mark")
            {
                var cp = mark < r.Checkpoints.Count ? r.Checkpoints[mark] : null; mark++;
                if (cp?.Pass == false) { status = "failed"; message = "Checkpoint " + (string.IsNullOrWhiteSpace(cp.Note) ? cp.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"\"{cp.Note}\"") + ": " + string.Join("; ", cp.Differences.Take(5).Select(EvidenceBook.Scrub)); }
                else if (cp != null && cp.Pass == null && status == "ok" && r.Verdict != ReplayResult.Pass) { status = "unverified"; message = "Checkpoint not verified."; }
            }
            if (end != EvidenceEnd.Completed && i == endStep && status is "" or "ok")
            {
                var why = Scrub(r.Message);
                (status, message) = end switch
                {
                    EvidenceEnd.Error => ("failed", why),
                    EvidenceEnd.Person => ("needs-person", why),
                    _ => ("stopped", "Stopped by the person here."),
                };
            }
            static string? Ex(string? f) => f != null && File.Exists(f) ? f : null;
            var sh = shots != null && i < shots.Count ? shots[i] : null;
            var ended = Ex(sh?.Ended ?? (i < pictures.Count ? pictures[i] : null));
            var failed = Ex(sh?.Failed);
            infos.Add(new ReplayStepInfo(i, s.T, status, message, ended ?? failed, res?.WaitedForPerson == true, s.Kind, Ex(sh?.Opened), Ex(sh?.Filled), ended, failed, sh?.OpenedNoWindow == true));
        }
        var map = EvidenceBook.Outcomes(numberedT, infos, bullets, rowCounts, subRowsFilled);

        if (end == EvidenceEnd.Completed)
        {
            // a numbered step the replay has no step of ran as part of its neighbour: same result, no picture
            for (int n = 1; n <= numberedT.Count; n++)
            {
                if (map.ContainsKey(n)) continue;
                for (int k = n - 1; k >= 1; k--) if (map.TryGetValue(k, out var before)) { if (before.Result == "Pass") map[n] = new StepOutcome("Pass", null, null); break; }
                if (!map.ContainsKey(n)) for (int k = n + 1; k <= numberedT.Count; k++) if (map.TryGetValue(k, out var after)) { if (after.Result == "Pass") map[n] = new StepOutcome("Pass", null, null); break; }
            }
        }

        int last = map.Count == 0 ? 0 : map.Keys.Max();
        if (last > 0)
        {
            var lastOut = map[last];
            if (r.BasicCheck?.Pass == false)
                map[last] = new StepOutcome("Fail", Join(lastOut.Notes, EvidenceBook.Scrub(r.BasicCheck.Line())), lastOut.Picture, lastOut.Rows);
            else if (r.Verdict != ReplayResult.Pass && map.Values.All(o => o.Result == "Pass"))
            {
                // the replay found nothing wrong step by step, but its verdict is not PASS: the sheet must not say so
                map[last] = r.Verdict == ReplayResult.Incomplete
                    ? new StepOutcome("Not run", Join(lastOut.Notes, NotVerified), lastOut.Picture, lastOut.Rows)
                    : new StepOutcome(r.Verdict == ReplayResult.Fail || r.Verdict == ReplayResult.Error ? "Fail" : "Not run", Join(lastOut.Notes, $"Replay verdict {r.Verdict}: {Scrub(r.Message)}"), lastOut.Picture, lastOut.Rows);
            }
        }
        return map;
    }

    static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + "\n" + b;

    /// <summary>See <see cref="EvidenceBook.Scrub"/>.</summary>
    public static string Scrub(string? text) => EvidenceBook.Scrub(text);
}
