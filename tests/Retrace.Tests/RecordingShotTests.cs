using System.Text.Json;
using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

public class RecordingShotTests
{
    // ---- event numbering ------------------------------------------------------------------------------------------

    static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "retrace-test-shots-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void Events_are_numbered_from_one_and_header_has_no_n()
    {
        var dir = TempDir();
        string path;
        using (var w = new SessionWriter("t", new Dictionary<string, object?>(), dir))
        {
            path = w.Path;
            Assert.Equal(1, w.Event("a", null));
            Assert.Equal(2, w.Event("b", "x.dwg", ("k", 1)));
            Assert.Equal(2, w.LastN);
        }
        var lines = File.ReadAllLines(path).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.False(lines[0].TryGetProperty("n", out _));
        Assert.Equal(1, lines[1].GetProperty("n").GetInt32());
        Assert.Equal(2, lines[2].GetProperty("n").GetInt32());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Resume_continues_after_the_highest_n()
    {
        var dir = TempDir();
        string path;
        using (var w = new SessionWriter("t", new Dictionary<string, object?>(), dir)) { path = w.Path; w.Event("a", null); w.Event("b", null); }
        using (var w = SessionWriter.Resume(path)) Assert.Equal(3, w.Event("c", null));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Resume_of_a_session_without_n_counts_its_events()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "old.jsonl");
        File.WriteAllLines(path, new[] { "{\"type\":\"header\",\"schema\":1}", "{\"t\":1,\"type\":\"a\"}", "{\"t\":2,\"type\":\"b\"}" });
        using (var w = SessionWriter.Resume(path)) Assert.Equal(3, w.Event("c", null));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Events_from_several_threads_get_distinct_numbers()
    {
        var dir = TempDir();
        using var w = new SessionWriter("t", new Dictionary<string, object?>(), dir);
        var ns = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 200, _ => ns.Add(w.Event("e", null)));
        Assert.Equal(200, ns.Distinct().Count());
        Assert.Equal(200, ns.Max());
    }

    // ---- naming ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(12, "ribbon", "012-ribbon.png")]
    [InlineData(1, "cmd-start", "001-cmd-start.png")]
    [InlineData(1234, "mark", "1234-mark.png")]
    [InlineData(5, "Cmd End!", "005-cmd-end.png")]
    [InlineData(5, "", "005-shot.png")]
    public void File_names(int n, string kind, string expected) => Assert.Equal(expected, ShotNaming.FileName(n, kind));

    [Fact]
    public void Relative_path_uses_forward_slashes_and_the_screenshots_folder()
    {
        Assert.Equal("screenshots", ShotNaming.FolderFor("/t/2026-10-09_1015_draw/recording.jsonl"));
        Assert.Equal("screenshots/012-ribbon.png", ShotNaming.RelativePathFor("/t/2026-10-09_1015_draw/recording.jsonl", 12, "ribbon"));
    }

    // ---- which events get a picture --------------------------------------------------------------------------------

    [Theory]
    [InlineData("ribbon", false, "ribbon")]
    [InlineData("cmd_start", false, "cmd-start")]
    [InlineData("prompt", true, "prompt")]
    [InlineData("cmd_end", false, "cmd-end")]
    [InlineData("cmd_cancel", false, "cmd-cancel")]
    [InlineData("mark", false, "mark")]
    public void Events_with_a_picture(string type, bool input, string kind)
    {
        var r = ShotRules.For(type, 7, input, 1000);
        Assert.NotNull(r);
        Assert.Equal(7, r!.N);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(ShotTarget.Window, r.Target);
        Assert.True(r.DueMs > 1000, "the UI needs time to paint");
    }

    [Theory]
    [InlineData("prompt", false)]   // mouse noise and scripted input: the command's cmd_end covers it
    [InlineData("tab", false)]
    [InlineData("modal_enter", false)]
    [InlineData("doc_active", false)]
    [InlineData("session_end", false)]
    public void Events_without_a_picture(string type, bool input) => Assert.Null(ShotRules.For(type, 1, input, 0));

    [Fact]
    public void Dialog_request_targets_the_dialog() => Assert.Equal(ShotTarget.Dialog, ShotRules.Dialog(3, 0).Target);

    // ---- throttle and de-duplication -------------------------------------------------------------------------------

    static ShotRequest W(int n, string kind, long due) => new(n, kind, ShotTarget.Window, due);

