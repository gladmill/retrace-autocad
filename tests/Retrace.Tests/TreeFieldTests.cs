using Retrace.Core;
using Retrace.Engine.Recordings;
using Retrace.Engine.Emit;

namespace Retrace.Tests;

/// <summary>The tree field of a dialog or palette (a Win32 SysTreeView32, WinForms TreeView): the path of the selected item, comparing two paths,
/// finding a path in a tree that fills its nodes when they are opened, the wording, and the way a tree is matched to a recorded change.</summary>
public class TreeFieldTests
{
    // ---- the path -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Join_puts_the_levels_in_one_path_and_nothing_selected_is_empty()
    {
        Assert.Equal("Parts › Fasteners › Bolts", TreePath.Join(new[] { "Parts", "Fasteners", "Bolts" }));
        Assert.Equal("Parts", TreePath.Join(new[] { " Parts " }));
        Assert.Equal("", TreePath.Join(Array.Empty<string>()));
        Assert.Equal("A › ", TreePath.Join(new[] { "A", null }));
    }

    [Fact]
    public void Split_is_the_inverse_of_Join_and_trims_each_level()
    {
        Assert.Equal(new[] { "Parts", "Fasteners", "Bolts" }, TreePath.Split("Parts › Fasteners › Bolts"));
        Assert.Equal(new[] { "Parts", "Bolts" }, TreePath.Split("  Parts  ›   Bolts "));
        Assert.Equal(new[] { "Single" }, TreePath.Split("Single"));
        Assert.Empty(TreePath.Split(""));
        Assert.Empty(TreePath.Split("   "));
        Assert.Empty(TreePath.Split(null));
    }

    [Theory]
    [InlineData("A › B", "A › B", true)]
    [InlineData("A › B", "a › b", true)]
    [InlineData("A › B", " A  ›  B ", true)]
    [InlineData("A › B", "A", false)]
    [InlineData("A", "A › B", false)]
    [InlineData("A › B", "B › A", false)]
    [InlineData("A › B", "A › C", false)]
    [InlineData("", "", true)]
    [InlineData("", "A", false)]
    [InlineData("A", "", false)]
    [InlineData("AB", "A › B", false)]   // a path is compared level by level, not as text
    public void Same_compares_level_by_level_trimmed_and_ignoring_case(string a, string b, bool same) =>
        Assert.Equal(same, TreePath.Same(a, b));

    [Fact]
    public void FillMatcher_compares_a_tree_by_its_levels()
    {
        Assert.True(FillMatcher.ValueMatches("tree", "Parts › Bolts", "parts  ›  BOLTS"));
        Assert.False(FillMatcher.ValueMatches("tree", "Parts › Bolts", "Parts › Nuts"));
        Assert.True(FillMatcher.ValueMatches("tree", "", ""));
        // numbers in a tree are texts: "1.0" is not "1"
        Assert.False(FillMatcher.ValueMatches("tree", "Rev 1.0", "Rev 1"));
        var c = new FieldChange("name:PartsTree", "Parts", "tree", "Parts", "Parts › Fasteners › Bolts");
        Assert.True(FillMatcher.Satisfied(c, "parts › fasteners › bolts"));
        Assert.False(FillMatcher.Satisfied(c, "Parts › Fasteners"));
        Assert.False(FillMatcher.Satisfied(c, null));
    }

    // ---- finding a path ---------------------------------------------------------------------------------------------------

    /// <summary>A tree as a dictionary from a node to its children; "" is the top.</summary>
    static readonly Dictionary<string, string[]> Nodes = new()
    {
        [""] = new[] { "Parts", "Tools" },
        ["Parts"] = new[] { "Fasteners", "Frames" },
        ["Parts/Fasteners"] = new[] { "Bolts", "Nuts" },
        ["Tools"] = new[] { "Fasteners" },   // the same name under another parent
    };

    static IEnumerable<string> Kids(string node) => Nodes.TryGetValue(node, out var k) ? k.Select(x => node.Length == 0 ? x : node + "/" + x) : Array.Empty<string>();
    static string Leaf(string node) => node[(node.LastIndexOf('/') + 1)..];

