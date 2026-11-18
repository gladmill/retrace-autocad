using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>A click on an item of AutoCAD's status bar: the event, the words of the step, finding the item again (text, the stable part of a text that changes with state, tool tip, place for an icon, key for AutoCAD's own),
/// what is and is not recorded, the plan (the step comes before the dialog it opens; the command it starts belongs to it), steps.md and the old readers. Synthetic recordings only.</summary>
public class StatusBarClickTests
{
    static StatusBarClick Add(string text, string tip = "", int index = 3, string button = "left") => new(text, tip, index, StatusBarClick.Pane, false, "", "", button);
    static StatusBarClick Own(string key, string name) => new("", "", -1, StatusBarClick.Pane, true, key, name, "left");
    static StatusBarMatch.Live L(string text, string tip = "", int index = 0, bool own = false, string key = "", bool visible = true, string kind = "pane") => new(text, tip, index, kind, own, key, visible);

    // ---- wording

    [Fact]
    public void The_step_says_click_the_text_on_the_status_bar()
    {
        Assert.Equal("Click Sample TB: edit on the status bar", StatusBarWording.Phrase(Add("Sample TB: edit")));
        Assert.Equal("Click **Sample TB: edit** on the status bar", StatusBarWording.Phrase(Add("Sample TB: edit"), x => $"**{x}**"));
        Assert.Equal("Right-click Sample menu on the status bar", StatusBarWording.Phrase(Add("Sample menu", button: "right")));
    }

    [Fact]
    public void An_item_with_no_text_is_called_by_its_tool_tip_then_by_the_name_autocad_gives_it_never_by_a_guess()
    {
        Assert.Equal("Click Edit the sample settings on the status bar", StatusBarWording.Phrase(Add("", "Edit the sample settings")));
        Assert.Equal("Click Grid on the status bar", StatusBarWording.Phrase(Own("GridPane", "Grid")));
        var nothing = StatusBarWording.Phrase(Add("", "", 2));
        Assert.Equal("Click the item at position 3 of the status bar (it has no text or tool tip)", nothing);
        Assert.Null(Add("  ", "").Label);
    }

    [Fact]
    public void Blanks_in_the_text_are_flattened_in_the_label()
    {
        Assert.Equal("Sample TB: edit", Add("  Sample   TB: edit ").Label);
    }

    // ---- the event

    [Fact]
    public void The_event_fields_read_back()
    {
        var c = new StatusBarClick("Sample TB: edit", "Edit", 61, StatusBarClick.Pane, false, "", "", "left");
        var json = JsonSerializer.Serialize(c.ToFields().ToDictionary(f => f.Item1, f => f.Item2));
        var back = StatusBarClick.From(JsonDocument.Parse(json).RootElement);
        Assert.Equal(c, back);
    }

    [Fact]
    public void An_event_with_nothing_to_find_the_item_by_is_no_click_and_unknown_kinds_are_panes()
    {
        Assert.Null(StatusBarClick.From(JsonDocument.Parse("""{"type":"statusbar_click","button":"left"}""").RootElement));
        var c = StatusBarClick.From(JsonDocument.Parse("""{"type":"statusbar_click","text":"X","kind":"weird","button":"middle"}""").RootElement)!;
        Assert.Equal(StatusBarClick.Pane, c.Kind);
        Assert.Equal(StatusBarClick.Left, c.Button);
    }

    // ---- finding the item again

    [Fact]
    public void Exact_text_is_found()
    {
        var f = StatusBarMatch.Find(Add("Sample TB: edit"), [L("Other", index: 1), L("sample tb:  edit", index: 4)]);
        Assert.Equal(4, f.Item!.Index);
        Assert.Equal("by its text", f.How);
        Assert.Null(f.Problem);
    }

    [Theory]
    [InlineData("Sample TB: edit", "Sample TB: view")]   // the state after the colon changes
    [InlineData("Layer 3", "Layer 12")]                  // a number changes
    [InlineData("Sample TB: edit", "Sample TB:")]
    public void A_text_that_changes_with_state_is_found_by_its_stable_part(string recorded, string live)
    {
        var f = StatusBarMatch.Find(Add(recorded), [L("Unrelated", index: 1), L(live, index: 5)]);
        Assert.Equal(5, f.Item!.Index);
        Assert.Equal("by the stable part of its text", f.How);
    }

    [Fact]
    public void Different_words_are_not_the_same_item_and_the_index_never_replaces_a_recorded_text()
    {
        var f = StatusBarMatch.Find(Add("Sample TB: edit", index: 1), [L("Other thing", index: 1), L("Sample run", index: 2)]);
        Assert.Null(f.Item);
        Assert.Contains("no status bar item with text \"Sample TB: edit\"", f.Problem);
    }

