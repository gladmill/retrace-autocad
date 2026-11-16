using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Retrace.Core;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>The settings object in a recording's header, the Settings window's unsaved-changes rule, a settings file with one unreadable value, and the wording of tree steps. Synthetic data only.</summary>
public class SettingsHeaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-sethdr").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    static Dictionary<string, JsonElement> HeaderOf(Dictionary<string, object?> snapshot)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = "header", [SettingsHeader.Field] = snapshot });
        using var d = JsonDocument.Parse(json);
        return d.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    // ---- header ----------------------------------------------------------------------------------------------

    [Fact]
    public void Every_setting_is_in_the_header_except_the_listed_paths()
    {
        var names = typeof(RetraceSettings).GetProperties()
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name).Where(n => n != null).Select(n => n!).ToList();
        var snap = SettingsHeader.Snapshot(new RetraceSettings());
        Assert.Equal(names.Where(n => !SettingsHeader.LeftOut.Contains(n)).OrderBy(n => n), snap.Keys.OrderBy(n => n));
        Assert.All(SettingsHeader.LeftOut, n => Assert.Contains(n, names));
        Assert.DoesNotContain(snap.Keys, k => k.EndsWith("Folder") || k == "editorPath");   // no path of a person's machine
    }

    [Fact]
    public void The_header_holds_the_values_in_force_and_the_line_names_those_not_at_default()
    {
        var s = new RetraceSettings { ScreenshotMode = "smart", RecordZoom = "extents", BasicCheck = true, ShowClickArrows = true, PromptStyle = "ui", SourceFolder = @"C:\private\src", TestsFolder = @"D:\mine" };
        var snap = SettingsHeader.Snapshot(s);
        var hdr = HeaderOf(snap);
        var read = SettingsHeader.FromHeader(hdr)!;
        Assert.Contains(("screenshotMode", "smart"), read);
        Assert.Contains(("recordZoom", "extents"), read);
        Assert.Contains(("promptStyle", "ui"), read);
        Assert.Contains(("basicCheckItems", "modelspace, layers, layouts"), read);
        Assert.Equal("Settings (not default): screenshotMode smart, showClickArrows true, recordZoom extents, promptStyle ui, basicCheck true", SettingsHeader.Line(read));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(snap));   // paths stay out
        Assert.DoesNotContain("mine", JsonSerializer.Serialize(snap));
    }

    [Fact]
    public void All_defaults_say_so_and_an_old_header_has_no_line()
    {
        Assert.Equal("Settings: all at their defaults", SettingsHeader.Line(SettingsHeader.FromHeader(HeaderOf(SettingsHeader.Snapshot(new RetraceSettings())))));
        Assert.Null(SettingsHeader.FromHeader(new Dictionary<string, JsonElement>()));
        Assert.Null(SettingsHeader.Line(null));
    }

    const string OldHeader = """{"type":"header","schema":3,"name":"old test","started":"2026-10-10T00:08:07","autocad":"25.1.0.0","product":"AutoCAD 2026","recordZoom":"none"}""";
    const string Cmd = """{"n":2,"t":1000,"type":"cmd_start","command":"LINE","origin":"typed/other"}""";

    static string StepsOf(string dir, string header)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "recording.jsonl");
        File.WriteAllLines(p, new[] { header, Cmd, """{"n":3,"t":1100,"type":"cmd_end","command":"LINE"}""" });
        return ScriptWriter.Build(RecordingReader.Load(p), null, dir, new List<string>());
    }

    [Fact]
    public void An_old_recording_without_settings_reads_and_writes_its_document_as_before()
    {
        var md = StepsOf(Path.Combine(_dir, "old"), OldHeader);
        Assert.DoesNotContain("Settings", md);
        Assert.Contains("# old test", md);
    }

    [Fact]
    public void The_test_document_shows_the_recorded_settings_in_one_line_under_the_title()
    {
        var snap = SettingsHeader.Snapshot(new RetraceSettings { ScreenshotMode = "smart", PostTestZoom = "extents" });
        var header = JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = "header", ["schema"] = 3, ["name"] = "new test", ["started"] = "2026-10-10T00:08:07", [SettingsHeader.Field] = snap });
        var md = StepsOf(Path.Combine(_dir, "new"), header);
        Assert.Contains("*Settings (not default): screenshotMode smart, postTestZoom extents*", md);
        Assert.True(md.IndexOf("*Settings", StringComparison.Ordinal) < md.IndexOf("**Start with:**", StringComparison.Ordinal));
    }

    // ---- unreadable value in settings.json ---------------------------------------------------------------------

    [Fact]
    public void One_unreadable_value_keeps_the_readable_ones()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Paths.SettingsFile(_dir), """{ "screenshotMode": "smart", "basicCheck": "true", "recordZoom": "extents", "tdocStepDelayMs": 300, "unknownKey": 1 }""");
        var ok = RetraceSettings.TryLoad(out var s, _dir);
        Assert.True(ok);
        Assert.Equal(("smart", "extents", 300, false), (s.ScreenshotMode, s.RecordZoom, s.TdocStepDelayMs, s.BasicCheck));
        Assert.Equal(new[] { "basicCheck" }, s.UnreadableKeys);
    }

    [Fact]
    public void An_unreadable_status_board_value_still_counts_as_not_understood_and_a_non_object_gives_defaults()
    {
        File.WriteAllText(Paths.SettingsFile(_dir), """{ "statusBoard": "false", "recordZoom": "extents" }""");
        Assert.False(RetraceSettings.TryLoad(out _, _dir));
        File.WriteAllText(Paths.SettingsFile(_dir), "[1,2]");
        Assert.False(RetraceSettings.TryLoad(out var s, _dir));
        Assert.Equal("none", s.RecordZoom);
        File.WriteAllText(Paths.SettingsFile(_dir), "{ not json");
        Assert.False(RetraceSettings.TryLoad(out _, _dir));
    }

    // ---- unsaved changes ---------------------------------------------------------------------------------------

    [Fact]
    public void Closing_asks_only_when_something_changed_and_a_change_changed_back_is_none()
    {
        string State(bool arrows, string delay, params string[] items) => SettingsClose.State(new object?[] { new[] { "C:\\a", "" }, arrows, delay, items.Select(i => (object?)i) });
        var opened = State(false, "1500", "layers");
        Assert.False(SettingsClose.Changed(opened, State(false, "1500", "layers")));
        Assert.True(SettingsClose.Changed(opened, State(true, "1500", "layers")));
        Assert.False(SettingsClose.Changed(opened, State(false, "1500", "layers")));   // changed back
        Assert.True(SettingsClose.Changed(opened, State(false, "15x0", "layers")));   // text that is no number is a change too
        Assert.True(SettingsClose.Changed(opened, State(false, "1500", "layers", "layouts")));
        Assert.NotEqual(SettingsClose.State(new object?[] { "ab", "c" }), SettingsClose.State(new object?[] { "a", "bc" }));   // parts do not run together
    }

    [Fact]
    public void A_window_that_opens_with_an_invalid_combination_still_asks_after_a_change()
    {
        // basicCheck on with no item left is a combination Save refuses; the dirty check reads the controls, not validated settings
        var invalid = new RetraceSettings { BasicCheck = true, BasicCheckItems = new() { "bogus" } };
        var opened = SettingsClose.State(new object?[] { invalid.BasicCheck, invalid.EffectiveBasicCheckItems().Select(i => (object?)i), invalid.ShowClickArrows });
        var after = SettingsClose.State(new object?[] { invalid.BasicCheck, invalid.EffectiveBasicCheckItems().Select(i => (object?)i), !invalid.ShowClickArrows });
        Assert.True(SettingsClose.Changed(opened, after));
        Assert.True(SettingsClose.Changed(opened, after));
        Assert.Equal(CloseAction.StayOpen, SettingsClose.Decide(SettingsClose.Changed(opened, after), CloseAnswer.Cancel));
    }

    [Fact]
    public void Every_writer_names_the_unreadable_keys_and_what_they_are_saved_as()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Paths.SettingsFile(_dir), """{"basicCheck":"true","screenshotMode":"smart","myNote":1}""");
        RetraceSettings.TryLoad(out var s, _dir);
        Assert.Equal("settings.json: could not read basicCheck (saved as false) (wrong type); saving writes the default for it.", s.UnreadableNotice());
        Assert.Equal("smart", s.ScreenshotMode);
        s.Clone().Save(out _);   // the rewrite is what the message announced: the unreadable key is now the default
        Assert.False(RetraceSettings.Load(_dir).BasicCheck);
        RetraceSettings.TryLoad(out var after, _dir);
        Assert.Null(after.UnreadableNotice());   // nothing to say once the file is whole
        Assert.Null(RetraceSettings.UnreadableNotice(null));
        Assert.Contains("tdocStepDelayMs (saved as 1500), suiteStepTimeoutSeconds (saved as 120)", RetraceSettings.UnreadableNotice(new[] { "tdocStepDelayMs", "suiteStepTimeoutSeconds" }));
        Assert.Contains("saving writes the default for them", RetraceSettings.UnreadableNotice(new[] { "tdocStepDelayMs", "suiteStepTimeoutSeconds" }));
    }

    [Fact]
    public void Header_free_text_is_normalised()
    {
        var s = new RetraceSettings { CheckpointKey = "ctrl + shift+j", SysvarsAdd = new() { " pdmode", "PDMODE", "ltscale", "" }, SysvarsRemove = new() { "x", "X" } };
        var snap = SettingsHeader.Snapshot(s);
        Assert.Equal("Ctrl+Shift+J", snap["checkpointKey"]);
        Assert.Equal(new[] { "pdmode", "ltscale" }, (List<string>)snap["sysvarsAdd"]!);
        Assert.Equal(new[] { "x" }, (List<string>)snap["sysvarsRemove"]!);
        Assert.Equal("None", SettingsHeader.Snapshot(new RetraceSettings { CheckpointKey = "None" })["checkpointKey"]);
        Assert.Equal("Ctrl+Shift+K", SettingsHeader.Snapshot(new RetraceSettings { CheckpointKey = "garbage" })["checkpointKey"]);
    }

    [Fact]
    public void The_settings_line_escapes_markdown()
    {
        var snap = SettingsHeader.Snapshot(new RetraceSettings { RibbonTabPosition = "after:My *Tab* `x`" });
        var header = JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = "header", ["schema"] = 3, ["name"] = "esc", [SettingsHeader.Field] = snap });
        var md = StepsOf(Path.Combine(_dir, "esc"), header);
        Assert.Contains(@"*Settings (not default): ribbonTabPosition after:My \*Tab\* \`x\`*", md);
    }

    [Theory]
    [InlineData(false, null, CloseAction.Close)]
    [InlineData(false, CloseAnswer.Cancel, CloseAction.Close)]
    [InlineData(true, CloseAnswer.Save, CloseAction.SaveThenClose)]
    [InlineData(true, CloseAnswer.DontSave, CloseAction.Close)]
    [InlineData(true, CloseAnswer.Cancel, CloseAction.StayOpen)]
    [InlineData(true, null, CloseAction.StayOpen)]
    public void The_three_answers(bool changed, CloseAnswer? answer, CloseAction expected) => Assert.Equal(expected, SettingsClose.Decide(changed, answer));

    // ---- tree wording ------------------------------------------------------------------------------------------

    static string Plain(string t) => t;
    static string Bold(string t) => $"**{t}**";

    [Theory]
    [InlineData("click", "Child", "Tree label: (Filter - 'abc')", "Click **Child** in the Tree label tree")]
    [InlineData("click", "Parent label › Child", "Tree label: (Filter - 'abc')", "Click **Child** under Parent label, in the Tree label tree")]
    [InlineData("click", "A › B › C", "Catalog", "Click **C** under A › B, in the Catalog tree")]
    [InlineData("expand", "Parent label", "Tree label:", "Expand **Parent label** in the Tree label tree")]
    [InlineData("collapse", "A › B", "", "Collapse **B** under A, in the tree")]
    [InlineData("collapse", "A", null, "Collapse **A** in the tree")]
    [InlineData("click", "A › B", "#1012", "Click **B** under A, in the tree")]
    public void A_tree_step_leads_with_the_action_and_the_item(string verb, string path, string? label, string expected) =>
        Assert.Equal(expected, TreeWording.Words(verb, path, label, Bold, Plain));

    [Fact]
    public void The_label_loses_a_trailing_bracket_and_colon_only()
    {
        Assert.Equal("Tree label", TreeWording.Label("Tree label: (Filter - 'abc')"));
        Assert.Equal("Tree label", TreeWording.Label("Tree label (Filter - 'a (b)')"));
        Assert.Equal("Parts (2) list", TreeWording.Label("Parts (2) list"));
        Assert.Equal("(Filter)", TreeWording.Label("(Filter)"));   // only a bracket: kept
        Assert.Equal("Catalog", TreeWording.Label("  Catalog:  "));
        Assert.Equal("", TreeWording.Label(null));
    }

    [Fact]
    public void The_recording_keeps_the_full_label_and_path_the_replay_matches_on()
    {
        var steps = Plan.Build(new[]
        {
            """{"t":1,"n":1,"type":"palette_click","palette":"Pick","set":"S","button":"B24","kind":"tree","tree":"name:T","treeLabel":"Catalog: (Filter - 'abc')","path":"Cabinets › B24"}""",
        }.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        var st = steps.Single(x => x.Kind == "palette");
        Assert.Equal(("Catalog: (Filter - 'abc')", "Cabinets › B24"), (st.ClickTreeLabel, st.ClickPath));
        Assert.Contains("click B24 under Cabinets, in the Catalog tree", st.Title);
    }

    [Fact]
    public void Item_and_label_text_cannot_turn_into_markdown()
    {
        Assert.Equal(@"Click **a\*b\*c** under Parent \`x\`, in the Tree \[1\] tree", TreeWording.Words("click", "Parent `x` › a*b*c", "Tree [1]:", t => $"**{Retrace.Engine.Util.Md.Inline(t)}**", Retrace.Engine.Util.Md.Inline));
        Assert.Equal("my_item", Retrace.Engine.Util.Md.Inline("my_item"));
        Assert.Equal(@"\_x\_", Retrace.Engine.Util.Md.Inline("_x_"));
        Assert.Equal("ab c", Retrace.Engine.Emit.StepWording.Alt(Retrace.Engine.Util.Md.Inline("a*b `c`")));
    }
}