    [Fact]
    public void Find_follows_the_levels_down_from_the_top()
    {
        var found = TreePath.Find(new[] { "Parts", "Fasteners", "Nuts" }, "", Kids, Leaf);
        Assert.Null(found.Problem);
        Assert.Equal(new[] { "Parts", "Parts/Fasteners", "Parts/Fasteners/Nuts" }, found.Items);
    }

    [Fact]
    public void Find_matches_each_level_trimmed_and_ignoring_case_and_takes_the_one_under_the_right_parent()
    {
        var found = TreePath.Find(new[] { " tools ", "FASTENERS" }, "", Kids, Leaf);
        Assert.Null(found.Problem);
        Assert.Equal(new[] { "Tools", "Tools/Fasteners" }, found.Items);
    }

    [Fact]
    public void Find_says_which_level_is_missing()
    {
        var top = TreePath.Find(new[] { "Nope" }, "", Kids, Leaf);
        Assert.Equal("no item Nope in the tree", top.Problem);
        Assert.Empty(top.Items);
        var deeper = TreePath.Find(new[] { "Parts", "Fasteners", "Screws" }, "", Kids, Leaf);
        Assert.Equal("no item Screws under Parts › Fasteners in the tree", deeper.Problem);
        Assert.Equal(new[] { "Parts", "Parts/Fasteners" }, deeper.Items);
    }

    [Fact]
    public void Find_with_no_levels_finds_nothing_and_no_problem()
    {
        var found = TreePath.Find(Array.Empty<string>(), "", Kids, Leaf);
        Assert.Null(found.Problem);
        Assert.Empty(found.Items);
    }

    [Fact]
    public void Find_opens_each_parent_before_it_lists_the_children_but_not_the_top_or_the_last_item()
    {
        // a tree that fills a node when it is opened (a WinForms BeforeExpand): the children are not there before
        var filled = new HashSet<string>();
        var opened = new List<string>();
        IEnumerable<string> LazyKids(string node) => filled.Contains(node) ? Kids(node) : Array.Empty<string>();
        filled.Add("");
        var found = TreePath.Find(new[] { "Parts", "Fasteners", "Bolts" }, "", LazyKids, Leaf, p => { opened.Add(p); filled.Add(p); });
        Assert.Null(found.Problem);
        Assert.Equal(new[] { "Parts", "Parts/Fasteners" }, opened);
        Assert.Equal("Parts/Fasteners/Bolts", found.Items[^1]);
        // without opening, the second level is not found
        var closed = TreePath.Find(new[] { "Parts", "Fasteners" }, "", node => node.Length == 0 ? Kids(node) : Array.Empty<string>(), Leaf);
        Assert.Equal("no item Fasteners under Parts in the tree", closed.Problem);
    }

    [Fact]
    public void Find_stops_at_the_depth_limit()
    {
        var deep = Enumerable.Repeat("x", TreePath.MaxDepth + 1).ToList();
        var found = TreePath.Find(deep, 0, n => new[] { n + 1 }, n => "x");
        Assert.NotNull(found.Problem);
        Assert.Equal(TreePath.MaxDepth, found.Items.Count);
    }

    // ---- wording ---------------------------------------------------------------------------------------------------------

    static string Words(RecordingDialogValue v) => StepWording.ChangeText(v, s => s ?? "", s => $"`{s}`", bold: true)!;

    [Fact]
    public void A_tree_reads_as_a_selection_in_the_steps()
    {
        Assert.Equal("Select `Parts › Fasteners` in **Catalog** (was `Parts`)", Words(new RecordingDialogValue("Catalog", "tree", "Parts", "Parts › Fasteners")));
        Assert.Equal("Select `Parts` in **Catalog**", Words(new RecordingDialogValue("Catalog", "tree", "", "Parts")));
        Assert.Equal("Clear the selection in **Catalog** (was `Parts`)", Words(new RecordingDialogValue("Catalog", "tree", "Parts", "")));
    }

    [Fact]
    public void A_tree_is_described_for_the_pause_message()
    {
        Assert.Equal("Select A › B in Catalog", DialogFieldsCore.Describe(new FieldChange("id:7", "Catalog", "tree", "A", "A › B")));
        Assert.Equal("Clear the selection in Catalog", DialogFieldsCore.Describe(new FieldChange("id:7", "Catalog", "tree", "A", "")));
        var results = new List<FieldFillResult> { FieldFillResult.Missing("Catalog") };
        Assert.Equal("A dialog opens: Parts. Filled 0 of 1 values; select A › B in Catalog yourself, then close it with OK.",
            FillMatcher.FallbackMessage("Parts", null, null, new List<FieldChange> { new("id:7", "Catalog", "tree", "A", "A › B") }, results, "ok", null));
    }

