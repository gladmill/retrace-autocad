using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Dialog field values: the pure reader parts (labels, radio groups, keys, diff), the engine wording and the replay message.</summary>
public class DialogValuesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-dv").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":1,"name":"t","started":"2026-10-08T10:00:00"}""";

    // ---- labels --------------------------------------------------------------------------------------------------------

    [Fact]
    public void CleanLabel_strips_mnemonics_and_trailing_colon()
    {
        Assert.Equal("Rows", DialogFieldsCore.CleanLabel("&Rows:"));
        Assert.Equal("Save & close", DialogFieldsCore.CleanLabel("Save && close "));
        Assert.Equal("", DialogFieldsCore.CleanLabel(null));
    }

    [Fact]
    public void Label_is_the_nearest_static_left_on_the_same_row()
    {
        var statics = new List<(Box, string)>
        {
            (new Box(10, 10, 60, 24), "Rows:"),
            (new Box(10, 40, 90, 54), "&Columns:"),
        };
        Assert.Equal("Rows", DialogFieldsCore.FindLabel(new Box(100, 8, 160, 26), statics));
        Assert.Equal("Columns", DialogFieldsCore.FindLabel(new Box(100, 38, 160, 56), statics));
    }

    [Fact]
    public void Repeated_labels_get_the_title_of_their_group_box()
    {
        var raw = new List<RawControl> { new(1, "Precision", "combo", "0.0000"), new(2, "Precision", "combo", "0"), new(3, "Type", "combo", "Decimal") };
        var rects = new List<Box> { new(20, 60, 120, 80), new(220, 60, 320, 80), new(20, 20, 120, 40) };
        var groups = new List<(Box, string)> { (new Box(10, 0, 200, 100), "Length"), (new Box(210, 0, 400, 100), "Angle"), (new Box(0, -10, 500, 300), "Units") };
        var q = DialogFieldsCore.Qualify(raw, rects, groups);
        Assert.Equal(new[] { "Length › Precision", "Angle › Precision", "Type" }, q.Select(r => r.Label));
    }

    [Fact]
    public void Label_falls_back_to_a_static_directly_above_else_null()
    {
        var statics = new List<(Box, string)> { (new Box(100, 10, 160, 24), "Name:") };
        Assert.Equal("Name", DialogFieldsCore.FindLabel(new Box(100, 30, 200, 50), statics));
        Assert.Null(DialogFieldsCore.FindLabel(new Box(100, 120, 200, 140), statics));          // too far below
        Assert.Null(DialogFieldsCore.FindLabel(new Box(300, 30, 400, 50), statics));            // not above it horizontally
        Assert.Null(DialogFieldsCore.FindLabel(new Box(100, 30, 200, 50), new List<(Box, string)>()));
    }

    [Fact]
    public void Label_prefers_the_row_neighbour_over_one_above()
    {
        var statics = new List<(Box, string)> { (new Box(10, 10, 60, 24), "Above:"), (new Box(10, 34, 60, 48), "Left:") };
        Assert.Equal("Left", DialogFieldsCore.FindLabel(new Box(70, 32, 150, 50), statics));
    }

    // ---- groups, keys ------------------------------------------------------------------------------------------------

    [Fact]
    public void Radios_form_a_group_whose_value_is_the_checked_text_and_a_group_start_splits()
    {
        var raw = new List<RawControl>
        {
            new(201, "Metric", "radio", "off", true, 1), new(202, "Imperial", "radio", "on", false, 1),
            new(203, "Fast", "radio", "on", true, 1), new(204, "Slow", "radio", "off", false, 1),
        };
        var f = DialogFieldsCore.Build(raw);
        Assert.Equal(2, f.Count);
        Assert.Equal(("id:201", "Metric / Imperial", "radio", "Imperial"), (f[0].Key, f[0].Label, f[0].Kind, f[0].Value));
        Assert.Equal(("id:203", "Fast / Slow", "radio", "Fast"), (f[1].Key, f[1].Label, f[1].Kind, f[1].Value));
    }

    [Fact]
    public void Another_control_or_parent_ends_a_radio_group_and_no_checked_radio_gives_empty()
    {
        var raw = new List<RawControl>
        {
            new(1, "A", "radio", "off", true, 1), new(2, "Snap", "check", "on"), new(3, "B", "radio", "off", false, 1),
            new(4, "C", "radio", "off", false, 2),
        };
        var f = DialogFieldsCore.Build(raw);
        Assert.Equal(new[] { "radio", "check", "radio", "radio" }, f.Select(x => x.Kind));
        Assert.Equal("", f[0].Value);
    }

    [Fact]
    public void Keys_use_the_control_id_when_unique_else_label_and_occurrence()
    {
        var raw = new List<RawControl>
        {
            new(1001, "Rows", "edit", "3"), new(1002, "Name", "edit", "a"),
            new(7, "Size", "edit", "1"), new(7, "Size", "edit", "2"), new(0, "Size", "edit", "3"),
        };
        var f = DialogFieldsCore.Build(raw);
        Assert.Equal(new[] { "id:1001", "id:1002", "label:Size#1", "label:Size#2", "label:Size#3" }, f.Select(x => x.Key));
    }

    [Fact]
    public void Values_are_clipped_to_200_chars()
    {
        var f = DialogFieldsCore.Build(new[] { new RawControl(1, "Note", "edit", new string('x', 500)) });
        Assert.Equal(200, f[0].Value.Length);
    }

    [Fact]
    public void Diff_reports_changed_values_only()
    {
        var a = new List<DialogField> { new("id:1", "Rows", "edit", "3"), new("id:2", "Snap", "check", "off"), new("id:3", "Cols", "edit", "6") };
        var b = new List<DialogField> { new("id:1", "Rows", "edit", "5"), new("id:2", "Snap", "check", "on"), new("id:3", "Cols", "edit", "6"), new("id:9", "New", "edit", "z") };
        var d = DialogFieldsCore.Diff(a, b);
        Assert.Equal(new[] { "id:1", "id:2" }, d.Select(x => x.Key));
        Assert.Equal(("3", "5"), (d[0].Before, d[0].After));
    }

    [Fact]
    public void Tracker_keeps_first_seen_as_baseline_and_latest_as_result()
    {
        var t = new FieldTracker();
        t.Observe(new[] { new DialogField("id:1", "Rows", "edit", "3") });
        t.Observe(new[] { new DialogField("id:1", "Rows", "edit", "4") });
        t.Observe(new[] { new DialogField("id:1", "Rows", "edit", "5"), new DialogField("id:2", "Tab2 field", "edit", "a") });   // appears later (another page)
        t.Observe(new[] { new DialogField("id:2", "Tab2 field", "edit", "b") });                                                // id:1 not visible now: keeps last
        Assert.Equal(2, t.ReadCount);
        var c = t.Changes();
        Assert.Equal(new[] { "id:1", "id:2" }, c.Select(x => x.Key));
        Assert.Equal(("3", "5"), (c[0].Before, c[0].After));
        Assert.Equal(("a", "b"), (c[1].Before, c[1].After));
    }

    [Fact]
    public void Tracker_with_no_change_has_empty_changes_but_a_read_count()
    {
        var t = new FieldTracker();
        t.Observe(new[] { new DialogField("id:1", "Rows", "edit", "3") });
        t.Observe(new[] { new DialogField("id:1", "Rows", "edit", "3") });
        Assert.Empty(t.Changes());
        Assert.Equal(1, t.ReadCount);
    }

    // ---- engine wording ----------------------------------------------------------------------------------------------

    string Script(params string[] lines)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "s"));
        var p = Path.Combine(_dir, "s", "recording.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(lines));
        return ScriptWriter.Build(RecordingReader.Load(p), null, _dir, new List<string>());
    }

    const string Values = """{"t":1900,"n":5,"type":"dialog_values","for":3,"title":"Draw grid","source":"win32","fields":[""" +
        """{"key":"id:1001","label":"Rows","kind":"edit","before":"3","after":"5"},""" +
        """{"key":"id:1002","label":"Name","kind":"edit","before":"x","after":""},""" +
        """{"key":"id:1003","label":"Snap","kind":"check","before":"off","after":"on"},""" +
        """{"key":"id:1004","label":"Ortho","kind":"check","before":"on","after":"off"},""" +
        """{"key":"id:1005","label":"Metric / Imperial","kind":"radio","before":"Metric","after":"Imperial"},""" +
        """{"key":"id:1006","label":"Style","kind":"combo","before":"A","after":"B"},""" +
        """{"key":"id:1007","label":"Quality","kind":"slider","before":"1","after":"7"}],"read":9}""";

    [Fact]
    public void A_changed_dialog_shows_a_before_and_an_after_picture_around_the_changes()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "s", "screenshots"));
        File.WriteAllBytes(Path.Combine(_dir, "s", "screenshots", "003-dialog.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_dir, "s", "screenshots", "003-dialog-after.png"), new byte[] { 1 });
        string[] Session(string values) => new[]
        {
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            """{"t":400,"n":4,"type":"shot","for":3,"file":"screenshots/003-dialog.png","what":"dialog"}""",
            """{"t":1700,"n":5,"type":"shot","for":3,"file":"screenshots/003-dialog-after.png","what":"dialog-after"}""",
            values,
            """{"t":1910,"n":7,"type":"modal_leave"}""",
            """{"t":1920,"n":8,"type":"cmd_end","command":"DRAWGRID"}""",
        };
        var md = Script(Session("""{"t":1900,"n":6,"type":"dialog_values","for":3,"title":"Draw grid","source":"win32","fields":[{"key":"id:1","label":"Rows","kind":"edit","before":"3","after":"5"}],"read":4,"closedWith":"ok"}"""));
        int I(string x) { var i = md.IndexOf(x, StringComparison.Ordinal); Assert.True(i >= 0, x + "\n" + md); return i; }
        Assert.True(I("     Before:") < I("![Draw grid as it opens](screenshots/003-dialog.png)"));
        Assert.True(I("(screenshots/003-dialog.png)") < I("     - Set **Rows** to `5` (was `3`)"));
        Assert.True(I("- Set **Rows**") < I("     After (what it should look like before you press OK):"));
        Assert.True(I("After (what") < I("![Draw grid with the changes](screenshots/003-dialog-after.png)"));

        // nothing changed: one picture, no after
        md = Script(Session("""{"t":1900,"n":6,"type":"dialog_values","for":3,"title":"Draw grid","source":"win32","fields":[],"read":4,"closedWith":"ok"}"""));
        Assert.Contains("(screenshots/003-dialog.png)", md);
        Assert.DoesNotContain("003-dialog-after", md);
        Assert.DoesNotContain("Before:", md);
    }

    [Fact]
    public void A_changed_dialog_shows_its_after_picture_even_when_the_before_picture_is_missing()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "s", "screenshots"));
        File.WriteAllBytes(Path.Combine(_dir, "s", "screenshots", "003-dialog-after.png"), new byte[] { 1 });
        string[] Session(string values) => new[]
        {
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            """{"t":1700,"n":5,"type":"shot","for":3,"file":"screenshots/003-dialog-after.png","what":"dialog-after"}""",
            values,
            """{"t":1910,"n":7,"type":"modal_leave"}""",
            """{"t":1920,"n":8,"type":"cmd_end","command":"DRAWGRID"}""",
        };
        var md = Script(Session("""{"t":1900,"n":6,"type":"dialog_values","for":3,"title":"Draw grid","source":"win32","fields":[{"key":"id:1","label":"Rows","kind":"edit","before":"3","after":"5"}],"read":4,"closedWith":"ok"}"""));
        Assert.Contains("After (what it should look like before you press OK):", md);
        Assert.Contains("![Draw grid with the changes](screenshots/003-dialog-after.png)", md);
        Assert.DoesNotContain("Before:", md);

        // no change made: the after picture stays out
        md = Script(Session("""{"t":1900,"n":6,"type":"dialog_values","for":3,"title":"Draw grid","source":"win32","fields":[],"read":4,"closedWith":"ok"}"""));
        Assert.DoesNotContain("003-dialog-after", md);
    }

    [Fact]
    public void A_dialog_closed_with_Cancel_says_so_and_lists_no_changes()
    {
        var md = Script(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            Values.Replace("\"read\":9}", "\"read\":9,\"closedWith\":\"cancel\"}"),
            """{"t":1910,"n":6,"type":"modal_leave"}""",
            """{"t":1920,"n":7,"type":"cmd_end","command":"DRAWGRID"}""");
        Assert.Contains("   - A dialog opens: **Draw grid**. Close it with Cancel.\n", md);
        Assert.DoesNotContain("Rows", md);
        Assert.Equal("A dialog opens: Draw grid. Close it with Cancel (see a.md, step 2).",
            Retrace.Core.ScriptRef.Message("Draw grid", "a.md", 2, new List<Retrace.Core.FieldChange>(), 3, "cancel"));
    }

    [Fact]
    public void A_dialog_recorded_as_cancelled_does_not_fail_when_its_command_ends()
    {
        var d = new Retrace.Core.DialogWait();
        d.Begin(DateTime.Now, false, expectCancel: true);
        d.ModalEntered();
        d.ModalLeft(0);
        d.CommandEnded(true, DateTime.Now);
        d.FailAfterClose("ended");
        Assert.Equal(Retrace.Core.DialogWaitPhase.Done, d.Phase);
    }

    [Fact]
    public void An_unknown_command_reported_just_before_the_same_command_starts_is_not_a_step()
    {
        var md = Script(
            """{"t":100,"n":1,"type":"cmd_unknown","command":"UNITS"}""",
            """{"t":160,"n":2,"type":"cmd_start","command":"UNITS","origin":"typed/other"}""",
            """{"t":900,"n":3,"type":"cmd_end","command":"UNITS"}""",
            """{"t":3000,"n":4,"type":"cmd_unknown","command":"FOO"}""");
        Assert.Contains("1. Type `UNITS`", md);
        Assert.DoesNotContain("2. Type `UNITS`", md);
        Assert.Contains("2. Type `FOO`", md);
    }

    [Fact]
    public void A_dialog_inside_its_command_lists_the_changes_as_deeper_sub_items()
    {
        var md = Script(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            """{"t":1800,"n":4,"type":"modal_leave"}""",
            Values,
            """{"t":1910,"n":6,"type":"cmd_end","command":"DRAWGRID"}""");
        Assert.Contains("   - A dialog opens: **Draw grid**. Make these changes, then confirm:\n", md);
        Assert.Contains("     - Set **Rows** to `5` (was `3`)\n", md);
        Assert.Contains("     - Clear **Name** (was `x`)\n", md);
        Assert.Contains("     - Tick **Snap**\n", md);
        Assert.Contains("     - Untick **Ortho**\n", md);
        Assert.Contains("     - Choose **Imperial**\n", md);
        Assert.Contains("     - Choose `B` in **Style** (was `A`)\n", md);
        Assert.Contains("     - Set **Quality** to `7` (was `1`)\n", md);
        Assert.Equal(1, Retrace.Core.ScriptRef.DialogStepNumber(md, 1));
    }

    [Fact]
    public void A_dialog_step_of_its_own_lists_the_changes_with_three_space_indent()
    {
        var md = Script(
            """{"t":100,"n":1,"type":"modal_enter","title":"Options"}""",
            """{"t":1800,"n":2,"type":"modal_leave"}""",
            """{"t":1810,"n":3,"type":"dialog_values","for":1,"title":"Options","source":"win32","fields":[{"key":"id:5","label":"Rows","kind":"edit","before":"3","after":"5"}],"read":2}""");
        Assert.Contains("1. A dialog opens: **Options**. Make these changes, then confirm:\n   - Set **Rows** to `5` (was `3`)\n", md);
        Assert.Equal(1, Retrace.Core.ScriptRef.DialogStepNumber(md, 1));
    }

    [Fact]
    public void Values_follow_the_screenshot_of_the_dialog()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "s1", "screenshots"));
        File.WriteAllLines(Path.Combine(_dir, "s1", "recording.jsonl"), new[]
        {
            Header,
            """{"t":100,"n":1,"type":"modal_enter","title":"Options"}""",
            """{"t":150,"n":2,"type":"shot","for":1,"file":"screenshots/001-dialog.png","what":"dialog"}""",
            """{"t":1800,"n":3,"type":"modal_leave"}""",
            """{"t":1810,"n":4,"type":"dialog_values","for":1,"fields":[{"key":"id:5","label":"Rows","kind":"edit","before":"3","after":"5"}],"read":2}""",
        });
        File.WriteAllBytes(Path.Combine(_dir, "s1", "screenshots", "001-dialog.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        Assert.True(EngineRunner.WriteSteps(Path.Combine(_dir, "s1", "recording.jsonl")).Success);
        var md = File.ReadAllText(Path.Combine(_dir, "s1", "steps.md"));
        var shot = md.IndexOf("step-01-dialog-before.png", StringComparison.Ordinal);
        var item = md.IndexOf("   - Set **Rows**", StringComparison.Ordinal);
        Assert.True(shot > 0 && item > shot, md);
    }

    [Fact]
    public void No_changes_but_fields_read_says_leave_the_values()
    {
        var md = Script(
            """{"t":100,"n":1,"type":"modal_enter","title":"Options"}""",
            """{"t":1800,"n":2,"type":"modal_leave"}""",
            """{"t":1810,"n":3,"type":"dialog_values","for":1,"fields":[],"read":4}""");
        Assert.Contains("1. A dialog opens: **Options**. Fill it in and confirm.\n   - Leave the values as they are.\n", md);
    }

    [Fact]
    public void Nothing_read_adds_no_sub_item_and_old_sessions_keep_todays_wording()
    {
        var none = Script(
            """{"t":100,"n":1,"type":"modal_enter","title":"Options"}""",
            """{"t":1800,"n":2,"type":"modal_leave"}""",
            """{"t":1810,"n":3,"type":"dialog_values","for":1,"fields":[],"read":0}""");
        var old = Script(
            """{"t":100,"n":1,"type":"modal_enter","title":"Options"}""",
            """{"t":1800,"n":2,"type":"modal_leave"}""");
        Assert.DoesNotContain("Leave the values", none);
        Assert.Equal(old, none);
        Assert.Contains("1. A dialog opens: **Options**. Fill it in and confirm.\n", old);
    }

    [Fact]
    public void A_radio_that_became_off_is_not_listed_and_a_per_radio_on_is()
    {
        var md = Script(
            """{"t":100,"n":1,"type":"modal_enter","title":"Options"}""",
            """{"t":1800,"n":2,"type":"modal_leave"}""",
            """{"t":1810,"n":3,"type":"dialog_values","for":1,"fields":[{"key":"label:Metric#1","label":"Metric","kind":"radio","before":"off","after":"on"},{"key":"label:Imperial#1","label":"Imperial","kind":"radio","before":"on","after":"off"}],"read":2}""");
        Assert.Contains("   - Choose **Metric**\n", md);
        Assert.DoesNotContain("Imperial", md);
    }

    // ---- replay ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Plan_puts_dialog_values_on_the_dialog_step_and_the_pause_message_names_them()
    {
        var lines = new[]
        {
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWGRID"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            """{"t":1800,"n":4,"type":"modal_leave"}""",
            """{"t":1810,"n":5,"type":"dialog_values","for":3,"title":"Draw grid","source":"win32","fields":[{"key":"id:1","label":"Rows","kind":"edit","before":"3","after":"5"},{"key":"id:2","label":"Columns","kind":"edit","before":"1","after":"6"},{"key":"id:3","label":"Snap","kind":"check","before":"on","after":"off"}],"read":3}""",
        };
        var steps = Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        var d = steps.Single(x => x.Kind == "dialog");
        Assert.Equal(3, d.ValuesRead);
        Assert.Equal(3, d.Values!.Count);
        Assert.Equal("A dialog opens: Draw grid. Set Rows to 5, Set Columns to 6, Untick Snap, then close it with OK (see x.md, step 3).",
            ScriptRef.Message(d.DialogTitle, "x.md", 3, d.Values, d.ValuesRead));
    }

    [Fact]
    public void Pause_message_without_values_is_unchanged_and_no_change_says_leave()
    {
        Assert.Equal("A dialog opens: Layer. Fill it in and confirm (see a.md, step 2), then close it with OK.", ScriptRef.Message("Layer", "a.md", 2));
        Assert.Equal("A dialog opens: Layer. Fill it in and confirm, then close it with OK.", ScriptRef.Message("Layer", null, null, null, 0));
        Assert.Equal("A dialog opens: Layer. Fill it in and confirm, then close it with OK.", ScriptRef.Message("Layer", null, null, new List<FieldChange>(), 0));
        Assert.Equal("A dialog opens: Layer. Leave the values as they are, then close it with OK.", ScriptRef.Message("Layer", null, null, new List<FieldChange>(), 3));
    }
}
