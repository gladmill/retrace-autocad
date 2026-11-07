using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>An unknown command that AutoCAD runs anyway (UNITS on a fresh start): the replay waits for its start before it is an ERROR, and the readers
/// make no step of the report.</summary>
public class UnknownCommandGraceTests
{
    static readonly DateTime T0 = new(2026, 11, 6, 12, 0, 0);
    static DateTime At(int ms) => T0.AddMilliseconds(ms);

    /// <summary>A grace in which the replay sent <paramref name="sent"/> at 0 ms.</summary>
    static UnknownCommandGrace After(string? sent)
    {
        var g = new UnknownCommandGrace();
        g.Sent(sent, At(0));
        return g;
    }

    [Fact]
    public void An_unknown_command_that_starts_soon_is_carried_on_once()
    {
        var g = After("UNITS");
        g.Unknown("UNITS", At(0));
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(200)).Verdict);
        g.Started("UNITS", At(350));
        var p = g.Poll(At(400));
        Assert.Equal(UnknownCommandVerdict.Recovered, p.Verdict);
        Assert.Equal("UNITS", p.Command);
        Assert.Equal(TimeSpan.FromMilliseconds(350), p.Waited);
        Assert.Null(p.Message);
        Assert.Equal(UnknownCommandVerdict.None, g.Poll(At(600)).Verdict);   // once only
        Assert.Equal(UnknownCommandVerdict.None, g.Poll(At(5000)).Verdict);
    }

    [Fact]
    public void An_unknown_command_that_never_starts_fails_after_the_window_with_todays_message()
    {
        var g = After("UNITS");
        g.Unknown("UNITS", At(0));
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(1999)).Verdict);
        var p = g.Poll(At(2000));
        Assert.Equal(UnknownCommandVerdict.Failed, p.Verdict);
        Assert.Equal("unknown command UNITS", p.Message);
        g.Started("UNITS", At(2100));   // too late: the run has ended
        Assert.Equal(UnknownCommandVerdict.Failed, g.Poll(At(2200)).Verdict);
    }

    [Fact]
    public void The_window_counts_from_the_report_not_from_the_send()
    {
        var g = After("UNITS");
        g.Unknown("UNITS", At(1500));   // reported 1.5 s after the command was sent
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(3000)).Verdict);
        Assert.Equal(UnknownCommandVerdict.Failed, g.Poll(At(3500)).Verdict);
    }

    [Fact]
    public void An_unknown_command_that_was_not_sent_fails_at_once()
    {
        var g = After("UNITS");
        g.Unknown("FOO", At(0));
        var p = g.Poll(At(0));
        Assert.Equal(UnknownCommandVerdict.Failed, p.Verdict);
        Assert.Equal("unknown command FOO", p.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("_")]
    public void A_step_with_no_command_notes_nothing(string? sent)
    {
        var g = After(sent);
        g.Unknown("UNITS", At(0));
        var p = g.Poll(At(0));
        Assert.Equal(UnknownCommandVerdict.Failed, p.Verdict);
        Assert.Equal("unknown command UNITS", p.Message);
    }

    [Fact]
    public void Nothing_sent_at_all_means_no_grace()
    {
        var g = new UnknownCommandGrace();   // a person typed it
        g.Unknown("UNITS", At(0));
        Assert.Equal(UnknownCommandVerdict.Failed, g.Poll(At(0)).Verdict);
    }

    [Fact]
    public void A_blank_report_fails_at_once()
    {
        var g = After("UNITS");
        g.Unknown("", At(0));
        Assert.Equal(UnknownCommandVerdict.Failed, g.Poll(At(0)).Verdict);
        var h = After("UNITS");
        h.Unknown(null, At(0));
        Assert.Equal(UnknownCommandVerdict.Failed, h.Poll(At(0)).Verdict);
    }

    [Theory]
    [InlineData("units", "UNITS", "UNITS")]
    [InlineData("_UNITS", "UNITS", "UNITS")]
    [InlineData("UNITS", "_units", "Units")]
    [InlineData(".UNITS", "UNITS", "_UNITS")]
    [InlineData("'ZOOM", "ZOOM", "ZOOM")]
    [InlineData("-LAYER", "LAYER", "-layer")]
    [InlineData("*UNITS", "_UNITS", "UNITS")]
    public void The_names_are_compared_without_case_and_prefix(string reported, string sent, string started)
    {
        var g = After(sent);
        g.Unknown(reported, At(0));
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(50)).Verdict);
        g.Started(started, At(100));
        Assert.Equal(UnknownCommandVerdict.Recovered, g.Poll(At(200)).Verdict);
    }

    [Fact]
    public void Another_command_starting_does_not_end_the_wait()
    {
        var g = After("UNITS");
        g.Unknown("UNITS", At(0));
        g.Started("REGEN", At(100));
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(300)).Verdict);
        var p = g.Poll(At(2500));
        Assert.Equal(UnknownCommandVerdict.Failed, p.Verdict);
        Assert.Equal("unknown command UNITS", p.Message);
    }

    [Fact]
    public void A_different_unknown_command_while_one_is_awaited_fails_at_once()
    {
        var g = After("UNITS");
        g.Sent("FOO", At(10));   // even one the replay sent: only one command is in flight
        g.Unknown("UNITS", At(100));
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(200)).Verdict);
        g.Unknown("FOO", At(300));
        var p = g.Poll(At(400));
        Assert.Equal(UnknownCommandVerdict.Failed, p.Verdict);
        Assert.Equal("unknown command FOO", p.Message);
        g.Started("UNITS", At(500));
        Assert.Equal(UnknownCommandVerdict.Failed, g.Poll(At(600)).Verdict);   // stays failed
    }

    [Fact]
    public void A_repeat_of_the_awaited_name_is_ignored_and_does_not_extend_the_wait()
    {
        var g = After("UNITS");
        g.Unknown("UNITS", At(0));
        g.Unknown("units", At(1500));
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(1600)).Verdict);
        Assert.Equal(UnknownCommandVerdict.Failed, g.Poll(At(2000)).Verdict);
    }

    [Fact]
    public void A_start_with_no_report_is_nothing()
    {
        var g = After("UNITS");
        g.Started("UNITS", At(0));
        Assert.Equal(UnknownCommandVerdict.None, g.Poll(At(10)).Verdict);
    }

    [Fact]
    public void A_start_the_timer_saw_late_still_counts()
    {
        // a command that loads on first use can probably hold the UI thread for longer than the window: its start event comes first, the tick later
        var g = After("UNITS");
        g.Unknown("UNITS", At(0));
        g.Started("UNITS", At(4800));
        var p = g.Poll(At(5000));
        Assert.Equal(UnknownCommandVerdict.Recovered, p.Verdict);
        Assert.Equal(TimeSpan.FromMilliseconds(4800), p.Waited);
    }

    [Fact]
    public void The_windows_can_be_set()
    {
        var g = new UnknownCommandGrace(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(800));
        Assert.Equal(TimeSpan.FromMilliseconds(500), g.Window);
        Assert.Equal(TimeSpan.FromMilliseconds(800), g.SentTtl);
        g.Sent("UNITS", At(0));
        g.Unknown("UNITS", At(700));
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(1199)).Verdict);
        Assert.Equal(UnknownCommandVerdict.Failed, g.Poll(At(1200)).Verdict);
        Assert.Equal(TimeSpan.FromSeconds(2), new UnknownCommandGrace().Window);
        Assert.Equal(TimeSpan.FromSeconds(5), new UnknownCommandGrace().SentTtl);
    }

    [Fact]
    public void An_idle_grace_polls_none()
    {
        Assert.Equal(UnknownCommandVerdict.None, new UnknownCommandGrace().Poll(At(0)).Verdict);
    }

    // ---- which commands count as just sent ----

    [Fact]
    public void A_command_sent_long_ago_is_not_just_sent()
    {
        var g = After("UNITS");
        g.Unknown("UNITS", At(5000));   // the 5 s are over
        Assert.Equal(UnknownCommandVerdict.Failed, g.Poll(At(5000)).Verdict);
        var h = After("UNITS");
        h.Unknown("UNITS", At(4999));
        Assert.Equal(UnknownCommandVerdict.Waiting, h.Poll(At(4999)).Verdict);
    }

    [Fact]
    public void A_command_that_started_is_no_longer_just_sent()
    {
        var g = After("UNITS");
        g.Started("UNITS", At(100));
        g.Unknown("UNITS", At(200));   // a later report of the same name is not the sent command's
        Assert.Equal(UnknownCommandVerdict.Failed, g.Poll(At(200)).Verdict);
    }

    [Fact]
    public void A_late_report_for_the_step_before_still_gets_the_grace()
    {
        var g = new UnknownCommandGrace();
        g.Sent("UNITS", At(0));
        g.Sent("ZOOM", At(300));   // the next step was sent already
        g.Unknown("UNITS", At(500));
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(600)).Verdict);
        g.Started("UNITS", At(700));
        Assert.Equal(UnknownCommandVerdict.Recovered, g.Poll(At(800)).Verdict);
    }

    [Fact]
    public void Sending_a_command_again_refreshes_its_time()
    {
        var g = new UnknownCommandGrace();
        g.Sent("UNITS", At(0));
        g.Sent("UNITS", At(4000));   // a retry (a palette step) or the macro typed after a silent click
        g.Unknown("UNITS", At(6000));
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(6100)).Verdict);
    }

    [Fact]
    public void A_start_of_another_command_keeps_the_sent_one()
    {
        var g = new UnknownCommandGrace();
        g.Sent("UNITS", At(0));
        g.Started("ZOOM", At(100));
        g.Unknown("UNITS", At(200));
        Assert.Equal(UnknownCommandVerdict.Waiting, g.Poll(At(300)).Verdict);
    }

    // ---- one way to compare command names ----

    [Theory]
    [InlineData("UNITS", "UNITS", true)]
    [InlineData("_units", ".UNITS", true)]
    [InlineData("'UNITS", "*UNITS", true)]
    [InlineData("-LAYER", "_LAYER", true)]
    [InlineData(" _UNITS ", "units", true)]
    [InlineData("UNITS", "UNIT", false)]
    [InlineData("", "", false)]
    [InlineData("_", "-", false)]
    [InlineData(null, null, false)]
    [InlineData("UNITS", null, false)]
    public void Command_names_agree_without_case_and_prefix(string? a, string? b, bool same)
    {
        Assert.Equal(same, CommandName.Same(a, b));
        Assert.Equal(same, CommandName.Same(b, a));
    }

    [Fact]
    public void Strip_keeps_the_case_and_Key_upper_cases()
    {
        Assert.Equal("Units", CommandName.Strip("_-Units"));
        Assert.Equal("UNITS", CommandName.Key("_-Units"));
        Assert.Equal("", CommandName.Strip(null));
        Assert.Equal("", CommandName.Key("  "));
    }

    [Fact]
    public void The_macro_reader_and_the_grace_use_the_same_prefixes()
    {
        Assert.Equal(new[] { "LINE", "-layer" }.Select(CommandName.Key), RecordingReader.MacroCommands("^C^C_LINE;*-layer;").Select(CommandName.Key));
        Assert.True(RecordingReader.MacroMentions("^C^C_-layer;m;", "LAYER"));
    }

    // ---- the readers: cmd_unknown right before the cmd_start of the same command is no step, no warning, no count ----

    static readonly string[] Recorded =
    [
        """{"t":100,"n":1,"type":"cmd_unknown","command":"UNITS"}""",
        """{"t":160,"n":2,"type":"cmd_start","command":"UNITS","origin":"typed/other"}""",
        """{"t":900,"n":3,"type":"cmd_end","command":"UNITS"}""",
    ];

    static List<System.Text.Json.JsonElement> Events(params string[] lines) => lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.Clone()).ToList();

    [Fact]
    public void The_replay_plan_has_one_step_for_an_unknown_command_that_then_starts()
    {
        var steps = Plan.Build(Events(Recorded));
        var step = Assert.Single(steps);
        Assert.Equal("command", step.Kind);
        Assert.Equal("UNITS", step.Command);
        Assert.Equal("Type command UNITS", step.Title);
    }

    [Fact]
    public void The_replay_plan_makes_no_step_of_an_unknown_command_that_never_starts()
    {
        Assert.Empty(Plan.Build(Events("""{"t":100,"n":1,"type":"cmd_unknown","command":"FOO"}""")));
    }

    [Fact]
    public void The_status_board_counts_the_command_once()
    {
        var tally = new StepTally();
        tally.Note("cmd_unknown", null, 0, 0, null, null, null, null);
        tally.Note("cmd_start", "typed/other", 0, 0, null, null, null, null);
        tally.Note("cmd_end", null, 1, 0, null, null, null, null);
        Assert.Equal(1, tally.Count);
    }

    [Fact]
    public void The_steps_of_a_recording_list_the_command_once_and_say_nothing_of_unknown()
    {
        var dir = Directory.CreateTempSubdirectory("retrace-test-unknown").FullName;
        try
        {
            var folder = Path.Combine(dir, "units");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "recording.jsonl");
            File.WriteAllLines(file, new[] { """{"type":"header","schema":1,"name":"units","autocad":"25.1.0.0","product":"AutoCAD 2026","locale":"en","workspace":"Drafting","started":"2026-11-06T10:00:00"}""" }.Concat(Recorded));
            var session = RecordingReader.Load(file);
            var st = Assert.Single(session.Steps);
            Assert.Equal("UNITS", st.Command);
            Assert.Equal("", st.Outcome);
            var warnings = new List<string>();
            var md = ScriptWriter.Build(session, null, dir, warnings);
            Assert.Contains("1. Type `UNITS`", md);
            Assert.DoesNotContain("unknown", md, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(warnings);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    [Theory]
    [InlineData("_UNITS", "UNITS")]
    [InlineData("units", "UNITS")]
    [InlineData("-UNITS", "UNITS")]
    [InlineData(".UNITS", "_UNITS")]
    public void An_unknown_report_spelled_differently_from_the_start_is_still_no_extra_step(string unknown, string started)
    {
        var steps = Plan.Build(Events($$"""{"t":100,"n":1,"type":"cmd_unknown","command":"{{unknown}}"}""", $$"""{"t":160,"n":2,"type":"cmd_start","command":"{{started}}","origin":"typed/other"}""", """{"t":900,"n":3,"type":"cmd_end","command":"UNITS"}"""));
        Assert.Single(steps);
        var dir = Directory.CreateTempSubdirectory("retrace-test-unknown2").FullName;
        try
        {
            var folder = Path.Combine(dir, "units");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "recording.jsonl");
            File.WriteAllLines(file, new[]
            {
                """{"type":"header","schema":1,"name":"units","autocad":"25.1.0.0","product":"AutoCAD 2026","locale":"en","workspace":"Drafting","started":"2026-11-06T10:00:00"}""",
                $$"""{"t":100,"n":1,"type":"cmd_unknown","command":"{{unknown}}"}""",
                $$"""{"t":160,"n":2,"type":"cmd_start","command":"{{started}}","origin":"typed/other"}""",
                """{"t":900,"n":3,"type":"cmd_end","command":"UNITS"}""",
            });
            var session = RecordingReader.Load(file);
            var st = Assert.Single(session.Steps);
            Assert.Equal("", st.Outcome);
            var md = ScriptWriter.Build(session, null, dir, new List<string>());
            Assert.DoesNotContain("2. Type", md);
            Assert.DoesNotContain("unknown", md, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
