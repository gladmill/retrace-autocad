using System.Text.Json;
using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

/// <summary>The view of clicks and pick points: what is recorded, what the replay shows, and when a pick point is typed instead of `(handent ..)`.</summary>
public class ViewPickTests
{
    static JsonElement Val(string json) => JsonDocument.Parse(json).RootElement.Clone();

    const string ViewJson = """{"space":"model","layout":"Model","vp":2,"center":[250,0],"height":200,"width":500,"dir":[0,0,1],"target":[250,0,0],"twist":0}""";
    static string Entity(string extra = "") => """{"handle":"7C","dxf":"LINE","pick":[30.5,0.0000002,0]""" + extra + "}";
    static int _prompt;
    /// <summary>An answer at a prompt of its own (each call another message: a repeated prompt means an edit happened in between).</summary>
    static Answer EntityAnswer(string kind, string json) => new(kind, "OK", Val(json), null, "Select " + System.Threading.Interlocked.Increment(ref _prompt));

    /// <summary>A replay that can show every view, and whose pick check says <paramref name="problem"/> (null = the point hits).</summary>
    static PickReplay Pick(string? problem = null, string? command = "BREAK", Func<ViewSpec, string?>? viewProblem = null) => new()
    {
        ViewProblem = viewProblem ?? (_ => null),
        PickProblem = (_, _, _, _, _) => problem,
        Command = command,
    };

    // ---- the view ----

    [Fact]
    public void A_recorded_view_is_read_back_and_a_malformed_one_is_no_view()
    {
        var v = ViewSpec.FromValue(Val("{\"view\":" + ViewJson + "}"));
        Assert.NotNull(v);
        Assert.Equal("model", v!.Space);
        Assert.Equal(200, v.Height);
        Assert.Equal(new double[] { 250, 0 }, v.Center);
        Assert.Null(ViewSpec.FromValue(Val("""{"handle":"1"}""")));
        Assert.Null(ViewSpec.FromValue(Val("""{"view":{"space":"model"}}""")));
        Assert.Null(ViewSpec.FromValue(Val("""{"view":{"space":"elsewhere","layout":"x","vp":1,"center":[0,0],"height":1,"width":1,"dir":[0,0,1],"target":[0,0,0],"twist":0}}""")));
        Assert.Null(ViewSpec.FromValue(Val("""{"view":{"space":"model","layout":"Model","vp":1,"center":[0,0],"height":0,"width":1,"dir":[0,0,1],"target":[0,0,0],"twist":0}}""")));
        Assert.Null(ViewSpec.FromValue(Val("""{"view":null}""")));
    }

