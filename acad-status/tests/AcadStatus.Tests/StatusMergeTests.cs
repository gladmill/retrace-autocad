using System;
using System.Collections.Generic;
using System.Linq;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class StatusMergeTests
{
    static DateTime Now => TestData.Now;

    static MergeResult Merge(Func<int, bool>? alive, params StatusEntry[] entries) => TestMerge.Merge(entries, Now, alive ?? (_ => true));

    [Fact]
    public void TheOrderIsTheContractsPriority()
    {
        var states = new[] { "stopped", "idle", "running", "error", "busy", "waiting-input", "needs-person", "blocked" };
        var entries = states.Select((s, i) => TestData.Entry(state: s, pid: 100 + i, file: "x-" + i + ".json")).ToArray();
        var merged = Merge(null, entries);
        Assert.Equal(states.Reverse().ToArray(), merged.Shown.Select(m => m.State).ToArray());
        Assert.Equal("blocked", merged.Top!.State);
    }

    [Fact]
    public void RanksStrictlyDecreaseAlongTheContractsList()
    {
        var order = new[] { "blocked", "needs-person", "waiting-input", "busy", "error", "running", "idle", "stopped" };
        var ranks = order.Select(States.Rank).ToArray();
        Assert.Equal(ranks.OrderByDescending(r => r).ToArray(), ranks);
        Assert.Equal(ranks.Length, ranks.Distinct().Count());
    }

    [Fact]
    public void AnUnknownStateRanksJustAboveIdle()
    {
        Assert.True(States.Rank("compiling") > States.Rank(States.Idle));
        Assert.True(States.Rank("compiling") < States.Rank(States.Running));
        var merged = Merge(null,
            TestData.Entry(state: "idle", pid: 1, file: "a-1.json"),
            TestData.Entry(state: "compiling", pid: 2, file: "b-2.json"),
            TestData.Entry(state: "running", pid: 3, file: "c-3.json"));
        Assert.Equal(new[] { "running", "compiling", "idle" }, merged.Shown.Select(m => m.State).ToArray());
        Assert.True(merged.Shown[1].Unknown);
        Assert.False(merged.Shown[0].Unknown);
    }

    [Fact]
    public void AnUnknownStateIsShownAsItsOwnText()
    {
        var m = Merge(null, TestData.Entry(state: "Compiling")).Shown.Single();
        Assert.Equal("Compiling", m.State);
        Assert.True(m.Unknown);
    }

    [Fact]
    public void ARankTieGoesToTheNewerUpdateThenTheFileName()
    {
        var older = TestData.Entry(state: "running", updated: Now.AddSeconds(-5), pid: 1, file: "a-1.json");
        var newer = TestData.Entry(state: "running", updated: Now.AddSeconds(-1), pid: 2, file: "z-2.json");
        var sameA = TestData.Entry(state: "running", updated: Now.AddSeconds(-1), pid: 3, file: "b-3.json");
        var ids = Merge(null, older, newer, sameA).Shown.Select(m => m.Entry.File).ToArray();
        Assert.Equal(new[] { "b-3.json", "z-2.json", "a-1.json" }, ids);
    }

    [Fact]
    public void AFreshEntryIsNotStaleAtItsTtlButIsJustPastIt()
    {
        var e = TestData.Entry(updated: Now.AddSeconds(-15), ttl: 15, state: "running");
        var at = TestMerge.Merge(new[] { e }, Now, _ => false);
        Assert.Single(at.Shown);
        Assert.False(at.Shown[0].Stale);
        var past = TestMerge.Merge(new[] { e }, Now.AddMilliseconds(1), _ => false);
        Assert.Empty(past.Shown);
        Assert.Single(past.Ignored);
    }

    [Fact]
    public void AStaleEntryWhoseProcessLivesRanksAsBusy()
    {
        var e = TestData.Entry(state: "idle", updated: Now.AddSeconds(-60), ttl: 5, pid: 4812);
        var m = Merge(pid => pid == 4812, e).Shown.Single();
        Assert.Equal("busy", m.State);
        Assert.True(m.Stale);
        Assert.True(m.PidAlive);
        Assert.True(m.NoReply);
        Assert.Equal(States.Rank(States.Busy), m.Rank);
        Assert.Equal(TimeSpan.FromSeconds(60), m.Age);
    }

    [Fact]
    public void AStaleBusyEntryOutranksAFreshRunningOne()
    {
        var stale = TestData.Entry(state: "running", updated: Now.AddSeconds(-60), ttl: 5, pid: 1, file: "a-1.json");
        var fresh = TestData.Entry(state: "running", pid: 2, file: "b-2.json");
        Assert.Equal("a-1.json", Merge(_ => true, stale, fresh).Top!.Entry.File);
    }

    [Fact]
    public void AStaleBlockedEntryWithALivePidIsBusyNotBlocked()
    {
        var e = TestData.Entry(state: "blocked", updated: Now.AddSeconds(-60), ttl: 5);
        Assert.Equal("busy", Merge(_ => true, e).Top!.State);
    }

    [Fact]
    public void AStaleEntryWhoseProcessIsGoneIsIgnored()
    {
        var e = TestData.Entry(state: "running", updated: Now.AddSeconds(-60), ttl: 5, pid: 99);
        var r = Merge(_ => false, e);
        Assert.Empty(r.Shown);
        Assert.Null(r.Top);
        var ignored = Assert.Single(r.Ignored);
        Assert.Contains("gone", ignored.Reason);
        Assert.Contains("99", ignored.Reason);
        Assert.Equal("process 99 gone", ignored.Why);                       // nothing that ticks
        Assert.Equal("no update for 1m00s, process 99 gone", ignored.Reason);   // the age is only in Reason
        Assert.Equal(TimeSpan.FromSeconds(60), ignored.Age);
    }

    [Fact]
    public void WhyOfAnIgnoredFileDoesNotChangeAsTimePasses()
    {
        var e = TestData.Entry(state: "running", updated: Now.AddSeconds(-60), ttl: 5, pid: 99);
        var a = TestMerge.Merge(new[] { e }, Now, _ => false).Ignored.Single();
        var b = TestMerge.Merge(new[] { e }, Now.AddMinutes(30), _ => false).Ignored.Single();
        Assert.Equal(a.Why, b.Why);
        Assert.NotEqual(a.Reason, b.Reason);
    }

    [Fact]
    public void AFreshEntryIsNeverCheckedAgainstItsPid()
    {
        var asked = new List<int>();
        var fresh = TestData.Entry(pid: 5);
        var stale = TestData.Entry(pid: 6, updated: Now.AddSeconds(-99), ttl: 5, file: "b-6.json");
        Merge(p => { asked.Add(p); return true; }, fresh, stale);
        Assert.Equal(new[] { 6 }, asked);
    }

    [Fact]
    public void APidOfZeroOrLessCountsAsGone()
    {
        var e = TestData.Entry(pid: 0, updated: Now.AddSeconds(-60), ttl: 5);
        Assert.Empty(Merge(_ => true, e).Shown);
    }

    [Fact]
    public void ACheckThatThrowsCountsAsGone()
    {
        var e = TestData.Entry(updated: Now.AddSeconds(-60), ttl: 5);
        var r = Merge(_ => throw new InvalidOperationException("boom"), e);
        Assert.Empty(r.Shown);
        Assert.Single(r.Ignored);
    }

    // CONTRACT.md: a stopped entry is never ranked busy, stale or not

    [Fact]
    public void AFreshStoppedEntryIsStopped()
    {
        var m = Merge(null, TestData.Entry(state: "stopped")).Shown.Single();
        Assert.Equal("stopped", m.State);
        Assert.False(m.Stale);
        Assert.False(m.NoReply);
    }

    [Fact]
    public void AStaleStoppedEntryWithALivePidIsStillNotBusy()
    {
        var e = TestData.Entry(state: "stopped", updated: Now.AddSeconds(-600), ttl: 5);
        var m = Merge(_ => true, e).Shown.Single();
        Assert.Equal("stopped", m.State);
        Assert.True(m.Stale);
        Assert.False(m.NoReply);
        Assert.Equal(States.Rank(States.Stopped), m.Rank);
    }

    [Fact]
    public void AStaleStoppedEntryWhoseProcessIsGoneIsIgnored()
    {
        var e = TestData.Entry(state: "stopped", updated: Now.AddSeconds(-600), ttl: 5);
        Assert.Empty(Merge(_ => false, e).Shown);
    }

    [Fact]
    public void AStoppedEntryNeverBeatsAnythingElse()
    {
        var stopped = TestData.Entry(state: "stopped", updated: Now.AddSeconds(-600), ttl: 5, pid: 1, file: "a-1.json");
        var idle = TestData.Entry(state: "idle", pid: 2, file: "b-2.json");
        var r = Merge(_ => true, stopped, idle);
        Assert.Equal(new[] { "idle", "stopped" }, r.Shown.Select(m => m.State).ToArray());
    }

    [Fact]
    public void AnUpdatedInTheFutureIsNotStaleAndHasNoNegativeAge()
    {
        var m = Merge(null, TestData.Entry(updated: Now.AddMinutes(10))).Shown.Single();
        Assert.False(m.Stale);
        Assert.Equal(TimeSpan.Zero, m.Age);
    }

    [Fact]
    public void NoEntriesGiveNoTop()
    {
        var r = Merge(null);
        Assert.Null(r.Top);
        Assert.Empty(r.Shown);
        Assert.Empty(r.Ignored);
    }

    [Fact]
    public void ManySourcesAreAllListed()
    {
        var entries = new[]
        {
            TestData.Entry("acad", 1, state: "idle", file: "acad-1.json"),
            TestData.Entry("retrace", 1, state: "running", file: "retrace-1.json"),
            TestData.Entry("acad-pilot", 9, state: "needs-person", file: "acad-pilot-9.json"),
            TestData.Entry("someother", 3, state: "error", file: "someother-3.json"),
        };
        var r = Merge(null, entries);
        Assert.Equal(new[] { "acad-pilot-9", "someother-3", "retrace-1", "acad-1" }, r.Shown.Select(m => m.Entry.Id).ToArray());
    }
}
