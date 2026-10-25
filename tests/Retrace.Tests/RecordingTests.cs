using System.Text.Json;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Recordings ingest on synthetic session files and a synthetic sourcemap. No real recordings.</summary>
public class RecordingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-rec").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":1,"name":"t","autocad":"25.1.0.0","product":"AutoCAD 2026","locale":"en","workspace":"Drafting","started":"2026-10-08T10:00:00"}""";

    private string Write(string name, params string[] lines)
    {
        // a recording lives in a test folder named after the stem: <dir>/<stem>/recording.jsonl
        var folder = Path.Combine(_dir, Path.GetFileNameWithoutExtension(name));
        Directory.CreateDirectory(folder);
        var p = Path.Combine(folder, "recording.jsonl");
        File.WriteAllLines(p, lines);
        return p;
    }

    // Home > Draw > Line (ribbon, with prompts), a typed CIRCLE, a dialog, a mark.
    static readonly string[] SessionA =
    {
        Header,
        """{"t":10,"type":"doc_active","doc":"a.dwg"}""",
        """{"t":1000,"type":"ribbon","doc":"a.dwg","id":"ID_Line","text":"Line","macro":"^C^C_line","itemType":"RibbonButton","tab":"Home","panel":"Draw"}""",
        """{"t":1100,"type":"cmd_start","doc":"a.dwg","command":"LINE","origin":"ribbon","via":"ID_Line"}""",
        """{"t":1200,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[10,20,0]}""",
        """{"t":1500,"type":"prompt","kind":"point","message":"Specify next point or [Undo]:","status":"OK","value":[30.5,40,0]}""",
        """{"t":1800,"type":"prompt","kind":"point","message":"Specify next point or [Undo]:","status":"None"}""",
        """{"t":1810,"type":"cmd_end","command":"LINE"}""",
        """{"t":3000,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""",
        """{"t":3100,"type":"prompt","kind":"point","message":"Specify center point:","status":"OK","value":[0,0,0]}""",
        """{"t":3200,"type":"prompt","kind":"distance","message":"Specify radius:","status":"OK","value":5}""",
        """{"t":3210,"type":"cmd_end","command":"CIRCLE"}""",
        """{"t":4000,"type":"ribbon","id":"ID_Layer","text":"Layer Properties","macro":"^C^C_layer","tab":"Home","panel":"Layers"}""",
        """{"t":4050,"type":"cmd_start","command":"LAYER","origin":"ribbon"}""",
        """{"t":4100,"type":"modal_enter","title":""}""",
        """{"t":4400,"type":"modal_title","title":"Layer Properties Manager","windowClass":"#32770"}""",
        """{"t":6000,"type":"modal_leave"}""",
        """{"t":6010,"type":"cmd_end","command":"LAYER"}""",
        """{"t":7000,"type":"mark","note":"expected: line and circle shown"}""",
        """{"t":8000,"type":"session_end"}""",
    };

    static readonly string[] SessionB =
    {
        Header,
        """{"t":100,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line","tab":"Home","panel":"Draw"}""",
        """{"t":150,"type":"cmd_start","command":"LINE","origin":"ribbon"}""",
        """{"t":200,"type":"cmd_end","command":"LINE"}""",
        // id differs from the sourcemap's (a rebuilt ribbon); the macro still names CIRCLE
        """{"t":900,"type":"ribbon","id":"RB_Circle_new","text":"Circle","macro":"^C^C_circle","tab":"Home","panel":"Draw"}""",
        """{"t":950,"type":"cmd_start","command":"CIRCLE","origin":"ribbon"}""",
        """{"t":990,"type":"cmd_end","command":"CIRCLE"}""",
        """{"t":1500,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line","tab":"Home","panel":"Draw"}""",
        """{"t":1550,"type":"cmd_start","command":"LINE","origin":"ribbon"}""",
        """{"t":1560,"type":"cmd_end","command":"LINE"}""",
        """{"t":1900,"type":"ribbon","id":"ID_Unknown","text":"Mystery","macro":"^C^C_mystery","tab":"Add-ins","panel":"X"}""",
    };

    private static SourceMapEntry Item(string id, string title, string tab, string panel, string? command, int line) => new()
    {
        Id = $"ribbon:cuix:acad.cuix:ID_TabHome/ID_Pnl{panel}/{id}", Kind = "ribbon-item", Title = title, Command = command,
        Source = new("acad.CUIX!RibbonRoot.cui", line), Doc = "reference/ribbon/cuix/acad/home.md",
        Capture = new() { ["tab"] = tab, ["panel"] = panel, ["item"] = title },
    };

    private SourceMapIndex SyntheticMap() => new(_dir, new[]
    {
        new SourceMapEntry { Id = "ribbon:cuix:acad.cuix:ID_TabHome", Kind = "ribbon-tab", Title = "Home", Doc = "reference/ribbon/cuix/acad/home.md", Capture = new() { ["tab"] = "Home" } },
        Item("ID_Line", "Line", "Home", "Draw", "LINE", 10),
        Item("ID_Circle", "Circle", "Home", "Draw", "CIRCLE", 11),
        Item("ID_Arc", "Arc", "Home", "Draw", "ARC", 12),
        Item("ID_Layer", "Layer Properties", "Home", "Layers", "LAYER", 20),
        new SourceMapEntry { Id = "form:winform.X.LayerForm", Kind = "form", Title = "Layer Properties Manager", Doc = "reference/dialogs/x/layer.md", Source = new("X/LayerForm.cs", 3), Capture = new() },
    });

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void ParsesHeaderEventsAndBuildsSteps()
    {
        var s = RecordingReader.Load(Write("a.jsonl", SessionA));
        Assert.Equal("AutoCAD 2026", s.HeaderStr("product"));
        Assert.Equal("Drafting", s.HeaderStr("workspace"));
        Assert.Equal(8000, s.DurationMs);

        var kinds = s.Steps.Select(x => x.Kind).ToList();
        Assert.Equal(new[] { "ribbon", "command", "ribbon", "dialog", "mark" }, kinds);

        var line = s.Steps[0];
        Assert.Equal("LINE", line.Command);
        Assert.Equal(new[] { "(10, 20, 0)", "(30.5, 40, 0)", "Enter" }, line.Answers.Select(a => a.Describe()));
        Assert.Equal("Specify first point:", line.Answers[0].Message);
        Assert.Equal("5", s.Steps[1].Answers[1].Describe());
        Assert.Equal("Layer Properties Manager", s.Steps[3].DialogTitle);   // late modal_title wins
        Assert.True(s.Steps[3].OpenedByPrevious);
        Assert.Equal("expected: line and circle shown", s.Steps[4].Note);
    }

    [Fact]
    public void Script_puts_a_dialog_inside_the_command_that_opened_it_before_later_answers_and_words_typed_points()
    {
        var s = RecordingReader.Load(Write("grid.jsonl", new[]
        {
            Header,
            """{"t":100,"n":1,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""",
            """{"t":200,"n":2,"type":"prompt","kind":"point","input":true,"by":"keys","message":"Specify center point for circle:","status":"OK","value":[500,450,0]}""",
            """{"t":300,"n":3,"type":"prompt","kind":"distance","input":true,"by":"keys","message":"Specify radius of circle:","status":"OK","value":40}""",
            """{"t":310,"n":4,"type":"cmd_end","command":"CIRCLE"}""",
            """{"t":1000,"n":5,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":1010,"n":6,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":1200,"n":7,"type":"modal_title","title":"Draw grid"}""",
            """{"t":2000,"n":8,"type":"modal_leave"}""",
            """{"t":2500,"n":9,"type":"prompt","kind":"point","input":true,"by":"mouse","message":"Lower-left point of the grid:","status":"OK","value":[0.5,2,0]}""",
            """{"t":2510,"n":10,"type":"cmd_end","command":"DRAWGRID"}""",
            """{"t":3000,"n":11,"type":"mark","note":"grid drawn"}""",
        }));
        // replay and ingest still see the dialog as a step of its own
        Assert.Equal(new[] { "command", "command", "dialog", "mark" }, s.Steps.Select(x => x.Kind));
        Assert.Same(s.Steps[1], s.Steps[2].OpenedIn);

        var md = ScriptWriter.Build(s, null, Path.GetTempPath(), new List<string>());
        Assert.Contains("   - Type `500,450` for the center point for circle\n", md);
        var grid = md.IndexOf("2. Type `DRAWGRID`", StringComparison.Ordinal);
        var dialog = md.IndexOf("   - A dialog opens: **Draw grid**. Fill it in and confirm.", StringComparison.Ordinal);
        var pick = md.IndexOf("   - Pick the Lower-left point of the grid (recorded at 0.5, 2)", StringComparison.Ordinal);
        Assert.True(grid >= 0 && grid < dialog && dialog < pick, md);
        Assert.Contains("3. **Check:** grid drawn", md);
        Assert.Equal(2, Retrace.Core.ScriptRef.DialogStepNumber(md, 1));
    }

    [Fact]
    public void SamplesButtonOwnsItsCommandAndRetraceStopIsNotAStep()
    {
        var s = RecordingReader.Load(Write("samples.jsonl", new[]
        {
            Header,
            """{"t":5100,"n":4,"type":"ribbon","id":"DRAWLINE","text":"Line","macro":"Autodesk.Windows.RibbonButton","tabId":"SAMPLES_TAB","tab":"Samples","panel":"Draw"}""",
            """{"t":5134,"n":7,"type":"cmd_start","command":"DRAWLINE","origin":"typed/other"}""",
            """{"t":6213,"n":11,"type":"prompt","kind":"point","input":true,"by":"mouse","message":"Start point:","status":"OK","value":[10,17,0]}""",
            """{"t":6619,"n":14,"type":"cmd_end","command":"DRAWLINE"}""",
            """{"t":21870,"n":35,"type":"ribbon","id":"RETRACESTOP","text":"Stop","macro":"^C^C_RETRACESTOP ","tabId":"RETRACE_TAB","tab":"Retrace","panel":"Record"}""",
        }));
        Assert.Single(s.Steps);
        Assert.Equal("ribbon", s.Steps[0].Kind);
        Assert.Equal("DRAWLINE", s.Steps[0].Command);
        Assert.Single(s.Steps[0].Answers);
    }

    [Fact]
    public void AnEventTypeTheReaderDoesNotKnowIsSkipped()
    {
        var path = Write("r.jsonl", Header,
            """{"t":100,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line","tab":"Home","panel":"Draw"}""",
            """{"t":150,"type":"cmd_start","command":"LINE","origin":"ribbon"}""",
            """{"t":5000,"type":"some_future_event","detail":"x"}""",
            """{"t":5200,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[1,2,0]}""");
        var s = RecordingReader.Load(path);
        var step = Assert.Single(s.Steps);                       // no extra step, and the prompt still attaches to LINE
        Assert.Single(step.Answers);
        Assert.Equal(0, s.BadLines);
    }

    [Fact]
    public void SkipsTornLastLine()
    {
        var s = RecordingReader.Load(Write("torn.jsonl", Header, """{"t":1,"type":"mark","note":"x"}""", """{"t":2,"type":"ribb"""));
        Assert.Equal(1, s.BadLines);
        Assert.Single(s.Steps);
    }

    [Fact]
    public void DescribesSelectionsAndEntities()
    {
        var sel = JsonDocument.Parse("""{"count":3,"handles":["1A","1B","1C"],"dxf":["LINE","LINE","CIRCLE"],"method":"Window"}""").RootElement;
        Assert.Equal("3 objects (LINE, CIRCLE) by Window", new RecordingAnswer { Kind = "selection", Status = "OK", Value = sel }.Describe());
        var ent = JsonDocument.Parse("""{"handle":"2F","dxf":"LWPOLYLINE","pick":[1,2,0]}""").RootElement;
        Assert.Equal("LWPOLYLINE (handle 2F)", new RecordingAnswer { Kind = "entity", Status = "OK", Value = ent }.Describe());
        Assert.Equal("keyword [Close]", new RecordingAnswer { Kind = "keyword", Status = "Keyword", Keyword = "Close" }.Describe());
    }

    // ------------------------------------------------------------------ origin attribution

    private static RecordingSession Session(params string[] events)
    {
        var s = new RecordingSession { Name = "t" };
        foreach (var l in events)
        {
            var root = JsonDocument.Parse(l).RootElement.Clone();
            s.Events.Add(new RecordingEvent { T = root.GetProperty("t").GetInt64(), Type = root.GetProperty("type").GetString()!, Raw = root });
        }
        RecordingReader.BuildSteps(s);
        return s;
    }

    [Fact]
    public void DerivesRibbonOriginWhenRecorderDidNotWriteIt()
    {
        var s = Session(
            """{"t":0,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line"}""",
            """{"t":500,"type":"cmd_start","command":"LINE"}""");
        var step = Assert.Single(s.Steps);
        Assert.Equal("ribbon", step.Kind);
        Assert.Equal("LINE", step.Command);
    }

    [Theory]
    [InlineData(2500, "LINE", 2)]   // more than 2 s after the click: typed
    [InlineData(500, "CIRCLE", 2)]  // macro does not name the command: typed
    [InlineData(2000, "LINE", 1)]   // exactly 2 s still counts
    public void NotRibbonIfLateOrMacroDoesNotMatch(long delay, string cmd, int steps)
    {
        var s = Session(
            """{"t":0,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line"}""",
            $$"""{"t":{{delay}},"type":"cmd_start","command":"{{cmd}}"}""");
        Assert.Equal(steps, s.Steps.Count);
    }

    [Fact]
    public void RibbonClickIsAttributedToOnlyOneCommand()
    {
        var s = Session(
            """{"t":0,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line"}""",
            """{"t":100,"type":"cmd_start","command":"LINE"}""",
            """{"t":200,"type":"cmd_end","command":"LINE"}""",
            """{"t":300,"type":"cmd_start","command":"LINE"}""");   // typed again right after
        Assert.Equal(new[] { "ribbon", "command" }, s.Steps.Select(x => x.Kind));
    }

    [Fact]
    public void MacroCommandParsing()
    {
        Assert.Equal("LINE", RecordingReader.MacroCommand("^C^C_line"));
        Assert.Equal("LAYER", RecordingReader.MacroCommand("^C^C_-layer;m;"));
        Assert.True(RecordingReader.MacroMentions("^C^C_-layer;m;", "LAYER"));
        Assert.False(RecordingReader.MacroMentions("^C^C_line", "LAYER"));
        Assert.Null(RecordingReader.MacroCommand(""));
    }

    // ------------------------------------------------------------------ join

    [Fact]
    public void JoinsById_ByMacro_AndLeavesUnknownUnmatched()
    {
        var map = SyntheticMap();
        var s = RecordingReader.Load(Write("b.jsonl", SessionB));
        map.Join(s);
        var clicks = s.Steps.Where(x => x.Kind == "ribbon").ToList();
        Assert.Equal("id", clicks[0].Join!.MatchedBy);
        Assert.EndsWith("/ID_Line", clicks[0].Join!.Entry.Id);
        Assert.Equal("macro", clicks[1].Join!.MatchedBy);          // id differs, macro _circle -> CIRCLE
        Assert.EndsWith("/ID_Circle", clicks[1].Join!.Entry.Id);
        Assert.Null(clicks[3].Join);
    }

    [Fact]
    public void PrefersEntryOnTheSameTab()
    {
        var a = Item("ID_Dup", "Dup", "Home", "P", "DUP", 1);
        var b = Item("ID_Dup", "Dup", "Insert", "P", "DUP", 2);
        var map = new SourceMapIndex(_dir, new[] { a, b });
        var j = map.JoinRibbon(new RecordingStep { Kind = "ribbon", ItemId = "ID_Dup", Tab = "Insert", Panel = "P", Text = "Dup" });
        Assert.Equal(2, j!.Entry.Source!.Line);
    }

    [Fact]
    public void MatchesDialogToFormByTitle()
    {
        var map = SyntheticMap();
        Assert.Equal("form:winform.X.LayerForm", map.MatchForm("layer properties manager")!.Entry.Id);
        Assert.Null(map.MatchForm("Something else"));
        Assert.Null(map.MatchForm(""));
    }

    // ------------------------------------------------------------------ n-grams

    [Fact]
    public void CountsNGramsAndDropsDominatedOnes()
    {
        var lists = new List<(string, List<string>)>
        {
            ("s1", new() { "Line", "Circle", "Layer", "Trim" }),
            ("s2", new() { "Line", "Circle", "Layer", "Move" }),
            ("s3", new() { "Zoom", "Line", "Circle" }),
        };
        var grams = RecordingAnalysis.NGrams(lists, 4, 20);
        // Line>Circle occurs 3 times (not dominated: Line>Circle>Layer only 2); Line>Circle>Layer 2; its sub-grams Circle>Layer, with 2 = dominated
        var lc = Assert.Single(grams, g => g.Tokens.SequenceEqual(new[] { "Line", "Circle" }));
        Assert.Equal(3, lc.Count);
        Assert.Equal(3, lc.Sessions);
        var lcl = Assert.Single(grams, g => g.Tokens.SequenceEqual(new[] { "Line", "Circle", "Layer" }));
        Assert.Equal(2, lcl.Count);
        Assert.DoesNotContain(grams, g => g.Tokens.SequenceEqual(new[] { "Circle", "Layer" }));
        Assert.DoesNotContain(grams, g => g.Count < 2);
        Assert.Equal(new[] { "Line", "Circle" }, grams[0].Tokens);   // highest count first
    }

    // ------------------------------------------------------------------ end to end

    [Fact]
    public void WritesIndexSessionPagesAndJson()
    {
        // a real sourcemap.json, as the engine writes it
        var mapDir = Path.Combine(_dir, "map");
        Directory.CreateDirectory(Path.Combine(mapDir, "data"));
        var m = SyntheticMap();
        File.WriteAllText(Path.Combine(mapDir, "data", "sourcemap.json"), JsonSerializer.Serialize(new { schema = "retrace/sourcemap@1", entries = m.Entries }, SourceMapWriter.Json));
        var sessions = Path.Combine(_dir, "sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(Path.Combine(sessions, "a"));
        Directory.CreateDirectory(Path.Combine(sessions, "b"));
        File.WriteAllLines(Path.Combine(sessions, "a", "recording.jsonl"), SessionA);
        File.WriteAllLines(Path.Combine(sessions, "b", "recording.jsonl"), SessionB);
        var outDir = Path.Combine(_dir, "out");

        Assert.True(EngineRunner.AnalyzeRecordings([sessions], outDir, mapDir).Success);

        var index = File.ReadAllText(Path.Combine(outDir, "recordings", "index.md"));
        Assert.Contains("[a](a.md)", index);
        Assert.Contains("Home ▸ Draw ▸ Line", index);                 // hot ribbon item
        Assert.Contains("No repeated sequences", index);              // nothing repeats in this fixture
        Assert.Contains("| 3 | 4 | 75% |", index);
        Assert.Contains("Arc", index);                                           // never exercised
        Assert.Contains("Mystery", index);                                       // click not in sourcemap

        var a = File.ReadAllText(Path.Combine(outDir, "recordings", "a.md"));
        Assert.Contains("1. Click **Home ▸ Draw ▸ Line**", a);
        Assert.Contains("../../map/reference/ribbon/cuix/acad/home.md", a);               // link to the ribbon page, relative
        Assert.Contains("`acad.CUIX!RibbonRoot.cui:10`", a);                     // source location
        Assert.Contains("prompt `Specify first point:` → (10, 20, 0)", a);
        Assert.Contains("Type command `CIRCLE`", a);
        Assert.Contains("Dialog opens: **\"Layer Properties Manager\"** - fill it in by hand", a);
        Assert.Contains("reference/dialogs/x/layer.md", a);                      // dialog -> dialog page
        Assert.Contains("**Expected:** expected: line and circle shown", a);

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "data", "recordings.json")));
        Assert.Equal("retrace/recordings@1", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("sessions").GetArrayLength());
        var cov = json.RootElement.GetProperty("coverage")[0];
        Assert.Equal("Home", cov.GetProperty("tab").GetString());
        Assert.Equal(4, cov.GetProperty("total").GetInt32());
        Assert.Equal(3, cov.GetProperty("exercised").GetInt32());
        Assert.Equal("ARC", cov.GetProperty("missing")[0].GetProperty("command").GetString());
    }

    [Fact]
    public void WorksWithoutAMap()
    {
        var f = Write("a.jsonl", SessionA);
        var outDir = Path.Combine(_dir, "out2");
        Assert.True(EngineRunner.AnalyzeRecordings([f], outDir).Success);
        var a = File.ReadAllText(Path.Combine(outDir, "recordings", "a.md"));
        Assert.Contains("Click **Home ▸ Draw ▸ Line**", a);
        Assert.DoesNotContain("Ribbon coverage", File.ReadAllText(Path.Combine(outDir, "recordings", "index.md")));
    }

    [Fact]
    public void BadArgumentsFailCleanly()
    {
        Assert.False(EngineRunner.AnalyzeRecordings([], Path.Combine(_dir, "o")).Success);
        Assert.False(EngineRunner.AnalyzeRecordings([Path.Combine(_dir, "missing.jsonl")], Path.Combine(_dir, "o")).Success);
    }

    // Shapes seen in a real AutoCAD Electrical 2026 recording.
    [Fact]
    public void DropsReAskedPromptAndPromptsOutsideCommands()
    {
        var s = RecordingReader.Load(Write("re.jsonl", Header,
            """{"t":1,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""",
            """{"t":2,"type":"prompt","kind":"point","message":"Specify center point:","status":"OK","value":[50,25,0]}""",
            """{"t":3,"type":"prompt","kind":"point","message":"Specify radius of circle or [Diameter]:","status":"Keyword","keyword":"10"}""",
            """{"t":4,"type":"prompt","kind":"distance","message":"Specify radius of circle or [Diameter]:","keywords":["Diameter"],"status":"OK","value":10}""",
            """{"t":5,"type":"cmd_end","command":"CIRCLE"}""",
            """{"t":6,"type":"prompt","kind":"string","message":"Note / expected result:","status":"Keyword","keyword":"after-circle"}""",
            """{"t":7,"type":"mark","note":"after-circle"}"""));
        Assert.Equal(new[] { "command", "mark" }, s.Steps.Select(x => x.Kind));
        Assert.Equal(2, s.Steps[0].Answers.Count);
        Assert.Equal("Keyword", s.Steps[0].Answers[1].Status);
    }

    // AutoCAD's own CUI buttons have no CommandParameter macro; the recorder used to tag their command "typed/other".
    [Fact]
    public void AttributesCommandToMacrolessRibbonClick()
    {
        var s = RecordingReader.Load(Write("nomacro.jsonl", Header,
            """{"t":1000,"type":"ribbon","id":"ID_Line","text":"Line","tab":"Home","panel":"Draw"}""",
            """{"t":1041,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":1500,"type":"cmd_end","command":"LINE"}""",
            """{"t":5000,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}"""));
        Assert.Equal(new[] { "ribbon", "command" }, s.Steps.Select(x => x.Kind));
        Assert.Equal("LINE", s.Steps[0].Command);
    }

    // AutoCAD Electrical requests a point on every mouse move while LINE waits; only results flagged as input are real.
    [Fact]
    public void DropsUnflaggedResultsWhenStepHasInputFlags()
    {
        var s = RecordingReader.Load(Write("noise.jsonl", Header,
            """{"t":1,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":2,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[5,5,0],"input":false}""",
            """{"t":3,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[1,1,0],"input":true}""",
            """{"t":4,"type":"prompt","kind":"point","message":"Specify next point or [Undo]:","status":"OK","value":[1,2,0],"input":false}""",
            """{"t":5,"type":"prompt","kind":"point","message":"Specify next point or [Undo]:","status":"OK","value":[2,2,0],"input":true}""",
            """{"t":6,"type":"prompt","kind":"point","message":"Specify next point or [Undo]:","status":"None","input":true}""",
            """{"t":7,"type":"cmd_end","command":"LINE"}""",
            """{"t":8,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""",
            """{"t":9,"type":"prompt","kind":"point","message":"Specify center point:","status":"OK","value":[0,0,0],"input":false}""",
            """{"t":10,"type":"cmd_end","command":"CIRCLE"}"""));
        Assert.Equal(new[] { "(1, 1, 0)", "(2, 2, 0)", "Enter" }, s.Steps[0].Answers.Select(a => a.Describe()));
        Assert.Single(s.Steps[1].Answers); // no flags set to true: scripted input, kept
    }

    // ---- checkpoints with drawing state ------------------------------------------------------

    const string StateA = """{"count":3,"byType":{"CIRCLE":1,"LINE":2},"byLayer":{"0":3},"layers":["0"],"hash":"aaaaaaaaaaaaaaaa"}""";
    const string StateB = """{"count":4,"byType":{"CIRCLE":1,"LINE":2,"TEXT":1},"byLayer":{"0":3,"DIM":1},"layers":["0","DIM"],"hash":"bbbbbbbbbbbbbbbb"}""";

    [Fact]
    public void ShowsCheckpointStateInMarkdownAndJson()
    {
        var f = Write("cp.jsonl", Header,
            """{"t":1,"type":"doc_active","doc":"a.dwg"}""",
            "{\"t\":2,\"type\":\"state\",\"doc\":\"a.dwg\",\"state\":" + """{"count":0,"byType":{},"byLayer":{},"layers":["0"],"hash":"e3b0c44298fc1c14"}}""",
            """{"t":100,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":110,"type":"cmd_end","command":"LINE"}""",
            "{\"t\":200,\"type\":\"mark\",\"note\":\"lines drawn\",\"state\":" + StateA + "}",
            "{\"t\":300,\"type\":\"mark\",\"note\":\"\",\"state\":" + StateB + "}",
            """{"t":400,"type":"mark","note":"old style"}""");
        var s = RecordingReader.Load(f);
        Assert.NotNull(s.InitialState);
        Assert.Equal("drawing: empty; layers: 0", s.InitialState!.Summary());
        // the state event is not a step
        Assert.Equal(new[] { "command", "mark", "mark", "mark" }, s.Steps.Select(x => x.Kind));
        Assert.Equal("drawing: 2 LINE, 1 CIRCLE; layers: 0", s.Steps[1].State!.Summary());
        Assert.Null(s.Steps[3].State);

        var outDir = Path.Combine(_dir, "outcp");
        Assert.True(EngineRunner.AnalyzeRecordings([f], outDir).Success);
        var md = File.ReadAllText(Path.Combine(outDir, "recordings", "cp.md"));
        Assert.Contains("**Expected:** lines drawn", md);
        Assert.Contains("drawing: 2 LINE, 1 CIRCLE; layers: 0 (3 objects", md);
        Assert.Contains("**Expected:** (checkpoint, no note)", md);
        Assert.Contains("drawing: 2 LINE, 1 CIRCLE, 1 TEXT; layers: 0, DIM", md);
        Assert.Contains("| Start state | drawing: empty; layers: 0 |", md);
        Assert.Contains("**Expected:** old style", md); // no state: unchanged

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "data", "recordings.json")));
        var sess = json.RootElement.GetProperty("sessions")[0];
        Assert.Equal("drawing: empty; layers: 0", sess.GetProperty("startState").GetProperty("summary").GetString());
        var cps = sess.GetProperty("checkpoints");
        Assert.Equal(3, cps.GetArrayLength());
        Assert.Equal("lines drawn", cps[0].GetProperty("note").GetString());
        Assert.Equal(3, cps[0].GetProperty("state").GetProperty("count").GetInt32());
        Assert.False(cps[2].TryGetProperty("state", out _)); // nulls are left out
    }

    [Fact]
    public void SessionsWithoutStateStillIngest()
    {
        var s = RecordingReader.Load(Write("nostate.jsonl", Header, """{"t":1,"type":"mark","note":"x"}"""));
        Assert.Null(s.InitialState);
        Assert.Null(s.Steps[0].State);
        var outDir = Path.Combine(_dir, "outns");
        Assert.True(EngineRunner.AnalyzeRecordings([Path.Combine(_dir, "nostate", "recording.jsonl")], outDir).Success);
        var md = File.ReadAllText(Path.Combine(outDir, "recordings", "nostate.md"));
        Assert.DoesNotContain("Start state", md);
    }

    // AutoCAD runs a point prompt inside GetCorner (and GetDistance); older recorders logged both results.
    [Fact]
    public void DropsResultOfPromptNestedInsideTheNextOne()
    {
        var s = RecordingReader.Load(Write("nested.jsonl", Header,
            """{"t":1,"type":"cmd_start","command":"DRAWRECT","origin":"typed/other"}""",
            """{"t":2,"type":"prompt","kind":"point","seq":1,"input":false,"message":"First corner:","status":"OK","value":[10,10,0]}""",
            """{"t":3,"type":"prompt","kind":"point","seq":3,"input":false,"message":"Opposite corner:","status":"OK","value":[40,30,0]}""",
            """{"t":4,"type":"prompt","kind":"corner","seq":2,"input":false,"message":"Opposite corner:","status":"OK","value":[40,30,0]}""",
            """{"t":5,"type":"cmd_end","command":"DRAWRECT"}"""));
        Assert.Equal(new[] { "point", "corner" }, s.Steps[0].Answers.Select(a => a.Kind));
    }

    // GetString with spaces came back as status Keyword in R25.1 (DRAWLABEL "Room 101").
    [Fact]
    public void KeywordResultOfTextPromptIsTypedText()
    {
        var s = RecordingReader.Load(Write("str.jsonl", Header,
            """{"t":1,"type":"cmd_start","command":"DRAWLABEL","origin":"typed/other"}""",
            """{"t":2,"type":"prompt","kind":"string","seq":5,"message":"Label text <Label>:","status":"Keyword","keyword":"Room 101"}""",
            """{"t":3,"type":"cmd_end","command":"DRAWLABEL"}"""));
        Assert.True(s.Steps[0].Answers[0].IsTypedInput);
    }
}
