using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>What a script chain's command is given after the click's dialogs: the person's answers, typed in order, each when the command waits at a prompt of the same kind and message (ChainDelivery, PromptWatch).</summary>
public class ChainDeliveryTests
{
    const string PointMsg = "Specify the first corner or [Flip]:";
    const string AngleMsg = "Specify the turn:";

    static Answer A(string kind, string value, string? message, string status = "OK", string? keyword = null) =>
        new(kind, status, value == "" ? null : JsonDocument.Parse(value).RootElement.Clone(), keyword, message, true);

    static readonly DateTime T0 = new(2026, 11, 8, 12, 0, 0);
    static DateTime At(double s) => T0.AddSeconds(s);

    [Fact]
    public void Nothing_is_typed_until_the_command_is_at_a_prompt_of_the_answers_kind_and_message()
    {
        var d = new ChainDelivery(new[] { A("point", "[0,0,0]", PointMsg), A("angle", "0", AngleMsg) }, At(0));
        var w = new PromptWatch();
        Assert.Equal(DeliveryKind.Wait, d.Tick(At(1), w).Kind);                                   // no prompt yet
        w.Prompting("angle", AngleMsg, At(2));
        Assert.Equal(DeliveryKind.Wait, d.Tick(At(2.1), w).Kind);                                 // the angle prompt is open, but the point is due first
        w.Prompting("point", "Some other prompt:", At(3));
        Assert.Equal(DeliveryKind.Wait, d.Tick(At(3.1), w).Kind);                                 // another message
        w.Prompting("point", "\n" + PointMsg + " ", At(4));
        var t = d.Tick(At(4.1), w);                                                               // the message is compared trimmed
        Assert.Equal(DeliveryKind.Type, t.Kind);
        Assert.Equal("0,0\n", t.Text);
    }

    [Fact]
    public void The_answers_go_in_order_each_after_a_fresh_prompt_and_then_it_is_done()
    {
        var d = new ChainDelivery(new[] { A("point", "[12.5,3,0]", PointMsg), A("angle", "1.5707963267948966", AngleMsg) }, At(0));
        var w = new PromptWatch();
        w.Prompting("point", PointMsg, At(1));
        Assert.Equal("12.5,3\n", d.Tick(At(1.5), w).Text);
        // the same prompt is still reported (a jig asks again and again) until the command takes the typed text: it is not a new prompt
        w.Prompting("point", PointMsg, At(1.6));
        Assert.Equal(DeliveryKind.Wait, d.Tick(At(2), w).Kind);
        w.Prompting("angle", AngleMsg, At(2.1));
        var t = d.Tick(At(2.5), w);
        Assert.Equal(DeliveryKind.Type, t.Kind);
        Assert.Equal("90\n", t.Text);                                                             // an angle is recorded in radians and typed in degrees
        Assert.Equal(DeliveryKind.Done, d.Tick(At(3), w).Kind);
        Assert.Equal(0, d.Remaining);
    }

    [Fact]
    public void Only_a_prompt_asked_after_the_last_typed_answer_counts_and_only_while_a_command_runs()
    {
        var w = new PromptWatch();
        Assert.Null(w.Open(DateTime.MinValue));
        w.Prompting("point", PointMsg, At(1));
        Assert.NotNull(w.Open(DateTime.MinValue));
        Assert.Null(w.Open(At(1)));                                                               // asked at the time the answer was typed or before
        w.Prompting("point", PointMsg, At(3));
        Assert.NotNull(w.Open(At(1)));
        var d = new ChainDelivery(new[] { A("point", "[1,2,0]", PointMsg) }, At(0));
        Assert.Equal(DeliveryKind.Wait, d.Tick(At(4), w, commandActive: false).Kind);            // the command is gone: nothing is typed into the command line
        Assert.Equal(DeliveryKind.Type, d.Tick(At(4.1), w, commandActive: true).Kind);
    }

    // finding 2
    [Fact]
    public void A_prompt_asked_before_the_delivery_started_is_not_the_one_the_first_answer_waits_for()
    {
        var w = new PromptWatch();
        w.Prompting("point", PointMsg, At(1));                                                    // an earlier prompt of the same message (the command's, before the dialog)
        var d = new ChainDelivery(new[] { A("point", "[0,0,0]", PointMsg) }, At(5));
        Assert.Equal(DeliveryKind.Wait, d.Tick(At(6), w).Kind);
        w.Prompting("point", PointMsg, At(7));
        Assert.Equal("0,0\n", d.Tick(At(7.1), w).Text);
    }

