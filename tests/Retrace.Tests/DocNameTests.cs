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

/// <summary>The search every document test reuses: no part of an Office file, and no text, has the product's name or the word "recorded" in any letter case (part names and the bytes of every part, as UTF-8 and as UTF-16).
/// "Recorded" also counts inside a longer word ("unrecorded", "prerecorded"); "recording" and "record" are fine.</summary>
public static class DocNameGuard
{
    static byte[] Lower(byte[] b) { var c = (byte[])b.Clone(); for (int i = 0; i < c.Length; i++) if (c[i] >= 'A' && c[i] <= 'Z') c[i] += 32; return c; }

    /// <summary>True when <paramref name="bytes"/> has "retrace" in any case as ASCII text or as UTF-16 text.</summary>
    public static bool HasName(byte[] bytes)
    {
        var low = Lower(bytes);
        return low.AsSpan().IndexOf(Encoding.ASCII.GetBytes("retrace")) >= 0 || low.AsSpan().IndexOf(Encoding.Unicode.GetBytes("retrace")) >= 0;
    }

    /// <summary>True when <paramref name="bytes"/> has "recorded" in any case as ASCII text or as UTF-16 text.</summary>
    public static bool HasRecorded(byte[] bytes)
    {
        var low = Lower(bytes);
        return low.AsSpan().IndexOf(Encoding.ASCII.GetBytes("recorded")) >= 0 || low.AsSpan().IndexOf(Encoding.Unicode.GetBytes("recorded")) >= 0;
    }

    /// <summary>The text a person typed or named in the guard recording that has one of the two words in it: it reaches the documents as it is, so the searches take it out first. Everything else is the program's own wording.</summary>
    public static readonly string[] UserContent = ["RETRACE_GRID", "prerecorded notes"];

    /// <summary><paramref name="xml"/> without the user content listed in <see cref="UserContent"/>.</summary>
    public static string WithoutUserContent(string xml) { foreach (var u in UserContent) xml = xml.Replace(u, ""); return xml; }

