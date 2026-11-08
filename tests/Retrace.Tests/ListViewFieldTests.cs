using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>A list view (SysListView32, WinForms ListView) as a dialog field of kind `listview`: the value of a selection, the items, which rows replay selects, keys that stay as
/// they were for the other fields, the JSON, the wording; and the back-to-back dialog rule.</summary>
public class ListViewFieldTests
{
    static readonly string[] Texts = { "Bolts", "Nuts", "Washers", "Bolts", "Screws" };

    // ---- the value -----------------------------------------------------------------------------------------------------

    [Fact]
    public void FromRows_gives_text_row_and_occurrence_in_row_order()
    {
        var items = ListSelection.FromRows(Texts, new[] { 3, 1 });
        Assert.Equal(new[] { new ListItem("Nuts", 1, 1), new ListItem("Bolts", 3, 2) }, items);
        Assert.Equal("Nuts; Bolts (2nd)", ListSelection.Format(items));
    }

    [Fact]
    public void Nothing_selected_is_an_empty_value_and_rows_outside_the_list_are_dropped()
    {
        Assert.Equal("", ListSelection.Format(ListSelection.FromRows(Texts, Array.Empty<int>())));
        Assert.Equal(new[] { new ListItem("Bolts", 0, 1) }, ListSelection.FromRows(Texts, new[] { 0, 9, -1 }));
    }

    [Theory]
    [InlineData("Bolts", "Bolts", 1)]
    [InlineData("Bolts (2nd)", "Bolts", 2)]
    [InlineData("Bolts (3rd)", "Bolts", 3)]
    [InlineData("Bolts (11th)", "Bolts", 11)]
    [InlineData("Bolts (large)", "Bolts (large)", 1)]   // a text that ends in brackets is a text
    [InlineData("Bolts (1st)", "Bolts (1st)", 1)]
    public void Parse_reads_a_value_back_for_a_recording_written_by_hand(string value, string text, int occurrence)
    {
        var item = Assert.Single(ListSelection.Parse(value));
        Assert.Equal(text, item.Text);
        Assert.Equal(occurrence, item.Occurrence);
    }

    [Fact]
    public void ItemsOf_prefers_the_recorded_items_and_falls_back_to_the_after_text()
    {
        var withItems = new FieldChange("id:1010", "Products", "listview", "", "Bolts", "", new[] { new ListItem("Bolts", 3, 2) });
        Assert.Equal(new ListItem("Bolts", 3, 2), Assert.Single(ListSelection.ItemsOf(withItems)));
        var byHand = new FieldChange("id:1010", "Products", "listview", "", "Nuts; Screws");
        Assert.Equal(new[] { "Nuts", "Screws" }, ListSelection.ItemsOf(byHand).Select(i => i.Text));
    }

    // ---- which rows replay selects ----------------------------------------------------------------------------------------

    [Fact]
    public void Plan_finds_each_item_by_text_and_occurrence()
    {
        var rows = ListSelection.Plan(Texts, new[] { new ListItem("Bolts", 3, 2), new ListItem("nuts ", 1, 1) }, out var problem);
        Assert.Null(problem);
        Assert.Equal(new[] { 1, 3 }, rows);
    }

    [Fact]
    public void Plan_follows_the_text_when_the_list_is_in_another_order()
    {
        var sorted = new[] { "Bolts", "Bolts", "Nuts", "Screws", "Washers" };
        Assert.Equal(new[] { 2 }, ListSelection.Plan(sorted, new[] { new ListItem("Nuts", 1, 1) }, out _));
    }

    [Fact]
    public void Plan_names_the_missing_item_and_selects_nothing()
    {
        Assert.Null(ListSelection.Plan(Texts, new[] { new ListItem("Bolts", 0, 1), new ListItem("Hinges", 2, 1) }, out var problem));
        Assert.Equal("no item \"Hinges\" in the list", problem);
    }

    [Fact]
    public void Plan_does_not_take_another_row_when_the_occurrence_is_missing()
    {
        Assert.Null(ListSelection.Plan(Texts, new[] { new ListItem("Screws", 4, 2) }, out var problem));
        Assert.Contains("only 1 row", problem);
        Assert.Contains("2nd", problem);
    }