    [Fact]
    public void Clear_forgets_the_last_pictures_so_a_new_recording_is_not_throttled()
    {
        var s = new ShotScheduler();
        s.Enqueue(W(1, "cmd-end", 192000));
        Assert.Single(s.TakeReady(192000));
        s.Enqueue(W(2, "cmd-end", 192000));
        s.TakeReady(192100);                       // one dropped as duplicate
        Assert.True(s.Dropped > 0);
        s.Clear();
        Assert.Equal(0, s.Dropped);
        s.Enqueue(W(1, "ribbon", 7016));
        s.Enqueue(new ShotRequest(2, "dialog", ShotTarget.Dialog, 7016));
        var taken = new List<int>();
        for (long t = 7016; t < 7016 + 1200 && taken.Count < 2; t += 120) taken.AddRange(s.TakeReady(t).Select(r => r.N));
        Assert.Equal(new[] { 1, 2 }, taken);
    }

    [Fact]
    public void Nothing_is_taken_before_it_is_due()
    {
        var s = new ShotScheduler();
        s.Enqueue(W(1, "ribbon", 500));
        Assert.Empty(s.TakeReady(499));
        Assert.Single(s.TakeReady(500));
        Assert.Equal(0, s.Pending);
    }

    [Fact]
    public void Window_pictures_are_at_least_250_ms_apart_and_a_different_kind_waits()
    {
        var s = new ShotScheduler();
        s.Enqueue(W(1, "ribbon", 0)); s.Enqueue(W(2, "cmd-start", 0));
        Assert.Equal(new[] { 1 }, s.TakeReady(100).Select(r => r.N));
        Assert.Empty(s.TakeReady(200));
        Assert.Equal(1, s.Pending);
        Assert.Equal(new[] { 2 }, s.TakeReady(350).Select(r => r.N));
    }

    [Fact]
    public void Same_kind_inside_the_gap_is_a_duplicate_and_dropped()
    {
        var s = new ShotScheduler();
        s.Enqueue(W(1, "prompt", 0)); s.Enqueue(W(2, "prompt", 0)); s.Enqueue(W(3, "prompt", 0));
        Assert.Equal(new[] { 1 }, s.TakeReady(100).Select(r => r.N));
        Assert.Equal(0, s.Pending);
        Assert.Equal(2, s.Dropped);
        s.Enqueue(W(4, "prompt", 400));
        Assert.Equal(new[] { 4 }, s.TakeReady(400).Select(r => r.N));   // 300 ms later: no duplicate
    }

    [Fact]
    public void A_request_that_waited_too_long_is_dropped()
    {
        var s = new ShotScheduler();
        s.Enqueue(W(1, "cmd-end", 0));
        Assert.Empty(s.TakeReady(ShotScheduler.MaxWaitMs + 1));
        Assert.Equal(0, s.Pending);
    }

    [Fact]
    public void Window_and_dialog_have_separate_gaps()
    {
        var s = new ShotScheduler();
        s.Enqueue(W(1, "ribbon", 0)); s.Enqueue(new ShotRequest(2, "dialog", ShotTarget.Dialog, 0));
        Assert.Equal(2, s.TakeReady(10).Count);
    }

    [Fact]
    public void Force_takes_what_is_not_due_yet()
    {
        var s = new ShotScheduler();
        s.Enqueue(W(1, "cmd-end", 5000));
        Assert.Single(s.TakeReady(100, force: true));
    }

    // ---- image sizing -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(800, 600, 800, 600)]
    [InlineData(2800, 1400, 1400, 700)]
    [InlineData(1000, 3000, 467, 1400)]
    public void Fit_size(int w, int h, int ew, int eh) => Assert.Equal((ew, eh), ShotImage.FitSize(w, h));

    [Fact]
    public void Scale_averages_blocks()
    {
        var px = new byte[4 * 2 * 4];   // 4x2, left half black, right half white
        for (int y = 0; y < 2; y++) for (int x = 2; x < 4; x++) { var i = (y * 4 + x) * 4; px[i] = px[i + 1] = px[i + 2] = 255; px[i + 3] = 255; }
        var o = ShotImage.Scale(px, 4, 2, 2, 1);
        Assert.Equal(2 * 4, o.Length);
        Assert.Equal(0, o[0]);
        Assert.Equal(255, o[4]);
    }

    // ---- interactive dialog pause -----------------------------------------------------------------------------------

