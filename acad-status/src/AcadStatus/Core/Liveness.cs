using System;
using System.Collections.Generic;

namespace AcadStatus.Core;

/// <summary>What looking at a process id found: no such process, the process that wrote the file (or nothing says it is another), or a
/// process whose identity could not be read at all (access denied to its start time: a SYSTEM process, another session).</summary>
public enum ProcessState { Gone, Same, Unknown }

/// <summary>Is the process a status file names the one that wrote it? Windows reuses process ids, so a pid alone can name another program
/// by now. CONTRACT.md: a reader that finds <c>pid</c> alive but with a start time more than 2 s from <c>pidStart</c> treats it as gone.</summary>
public static class ProcessIdentity
{
    public const double ToleranceSec = 2;

    /// <summary>A stale file that names a process we cannot identify, and states its start time, is treated as gone after this long: a writer
    /// that stays silent for an hour is not the unreadable process that happens to hold its pid now.</summary>
    public static readonly TimeSpan UnknownGoneAfter = TimeSpan.FromHours(1);

    /// <param name="running">A process with that id exists.</param>
    /// <param name="runningStartUtc">Its start time, when it could be read.</param>
    /// <param name="pidStart">The start time the file states, when it states one.</param>
    public static bool IsSame(bool running, DateTime? runningStartUtc, DateTime? pidStart)
    {
        if (!running) return false;
        if (runningStartUtc is not { } actual || pidStart is not { } stated) return true;   // nothing to compare: the pid is all there is
        return Math.Abs((actual - stated).TotalSeconds) <= ToleranceSec;
    }

    /// <summary>The state of a process, from its start time when it can be read.</summary>
    public static ProcessState StateOf(bool running, DateTime? runningStartUtc, DateTime? pidStart) =>
        !running ? ProcessState.Gone : IsSame(true, runningStartUtc, pidStart) ? ProcessState.Same : ProcessState.Gone;

    /// <summary>Alive or not, for an entry that has been stale for <paramref name="staleFor"/>. <see cref="ProcessState.Unknown"/> counts as alive
    /// (do not call a writer gone on a guess), except for a file that states <c>pidStart</c> and has been stale for over an hour.</summary>
    public static bool Resolve(ProcessState state, bool hasPidStart, TimeSpan staleFor) => state switch
    {
        ProcessState.Gone => false,
        ProcessState.Same => true,
        _ => !(hasPidStart && staleFor > UnknownGoneAfter),
    };
}

/// <summary>Asks about a writer's process and remembers the answer for a few seconds: the pane merges the folder every tick, and a process
/// lookup per stale file per tick is work the UI thread should not do. What is remembered is the state of the process; the age-dependent
/// rule for an unidentifiable one is applied on every question.</summary>
public sealed class LivenessCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);
    const int MaxEntries = 64;

    readonly Func<int, DateTime?, ProcessState> _probe;
    readonly TimeSpan _ttl;
    readonly Dictionary<(int Pid, long Start), (ProcessState State, DateTime At)> _known = new();

    public LivenessCache(Func<int, DateTime?, ProcessState> probe, TimeSpan? ttl = null)
    {
        _probe = probe;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>How many times the probe ran (for tests).</summary>
    public int Probes { get; private set; }

    /// <param name="staleFor">How long ago the entry was last updated.</param>
    public bool IsAlive(int pid, DateTime? pidStart, DateTime nowUtc, TimeSpan staleFor)
    {
        var key = (pid, pidStart?.Ticks ?? 0L);
        if (!_known.TryGetValue(key, out var k) || nowUtc - k.At >= _ttl || nowUtc < k.At)
        {
            Probes++;
            ProcessState state;
            try { state = _probe(pid, pidStart); } catch { state = ProcessState.Gone; }
            if (_known.Count >= MaxEntries) Prune(nowUtc);
            k = (state, nowUtc);
            _known[key] = k;
        }
        return ProcessIdentity.Resolve(k.State, pidStart != null, staleFor);
    }

    void Prune(DateTime nowUtc)
    {
        foreach (var key in new List<(int, long)>(_known.Keys))
            if (nowUtc - _known[key].At >= _ttl) _known.Remove(key);
        if (_known.Count >= MaxEntries) _known.Clear();
    }
}

/// <summary>When the folder is cleaned: at start and then every ten minutes (CONTRACT.md, "Files").</summary>
public static class CleanupSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    public static bool Due(DateTime? lastUtc, DateTime nowUtc) => lastUtc is not { } last || nowUtc - last >= Interval || nowUtc < last;
}