    /// <summary>Fails when a part of the Office file <paramref name="file"/> (its name or its bytes) has the name or the word "recorded". Returns the part names that were searched.</summary>
    public static List<string> AssertNoName(string file)
    {
        var parts = new List<string>();
        using var zip = ZipFile.OpenRead(file);
        foreach (var e in zip.Entries)
        {
            parts.Add(e.FullName);
            Assert.False(DocText.MentionsProduct(e.FullName), $"{Path.GetFileName(file)}: part name {e.FullName}");
            Assert.False(DocText.MentionsRecorded(e.FullName), $"{Path.GetFileName(file)}: part name {e.FullName}");
            using var ms = new MemoryStream();
            using (var s = e.Open()) s.CopyTo(ms);
            var bytes = ms.ToArray();
            if (HasRecorded(bytes))
            {
                var utf8 = Encoding.UTF8.GetString(bytes);
                var at = utf8.IndexOf("recorded", StringComparison.OrdinalIgnoreCase);
                Assert.Fail($"{Path.GetFileName(file)}: {e.FullName}: says \"recorded\": ...{(at >= 0 ? utf8.Substring(Math.Max(0, at - 80), Math.Min(160, utf8.Length - Math.Max(0, at - 80))) : "(UTF-16)")}...");
            }
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
        """{"n":14,"t":2900,"type":"dialog_values","for":13,"title":"Draw grid","source":"win32","fields":[{"key":"id:1","label":"Layer name","kind":"edit","before":"GRID","after":"RETRACE_GRID"},{"key":"id:2","label":"Notes","kind":"edit","before":"","after":"prerecorded notes"},{"key":"id:3","label":"Products","kind":"listview","before":"","after":"(selection not recorded)"}],"read":4,"closedWith":"ok"}""",
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
        var mdOwn = md.Replace("prerecorded notes", "").Replace("RETRACE_GRID", "");   // the hits allowed: a layer name and a note the person typed (user content)
        Assert.False(DocText.MentionsProduct(mdOwn), md);
        Assert.False(DocText.MentionsRecorded(mdOwn), md);
        Assert.Contains("prerecorded notes", md);   // the person's own text comes through unchanged
        var docx = AssertNoNameExceptUserContent(Path.Combine(dir, "steps.docx"));
        var xlsx = AssertNoNameExceptUserContent(Path.Combine(dir, "steps.xlsx"));
        Assert.Contains("word/document.xml", docx);
        Assert.Contains("xl/workbook.xml", xlsx);
        Assert.Contains(xlsx, p => p.Contains("media/"));   // the pictures were in: their file names were searched too
        Assert.Contains(docx, p => p.Contains("media/"));
    }

    /// <summary>Like <see cref="DocNameGuard.AssertNoName"/>, with the user content of the recording (<see cref="DocNameGuard.UserContent"/>: the layer RETRACE_GRID, the note "prerecorded notes") taken out of the XML first. Both words are searched.</summary>
    static List<string> AssertNoNameExceptUserContent(string file)
    {
        var parts = new List<string>();
        using var zip = ZipFile.OpenRead(file);
        foreach (var e in zip.Entries)
        {
            parts.Add(e.FullName);
            Assert.False(DocText.MentionsProduct(e.FullName), e.FullName);
            Assert.False(DocText.MentionsRecorded(e.FullName), e.FullName);
            using var ms = new MemoryStream(); using (var s = e.Open()) s.CopyTo(ms);
            var bytes = ms.ToArray();
            if (e.FullName.EndsWith(".xml", StringComparison.Ordinal)) bytes = Encoding.UTF8.GetBytes(DocNameGuard.WithoutUserContent(Encoding.UTF8.GetString(bytes)));
            Assert.False(DocNameGuard.HasName(bytes), $"{Path.GetFileName(file)}: {e.FullName}");
            if (DocNameGuard.HasRecorded(bytes))
            {
                var s8 = Encoding.UTF8.GetString(bytes); var at = s8.IndexOf("recorded", StringComparison.OrdinalIgnoreCase);
                Assert.Fail($"{Path.GetFileName(file)}: {e.FullName}: says \"recorded\": ...{(at >= 0 ? s8.Substring(Math.Max(0, at - 80), Math.Min(160, s8.Length - Math.Max(0, at - 80))) : "(UTF-16)")}...");
            }
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
    public void The_guard_finds_the_word_recorded_as_ascii_and_as_utf16_in_any_case_and_inside_longer_words_but_not_recording_or_record()
    {
        Assert.True(DocNameGuard.HasRecorded(Encoding.UTF8.GetBytes("it was ReCoRdEd then")));
        Assert.True(DocNameGuard.HasRecorded(Encoding.Unicode.GetBytes("RECORDED")));
        Assert.True(DocNameGuard.HasRecorded(Encoding.UTF8.GetBytes("an unrecorded step")));
        Assert.True(DocNameGuard.HasRecorded(Encoding.UTF8.GetBytes("a prerecorded one")));
        Assert.False(DocNameGuard.HasRecorded(Encoding.UTF8.GetBytes("the recording and a record of it, recorder")));
        Assert.False(DocNameGuard.HasRecorded(Encoding.Unicode.GetBytes("nothing here")));
        Assert.True(DocText.MentionsRecorded("Recorded 2026-10-10"));
        Assert.True(DocText.MentionsRecorded("not recorded"));
        Assert.False(DocText.MentionsRecorded("recording"));
        Assert.False(DocText.MentionsRecorded(null));
        Assert.False(DocText.MentionsRecorded(""));
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
        Assert.StartsWith("Created 2026-10-10, with AutoCAD", lines[1]);
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

           - Pick the first point (recorded at 0, 0)
           - Pick the next point (recorded at -5.5, 5)

        2. Fill in the **Opts** dialog

           - Select the items in **Products** yourself (the selection could not be recorded)

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
        // the stored lines of an old steps.md ("Recorded 2026-...", the picture sentence, the picks, the list) are read as today's wording: the whole search passes on both files
        AssertNoName(docx); AssertNoName(xlsx);
        var xml = DocNameGuard.PartText(docx, "word/document.xml");
        Assert.Contains("Created 2026-10-10, with AutoCAD 2026 25.1.0.0, workspace Drafting", xml);
        Assert.Contains("(picked at 0, 0)", xml);
        Assert.Contains("(picked at -5.5, 5)", xml);
        Assert.Contains("the selection was not stored in the test", xml);
        var sheet = DocNameGuard.AllXml(xlsx);
        Assert.Contains("Created", sheet);
        Assert.Contains("2026-10-10, with AutoCAD 2026 25.1.0.0, workspace Drafting", sheet);
        Assert.Contains("(picked at 0, 0)", sheet);
        Assert.Equal("2026-10-10, with AutoCAD 2026 25.1.0.0, workspace Drafting", StepsDoc.Load(Path.Combine(dir, "steps.md")).MetaValue);
        // the file on disk keeps the words it was written with
        Assert.Contains("Recorded 2026-10-10", File.ReadAllText(Path.Combine(dir, "steps.md")));
        Assert.DoesNotContain("Files in this folder", xml);
        Assert.DoesNotContain("automatically", xml);
        Assert.Contains("Click", xml);
        Assert.Contains("Overall", xml);
        var doc = StepsDoc.Load(Path.Combine(dir, "steps.md"));
        Assert.Equal(2, doc.Steps.Count);
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
        Assert.True(EvidenceFiles.StepsUnchanged(dir, StepsDoc.DropAuthorSection(OldSteps)));          // the same steps.md written again without the Files section: no false alarm
        // the same steps.md written again in today's wording (no Files section, "Created", "picked at", ...): the stamp of the older wording still holds
        var again = OlderStepsWording.ToCurrent(StepsDoc.DropAuthorSection(OldSteps));
        Assert.Contains("(picked at 0, 0)", again);
        Assert.DoesNotContain("ecorded", again);
        Assert.True(EvidenceFiles.StepsUnchanged(dir, again));
        File.WriteAllText(Path.Combine(evi, ".steps-sha"), EvidenceFiles.StepsFingerprint(OldSteps));   // the other scheme: the steps part of the old text
        Assert.True(EvidenceFiles.StepsUnchanged(dir, again));
        Assert.False(EvidenceFiles.StepsUnchanged(dir, again.Replace("Click **Line**", "Click **Circle**")));   // a real change of the steps is still noticed
        Assert.False(EvidenceFiles.StepsUnchanged(dir, again.Replace("(picked at 0, 0)", "(picked at 1, 0)")));
        Assert.False(EvidenceFiles.StepsUnchanged(dir, StepsDoc.DropAuthorSection(OldSteps).Replace("Click **Line**", "Click **Circle**")));
        File.WriteAllText(Path.Combine(evi, ".steps-sha"), Sha(OldSteps));
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

        var user = new[] { "Layer RETRACE_GRID is frozen", "Palette \"Retrace Details\" did not open", "Retracement and retraced are words", "RETRACEX (another add-in's command) did not finish", "Prompt \"Select the recorded object:\" was shown by an add-in", "Layer UNRECORDED-1 and PreRecorded_2 are frozen" };
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
        Assert.Contains("Select the recorded object", md);
        Assert.Contains("PreRecorded_2", md);
        // the guard reports the word in a program message (and passes it on unchanged), like the product's name
        var seen = new List<string>(); var old = DocText.OnLeak;
        try
        {
            DocText.OnLeak = seen.Add;
            Assert.Equal("it was recorded", DocText.Guard("it was recorded", "suite report"));
            Assert.Single(seen);
            Assert.Contains("suite report", seen[0]);
            Assert.Equal("the recording", DocText.Guard("the recording", "x"));
            Assert.Single(seen);
        }
        finally { DocText.OnLeak = old; }
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
            foreach (var row in ev.Rows.Values)
            {
                var own = DocNameGuard.WithoutUserContent(row.Notes ?? "");
                Assert.False(DocText.MentionsProduct(own), $"{name}: {row.Notes}");
                Assert.False(DocText.MentionsRecorded(own), $"{name}: {row.Notes}");
            }
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
    static bool LiteralNamesProduct(string literal) => DocText.MentionsProduct(WithoutHoles(literal));

    /// <summary>True when the text of a literal (not the code in its {holes}: a variable called "recorded" is not a message) has the word "recorded".</summary>
    static bool LiteralSaysRecorded(string literal) => DocText.MentionsRecorded(WithoutHoles(literal));

    /// <summary>The text of every string literal in one line of C# (regular, verbatim, interpolated), the code in the {holes} of an interpolated string left out and the literals inside a hole (<c>{(x ? "at the recorded place" : "")}</c>) listed on their own.
    /// What this cannot see: a string that goes over several lines (a raw or verbatim literal whose text is on the next line) and a word built from pieces.</summary>
    static List<string> LiteralTexts(string line)
    {
        var res = new List<string>();
        int i = 0;
        LexCode(line, ref i, res, top: true);
        return res;
    }

    static void LexCode(string s, ref int i, List<string> res, bool top)
    {
        int depth = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (!top && c == '{') depth++;
            else if (!top && c == '}') { if (depth == 0) { i++; return; } depth--; }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { i = s.Length; return; }
            else if (c == '\'')   // a char literal: '"', '{', '\n'
            {
                i++;
                if (i < s.Length && s[i] == '\\') i++;
                i += 2;
                continue;
            }
            if (c == '"' || c == '$' || c == '@')
            {
                int j = i; bool interp = false, verbatim = false;
                while (j < s.Length && (s[j] == '$' || s[j] == '@')) { if (s[j] == '$') interp = true; else verbatim = true; j++; }
                if (j < s.Length && s[j] == '"') { i = j + 1; LexString(s, ref i, interp, verbatim, res); continue; }
            }
            i++;
        }
    }

    static void LexString(string s, ref int i, bool interp, bool verbatim, List<string> res)
    {
        var sb = new System.Text.StringBuilder();
        while (i < s.Length)
        {
            char ch = s[i];
            if (ch == '"')
            {
                if (verbatim && i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                i++; break;
            }
            if (!verbatim && ch == '\\' && i + 1 < s.Length) { sb.Append(s, i, 2); i += 2; continue; }
            if (interp && ch == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { i += 2; continue; }
                i++;
                LexCode(s, ref i, res, top: false);
                continue;
            }
            sb.Append(ch); i++;
        }
        res.Add(sb.ToString());
    }

    static string WithoutHoles(string literal)
    {
        string prev;
        do { prev = literal; literal = Regex.Replace(literal, @"\{[^{}]*\}", ""); } while (literal != prev);
        return literal;
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
        "Core/ScriptExport.cs", "Core/Plan.cs", "Core/ReplayRules.cs", "Core/SuiteResult.cs", "Core/TdocPlan.cs", "Core/TdocSplit.cs", "Core/ChainDelivery.cs", "Core/BasicCheckRules.cs",
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

    // Lines with "recorded" in a string that are not documents: the log, the command line, the product's own windows, ribbon and settings text, and the one stored marker. A line is listed by a piece of its text and the file it is in, with the reason.
    // Anything else with the word in a string literal can reach a document (a replay failure, a note, a wording, a sheet label), so it must be reworded at its source ("in the original run", "in the test", "stored", "made").
    static readonly (string File, string Piece, string Why)[] NotDocumentsForRecorded =
    [
        ("AutoCad/Recorder.cs", "Log.Info(", "the log"),
        ("AutoCad/Recorder.cs", "StatusNotRecorded(", "the reason goes to the log"),
        ("AutoCad/Recorder.cs", "both are recorded", "the end of a Log.Info( message, on its own line"),
        ("AutoCad/Commands.Tools.cs", "RETRACERUN fills a dialog", "the settings listing on the command line"),
        ("Engine/Recordings/OlderStepsWording.cs", "IntroOld =", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "DateOld =", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "StartOld =", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "NoStepsOld =", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "PickOld =", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "AltOld =", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "SelOld =", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "head = DateNew.Replace", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "head = StartNew.Replace", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "rest = NoStepsNew.Replace", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "rest = PickNew.Replace", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "rest = AltNew.Replace", "the older wording, read or put back for the fingerprint"),
        ("Engine/Recordings/OlderStepsWording.cs", "rest = SelNew.Replace", "the older wording, read or put back for the fingerprint"),
        ("Engine/Util/DocText.cs", "Rx.Create(@\"recorded\"", "the guard's own pattern"),
        ("Engine/Util/DocText.cs", "a program message says", "the guard's own log line"),
        ("AutoCad/PaletteWatch.cs", "Log.Info(", "the log"),
        ("AutoCad/PaletteClicks.cs", "Ignored(", "the log (why a click was not taken)"),
        ("AutoCad/PaletteClicks.cs", "Log.Info(", "the log"),
        ("AutoCad/TreeControl.cs", "Log.Info(", "the log"),
        ("AutoCad/MenuWatch.cs", "Log.Info(", "the log"),
        ("AutoCad/MenuListView.cs", "Log.Info(", "the log"),
        ("AutoCad/DialogFill.cs", "Log.Info(", "the log"),
        ("AutoCad/Replayer.cs", "ed.WriteMessage(", "the command line"),
        ("AutoCad/Replayer.cs", "Say(", "the command line"),
        ("AutoCad/Replayer.cs", "_win.Show(", "the replay window"),
        ("AutoCad/Replayer.cs", "Log.Info(", "the log"),
        ("AutoCad/Commands.cs", "ed.WriteMessage(", "the command line"),
        ("AutoCad/Commands.Tools.cs", "ed.WriteMessage(", "the command line"),
        ("AutoCad/Commands.Tools.cs", "Ask.String(", "a command line prompt"),
        ("AutoCad/Commands.Checkpoints.cs", "Done(false,", "a window's message: the checkpoints window"),
        ("AutoCad/RetraceTab.cs", "Button(", "ribbon tooltips"),
        ("Core/StatusBarClick.cs", "toggles are not recorded", "why a status bar click was not taken: the log"),
        ("Core/BehaviorUiModel.cs", "AutoExplanation", "the explanation in the settings window"),
        ("Core/BehaviorUiModel.cs", "NotRecordedTip", "the tooltip of the checkpoint window"),
        ("Core/StatusBarClick.cs", "at the recorded place", "why a status bar click was not taken: the log"),
        ("Engine/Recordings/TestFolders.cs", "RunLastTip", "the prompt window's tooltip"),
        ("Engine/Recordings/TestFolders.cs", "A recording is running", "the prompt window's tooltip"),
        ("Engine/Recordings/TestFolders.cs", "There is no recorded test yet", "the prompt window's tooltip"),
        ("Engine/Recordings/TestFolders.cs", "Runs the test recorded most recently", "the prompt window's tooltip"),
        ("Core/ListSelection.cs", "public const string NotRecorded", "the marker stored in a recording as the value of a list whose selection could not be read; a document says \"yourself\" instead (see the guard recording)"),
        ("Ui/", "", "the product's own windows"),
    ];

    [Fact]
    public void No_string_in_the_source_that_can_reach_a_document_says_recorded()
    {
        var root = LayerRulesTests.SourceRoot();
        var literal = new Regex("\\$?@?\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.CultureInvariant);
        var hits = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
            int n = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                n++;
                var t = line.TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
                if (!LiteralTexts(line).Any(x => DocText.MentionsRecorded(x))) continue;
                if (NotDocumentsForRecorded.Any(a => rel.StartsWith(a.File, StringComparison.Ordinal) && line.Contains(a.Piece, StringComparison.Ordinal))) continue;
                hits.Add($"{rel}:{n}: {t}");
            }
        }
        Assert.True(hits.Count == 0, "strings that say \"recorded\" and can reach a document (reword them at their source, or list the line as not a document):\n" + string.Join("\n", hits.Select(h => h.Length > 240 ? h[..240] : h)));
    }

    [Fact]
    public void The_source_scan_for_recorded_finds_a_new_message_and_skips_the_listed_lines()
    {
        var literal = new Regex("\\$?@?\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.CultureInvariant);
        bool Hit(string line) => LiteralTexts(line).Any(x => DocText.MentionsRecorded(x));
        Assert.True(Hit("notes.Add(\"the click was recorded\");"));
        Assert.True(Hit("var m = $\"{x} is Recorded as {y}\";"));
        Assert.True(Hit("\"an UNRECORDED value\""));
        Assert.True(Hit("\"a prerecorded value\""));
        Assert.False(Hit("var recorded = 1; Use(recorded);"));              // code, not a message
        Assert.False(Hit("$\"the {recorded} value is {recordedName}\""));   // the holes of an interpolated string are code
        Assert.False(Hit("\"the recording has a record\""));
        Assert.True(Hit("var m = $\"left {(x ? \"at the recorded place\" : \"\")} and {y}\";"));   // a literal inside a hole
        Assert.True(Hit("var m = @\"a \"\"verbatim\"\" recorded text\";"));
        Assert.False(Hit("var m = '\"'; var recorded = 2; // it was recorded"));                     // a char literal, code and a comment
        Assert.False(Hit("var m = $\"{recorded} and {x.recorded}\";"));                              // holes of code
        Assert.True(Hit("var m = $\"{{recorded}}\";"));                                                // a doubled brace is text
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

    const string OldNoSteps = """
        # empty test

        Recorded 2026-10-10, with AutoCAD 2026 25.1.0.0.

        **Start with:** a drawing like the one recorded at the start (drawing: 1 LINE; layers: 1). Open or build it before the first step.

        Do the steps in order. A picture under a step shows what the screen looked like when the test was recorded.

        ## Steps

        _No steps were recorded._

        ## Result

        Overall: [ ] pass  [ ] fail
        """;

    [Fact]
    public void An_old_steps_md_with_no_steps_and_the_oldest_start_sentence_is_exported_clean()
    {
        var dir = Path.Combine(_root, "tests", "2026-10-10_0201_old-empty");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "steps.md"), OldNoSteps);
        var r = EngineRunner.Export(dir, true, true);
        Assert.True(r.Success, r.Error);
        AssertNoName(Path.Combine(dir, "steps.docx")); AssertNoName(Path.Combine(dir, "steps.xlsx"));
        var xml = DocNameGuard.PartText(Path.Combine(dir, "steps.docx"), "word/document.xml");
        Assert.Contains("This test has no steps.", xml);
        Assert.Contains("a drawing like the one at the start of the test", xml);
        Assert.Contains("Created 2026-10-10", xml);
    }

    [Fact]
    public void The_older_wording_is_read_as_todays_by_exact_sentences_only_and_a_persons_text_is_never_touched()
    {
        var old = OldSteps.Replace("\r\n", "\n");
        var now = OlderStepsWording.ToCurrent(old);
        Assert.False(DocText.MentionsRecorded(now), now);
        Assert.Contains("Created 2026-10-10, with AutoCAD", now);
        Assert.Contains("looked like at that step.", now);
        Assert.Contains("   - Pick the next point (picked at -5.5, 5)", now);
        Assert.Contains("Select the items in **Products** yourself (the selection was not stored in the test)", now);
        Assert.Equal(old, OlderStepsWording.ToOlder(now));   // the way back is exact
        Assert.Equal("_This test has no steps._", OlderStepsWording.ToCurrent("## Steps\n\n_No steps were recorded._\n").Split('\n')[2]);
        Assert.Contains("(picked at 1.5, -2, 0)", OlderStepsWording.ToCurrent("## Steps\n\n1. Do\n\n   - Pick a point (recorded at 1.5, -2, 0)\n"));
        Assert.Contains("![Pick a point (picked at 1, 2)](a.png)", OlderStepsWording.ToCurrent("## Steps\n\n1. Do\n\n   ![Pick a point (recorded at 1, 2)](a.png)\n"));
        Assert.Contains("\r\n   - Pick a point (picked at 1, 2)\r\n", OlderStepsWording.ToCurrent("## Steps\r\n\r\n1. Do\r\n\r\n   - Pick a point (recorded at 1, 2)\r\n"));

        // a person's text with the word stays: a check note, a typed value, a prompt name, text that only looks like a rule
        var user = string.Join("\n",
            "# the recorded test", "", "Recorded by hand on Monday.", "", "**Start with:** the recorded drawing.", "", "## Steps", "",
            "1. Check: the recorded value is 4", "",
            "   - Pick the recorded object (recorded at 1, 2)",
            "   - Pick the object (recorded at the corner)",
            "   - Pick the object (recorded at 1, 2) and press Enter",
            "   - Type prerecorded and Enter",
            "   - Select the items in **Recorded parts** yourself (the selection could not be recorded) now",
            "", "Do the steps in order. A picture under a step shows what the screen looked like when the test was recorded, said Ann.",
            "A picture under a step shows what the screen looked like when the test was recorded.", "",
            "_No steps were recorded._ A note.", "", "## Result", "", "Overall: recorded ok", "");
        var u = OlderStepsWording.ToCurrent(user);
        var expect = user.Replace("(recorded at 1, 2)\n   - Pick the object (recorded at the corner)", "(picked at 1, 2)\n   - Pick the object (recorded at the corner)");
        Assert.Equal(expect, u);   // only the Pick bullet with the number suffix at its end changed; "Recorded by hand" has no date, so it stays
        Assert.Contains("Pick the recorded object (picked at 1, 2)", u);
        // a person's head lines are not the date line
        Assert.Equal("", new StepsDoc("# t\n\nRecorded by hand on Monday.\n\nCreated by Ann\n\n## Steps\n", _root).Meta);
        Assert.Equal("Created 2026-10-10, with X.", new StepsDoc("# t\n\nCreated by Ann\n\nCreated 2026-10-10, with X.\n\n## Steps\n", _root).Meta);
        Assert.Equal("2026-10-10, with X", new StepsDoc("# t\n\nRecorded 2026-10-10, with X.\n\n## Steps\n", _root).MetaValue);   // an old date line, read as today's
        // and through the Word export
        var dir = Path.Combine(_root, "tests", "2026-10-10_0202_old-user"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "steps.md"), user);
        Assert.True(EngineRunner.Export(dir, true, false).Success);
        var xml = DocNameGuard.PartText(Path.Combine(dir, "steps.docx"), "word/document.xml");
        Assert.Contains("the recorded value is 4", xml);
        Assert.Contains("Pick the recorded object (picked at 1, 2)", xml);
        Assert.Contains("Type prerecorded and Enter", xml);
    }

    [Fact]
    public void An_exported_script_names_neither_the_product_nor_says_recorded()
    {
        var steps = Plan.Build(Recording("draw test").Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => e.GetProperty("type").GetString() != "header").ToList());
        var scr = ScriptExport.Build(steps, "2026-10-10_0008_draw-test");
        Assert.StartsWith("; Export of 2026-10-10_0008_draw-test", scr);
        Assert.False(DocText.MentionsProduct(scr.Replace("RETRACE_GRID", "")), scr);
        Assert.False(DocText.MentionsRecorded(scr.Replace("prerecorded notes", "")), scr);
    }

    [Fact]
    public void A_list_whose_selection_was_not_stored_reads_plainly_in_the_training_document_and_the_docs_set()
    {
        var c = new FieldChange("id:7", "Products", ListSelection.Kind, "", ListSelection.NotRecorded);
        var callout = TdocPlan.FieldCallout(c);
        Assert.Equal("Products: (the selection was not stored in the test)", callout);
        Assert.False(DocText.MentionsRecorded(TdocPlan.Describe(c)));
        // the same marker as the "before" of a later value is not shown as "(was ...)"
        var v = new RecordingDialogValue("Products", "listview", ListSelection.NotRecorded, "Bolts");
        var words = Retrace.Engine.Emit.StepWording.ChangeText(v, s => s ?? "", s => $"`{s}`", bold: true)!;
        Assert.Equal("Select `Bolts` in **Products**", words);
        // a text field whose value is literally the marker is a normal value
        var edit = new RecordingDialogValue("Notes", "edit", ListSelection.NotRecorded, "x");
        Assert.Contains($"(was `{ListSelection.NotRecorded}`)", Retrace.Engine.Emit.StepWording.ChangeText(edit, s => s ?? "", s => $"`{s}`", bold: true));
        // a training document whose legend has the callout: every part searched
        WritePng(Path.Combine(_root, "t3", "pictures", "s1.png"), 64, 40);
        var m = new TrainingManifest
        {
            Title = "Lists", CreatedUtc = "2026-10-30T09:15:00Z",
            Tests = [new TrainingTest { Name = "list palette", Folder = "tests/list", Verdict = "PASS", Steps = [new TrainingStep { Number = 1, Kind = "palette", Title = "Parts palette", Instructions = [TdocPlan.Describe(c) ?? ""],
                Pictures = [new TrainingPicture { File = "pictures/s1.png", Caption = "The palette", Callouts = [new TrainingCallout { Number = 1, Text = callout }] }] }] }],
        };
        var docx = Path.Combine(_root, "t3", "list.docx");
        TrainingDocx.Write(m, Path.Combine(_root, "t3"), docx);
        AssertNoName(docx);
        Assert.Contains("the selection was not stored in the test", DocNameGuard.PartText(docx, "word/document.xml"));
    }
}
