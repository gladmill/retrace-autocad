using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Toolbar clicks of a palette (`palette_click` kind `toolstrip` for a WinForms ToolStrip item, `toolbar` for a ToolbarWindow32 button): naming, finding the live button again, the steps and the words. Synthetic data only.</summary>
public class PaletteToolbarTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-paltool").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":1,"name":"palette toolbars","started":"2026-11-14T15:00:00+00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""";
    static string Pc(int n, string rest) => "{\"t\":" + (1000 + n * 10) + ",\"n\":" + n + ",\"type\":\"palette_click\",\"palette\":\"Projects\",\"set\":\"Tools\"," + rest[1..^1] + "}";

    static readonly string[] Session =
    {
        Pc(1, """{"button":"Beta","kind":"tree","tree":"name:ProjectsTree","treeLabel":"Projects","path":"Projects › Beta"}"""),
        Pc(2, """{"button":"Download","kind":"toolstrip","buttonFrom":"tooltip","strip":"ProjStrip","stripIndex":1,"itemName":"tsDownload","position":3,"count":5}"""),
        Pc(3, """{"button":"Sync","kind":"toolbar","buttonFrom":"tooltip","strip":"ProjToolBar","stripIndex":1,"position":1,"count":2,"cmd":0}"""),
        Pc(4, """{"button":"Export","kind":"toolstrip","buttonFrom":"tooltip","strip":"ProjStrip","stripIndex":1,"itemName":"tsExport","position":5,"count":5,"menu":"As text"}"""),
        Pc(5, """{"button":"(button)","kind":"toolstrip","stripIndex":2,"position":3,"count":4}"""),
    };

    static List<Step> Build(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    static ToolCandidate C(string name, string text = "", string tip = "", string acc = "", bool enabled = true, int cmd = 0) => new(name, text, tip, acc, enabled, cmd);

    // ---- naming ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_image_only_item_is_named_by_its_tool_tip_then_accessible_name_then_Name()
    {
        Assert.Equal(("Download", "tooltip"), PaletteToolbar.Display(C("tsDownload", tip: "Download")));
        Assert.Equal(("Rename", "accessible"), PaletteToolbar.Display(C("x", acc: "Rename")));
        Assert.Equal(("tsRefresh", "name"), PaletteToolbar.Display(C("tsRefresh")));
        Assert.Equal(("Save", "text"), PaletteToolbar.Display(C("tsSave", text: "&Save", tip: "Save the file")));   // its own text wins, mnemonic removed
        Assert.Equal((ButtonNames.Unnamed, ""), PaletteToolbar.Display(C("")));
    }

    [Fact]
    public void An_MFC_style_tool_tip_gives_the_part_after_the_last_line_break()
    {
        Assert.Equal("Copy", PaletteToolbar.Display(C("", tip: "Copy the selection\nCopy")).Name);
    }

    [Fact]
    public void The_occurrence_counts_buttons_with_the_same_name()
    {
        var live = new[] { C("a", tip: "Open"), C("b", tip: "Close"), C("c", tip: "open") };
        Assert.Equal(2, PaletteToolbar.Occurrence(live, 2));
        Assert.Equal(1, PaletteToolbar.Occurrence(live, 1));
    }

    // ---- finding the live button -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_button_is_found_by_command_id_then_Name_then_the_recorded_name()
    {
        var live = new[] { C("", tip: "Upload", cmd: 100), C("", tip: "Download", cmd: 101), C("", tip: "Refresh", cmd: 102) };
        Assert.Equal(1, PaletteToolbar.Find(live, "Download", 1, "", 3, 3, 101).At);              // by id
        var strip = new[] { C("tsUpload", tip: "Upload"), C("tsDownload", tip: "Download"), C("tsRefresh", tip: "Refresh") };
        Assert.Equal(1, PaletteToolbar.Find(strip, "Download", 1, "tsDownload", 3, 3, 0).At);      // by Name
        Assert.Equal(2, PaletteToolbar.Find(strip, "Refresh", 1, "gone", 1, 3, 0).At);             // the Name is gone: the recorded name says which
    }

    [Fact]
    public void An_item_found_by_Name_or_id_is_clicked_only_while_it_still_has_the_recorded_name()
    {
        var strip = new[] { C("tsUpload", tip: "Upload"), C("tsDownload", tip: "Delete"), C("tsRefresh", tip: "Refresh") };   // Download now does something else
        var f = PaletteToolbar.Find(strip, "Download", 1, "tsDownload", 2, 3, 0);
        Assert.Equal(-1, f.At);
        Assert.Contains("is called \"Delete\" now", f.Problem);
        var bar = new[] { C("", tip: "Upload", cmd: 100), C("", tip: "Delete", cmd: 101) };
        Assert.Equal(-1, PaletteToolbar.Find(bar, "Download", 1, "", 2, 2, 101).At);
    }

    [Fact]
    public void A_named_recording_whose_button_is_gone_fails_even_when_the_count_and_place_fit()
    {
        var live = new[] { C("", tip: "A"), C("", tip: "B2"), C("", tip: "C") };
        var f = PaletteToolbar.Find(live, "B", 1, "", 2, 3, 0);
        Assert.Equal(-1, f.At);
        Assert.Contains("toolbar button \"B\" not found", f.Problem);
        Assert.Equal(-1, PaletteToolbar.Find(live.Append(C("", tip: "D")).ToArray(), "B", 1, "", 2, 3, 0).At);
    }

    [Fact]
    public void Two_buttons_with_one_name_are_told_apart_by_occurrence_and_a_shared_Name_or_id_is_never_first_wins()
    {
        var live = new[] { C("x", tip: "Add"), C("x", tip: "Del"), C("x", tip: "Add") };
        Assert.Equal(2, PaletteToolbar.Find(live, "Add", 2, "x", 3, 3, 0).At);                     // three buttons have the Name: it names none, the display name and occurrence do
        Assert.Equal(-1, PaletteToolbar.Find(live, "Add", 3, "x", 0, 0, 0).At);                    // fewer than recorded: never another button
        var ids = new[] { C("", tip: "Add", cmd: 5), C("", tip: "Del", cmd: 5), C("", tip: "Add", cmd: 5) };
        Assert.Equal(2, PaletteToolbar.Find(ids, "Add", 2, "", 3, 3, 0).At);                       // the same id on several buttons is never recorded (cmd 0): name and occurrence
        Assert.Equal(-1, PaletteToolbar.Find(ids, "Add", 2, "", 3, 3, 5).At);                      // a recording that names an id several buttons share does not pick one
        Assert.Equal(1, PaletteToolbar.Find(ids, "Del", 1, "", 2, 3, 0).At);
    }

    [Fact]
    public void Only_a_recording_with_no_name_at_all_takes_a_button_by_place_and_only_an_unnamed_one()
    {
        var live = new[] { C(""), C(""), C("") };
        Assert.Equal(2, PaletteToolbar.Find(live, ButtonNames.Unnamed, 1, "", 3, 3, 0).At);
        var f = PaletteToolbar.Find(live, ButtonNames.Unnamed, 1, "", 3, 2, 0);                    // another number of buttons
        Assert.Equal(-1, f.At);
        Assert.Contains("no. 3", f.Problem);
        Assert.Equal(-1, PaletteToolbar.Find(new[] { C(""), C(""), C("", tip: "Now named") }, ButtonNames.Unnamed, 1, "", 3, 3, 0).At);   // it has a name now: not the same button
        Assert.Equal(-1, PaletteToolbar.Find(live, ButtonNames.Unnamed, 1, "tsGone", 3, 3, 0).At);  // it had a Name that is gone
    }

    [Fact]
    public void A_recorded_command_id_that_is_gone_never_falls_back_to_the_place()
    {
        var live = new[] { C("", cmd: 1001), C("", cmd: 1002), C("", cmd: 1003) };          // three buttons nothing names; the one recorded with id 1005 is gone
        var f = PaletteToolbar.Find(live, ButtonNames.Unnamed, 1, "", 3, 3, 1005);
        Assert.Equal(-1, f.At);
        Assert.Contains("command id 1005", f.Problem);
        Assert.Equal(2, PaletteToolbar.Find(live, ButtonNames.Unnamed, 1, "", 3, 3, 0).At);   // a recording with no id (and no name) is still taken by place
    }

    [Fact]
    public void A_gone_command_id_fails_even_when_the_name_is_unique_there_is_no_name_fallback()
    {
        var live = new[] { C("", tip: "Sync", cmd: 1101), C("", tip: "Fetch", cmd: 1102) };
        var f = PaletteToolbar.Find(live, "Sync", 1, "", 1, 2, 1001);
        Assert.Equal(-1, f.At);
        Assert.Contains("command id 1001 (\"Sync\") not found", f.Problem);
    }

    [Fact]
    public void The_reviewers_probe_and_the_removed_namesake_never_click_the_other_Add()
    {
        // partly known tool tips in the first pass: [Add (5), (unknown) (9)], the recorded Add had id 6
        Assert.Equal(-1, PaletteToolbar.Find(new[] { C("", tip: "Add", cmd: 5), C("", cmd: 9) }, "Add", 2, "", 2, 2, 6).At);
        // the recorded Add (id 6) was removed and its namesake (id 5) remains
        var f = PaletteToolbar.Find(new[] { C("", tip: "Add", cmd: 5), C("", tip: "Del", cmd: 9) }, "Add", 2, "", 2, 2, 6);
        Assert.Equal(-1, f.At);
        Assert.Contains("command id 6", f.Problem);
    }

    [Fact]
    public void The_occurrence_is_counted_over_every_button_so_the_second_Add_is_the_second_whatever_its_id()
    {
        var live = new[] { C("", tip: "Add", cmd: 5), C("", tip: "Add", cmd: 6) };
        Assert.Equal(2, PaletteToolbar.Occurrence(live, 1));                                   // clicking Y (id 6): the occurrence is 2
        Assert.Equal(1, PaletteToolbar.Find(live, "Add", 2, "", 2, 2, 6).At);                   // by id first
        var renumbered = new[] { C("", tip: "Add", cmd: 5), C("", tip: "Add", cmd: 9) };
        Assert.Equal(-1, PaletteToolbar.Find(renumbered, "Add", 2, "", 2, 2, 6).At);            // id 6 gone: stop, never "Add no. 1"
    }

    [Fact]
    public void A_button_taken_by_place_says_so_in_a_note()
    {
        var f = PaletteToolbar.Find(new[] { C(""), C("") }, ButtonNames.Unnamed, 1, "", 2, 2, 0);
        Assert.Equal(1, f.At);
        Assert.Contains("found by position: this button has no name", f.Note);
    }

    // ---- recording, plan and words -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_toolbar_click_is_a_step_of_its_own_with_what_finds_it_again()
    {
        var steps = Build(Session).Where(s => s.Kind == "palette").ToList();
        Assert.Equal(new[] { "tree", "toolstrip", "toolbar", "toolstrip", "toolstrip" }, steps.Select(s => s.ClickKind));
        Assert.All(steps.Skip(1), s => Assert.True(PaletteClickKinds.IsIntermediate(s.ClickKind)));
        var d = steps[1];
        Assert.Equal(("Download", "tooltip"), (d.Button, d.ButtonFrom));
        Assert.Equal(("ProjStrip", 1, "tsDownload", 3, 5), (d.Hit!.Strip, d.Hit.StripIndex, d.Hit.ItemName, d.Hit.Position, d.Hit.Count));
        Assert.Equal("As text", steps[3].Hit!.Menu);
        Assert.Equal(("ProjToolBar", 1), (steps[2].Hit!.Strip, steps[2].Hit!.StripIndex));
    }

    [Fact]
    public void A_toolbar_command_id_is_kept()
    {
        var s = Build(Pc(1, """{"button":"Copy","kind":"toolbar","buttonFrom":"tooltip","stripIndex":1,"position":2,"count":9,"cmd":32771}""")).Single();
        Assert.Equal(32771, s.Hit!.Cmd);
    }

    [Fact]
    public void The_words_name_the_button_by_tool_tip_or_by_place()
    {
        string W(string b, PaletteHit h, bool cap = true) => PaletteClickKinds.Phrase(PaletteClickKinds.ToolStrip, b, "", "", 1, 0, h, x => $"`{x}`", x => $"**{x}**", cap);
        Assert.Equal("Click toolbar button **Download**", W("Download", new PaletteHit(Position: 3)));
        Assert.Equal("click toolbar button no. 3", W(ButtonNames.Unnamed, new PaletteHit(Position: 3), cap: false));
        Assert.Equal("Choose **As text** from toolbar button **Export**", W("Export", new PaletteHit(Position: 5, Menu: "As text")));
        Assert.Equal("Choose **Export as › PDF** from toolbar button **Save**", W("Save", new PaletteHit(Position: 1, Menu: "Export as › PDF")));
    }

    [Fact]
    public void Step_titles_steps_md_and_Tdoc_word_a_toolbar_click()
    {
        var steps = Build(Session).Where(x => x.Kind == "palette").ToList();
        Assert.Equal("Palette \"Projects\": click toolbar button Download", steps[1].Title);
        Assert.Equal("Palette \"Projects\": click toolbar button Sync", steps[2].Title);
        Assert.Equal("Palette \"Projects\": choose As text from toolbar button Export", steps[3].Title);
        Assert.Equal("Palette \"Projects\": click toolbar button no. 3", steps[4].Title);
        var dir = Path.Combine(_dir, "s");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "recording.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(Session));
        var md = ScriptWriter.Build(RecordingReader.Load(p), null, _dir, new List<string>());
        Assert.Contains("Click toolbar button **Download**", md);
        Assert.Contains("Click toolbar button **Sync**", md);
        Assert.Contains("Choose **As text** from toolbar button **Export**", md);
        Assert.Contains("Click toolbar button no. 3", md);
        Assert.Contains(TdocPlan.Instructions(steps[1]), l => l.Contains("lick toolbar button Download"));
    }

    [Fact]
    public void A_toolbar_hosted_by_a_WinForms_control_is_a_toolbar_window()
    {
        Assert.True(NativeParts.IsToolbarWindow("ToolbarWindow32"));
        Assert.True(NativeParts.IsToolbarWindow("WindowsForms10.ToolbarWindow32.app.0.e0d36c_r3_ad1"));
        Assert.False(NativeParts.IsToolbarWindow("WindowsForms10.Window.8.app.0.e0d36c_r3_ad1"));
        Assert.False(NativeParts.IsToolbarClass("WindowsForms10.ToolbarWindow32.app.0.e0d36c_r3_ad1"));   // the dialog side is unchanged
    }

    [Fact]
    public void The_kinds_are_known_to_the_plan_and_toolbar_is_not_the_dialog_kind()
    {
        Assert.Equal(PaletteClickKinds.ToolStrip, PaletteClickKinds.Of("toolstrip"));
        Assert.Equal(PaletteClickKinds.Toolbar, PaletteClickKinds.Of("toolbar"));
        Assert.True(PaletteClickKinds.IsIntermediate("toolstrip"));
        Assert.True(PaletteClickKinds.IsIntermediate("toolbar"));
        Assert.False(PaletteClickKinds.OnTree("toolstrip"));
    }
}
