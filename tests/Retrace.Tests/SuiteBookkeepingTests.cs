using Retrace.Core;

namespace Retrace.Tests;

/// <summary>RETRACESUITE bookkeeping without AutoCAD: what a stopped suite lists, which suite file is the latest, the step clock.</summary>
public class SuiteBookkeepingTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("retrace-test-suite2").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    static SuiteResult WithTests(params (string rec, string verdict)[] done)
    {
        var s = new SuiteResult { Stamp = "2026-11-02_10-00-00" };
        foreach (var (rec, verdict) in done) s.Tests.Add(new SuiteTest { Name = rec, Recording = rec, Verdict = verdict });
        return s;
    }

    [Fact]
    public void A_stopped_suite_lists_every_test_it_did_not_reach_as_NOT_RUN()
    {
        var s = WithTests(("a", "PASS"));
        s.StopReason = "no drawing is open";
        Assert.True(s.AddMissing(["a", "b", "c"], null, false));
        Assert.Equal(new[] { "a", "b", "c" }, s.Tests.Select(t => t.Recording));
        Assert.Equal("NOT-RUN", s.Tests[1].Verdict);
        Assert.Equal("NOT-RUN", s.Tests[2].Verdict);
        Assert.Equal("STOPPED", s.Verdict);
        Assert.Equal("no drawing is open", s.StopReason);
    }

    [Fact]
    public void A_test_that_was_running_when_the_suite_ended_is_INCOMPLETE_and_one_that_was_only_sent_is_NOT_RUN()
    {
        var ran = WithTests(("a", "PASS"));
        ran.StopReason = "AutoCAD was closed";
        ran.AddMissing(["a", "b", "c"], "b", true);
        Assert.Equal("INCOMPLETE", ran.Tests.Single(t => t.Recording == "b").Verdict);
        Assert.Contains("while this test ran", ran.Tests.Single(t => t.Recording == "b").Message);
        Assert.Equal("NOT-RUN", ran.Tests.Single(t => t.Recording == "c").Verdict);

        var sent = WithTests(("a", "PASS"));
        sent.StopReason = "AutoCAD was closed";
        sent.AddMissing(["a", "b"], "b", false);
        Assert.Equal("NOT-RUN", sent.Tests.Single(t => t.Recording == "b").Verdict);
        Assert.Equal(2, sent.Tests.Count);   // listed once
    }

    [Fact]
    public void A_suite_with_nothing_missing_is_unchanged_and_one_with_something_missing_never_passes()
    {
        var full = WithTests(("a", "PASS"), ("b", "PASS"));
        Assert.False(full.AddMissing(["a", "b"], null, false));
        Assert.Equal("PASS", full.Verdict);
        Assert.Equal(2, full.Tests.Count);

        var gap = WithTests(("a", "PASS"));
        Assert.True(gap.AddMissing(["a", "b"], null, false));   // no reason given: still not a PASS
        Assert.NotEqual("PASS", gap.Verdict);
        Assert.NotEqual("", gap.StopReason);
    }

    [Fact]
    public void Two_suites_with_the_same_stamp_get_sortable_names_and_the_newer_is_the_latest()
    {
        var dir = Path.Combine(_root, "results");
        var first = WithTests(("a", "PASS")).Write(dir);
        var second = WithTests(("a", "FAIL")).Write(dir);
        Assert.EndsWith("2026-11-02_10-00-00_suite.json", first);
        Assert.EndsWith("2026-11-02_10-00-00_suite_2.json", second);
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow);   // the same time: the name decides
        File.SetLastWriteTimeUtc(second, File.GetLastWriteTimeUtc(first));
        Assert.Equal(second, SuiteResult.LatestJson(dir));
        var third = WithTests(("a", "PASS")).Write(dir);
        Assert.EndsWith("_suite_3.json", third);
        File.SetLastWriteTimeUtc(third, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(third, SuiteResult.LatestJson(dir));
        Assert.True(File.Exists(Path.ChangeExtension(second, ".md")));
    }

    [Fact]
    public void LatestJson_ignores_files_that_are_not_suite_summaries()
    {
        var dir = Path.Combine(_root, "results");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "2026-11-02_10-00-00_suite_x.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "2026-11-02_10-00-00_PASS.json"), "{}");
        Assert.Null(SuiteResult.LatestJson(dir));
    }

    [Fact]
    public void UniquePath_takes_the_first_free_number()
    {
        var dir = Path.Combine(_root, "u");
        Directory.CreateDirectory(dir);
        string Make(int i) => UniquePath.Numbered(dir, "x", ".json", i);
        Assert.Equal(Path.Combine(dir, "x.json"), UniquePath.Find(Make));
        File.WriteAllText(Path.Combine(dir, "x.json"), "");
        File.WriteAllText(Path.Combine(dir, "x_2.json"), "");
        Assert.Equal(Path.Combine(dir, "x_3.json"), UniquePath.Find(Make));
    }

    [Fact]
    public void The_step_clock_blames_the_step_that_runs_and_does_not_restart_for_it()
    {
        var t0 = new DateTime(2026, 11, 2, 10, 0, 0);
        var c = new StepClock(TimeSpan.FromSeconds(120));
        Assert.False(c.Expired(t0.AddHours(1)));            // nothing started yet
        c.Begin(3, t0);
        c.Begin(3, t0.AddSeconds(100));                     // the same step again: no restart
        Assert.Equal(3, c.Step);
        Assert.False(c.Expired(t0.AddSeconds(119)));
        Assert.True(c.Expired(t0.AddSeconds(120)));
        c.Begin(4, t0.AddSeconds(121));                     // the next step starts: a new clock for it
        Assert.Equal(4, c.Step);
        Assert.False(c.Expired(t0.AddSeconds(200)));
        Assert.True(c.Expired(t0.AddSeconds(241)));
    }
}
