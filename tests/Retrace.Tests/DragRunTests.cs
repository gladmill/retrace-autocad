using System.Globalization;
using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Drag samples of a jig (one `prompt` event per sample, 1 to 10 ms apart) are one answer in the plan (DragRuns). Synthetic recordings, generic prompt texts.</summary>
public class DragRunTests
{
    static List<Step> Build(IEnumerable<string> lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
    static List<JsonElement> Events(IEnumerable<string> lines) => lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    const string PointMsg = "Specify the first corner or [Flip]:";
    const string AngleMsg = "Specify the turn:";

    static string D(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>One point sample. <paramref name="extra"/> goes in after "input", e.g. <c>"by":"keys",</c>.</summary>
    static string Point(int n, long t, double x, double y, bool input = false, string by = "", bool? drag = null, string msg = PointMsg, string status = "OK", string kw = "") =>
        $$"""{"t":{{t}},"n":{{n}},"type":"prompt","kind":"point","seq":{{n}},"input":{{(input ? "true" : "false")}},{{(by == "" ? "" : $"\"by\":\"{by}\",")}}{{(drag is { } dg ? $"\"drag\":{(dg ? "true" : "false")}," : "")}}"message":"{{msg}}","status":"{{status}}",{{(kw == "" ? "" : $"\"keyword\":\"{kw}\",")}}"value":[{{D(x)}},{{D(y)}},0]}""";

    static string Angle(int n, long t, double rad, bool input = false, string by = "", bool? drag = null) =>
        $$"""{"t":{{t}},"n":{{n}},"type":"prompt","kind":"angle","seq":{{n}},"input":{{(input ? "true" : "false")}},{{(by == "" ? "" : $"\"by\":\"{by}\",")}}{{(drag == true ? "\"drag\":true," : "")}}"message":"{{AngleMsg}}","status":"OK","value":{{D(rad)}}}""";

    /// <summary>Click, LISP, SCRIPT and a command that shows one dialog; the events after it are the command's prompts.</summary>
    static List<string> ChainHead() => new()
    {
        """{"t":500,"n":1,"type":"tab","id":"SAMPLES_TAB","title":"Samples"}""",
        """{"t":1000,"n":2,"type":"ribbon","id":"PLACE_BTN","text":"Place...","itemType":"RibbonCommandButton","macro":"^C^C(C:PLACEADD)","tabId":"SAMPLES_TAB","tab":"Samples","panel":"Dialogs"}""",
        """{"t":1020,"n":4,"type":"lisp_start","firstLine":"(C:PLACEADD)","startup":false,"script":false}""",
        """{"t":1100,"n":8,"type":"cmd_start","command":"SCRIPT","origin":"typed/other","script":false}""",
        """{"t":1150,"n":10,"type":"cmd_end","command":"SCRIPT"}""",
        """{"t":1150,"n":11,"type":"lisp_end"}""",
        """{"t":1200,"n":13,"type":"cmd_start","command":"PLACEIT","origin":"typed/other","script":true}""",
        """{"t":1300,"n":14,"type":"modal_enter","title":"AutoCAD - [Drawing1.dwg]"}""",
        """{"t":1400,"n":15,"type":"modal_title","title":"Place options","windowClass":"WindowsForms10.Window.8.app.0.1a2b3c"}""",
        """{"t":1600,"n":17,"type":"dialog_values","for":15,"title":"Place options","source":"win32","closedWith":"ok","fields":[],"read":3}""",
        """{"t":1610,"n":18,"type":"modal_leave"}""",
    };

    /// <summary>The run the person's recording had: samples with the mouse resting, a key press that claimed a sample, samples while typing, the typed point, then the angle the same way.</summary>
    static List<string> TypedDrag()
    {
        var l = new List<string>();
        int n = 100; long t = 3000;
        for (int i = 0; i < 4; i++) l.Add(Point(n++, t += 3, 1671.74, 3515));                                  // mouse resting
        l.Add(Point(n++, t += 3, 1671.74, 3515, input: true, by: "keys"));                                       // the key press "0" claimed a sample: same value
        for (int i = 0; i < 3; i++) l.Add(Point(n++, t += 3, i + 1, 3515));                                      // typing changes the preview
        l.Add(Point(n++, t += 150, 0, 0, input: true, by: "keys"));                                              // Enter: the answer
        t += 100;
        for (int i = 0; i < 4; i++) l.Add(Angle(n++, t += 3, 1.1269));
        l.Add(Angle(n++, t += 3, 1.1269, input: true, by: "keys"));                                              // spurious: the key "0"
        l.Add(Angle(n++, t += 3, 0.1));
        l.Add(Angle(n++, t += 150, 0, input: true, by: "keys"));                                                 // Enter
        l.Add("""{"t":9000,"n":999,"type":"cmd_end","command":"PLACEIT"}""");
        return l;
    }

    [Fact]
    public void A_typed_drag_in_a_script_chain_is_two_answers_the_typed_point_and_the_typed_angle()
    {
        var steps = Build(ChainHead().Concat(TypedDrag()));
        var ribbon = steps.First(s => s.Kind == "ribbon");
        Assert.True(ribbon.ChainClick);
        Assert.Equal(2, ribbon.Answers.Count);
        Assert.Equal(("point", "[0,0,0]"), (ribbon.Answers[0].Kind, ribbon.Answers[0].Value!.Value.GetRawText()));
        Assert.Equal(("angle", "0"), (ribbon.Answers[1].Kind, ribbon.Answers[1].Value!.Value.GetRawText()));
        Assert.All(ribbon.Answers, a => Assert.True(a.Input));
        Assert.Equal(new[] { "0,0", "0" }, ribbon.Answers.Select(a => Plan.TypedLines(a)!.Single()));
    }

    [Fact]
    public void The_spurious_key_claim_is_not_an_answer_but_the_last_claim_is()
    {
        var events = Events(TypedDrag());
        var c = DragRuns.Collapse(events);
        Assert.Equal(events.Count, c.Count);
        var kept = c.Where(e => Plan.Str(e, "type") == "prompt").Select(e => Plan.Num(e, "n")).ToList();
        Assert.Equal(2, kept.Count);                                   // the point's Enter and the angle's Enter
        Assert.All(c.Where(e => Plan.Str(e, "type") == DragRuns.DroppedType), e => Assert.True(Plan.Num(e, "n") > 0));
    }

    /// <summary>A recording made before the recorder wrote `drag`, whose samples were all `input: false`: the last one is the answer, and it counts as the person's.</summary>
    [Fact]
    public void An_old_recording_with_only_unclaimed_samples_in_a_chain_gives_the_last_value_of_each_run_as_the_persons_answer()
    {
        var l = new List<string>();
        int n = 100; long t = 3000;
        for (int i = 0; i < 30; i++) l.Add(Point(n++, t += 4, 1404.95 + i * 9, 3555));
        t += 50;
        for (int i = 0; i < 8; i++) l.Add(Angle(n++, t += 3, 1.1 + i / 100.0));
        l.Add("""{"t":9000,"n":999,"type":"cmd_end","command":"PLACEIT"}""");
        var steps = Build(ChainHead().Concat(l));
        var ribbon = steps.First(s => s.Kind == "ribbon");
        Assert.True(ribbon.ChainClick);
        Assert.Equal(2, ribbon.Answers.Count);
        Assert.Equal(1404.95 + 29 * 9, ribbon.Answers[0].Value!.Value[0].GetDouble(), 6);
        Assert.Equal(1.17, ribbon.Answers[1].Value!.Value.GetDouble(), 6);
        Assert.All(ribbon.Answers, a => Assert.True(a.Input));
    }

    [Fact]
    public void Flagged_samples_are_dropped_and_the_clicked_sample_is_the_answer()
    {
        var l = new List<string>();
        int n = 100; long t = 3000;
        for (int i = 0; i < 6; i++) l.Add(Point(n++, t += 5, 10 + i, 20, drag: true));
        l.Add(Point(n++, t += 5, 20, 30, input: true, by: "mouse"));
        for (int i = 0; i < 6; i++) l.Add(Angle(n++, t += 5 + (i == 0 ? 500 : 0), 0.1 * i, drag: true));
        l.Add(Angle(n++, t += 5, 0.9, input: true, by: "mouse"));
        l.Add("""{"t":9000,"n":999,"type":"cmd_end","command":"PLACEIT"}""");
        var ribbon = Build(ChainHead().Concat(l)).First(s => s.Kind == "ribbon");
        Assert.Equal(2, ribbon.Answers.Count);
        Assert.Equal("[20,30,0]", ribbon.Answers[0].Value!.Value.GetRawText());
        Assert.Equal("0.9", ribbon.Answers[1].Value!.Value.GetRawText());
    }

    [Fact]
    public void A_drag_in_a_plain_command_step_is_typed_once()
    {
        var l = new List<string> { """{"t":2000,"n":50,"type":"cmd_start","command":"PLACEIT","origin":"typed/other","script":false}""" };
        l.AddRange(TypedDrag().Take(TypedDrag().Count - 1));
        l.Add("""{"t":9000,"n":999,"type":"cmd_end","command":"PLACEIT"}""");
        var step = Assert.Single(Build(l));
        Assert.Equal("_PLACEIT\n0,0\n0\n", Plan.InputText(step));
    }

    [Fact]
    public void An_old_recording_in_a_plain_command_step_types_the_last_value_not_every_sample()
    {
        var l = new List<string> { """{"t":2000,"n":50,"type":"cmd_start","command":"PLACEIT","origin":"typed/other"}""" };
        int n = 100; long t = 3000;
        for (int i = 0; i < 12; i++) l.Add(Point(n++, t += 5, 100 + i, 200));
        l.Add("""{"t":9000,"n":999,"type":"cmd_end","command":"PLACEIT"}""");
        var step = Assert.Single(Build(l));
        Assert.Equal("_PLACEIT\n111,200\n", Plan.InputText(step));
    }

    [Fact]
    public void Two_prompts_in_a_row_or_a_slow_run_are_not_drag_samples()
    {
        // two results (a retry), and three typed answers seconds apart: nothing collapses
        var l = new List<string> { """{"t":2000,"n":50,"type":"cmd_start","command":"PLACEIT","origin":"typed/other"}""" };
        l.Add(Point(60, 2100, 1, 1, input: true, by: "keys"));
        l.Add(Point(61, 2150, 2, 2, input: true, by: "keys"));
        l.Add(Point(62, 5000, 3, 3, input: true, by: "keys"));
        l.Add(Point(63, 9000, 4, 4, input: true, by: "keys"));
        l.Add("""{"t":9500,"n":999,"type":"cmd_end","command":"PLACEIT"}""");
        var c = DragRuns.Collapse(Events(l));
        Assert.DoesNotContain(c, e => Plan.Str(e, "type") == DragRuns.DroppedType);
        Assert.Equal("_PLACEIT\n1,1\n2,2\n3,3\n4,4\n", Plan.InputText(Assert.Single(Build(l))));
    }

    [Fact]
    public void Clicks_end_a_run_so_two_picks_in_a_row_are_two_answers()
    {
        var l = new List<string> { """{"t":2000,"n":50,"type":"cmd_start","command":"PLACEIT","origin":"typed/other"}""" };
        int n = 100; long t = 3000;
        for (int i = 0; i < 5; i++) l.Add(Point(n++, t += 10, i, 0, drag: true));
        l.Add(Point(n++, t += 10, 5, 0, input: true, by: "mouse"));
        for (int i = 0; i < 5; i++) l.Add(Point(n++, t += 10, 5 + i, 7, drag: true));
        l.Add(Point(n++, t += 10, 10, 7, input: true, by: "mouse"));
        l.Add("""{"t":9500,"n":999,"type":"cmd_end","command":"PLACEIT"}""");
        Assert.Equal("_PLACEIT\n5,0\n10,7\n", Plan.InputText(Assert.Single(Build(l))));
    }

    [Fact]
    public void A_run_that_ends_in_a_keyword_has_the_keyword_as_its_answer()
    {
        var l = new List<string> { """{"t":2000,"n":50,"type":"cmd_start","command":"PLACEIT","origin":"typed/other"}""" };
        int n = 100; long t = 3000;
        for (int i = 0; i < 20; i++) l.Add(Point(n++, t += 3, 5, 5));
        l.Add(Point(n++, t += 3, 0, 0, input: true, by: "keys", status: "Keyword", kw: "Flip"));
        l.Add("""{"t":9500,"n":999,"type":"cmd_end","command":"PLACEIT"}""");
        Assert.Equal("_PLACEIT\nFlip\n", Plan.InputText(Assert.Single(Build(l))));
    }

    [Fact]
    public void Shots_and_view_marks_between_samples_do_not_split_a_run()
    {
        var l = new List<string>();
        int n = 100; long t = 3000;
        for (int i = 0; i < 6; i++)
        {
            l.Add(Point(n++, t += 5, i, 0));
            l.Add($$"""{"t":{{t}},"n":{{n++}},"type":"shot","for":{{n - 2}},"file":"x.png"}""");
        }
        var c = DragRuns.Collapse(Events(l));
        Assert.Equal(1, c.Count(e => Plan.Str(e, "type") == "prompt"));
    }

    /// <summary>A jig that samples only when the mouse moves or a key is pressed (the sample command's): bursts of two to four results, seconds apart; the claims of the typed keys 0.3 to 0.6 s apart.</summary>
    [Fact]
    public void A_jig_that_samples_on_events_gives_the_typed_answers_too()
    {
        var l = new List<string>();
        int n = 100;
        foreach (var t in new long[] { 14912, 14915, 18423, 18428, 18529, 18530, 18720, 18721, 18887, 18889, 19000, 19001 }) l.Add(Point(n++, t, 9.1, 4.97));
        l.Add(Point(n++, 19377, 9.1, 4.97, input: true, by: "keys"));
        for (int i = 0; i < 3; i++) l.Add(Point(n++, 19378 + i, 9.1, 4.97));
        l.Add(Point(n++, 19690, 9.1, 4.97, input: true, by: "keys"));
        l.Add(Point(n++, 19938, 9.1, 4.97, input: true, by: "keys"));
        l.Add(Point(n++, 20583, 0, 0, input: true, by: "keys"));
        l.Add(Angle(n++, 20589, 0.4998));
        l.Add(Angle(n++, 22115, 0.4998, input: true, by: "keys"));
        l.Add(Angle(n++, 22766, 0, input: true, by: "keys"));
        l.Add("""{"t":23000,"n":999,"type":"cmd_end","command":"PLACEIT"}""");
        var ribbon = Build(ChainHead().Concat(l)).First(s => s.Kind == "ribbon");
        Assert.Equal(new[] { "0,0", "0" }, ribbon.Answers.Select(a => Plan.TypedLines(a)!.Single()));
    }

    /// <summary>The recorder's own pattern: a key typed on the way claims nothing, the answer sample is the claimed one, the others in a burst are flagged.</summary>
    [Fact]
    public void A_typed_drag_made_by_the_new_recorder_is_the_answer_sample_alone()
    {
        var l = new List<string>();
        int n = 100; long t = 3000;
        for (int i = 0; i < 5; i++) l.Add(Point(n++, t += 3, 9.1, 4.97, drag: i > 0 ? true : null));
        l.Add(Point(n++, t += 400, 0, 0, input: true, by: "keys"));
        Assert.Equal(new[] { "0,0" }, Build(ChainHead().Concat(l)).First(s => s.Kind == "ribbon").Answers.Select(a => Plan.TypedLines(a)!.Single()));
    }

    static List<string> PlainStep() => new() { """{"t":2000,"n":50,"type":"cmd_start","command":"PLACEIT","origin":"typed/other","script":true}""" };
    static void End(List<string> l) => l.Add("""{"t":9500,"n":999,"type":"cmd_end","command":"PLACEIT"}""");

    // finding 1: a script that answers a jig itself
    [Fact]
    public void A_recording_that_writes_drag_never_promotes_a_burst_a_script_gave()
    {
        // (command "_.PLACEIT" ...) answered by a script: 4 results within a few ms, nobody at the input; the recorder wrote drag:false on the candidates
        var l = PlainStep();
        l.Add(Point(60, 3000, 1, 1));
        l.Add(Point(61, 3002, 2, 2, drag: false));
        l.Add(Point(62, 3004, 3, 3, drag: false));
        l.Add(Point(63, 3006, 0, 0, drag: false));
        End(l);
        var c = DragRuns.Collapse(Events(l));
        Assert.DoesNotContain(c, e => Plan.Str(e, "type") == DragRuns.DroppedType);
        Assert.All(c.Where(e => Plan.Str(e, "type") == "prompt"), e => Assert.False(Plan.Bool(e, "input") == true));
    }

    [Fact]
    public void A_recording_from_before_the_drag_field_still_promotes_the_last_result_of_a_burst()
    {
        var l = PlainStep();
        for (int i = 0; i < 4; i++) l.Add(Point(60 + i, 3000 + i * 2, i, i));
        End(l);
        Assert.Equal("_PLACEIT\n3,3\n", Plan.InputText(Assert.Single(Build(l))));
    }

    [Fact]
    public void The_recorder_flags_only_a_burst_somebody_made_with_no_lisp_running()
    {
        Assert.True(DragRuns.IsDragSample(candidate: true, claimed: false, msSincePrevious: 3, inputSincePrevious: true, lispRunning: false));
        Assert.False(DragRuns.IsDragSample(true, false, 3, inputSincePrevious: false, lispRunning: false));   // a script gave the answers: nothing moved
        Assert.False(DragRuns.IsDragSample(true, false, 3, true, lispRunning: true));                         // (command ...) of a LISP
        Assert.False(DragRuns.IsDragSample(true, true, 3, true, false));                                       // the answer
        Assert.False(DragRuns.IsDragSample(true, false, DragRuns.BurstGapMs + 1, true, false));                // too slow
        Assert.False(DragRuns.IsDragSample(false, false, 3, true, false));                                     // not the same prompt as the one before
    }

    // finding 3
    [Fact]
    public void Only_the_left_button_Enter_and_Space_end_a_prompt()
    {
        Assert.True(DragRuns.EndsPrompt(0x0201, 0));          // left button down
        Assert.True(DragRuns.EndsPrompt(0x0100, 0x0D));       // Enter
        Assert.True(DragRuns.EndsPrompt(0x0100, 0x20));       // Space
        Assert.False(DragRuns.EndsPrompt(0x0207, 0));         // middle button (pan)
        Assert.False(DragRuns.EndsPrompt(0x0204, 0));         // right button
        Assert.False(DragRuns.EndsPrompt(0x0100, 0x30));      // the key "0"
    }

    // finding 4
    [Fact]
    public void A_run_ended_by_Enter_keeps_the_scripts_own_results_that_show_no_drag()
    {
        // (command "_.PLACEIT" p1 p2 "") : two results, then Enter. Nothing says they are drag samples.
        var l = PlainStep();
        l.Add(Point(60, 3000, 1, 1));
        l.Add(Point(61, 3010, 5, 5));
        l.Add(Point(62, 3020, 0, 0, status: "None"));
        End(l);
        var c = DragRuns.Collapse(Events(l));
        Assert.DoesNotContain(c, e => Plan.Str(e, "type") == DragRuns.DroppedType);
    }

    // finding 6
    [Fact]
    public void A_person_typing_the_same_value_at_a_repeating_prompt_gives_every_answer()
    {
        var l = PlainStep();
        l.Add(Point(60, 3000, 5, 5, input: true, by: "keys"));
        l.Add(Point(61, 6000, 5, 5, input: true, by: "keys"));
        l.Add(Point(62, 9000, 5, 5, input: true, by: "keys"));
        End(l);
        var c = DragRuns.Collapse(Events(l));
        Assert.DoesNotContain(c, e => Plan.Str(e, "type") == DragRuns.DroppedType);
        Assert.Equal("_PLACEIT\n5,5\n5,5\n5,5\n", Plan.InputText(Assert.Single(Build(l))));
    }

    // finding 8
    [Fact]
    public void A_recording_without_a_drag_comes_back_with_the_same_elements()
    {
        var events = Events(PlainStep().Concat(new[] { Point(60, 3000, 1, 1, input: true, by: "mouse") }).Concat(new[] { """{"t":9500,"n":999,"type":"cmd_end","command":"PLACEIT"}""" }));
        var c = DragRuns.Collapse(events);
        for (int i = 0; i < events.Count; i++) Assert.Equal(events[i].GetRawText(), c[i].GetRawText());
    }

    [Fact]
    public void A_dropped_sample_does_not_end_a_script_chain()
    {
        var steps = Build(ChainHead().Concat(TypedDrag()));
        Assert.DoesNotContain(steps, s => s.Kind == "command");
    }
}
