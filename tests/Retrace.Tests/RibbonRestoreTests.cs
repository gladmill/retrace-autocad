using Retrace.Core;

namespace Retrace.Tests;

public class RibbonRestoreTests
{
    [Theory]
    [InlineData(false, false, 0, true, RibbonFinding.NoRibbon)]
    [InlineData(true, false, 1, true, RibbonFinding.Closed)]
    [InlineData(true, true, 0, true, RibbonFinding.TabMissing)]
    [InlineData(true, true, 2, true, RibbonFinding.TabDuplicate)]
    [InlineData(true, true, 1, false, RibbonFinding.TabHidden)]
    [InlineData(true, true, 1, true, RibbonFinding.Fine)]
    public void Find(bool exists, bool open, int n, bool visible, RibbonFinding expected) => Assert.Equal(expected, RibbonRestoreRules.Find(exists, open, n, visible));

    [Theory]
    [InlineData(RibbonFinding.TabMissing, RibbonAction.Build)]
    [InlineData(RibbonFinding.TabHidden, RibbonAction.None)]       // the user's Show Tabs choice
    [InlineData(RibbonFinding.Closed, RibbonAction.None)]          // the user closed the ribbon
    [InlineData(RibbonFinding.NoRibbon, RibbonAction.None)]
    [InlineData(RibbonFinding.Fine, RibbonAction.None)]
    [InlineData(RibbonFinding.TabDuplicate, RibbonAction.None)]
    public void Auto_repairs_only_a_missing_tab(RibbonFinding f, RibbonAction expected) => Assert.Equal(expected, RibbonRestoreRules.Auto(f));

    [Theory]
    [InlineData(RibbonFinding.Fine, RibbonAction.None)]
    [InlineData(RibbonFinding.TabMissing, RibbonAction.Build)]
    [InlineData(RibbonFinding.TabHidden, RibbonAction.RemoveStale | RibbonAction.Build)]
    [InlineData(RibbonFinding.TabDuplicate, RibbonAction.RemoveStale | RibbonAction.Build)]
    [InlineData(RibbonFinding.Closed, RibbonAction.OpenRibbon | RibbonAction.RemoveStale | RibbonAction.Build)]
    [InlineData(RibbonFinding.NoRibbon, RibbonAction.OpenRibbon | RibbonAction.RemoveStale | RibbonAction.Build)]
    public void Manual_does_everything_needed(RibbonFinding f, RibbonAction expected) => Assert.Equal(expected, RibbonRestoreRules.Manual(f));

    [Fact]
    public void Every_finding_has_a_message_and_a_failure_names_its_text()
    {
        foreach (var f in Enum.GetValues(typeof(RibbonFinding)).Cast<RibbonFinding>())
            Assert.False(string.IsNullOrWhiteSpace(RibbonRestoreRules.Message(f, true, null)));
        Assert.Contains("bad position", RibbonRestoreRules.Message(RibbonFinding.TabMissing, false, "bad position"));
        Assert.DoesNotContain("opened it", RibbonRestoreRules.Message(RibbonFinding.Closed, false, null));
    }

    [Fact]
    public void Guard_allows_five_in_ten_seconds_then_stops_once()
    {
        var g = new RestoreGuard();
        for (var i = 0; i < 5; i++) Assert.Equal(RestoreGuard.Gate.Allowed, g.Try(i * 500));
        Assert.Equal(RestoreGuard.Gate.JustStopped, g.Try(3000));
        Assert.True(g.Tripped);
        Assert.Equal(1, g.Trips);
        Assert.Equal(10_000, g.PauseMs);
        Assert.Equal(RestoreGuard.Gate.Stopped, g.Try(3500));
        Assert.Equal(RestoreGuard.Gate.Stopped, g.Try(12_999));
    }

    [Fact]
    public void Guard_pause_doubles_with_every_stop_up_to_the_cap()
    {
        var g = new RestoreGuard();
        long now = 0, expected = 10_000;
        for (var trip = 1; trip <= 9; trip++)
        {
            while (g.Try(now) == RestoreGuard.Gate.Allowed) now += 1000;
            Assert.Equal(trip, g.Trips);
            Assert.Equal(Math.Min(expected, RestoreGuard.CapMs), g.PauseMs);
            now += g.PauseMs;   // the pause is over: a new window
            expected *= 2;
        }
        Assert.Equal(RestoreGuard.CapMs, g.PauseMs);
    }

