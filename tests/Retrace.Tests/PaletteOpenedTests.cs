using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Retrace.Core;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>A command a ribbon / Quick Access Toolbar click ran that opened a palette: the decision (opened, already open, hidden, unreadable), the green box without an arrow, the caption under the
/// end picture in steps.md / Word / Excel, old recordings unchanged, and the "Quick Access Toolbar" location line. Synthetic recordings only.</summary>
public class PaletteOpenedTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("retrace-test-opened").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    // ---- the decision ---------------------------------------------------------------------------------------------------

    static readonly PxRect Main = new(0, 0, 1920, 1080);
    static PaletteState P(long id, string name, bool shown, PxRect? rect = null) => new(id, name, "Set", shown, shown ? rect ?? new PxRect(1500, 100, 1900, 900) : null);

    [Fact]
    public void A_palette_that_was_closed_at_the_start_and_shows_now_is_opened_with_its_rectangle()
    {
        var d = PaletteOpening.Decide([P(1, "Pick", false)], [P(1, "Pick", true, new PxRect(1500, 100, 1900, 900))], Main);
        Assert.Equal("Pick", d.Opened!.Name);
        Assert.Equal(new PxRect(1500, 100, 1900, 900), d.Opened.Rect);
    }

    [Fact]
    public void A_palette_that_did_not_exist_at_the_start_counts_as_opened()
    {
        var d = PaletteOpening.Decide([], [P(7, "Catalog", true)], Main);
        Assert.Equal("Catalog", d.Opened!.Name);
    }

    [Fact]
    public void A_palette_that_was_open_already_gets_no_box_and_the_reason_says_so()
    {
        var d = PaletteOpening.Decide([P(1, "Pick", true)], [P(1, "Pick", true)], Main);
        Assert.Null(d.Opened);
        Assert.Contains("already open: Pick", d.Reason);
    }

    [Fact]
    public void A_palette_that_is_collapsed_or_auto_hidden_or_closed_again_gets_no_box()
    {
        var d = PaletteOpening.Decide([P(1, "Pick", false)], [P(1, "Pick", false)], Main);
        Assert.Null(d.Opened);
        Assert.Contains("closed, collapsed or auto-hidden: Pick", d.Reason);
        Assert.Null(PaletteOpening.Decide([], [], Main).Opened);   // nothing at all
    }

    [Fact]
    public void An_unreadable_or_empty_rectangle_is_never_boxed()
    {
        var after = new[] { new PaletteState(1, "Pick", "Set", true, null) };
        var d = PaletteOpening.Decide([], after, Main);
        Assert.Null(d.Opened);
        Assert.Contains("rectangle unreadable", d.Reason);
        Assert.Null(PaletteOpening.Decide([], [new PaletteState(1, "Pick", "Set", true, new PxRect(10, 10, 10, 50))], Main).Opened);
    }

    [Fact]
    public void A_palette_outside_the_autocad_window_or_with_no_window_known_is_never_boxed()
    {
        var floating = P(1, "Pick", true, new PxRect(2000, 100, 2300, 700));
        var d = PaletteOpening.Decide([], [floating], Main);
        Assert.Null(d.Opened);
        Assert.Contains("outside AutoCAD's window", d.Reason);
        Assert.Null(PaletteOpening.Decide([], [P(1, "Pick", true)], null).Opened);
    }

    [Fact]
    public void Of_several_that_opened_the_leftmost_is_boxed_and_the_others_are_only_in_the_reason()
    {
        var d = PaletteOpening.Decide([], [P(2, "Right", true, new PxRect(1400, 50, 1900, 500)), P(1, "Left", true, new PxRect(10, 200, 400, 900))], Main);
        Assert.Equal("Left", d.Opened!.Name);
        Assert.Contains("also visible, not marked: Right", d.Reason);
    }

    [Fact]
    public void An_unmarkable_palette_does_not_stop_a_markable_one()
    {
        var d = PaletteOpening.Decide([], [new PaletteState(1, "Lost", "S", true, null), P(2, "Pick", true)], Main);
        Assert.Equal("Pick", d.Opened!.Name);
        Assert.Contains("Lost (window rectangle unreadable)", d.Reason);
    }

    // ---- the mark: a box, no arrow -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, false, PictureMark.Box)]            // a palette that opened: the frame alone, even with a click point noted
    [InlineData(true, true, true, PictureMark.Box)]
    [InlineData(true, false, true, PictureMark.None)]           // box-only without a rectangle: nothing (never an arrow at a point)
    [InlineData(false, true, true, PictureMark.BoxAndArrow)]    // a click: frame and arrow
    [InlineData(false, true, false, PictureMark.BoxAndArrow)]
    [InlineData(false, false, true, PictureMark.ArrowAtPoint)]
    [InlineData(false, false, false, PictureMark.None)]
    public void A_box_only_picture_never_gets_an_arrow_and_a_click_picture_does(bool boxOnly, bool rect, bool click, PictureMark expected) =>
        Assert.Equal(expected, PictureMarks.Choose(boxOnly, rect, click));

    [Fact]
    public void A_box_only_frame_is_drawn_without_any_arrow_pixels()
    {
        // the drawing the box-only choice leads to: Annotate.Draw alone leaves everything farther than the frame untouched
        const int w = 800, h = 600;
        var box = new PxRect(500, 150, 760, 520);
        var ab = Annotate.ToPicture(box.Left, box.Top, box.Right, box.Bottom, 0, 0, w, h, w, h, 0)!.Value;
        var px = new byte[w * h * 4];
        Annotate.Draw(px, w, h, new[] { ab });
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x < box.Left - 8 || x > box.Right + 8 || y < box.Top - 8 || y > box.Bottom + 8) Assert.Equal(0, px[(y * w + x) * 4 + 3]);
        Assert.NotEqual(0, px[((box.Top - 1) * w + box.Left + 100) * 4 + 3] + px[(box.Top * w + box.Left + 100) * 4 + 3]);   // the frame is there
    }

    // ---- the recording and the documents ---------------------------------------------------------------------------------

    const string Header = """{"type":"header","schema":3,"name":"opens","started":"2026-11-16T10:00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""";

    /// <summary>A recording of one ribbon click whose command ends with a whole-window picture; <paramref name="opened"/> is the `opened` field of that picture's `shot` event (null: none, as before).</summary>
    string Folder(string? opened, string? bar = null, string name = "t")
    {
        var dir = Path.Combine(_root, name + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        var barPart = bar == null ? "" : $",\"bar\":\"{bar}\"";
        var endShot = "{\"t\":1700,\"n\":6,\"type\":\"shot\",\"for\":4,\"file\":\"screenshots/004-cmd-end.png\",\"what\":\"window\"" + (opened == null ? "" : ",\"opened\":" + opened) + "}";
        File.WriteAllLines(Path.Combine(dir, "recording.jsonl"),
        [
            Header,
            """{"t":10,"n":1,"type":"state","state":{"count":0,"byType":{},"layers":["0"],"hash":"e3"}}""",
            $$"""{"t":1000,"n":2,"type":"ribbon","id":"ID_Pick","text":"Pick","macro":"^C^C_drawpick","itemType":"RibbonCommandButton","tab":"Samples","panel":"Pick"{{barPart}}}""",
            """{"t":1010,"n":3,"type":"cmd_start","command":"DRAWPICK","origin":"ribbon","via":"ID_Pick"}""",
            """{"t":1400,"n":4,"type":"cmd_end","command":"DRAWPICK"}""",
            """{"t":1500,"n":5,"type":"shot","for":2,"file":"screenshots/002-ribbon.png","what":"window"}""",
            endShot,
            """{"t":5000,"n":7,"type":"session_end"}""",
        ]);
        foreach (var f in new[] { "002-ribbon", "004-cmd-end" }) File.WriteAllBytes(Path.Combine(dir, "screenshots", f + ".png"), Png(1400, 800));
        Assert.True(EngineRunner.WriteSteps(Path.Combine(dir, "recording.jsonl")).Success);
        return dir;
    }

    static byte[] Png(int w, int h)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        foreach (var v in new[] { w, h }) b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        b.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0, 0, 0, 0]);
        return b.ToArray();
    }

    const string Opened = """{"palette":"Pick"}""";

    [Fact]
    public void Steps_md_puts_the_caption_right_under_the_end_picture_only()
    {
        var md = File.ReadAllText(Path.Combine(Folder(Opened), "steps.md"));
        var lines = md.Split('\n').ToList();
        int end = lines.FindIndex(l => l.Contains("004") || l.Contains("-end"));
        Assert.True(end >= 0, md);
        Assert.Equal("   The **Pick** palette opens.", lines.Skip(end + 1).First(l => l.Trim().Length > 0));
        Assert.Single(lines, l => l.Contains("palette opens."));   // not under the click picture
        Assert.True(md.IndexOf("palette opens.", StringComparison.Ordinal) > md.IndexOf("-click", StringComparison.Ordinal));
    }

    [Fact]
    public void An_old_recording_without_the_field_gives_the_same_steps_md_as_before()
    {
        var withField = File.ReadAllText(Path.Combine(Folder(Opened), "steps.md"));
        var without = File.ReadAllText(Path.Combine(Folder(null), "steps.md"));
        Assert.DoesNotContain("palette opens", without);
        // the only difference is the caption lines
        string Squash(string s) => System.Text.RegularExpressions.Regex.Replace(s.Replace("\r", ""), "\n{3,}", "\n\n");
        Assert.Equal(Squash(without), Squash(string.Join("\n", withField.Replace("\r", "").Split('\n').Where(l => !l.Contains("palette opens.")))));
    }

    [Theory]
    [InlineData("""{"palette":""}""")]
    [InlineData("""{"other":"x"}""")]
    [InlineData("""{}""")]
    [InlineData("""5""")]
    [InlineData("""null""")]
    public void A_field_without_a_usable_name_is_ignored(string opened)
    {
        var md = File.ReadAllText(Path.Combine(Folder(opened), "steps.md"));
        Assert.DoesNotContain("palette opens", md);
    }

    [Fact]
    public void The_recording_reader_reads_the_name_and_ignores_events_without_the_field()
    {
        using var d = JsonDocument.Parse("""{"n":6,"type":"shot","for":4,"file":"x.png","what":"window","opened":{"palette":" Pick "}}""");
        using var e = JsonDocument.Parse("""{"n":6,"type":"shot","for":4,"file":"x.png","what":"window"}""");
        Assert.Equal("Pick", new RecordingEvent { Raw = d.RootElement.Clone(), Type = "shot" }.OpenedPalette);
        Assert.Null(new RecordingEvent { Raw = e.RootElement.Clone(), Type = "shot" }.OpenedPalette);
        Assert.Equal("The **Pick** palette opens.", StepWording.OpensCaption("Pick"));
        Assert.True(StepWording.IsOpensCaption("The Pick palette opens."));
        Assert.False(StepWording.IsOpensCaption("The drawing is empty."));
    }

    [Fact]
    public void Word_has_the_caption_as_its_own_paragraph_after_the_end_picture_with_the_name_bold()
    {
        var dir = Folder(Opened);
        Assert.True(EngineRunner.Export(dir, true, false).Success);
        using var w = WordprocessingDocument.Open(Path.Combine(dir, "steps.docx"), false);
        var paras = w.MainDocumentPart!.Document!.Body!.Descendants<W.Paragraph>().ToList();
        int cap = paras.FindIndex(p => p.InnerText == "The Pick palette opens.");
        Assert.True(cap > 0);
        Assert.Contains(paras[cap].Elements<W.Run>(), r => r.InnerText == "Pick" && r.RunProperties?.Bold != null);
        Assert.NotEmpty(paras[cap - 1].Descendants<W.Drawing>());   // the picture is the paragraph before it
        Assert.Equal(2, w.MainDocumentPart.Document.Body.Descendants<W.Drawing>().Count());   // both pictures still there, nothing cropped or dropped
    }

    [Fact]
    public void Excel_puts_the_caption_in_expected_because_it_is_what_the_tester_should_see()
    {
        var dir = Folder(Opened);
        Assert.True(EngineRunner.Export(dir, false, true).Success);
        using var xl = SpreadsheetDocument.Open(Path.Combine(dir, "steps.xlsx"), false);
        var wb = xl.WorkbookPart!;
        var sheet = (WorksheetPart)wb.GetPartById(wb.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!);
        var rows = sheet.Worksheet!.Descendants<S.Row>().ToDictionary(r => (int)r.RowIndex!.Value);
        string Cell(int row, int col) => rows[row].Elements<S.Cell>().FirstOrDefault(c => c.CellReference!.Value == $"{(char)('A' + col)}{row}")?.InnerText ?? "";
        var header = rows.First(r => Cell(r.Key, 0) == "#").Key;
        Assert.Equal("Expected", Cell(header, 4));
        var row = header + 1;
        Assert.Equal("Click Pick", Cell(row, 1));
        Assert.Equal("The Pick palette opens.", Cell(row, 4));   // the Expected cell of the written sheet
        Assert.Equal("", Cell(row, 3));                          // not in Details
    }

    [Fact]
    public void Excel_has_the_result_how_to_note_in_g3_merged_with_h3_and_stays_valid()
    {
        var dir = Folder(null);
        Assert.True(EngineRunner.Export(dir, false, true).Success);
        using var xl = SpreadsheetDocument.Open(Path.Combine(dir, "steps.xlsx"), false);
        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(xl).Select(e => e.Description).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        var wb = xl.WorkbookPart!;
        var sheet = (WorksheetPart)wb.GetPartById(wb.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!);
        var ws = sheet.Worksheet!;
        var g3 = ws.Descendants<S.Cell>().Single(c => c.CellReference!.Value == "G3");
        Assert.Equal(ExportXlsx.ResultHint, g3.InnerText);
        Assert.Contains("Alt+Down", g3.InnerText);
        Assert.Contains(ws.Descendants<S.MergeCell>(), m => m.Reference!.Value == "G3:H3");
        // the Evidence column: last (I), header, empty bordered cells, its own how-to in I3, as wide as Picture
        Assert.Equal(ExportXlsx.EvidenceHint, ws.Descendants<S.Cell>().Single(c => c.CellReference!.Value == "I3").InnerText);
        Assert.Equal("Evidence", ws.Descendants<S.Cell>().Single(c => c.CellReference!.Value == "I6").InnerText);
        Assert.Equal("", ws.Descendants<S.Cell>().Single(c => c.CellReference!.Value == "I7").InnerText);
        Assert.Equal("Notes", ws.Descendants<S.Cell>().Single(c => c.CellReference!.Value == "H6").InnerText);
        var cols = ws.Descendants<S.Column>().ToList();
        Assert.Equal(9, cols.Count);
        Assert.Equal(cols[5].Width!.Value, cols[8].Width!.Value);
        Assert.Contains("Win+Shift+S", ExportXlsx.EvidenceHint);
        // what was there stays: the top block in B:F, the frozen header, the print titles and the drop-down's range
        Assert.Equal("Overall result", ws.Descendants<S.Cell>().Single(c => c.CellReference!.Value == "E3").InnerText);
        Assert.Equal("A7", ws.Descendants<S.Pane>().Single().TopLeftCell!.Value);
        Assert.Equal("G7:G7", ws.Descendants<S.DataValidation>().Single().SequenceOfReferences!.InnerText.Replace(" ", ""));
        Assert.Contains(wb.Workbook.Descendants<S.DefinedName>(), n => n.Name!.Value == "_xlnm.Print_Titles");
        Assert.True(ws.Descendants<S.Row>().Single(r => r.RowIndex!.Value == 3).Height!.Value >= 36);   // tall enough for the wrapped note
    }

    [Fact]
    public void Excel_of_an_old_recording_has_an_empty_expected()
    {
        var doc = StepsDoc.Load(Path.Combine(Folder(null), "steps.md"));
        Assert.Equal("", doc.Rows[0].Expected);
    }

    // ---- the end picture is the whole window in both modes --------------------------------------------------------------

    [Fact]
    public void An_end_picture_is_a_whole_window_request_that_smart_zoom_never_crops()
    {
        var cmd = ShotRules.For("cmd_end", 4, false, 1000)!;
        Assert.Equal(ShotTarget.Window, cmd.Target);
        Assert.True(ShotRules.IsEndPicture(cmd.Kind));
        Assert.False(ShotRules.IsCropped(cmd.Kind));
        var lisp = ShotRules.LispEnd(9, 1000);
        Assert.Equal(ShotTarget.Window, lisp.Target);
        Assert.True(ShotRules.IsEndPicture(lisp.Kind));
        Assert.False(ShotRules.IsCropped(lisp.Kind));
        Assert.Equal("009-lisp-end.png", ShotNaming.FileName(9, lisp.Kind));
        // the pictures smart zoom crops are the ribbon click's and a palette step's, and those never get the "opened" box
        Assert.True(ShotRules.IsCropped(ShotRules.For("ribbon", 1, false, 0)!.Kind));
        Assert.True(ShotRules.IsCropped(ShotRules.For("palette_click", 1, false, 0)!.Kind));
        Assert.False(ShotRules.IsEndPicture(ShotRules.For("ribbon", 1, false, 0)!.Kind));
        Assert.Null(ShotRules.For("lisp_end", 9, false, 0));   // a LISP's end has no picture unless the recorder asks for one
    }

    [Fact]
    public void A_lisp_end_picture_is_named_end_in_the_documents_and_shown_as_the_end_picture()
    {
        var dir = Path.Combine(_root, "lisp" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        File.WriteAllLines(Path.Combine(dir, "recording.jsonl"),
        [
            Header,
            """{"t":1000,"n":2,"type":"ribbon","id":"ID_Pal","text":"Pal","macro":"^C^C_MYPAL","itemType":"RibbonButton","tab":"Samples","panel":"Pal"}""",
            """{"t":1010,"n":3,"type":"lisp_start","firstLine":"(c:MYPAL)"}""",
            """{"t":1400,"n":4,"type":"lisp_end"}""",
            """{"t":1500,"n":5,"type":"shot","for":2,"file":"screenshots/002-ribbon.png","what":"window"}""",
            """{"t":1700,"n":6,"type":"shot","for":4,"file":"screenshots/004-lisp-end.png","what":"window","opened":{"palette":"Pal"}}""",
            """{"t":5000,"n":7,"type":"session_end"}""",
        ]);
        foreach (var f in new[] { "002-ribbon", "004-lisp-end" }) File.WriteAllBytes(Path.Combine(dir, "screenshots", f + ".png"), Png(1400, 800));
        Assert.True(EngineRunner.WriteSteps(Path.Combine(dir, "recording.jsonl")).Success);
        var md = File.ReadAllText(Path.Combine(dir, "steps.md"));
        Assert.Contains("-end.png", md);
        Assert.Contains("The **Pal** palette opens.", md);
    }

    // ---- which top-level command keeps the click's palettes (review item 1 and 2) -------------------------------------------

    static IReadOnlyList<PaletteState> Snap(string name = "Pick") => new[] { new PaletteState(1, name, "Set", false, null) };

    [Fact]
    public void A_nested_command_neither_takes_nor_drops_the_snapshot_of_the_command_that_started_it()
    {
        var t = new OpenedTracker();
        t.Click(Snap(), 1000);
        t.CommandStart("DRAWPICK", true, 0, 0, 1010);          // the ribbon-started command (top level)
        Assert.True(t.Keeping);
        t.CommandStart("ZOOM", false, 1, 0, 1100);             // a command it runs (ed.Command) is nested
        Assert.True(t.Keeping);
        Assert.Null(t.CommandEnd("ZOOM", 1, 0));                // its end is nested: nothing handed out, nothing dropped
        Assert.True(t.Keeping);
        Assert.NotNull(t.CommandEnd("DRAWPICK", 0, 0));         // the top-level end hands the snapshot over
        Assert.False(t.Keeping);
    }

    [Fact]
    public void A_cancelled_or_failed_top_level_command_drops_the_snapshot_a_nested_one_does_not()
    {
        var t = new OpenedTracker();
        t.Click(Snap(), 1000); t.CommandStart("DRAWPICK", true, 0, 0, 1010);
        t.CommandAborted("ZOOM", 1, 0);                         // nested
        Assert.True(t.Keeping);
        t.CommandAborted("DRAWPICK", 0, 0);                     // the command itself
        Assert.False(t.Keeping);
        Assert.Null(t.CommandEnd("DRAWPICK", 0, 0));            // nothing stale left for a later end
    }

    [Fact]
    public void A_later_top_level_command_that_the_click_did_not_start_drops_the_snapshot_and_a_stale_click_is_not_used()
    {
        var t = new OpenedTracker();
        t.Click(Snap(), 1000); t.CommandStart("DRAWPICK", true, 0, 0, 1010);
        t.CommandStart("LINE", false, 0, 0, 5000);              // typed: top level, not the click's
        Assert.False(t.Keeping);
        t.Click(Snap(), 10_000);
        t.CommandStart("DRAWPICK", true, 0, 0, 10_000 + OpenedTracker.FreshMs + 1);   // started too long after the click
        Assert.False(t.Keeping);
    }

    [Fact]
    public void A_lisp_defined_command_keeps_the_snapshot_and_the_commands_it_runs_do_not_take_it()
    {
        var t = new OpenedTracker();
        t.Click(Snap(), 1000);
        t.LispStart(0, 0, 1010);                                 // (c:MYPAL)
        Assert.True(t.Keeping);
        t.CommandStart("PALETTE", false, 0, 1, 1100);            // a command the LISP runs: cmd depth 0, LISP depth 1: not top level
        Assert.True(t.Keeping);
        Assert.Null(t.CommandEnd("PALETTE", 0, 1));
        Assert.True(t.Keeping);
        Assert.NotNull(t.LispEnd(0, 0));
        Assert.False(t.Keeping);
    }

    [Fact]
    public void A_lisp_that_starts_without_a_fresh_click_keeps_nothing()
    {
        var t = new OpenedTracker();
        t.LispStart(0, 0, 1000);
        Assert.False(t.Keeping);
        t.Click(Snap(), 1000);
        t.LispStart(0, 0, 1000 + OpenedTracker.FreshMs + 1);
        Assert.False(t.Keeping);
    }

    [Theory]
    [InlineData(true, true, false, true)]     // the overflow host draws the button: the toolbar's list holds the item
    [InlineData(true, false, true, false)]    // host and the list does not hold it: no, whatever the texts say
    [InlineData(false, false, true, true)]    // the button's own item is the executed one
    [InlineData(false, true, false, false)]   // another item's button was pressed: no, even if the list holds the item
    public void A_toolbar_click_needs_the_button_or_the_toolbar_list_to_show_the_executed_item(bool host, bool holds, bool same, bool expected) =>
        Assert.Equal(expected, QatMatch.Accept(host, holds, same));

    // ---- which toolbar button is the executed item (review item 3) -----------------------------------------------------------

    [Theory]
    [InlineData("ID_A", null, "A", "ID_A", null, "A", true)]
    [InlineData("ID_A", null, "A", "ID_B", null, "A", false)]    // both have ids: they decide, even with the same text
    [InlineData(null, "^C^C_x", "X", "ID_A", "^C^C_x", "Y", true)]
    [InlineData("", null, "Pick", "ID_A", null, "Pick", true)]   // the copy has no id: the text
    [InlineData("", null, "Pick", "ID_A", null, "Other", false)] // a button pressed on the toolbar, another item run by a keytip
    [InlineData("", null, null, "ID_A", null, null, false)]
    public void The_toolbar_button_must_be_the_executed_item(string? cid, string? cmac, string? ctxt, string? iid, string? imac, string? itxt, bool expected) =>
        Assert.Equal(expected, QatMatch.Same(cid, cmac, ctxt, iid, imac, itxt));

    // ---- Quick Access Toolbar -------------------------------------------------------------------------------------------

    [Fact]
    public void A_quick_access_toolbar_click_says_so_without_tab_or_panel()
    {
        var md = File.ReadAllText(Path.Combine(Folder(null, "qat"), "steps.md"));
        Assert.Contains("1. Click **Pick**\n\n   *Quick Access Toolbar*\n", md);
        Assert.DoesNotContain("Ribbon ›", md);
        Assert.DoesNotContain("Samples tab", md);
    }

    [Fact]
    public void A_ribbon_click_and_an_old_recording_keep_the_ribbon_line()
    {
        Assert.Contains("   *Ribbon › Samples tab › Pick panel*", File.ReadAllText(Path.Combine(Folder(null, null), "steps.md")));
        Assert.Contains("   *Ribbon › Samples tab › Pick panel*", File.ReadAllText(Path.Combine(Folder(null, "ribbon"), "steps.md")));   // any other value is the ribbon
    }

    [Fact]
    public void The_word_and_excel_documents_show_the_quick_access_toolbar_line()
    {
        var dir = Folder(null, "qat");
        Assert.True(EngineRunner.Export(dir, true, true).Success);
        using var w = WordprocessingDocument.Open(Path.Combine(dir, "steps.docx"), false);
        Assert.Contains(w.MainDocumentPart!.Document!.Body!.Descendants<W.Paragraph>(), p => p.InnerText == "Quick Access Toolbar");
        Assert.DoesNotContain("Ribbon ›", w.MainDocumentPart.Document.Body.InnerText);
        Assert.Equal("Quick Access Toolbar", StepsDoc.Load(Path.Combine(dir, "steps.md")).Rows[0].Where);
    }
}
