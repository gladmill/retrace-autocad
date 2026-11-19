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
        Assert.DoesNotContain("Files in this folder", text);
        Assert.Empty(body.Descendants<W.Table>());   // the files table is gone
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
    public void A_steps_text_stays_on_the_page_of_its_first_picture()
    {
        var dir = TestFolder();
        Assert.True(EngineRunner.Export(dir, true, false).Success);
        using var doc = WordprocessingDocument.Open(Path.Combine(dir, "steps.docx"), false);
        NoErrors(doc);
        var body = doc.MainDocumentPart!.Document!.Body!;
        var firstPictures = body.Elements<W.Paragraph>().Where(p => p.Descendants<W.Drawing>().Any() && p.PreviousSibling() is W.Paragraph prev && !prev.Descendants<W.Drawing>().Any()).ToList();
        Assert.NotEmpty(firstPictures);
        foreach (var pic in firstPictures)
        {
            var text = (W.Paragraph)pic.PreviousSibling()!;
            Assert.NotNull(text.ParagraphProperties!.KeepNext);    // the text is kept with the picture under it...
            Assert.NotNull(text.ParagraphProperties.KeepLines);    // ...and its own lines together
            var order = text.ParagraphProperties.ChildElements.Select(c => c.GetType().Name).ToList();
            Assert.True(order.IndexOf("KeepNext") < order.IndexOf("KeepLines"));
            if (order.Contains("SpacingBetweenLines")) Assert.True(order.IndexOf("KeepLines") < order.IndexOf("SpacingBetweenLines"));   // schema order
        }
        Assert.Contains(firstPictures, p => ((W.Paragraph)p.PreviousSibling()!).InnerText.Contains("Draw panel"));
    }

    [Fact]
    public void A_palette_step_with_change_lines_keeps_text_and_changes_with_its_first_picture()
    {
        var dir = TestFolder();
        var pic = "screenshots/" + Path.GetFileName(Directory.GetFiles(Path.Combine(dir, "screenshots"), "*.png").OrderBy(f => f).First());
        File.WriteAllText(Path.Combine(dir, "steps.md"), $"""
            # t

            ## Steps

            1. First step, no picture

            2. In the **Box** palette (Samples), make these changes, then click **Draw**:
               - Size: 1 to 2
               - Colour: red

               ![one]({pic})

               ![two]({pic})

            3. A dialog opens: **Draw grid**. Make these changes, then confirm:
               - Rows: 3

               Before:

               ![b]({pic})

               After (what it should look like before you press OK):

               ![a]({pic})

            > 4. quoted step
            >
            >    ![q]({pic})
            """.Replace("\n            ", "\n").TrimStart());
        Assert.True(EngineRunner.Export(dir, true, false).Success);
        using var doc = WordprocessingDocument.Open(Path.Combine(dir, "steps.docx"), false);
        var paras = doc.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>().ToList();
        bool Kept(W.Paragraph p) => p.ParagraphProperties?.KeepNext != null && p.ParagraphProperties.KeepLines != null;
        bool Pic(W.Paragraph p) => p.Descendants<W.Drawing>().Any();
        W.Paragraph Find(string t) => paras.FirstOrDefault(p => p.InnerText.Contains(t)) ?? throw new Xunit.Sdk.XunitException("no paragraph " + t + " in: " + string.Join(" / ", paras.Select(p => p.InnerText)));
        Assert.False(Kept(Find("First step")));                       // no picture after it
        Assert.True(Kept(Find("In the Box palette")));                // the step text...
        Assert.True(Kept(Find("Size: 1 to 2")));                      // ...its change lines...
        Assert.True(Kept(Find("Colour: red")));
        Assert.True(paras.Any(Pic), string.Join(" / ", paras.Select(p => p.InnerText))); Assert.False(Kept(paras.First(Pic)));                         // ...down to the first picture, which is not kept with the next one
        Assert.True(Kept(Find("A dialog opens")));
        Assert.True(Kept(Find("Rows: 3")));
        Assert.True(Kept(Find("Before:")));                           // a label stays with the picture under it
        Assert.True(Kept(Find("After (what")));
        Assert.True(Kept(Find("quoted step")));                       // also inside a quote
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
    public void Export_writes_a_valid_xlsx_with_one_row_per_thing_to_do()
    {
        var dir = TestFolder();
        Assert.True(EngineRunner.Export(dir, false, true).Success);
        using var xl = SpreadsheetDocument.Open(Path.Combine(dir, "steps.xlsx"), false);
        NoErrors(xl);

        var wb = xl.WorkbookPart!;
        Assert.Equal("Steps|Info", string.Join("|", wb.Workbook!.Sheets!.Elements<S.Sheet>().Select(s => s.Name!.Value!)));
        var steps = (WorksheetPart)wb.GetPartById(wb.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!);
        var rows = steps.Worksheet!.Descendants<S.Row>().ToDictionary(r => (int)r.RowIndex!.Value);
        string Cell(int row, int col) => rows[row].Elements<S.Cell>().First(c => c.CellReference!.Value == $"{(char)('A' + col)}{row}").InnerText;

        // the table: a header row, then a row per thing the tester does, numbered like the Word document (1, then 1.1, 1.2 ... for its sub-steps)
        Assert.Equal("#|Action|Where|Details|Expected|Picture|Result|Notes|Evidence", string.Join("|", Enumerable.Range(0, 9).Select(i => Cell(6, i))));
        Assert.Equal("1|1.1|1.2|2|2.1|3", string.Join("|", Enumerable.Range(7, 6).Select(r => Cell(r, 0))));
        Assert.Equal("Click Line", Cell(7, 1));
        Assert.Equal("Ribbon › Home tab › Draw panel", Cell(7, 2));
        Assert.Contains("Pick the first point", Cell(8, 1));
        Assert.Equal("", Cell(8, 2));
        Assert.Equal("Type DRAWGRID and press Enter", Cell(10, 1));
        Assert.Equal("Command line", Cell(10, 2));
        Assert.Equal("Fill in the Draw grid dialog", Cell(11, 1));
        Assert.Equal("Check the drawing", Cell(12, 1));
        Assert.Contains("line drawn", Cell(12, 4));   // the checkpoint's text is the Expected column
        Assert.All(Enumerable.Range(7, 6), r => Assert.Equal("Not run", Cell(r, 6)));

        // the picture of a row is inside the file (xl/media) and anchored in its Picture cell; the file name is a relative link under it
        var drawing = steps.DrawingsPart!;
        var anchors = drawing.WorksheetDrawing!.Descendants<DocumentFormat.OpenXml.Drawing.Spreadsheet.OneCellAnchor>().ToList();
        Assert.Equal(4, anchors.Count);          // the ribbon click, its second answer, the dialog, the check: one picture per row
        Assert.Equal(4, drawing.ImageParts.Count());
        Assert.All(anchors, a => Assert.Equal("5", a.FromMarker!.ColumnId!.Text));
        Assert.Equal(new[] { "6", "8", "10", "11" }, anchors.Select(a => a.FromMarker!.RowId!.Text));   // zero-based rows 7, 9, 11, 12
        var wide = anchors[0].Extent!;           // 2000 x 1000 px: 420 px wide, 210 px high
        Assert.Equal(420L * 9525, wide.Cx!.Value);
        Assert.Equal(210L * 9525, wide.Cy!.Value);
        Assert.Equal("", Cell(7, 5));   // no file name and no link under the picture
        Assert.Empty(steps.HyperlinkRelationships);
        Assert.Empty(steps.Worksheet!.Descendants<S.Hyperlink>());
        Assert.True(double.Parse(rows[7].Height!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture) >= 210 * 0.75);   // the row is as high as the picture

        // Result: a drop-down with colours; Overall result: a formula; the table's header is frozen and repeated when printed
        var validation = steps.Worksheet!.Descendants<S.DataValidation>().Single();
        Assert.Equal("G7:G12", validation.SequenceOfReferences!.InnerText);
        Assert.Equal("\"Pass,Fail,Blocked,Not run\"", validation.Formula1!.Text);
        Assert.Contains(steps.Worksheet.Descendants<S.ConditionalFormattingRule>(), r => r.Elements<S.Formula>().Any(f => f.Text == "\"Fail\""));
        var overall = rows[3].Elements<S.Cell>().Single(c => c.CellReference!.Value == "F3").CellFormula!.Text;
        Assert.Equal("IF(COUNTIF(G7:G12,\"Fail\")>0,\"Fail\",IF(COUNTIF(G7:G12,\"Pass\")=ROWS(G7:G12),\"Pass\",\"In progress\"))", overall);
        Assert.Contains("COUNTIF(G7:G12,\"Pass\")", rows[4].Elements<S.Cell>().First(c => c.CellFormula != null).CellFormula!.Text);
        Assert.Equal(S.PaneStateValues.Frozen, steps.Worksheet!.Descendants<S.Pane>().Single().State!.Value);
        Assert.Equal(S.OrientationValues.Landscape, steps.Worksheet.Descendants<S.PageSetup>().Single().Orientation!.Value);
        Assert.Equal("Steps!$6:$6", wb.Workbook.Descendants<S.DefinedName>().Single().Text);

        // the top block: test, recorded, starts with (no settings line), and the sign-off cells
        var top = string.Concat(Enumerable.Range(1, 4).Select(r => rows[r].InnerText));
        Assert.Contains("draw test", top);
        Assert.Contains("2026-10-10, with AutoCAD 2026 25.1.0.0", top);   // the label says "Recorded", the value is the date and the AutoCAD version
        Assert.Contains("a new, empty drawing", top);
        Assert.DoesNotContain("Settings", top);
        Assert.Contains("Tester", top);
        Assert.Contains("Overall result", top);

        var info = (WorksheetPart)wb.GetPartById(wb.Workbook!.Sheets!.Elements<S.Sheet>().Last().Id!);
        var all = info.Worksheet!.InnerText;
        Assert.Contains("draw test", all);
        Assert.Contains(Path.GetFileName(dir), all);
        Assert.DoesNotContain(dir, all);   // the folder's name, not its path
    }

    [Fact]
    public void A_row_has_one_picture_and_a_dialog_row_shows_the_filled_in_one()
    {
        var md = "# t\n\n## Steps\n\n1. Click **Go**\n\n   *Ribbon › Home tab*\n\n   ![a](screenshots/step-01-a.png)\n\n   ![b](screenshots/step-01-b%20x.png)\n\n2. Fill in the **Opts** dialog\n\n   - Set **Rows** to `5`\n   - Click **OK**\n\n   Before:\n\n   ![c](screenshots/step-02-before.png)\n\n   After (what it should look like before you press OK):\n\n   ![d](screenshots/step-02-after.png)\n";
        var doc = new Retrace.Engine.Recordings.StepsDoc(md, _root);
        Assert.Equal(2, doc.Steps.Count);
        Assert.Equal(2, doc.Rows.Count);
        Assert.Equal("screenshots/step-01-a.png", doc.Rows[0].Picture);   // a click: its first picture
        Assert.Equal("Ribbon › Home tab", doc.Rows[0].Where);
        Assert.Equal("screenshots/step-02-after.png", doc.Rows[1].Picture);   // a dialog: the filled-in one
        Assert.Equal("Set Rows to 5\nClick OK", doc.Rows[1].Details);
        Directory.CreateDirectory(Path.Combine(_root, "screenshots"));
        foreach (var f in new[] { "step-01-a", "step-01-b x", "step-02-before" }) File.WriteAllBytes(Path.Combine(_root, "screenshots", f + ".png"), PngOf(840, 300));
        File.WriteAllBytes(Path.Combine(_root, "screenshots", "step-02-after.png"), PngOf(600, 300));   // a different size: which picture sits on which row shows in the anchors
        var xlsx = Path.Combine(_root, "multi.xlsx");
        Retrace.Engine.Recordings.ExportXlsx.Write(doc, _root, xlsx);
        using var xl = SpreadsheetDocument.Open(xlsx, false);
        NoErrors(xl);
        var wb = xl.WorkbookPart!;
        var sheet = (WorksheetPart)wb.GetPartById(wb.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!);
        Assert.Equal(2, sheet.DrawingsPart!.ImageParts.Count());   // never two pictures of one moment: one per row
        Assert.Empty(sheet.HyperlinkRelationships);
        // which picture is on which row: a click shows its first picture (840 x 300: 420 x 150 px) on row 7, the dialog its filled-in one (600 x 300: 420 x 210 px) on row 8
        var anchors = sheet.DrawingsPart!.WorksheetDrawing!.Descendants<DocumentFormat.OpenXml.Drawing.Spreadsheet.OneCellAnchor>().ToList();
        Assert.Equal(new[] { "6", "7" }, anchors.Select(x => x.FromMarker!.RowId!.Text));
        Assert.Equal(new[] { 150L * 9525, 210L * 9525 }, anchors.Select(x => x.Extent!.Cy!.Value));
        Assert.Equal("Set Rows to 5\nClick OK", sheet.Worksheet!.Descendants<S.Row>().First(r => r.RowIndex!.Value == 8).Elements<S.Cell>().First(c => c.CellReference!.Value == "D8").InnerText);
    }

    [Fact]
    public void A_thumbnail_is_420_px_wide_and_at_most_380_px_high()
    {
        Assert.Equal((420L * 9525, 210L * 9525), Retrace.Engine.Recordings.ExportXlsx.Thumb(2000, 1000));
        Assert.Equal((300L * 9525, 200L * 9525), Retrace.Engine.Recordings.ExportXlsx.Thumb(300, 200));   // never enlarged
        var tall = Retrace.Engine.Recordings.ExportXlsx.Thumb(600, 1900);
        Assert.Equal(380L * 9525, tall.Cy);
    }

    [Fact]
    public void The_settings_line_is_left_out_of_the_word_and_excel_documents_and_kept_in_steps_md()
    {
        var dir = TestFolder("settings");
        var rec = Path.Combine(dir, "recording.jsonl");
        File.WriteAllLines(rec, Lines.Select(l => l.StartsWith("{\"type\":\"header\"") ? l.Replace("\"workspace\":\"Drafting\"", "\"workspace\":\"Drafting\",\"settings\":{\"screenshotMode\":\"smart\",\"recordZoom\":\"extents\"}") : l));
        Assert.True(EngineRunner.WriteSteps(rec).Success);
        Assert.Contains("Settings (not default)", File.ReadAllText(Path.Combine(dir, "steps.md")));
        Assert.True(EngineRunner.Export(dir, true, true).Success);
        using var w = WordprocessingDocument.Open(Path.Combine(dir, "steps.docx"), false);
        Assert.DoesNotContain("Settings", w.MainDocumentPart!.Document!.Body!.InnerText);
        Assert.Contains("recorded", w.MainDocumentPart.Document.Body.InnerText + "recorded");
        using var x = SpreadsheetDocument.Open(Path.Combine(dir, "steps.xlsx"), false);
        foreach (var part in x.WorkbookPart!.WorksheetParts) Assert.DoesNotContain("Settings", part.Worksheet!.InnerText);
        Assert.Contains("screenshotMode", File.ReadAllText(rec));   // the recording keeps its settings object
    }

    [Fact]
    public void Steps_are_read_back_from_the_script()
    {
        var doc = StepsDoc.Load(Path.Combine(TestFolder(), "steps.md"));
        Assert.Equal("draw test", doc.Name);
        Assert.Equal(3, doc.Steps.Count);
        Assert.Equal([1, 2, 3], doc.Steps.Select(s => s.Number).ToArray());
        Assert.StartsWith("a new, empty drawing", doc.StartWith);
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