    [Fact]
    public void A_fault_that_never_ends_costs_a_bounded_number_of_stop_lines_per_hour()
    {
        // the tab is missing and the build fails (or something removes it): the once-a-second look, 3600 times
        var g = new RestoreGuard();
        int lines = 0, builds = 0;
        for (long now = 0; now < 3_600_000; now += 1000)
        {
            switch (g.Try(now)) { case RestoreGuard.Gate.Allowed: builds++; break; case RestoreGuard.Gate.JustStopped: lines++; break; }
        }
        Assert.InRange(lines, 8, 12);
        Assert.InRange(builds, 40, 60);   // 5 per window, not 3600
        // the same with events that keep reporting the removal: stopped once, never resumes while they continue
        var h = new RestoreGuard();
        int hLines = 0, hBuilds = 0;
        for (long now = 0; now < 3_600_000; now += 1000)
        {
            switch (h.Try(now, removal: true)) { case RestoreGuard.Gate.Allowed: hBuilds++; break; case RestoreGuard.Gate.JustStopped: hLines++; break; }
        }
        Assert.Equal(1, hLines);
        Assert.Equal(5, hBuilds);
    }

    [Fact]
    public void Only_the_removal_of_the_tab_pushes_the_resume_time_out()
    {
        var g = new RestoreGuard();
        for (var i = 0; i < 6; i++) g.Try(i * 100L);
        Assert.True(g.Tripped);
        // checks that only ask (ItemInitialized, WSCURRENT, the periodic look) do not postpone the resume
        for (long t = 1000; t < 10_500; t += 500) Assert.Equal(RestoreGuard.Gate.Stopped, g.Try(t));
        Assert.Equal(RestoreGuard.Gate.Allowed, g.Try(10_501));
        // a removal pushes it out by the pause
        var h = new RestoreGuard();
        for (var i = 0; i < 6; i++) h.Try(i * 100L);
        Assert.Equal(RestoreGuard.Gate.Stopped, h.Try(9_000, removal: true));
        Assert.Equal(RestoreGuard.Gate.Stopped, h.Try(15_000));
        Assert.Equal(RestoreGuard.Gate.Allowed, h.Try(19_001));
    }

    [Fact]
    public void Guard_does_not_trip_for_slow_removals_and_reset_clears_it()
    {
        var g = new RestoreGuard();
        for (var i = 0; i < 50; i++) Assert.Equal(RestoreGuard.Gate.Allowed, g.Try(i * 3000L));   // one every 3 s: never 5 within 10 s
        var h = new RestoreGuard();
        for (var i = 0; i < 6; i++) h.Try(i * 10L);
        Assert.True(h.Tripped);
        h.Reset();
        Assert.Equal(0, h.Trips);
        Assert.Equal(RestoreGuard.Gate.Allowed, h.Try(100));
    }

    [Fact]
    public void Doubling_starts_again_after_a_calm_of_ten_minutes()
    {
        var g = new RestoreGuard();
        long now = 0;
        for (var trip = 1; trip <= 3; trip++) { while (g.Try(now) == RestoreGuard.Gate.Allowed) now += 1000; now += g.PauseMs; }
        Assert.Equal(3, g.Trips);
        now += RestoreGuard.CapMs + g.PauseMs + 1;
        while (g.Try(now) == RestoreGuard.Gate.Allowed) now += 1000;
        Assert.Equal(1, g.Trips);
        Assert.Equal(10_000, g.PauseMs);
    }

    [Fact]
    public void Stop_line_words_a_build_failure_apart_from_a_tab_that_keeps_being_removed()
    {
        var removed = RestoreLog.Stopped(2, 20_000, null);
        var failed = RestoreLog.Stopped(1, 10_000, "ArgumentException: bad");
        Assert.Contains("keeps being removed", removed);
        Assert.Contains("paused for 20 s (stop 2)", removed);
        Assert.Contains("could not be built: ArgumentException: bad", failed);
        Assert.DoesNotContain("keeps being removed", failed);
    }

    [Fact]
    public void The_ribbon_must_be_quiet_for_the_settle_time_before_a_repair()
    {
        Assert.False(RestoreSettle.Ready(1000, 1000));
        Assert.False(RestoreSettle.Ready(1000 + RestoreSettle.SettleMs - 1, 1000));
        Assert.True(RestoreSettle.Ready(1000 + RestoreSettle.SettleMs, 1000));
        Assert.True(RestoreSettle.SettleMs is >= 100 and <= 300);   // a plain removal is restored well under a second
    }

    [Fact]
    public void Flag_keeps_the_first_reason_and_is_taken_once()
    {
        var f = new RestoreFlag();
        Assert.Null(f.Take());
        f.Set(RestoreReason.CollectionChanged);
        f.Set(RestoreReason.WorkspaceChanged);
        Assert.True(f.IsSet);
        Assert.Equal(RestoreReason.CollectionChanged, f.Take());
        Assert.False(f.IsSet);
        Assert.Null(f.Take());
    }

    [Fact]
    public void Same_failure_text_is_logged_once()
    {
        var d = new DistinctOnce();
        Assert.True(d.IsNew("a"));
        Assert.False(d.IsNew("a"));
        Assert.True(d.IsNew("b"));
        Assert.False(d.IsNew(null));
        Assert.True(d.IsNew("b"));   // cleared by a success, so it is news again
    }
}
