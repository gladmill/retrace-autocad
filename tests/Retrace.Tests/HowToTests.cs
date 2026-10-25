using System.Text.Json;
using System.Text.RegularExpressions;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Tests folder given: how-to pages in the guide, "Used in" lines on tab pages, recordings/ written by the same run. Synthetic sessions only.</summary>
public class HowToTests : IDisposable
{
    private readonly string _out = Directory.CreateTempSubdirectory("retrace-test-howto-out").FullName;
    private readonly string _sess = Directory.CreateTempSubdirectory("retrace-test-howto-sess").FullName;

    public void Dispose()
    {
        Directory.Delete(_out, true);
        Directory.Delete(_sess, true);
    }

    static string Header(string? name, string started = "2026-10-08T10:00:00") =>
        "{\"type\":\"header\",\"schema\":1," + (name == null ? "" : $"\"name\":\"{name}\",") +
        $"\"autocad\":\"25.1.0.0\",\"product\":\"AutoCAD 2026\",\"workspace\":\"Drafting\",\"started\":\"{started}\"}}";

    static string[] Session(string? name, string started = "2026-10-08T10:00:00") => new[]
    {
        Header(name, started),
        // ribbon step: the Garden tab button with a dialog behind it
        """{"t":1000,"type":"ribbon","id":"DEMO_Settings","text":"Settings","macro":"^C^C_DEMO_SETTINGS","tab":"Garden","panel":"Beds"}""",
        """{"t":1050,"type":"cmd_start","command":"DEMO_SETTINGS","origin":"ribbon"}""",
        """{"t":1100,"type":"modal_enter","title":"Settings"}""",
        """{"t":2000,"type":"modal_leave"}""",
        """{"t":2010,"type":"cmd_end","command":"DEMO_SETTINGS"}""",
        // typed command with point and radius prompts
        """{"t":3000,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""",
        """{"t":3100,"type":"prompt","kind":"point","message":"Specify center point for circle or [3P/2P/Ttr]:","status":"OK","value":[12.123456789012,20,0],"input":true}""",
        """{"t":3200,"type":"prompt","kind":"distance","message":"Specify radius of circle or [Diameter]:","status":"OK","value":10}""",
        """{"t":3210,"type":"cmd_end","command":"CIRCLE"}""",
        """{"t":4000,"type":"mark","note":"circle is visible","state":{"count":2,"byType":{"LINE":1,"CIRCLE":1},"layers":["0"],"hash":"abcd"}}""",
        """{"t":5000,"type":"session_end"}""",
    };

