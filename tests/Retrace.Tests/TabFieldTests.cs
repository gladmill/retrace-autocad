using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Tab controls in dialogs, palettes and native palettes: the path of tab texts a field is on, the keys that name it, finding a recorded field on the right page, what a replay must
/// switch to, the wording (steps.md, the pause message, Tdoc) and the native tab click. Synthetic data only: the Win32 reading is not testable here.</summary>
public class TabFieldTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-tabs").FullName;
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

    // ---- the path -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Join_and_Split_make_a_path_of_tab_texts_outermost_first()
    {
        Assert.Equal("Fit › Text placement", TabPath.Join(new[] { "Fit", "Text placement" }));
        Assert.Equal("Lines", TabPath.Join(new[] { "&Lines" }));   // mnemonic out
        Assert.Equal("", TabPath.Join(new string?[] { null, " " }));
        Assert.Equal(new[] { "Fit", "Text placement" }, TabPath.Split("Fit › Text placement"));
        Assert.Empty(TabPath.Split(""));
        Assert.Empty(TabPath.Split(null));
    }

    [Theory]
    [InlineData("Lines", "lines ", true)]
    [InlineData("&Lines", "Lines", true)]
    [InlineData("Fit › Text", "fit › text", true)]
    [InlineData("Fit › Text", "Fit", false)]
    [InlineData("Lines", "Text", false)]
    [InlineData("", "", true)]
    public void Same_compares_level_by_level_trimmed_ignoring_case_and_mnemonics(string a, string b, bool same) => Assert.Equal(same, TabPath.Same(a, b));

    // ---- keys -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_key_names_its_tab_and_a_field_outside_the_tabs_keeps_its_key()
    {
        Assert.Equal("tab:Lines|id:1001", TabPath.Qualify("id:1001", "Lines"));
        Assert.Equal("tab:Fit › Text placement|name:txtHeight", TabPath.Qualify("name:txtHeight", "Fit › Text placement"));
        Assert.Equal("id:1001", TabPath.Qualify("id:1001", ""));
        Assert.Equal("id:1001", TabPath.Qualify("id:1001", null));
    }

    [Theory]
    [InlineData("tab:Lines|id:1001", "Lines", "id:1001")]
    [InlineData("tab:Text|name:txtHeight", "Text", "name:txtHeight")]
    [InlineData("tab:Text|label:Height#2", "Text", "label:Height#2")]
    [InlineData("tab:Fit › Text placement|label:Cut | copy#1", "Fit › Text placement", "label:Cut | copy#1")]   // a label with a bar in it
    [InlineData("id:1001", "", "id:1001")]
    [InlineData("label:Name#1", "", "label:Name#1")]
    [InlineData("tab:odd", "", "tab:odd")]   // not one of the forms: left as it is
    public void A_key_is_taken_apart_into_its_tab_and_the_rest(string key, string tab, string rest)
    {
        Assert.Equal(tab, TabPath.TabOf(key));
        Assert.Equal(rest, TabPath.Unqualified(key));
    }

    [Fact]
    public void Two_pages_that_reuse_one_control_id_are_two_fields_with_two_keys()
    {
        // a property sheet reuses ids across its pages: id 1001 is "Line spacing" on Lines and "Text height" on Text
        var raw = new List<RawControl>
        {
            new(1001, "Line spacing", "edit", "1.5", Tab: "Lines"),
            new(1001, "Text height", "edit", "2.5", Tab: "Text"),
            new(1002, "Draw frame", "check", "off", Tab: "Text"),
            new(1, "Preview", "check", "on"),   // outside the tabs
        };
        var fields = DialogFieldsCore.Build(raw);
        Assert.Equal(new[] { "tab:Lines|id:1001", "tab:Text|id:1001", "tab:Text|id:1002", "id:1" }, fields.Select(f => f.Key));
        Assert.Equal(new[] { "Lines", "Text", "Text", "" }, fields.Select(f => f.Tab));
        Assert.Equal(fields.Count, fields.Select(f => f.Key).Distinct().Count());
    }

    [Fact]
    public void Ids_names_and_labels_are_counted_per_tab()
    {
        var raw = new List<RawControl>
        {
            new(0, "Name", "edit", "a", Tab: "One"), new(0, "Name", "edit", "b", Tab: "One"),   // two on a page: occurrences 1 and 2 on that page
            new(0, "Name", "edit", "c", Tab: "Two"),                                            // the first on the other page, not the third
            new(7, "X", "edit", "1", Tab: "One", Name: "txtX"), new(8, "Y", "edit", "2", Tab: "Two", Name: "txtX"),   // one name on two pages
        };
        var keys = DialogFieldsCore.Build(raw).Select(f => f.Key).ToList();
        Assert.Equal(new[] { "tab:One|label:Name#1", "tab:One|label:Name#2", "tab:Two|label:Name#1", "tab:One|name:txtX", "tab:Two|name:txtX" }, keys);
    }

    [Fact]
    public void A_dialog_without_tabs_keys_exactly_as_before()
    {
        var raw = new List<RawControl> { new(1001, "Rows", "edit", "3"), new(1002, "Snap", "check", "off"), new(0, "Name", "edit", ""), new(0, "Name", "edit", "") };
        var fields = DialogFieldsCore.Build(raw);
        Assert.Equal(new[] { "id:1001", "id:1002", "label:Name#1", "label:Name#2" }, fields.Select(f => f.Key));
        Assert.All(fields, f => Assert.Equal("", f.Tab));
    }

    [Fact]
    public void A_radio_group_is_on_the_tab_of_its_buttons()
    {
        var raw = new List<RawControl>
        {
            new(5, "Metric", "radio", "on", true, 100, Tab: "Units"), new(6, "Imperial", "radio", "off", false, 100, Tab: "Units"),
        };
        var f = Assert.Single(DialogFieldsCore.Build(raw));
        Assert.Equal("Units", f.Tab);
        Assert.StartsWith("tab:Units|", f.Key);
    }

    // ---- the tracker: pages that reuse ids do not collide -----------------------------------------------------------------

    [Fact]
    public void The_tracker_does_not_diff_one_page_against_another_page_that_reuses_the_id()
    {
        var t = new FieldTracker();
        var lines = new DialogField("tab:Lines|id:1001", "Line spacing", "edit", "1.5", "Lines");
        var text = new DialogField("tab:Text|id:1001", "Text height", "edit", "2.5", "Text");
        t.Observe(new[] { lines });
        t.Observe(new[] { text });   // the person switched pages: the same control id, another field
        Assert.Empty(t.Changes());   // 1.5 against 2.5 is no change
        t.Observe(new[] { text with { Value = "3.5" } });
        var c = Assert.Single(t.Changes());
        Assert.Equal(("Text height", "2.5", "3.5", "Text"), (c.Label, c.Before, c.After, c.Tab));
        Assert.Equal("tab:Text|id:1001", c.Key);
    }

    [Fact]
    public void A_change_made_on_a_page_is_kept_when_the_person_leaves_the_page()
    {
        var t = new FieldTracker();
        t.Observe(new[] { new DialogField("tab:Lines|id:1001", "Line spacing", "edit", "1.5", "Lines") });
        t.Observe(new[] { new DialogField("tab:Lines|id:1001", "Line spacing", "edit", "2", "Lines") });
        t.Observe(new[] { new DialogField("tab:Text|id:1001", "Text height", "edit", "2.5", "Text") });   // the Lines page is not visible any more
        var c = Assert.Single(t.Changes());
        Assert.Equal(("Line spacing", "Lines"), (c.Label, c.Tab));
    }

    // ---- finding a recorded field --------------------------------------------------------------------------------------------

    static LiveField L(string key, string label, string kind, string tab) => new(key, label, kind, tab);
    static FieldChange C(string key, string label, string kind, string after, string tab = "") => new(key, label, kind, "", after, tab);

    [Fact]
    public void A_recorded_field_is_only_found_on_its_own_tab()
    {
        var live = new[] { L("tab:Lines|id:1001", "Line spacing", "edit", "Lines"), L("tab:Lines|id:1002", "Height", "edit", "Lines") };
        // the Text page is not showing: its field has no live field yet, even though the id and the label of another page's field are alike
        Assert.Equal(new[] { -1 }, FillMatcher.Match(new[] { C("tab:Text|id:1001", "Text height", "edit", "3.5", "Text") }, live));
        Assert.Equal(new[] { -1 }, FillMatcher.Match(new[] { C("tab:Text|id:1002", "Height", "edit", "3.5", "Text") }, live));
        Assert.Equal(new[] { 0 }, FillMatcher.Match(new[] { C("tab:Lines|id:1001", "Line spacing", "edit", "2", "Lines") }, live));
    }

    [Fact]
    public void Two_pages_with_one_id_are_told_apart_when_both_are_in_the_list()
    {
        var live = new[] { L("tab:Lines|id:1001", "Spacing", "edit", "Lines"), L("tab:Text|id:1001", "Spacing", "edit", "Text") };
        Assert.Equal(new[] { 1, 0 }, FillMatcher.Match(new[] { C("tab:Text|id:1001", "Spacing", "edit", "3", "Text"), C("tab:Lines|id:1001", "Spacing", "edit", "2", "Lines") }, live));
    }

    [Fact]
    public void A_recorded_key_without_a_tab_still_matches_a_live_key_by_the_part_after_the_tab()
    {
        var live = new[] { L("tab:Text|id:1004", "Text height", "edit", "Text"), L("tab:Text|name:txtGap", "Gap", "edit", "Text"), L("tab:Text|label:Offset#2", "Offset", "edit", "Text") };
        // a recording from before tabs was read: id and name and occurrence keys with no tab
        Assert.Equal(new[] { 0, 1 }, FillMatcher.Match(new[] { C("id:1004", "Text height", "edit", "3.5"), C("name:txtGap", "Gap", "edit", "1") }, live));
        // by id alone, when the label has changed and the id is a stock one
        Assert.Equal(new[] { 0 }, FillMatcher.Match(new[] { C("id:1004", "Height of text", "edit", "3.5") }, live));
        // by name alone
        Assert.Equal(new[] { 1 }, FillMatcher.Match(new[] { C("name:txtGap", "Spacing", "edit", "3") }, live));
    }

    [Fact]
    public void The_tab_in_the_key_counts_when_the_event_has_no_tab_of_its_own()
    {
        var live = new[] { L("tab:Lines|id:1001", "Spacing", "edit", "Lines"), L("tab:Text|id:1001", "Spacing", "edit", "Text") };
        Assert.Equal(new[] { 1 }, FillMatcher.Match(new[] { C("tab:Text|id:1001", "Spacing", "edit", "3") }, live));
    }

    [Fact]
    public void An_occurrence_number_counts_the_fields_of_its_tab()
    {
        var live = new[]
        {
            L("tab:One|label:Name#1", "Name", "edit", "One"), L("tab:One|label:Name#2", "Name", "edit", "One"), L("tab:Two|label:Name#1", "Name", "edit", "Two"),
        };
        Assert.Equal(new[] { 2 }, FillMatcher.Match(new[] { C("tab:Two|label:Name#1", "Name", "edit", "x", "Two") }, live));
        Assert.Equal(new[] { 1 }, FillMatcher.Match(new[] { C("tab:One|label:Name#2", "Name", "edit", "x", "One") }, live));
        // an older recording, no tabs: the n-th live field with the label, whatever its tab (as before)
        Assert.Equal(new[] { 1 }, FillMatcher.Match(new[] { C("label:Name#2", "Name", "edit", "x") }, live));
    }

    [Fact]
    public void A_recorded_field_with_a_tab_never_matches_a_live_field_outside_the_tabs()
    {
        // an outside "Name" before the General page's "Name": the recorded General field is the one on the page, and with no page showing it is not the outside one
        var live = new[] { L("id:1", "Name", "edit", ""), L("tab:General|id:2", "Name", "edit", "General") };
        Assert.Equal(new[] { 1 }, FillMatcher.Match(new[] { C("tab:General|id:2", "Name", "edit", "x", "General") }, live));
        Assert.Equal(new[] { -1 }, FillMatcher.Match(new[] { C("tab:Text|id:2", "Name", "edit", "x", "Text") }, live));
        Assert.Equal(new[] { -1 }, FillMatcher.Match(new[] { C("tab:General|id:1", "Name", "edit", "x", "General") }, new[] { L("id:1", "Name", "edit", "") }));
    }

    [Fact]
    public void An_occurrence_number_counts_only_the_fields_of_its_tab_not_an_outside_field_with_the_label()
    {
        var live = new[] { L("label:Height#1", "Height", "edit", ""), L("tab:Text|label:Height#1", "Height", "edit", "Text") };
        Assert.Equal(new[] { 1 }, FillMatcher.Match(new[] { C("tab:Text|label:Height#1", "Height", "edit", "x", "Text") }, live));
        Assert.Equal(new[] { 0 }, FillMatcher.Match(new[] { C("label:Height#1", "Height", "edit", "x") }, live));   // outside the tabs, no tab recorded
    }

    [Fact]
    public void A_nested_tab_path_is_compared_whole()
    {
        var live = new[] { L("tab:Fit › Text placement|id:5", "Position", "combo", "Fit › Text placement"), L("tab:Fit › Arrows|id:5", "Position", "combo", "Fit › Arrows") };
        Assert.Equal(new[] { 1 }, FillMatcher.Match(new[] { C("tab:Fit › Arrows|id:5", "Position", "combo", "x", "Fit › Arrows") }, live));
        Assert.Equal(new[] { -1 }, FillMatcher.Match(new[] { C("tab:Fit|id:5", "Position", "combo", "x", "Fit") }, live));   // another level
    }

    [Fact]
    public void IdOf_and_OccurrenceOf_read_through_the_tab()
    {
        Assert.Equal(1001, FillMatcher.IdOf("tab:Lines|id:1001"));
        Assert.Equal(1001, FillMatcher.IdOf("id:1001"));
        Assert.Equal(2, FillMatcher.OccurrenceOf("tab:Lines|label:Height#2"));
        Assert.Null(FillMatcher.IdOf("tab:Lines|name:x"));
    }

    // ---- the tab controls and what to switch ----------------------------------------------------------------------------------

    static TabInfo Tab(int id, int selected, int[] outer, params string[] items) => new(id, items, selected, outer);

    [Fact]
    public void The_path_of_a_control_is_the_selected_tab_of_each_tab_control_it_sits_in_outermost_first()
    {
        var tabs = new[] { Tab(0, 1, Array.Empty<int>(), "Lines", "Fit", "Text"), Tab(1, 0, new[] { 0 }, "Text placement", "Arrows") };
        Assert.Equal("Fit › Text placement", TabPlan.PathOf(new[] { 1, 0 }, tabs));   // in any order
        Assert.Equal("Fit", TabPlan.PathOf(new[] { 0 }, tabs));
        Assert.Equal("", TabPlan.PathOf(Array.Empty<int>(), tabs));
    }

    [Fact]
    public void What_the_dialog_shows_follows_the_first_tab_control_down_its_pages()
    {
        var tabs = new[] { Tab(0, 1, Array.Empty<int>(), "Lines", "Fit"), Tab(1, 0, new[] { 0 }, "Text placement", "Arrows"), Tab(2, 2, Array.Empty<int>(), "a", "b", "c") };
        Assert.Equal("Fit › Text placement", TabPlan.Showing(tabs));
        Assert.Equal("", TabPlan.Showing(new List<TabInfo>()));
        Assert.Equal("", TabPlan.Showing(new[] { Tab(0, -1, Array.Empty<int>(), "A") }));   // nothing selected
    }

    [Fact]
    public void Next_is_done_when_the_tab_shows_and_for_no_tab_at_all()
    {
        var tabs = new[] { Tab(0, 2, Array.Empty<int>(), "Lines", "Fit", "Text") };
        Assert.Equal(TabAction.Done, TabPlan.Next("Text", tabs).Action);
        Assert.Equal(TabAction.Done, TabPlan.Next("text", tabs).Action);
        Assert.Equal(TabAction.Done, TabPlan.Next("", tabs).Action);
        Assert.Equal(TabAction.Done, TabPlan.Next(null, new List<TabInfo>()).Action);
    }

    [Fact]
    public void Next_switches_the_tab_control_that_has_the_tab()
    {
        var tabs = new[] { Tab(0, 0, Array.Empty<int>(), "Lines", "Fit", "Text"), Tab(1, 0, Array.Empty<int>(), "Left", "Right") };
        var step = TabPlan.Next("Text", tabs);
        Assert.Equal((TabAction.Switch, 0, 2, "Text"), (step.Action, step.Tab, step.Item, step.Text));
        step = TabPlan.Next("Right", tabs);
        Assert.Equal((TabAction.Switch, 1, 1), (step.Action, step.Tab, step.Item));
    }

    [Fact]
    public void Next_goes_level_by_level_and_the_inner_tab_control_exists_only_once_its_page_shows()
    {
        // Fit is not showing: its page, with the inner tab control, does not exist yet. One switch at a time
        var outerOnly = new[] { Tab(0, 0, Array.Empty<int>(), "Lines", "Fit") };
        var step = TabPlan.Next("Fit › Text placement", outerOnly);
        Assert.Equal((TabAction.Switch, 0, 1), (step.Action, step.Tab, step.Item));
        // Fit shows; the inner control (showing Arrows) is in it
        var both = new[] { Tab(0, 1, Array.Empty<int>(), "Lines", "Fit"), Tab(1, 1, new[] { 0 }, "Text placement", "Arrows") };
        step = TabPlan.Next("Fit › Text placement", both);
        Assert.Equal((TabAction.Switch, 1, 0, "Text placement"), (step.Action, step.Tab, step.Item, step.Text));
        // and with the inner tab showing, nothing is left
        Assert.Equal(TabAction.Done, TabPlan.Next("Fit › Text placement", new[] { both[0], both[1] with { Selected = 0 } }).Action);
    }

    [Fact]
    public void Next_says_which_tab_is_missing()
    {
        var tabs = new[] { Tab(0, 0, Array.Empty<int>(), "Lines", "Fit") };
        var step = TabPlan.Next("Colors", tabs);
        Assert.Equal(TabAction.Missing, step.Action);
        Assert.Equal("no tab Colors in the dialog", step.Problem);
        step = TabPlan.Next("Lines › Inner", tabs);   // Lines shows; there is no inner control
        Assert.Equal("no tab Inner in the dialog (under Lines)", step.Problem);
        Assert.Equal(TabAction.Missing, TabPlan.Next("Lines", new List<TabInfo>()).Action);
    }

    [Fact]
    public void Next_takes_the_tab_control_that_already_shows_the_tab_when_two_have_one()
    {
        var tabs = new[] { Tab(0, 0, Array.Empty<int>(), "General", "More"), Tab(1, 1, Array.Empty<int>(), "Other", "More") };
        Assert.Equal(TabAction.Done, TabPlan.Next("More", tabs).Action);   // the second one shows More
    }

    // ---- the shared planner ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_value_that_is_not_set_yet_on_the_showing_tab_waits_there_and_the_tabs_never_alternate()
    {
        var tabs = new[] { Tab(0, 0, Array.Empty<int>(), "A", "B") };   // A shows
        var items = new[] { new FillItem("A", Matched: false, Pending: true), new FillItem("B", Matched: true, Pending: true) };
        var plan = TabFill.Next(items, tabs, expired: false);
        Assert.Equal(0, plan.WaitFor);
        Assert.Null(plan.Switch);
        Assert.Empty(plan.Set);
        plan = TabFill.Next(new[] { items[0] with { Expired = true }, items[1] }, tabs, expired: true);   // its own controls timeout: it fails with its own reason, B is never shown
        Assert.Equal(new[] { 0 }, plan.Fail);
        Assert.Null(plan.Switch);
    }

    [Fact]
    public void A_late_filled_list_on_the_showing_tab_blocks_the_switch_until_its_own_timeout_runs_out()
    {
        var tabs = new[] { Tab(0, 0, Array.Empty<int>(), "A", "B") };
        // the field matches but setting it failed before (the list is still filling): tried again on A, B is not shown
        var items = new[] { new FillItem("A", true, true, Retrying: true), new FillItem("B", true, true) };
        var plan = TabFill.Next(items, tabs, expired: false);
        Assert.Equal(new[] { 0 }, plan.Set);
        Assert.Equal(0, plan.WaitFor);
        Assert.Null(plan.Switch);
        // a tab switch does not restart the timeout: the item's own Expired decides, whatever the segment's clock says
        var late = TabFill.Next(new[] { items[0] with { Expired = false }, items[1] }, tabs, expired: true);
        Assert.Null(late.Switch);
        Assert.Empty(late.Fail);
    }

    [Fact]
    public void A_recorded_field_outside_the_tabs_matches_the_live_field_outside_them_not_the_one_on_a_page()
    {
        var live = new[] { L("tab:Fit|label:Scale#1", "Scale", "edit", "Fit"), L("label:Scale#1", "Scale", "edit", "") };
        Assert.Equal(new[] { 1 }, FillMatcher.Match(new[] { C("label:Scale#1", "Scale", "edit", "2") }, live));
        Assert.Equal(new[] { 0 }, FillMatcher.Match(new[] { C("tab:Fit|label:Scale#1", "Scale", "edit", "2", "Fit") }, live));
        // an older recording of a tabbed dialog (no tab for any field) still finds the field on its page
        Assert.Equal(new[] { 0 }, FillMatcher.Match(new[] { C("label:Scale#1", "Scale", "edit", "2") }, new[] { live[0] }));
    }

    [Fact]
    public void What_is_set_on_the_showing_tab_comes_before_the_switch_and_the_recorded_order_is_kept()
    {
        var tabs = new[] { Tab(0, 0, Array.Empty<int>(), "A", "B") };
        var items = new[] { new FillItem("A", true, true), new FillItem("B", true, true), new FillItem("A", true, true) };
        var plan = TabFill.Next(items, tabs, expired: false);
        Assert.Equal(new[] { 0 }, plan.Set);
        Assert.Equal((1, "B"), (plan.Switch!.Item, plan.SwitchPath));
        // done values are skipped; with B showing the next pass sets B's value and then asks for A
        var next = TabFill.Next(new[] { items[0] with { Pending = false }, items[1], items[2] }, new[] { Tab(0, 1, Array.Empty<int>(), "A", "B") }, false);
        Assert.Equal(new[] { 1 }, next.Set);
        Assert.Equal("A", next.SwitchPath);
    }

    [Fact]
    public void A_tab_that_is_not_there_waits_then_fails_with_the_reason_and_a_value_on_no_tab_never_switches()
    {
        var tabs = new[] { Tab(0, 0, Array.Empty<int>(), "A") };
        var items = new[] { new FillItem("Z", true, true) };
        Assert.Equal(0, TabFill.Next(items, tabs, false).WaitFor);
        var plan = TabFill.Next(items, tabs, true);
        Assert.Equal(new[] { 0 }, plan.Fail);
        Assert.Equal("no tab Z in the dialog", plan.Problem);
        var none = TabFill.Next(new[] { new FillItem("", true, true) }, tabs, false);
        Assert.Equal(new[] { 0 }, none.Set);
        Assert.Null(none.Switch);
    }

    // ---- wording helpers -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Runs_keep_the_order_and_group_the_consecutive_items_on_one_tab()
    {
        var runs = TabWording.Runs(new[] { ("Text", "a"), ("Text", "b"), ("Units", "c"), ("Text", "d"), ("", "e"), ("", "f") }, x => x.Item1);
        Assert.Equal(new[] { "Text", "Units", "Text", "" }, runs.Select(r => r.Tab));
        Assert.Equal(new[] { 2, 1, 1, 2 }, runs.Select(r => r.Items.Count));
    }

    [Fact]
    public void Phrases_name_the_tab_at_the_start_of_each_run()
    {
        var p = TabWording.Phrases(new[] { ("", "set Name to A"), ("Text", "set Text height to 3.5"), ("Text", "tick Draw frame"), ("Units", "set Precision to 0.000"), ("", "press Apply") });
        Assert.Equal(new[] { "set Name to A", "on the Text tab: set Text height to 3.5", "tick Draw frame", "on the Units tab: set Precision to 0.000", "outside the tabs: press Apply" }, p);
        Assert.Equal(new[] { "set A", "tick B" }, TabWording.Phrases(new[] { ("", "set A"), ("", "tick B") }));   // no tabs: as they are
    }

    [Fact]
    public void TabLines_put_a_header_line_before_a_run_on_a_tab()
    {
        var t = new TabLines();
        var lines = new[] { ("", "Set Name to A."), ("Text", "Set Height to 3."), ("Text", "Tick Frame."), ("Units", "Set Precision to 2.") }.SelectMany(x => t.Add(x.Item1, x.Item2)).ToList();
        Assert.Equal(new[] { "Set Name to A.", "On the Text tab:", "Set Height to 3.", "Tick Frame.", "On the Units tab:", "Set Precision to 2." }, lines);
        Assert.Equal("Units", t.Current);
        t.Reset();
        Assert.Equal(new[] { "On the Units tab:", "x" }, t.Add("Units", "x"));
    }

    // ---- a recording with tabs: the plan ---------------------------------------------------------------------------------------

    // A property sheet like DIMSTYLE's Modify: Text height on the Text tab, Precision on the Primary Units tab (both control id 1001), OK from the Primary Units tab
    static readonly string[] Sheet =
    {
        """{"t":100,"n":1,"type":"cmd_start","command":"DIMSTYLE","origin":"typed/other"}""",
        """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
        """{"t":200,"n":3,"type":"modal_title","title":"Dimension Style Manager"}""",
        """{"t":900,"n":4,"type":"dialog_values","for":3,"title":"Dimension Style Manager","source":"win32","fields":[{"key":"tab:Text|id:1001","label":"Text height","kind":"edit","before":"2.5","after":"3.5","tab":"Text"},{"key":"tab:Primary Units|id:1001","label":"Precision","kind":"combo","before":"0.00","after":"0.000","tab":"Primary Units"}],"read":6,"closedWith":"ok","closeTab":"Primary Units"}""",
    };

    [Fact]
    public void The_plan_reads_the_tab_of_each_field_and_the_tab_the_dialog_was_closed_from()
    {
        var dlg = Build(Sheet).Single(s => s.Kind == "dialog");
        Assert.Equal(new[] { "Text", "Primary Units" }, dlg.Values!.Select(v => v.Tab));
        Assert.Equal(new[] { "tab:Text|id:1001", "tab:Primary Units|id:1001" }, dlg.Values!.Select(v => v.Key));
        Assert.Equal("Primary Units", dlg.CloseTab);
    }

    [Fact]
    public void A_recording_without_tabs_reads_with_no_tab_anywhere()
    {
        var dlg = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            """{"t":900,"n":4,"type":"dialog_values","for":3,"title":"Draw grid","source":"win32","fields":[{"key":"id:1","label":"Rows","kind":"edit","before":"3","after":"5"}],"read":4,"closedWith":"ok"}""").Single(s => s.Kind == "dialog");
        Assert.Equal("", dlg.Values!.Single().Tab);
        Assert.Null(dlg.CloseTab);
        var segs = DialogSegments.Of(dlg, closingValues: true);
        Assert.Equal("", segs[^1].Tab);
    }

    [Fact]
    public void The_closing_segment_shows_the_close_tab_for_OK_and_nothing_for_Cancel()
    {
        var dlg = Build(Sheet).Single(s => s.Kind == "dialog");
        Assert.Equal("Primary Units", DialogSegments.Of(dlg, closingValues: true)[^1].Tab);
        Assert.Equal("", DialogSegments.Of(dlg, closingValues: false)[^1].Tab);   // closed with Cancel: the page showing does not matter
    }

    static readonly string[] SheetClicks =
    {
        """{"t":100,"n":1,"type":"cmd_start","command":"ITEMS","origin":"typed/other"}""",
        """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
        """{"t":200,"n":3,"type":"modal_title","title":"Items"}""",
        """{"t":500,"n":4,"type":"dialog_click","for":3,"button":"Add","id":1010,"index":1,"fields":[{"key":"tab:Items|id:1001","label":"Name","kind":"edit","before":"","after":"A","tab":"Items"}],"read":3,"tab":"Items"}""",
        """{"t":900,"n":5,"type":"dialog_values","for":3,"title":"Items","source":"win32","fields":[{"key":"tab:Options|id:1001","label":"Prefix","kind":"edit","before":"","after":"X","tab":"Options"}],"read":3,"closedWith":"ok","closeTab":"Items"}""",
    };

    [Fact]
    public void A_click_carries_the_tab_it_was_made_on_and_becomes_a_segment_that_shows_it()
    {
        var dlg = Build(SheetClicks).Single(s => s.Kind == "dialog");
        Assert.Equal("Items", dlg.Clicks.Single().Tab);
        var segs = DialogSegments.Of(dlg, closingValues: true);
        Assert.Equal(new[] { "Items", "Items" }, segs.Select(s => s.Tab));   // the click, then the close: back on Items for OK, after the value on Options
        Assert.Equal(new[] { "Items", "Options" }, segs.SelectMany(s => s.Values).Select(v => v.Tab));
    }

    [Fact]
    public void A_palette_value_keeps_its_tab()
    {
        var palette = Build(
            """{"t":100,"n":1,"type":"palette_values","palette":"Box","set":"Samples","source":"win32","fields":[{"key":"tab:Size|id:5","label":"Width","kind":"edit","before":"1","after":"2","tab":"Size"}],"read":3}""",
            """{"t":150,"n":2,"type":"palette_click","palette":"Box","set":"Samples","button":"Draw"}""").Single(s => s.Kind == "palette");
        Assert.Equal("Size", palette.Values!.Single().Tab);
    }

    // ---- the messages ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_pause_message_groups_what_is_left_by_tab()
    {
        var dlg = Build(Sheet).Single(s => s.Kind == "dialog");
        var msg = DialogSegments.ManualMessage("Dimension Style Manager", "f.md", 4, dlg);
        Assert.Equal("A dialog opens: Dimension Style Manager. On the Text tab: set Text height to 3.5, on the Primary Units tab: set Precision to 0.000, then close it with OK (see f.md, step 4).", msg);
    }

    [Fact]
    public void The_pause_message_for_a_dialog_with_clicks_names_the_tab_of_the_click()
    {
        var dlg = Build(SheetClicks).Single(s => s.Kind == "dialog");
        var msg = DialogSegments.ManualMessage("Items", "f.md", 4, dlg);
        Assert.Equal("A dialog opens: Items. On the Items tab: set Name to A, press Add, on the Options tab: set Prefix to X, then close it with OK (see f.md, step 4).", msg);
    }

    [Fact]
    public void The_fallback_message_names_the_tab_of_the_values_that_did_not_go_in()
    {
        var values = new List<FieldChange> { C("tab:Text|id:1", "Height", "edit", "3", "Text"), C("tab:Text|id:2", "Frame", "check", "on", "Text"), C("tab:Units|id:1", "Precision", "combo", "0.000", "Units") };
        var results = new List<FieldFillResult> { FieldFillResult.Ok("Height") };
        var msg = FillMatcher.FallbackMessage("Dim", "f.md", 2, values, results, "ok", null);
        Assert.Equal("A dialog opens: Dim. Filled 1 of 3 values; on the Text tab: tick Frame, on the Units tab: set Precision to 0.000 yourself, then close it with OK (see f.md, step 2).", msg);
    }

    [Fact]
    public void A_dialog_without_tabs_reads_in_the_pause_message_as_before()
    {
        var values = new List<FieldChange> { C("id:1", "Rows", "edit", "5"), C("id:2", "Snap", "check", "on") };
        Assert.Equal("A dialog opens: Grid. Filled 0 of 2 values; set Rows to 5, tick Snap yourself, then close it with OK (see f.md, step 1).",
            FillMatcher.FallbackMessage("Grid", "f.md", 1, values, new List<FieldFillResult>(), "ok", null));
    }

    // ---- steps.md ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Steps_md_groups_the_changes_of_a_dialog_by_tab()
    {
        var md = Script(Sheet);
        Assert.Contains("A dialog opens: **Dimension Style Manager**. Make these changes, then confirm:", md);
        Assert.Contains("- On the **Text** tab: set **Text height** to `3.5` (was `2.5`)", md);
        Assert.Contains("- On the **Primary Units** tab: choose `0.000` in **Precision** (was `0.00`)", md);
    }

    [Fact]
    public void Steps_md_puts_the_consecutive_changes_on_one_tab_in_one_line()
    {
        var md = Script(
            """{"t":100,"n":1,"type":"cmd_start","command":"DIMSTYLE","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Dim"}""",
            """{"t":900,"n":4,"type":"dialog_values","for":3,"title":"Dim","source":"win32","fields":[{"key":"tab:Text|id:1","label":"Height","kind":"edit","before":"2","after":"3","tab":"Text"},{"key":"tab:Text|id:2","label":"Draw frame","kind":"check","before":"off","after":"on","tab":"Text"},{"key":"id:9","label":"Name","kind":"edit","before":"","after":"x"}],"read":6,"closedWith":"ok"}""");
        Assert.Contains("- On the **Text** tab: set **Height** to `3` (was `2`), tick **Draw frame**", md);
        Assert.Contains("- Set **Name** to `x`", md);   // a field outside the tabs: a line of its own, as before
    }

    [Fact]
    public void Steps_md_puts_a_click_with_its_changes_on_its_tab()
    {
        var md = Script(SheetClicks);
        Assert.Contains("- On the **Items** tab: set **Name** to `A`, click **Add**", md);
        Assert.Contains("- On the **Options** tab: set **Prefix** to `X`", md);
    }

    [Fact]
    public void Steps_md_of_a_dialog_without_tabs_is_unchanged()
    {
        var md = Script(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            """{"t":500,"n":4,"type":"dialog_click","for":3,"button":"Add","id":1,"index":1,"fields":[{"key":"id:1","label":"Name","kind":"edit","before":"","after":"A"}],"read":3}""",
            """{"t":900,"n":5,"type":"dialog_values","for":3,"title":"Draw grid","source":"win32","fields":[{"key":"id:2","label":"Rows","kind":"edit","before":"3","after":"5"},{"key":"id:3","label":"Snap","kind":"check","before":"off","after":"on"}],"read":4,"closedWith":"ok"}""");
        Assert.Contains("- Set **Name** to `A`, click **Add**", md);
        Assert.Contains("- Set **Rows** to `5` (was `3`)", md);
        Assert.Contains("- Tick **Snap**", md);
        Assert.DoesNotContain(" tab", md);
    }

    // ---- Tdoc ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tdoc_sentences_say_which_tab_each_change_is_on_and_the_tab_the_dialog_is_closed_from()
    {
        var dlg = Build(Sheet).Single(s => s.Kind == "dialog");
        Assert.Equal(new[] { "In the Dimension Style Manager dialog:", "On the Text tab:", "Set Text height to 3.5.", "On the Primary Units tab:", "Set Precision to 0.000.", "Click OK." },
            TdocPlan.Instructions(dlg));
    }

    [Fact]
    public void Tdoc_goes_back_to_the_tab_the_dialog_was_closed_from_before_OK()
    {
        var dlg = Build(SheetClicks).Single(s => s.Kind == "dialog");
        Assert.Equal(new[] { "In the Items dialog:", "On the Items tab:", "Set Name to A.", "Click Add.", "On the Options tab:", "Set Prefix to X.", "On the Items tab:", "Click OK." }, TdocPlan.Instructions(dlg));
    }

    [Fact]
    public void Tdoc_sentences_of_a_dialog_without_tabs_are_unchanged()
    {
        var dlg = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            """{"t":900,"n":4,"type":"dialog_values","for":3,"title":"Draw grid","source":"win32","fields":[{"key":"id:2","label":"Rows","kind":"edit","before":"3","after":"5"}],"read":4,"closedWith":"ok"}""").Single(s => s.Kind == "dialog");
        Assert.Equal(new[] { "In the Draw grid dialog:", "Set Rows to 5.", "Click OK." }, TdocPlan.Instructions(dlg));
    }

    // ---- a tab click in a native palette ---------------------------------------------------------------------------------------

    const string TabClick = """{"t":500,"n":3,"type":"native_click","palette":"Project Manager","set":"Project","control":"tab","action":"click","index":1,"id":1005,"label":"Details","statics":[{"id":1010,"text":"3 drawings"}]}""";

    [Fact]
    public void A_native_tab_click_is_a_palette_step_that_opens_the_tab()
    {
        var step = Build(TabClick).Single();
        Assert.Equal("palette", step.Kind);
        Assert.Equal(("tab", "click", "Details", 1, 1005), (step.Native!.Control, step.Native.Action, step.Native.Label, step.Native.Index, step.Native.Id));
        Assert.Equal("Details", step.Native.Target);
        Assert.Equal("open the Details tab", step.Native.Describe());
        Assert.Equal("Palette \"Project Manager\": open the Details tab", step.Title);
    }

    [Fact]
    public void A_native_tab_click_reads_in_steps_md_as_a_palette_step()
    {
        var md = Script(TabClick);
        Assert.Contains("In the **Project Manager** palette (Project), open the **Details** tab", md);
        Assert.Equal(new[] { "In the Project Manager palette, open the Details tab." }, TdocPlan.Instructions(Build(TabClick).Single()));
    }

    [Fact]
    public void A_native_tab_with_no_label_is_not_a_step_a_replay_could_find()
    {
        Assert.Empty(Build("""{"t":500,"n":3,"type":"native_click","palette":"P","set":"S","control":"tab","action":"click","index":1,"id":5}"""));
    }

    [Fact]
    public void A_native_tab_click_then_a_tree_click_are_two_steps()
    {
        var steps = Build(TabClick,
            """{"t":900,"n":4,"type":"native_click","palette":"Project Manager","set":"Project","control":"tree","action":"click","path":["P","a.dwg"],"index":1,"id":1002}""");
        Assert.Equal(new[] { "tab", "tree" }, steps.Select(s => s.Native!.Control));
    }
}