    [Fact]
    public void A_prompt_asked_while_the_dialog_was_open_counts_when_the_delivery_is_told_when_the_dialog_opened()
    {
        var w = new PromptWatch();
        w.Prompting("point", PointMsg, At(1));                                                    // before the dialog opened
        var d = new ChainDelivery(new[] { A("point", "[0,0,0]", PointMsg) }, At(5), promptsAfter: At(3));
        Assert.Equal(DeliveryKind.Wait, d.Tick(At(5), w).Kind);
        w.Prompting("point", PointMsg, At(4));                                                    // after the dialog opened, before the first look
        Assert.Equal(DeliveryKind.Type, d.Tick(At(5.1), w).Kind);
    }

    // finding 7
    [Fact]
    public void Answers_of_a_recording_with_no_input_flags_are_not_typed()
    {
        var noFlag = new Answer("point", "OK", JsonDocument.Parse("[0,0,0]").RootElement.Clone(), null, PointMsg, null);
        Assert.NotNull(ChainDelivery.Unsafe(new[] { noFlag }));
        Assert.NotNull(ChainDelivery.Unsafe(new[] { A("point", "[0,0,0]", PointMsg), noFlag }));
        Assert.Null(ChainDelivery.Unsafe(new[] { A("point", "[0,0,0]", PointMsg) }));
    }

    [Fact]
    public void A_prompt_that_never_comes_times_out_with_the_existing_pause_wording()
    {
        var d = new ChainDelivery(new[] { A("point", "[1,2,0]", PointMsg) }, At(0));
        var w = new PromptWatch();
        Assert.Equal(DeliveryKind.Wait, d.Tick(At(ChainDelivery.WaitSeconds - 1), w).Kind);
        var t = d.Tick(At(ChainDelivery.WaitSeconds), w);
        Assert.Equal(DeliveryKind.TimedOut, t.Kind);
        var step = new Step { Kind = "ribbon", Title = "Click Samples > Dialogs > Place..." };
        var msg = d.TimedOutMessage(step);
        Assert.Contains("Place...", msg);
        Assert.Contains("press Next step", msg);
    }

    [Fact]
    public void Keywords_and_Enter_are_typed_and_text_with_a_space_a_selection_and_Esc_are_not()
    {
        Assert.Null(ChainDelivery.Unsafe(new[] { A("point", "", PointMsg, "Keyword", "Flip"), A("point", "", PointMsg, "None"), A("distance", "7.5", "Radius:"), A("integer", "3", "How many:"), A("string", "\"abc\"", "Name:") }));
        Assert.Equal("Flip\n", ChainDelivery.TextOf(A("point", "", PointMsg, "Keyword", "Flip")));
        Assert.Equal("\n", ChainDelivery.TextOf(A("point", "", PointMsg, "None")));
        Assert.NotNull(ChainDelivery.Unsafe(new[] { A("string", "\"a b\"", "Name:") }));
        Assert.NotNull(ChainDelivery.Unsafe(new[] { A("selection", "{\"handles\":[\"1F\"]}", "Select objects:") }));
        Assert.NotNull(ChainDelivery.Unsafe(new[] { A("entity", "{\"handle\":\"1F\"}", "Select an object:") }));
        Assert.NotNull(ChainDelivery.Unsafe(new[] { A("point", "", PointMsg, "Cancel") }));
        // one answer that cannot be typed holds back the whole group: nothing is typed out of order
        Assert.NotNull(ChainDelivery.Unsafe(new[] { A("point", "[0,0,0]", PointMsg), A("string", "\"a b\"", "Name:") }));
    }

    [Fact]
    public void An_answer_without_a_recorded_message_matches_on_kind_alone()
    {
        Assert.True(ChainDelivery.Matches(A("angle", "0", null), ("angle", "Anything:")));
        Assert.False(ChainDelivery.Matches(A("angle", "0", null), ("point", "Anything:")));
        Assert.False(ChainDelivery.Matches(A("angle", "0", AngleMsg), ("angle", "Another:")));
        Assert.True(ChainDelivery.Matches(A("angle", "0", AngleMsg), ("point", AngleMsg)));       // an angle with a base point asks for a point inside, with the same message
        Assert.True(ChainDelivery.Matches(A("angle", "0", AngleMsg.ToUpperInvariant()), ("angle", " " + AngleMsg)));
    }