    [Fact]
    public void Plan_of_no_items_clears_the_selection()
    {
        Assert.Empty(ListSelection.Plan(Texts, Array.Empty<ListItem>(), out var problem)!);
        Assert.Null(problem);
    }

    [Fact]
    public void Changes_touch_only_the_rows_that_differ()
    {
        var (off, on) = ListSelection.Changes(new[] { 0, 1 }, new[] { 1, 3 });
        Assert.Equal(new[] { 0 }, off);
        Assert.Equal(new[] { 3 }, on);
        var (off2, on2) = ListSelection.Changes(new[] { 2 }, new[] { 2 });
        Assert.Empty(off2); Assert.Empty(on2);
    }

    // ---- blank rows, separators, duplicates -------------------------------------------------------------------------------

    [Fact]
    public void A_selected_blank_row_is_not_nothing_selected()
    {
        var texts = new[] { "", "Bolts", "", " " };
        var one = ListSelection.FromRows(texts, new[] { 0 });
        Assert.Equal("(blank)", ListSelection.Format(one));
        Assert.NotEqual(ListSelection.Format(Array.Empty<ListItem>()), ListSelection.Format(one));
        Assert.Equal("(blank) (2nd)", ListSelection.Format(ListSelection.FromRows(texts, new[] { 2 })));   // the occurrence suffix as for any text
        Assert.Equal("(blank)", ListSelection.Parse("(blank)")[0].Text);   // hand-written text is taken as written; a blank row is recorded with its items
        // two blank rows are two selections: the key tells them apart by row
        Assert.NotEqual(ListSelection.Key(ListSelection.FromRows(texts, new[] { 0 })), ListSelection.Key(ListSelection.FromRows(texts, new[] { 2 })));
        Assert.NotEqual(ListSelection.Key(Array.Empty<ListItem>()), ListSelection.Key(one));
    }

    [Fact]
    public void Diff_sees_a_blank_row_selected_after_nothing()
    {
        var none = new DialogField("id:1", "Products", "listview", "", "", Array.Empty<ListItem>());
        var blank = new DialogField("id:1", "Products", "listview", "(blank)", "", new[] { new ListItem("", 0, 1) });
        Assert.Single(DialogFieldsCore.Diff(new[] { none }, new[] { blank }));
    }

    [Fact]
    public void Items_are_the_truth_when_a_text_holds_the_separator_or_a_suffix()
    {
        var texts = new[] { "Nuts; Bolts", "Bolts (2nd)", "Bolts" };
        var items = ListSelection.FromRows(texts, new[] { 0, 1 });
        // the display value is ambiguous but the items are exact: replay and the comparison use them
        Assert.Equal(new[] { 0, 1 }, ListSelection.Plan(texts, items, out _));
        var c = new FieldChange("id:1", "Products", "listview", "", ListSelection.Format(items), "", items);
        Assert.Equal(new[] { "Nuts; Bolts", "Bolts (2nd)" }, ListSelection.ItemsOf(c).Select(i => i.Text));
        Assert.Equal("Select Nuts; Bolts, Bolts (2nd) in Products", DialogFieldsCore.Describe(c));
        Assert.True(FillMatcher.Satisfied(c, ListSelection.Key(items)));
        Assert.False(FillMatcher.Satisfied(c, ListSelection.Key(ListSelection.FromRows(texts, new[] { 0, 2 }))));
    }

    [Fact]
    public void Two_items_that_are_one_row_warn_instead_of_being_dropped_silently()
    {
        var warnings = new List<string>();
        var rows = ListSelection.Plan(Texts, new[] { new ListItem("Nuts", 1, 1), new ListItem("nuts", 7, 1) }, out var problem, warnings);
        Assert.Null(problem);
        Assert.Equal(new[] { 1 }, rows);
        var w = Assert.Single(warnings);
        Assert.Contains("row 1", w);
    }

