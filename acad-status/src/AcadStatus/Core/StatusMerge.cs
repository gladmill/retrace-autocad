using System;
using System.Collections.Generic;
using System.Linq;

namespace AcadStatus.Core;

/// <summary>An entry as it takes part in the display. <see cref="State"/> is the effective state: the written one, or "busy" for a stale
/// entry whose process is alive. Unknown states keep their text.</summary>
public sealed record MergedEntry(StatusEntry Entry, string State, int Rank, bool Stale, bool PidAlive, bool Unknown, TimeSpan Age)
{
    /// <summary>Stale with its process alive: the writer does not answer, so it ranks as busy.</summary>
    public bool NoReply => Stale && PidAlive && State == States.Busy;
}

/// <summary>A file the merge leaves out. <see cref="Why"/> does not change while the file stays as it is; <see cref="Reason"/> adds how old it is
/// (for the dump, which is read once, not for a window that updates every second).</summary>
public sealed record IgnoredEntry(StatusEntry Entry, TimeSpan Age, string Why)
{
    public string Reason => "no update for " + CompactText.Age(Age) + ", " + Why;
}

public sealed record MergeResult(IReadOnlyList<MergedEntry> Shown, IReadOnlyList<IgnoredEntry> Ignored)
{
    /// <summary>The entry the pane shows: the highest priority one.</summary>
    public MergedEntry? Top => Shown.Count > 0 ? Shown[0] : null;
}

/// <summary>Merges the entries of all files into one ranked list (CONTRACT.md, "States").</summary>
public static class StatusMerge
{
    /// <param name="pidAlive">Is the process an entry names running: its pid, its start time when the file gives one, and how long the entry
    /// has been stale (a process that cannot be identified counts as gone only after an hour). Asked only for stale entries.</param>
    public static MergeResult Merge(IEnumerable<StatusEntry> entries, DateTime nowUtc, Func<int, DateTime?, TimeSpan, bool> pidAlive)
    {
        var shown = new List<MergedEntry>();
        var ignored = new List<IgnoredEntry>();
        foreach (var e in entries)
        {
            // One entry that cannot be ranked (a date the arithmetic cannot take, a check that throws) must not abort the merge: the pane
            // would freeze on its last view. It is listed as ignored and the others are ranked.
            try
            {
                var key = e.StateKey;
                bool stale = nowUtc > e.StaleAfter;
                var age = nowUtc - e.Updated;
                if (age < TimeSpan.Zero) age = TimeSpan.Zero;
                bool known = States.IsKnown(key);
                if (!stale)
                {
                    shown.Add(new MergedEntry(e, known ? key : e.State, States.Rank(key), false, false, !known, age));
                    continue;
                }
                bool alive = false;
                try { alive = e.Pid > 0 && pidAlive(e.Pid, e.PidStart, age); } catch { /* cannot tell: not alive */ }
                if (!alive)
                {
                    ignored.Add(new IgnoredEntry(e, age, "process " + (e.Pid > 0 ? e.Pid.ToString() : "unknown") + " gone"));
                    continue;
                }
                // A stopped entry only says the writer is gone: it is never busy, stale or not.
                if (key == States.Stopped)
                {
                    shown.Add(new MergedEntry(e, States.Stopped, States.Rank(States.Stopped), true, true, false, age));
                    continue;
                }
                // The writer cannot refresh while its process lives: for in-process writers AutoCAD's UI thread is not running.
                shown.Add(new MergedEntry(e, States.Busy, States.Rank(States.Busy), true, true, false, age));
            }
            catch (Exception ex)
            {
                ignored.Add(new IgnoredEntry(e, TimeSpan.Zero, "could not be ranked (" + ex.GetType().Name + ")"));
            }
        }
        shown.Sort(Compare);
        return new MergeResult(shown, ignored);
    }

    static int Compare(MergedEntry a, MergedEntry b)
    {
        int c = b.Rank.CompareTo(a.Rank);
        if (c != 0) return c;
        c = b.Entry.Updated.CompareTo(a.Entry.Updated);
        if (c != 0) return c;
        return string.CompareOrdinal(a.Entry.File, b.Entry.File);
    }
}
