// "steps.md" -> "steps.xlsx": a handoff sheet "Steps" a tester signs off (one row per thing to do, the picture of each row inside the file, a Result drop-down) and a sheet "Info".
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace Retrace.Engine.Recordings;

public static class ExportXlsx
{
    public static readonly string[] Headers = ["#", "Action", "Where", "Details", "Expected", "Picture", "Result", "Notes", "Evidence"];
    // Evidence (last, I): empty cells for the tester's own screenshots, as wide as Picture (62) so a pasted picture is readable; Retrace writes nothing into it
    static readonly double[] Widths = [6, 34, 30, 44, 30, 62, 12, 28, 62];

    /// <summary>The values of the Result drop-down; a row starts as "Not run".</summary>
    public static readonly string[] Results = ["Pass", "Fail", "Blocked", "Not run"];

    // cell format ids of the stylesheet below
    const uint Plain = 0, Head = 1, Wrap = 2, Link = 3, Bold = 4, Cell = 5, CellBold = 6, PicLink = 7, Title = 8, Label = 9, Input = 10, Centered = 11, Hint = 12;

    const int HeaderRow = 6;                 // rows 1-4: the top block, 5: a thin spacer, 6: the table's header (frozen and repeated when printed)
    const int PictureCol = 5, ResultCol = 6; // zero-based: F, G
    const int ThumbWidthPx = 420, ThumbMaxHeightPx = 380;
    const long EmuPerPx = 9525;

    /// <summary>The how-to note in G3 (merged with H3, above Result and Notes): Excel shows a drop-down's arrow only at the corner of the selected cell, which is hard to hit in a tall picture row. Always true: typing the word
    /// works in every Excel (the list is a validation list, so only these four words are accepted); Alt+Down opens the list.</summary>
    public const string ResultHint = "Result: select the cell, then press Alt+Down and pick (Pass, Fail, Blocked, Not run), or type the word.";

    /// <summary>The how-to note in I3 (above the Evidence column), same secondary style as <see cref="ResultHint"/>.</summary>
    public const string EvidenceHint = "Evidence: press Win+Shift+S, drag over AutoCAD, click the cell in this column and press Ctrl+V.";

    /// <summary>Writes the workbook; returns the number of step rows (steps, not sub-steps).</summary>
    public static int Write(StepsDoc doc, string folder, string path, Func<string, int, (byte[] Png, int Width, int Height)?>? rescale = null)
    {
        using var pkg = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wb = pkg.AddWorkbookPart();
        wb.Workbook = new Workbook();
        var styles = wb.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = Stylesheet();
        styles.Stylesheet.Save();

        var sheets = wb.Workbook.AppendChild(new Sheets());
        AddSheet(wb, sheets, 1, "Steps", StepsSheet(wb, doc, rescale ?? Rescale));
        AddSheet(wb, sheets, 2, "Info", InfoSheet(wb, doc, folder));
        wb.Workbook.AppendChild(new DefinedNames(new DefinedName($"Steps!${HeaderRow}:${HeaderRow}") { Name = "_xlnm.Print_Titles", LocalSheetId = 0U }));   // the header row repeats on every printed page
        wb.Workbook.AppendChild(new CalculationProperties { FullCalculationOnLoad = true });
        wb.Workbook.Save();
        return doc.Steps.Count;
    }

    static void AddSheet(WorkbookPart wb, Sheets sheets, uint id, string name, WorksheetPart part) =>
        sheets.Append(new Sheet { Id = wb.GetIdOfPart(part), SheetId = id, Name = name });