    [Fact]
    public void The_tool_tip_finds_an_icon_item_and_the_place_only_an_item_that_had_neither_text_nor_tool_tip()
    {
        Assert.Equal(2, StatusBarMatch.Find(Add("", "Edit settings", 9), [L("", "Edit settings", 2), L("", "Other", 9)]).Item!.Index);
        var byPlace = StatusBarMatch.Find(Add("", "", 7), [L("", "", 6), L("", "", 7), L("Text", "", 7)]);
        Assert.Equal(7, byPlace.Item!.Index);
        Assert.Equal("", byPlace.Item.Text);
        Assert.Null(StatusBarMatch.Find(Add("", "", 7), [L("Some text", "", 7)]).Item);
    }

    [Fact]
    public void Several_matches_prefer_the_recorded_place_and_the_answer_says_so()
    {
        var f = StatusBarMatch.Find(Add("Same", index: 8), [L("Same", index: 2), L("Same", index: 8)]);
        Assert.Equal(8, f.Item!.Index);
        Assert.Contains("2 items match", f.How);
    }

    [Fact]
    public void Autocads_own_items_are_found_by_key_only()
    {
        var live = new[] { L("", index: 0, own: true, key: "SnapPane"), L("", index: 1, own: true, key: "GridPane"), L("Grid", index: 2) };
        Assert.Equal("GridPane", StatusBarMatch.Find(Own("GridPane", "Grid"), live).Item!.Key);
        var missing = StatusBarMatch.Find(Own("OrthoPane", "Ortho Mode"), live);
        Assert.Null(missing.Item);
        Assert.Contains("OrthoPane", missing.Problem);
    }

    [Fact]
    public void An_item_that_is_hidden_is_a_problem_not_a_missing_item()
    {
        var f = StatusBarMatch.Find(Add("Sample TB: edit"), [L("Sample TB: edit", index: 4, visible: false)]);
        Assert.NotNull(f.Item);
        Assert.Contains("not shown", f.Problem);
        Assert.Contains("Customization menu or widen AutoCAD's window", f.Problem);
    }

    [Fact]
    public void Panes_and_tray_items_are_not_mixed_up()
    {
        var rec = new StatusBarClick("", "Sample tray", 0, StatusBarClick.Tray, false, "", "", "left");
        var f = StatusBarMatch.Find(rec, [L("", "Sample tray", 0, kind: "pane"), L("", "Sample tray", 1, kind: "tray")]);
        Assert.Equal(1, f.Item!.Index);
        Assert.Equal("tray", f.Item.Kind);
    }

    [Theory]
    [InlineData("Sample TB: edit", "sample tb")]
    [InlineData("Layer 12", "layer #")]
    [InlineData("MODEL", "model")]
    [InlineData("A: b: c", "a: b")]
    public void Stable_part(string text, string expected) => Assert.Equal(expected, StatusBarMatch.StablePart(text));

    // ---- what is recorded

