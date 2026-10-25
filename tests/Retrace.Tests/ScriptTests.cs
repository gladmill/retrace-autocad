using System.Text.RegularExpressions;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>steps.md next to a recording and screenshots (`shot` events) in How-to and manual-test pages. Synthetic data only.</summary>
public class ScriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-script").FullName;
    private readonly string _out = Directory.CreateTempSubdirectory("retrace-test-script-out").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        Directory.Delete(_out, true);
    }

    // a 1x1 PNG
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    const string Header = """{"type":"header","schema":1,"name":"draw test","started":"2026-10-10T00:08:07","autocad":"25.1.0.0","product":"AutoCAD 2026","workspace":"Drafting"}""";

    static readonly string[] WithShots =
    {
        Header,
        """{"n":1,"t":10,"type":"state","state":{"count":0,"byType":{},"layers":["0"],"hash":"e3"}}""",
        """{"n":2,"t":1000,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line","tab":"Home","panel":"Draw"}""",
        """{"n":3,"t":1010,"type":"cmd_start","command":"LINE","origin":"ribbon"}""",
        """{"n":4,"t":1100,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[9,9,0],"input":false}""",
        """{"n":5,"t":1200,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[0,0,0],"input":true}""",
        """{"n":6,"t":1300,"type":"prompt","kind":"point","message":"Specify next point or [Undo]:","keywords":["Undo"],"status":"OK","value":[5,5,0],"input":true}""",
        """{"n":7,"t":1400,"type":"cmd_end","command":"LINE"}""",
        """{"n":8,"t":2000,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
        """{"n":9,"t":2010,"type":"modal_enter","title":""}""",
        """{"n":10,"t":2300,"type":"modal_title","title":"Draw grid"}""",
        """{"n":11,"t":3000,"type":"modal_leave"}""",
        """{"n":12,"t":3010,"type":"cmd_end","command":"DRAWGRID"}""",
        """{"n":13,"t":4000,"type":"mark","note":"line drawn","state":{"count":1,"byType":{"LINE":1},"layers":["0"],"hash":"ab"}}""",
        """{"n":14,"t":4001,"type":"shot","for":2,"file":"screenshots/002-ribbon.png","what":"window"}""",
        """{"n":15,"t":4002,"type":"shot","for":4,"file":"screenshots/004-noise.png","what":"window"}""",
        """{"n":16,"t":4003,"type":"shot","for":6,"file":"screenshots/006-prompt.png","what":"window"}""",
        """{"n":17,"t":4004,"type":"shot","for":7,"file":"screenshots/007-end.png","what":"window"}""",
        """{"n":18,"t":4005,"type":"shot","for":10,"file":"screenshots/010-dialog.png","what":"dialog"}""",
        """{"n":19,"t":4006,"type":"shot","for":13,"file":"screenshots/013-mark.png","what":"window"}""",
        """{"n":20,"t":4007,"type":"shot","for":999,"file":"screenshots/orphan.png","what":"window"}""",
        """{"n":21,"t":5000,"type":"session_end"}""",
    };

    /// <summary>A test folder `name` with recording.jsonl and (with pngs) the pictures in screenshots\. Returns the recording.</summary>
    private string Session(string name, string[] lines, bool pngs = true)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "recording.jsonl");
        File.WriteAllLines(file, lines);
        if (pngs)
        {
            Directory.CreateDirectory(Path.Combine(folder, "screenshots"));
            foreach (var f in new[] { "002-ribbon", "004-noise", "006-prompt", "007-end", "010-dialog", "013-mark", "orphan" })
                File.WriteAllBytes(Path.Combine(folder, "screenshots", f + ".png"), Png);
        }
        return file;
    }

    private static string[] Shots(string[] lines, string name) => lines;

    private static int Idx(string text, string needle)
    {
        var i = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"missing: {needle}\n{text}");
        return i;
    }

    [Fact]
    public void StepsAreWrittenNextToTheRecordingWithShotsUnderTheirSteps()
    {
        var file = Session("s1", Shots(WithShots, "s1"));
        Assert.True(EngineRunner.WriteSteps(file).Success);
        var md = File.ReadAllText(Path.Combine(_dir, "s1", "steps.md"));

        Assert.StartsWith("# draw test\n", md);
        Assert.Contains("Recorded 2026-10-10, with AutoCAD 2026 25.1.0.0, workspace Drafting.", md);
        Assert.Contains("**Start with:** a new empty drawing.", md);
        Assert.Contains("1. Click **Home › Draw › Line**", md);
        Assert.Contains("   - Pick the first point (recorded at 0, 0)", md);
        Assert.Contains("   - Pick the next point (recorded at 5, 5)", md);
        Assert.Contains("2. Type `DRAWGRID` on the command line and press Enter", md);
        Assert.Contains("   - A dialog opens: **Draw grid**.", md);
        Assert.Contains("3. **Check:** line drawn — the drawing should now have 1 LINE", md);

        // each picture sits under its own step, in order; alt text is the step text
        var ribbon = Idx(md, "![Click Home › Draw › Line](screenshots/step-01-click.png)");
        var pick = Idx(md, "(screenshots/step-01-answer-1.png)");
        var end = Idx(md, "(screenshots/step-01-end.png)");
        var dialog = Idx(md, "![A dialog opens: Draw grid. Fill it in and confirm.](screenshots/step-02-dialog-before.png)");
        var mark = Idx(md, "(screenshots/step-03-check.png)");
        Assert.True(Idx(md, "1. Click") < ribbon && ribbon < Idx(md, "- Pick the first point"));
        Assert.True(Idx(md, "- Pick the next point") < pick && pick < end && end < Idx(md, "2. Type"));
        Assert.True(Idx(md, "2. Type") < Idx(md, "- A dialog") && Idx(md, "- A dialog") < dialog && dialog < Idx(md, "3. **Check"));
        Assert.True(Idx(md, "3. **Check") < mark);

        // mouse noise and its picture, and a picture of no event, are absent
        Assert.DoesNotContain("(9, 9)", md);
        Assert.DoesNotContain("noise", md);
        Assert.DoesNotContain("orphan", md);
        Assert.Contains("## Result", md);
        Assert.Contains("- Check 1 (step 3): line drawn: [ ] pass  [ ] fail", md);
        Assert.False(Directory.Exists(Path.Combine(_dir, "s1", "recordings")), "no recordings folder is written");
    }

    [Fact]
    public void StepsWithoutShotsAreTextOnlyAndRewrittenFromScratch()
    {
        var plain = WithShots.Where(l => !l.Contains("\"shot\"")).ToArray();
        var file = Session("s2", plain, pngs: false);
        var scriptPath = Path.Combine(_dir, "s2", "steps.md");
        File.WriteAllText(scriptPath, "old owner text");
        Assert.True(EngineRunner.WriteSteps(file).Success);
        var md = File.ReadAllText(scriptPath);
        Assert.DoesNotContain("![", md);
        Assert.DoesNotContain("old owner text", md);
        Assert.DoesNotContain("<!--", md);
        Assert.Contains("1. Click **Home › Draw › Line**", md);
    }

    [Fact]
    public void SessionsWithoutNumbersKeepWorking()
    {
        var lines = WithShots.Select(l => Regex.Replace(l, "\"n\":\\d+,", "")).ToArray();
        var file = Session("s3", Shots(lines, "s3"));
        Assert.True(EngineRunner.WriteSteps(file).Success);
        var md = File.ReadAllText(Path.Combine(_dir, "s3", "steps.md"));
        Assert.Contains("1. Click **Home › Draw › Line**", md);
        Assert.DoesNotContain("![", md); // no event numbers: nothing to attach shots to
    }

    [Fact]
    public void MissingScreenshotFileIsLeftOut()
    {
        var file = Session("s4", Shots(WithShots, "s4"), pngs: false);
        Assert.True(EngineRunner.WriteSteps(file).Success);
        Assert.DoesNotContain("![", File.ReadAllText(Path.Combine(_dir, "s4", "steps.md")));
    }

    [Fact]
    public void NonEmptyStartStateIsSummarised()
    {
        var lines = WithShots.ToArray();
        lines[1] = """{"n":1,"t":10,"type":"state","state":{"count":3,"byType":{"LINE":2,"CIRCLE":1},"layers":["0","DIM"],"hash":"e3"}}""";
        var file = Session("s5", Shots(lines, "s5"));
        Assert.True(EngineRunner.WriteSteps(file).Success);
        var md = File.ReadAllText(Path.Combine(_dir, "s5", "steps.md"));
        Assert.Contains("**Start with:** a drawing like the one recorded at the start (drawing: 2 LINE, 1 CIRCLE; layers: 0, DIM)", md);
        Assert.DoesNotContain("new empty drawing", md);
    }

    [Fact]
    public void WriteStepsRejectsAFolder()
    {
        Assert.False(EngineRunner.WriteSteps(_dir).Success);
    }

    [Fact]
    public void ScriptAndHowToShareTheWording()
    {
        var file = Session("s6", Shots(WithShots, "s6"));
        EngineRunner.WriteSteps(file);
        var script = File.ReadAllText(Path.Combine(_dir, "s6", "steps.md"));

        var m = EmitTests.SampleModel();
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = _out, Sessions = [file] });
        var howto = File.ReadAllText(Path.Combine(_out, "guide", "howto", "draw-test.md"));

        foreach (var line in new[]
        {
            "2. Type `DRAWGRID` on the command line and press Enter",
            "   - Pick the first point (recorded at 0, 0)",
            "   - Pick the next point (recorded at 5, 5)",
            "   - A dialog opens: **Draw grid**. Fill it in and confirm.",
        })
        {
            Assert.Contains(line, script);
            Assert.Contains(line, howto);
        }
        Assert.Contains("Click **Home › Draw › Line**", script);
        Assert.Contains("Click **Home › Draw › Line**", howto);
    }

    [Fact]
    public void HowToCopiesScreenshotsIdempotently()
    {
        var file = Session("s7", Shots(WithShots, "s7"));
        void Run()
        {
            var m = EmitTests.SampleModel();
            Linker.AddRibbonHandlers(m);
            Linker.Link(m);
            Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = _out, Sessions = [file] });
        }
        Run();
        var page = Path.Combine(_out, "guide", "howto", "draw-test.md");
        var md = File.ReadAllText(page);
        foreach (var f in new[] { "002-ribbon", "006-prompt", "007-end", "010-dialog", "013-mark" })
        {
            Assert.Contains($"(../images/howto/draw-test/{f}.png)", md);
            Assert.True(File.Exists(Path.Combine(_out, "guide", "images", "howto", "draw-test", f + ".png")));
        }
        Assert.DoesNotContain("noise", md);
        Assert.DoesNotContain("orphan", md);

        // the recordings page shows them too, from a folder inside the docs folder
        var test = File.ReadAllText(Path.Combine(_out, "recordings", "s7.md"));
        Assert.Contains("(images/s7/002-ribbon.png)", test);
        Assert.True(File.Exists(Path.Combine(_out, "recordings", "images", "s7", "013-mark.png")));

        // owner text survives, a rerun changes nothing
        File.WriteAllText(page, md.Replace("_What does this procedure achieve", "Owner wrote this.\n\n_What does this procedure achieve"));
        Run();
        Assert.Contains("Owner wrote this.", File.ReadAllText(page));
        var before = Directory.GetFiles(_out, "*", SearchOption.AllDirectories).Where(f => f.Contains("guide") || f.Contains("recordings")).ToDictionary(f => f, f => File.ReadAllBytes(f));
        Run();
        var after = Directory.GetFiles(_out, "*", SearchOption.AllDirectories).Where(f => f.Contains("guide") || f.Contains("recordings")).ToDictionary(f => f, f => File.ReadAllBytes(f));
        foreach (var (f, bytes) in after.Where(kv => before.ContainsKey(kv.Key)))
            Assert.True(before[f].AsSpan().SequenceEqual(bytes), f);
        Assert.Equal(before.Count, after.Count);
    }
}
