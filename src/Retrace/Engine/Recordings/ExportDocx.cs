// "steps.md" -> "steps.docx" (DocumentFormat.OpenXml, MIT). Walks the Markdig tree of the script.
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Retrace.Engine.Recordings;

public static class ExportDocx
{
    /// <summary>Widest a picture gets, in EMU (16 cm; 1 cm = 360000 EMU).</summary>
    public const long MaxWidthEmu = 16 * 360000L;
    const long MaxHeightEmu = 22 * 360000L;
    const long EmuPerPixel = 9525;   // 96 dpi

    /// <summary>Writes the .docx; returns how many pictures were embedded.</summary>
    public static int Write(StepsDoc doc, string path)
    {
        using var pkg = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = pkg.AddMainDocumentPart();
        AddStyles(main);
        var body = new Body();
        main.Document = new Document(body);
        var w = new Writer(doc, main);
        w.All(doc.Ast, body);
        body.Append(new SectionProperties(
            new PageSize { Width = 11906, Height = 16838 },
            new PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134, Header = 708, Footer = 708, Gutter = 0 }));
        main.Document.Save();
        return w.Pictures;
    }

    /// <summary>PNG width and height from the IHDR chunk, or null if the file is not a PNG.</summary>
    public static (int W, int H)? PngSize(string file)
    {
        try
        {
            Span<byte> h = stackalloc byte[24];
            using var fs = File.OpenRead(file);
            if (fs.Read(h) < 24) return null;
            ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (!h[..8].SequenceEqual(sig) || h[12] != (byte)'I' || h[13] != (byte)'H' || h[14] != (byte)'D' || h[15] != (byte)'R') return null;
            int w = (h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19];
            int ht = (h[20] << 24) | (h[21] << 16) | (h[22] << 8) | h[23];
            return w > 0 && ht > 0 ? (w, ht) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Picture size in EMU: natural size at 96 dpi, scaled down (aspect kept) to at most 16 cm wide.</summary>
    public static (long Cx, long Cy) FitEmu(int w, int h)
    {
        double cx = w * (double)EmuPerPixel, cy = h * (double)EmuPerPixel;
        double k = Math.Min(1.0, Math.Min(MaxWidthEmu / cx, MaxHeightEmu / cy));
        return (Math.Max(1, (long)(cx * k)), Math.Max(1, (long)(cy * k)));
    }

    static void AddStyles(MainDocumentPart main)
    {
        var sp = main.AddNewPart<StyleDefinitionsPart>();
        Style Para(string id, string name, string? basedOn, string size, bool bold, string? before = null, string? after = null, string? next = "Normal")
        {
            var rpr = new StyleRunProperties();
            if (bold) rpr.Append(new Bold());
            rpr.Append(new FontSize { Val = size });
            var st = new Style(new StyleName { Val = name }) { Type = StyleValues.Paragraph, StyleId = id };
            if (basedOn != null) st.Append(new BasedOn { Val = basedOn });
            if (next != null) st.Append(new NextParagraphStyle { Val = next });
            st.Append(new PrimaryStyle());
            st.Append(new StyleParagraphProperties(new SpacingBetweenLines { Before = before ?? "0", After = after ?? "120" }));
            st.Append(rpr);
            return st;
        }
        var normal = new Style(new StyleName { Val = "Normal" }, new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { After = "120", Line = "264", LineRule = LineSpacingRuleValues.Auto}),
            new StyleRunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" }, new FontSize { Val = "22" }))
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true };
        var h1 = Para("Heading1", "heading 1", "Normal", "36", true, "240", "160");
        var h2 = Para("Heading2", "heading 2", "Normal", "28", true, "280", "120");
        var h3 = Para("Heading3", "heading 3", "Normal", "24", true, "200", "80");
        foreach (var h in new[] { h1, h2, h3 }) h.StyleParagraphProperties!.PrependChild(new KeepNext());
        var code = new Style(new StyleName { Val = "Code" }, new PrimaryStyle(),
            new StyleRunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }, new FontSize { Val = "20" },
                new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F0F0F0" }))
        { Type = StyleValues.Character, StyleId = "Code" };
        var grid = new Style(new StyleName { Val = "Table Grid" }, new BasedOn { Val = "TableNormal" }, new PrimaryStyle(),
            new StyleTableProperties(new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "A0A0A0" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "A0A0A0" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "A0A0A0" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "A0A0A0" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "A0A0A0" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "A0A0A0" }),
                new TableCellMarginDefault(new TopMargin { Width = "40", Type = TableWidthUnitValues.Dxa }, new TableCellLeftMargin { Width = 100, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "40", Type = TableWidthUnitValues.Dxa }, new TableCellRightMargin { Width = 100, Type = TableWidthValues.Dxa })))
        { Type = StyleValues.Table, StyleId = "TableGrid" };
        var tableNormal = new Style(new StyleName { Val = "Normal Table" }, new UIPriority { Val = 99 }, new SemiHidden(), new UnhideWhenUsed(),
            new StyleTableProperties(new TableIndentation { Width = 0, Type = TableWidthUnitValues.Dxa },
                new TableCellMarginDefault(new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa }, new TableCellLeftMargin { Width = 108, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "0", Type = TableWidthUnitValues.Dxa }, new TableCellRightMargin { Width = 108, Type = TableWidthValues.Dxa })))
        { Type = StyleValues.Table, StyleId = "TableNormal", Default = true };
        sp.Styles = new Styles(normal, h1, h2, h3, code, tableNormal, grid);
        sp.Styles.Save();
    }

    sealed class Writer(StepsDoc doc, MainDocumentPart main)
    {
        public int Pictures;
        uint _drawingId = 1;

        public void All(IEnumerable<Block> blocks, OpenXmlElement into) { foreach (var b in blocks) Block(b, into, 0, null); }

        record Marker(string Text);   // the "1." / bullet to put in front of a list item's first paragraph

        void Block(Block b, OpenXmlElement into, int indent, Marker? marker)
        {
            switch (b)
            {
                case HeadingBlock h:
                    into.Append(Para(h.Inline, h.Level switch { 1 => "Heading1", 2 => "Heading2", _ => "Heading3" }, 0, null, false));
                    break;
                case ParagraphBlock p:
                    Paragraph(p, into, indent, marker);
                    break;
                case ListBlock l:
                    WriteList(l, into, indent);
                    break;
                case MdTable t:
                    into.Append(TableOf(t));
                    into.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "60" })));   // Word wants a paragraph after a table
                    break;
                case QuoteBlock q:
                    foreach (var c in q) Block(c, into, indent + 1, null);
                    break;
                case FencedCodeBlock or CodeBlock:
                    foreach (var line in ((LeafBlock)b).Lines.Lines.Take(((LeafBlock)b).Lines.Count))
                        into.Append(IndentPara(indent, null, new Run(new RunProperties(new RunStyle { Val = "Code" }), Text(line.ToString()))));
                    break;
                case ThematicBreakBlock:
                    into.Append(new Paragraph(new ParagraphProperties(new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "A0A0A0" }))));
                    break;
                case ContainerBlock cb:
                    foreach (var c in cb) Block(c, into, indent, null);
                    break;
            }
        }

        void WriteList(ListBlock l, OpenXmlElement into, int indent)
        {
            int n = int.TryParse(l.OrderedStart, out var s) ? s : 1;
            foreach (var item in l.OfType<ListItemBlock>())
            {
                var m = new Marker(l.IsOrdered ? $"{n++}." : "•");
                bool first = true;
                foreach (var c in item)
                {
                    Block(c, into, indent + 1, first && c is ParagraphBlock ? m : null);
                    if (c is ParagraphBlock) first = false;
                }
            }
        }

        static Paragraph IndentPara(int indent, Marker? marker, params OpenXmlElement[] runs)
        {
            var pp = new ParagraphProperties(new SpacingBetweenLines { After = "80" });
            if (indent > 0)
                pp.Append(marker != null
                    ? new Indentation { Left = (indent * 420).ToString(), Hanging = "360" }
                    : new Indentation { Left = (indent * 420).ToString() });
            var p = new Paragraph(pp);
            if (marker != null) p.Append(new Run(Text(marker.Text), new TabChar()));
            foreach (var r in runs) p.Append(r);
            return p;
        }

        void Paragraph(ParagraphBlock p, OpenXmlElement into, int indent, Marker? marker)
        {
            // a paragraph that is only pictures: one paragraph per picture, so Word lays them out one under the other
            bool onlyImages = p.Inline != null && p.Inline.All(i => i is LinkInline { IsImage: true } || i is LineBreakInline || (i is LiteralInline l && string.IsNullOrWhiteSpace(l.Content.ToString())));
            if (onlyImages && p.Inline!.Any(i => i is LinkInline))
            {
                foreach (var li in p.Inline!.OfType<LinkInline>())
                    into.Append(Para(null, null, indent, marker, false, li));
                return;
            }
            into.Append(Para(p.Inline, null, indent, marker, false));
        }

        Paragraph Para(ContainerInline? inl, string? style, int indent, Marker? marker, bool bold, LinkInline? onlyImage = null)
        {
            var p = IndentPara(indent, marker);
            if (style != null) p.ParagraphProperties!.PrependChild(new ParagraphStyleId { Val = style });
            if (onlyImage != null) p.Append(ImageRun(onlyImage));
            else Inlines(inl, p, bold, false, false);
            return p;
        }

        void Inlines(ContainerInline? c, Paragraph p, bool bold, bool italic, bool code)
        {
            if (c == null) return;
            for (var n = c.FirstChild; n != null; n = n.NextSibling)
                switch (n)
                {
                    case LiteralInline l: p.Append(Run(l.Content.ToString(), bold, italic, code)); break;
                    case CodeInline ci: p.Append(Run(ci.Content, bold, italic, true)); break;
                    case LineBreakInline lb: p.Append(lb.IsHard ? new Run(new Break()) : Run(" ", bold, italic, code)); break;
                    case LinkInline { IsImage: true } img: p.Append(ImageRun(img)); break;
                    case EmphasisInline em:
                        Inlines(em, p, bold || em.DelimiterCount >= 2, italic || em.DelimiterCount == 1 || em.DelimiterCount >= 3, code);
                        break;
                    case ContainerInline other: Inlines(other, p, bold, italic, code); break;   // links: their text
                }
        }

        static Text Text(string s) => new(s) { Space = SpaceProcessingModeValues.Preserve };

        static Run Run(string text, bool bold, bool italic, bool code)
        {
            var rp = new RunProperties();
            if (code) rp.Append(new RunStyle { Val = "Code" });
            if (bold) rp.Append(new Bold());
            if (italic) rp.Append(new Italic());
            var r = new Run();
            if (rp.HasChildren) r.Append(rp);
            r.Append(Text(text));
            return r;
        }

        Run ImageRun(LinkInline img)
        {
            var alt = StepsDoc.PlainText(img);
            var altText = alt.Length > 0 ? alt : "picture";
            var url = img.Url ?? "";
            string file;
            try { file = doc.ResolvePath(url); } catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException) { return Run($"[picture: {altText}]", false, true, false); }
            var size = File.Exists(file) ? ExportDocx.PngSize(file) : null;
            if (size == null) return Run($"[picture not found: {url}]", false, true, false);

            var (cx, cy) = FitEmu(size.Value.W, size.Value.H);
            var part = main.AddImagePart(ImagePartType.Png);
            using (var fs = File.OpenRead(file)) part.FeedData(fs);
            var rid = main.GetIdOfPart(part);
            var id = _drawingId++;
            Pictures++;
            var drawing = new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = cx, Cy = cy },
                    new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                    new DW.DocProperties { Id = id, Name = $"Picture {id}", Description = altText },
                    new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0U, Name = Path.GetFileName(file), Description = altText }, new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(new A.Blip { Embed = rid }, new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
                { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
            return new Run(drawing);
        }

        DocumentFormat.OpenXml.Wordprocessing.Table TableOf(MdTable t)
        {
            var tbl = new DocumentFormat.OpenXml.Wordprocessing.Table(new TableProperties(
                new TableStyle { Val = "TableGrid" }, new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));
            int cols = t.OfType<MdRow>().Select(r => r.Count).DefaultIfEmpty(1).Max();
            var grid = new TableGrid();
            for (int i = 0; i < cols; i++) grid.Append(new GridColumn { Width = (9638 / cols).ToString() });
            tbl.Append(grid);
            foreach (var row in t.OfType<MdRow>())
            {
                var tr = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
                if (row.IsHeader) tr.Append(new TableRowProperties(new TableHeader()));
                foreach (var cell in row.OfType<MdCell>())
                {
                    var tc = new DocumentFormat.OpenXml.Wordprocessing.TableCell();
                    if (row.IsHeader) tc.Append(new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "E7E6E6" }));
                    foreach (var cb in cell)
                        if (cb is ParagraphBlock pb) tc.Append(Para(pb.Inline, null, 0, null, row.IsHeader));
                        else Block(cb, tc, 0, null);
                    if (!tc.Elements<Paragraph>().Any()) tc.Append(new Paragraph());   // a cell needs a paragraph
                    tr.Append(tc);
                }
                tbl.Append(tr);
            }
            return tbl;
        }
    }
}
