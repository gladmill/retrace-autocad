using System;
using System.Collections.Generic;
using System.Linq;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class LivenessTests
{
    static DateTime Now => TestData.Now;

    // ---- ProcessIdentity: the pid, and pidStart when the file gives one

    [Theory]
    [InlineData(false, 0, 0, false)]    // no such process
    [InlineData(false, 0, 100, false)]
    [InlineData(true, 0, 0, true)]      // no start times at all: the pid is all there is
    [InlineData(true, 100, 100, true)]
    [InlineData(true, 100, 101, true)]  // 1 s apart: the file is written to the whole second
    [InlineData(true, 100, 102, true)]  // exactly 2 s: still the same
    [InlineData(true, 100, 103, false)] // more than 2 s apart: another process
    [InlineData(true, 103, 100, false)]
    [InlineData(true, 100, 100000, false)]
    public void IsSameFollowsTheContractRule(bool running, int actualSec, int statedSec, bool expected)
    {
        DateTime? actual = actualSec == 0 ? null : Now.AddSeconds(actualSec);
        DateTime? stated = statedSec == 0 ? null : Now.AddSeconds(statedSec);
        Assert.Equal(expected, ProcessIdentity.IsSame(running, actual, stated));
    }

    [Fact]
    public void WithoutAStatedStartTheRunningProcessCountsEvenIfItsStartIsKnown() =>
        Assert.True(ProcessIdentity.IsSame(true, Now, null));

    [Fact]
    public void WithoutAReadableStartTheStatedStartCannotCondemnIt() =>
        Assert.True(ProcessIdentity.IsSame(true, null, Now));

    // ---- merge with pidStart

    [Fact]
    public void AStaleEntryWhosePidWasReusedIsIgnoredNotBusy()
    {
        var start = Now.AddHours(-3);
        var e = TestData.Entry("acad", 4812, updated: Now.AddMinutes(-20), ttl: 10, state: "idle", pidStart: start, file: "acad-4812.json");
        // a process runs under pid 4812, but it started long after the writer did
        var reused = TestMerge.Merge(new[] { e }, Now, (pid, st) => ProcessIdentity.IsSame(true, Now.AddMinutes(-5), st));
        Assert.Empty(reused.Shown);
        Assert.Contains("gone", Assert.Single(reused.Ignored).Reason);
        // the same process: busy
        var same = TestMerge.Merge(new[] { e }, Now, (pid, st) => ProcessIdentity.IsSame(true, start.AddSeconds(1), st));
        Assert.Equal("busy", same.Shown.Single().State);
    }

    [Fact]
    public void TheLivenessCheckGetsThePidStartOfTheEntry()
    {
        var start = Now.AddHours(-1);
        var e = TestData.Entry("acad", 77, updated: Now.AddMinutes(-20), ttl: 10, pidStart: start, file: "acad-77.json");
        var seen = new List<(int, DateTime?)>();
        TestMerge.Merge(new[] { e }, Now, (pid, st) => { seen.Add((pid, st)); return true; });
        Assert.Equal(new[] { (77, (DateTime?)start) }, seen);
    }

    [Fact]
    public void AStaleStoppedEntryOfAReusedPidIsIgnoredToo()
    {
        var start = Now.AddHours(-3);
        var e = TestData.Entry("acad", 4812, updated: Now.AddMinutes(-20), ttl: 10, state: "stopped", pidStart: start, file: "acad-4812.json");
        Assert.Empty(TestMerge.Merge(new[] { e }, Now, (pid, st) => ProcessIdentity.IsSame(true, Now, st)).Shown);
    }

    // ---- LivenessCache

    static ProcessState S(bool alive) => alive ? ProcessState.Same : ProcessState.Gone;
    static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    [Fact]
    public void TheProbeRunsOncePerPidAndStartWithinTheTtl()
    {
        var calls = 0;
        var cache = new LivenessCache((pid, st) => { calls++; return ProcessState.Same; });
        for (int i = 0; i < 20; i++) Assert.True(cache.IsAlive(10, null, Now.AddMilliseconds(i * 100), Stale));
        Assert.Equal(1, calls);
        Assert.Equal(1, cache.Probes);
    }

    [Fact]
    public void TheProbeRunsAgainAfterTheTtl()
    {
        var alive = true;
        var cache = new LivenessCache((pid, st) => S(alive), TimeSpan.FromSeconds(5));
        Assert.True(cache.IsAlive(10, null, Now, Stale));
        alive = false;
        Assert.True(cache.IsAlive(10, null, Now.AddSeconds(4.9), Stale));    // still the remembered answer
        Assert.False(cache.IsAlive(10, null, Now.AddSeconds(5), Stale));     // asked again
        Assert.Equal(2, cache.Probes);
    }

    [Fact]
    public void AnotherPidOrAnotherStartIsAnotherQuestion()
    {
        var cache = new LivenessCache((pid, st) => S(pid == 1 || st == Now));
        Assert.True(cache.IsAlive(1, null, Now, Stale));
        Assert.False(cache.IsAlive(2, null, Now, Stale));
        Assert.True(cache.IsAlive(2, Now, Now, Stale));
        Assert.False(cache.IsAlive(2, Now.AddMinutes(1), Now, Stale));
        Assert.Equal(4, cache.Probes);
    }

    [Fact]
    public void AProbeThatThrowsCountsAsGoneAndIsRemembered()
    {
        var calls = 0;
        var cache = new LivenessCache((pid, st) => { calls++; throw new InvalidOperationException(); });
        Assert.False(cache.IsAlive(1, null, Now, Stale));
        Assert.False(cache.IsAlive(1, null, Now.AddSeconds(1), Stale));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AClockThatWentBackAsksAgain()
    {
        var cache = new LivenessCache((pid, st) => ProcessState.Same);
        cache.IsAlive(1, null, Now, Stale);
        cache.IsAlive(1, null, Now.AddMinutes(-1), Stale);
        Assert.Equal(2, cache.Probes);
    }

    [Fact]
    public void TheCacheStaysSmall()
    {
        var cache = new LivenessCache((pid, st) => ProcessState.Same);
        for (int pid = 1; pid <= 500; pid++) cache.IsAlive(pid, null, Now.AddMilliseconds(pid), Stale);
        // all 500 were asked once; asking the newest again right away is answered from memory
        var before = cache.Probes;
        cache.IsAlive(500, null, Now.AddMilliseconds(501), Stale);
        Assert.Equal(before, cache.Probes);
    }

    [Fact]
    public void AStaleFilePerTickCostsOneLookupPerFiveSeconds()
    {
        // 30 stale files, one merge per second for a minute: 30 lookups per 5 s window, not 30 per tick
        var entries = Enumerable.Range(1, 30).Select(i =>
            TestData.Entry("t", i, updated: Now.AddHours(-2), ttl: 10, file: "t-" + i + ".json")).ToArray();
        var cache = new LivenessCache((pid, st) => ProcessState.Same);
        for (int s = 0; s < 60; s++)
        {
            var now = Now.AddSeconds(s);
            TestMerge.Merge(entries, now, (pid, st, age) => cache.IsAlive(pid, st, now, age));
        }
        Assert.Equal(30 * 12, cache.Probes);
    }

    // ---- a process that cannot be identified (access denied: a SYSTEM or other-session process holds the pid)

    [Theory]
    [InlineData(ProcessState.Gone, false, 0, false)]
    [InlineData(ProcessState.Gone, true, 9000, false)]
    [InlineData(ProcessState.Same, false, 0, true)]
    [InlineData(ProcessState.Same, true, 9000, true)]
    [InlineData(ProcessState.Unknown, false, 0, true)]        // no pidStart in the file: the pid alone has to do
    [InlineData(ProcessState.Unknown, false, 99999, true)]    // ... however long ago
    [InlineData(ProcessState.Unknown, true, 60, true)]        // pidStart stated, stale a minute: do not call it gone on a guess
    [InlineData(ProcessState.Unknown, true, 3600, true)]      // exactly an hour: still alive
    [InlineData(ProcessState.Unknown, true, 3601, false)]     // over an hour: not the writer
    public void ResolveTreatsAnUnidentifiableProcessAsAliveUntilAnHourStale(ProcessState state, bool hasPidStart, int staleSec, bool expected) =>
        Assert.Equal(expected, ProcessIdentity.Resolve(state, hasPidStart, TimeSpan.FromSeconds(staleSec)));

    [Theory]
    [InlineData(false, 0, 0, ProcessState.Gone)]
    [InlineData(true, 0, 0, ProcessState.Same)]
    [InlineData(true, 100, 101, ProcessState.Same)]
    [InlineData(true, 100, 200, ProcessState.Gone)]
    public void StateOfFollowsTheContractRule(bool running, int actualSec, int statedSec, ProcessState expected)
    {
        DateTime? actual = actualSec == 0 ? null : Now.AddSeconds(actualSec);
        DateTime? stated = statedSec == 0 ? null : Now.AddSeconds(statedSec);
        Assert.Equal(expected, ProcessIdentity.StateOf(running, actual, stated));
    }

    [Fact]
    public void TheCacheRemembersTheStateAndAppliesTheAgeRuleOnEveryQuestion()
    {
        var start = Now.AddDays(-1);
        var cache = new LivenessCache((pid, st) => ProcessState.Unknown);
        Assert.True(cache.IsAlive(4, start, Now, TimeSpan.FromMinutes(10)));          // stale ten minutes: alive
        Assert.False(cache.IsAlive(4, start, Now.AddSeconds(1), TimeSpan.FromHours(2)));   // the same remembered answer, two hours stale: gone
        Assert.Equal(1, cache.Probes);
        Assert.True(cache.IsAlive(5, null, Now, TimeSpan.FromHours(2)));              // no pidStart: alive however stale
    }

    [Fact]
    public void AnAccessDeniedSystemProcessKeepsAFreshStaleEntryBusyButNotAnOldOne()
    {
        var start = Now.AddDays(-1);
        var recent = TestData.Entry("acad", 4, updated: Now.AddMinutes(-20), ttl: 10, pidStart: start, file: "acad-4.json");
        var old = TestData.Entry("acad", 5, updated: Now.AddHours(-3), ttl: 10, pidStart: start, file: "acad-5.json");
        var cache = new LivenessCache((pid, st) => ProcessState.Unknown);
        var merged = TestMerge.Merge(new[] { recent, old }, Now, (pid, st, age) => cache.IsAlive(pid, st, Now, age));
        Assert.Equal("acad-4.json", Assert.Single(merged.Shown).Entry.File);
        Assert.Equal("acad-5.json", Assert.Single(merged.Ignored).Entry.File);
        // and the clean-up takes the old one only (it is over an hour old by definition)
        Assert.Equal(new[] { "acad-5.json" }, StatusFiles.CleanupCandidates(new[] { recent, old }, Now, (pid, st, age) => cache.IsAlive(pid, st, Now, age)));
    }

    // ---- the clean-up schedule

    [Fact]
    public void TheFirstCleanupIsDueAtOnce() => Assert.True(CleanupSchedule.Due(null, Now));

    [Theory]
    [InlineData(0, false)]
    [InlineData(300, false)]
    [InlineData(599, false)]
    [InlineData(600, true)]
    [InlineData(6000, true)]
    public void ThenItIsDueEveryTenMinutes(int sinceLastSec, bool expected) =>
        Assert.Equal(expected, CleanupSchedule.Due(Now, Now.AddSeconds(sinceLastSec)));

    [Fact]
    public void AClockThatWentBackIsDue() => Assert.True(CleanupSchedule.Due(Now, Now.AddMinutes(-5)));
}

public class PlacementTests
{
    // a 1280 x 800 primary screen whose working area ends at the taskbar (y 760)
    static (int X, int Y) Above(int clickX, int barTop, int w, int h, int l = 0, int t = 0, int r = 1280, int b = 760) =>
        Placement.Above(clickX, barTop, w, h, l, t, r, b);

    [Fact]
    public void TheBottomEdgeSitsFourPixelsAboveTheBar()
    {
        var (_, y) = Above(600, 719, 470, 290);
        Assert.Equal(719 - 4 - 290, y);
    }

    [Fact]
    public void ItIsCentredOnTheClick()
    {
        var (x, _) = Above(600, 719, 470, 290);
        Assert.Equal(600 - 235, x);
    }

    [Fact]
    public void ItIsKeptInsideTheAreaOnTheRightAndOnTheLeft()
    {
        Assert.Equal(1280 - 470, Above(1217, 719, 470, 290).X);
        Assert.Equal(0, Above(8, 719, 470, 290).X);
    }

    [Fact]
    public void ItNeverGoesAboveTheTopOfTheAreaItIsOn()
    {
        Assert.Equal(0, Above(600, 100, 470, 290).Y);
    }

    [Fact]
    public void AMonitorAboveThePrimaryHasANegativeTop()
    {
        // working area of a screen above the primary: y from -1080 to -40. A tall window is clamped to -1080, not to 0.
        var (_, y) = Above(600, -61, 470, 2000, 0, -1080, 1920, -40);
        Assert.Equal(-1080, y);
        // and a window that fits stays 4 px above the bar, at a negative y
        var (_, y2) = Above(600, -61, 470, 290, 0, -1080, 1920, -40);
        Assert.Equal(-61 - 4 - 290, y2);
    }

    [Fact]
    public void AMonitorToTheLeftHasANegativeLeft()
    {
        var (x, _) = Above(-1900, 719, 470, 290, -1920, 0, 0, 760);
        Assert.Equal(-1920, x);   // centred it would start at -2135, left of the area
    }

    [Fact]
    public void AWindowTallerThanTheAreaStartsAtItsTop()
    {
        Assert.Equal(0, Above(600, 719, 470, 5000).Y);
    }

    [Fact]
    public void ABarBelowTheWorkingAreaPullsTheWindowUp()
    {
        // the bar's top is below the area's bottom (an auto-hidden taskbar ate the area): the window still ends inside the area
        var (_, y) = Above(600, 790, 470, 290);
        Assert.Equal(760 - 290, y);
    }
}

public class ClickGuardTests
{
    [Fact]
    public void TheClickThatClosedTheWindowIsNotANewClick()
    {
        var g = new ClickGuard();
        g.Deactivated(1234);
        Assert.False(g.IsNewClick(1234));
    }

    [Fact]
    public void AnotherMessageIsANewClick()
    {
        var g = new ClickGuard();
        g.Deactivated(1234);
        Assert.True(g.IsNewClick(1300));
    }

    [Fact]
    public void WithoutADeactivationEveryClickIsNew()
    {
        var g = new ClickGuard();
        Assert.True(g.IsNewClick(5));
        Assert.True(g.IsNewClick(5));
    }

    [Fact]
    public void TheGuardWorksOnceOnly()
    {
        var g = new ClickGuard();
        g.Deactivated(10);
        Assert.False(g.IsNewClick(10));
        Assert.True(g.IsNewClick(10));   // a second click can never carry the same message
    }

    [Fact]
    public void ADeactivationThatNoPaneClickFollowsDoesNotSwallowALaterClick()
    {
        var g = new ClickGuard();
        g.Deactivated(10);                  // closed by clicking in the drawing; the pane never heard of it
        Assert.True(g.IsNewClick(5000));    // a later click on the pane opens the window
        Assert.True(g.IsNewClick(5000));
    }

    [Fact]
    public void ResetForgetsTheDeactivation()
    {
        var g = new ClickGuard();
        g.Deactivated(10);
        g.Reset();
        Assert.True(g.IsNewClick(10));
    }

    [Fact]
    public void NoTimingIsInvolved()
    {
        // the same message identity 10 minutes later is still the same message: the rule is identity, not elapsed time
        var g = new ClickGuard();
        g.Deactivated(77);
        Assert.False(g.IsNewClick(77));
    }
}
