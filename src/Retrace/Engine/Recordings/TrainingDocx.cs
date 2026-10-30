// A training document (.docx) from a TrainingManifest (DocumentFormat.OpenXml, MIT). Styles, page and picture code are shared with ExportDocx.
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Table = DocumentFormat.OpenXml.Wordprocessing.Table;
using TableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using TableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;

namespace Retrace.Engine.Recordings;

public static class TrainingDocx
{
    const string Green = "22B14C", PassFill = "C6EFCE", FailFill = "FFC7CE", GreyFill = "E7E6E6";

    /// <summary>Writes the .docx the way <see cref="EngineRunner.WriteTrainingDoc"/> does (<see cref="Exporter.WriteAtomic"/>: a temp file moved
    /// over the target, `... (new).docx` when the target is open in Word). Returns how many pictures were embedded. A picture file that is missing
    /// (or not a PNG) becomes a short italic note.</summary>
    public static int Write(TrainingManifest m, string manifestFolder, string docxPath)
    {
        var target = Path.GetFullPath(docxPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
        (int Embedded, int Missing) counts = default;
        Exporter.WriteAtomic(target, t => counts = Build(m, manifestFolder, t), out _);
        return counts.Embedded;
    }

    /// <summary>Writes the .docx to <paramref name="path"/> itself; returns the pictures embedded and the picture files missing.</summary>
    internal static (int Embedded, int Missing) Build(TrainingManifest m, string manifestFolder, string path)
    {
        using var pkg = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = pkg.AddMainDocumentPart();
        ExportDocx.AddStyles(main,
            ExportDocx.ParaStyle("Title", "Title", "Normal", "52", true, "0", "120"),
            ExportDocx.ParaStyle("ContentsHeading", "Contents Heading", "Normal", "28", true, "240", "120"));
        var body = new Body();
        main.Document = new Document(body);
        var w = new Writer(main, manifestFolder);
        w.All(m, body);
        body.Append(ExportDocx.PageSection());
        main.Document.Save();

        // Word fills the table of contents when it opens the file
        var settings = main.AddNewPart<DocumentSettingsPart>();
        settings.Settings = new Settings(new UpdateFieldsOnOpen { Val = true });
        settings.Settings.Save();
        return (w.Embedded, w.Missing);
    }

    sealed class Writer(MainDocumentPart main, string folder)
    {
        public int Embedded, Missing;
        uint _drawingId = 1;

        public void All(TrainingManifest m, Body body)
        {
            body.Append(Para("Title", m.Title.Length > 0 ? m.Title : "Training document"));
            var date = DateLine(m);
            if (date.Length > 0) body.Append(Para(null, date, italic: true));

            body.Append(Para("ContentsHeading", "Contents"));
            body.Append(Toc());

            bool firstTest = true;
            foreach (var t in m.Tests)
            {
                body.Append(Para("Heading1", t.Name.Length > 0 ? t.Name : "Test", pageBreakBefore: !firstTest));
                firstTest = false;
                Summary(t, body);
                foreach (var s in t.Steps) Step(s, body);
            }
        }

        static string DateLine(TrainingManifest m)
        {
            var parts = new List<string>();
            if (DateTime.TryParse(m.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                parts.Add("Created " + d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
            else if (m.CreatedUtc.Length > 0) parts.Add("Created " + m.CreatedUtc);
            if (m.RetraceVersion.Length > 0) parts.Add("made with Retrace " + m.RetraceVersion);
            return string.Join(", ", parts);
        }

        /// <summary>A TOC field of Heading 1-2; the text inside is what shows until Word updates the field.</summary>
        static Paragraph Toc() => new(
            new ParagraphProperties(new SpacingBetweenLines { After = "120" }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" TOC \\o \"1-2\" \\h \\z \\u ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(new RunProperties(new Italic()), ExportDocx.TextOf("Right-click here and choose Update field to fill in the contents.")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End }));

        void Summary(TrainingTest t, Body body)
        {
            int checkpoints = t.Steps.Count(s => s.Checkpoint != null);
            var line = $"{t.Steps.Count} {(t.Steps.Count == 1 ? "step" : "steps")}, {checkpoints} {(checkpoints == 1 ? "checkpoint" : "checkpoints")}";
            if (!string.IsNullOrWhiteSpace(t.Verdict)) line += ", last result: " + t.Verdict;
            body.Append(Para(null, line, italic: true));
            if (!string.IsNullOrWhiteSpace(t.Message)) body.Append(Para(null, t.Message!, italic: true));
        }

        void Step(TrainingStep s, Body body)
        {
            body.Append(Para("Heading2", $"Step {s.Number}: {s.Title}"));
            foreach (var i in s.Instructions) body.Append(Para(null, i));
            if (s.Checkpoint is { } c)
            {
                body.Append(Para("Heading3", $"Checkpoint {c.Index}: {c.Note}"));
                foreach (var p in s.Pictures) Picture(p, s, body);
                Result(c, body);
            }
            else foreach (var p in s.Pictures) Picture(p, s, body);
        }

        void Picture(TrainingPicture p, TrainingStep s, Body body)
        {
            var alt = !string.IsNullOrWhiteSpace(p.Caption) ? p.Caption! : $"Step {s.Number} picture";
            var file = Resolve(p.File);
            var size = file != null && File.Exists(file) ? ExportDocx.PngSize(file) : null;
            if (size == null)
            {
                Missing++;
                body.Append(Para(null, $"[picture not found: {p.File}]", italic: true));
            }
            else
            {
                Embedded++;
                var para = new Paragraph(new ParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "60", After = "60" }));
                para.Append(ExportDocx.PictureRun(main, file!, size.Value.W, size.Value.H, _drawingId++, alt));
                body.Append(para);
            }
            if (!string.IsNullOrWhiteSpace(p.Caption)) body.Append(Para(null, p.Caption!, italic: true, keepNext: p.Callouts.Count > 0));
            if (p.Callouts.Count > 0) Legend(p.Callouts, body);
        }

        /// <summary>The picture file under the manifest folder, or null when the name is empty or not a path.</summary>
        string? Resolve(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            try { return Path.GetFullPath(Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }

        /// <summary>Two columns: the number in a green cell with white bold text, then the text.</summary>
        static void Legend(List<TrainingCallout> callouts, Body body)
        {
            var tbl = new Table(
                new TableProperties(new TableStyle { Val = "TableGrid" }, new TableWidth { Width = "9000", Type = TableWidthUnitValues.Dxa }, new TableLayout { Type = TableLayoutValues.Fixed }),
                new TableGrid(new GridColumn { Width = "600" }, new GridColumn { Width = "8400" }));
            foreach (var c in callouts)
            {
                var numberRun = new Run(new RunProperties(new Bold(), new Color { Val = "FFFFFF" }), ExportDocx.TextOf(c.Number.ToString(CultureInfo.InvariantCulture)));
                tbl.Append(new TableRow(
                    new TableCell(
                        new TableCellProperties(new TableCellWidth { Width = "600", Type = TableWidthUnitValues.Dxa }, new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = Green }),
                        new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0" }, new Justification { Val = JustificationValues.Center }), numberRun)),
                    new TableCell(
                        new TableCellProperties(new TableCellWidth { Width = "8400", Type = TableWidthUnitValues.Dxa }),
                        new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0" }), new Run(ExportDocx.TextOf(c.Text))))));
            }
            body.Append(tbl);
            body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "60" })));   // Word wants a paragraph after a table
        }

        void Result(TrainingCheckpoint c, Body body)
        {
            var (text, fill) = c.Pass switch { true => ("Result: PASS", PassFill), false => ("Result: FAIL", FailFill), _ => ("Result: not verified", GreyFill) };
            body.Append(Para(null, text, bold: true, shade: fill, keepNext: c.Differences.Count > 0));
            foreach (var d in c.Differences)
                body.Append(new Paragraph(
                    new ParagraphProperties(new SpacingBetweenLines { After = "40" }, new Indentation { Left = "420", Hanging = "300" }),
                    new Run(ExportDocx.TextOf("•"), new TabChar()),
                    new Run(ExportDocx.TextOf(d))));
        }

        /// <summary>A paragraph; property order follows the schema (style, keep next, page break, shading, spacing).</summary>
        static Paragraph Para(string? style, string text, bool bold = false, bool italic = false, bool pageBreakBefore = false, bool keepNext = false, string? shade = null)
        {
            var pp = new ParagraphProperties();
            if (style != null) pp.Append(new ParagraphStyleId { Val = style });
            if (keepNext) pp.Append(new KeepNext());
            if (pageBreakBefore) pp.Append(new PageBreakBefore());
            if (shade != null) pp.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = shade });
            var rp = new RunProperties();
            if (bold) rp.Append(new Bold());
            if (italic) rp.Append(new Italic());
            var run = new Run();
            if (rp.HasChildren) run.Append(rp);
            run.Append(ExportDocx.TextOf(text));
            return pp.HasChildren ? new Paragraph(pp, run) : new Paragraph(run);
        }
    }
}
