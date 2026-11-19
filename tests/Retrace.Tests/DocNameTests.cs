using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Retrace.Core;
using Retrace.Engine;
using Retrace.Engine.Recordings;
using Retrace.Engine.Util;

namespace Retrace.Tests;

/// <summary>The search every document test reuses: no part of an Office file, and no text, has the product's name in any letter case (part names and the bytes of every part, as UTF-8 and as UTF-16).</summary>
public static class DocNameGuard
{
    static byte[] Lower(byte[] b) { var c = (byte[])b.Clone(); for (int i = 0; i < c.Length; i++) if (c[i] >= 'A' && c[i] <= 'Z') c[i] += 32; return c; }

    /// <summary>True when <paramref name="bytes"/> has "retrace" in any case as ASCII text or as UTF-16 text.</summary>
    public static bool HasName(byte[] bytes)
    {
        var low = Lower(bytes);
        return low.AsSpan().IndexOf(Encoding.ASCII.GetBytes("retrace")) >= 0 || low.AsSpan().IndexOf(Encoding.Unicode.GetBytes("retrace")) >= 0;
    }

    /// <summary>Fails when a part of the Office file <paramref name="file"/> (its name or its bytes) has the name. Returns the part names that were searched.</summary>
    public static List<string> AssertNoName(string file)
    {
        var parts = new List<string>();
        using var zip = ZipFile.OpenRead(file);
        foreach (var e in zip.Entries)
        {
            parts.Add(e.FullName);
            Assert.False(DocText.MentionsProduct(e.FullName), $"{Path.GetFileName(file)}: part name {e.FullName}");
            using var ms = new MemoryStream();
            using (var s = e.Open()) s.CopyTo(ms);
            var bytes = ms.ToArray();
            if (HasName(bytes))
            {
                var utf8 = Encoding.UTF8.GetString(bytes);
                var at = utf8.IndexOf("retrace", StringComparison.OrdinalIgnoreCase);
                Assert.Fail($"{Path.GetFileName(file)}: {e.FullName}: ...{(at >= 0 ? utf8.Substring(Math.Max(0, at - 80), Math.Min(160, utf8.Length - Math.Max(0, at - 80))) : "(UTF-16)")}...");
            }
        }
        Assert.NotEmpty(parts);
        return parts;
    }

    /// <summary>The text of one part of an Office file.</summary>
    public static string PartText(string file, string part)
    {
        using var zip = ZipFile.OpenRead(file);
        using var rd = new StreamReader(zip.GetEntry(part)!.Open());
        return rd.ReadToEnd();
    }

    /// <summary>All XML parts of an Office file as one string.</summary>
    public static string AllXml(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        return string.Concat(zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal)).Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }));
    }
}

