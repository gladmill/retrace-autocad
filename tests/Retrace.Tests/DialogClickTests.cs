using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Buttons clicked inside a dialog (`dialog_click`), nested dialogs (`parent`) and static texts (`statics`): the recorder's pure parts, the replay plan,
/// the segments the replay does, the pause message and the script. Synthetic data only.</summary>
public class DialogClickTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-dclick").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":1,"name":"t","started":"2026-11-06T10:00:00"}""";

    static List<Step> Build(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    string Script(params string[] lines)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "s"));
        var p = Path.Combine(_dir, "s", "recording.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(lines));
        return ScriptWriter.Build(RecordingReader.Load(p), null, _dir, new List<string>());
    }

    // ---- statics -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Statics_keep_window_order_drop_empties_and_cut_each_text_at_200()
    {
        var s = DialogStatics.Build(new (int, string?)[] { (1003, "3 components found"), (1004, "  "), (1005, null), (1006, new string('x', 500)), (1007, "&Name:") });
        Assert.Equal(new[] { 1003, 1006, 1007 }, s.Select(x => x.Id));
        Assert.Equal("3 components found", s[0].Text);
        Assert.Equal(200, s[1].Text.Length);
        Assert.Equal("Name:", s[2].Text);   // the mnemonic is not shown
    }

    [Fact]
    public void Statics_stop_at_100_and_only_read_what_they_need()
    {
        var read = 0;
        IEnumerable<(int, string?)> Many() { for (int i = 1; i <= 1000; i++) { read++; yield return (i, "t" + i); } }
        var s = DialogStatics.Build(Many());
        Assert.Equal(100, s.Count);
        Assert.Equal(100, s[^1].Id);
        Assert.True(read <= 101, "the 1000 texts were not all read: " + read);
    }

    [Fact]
    public void Statics_mnemonics_and_double_ampersands()
    {
        Assert.Equal("A & B", DialogStatics.StripMnemonics("A && B"));
        Assert.Equal("Save", DialogStatics.StripMnemonics("&Save"));
        Assert.Equal("", DialogStatics.StripMnemonics(null));
    }

    [Fact]
    public void Statics_json_round_trips_and_an_empty_list_is_left_out()
    {
        var statics = new List<DialogStatic> { new(1003, "2 items") };
        var json = JsonSerializer.Serialize(DialogStatics.ToJson(statics));
        Assert.Equal("""[{"id":1003,"text":"2 items"}]""", json);
        Assert.Equal(statics, DialogStatics.FromJson(JsonDocument.Parse(json).RootElement));
        Assert.Null(DialogStatics.ToJson(new List<DialogStatic>()));
        Assert.Empty(DialogStatics.FromJson(JsonDocument.Parse("""{"id":1}""").RootElement));   // not an array
        Assert.Single(DialogStatics.FromJson(JsonDocument.Parse("""[{"id":"x","text":"a"},{"id":2},5]""").RootElement));   // the ones with a text
    }

    // ---- tracker and buttons -------------------------------------------------------------------------------------------

    [Fact]
    public void Rebase_moves_the_baseline_to_the_latest_values()
    {
        var t = new FieldTracker();
        t.Observe(new[] { new DialogField("id:1", "Name", "edit", ""), new DialogField("id:2", "Snap", "check", "off") });
        t.Observe(new[] { new DialogField("id:1", "Name", "edit", "A") });
        Assert.Single(t.Changes());
        t.Rebase();
        Assert.Empty(t.Changes());
        Assert.Equal(2, t.ReadCount);
        t.Observe(new[] { new DialogField("id:1", "Name", "edit", "B"), new DialogField("id:2", "Snap", "check", "on") });
        var c = t.Changes();
        Assert.Equal(new[] { ("id:1", "A", "B"), ("id:2", "off", "on") }, c.Select(x => (x.Key, x.Before, x.After)));
        // a field first seen after the rebase still takes its first-seen value as baseline
        t.Rebase();
        t.Observe(new[] { new DialogField("id:9", "Later", "edit", "x") });
        t.Observe(new[] { new DialogField("id:9", "Later", "edit", "y") });
        Assert.Equal(new[] { ("id:9", "x", "y") }, t.Changes().Select(x => (x.Key, x.Before, x.After)));
    }

    [Fact]
    public void The_Help_button_is_not_a_click_to_record()
    {
        Assert.True(DialogButtons.IsHelp("&Help"));
        Assert.True(DialogButtons.IsHelp("Help"));
        Assert.False(DialogButtons.IsHelp("Help me"));
        Assert.False(DialogButtons.IsHelp("Add"));
        Assert.False(DialogButtons.IsHelp(null));
    }

    // ---- the replay plan -----------------------------------------------------------------------------------------------

    const string Open = """{"t":100,"n":1,"type":"cmd_start","command":"STYLE","origin":"typed/other"}""";
    const string Enter = """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""";
    const string Title = """{"t":200,"n":3,"type":"modal_title","title":"Text Style"}""";

    /// <summary>STYLE: type a name, New... (a nested dialog: Style Name, OK), Set Current, then Cancel (Close).</summary>
    static readonly string[] Style =
    {
        Open, Enter, Title,
        """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"New...","id":1002,"fields":[],"read":5,"statics":[{"id":1010,"text":"Current text style: Standard"}]}""",
        """{"t":1100,"n":5,"type":"modal_enter","title":"AutoCAD"}""",
        """{"t":1200,"n":6,"type":"modal_title","title":"New Text Style"}""",
        """{"t":2000,"n":7,"type":"dialog_values","for":6,"title":"New Text Style","source":"win32","parent":3,"fields":[{"key":"id:1001","label":"Style Name","kind":"edit","before":"style1","after":"Notes"}],"read":1,"closedWith":"ok"}""",
        """{"t":2010,"n":8,"type":"modal_leave"}""",
        """{"t":3000,"n":9,"type":"dialog_click","for":3,"button":"Set Current","id":1003,"fields":[{"key":"id:1004","label":"Styles","kind":"list","before":"Standard","after":"Notes"}],"read":5}""",
        """{"t":4000,"n":10,"type":"dialog_values","for":3,"title":"Text Style","source":"win32","fields":[],"read":5,"closedWith":"cancel","statics":[{"id":1010,"text":"Current text style: Notes"}]}""",
        """{"t":4010,"n":11,"type":"modal_leave"}""",
        """{"t":4020,"n":12,"type":"cmd_end","command":"STYLE"}""",
    };

    [Fact]
    public void Clicks_are_collected_per_dialog_and_a_nested_dialog_belongs_to_the_click_that_opened_it()
    {
        var steps = Build(Style);
        var dlg = steps.Single(s => s.Kind == "dialog");   // the nested dialog is not a step of its own
        Assert.Equal("Text Style", dlg.DialogTitle);
        Assert.Equal(new[] { "New...", "Set Current" }, dlg.Clicks.Select(c => c.Button));
        Assert.Equal(new[] { 1002, 1003 }, dlg.Clicks.Select(c => c.Id));
        Assert.Equal(new[] { 1, 1 }, dlg.Clicks.Select(c => c.Index));
        Assert.Equal("Notes", dlg.Clicks[1].Values.Single().After);
        Assert.Empty(dlg.Values!);   // the close holds what changed after the last click: nothing
        Assert.Equal("cancel", dlg.ClosedWith);
        var nested = dlg.Clicks[0].Nested.Single();
        Assert.Equal("New Text Style", nested.DialogTitle);
        Assert.Equal(("Style Name", "Notes"), (nested.Values![0].Label, nested.Values[0].After));
        Assert.Equal("ok", nested.ClosedWith);
        Assert.Empty(dlg.Clicks[1].Nested);
    }

    [Fact]
    public void A_nested_dialog_with_its_own_click_and_a_third_level()
    {
        var steps = Build(
            Open, Enter, Title,
            """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Options...","fields":[],"read":2}""",
            """{"t":1100,"n":5,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":1200,"n":6,"type":"modal_title","title":"Options"}""",
            """{"t":1500,"n":7,"type":"dialog_click","for":6,"button":"More...","fields":[{"key":"id:1","label":"Scale","kind":"edit","before":"1","after":"2"}],"read":1}""",
            """{"t":1600,"n":8,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":1700,"n":9,"type":"modal_title","title":"More"}""",
            """{"t":1900,"n":10,"type":"dialog_values","for":9,"title":"More","source":"win32","parent":6,"fields":[],"read":0,"closedWith":"ok"}""",
            """{"t":1910,"n":11,"type":"modal_leave"}""",
            """{"t":2000,"n":12,"type":"dialog_values","for":6,"title":"Options","source":"win32","parent":3,"fields":[],"read":1,"closedWith":"ok"}""",
            """{"t":2010,"n":13,"type":"modal_leave"}""",
            """{"t":3000,"n":14,"type":"dialog_values","for":3,"title":"Text Style","source":"win32","fields":[],"read":2,"closedWith":"ok"}""");
        var dlg = steps.Single(s => s.Kind == "dialog");
        var options = dlg.Clicks[0].Nested.Single();
        Assert.Equal("Options", options.DialogTitle);
        Assert.Equal("More...", options.Clicks.Single().Button);
        Assert.Equal("More", options.Clicks[0].Nested.Single().DialogTitle);
    }

    [Fact]
    public void A_nested_dialog_without_a_click_before_it_stays_a_step_of_its_own_as_before()
    {
        // opened by something the recorder did not see as a button click (a key, a menu): nothing to attach it to
        var steps = Build(
            Open, Enter, Title,
            """{"t":1100,"n":4,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":1200,"n":5,"type":"modal_title","title":"New Text Style"}""",
            """{"t":2000,"n":6,"type":"dialog_values","for":5,"title":"New Text Style","source":"win32","parent":3,"fields":[],"read":1,"closedWith":"ok"}""",
            """{"t":3000,"n":7,"type":"dialog_values","for":3,"title":"Text Style","source":"win32","fields":[],"read":5,"closedWith":"ok"}""");
        Assert.Equal(new[] { "Text Style", "New Text Style" }, steps.Where(s => s.Kind == "dialog").Select(s => s.DialogTitle));
    }

    [Fact]
    public void A_recording_without_dialog_click_plans_exactly_as_before()
    {
        var steps = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            """{"t":900,"n":4,"type":"dialog_values","for":3,"title":"Draw grid","source":"win32","fields":[{"key":"id:1","label":"Rows","kind":"edit","before":"3","after":"5"}],"read":4,"closedWith":"ok"}""");
        var dlg = steps.Single(s => s.Kind == "dialog");
        Assert.Empty(dlg.Clicks);
        Assert.Equal("5", dlg.Values!.Single().After);
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(dlg));
        var segs = DialogSegments.Of(dlg, closingValues: true);
        Assert.Single(segs);
        Assert.Null(segs[0].Button);
        Assert.False(DialogSegments.HasClicks(segs));
    }

    // ---- segments --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_dialog_is_one_segment_per_click_then_the_close()
    {
        var dlg = Build(Style).Single(s => s.Kind == "dialog");
        // closed with Cancel: the values changed after the last click went away with the dialog, but the clicks were real
        Assert.Equal(AutoMode.Cancel, DialogSegments.ModeOf(dlg));
        var segs = DialogSegments.Of(dlg, closingValues: false);
        Assert.Equal(new string?[] { "New...", "Set Current", null }, segs.Select(s => s.Button));
        Assert.Equal("New Text Style", segs[0].Nested.Single().DialogTitle);
        Assert.Equal("Notes", segs[1].Values.Single().After);
        Assert.Empty(segs[2].Values);
        Assert.True(DialogSegments.HasClicks(segs));
    }

    [Fact]
    public void A_click_is_work_even_when_nothing_else_changed()
    {
        var dlg = new Step { Kind = "dialog", Values = new List<FieldChange>(), ValuesRead = 3, ClosedWith = "ok" };
        Assert.Equal(0, DialogSegments.Work(dlg));
        dlg.Clicks.Add(new DialogClick { Button = "Add" });
        Assert.Equal(1, DialogSegments.Work(dlg));
        // closed some other way (closedWith not known), a click was done and nothing set after it: a person finishes it, as a dialog with nothing to set is left to one (never an OK nobody pressed)
        var other = new Step { Kind = "dialog", Values = new List<FieldChange>(), ValuesRead = 0, ClosedWith = null };
        Assert.Equal(AutoMode.None, DialogSegments.ModeOf(other));
        other.Clicks.Add(new DialogClick { Button = "Yes" });
        Assert.Equal(AutoMode.None, DialogSegments.ModeOf(other));
        // values set after the last click are a change: a closedWith left out counts as OK, as on main
        other.Values!.Add(Ch("Name", "B"));
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(other));
    }

    // ---- the pause message -----------------------------------------------------------------------------------------------

    static FieldChange Ch(string label, string after) => new("", label, "edit", "", after);

    static List<DialogSegment> Three() => new()
    {
        new DialogSegment(new[] { Ch("Name", "A") }, "Add", 1, new List<Step>()),
        new DialogSegment(new[] { Ch("Name", "B") }, "Add", 1, new List<Step>()),
        new DialogSegment(new[] { Ch("Scale", "2") }, null, 1, new List<Step>()),
    };

    [Fact]
    public void Without_clicks_the_message_is_todays()
    {
        var segs = new List<DialogSegment> { new(new[] { Ch("Spacing", "20"), Ch("Rows", "5") }, null, 1, new List<Step>()) };
        var progress = new List<SegmentProgress> { new(new[] { FieldFillResult.Ok("Spacing"), FieldFillResult.Missing("Rows") }, false) };
        var today = FillMatcher.FallbackMessage("Grid", "a.md", 4, segs[0].Values, progress[0].Results, "ok", null);
        Assert.Equal(today, DialogSegments.FallbackMessage("Grid", "a.md", 4, segs, progress, "ok", null));
        Assert.Equal("A dialog opens: Grid. Filled 1 of 2 values; set Rows to 5 yourself, then close it with OK (see a.md, step 4).", today);
    }

    [Fact]
    public void The_message_names_the_buttons_left_to_press()
    {
        // the first click is done; the second segment's value did not go in
        var progress = new List<SegmentProgress>
        {
            new(new[] { FieldFillResult.Ok("Name") }, true),
            new(new[] { FieldFillResult.Fail("Name", "x") }, false),
        };
        Assert.Equal("A dialog opens: Items. Filled 1 of 3 values; set Name to B, press Add, set Scale to 2 yourself, then close it with OK (see a.md, step 2).",
            DialogSegments.FallbackMessage("Items", "a.md", 2, Three(), progress, "ok", null));
        // everything went in but the button could not be pressed
        var allIn = new List<SegmentProgress> { new(new[] { FieldFillResult.Ok("Name") }, true), new(new[] { FieldFillResult.Ok("Name") }, false) };
        Assert.Equal("A dialog opens: Items. Filled 2 of 3 values; press Add, set Scale to 2 yourself, then close it with OK.",
            DialogSegments.FallbackMessage("Items", null, null, Three(), allIn, "ok", null));
    }

    [Fact]
    public void The_message_puts_the_note_first_and_a_press_alone_reads_as_a_sentence()
    {
        var segs = new List<DialogSegment> { new(new List<FieldChange>(), "Options...", 1, new List<Step>()), new(new List<FieldChange>(), null, 1, new List<Step>()) };
        var progress = new List<SegmentProgress> { new(new List<FieldFillResult>(), true) };   // pressed, then the nested dialog was not in the recording
        Assert.Equal("A dialog opens: Items. pressing Options... opened the dialog \"Color\", which the recording does not have. Close it with Cancel.",
            DialogSegments.FallbackMessage("Items", null, null, segs, progress, "cancel", "pressing Options... opened the dialog \"Color\", which the recording does not have."));
        var notPressed = new List<SegmentProgress> { new(new List<FieldFillResult>(), false) };
        Assert.Equal("A dialog opens: Items. Press Options... yourself, then close it with OK (see a.md, step 1).",
            DialogSegments.FallbackMessage("Items", "a.md", 1, segs, notPressed, "ok", null));
    }

    [Fact]
    public void Without_auto_fill_the_message_lists_everything_to_do_in_order()
    {
        var dlg = Build(Style).Single(s => s.Kind == "dialog");
        Assert.Equal("A dialog opens: Text Style. Press New... (nested dialog \"New Text Style\": set Style Name to Notes, then close it with OK), set Styles to Notes, press Set Current, then close it with Cancel (see a.md, step 3).",
            DialogSegments.ManualMessage("Text Style", "a.md", 3, dlg));
        var plain = new Step { Kind = "dialog", Values = new List<FieldChange>(), ClosedWith = "ok" };
        plain.Clicks.Add(new DialogClick { Button = "Add" });
        plain.Values.Add(Ch("Scale", "2"));
        Assert.Equal("A dialog opens: Items. Press Add, set Scale to 2, then close it with OK.", DialogSegments.ManualMessage("Items", null, null, plain));
    }

    /// <summary>The Text Style dialog as it was recorded on the VM: New... (nested), Apply after a Height change, then Close, a button whose id is not IDCANCEL: no OK, no Cancel; the recorder saw the
    /// dialog go away after that click with no other input, so it wrote `closedWith: "click"` and the button.</summary>
    static readonly string[] StyleClose =
    {
        Open, Enter, Title,
        """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"New...","id":1257,"index":1,"fields":[],"read":12}""",
        """{"t":1100,"n":5,"type":"modal_enter","title":"New Text Style"}""",
        """{"t":2000,"n":6,"type":"dialog_values","for":5,"title":"New Text Style","source":"win32","parent":3,"fields":[{"key":"id:1362","label":"Style Name","kind":"edit","before":"style1","after":"Notes"}],"read":1,"closedWith":"ok"}""",
        """{"t":3000,"n":7,"type":"dialog_click","for":3,"button":"Apply","id":915,"index":1,"fields":[{"key":"id:1265","label":"Height","kind":"edit","before":"0.0000","after":"2.5"}],"read":12}""",
        """{"t":4000,"n":8,"type":"dialog_click","for":3,"button":"Close","id":917,"index":1,"fields":[],"read":12}""",
        """{"t":4050,"n":9,"type":"dialog_values","for":3,"title":"Text Style","source":"win32","fields":[],"read":12,"closedWith":"click","button":"Close"}""",
        """{"t":4060,"n":10,"type":"modal_leave"}""",
        """{"t":4070,"n":11,"type":"cmd_end","command":"STYLE"}""",
    };

    [Fact]
    public void A_last_click_that_closed_the_dialog_needs_no_OK_in_the_words()
    {
        var dlg = Build(StyleClose).Single(s => s.Kind == "dialog");
        Assert.Equal("click", dlg.ClosedWith);
        Assert.True(DialogSegments.LastClickCloses(dlg));
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(dlg));   // the clicks are work: the replay does them and presses nothing after Close
        Assert.Equal("A dialog opens: Text Style. Press New... (nested dialog \"New Text Style\": set Style Name to Notes, then close it with OK), set Height to 2.5, press Apply, press Close (see a.md, step 2).",
            DialogSegments.ManualMessage("Text Style", "a.md", 2, dlg));
        Assert.Equal(new[] { "In the Text Style dialog:", "Click New....", "In the New Text Style dialog:", "Set Style Name to Notes.", "Click OK.", "Set Height to 2.5.", "Click Apply.", "Click Close." },
            TdocPlan.Instructions(dlg));
        var md = Script(StyleClose);
        Assert.Contains("   - A dialog opens: **Text Style**. Make these changes (the last click closes the dialog):\n", md);
        Assert.Contains("     - Set **Height** to `2.5` (was `0.0000`), click **Apply**\n", md);
        Assert.Contains("     - Click **Close**\n", md);
        // a dialog whose OK was pressed after clicks is not one, and neither is one the recorder did not tell was closed by its last click (no guessing)
        Assert.False(DialogSegments.LastClickCloses(Build(Style).Single(s => s.Kind == "dialog")));
        var unsaid = Build(StyleClose.Select(l => l.Replace(",\"closedWith\":\"click\",\"button\":\"Close\"", "")).ToArray()).Single(s => s.Kind == "dialog");
        Assert.False(DialogSegments.LastClickCloses(unsaid));
        Assert.Contains("then close it with OK", DialogSegments.ManualMessage("Text Style", null, null, unsaid));
        // the pause message, when the replay got through New... and failed at Apply: no OK is named after the last button
        var segs = DialogSegments.Of(dlg, closingValues: true);
        var progress = new List<SegmentProgress> { new(new List<FieldFillResult>(), true), new(new[] { FieldFillResult.Fail("Height", "x") }, false) };
        Assert.Equal("A dialog opens: Text Style. Filled 0 of 1 values; set Height to 2.5, press Apply, press Close yourself (see a.md, step 2).",
            DialogSegments.FallbackMessage("Text Style", "a.md", 2, segs, progress, "click", null));
    }

    // ---- the result file -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_result_of_a_dialog_step_holds_segments_with_the_static_texts_before_each_button()
    {
        var result = new ReplayResult();
        result.Steps.Add(new StepResult
        {
            Index = 1, Kind = "dialog", Title = "Items", AutoFilled = true,
            Segments = new List<SegmentResult>
            {
                new() { Button = "Add", Status = SegmentResult.Ok, Fields = new List<FieldFillResult> { FieldFillResult.Ok("Name") }, Statics = new List<DialogStatic> { new(1003, "1 item") } },
                new()
                {
                    Button = "Options...", Status = SegmentResult.Ok, Statics = new List<DialogStatic> { new(1003, "2 items") },
                    Nested = new List<NestedDialogResult> { new() { Title = "Options", Segments = new List<SegmentResult> { new() { Status = SegmentResult.Ok } } } },
                },
                new() { Status = SegmentResult.Ok, Statics = new List<DialogStatic> { new(1003, "2 items") } },
            },
        });
        using var doc = JsonDocument.Parse(result.ToJson());
        var segs = doc.RootElement.GetProperty("steps")[0].GetProperty("segments");
        Assert.Equal(3, segs.GetArrayLength());
        Assert.Equal((1003, "1 item"), (segs[0].GetProperty("statics")[0].GetProperty("id").GetInt32(), segs[0].GetProperty("statics")[0].GetProperty("text").GetString()));
        Assert.Equal("Options", segs[1].GetProperty("nested")[0].GetProperty("title").GetString());
        Assert.False(segs[2].TryGetProperty("button", out _));   // the closing segment has no button
        Assert.False(segs[2].TryGetProperty("nested", out _));
        // a step with no segments (a dialog filled through UI Automation) has no such field
        using var plain = JsonDocument.Parse(JsonSerializer.Serialize(new StepResult { Index = 2, Kind = "dialog" }));
        Assert.False(plain.RootElement.TryGetProperty("segments", out _));
    }

    // ---- the test script (steps.md) ------------------------------------------------------------------------------------------

    [Fact]
    public void The_script_lists_the_changes_and_clicks_in_order_with_the_nested_dialog_under_its_click()
    {
        var md = Script(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWITEMS","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Items"}""",
            """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Add","fields":[{"key":"name:nameBox","label":"Name","kind":"edit","before":"","after":"A"}],"read":3,"statics":[{"id":1,"text":"0 items"}]}""",
            """{"t":1500,"n":5,"type":"dialog_click","for":3,"button":"Add","fields":[{"key":"name:nameBox","label":"Name","kind":"edit","before":"A","after":"B"}],"read":3}""",
            """{"t":2000,"n":6,"type":"dialog_click","for":3,"button":"Options...","fields":[],"read":3}""",
            """{"t":2100,"n":7,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":2200,"n":8,"type":"modal_title","title":"Options"}""",
            """{"t":3000,"n":9,"type":"dialog_values","for":8,"title":"Options","source":"win32","parent":3,"fields":[{"key":"name:scale","label":"Scale","kind":"edit","before":"1","after":"2"}],"read":2,"closedWith":"ok"}""",
            """{"t":3010,"n":10,"type":"modal_leave"}""",
            """{"t":4000,"n":11,"type":"dialog_values","for":3,"title":"Items","source":"win32","fields":[{"key":"id:5","label":"Snap","kind":"check","before":"off","after":"on"}],"read":4,"closedWith":"ok","statics":[{"id":1,"text":"2 items"}]}""",
            """{"t":4010,"n":12,"type":"modal_leave"}""",
            """{"t":4020,"n":13,"type":"cmd_end","command":"DRAWITEMS"}""");
        Assert.Contains("   - A dialog opens: **Items**. Make these changes, then confirm:\n", md);
        int At(string x) { var i = md.IndexOf(x, StringComparison.Ordinal); Assert.True(i >= 0, x + "\n" + md); return i; }
        Assert.True(At("     - Set **Name** to `A`, click **Add**\n") < At("     - Set **Name** to `B` (was `A`), click **Add**\n"));
        Assert.True(At("(was `A`), click **Add**") < At("     - Click **Options...**\n"));
        Assert.True(At("     - Click **Options...**\n") < At("       - A nested dialog opens: **Options**. Make these changes, then confirm:\n"));
        Assert.True(At("**Options**. Make these") < At("         - Set **Scale** to `2` (was `1`)\n"));
        Assert.True(At("Set **Scale**") < At("     - Tick **Snap**\n"));
        // static texts stay in the machine-readable files
        Assert.DoesNotContain("items", md.Replace("DRAWITEMS", "").Replace("Items", ""), StringComparison.Ordinal);
        // the nested dialog is not counted as a dialog step: the replay finds its place in the script by counting "A dialog opens:" lines
        Assert.Equal(1, ScriptRef.DialogStepNumber(md, 1));
        Assert.Null(ScriptRef.DialogStepNumber(md, 2));
    }

    [Fact]
    public void A_dialog_closed_with_Cancel_after_clicks_lists_the_clicks_and_not_the_last_values()
    {
        var md = Script(Style);
        Assert.Contains("   - A dialog opens: **Text Style**. Make these changes, then close it with Cancel:\n", md);
        Assert.Contains("     - Click **New...**\n", md);
        Assert.Contains("       - A nested dialog opens: **New Text Style**. Make these changes, then confirm:\n", md);
        Assert.Contains("         - Set **Style Name** to `Notes` (was `style1`)\n", md);
        Assert.Contains("     - Choose `Notes` in **Styles** (was `Standard`), click **Set Current**\n", md);
        Assert.DoesNotContain("Leave the values", md);
    }

    // ---- Tdoc sentences ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tdoc_says_the_clicks_in_order_and_the_nested_dialog_after_its_button()
    {
        var dlg = Build(Style).Single(s => s.Kind == "dialog");
        var lines = TdocPlan.Instructions(dlg);
        Assert.Equal(new[]
        {
            "In the Text Style dialog:",
            "Click New....",
            "In the New Text Style dialog:", "Set Style Name to Notes.", "Click OK.",
            "Set Styles to Notes.", "Click Set Current.",
            "Click Cancel.",
        }, lines);
    }
}