    [Fact]
    public void The_steps_say_it_was_there_when_the_selection_is_cleared_and_use_the_items()
    {
        string Words(string before, string after, IReadOnlyList<ListItem>? items = null) =>
            StepWording.ChangeText(new RecordingDialogValue("Products", "listview", before, after, "", items), s => s ?? "", s => $"`{s}`", bold: true)!;
        Assert.Equal("Clear the selection in **Products** (was `Bolts`)", Words("Bolts", ""));
        Assert.Equal("Select `Nuts; Bolts`, `Bolts (2nd)` in **Products**", Words("", "x", ListSelection.FromRows(new[] { "Nuts; Bolts", "Bolts (2nd)" }, new[] { 0, 1 })));
    }

    // ---- a list view with a context menu on a tab ---------------------------------------------------------------------------------

    [Fact]
    public void A_context_menu_target_key_finds_the_list_view_field_on_a_tab()
    {
        // the menu target keeps the key replay finds a list view by (`id:1010`, no tab part); the field of the same list view is keyed `tab:Lines|id:1010`
        var target = new FieldChange("id:1010", "Products", "listview", "", "");
        var live = new[] { new LiveField("tab:Lines|id:1010", "Products", "listview", "Lines"), new LiveField("tab:Text|id:1010", "Products", "listview", "Text") };
        Assert.Equal(0, FillMatcher.Match(new[] { target with { Tab = "Lines" } }, live)[0]);
        Assert.Equal(1, FillMatcher.Match(new[] { target with { Tab = "Text" } }, live)[0]);
        // a list view is numbered among list views only: an edit with the same label does not take its place
        Assert.Equal(-1, FillMatcher.Match(new[] { target }, new[] { new LiveField("id:1010", "Products", "edit") })[0]);
    }

    // ---- keys: a list view takes no number of another field ---------------------------------------------------------------------

    [Fact]
    public void A_list_view_is_numbered_among_list_views_so_other_keys_stay_as_before()
    {
        var before = DialogFieldsCore.Build(new[] { new RawControl(0, "Name", "edit", "a"), new RawControl(0, "Rows", "edit", "1") });
        var after = DialogFieldsCore.Build(new[] { new RawControl(0, "Name", "edit", "a"), new RawControl(0, "Name", "listview", "Bolts"), new RawControl(0, "Rows", "edit", "1") });
        Assert.Equal(before.Select(f => f.Key), after.Where(f => f.Kind == "edit").Select(f => f.Key));
        Assert.Equal("label:Name#1~listview", after.Single(f => f.Kind == "listview").Key);   // its own sort numbers it #1, the edit has that key already
    }

    [Fact]
    public void The_value_of_a_long_selection_is_not_cut_and_the_items_are_kept()
    {
        var items = Enumerable.Range(0, 60).Select(i => new ListItem("Product number " + i, i, 1)).ToList();
        var f = Assert.Single(DialogFieldsCore.Build(new[] { new RawControl(1010, "Products", "listview", ListSelection.Format(items), Items: items) }));
        Assert.True(f.Value.Length > DialogFieldsCore.MaxValueLength);
        Assert.Equal(60, f.Items!.Count);
    }

    [Fact]
    public void Diff_reports_a_changed_selection_with_its_items()
    {
        var b = new DialogField("id:1010", "Products", "listview", "", "", null);
        var a = new DialogField("id:1010", "Products", "listview", "Bolts", "", new[] { new ListItem("Bolts", 0, 1) });
        var change = Assert.Single(DialogFieldsCore.Diff(new[] { b }, new[] { a }));
        Assert.Equal("Bolts", change.After);
        Assert.Equal("Bolts", change.Items![0].Text);
        Assert.Empty(DialogFieldsCore.Diff(new[] { a }, new[] { a }));
    }

    [Fact]
    public void FillMatcher_matches_a_list_view_only_with_a_list_view()
    {
        var recorded = new[] { new FieldChange("id:1010", "Products", "listview", "", "Bolts") };
        var live = new[] { new LiveField("id:1010", "Products", "edit"), new LiveField("id:1011", "Products", "listview") };
        Assert.Equal(1, FillMatcher.Match(recorded, live)[0]);
        Assert.Equal(-1, FillMatcher.Match(recorded, new[] { new LiveField("id:1010", "Products", "edit") })[0]);
    }