/// <summary>The guard: no document a tester, trainee or customer opens (steps.md, steps.docx, steps.xlsx, the evidence workbook, the training document, the suite report) has the product's name in it, in any letter case:
/// not in a part's text, not in a part's file name, not in the properties. The program's own messages are worded without the name at their sources; content a person recorded or named stays as it is. Synthetic data only.</summary>
public class DocNameTests : IDisposable
{
    // the folder names here must not have the product's name in them (the tests run from a temp folder; "retrace-test-..." would trip the guard on the path)
    readonly string _root = Directory.CreateTempSubdirectory("doc-guard-test").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    static string[] Recording(string name) =>
    [
        $$"""{"type":"header","schema":1,"name":"{{name}}","started":"2026-10-10T00:08:07.1234567+02:00","autocad":"25.1.0.0","product":"AutoCAD 2026","workspace":"Drafting"}""",
        """{"n":1,"t":10,"type":"state","state":{"count":0,"byType":{},"layers":["0"],"hash":"e3"}}""",
        // the person went to the product's own tab and typed its own commands: none of that is a step, and a prompt inside one belongs to no step
        """{"n":2,"t":500,"type":"ribbon","id":"RETRACEMARK","text":"Mark","macro":"^C^C_RETRACEMARK","tab":"Retrace","tabId":"RETRACE_TAB","panel":"Test"}""",
        """{"n":3,"t":510,"type":"cmd_start","command":"RETRACEMARK","origin":"ribbon"}""",
        """{"n":4,"t":515,"type":"prompt","kind":"string","input":true,"message":"Enter a note:","status":"OK","value":"own"}""",
        """{"n":5,"t":520,"type":"cmd_end","command":"RETRACEMARK"}""",
        """{"n":6,"t":1000,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line","tab":"Home","panel":"Draw"}""",
        """{"n":7,"t":1010,"type":"cmd_start","command":"LINE","origin":"ribbon"}""",
        """{"n":8,"t":1200,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[0,0,0],"input":true,"by":"mouse"}""",
        """{"n":9,"t":1300,"type":"prompt","kind":"point","message":"Specify next point or [Undo]:","keywords":["Undo"],"status":"OK","value":[5,5,0],"input":true,"by":"mouse"}""",
        """{"n":10,"t":1400,"type":"cmd_end","command":"LINE"}""",
        """{"n":11,"t":2000,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
        """{"n":12,"t":2010,"type":"modal_enter","title":""}""",
        """{"n":13,"t":2300,"type":"modal_title","title":"Draw grid"}""",
        """{"n":14,"t":2900,"type":"dialog_values","for":13,"title":"Draw grid","source":"win32","fields":[{"key":"id:1","label":"Layer name","kind":"edit","before":"GRID","after":"RETRACE_GRID"}],"read":4,"closedWith":"ok"}""",
        """{"n":15,"t":3000,"type":"modal_leave"}""",
        """{"n":16,"t":3010,"type":"cmd_end","command":"DRAWGRID"}""",
        """{"n":17,"t":3500,"type":"palette_values","palette":"Box","set":"Samples","source":"win32","fields":[{"key":"id:4654070","label":"Width","kind":"edit","before":"20","after":"35"}],"read":5}""",
        """{"n":18,"t":3510,"type":"palette_click","palette":"Box","set":"Samples","button":"Draw box"}""",
        """{"n":19,"t":3800,"type":"statusbar_click","text":"Sample TB: edit","tooltip":"Edit the sample settings","index":61,"kind":"pane","own":false,"button":"left"}""",
        """{"n":20,"t":4000,"type":"mark","note":"line drawn","state":{"count":1,"byType":{"LINE":1},"layers":["0"],"hash":"ab"}}""",
        """{"n":21,"t":4001,"type":"shot","for":6,"file":"screenshots/006-ribbon.png","what":"window"}""",
        """{"n":22,"t":4002,"type":"shot","for":13,"file":"screenshots/013-dialog.png","what":"dialog"}""",
        """{"n":23,"t":4003,"type":"shot","for":18,"file":"screenshots/018-palette.png","what":"window"}""",
        """{"n":24,"t":4004,"type":"shot","for":20,"file":"screenshots/020-mark.png","what":"window"}""",
        """{"n":25,"t":5000,"type":"session_end"}""",
    ];

    static void WritePng(string path, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)200);
        Retrace.Core.Png.Write(path, w, h, px);
    }

    /// <summary>A test folder with the recording and its pictures; its steps.md is written, and steps.docx / steps.xlsx beside it.</summary>
    string Folder(string testName, string folderName = "2026-10-10_0008_draw-test")
    {
        var dir = Path.Combine(_root, "tests", folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "recording.jsonl"), Recording(testName));
        foreach (var f in new[] { "006-ribbon", "013-dialog", "018-palette", "020-mark" }) WritePng(Path.Combine(dir, "screenshots", f + ".png"), 320, 200);
        var r = EngineRunner.WriteSteps(Path.Combine(dir, "recording.jsonl"), docx: true, xlsx: true);
        Assert.True(r.Success, r.Error);
        return dir;
    }

    static List<string> AssertNoName(string file) => DocNameGuard.AssertNoName(file);

    [Fact]
    public void Steps_md_steps_docx_and_steps_xlsx_have_no_product_name_in_any_part()
    {
        var dir = Folder("draw test");
        var md = File.ReadAllText(Path.Combine(dir, "steps.md"));
        Assert.False(DocText.MentionsProduct(md.Replace("RETRACE_GRID", "")), md);   // the one hit allowed: a layer name the person typed (user content)
        var docx = AssertNoNameExceptUserContent(Path.Combine(dir, "steps.docx"));
        var xlsx = AssertNoNameExceptUserContent(Path.Combine(dir, "steps.xlsx"));
        Assert.Contains("word/document.xml", docx);
        Assert.Contains("xl/workbook.xml", xlsx);
        Assert.Contains(xlsx, p => p.Contains("media/"));   // the pictures were in: their file names were searched too
        Assert.Contains(docx, p => p.Contains("media/"));
    }

    /// <summary>Like <see cref="DocNameGuard.AssertNoName"/>, with the one user-content word of the recording (the layer RETRACE_GRID) taken out of the XML first.</summary>
    static List<string> AssertNoNameExceptUserContent(string file)
    {
        var parts = new List<string>();
        using var zip = ZipFile.OpenRead(file);
        foreach (var e in zip.Entries)
        {
            parts.Add(e.FullName);
            Assert.False(DocText.MentionsProduct(e.FullName), e.FullName);
            using var ms = new MemoryStream(); using (var s = e.Open()) s.CopyTo(ms);
            var bytes = ms.ToArray();
            if (e.FullName.EndsWith(".xml", StringComparison.Ordinal)) bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("RETRACE_GRID", ""));
            Assert.False(DocNameGuard.HasName(bytes), $"{Path.GetFileName(file)}: {e.FullName}");
        }
        return parts;
    }

    [Fact]
    public void The_guard_finds_the_name_as_ascii_and_as_utf16_in_any_case()
    {
        Assert.True(DocNameGuard.HasName(Encoding.UTF8.GetBytes("a ReTrAcE b")));
        Assert.True(DocNameGuard.HasName(Encoding.Unicode.GetBytes("a RETRACE b")));
        Assert.False(DocNameGuard.HasName(Encoding.Unicode.GetBytes("nothing here")));
    }

    [Fact]
    public void Steps_md_starts_with_the_title_the_recorded_line_the_start_line_and_the_steps()
    {
        var dir = Folder("draw test");
        var md = File.ReadAllText(Path.Combine(dir, "steps.md"));
        Assert.DoesNotContain("Files in this folder", md);
        Assert.DoesNotContain("Run test", md);
        var lines = md.Split('\n').Where(l => l.Trim().Length > 0).Take(5).ToList();
        Assert.Equal("# draw test", lines[0]);
        Assert.StartsWith("Recorded 2026-10-10, with AutoCAD", lines[1]);
        Assert.StartsWith("**Start with:**", lines[2]);
        Assert.StartsWith("Do the steps in order.", lines[3]);
        Assert.Equal("## Steps", lines[4]);
        // the product's own tab and command are not steps, and the prompt inside the own command answered no step
        var steps = md.Split("## Steps")[1].Split("## Result")[0];
        Assert.StartsWith("1. Click **Line**", steps.TrimStart());
        Assert.DoesNotContain("Enter a note", steps);
    }

    [Fact]
    public void Plan_and_the_script_agree_that_the_tools_own_commands_are_no_step_and_own_prompts_attach_to_none()
    {
        var events = Recording("draw test").Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => e.GetProperty("type").GetString() != "header").ToList();
        var steps = Plan.Build(events);
        Assert.DoesNotContain(steps, s => (s.Command ?? "").StartsWith("RETRACE", StringComparison.OrdinalIgnoreCase) || (s.Title ?? "").Contains("RETRACE"));
        Assert.Equal(new[] { "ribbon", "dialog", "palette", "statusbar", "mark" }.Length, steps.Count(s => s.Kind is "ribbon" or "dialog" or "palette" or "statusbar" or "mark"));
        // a hand-made recording: the own command typed first, with a prompt, then a real command. The prompt of the own command must not become an answer of the step before it.
        var hand = new[]
        {
            """{"t":100,"n":1,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""",
            """{"t":120,"n":2,"type":"prompt","kind":"point","message":"Specify center point:","status":"OK","value":[1,1,0],"input":true}""",
            """{"t":150,"n":3,"type":"cmd_end","command":"CIRCLE"}""",
            """{"t":200,"n":4,"type":"cmd_start","command":"RETRACEMARK","origin":"typed/other"}""",
            """{"t":210,"n":5,"type":"prompt","kind":"string","message":"Enter a note:","status":"OK","value":"x","input":true}""",
            """{"t":220,"n":6,"type":"cmd_end","command":"RETRACEMARK"}""",
        };
        var planSteps = Plan.Build(hand.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        Assert.Single(planSteps);
        Assert.Equal(1, planSteps[0].Answers.Count);
        var dir = Path.Combine(_root, "tests", "2026-10-10_0100_hand"); Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "recording.jsonl"), new[] { Recording("hand")[0] }.Concat(hand));
        var one = RecordingReader.Load(Path.Combine(dir, "recording.jsonl"));
        RecordingAnalysis.Run([one], null);
        Assert.Single(one.Steps);
        Assert.Equal(1, one.Steps[0].Answers.Count);
    }

    [Fact]
    public void The_Word_document_does_not_say_how_to_run_the_test_with_the_tool()
    {
        var dir = Folder("draw test");
        var xml = DocNameGuard.PartText(Path.Combine(dir, "steps.docx"), "word/document.xml");
        Assert.DoesNotContain("Files in this folder", xml);
        Assert.DoesNotContain("automatically", xml);
        Assert.DoesNotContain("recording.jsonl", xml);
    }

    // an older steps.md, as the 0.6.18 writer made it: the Files table and the how-to-run line the test's author reads
    const string OldSteps = """
        # old test

        Recorded 2026-10-10, with AutoCAD 2026 25.1.0.0, workspace Drafting.

        **Start with:** a new, empty drawing.

        Do the steps in order. A picture under a step shows what the screen looked like when the test was recorded.

        ## Files in this folder

        | File | What it is for |
        |---|---|
        | `steps.md` | This page: the steps to follow by hand. It is written again each time the recording is processed, so do not edit it. |
        | `recording.jsonl` | The recording itself, one event per line. This is what **Run test** replays. |
        | `screenshots` | Pictures of the screen, named after the step that shows them. `unused` holds the ones this page does not show. |
        | `results` | One file per automatic run, named by date and verdict (`2026-10-10_03-15-02_PASS.json`). `latest.json` is a copy of the newest. |

        To run this test automatically: in AutoCAD open the **Retrace** tab, click **Run test** and pick `recording.jsonl` in this folder.

        ## Steps

        1. Click **Line**

           *Ribbon › Home tab › Draw panel*

        ## Result

        Overall: [ ] pass  [ ] fail
        """;

    [Fact]
    public void An_old_steps_md_is_exported_without_the_files_table_and_the_how_to_run_line()
    {
        var dir = Path.Combine(_root, "tests", "2026-10-10_0200_old");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "steps.md"), OldSteps);
        var r = EngineRunner.Export(dir, true, true);
        Assert.True(r.Success, r.Error);
        var docx = Path.Combine(dir, "steps.docx"); var xlsx = Path.Combine(dir, "steps.xlsx");
        AssertNoName(docx); AssertNoName(xlsx);
        var xml = DocNameGuard.PartText(docx, "word/document.xml");
        Assert.DoesNotContain("Files in this folder", xml);
        Assert.DoesNotContain("automatically", xml);
        Assert.Contains("Click", xml);
        Assert.Contains("Overall", xml);
        var doc = StepsDoc.Load(Path.Combine(dir, "steps.md"));
        Assert.Single(doc.Steps);
        Assert.Equal("old test", doc.Name);
        // a steps.md without the section comes back as it is
        Assert.Equal("# t\n\n## Steps\n", StepsDoc.DropAuthorSection("# t\n\n## Steps\n"));
        Assert.DoesNotContain("Files in this folder", StepsDoc.DropAuthorSection(OldSteps));
        Assert.Contains("## Steps", StepsDoc.DropAuthorSection(OldSteps));
    }

    static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).Substring(0, 16);

    [Fact]
    public void The_steps_stamp_looks_at_the_steps_part_only_and_stamps_of_older_versions_stay_valid()
    {
        var dir = Path.Combine(_root, "tests", "2026-10-10_0300_stamp"); Directory.CreateDirectory(dir);
        var evi = EvidenceFiles.FolderOf(dir); Directory.CreateDirectory(evi);
        File.WriteAllBytes(Path.Combine(evi, "step-1_20261117-100000-000.png"), [1]);
        // an old stamp: the fingerprint of the whole old text, Files section and all
        File.WriteAllText(Path.Combine(evi, ".steps-sha"), Sha(OldSteps));
        Assert.True(EvidenceFiles.StepsUnchanged(dir, OldSteps));                                      // the old text, unchanged
        Assert.True(EvidenceFiles.StepsUnchanged(dir, StepsDoc.DropAuthorSection(OldSteps)));          // the same steps.md written again by this version: no false alarm
        Assert.False(EvidenceFiles.StepsUnchanged(dir, StepsDoc.DropAuthorSection(OldSteps).Replace("Click **Line**", "Click **Circle**")));   // a real change of the steps is still noticed
        // a new stamp: the steps part; a changed intro is no change
        EvidenceFiles.StampSteps(dir, "x");   // keeps the old stamp (pictures exist)
        File.Delete(Path.Combine(evi, ".steps-sha"));
        var text = "# a\n\nintro\n\n## Steps\n\n1. one\n\n## Result\n\nOverall\n";
        EvidenceFiles.StampSteps(dir, text);
        Assert.True(EvidenceFiles.StepsUnchanged(dir, text.Replace("intro", "another intro, longer")));
        Assert.False(EvidenceFiles.StepsUnchanged(dir, text.Replace("1. one", "1. two")));
    }

    [Fact]
    public void Evidence_workbooks_of_a_failed_and_a_passing_run_and_of_a_tester_have_no_product_name()
    {
        var dir = Folder("draw test");
        var doc = StepsDoc.Load(Path.Combine(dir, "steps.md"));
        var pic = Path.Combine(dir, "screenshots", "006-ribbon.png");

        // a failed run: the replay's own messages, as the plan and the rules word them
        var statusClick = new Step { Kind = "command", Command = "LINE", InlineStatusClicks = { "Click **X**" } };
        var failed = new Dictionary<int, StepOutcome>
        {
            [1] = new("Pass", null, pic),
            [2] = new("Fail", EvidenceBook.Scrub(Plan.Problem(statusClick)), pic),
            [3] = new("Blocked", EvidenceBook.Scrub(ReplayRules.ChainAnswersNeedPerson(new Step { Kind = "ribbon", Title = "Insert", ChainClick = true, Answers = { new Answer("point", "OK", null, null, "Specify a point:", true, 1, 5) } })), null),
        };
        var info = EvidenceBook.AutomatedInfo(new DateTime(2026, 11, 18, 9, 30, 0), "25.1", "FAIL", 3, 5, anyWaited: true);
        info.Add(("Message", EvidenceBook.Scrub("drawing not found: " + DocText.UnderTests(@"C:\temp\retrace\tests\2026-10-10_0008_draw-test\start.dwg", @"C:\temp\retrace\tests"))));
        var ev = EvidenceBook.FromOutcomes(doc, failed, info, "Automated run", "2026-11-18");
        var failPath = EvidenceBook.Write(doc, dir, ev, new DateTime(2026, 11, 18, 9, 31, 0));
        AssertNoNameExceptUserContent(failPath);
        Assert.Contains("the replay cannot click on the status bar", DocNameGuard.PartText(failPath, "xl/worksheets/sheet1.xml"));
        Assert.Contains("drawing not found: 2026-10-10_0008_draw-test\\start.dwg", DocNameGuard.PartText(failPath, "xl/worksheets/sheet2.xml"));

        var passed = EvidenceBook.FromOutcomes(doc, new Dictionary<int, StepOutcome> { [1] = new("Pass", null, pic), [2] = new("Pass", null, pic) },
            EvidenceBook.AutomatedInfo(new DateTime(2026, 11, 18, 9, 30, 0), "25.1", "PASS", 5, 5), "Automated run", "2026-11-18");
        AssertNoNameExceptUserContent(EvidenceBook.Write(doc, dir, passed, new DateTime(2026, 11, 18, 9, 45, 0)));

        // the tester's own: pictures from evidence\ and a result and a note typed in the window
        Directory.CreateDirectory(Path.Combine(dir, "evidence"));
        var tester = EvidenceBook.FromTester(doc, dir, new Dictionary<string, ManualRow> { ["1"] = new("Pass", "fine") }, "A. Tester", "2026-11-18");
        AssertNoNameExceptUserContent(EvidenceBook.Write(doc, dir, tester, new DateTime(2026, 11, 18, 10, 0, 0)));

        var text = string.Join("\n", info.Select(i => i.Value));
        Assert.Contains("Automated run on AutoCAD 25.1, 2026-11-18 09:30", text);
        Assert.DoesNotContain("9.9.9", text);
    }

    [Fact]
    public void The_test_folder_is_named_not_given_as_a_path()
    {
        var dir = Folder("draw test");
        using var xl = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(Path.Combine(dir, "steps.xlsx"), false);
        var wb = xl.WorkbookPart!;
        var info = (DocumentFormat.OpenXml.Packaging.WorksheetPart)wb.GetPartById(wb.Workbook!.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Last().Id!);
        var text = info.Worksheet!.InnerText;
        Assert.Contains("2026-10-10_0008_draw-test", text);
        Assert.DoesNotContain(_root, text);
        Assert.Equal("t1", DocText.FolderName(@"C:\temp\retrace\tests\t1"));
        Assert.Equal("t1", DocText.FolderName(@"C:\temp\retrace\tests\t1\"));
        Assert.Equal("t1", DocText.FolderName("/x/retrace/t1"));
    }

    [Fact]
    public void A_path_under_the_tests_folder_is_the_test_folders_name_and_the_rest_any_other_path_stays()
    {
        Assert.Equal(@"2026-10-10_0008_t\start.dwg", DocText.UnderTests(@"C:\temp\retrace\tests\2026-10-10_0008_t\start.dwg", @"C:\temp\retrace\tests"));
        Assert.Equal(@"2026-10-10_0008_t\start.dwg", DocText.UnderTests(@"c:\TEMP\retrace\tests\2026-10-10_0008_t\start.dwg", @"C:\temp\retrace\tests\"));
        Assert.Equal("t/x.dwg", DocText.UnderTests("D:/tests/t/x.dwg", @"D:\tests"));
        Assert.Equal(@"D:\work\retrace\plan.dwg", DocText.UnderTests(@"D:\work\retrace\plan.dwg", @"C:\temp\retrace\tests"));   // a person's own path, with their own word in it: as it is
        Assert.Equal(@"C:\temp\retrace\testsX\a.dwg", DocText.UnderTests(@"C:\temp\retrace\testsX\a.dwg", @"C:\temp\retrace\tests"));
        Assert.Equal("", DocText.UnderTests(null, @"C:\x"));
    }

    [Fact]
    public void An_open_drawing_step_keeps_a_persons_own_path_whole()
    {
        // the path rule itself (relative to the tests folder when under it) is the UnderTests test above; here a drawing elsewhere, with the word in its path, stays exactly as recorded
        var dir = Path.Combine(_root, "tests", "2026-10-10_0400_open"); Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "recording.jsonl"),
        [
            Recording("open")[0],
            """{"n":1,"t":100,"type":"doc_created","doc":"plan.dwg","path":"C:\\work\\retrace\\plan.dwg"}""",
            """{"n":2,"t":200,"type":"session_end"}""",
        ]);
        var one = RecordingReader.Load(Path.Combine(dir, "recording.jsonl")); RecordingAnalysis.Run([one], null);
        var note = one.Steps.Single(s => s.Kind == "doc").Note;
        Assert.Equal(@"Open drawing C:\work\retrace\plan.dwg", note);
    }

    [Fact]
    public void A_training_document_built_from_a_real_plan_has_no_product_name()
    {
        var events = Recording("draw test").Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => e.GetProperty("type").GetString() != "header").ToList();
        var steps = Plan.Build(events);
        var test = new TrainingTest { Name = "draw test", Folder = "tests/draw-test", Verdict = "PASS" };
        foreach (var e in TdocSplit.Entries(steps))
            test.Steps.Add(new TrainingStep { Number = test.Steps.Count + 1, Kind = TdocSplit.Kind(e), Title = TdocSplit.Title(e), Instructions = TdocSplit.Instructions(e), Where = TdocSplit.Where(e) });
        Assert.True(test.Steps.Count >= 4);
        var m = new TrainingManifest { Title = "Training", CreatedUtc = "2026-10-30T09:15:00Z", RetraceVersion = "1.2.3", Tests = [test] };
        var docx = Path.Combine(_root, "plan.docx");
        TrainingDocx.Write(m, _root, docx);
        AssertNoNameExceptUserContent(docx);
        Assert.Contains("Created 2026-10-30 09:15 UTC", DocNameGuard.PartText(docx, "word/document.xml"));
    }

    [Fact]
    public void A_training_document_has_no_product_name_when_the_message_comes_from_the_replay()
    {
        WritePng(Path.Combine(_root, "t", "pictures", "s1.png"), 64, 40);
        var m = new TrainingManifest
        {
            Title = "Kit training", CreatedUtc = "2026-10-30T09:15:00Z", RetraceVersion = "1.2.3",
            Tests =
            [
                new TrainingTest
                {
                    Name = "Insert kit block", Folder = "tests/insert-kit-block", Verdict = "FAIL", Message = Plan.Problem(new Step { Kind = "command", Command = "X", InlineStatusClicks = { "Click **X**" } }),
                    Steps =
                    [
                        new TrainingStep { Number = 1, Kind = "ribbon", Title = "Insert > Create Block", Instructions = ["Click Create Block on the Insert tab."], Pictures = [new TrainingPicture { File = "pictures/s1.png", Caption = "The Insert tab" }] },
                        new TrainingStep { Number = 2, Kind = "mark", Title = "Checkpoint", Instructions = ["Checkpoint: block exists."],
                            Checkpoint = new TrainingCheckpoint { Index = 1, Note = "block exists", Pass = false, Differences = ["1 object less"] } },
                    ],
                },
            ],
        };
        var docx = Path.Combine(_root, "t", "tdoc.docx");
        Assert.Equal(1, TrainingDocx.Write(m, Path.Combine(_root, "t"), docx));
        var parts = AssertNoName(docx);
        Assert.Contains(parts, p => p.Contains("media/"));
        var xml = DocNameGuard.PartText(docx, "word/document.xml");
        Assert.Contains("Created 2026-10-30 09:15 UTC", xml);
        Assert.DoesNotContain("made with", xml);
        Assert.Contains("the replay cannot click on the status bar", xml);
    }

    [Fact]
    public void Content_a_person_recorded_or_named_reaches_notes_the_Tdoc_and_the_suite_report_unchanged()
    {
        // a test named after the product, a layer, a palette title and a command of another add-in with the word in them: user content, never rewritten
        var dir = Folder("retrace-demo", "2026-10-10_0008_retrace-demo");
        Assert.StartsWith("# retrace-demo", File.ReadAllText(Path.Combine(dir, "steps.md")));
        Assert.Contains("RETRACE_GRID", File.ReadAllText(Path.Combine(dir, "steps.md")));
        foreach (var file in new[] { "steps.docx", "steps.xlsx" })
        {
            var all = DocNameGuard.AllXml(Path.Combine(dir, file));
            Assert.Contains("retrace-demo", all);
        }
        Assert.Contains("RETRACE_GRID", DocNameGuard.AllXml(Path.Combine(dir, "steps.docx")));

        var user = new[] { "Layer RETRACE_GRID is frozen", "Palette \"Retrace Details\" did not open", "Retracement and retraced are words", "RETRACEX (another add-in's command) did not finish" };
        // evidence workbook notes: a note is the program's wording plus the user's names; the user's part is untouched
        var doc = StepsDoc.Load(Path.Combine(dir, "steps.md"));
        var outcomes = new Dictionary<int, StepOutcome> { [1] = new("Fail", EvidenceBook.Scrub("The step failed: " + string.Join("; ", user)), null) };
        var ev = EvidenceBook.FromOutcomes(doc, outcomes, [], "Automated run", "2026-11-18");
        var xlsx = EvidenceBook.Write(doc, dir, ev, new DateTime(2026, 11, 18, 9, 31, 0));
        var sheet = DocNameGuard.PartText(xlsx, "xl/worksheets/sheet1.xml");
        foreach (var u in user) Assert.Contains(XmlText(u), sheet);
        // the Tdoc message
        var m = new TrainingManifest { Title = "T", CreatedUtc = "2026-10-30T09:15:00Z", Tests = [new TrainingTest { Name = "retrace-demo", Verdict = "FAIL", Message = string.Join("; ", user), Steps = [new TrainingStep { Number = 1, Kind = "command", Title = "X", Instructions = ["Type X."] }] }] };
        var docx = Path.Combine(_root, "user.docx");
        TrainingDocx.Write(m, _root, docx);
        var wxml = DocNameGuard.PartText(docx, "word/document.xml");
        foreach (var u in user) Assert.Contains(XmlText(u), wxml);
        // the suite report
        var s = new SuiteResult { Stamp = "2026-11-18_09-30-00", CoreVersion = "9.9.9" };
        s.Tests.Add(new SuiteTest { Name = "retrace-demo", Verdict = "FAIL", Message = string.Join("; ", user), Result = @"C:\temp\retrace\tests\retrace-demo\results\2026-11-18_09-30-05_FAIL.json" });
        var md = s.ToMarkdown();
        foreach (var u in user) Assert.Contains(u, md);
        Assert.Contains("| retrace-demo | FAIL |", md);
    }

    /// <summary>The per-row evidence (EvidenceRows: shots and bullets per row) for the guard recording in several shapes, written as workbooks: no row's note, and nothing else, has the product's name.</summary>
    [Fact]
    public void Per_row_evidence_notes_and_pictures_have_no_product_name_in_any_shape()
    {
        var dir = Folder("draw test");
        var pic = Path.Combine(dir, "screenshots", "006-ribbon.png");
        var recording = Path.Combine(dir, "recording.jsonl");
        var one = RecordingReader.Load(recording); RecordingAnalysis.Run([one], null);
        var numberedT = new List<long>(); var bullets = new List<IReadOnlyList<Retrace.Engine.Emit.WordingBullet>>();
        foreach (var st in one.Steps)
        {
            var l = new List<Retrace.Engine.Emit.WordingBullet>();
            if (Retrace.Engine.Emit.StepWording.Render(st, numberedT.Count + 1, new Retrace.Engine.Emit.WordingContext { Script = true, Bullets = l }) == null) continue;
            numberedT.Add(st.T); bullets.Add(l);
        }
        var md = Path.Combine(dir, "steps.md");
        var steps = Plan.Build(Plan.ReadEvents(recording)).Where(s => s.Kind != "tab").ToList();
        int n = 0;
        void Write(string name, string stepsText, List<StepShots?> shots, ReplayResult r, int endStep, EvidenceEnd end)
        {
            File.WriteAllText(md, stepsText);
            var doc = StepsDoc.Load(md);
            var shape = EvidenceRows.Shape(doc, numberedT.Count);
            var o = EvidenceMapping.Outcomes(steps, r, new string?[steps.Count], numberedT, endStep, end, shots, shape == null ? null : bullets, shape?.RowCounts, shape?.SubRowsFilled);
            var ev = EvidenceBook.FromOutcomes(doc, o, EvidenceBook.AutomatedInfo(new DateTime(2026, 11, 19, 9, 30, 0), "25.1", r.Verdict, r.StepsRun, r.StepsTotal), "Automated run", "2026-11-19");
            var path = EvidenceBook.Write(doc, dir, ev, new DateTime(2026, 11, 19, 9, 31, ++n));
            AssertNoNameExceptUserContent(path);
            foreach (var row in ev.Rows.Values) Assert.False(DocText.MentionsProduct((row.Notes ?? "").Replace("RETRACE_GRID", "")), $"{name}: {row.Notes}");
        }
        ReplayResult Res(string verdict, params (int Index, string Status, string? Message)[] list)
        {
            var r = new ReplayResult { Verdict = verdict, Message = "" };
            foreach (var (i, st, m) in list) r.Steps.Add(new StepResult { Index = i, Kind = steps[i - 1].Kind, Status = st, Message = m });
            return r;
        }
        List<StepShots?> Shots(Func<int, StepShots?> f) => steps.Select((_, i) => f(i)).ToList();
        var original = File.ReadAllText(md);
        var all = Res("PASS", Enumerable.Range(1, steps.Count).Select(i => (i, "ok", (string?)null)).ToArray());

        // every shot taken, a dialog with no window, no shots at all, a failure picture, a replay that stopped for a person
        Write("full", original, Shots(i => new(Opened: pic, Filled: pic, Ended: pic)), all, -1, EvidenceEnd.Completed);
        Write("no window", original, Shots(i => new(OpenedNoWindow: true, Ended: pic)), all, -1, EvidenceEnd.Completed);
        Write("none", original, Shots(i => null), all, -1, EvidenceEnd.Completed);
        Write("failed", original, Shots(i => i == 0 ? new(Failed: pic) : null), Res("ERROR", (1, "failed", "the ribbon item was not found")), 0, EvidenceEnd.Error);
        Write("person", original, Shots(i => new(Opened: pic)), Res("INCOMPLETE", (1, "ok", null), (2, "needs-person", "a dialog the replay cannot fill: Draw grid")), 1, EvidenceEnd.Person);
        // a steps.md that does not belong to the recording (another number of steps, a row that is not a dialog row): the notes that say so
        Write("fewer steps", original.Replace("\n1. Click **Line**", "\n1. Click **Line**\n\n   Press Enter"), Shots(i => new(Ended: pic)), all, -1, EvidenceEnd.Completed);
        var lines = original.Split('\n').ToList();
        int cut = lines.FindIndex(l => l.StartsWith("2. ", StringComparison.Ordinal));
        if (cut > 0) Write("mismatch", string.Join("\n", lines.Take(cut)) + "\n" + string.Join("\n", lines.Skip(cut + 1).SkipWhile(l => !Regex.IsMatch(l, @"^\d+\. ") && !l.StartsWith("## ", StringComparison.Ordinal))), Shots(i => new(Opened: pic, Filled: pic, Ended: pic)), all, -1, EvidenceEnd.Completed);
        Assert.True(n >= 6);
    }

    static string XmlText(string t) => t.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>The text of the literals of a line, with the {holes} of an interpolated string taken out (code in a hole, such as a namespace, is not a message).</summary>
    static bool LiteralNamesProduct(string literal)
    {
        string prev;
        do { prev = literal; literal = Regex.Replace(literal, @"\{[^{}]*\}", ""); } while (literal != prev);
        return DocText.MentionsProduct(literal);
    }

    [Fact]
    public void The_suite_report_has_no_product_name_from_the_program_and_names_the_result_by_test_folder()
    {
        var s = new SuiteResult { Stamp = "2026-11-18_09-30-00", CoreVersion = "9.9.9", StopReason = "AutoCAD was closed or the add-in was unloaded" };
        s.Tests.Add(new SuiteTest { Name = "one", Verdict = "ERROR", Message = ReplayRules.ChainAnswersNeedPerson(new Step { Kind = "ribbon", Title = "Insert", ChainClick = true, Answers = { new Answer("point", "OK", null, null, "Specify a point:", true, 1, 5) } }) ?? "", Result = @"C:\temp\retrace\tests\2026-11-18_0930_one\results\2026-11-18_09-30-05_ERROR.json" });
        s.Tests.Add(new SuiteTest { Name = "two", Verdict = "PASS", Result = @"C:\temp\retrace\tests\2026-11-18_0930_two\results\2026-11-18_09-30-09_PASS.json" });
        s.Tests.Add(new SuiteTest { Name = "three", Verdict = "NOT-RUN", Message = "the suite stopped before this test" });
        var md = s.ToMarkdown();
        Assert.False(DocText.MentionsProduct(md), md);
        Assert.StartsWith("# Suite 2026-11-18_09-30-00:", md);
        Assert.Contains("suite ", md);
        Assert.Contains("| 2026-11-18_0930_one/results/2026-11-18_09-30-05_ERROR.json |", md);
        Assert.Contains("| 2026-11-18_0930_two/results/2026-11-18_09-30-09_PASS.json |", md);
        Assert.Contains("Stopped: AutoCAD was closed or the add-in was unloaded", md);
        Assert.Equal("a/results/b.json", SuiteResult.ResultRef(@"C:\x\a\results\b.json"));
        Assert.Equal("b.json", SuiteResult.ResultRef("b.json"));
        Assert.Equal("", SuiteResult.ResultRef(""));
    }

    static readonly string[] ScannedSources =
    [
        "Core/Plan.cs", "Core/ReplayRules.cs", "Core/SuiteResult.cs", "Core/TdocPlan.cs", "Core/TdocSplit.cs", "Core/ChainDelivery.cs", "Core/BasicCheckRules.cs",
        "Engine/Recordings/ScriptWriter.cs", "Engine/Recordings/StepsDoc.cs", "Engine/Recordings/ExportDocx.cs", "Engine/Recordings/ExportXlsx.cs", "Engine/Recordings/TrainingDocx.cs",
        "Engine/Recordings/EvidenceBook.cs", "Engine/Recordings/EvidenceData.cs", "Engine/Recordings/RecordingSession.cs", "Engine/Emit/StepWording.cs",
        "Engine/Recordings/EvidenceRows.cs", "Core/EvidenceMapping.cs", "AutoCad/EvidenceShot.cs", "AutoCad/PictureWindows.cs",
        "AutoCad/Replayer.cs", "AutoCad/PaletteFill.cs", "AutoCad/DialogAutoFill.cs", "AutoCad/PaletteClicks.cs", "AutoCad/SuiteRun.cs", "AutoCad/SdiDocs.cs", "AutoCad/TdocCapture.cs", "AutoCad/EvidenceCapture.cs", "AutoCad/TdocRun.cs",
    ];

    // lines whose literals the person reads in the product's own window, the command line or the log: not documents. Anything else with the name in a string is a message that can reach a document.
    static readonly string[] NotDocuments =
    [
        "Log.", "Say(", "_win.Show(\"Retrace replay\"", "sb.AppendLine($\"Retrace test:", "WriteMessage(", "Commands.Print(", "Native.ForegroundDialogStartsWith(", "\"RETRACETYPE\"", "RETRACETYPE\\n",
        "another Retrace job", "Stop the recording first", "\"RETRACE\"",
        // the older versions' Files section, kept only to recognise a stamp made then
        "OldFilesSection",
        // the old stamp file's name, read as a fallback
        ".retrace\"",
    ];

    [Fact]
    public void No_message_the_replay_or_the_writers_can_put_into_a_document_names_the_product()
    {
        var root = LayerRulesTests.SourceRoot();
        var literal = new Regex("\\$?@?\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.CultureInvariant);
        var hits = new List<string>();
        foreach (var rel in ScannedSources)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), path);
            int n = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                n++;
                var t = line.TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("///", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
                if (!literal.Matches(line).Any(m => LiteralNamesProduct(m.Value))) continue;
                if (NotDocuments.Any(a => line.Contains(a, StringComparison.Ordinal))) continue;
                hits.Add($"{rel}:{n}: {t}");
            }
        }
        Assert.True(hits.Count == 0, "program messages that name the product (reword them, or list the line as not a document):\n" + string.Join("\n", hits.Select(h => h.Length > 220 ? h[..220] : h)));
    }

    [Fact]
    public void A_program_message_with_the_name_is_passed_on_unchanged_and_reported_once()
    {
        var seen = new List<string>();
        var old = DocText.OnLeak;
        try
        {
            DocText.OnLeak = seen.Add;
            Assert.Equal("fine", DocText.Guard("fine", "x"));
            Assert.Empty(seen);
            Assert.Equal("a Retrace thing", DocText.Guard("a Retrace thing", "evidence workbook"));
            Assert.Single(seen);
            Assert.Contains("evidence workbook", seen[0]);
            Assert.Equal("", DocText.Guard(null, "x"));
        }
        finally { DocText.OnLeak = old; }
    }
}