    /// <summary>A test folder named after the stem of <paramref name="file"/> holding the recording.</summary>
    private void Write(string file, string[] lines)
    {
        var folder = Path.Combine(_sess, Path.GetFileNameWithoutExtension(file));
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "recording.jsonl"), lines);
    }

    private EngineModel Run(params string[] sessionPaths)
    {
        var m = EmitTests.SampleModel();
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = _out, Sessions = sessionPaths.Length == 0 ? [_sess] : sessionPaths });
        return m;
    }

    private string Read(params string[] rel) => File.ReadAllText(Path.Combine(new[] { _out }.Concat(rel).ToArray()));
    private string HowTo(string slug) => Read("guide", "howto", slug + ".md");
    private string TabPage => Read("guide", "ribbon", "code", "plugin", "garden.md");

    private Dictionary<string, byte[]> Snap() => Directory.GetFiles(Path.Combine(_out, "guide"), "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(_out, f), File.ReadAllBytes);

    [Fact]
    public void WritesHowToPageWithAllStepKinds()
    {
        Write("a.jsonl", Session("Change settings"));
        Run();
        var page = HowTo("change-settings");
        Assert.Contains("# Change settings", page);
        Assert.Contains("Recorded 2026-10-08, with AutoCAD 2026 25.1.0.0, workspace Drafting.", page);
        Assert.Contains("[a](../../recordings/a.md)", page);
        Assert.Contains("_What does this procedure achieve, and when should someone follow it? (your text; the engine never changes it)_", page);

        // ribbon step links to the button section on the tab page
        Assert.Contains("1. Click [**Garden › Beds › Settings**](../ribbon/code/plugin/garden.md#b-settings)", page);
        // the dialog the button opened sits inside its step and shows the wireframe (no capture given)
        Assert.Contains("   - A dialog opens: **Settings**", page);
        Assert.Contains("![Settings \\| Main](../../reference/images/dialogs/plugin/formsettings.svg)", page);
        // typed command with a point (rounded) and a number
        Assert.Contains("2. Type `CIRCLE` on the command line and press Enter", page);
        Assert.Contains("   - Pick the center point for circle (recorded at 12.1235, 20)", page);
        Assert.Contains("   - Enter 10 for the radius of circle", page);
        Assert.DoesNotContain("12.123456789", page);
        // checkpoint with state
        Assert.Contains("3. **Check:** circle is visible (the drawing now has 1 CIRCLE, 1 LINE)", page);
        // keys: kind + what + occurrence, one block per step, no per-step stub
        Assert.Contains("<!-- engine:begin step:ribbon:DEMO_Settings:1 -->", page);
        Assert.Contains("<!-- engine:begin step:command:circle:1 -->", page);
        Assert.Contains("<!-- engine:begin step:mark:circle-is-visible:1 -->", page);
        Assert.Single(Regex.Matches(page, "your text; the engine never changes it"));

        // the recordings manual test came from the same run
        Assert.True(File.Exists(Path.Combine(_out, "recordings", "a.md")));
        Assert.Contains("ribbon page", Read("recordings", "a.md"));
    }

    [Fact]
    public void CaptureReplacesWireframeInDialogStep()
    {
        var caps = Directory.CreateTempSubdirectory("retrace-test-howto-caps").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(caps, "Plugin__FormSettings.png"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(caps, "Plugin__FormSettings.json"), "{\"formId\":\"winform.Plugin.FormSettings\"}");
            Write("a.jsonl", Session("Change settings"));
            var m = EmitTests.SampleModel();
            Linker.AddRibbonHandlers(m);
            Linker.Link(m);
            Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = _out, Sessions = [_sess], Captures = [caps] });
            var page = HowTo("change-settings");
            Assert.Contains("(../images/captures/winform-plugin-formsettings.png)", page);
            Assert.DoesNotContain("formsettings.svg", page);
        }
        finally { Directory.Delete(caps, true); }
    }

    [Fact]
    public void TabPageListsHowTosAndIndexHasSection()
    {
        Write("b.jsonl", Session("Zeta"));
        Write("a.jsonl", Session("Alpha"));
        Run();
        var tab = TabPage;
        Assert.Contains("<a id=\"b-settings\"></a>", tab);
        Assert.Contains("**Used in:** [Alpha](../../../howto/alpha.md), [Zeta](../../../howto/zeta.md)", tab);
        // buttons nobody clicks have no line
        Assert.Single(Regex.Matches(tab, @"\*\*Used in:\*\*"));
        var index = Read("guide", "index.md");
        Assert.Contains("## How-to", index);
        Assert.Contains("- [Alpha](howto/alpha.md)", index);
        Assert.Contains("- [Zeta](howto/zeta.md)", index);
    }

    [Fact]
    public void NoSessionsMeansNoHowToAndNoIndexSection()
    {
        var m = EmitTests.SampleModel();
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = _out });
        Assert.False(Directory.Exists(Path.Combine(_out, "guide", "howto")));
        Assert.False(Directory.Exists(Path.Combine(_out, "recordings")));
        Assert.DoesNotContain("How-to", Read("guide", "index.md"));
        Assert.DoesNotContain("Used in", TabPage);
    }

    [Fact]
    public void ClickMatchedByCommandStillLinksAndFileNameIsTheFallbackName()
    {
        // wrong item id, but the macro names the button's command
        Write("my-run.jsonl", new[]
        {
            Header(null),
            """{"t":10,"type":"ribbon","id":"DEMO_Settings_v2","text":"Settings","macro":"^C^C_DEMO_SETTINGS","tab":"Garden","panel":"Beds"}""",
            """{"t":20,"type":"ribbon","id":"Nope","text":"Elsewhere","tab":"Other","panel":"P"}""",
        });
        Run();
        var page = HowTo("my-run");
        Assert.Contains("# my-run", page);
        Assert.Contains("1. Click [**Garden › Beds › Settings**](../ribbon/code/plugin/garden.md#b-settings)", page);
        Assert.Contains("2. Click **Other › P › Elsewhere**\n", page); // not in the guide: no link
        Assert.Contains("[my-run](../../../howto/my-run.md)", TabPage);
    }

    [Fact]
    public void DuplicateSessionNamesGetStableSuffixes()
    {
        Write("x1.jsonl", Session("Same name", "2026-10-09T10:00:00"));
        Write("x2.jsonl", Session("Same name", "2026-10-08T10:00:00"));
        Run();
        // the earlier recording comes first, whatever the file names
        Assert.Contains("Recorded 2026-10-08", HowTo("same-name"));
        Assert.Contains("# Same name\n", HowTo("same-name"));
        Assert.Contains("Recorded 2026-10-09", HowTo("same-name-2"));
        Assert.Contains("# Same name (2)", HowTo("same-name-2"));
        Assert.Contains("[Same name](../../../howto/same-name.md), [Same name (2)](../../../howto/same-name-2.md)", TabPage);
        var first = Snap();
        Run(_sess + Path.DirectorySeparatorChar);
        var second = Snap();
        Assert.Equal(first.Keys.OrderBy(k => k), second.Keys.OrderBy(k => k));
        foreach (var (k, v) in first) Assert.Equal(v, second[k]);
    }

    [Fact]
    public void SecondRunIsByteIdenticalAndOwnerTextSurvives()
    {
        Write("a.jsonl", Session("Change settings"));
        Run();
        var first = Snap();
        Run();
        var second = Snap();
        Assert.Equal(first.Keys.OrderBy(k => k), second.Keys.OrderBy(k => k));
        foreach (var (k, v) in first) Assert.Equal(v, second[k]);

        var path = Path.Combine(_out, "guide", "howto", "change-settings.md");
        var text = File.ReadAllText(path)
            .Replace("_What does this procedure achieve", "OWNER INTRO. _What does this procedure achieve")
            .Replace("<!-- engine:begin step:command:circle:1 -->", "OWNER BETWEEN STEPS\n\n<!-- engine:begin step:command:circle:1 -->");
        File.WriteAllText(path, text);
        // re-recorded: a step is added in the middle, the radius changes
        var lines = Session("Change settings").ToList();
        lines.Insert(6, """{"t":2500,"type":"ribbon","id":"Pts","text":"List Points","macro":"^C^C_other","tab":"Garden","panel":"Beds"}""");
        var at = lines.FindIndex(l => l.Contains("\"value\":10"));
        lines[at] = lines[at].Replace("\"value\":10", "\"value\":7.5");
        Write("a.jsonl", lines.ToArray());
        Run();
        var page = File.ReadAllText(path);
        Assert.Contains("OWNER INTRO.", page);
        Assert.Contains("OWNER BETWEEN STEPS", page);
        Assert.Contains("Enter 7.5 for the radius of circle", page);
        Assert.DoesNotContain("Enter 10 for", page);
        // the new step is in order, and later steps keep their keys (only their numbers move)
        // (a new block goes right before the next existing one, after the owner's text, as on the tab pages)
        Assert.True(page.IndexOf("OWNER BETWEEN STEPS") < page.IndexOf("step:ribbon:Pts:1"));
        Assert.True(page.IndexOf("step:ribbon:Pts:1") < page.IndexOf("step:command:circle:1"));
        Assert.Contains("3. Type `CIRCLE`", page);
        Assert.Contains("4. **Check:**", page);
        // and a further run changes nothing
        var again = File.ReadAllBytes(path);
        Run();
        Assert.Equal(again, File.ReadAllBytes(path));
    }

    [Fact]
    public void RepeatedStepsGetOccurrenceNumbersInTheirKeys()
    {
        var click = """{"t":1,"type":"ribbon","id":"DEMO_Settings","text":"Settings","tab":"Garden","panel":"Beds"}""";
        Write("r.jsonl", new[] { Header("Twice"), click, click.Replace("\"t\":1", "\"t\":9000") });
        Run();
        var page = HowTo("twice");
        Assert.Contains("<!-- engine:begin step:ribbon:DEMO_Settings:1 -->", page);
        Assert.Contains("<!-- engine:begin step:ribbon:DEMO_Settings:2 -->", page);
        Assert.Contains("2. Click", page);
    }

    [Theory]
    [InlineData("Specify first point:", "first point")]
    [InlineData("Specify next point or [Undo]:", "next point")]
    [InlineData("Specify radius of circle or [Diameter] <5.0000>:", "radius of circle")]
    [InlineData("Enter the new name:", "new name")]
    [InlineData("", null)]
    public void PromptSubject(string message, string? expected) => Assert.Equal(expected, GuideWriter.PromptSubject(message));

    static RecordingAnswer Ans(string kind, string status, string? message, string? valueJson = null, string? keyword = null) => new()
    {
        Kind = kind, Status = status, Message = message, Keyword = keyword,
        Value = valueJson == null ? null : JsonDocument.Parse(valueJson).RootElement.Clone(),
    };

    [Fact]
    public void AnswerTexts()
    {
        Assert.Equal("Pick the first point (recorded at 1.5, -2)", GuideWriter.AnswerText(Ans("point", "OK", "Specify first point:", "[1.5,-2.00001,0]")));
        Assert.Equal("Pick a point", GuideWriter.AnswerText(Ans("point", "OK", null)));
        Assert.Equal("Pick the opposite corner", GuideWriter.AnswerText(Ans("corner", "OK", null)));
        Assert.Equal("Enter 3.1416 for the width", GuideWriter.AnswerText(Ans("double", "OK", "Specify width:", "3.14159265")));
        Assert.Equal("Enter 90 degrees for the rotation angle", GuideWriter.AnswerText(Ans("angle", "OK", "Specify rotation angle:", (Math.PI / 2).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal("Press Enter", GuideWriter.AnswerText(Ans("point", "None", "x")));
        Assert.Equal("Press Esc to cancel", GuideWriter.AnswerText(Ans("point", "Cancel", "x")));
        Assert.Equal("Choose the option **Diameter**", GuideWriter.AnswerText(Ans("distance", "Keyword", "x", null, "Diameter")));
        Assert.Equal("Type `Wall` for the name", GuideWriter.AnswerText(Ans("string", "OK", "Enter name:", "\"Wall\"")));
        Assert.StartsWith("Select 2 objects", GuideWriter.AnswerText(Ans("selection", "OK", "Select objects:", "{\"count\":2,\"dxf\":[\"LINE\",\"LINE\"],\"method\":\"Window\"}")));
    }

    [Fact]
    public void StateText()
    {
        var s = RecordingState.From(JsonDocument.Parse("""{"count":0,"byType":{},"layers":["0"],"hash":"x"}""").RootElement)!;
        Assert.Equal("the drawing is empty", GuideWriter.StateText(s));
    }

    [Fact]
    public void StateVersionDefaultsTo1AndIsRead()
    {
        Assert.Equal(1, RecordingState.From(JsonDocument.Parse("""{"count":0,"hash":"x"}""").RootElement)!.Version);
        Assert.Equal(2, RecordingState.From(JsonDocument.Parse("""{"count":0,"hash":"x","v":2}""").RootElement)!.Version);
    }

    [Theory]
    [InlineData("20", "Specify radius of circle or [Diameter]:", "Diameter", true)]
    [InlineData("Diameter", "Specify radius of circle or [Diameter]:", "Diameter", false)]
    [InlineData("d", "Specify radius of circle or [Diameter]:", "Diameter", false)]   // unique prefix, as AutoCAD accepts
    [InlineData("D", "Specify center point for circle or [3P/2P/Ttr (tan tan radius)]:", null, true)] // no option starts with D
    [InlineData("2", "Specify center point for circle or [3P/2P/Ttr (tan tan radius)]:", null, false)]  // prefix of 2P
    [InlineData("ttr", "Specify center point for circle or [3P/2P/Ttr (tan tan radius)]:", null, false)]
    public void KeywordResultIsTypedInputOnlyWhenNotAnOption(string keyword, string message, string? option, bool typed)
    {
        var a = new RecordingAnswer { Kind = "distance", Status = "Keyword", Message = message, Keyword = keyword };
        if (option != null) a.Options.Add(option);
        Assert.Equal(typed, a.IsTypedInput);
    }

    [Fact]
    public void TypedKeywordReadsAsEnteredValueInHowToAndRetrace()
    {
        var typed = new RecordingAnswer { Kind = "distance", Status = "Keyword", Message = "Specify radius of circle or [Diameter]:", Keyword = "20", Options = { "Diameter" } };
        Assert.Equal("Enter 20 for the radius of circle", GuideWriter.AnswerText(typed));
        Assert.Equal("typed \"20\"", typed.Describe());
        var word = new RecordingAnswer { Kind = "string", Status = "Keyword", Message = "Specify name or [List]:", Keyword = "Wall", Options = { "List" } };
        Assert.Equal("Type `Wall` for the name", GuideWriter.AnswerText(word));
        var real = new RecordingAnswer { Kind = "distance", Status = "Keyword", Message = "Specify radius of circle or [Diameter]:", Keyword = "Diameter", Options = { "Diameter" } };
        Assert.Equal("Choose the option **Diameter**", GuideWriter.AnswerText(real));
        Assert.Equal("keyword [Diameter]", real.Describe());
    }

    [Fact]
    public void RecordedKeywordsArrayIsRead()
    {
        Write("k.jsonl", new[]
        {
            Header("Kw"),
            """{"t":1,"type":"ribbon","id":"DEMO_Settings","text":"Settings","tab":"Garden","panel":"Beds"}""",
            """{"t":2,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""",
            """{"t":6541,"type":"prompt","kind":"point","message":"Specify radius of circle or [Diameter]:","keywords":["Diameter"],"status":"Keyword","keyword":"20"}""",
        });
        Run();
        var page = HowTo("kw");
        Assert.Contains("   - Enter 20 for the radius of circle", page);
        Assert.Contains("typed \"20\"", Read("recordings", "k.md"));
        Assert.DoesNotContain("keyword [20]", Read("recordings", "k.md"));
    }

    [Fact]
    public void TabIdPicksTheRightTabAmongSameTitleTabs()
    {
        SourceMapEntry Item(string tab) => new()
        {
            Id = $"ribbon:cuix:acad.CUIX:{tab}/ID_PanelDraw/ID_CircleRad", Kind = "ribbon-item", Title = "Circle",
            Capture = new() { ["tab"] = "Home", ["panel"] = "Draw" },
        };
        var entries = new[] { Item("ID_3dBasics_Home"), Item("ID_TabHome3D"), Item("ID_TabHome") };
        var index = new SourceMapIndex("/x", entries);
        RecordingStep Step(string? tabId) => new() { Kind = "ribbon", ItemId = "ID_CircleRad", Text = "Circle", Tab = "Home", Panel = "Draw", TabId = tabId };
        foreach (var (tabId, want) in new[] { ("ACAD.ID_TabHome", "ID_TabHome"), ("acad.id_tabhome3d", "ID_TabHome3D"), ("ID_3dBasics_Home", "ID_3dBasics_Home"), ("acad.CUIX.ID_TabHome", "ID_TabHome") })
        {
            var join = index.JoinRibbon(Step(tabId));
            Assert.Equal(want, SourceMapIndex.TabIdOf(join!.Entry.Id));
        }
        // no tab id: the old title rule applies (first of the equal candidates)
        Assert.Equal("ID_3dBasics_Home", SourceMapIndex.TabIdOf(index.JoinRibbon(Step(null))!.Entry.Id));
        // the macro fallback honours the tab id too
        var byMacro = Item("ID_TabHome");
        var macroIndex = new SourceMapIndex("/x", new[]
        {
            new SourceMapEntry { Id = "ribbon:cuix:acad.CUIX:ID_3dBasics_Home/P/A", Kind = "ribbon-item", Title = "Circle", Command = "CIRCLE", Capture = new() { ["tab"] = "Home" } },
            new SourceMapEntry { Id = "ribbon:cuix:acad.CUIX:ID_TabHome/P/B", Kind = "ribbon-item", Title = "Circle", Command = "CIRCLE", Capture = new() { ["tab"] = "Home" } },
        });
        var m = macroIndex.JoinRibbon(new RecordingStep { Kind = "ribbon", ItemId = "gone", Macro = "^C^C_circle", Tab = "Home", TabId = "ACAD.ID_TabHome" });
        Assert.Equal("ID_TabHome", SourceMapIndex.TabIdOf(m!.Entry.Id));
    }

    [Fact]
    public void A_recording_still_open_for_writing_does_not_drop_the_others()
    {
        Write("a.jsonl", Session("Change settings"));
        Write("b.jsonl", Session("Draw a circle"));
        // SessionWriter keeps recording.jsonl open with FileShare.Read while a test is being recorded
        using (new FileStream(Path.Combine(_sess, "b", "recording.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var m = Run();
            Assert.DoesNotContain(m.Warnings, w => w.StartsWith("sessions:"));
            Assert.True(File.Exists(Path.Combine(_out, "guide", "howto", "change-settings.md")));
            Assert.True(File.Exists(Path.Combine(_out, "guide", "howto", "draw-a-circle.md")));
        }
    }

    [Fact]
    public void One_unreadable_recording_is_a_warning_naming_it_and_the_others_are_kept()
    {
        Write("a.jsonl", Session("Change settings"));
        Write("b.jsonl", Session("Draw a circle"));
        var bad = Path.Combine(_sess, "b", "recording.jsonl");
        using (new FileStream(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // nobody else may read it
        {
            var m = Run();
            Assert.Contains(m.Warnings, w => w.StartsWith("sessions:") && w.Contains(bad));
            Assert.True(File.Exists(Path.Combine(_out, "guide", "howto", "change-settings.md")));
        }
    }
}