    [Fact]
    public void An_add_ins_click_is_recorded_with_either_button_and_ours_are_left_out_silently()
    {
        Assert.Equal((true, false, null), StatusBarMatch.Decide("Sample TB: edit", "", false, "", "left"));
        Assert.Equal((true, false, null), StatusBarMatch.Decide("Sample TB: edit", "", false, "", "right"));
        Assert.Equal((false, true, null), StatusBarMatch.Decide("", "acad-1234: idle - Idle, Drawing1.dwg", false, "", "left"));
        Assert.True(StatusBarMatch.IsToolItem("acad-77: busy", ""));
        Assert.False(StatusBarMatch.IsToolItem("my acad-77: x", "Sample"));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    public void AutoCADs_own_items_are_not_recorded_whichever_button_and_the_line_names_the_item(string button)
    {
        var d = StatusBarMatch.Decide("", "", true, "Grid", button);
        Assert.False(d.Record); Assert.False(d.Silent);
        Assert.Equal("AutoCAD's own item Grid (toggles are not recorded)", d.Reason);
        Assert.Contains("(no name)", StatusBarMatch.Decide("", "", true, null, button).Reason);
    }

    [Fact]
    public void A_text_with_only_digits_has_no_stable_part_and_cannot_match_another_number()
    {
        Assert.Equal("", StatusBarMatch.StablePart("1:1"));
        Assert.Equal("", StatusBarMatch.StablePart("100%"));
        Assert.Null(StatusBarMatch.Find(Add("1:1"), [L("1:2", index: 3), L("50", index: 4)]).Item);
    }

    [Fact]
    public void A_command_after_a_dialog_the_click_opened_is_the_dialogs_not_the_clicks()
    {
        Assert.Equal("statusbar", StatusBarMatch.Claim(false, 5000, 1000, 5, 5, modalSinceClick: false));
        Assert.Equal("dialog", StatusBarMatch.Claim(false, 5000, 1000, 5, 5, modalSinceClick: true));
        Assert.Null(StatusBarMatch.Claim(true, 5000, 1000, 5, 5, modalSinceClick: false));
        Assert.Null(StatusBarMatch.Claim(false, 40000, 1000, 5, 5, modalSinceClick: false));   // later than 30 s
        Assert.Null(StatusBarMatch.Claim(false, 5000, 1000, 9, 5, modalSinceClick: true));     // another input came in between
    }

    [Fact]
    public void A_command_the_dialog_started_is_no_step_and_the_dialog_step_says_it_starts_it()
    {
        const string Events = """{"t":1100,"n":3,"type":"modal_enter","title":"T","windowClass":"X"}""";
        var steps = Plan_(Click, Events, """{"t":3000,"n":6,"type":"dialog_values","for":3,"title":"T","closedWith":"ok","fields":[]}""",
            """{"t":3100,"n":7,"type":"cmd_start","command":"CIRCLE","origin":"dialog"}""", """{"t":3200,"n":8,"type":"prompt","kind":"point","status":"OK","value":[10,0,0],"message":"Specify center point","input":false}""", """{"t":3300,"n":9,"type":"cmd_end"}""");
        Assert.Equal(["statusbar", "dialog"], steps.Select(x => x.Kind).ToArray());
        Assert.Equal("CIRCLE", steps[1].StartsCommand);
        var s = Session(Click, Events, """{"t":3000,"n":6,"type":"dialog_values","for":3,"title":"T","closedWith":"ok","fields":[]}""",
            """{"t":3100,"n":7,"type":"cmd_start","command":"CIRCLE","origin":"dialog"}""", """{"t":3300,"n":9,"type":"cmd_end"}""");
        Assert.Equal(["statusbar", "dialog"], s.Steps.Select(x => x.Kind).ToArray());
        Assert.Equal("CIRCLE", s.Steps[1].StartsCommand);
    }

    [Fact]
    public void A_profile_path_in_the_text_is_replaced_and_everything_else_is_written_as_shown()
    {
        const string Profile = "D:\\Profiles\\jane";
        Assert.Equal("Project %USERPROFILE%\\Work\\a.dwg", StatusBarMatch.Scrub("Project D:\\Profiles\\jane\\Work\\a.dwg", Profile));
        Assert.Equal("%USERPROFILE%/x", StatusBarMatch.Scrub("d:/profiles/jane/x", Profile));
        Assert.Equal("Sample TB: edit", StatusBarMatch.Scrub("Sample TB: edit", Profile));
        Assert.Equal("jane@example.org", StatusBarMatch.Scrub("jane@example.org", Profile));   // written as shown (documented)
        Assert.Equal("x", StatusBarMatch.Scrub("x", ""));
    }

    // ---- the picture

    [Fact]
    public void The_smart_crop_is_the_bar_strip_with_context_above_and_holds_the_item()
    {
        var window = new PxRect(0, 0, 1280, 920);
        var bar = new PxRect(0, 840, 1280, 872);
        var item = new PxRect(974, 845, 1057, 869);
        var c = StatusBarPicture.Crop(window, bar, item)!.Value;
        Assert.Equal(0, c.Left); Assert.Equal(1280, c.Right);
        Assert.Equal(840 - StatusBarPicture.ContextAbove, c.Top);
        Assert.True(c.Left <= item.Left && c.Top <= item.Top && c.Right >= item.Right && c.Bottom >= item.Bottom);
        Assert.Null(StatusBarPicture.Crop(window, null, item));
        Assert.Null(StatusBarPicture.Crop(window, bar, new PxRect(5, 5, 5, 5)));
    }

    [Fact]
    public void A_narrow_window_crop_stays_inside_the_window()
    {
        var window = new PxRect(100, 100, 700, 700);
        var c = StatusBarPicture.Crop(window, new PxRect(100, 660, 700, 692), new PxRect(500, 665, 560, 689))!.Value;
        Assert.True(c.Left >= 100 && c.Right <= 700 && c.Top >= 100 && c.Bottom <= 700);
    }

    // ---- the application frame is no pane

    [Fact]
    public void The_application_frame_and_the_status_bar_chain_is_not_a_pane()
    {
        Assert.True(PaneClicks.IsApplicationFrame(["HwndWrapper[DefaultDomain;;g]", "AdImpApplicationFrame", "AfxMDIFrame140u"]));   // the Quick Access Toolbar in AutoCAD 2024
        Assert.True(PaneClicks.IsApplicationFrame(["HwndWrapper[DefaultDomain;;g]", "msctls_statusbar32", "AfxMDIFrame140u"]));       // the status bar in AutoCAD 2026
        Assert.False(PaneClicks.IsApplicationFrame(["HwndWrapper[DefaultDomain;;g]", "AdImpFloatingWindow", "AfxMDIFrame140u"]));   // an unknown parent
        Assert.False(PaneClicks.IsApplicationFrame(["HwndWrapper[DefaultDomain;;g]"]));
        Assert.False(PaneClicks.IsApplicationFrame(["WindowsForms10.Window.8.app.0.1", "AfxMDIFrame140u"]));
    }

    // ---- the plan

    static List<Step> Plan_(params string[] events) => Plan.Build(events.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    const string Click = """{"t":1000,"n":2,"type":"statusbar_click","text":"Sample TB: edit","tooltip":"Edit the sample settings","index":61,"kind":"pane","own":false,"button":"left"}""";

    [Fact]
    public void The_click_is_a_step_before_the_dialog_it_opens_and_the_dialog_knows_it_was_opened_by_it()
    {
        var steps = Plan_(Click,
            """{"t":1100,"n":3,"type":"modal_enter","title":"Autodesk AutoCAD 2026","windowClass":"AfxMDIFrame140u","main":true}""",
            """{"t":1200,"n":4,"type":"modal_title","title":"Sample TB settings","windowClass":"WindowsForms10.Window.8.app.0.1"}""",
            """{"t":3000,"n":6,"type":"dialog_values","for":3,"title":"Sample TB settings","closedWith":"cancel","fields":[]}""");
        Assert.Equal(["statusbar", "dialog"], steps.Select(s => s.Kind).ToArray());
        Assert.Equal("Click Sample TB: edit on the status bar", steps[0].Title);
        Assert.True(steps[0].HadDialog);
        Assert.Equal("Sample TB: edit", steps[0].StatusBar!.Text);
        Assert.Equal("Sample TB settings", steps[1].DialogTitle);
    }

    [Fact]
    public void A_command_the_click_starts_belongs_to_its_step_with_its_answers()
    {
        var steps = Plan_(Click,
            """{"t":1100,"n":3,"type":"cmd_start","command":"CIRCLE","origin":"statusbar","via":"Sample run"}""",
            """{"t":1200,"n":4,"type":"prompt","kind":"point","status":"OK","value":[0,0,0],"message":"Specify center point","input":true}""",
            """{"t":1500,"n":5,"type":"cmd_end"}""");
        var step = Assert.Single(steps);
        Assert.Equal("statusbar", step.Kind);
        Assert.Equal("CIRCLE", step.Command);
        Assert.Single(step.Answers);
        Assert.Equal(1500, step.EndT);
    }

    [Fact]
    public void A_command_typed_after_the_click_is_a_step_of_its_own()
    {
        var steps = Plan_(Click, """{"t":9000,"n":3,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""", """{"t":9500,"n":4,"type":"cmd_end"}""");
        Assert.Equal(["statusbar", "command"], steps.Select(s => s.Kind).ToArray());
        Assert.Null(steps[0].Command);
    }

    [Fact]
    public void A_click_with_nothing_to_find_the_item_by_makes_no_step()
    {
        Assert.Empty(Plan_("""{"t":1000,"n":2,"type":"statusbar_click","button":"left"}"""));
    }

    // ---- steps.md and the readers

    static RecordingSession Session(params string[] events) => SessionAt(null, events);

    static RecordingSession SessionAt(string? file, params string[] events)
    {
        var s = new RecordingSession { Name = "t", File = file ?? "" };
        foreach (var l in events)
        {
            var root = JsonDocument.Parse(l).RootElement.Clone();
            s.Events.Add(new RecordingEvent { T = root.GetProperty("t").GetInt64(), N = root.TryGetProperty("n", out var n) ? n.GetInt64() : null, Type = root.GetProperty("type").GetString()!, Raw = root });
        }
        RecordingReader.BuildSteps(s);
        return s;
    }

    [Fact]
    public void Steps_md_shows_the_click_with_its_picture_then_the_dialog()
    {
        var dir = Directory.CreateTempSubdirectory("retrace-test-sb").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
            File.WriteAllBytes(Path.Combine(dir, "screenshots", "002-statusbar.png"), [1]);
            var s = SessionAt(Path.Combine(dir, "recording.jsonl"), Click,
                """{"t":1100,"n":3,"type":"shot","for":2,"file":"screenshots/002-statusbar.png","what":"window"}""",
                """{"t":1200,"n":4,"type":"modal_enter","title":"Sample TB settings","windowClass":"WindowsForms10.Window.8.app.0.1"}""",
                """{"t":3000,"n":6,"type":"dialog_values","for":4,"title":"Sample TB settings","closedWith":"cancel","fields":[]}""");
            Assert.Equal(["statusbar", "dialog"], s.Steps.Select(x => x.Kind).ToArray());
            var md = ScriptWriter.Build(s, null, dir, new List<string>());
            var one = md.IndexOf("Click **Sample TB: edit** on the status bar", StringComparison.Ordinal);
            Assert.True(one >= 0, md);
            Assert.Contains("002-statusbar", md[one..]);
            Assert.True(md.IndexOf("Sample TB settings", one, StringComparison.Ordinal) > one);
            Assert.Contains("Status bar", md);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_status_bar_command_step_lists_its_command_and_answers()
    {
        var s = Session(Click, """{"t":1100,"n":3,"type":"cmd_start","command":"CIRCLE","origin":"statusbar"}""",
            """{"t":1200,"n":4,"type":"prompt","kind":"point","status":"OK","value":[0,0,0],"message":"Specify center point","input":true}""", """{"t":1300,"n":5,"type":"cmd_end"}""");
        var step = Assert.Single(s.Steps);
        Assert.Equal("CIRCLE", step.Command);
        Assert.Single(step.Answers);
    }

    [Fact]
    public void An_event_type_the_plan_does_not_know_is_skipped_like_the_click_is_by_a_reader_without_this_feature()
    {
        // a reader without the feature sees the click as any other unknown event: skipped; what is left is the dialog step (the plan skips unknown types, shown here with an invented one)
        var steps = Plan_("""{"t":1000,"n":2,"type":"weird_future_event","text":"x"}""", """{"t":1200,"n":4,"type":"modal_enter","title":"T","windowClass":"X"}""", """{"t":3000,"n":6,"type":"dialog_values","for":4,"title":"T","closedWith":"cancel","fields":[]}""");
        Assert.Equal("dialog", Assert.Single(steps).Kind);
    }

    const string Line = """{"t":1000,"n":2,"type":"cmd_start","command":"LINE","origin":"ribbon"}""";
    static string Pick(int t, int n, double x) => $$"""{"t":{{t}},"n":{{n}},"type":"prompt","kind":"point","status":"OK","value":[{{x}},0,0],"message":"Specify point","input":true}""";

    [Fact]
    public void A_status_bar_click_in_the_middle_of_a_command_keeps_every_point_on_that_command()
    {
        var steps = Plan_(Line, Pick(1100, 3, 1), Click.Replace("\"t\":1000", "\"t\":1200").Replace("\"n\":2", "\"n\":4"), Pick(1300, 5, 2), Pick(1400, 6, 3),
            """{"t":1500,"n":7,"type":"prompt","kind":"point","status":"None","message":"Specify point","input":true}""", """{"t":1600,"n":8,"type":"cmd_end"}""");
        var step = Assert.Single(steps);
        Assert.Equal("LINE", step.Command);
        Assert.Equal(4, step.Answers.Count);
        Assert.Single(step.InlineStatusClicks);
        Assert.Equal(1600, step.EndT);
        Assert.Contains("status bar", Plan.Problem(step));   // not replayable in place: the step is a person's
    }

    [Fact]
    public void The_session_keeps_the_points_on_the_command_and_words_the_click_inline()
    {
        var s = Session(Line, Pick(1100, 3, 1), Click.Replace("\"t\":1000", "\"t\":1200").Replace("\"n\":2", "\"n\":4"), Pick(1300, 5, 2), Pick(1400, 6, 3), """{"t":1600,"n":8,"type":"cmd_end"}""");
        var step = Assert.Single(s.Steps);
        Assert.Equal("command", step.Kind);
        Assert.Equal(3, step.Answers.Count);
        Assert.Single(step.InlineStatusClicks);
        Assert.Contains("**Sample TB: edit**", step.InlineStatusClicks[0].Text);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public void The_live_count_takes_a_click_only_outside_a_running_command(int commandDepth, int lispDepth, bool counts) =>
        Assert.Equal(counts, StatusBoard.CountsAsStep("statusbar_click", null, commandDepth, lispDepth));
}
