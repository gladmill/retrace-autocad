using System.Xml.Linq;
using Retrace.Engine.Scan.Forms;
using Retrace.Engine.Util;

namespace Retrace.Tests;

public class FormRendererTests
{
    private static List<UiForm> All() => FormScanner.Scan(new[]
    {
        FormScannerTests.Src("Forms/ExportForm.Designer.cs", Lang.CSharp, FormScannerTests.CsDesigner),
        FormScannerTests.Src("res/demo.rc", Lang.Rc, FormScannerTests.Rc, "Demo"),
        FormScannerTests.Src("Views/MainView.xaml", Lang.Xaml, FormScannerTests.Xaml),
        FormScannerTests.Src("acad.dcl", Lang.Dcl, FormScannerTests.Dcl, "(root)"),
    });

    private static XDocument Parse(string svg) => XDocument.Parse(svg);

    private static string[] Texts(XDocument d) =>
        d.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value).ToArray();

    [Fact]
    public void EveryKindRendersParseableSvgWithItsTexts()
    {
        var forms = All();
        Assert.Equal(new[] { UiFormKind.WinForm, UiFormKind.Win32Dialog, UiFormKind.Win32Dialog, UiFormKind.Xaml, UiFormKind.Dcl, UiFormKind.Dcl },
            forms.Select(f => f.Kind).ToArray());
        var expect = new Dictionary<string, string[]>
        {
            ["winform.Demo.Forms.ExportForm"] = new[] { "Export", "Output Folder", "Suffix", "Run" },
            ["rc.Demo.IDD_DEMO"] = new[] { "Demo dialog", "Close", "Name:", "Auto", "Options" },
            ["xaml.Demo.Views.MainView"] = new[] { "Name", "Go" },
            ["dcl.acad.sample"] = new[] { "Sample Dialog", "Modes", "Ortho", "Value", "Mine", "OK", "Cancel" },
        };
        foreach (var f in forms)
        {
            var doc = Parse(FormRenderer.Render(f));
            var texts = Texts(doc);
            if (expect.TryGetValue(f.Id, out var want))
                foreach (var w in want) Assert.Contains(w, texts);
            Assert.Contains(doc.Descendants(), e => e.Attribute("data-source") != null || f.Controls.Count == 0);
        }
    }

    [Fact]
    public void ControlGroupsCarrySourceAttributes()
    {
        var f = All()[0];
        var doc = Parse(FormRenderer.Render(f));
        var sources = doc.Descendants().Select(e => (string?)e.Attribute("data-source")).Where(s => s != null).ToList();
        Assert.Contains(sources, s => s!.StartsWith("Forms/ExportForm.Designer.cs:"));
        Assert.True(sources.Count >= 3);
    }

    [Fact]
    public void ChildrenAreOffsetByTheirParent()
    {
        var f = All()[0];
        var doc = Parse(FormRenderer.Render(f));
        var suffix = doc.Descendants().First(e => e.Name.LocalName == "text" && e.Value == "Suffix");
        // group at (16,10), label at (8,30) in the group: x = 1 (border) + 16 + 8 = 25 before scaling
        double x = double.Parse((string)suffix.Attribute("x")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(25, x, 0.01);
    }

    private static UiForm Dlg(string font, params UiControl[] cs)
    {
        var f = new UiForm { Id = "t.dlg", Title = "T", Kind = UiFormKind.Win32Dialog, Project = "P", Source = new SourceRef("a.rc", 1), Width = 200, Height = 100, Units = "dlu" };
        if (font != "") f.Extra["font"] = font;
        f.Controls.AddRange(cs);
        return f;
    }

    private static UiControl Ctl(string type, string? text, int x, int y, int w, int h, string? style = null)
    {
        var c = new UiControl { Type = type, Name = type, Text = text, X = x, Y = y, W = w, H = h, Source = new SourceRef("a.rc", 2) };
        if (style != null) c.Extra["style"] = style;
        return c;
    }

    private static double Attr(XElement e, string name) => double.Parse((string)e.Attribute(name)!, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("8 MS Shell Dlg", 6, 13)]
    [InlineData("8 Microsoft Sans Serif", 6, 13)]
    [InlineData("9 Segoe UI", 7, 15)]
    [InlineData("9 Arial", 7, 15)]
    [InlineData("", 6, 13)]
    [InlineData("8 Frobnicator", 6, 13)]
    public void DialogUnitsComeFromTheFontLine(string font, int cx, int cy)
    {
        var tf = FontMetrics.Parse(font);
        Assert.Equal((cx, cy), (tf.Cx, tf.Cy));
        // a 100x50 dlu form is (100*cx/4, 50*cy/8) px of client area, plus 2 and 32 px of window chrome
        var f = Dlg(font);
        f.Width = 100; f.Height = 50;
        var root = Parse(FormRenderer.Render(f)).Root!;
        Assert.Equal(Math.Round(100 * cx / 4.0) + 2, Attr(root, "width"));
        Assert.Equal(Math.Round(50 * cy / 8.0) + 32, Attr(root, "height"));
    }

    [Fact]
    public void ControlRectsAreRoundedPerEdgeLikeWindows()
    {
        // Segoe UI 9: 1.75 x 1.875 px per dlu. x=3,w=5: left = round(5.25) = 5, right = round(14) = 14; y=3,h=5: top 6, bottom 15.
        var f = Dlg("9 Segoe UI", Ctl("PUSHBUTTON", "OK", 3, 3, 5, 5));
        var rect = Parse(FormRenderer.Render(f)).Descendants().First(e => e.Name.LocalName == "rect" && (string?)e.Attribute("fill") == "#fdfdfd");
        // Win11 button: 1 px transparent margin, then a 1 px border drawn inside the rect (stroke centered 0.5 px in)
        Assert.Equal(1 + 5 + 1 + 0.5, Attr(rect, "x"));
        Assert.Equal(31 + 6 + 1 + 0.5, Attr(rect, "y"));
        Assert.Equal(14 - 5 - 2 - 1, Attr(rect, "width"));
        Assert.Equal(15 - 6 - 2 - 1, Attr(rect, "height"));
    }

    [Fact]
    public void LtextWrapsToItsRectTopAlignedAndNeverOverflows()
    {
        // 60x16 dlu at 8 pt MS Shell Dlg = 90x26 px: two 13 px lines fit, a third is dropped
        var text = "This label is long enough to need several lines of wrapped text";
        var f = Dlg("8 MS Shell Dlg", Ctl("LTEXT", text, 10, 10, 60, 16));
        var doc = Parse(FormRenderer.Render(f));
        var g = doc.Descendants().First(e => e.Name.LocalName == "g" && ((string?)e.Attribute("id"))!.StartsWith("c1-"));
        var lines = g.Elements().Where(e => e.Name.LocalName == "text").ToList();
        Assert.Equal(2, lines.Count);
        var tf = FontMetrics.Parse("8 MS Shell Dlg");
        double top = 31 + Math.Round(10 * 13 / 8.0);
        double left = 1 + Math.Round(10 * 6 / 4.0);
        Assert.Equal(left, Attr(lines[0], "x"));
        Assert.Equal(top + tf.Ascent, Attr(lines[0], "y"));          // top-aligned: baseline = top + ascent
        Assert.Equal(top + 13 + tf.Ascent, Attr(lines[1], "y"));      // line height = font cy
        Assert.Equal("11", (string)lines[0].Attribute("font-size")!);
        foreach (var l in lines) Assert.True(tf.Width(l.Value) <= 90, l.Value);
        Assert.DoesNotContain("…", g.Value);
        Assert.StartsWith("This label", lines[0].Value);
    }

    [Fact]
    public void LeftNoWordWrapStaticStaysOnOneClippedLine()
    {
        var f = Dlg("8 MS Shell Dlg", Ctl("LTEXT", "A static that is much too long for its rectangle", 10, 10, 40, 24, "SS_LEFTNOWORDWRAP"));
        var g = Parse(FormRenderer.Render(f)).Descendants().First(e => e.Name.LocalName == "g" && ((string?)e.Attribute("id"))!.StartsWith("c1-"));
        var lines = g.Elements().Where(e => e.Name.LocalName == "text").ToList();
        Assert.Single(lines);
        // not truncated: drawn whole and clipped by a clipPath at the rect edge
        Assert.Equal("A static that is much too long for its rectangle", lines[0].Value);
        Assert.NotNull(lines[0].Attribute("clip-path"));
        Assert.DoesNotContain("…", lines[0].Value);
    }

    [Fact]
    public void ComboIsDrawnAtClosedHeightNotDroppedListHeight()
    {
        var f = Dlg("8 MS Shell Dlg", Ctl("COMBOBOX", null, 10, 10, 80, 60, "CBS_DROPDOWNLIST | WS_VSCROLL"));
        var g = Parse(FormRenderer.Render(f)).Descendants().First(e => e.Name.LocalName == "g" && ((string?)e.Attribute("id"))!.StartsWith("c1-"));
        var border = g.Elements().First(e => e.Name.LocalName == "rect" && (string?)e.Attribute("fill") == "#ffffff");
        Assert.Equal(13 + 8, Attr(border, "height") + 1); // cy + 8 = 21 px; box is inset by the 1 px stroke
    }

    [Fact]
    public void WordWiderThanTheRectIsClippedNotBrokenOrDropped()
    {
        // "CLASS:" is 37 px in Microsoft Sans Serif 11 px; the LTEXT is 24 dlu = 36 px wide
        var f = Dlg("8 Microsoft Sans Serif", Ctl("LTEXT", "CLASS:", 10, 10, 24, 8), Ctl("LTEXT", "one two", 50, 10, 24, 8));
        var doc = Parse(FormRenderer.Render(f));
        var g1 = doc.Descendants().First(e => e.Name.LocalName == "g" && ((string?)e.Attribute("id"))!.StartsWith("c1-"));
        var t = g1.Elements().Single(e => e.Name.LocalName == "text");
        Assert.Equal("CLASS:", t.Value);
        var clipId = ((string)t.Attribute("clip-path")!).Replace("url(#", "").TrimEnd(')');
        var clip = g1.Elements().Single(e => e.Name.LocalName == "clipPath");
        Assert.Equal(clipId, (string)clip.Attribute("id")!);
        Assert.Equal(36, Attr(clip.Elements().Single(), "width"));
        // wrapped second word falls below an 8 dlu (13 px) rect: only the first line is drawn
        var g2 = doc.Descendants().First(e => e.Name.LocalName == "g" && ((string?)e.Attribute("id"))!.StartsWith("c2-"));
        Assert.Equal("one", g2.Elements().Single(e => e.Name.LocalName == "text").Value);
        Assert.Equal(doc.Descendants().Count(e => e.Name.LocalName == "clipPath"),
            doc.Descendants().Where(e => e.Name.LocalName == "clipPath").Select(e => (string)e.Attribute("id")!).Distinct().Count());
    }

    private static int AccentButtons(XDocument d, out string? text)
    {
        var gs = d.Descendants().Where(e => e.Name.LocalName == "g" && e.Elements().Any(r => r.Name.LocalName == "rect" && (string?)r.Attribute("stroke") == "#0078d4")).ToList();
        text = gs.FirstOrDefault()?.Elements().FirstOrDefault(e => e.Name.LocalName == "text")?.Value;
        return gs.Count;
    }

    [Fact]
    public void FocusedPushButtonTakesTheAccentFromTheDefaultButton()
    {
        // first WS_TABSTOP control is "Rotate": it gets the accent, DEFPUSHBUTTON "Exit" loses it (XFORM capture)
        var f = Dlg("8 Microsoft Sans Serif",
            Ctl("LTEXT", "label", 5, 5, 40, 8), Ctl("GROUPBOX", "g", 5, 20, 60, 30),
            Ctl("PUSHBUTTON", "Rotate", 10, 30, 40, 14), Ctl("DEFPUSHBUTTON", "Exit", 60, 80, 40, 14));
        Assert.Equal(1, AccentButtons(Parse(FormRenderer.Render(f)), out var t));
        Assert.Equal("Rotate", t);
    }

    [Fact]
    public void DefaultButtonGetsTheAccentWhenFirstTabStopIsNotAButton()
    {
        var f = Dlg("8 Microsoft Sans Serif",
            Ctl("EDITTEXT", null, 5, 5, 60, 13), Ctl("PUSHBUTTON", "Other", 10, 30, 40, 14), Ctl("DEFPUSHBUTTON", "OK", 60, 80, 40, 14));
        Assert.Equal(1, AccentButtons(Parse(FormRenderer.Render(f)), out var t));
        Assert.Equal("OK", t);
        // a CONTROL button without WS_TABSTOP in its style is not a tab stop
        var c = Ctl("PUSHBUTTON", "NoTab", 5, 5, 40, 14, "BS_PUSHBUTTON"); c.Extra["class"] = "Button";
        var f2 = Dlg("8 Microsoft Sans Serif", c, Ctl("DEFPUSHBUTTON", "OK", 60, 80, 40, 14));
        Assert.Equal(1, AccentButtons(Parse(FormRenderer.Render(f2)), out var t2));
        Assert.Equal("OK", t2);
    }

    [Fact]
    public void AbsoluteFormsAreDrawnAtTrueSizeWithoutAutoZoom()
    {
        var small = new UiForm { Id = "t.small", Title = "S", Kind = UiFormKind.WinForm, Project = "P", Source = new SourceRef("a", 1), Width = 100, Height = 50 };
        var big = new UiForm { Id = "t.big", Title = "B", Kind = UiFormKind.WinForm, Project = "P", Source = new SourceRef("a", 1), Width = 5000, Height = 4000 };
        var s = Parse(FormRenderer.Render(small)).Root!;
        var b = Parse(FormRenderer.Render(big)).Root!;
        Assert.Equal(102, Attr(s, "width"));
        Assert.Equal(82, Attr(s, "height"));
        Assert.Equal(5002, Attr(b, "width"));
        Assert.Equal(4032, Attr(b, "height"));
    }

    [Fact]
    public void AutoLayoutsStillScaleSmallFormsUp()
    {
        var e = new UiForm { Id = "e", Title = "Empty", Kind = UiFormKind.Dcl, Project = "P", Source = new SourceRef("a", 1), Units = "auto" };
        Assert.True(Attr(Parse(FormRenderer.Render(e)).Root!, "height") >= 300);
    }

    [Fact]
    public void IconStaticEmbedsTheResourceFileInsideTheInputRoot()
    {
        var root = Directory.CreateTempSubdirectory("retrace-test").FullName;
        try
        {
            // minimal 1x1 BMP header (only the size fields are read)
            var bmp = new byte[58]; bmp[0] = (byte)'B'; bmp[1] = (byte)'M'; bmp[14] = 40; bmp[18] = 16; bmp[22] = 8;
            File.WriteAllBytes(Path.Combine(root, "pic.bmp"), bmp);
            var c = Ctl("ICON", null, 4, 4, 20, 20); c.Extra["image"] = "pic.bmp"; c.Extra["imagekind"] = "bitmap";
            var outside = Ctl("ICON", null, 4, 40, 20, 20); outside.Extra["image"] = "../../etc/hosts";
            var svg = FormRenderer.Render(Dlg("8 MS Shell Dlg", c, outside), root);
            var imgs = Parse(svg).Descendants().Where(e => e.Name.LocalName == "image").ToList();
            Assert.Single(imgs);
            Assert.Equal(16, Attr(imgs[0], "width"));
            Assert.Equal(8, Attr(imgs[0], "height"));
            Assert.StartsWith("data:image/bmp;base64,", (string)imgs[0].Attribute("href")!);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MeasuredWidthsDifferFromAFlatEstimate()
    {
        var f = FontMetrics.Parse("8 Microsoft Sans Serif");
        Assert.True(f.Width("iiiiii") < f.Width("WWWWWW"));
        Assert.True(f.Width("Hello") > 0);
    }

    [Fact]
    public void AllControlKindsAndHostileTextStayValidXml()
    {
        var f = new UiForm { Id = "t.kinds", Title = "A <&> \"b\" \u0001", Kind = UiFormKind.Win32Dialog, Project = "P", Source = new SourceRef("a", 1), Width = 300, Height = 300, Units = "dlu" };
        int y = 0;
        foreach (var t in new[] { "PUSHBUTTON", "EDITTEXT", "LTEXT", "COMBOBOX", "CHECKBOX", "RADIOBUTTON", "GROUPBOX", "LISTBOX", "ListView", "TreeView", "ICON", "Slider", "ProgressBar", "TabControl", "MenuStrip", "FrobnicatorWidget" })
            f.Controls.Add(new UiControl { Type = t, Name = t, Text = "<" + t + "> & \u0002 \ud800", X = 2, Y = y += 12, W = 80, H = 10, Source = new SourceRef("a.rc", y) });
        var doc = Parse(FormRenderer.Render(f));
        var texts = Texts(doc);
        Assert.Contains(texts, t => t.Contains("FrobnicatorWidget"));
        Assert.Contains(texts, t => t.StartsWith("<PUSHBUTTON>"));
    }

    [Fact]
    public void PaletteAndEmptyFormsRender()
    {
        var p = new UiForm { Id = "palette.X", Title = "X", Kind = UiFormKind.PaletteSet, Project = "P", Source = new SourceRef("a", 1), Units = "auto" };
        p.Controls.Add(new UiControl { Type = "MainView", Text = "Main" });
        Assert.Contains(Texts(Parse(FormRenderer.Render(p))), t => t.Contains("MainView"));
        var e = new UiForm { Id = "e", Title = "Empty", Kind = UiFormKind.Dcl, Project = "P", Source = new SourceRef("a", 1), Units = "auto" };
        Parse(FormRenderer.Render(e));
    }
}
