using System.Text.Json;
using Retrace.Core;
using Retrace.Engine;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>The intermediate clicks of a palette: tree items, +/- buttons, images, list view items and unnamed controls (`palette_click` with `kind`): their steps, words and the choice of the live control a replay clicks. Synthetic data only.</summary>
public class PaletteIntermediateClickTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-palclick").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":1,"name":"palette clicks","started":"2026-10-27T15:00:00+00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""";
    static string Pc(int n, string rest) => "{\"t\":" + (1000 + n * 10) + ",\"n\":" + n + ",\"type\":\"palette_click\",\"palette\":\"Pick\",\"set\":\"Samples\"," + rest[1..^1] + "}";

    static readonly string[] Session =
    {
        """{"t":900,"n":1,"type":"palette_values","palette":"Pick","set":"Samples","source":"win32","fields":[{"key":"name:Size","label":"Size","kind":"edit","before":"1","after":"2"}],"read":4}""",
        Pc(2, """{"button":"Cabinets","kind":"expand","tree":"name:PickTree","treeLabel":"Catalog","path":"Cabinets"}"""),
        Pc(3, """{"button":"B24","kind":"tree","tree":"name:PickTree","treeLabel":"Catalog","path":"Cabinets › Base › B24"}"""),
        Pc(4, """{"button":"Door style 3","kind":"image","buttonFrom":"tooltip","index":2,"legacyIndex":3,"noTextCount":4,"x":0.5,"y":0.25}"""),
        Pc(5, """{"button":"Oak","kind":"listview","list":"name:Finishes","listLabel":"Finish","item":"Oak","itemIndex":1,"occurrence":1,"view":"icon"}"""),
        Pc(6, """{"button":"swatch","kind":"point","class":"window","classIndex":2,"controlName":"swatch","x":0.4,"y":0.6}"""),
    };

    static List<Step> Build(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    [Fact]
    public void Every_click_is_a_step_of_its_own_and_the_first_one_joins_the_values_before_it()
    {
        var steps = Build(Session).Where(s => s.Kind == "palette").ToList();
        Assert.Equal(new[] { "expand", "tree", "image", "listview", "point" }, steps.Select(s => s.ClickKind));   // five clicks, five steps...
        Assert.Single(steps[0].Values!);                                                                         // ...the values went with the first
        Assert.All(steps.Skip(1), s => Assert.Null(s.Values));
    }

    [Fact]
    public void A_tree_click_keeps_its_tree_path_and_label_like_a_double_click()
    {
        var s = Build(Session).Where(x => x.Kind == "palette").ElementAt(1);
        Assert.Equal((PaletteClickKinds.Tree, "name:PickTree", "Cabinets › Base › B24", "Catalog", "B24"), (s.ClickKind, s.ClickTree, s.ClickPath, s.ClickTreeLabel, s.Button));
    }

    [Fact]
    public void An_image_click_keeps_the_button_naming_fields_and_the_click_point()
    {
        var s = Build(Session).Where(x => x.Kind == "palette").ElementAt(2);
        Assert.Equal(("Door style 3", "tooltip", 2, 3, 4), (s.Button, s.ButtonFrom, s.ButtonIndex, s.ButtonLegacyIndex, s.ButtonNoTextCount));
        Assert.Equal((0.5, 0.25), (s.Hit!.Fx, s.Hit.Fy));
    }

    [Fact]
    public void A_list_view_and_a_point_click_keep_what_finds_them_again()
    {
        var steps = Build(Session).Where(x => x.Kind == "palette").ToList();
        Assert.Equal(("name:Finishes", "Finish", "Oak", 1, 1, "icon"), (steps[3].Hit!.Control, steps[3].Hit!.ControlLabel, steps[3].Hit!.Item, steps[3].Hit!.ItemIndex, steps[3].Hit!.Occurrence, steps[3].Hit!.View));
        Assert.Equal(("window", 2, "swatch"), (steps[4].Hit!.Class, steps[4].Hit!.ClassIndex, steps[4].Hit!.Name));
    }

    [Fact]
    public void The_step_titles_read_naturally()
    {
        var titles = Build(Session).Where(x => x.Kind == "palette").Select(s => s.Title).ToList();
        Assert.Contains("expand tree item Cabinets in Catalog", titles[0]);
        Assert.Equal("Palette \"Pick\": click tree item Cabinets › Base › B24 in Catalog", titles[1]);
        Assert.Equal("Palette \"Pick\": click image Door style 3 (no. 2)", titles[2]);
        Assert.Equal("Palette \"Pick\": click list item Oak in Finish", titles[3]);
        Assert.Equal("Palette \"Pick\": click swatch at 40% × 60%", titles[4]);
    }

    [Fact]
    public void The_words_name_an_unnamed_image_by_its_place_and_a_point_by_class_and_place()
    {
        Assert.Equal("Click image no. 3", PaletteClickKinds.ImageWords(PaletteClickKinds.ImageUnnamed, 3, 0, x => x));
        Assert.Equal("click image **Open**", PaletteClickKinds.ImageWords("Open", 1, 2, x => $"**{x}**", capital: false));
        Assert.Equal("Click window no. 2 at 50% × 50%", PaletteClickKinds.Words(PaletteClickKinds.Point, "window", "", "", new PaletteHit(Class: "window", ClassIndex: 2, Fx: 0.5, Fy: 0.5), x => x, x => x));
        Assert.Equal("Collapse tree item `A`", PaletteClickKinds.Words(PaletteClickKinds.Collapse, "A", "A", "", null, x => $"`{x}`", x => x));
    }

    [Fact]
    public void Steps_md_and_Tdoc_word_the_new_clicks()
    {
        var dir = Path.Combine(_dir, "s");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "recording.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(Session));
        var md = ScriptWriter.Build(RecordingReader.Load(p), null, _dir, new List<string>());
        Assert.Contains("click tree item `Cabinets › Base › B24` in **Catalog**", md);
        Assert.Contains("click image **Door style 3**", md);
        Assert.Contains("click list item `Oak` in **Finish**", md);
        var tree = Build(Session).Where(x => x.Kind == "palette").ElementAt(1);
        Assert.Contains(TdocPlan.Instructions(tree), l => l.Contains("lick tree item Cabinets › Base › B24 in Catalog"));
    }

    [Fact]
    public void An_old_recording_without_kind_is_a_button_click_and_an_unknown_kind_too()
    {
        Assert.Equal(DialogClickKinds.Click, PaletteClickKinds.Of(null));
        Assert.Equal(DialogClickKinds.Click, PaletteClickKinds.Of("future"));
        Assert.Equal(DialogClickKinds.DoubleClick, PaletteClickKinds.Of("dblclick"));
        var s = Build("""{"t":1,"n":1,"type":"palette_click","palette":"Box","set":"S","button":"Draw box"}""").Single();
        Assert.Equal("click", s.ClickKind);
        Assert.Null(s.Hit);
        Assert.False(PaletteClickKinds.IsIntermediate(s.ClickKind));
    }

    // ---- which live control is the recorded one ----------------------------------------------------------------------------

    [Fact]
    public void An_image_is_found_by_the_name_of_its_source_and_its_index_among_those_with_that_name()
    {
        var live = new List<string?> { "Door style 1", "Door style 3", null, "Door style 3" };
        Assert.Equal(1, PaletteClickKinds.FindImage(live, "Door style 3", ButtonNames.FromTooltip, 1));
        Assert.Equal(3, PaletteClickKinds.FindImage(live, "Door style 3", ButtonNames.FromTooltip, 2));
        Assert.Equal(-1, PaletteClickKinds.FindImage(live, "Door style 3", ButtonNames.FromTooltip, 3));   // fewer than recorded: not found, never another one
        Assert.Equal(-1, PaletteClickKinds.FindImage(live, "Missing", ButtonNames.FromTooltip, 1));
        Assert.Equal(2, PaletteClickKinds.ImageIndexAmong(live, 3, "Door style 3", ButtonNames.FromTooltip));
    }

    [Fact]
    public void Pictures_nothing_names_are_found_by_their_place_among_all_pictures()
    {
        var live = new List<string?> { null, null, null };
        Assert.Equal(2, PaletteClickKinds.FindImage(live, PaletteClickKinds.ImageUnnamed, null, 3));
        Assert.Equal(-1, PaletteClickKinds.FindImage(live, PaletteClickKinds.ImageUnnamed, null, 4));
        Assert.Equal(3, PaletteClickKinds.ImageIndexAmong(live, 2, PaletteClickKinds.ImageUnnamed, null));
        Assert.True(ButtonNames.PositionAllowed(2, 3, 3));   // the by-position fallback of a named image
        Assert.False(ButtonNames.PositionAllowed(2, 3, 4));
    }

    [Fact]
    public void A_point_click_prefers_the_control_name_then_the_place_among_its_class()
    {
        var live = new[] { new PaletteClickKinds.Candidate("window", "a"), new PaletteClickKinds.Candidate("window", "swatch"), new PaletteClickKinds.Candidate("window", "") };
        Assert.Equal(1, PaletteClickKinds.FindPoint(live, new PaletteHit(Class: "window", ClassIndex: 3, Name: "swatch"), out _));
        Assert.Equal(2, PaletteClickKinds.FindPoint(live, new PaletteHit(Class: "window", ClassIndex: 3), out _));
        Assert.Equal(-1, PaletteClickKinds.FindPoint(live, new PaletteHit(Class: "window", ClassIndex: 3, Name: "gone"), out var gone));   // the name is gone: never another control of the class
        Assert.Contains("no control named gone", gone);
        Assert.Equal(-1, PaletteClickKinds.FindPoint(live, new PaletteHit(Class: "window", ClassIndex: 4), out var problem));
        Assert.Contains("no window control no. 4", problem);
        Assert.Equal(-1, PaletteClickKinds.FindPoint(live, new PaletteHit(Class: "static", ClassIndex: 1), out _));
    }

    [Fact]
    public void A_point_is_the_recorded_fraction_of_the_control_kept_inside_it()
    {
        Assert.Equal((40, 30), PaletteClickKinds.PointIn(new PaletteHit(Fx: 0.4, Fy: 0.6), 100, 50));
        Assert.Equal((50, 25), PaletteClickKinds.PointIn(new PaletteHit(), 100, 50));   // none recorded: the middle
        Assert.Equal((99, 49), PaletteClickKinds.PointIn(new PaletteHit(Fx: 1, Fy: 1), 100, 50));
        Assert.Equal(0.4, PaletteClickKinds.Fraction(40, 100));
        Assert.Equal(0.333, PaletteClickKinds.Fraction(1, 3));
        Assert.Equal("icon", PaletteClickKinds.ViewName(0));
        Assert.Equal("report", PaletteClickKinds.ViewName(1));
    }

    [Fact]
    public void A_list_item_is_resolved_by_text_and_occurrence_and_a_missing_one_is_an_error()
    {
        var texts = new[] { "Oak", "Pine", "Oak" };
        Assert.Equal(2, ListViewRows.Resolve(texts, "Oak", 2, 2, out _));
        Assert.Equal(-1, ListViewRows.Resolve(texts, "Oak", 3, 2, out var problem));
        Assert.Contains("only 2 rows", problem);
        Assert.Equal(-1, ListViewRows.Resolve(texts, "Birch", 1, 0, out problem));
        Assert.Contains("no item", problem);
    }

    [Fact]
    public void A_tree_is_found_by_its_control_name_even_when_its_label_holds_filter_text_that_changed()
    {
        var recorded = new FieldChange("name:PickTree", "Catalog: (Filter = 'ab*')", "tree", "", "Cabinets");
        var live = new[] { new LiveField("name:PickTree", "Catalog: (Filter = '*')", "tree"), new LiveField("name:Other", "Catalog: (Filter = 'ab*')", "tree") };
        Assert.Equal(0, FillMatcher.Match(new[] { recorded }, live)[0]);
    }

    [Fact]
    public void Two_clicks_near_each_other_are_spaced_beyond_the_double_click_time()
    {
        Assert.Equal(0, PaletteClickKinds.SpacingWaitMs(0, 5000, 500, true));            // no click before
        Assert.Equal(0, PaletteClickKinds.SpacingWaitMs(4800, 5000, 500, false));        // the last one was somewhere else
        Assert.Equal(360, PaletteClickKinds.SpacingWaitMs(4800, 5000, 500, true));       // 200 ms after it: wait until 500 + 60 have passed
        Assert.Equal(0, PaletteClickKinds.SpacingWaitMs(4000, 5000, 500, true));         // long enough ago
    }

    [Fact]
    public void A_click_with_an_empty_name_is_still_a_click_in_the_plan_the_words_and_Tdoc()
    {
        var line = Pc(2, """{"button":"","kind":"tree","tree":"name:PickTree","treeLabel":"Catalog","path":"Cabinets › "}""");
        var step = Build(line).Single();
        Assert.True(PaletteClickKinds.IsIntermediate(step.ClickKind));   // the replay dispatches by kind before it looks at the name
        Assert.Contains("click tree item", step.Title);
        Assert.Contains(TdocPlan.Instructions(step), l => l.Contains("lick tree item Cabinets"));
        Assert.Equal("(blank)", PaletteClickKinds.ItemName("  "));
        Assert.Equal("B24", PaletteClickKinds.ItemName(" B24 "));
    }

    [Fact]
    public void An_image_only_push_button_is_recorded_as_a_button_with_its_tool_tip_name()
    {
        // the recorder's naming for buttons without text (existing): an icon-only button is a click of the default kind with buttonFrom, never lost
        var s = Build("""{"t":1,"n":1,"type":"palette_click","palette":"Icons","set":"S","button":"Draw disc","buttonFrom":"tooltip","legacyIndex":3,"noTextCount":4}""").Single();
        Assert.Equal(("Draw disc", "tooltip", 3, 4, "click"), (s.Button, s.ButtonFrom, s.ButtonLegacyIndex, s.ButtonNoTextCount, s.ClickKind));
    }

    [Fact]
    public void A_blank_top_level_item_has_a_path_and_a_replay_finds_it_again()
    {
        Assert.Equal("(blank)", PaletteClickKinds.ClickPath(new string?[] { "" }));
        Assert.Equal("A › (blank) › B", PaletteClickKinds.ClickPath(new string?[] { "A", " ", "B" }));
        Assert.Equal(new[] { "(blank)" }, PaletteClickKinds.ClickLevels("(blank)"));
        Assert.Equal(new[] { "A", "(blank)", "B" }, PaletteClickKinds.ClickLevels("A ›  › B"));   // an older recording holds the empty level
        Assert.Equal("(blank)", PaletteClickKinds.LevelText(""));
        // the walk finds the blank top-level node by the same word on both sides
        var kids = new Dictionary<string, string[]> { [""] = new[] { "", "Doors" } };
        var found = TreePath.Find(PaletteClickKinds.ClickLevels("(blank)"), "", p => kids.GetValueOrDefault(p) ?? Array.Empty<string>(), t => PaletteClickKinds.LevelText(t));
        Assert.Null(found.Problem);
        Assert.Single(found.Items);
    }

    [Theory]
    [InlineData(0x0u, false, false)]
    [InlineData(0x1u, false, true)]          // a deeper node of a tree with buttons
    [InlineData(0x5u, true, true)]
    [InlineData(0x1u, true, false)]          // buttons without TVS_LINESATROOT: no button on a top-level node (ShowRootLines = false)
    [InlineData(0x4u, true, false)]          // lines at the root but no buttons at all
    public void A_plus_minus_button_is_expected_only_where_comctl32_draws_one(uint style, bool topLevel, bool expected) =>
        Assert.Equal(expected, PaletteClickKinds.ButtonExpected(style, topLevel));

    [Fact]
    public void A_plus_minus_button_is_expected_on_a_deeper_node_of_a_tree_without_lines_at_root() =>
        Assert.True(PaletteClickKinds.ButtonExpected(0x1, topLevel: false));
}
