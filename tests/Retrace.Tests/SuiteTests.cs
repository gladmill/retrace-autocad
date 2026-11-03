using Retrace.Core;

namespace Retrace.Tests;

/// <summary>RETRACESUITE: the test list and the summary files (the runner itself needs AutoCAD and is tried on the VM).</summary>
public class SuiteTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("retrace-test-suite").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    string Test(string folder, string started)
    {
        var dir = Path.Combine(_root, "tests", folder);
        Directory.CreateDirectory(dir);
        var rec = Path.Combine(dir, "recording.jsonl");
        File.WriteAllText(rec, $$"""{"type":"header","schema":1,"name":"{{folder}}","started":"{{started}}"}""" + "\n");
        return rec;
    }

    string Tests => Path.Combine(_root, "tests");

    [Fact]
    public void All_tests_run_oldest_first()
    {
        var b = Test("2026-10-02_0900_b", "2026-10-02T09:00:00");
        var a = Test("2026-10-01_0900_a", "2026-10-01T09:00:00");
        var c = Test("2026-10-03_0900_c", "2026-10-03T09:00:00");
        Assert.Equal(new[] { a, b, c }, SuiteTestList.All(Tests));
        Assert.Equal(new[] { a, b, c }, SuiteTestList.FromText("", Tests, out var p1));
        Assert.Empty(p1);
        Assert.Equal(new[] { a, b, c }, SuiteTestList.FromText(" ALL ", Tests, out _));
    }

    [Fact]
    public void Names_keep_their_order_and_a_missing_one_is_a_problem()
    {
        var a = Test("2026-10-01_0900_a", "2026-10-01T09:00:00");
        var b = Test("2026-10-02_0900_b", "2026-10-02T09:00:00");
        var found = SuiteTestList.FromText("b;a;b;nope", Tests, out var problems);
        Assert.Equal(new[] { b, a }, found);   // a duplicate runs once
        Assert.Single(problems);
        Assert.Contains("nope", problems[0]);
    }

    [Fact]
    public void An_at_file_gives_the_list_and_an_unreadable_one_is_a_problem()
    {
        var a = Test("2026-10-01_0900_a", "2026-10-01T09:00:00");
        var b = Test("2026-10-02_0900_b", "2026-10-02T09:00:00");
        var f = Path.Combine(_root, "list.txt");
        File.WriteAllText(f, "# suite\nb\na\n");
        Assert.Equal(new[] { b, a }, SuiteTestList.FromText("@" + f, Tests, out var problems));
        Assert.Empty(problems);
        SuiteTestList.FromText("@" + Path.Combine(_root, "none.txt"), Tests, out var bad);
        Assert.NotEmpty(bad);
    }

    static ReplayResult Replay(string verdict, params (int index, string note, string diff, bool? pass)[] marks)
    {
        var r = new ReplayResult { Verdict = verdict, Message = verdict == ReplayResult.Pass ? "" : "something", WrittenTo = @"C:\t\results\x.json" };
        foreach (var m in marks)
        {
            var c = new CheckpointResult { Index = m.index, Note = m.note, Pass = m.pass };
            if (m.diff.Length > 0) c.Differences.Add(m.diff);
            r.Checkpoints.Add(c);
        }
        return r;
    }

    [Fact]
    public void A_test_line_has_verdict_duration_failing_checkpoints_and_result_path()
    {
        var rec = Test("2026-10-01_0900_walls", "2026-10-01T09:00:00");
        var t = SuiteTest.From(rec, Replay(ReplayResult.Fail, (1, "ok", "", true), (2, "after move", "layer A-WALL: frozen 0 -> 1", false)), 12.34);
        Assert.Equal("walls", t.Name);
        Assert.Equal(ReplayResult.Fail, t.Verdict);
        Assert.Equal(12.3, t.DurationSeconds);
        Assert.Single(t.FailingCheckpoints);
        Assert.Equal(2, t.FailingCheckpoints[0].Index);
        Assert.Equal("layer A-WALL: frozen 0 -> 1", t.FailingCheckpoints[0].Differences[0]);
        Assert.Equal(@"C:\t\results\x.json", t.Result);
    }

    [Fact]
    public void Behavior_check_differences_are_listed_too()
    {
        var r = Replay(ReplayResult.Fail);
        r.Checkpoints.Add(new CheckpointResult { Index = 1, Pass = false, CheckDifferences = ["layers: color changed"] });
        var t = SuiteTest.From("x\\recording.jsonl", r, 1);
        Assert.Equal("layers: color changed", t.FailingCheckpoints.Single().Differences.Single());
    }

    [Fact]
    public void An_incomplete_run_that_needed_a_person_is_NEEDS_PERSON()
    {
        var r = Replay(ReplayResult.Incomplete);
        r.Steps.Add(new StepResult { Index = 3, Status = "needs-person" });
        Assert.Equal(SuiteTest.NeedsPerson, SuiteTest.From("x\\recording.jsonl", r, 1).Verdict);
        var blocked = Replay(ReplayResult.Incomplete);
        blocked.Steps.Add(new StepResult { Index = 1, Status = "blocked" });
        Assert.Equal(SuiteTest.NeedsPerson, SuiteTest.From("x\\recording.jsonl", blocked, 1).Verdict);
        Assert.Equal(ReplayResult.Incomplete, SuiteTest.From("x\\recording.jsonl", Replay(ReplayResult.Incomplete), 1).Verdict);
        var timeout = Replay(ReplayResult.Error);
        timeout.Steps.Add(new StepResult { Index = 2, Status = "timed-out" });
        Assert.Equal(ReplayResult.Error, SuiteTest.From("x\\recording.jsonl", timeout, 1).Verdict);
    }

    SuiteResult Sample(string stop = "")
    {
        var s = new SuiteResult { Stamp = "2026-11-02_10-00-00", StartedUtc = "a", FinishedUtc = "b", CoreVersion = "1", StepTimeoutSeconds = 120, StopReason = stop };
        s.Tests.Add(new SuiteTest { Name = "one", Verdict = "PASS", DurationSeconds = 4, Result = "r1" });
        s.Tests.Add(new SuiteTest
        {
            Name = "two", Verdict = "FAIL", DurationSeconds = 5.5, Result = "r2",
            FailingCheckpoints = [new SuiteCheckpoint { Index = 2, Note = "n|x", Differences = ["a", "b"] }],
        });
        s.Tests.Add(new SuiteTest { Name = "three", Verdict = "ERROR", Message = "step 4 timed out after 120 s" });
        return s;
    }

    [Fact]
    public void The_suite_verdict_counts_and_summary()
    {
        var s = Sample();
        Assert.Equal("FAIL", s.Verdict);
        Assert.Equal("1 ERROR, 1 FAIL, 1 PASS", s.CountsText());
        Assert.Equal("suite FAIL: 1 ERROR, 1 FAIL, 1 PASS (3 tests)", s.Summary());
        var ok = new SuiteResult();
        ok.Tests.Add(new SuiteTest { Verdict = "PASS" });
        Assert.Equal("PASS", ok.Verdict);
        Assert.Equal("FAIL", new SuiteResult().Verdict);   // nothing ran: not a pass
        var stopped = Sample("AutoCAD did not answer");
        Assert.Equal("STOPPED", stopped.Verdict);
        Assert.EndsWith("Stopped: AutoCAD did not answer", stopped.Summary());
    }

    [Fact]
    public void Markdown_has_one_line_per_test_with_the_differences_and_escaped_bars()
    {
        var md = Sample().ToMarkdown();
        Assert.Contains("| 1 | one | PASS | 4 |  | r1 |", md);
        Assert.Contains("| 2 | two | FAIL | 5.5 | checkpoint 2 \"n\\|x\": a; b | r2 |", md);
        Assert.Contains("| 3 | three | ERROR | 0 | step 4 timed out after 120 s |  |", md);
    }

    [Fact]
    public void Write_makes_json_and_md_and_does_not_overwrite_an_earlier_suite()
    {
        var dir = Path.Combine(_root, "results");
        var j1 = Sample().Write(dir);
        var j2 = Sample().Write(dir);
        Assert.NotEqual(j1, j2);
        Assert.EndsWith("2026-11-02_10-00-00_suite.json", j1);
        Assert.True(File.Exists(Path.ChangeExtension(j1, ".md")));
        Assert.True(File.Exists(Path.ChangeExtension(j2, ".md")));
        var back = SuiteResult.Read(j1)!;
        Assert.Equal(3, back.Tests.Count);
        Assert.Equal("FAIL", back.Verdict);
        Assert.Equal("r2", back.Tests[1].Result);
        Assert.Equal(2, back.Tests[1].FailingCheckpoints[0].Index);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Latest_is_the_newest_suite_and_none_when_there_is_no_folder_or_no_suite()
    {
        var dir = Path.Combine(_root, "results");
        Assert.Null(SuiteResult.Latest(dir));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "2026-11-02_10-00-00_PASS.json"), "{}");   // a per-test result is not a suite
        Assert.Null(SuiteResult.Latest(dir));
        var old = Sample(); old.Stamp = "2026-11-01_09-00-00"; old.Write(dir);
        var s = Sample(); s.Stamp = "2026-11-02_10-00-00"; s.Write(dir);
        var latest = SuiteResult.Latest(dir)!.Value;
        Assert.StartsWith("2026-11-02_10-00-00: suite FAIL", latest.Summary);
        Assert.EndsWith("_suite.md", latest.Md);
    }

    [Fact]
    public void The_step_timeout_setting_defaults_to_120_and_is_clamped()
    {
        Assert.Equal(120, new RetraceSettings().SuiteStepTimeoutSeconds);
        Assert.Equal(5, new RetraceSettings { SuiteStepTimeoutSeconds = 0 }.SuiteStepTimeoutSeconds);
        Assert.Equal(3600, new RetraceSettings { SuiteStepTimeoutSeconds = 99999 }.SuiteStepTimeoutSeconds);
        Assert.Equal(Path.Combine(_root, "results"), new RetraceSettings { RootDir = _root }.Results);
    }
}
