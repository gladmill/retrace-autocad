using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Tree check boxes (`treecheck` fields), a double-click on a tree item (`dialog_click` with `kind: "dblclick"`), the rule for a double-click that only opened a node, and sub forms of
/// sub forms to any depth: the pure parts, the replay plan, the segments, the wording. Synthetic data only.</summary>
public class SubFormTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-subforms").FullName;
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

    // ---- the keys and the walk -------------------------------------------------------------------------------------------

    [Fact]
    public void A_tick_key_and_label_hold_the_tree_and_the_item_and_parse_back()
    {
        var key = TreeChecks.KeyOf("tab:Lines|id:1001", "Parts › Fasteners");
        Assert.Equal("tab:Lines|id:1001|check:Parts › Fasteners", key);
        Assert.True(TreeChecks.TryParseKey(key, out var tree, out var path));
        Assert.Equal(("tab:Lines|id:1001", "Parts › Fasteners"), (tree, path));
        Assert.False(TreeChecks.TryParseKey("id:1001", out _, out _));
        var label = TreeChecks.LabelOf("Layers", "Parts › Fasteners");
        Assert.Equal(("Layers", "Parts › Fasteners"), TreeChecks.SplitLabel(label));
        var change = new FieldChange(key, label, TreeChecks.Kind, "off", "on", "Lines");
        Assert.Equal("Parts › Fasteners", TreeChecks.PathOf(change));
        var asTree = TreeChecks.TreeOf(change);
        Assert.Equal(("tab:Lines|id:1001", "Layers", "tree", "Lines"), (asTree.Key, asTree.Label, asTree.Kind, asTree.Tab));
    }

    [Fact]
    public void State_images_are_on_off_or_no_check_box()
    {
        Assert.Equal("on", TreeChecks.ValueOf(2));
        Assert.Equal("off", TreeChecks.ValueOf(1));
        Assert.Null(TreeChecks.ValueOf(0));
        Assert.Null(TreeChecks.ValueOf(3));
    }

    sealed record Node(string Text, string? State, params Node[] Kids);

    static List<(string Path, string Value)> Collect(params Node[] top)
    {
        var root = new Node("", null, top);
        return TreeChecks.Collect(root, n => n.Kids, n => n.Text, n => n.State);
    }

    [Fact]
    public void Collect_lists_every_item_with_its_path_depth_first_and_skips_items_without_a_box()
    {
        var list = Collect(
            new Node("Site", "off", new Node("Walls", "on"), new Node("Doors", "off")),
            new Node("Note", null, new Node("Inner", "on")),
            new Node("Plant", "off"));
        Assert.Equal(new[] { ("Site", "off"), ("Site › Walls", "on"), ("Site › Doors", "off"), ("Note › Inner", "on"), ("Plant", "off") }, list);
    }

    [Fact]
    public void Collect_lists_a_path_once_and_stops_at_the_cap()
    {
        Assert.Equal(new[] { ("A", "on") }, Collect(new Node("A", "on"), new Node(" a ", "off")));
        var many = Enumerable.Range(0, TreeChecks.MaxItems + 500).Select(i => new Node("n" + i, "off")).ToArray();
        Assert.Equal(TreeChecks.MaxItems, Collect(many).Count);
    }

    // ---- what is recorded: only the items the person changed ---------------------------------------------------------------

    [Fact]
    public void Only_items_the_person_changed_are_changes_and_an_item_that_appears_later_takes_its_first_state_as_baseline()
    {
        DialogField Tick(string path, string v) => new(TreeChecks.KeyOf("name:CheckTree", path), TreeChecks.LabelOf("Layers", path), TreeChecks.Kind, v);
        var t = new FieldTracker();
        t.Observe(new[] { Tick("Site", "off"), Tick("Site › Walls", "on"), Tick("Plant", "off") });
        // Plant is opened: its (lazily filled) children show up, one of them is ticked
        t.Observe(new[] { Tick("Site", "off"), Tick("Site › Walls", "on"), Tick("Plant", "off"), Tick("Plant › Pumps", "off"), Tick("Plant › Valves", "off") });
        t.Observe(new[] { Tick("Site", "on"), Tick("Site › Walls", "on"), Tick("Plant", "off"), Tick("Plant › Pumps", "on"), Tick("Plant › Valves", "off") });
        var changes = t.Changes();
        Assert.Equal(new[] { ("Site", "off", "on"), ("Plant › Pumps", "off", "on") }, changes.Select(c => (TreeChecks.PathOf(c), c.Before, c.After)));
        Assert.All(changes, c => Assert.Equal("treecheck", c.Kind));
    }

    [Fact]
    public void A_long_selection_is_never_cut_because_each_item_is_its_own_value()
    {
        var path = string.Join(" › ", Enumerable.Range(0, 30).Select(i => "Level" + i));
        Assert.True(path.Length > DialogFieldsCore.MaxValueLength);
        var f = new DialogField(TreeChecks.KeyOf("id:1", path), TreeChecks.LabelOf("Layers", path), TreeChecks.Kind, "on");
        Assert.Equal("on", f.Value);
        Assert.Equal(path, TreeChecks.PathOf(new FieldChange(f.Key, f.Label, f.Kind, "off", "on")));
    }

    // ---- finding the tree of a tick ----------------------------------------------------------------------------------------

    [Fact]
    public void A_tick_is_set_through_its_tree_found_by_name_or_by_label()
    {
        var live = new List<LiveField>
        {
            new("name:ItemTree", "Items", "tree"), new("name:CheckTree", "Layers", "tree"),
            new("name:CheckTree|check:Site", "Layers ¦ Site", TreeChecks.Kind),   // the ticks the scan lists are no tree
        };
        var byName = new FieldChange(TreeChecks.KeyOf("name:CheckTree", "Plant › Pumps"), TreeChecks.LabelOf("Layers", "Plant › Pumps"), TreeChecks.Kind, "off", "on");
        Assert.Equal(1, FillMatcher.Match(new[] { byName }, live)[0]);
        // WinForms ids change between runs: the id of the recording does not name the live tree, the label does
        var byLabel = new FieldChange(TreeChecks.KeyOf("id:918556", "Plant › Pumps"), TreeChecks.LabelOf("Layers", "Plant › Pumps"), TreeChecks.Kind, "off", "on");
        var liveById = new List<LiveField> { new("id:1234", "Items", "tree"), new("id:5678", "Layers", "tree") };
        Assert.Equal(1, FillMatcher.Match(new[] { byLabel }, liveById)[0]);
        // several ticks of one tree all find it, and a tree selection beside them still has its own field
        var sel = new FieldChange("id:5678", "Layers", "tree", "", "Site");
        var map = FillMatcher.Match(new[] { byLabel, byLabel with { Key = TreeChecks.KeyOf("id:918556", "Site") }, sel }, liveById);
        Assert.Equal(new[] { 1, 1, 1 }, map);
    }

    [Fact]
    public void A_tick_in_a_tree_on_a_tab_is_found_on_that_tab_only()
    {
        var live = new List<LiveField> { new("tab:Text|name:Tree", "Layers", "tree", "Text"), new("tab:Lines|name:Tree", "Layers", "tree", "Lines") };
        var c = new FieldChange(TreeChecks.KeyOf("tab:Lines|name:Tree", "A"), TreeChecks.LabelOf("Layers", "A"), TreeChecks.Kind, "off", "on", "Lines");
        Assert.Equal(1, FillMatcher.Match(new[] { c }, live)[0]);
        Assert.Equal(-1, FillMatcher.Match(new[] { c }, live.Take(1).ToList())[0]);
    }

    // ---- the wording -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_tick_reads_Tick_item_in_tree_in_a_pause_message_and_in_steps_md()
    {
        var on = new FieldChange(TreeChecks.KeyOf("name:CheckTree", "Plant › Pumps"), TreeChecks.LabelOf("Layers", "Plant › Pumps"), TreeChecks.Kind, "off", "on");
        Assert.Equal("Tick Plant › Pumps in Layers", DialogFieldsCore.Describe(on));
        Assert.Equal("Untick Plant › Pumps in Layers", DialogFieldsCore.Describe(on with { Before = "on", After = "off" }));
        var script = Script(
            """{"t":100,"n":1,"type":"modal_enter","title":"Assembly"}""",
            """{"t":900,"n":2,"type":"dialog_values","for":1,"title":"Assembly","source":"win32","fields":[{"key":"name:CheckTree|check:Plant › Pumps","label":"Layers ¦ Plant › Pumps","kind":"treecheck","before":"off","after":"on"}],"read":4,"closedWith":"ok"}""",
            """{"t":910,"n":3,"type":"modal_leave"}""");
        Assert.Contains("Tick `Plant › Pumps` in **Layers**", script);
    }

    [Fact]
    public void A_double_click_reads_Double_click_item_in_tree_in_steps_md_and_the_pause_message()
    {
        var lines = DblclickRecording();
        var script = Script(lines);
        Assert.Contains("ouble-click `Housing › Lid` in **Items**", script);   // lower case after the changes made before it on the same line
        var dlg = Build(lines).Single(s => s.Kind == "dialog");
        var msg = DialogSegments.ManualMessage(dlg.DialogTitle, "f.md", 3, dlg);
        Assert.Contains("double-click Housing › Lid in Items", msg);
        Assert.DoesNotContain("press Lid", msg);
    }

    // ---- the plan and the segments -----------------------------------------------------------------------------------------

    static string[] DblclickRecording() => new[]
    {
        """{"t":100,"n":1,"type":"modal_enter","title":"Assembly"}""",
        """{"t":1000,"n":2,"type":"dialog_click","for":1,"kind":"dblclick","tree":"name:ItemTree","treeLabel":"Items","path":"Housing › Lid","button":"Lid","id":4,"index":1,"fields":[{"key":"name:ItemTree","label":"Items","kind":"tree","before":"","after":"Housing › Lid"}],"read":3}""",
        """{"t":1100,"n":3,"type":"modal_enter","title":"Part Housing>Lid"}""",
        """{"t":2000,"n":4,"type":"dialog_values","for":3,"title":"Part Housing>Lid","source":"win32","parent":1,"fields":[{"key":"name:qtyBox","label":"Quantity","kind":"edit","before":"1","after":"4"}],"read":1,"closedWith":"ok"}""",
        """{"t":2010,"n":5,"type":"modal_leave"}""",
        """{"t":3000,"n":6,"type":"dialog_values","for":1,"title":"Assembly","source":"win32","fields":[],"read":3,"closedWith":"ok"}""",
        """{"t":3010,"n":7,"type":"modal_leave"}""",
    };

    [Fact]
    public void A_double_click_is_a_click_of_kind_dblclick_with_its_tree_and_path_and_the_sub_form_it_opened()
    {
        var dlg = Build(DblclickRecording()).Single(s => s.Kind == "dialog");
        var c = dlg.Clicks.Single();
        Assert.Equal((DialogClickKinds.DoubleClick, "name:ItemTree", "Housing › Lid", "Items", "Lid"), (c.Kind, c.Tree, c.Path, c.TreeLabel, c.Button));
        Assert.Equal("Housing › Lid", c.Values.Single().After);
        Assert.Equal("Part Housing>Lid", c.Nested.Single().DialogTitle);
        var seg = DialogSegments.Of(dlg, true)[0];
        Assert.True(seg.IsDoubleClick);
        Assert.Equal("double-click Housing › Lid in Items", seg.Phrase);
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(dlg));
    }

    [Fact]
    public void A_recording_from_before_double_clicks_is_a_button_click_as_ever()
    {
        var dlg = Build(
            """{"t":100,"n":1,"type":"modal_enter","title":"Items"}""",
            """{"t":1000,"n":2,"type":"dialog_click","for":1,"button":"Add","id":1002,"index":1,"fields":[],"read":3}""",
            """{"t":3000,"n":6,"type":"dialog_values","for":1,"title":"Items","source":"win32","fields":[],"read":3,"closedWith":"ok"}""").Single(s => s.Kind == "dialog");
        var c = dlg.Clicks.Single();
        Assert.Equal((DialogClickKinds.Click, "", ""), (c.Kind, c.Tree, c.Path));
        var seg = DialogSegments.Of(dlg, true)[0];
        Assert.False(seg.IsDoubleClick);
        Assert.Equal("press Add", seg.Phrase);
        Assert.Equal(DialogClickKinds.Click, DialogClickKinds.Of("whatever"));
    }

    /// <summary>Main > (double-click Housing › Lid) Part > (Finish...) Finish > (Details...) Details > (double-click Spec › A) Spec: four levels, a tick, a tree selection, a tab and a
    /// button or double-click at each level, closed innermost first.</summary>
    static string[] DeepRecording() => new[]
    {
        """{"t":100,"n":1,"type":"modal_enter","title":"Assembly"}""",
        """{"t":1000,"n":2,"type":"dialog_click","for":1,"kind":"dblclick","tree":"name:ItemTree","treeLabel":"Items","path":"Housing › Lid","button":"Lid","id":4,"index":1,"fields":[{"key":"name:CheckTree|check:Site","label":"Layers ¦ Site","kind":"treecheck","before":"off","after":"on"},{"key":"name:ItemTree","label":"Items","kind":"tree","before":"","after":"Housing › Lid"}],"read":4}""",
        """{"t":1100,"n":3,"type":"modal_enter","title":"Part"}""",
        """{"t":1500,"n":4,"type":"dialog_click","for":3,"button":"Finish...","id":5,"index":1,"tab":"General","fields":[{"key":"tab:General|name:qty","label":"Quantity","kind":"edit","before":"1","after":"4","tab":"General"}],"read":2}""",
        """{"t":1600,"n":5,"type":"modal_enter","title":"Finish"}""",
        """{"t":1800,"n":6,"type":"dialog_click","for":5,"button":"Details...","id":6,"index":1,"fields":[{"key":"name:coat","label":"Coating","kind":"edit","before":"","after":"Zinc"}],"read":2}""",
        """{"t":1900,"n":7,"type":"modal_enter","title":"Details"}""",
        """{"t":2200,"n":8,"type":"dialog_click","for":7,"kind":"dblclick","tree":"name:SpecTree","treeLabel":"Spec","path":"Spec › A","button":"A","id":7,"index":1,"fields":[{"key":"name:SpecTree|check:Spec › A","label":"Spec ¦ Spec › A","kind":"treecheck","before":"off","after":"on"}],"read":2}""",
        """{"t":2300,"n":9,"type":"modal_enter","title":"Spec"}""",
        """{"t":2600,"n":10,"type":"dialog_values","for":9,"title":"Spec","source":"win32","parent":7,"fields":[{"key":"name:grade","label":"Grade","kind":"edit","before":"","after":"B2"}],"read":1,"closedWith":"ok"}""",
        """{"t":2610,"n":11,"type":"modal_leave"}""",
        """{"t":2900,"n":12,"type":"dialog_values","for":7,"title":"Details","source":"win32","parent":5,"fields":[{"key":"name:note","label":"Note","kind":"edit","before":"","after":"ok"}],"read":2,"closedWith":"ok"}""",
        """{"t":2910,"n":13,"type":"modal_leave"}""",
        """{"t":3200,"n":14,"type":"dialog_values","for":5,"title":"Finish","source":"win32","parent":3,"fields":[],"read":2,"closedWith":"ok"}""",
        """{"t":3210,"n":15,"type":"modal_leave"}""",
        """{"t":3500,"n":16,"type":"dialog_values","for":3,"title":"Part","source":"win32","parent":1,"fields":[],"read":2,"closedWith":"ok"}""",
        """{"t":3510,"n":17,"type":"modal_leave"}""",
        """{"t":4000,"n":18,"type":"dialog_values","for":1,"title":"Assembly","source":"win32","fields":[],"read":4,"closedWith":"ok"}""",
        """{"t":4010,"n":19,"type":"modal_leave"}""",
    };

    [Fact]
    public void Sub_forms_of_sub_forms_nest_to_any_depth_in_the_plan_with_their_values_ticks_tabs_and_clicks()
    {
        var steps = Build(DeepRecording());
        var main = steps.Single(s => s.Kind == "dialog");   // none of the inner forms is a step of its own
        var part = main.Clicks.Single().Nested.Single();
        var finish = part.Clicks.Single().Nested.Single();
        var details = finish.Clicks.Single().Nested.Single();
        var spec = details.Clicks.Single().Nested.Single();
        Assert.Equal(new[] { "Assembly", "Part", "Finish", "Details", "Spec" }, new[] { main, part, finish, details, spec }.Select(s => s.DialogTitle));
        Assert.Equal(new[] { "treecheck", "tree" }, main.Clicks[0].Values.Select(v => v.Kind));
        Assert.Equal(("General", "Quantity"), (part.Clicks[0].Tab, part.Clicks[0].Values.Single().Label));
        Assert.Equal("Zinc", finish.Clicks[0].Values.Single().After);
        Assert.Equal(("dblclick", "Spec › A"), (details.Clicks[0].Kind, details.Clicks[0].Path));
        Assert.Equal("treecheck", details.Clicks[0].Values.Single().Kind);
        Assert.Equal("B2", spec.Values!.Single().After);
        Assert.All(new[] { main, part, finish, details, spec }, s => Assert.Equal("ok", s.ClosedWith));
        // the segments of every level are the same recursion the replay's child fillers use
        Assert.Same(part, DialogSegments.Of(main, true)[0].Nested.Single());
        Assert.Same(spec, DialogSegments.Of(details, true)[0].Nested.Single());
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(spec));
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(details));
        // the pause message names the whole chain
        var msg = DialogSegments.ManualMessage("Assembly", "f.md", 2, main);
        Assert.Contains("Tick Site in Layers", msg);
        Assert.Contains("double-click Housing › Lid in Items (nested dialog \"Part\"", msg);
        Assert.Contains("press Finish... (nested dialog \"Finish\"", msg);
        Assert.Contains("double-click Spec › A in Spec (nested dialog \"Spec\"", msg);
    }

    [Fact]
    public void The_script_of_a_deep_recording_has_every_level()
    {
        var script = Script(DeepRecording());
        Assert.Contains("Tick `Site` in **Layers**", script);
        Assert.Contains("ouble-click `Housing › Lid` in **Items**", script);   // lower case after the changes made before it on the same line
        Assert.Contains("click **Finish...**", script);
        Assert.Contains("click **Details...**", script);
        Assert.Contains("ouble-click `Spec › A` in **Spec**", script);
        Assert.Contains("Set **Grade** to `B2`", script);
        Assert.Equal(1, script.Split("A dialog opens:").Length - 1);   // one dialog step; the others are "A nested dialog opens:"
        Assert.Equal(4, script.Split("A nested dialog opens:").Length - 1);
    }

    [Fact]
    public void A_double_click_that_closes_the_dialog_is_the_last_click_and_a_dialog_of_double_clicks_alone_is_work()
    {
        var dlg = Build(
            """{"t":100,"n":1,"type":"modal_enter","title":"Pick"}""",
            """{"t":1000,"n":2,"type":"dialog_click","for":1,"kind":"dblclick","tree":"id:1","treeLabel":"Parts","path":"M8","button":"M8","id":1,"index":1,"fields":[],"read":1}""",
            """{"t":1100,"n":3,"type":"dialog_values","for":1,"title":"Pick","source":"win32","fields":[],"read":1,"closedWith":"click","button":"M8"}""").Single(s => s.Kind == "dialog");
        Assert.True(DialogSegments.LastClickCloses(dlg));
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(dlg));
        Assert.Equal(1, DialogSegments.Work(dlg));
    }

    // ---- the rule for a double-click that only opens a node ------------------------------------------------------------

    [Theory]
    [InlineData(true, true, false, false, false, false)]    // a node with children whose state flipped, nothing else: only opened or closed
    [InlineData(false, true, false, false, false, true)]    // a leaf: it did something
    [InlineData(true, false, false, false, false, true)]    // a node that stayed as it was: it did something
    [InlineData(true, true, true, false, false, true)]      // a field changed
    [InlineData(true, true, false, true, false, true)]      // a window opened
    [InlineData(true, true, false, false, true, true)]      // the dialog closed
    public void A_double_click_is_an_action_unless_all_it_did_was_open_or_close_a_node(bool children, bool flipped, bool fields, bool window, bool gone, bool action) =>
        Assert.Equal(action, TreeClickRules.IsAction(children, flipped, fields, window, gone));

    // ---- a button that acts on mouse-down and opens a sub form -----------------------------------------------------------

    [Fact]
    public void The_click_of_a_mouse_down_button_opens_the_nested_gate_the_same_as_any_click()
    {
        // the recorder writes the click when the sub form appears (before the button-up, which goes to the sub form): the trail is then as after a click
        var trail = new ClickTrail();
        Assert.False(trail.MayOpenNested(1000));
        trail.Input();   // the press went down: input to the dialog
        Assert.False(trail.MayOpenNested(1100));
        trail.Clicked("Notes...", 1200);   // committed on the window that opened
        Assert.True(trail.MayOpenNested(1300));
        trail.NestedOpened();
        trail.NestedClosed(2000);
        Assert.True(trail.MayOpenNested(2100));
        // a dialog the sub form's own button opened is the sub form's gate, not the outer one's
        var inner = new ClickTrail();
        Assert.False(inner.MayOpenNested(2200));
        inner.Clicked("Finish...", 2300);
        Assert.True(inner.MayOpenNested(2400));
    }

    // ---- applying ticks to a tree that ticks by itself --------------------------------------------------------------------

    /// <summary>A native-like tree: ticking an item sets its whole subtree, and a parent shows on / off when all its children are (no state image, so not listed, when they are mixed).</summary>
    sealed class AutoTickTree
    {
        public readonly Dictionary<string, bool> Leaves = new();
        public readonly List<string> Parents = new();
        public int Sets;
        public AutoTickTree(string[] parents, string[] leaves, string[] on) { Parents.AddRange(parents); foreach (var l in leaves) Leaves[l] = on.Contains(l); }
        IEnumerable<string> Below(string p) => Leaves.Keys.Where(l => l.StartsWith(p + " › "));
        public List<(string Path, string Value)> Read()
        {
            var list = new List<(string, string)>();
            foreach (var p in Parents) { var kids = Below(p).Select(k => Leaves[k]).ToList(); if (kids.All(x => x)) list.Add((p, "on")); else if (kids.All(x => !x)) list.Add((p, "off")); }
            foreach (var l in Leaves) list.Add((l.Key, l.Value ? "on" : "off"));
            return list;
        }
        public string? Set(string path, bool on)
        {
            Sets++;
            if (Leaves.ContainsKey(path)) { Leaves[path] = on; return null; }
            foreach (var k in Below(path).ToList()) Leaves[k] = on;
            return Parents.Contains(path) ? null : "no such item";
        }
    }

    static AutoTickTree Purge(params string[] on) =>
        new(new[] { "Dim", "Text" }, new[] { "Dim › A", "Text › B", "Text › C" }, on);

    [Fact]
    public void Ticks_are_applied_deepest_first_and_keep_the_slots_of_the_other_values()
    {
        var values = new List<FieldChange>
        {
            new(TreeChecks.KeyOf("id:1", "Dim"), TreeChecks.LabelOf("T", "Dim"), "treecheck", "off", "on"),
            new("id:9", "Name", "edit", "", "x"),
            new(TreeChecks.KeyOf("id:1", "Dim › A"), TreeChecks.LabelOf("T", "Dim › A"), "treecheck", "off", "on"),
        };
        Assert.Equal(new[] { 2, 1, 0 }, TreeChecks.OrderForApply(values, new[] { 0, 1, 2 }));
        Assert.Equal(new[] { 1 }, TreeChecks.OrderForApply(values, new[] { 1 }));
    }

    [Fact]
    public void A_parent_and_its_child_ticked_in_a_tree_that_ticks_children_end_as_recorded()
    {
        var tree = Purge();
        var applier = new TreeCheckApplier(new[] { ("Dim", true), ("Dim › A", true) }, tree.Read, tree.Set);
        Assert.Null(applier.Apply("Dim › A", true));
        Assert.Null(applier.Apply("Dim", true));
        Assert.True(tree.Leaves["Dim › A"]);
        Assert.False(tree.Leaves["Text › B"]);
    }

    [Fact]
    public void A_tick_that_ticks_unrecorded_siblings_has_them_put_back_and_when_that_undoes_it_a_person_is_told()
    {
        var tree = Purge();   // only the parent Text was recorded: the tree ticked B and C with it
        var applier = new TreeCheckApplier(new[] { ("Text", true) }, tree.Read, tree.Set);
        var why = applier.Apply("Text", true);
        Assert.NotNull(why);   // B and C are put back, which unticks Text again: not reproducible, said so
        Assert.Contains("Text", why);
        // one sibling put back and the parent still on is fine: B recorded, C not (C is ticked by the tree, put back, the parent is mixed and not listed)
        var tree2 = Purge();
        var a2 = new TreeCheckApplier(new[] { ("Text › B", true) }, tree2.Read, tree2.Set);
        Assert.Null(a2.Apply("Text › B", true));
        Assert.False(tree2.Leaves["Text › C"]);
    }

    [Fact]
    public void A_recorded_item_a_later_tick_changed_is_set_again_and_an_item_that_is_right_is_not_set_at_all()
    {
        var tree = Purge("Dim › A");
        var applier = new TreeCheckApplier(new[] { ("Dim › A", true), ("Dim", false) }, tree.Read, tree.Set);
        Assert.Null(applier.Apply("Dim › A", true));
        var sets = tree.Sets;
        Assert.NotNull(applier.Apply("Dim", false));   // unticking the parent unticks A, set again, which ticks the parent: it cannot be both
        Assert.True(tree.Sets > sets);
    }

    [Fact]
    public void A_tree_with_too_many_items_says_it_was_cut()
    {
        var many = Enumerable.Range(0, TreeChecks.MaxItems + 5).Select(i => new Node("n" + i, "off")).ToArray();
        var root = new Node("", null, many);
        TreeChecks.Collect(root, n => n.Kids, n => n.Text, n => n.State, out var truncated);
        Assert.True(truncated);
        TreeChecks.Collect(new Node("", null, many.Take(3).ToArray()), n => n.Kids, n => n.Text, n => n.State, out var small);
        Assert.False(small);
    }

    [Fact]
    public void A_button_up_that_went_to_the_window_the_press_opened_completes_the_press_at_once()
    {
        // window opens (the dialog is disabled and a window is over it), the button-up arrives on that window, then a tick: the press is the click at the button-up
        Assert.True(DialogClickRules.UpCommits(landedOnButton: false, dialogDisabled: true, windowOver: true));
        Assert.True(DialogClickRules.UpCommits(true, false, false));
        Assert.False(DialogClickRules.UpCommits(false, false, false));   // released elsewhere with no window over the dialog: dropped
        Assert.False(DialogClickRules.UpCommits(false, true, false));    // disabled for another reason
    }

    [Fact]
    public void The_words_of_a_tick_and_a_double_click_are_one_source_for_steps_md_the_pause_message_and_Tdoc()
    {
        Assert.Equal("Tick `A` in **Layers**", TreeChecks.Sentence("on", "Layers", "A", t => $"**{t}**", p => $"`{p}`"));
        Assert.Null(TreeChecks.Sentence("mixed", "Layers", "A", t => t, p => p));
        Assert.Equal("Double-click `H › Lid` in **Items**", DialogClickKinds.DoubleClickWords("H › Lid", "Items", p => $"`{p}`", t => $"**{t}**"));
        Assert.Equal("double-click H › Lid in Items", DialogClickKinds.Phrase("dblclick", "Lid", "H › Lid", "Items"));
    }

    [Fact]
    public void onDown_is_read_from_the_event_and_defaults_to_false()
    {
        var on = Build("""{"t":100,"n":1,"type":"modal_enter","title":"M"}""",
            """{"t":1000,"n":2,"type":"dialog_click","for":1,"button":"Notes...","id":3,"index":1,"onDown":true,"fields":[],"read":1}""",
            """{"t":3000,"n":3,"type":"dialog_values","for":1,"title":"M","source":"win32","fields":[],"read":1,"closedWith":"ok"}""").Single(x => x.Kind == "dialog");
        Assert.True(on.Clicks[0].OnDown);
        Assert.True(DialogSegments.Of(on, true)[0].OnDown);
        var off = Build(DblclickRecording()).Single(x => x.Kind == "dialog");
        Assert.False(off.Clicks[0].OnDown);
    }

    [Fact]
    public void A_tick_is_satisfied_by_the_items_state_and_never_by_the_selected_path_of_its_tree()
    {
        // what a palette compared before (the tree field's value, the selected path) says nothing about a tick: the live value for a tick is the item's own state
        var tick = new FieldChange(TreeChecks.KeyOf("name:T", "A › B"), TreeChecks.LabelOf("Layers", "A › B"), TreeChecks.Kind, "off", "on");
        Assert.False(FillMatcher.Satisfied(tick, "A › B"));
        Assert.False(FillMatcher.Satisfied(tick, "off"));
        Assert.True(FillMatcher.Satisfied(tick, "on"));
        Assert.False(FillMatcher.Satisfied(tick, null));
    }

    static string[] PaletteDbl() => new[]
    {
        """{"t":500,"n":1,"type":"palette_values","palette":"Parts","set":"Samples","source":"win32","fields":[{"key":"name:PartsTree","label":"Catalog","kind":"tree","before":"","after":"Parts › Fasteners › Bolts › M8"}],"read":3}""",
        """{"t":510,"n":2,"type":"palette_click","palette":"Parts","set":"Samples","button":"M8","kind":"dblclick","tree":"name:PartsTree","treeLabel":"Catalog","path":"Parts › Fasteners › Bolts › M8"}""",
    };

    [Fact]
    public void A_double_click_in_a_palette_joins_the_values_step_before_it_as_a_palette_step()
    {
        var step = Build(PaletteDbl()).Single(x => x.Kind == "palette");
        Assert.Equal((DialogClickKinds.DoubleClick, "name:PartsTree", "Parts › Fasteners › Bolts › M8", "Catalog", "M8"), (step.ClickKind, step.ClickTree, step.ClickPath, step.ClickTreeLabel, step.Button));
        Assert.Contains("double-click Parts › Fasteners › Bolts › M8 in Catalog", step.Title);
        Assert.Equal("Parts › Fasteners › Bolts › M8", step.Values!.Single().After);
    }

    [Fact]
    public void A_double_click_in_a_palette_reads_double_click_item_in_tree_in_steps_md_and_Tdoc()
    {
        var script = Script(PaletteDbl());
        Assert.Contains("double-click `Parts › Fasteners › Bolts › M8` in **Catalog**", script);
        var step = Build(PaletteDbl()).Single(x => x.Kind == "palette");
        Assert.Contains(TdocPlan.Instructions(step), l => l.Contains("ouble-click Parts › Fasteners › Bolts › M8 in Catalog"));
    }

    // ---- what a click is written from -----------------------------------------------------------------------------------

    [Fact]
    public void A_normal_press_is_written_from_the_read_at_its_button_up_and_the_button_down_read_is_only_the_fallback()
    {
        Assert.False(DialogClickRules.UseDownSnapshot(committedBeforeUp: false, haveDownSnapshot: true));
        Assert.True(DialogClickRules.UseDownSnapshot(true, true));
        Assert.False(DialogClickRules.UseDownSnapshot(true, false));
    }

    [Fact]
    public void A_field_that_reformats_on_Leave_as_the_mouse_goes_down_is_in_the_click_when_read_at_the_button_up_but_not_in_the_read_before_it()
    {
        DialogField Qty(string v) => new("name:qty", "Weight", "edit", v);
        var t = new FieldTracker();
        t.Observe(new[] { Qty("") });
        t.Observe(new[] { Qty("2") });   // the person typed 2
        // at the button-down the field still shows what was typed; the Leave handler reformats it as the button takes the focus
        var down = t.ChangesWith(new[] { Qty("2") });
        var up = t.ChangesWith(new[] { Qty("2.000") });
        Assert.Equal("2", down.Single().After);
        Assert.Equal("2.000", up.Single().After);   // what the click is written from, as before mouse-down snapshots
        Assert.Equal("2", t.Changes().Single().After);   // ChangesWith observed nothing
    }

    [Fact]
    public void A_message_box_a_focused_fields_validation_opens_on_the_mouse_down_is_not_the_buttons_nested_dialog()
    {
        Assert.False(DialogClickRules.OnDownWindowIsNested(fieldFocusedAtDown: true, windowIsMessageBox: true));
        Assert.True(DialogClickRules.OnDownWindowIsNested(true, false));    // a sub form with fields
        Assert.True(DialogClickRules.OnDownWindowIsNested(false, true));    // no field had the focus: the button opened it
        Assert.True(DialogClickRules.OnDownWindowIsNested(false, false));
    }

    [Fact]
    public void A_button_is_pressed_again_only_when_nothing_happened_since_the_first_press()
    {
        Assert.True(DialogClickRules.MayRetryPress(dialogEnabled: true, windowOpened: false, somethingChanged: false));
        Assert.False(DialogClickRules.MayRetryPress(false, false, false));   // disabled: a window is over it
        Assert.False(DialogClickRules.MayRetryPress(true, true, false));
        Assert.False(DialogClickRules.MayRetryPress(true, false, true));     // a field or text changed: the first press did something
    }

    [Fact]
    public void The_applier_reads_the_tree_once_per_tick_when_nothing_has_to_be_put_back()
    {
        var tree = Purge();
        int reads = 0;
        var applier = new TreeCheckApplier(new[] { ("Dim", true), ("Dim › A", true), ("Text › B", true) }, () => { reads++; return tree.Read(); }, tree.Set);
        Assert.Null(applier.Apply("Dim › A", true));
        Assert.Equal(2, reads);   // the baseline and the one read after the set
        reads = 0;
        Assert.Null(applier.Apply("Text › B", true));
        Assert.Equal(1, reads);
    }
}
