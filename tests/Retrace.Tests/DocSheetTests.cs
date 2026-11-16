using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Edge cases of the document wording and of the Excel handoff sheet (review fixes). Synthetic data only.</summary>
public class DocSheetTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-sheet").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":3,"name":"sheet","started":"2026-11-16T10:00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""";

    string Md(params string[] lines)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N"), "recording.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllLines(p, new[] { Header }.Concat(lines));
        return ScriptWriter.Build(RecordingReader.Load(p), null, _dir, new List<string>());
    }

    static byte[] PngOf(int w, int h)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        foreach (var v in new[] { w, h }) b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        b.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0, 0, 0, 0]);
        return b.ToArray();
    }

    (WorksheetPart Sheet, Dictionary<int, S.Row> Rows, SpreadsheetDocument Doc) Sheet(string md, bool pictures = false, Func<string, int, (byte[] Png, int Width, int Height)?>? rescale = null)
    {
        if (pictures)
        {
            Directory.CreateDirectory(Path.Combine(_dir, "screenshots"));
            foreach (var f in new[] { "a", "b" }) File.WriteAllBytes(Path.Combine(_dir, "screenshots", f + ".png"), PngOf(2000, 1000));
        }
        var doc = new StepsDoc(md, _dir);
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        ExportXlsx.Write(doc, _dir, path, rescale);
        var xl = SpreadsheetDocument.Open(path, false);
        var wb = xl.WorkbookPart!;
        var sheet = (WorksheetPart)wb.GetPartById(wb.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!);
        return (sheet, sheet.Worksheet!.Descendants<S.Row>().ToDictionary(r => (int)r.RowIndex!.Value), xl);
    }

    static string Cell(Dictionary<int, S.Row> rows, int row, int col) => rows[row].Elements<S.Cell>().First(c => c.CellReference!.Value == $"{(char)('A' + col)}{row}").InnerText;

    // ---- wording -------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_icon_only_ribbon_button_is_named_by_its_item_id()
    {
        var md = Md("""{"t":10,"n":1,"type":"ribbon","id":"ID_Line","text":"","macro":"^C^C_line","tab":"Home","panel":"Draw"}""");
        Assert.Contains("1. Click **ID_Line**\n\n   *Ribbon › Home tab › Draw panel*", md);
        Assert.DoesNotContain("unnamed", md);
    }

    [Fact]
    public void An_untitled_dialog_is_the_dialog_and_the_replay_still_counts_it()
    {
        var md = Md("""{"t":100,"n":1,"type":"modal_enter","title":""}""", """{"t":1800,"n":2,"type":"modal_leave"}""", """{"t":1810,"n":3,"type":"dialog_values","for":1,"fields":[],"read":2,"closedWith":"ok"}""");
        Assert.Contains("1. Fill in the dialog\n", md);
        Assert.DoesNotContain("untitled", md);
        Assert.Equal(1, Retrace.Core.ScriptRef.DialogStepNumber(md, 1));
    }

    [Fact]
    public void Values_ended_by_a_tree_click_keep_the_trees_path_in_the_location_line()
    {
        var md = Md("""{"t":900,"n":1,"type":"palette_values","palette":"Pick","set":"Samples","source":"win32","fields":[{"key":"name:Size","label":"Size","kind":"edit","before":"1","after":"2"}],"read":4}""",
            """{"t":1010,"n":2,"type":"palette_click","palette":"Pick","set":"Samples","button":"B24","kind":"tree","tree":"name:PickTree","treeLabel":"Catalog","path":"Cabinets › Base › B24"}""");
        Assert.Contains("1. Fill in the **Pick** palette\n\n   *Pick palette › Catalog tree › Cabinets › Base*", md);
        Assert.Contains("   - Set **Size** to `2`\n   - Click **B24**\n", md);
    }

    [Fact]
    public void The_start_sentence_has_the_right_singulars_and_never_a_blind_plural()
    {
        string Start(string state, string doc = "Plan.dwg") =>
            Md($$"""{"t":1,"n":1,"type":"state","doc":"{{doc}}","state":{{state}}}""").Split('\n').First(l => l.StartsWith("**Start with:**"));
        Assert.Equal("**Start with:** the drawing Plan.dwg, 1 object. The drawing has 1 layer. Open or build it before the first step.",
            Start("""{"count":1,"byType":{"LINE":1},"byLayer":{"0":1},"layers":["0"],"hash":"a"}"""));   // one object: no "(all lines)"
        Assert.Contains("(other objects)", Start("""{"count":3,"byType":{"ACDBFOO":2,"ACDBBAR":1},"layers":["0"],"hash":"a"}""").Replace("(all other objects)", "(other objects)"));
        Assert.DoesNotContain("acdbfoos", Start("""{"count":3,"byType":{"ACDBFOO":2,"LINE":1},"layers":["0"],"hash":"a"}"""));
        Assert.Equal("**Start with:** the empty drawing Plan.dwg. Open it before the first step.", Start("""{"count":0,"byType":{},"layers":["0"],"hash":"a"}"""));
        Assert.Equal("**Start with:** a new, empty drawing.", Start("""{"count":0,"byType":{},"layers":["0"],"hash":"a"}""", "Drawing1.dwg"));
        // layers: only the drawing's own count, Defpoints left out; none known: no layer sentence
        Assert.Contains("The drawing has 2 layers.", Start("""{"count":5,"byType":{"LINE":5},"byLayer":{"0":5},"layers":["0","A","Defpoints"],"hash":"a"}"""));
        Assert.DoesNotContain("layer", Start("""{"count":5,"byType":{"LINE":5},"byLayer":{"0":5},"hash":"a"}"""));
    }

    [Fact]
    public void A_where_line_survives_spaces_around_its_levels()
    {
        var md = Md("""{"t":900,"n":1,"type":"palette_click","palette":" Pick ","set":"Samples","button":"B24","kind":"tree","tree":"name:T","treeLabel":" Catalog ","path":" Cabinets  › Base › B24 "}""");
        Assert.Contains("\n   *Pick palette › Catalog tree › Cabinets › Base*\n", md);
        Assert.True(StepsDoc.IsWhere(Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.ParagraphBlock>(new StepsDoc("# t\n\n## Steps\n\n1. A\n\n   *Pick palette › Cabinets*\n", _dir).Ast).Last()));
    }

    // ---- the end check -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_end_check_says_what_the_drawing_holds_at_the_end_and_lands_in_the_last_rows_Expected()
    {
        var md = Md("""{"t":10,"n":1,"type":"ribbon","id":"ID_Line","text":"Line","tab":"Home","panel":"Draw"}""", """{"t":20,"n":2,"type":"end_counts","modelspace":2,"layers":1,"layouts":1}""");
        Assert.Contains("**End check:** Model space has 2 objects, 1 layer, 1 layout.", md);
        var (_, rows, xl) = Sheet(md);
        using (xl) Assert.Equal("Model space has 2 objects, 1 layer, 1 layout", Cell(rows, 7, 4));
        Assert.DoesNotContain("End check", Md("""{"t":10,"n":1,"type":"ribbon","id":"ID_Line","text":"Line"}"""));   // no end_counts: nothing invented
    }

    // ---- Excel ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_old_steps_md_still_gives_one_row_per_action()
    {
        var md = "# t\n\n## Steps\n\n1. A dialog opens: **Items**. Make these changes, then confirm:\n   - Set **Rows** to `5` (was `3`)\n   - Tick **Snap**\n2. In the **Box** palette (Samples), make these changes, then click **Draw box**:\n   - Set **Width** to `35` (was `20`)\n";
        var doc = new StepsDoc(md, _dir);
        Assert.Equal(2, doc.Rows.Count);
        Assert.Equal("Set Rows to 5 (was 3)\nTick Snap", doc.Rows[0].Details);
        Assert.Equal("Set Width to 35 (was 20)", doc.Rows[1].Details);
    }

    [Fact]
    public void A_checkpoints_description_is_in_its_rows_Details()
    {
        var md = "# t\n\n## Steps\n\n1. **Check:** grid drawn — the drawing should now have 1 LINE\n   - near the lower-left corner\n";
        var doc = new StepsDoc(md, _dir);
        Assert.Equal("near the lower-left corner", doc.Rows[0].Details);
        Assert.Equal("grid drawn — the drawing should now have 1 LINE", doc.Rows[0].Expected);
    }

    [Fact]
    public void A_long_change_list_gets_a_tall_enough_row_and_a_row_without_a_picture_gets_a_height_too()
    {
        var details = string.Join("\n", Enumerable.Range(1, 14).Select(i => $"- Set **Field {i}** to `{i}`"));
        var md = "# t\n\n## Steps\n\n1. Fill in the **Big** dialog\n" + string.Join("\n", Enumerable.Range(1, 14).Select(i => $"   - Set **Field {i}** to `{i}`")) + "\n   - Click **OK**\n\n   ![a](screenshots/a.png)\n\n2. Click **Go**\n";
        var (sheet, rows, xl) = Sheet(md, pictures: true);
        using (xl)
        {
            Assert.True(rows[7].Height!.Value >= 15 * 15 + 6);          // 15 lines of Details are all visible, though the picture alone is 210 px (about 170 pt)
            Assert.True(rows[7].CustomHeight!.Value);
            Assert.True(rows[8].Height!.Value >= 21 && rows[8].CustomHeight!.Value);   // no picture: the text decides, not auto-fit
            Assert.Contains("Click OK", Cell(rows, 7, 3));
        }
    }

    [Fact]
    public void Step_numbers_are_numbers_and_sub_step_labels_are_text_and_no_rows_means_no_Not_run_count()
    {
        var md = "# t\n\n## Steps\n\n1. Click **A**\n\n   - Press Enter\n";
        var (_, rows, xl) = Sheet(md);
        using (xl)
        {
            Assert.Equal(S.CellValues.Number, rows[7].Elements<S.Cell>().First().DataType!.Value);
            Assert.Equal(S.CellValues.InlineString, rows[8].Elements<S.Cell>().First().DataType!.Value);
            Assert.Equal("1.1", Cell(rows, 8, 0));
        }
        var (_, empty, xl2) = Sheet("# t\n\n## Steps\n\n");
        using (xl2)
        {
            Assert.Contains("Not run: 0", empty[4].InnerText);
            Assert.DoesNotContain("Not run: 1", empty[4].InnerText);
            Assert.Null(empty[4].Elements<S.Cell>().FirstOrDefault(c => c.CellFormula != null));
        }
    }

    [Fact]
    public void A_wide_picture_is_stored_smaller_when_a_rescaler_is_there_and_as_it_is_when_not()
    {
        var md = "# t\n\n## Steps\n\n1. Click **A**\n\n   ![a](screenshots/a.png)\n";
        var (sheet, _, xl) = Sheet(md, pictures: true);
        long plain;
        using (xl) { using var s = sheet.DrawingsPart!.ImageParts.Single().GetStream(); plain = s.Length; }
        Assert.Equal(new FileInfo(Path.Combine(_dir, "screenshots", "a.png")).Length, plain);   // no rescaler: the file as it is
        var asked = new List<(string, int)>();
        var (sheet2, _, xl2) = Sheet(md, pictures: true, rescale: (file, width) => { asked.Add((Path.GetFileName(file), width)); return (new byte[] { 1, 2, 3 }, 1280, 640); });
        using (xl2) { using var s = sheet2.DrawingsPart!.ImageParts.Single().GetStream(); Assert.Equal(3, s.Length); }
        Assert.Equal(("a.png", 1280), asked.Single());
        var (sheet3, _, xl3) = Sheet(md, pictures: true, rescale: (_, _) => (new byte[10_000], 1280, 640));   // a copy that is not smaller is not used
        using (xl3) { using var s = sheet3.DrawingsPart!.ImageParts.Single().GetStream(); Assert.Equal(plain, s.Length); }
    }

    // ---- Word ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_word_document_numbers_sub_steps_like_the_sheet_and_keeps_bullets_for_a_change_list()
    {
        var md = "# t\n\n## Steps\n\n1. Click **Go**\n\n   - Pick the point\n   - Press Enter\n\n2. Fill in the **Items** dialog\n\n   - Set **Rows** to `5`\n   - Click **OK**\n\n3. Click **Stop**\n\n   - Press Enter\n";
        var path = Path.Combine(_dir, "n.docx");
        ExportDocx.Write(new StepsDoc(md, _dir), path);
        using var w = WordprocessingDocument.Open(path, false);
        var texts = w.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>().Select(p => p.InnerText).ToList();
        Assert.Contains(texts, t => t.StartsWith("1.1.") && t.Contains("Pick the point"));
        Assert.Contains(texts, t => t.StartsWith("1.2.") && t.Contains("Press Enter"));
        Assert.Contains(texts, t => t.StartsWith("•") && t.Contains("Set Rows to 5"));   // a dialog's change list is not numbered
        Assert.Contains(texts, t => t.StartsWith("3.1.") && t.Contains("Press Enter"));
        Assert.DoesNotContain(texts, t => t.StartsWith("2.1."));
    }
}
