using System.Text.Json;
using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

/// <summary>Selection answers as AutoCAD 2026 raised them for -ATTEDIT and ERASE (synthetic copies of a real recording: no drawing names or handles of a user's file).</summary>
public class SelectionReplayTests
{
    static List<Step> Build(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
    const string Sel18 = """{"count":3,"handles":["268","267","187"],"dxf":["TEXT","LINE","VIEWPORT"],"method":"Unavailable"}""";

    [Fact]
    public void AttEdit_window_replays_as_its_corners_not_as_handles()
    {
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"-ATTEDIT","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"string","seq":1,"input":true,"by":"keys","message":"Edit attributes one at a time? [Yes/No] <Y>:","status":"Keyword","keyword":"N"}""",
            """{"t":30,"n":3,"type":"prompt","kind":"point","seq":7,"input":true,"by":"keys","message":"Select Attributes:","status":"OK","value":[0,0,0]}""",
            """{"t":40,"n":4,"type":"prompt","kind":"corner","seq":8,"input":true,"by":"keys","message":"Select Attributes:","status":"OK","value":[11,8.5,0]}""",
            "{\"t\":41,\"n\":5,\"type\":\"prompt\",\"kind\":\"selection\",\"input\":false,\"status\":\"OK\",\"value\":" + Sel18 + "}",
            """{"t":50,"n":6,"type":"prompt","kind":"selection","input":true,"by":"keys","status":"Error"}""",
            """{"t":60,"n":7,"type":"prompt","kind":"string","seq":11,"input":true,"by":"keys","message":"Enter string to change:","status":"Keyword","keyword":"B"}""",
            """{"t":70,"n":8,"type":"prompt","kind":"string","seq":12,"input":true,"by":"keys","message":"Enter new string:","status":"Keyword","keyword":"Z"}""",
            """{"t":71,"n":9,"type":"cmd_end","doc":"a.dwg","command":"-ATTEDIT"}""");
        Assert.Equal("_-ATTEDIT\nN\n0,0\n11,8.5\n\nB\nZ\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void Erase_window_keyword_corners_and_enter_replay_as_typed_and_an_empty_result_does_not_stop_the_typing()
    {
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"ERASE","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"point","seq":15,"input":true,"by":"keys","message":"Select objects:","status":"Keyword","keyword":"Window"}""",
            """{"t":30,"n":3,"type":"prompt","kind":"point","seq":16,"input":true,"by":"keys","message":"Specify first corner:","status":"OK","value":[-1,5,0]}""",
            """{"t":40,"n":4,"type":"prompt","kind":"corner","seq":17,"input":true,"by":"keys","message":"Specify first corner:","status":"OK","value":[30,30,0]}""",
            """{"t":50,"n":5,"type":"prompt","kind":"point","seq":19,"input":true,"by":"keys","message":"Select objects:","status":"None"}""",
            "{\"t\":51,\"n\":6,\"type\":\"prompt\",\"kind\":\"selection\",\"input\":false,\"status\":\"OK\",\"value\":" + Sel18 + "}",
            """{"t":52,"n":7,"type":"prompt","kind":"selection","input":false,"status":"Error"}""",
            """{"t":60,"n":8,"type":"cmd_end","doc":"a.dwg","command":"ERASE"}""");
        Assert.Equal("_ERASE\nWindow\n-1,5\n30,30\n\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void A_selection_that_stands_alone_still_replays_from_handles_and_leaves_out_what_the_drawing_lacks()
    {
        var answers = new List<Answer> { new("selection", "OK", JsonDocument.Parse("""{"handles":["1A","1A","2F","3C","?"],"dxf":["LINE","LINE","CIRCLE","LINE","?"]}""").RootElement.Clone(), null, null) };
        var step = new Step { Kind = "command", Command = "ERASE", Answers = answers };
        Assert.Equal("_ERASE\n(handent \"1A\")\n(handent \"2F\")\n(handent \"3C\")\n\n", Plan.InputText(step));
        // 2F is a LINE in the replay drawing (recorded CIRCLE), 3C is gone
        bool Exists(string h, string? dxf) => h == "1A" && dxf == "LINE";
        Assert.Equal("_ERASE\n(handent \"1A\")\n\n", Plan.InputText(step, handleExists: Exists));
        // none left: nothing to type after the command
        Assert.Equal("_ERASE\n", Plan.InputText(step, handleExists: (_, _) => false));
    }

    [Fact]
    public void Enter_is_not_doubled_when_it_was_recorded_as_its_own_answer()
    {
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"ERASE","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"point","seq":1,"input":true,"by":"mouse","message":"Select objects:","status":"OK","value":[5,5,0]}""",
            """{"t":30,"n":3,"type":"prompt","kind":"point","seq":2,"input":true,"by":"keys","message":"Select objects:","status":"None"}""",
            """{"t":31,"n":4,"type":"prompt","kind":"selection","input":true,"by":"keys","status":"Error"}""",
            """{"t":60,"n":8,"type":"cmd_end","doc":"a.dwg","command":"ERASE"}""");
        Assert.Equal("_ERASE\n5,5\n\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void Selection_input_is_told_from_other_points_by_prompt_text()
    {
        Answer P(string kind, string? msg) => new(kind, "OK", null, null, msg);
        Assert.True(SelectionReplay.IsSelectionInput(P("corner", null)));
        Assert.True(SelectionReplay.IsSelectionInput(P("point", "Select objects:")));
        Assert.True(SelectionReplay.IsSelectionInput(P("point", "Specify first corner:")));
        Assert.False(SelectionReplay.IsSelectionInput(P("point", "Specify base point:")));
        Assert.False(SelectionReplay.IsSelectionInput(P("string", "Select objects:")));
    }

    [Fact]
    public void A_base_point_before_a_stand_alone_selection_does_not_make_it_an_echo()
    {
        var sel = new Answer("selection", "OK", JsonDocument.Parse("""{"handles":["1A"],"dxf":["LINE"]}""").RootElement.Clone(), null, null);
        var effective = SelectionReplay.Effective(new List<Answer> { new("point", "OK", JsonDocument.Parse("[1,2,0]").RootElement.Clone(), null, "Specify base point:"), sel });
        Assert.Equal(2, effective.Count);
    }

    [Fact]
    public void A_selection_recorded_with_its_typed_text_replays_that_text_not_handles()
    {
        // what AutoCAD 2026 raised for "ERASE, w, -1,5, 30,30, Enter": one selection result, no point prompts
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"ERASE","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"selection","input":true,"by":"keys","status":"OK","value":{"count":2,"handles":["8E","8A"],"dxf":["LINE","LINE"],"method":"Unavailable","typed":"w\n-1,5\n30,30\n\n"}}""",
            """{"t":30,"n":3,"type":"cmd_end","doc":"a.dwg","command":"ERASE"}""");
        Assert.Equal("_ERASE\nw\n-1,5\n30,30\n\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void AttEdit_typed_corners_then_the_Enter_that_ended_the_selection()
    {
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"-ATTEDIT","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"selection","input":true,"by":"keys","status":"OK","value":{"count":3,"handles":["E0","E0","D9"],"dxf":["INSERT","INSERT","LINE"],"method":"Unavailable","typed":"0,0\n11,8.5\n"}}""",
            """{"t":30,"n":3,"type":"prompt","kind":"selection","input":true,"by":"keys","status":"Error"}""",
            """{"t":40,"n":4,"type":"prompt","kind":"string","seq":11,"input":true,"by":"keys","message":"Enter string to change:","status":"Keyword","keyword":"B"}""",
            """{"t":50,"n":5,"type":"cmd_end","doc":"a.dwg","command":"-ATTEDIT"}""");
        Assert.Equal("_-ATTEDIT\n0,0\n11,8.5\n\nB\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void Keys_become_the_characters_they_type_on_a_US_layout()
    {
        string Typed(params (uint vk, bool shift)[] keys) => new string(keys.Select(k => TypedInput.CharOf(k.vk, k.shift, false)).Where(c => c != null).Select(c => c!.Value).ToArray());
        Assert.Equal("-1,5", Typed((0xBD, false), (0x31, false), (0xBC, false), (0x35, false)));
        Assert.Equal("11,8.5", Typed((0x31, false), (0x31, false), (0xBC, false), (0x38, false), (0xBE, false), (0x35, false)));
        Assert.Equal("-ATTEDIT", Typed((0xBD, false), (0x41, true), (0x54, true), (0x54, true), (0x45, true), (0x44, true), (0x49, true), (0x54, true)));
        Assert.Equal("w", Typed((0x57, false)));
        Assert.Equal("FX_T", Typed((0x46, true), (0x58, true), (0xBD, true), (0x54, true)));
        Assert.Equal("(1)", Typed((0x39, true), (0x31, false), (0x30, true)));
        Assert.Null(TypedInput.CharOf(0x10, false, false));   // Shift alone
        Assert.Null(TypedInput.CharOf(0x25, false, false));   // an arrow
        Assert.Null(TypedInput.CharOf(0x1B, false, false));   // Esc
    }

    [Fact]
    public void Typed_text_is_kept_with_backspace_enter_and_esc_applied()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in "w\r-1,5x\b\r30,30\r\r") TypedInput.Append(sb, c);
        Assert.Equal("w\n-1,5\n30,30\n\n", sb.ToString());
        Assert.Equal(new[] { "w", "-1,5", "30,30", "" }, TypedInput.Lines(sb.ToString()));
        TypedInput.Append(sb, '\u001b');   // Esc is ignored
        Assert.Equal("w\n-1,5\n30,30\n\n", sb.ToString());
        sb.Clear();
        Assert.Null(TypedInput.Lines("12"));          // typed, never entered
        Assert.Null(TypedInput.Lines(null));
        sb.Append("a\n"); TypedInput.Append(sb, '\b');
        Assert.Equal("a\n", sb.ToString());           // Backspace does not cross a line break
    }

    // ---- review fixes ----

    static string? Typed(Action<TypedSelection> act)
    {
        var t = new TypedSelection();
        act(t);
        return t.Take();
    }
    static void Keys(TypedSelection t, string keys) { foreach (var c in keys) t.Key(c == '\n' ? '\r' : c); }
    static readonly double[] P1 = { 0, 0, 0 }, P2 = { 5, 5, 0 };

    [Fact]
    public void Typed_text_is_stored_only_when_it_alone_makes_the_whole_selection()
    {
        // typed window: two corners typed
        Assert.Equal("w\n0,0\n5,5\n", Typed(t => { Keys(t, "w\n0,0\n5,5\n"); t.Point(P1); t.Point(P2); t.Point(P2); }));
        // mouse window ended with Enter: two points arrived, none typed
        Assert.Null(Typed(t => { Keys(t, "\n"); t.Point(P1); t.Point(P2); }));
        // keys and mouse mixed: "w" typed, the corners clicked
        Assert.Null(Typed(t => { Keys(t, "w\n"); t.Point(P1); t.Point(P2); }));
        // mouse pick, Enter
        Assert.Null(Typed(t => { Keys(t, "\n"); t.Point(P1); }));
        // typed "all" needs no points
        Assert.Equal("all\n\n", Typed(t => Keys(t, "all\n\n")));
        // nothing typed at all
        Assert.Null(Typed(_ => { }));
    }

    [Fact]
    public void Space_is_Enter_and_text_after_the_last_Enter_is_never_replayed_quietly()
    {
        var t = new TypedSelection();
        foreach (var c in "w 0,0 5,5 ") t.Key(c);
        Assert.Equal("w\n0,0\n5,5\n", t.Text);
        Assert.Equal(new[] { "w", "0,0", "5,5" }, TypedInput.Lines(t.Text));
        Assert.Null(TypedInput.Lines("w\n0,0\n5,"));   // half a line left over: no replay from it
        Assert.Equal("w\n0,0\n5,5\n", Typed(x => { foreach (var c in "w 0,0 5,5 ") x.Key(c); x.Point(P1); x.Point(P2); }));
    }

    [Fact]
    public void A_LISP_expression_starts_the_text_over_but_one_typed_inside_a_prompt_spoils_it()
    {
        // (ssget) typed at the command line, then the selection keys
        Assert.Equal("w\n0,0\n5,5\n", Typed(t => { Keys(t, "(ssget)\n"); t.LispStart(nested: false); Keys(t, "w\n0,0\n5,5\n"); t.Point(P1); t.Point(P2); }));
        // a (handent ..) typed at "Select objects:" inside ERASE
        Assert.Null(Typed(t => { Keys(t, "w\n"); t.LispStart(nested: true); Keys(t, "0,0\n5,5\n"); t.Point(P1); t.Point(P2); }));
    }

    [Fact]
    public void A_point_that_is_not_selection_input_ends_the_text()
    {
        var t = new TypedSelection();
        Keys(t, "0,0\n");
        t.Point(P1);
        t.OtherAnswer();   // "Specify base point:" is not part of a selection
        Keys(t, "w\n0,0\n5,5\n"); t.Point(P1); t.Point(P2);
        Assert.Equal("w\n0,0\n5,5\n", t.Take());
        Assert.False(SelectionReplay.IsSelectionInput(new Answer("point", "OK", null, null, "Specify base point:")));
        Assert.True(SelectionReplay.IsSelectionInput(new Answer("point", "OK", null, null, "Select objects:")));
    }

    [Fact]
    public void A_transparent_or_own_command_inside_the_prompt_spoils_the_outer_text_and_never_half_of_it_is_replayed()
    {
        Assert.Null(Typed(t => { Keys(t, "w\n"); t.CommandStart(nested: true); Keys(t, "1\n"); Keys(t, "0,0\n5,5\n"); t.Point(P1); t.Point(P2); }));
        Assert.Null(Typed(t => { Keys(t, "w\n0,0\n"); t.Point(P1); t.OwnCommandStart(nested: true); t.Point(P2); Keys(t, "5,5\n"); }));
        // an own command at the command prompt (nothing running) changes nothing, and a top-level command starts clean
        Assert.Equal("w\n0,0\n5,5\n", Typed(t => { Keys(t, "-RETRACEMARK\n"); t.OwnCommandStart(nested: false); t.CommandStart(nested: false); Keys(t, "w\n0,0\n5,5\n"); t.Point(P1); t.Point(P2); }));
        // a bad text is dropped, the next selection is clean again
        var x = new TypedSelection();
        x.CommandStart(nested: true);
        Assert.Null(x.Take());
        Keys(x, "all\n\n");
        Assert.Equal("all\n\n", x.Take());
    }

    [Fact]
    public void A_keyword_result_at_the_selection_prompt_is_typed_as_it_was()
    {
        var kw = new Answer("selection", "Keyword", null, "All", null);
        var step = new Step { Kind = "command", Command = "ERASE", Answers = new List<Answer> { kw } };
        Assert.Equal("_ERASE\nAll\n", Plan.InputText(step));
    }

    [Fact]
    public void The_closing_Enter_is_not_typed_twice()
    {
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"ERASE","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"selection","input":true,"by":"keys","status":"OK","value":{"count":1,"handles":["8E"],"dxf":["LINE"],"typed":"w\n0,0\n5,5\n\n"}}""",
            """{"t":30,"n":3,"type":"prompt","kind":"selection","input":true,"by":"keys","status":"Error"}""",
            """{"t":40,"n":4,"type":"cmd_end","doc":"a.dwg","command":"ERASE"}""");
        Assert.Equal("_ERASE\nw\n0,0\n5,5\n\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void A_second_selection_result_after_an_echo_is_kept()
    {
        var pt = JsonDocument.Parse("[1,2,0]").RootElement.Clone();
        var sel = new Answer("selection", "OK", JsonDocument.Parse("""{"handles":["1A"],"dxf":["LINE"]}""").RootElement.Clone(), null, null);
        var effective = SelectionReplay.Effective(new List<Answer> { new("corner", "OK", pt, null, "Select objects:"), sel, sel });
        Assert.Equal(new[] { "corner", "selection" }, effective.Select(a => a.Kind));
    }

    [Fact]
    public void How_to_text_uses_the_same_answers_and_typed_text_as_the_replay()
    {
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"ERASE","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"corner","seq":1,"input":true,"by":"keys","message":"Select objects:","status":"OK","value":[0,0,0]}""",
            "{\"t\":21,\"n\":3,\"type\":\"prompt\",\"kind\":\"selection\",\"input\":false,\"status\":\"OK\",\"value\":" + Sel18 + "}",
            """{"t":22,"n":4,"type":"prompt","kind":"selection","input":true,"by":"keys","status":"Error"}""",
            """{"t":40,"n":5,"type":"cmd_end","doc":"a.dwg","command":"ERASE"}""");
        // the echo is gone, the Enter is there once
        Assert.Equal(new[] { "At the prompt 'Select objects', type 0,0.", "Press Enter." }, TdocPlan.AnswerLines(steps[0].Answers));
        var typed = new Answer("selection", "OK", JsonDocument.Parse("""{"handles":["1"],"typed":"w\n-1,5\n30,30\n\n"}""").RootElement.Clone(), null, "Select objects:");
        Assert.Equal("type w, then -1,5, then 30,30 and press Enter", TdocPlan.AnswerAction(typed));
        var s = new Step { Kind = "command", Command = "ERASE", Answers = new List<Answer> { typed } };
        Assert.Equal("Command line: type ERASE, then w, then -1,5, then 30,30 and press Enter", TdocPlan.CommandLineText(s));
    }
}