    static Stylesheet Stylesheet()
    {
        CellFormat Fmt(uint font, uint fill, uint border, Alignment al) =>
            new(al) { NumberFormatId = 0, FontId = font, FillId = fill, BorderId = border, FormatId = 0, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true };
        Alignment Top(bool center = false) => new() { Vertical = VerticalAlignmentValues.Top, WrapText = true, Horizontal = center ? HorizontalAlignmentValues.Center : HorizontalAlignmentValues.Left };
        DifferentialFormat Dxf(string rgb) => new(new Fill(new PatternFill(new BackgroundColor { Rgb = rgb }) { PatternType = PatternValues.Solid }));
        return new Stylesheet(
            new Fonts(
                new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
                new Font(new Underline(), new FontSize { Val = 11 }, new Color { Rgb = "FF0563C1" }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 16 }, new FontName { Val = "Calibri" }),
                new Font(new FontSize { Val = 9 }, new Color { Rgb = "FF6B6B6B" }, new FontName { Val = "Calibri" })),
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFD9E2F3" }, new BackgroundColor { Indexed = 64 }) { PatternType = PatternValues.Solid }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFF2F2F2" }, new BackgroundColor { Indexed = 64 }) { PatternType = PatternValues.Solid })),
            new Borders(
                new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()),
                new Border(
                    new LeftBorder(new Color { Rgb = "FFBFBFBF" }) { Style = BorderStyleValues.Thin },
                    new RightBorder(new Color { Rgb = "FFBFBFBF" }) { Style = BorderStyleValues.Thin },
                    new TopBorder(new Color { Rgb = "FFBFBFBF" }) { Style = BorderStyleValues.Thin },
                    new BottomBorder(new Color { Rgb = "FFBFBFBF" }) { Style = BorderStyleValues.Thin },
                    new DiagonalBorder())),
            new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }),
            new CellFormats(
                new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0 },                  // 0 Plain
                Fmt(1, 2, 1, Top()),                                                                                         // 1 Head
                new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = true }) { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0, ApplyAlignment = true },   // 2 Wrap
                new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = true }) { NumberFormatId = 0, FontId = 2, FillId = 0, BorderId = 0, FormatId = 0, ApplyFont = true, ApplyAlignment = true },   // 3 Link
                new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = true }) { NumberFormatId = 0, FontId = 1, FillId = 0, BorderId = 0, FormatId = 0, ApplyFont = true, ApplyAlignment = true },   // 4 Bold
                Fmt(0, 0, 1, Top()),                                                                                         // 5 Cell
                Fmt(1, 0, 1, Top()),                                                                                         // 6 CellBold
                Fmt(2, 0, 1, new Alignment { Vertical = VerticalAlignmentValues.Bottom, WrapText = true }),                  // 7 PicLink: the file name sits under the picture
                new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Center, WrapText = true }) { NumberFormatId = 0, FontId = 3, FillId = 0, BorderId = 0, FormatId = 0, ApplyFont = true, ApplyAlignment = true },   // 8 Title
                Fmt(1, 3, 1, Top()),                                                                                         // 9 Label
                Fmt(0, 0, 1, Top()),                                                                                         // 10 Input (a blank bordered cell)
                Fmt(0, 0, 1, Top(center: true)),                                                                             // 11 Centered
                new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = true }) { NumberFormatId = 0, FontId = 4, FillId = 0, BorderId = 0, FormatId = 0, ApplyFont = true, ApplyAlignment = true }),   // 12 Hint: small grey wrapped note
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }),
            new DifferentialFormats(Dxf("FFC6EFCE"), Dxf("FFFFC7CE"), Dxf("FFFFEB9C")) { Count = 3U });   // Pass green, Fail red, Blocked amber
    }

    static string Ref(int col, int row) => $"{(char)('A' + col)}{row}";

    static Cell Str(int col, int row, string text, uint style) => new()
    {
        CellReference = Ref(col, row),
        DataType = CellValues.InlineString,
        StyleIndex = style,
        InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }),
    };

    static Cell Formula(int col, int row, string formula, string cached, uint style) => new()
    {
        CellReference = Ref(col, row),
        DataType = CellValues.String,
        StyleIndex = style,
        CellFormula = new CellFormula(formula),
        CellValue = new CellValue(cached),
    };

    static string FileLabel(string url) => Path.GetFileName(Uri.UnescapeDataString(url).Replace('/', Path.DirectorySeparatorChar));

    static WorksheetPart StepsSheet(WorkbookPart wb, StepsDoc doc, Func<string, int, (byte[] Png, int Width, int Height)?>? rescale)
    {
        var part = wb.AddNewPart<WorksheetPart>();
        var rows = doc.Rows;
        int first = HeaderRow + 1, last = HeaderRow + Math.Max(1, rows.Count);
        string range = $"{(char)('A' + ResultCol)}{first}:{(char)('A' + ResultCol)}{last}";
        var data = new SheetData();

        // top block: test, recorded, starts with; the sign-off area beside it (Tester, Date, Overall result) and the counts
        Row Top(int r, double height = 0)
        {
            var row = new Row { RowIndex = (uint)r };
            if (height > 0) { row.Height = height; row.CustomHeight = true; }
            return row;
        }
        var r1 = Top(1, 26); r1.Append(Str(1, 1, "Test", Label), Str(2, 1, doc.Name, Title), Str(3, 1, "", Plain), Str(4, 1, "Tester", Label), Str(5, 1, "", Input));
        var r2 = Top(2); r2.Append(Str(1, 2, "Recorded", Label), Str(2, 2, doc.Meta.TrimEnd('.').StartsWith("Recorded ", StringComparison.Ordinal) ? doc.Meta.TrimEnd('.')["Recorded ".Length..] : doc.Meta.TrimEnd('.'), Wrap), Str(3, 2, "", Plain), Str(4, 2, "Date", Label), Str(5, 2, "", Input));
        var r3 = Top(3, 48); r3.Append(Str(1, 3, "Starts with", Label), Str(2, 3, doc.StartWith.TrimEnd('.'), Wrap), Str(3, 3, "", Plain), Str(4, 3, "Overall result", Label),
            rows.Count == 0 ? Str(5, 3, "In progress", CellBold)
            : Formula(5, 3, $"IF(COUNTIF({range},\"Fail\")>0,\"Fail\",IF(COUNTIF({range},\"Pass\")=ROWS({range}),\"Pass\",\"In progress\"))", "In progress", CellBold),
            Str(ResultCol, 3, ResultHint, Hint), Str(ResultCol + 1, 3, "", Hint),   // G3:H3, merged below
            Str(ResultCol + 2, 3, EvidenceHint, Hint));
        var r4 = Top(4); r4.Append(Str(1, 4, "Counts", Label), rows.Count == 0 ? Str(2, 4, "Pass: 0   Fail: 0   Blocked: 0   Not run: 0", Wrap) : Formula(2, 4,
            $"\"Pass: \"&COUNTIF({range},\"Pass\")&\"   Fail: \"&COUNTIF({range},\"Fail\")&\"   Blocked: \"&COUNTIF({range},\"Blocked\")&\"   Not run: \"&(COUNTIF({range},\"Not run\")+COUNTBLANK({range}))",
            $"Pass: 0   Fail: 0   Blocked: 0   Not run: {rows.Count}", Wrap), Str(3, 4, "", Plain));
        var r5 = Top(5, 6);
        data.Append(r1, r2, r3, r4, r5);

        var head = new Row { RowIndex = (uint)HeaderRow };
        for (int c = 0; c < Headers.Length; c++) head.Append(Str(c, HeaderRow, Headers[c], Head));
        data.Append(head);

        var links = new Hyperlinks();
        var drawingPart = part.AddNewPart<DrawingsPart>();
        drawingPart.WorksheetDrawing = new Xdr.WorksheetDrawing();
        uint pictureId = 1;
        int r = HeaderRow;
        foreach (var s in rows)
        {
            r++;
            double height = TextHeight(s);   // what the text needs; a picture makes it taller
            var row = new Row { RowIndex = (uint)r };
            if (int.TryParse(s.Label, out var whole)) row.Append(new Cell { CellReference = Ref(0, r), DataType = CellValues.Number, StyleIndex = Centered, CellValue = new CellValue(whole.ToString()) });   // a step number is a number (no "number stored as text" mark)
            else row.Append(Str(0, r, s.Label, Centered));   // 1.1 stays text
            row.Append(Str(1, r, s.Action, CellBold));
            row.Append(Str(2, r, s.Where, Cell));
            row.Append(Str(3, r, s.Details, Cell));
            row.Append(Str(4, r, s.Expected, Cell));
            if (s.Picture is { } url)
            {
                row.Append(Str(PictureCol, r, FileLabel(url), PicLink));
                var rel = part.AddHyperlinkRelationship(new Uri(url, UriKind.Relative), true);
                links.Append(new Hyperlink { Reference = Ref(PictureCol, r), Id = rel.Id });
                var file = SafeResolve(doc, url);
                if (file != null && File.Exists(file) && ExportDocx.PngSize(file) is { } size)
                {
                    var (cx, cy) = Thumb(size.W, size.H);
                    AddPicture(drawingPart, file, size.W, r, cx, cy, pictureId++, s.Action, rescale);
                    height = Math.Max(height, cy / 12700.0 + 22);   // the picture, and a line for the file name under it
                }
            }
            else row.Append(Str(PictureCol, r, "", Cell));
            row.Append(Str(ResultCol, r, "Not run", Centered));
            row.Append(Str(ResultCol + 1, r, "", Input));
            row.Append(Str(ResultCol + 2, r, "", Input));   // Evidence: the tester pastes their own pictures here
            row.Height = Math.Min(409, height); row.CustomHeight = true;   // always set: text and picture both decide, never auto-fit
            data.Append(row);
        }

        var ws = new Worksheet();
        ws.Append(new SheetProperties(new PageSetupProperties { FitToPage = true }));
        ws.Append(new SheetViews(new SheetView(
            new Pane { VerticalSplit = (double)HeaderRow, TopLeftCell = $"A{HeaderRow + 1}", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen },
            new Selection { Pane = PaneValues.BottomLeft, ActiveCell = $"A{HeaderRow + 1}", SequenceOfReferences = new ListValue<StringValue> { InnerText = $"A{HeaderRow + 1}" } })
        { WorkbookViewId = 0U, TabSelected = true, ShowGridLines = false }));
        ws.Append(new SheetFormatProperties { DefaultRowHeight = 15D });
        ws.Append(Columns());
        ws.Append(data);
        ws.Append(new MergeCells(new MergeCell { Reference = "C1:D1" }, new MergeCell { Reference = "C2:D2" }, new MergeCell { Reference = "C3:D3" }, new MergeCell { Reference = "C4:D4" }, new MergeCell { Reference = "G3:H3" }) { Count = 5U });
        ws.Append(new ConditionalFormatting(
            new ConditionalFormattingRule(new Formula("\"Pass\"")) { Type = ConditionalFormatValues.CellIs, Operator = ConditionalFormattingOperatorValues.Equal, Priority = 1, FormatId = 0U },
            new ConditionalFormattingRule(new Formula("\"Fail\"")) { Type = ConditionalFormatValues.CellIs, Operator = ConditionalFormattingOperatorValues.Equal, Priority = 2, FormatId = 1U },
            new ConditionalFormattingRule(new Formula("\"Blocked\"")) { Type = ConditionalFormatValues.CellIs, Operator = ConditionalFormattingOperatorValues.Equal, Priority = 3, FormatId = 2U })
        { SequenceOfReferences = new ListValue<StringValue> { InnerText = range } });
        ws.Append(new ConditionalFormatting(
            new ConditionalFormattingRule(new Formula("\"Pass\"")) { Type = ConditionalFormatValues.CellIs, Operator = ConditionalFormattingOperatorValues.Equal, Priority = 4, FormatId = 0U },
            new ConditionalFormattingRule(new Formula("\"Fail\"")) { Type = ConditionalFormatValues.CellIs, Operator = ConditionalFormattingOperatorValues.Equal, Priority = 5, FormatId = 1U })
        { SequenceOfReferences = new ListValue<StringValue> { InnerText = "F3" } });
        ws.Append(new DataValidations(new DataValidation(new Formula1("\"" + string.Join(",", Results) + "\""))
        {
            Type = DataValidationValues.List, AllowBlank = true, ShowErrorMessage = true, ShowInputMessage = true,
            ErrorTitle = "Result", Error = "Pick Pass, Fail, Blocked or Not run.",
            SequenceOfReferences = new ListValue<StringValue> { InnerText = range },
        }) { Count = 1U });
        if (links.HasChildren) ws.Append(links);
        ws.Append(new PageMargins { Left = 0.4, Right = 0.4, Top = 0.5, Bottom = 0.5, Header = 0.3, Footer = 0.3 });
        ws.Append(new PageSetup { PaperSize = 9U, Orientation = OrientationValues.Landscape, FitToWidth = 1U, FitToHeight = 0U });   // landscape, one page wide
        bool hasPictures = pictureId > 1;
        if (hasPictures) ws.Append(new Drawing { Id = part.GetIdOfPart(drawingPart) });
        else part.DeletePart(drawingPart);
        part.Worksheet = ws;
        part.Worksheet.Save();
        if (hasPictures) drawingPart.WorksheetDrawing!.Save();
        return part;
    }

    /// <summary>The row height in points the text needs: the most lines of Action, Where, Details and Expected (a line wraps at about the column's width in characters), 15 pt each plus 6.</summary>
    public static double TextHeight(SheetRow s)
    {
        int Lines(string text, double widthChars) =>
            string.IsNullOrEmpty(text) ? 1 : text.Split('\n').Sum(l => Math.Max(1, (int)Math.Ceiling(l.Length / Math.Max(1.0, widthChars - 3))));
        int lines = new[] { Lines(s.Action, Widths[1] * 0.9), Lines(s.Where, Widths[2]), Lines(s.Details, Widths[3]), Lines(s.Expected, Widths[4]) }.Max();
        return lines * 15 + 6;
    }

    /// <summary>Optional: makes a smaller copy of a picture for the sheet. Given the file and the widest width wanted, returns the PNG bytes and their size, or null to embed the file as it is.
    /// The engine has no image codec; the plugin sets this at start-up with WPF's (<c>AutoCad/ImageScale</c>). Null in the tests and anywhere WPF is not there.</summary>
    public static Func<string, int, (byte[] Png, int Width, int Height)?>? Rescale { get; set; }

    /// <summary>The widest a picture is stored in the sheet; a wider one is made smaller by <see cref="Rescale"/> when that makes it smaller on disk too.</summary>
    public const int MaxStoredWidth = 1280;

    static string? SafeResolve(StepsDoc doc, string url)
    {
        try { return doc.ResolvePath(url); }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The thumbnail size in EMU: 420 px wide (never enlarged), at most 380 px high, the aspect ratio kept.</summary>
    public static (long Cx, long Cy) Thumb(int w, int h)
    {
        double k = Math.Min(1.0, Math.Min(ThumbWidthPx / (double)w, ThumbMaxHeightPx / (double)h));
        return (Math.Max(1, (long)(w * k * EmuPerPx)), Math.Max(1, (long)(h * k * EmuPerPx)));
    }

    /// <summary>The picture inside the file (xl/media), anchored at the top of the row's Picture cell.</summary>
    static void AddPicture(DrawingsPart dp, string file, int pixelWidth, int row, long cx, long cy, uint id, string alt, Func<string, int, (byte[] Png, int Width, int Height)?>? rescale)
    {
        var img = dp.AddImagePart(ImagePartType.Png);
        (byte[] Png, int Width, int Height)? small = null;
        if (pixelWidth > MaxStoredWidth && rescale != null)
        {
            try { small = rescale(file, MaxStoredWidth); } catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException or UnauthorizedAccessException) { small = null; }
            if (small != null && small.Value.Png.Length >= new FileInfo(file).Length) small = null;   // never store a bigger copy
        }
        if (small != null) { using var ms = new MemoryStream(small.Value.Png); img.FeedData(ms); }
        else using (var fs = File.OpenRead(file)) img.FeedData(fs);
        var rid = dp.GetIdOfPart(img);
        dp.WorksheetDrawing!.Append(new Xdr.OneCellAnchor(
            new Xdr.FromMarker(new Xdr.ColumnId(PictureCol.ToString(CultureInfo.InvariantCulture)), new Xdr.ColumnOffset("38100"), new Xdr.RowId((row - 1).ToString(CultureInfo.InvariantCulture)), new Xdr.RowOffset("38100")),
            new Xdr.Extent { Cx = cx, Cy = cy },
            new Xdr.Picture(
                new Xdr.NonVisualPictureProperties(
                    new Xdr.NonVisualDrawingProperties { Id = id + 1U, Name = $"Picture {id}", Description = alt },
                    new Xdr.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true })),
                new Xdr.BlipFill(new A.Blip { Embed = rid }, new A.Stretch(new A.FillRectangle())),
                new Xdr.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })),
            new Xdr.ClientData()));
    }

    static Columns Columns()
    {
        var cols = new Columns();
        for (int i = 0; i < Widths.Length; i++) cols.Append(new Column { Min = (uint)(i + 1), Max = (uint)(i + 1), Width = Widths[i], CustomWidth = true });
        return cols;
    }

    static WorksheetPart InfoSheet(WorkbookPart wb, StepsDoc doc, string folder)
    {
        var part = wb.AddNewPart<WorksheetPart>();
        var rows = new (string Label, string Value)[]
        {
            ("Test name", doc.Name),
            ("Recorded", doc.Meta.TrimEnd('.')),
            ("Start with", doc.StartWith.TrimEnd('.')),
            ("Steps", doc.Steps.Count.ToString()),
            ("Test folder", folder),
        };
        var data = new SheetData();
        for (int i = 0; i < rows.Length; i++)
        {
            var row = new Row { RowIndex = (uint)(i + 1) };
            row.Append(Str(0, i + 1, rows[i].Label, Bold));
            row.Append(Str(1, i + 1, rows[i].Value, Wrap));
            data.Append(row);
        }
        var ws = new Worksheet(new SheetFormatProperties { DefaultRowHeight = 15D },
            new Columns(new Column { Min = 1, Max = 1, Width = 16, CustomWidth = true }, new Column { Min = 2, Max = 2, Width = 100, CustomWidth = true }),
            data);
        part.Worksheet = ws;
        part.Worksheet.Save();
        return part;
    }
}
