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

    static (string? Typed, string? Clicks) Both(Action<TypedSelection> act)
    {
        var t = new TypedSelection();
        act(t);
        return t.Take();
    }
    static string? Typed(Action<TypedSelection> act) => Both(act).Typed;
    static void Keys(TypedSelection t, string keys) { foreach (var c in keys) t.Key(c == '\n' ? '\r' : c); }
    static readonly double[] P1 = { 0, 0, 0 }, P2 = { 5, 5, 0 };
    /// <summary>A point typed at the keyboard: its line, Enter, then the point result (what the recorder sees).</summary>
    static void TypedPt(TypedSelection t, double[] p) { Keys(t, $"{p[0]},{p[1]}\n"); t.Point(p); }
    /// <summary>A point clicked in the drawing window.</summary>
    static void Click(TypedSelection t, double[] p) => t.Point(p, TypedSelection.PointLine(p[0], p[1], p[2]), click: true);

    [Fact]
    public void Clicks_are_stored_in_their_own_field_as_the_point_lines_that_type_them_and_typed_stays_typed()
    {
        // mouse window (two clicks) ended with Enter: no `typed`, the clicks text
        var w = Both(t => { Click(t, P1); Click(t, P2); Keys(t, "\n"); });
        Assert.Null(w.Typed);
        Assert.Equal("0,0\n5,5\n\n", w.Clicks);
        // two real clicks at the same spot are two points
        Assert.Equal("5,5\n5,5\n\n", Both(t => { Click(t, P2); Click(t, P2); Keys(t, "\n"); }).Clicks);
        // one pick
        Assert.Equal("1.5,2\n\n", Both(t => { Click(t, new[] { 1.5, 2, 0 }); Keys(t, "\n"); }).Clicks);
        // the last click completes the selection (-ATTEDIT, ERASE): the Enter comes later as its own selection result
        Assert.Equal("0,0\n5,5\n", Both(t => { Click(t, P1); Click(t, P2); }).Clicks);
        // "w" typed, corners clicked: the clicks text holds both, `typed` is empty
        var mixed = Both(t => { Keys(t, "w\n"); Click(t, P1); Click(t, P2); Keys(t, "\n"); });
        Assert.Null(mixed.Typed);
        Assert.Equal("w\n0,0\n5,5\n\n", mixed.Clicks);
        // a point typed, then one clicked
        Assert.Equal("1,1\n5,5\n", Both(t => { TypedPt(t, new[] { 1.0, 1, 0 }); Click(t, P2); }).Clicks);
        // a typed selection has no clicks field
        Assert.Null(Both(t => { Keys(t, "w\n"); TypedPt(t, P1); TypedPt(t, P2); Keys(t, "\n"); }).Clicks);
    }

    [Fact]
    public void Clicks_are_dropped_when_they_cannot_stand_for_the_selection()
    {
        // a Shift / Ctrl click (deselect, sub-object, cycling pick)
        Assert.Null(Both(t => { Click(t, P1); t.ClickModifier(); Click(t, P2); Keys(t, "\n"); }).Clicks);
        // a half-typed line before a click
        Assert.Null(Both(t => { Keys(t, "w"); Click(t, P1); Click(t, P2); Keys(t, "\n"); }).Clicks);
        // a transparent command inside the prompt
        Assert.Equal((null, null), Both(t => { Click(t, P1); t.CommandStart(true); Keys(t, "\n"); }));
        // a point that is neither typed nor clicked (LISP, noise) spoils both
        Assert.Equal((null, null), Both(t => { Click(t, P1); t.Point(P2); Keys(t, "\n"); }));
        // a click, then a bad text is dropped and the next selection is clean
        var x = new TypedSelection();
        x.ClickModifier();
        Assert.Null(x.Take().Clicks);
        Click(x, P1);
        Assert.Equal("0,0\n", x.Take().Clicks);
    }

    [Fact]
    public void Point_lines_are_fixed_notation_never_an_exponent()
    {
        Assert.Equal("0,0.1", TypedSelection.PointLine(0, 0.1, 0));
        Assert.Equal("0,0", TypedSelection.PointLine(1e-10, -1e-10, 0));   // rounded to 1e-8, no "1E-10", no "-0"
        Assert.Equal("0.00001,-0.00002", TypedSelection.PointLine(0.00001, -0.00002, 0));
        Assert.Equal("123456789012345,-98765432109876.5", TypedSelection.PointLine(123456789012345, -98765432109876.5, 0));
        Assert.Equal("100000000000000000000,1", TypedSelection.PointLine(1e20, 1, 0));
        Assert.Equal("1.5,2,3.25", TypedSelection.PointLine(1.5, 2, 3.25));
        Assert.Equal("1,2", TypedSelection.PointLine(1, 2, 1e-12));
        foreach (var v in new[] { 1e-300, 1e-7, 3.3e-9, 1.23456789e15, 4.5e25, -2.2e-6 })
            Assert.DoesNotContain("E", TypedSelection.PointLine(v, v, v), StringComparison.OrdinalIgnoreCase);
        Assert.True(TypedInput.IsPointLine(TypedSelection.PointLine(1e-300, 4.5e25, 0)));
    }

    [Fact]
    public void Typed_text_is_stored_only_when_it_alone_makes_the_whole_selection()
    {
        // typed window: two corners typed
        Assert.Equal("w\n0,0\n5,5\n", Typed(t => { Keys(t, "w\n"); TypedPt(t, P1); TypedPt(t, P2); }));
        // a point with no typed line and no click behind it: not reproducible
        Assert.Null(Typed(t => { Keys(t, "\n"); t.Point(P1); t.Point(P2); }));
        Assert.Null(Typed(t => { Keys(t, "w\n"); t.Point(P1); t.Point(P2); }));
        // typed "all" needs no points
        Assert.Equal("all\n\n", Typed(t => Keys(t, "all\n\n")));
        // nothing typed at all
        Assert.Null(Typed(_ => { }));
    }

    [Fact]
    public void A_point_is_typed_exactly_when_the_keys_since_the_last_point_end_with_a_point_line()
    {
        // click, then typed point: both known, in order
        Assert.Equal("0,0\n5,5\n", Both(t => { Click(t, P1); TypedPt(t, P2); }).Clicks);
        // typed point, then a click that follows a keyword line
        Assert.Equal("0,0\nw\n5,5\n", Both(t => { TypedPt(t, P1); Keys(t, "w\n"); Click(t, P2); }).Clicks);
        // a click is not mistaken for the typed point of an earlier line
        Assert.Equal("0,0\n5,5\n", Both(t => { TypedPt(t, P1); Click(t, P2); }).Clicks);
    }

    [Fact]
    public void Space_is_Enter_and_text_after_the_last_Enter_is_never_replayed_quietly()
    {
        var t = new TypedSelection();
        foreach (var c in "w 0,0 5,5 ") t.Key(c);
        Assert.Equal("w\n0,0\n5,5\n", t.Text);
        Assert.Equal(new[] { "w", "0,0", "5,5" }, TypedInput.Lines(t.Text));
        Assert.Null(TypedInput.Lines("w\n0,0\n5,"));   // half a line left over: no replay from it
        Assert.Equal("w\n0,0\n5,5\n", Typed(x => { foreach (var c in "w 0,0 ") x.Key(c); x.Point(P1); foreach (var c in "5,5 ") x.Key(c); x.Point(P2); }));
    }

    [Fact]
    public void A_LISP_expression_starts_the_text_over_but_one_typed_inside_a_prompt_spoils_it()
    {
        // (ssget) typed at the command line, then the selection keys
        Assert.Equal("w\n0,0\n5,5\n", Typed(t => { Keys(t, "(ssget)\n"); t.LispStart(nested: false); Keys(t, "w\n"); TypedPt(t, P1); TypedPt(t, P2); }));
        // a (handent ..) typed at "Select objects:" inside ERASE
        Assert.Null(Typed(t => { Keys(t, "w\n"); t.LispStart(nested: true); TypedPt(t, P1); TypedPt(t, P2); }));
    }

    [Fact]
    public void A_point_that_is_not_selection_input_ends_the_text()
    {
        var t = new TypedSelection();
        TypedPt(t, P1);
        t.OtherAnswer();   // "Specify base point:" is not part of a selection
        Keys(t, "w\n"); TypedPt(t, P1); TypedPt(t, P2);
        Assert.Equal("w\n0,0\n5,5\n", t.Take().Typed);
        Assert.False(SelectionReplay.IsSelectionInput(new Answer("point", "OK", null, null, "Specify base point:")));
        Assert.True(SelectionReplay.IsSelectionInput(new Answer("point", "OK", null, null, "Select objects:")));
    }

    [Fact]
    public void A_transparent_or_own_command_inside_the_prompt_spoils_the_outer_text_and_never_half_of_it_is_replayed()
    {
        Assert.Null(Typed(t => { Keys(t, "w\n"); t.CommandStart(nested: true); Keys(t, "1\n"); TypedPt(t, P1); TypedPt(t, P2); }));
        Assert.Null(Typed(t => { Keys(t, "w\n"); TypedPt(t, P1); t.OwnCommandStart(nested: true); Keys(t, "5,5\n"); t.Point(P2); }));
        // an own command at the command prompt (nothing running) changes nothing, and a top-level command starts clean
        Assert.Equal("w\n0,0\n5,5\n", Typed(t => { Keys(t, "-RETRACEMARK\n"); t.OwnCommandStart(nested: false); t.CommandStart(nested: false); Keys(t, "w\n"); TypedPt(t, P1); TypedPt(t, P2); }));
        // a bad text is dropped, the next selection is clean again
        var x = new TypedSelection();
        x.CommandStart(nested: true);
        Assert.Null(x.Take().Typed);
        Keys(x, "all\n\n");
        Assert.Equal("all\n\n", x.Take().Typed);
    }

    // ---- which way a mouse selection replays ----

    static JsonElement Val(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_mouse_selection_replays_from_its_handles_unless_handles_cannot_express_it()
    {
        // plain handles: clicks present but not used (the view, pickbox and window size must not matter)
        var plain = Val("""{"count":2,"handles":["8E","8A"],"dxf":["LINE","LINE"],"clicks":"0,0\n5,5\n\n"}""");
        Assert.False(SelectionReplay.UseClicks(plain));
        Assert.Equal(new[] { "(handent \"8E\")", "(handent \"8A\")", "" }, Plan.TypedLines(new Answer("selection", "OK", plain, null, null)));
        // -ATTEDIT: the insert's handle once per attribute: the clicks
        var att = Val("""{"count":3,"handles":["145","145","145"],"dxf":["INSERT","INSERT","INSERT"],"clicks":"0,0\n11,8.5\n"}""");
        Assert.True(SelectionReplay.UseClicks(att));
        Assert.Equal(new[] { "0,0", "11,8.5" }, Plan.TypedLines(new Answer("selection", "OK", att, null, null)));
        // an attribute in the list; a list cut short; no handles at all
        Assert.True(SelectionReplay.UseClicks(Val("""{"count":1,"handles":["9A"],"dxf":["ATTRIB"],"clicks":"1,1\n"}""")));
        Assert.True(SelectionReplay.UseClicks(Val("""{"count":300,"handles":["1","2"],"dxf":["LINE","LINE"],"clicks":"1,1\n3,3\n"}""")));
        Assert.True(SelectionReplay.UseClicks(Val("""{"count":2,"clicks":"1,1\n3,3\n"}""")));
        // no clicks (older recordings, Shift / Ctrl, a half-typed line): handles, as before, repeats dropped
        var none = Val("""{"count":3,"handles":["145","145","145"],"dxf":["INSERT","INSERT","INSERT"]}""");
        Assert.False(SelectionReplay.UseClicks(none));
        Assert.Equal(new[] { "(handent \"145\")", "" }, Plan.TypedLines(new Answer("selection", "OK", none, null, null)));
        // typed text wins over everything
        var typed = Val("""{"count":3,"handles":["145","145","145"],"dxf":["INSERT","INSERT","INSERT"],"typed":"w\n0,0\n5,5\n","clicks":"1,1\n"}""");
        Assert.Equal(new[] { "w", "0,0", "5,5" }, Plan.TypedLines(new Answer("selection", "OK", typed, null, null)));
    }

    [Fact]
    public void The_guide_never_prints_click_coordinates()
    {
        var a = new Answer("selection", "OK", Val("""{"count":3,"handles":["145","145","145"],"dxf":["INSERT","INSERT","INSERT"],"clicks":"0.5,2\n11,8.5\n\n"}"""), null, null);
        Assert.Equal("select the objects and press Enter", TdocPlan.AnswerAction(a));
        var s = new Step { Kind = "command", Command = "-ATTEDIT", Answers = new List<Answer> { a } };
        Assert.DoesNotContain("0.5", TdocPlan.CommandLineText(s));
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