    [Fact]
    public void A_tree_has_a_legend_line_in_a_training_doc()
    {
        Assert.Equal("Catalog: Parts › Fasteners", TdocPlan.FieldCallout(new FieldChange("id:7", "Catalog", "tree", "", "Parts › Fasteners")));
    }

    // ---- matching a recorded tree to the live one ------------------------------------------------------------------------

    [Fact]
    public void A_recorded_tree_finds_the_live_tree_by_name_then_by_label_and_never_a_field_of_another_kind()
    {
        var rec = new[] { new FieldChange("name:PartsTree", "Catalog", "tree", "", "Parts › Bolts") };
        // WinForms: the control's own name decides
        Assert.Equal(new[] { 1 }, FillMatcher.Match(rec, new[] { new LiveField("name:Other", "Other", "edit"), new LiveField("name:PartsTree", "Catalog", "tree") }));
        // a Win32 dialog: by label, whatever the window id was
        var byId = new[] { new FieldChange("id:1007", "Catalog", "tree", "", "Parts") };
        Assert.Equal(new[] { 0 }, FillMatcher.Match(byId, new[] { new LiveField("id:2001", "Catalog", "tree") }));
        // a list box with the same label is not a tree
        Assert.Equal(new[] { -1 }, FillMatcher.Match(byId, new[] { new LiveField("id:1007", "Catalog", "list") }));
    }

    [Fact]
    public void A_tree_is_a_field_that_is_read_and_keyed_like_any_other()
    {
        var raw = new List<RawControl> { new(1007, "Catalog", "tree", "Parts › Bolts"), new(1008, "Rows", "edit", "3") };
        var fields = DialogFieldsCore.Build(raw);
        Assert.Equal(new DialogField("id:1007", "Catalog", "tree", "Parts › Bolts"), fields[0]);
        var named = DialogFieldsCore.Build(new List<RawControl> { new(0xFFFF, "Catalog", "tree", "A", Name: "PartsTree") });
        Assert.Equal("name:PartsTree", named[0].Key);
    }

    // ---- old recordings keep their keys ------------------------------------------------------------------------------------

    static List<string> Keys(params RawControl[] raw) => DialogFieldsCore.Build(raw).Select(f => f.Key).ToList();

    [Fact]
    public void A_tree_does_not_change_the_keys_of_the_other_fields()
    {
        // a list and a tree both labelled "Items": the list is keyed exactly as it was before trees were read, the tree is numbered among trees
        var list = new RawControl(0, "Items", "list", "A");
        var tree = new RawControl(0, "Items", "tree", "X");
        Assert.Equal(new[] { "label:Items#1" }, Keys(list));
        Assert.Equal(new[] { "label:Items#1", "label:Items#1" }, Keys(list, tree));
        Assert.Equal(new[] { "label:Items#1", "label:Items#1" }, Keys(tree, list));
        // two edits with the label and a tree before them: the edits are still #1 and #2
        var edit = new RawControl(0, "Items", "edit", "");
        Assert.Equal(new[] { "label:Items#1", "label:Items#1", "label:Items#2" }, Keys(tree, edit, edit));
        Assert.Equal(new[] { "label:Items#1", "label:Items#2" }, Keys(edit, edit));
        // two trees are numbered among themselves
        Assert.Equal(new[] { "label:Items#1", "label:Items#2", "label:Items#1" }, Keys(tree, tree, list));
    }

    [Fact]
    public void A_tree_does_not_take_the_id_or_the_name_of_another_field()
    {
        // the same id (a dialog that reuses one) or WinForms Name on a tree and on another field: the other keeps its id: / name: key
        Assert.Equal(new[] { "id:1007", "id:1007" }, Keys(new RawControl(1007, "Rows", "edit", "3"), new RawControl(1007, "Catalog", "tree", "A")));
        var named = DialogFieldsCore.Build(new List<RawControl> { new(0xFFFF, "Rows", "edit", "3", Name: "X"), new(0xFFFF, "Catalog", "tree", "A", Name: "X") });
        Assert.Equal(new[] { "name:X", "name:X" }, named.Select(f => f.Key));
    }

