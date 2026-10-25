using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>RETRACERUN filling a dialog itself: the pure parts (matching recorded fields to live controls, value compare, close button,
/// when to try, fallback message) and the result JSON.</summary>
public class DialogFillTests
{
    static FieldChange Ch(string key, string label, string kind, string after, string before = "") => new(key, label, kind, before, after);
    static LiveField Lf(string key, string label, string kind) => new(key, label, kind);

    // ---- Decide ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, 2, 5, "ok", AutoMode.Ok)]
    [InlineData(true, 2, 5, null, AutoMode.Ok)]
    [InlineData(true, 0, 5, "ok", AutoMode.Ok)]        // looked, nothing changed: just OK
    [InlineData(true, 0, 0, "ok", AutoMode.None)]      // nothing could be read: a person decides
    [InlineData(true, 0, 5, null, AutoMode.None)]      // closing unknown and no values: a person
    [InlineData(false, 0, 0, "ok", AutoMode.None)]     // no dialog_values event
    [InlineData(false, 0, 0, null, AutoMode.None)]
    [InlineData(true, 3, 5, "cancel", AutoMode.Cancel)]
    [InlineData(false, 0, 0, "cancel", AutoMode.Cancel)]
    public void Decide_picks_what_the_replay_may_do_alone(bool known, int changed, int read, string? closed, AutoMode expected) =>
        Assert.Equal(expected, FillMatcher.Decide(known, changed, read, closed));

    // ---- Match ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_label_without_its_group_box_matches_the_grouped_live_label_by_occurrence()
    {
        var live = new List<LiveField> { new("id:1004", "Length › Precision", "combo"), new("id:1005", "Angle › Precision", "combo"), new("id:1010", "Clockwise", "check") };
        var rec = new List<FieldChange> { new("label:Precision#2", "Precision", "combo", "0", "0.0"), new("label:Clockwise#1", "Clockwise", "check", "off", "on") };
        Assert.Equal(new[] { 1, 2 }, FillMatcher.Match(rec, live));
        var grouped = new List<FieldChange> { new("label:Length › Precision#1", "Length › Precision", "combo", "0", "0.0") };
        Assert.Equal(new[] { 0 }, FillMatcher.Match(grouped, new List<LiveField> { new("label:Precision#1", "Precision", "combo"), new("label:Precision#2", "Precision", "combo") }));
    }

    [Fact]
    public void Match_uses_the_label_even_when_the_control_id_changed()
    {
        // WinForms: handle-derived ids differ between runs
        var rec = new[] { Ch("id:4392698", "Rows", "edit", "5"), Ch("id:4392700", "Columns", "edit", "4") };
        var live = new[] { Lf("id:395290", "Rows", "edit"), Lf("id:395292", "Columns", "edit") };
        Assert.Equal(new[] { 0, 1 }, FillMatcher.Match(rec, live));
    }

    [Fact]
    public void Match_ignores_case_mnemonics_and_trailing_colon_of_labels()
    {
        var rec = new[] { Ch("id:7", "Rows", "edit", "5") };
        Assert.Equal(new[] { 0 }, FillMatcher.Match(rec, new[] { Lf("id:9", "&rows:", "edit") }));
    }

    [Fact]
    public void Match_picks_the_nth_field_with_the_same_label()
    {
        var rec = new[] { Ch("label:Precision#2", "Precision", "combo", "0.00") };
        var live = new[] { Lf("label:Precision#1", "Precision", "combo"), Lf("label:Precision#2", "Precision", "combo") };
        Assert.Equal(new[] { 1 }, FillMatcher.Match(rec, live));
    }

    [Fact]
    public void Match_with_a_missing_occurrence_is_not_found()
    {
        var rec = new[] { Ch("label:Precision#2", "Precision", "combo", "0.00") };
        Assert.Equal(new[] { -1 }, FillMatcher.Match(rec, new[] { Lf("id:1", "Precision", "combo") }));
    }

    [Fact]
    public void Match_with_an_id_key_and_a_repeated_label_prefers_the_same_id()
    {
        var rec = new[] { Ch("id:1010", "Name", "edit", "x") };
        var live = new[] { Lf("id:1008", "Name", "edit"), Lf("id:1010", "Name", "edit") };
        Assert.Equal(new[] { 1 }, FillMatcher.Match(rec, live));
    }

    [Fact]
    public void Match_falls_back_to_a_stock_id_only_when_no_label_matches()
    {
        var rec = new[] { Ch("id:1004", "Rows", "edit", "5") };
        Assert.Equal(new[] { 0 }, FillMatcher.Match(rec, new[] { Lf("id:1004", "Edit1", "edit") }));
        Assert.Equal(new[] { -1 }, FillMatcher.Match(rec, new[] { Lf("id:1005", "Edit1", "edit") }));
    }

    [Fact]
    public void Match_never_falls_back_to_a_handle_derived_id()
    {
        var rec = new[] { Ch("id:4392698", "Rows", "edit", "5") };
        Assert.Equal(new[] { -1 }, FillMatcher.Match(rec, new[] { Lf("id:4392698", "Lines", "edit") }));
    }

    [Fact]
    public void Match_needs_the_same_kind_and_uses_a_field_once()
    {
        var rec = new[] { Ch("id:1", "Snap", "check", "on"), Ch("id:2", "Snap", "check", "off") };
        Assert.Equal(new[] { -1 }, FillMatcher.Match(new[] { rec[0] }, new[] { Lf("id:1", "Snap", "edit") }));
        var r = FillMatcher.Match(rec, new[] { Lf("id:1", "Snap", "check") });
        Assert.Equal(new[] { 0, -1 }, r);
    }

    [Fact]
    public void Match_finds_a_radio_group_by_its_joined_label()
    {
        var rec = new[] { Ch("label:Metric / Imperial#1", "Metric / Imperial", "radio", "Imperial") };
        Assert.Equal(new[] { 0 }, FillMatcher.Match(rec, new[] { Lf("id:5", "Metric / Imperial", "radio") }));
    }

    [Fact]
    public void BuildMapped_says_which_raw_controls_make_a_field()
    {
        var raw = new List<RawControl>
        {
            new(1, "Rows", "edit", "3"),
            new(2, "Metric", "radio", "on", true, 1), new(3, "Imperial", "radio", "off", false, 1),
            new(4, "Snap", "check", "off"),
        };
        var m = DialogFieldsCore.BuildMapped(raw);
        Assert.Equal(3, m.Count);
        Assert.Equal(new[] { 1, 2 }, m[1].Raw);
        Assert.Equal("Metric / Imperial", m[1].Field.Label);
        Assert.Equal(new[] { 3 }, m[2].Raw);
        Assert.Equal(DialogFieldsCore.Build(raw), m.Select(x => x.Field).ToList());
    }

    // ---- values ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("edit", "20", "20.00", true)]
    [InlineData("edit", " 20 ", "20", true)]
    [InlineData("edit", "0.5", ".5", true)]
    [InlineData("edit", "20", "21", false)]
    [InlineData("edit", "abc", "ABC", true)]
    [InlineData("edit", "", "", true)]
    [InlineData("check", "on", "on", true)]
    [InlineData("check", "on", "off", false)]
    [InlineData("combo", "Decimal", "decimal", true)]
    [InlineData("radio", "Imperial", "Length › Imperial", true)]
    [InlineData("radio", "Imperial", "Metric", false)]
    [InlineData("slider", "5", "5", true)]
    public void ValueMatches_compares_numbers_as_numbers(string kind, string expected, string actual, bool same) =>
        Assert.Equal(same, FillMatcher.ValueMatches(kind, expected, actual));

    [Fact]
    public void Expected_of_a_per_button_radio_is_its_label()
    {
        Assert.Equal("Metric", FillMatcher.Expected(Ch("x", "Metric", "radio", "on")));
        Assert.Equal("Imperial", FillMatcher.Expected(Ch("x", "Metric / Imperial", "radio", "Imperial")));
        Assert.Equal("5", FillMatcher.Expected(Ch("x", "Rows", "edit", "5")));
        Assert.True(FillMatcher.IsNoOp(Ch("x", "A / B", "radio", "")));
        Assert.False(FillMatcher.IsNoOp(Ch("x", "Rows", "edit", "")));
    }

    // ---- close button ---------------------------------------------------------------------------------------------------

    [Fact]
    public void PickButton_prefers_the_stock_id_then_the_text()
    {
        var stock = new List<(int, string)> { (1010, "Help"), (2, "Cancel"), (1, "OK") };
        Assert.Equal(2, FillMatcher.PickButton(stock, true));
        Assert.Equal(1, FillMatcher.PickButton(stock, false));
        var winForms = new List<(int, string)> { (4392698, "&Help"), (4392700, "OK"), (4392702, "&Cancel") };
        Assert.Equal(1, FillMatcher.PickButton(winForms, true));
        Assert.Equal(2, FillMatcher.PickButton(winForms, false));
        Assert.Equal(-1, FillMatcher.PickButton(new List<(int, string)> { (7, "Apply") }, true));
        Assert.Equal(-1, FillMatcher.PickButton(new List<(int, string)>(), false));
    }

    [Fact]
    public void TitleMatches_needs_the_recorded_title_when_there_is_one()
    {
        Assert.True(FillMatcher.TitleMatches("Draw grid", " draw grid "));
        Assert.False(FillMatcher.TitleMatches("Draw grid", "Drawing Units"));
        Assert.True(FillMatcher.TitleMatches(null, "anything"));
        Assert.True(FillMatcher.TitleMatches("", "anything"));
    }

    // ---- message --------------------------------------------------------------------------------------------------------

    static readonly List<FieldChange> Three = new()
    {
        Ch("id:1", "Rows", "edit", "5", "3"), Ch("id:2", "Columns", "edit", "4", "3"), Ch("id:3", "Spacing", "edit", "20", "10"),
    };

    [Fact]
    public void FallbackMessage_lists_what_is_left_to_do()
    {
        var results = new List<FieldFillResult> { FieldFillResult.Ok("Rows"), FieldFillResult.Ok("Columns"), FieldFillResult.Missing("Spacing") };
        Assert.Equal("A dialog opens: Draw grid. Filled 2 of 3 values; set Spacing to 20 yourself, then close it with OK (see s.md, step 4).",
            FillMatcher.FallbackMessage("Draw grid", "s.md", 4, Three, results, "ok", null));
    }

    [Fact]
    public void FallbackMessage_when_everything_went_in_but_it_did_not_close()
    {
        var results = Three.Select(c => FieldFillResult.Ok(c.Label)).ToList();
        Assert.Equal("A dialog opens: Draw grid. The dialog is still open after pressing OK (a message box?). Filled all 3 values; close it with OK.",
            FillMatcher.FallbackMessage("Draw grid", null, null, Three, results, "ok", "The dialog is still open after pressing OK (a message box?)."));
    }

    [Fact]
    public void FallbackMessage_for_cancel_and_for_no_values()
    {
        Assert.Equal("A dialog opens: Units. Close it with Cancel (see s.md, step 2).", FillMatcher.FallbackMessage("Units", "s.md", 2, null, new List<FieldFillResult>(), "cancel", null));
        Assert.Equal("A dialog opens: Units. could not find the Cancel button. Close it with Cancel.",
            FillMatcher.FallbackMessage("Units", null, null, Three, new List<FieldFillResult>(), "cancel", "could not find the Cancel button"));
        Assert.Equal("A dialog opens: Units. Close it with OK.", FillMatcher.FallbackMessage("Units", null, null, new List<FieldChange>(), new List<FieldFillResult>(), "ok", null));
    }

    [Fact]
    public void FallbackMessage_with_no_results_yet_lists_every_value()
    {
        var m = FillMatcher.FallbackMessage("Draw grid", null, null, Three, new List<FieldFillResult>(), null, null);
        Assert.Equal("A dialog opens: Draw grid. Filled 0 of 3 values; set Rows to 5, set Columns to 4, set Spacing to 20 yourself, then close it with OK.", m);
    }

    // ---- result json, settings, plan --------------------------------------------------------------------------------------

    [Fact]
    public void Result_json_has_autoFilled_and_per_field_status()
    {
        var r = new ReplayResult();
        r.Steps.Add(new StepResult
        {
            Index = 2, Kind = "dialog", AutoFilled = true,
            Fields = new() { FieldFillResult.Ok("Rows"), FieldFillResult.Fail("Spacing", "value is \"10\" after setting"), FieldFillResult.Missing("Gap") },
        });
        var s = JsonDocument.Parse(r.ToJson()).RootElement.GetProperty("steps")[0];
        Assert.True(s.GetProperty("autoFilled").GetBoolean());
        Assert.False(s.GetProperty("waitedForPerson").GetBoolean());
        var f = s.GetProperty("fields");
        Assert.Equal("applied", f[0].GetProperty("status").GetString());
        Assert.False(f[0].TryGetProperty("message", out _));
        Assert.Equal("failed", f[1].GetProperty("status").GetString());
        Assert.Equal("value is \"10\" after setting", f[1].GetProperty("message").GetString());
        Assert.Equal("notFound", f[2].GetProperty("status").GetString());
        Assert.Equal("Rows", f[0].GetProperty("label").GetString());
    }

    [Fact]
    public void Result_json_leaves_out_autoFilled_and_fields_when_there_were_none()
    {
        var r = new ReplayResult();
        r.Steps.Add(new StepResult { Index = 1, Kind = "command" });
        var s = JsonDocument.Parse(r.ToJson()).RootElement.GetProperty("steps")[0];
        Assert.False(s.TryGetProperty("autoFilled", out _));
        Assert.False(s.TryGetProperty("fields", out _));
        Assert.True(s.TryGetProperty("waitedForPerson", out _));
    }

    [Fact]
    public void Setting_autoFillDialogs_is_on_by_default_and_round_trips()
    {
        var s = new RetraceSettings();
        Assert.True(s.AutoFillDialogs);
        var json = JsonSerializer.Serialize(new RetraceSettings { AutoFillDialogs = false });
        Assert.Contains("\"autoFillDialogs\":false", json);
        Assert.False(JsonSerializer.Deserialize<RetraceSettings>(json)!.AutoFillDialogs);
        Assert.True(JsonSerializer.Deserialize<RetraceSettings>("{}")!.AutoFillDialogs);
    }

    [Fact]
    public void Plan_keeps_the_source_of_the_recorded_values()
    {
        var lines = new[]
        {
            """{"type":"header","schema":1,"name":"t","started":"2026-10-08T10:00:00"}""",
            """{"type":"modal_enter","t":10,"n":1,"title":"Draw grid"}""",
            """{"type":"dialog_values","t":20,"for":1,"title":"Draw grid","source":"uia","fields":[{"key":"label:Rows#1","label":"Rows","kind":"edit","before":"3","after":"5"}],"read":4,"closedWith":"ok"}""",
        };
        var step = Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone())).Single(x => x.Kind == "dialog");
        Assert.Equal("uia", step.ValuesSource);
        Assert.Equal("ok", step.ClosedWith);
    }
}