    [Fact]
    public void Two_answers_are_not_typed_within_the_minimum_gap()
    {
        var d = new ChainDelivery(new[] { A("point", "[1,1,0]", PointMsg), A("point", "[2,2,0]", PointMsg) }, At(0));
        var w = new PromptWatch();
        w.Prompting("point", PointMsg, At(1));
        Assert.Equal(DeliveryKind.Type, d.Tick(At(1.0), w).Kind);
        w.Prompting("point", PointMsg, At(1.1));
        Assert.Equal(DeliveryKind.Wait, d.Tick(At(1.1), w).Kind);
        w.Prompting("point", PointMsg, At(1.5));
        Assert.Equal("2,2\n", d.Tick(At(1.5), w).Text);
    }

    [Fact]
    public void The_recorded_typed_drag_becomes_exactly_two_typed_lines()
    {
        // the plan of a jig recording (DragRunTests) feeds the delivery: one point and one angle, the spurious claim dropped
        var lines = new[]
        {
            """{"t":1000,"n":2,"type":"ribbon","id":"PLACE_BTN","text":"Place...","itemType":"RibbonCommandButton","macro":"^C^C(C:PLACEADD)","tabId":"T","tab":"Samples","panel":"Dialogs"}""",
            """{"t":1020,"n":4,"type":"lisp_start","firstLine":"(C:PLACEADD)","startup":false,"script":false}""",
            """{"t":1100,"n":8,"type":"cmd_start","command":"SCRIPT","origin":"typed/other","script":false}""",
            """{"t":1150,"n":10,"type":"cmd_end","command":"SCRIPT"}""",
            """{"t":1150,"n":11,"type":"lisp_end"}""",
            """{"t":1200,"n":13,"type":"cmd_start","command":"PLACEIT","origin":"typed/other","script":true}""",
            """{"t":1300,"n":14,"type":"modal_enter","title":"AutoCAD - [Drawing1.dwg]"}""",
            """{"t":1400,"n":15,"type":"modal_title","title":"Place options","windowClass":"X"}""",
            """{"t":1600,"n":17,"type":"dialog_values","for":15,"title":"Place options","source":"win32","closedWith":"ok","fields":[],"read":3}""",
            """{"t":1610,"n":18,"type":"modal_leave"}""",
            """{"t":3000,"n":100,"type":"prompt","kind":"point","input":false,"message":"Specify the first corner or [Flip]:","status":"OK","value":[5,5,0]}""",
            """{"t":3003,"n":101,"type":"prompt","kind":"point","input":false,"message":"Specify the first corner or [Flip]:","status":"OK","value":[5,5,0]}""",
            """{"t":3006,"n":102,"type":"prompt","kind":"point","input":true,"by":"keys","message":"Specify the first corner or [Flip]:","status":"OK","value":[5,5,0]}""",
            """{"t":3009,"n":103,"type":"prompt","kind":"point","input":false,"message":"Specify the first corner or [Flip]:","status":"OK","value":[0,5,0]}""",
            """{"t":3012,"n":104,"type":"prompt","kind":"point","input":true,"by":"keys","message":"Specify the first corner or [Flip]:","status":"OK","value":[0,0,0]}""",
            """{"t":3100,"n":105,"type":"prompt","kind":"angle","input":false,"message":"Specify the turn:","status":"OK","value":0.5}""",
            """{"t":3103,"n":106,"type":"prompt","kind":"angle","input":false,"message":"Specify the turn:","status":"OK","value":0.5}""",
            """{"t":3106,"n":107,"type":"prompt","kind":"angle","input":true,"by":"keys","message":"Specify the turn:","status":"OK","value":0.5}""",
            """{"t":3109,"n":108,"type":"prompt","kind":"angle","input":true,"by":"keys","message":"Specify the turn:","status":"OK","value":0}""",
            """{"t":9000,"n":999,"type":"cmd_end","command":"PLACEIT"}""",
        };
        var steps = Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        var ribbon = steps.First(s => s.Kind == "ribbon");
        var dialog = steps.First(s => s.Kind == "dialog");
        var due = ReplayRules.ChainAnswersAfter(ribbon.Answers, steps, steps.IndexOf(dialog));
        Assert.Null(ChainDelivery.Unsafe(due));
        Assert.Equal(new[] { "0,0\n", "0\n" }, due.Select(ChainDelivery.TextOf));
    }
}
