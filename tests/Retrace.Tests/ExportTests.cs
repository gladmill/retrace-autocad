using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Test script without sign-off, and the Word / Excel export of "steps.md". Synthetic data only.</summary>
public class ExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("retrace-test-export").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    const string Header = """{"type":"header","schema":1,"name":"draw test","started":"2026-10-10T00:08:07.1234567+02:00","autocad":"25.1.0.0","product":"AutoCAD 2026","workspace":"Drafting"}""";

    static readonly string[] Lines =
    {
        Header,
        """{"n":1,"t":10,"type":"state","state":{"count":0,"byType":{},"layers":["0"],"hash":"e3"}}""",
        """{"n":2,"t":1000,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line","tab":"Home","panel":"Draw"}""",
        """{"n":3,"t":1010,"type":"cmd_start","command":"LINE","origin":"ribbon"}""",
        """{"n":5,"t":1200,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[0,0,0],"input":true,"by":"mouse"}""",
        """{"n":6,"t":1300,"type":"prompt","kind":"point","message":"Specify next point or [Undo]:","keywords":["Undo"],"status":"OK","value":[5,5,0],"input":true,"by":"mouse"}""",
        """{"n":7,"t":1400,"type":"cmd_end","command":"LINE"}""",
        """{"n":8,"t":2000,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
        """{"n":9,"t":2010,"type":"modal_enter","title":""}""",
        """{"n":10,"t":2300,"type":"modal_title","title":"Draw grid"}""",
        """{"n":11,"t":3000,"type":"modal_leave"}""",
        """{"n":12,"t":3010,"type":"cmd_end","command":"DRAWGRID"}""",
        """{"n":13,"t":4000,"type":"mark","note":"line drawn","state":{"count":1,"byType":{"LINE":1},"layers":["0"],"hash":"ab"}}""",
        """{"n":14,"t":4001,"type":"shot","for":2,"file":"screenshots/002-ribbon.png","what":"window"}""",
        """{"n":16,"t":4003,"type":"shot","for":6,"file":"screenshots/006-prompt.png","what":"window"}""",
        """{"n":18,"t":4005,"type":"shot","for":10,"file":"screenshots/010-dialog.png","what":"dialog"}""",
        """{"n":19,"t":4006,"type":"shot","for":13,"file":"screenshots/013-mark.png","what":"window"}""",
        """{"n":21,"t":5000,"type":"session_end"}""",
    };

    /// <summary>A PNG signature plus an IHDR chunk saying w x h, then IEND: enough for the size reader and the package (no decoder runs on it).</summary>
    static byte[] PngOf(int w, int h)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        foreach (var v in new[] { w, h }) b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        b.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0, 0, 0, 0]);
        return b.ToArray();
    }

    /// <summary>A test folder with 4 pictures (one 2000x1000), steps.md written from the recording.</summary>
    private string TestFolder(string name = "draw test 2026-10-10 00-08")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        File.WriteAllLines(Path.Combine(dir, "recording.jsonl"), Lines);
        File.WriteAllBytes(Path.Combine(dir, "screenshots", "002-ribbon.png"), PngOf(2000, 1000));
        foreach (var f in new[] { "006-prompt", "010-dialog", "013-mark" }) File.WriteAllBytes(Path.Combine(dir, "screenshots", f + ".png"), PngOf(300, 200));
        Assert.True(EngineRunner.WriteSteps(Path.Combine(dir, "recording.jsonl")).Success);
        return dir;
    }

    static List<string> Errors(OpenXmlPackage pkg) =>
        new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(pkg).Select(e => $"{e.Description} @ {e.Path?.XPath}").ToList();

    static void NoErrors(OpenXmlPackage pkg) { var e = Errors(pkg); Assert.True(e.Count == 0, string.Join("\n", e)); }

    // ---- sign-off ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Picture_captions_are_not_spreadsheet_details()
    {
        Assert.True(Retrace.Engine.Recordings.StepsDoc.IsPictureCaption("Before:"));
        Assert.True(Retrace.Engine.Recordings.StepsDoc.IsPictureCaption("After (what it should look like before you press OK):"));
        Assert.False(Retrace.Engine.Recordings.StepsDoc.IsPictureCaption("Set Rows to 5 (was 3)"));
    }

    [Fact]
    public void Script_has_no_sign_off_but_keeps_the_result_section()
    {
        var md = File.ReadAllText(Path.Combine(TestFolder(), "steps.md"));
        Assert.DoesNotContain("Tested by", md);
        Assert.DoesNotContain("____", md);
        Assert.Contains("## Result", md);
        Assert.Contains("- Check 1 (step 3): line drawn: [ ] pass  [ ] fail", md);
        Assert.Contains("Overall: [ ] pass  [ ] fail", md);
    }

    // ---- arguments ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Export_with_neither_flag_makes_both_and_a_missing_input_fails_with_a_reason()
    {
        var dir = TestFolder();
        var both = EngineRunner.Export(dir, false, false);
        Assert.True(both.Success);
        Assert.NotNull(both.Docx); Assert.NotNull(both.Xlsx);
        Assert.True(File.Exists(both.Docx)); Assert.True(File.Exists(both.Xlsx));
        Assert.Equal(Path.Combine(dir, "steps.md"), both.Script);

        var nope = EngineRunner.Export(Path.Combine(_root, "nope"), true, true);
        Assert.False(nope.Success);
        Assert.Contains("not found", nope.Error);
        Assert.Null(nope.Docx);
    }

    // ---- docx --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Export_writes_a_valid_docx_with_the_pictures_and_checkboxes()
    {
        var dir = TestFolder();
        Assert.True(EngineRunner.Export(dir, true, false).Success);
        Assert.True(File.Exists(Path.Combine(dir, "steps.docx")));
        Assert.False(File.Exists(Path.Combine(dir, "steps.xlsx")));
        Assert.Empty(Directory.GetFiles(dir, ".*.tmp"));

        using var doc = WordprocessingDocument.Open(Path.Combine(dir, "steps.docx"), false);
        NoErrors(doc);
        var body = doc.MainDocumentPart!.Document!.Body!;
        Assert.Equal(4, doc.MainDocumentPart!.ImageParts.Count());   // 4 pictures in the script, each a part
        Assert.Equal(4, body.Descendants<W.Drawing>().Count());

        var h = body.Descendants<W.ParagraphStyleId>().Select(p => p.Val!.Value).ToList();
        Assert.Contains("Heading1", h); Assert.Contains("Heading2", h);
        var text = body.InnerText;
        Assert.Contains("draw test", text);
        Assert.Contains("☐ pass", text);          // [ ] -> ballot box
        Assert.DoesNotContain("[ ]", text);
        Assert.Contains("1.", text);                     // step numbers kept as text
        Assert.Contains("Files in this folder", text);
        Assert.Single(body.Descendants<W.Table>());
        Assert.True(body.Descendants<W.TableRow>().Count() >= 5);
        Assert.Contains(body.Descendants<W.RunStyle>(), r => r.Val == "Code");   // `inline code`
        Assert.Contains(body.Descendants<W.Bold>(), _ => true);
    }

    [Fact]
    public void Pictures_are_scaled_to_at_most_16_cm_and_keep_their_aspect()
    {
        var dir = TestFolder();
        Assert.Equal((2000, 1000), ExportDocx.PngSize(Path.Combine(dir, "screenshots", "step-01-click.png")));
        Assert.Null(ExportDocx.PngSize(Path.Combine(dir, "steps.md")));
        Assert.Null(ExportDocx.PngSize(Path.Combine(dir, "nope.png")));

        var (cx, cy) = ExportDocx.FitEmu(2000, 1000);
        Assert.Equal(16 * 360000L, cx);
        Assert.Equal(8 * 360000L, cy);
        var (sx, sy) = ExportDocx.FitEmu(300, 200);    // small pictures stay at their own size
        Assert.Equal(300 * 9525L, sx); Assert.Equal(200 * 9525L, sy);

        Assert.True(EngineRunner.Export(dir, true, false).Success);
        using var doc = WordprocessingDocument.Open(Path.Combine(dir, "steps.docx"), false);
        var widths = doc.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().Select(e => e.Cx!.Value).ToList();
        Assert.All(widths, w => Assert.True(w <= 16 * 360000L));
        Assert.Contains(16 * 360000L, widths);
    }

    [Fact]
    public void Missing_pictures_do_not_break_the_docx()
    {
        var dir = TestFolder();
        File.Delete(Path.Combine(dir, "screenshots", "step-03-check.png"));
        Assert.True(EngineRunner.Export(dir, true, false).Success);
        using var doc = WordprocessingDocument.Open(Path.Combine(dir, "steps.docx"), false);
        NoErrors(doc);
        Assert.Equal(3, doc.MainDocumentPart!.Document!.Body!.Descendants<W.Drawing>().Count());
        Assert.Contains("picture not found", doc.MainDocumentPart.Document!.Body.InnerText);
    }

    // ---- xlsx --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Export_writes_a_valid_xlsx_with_one_row_per_step()
    {
        var dir = TestFolder();
        Assert.True(EngineRunner.Export(dir, false, true).Success);
        using var xl = SpreadsheetDocument.Open(Path.Combine(dir, "steps.xlsx"), false);
        NoErrors(xl);

        var wb = xl.WorkbookPart!;
        Assert.Equal("Steps|Info", string.Join("|", wb.Workbook!.Sheets!.Elements<S.Sheet>().Select(s => s.Name!.Value!)));
        var steps = (WorksheetPart)wb.GetPartById(wb.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!);
        var rows = steps.Worksheet!.Descendants<S.Row>().ToList();
        string Cell(S.Row r, int i) => r.Elements<S.Cell>().ElementAt(i).InnerText;

        Assert.Equal("Step|Action|Details|Check|Screenshot|Result|Notes", string.Join("|", Enumerable.Range(0, 7).Select(i => Cell(rows[0], i))));
        Assert.Equal(4, rows.Count);                 // header + 3 steps
        Assert.Contains("Click", Cell(rows[1], 1));
        Assert.Contains("Pick the first point", Cell(rows[1], 2));
        Assert.Contains("\n", Cell(rows[1], 2));     // sub-items joined with line breaks
        Assert.Equal("", Cell(rows[1], 3));
        Assert.Contains("line drawn", Cell(rows[3], 3));   // the checkpoint's expected text
        Assert.StartsWith("screenshots/step-01-click.png", Cell(rows[1], 4));   // one line per picture, the link opens the first
        Assert.Equal(3, steps.HyperlinkRelationships.Count());   // a link per step that has a picture
        Assert.All(steps.HyperlinkRelationships, r => Assert.False(Path.IsPathRooted(r.Uri.OriginalString)));
        Assert.Equal("", Cell(rows[1], 5));
        Assert.Equal("F2:F4", steps.Worksheet!.Descendants<S.DataValidation>().Single().SequenceOfReferences!.InnerText);
        Assert.Equal("\"PASS,FAIL\"", steps.Worksheet!.Descendants<S.Formula1>().Single().Text);
        Assert.Equal(S.PaneStateValues.Frozen, steps.Worksheet!.Descendants<S.Pane>().Single().State!.Value);

        var info = (WorksheetPart)wb.GetPartById(wb.Workbook!.Sheets!.Elements<S.Sheet>().Last().Id!);
        var all = info.Worksheet!.InnerText;
        Assert.Contains("draw test", all);
        Assert.Contains("Recorded 2026-10-10", all);
        Assert.Contains("a new empty drawing", all);
        Assert.Contains(dir, all);
    }

    [Fact]
    public void A_step_with_several_pictures_lists_all_of_them_and_links_the_first()
    {
        var md = "# t\n\n## Steps\n\n1. Click **Go**\n\n   ![a](screenshots/step-01-a.png)\n\n   ![b](screenshots/step-01-b%20x.png)\n\n   - Detail\n\n     ![c](screenshots/step-01-c.png)\n\n2. Next\n";
        var doc = new Retrace.Engine.Recordings.StepsDoc(md, _root);
        Assert.Equal(new[] { "screenshots/step-01-a.png", "screenshots/step-01-b%20x.png", "screenshots/step-01-c.png" }, doc.Steps[0].Pictures);
        Assert.Empty(doc.Steps[1].Pictures);
        var xlsx = Path.Combine(_root, "multi.xlsx");
        Retrace.Engine.Recordings.ExportXlsx.Write(doc, _root, xlsx);
        using var xl = SpreadsheetDocument.Open(xlsx, false);
        NoErrors(xl);
        var wb = xl.WorkbookPart!;
        var sheet = (WorksheetPart)wb.GetPartById(wb.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!);
        var cell = sheet.Worksheet!.Descendants<S.Row>().ElementAt(1).Elements<S.Cell>().ElementAt(4);
        Assert.Equal("screenshots/step-01-a.png\nscreenshots/step-01-b x.png\nscreenshots/step-01-c.png", cell.InnerText);
        Assert.Equal("screenshots/step-01-a.png", sheet.HyperlinkRelationships.Single().Uri.OriginalString);
    }

    [Fact]
    public void Steps_are_read_back_from_the_script()
    {
        var doc = StepsDoc.Load(Path.Combine(TestFolder(), "steps.md"));
        Assert.Equal("draw test", doc.Name);
        Assert.Equal(3, doc.Steps.Count);
        Assert.Equal([1, 2, 3], doc.Steps.Select(s => s.Number).ToArray());
        Assert.StartsWith("a new empty drawing", doc.StartWith);
        Assert.Equal("screenshots/step-02-dialog-before.png", doc.Steps[1].Pictures[0]);
        Assert.StartsWith("Check:", doc.Steps[2].Action);
    }

    // ---- overwrite, locked files ---------------------------------------------------------------------------

    [Fact]
    public void Beside_with_flags_writes_both_files_and_a_second_run_overwrites()
    {
        var dir = TestFolder();
        var rec = Path.Combine(dir, "recording.jsonl");
        Assert.True(EngineRunner.WriteSteps(rec, true, true).Success);
        Assert.True(File.Exists(Path.Combine(dir, "steps.docx")));
        Assert.True(File.Exists(Path.Combine(dir, "steps.xlsx")));
        File.WriteAllText(Path.Combine(dir, "steps.docx"), "junk");
        Assert.True(EngineRunner.Export(Path.Combine(dir, "steps.md"), true, true).Success);
        using var doc = WordprocessingDocument.Open(Path.Combine(dir, "steps.docx"), false);   // replaced the junk
        NoErrors(doc);
        Assert.Empty(Directory.GetFiles(dir, "*(new)*"));
    }

    [Fact]
    public void Export_from_a_recording_writes_the_script_first_when_it_is_missing()
    {
        var dir = TestFolder();
        File.Delete(Path.Combine(dir, "steps.md"));
        Assert.True(EngineRunner.Export(Path.Combine(dir, "recording.jsonl"), false, true).Success);
        Assert.True(File.Exists(Path.Combine(dir, "steps.md")));
        Assert.True(File.Exists(Path.Combine(dir, "steps.xlsx")));
    }

    [Fact]
    public void Locked_target_falls_back_to_a_new_name_and_says_so()
    {
        Assert.Equal(Path.Combine("d", "steps (new).docx"), Exporter.FallbackPath(Path.Combine("d", "steps.docx")));

        var dir = TestFolder();
        var locked = Path.Combine(dir, "steps.docx");
        // the move onto the real name fails the way a file open in Word does
        Action<string, string> move = (from, to) =>
        {
            if (to == locked) throw new IOException("being used by another process");
            File.Move(from, to, true);
        };
        var res = new EngineResult();
        Assert.True(Exporter.Export(Path.Combine(dir, "steps.md"), true, false, res, move));
        Assert.False(File.Exists(locked));
        Assert.True(File.Exists(Path.Combine(dir, "steps (new).docx")));
        Assert.Contains("steps.docx is open in Word; wrote steps (new).docx instead", Assert.Single(res.Warnings));
        Assert.Equal(Path.Combine(dir, "steps (new).docx"), res.Docx);
        Assert.True(res.Success);
        Assert.Empty(Directory.GetFiles(dir, ".*.tmp"));
    }
}
