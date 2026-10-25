// "steps.md" -> "steps.xlsx": a sheet "Steps" to tick off by hand and a sheet "Info".
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Retrace.Engine.Recordings;

public static class ExportXlsx
{
    public static readonly string[] Headers = ["Step", "Action", "Details", "Check", "Screenshot", "Result", "Notes"];
    static readonly double[] Widths = [7, 48, 60, 40, 28, 12, 30];

    // cell format ids of the stylesheet below
    const uint Plain = 0, Head = 1, Wrap = 2, Link = 3, Bold = 4;

    /// <summary>Writes the workbook; returns the number of step rows.</summary>
    public static int Write(StepsDoc doc, string folder, string path)
    {
        using var pkg = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wb = pkg.AddWorkbookPart();
        wb.Workbook = new Workbook();
        var styles = wb.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = Stylesheet();
        styles.Stylesheet.Save();

        var sheets = wb.Workbook.AppendChild(new Sheets());
        AddSheet(wb, sheets, 1, "Steps", StepsSheet(wb, doc));
        AddSheet(wb, sheets, 2, "Info", InfoSheet(wb, doc, folder));
        wb.Workbook.Save();
        return doc.Steps.Count;
    }

    static void AddSheet(WorkbookPart wb, Sheets sheets, uint id, string name, WorksheetPart part) =>
        sheets.Append(new Sheet { Id = wb.GetIdOfPart(part), SheetId = id, Name = name });

    static Stylesheet Stylesheet() => new(
        new Fonts(
            new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Underline(), new FontSize { Val = 11 }, new Color { Rgb = "FF0563C1" }, new FontName { Val = "Calibri" })),
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFE7E6E6" }, new BackgroundColor { Indexed = 64 }) { PatternType = PatternValues.Solid })),
        new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())),
        new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }),
        new CellFormats(
            new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0 },
            new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = true }) { NumberFormatId = 0, FontId = 1, FillId = 2, BorderId = 0, FormatId = 0, ApplyFont = true, ApplyFill = true, ApplyAlignment = true },
            new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = true }) { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0, ApplyAlignment = true },
            new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = true }) { NumberFormatId = 0, FontId = 2, FillId = 0, BorderId = 0, FormatId = 0, ApplyFont = true, ApplyAlignment = true },
            new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = true }) { NumberFormatId = 0, FontId = 1, FillId = 0, BorderId = 0, FormatId = 0, ApplyFont = true, ApplyAlignment = true }),
        new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }));

    static string Ref(int col, int row) => $"{(char)('A' + col)}{row}";

    static Cell Str(int col, int row, string text, uint style) => new()
    {
        CellReference = Ref(col, row),
        DataType = CellValues.InlineString,
        StyleIndex = style,
        InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }),
    };

    static WorksheetPart StepsSheet(WorkbookPart wb, StepsDoc doc)
    {
        var part = wb.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        var head = new Row { RowIndex = 1 };
        for (int c = 0; c < Headers.Length; c++) head.Append(Str(c, 1, Headers[c], Head));
        data.Append(head);

        var links = new Hyperlinks();
        int r = 1;
        foreach (var s in doc.Steps)
        {
            r++;
            var row = new Row { RowIndex = (uint)r };
            row.Append(new Cell { CellReference = Ref(0, r), DataType = CellValues.Number, StyleIndex = Wrap, CellValue = new CellValue(s.Number.ToString()) });
            row.Append(Str(1, r, s.Action, Bold));
            row.Append(Str(2, r, s.Details, Wrap));
            row.Append(Str(3, r, s.Check, Wrap));
            if (s.Picture != null)
            {
                var rel = part.AddHyperlinkRelationship(new Uri(s.Picture, UriKind.Relative), true);
                row.Append(Str(4, r, Uri.UnescapeDataString(s.Picture), Link));
                links.Append(new Hyperlink { Reference = Ref(4, r), Id = rel.Id });
            }
            else row.Append(Str(4, r, "", Wrap));
            row.Append(new Cell { CellReference = Ref(5, r), StyleIndex = Wrap });
            row.Append(new Cell { CellReference = Ref(6, r), StyleIndex = Wrap });
            data.Append(row);
        }

        var ws = new Worksheet();
        ws.Append(new SheetViews(new SheetView(
            new Pane { VerticalSplit = 1D, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen },
            new Selection { Pane = PaneValues.BottomLeft, ActiveCell = "A2", SequenceOfReferences = new ListValue<StringValue> { InnerText = "A2" } })
        { WorkbookViewId = 0U, TabSelected = true }));
        ws.Append(new SheetFormatProperties { DefaultRowHeight = 15D });
        ws.Append(Columns());
        ws.Append(data);
        if (doc.Steps.Count > 0)
            ws.Append(new DataValidations(new DataValidation(new Formula1("\"PASS,FAIL\""))
            {
                Type = DataValidationValues.List, AllowBlank = true, ShowErrorMessage = true, ShowInputMessage = true,
                ErrorTitle = "Result", Error = "Pick PASS or FAIL.",
                SequenceOfReferences = new ListValue<StringValue> { InnerText = $"F2:F{doc.Steps.Count + 1}" },
            }) { Count = 1U });
        if (links.HasChildren) ws.Append(links);
        part.Worksheet = ws;
        part.Worksheet.Save();
        return part;
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