    static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0);

    [Fact]
    public void Dialog_opens_the_person_closes_it_and_the_step_is_done()
    {
        var d = new DialogWait();
        d.Begin(T0, false);
        Assert.Equal(DialogWaitPhase.WaitingForOpen, d.Phase);
        d.ModalEntered();
        Assert.Equal(DialogWaitPhase.WaitingForPerson, d.Phase);
        d.Tick(T0.AddMinutes(10));   // no time limit for a person
        Assert.Equal(DialogWaitPhase.WaitingForPerson, d.Phase);
        d.ModalLeft(0);
        Assert.Equal(DialogWaitPhase.Done, d.Phase);
        Assert.True(d.PersonWaited);
    }

    [Fact]
    public void Nested_dialogs_finish_when_the_last_one_closes()
    {
        var d = new DialogWait();
        d.Begin(T0, false); d.ModalEntered();
        d.ModalLeft(1);
        Assert.Equal(DialogWaitPhase.WaitingForPerson, d.Phase);
        d.ModalLeft(0);
        Assert.Equal(DialogWaitPhase.Done, d.Phase);
    }

    [Fact]
    public void No_dialog_within_30_seconds_fails()
    {
        var d = new DialogWait();
        d.Begin(T0, false);
        d.Tick(T0.AddSeconds(29));
        Assert.Equal(DialogWaitPhase.WaitingForOpen, d.Phase);
        d.Tick(T0.AddSeconds(31));
        Assert.Equal(DialogWaitPhase.Failed, d.Phase);
        Assert.Contains("30", d.FailReason);
        Assert.False(d.PersonWaited);
    }

    [Fact]
    public void Cancelled_command_fails_before_and_after_the_dialog()
    {
        var a = new DialogWait(); a.Begin(T0, false); a.CommandEnded(true, T0);
        Assert.Equal(DialogWaitPhase.Failed, a.Phase);
        var b = new DialogWait(); b.Begin(T0, false); b.ModalEntered(); b.ModalLeft(0); b.CommandEnded(true, T0);
        Assert.Equal(DialogWaitPhase.Failed, b.Phase);
    }

    [Fact]
    public void Command_ending_without_a_dialog_fails_after_a_short_grace()
    {
        var d = new DialogWait();
        d.Begin(T0, false);
        d.CommandEnded(false, T0.AddSeconds(1));
        d.Tick(T0.AddSeconds(2));
        Assert.Equal(DialogWaitPhase.WaitingForOpen, d.Phase);
        d.Tick(T0.AddSeconds(4));
        Assert.Equal(DialogWaitPhase.Failed, d.Phase);
    }

    [Fact]
    public void Normal_end_after_the_dialog_keeps_the_step_done()
    {
        var d = new DialogWait();
        d.Begin(T0, false); d.ModalEntered(); d.ModalLeft(0); d.CommandEnded(false, T0);
        Assert.Equal(DialogWaitPhase.Done, d.Phase);
    }

    [Fact]
    public void A_dialog_already_open_at_the_start_goes_straight_to_the_person()
    {
        var d = new DialogWait();
        d.Begin(T0, true);
        Assert.Equal(DialogWaitPhase.WaitingForPerson, d.Phase);
    }

    [Fact]
    public void Failed_dialog_steps_make_the_verdict_FAIL_but_not_over_ERROR()
    {
        var none = new List<CheckpointResult>();
        Assert.Equal(ReplayResult.Fail, ReplayResult.ComputeVerdict(true, false, new[] { new CheckpointResult { Pass = true } }, failedSteps: 1));
        Assert.Equal(ReplayResult.Error, ReplayResult.ComputeVerdict(true, true, none, failedSteps: 1));
        Assert.Equal(ReplayResult.Pass, ReplayResult.ComputeVerdict(true, false, new[] { new CheckpointResult { Pass = true } }));
    }

    [Fact]
    public void Result_json_has_interactive_and_per_step_waitedForPerson()
    {
        var r = new ReplayResult { Interactive = true };
        r.Steps.Add(new StepResult { Index = 2, Kind = "dialog", WaitedForPerson = true });
        var j = JsonDocument.Parse(r.ToJson()).RootElement;
        Assert.True(j.GetProperty("interactive").GetBoolean());
        Assert.True(j.GetProperty("steps")[0].GetProperty("waitedForPerson").GetBoolean());
    }

    [Fact]
    public void A_command_that_opens_a_dialog_types_its_later_answers_only_after_the_dialog()
    {
        var lines = new[]
        {
            """{"t":1000,"n":1,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":1010,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":1200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            """{"t":2000,"n":4,"type":"modal_leave"}""",
            """{"t":2500,"n":5,"type":"prompt","kind":"point","input":true,"message":"Lower-left point of the grid:","status":"OK","value":[8.5,3.25,0]}""",
            """{"t":2510,"n":6,"type":"cmd_end","command":"DRAWGRID"}""",
        };
        var steps = Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        Assert.Equal(new[] { "command", "dialog" }, steps.Select(x => x.Kind));
        var (first, rest) = Plan.InputAroundDialog(steps[0], steps[1].T);
        Assert.Equal("_DRAWGRID\n", first);
        Assert.Equal("8.5,3.25\n", rest);
        Assert.Equal(Plan.InputText(steps[0]), first + rest);   // unattended replay still sends it all at once
    }

    static readonly string[] SamplesSession =
    {
        """{"t":3919,"n":3,"type":"tab","id":"SAMPLES_TAB","title":"Samples"}""",
        """{"t":5100,"n":4,"type":"ribbon","id":"DRAWLINE","text":"Line","macro":"Autodesk.Windows.RibbonButton","tabId":"SAMPLES_TAB","tab":"Samples","panel":"Draw"}""",
        """{"t":5134,"n":7,"type":"cmd_start","command":"DRAWLINE","origin":"typed/other"}""",
        """{"t":6213,"n":11,"type":"prompt","kind":"point","input":true,"by":"mouse","message":"Start point:","status":"OK","value":[10,17,0]}""",
        """{"t":6611,"n":13,"type":"prompt","kind":"point","input":true,"by":"mouse","message":"End point:","status":"OK","value":[12,19,0]}""",
        """{"t":6619,"n":14,"type":"cmd_end","command":"DRAWLINE"}""",
        """{"t":20008,"n":34,"type":"tab","id":"RETRACE_TAB","title":"Retrace"}""",
        """{"t":21870,"n":35,"type":"ribbon","id":"RETRACESTOP","text":"Stop","macro":"^C^C_RETRACESTOP ","tabId":"RETRACE_TAB","tab":"Retrace","panel":"Record"}""",
    };

    [Fact]
    public void A_code_button_with_a_type_name_for_macro_owns_its_command_and_Retrace_own_clicks_are_dropped()
    {
        Assert.Null(Plan.CleanMacro("Autodesk.Windows.RibbonButton"));
        Assert.Equal("^C^C_LINE ", Plan.CleanMacro("^C^C_LINE "));
        var steps = Plan.Build(SamplesSession.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        Assert.Equal(new[] { "tab", "ribbon" }, steps.Select(x => x.Kind));
        Assert.Equal("DRAWLINE", steps[1].Command);
        Assert.Equal(2, steps[1].Answers.Count);
    }

    [Fact]
    public void A_dialog_closed_without_confirming_fails_the_step()
    {
        var d = new DialogWait();
        d.Begin(DateTime.Now, false);
        d.ModalEntered();
        d.ModalLeft(0);
        Assert.Equal(DialogWaitPhase.Done, d.Phase);
        d.FailAfterClose("the command ended when the dialog closed");
        Assert.Equal(DialogWaitPhase.Failed, d.Phase);
        Assert.Equal("the command ended when the dialog closed", d.FailReason);
    }

    [Fact]
    public void Script_reference_finds_the_dialog_step_number_and_words_the_message()
    {
        var md = "1. Click **Home**\n2. A dialog opens: **Layer**. Fill it in and confirm.\n3. Check\n4. A dialog opens: **Plot**. Fill it in and confirm.\n";
        Assert.Equal(2, ScriptRef.DialogStepNumber(md, 1));
        Assert.Equal(4, ScriptRef.DialogStepNumber(md, 2));
        Assert.Null(ScriptRef.DialogStepNumber(md, 3));
        var nested = "1. Click **Home**\n2. Type `DRAWGRID` on the command line and press Enter\n   - A dialog opens: **Draw grid**. Fill it in and confirm.\n   - Pick a point\n3. A dialog opens: **Plot**. Fill it in and confirm.\n";
        Assert.Equal(2, ScriptRef.DialogStepNumber(nested, 1));
        Assert.Equal(3, ScriptRef.DialogStepNumber(nested, 2));
        Assert.Equal("A dialog opens: Layer. Fill it in and confirm (see a.md, step 2), then close it with OK.", ScriptRef.Message("Layer", "a.md", 2));
        Assert.Equal("A dialog opens: Layer. Fill it in and confirm, then close it with OK.", ScriptRef.Message("Layer", null, null));
    }

    [Fact]
    public void Screenshots_setting_defaults_on_and_roundtrips()
    {
        Assert.True(new RetraceSettings().Screenshots);
        var j = JsonSerializer.Serialize(new RetraceSettings { Screenshots = false });
        Assert.False(JsonSerializer.Deserialize<RetraceSettings>(j)!.Screenshots);
        Assert.True(JsonSerializer.Deserialize<RetraceSettings>("{}")!.Screenshots);
    }
}
