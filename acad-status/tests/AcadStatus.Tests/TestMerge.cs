using System;
using System.Collections.Generic;
using AcadStatus.Core;

namespace AcadStatus.Tests;

/// <summary>Shorthands for the tests: <see cref="StatusMerge.Merge"/> takes a liveness check with the pid, the start time and the age; most
/// tests only care about the pid, or about the pid and its start time.</summary>
static class TestMerge
{
    public static MergeResult Merge(IEnumerable<StatusEntry> entries, DateTime nowUtc, Func<int, DateTime?, TimeSpan, bool> pidAlive) =>
        StatusMerge.Merge(entries, nowUtc, pidAlive);

    public static MergeResult Merge(IEnumerable<StatusEntry> entries, DateTime nowUtc, Func<int, DateTime?, bool> pidAlive) =>
        StatusMerge.Merge(entries, nowUtc, (pid, start, _) => pidAlive(pid, start));

    public static MergeResult Merge(IEnumerable<StatusEntry> entries, DateTime nowUtc, Func<int, bool> pidAlive) =>
        StatusMerge.Merge(entries, nowUtc, (pid, _, _) => pidAlive(pid));
}