    [Fact]
    public void A_tree_does_not_make_the_label_of_another_field_a_repeated_one_that_takes_a_group_box_title()
    {
        var raw = new List<RawControl> { new(1, "Precision", "combo", "0.0"), new(2, "Precision", "tree", "A") };
        var rects = new List<Box> { new(20, 60, 120, 80), new(220, 60, 320, 80) };
        var groups = new List<(Box, string)> { (new Box(10, 0, 400, 100), "Length") };
        Assert.Equal(new[] { "Precision", "Precision" }, DialogFieldsCore.Qualify(raw, rects, groups).Select(r => r.Label));
        // two trees with the label are repeated, among themselves
        var two = new List<RawControl> { new(2, "Precision", "tree", "A"), new(3, "Precision", "tree", "B"), new(1, "Precision", "combo", "0.0") };
        var rects3 = new List<Box> { new(20, 60, 120, 80), new(220, 60, 320, 80), new(20, 20, 60, 40) };
        Assert.Equal(new[] { "Length › Precision", "Length › Precision", "Precision" }, DialogFieldsCore.Qualify(two, rects3, groups).Select(r => r.Label));
    }

    [Fact]
    public void An_older_recording_still_finds_its_list_when_the_dialog_has_a_tree_with_the_same_label()
    {
        var oldList = new[] { new FieldChange("label:Items#1", "Items", "list", "A", "B") };
        var live = new[] { new LiveField("label:Items#1", "Items", "tree"), new LiveField("label:Items#1", "Items", "list") };
        Assert.Equal(new[] { 1 }, FillMatcher.Match(oldList, live));
        var tree = new[] { new FieldChange("label:Items#1", "Items", "tree", "", "A › B") };
        Assert.Equal(new[] { 0 }, FillMatcher.Match(tree, live));
        // the second edit of an old recording is still the second field that is not a tree
        var second = new[] { new FieldChange("label:Items#2", "Items", "edit", "", "x") };
        var live2 = new[] { new LiveField("label:Items#1", "Items", "edit"), new LiveField("label:Items#1", "Items", "tree"), new LiveField("label:Items#2", "Items", "edit") };
        Assert.Equal(new[] { 2 }, FillMatcher.Match(second, live2));
    }

    [Fact]
    public void A_changed_selection_is_a_change_and_an_unchanged_one_is_not()
    {
        var t = new FieldTracker();
        t.Observe(new[] { new DialogField("id:1", "Catalog", "tree", "Parts") });
        t.Observe(new[] { new DialogField("id:1", "Catalog", "tree", "Parts › Fasteners › Bolts") });
        var ch = Assert.Single(t.Changes());
        Assert.Equal(("tree", "Parts", "Parts › Fasteners › Bolts"), (ch.Kind, ch.Before, ch.After));
        var same = new FieldTracker();
        same.Observe(new[] { new DialogField("id:1", "Catalog", "tree", "Parts") });
        same.Observe(new[] { new DialogField("id:1", "Catalog", "tree", "Parts") });
        Assert.Empty(same.Changes());
    }

    [Fact]
    public void A_value_longer_than_the_cap_is_cut_like_any_other()
    {
        var long1 = string.Join(TreePath.Separator, Enumerable.Repeat(new string('x', 60), 5));
        var f = DialogFieldsCore.Build(new List<RawControl> { new(5, "Catalog", "tree", long1) })[0];
        Assert.Equal(DialogFieldsCore.MaxValueLength, f.Value.Length);
    }

    [Fact]
    public void A_dialog_with_nothing_but_a_tree_is_a_dialog_with_a_field_not_a_message_box()
    {
        // read > 0: nothing changed and OK pressed is Ok (press OK), not MessageOk (which a person must confirm has no fields)
        Assert.Equal(AutoMode.Ok, FillMatcher.Decide(true, 0, 1, "ok"));
        Assert.Equal(AutoMode.Ok, FillMatcher.Decide(true, 1, 1, "ok"));
        // a recording made before trees were read (read 0) stays a message box recording; the replay still hands a dialog that has fields to a person
        Assert.Equal(AutoMode.MessageOk, FillMatcher.Decide(true, 0, 0, "ok"));
    }
}
