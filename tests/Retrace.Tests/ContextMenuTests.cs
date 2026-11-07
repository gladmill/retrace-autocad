using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Right-click context menus (`context_menu`): the menu item texts and paths, the Win32 popup menu bookkeeping, the choice in a recording, the replay plan (a click of a dialog, a palette step),
/// the nested dialog a menu item opens, the segments, the wording (steps.md, Tdoc, the pause message). Synthetic data only.</summary>
public class ContextMenuTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-ctxmenu").FullName;
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

    // ---- texts and paths ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("&Rename", "Rename")]
    [InlineData("Rename\tF2", "Rename")]
    [InlineData("A && B", "A & B")]
    [InlineData("Properties...", "Properties")]
    [InlineData("Properties…", "Properties")]
    [InlineData("  &Edit  ", "Edit")]
    [InlineData(null, "")]
    public void Menu_texts_are_compared_without_mnemonics_accelerators_and_ellipsis(string? raw, string clean) => Assert.Equal(clean, MenuText.Clean(raw));

    [Fact]
    public void The_recorded_text_keeps_the_ellipsis_and_matches_either_spelling()
    {
        Assert.Equal("Properties...", MenuText.Display("&Properties...\tCtrl+P"));
        Assert.True(MenuText.Same("&properties", "Properties..."));
        Assert.False(MenuText.Same("Rename", "Delete"));
    }

    sealed class Item { public string Text = ""; public bool On = true; public List<Item> Kids = new(); }
    static Item I(string text, bool on = true, params Item[] kids) => new() { Text = text, On = on, Kids = kids.ToList() };

    [Fact]
    public void A_path_is_walked_by_text_and_a_disabled_item_is_a_problem_not_a_hit()
    {
        var menu = new List<Item> { I("&Edit", true, I("Rename\tF2"), I("Delete", false), I("Duplicate")), I("View", true, I("Rename")) };
        Func<Item?, IEnumerable<Item>> kids = p => p == null ? menu : p.Kids;
        var ok = MenuPath.Find<Item>(new[] { "Edit", "Rename" }, null, kids, i => i.Text, i => i.On);
        Assert.Null(ok.Problem);
        Assert.Equal(2, ok.Items.Count);
        Assert.Equal("Rename\tF2", ok.Items[1].Text);
        Assert.Contains("disabled", MenuPath.Find<Item>(new[] { "Edit", "Delete" }, null, kids, i => i.Text, i => i.On).Problem);
        Assert.Equal("no menu item Copy under Edit", MenuPath.Find<Item>(new[] { "Edit", "Copy" }, null, kids, i => i.Text, i => i.On).Problem);
        Assert.Equal("no menu item File", MenuPath.Find<Item>(new[] { "File" }, null, kids, i => i.Text, i => i.On).Problem);
    }

    [Fact]
    public void An_enabled_item_wins_over_a_disabled_one_with_the_same_text()
    {
        var menu = new List<Item> { I("Paste", false), I("Paste", true) };
        var r = MenuPath.Find<Item>(new[] { "Paste" }, null, p => p == null ? menu : p.Kids, i => i.Text, i => i.On);
        Assert.Null(r.Problem);
        Assert.Same(menu[1], r.Items[0]);
    }

    // ---- the Win32 popup menu --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_chosen_item_gives_its_path_through_the_submenu_and_the_command_arrives_after_the_menu_closed()
    {
        var t = new MenuTracker();
        t.Select(1, "Edit", -1, popup: true);
        t.Select(2, "Rename", 101, popup: false);
        t.Select(2, "Duplicate", 102, popup: false);   // the highlight moves inside the same menu
        t.Closed(1000);
        Assert.Equal(new[] { "Edit", "Duplicate" }, t.Chosen(102, 1500));
        Assert.False(t.Active);
    }

    [Fact]
    public void A_menu_closed_without_a_choice_records_nothing()
    {
        var t = new MenuTracker();
        t.Select(1, "Edit", -1, true);
        t.Select(2, "Rename", 101, false);
        t.Closed(1000);
        Assert.Null(t.Chosen(555, 1100));        // the menu was dismissed: no command with the highlighted item's id arrives, nothing is chosen
    }

    [Fact]
    public void A_command_long_after_the_close_is_not_the_menu_and_a_submenu_header_is_never_a_choice()
    {
        var t = new MenuTracker();
        t.Select(1, "Edit", 7, false);
        t.Closed(1000);
        Assert.Null(t.Chosen(7, 1000 + MenuTracker.ChoiceWithinMs + 1));
        var s = new MenuTracker();
        s.Select(1, "Edit", -1, true);
        Assert.Null(s.Chosen(0, 10));
    }

    [Fact]
    public void A_new_menu_starts_a_new_path()
    {
        var t = new MenuTracker();
        t.Select(1, "Edit", -1, true); t.Select(2, "Rename", 101, false); t.Closed(10);
        t.Select(9, "Delete", 9, false);
        Assert.Equal(new[] { "Delete" }, t.Chosen(9, 20));
    }

    // ---- the event -------------------------------------------------------------------------------------------------------------

    const string TreeMenu = """{"t":900,"n":20,"type":"context_menu","for":3,"kind":"winforms","target":"name:tree","label":"Catalog","targetKind":"tree","item":"Parts › Bolts","path":["Edit","Rename"],"fields":[],"read":3}""";

    [Fact]
    public void The_event_gives_the_choice_and_the_wording()
    {
        var c = MenuChoice.From(JsonDocument.Parse(TreeMenu).RootElement)!;
        Assert.Equal("winforms", c.Kind);
        Assert.Equal("name:tree", c.Target);
        Assert.Equal("Bolts", c.ItemLeaf);
        Assert.Equal("Edit › Rename", c.PathText);
        Assert.Equal("Right-click **Bolts** in **Catalog**, choose **Edit › Rename**", c.Wording());
        Assert.Equal("right-click Bolts in Catalog, choose Edit › Rename", c.Phrase());
        Assert.Equal("Right-click **Catalog**, choose **Open**", new MenuChoice("win32", "id:1", "Catalog", "", new[] { "Open" }).Wording());
        Assert.Equal("Right-click the dialog, choose Open", new MenuChoice("win32", "", "", "", new[] { "Open" }).Wording(false));
    }

    [Fact]
    public void An_event_without_a_path_is_no_choice_and_fields_round_trip()
    {
        Assert.Null(MenuChoice.From(JsonDocument.Parse("""{"type":"context_menu","path":[]}""").RootElement));
        Assert.Null(MenuChoice.From(JsonDocument.Parse("""{"type":"context_menu"}""").RootElement));
        var c = new MenuChoice("win32", "id:1002", "Files", "a.txt", new[] { "Open" }, 301, "list");
        var json = JsonSerializer.Serialize(c.ToFields().Where(f => f.Item2 != null).ToDictionary(f => f.Item1, f => f.Item2));
        var back = MenuChoice.From(JsonDocument.Parse(json).RootElement)!;
        Assert.Equal(c.Path, back.Path);
        Assert.Equal(301, back.Id);
        Assert.Equal("a.txt", back.Item);
        Assert.Equal("list", back.TargetKind);
    }

    // ---- the plan ---------------------------------------------------------------------------------------------------------------

    static readonly string[] DialogWithMenu =
    {
        """{"t":100,"n":1,"type":"cmd_start","command":"CATALOG","origin":"typed/other"}""",
        """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
        """{"t":200,"n":3,"type":"modal_title","title":"Catalog"}""",
        TreeMenu,
        """{"t":1000,"n":21,"type":"modal_enter","title":"AutoCAD"}""",
        """{"t":1010,"n":22,"type":"modal_title","title":"Properties"}""",
        """{"t":2000,"n":23,"type":"dialog_values","for":22,"title":"Properties","source":"win32","parent":3,"fields":[{"key":"name:note","label":"Note","kind":"edit","before":"","after":"x"}],"read":2,"closedWith":"ok"}""",
        """{"t":2010,"n":24,"type":"modal_leave"}""",
        """{"t":3000,"n":25,"type":"dialog_values","for":3,"title":"Catalog","source":"win32","fields":[],"read":3,"closedWith":"ok"}""",
        """{"t":3010,"n":26,"type":"modal_leave"}""",
        """{"t":3020,"n":27,"type":"cmd_end","command":"CATALOG"}""",
    };

    [Fact]
    public void A_menu_choice_in_a_dialog_is_a_click_and_the_form_it_opened_is_nested_under_it()
    {
        var steps = Build(DialogWithMenu);
        var dlg = steps.Single(s => s.Kind == "dialog");
        var click = dlg.Clicks.Single();
        Assert.Equal("Edit › Rename", click.Button);
        Assert.NotNull(click.Menu);
        Assert.Equal("Parts › Bolts", click.Menu!.Item);
        Assert.Equal("Properties", click.Nested.Single().DialogTitle);
        Assert.DoesNotContain(steps, s => s.Kind == "dialog" && s.DialogTitle == "Properties");   // not a step of its own
        var segments = DialogSegments.Of(dlg, closingValues: true);
        Assert.Equal(2, segments.Count);
        Assert.Same(click.Menu, segments[0].Menu);
        Assert.True(DialogSegments.HasClicks(segments));
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(dlg));
    }

    [Fact]
    public void The_pause_messages_say_what_to_right_click_and_choose()
    {
        var dlg = Build(DialogWithMenu).Single(s => s.Kind == "dialog");
        var manual = DialogSegments.ManualMessage("Catalog", null, null, dlg);
        Assert.Contains("Right-click Bolts in Catalog, choose Edit › Rename (nested dialog \"Properties\": set Note to x, then close it with OK), then close it with OK", manual);
        var segments = DialogSegments.Of(dlg, true);
        var left = DialogSegments.FallbackMessage("Catalog", null, null, segments, new List<SegmentProgress>(), "ok", "no item Bolts in Catalog");
        Assert.Contains("ight-click Bolts in Catalog, choose Edit › Rename yourself", left);
    }

    [Fact]
    public void The_script_and_Tdoc_name_the_item_the_menu_and_the_form_it_opens()
    {
        var md = Script(DialogWithMenu);
        Assert.Contains("     - Right-click **Bolts** in **Catalog**, choose **Edit › Rename**\n", md);
        Assert.Contains("       - A nested dialog opens: **Properties**. Make these changes, then confirm:\n", md);
        var lines = TdocPlan.Instructions(Build(DialogWithMenu).Single(s => s.Kind == "dialog"));
        Assert.Contains("Right-click Bolts in Catalog, choose Edit › Rename.", lines);
        Assert.Contains("In the Properties dialog:", lines);
    }

    static readonly string[] PaletteMenu =
    {
        """{"t":100,"n":1,"type":"palette_values","palette":"Parts","set":"Samples","source":"win32","fields":[{"key":"name:filter","label":"Filter","kind":"edit","before":"","after":"b"}],"read":2}""",
        """{"t":300,"n":2,"type":"context_menu","palette":"Parts","set":"Samples","kind":"winforms","target":"name:tree","label":"Catalog","targetKind":"tree","item":"Parts › Bolts","path":["Edit","Rename"]}""",
        """{"t":900,"n":3,"type":"cmd_start","command":"RENAMEPART","origin":"palette"}""",
        """{"t":910,"n":4,"type":"cmd_end","command":"RENAMEPART"}""",
    };

    [Fact]
    public void A_palette_menu_choice_joins_the_values_before_it_and_a_command_it_starts_belongs_to_the_step()
    {
        var steps = Build(PaletteMenu);
        var step = steps.Single(s => s.Kind == "palette");
        Assert.Equal("Edit › Rename", step.Button);
        Assert.NotNull(step.Menu);
        Assert.Single(step.Values!);
        Assert.Equal("RENAMEPART", step.Command);
        Assert.Contains("right-click Bolts in Catalog, choose Edit › Rename", step.Title);
        Assert.Equal(1, steps.Count(s => s.Kind is "palette" or "command"));
    }

    [Fact]
    public void The_palette_line_of_the_script_and_Tdoc_use_the_menu_wording()
    {
        var md = Script(PaletteMenu);
        Assert.Contains("In the **Parts** palette (Samples), make these changes, then right-click **Bolts** in **Catalog**, choose **Edit › Rename**:", md);
        var lines = TdocPlan.Instructions(Build(PaletteMenu).Single(s => s.Kind == "palette")).ToList();
        Assert.Contains("Right-click Bolts in Catalog, choose Edit › Rename.", lines);
    }

    [Fact]
    public void A_menu_choice_of_a_dialog_nobody_tracks_is_left_out_and_does_not_break_the_plan()
    {
        var steps = Build("""{"t":1,"n":9,"type":"context_menu","for":99,"kind":"win32","path":["X"]}""");
        Assert.Empty(steps);
    }

    [Fact]
    public void Steps_count_a_palette_menu_choice_as_the_palette_click_it_is()
    {
        var tally = new StepTally();
        tally.Note("palette_values", null, 0, 0, null, null, "Parts", "Samples");
        tally.Note("palette_click", null, 0, 0, null, null, "Parts", "Samples");   // what the recorder tells it for a palette `context_menu`
        Assert.Equal(1, tally.Count);
    }

    [Fact]
    public void A_palette_menu_choice_gets_the_palette_picture_request()
    {
        var r = ShotRules.Palette(7, 1000);
        Assert.Equal(7, r.N);
        Assert.Equal("palette", r.Kind);
        Assert.Equal(ShotTarget.Window, r.Target);
    }

    [Fact]
    public void A_palette_menu_says_the_palette_and_a_win32_menu_keeps_its_owner()
    {
        var none = new MenuChoice("win32", "", "", "", new[] { "Open" });
        Assert.Equal("Right-click the dialog, choose Open", none.Wording(false));
        Assert.Equal("Right-click the palette, choose Open", none.Wording(false, null, "the palette"));
        var c = new MenuChoice("win32", "id:5", "Files", "", new[] { "Open" }, 301, "list", "id:1002");
        var json = JsonSerializer.Serialize(c.ToFields().Where(f => f.Item2 != null).ToDictionary(f => f.Item1, f => f.Item2));
        Assert.Equal("id:1002", MenuChoice.From(JsonDocument.Parse(json).RootElement)!.Owner);
        Assert.Null((c with { Owner = "" }).ToFields().Single(f => f.Item1 == "owner").Item2);
    }

    [Fact]
    public void The_fields_of_a_palette_menu_event_are_in_the_script_like_the_plan_has_them()
    {
        const string withFields = """{"t":300,"n":2,"type":"context_menu","palette":"Parts","set":"Samples","kind":"winforms","target":"name:tree","label":"Catalog","targetKind":"tree","item":"Bolts","path":["Edit","Rename"],"fields":[{"key":"name:filter","label":"Filter","kind":"edit","before":"","after":"b"}]}""";
        Assert.Single(Build(withFields).Single(s => s.Kind == "palette").Values!);
        Assert.Contains("Set **Filter** to `b`", Script(withFields));
    }
}
