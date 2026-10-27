using System.Text.Json;
using Retrace.Core;
using Retrace.Engine;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Palette sets: `palette_values` / `palette_click` as steps (the test script, the replay plan), checkpoint names and pictures. Synthetic data only.</summary>
public class PaletteStepTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-palette").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":1,"name":"palette test","started":"2026-10-27T15:00:00+00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""";
    const string Values = """{"t":1000,"n":3,"type":"palette_values","palette":"Box","set":"Samples","source":"win32","fields":[{"key":"id:4654070","label":"Width","kind":"edit","before":"20","after":"35"},{"key":"id:3474374","label":"Closed","kind":"check","before":"on","after":"off"}],"read":5}""";
    const string Click = """{"t":1010,"n":4,"type":"palette_click","palette":"Box","set":"Samples","button":"Draw box"}""";

    string Script(params string[] lines)
    {
        var dir = Path.Combine(_dir, "s");
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        File.WriteAllBytes(Path.Combine(dir, "screenshots", "003-palette.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var p = Path.Combine(dir, "recording.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(lines));
        return ScriptWriter.Build(RecordingReader.Load(p), null, _dir, new List<string>());
    }

    static List<Step> Plan_(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    [Fact]
    public void Values_and_the_button_that_applies_them_are_one_step()
    {
        var md = Script(Values, Click, """{"t":1400,"n":5,"type":"shot","for":3,"file":"screenshots/003-palette.png","what":"window"}""");
        Assert.Contains("1. In the **Box** palette (Samples), make these changes, then click **Draw box**:", md);
        Assert.Contains("   - Set **Width** to `35` (was `20`)", md);
        Assert.Contains("   - Untick **Closed**", md);
        Assert.Contains("](screenshots/003-palette.png)", md);   // the picture belongs to the step
        Assert.DoesNotContain("2. ", md);
    }

    [Fact]
    public void A_click_alone_and_values_alone_read_as_such()
    {
        Assert.Contains("1. In the **Box** palette (Samples), click **Draw box**", Script(Click));
        var md = Script(Values);
        Assert.Contains("1. In the **Box** palette (Samples), make these changes:", md);
        Assert.Contains("   - Set **Width** to `35` (was `20`)", md);
    }

    [Fact]
    public void A_click_after_another_step_or_in_another_palette_is_its_own_step()
    {
        var other = """{"t":1010,"n":4,"type":"palette_click","palette":"Circle","set":"Samples","button":"Draw circle"}""";
        var md = Script(Values, other);
        Assert.Contains("1. In the **Box** palette (Samples), make these changes:", md);
        Assert.Contains("2. In the **Circle** palette (Samples), click **Draw circle**", md);
    }

    [Fact]
    public void The_replay_plan_has_one_palette_step_with_values_and_button()
    {
        var s = Plan_(Values, Click).Single();
        Assert.Equal("palette", s.Kind);
        Assert.Equal(("Box", "Samples", "Draw box"), (s.DialogTitle, s.Panel, s.Button));
        Assert.Equal(new[] { "Width", "Closed" }, s.Values!.Select(v => v.Label));
        Assert.Equal("win32", s.ValuesSource);
        Assert.Equal("Palette \"Box\": 2 value(s), then click Draw box", s.Title);
        Assert.Contains("; MANUAL: Palette \"Box\"", ScriptExport.Build(Plan_(Values, Click), "x"));
    }

    [Fact]
    public void Checkpoints_after_a_palette_are_named_after_it()
    {
        Assert.Equal("checkpoint3_box_button_draw_box", Checkpoints.AutoName(3, new("palette", "Box", "Draw box")));
        Assert.Equal("checkpoint1_box_palette", Checkpoints.AutoName(1, new("palette", "Box", null)));
        Assert.Equal("Draw box in the Box palette", new Checkpoints.CheckpointContext("palette", "Box", "Draw box").Describe());
    }

    [Fact]
    public void Palette_events_get_a_window_picture()
    {
        Assert.Equal("palette", ShotRules.For("palette_click", 4, false, 0)!.Kind);
        Assert.Equal("palette", ShotRules.For("palette_values", 3, false, 0)!.Kind);
    }

    const string Place = """{"t":2000,"n":6,"type":"palette_click","palette":"Search","set":"Parts Finder","button":"Place..."}""";
    const string PlaceStart = """{"t":2030,"n":7,"type":"cmd_start","command":"PARTSPLACE","origin":"palette","via":"Place..."}""";
    const string PlacePoint = """{"t":3000,"n":8,"type":"prompt","kind":"point","seq":1,"input":true,"by":"mouse","message":"Position for iPhone 5s:","status":"OK","value":[50,40,0]}""";
    const string PlaceEnd = """{"t":3010,"n":9,"type":"cmd_end","command":"PARTSPLACE"}""";

    [Fact]
    public void A_command_started_by_a_palette_button_is_part_of_its_step()
    {
        var md = Script(Place, PlaceStart, PlacePoint, PlaceEnd);
        Assert.Contains("1. In the **Search** palette (Parts Finder), click **Place...**", md);
        Assert.Contains("\n   - ", md[md.IndexOf("Place...")..]);   // the point answer is a sub-item of the palette step
        Assert.Contains("50", md[md.IndexOf("Place...")..]);
        Assert.DoesNotContain("PARTSPLACE` on the command line", md);
        var s = Plan_(Place, PlaceStart, PlacePoint, PlaceEnd).Single();
        Assert.Equal(("palette", "PARTSPLACE"), (s.Kind, s.Command));
        Assert.Equal("50,40\n", Plan.InputText(s, includeStart: false));
    }

    [Fact]
    public void A_typed_command_after_a_palette_click_stays_its_own_step()
    {
        var typed = PlaceStart.Replace("\"origin\":\"palette\"", "\"origin\":\"typed/other\"");
        var steps = Plan_(Place, typed, PlacePoint, PlaceEnd);
        Assert.Equal(new[] { "palette", "command" }, steps.Select(x => x.Kind));
    }

    [Fact]
    public void The_nth_button_with_the_same_text_is_kept_for_replay()
    {
        var second = Click.Replace("\"button\":\"Draw box\"", "\"button\":\"Draw box\",\"index\":2");
        Assert.Equal(2, Plan_(second).Single().ButtonIndex);
        Assert.Equal(1, Plan_(Click).Single().ButtonIndex);
    }

    [Fact]
    public void WinForms_names_key_the_fields_and_decide_the_match()
    {
        var fields = DialogFieldsCore.Build(new[]
        {
            new RawControl(4981990, "21 parts", "list", "a", Name: "ResultsList"),
            new RawControl(591542, "Search", "edit", "phone", Name: "SearchBox"),
        });
        Assert.Equal(new[] { "name:ResultsList", "name:SearchBox" }, fields.Select(f => f.Key));
        // the next run: other window ids, and the status text above the list says something else
        var live = new List<LiveField> { new("name:SearchBox", "Search", "edit"), new("name:ResultsList", "3 parts", "list") };
        var map = FillMatcher.Match(new[] { new FieldChange("name:ResultsList", "21 parts", "list", "", "iPhone 5s - 199.99") }, live);
        Assert.Equal(1, map[0]);
    }

    [Fact]
    public void A_radio_read_on_its_own_is_satisfied_by_on()
    {
        var c = new FieldChange("name:Metric", "Metric", "radio", "off", "on");
        Assert.True(FillMatcher.Satisfied(c, "on"));
        Assert.False(FillMatcher.Satisfied(c, "off"));
        Assert.True(FillMatcher.Satisfied(new FieldChange("id:5", "Inch / Metric", "radio", "Inch", "Metric"), "Metric"));
    }

    [Fact]
    public void A_shortcut_checkpoint_is_named_after_the_control_at_the_cursor_and_says_what_was_there()
    {
        var at = new Checkpoints.CheckpointContext("control", "Find", "Results", "At the cursor: Results (list: \"iPhone 5s\") in the Find palette.");
        Assert.Equal("checkpoint2_find_results", Checkpoints.AutoName(2, at));
        Assert.Equal("checkpoint1_find", Checkpoints.AutoName(1, new("control", "Find", null)));
        Assert.Equal(at.Description, at.Describe());
        var mark = """{"t":3000,"n":9,"type":"mark","note":"checkpoint1_find_results","autoName":"checkpoint1_find_results","near":"At the cursor: Results (list: \"iPhone 5s\") in the Find palette.","state":{"count":1,"byType":{"TEXT":1},"byLayer":{"PARTS":1},"layers":["0","PARTS"],"hash":"aa","v":2}}""";
        var md = Script(Values, Click, mark);
        Assert.Contains("**Check:** checkpoint1_find_results", md);
        Assert.Contains("   - At the cursor: Results (list: \"iPhone 5s\") in the Find palette.", md);
    }

    [Fact]
    public void Lists_and_combos_read_as_a_choice()
    {
        var v = new RecordingDialogValue("Category", "combo", "(all categories)", "smartphones");
        Assert.Equal("Choose `smartphones` in **Category** (was `(all categories)`)", StepWording.ChangeText(v, s => s ?? "", s => $"`{s}`", bold: true));
    }

    [Fact]
    public void The_palette_picture_is_named_after_its_step()
    {
        var dir = Path.Combine(_dir, "t");
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        foreach (var f in new[] { "004-palette-closeup.png", "005-palette.png" }) File.WriteAllBytes(Path.Combine(dir, "screenshots", f), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var rec = Path.Combine(dir, "recording.jsonl");
        File.WriteAllLines(rec, new[] { Header, Values, Click,
            """{"t":1020,"n":5,"type":"shot","for":4,"file":"screenshots/004-palette-closeup.png","what":"palette"}""",
            """{"t":1300,"n":6,"type":"shot","for":4,"file":"screenshots/005-palette.png","what":"window"}""" });
        Assert.True(EngineRunner.WriteSteps(rec).Success);
        var md = File.ReadAllText(Path.Combine(dir, "steps.md"));
        Assert.Contains("](screenshots/step-01-palette.png)", md);   // the close-up of the palette, taken while it was open
        Assert.Contains("](screenshots/step-01-click.png)", md);     // the whole window
    }
}