    [Fact]
    public void The_view_round_trips_through_its_json()
    {
        var v = ViewSpec.FromValue(Val("{\"view\":" + ViewJson + "}"))!;
        var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["view"] = v.ToJson() });
        var back = ViewSpec.FromValue(Val(json))!;
        Assert.True(v.Same(back));
    }

    [Theory]
    [InlineData(2.5, 500, 200)]      // the recorded window shape: unchanged
    [InlineData(5.0, 1000, 200)]     // a wider window: more width, the recorded height stays
    [InlineData(1.25, 500, 400)]     // a narrower window: the recorded width stays, more height
    [InlineData(1.0, 500, 500)]
    public void Fit_keeps_the_whole_recorded_area_visible_in_any_window_shape(double aspect, double width, double height)
    {
        var v = ViewSpec.FromValue(Val("{\"view\":" + ViewJson + "}"))!;
        var (w, h) = v.Fit(aspect);
        Assert.Equal(width, w, 6);
        Assert.Equal(height, h, 6);
        Assert.True(w >= v.Width - 1e-9 && h >= v.Height - 1e-9);   // contains the recorded area
        Assert.Equal(aspect, w / h, 6);                              // and has the window's shape
    }

    [Fact]
    public void Fit_ignores_an_unusable_window_shape()
    {
        var v = ViewSpec.FromValue(Val("{\"view\":" + ViewJson + "}"))!;
        Assert.Equal((500.0, 200.0), v.Fit(0));
        Assert.Equal((500.0, 200.0), v.Fit(double.NaN));
    }

    [Fact]
    public void Same_view_and_plan_view_tests()
    {
        var a = ViewSpec.FromValue(Val("{\"view\":" + ViewJson + "}"))!;
        Assert.True(a.Same(a with { Center = new[] { 250.0, 0.0 } }));
        Assert.False(a.Same(a with { Height = 201 }));
        Assert.False(a.Same(a with { Layout = "A-101" }));
        Assert.False(a.Same(a with { Viewport = 3 }));
        Assert.True(a.IsPlanOf(new double[] { 0, 0, 1 }));
        Assert.False(a.IsPlanOf(new double[] { 1, 0, 0 }));
        Assert.True((a with { Direction = new[] { 0.0, 0.0, -1.0 } }).IsPlanOf(new double[] { 0, 0, 1 }));
    }

    // ---- pick points of entity answers ----

    [Fact]
    public void An_entity_pick_is_typed_as_its_point_when_it_still_hits_and_the_view_is_asked_for()
    {
        var pick = Pick();
        var lines = Plan.TypedLines(EntityAnswer("entity", Entity(",\"view\":" + ViewJson)), pick: pick);
        Assert.Equal(new[] { "30.5,0.0000002" }, lines);
        Assert.NotNull(pick.View);
        Assert.Equal(200, pick.View!.Height);
        Assert.Empty(pick.Notes);
    }

    [Fact]
    public void An_entity_pick_falls_back_to_the_handle_and_says_why()
    {
        // the point no longer hits
        var miss = Pick("the point is 4 from the object");
        Assert.Equal(new[] { "(handent \"7C\")" }, Plan.TypedLines(EntityAnswer("entity", Entity(",\"view\":" + ViewJson)), pick: miss));
        Assert.Null(miss.View);   // a failed check gives the view back
        Assert.Contains(miss.Notes, n => n.Contains("7C") && n.Contains("4 from") && n.Contains("(handent)"));
        // no pick point recorded
        var nopoint = Pick();
        Assert.Equal(new[] { "(handent \"7C\")" }, Plan.TypedLines(EntityAnswer("entity", """{"handle":"7C","dxf":"LINE","view":""" + ViewJson + "}"), pick: nopoint));
        Assert.Contains(nopoint.Notes, n => n.Contains("no pick point"));
        // no view recorded (a recording from before the view was kept)
        var noview = Pick();
        Assert.Equal(new[] { "(handent \"7C\")" }, Plan.TypedLines(EntityAnswer("entity", Entity()), pick: noview));
        Assert.Contains(noview.Notes, n => n.Contains("no view recorded"));
        // the view cannot be shown here (another layout)
        var other = Pick(viewProblem: _ => "recorded in Model, the replay is in layout \"A\"");
        Assert.Equal(new[] { "(handent \"7C\")" }, Plan.TypedLines(EntityAnswer("entity", Entity(",\"view\":" + ViewJson)), pick: other));
        Assert.Contains(other.Notes, n => n.Contains("layout"));
        Assert.Null(other.View);
    }

    [Fact]
    public void Without_a_pick_replay_an_entity_is_typed_as_a_handle_exactly_as_before()
    {
        Assert.Equal(new[] { "(handent \"7C\")" }, Plan.TypedLines(EntityAnswer("entity", Entity(",\"view\":" + ViewJson))));
        Assert.Equal(new[] { "(handent \"7C\")" }, Plan.TypedLines(EntityAnswer("nestedEntity", Entity(",\"view\":" + ViewJson))));
    }

    [Fact]
    public void A_nested_pick_is_typed_as_its_point_too_and_is_told_it_is_nested()
    {
        bool? sawNested = null;
        var pick = new PickReplay { ViewProblem = _ => null, PickProblem = (_, _, _, _, nested) => { sawNested = nested; return null; } };
        Assert.Equal(new[] { "30.5,0.0000002" }, Plan.TypedLines(EntityAnswer("nestedEntity", Entity(",\"view\":" + ViewJson)), pick: pick));
        Assert.True(sawNested);
    }

    [Fact]
    public void Two_picks_in_different_views_use_the_first_view_and_the_second_falls_back_to_its_handle()
    {
        var pick = Pick();
        var other = ViewJson.Replace("\"height\":200", "\"height\":100");
        Assert.Equal(new[] { "30.5,0.0000002" }, Plan.TypedLines(EntityAnswer("entity", Entity(",\"view\":" + ViewJson)), pick: pick));
        Assert.Equal(new[] { "(handent \"7C\")" }, Plan.TypedLines(EntityAnswer("entity", Entity(",\"view\":" + other)), pick: pick));
        Assert.Contains(pick.Notes, n => n.Contains("two different views"));
        // the same view twice: both typed
        Assert.Equal(new[] { "30.5,0.0000002" }, Plan.TypedLines(EntityAnswer("entity", Entity(",\"view\":" + ViewJson)), pick: pick));
    }

    [Fact]
    public void A_whole_break_step_types_the_pick_and_the_point_and_a_missing_object_still_stops_it()
    {
        var events = new[]
        {
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"BREAK","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"entity","seq":1,"input":true,"by":"mouse","message":"Select object:","status":"OK","value":{"handle":"7C","dxf":"LINE","pick":[30,0,0],"view":""" + ViewJson + "}}",
            """{"t":30,"n":3,"type":"prompt","kind":"point","seq":2,"input":true,"by":"mouse","message":"Specify second break point or [First point]:","status":"OK","value":[60,0,0]}""",
            """{"t":31,"n":4,"type":"cmd_end","doc":"a.dwg","command":"BREAK"}""",
        };
        var steps = Plan.Build(events.Select(Val));
        Assert.Equal("_BREAK\n30,0\n60,0\n", Plan.InputText(steps[0], pick: Pick()));
        Assert.Equal("_BREAK\n(handent \"7C\")\n60,0\n", Plan.InputText(steps[0]));
        Assert.Equal("_BREAK\n(handent \"7C\")\n60,0\n", Plan.InputText(steps[0], pick: Pick("another object (LINE 7D) is within the pick box too")));
        // the handle is gone from the replay drawing: nothing is typed for it (as before)
        Assert.Equal("_BREAK\n", Plan.InputText(steps[0], handleExists: (_, _) => false, pick: Pick()));
    }

    // ---- TRIM / EXTEND: a single click on one object ----

    static string ClickSel(string clicks = "280.0227367,0.00000025\\n", string handles = "[\"7E\"]", int count = 1) =>
        "{\"count\":" + count + ",\"handles\":" + handles + ",\"dxf\":[\"LINE\"],\"method\":\"Unavailable\",\"clicks\":\"" + clicks + "\",\"view\":" + ViewJson + "}";

    [Fact]
    public void In_trim_and_extend_a_single_click_on_one_object_replays_as_the_click()
    {
        foreach (var cmd in new[] { "TRIM", "EXTEND", "_.TRIM" })
        {
            var pick = Pick(command: cmd);
            var lines = Plan.TypedLines(EntityAnswer("selection", ClickSel()), pick: pick);
            Assert.Equal(new[] { "280.0227367,0.00000025" }, lines);
            Assert.NotNull(pick.View);
        }
    }

    [Fact]
    public void A_single_click_keeps_its_handle_when_the_command_does_not_pick_by_part_or_the_check_fails()
    {
        var erase = Pick(command: "ERASE");
        Assert.Equal(new[] { "(handent \"7E\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel()), pick: erase));
        Assert.Null(erase.View);
        var miss = Pick("another object (LINE 7D) is within the pick box too", "TRIM");
        Assert.Equal(new[] { "(handent \"7E\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel()), pick: miss));
        Assert.Contains(miss.Notes, n => n.Contains("selected by handle"));
        Assert.Null(miss.View);
        // no view recorded
        var noview = Pick(command: "TRIM");
        Assert.Equal(new[] { "(handent \"7E\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel().Replace(",\"view\":" + ViewJson, "")), pick: noview));
        // a window (two clicks) or several objects stay with the handles, as before
        Assert.Equal(new[] { "(handent \"7E\")", "(handent \"7D\")", "" },
            Plan.TypedLines(EntityAnswer("selection", ClickSel("1,1\\n5,5\\n", "[\"7E\",\"7D\"]", 2)), pick: Pick(command: "TRIM")));
        // and no pick replay at all: as before
        Assert.Equal(new[] { "(handent \"7E\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel())));
    }

    [Fact]
    public void A_whole_trim_step_types_the_click_and_the_enters()
    {
        var events = new[]
        {
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"TRIM","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"selection","input":true,"by":"keys","status":"Error"}""",
            "{\"t\":30,\"n\":3,\"type\":\"prompt\",\"kind\":\"selection\",\"input\":true,\"by\":\"mouse\",\"status\":\"OK\",\"value\":" + ClickSel() + "}",
            """{"t":31,"n":4,"type":"prompt","kind":"selection","input":false,"status":"OK","value":{"count":2,"handles":["7E","7D"],"dxf":["LINE","LINE"],"method":"Unavailable"}}""",
            """{"t":40,"n":5,"type":"prompt","kind":"selection","input":true,"by":"keys","status":"Error"}""",
            """{"t":41,"n":6,"type":"cmd_end","doc":"a.dwg","command":"TRIM"}""",
        };
        var steps = Plan.Build(events.Select(Val));
        Assert.Equal("_TRIM\n\n280.0227367,0.00000025\n\n", Plan.InputText(steps[0], pick: Pick(command: null)));
    }

    // ---- clicks that must be replayed as clicks (attributes) ----

    [Fact]
    public void Clicks_that_replay_as_clicks_ask_for_their_view_but_are_typed_in_any_case()
    {
        var att = "{\"count\":3,\"handles\":[\"145\",\"145\",\"145\"],\"dxf\":[\"INSERT\",\"INSERT\",\"INSERT\"],\"clicks\":\"0,0\\n11,8.5\\n\",\"view\":" + ViewJson + "}";
        var pick = Pick(command: "-ATTEDIT");
        Assert.Equal(new[] { "0,0", "11,8.5" }, Plan.TypedLines(EntityAnswer("selection", att), pick: pick));
        Assert.NotNull(pick.View);
        // a view that cannot be shown: the clicks are typed as before, a note says so
        var other = Pick(command: "-ATTEDIT", viewProblem: _ => "recorded in Model, the replay is in layout \"A\"");
        Assert.Equal(new[] { "0,0", "11,8.5" }, Plan.TypedLines(EntityAnswer("selection", att), pick: other));
        Assert.Null(other.View);
        Assert.Contains(other.Notes, n => n.Contains("clicks replayed in the current view"));
        // an old recording (no view): as before
        var old = Pick(command: "-ATTEDIT");
        Assert.Equal(new[] { "0,0", "11,8.5" }, Plan.TypedLines(EntityAnswer("selection", att.Replace(",\"view\":" + ViewJson, "")), pick: old));
        Assert.Null(old.View);
    }

    [Fact]
    public void A_plain_mouse_selection_asks_for_no_view()
    {
        var plain = "{\"count\":2,\"handles\":[\"8E\",\"8A\"],\"dxf\":[\"LINE\",\"LINE\"],\"clicks\":\"0,0\\n5,5\\n\\n\",\"view\":" + ViewJson + "}";
        var pick = Pick(command: "ERASE");
        Assert.Equal(new[] { "(handent \"8E\")", "(handent \"8A\")", "" }, Plan.TypedLines(EntityAnswer("selection", plain), pick: pick));
        Assert.Null(pick.View);
    }

    // ---- findings of the review ----

    [Fact]
    public void Nothing_is_typed_or_shown_for_a_view_recorded_inside_a_floating_viewport()
    {
        var vp = ViewJson.Replace("\"space\":\"model\"", "\"space\":\"viewport\"").Replace("\"layout\":\"Model\"", "\"layout\":\"A-101\"");
        var pick = Pick();
        Assert.Equal(new[] { "(handent \"7C\")" }, Plan.TypedLines(EntityAnswer("entity", Entity(",\"view\":" + vp)), pick: pick));
        Assert.Null(pick.View);
        Assert.Contains(pick.Notes, n => n.Contains("floating viewport"));
        // TRIM / EXTEND single click: the handle
        var trim = Pick(command: "TRIM");
        Assert.Equal(new[] { "(handent \"7E\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel().Replace(ViewJson, vp)), pick: trim));
        Assert.Null(trim.View);
        // clicks that must be clicks: typed as before, no view
        var att = "{\"count\":2,\"handles\":[\"145\",\"145\"],\"dxf\":[\"INSERT\",\"INSERT\"],\"clicks\":\"0,0\\n11,8.5\\n\",\"view\":" + vp + "}";
        var c = Pick(command: "-ATTEDIT");
        Assert.Equal(new[] { "0,0", "11,8.5" }, Plan.TypedLines(EntityAnswer("selection", att), pick: c));
        Assert.Null(c.View);
    }

    [Fact]
    public void A_pick_check_that_fails_gives_the_view_back_only_when_nothing_else_uses_it()
    {
        int n = 0;
        var pick = new PickReplay { ViewProblem = _ => null, PickProblem = (_, _, _, _, _) => ++n == 2 ? "miss" : null, Command = "BREAK" };
        Assert.Single(Plan.TypedLines(EntityAnswer("entity", Entity(",\"view\":" + ViewJson)), pick: pick)!);   // first pick uses the view
        Assert.Equal("(handent \"7C\")", Plan.TypedLines(EntityAnswer("entity", Entity(",\"view\":" + ViewJson)), pick: pick)![0]);   // second misses
        Assert.NotNull(pick.View);   // the first still needs it
    }

    [Fact]
    public void The_pick_array_parser_is_shared_and_strict()
    {
        Assert.Equal(new[] { 1.0, 2.0 }, ViewSpec.Nums(Val("""{"a":[1,2]}"""), "a", 2));
        Assert.Null(ViewSpec.Nums(Val("""{"a":[1]}"""), "a", 2));
        Assert.Null(ViewSpec.Nums(Val("""{"a":[1,"x"]}"""), "a", 2));
        Assert.Null(ViewSpec.Nums(Val("""{"a":5}"""), "a", 1));
        Assert.Null(ViewSpec.Nums(Val("[1,2]"), "a", 1));
    }

    [Fact]
    public void The_plane_of_a_plan_view_is_x_and_y_and_distances_ignore_height()
    {
        var b = ViewMath.PlaneBasis(new double[] { 0, 0, 1 });
        Assert.Equal(new[] { 1.0, 0.0, 0.0 }, b.X.Select(Math.Abs).Select(x => Math.Round(x, 9)).ToArray());
        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, b.Y.Select(Math.Abs).Select(x => Math.Round(x, 9)).ToArray());
        // a box 5 units above the picked point is not far in a plan view
        double[] origin = { 0, 0, 0 };
        Assert.Equal(0, ViewMath.BoxDistance(new double[] { -1, -1, 5 }, new double[] { 1, 1, 6 }, new double[] { 0, 0, 0 }, origin, b), 9);
        Assert.Equal(4, ViewMath.BoxDistance(new double[] { 4, -1, 5 }, new double[] { 6, 1, 6 }, new double[] { 0, 0, 0 }, origin, b), 9);
        Assert.Equal(3, ViewMath.PlaneLength(new double[] { 3, 0, 40 }, new double[] { 0, 0, 1 }), 9);
    }

    [Fact]
    public void A_point_outside_the_shown_view_or_in_a_twisted_view_is_not_accepted()
    {
        var v = ViewSpec.FromValue(Val("{\"view\":" + ViewJson.Replace("\"target\":[250,0,0]", "\"target\":[0,0,0]") + "}"))!;   // centre (250,0) from target (0,0), 500 x 200
        Assert.True(ViewMath.InView(v, 2.5, new double[] { 30, 0, 0 }));
        Assert.True(ViewMath.InView(v, 2.5, new double[] { 499, 99, 7 }));
        Assert.False(ViewMath.InView(v, 2.5, new double[] { 520, 0, 0 }));
        Assert.False(ViewMath.InView(v, 2.5, new double[] { 30, 150, 0 }));
        // a wider window shows more width: 520 is inside now
        Assert.True(ViewMath.InView(v, 5.0, new double[] { 520, 0, 0 }));
    }

    [Fact]
    public void A_twisted_view_turns_the_window_about_its_centre()
    {
        // the view of the VM check: PLAN in a UCS rotated 30 degrees: twist 330 degrees, centre (216.5, -125) in the view's own axes, 812 x 340
        var tw = "{\"space\":\"model\",\"layout\":\"Model\",\"vp\":2,\"center\":[216.5,-125],\"height\":340.36,\"width\":812.17,\"dir\":[0,0,1],\"target\":[0,0,0],\"twist\":5.759586532}";
        var v = ViewSpec.FromValue(Val("{\"view\":" + tw + "}"))!;
        double aspect = 812.17 / 340.36;
        Assert.True(ViewMath.InView(v, aspect, new double[] { 420, 0, 0 }));      // world (420,0) is (364,-210) in the view's axes: where the click was
        Assert.True(ViewMath.InView(v, aspect, new double[] { 450, 30, 0 }));
        Assert.False(ViewMath.InView(v, aspect, new double[] { 0, 420, 0 }));     // (210, 364) in the view's axes: far above the window
        Assert.False(ViewMath.InView(v, aspect, new double[] { 900, 0, 0 }));
        // a point just inside an untwisted window is out of it once the view is twisted by a quarter turn
        var plain = ViewSpec.FromValue(Val("{\"view\":" + ViewJson.Replace("\"center\":[250,0]", "\"center\":[0,0]").Replace("\"target\":[250,0,0]", "\"target\":[0,0,0]") + "}"))!;   // 500 x 200 about (0,0)
        Assert.True(ViewMath.InView(plain, 2.5, new double[] { 240, 0, 0 }));
        Assert.False(ViewMath.InView(plain with { Twist = Math.PI / 2 }, 2.5, new double[] { 240, 0, 0 }));
    }

    // ---- second review: one edit-capable pick per step, a stale view ----

    [Fact]
    public void A_second_pick_at_the_same_prompt_comes_after_an_edit_and_uses_the_handle_but_other_prompts_do_not()
    {
        var pick = Pick();
        string A(string prompt) => prompt;
        Assert.Equal(new[] { "30.5,0.0000002" }, Plan.TypedLines(new Answer("entity", "OK", Val(Entity(",\"view\":" + ViewJson)), null, A("Select object to offset:")), pick: pick));
        var second = Plan.TypedLines(new Answer("entity", "OK", Val(Entity(",\"view\":" + ViewJson)), null, A("Select object to offset:")), pick: pick);
        Assert.Equal(new[] { "(handent \"7C\")" }, second);
        Assert.Contains(pick.Notes, n => n.Contains("earlier pick at this prompt"));
        // FILLET's first and second object are different prompts, both before the edit
        var fillet = Pick(command: "FILLET");
        Assert.Equal(new[] { "30.5,0.0000002" }, Plan.TypedLines(new Answer("entity", "OK", Val(Entity(",\"view\":" + ViewJson)), null, "Select first object"), pick: fillet));
        Assert.Equal(new[] { "30.5,0.0000002" }, Plan.TypedLines(new Answer("entity", "OK", Val(Entity(",\"view\":" + ViewJson)), null, "Select second object"), pick: fillet));
    }

    [Fact]
    public void In_trim_a_later_click_on_another_object_is_typed_and_a_second_click_on_the_same_object_uses_its_handle()
    {
        var pick = Pick(command: "TRIM");
        Assert.Equal(new[] { "280.0227367,0.00000025" }, Plan.TypedLines(EntityAnswer("selection", ClickSel()), pick: pick));
        // another object: typed (TRIM only removes geometry on the object clicked)
        Assert.Equal(new[] { "469.97,0.5" }, Plan.TypedLines(EntityAnswer("selection", ClickSel("469.97,0.5\\n", "[\"7F\"]")), pick: pick));
        // the first object again: the handle
        Assert.Equal(new[] { "(handent \"7E\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel()), pick: pick));
        Assert.Contains(pick.Notes, n => n.Contains("clicked earlier"));
    }

    [Fact]
    public void In_trim_a_later_click_that_fails_its_check_or_in_extend_any_later_click_uses_the_handle()
    {
        int n = 0;
        var trim = new PickReplay { ViewProblem = _ => null, PickProblem = (_, _, _, _, _) => ++n == 2 ? "another object may be picked too" : null, Command = "TRIM" };
        Assert.Single(Plan.TypedLines(EntityAnswer("selection", ClickSel()), pick: trim)!);
        Assert.Equal(new[] { "(handent \"7F\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel("469.97,0.5\\n", "[\"7F\"]")), pick: trim));
        var extend = Pick(command: "EXTEND");
        Assert.Single(Plan.TypedLines(EntityAnswer("selection", ClickSel()), pick: extend)!);
        Assert.Equal(new[] { "(handent \"7F\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel("469.97,0.5\\n", "[\"7F\"]")), pick: extend));
        Assert.Contains(extend.Notes, x => x.Contains("earlier click"));
    }

    [Fact]
    public void A_first_pick_that_fell_back_to_its_handle_still_blocks_typed_picks_at_that_prompt()
    {
        int n = 0;
        var pick = new PickReplay { ViewProblem = _ => null, PickProblem = (_, _, _, _, _) => ++n == 1 ? "another object may be picked too" : null, Command = "OFFSET" };
        Answer A() => new("entity", "OK", Val(Entity(",\"view\":" + ViewJson)), null, "Select object to offset");
        Assert.Equal(new[] { "(handent \"7C\")" }, Plan.TypedLines(A(), pick: pick));   // first: check failed, handle
        Assert.Equal(new[] { "(handent \"7C\")" }, Plan.TypedLines(A(), pick: pick));   // second: the check would pass, but the first one edited
        Assert.Contains(pick.Notes, x => x.Contains("earlier pick at this prompt"));
    }

    [Fact]
    public void An_extend_first_click_that_fell_back_still_blocks_later_typed_extend_clicks()
    {
        int n = 0;
        var pick = new PickReplay { ViewProblem = _ => null, PickProblem = (_, _, _, _, _) => ++n == 1 ? "miss" : null, Command = "EXTEND" };
        Assert.Equal(new[] { "(handent \"7E\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel()), pick: pick));
        Assert.Equal(new[] { "(handent \"7F\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel("469.97,0.5\\n", "[\"7F\"]")), pick: pick));
        Assert.Contains(pick.Notes, x => x.Contains("earlier click"));
    }

    [Fact]
    public void In_trim_a_shift_click_or_a_window_or_any_other_selection_answer_blocks_later_typed_clicks()
    {
        // a Shift-click (extend) keeps no clicks, only handles
        var shift = Pick(command: "TRIM");
        var shiftSel = "{\"count\":1,\"handles\":[\"7D\"],\"dxf\":[\"LINE\"],\"method\":\"Unavailable\"}";
        Assert.Equal(new[] { "(handent \"7D\")", "" }, Plan.TypedLines(EntityAnswer("selection", shiftSel), pick: shift));
        Assert.Equal(new[] { "(handent \"7E\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel()), pick: shift));
        Assert.Contains(shift.Notes, x => x.Contains("earlier selection"));
        // a window made of two clicks
        var window = Pick(command: "TRIM");
        Assert.Equal(new[] { "(handent \"7E\")", "(handent \"7D\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel("1,1\\n5,5\\n", "[\"7E\",\"7D\"]", 2)), pick: window));
        Assert.Equal(new[] { "(handent \"7F\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel("469.97,0.5\\n", "[\"7F\"]")), pick: window));
        // a typed fence
        var fence = Pick(command: "TRIM");
        Assert.Equal(new[] { "f", "0,0", "5,5" }, Plan.TypedLines(EntityAnswer("selection", "{\"count\":2,\"handles\":[\"7E\",\"7D\"],\"typed\":\"f\\n0,0\\n5,5\\n\"}"), pick: fence));
        Assert.Equal(new[] { "(handent \"7F\")", "" }, Plan.TypedLines(EntityAnswer("selection", ClickSel("469.97,0.5\\n", "[\"7F\"]")), pick: fence));
    }

    [Fact]
    public void Picks_after_a_dialog_use_handles_and_the_answers_before_it_are_built_first_with_the_same_pick_replay()
    {
        Answer A(string prompt, long t) => new("entity", "OK", Val(Entity(",\"view\":" + ViewJson)), null, prompt, true, null, t);
        var step = new Step { Kind = "command", Command = "FILLET", Answers = new List<Answer> { A("Select first", 10), A("Select second", 30) } };
        var pick = Pick(command: "FILLET");
        var (first, after) = Plan.InputAroundDialog(step, 20, pick: pick);
        Assert.Equal("_FILLET\n30.5,0.0000002\n", first);          // before the dialog: a point, view claimed
        Assert.Equal("(handent \"7C\")\n", after);                  // after it: the dialog may have changed the drawing
        Assert.NotNull(pick.View);
        Assert.Contains(pick.Notes, n => n.Contains("follows a dialog"));
        // TRIM: a click after the dialog uses its handle too
        var trim = new Step { Kind = "command", Command = "TRIM", Answers = new List<Answer> { new("selection", "OK", Val(ClickSel("1,1\\n", "[\"7E\"]")), null, null, true, null, 30) } };
        var tpick = Pick(command: "TRIM");
        var (tf, ta) = Plan.InputAroundDialog(trim, 20, pick: tpick);
        Assert.Equal("(handent \"7E\")\n\n", ta);
    }

    [Fact]
    public void A_view_marked_stale_by_a_view_stale_event_is_not_used()
    {
        var events = new[]
        {
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"BREAK","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"entity","seq":1,"input":true,"by":"mouse","message":"Select object:","status":"OK","value":{"handle":"7C","dxf":"LINE","pick":[30,0,0],"view":""" + ViewJson + "}}",
            """{"t":30,"n":3,"type":"prompt","kind":"point","seq":2,"input":true,"by":"mouse","message":"Specify second break point or [First point]:","status":"OK","value":[60,0,0]}""",
            """{"t":31,"n":4,"type":"cmd_end","doc":"a.dwg","command":"BREAK"}""",
        };
        var fresh = Plan.Build(events.Select(Val));
        Assert.Equal("_BREAK\n30,0\n60,0\n", Plan.InputText(fresh[0], pick: Pick()));
        var stale = Plan.Build(events.Append("""{"t":32,"n":5,"type":"view_stale","for":2}""").Select(Val));
        Assert.Equal("_BREAK\n(handent \"7C\")\n60,0\n", Plan.InputText(stale[0], pick: Pick()));
        // the other fields of the answer are kept
        Assert.Equal("7C", Plan.Str(stale[0].Answers[0].Value!.Value, "handle"));
    }
}
