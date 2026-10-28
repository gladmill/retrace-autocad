using Retrace.Core;
using Retrace.Engine.Emit;

namespace Retrace.Tests;

/// <summary>Ribbon tabs captured in AutoCAD (RETRACECAPTURERIBBON) replace the wireframes in the docs. Synthetic capture of the sample model's
/// Garden tab: the live ribbon's id has the group in front, the panel is matched by title, the items by id and by text.</summary>
public class RibbonCaptureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("retrace-ribcap").FullName;
    private string Docs => Path.Combine(_root, "docs");
    private string Captures => Path.Combine(_root, "captures");
    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>ribbon__garden.png (200 x 100), its Beds panel picture and the .json the capture command writes.</summary>
    public static void WriteGardenCapture(string dir)
    {
        Directory.CreateDirectory(dir);
        Png.Write(Path.Combine(dir, "ribbon__plugin.tab_garden.png"), 200, 100, new byte[200 * 100 * 4]);
        Png.Write(Path.Combine(dir, "ribbon__plugin.tab_garden__1.png"), 120, 70, new byte[120 * 70 * 4]);
        File.WriteAllText(Path.Combine(dir, "ribbon__plugin.tab_garden.json"), """
            {"kind":"ribbon-tab","tabId":"PLUGIN.TAB_Garden","tabTitle":"Garden","width":200,"height":100,"dpi":96,
             "panels":[{"id":null,"title":"Beds","rect":[10,25,120,70],"picture":"ribbon__plugin.tab_garden__1.png",
               "items":[{"id":"DEMO_Settings","text":"Settings","rect":[14,30,40,60]},{"id":null,"text":"List Points","rect":[60,30,50,22]}]}]}
            """);
    }

    private (EngineModel M, string TabSvg) Build(bool withCapture)
    {
        if (withCapture) WriteGardenCapture(Captures);
        var m = EmitTests.SampleModel();
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = Docs, Captures = withCapture ? [Captures] : [] });
        var svg = Directory.GetFiles(Path.Combine(Docs, "reference", "images", "ribbon"), "*.svg", SearchOption.AllDirectories).Single();
        return (m, File.ReadAllText(svg));
    }

    [Fact]
    public void A_captured_tab_is_its_picture_with_the_wireframe_ids_over_it()
    {
        var (_, svg) = Build(withCapture: true);
        Assert.Contains("href=\"data:image/png;base64,", svg);
        Assert.Contains("id=\"tab-TAB_Garden\"", svg);
        Assert.Contains("id=\"panel-Beds\"", svg);
        Assert.Contains("id=\"item-DEMO_Settings\" data-source=\"Plugin/Tabs/GardenTab.cs:210\"><rect x=\"14\" y=\"30\" width=\"40\" height=\"60\"", svg);
        Assert.Contains("id=\"item-Pts\"", svg);   // matched by its text
        Assert.Contains("(captured in AutoCAD)", svg);
    }

    [Fact]
    public void A_button_section_shows_its_panel_with_the_button_outlined()
    {
        Build(withCapture: true);
        var page = File.ReadAllText(Directory.GetFiles(Path.Combine(Docs, "guide", "ribbon"), "*.md", SearchOption.AllDirectories).Single());
        Assert.Contains("![Garden ribbon](", page);
        Assert.Contains("![Settings in the Beds panel](", page);
        var pics = Directory.GetFiles(Path.Combine(Docs, "guide", "images", "ribbon"), "*.svg", SearchOption.AllDirectories);
        var settings = File.ReadAllText(pics.Single(p => p.EndsWith("b-settings.svg")));
        Assert.Contains("<rect x=\"2.5\" y=\"3.5\" width=\"43\" height=\"63\"", settings);   // 14-10-1.5, 30-25-1.5
    }

    [Fact]
    public void Without_a_capture_the_tab_keeps_its_wireframe_and_no_warning()
    {
        var (m, svg) = Build(withCapture: false);
        Assert.DoesNotContain("data:image/png", svg);
        Assert.DoesNotContain(m.Warnings, w => w.StartsWith("ribbon: no capture"));
    }

    [Fact]
    public void A_second_build_changes_nothing()
    {
        Build(withCapture: true);
        var before = Directory.GetFiles(Docs, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".svg") || f.EndsWith(".md")).ToDictionary(f => f, File.ReadAllText);   // data/*.json carry the build time
        Build(withCapture: true);
        foreach (var (f, text) in before) Assert.Equal(text, File.ReadAllText(f));
    }

    [Fact]
    public void AutoCADs_own_tab_with_the_same_title_is_not_borrowed()
    {
        Directory.CreateDirectory(Captures);
        Png.Write(Path.Combine(Captures, "ribbon__acad.garden.png"), 20, 10, new byte[20 * 10 * 4]);
        File.WriteAllText(Path.Combine(Captures, "ribbon__acad.garden.json"),
            """{"kind":"ribbon-tab","tabId":"ACAD.ID_TabGarden","tabTitle":"Garden","width":20,"height":10,"panels":[]}""");
        var m = EmitTests.SampleModel();
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = Docs, Captures = [Captures] });
        var svg = File.ReadAllText(Directory.GetFiles(Path.Combine(Docs, "reference", "images", "ribbon"), "*.svg", SearchOption.AllDirectories).Single());
        Assert.DoesNotContain("data:image/png", svg);
        Assert.Contains(m.Warnings, w => w.StartsWith("ribbon: no capture of the tab Garden"));
    }

    [Fact]
    public void A_panel_cut_off_at_the_ribbons_edge_is_outlined_inside_its_picture()
    {
        Directory.CreateDirectory(Captures);
        Png.Write(Path.Combine(Captures, "ribbon__plugin.tab_garden.png"), 200, 100, new byte[200 * 100 * 4]);
        Png.Write(Path.Combine(Captures, "ribbon__plugin.tab_garden__1.png"), 50, 70, new byte[50 * 70 * 4]);   // the panel goes on past x = 200
        File.WriteAllText(Path.Combine(Captures, "ribbon__plugin.tab_garden.json"), """
            {"kind":"ribbon-tab","tabId":"PLUGIN.TAB_Garden","tabTitle":"Garden","width":200,"height":100,
             "panels":[{"title":"Beds","rect":[150,25,120,70],"pictureRect":[150,25,50,70],"picture":"ribbon__plugin.tab_garden__1.png",
               "items":[{"id":"DEMO_Settings","text":"Settings","rect":[160,30,20,20]},{"text":"List Points","rect":[190,30,40,20]}]}]}
            """);
        var m = EmitTests.SampleModel();
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = Docs, Captures = [Captures] });
        var pics = Directory.GetFiles(Path.Combine(Docs, "guide", "images", "ribbon"), "*.svg", SearchOption.AllDirectories);
        Assert.Contains("<rect x=\"8.5\" y=\"3.5\" width=\"23\" height=\"23\"", File.ReadAllText(pics.Single(p => p.EndsWith("b-settings.svg"))));
        Assert.DoesNotContain(pics, p => p.EndsWith("b-list-points.svg"));   // past the picture's edge: no outline
    }

    [Fact]
    public void A_picture_that_cannot_be_read_falls_back_to_the_wireframe()
    {
        WriteGardenCapture(Captures);
        File.WriteAllBytes(Path.Combine(Captures, "ribbon__plugin.tab_garden.png"), new byte[] { 1, 2, 3 });   // not a PNG: still embedded as data
        File.Delete(Path.Combine(Captures, "ribbon__plugin.tab_garden__1.png"));                           // the panel picture is gone
        var m = EmitTests.SampleModel();
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = Docs, Captures = [Captures] });
        Assert.False(Directory.Exists(Path.Combine(Docs, "guide", "images", "ribbon")));   // no panel picture, no button pictures
    }
}