    [Fact]
    public void FillMatcher_compares_the_selection_by_its_items()
    {
        var items = new[] { new ListItem("Nuts", 1, 1), new ListItem("Bolts", 3, 2) };
        var c = new FieldChange("id:1010", "Products", "listview", "", ListSelection.Format(items), "", items);
        // the live value is the key of the live items: row and order do not matter, text (any case) and occurrence do
        Assert.True(FillMatcher.Satisfied(c, ListSelection.Key(new[] { new ListItem("bolts", 0, 2), new ListItem("NUTS", 5, 1) })));
        Assert.False(FillMatcher.Satisfied(c, ListSelection.Key(new[] { new ListItem("Nuts", 1, 1), new ListItem("Bolts", 0, 1) })));
        Assert.False(FillMatcher.Satisfied(c, null));
    }

    // ---- the recording and the words -----------------------------------------------------------------------------------------

    [Fact]
    public void Items_round_trip_through_the_recording_json()
    {
        var items = new[] { new ListItem("Bolts", 3, 2), new ListItem("Nuts", 1, 1) };
        var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["key"] = "id:1010", ["label"] = "Products", ["kind"] = "listview", ["before"] = "", ["after"] = ListSelection.Format(items), ["items"] = ListSelection.ToJson(items) });
        using var doc = JsonDocument.Parse("[" + json + "]");
        var back = Assert.Single(DialogNesting.FieldsOf(doc.RootElement));
        Assert.Equal("Nuts; Bolts (2nd)", back.After);
        Assert.Equal(new[] { new ListItem("Bolts", 3, 2), new ListItem("Nuts", 1, 1) }, back.Items);
    }

    [Fact]
    public void A_field_without_items_reads_as_before()
    {
        using var doc = JsonDocument.Parse("""[{"key":"id:5","label":"Rows","kind":"edit","before":"1","after":"2"}]""");
        Assert.Null(Assert.Single(DialogNesting.FieldsOf(doc.RootElement)).Items);
    }

    [Fact]
    public void Steps_say_select_item_in_list()
    {
        string Words(string before, string after) => StepWording.ChangeText(new RecordingDialogValue("Products", "listview", before, after, ""), s => s ?? "", s => $"`{s}`", bold: true)!;
        Assert.Equal("Select `Bolts` in **Products**", Words("", "Bolts"));
        Assert.Equal("Select `Bolts`, `Nuts (2nd)` in **Products**", Words("", "Bolts; Nuts (2nd)"));
        Assert.Equal("Select `Nuts` in **Products** (was `Bolts`)", Words("Bolts", "Nuts"));
        Assert.Equal("Clear the selection in **Products** (was `Bolts`)", Words("Bolts", ""));
    }

    [Fact]
    public void The_pause_message_and_the_tdoc_legend_say_it()
    {
        var c = new FieldChange("id:1010", "Products", "listview", "", "Bolts; Nuts");
        Assert.Equal("Select Bolts, Nuts in Products", DialogFieldsCore.Describe(c));
        Assert.Equal("Products: Bolts; Nuts", TdocPlan.FieldCallout(c));
        Assert.Equal("Clear the selection in Products", DialogFieldsCore.Describe(c with { After = "" }));
    }

    // ---- back-to-back dialogs ----------------------------------------------------------------------------------------------

    static Step Dialog(string title) => new() { Kind = "dialog", DialogTitle = title };

    [Fact]
    public void IsNext_needs_a_modal_in_front_with_the_next_dialogs_title()
    {
        var next = Dialog("Order details");
        Assert.True(DialogChain.IsNext(1, next, "Order details"));
        Assert.True(DialogChain.IsNext(2, next, "order DETAILS"));
        Assert.False(DialogChain.IsNext(0, next, "Order details"));   // no modal open
        Assert.False(DialogChain.IsNext(1, next, "Warning"));          // an unexpected box in front keeps the wait
        Assert.False(DialogChain.IsNext(1, next, null));               // nothing in front, or AutoCAD's own window (the caller passes null)
        Assert.False(DialogChain.IsNext(1, null, "Order details"));
        Assert.False(DialogChain.IsNext(1, new Step { Kind = "command" }, "Order details"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsNext_takes_any_modal_in_front_for_a_dialog_recorded_without_a_title(string? recorded)
    {
        Assert.True(DialogChain.IsNext(1, Dialog(recorded!), "Properties"));
        Assert.False(DialogChain.IsNext(0, Dialog(recorded!), "Properties"));
        Assert.False(DialogChain.IsNext(1, Dialog(recorded!), null));
    }

    // ---- a selection that cannot be read, sorted lists, duplicates, hand-written blanks -------------------------------------------------

    [Fact]
    public void An_unreadable_selection_is_not_the_one_read_before()
    {
        var before = new DialogField("id:1", "Products", "listview", "", "", Array.Empty<ListItem>());
        var read = new DialogField("id:1", "Products", "listview", "Nuts", "", new[] { new ListItem("Nuts", 1, 1) });
        var unreadable = new DialogField("id:1", "Products", "listview", ListSelection.NotRecorded, "", null);
        var t = new FieldTracker();
        t.Observe(new[] { before }); t.Observe(new[] { read }); t.Observe(new[] { unreadable });
        var change = Assert.Single(t.Changes());
        Assert.Equal(ListSelection.NotRecorded, change.After);   // not "Nuts": a stale earlier selection is never recorded
        Assert.Null(change.Items);
        Assert.Equal("Select the items in Products yourself (the selection could not be recorded)", DialogFieldsCore.Describe(change));
        // nothing readable from the start and nothing after: no change at all
        var t2 = new FieldTracker();
        t2.Observe(new[] { unreadable }); t2.Observe(new[] { unreadable });
        Assert.Empty(t2.Changes());
    }

    [Fact]
    public void A_list_view_value_that_could_not_be_recorded_has_wording_and_is_not_applied()
    {
        string Words(string after) => StepWording.ChangeText(new RecordingDialogValue("Products", "listview", "", after, ""), s => s ?? "", s => $"`{s}`", bold: true)!;
        Assert.Equal("Select the items in **Products** yourself (the selection could not be recorded)", Words(ListSelection.NotRecorded));
        Assert.True(ListSelection.IsNotRecorded(ListSelection.NotRecorded));
        Assert.False(ListSelection.IsNotRecorded("Nuts"));
    }

    [Fact]
    public void A_sorted_or_inserted_row_is_no_change_but_another_item_or_occurrence_is()
    {
        var first = new DialogField("id:1", "Products", "listview", "Nuts", "", new[] { new ListItem("Nuts", 1, 1), new ListItem("Bolts", 3, 2) });
        var moved = new DialogField("id:1", "Products", "listview", "Bolts (2nd); Nuts", "", new[] { new ListItem("Nuts", 2, 1), new ListItem("Bolts", 5, 2) });   // the list was sorted, rows were inserted
        Assert.Empty(DialogFieldsCore.Diff(new[] { first }, new[] { moved }));
        var other = first with { Items = new[] { new ListItem("Nuts", 1, 1), new ListItem("Bolts", 3, 1) } };
        Assert.Single(DialogFieldsCore.Diff(new[] { first }, new[] { other }));
        // a blank item is told by its row
        var blank1 = first with { Items = new[] { new ListItem("", 0, 1) } };
        var blank2 = first with { Items = new[] { new ListItem("", 2, 2) } };
        Assert.Single(DialogFieldsCore.Diff(new[] { blank1 }, new[] { blank2 }));
    }

    [Fact]
    public void Two_recorded_items_that_are_one_row_verify_against_the_set()
    {
        var items = new[] { new ListItem("Nuts", 1, 1), new ListItem("nuts", 7, 1) };
        var c = new FieldChange("id:1", "Products", "listview", "", ListSelection.Format(items), "", items);
        // the replay selects row 1 once; the live selection is one item: that is what was recorded
        Assert.True(FillMatcher.Satisfied(c, ListSelection.Key(new[] { new ListItem("Nuts", 1, 1) })));
    }

    [Fact]
    public void A_blank_item_without_its_row_stops_for_a_person()
    {
        var items = new[] { new ListItem("", -1, 1), new ListItem("Bolts", 1, 1) };
        Assert.Null(ListSelection.Plan(new[] { "", "Bolts" }, items, out var problem));
        Assert.Contains("blank item has no row", problem);
        Assert.Equal(new[] { 0, 1 }, ListSelection.Plan(new[] { "", "Bolts" }, new[] { new ListItem("", 0, 1), new ListItem("Bolts", 1, 1) }, out _));
        // texted items of a hand-written value are found by text and occurrence
        Assert.Equal(new[] { 1 }, ListSelection.Plan(new[] { "", "Bolts" }, ListSelection.Parse("Bolts"), out _));
    }

    [Fact]
    public void A_row_really_named_blank_is_a_row_named_blank()
    {
        using var doc = JsonDocument.Parse("""{"items":[{"text":"(blank)","row":2,"occurrence":1},{"text":"","row":3,"occurrence":1}]}""");
        var items = ListSelection.ItemsFromJson(doc.RootElement)!;
        Assert.Equal("(blank)", items[0].Text);   // not mapped to a blank row
        Assert.Equal("", items[1].Text);
        Assert.Equal(new[] { 2, 3 }, ListSelection.Plan(new[] { "x", "x", "(blank)", "" }, items, out _));
    }

    [Fact]
    public void A_list_view_whose_key_is_taken_by_another_field_is_told_by_its_kind()
    {
        var f = DialogFieldsCore.Build(new[]
        {
            new RawControl(0, "Products", "listview", "Nuts"),   // before the edit: the edit keeps its key all the same
            new RawControl(0, "Products", "edit", "a"),
            new RawControl(0, "Products", "listview", "Bolts"),
            new RawControl(1002, "Rows", "edit", "1"),
        });
        Assert.Equal("label:Products#1~listview", f[0].Key);
        Assert.Equal("label:Products#1", f[1].Key);                // as it always was
        Assert.Equal("label:Products#2", f[2].Key);                // numbered among list views: no collision
        Assert.Equal("id:1002", f[3].Key);
        Assert.Equal(4, f.Select(x => x.Key).Distinct().Count());
        // without a collision the list view's key is the plain one
        Assert.Equal("label:Products#1", DialogFieldsCore.Build(new[] { new RawControl(0, "Products", "listview", "Nuts") })[0].Key);
    }

    [Fact]
    public void Two_sequential_dialogs_are_two_dialog_steps()
    {
        var events = new[]
        {
            """{"t":10,"n":1,"type":"modal_enter","title":"Catalog","windowClass":"AfxFrameOrView140u"}""",
            """{"t":20,"n":2,"type":"dialog_click","for":1,"button":"Add","id":1026,"index":1,"fields":[{"key":"id:1010","label":"Products","kind":"listview","before":"","after":"Bolts","items":[{"text":"Bolts","row":0,"occurrence":1}]}],"read":2}""",
            """{"t":30,"n":3,"type":"dialog_values","for":1,"closedWith":"click","fields":[],"read":2}""",
            """{"t":31,"n":4,"type":"modal_leave"}""",
            """{"t":40,"n":5,"type":"modal_enter","title":"Order","windowClass":"#32770"}""",
            """{"t":50,"n":6,"type":"dialog_values","for":5,"closedWith":"ok","fields":[{"key":"id:1001","label":"Qty","kind":"edit","before":"1","after":"4"}],"read":3}""",
            """{"t":51,"n":7,"type":"modal_leave"}""",
        };
        var docs = events.Select(e => JsonDocument.Parse(e)).ToList();
        try
        {
            var steps = Plan.Build(docs.Select(d => d.RootElement)).Where(s => s.Kind == "dialog").ToList();
            Assert.Equal(new[] { "Catalog", "Order" }, steps.Select(s => s.DialogTitle));
            var click = Assert.Single(steps[0].Clicks);
            Assert.Equal("Add", click.Button);
            var field = Assert.Single(click.Values);
            Assert.Equal("listview", field.Kind);
            Assert.Equal("Bolts", field.Items![0].Text);
            Assert.Equal("click", steps[0].ClosedWith);
            Assert.Equal("4", Assert.Single(steps[1].Values!).After);
        }
        finally { foreach (var d in docs) d.Dispose(); }
    }
}
