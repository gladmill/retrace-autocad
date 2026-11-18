using System.Text.Json;
using Retrace.Core;
using Retrace.Engine;
using Xunit;

namespace Retrace.Tests;

/// <summary>Steps made of pane clicks: the optional `paneType` of a `palette_click`, the wording of a modeless window and of an application menu item, an old recording without the new fields, the geometry that tells the application menu.</summary>
public class PaneStepsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "retrace-panesteps-" + Guid.NewGuid().ToString("N"));
    public PaneStepsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    static List<Step> Plan_(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    const string Header = """{"type":"header","name":"t","product":"AutoCAD 2026"}""";

    string StepsMd(params string[] events)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rec = Path.Combine(dir, "recording.jsonl");
        File.WriteAllLines(rec, new[] { Header }.Concat(events).Concat(new[] { """{"n":99,"t":9000,"type":"session_end"}""" }));
        Assert.True(EngineRunner.WriteSteps(rec).Success);
        return File.ReadAllText(Path.Combine(dir, "steps.md"));
    }

    [Fact]
    public void A_palette_click_carries_the_pane_type_into_its_step()
    {
        var steps = Plan_("""{"n":1,"t":100,"type":"palette_click","palette":"Editor","set":"","paneType":"My.Editor.Form","button":"Canvas","kind":"point","class":"window","classIndex":1,"x":0.5,"y":0.5}""");
        var s = Assert.Single(steps);
        Assert.Equal("My.Editor.Form", s.PaneType);
        Assert.Equal("Editor", s.DialogTitle);
    }

    [Fact]
    public void An_old_recording_without_pane_type_builds_the_same_step()
    {
        var old = Plan_("""{"n":1,"t":100,"type":"palette_click","palette":"Box","set":"Samples","button":"Draw box"}""");
        var s = Assert.Single(old);
        Assert.Null(s.PaneType);
        Assert.Equal("Samples", s.Panel);
        Assert.Equal("Draw box", s.Button);
        // the same event with the new field differs only by that field
        var withField = Assert.Single(Plan_("""{"n":1,"t":100,"type":"palette_click","palette":"Box","set":"Samples","paneType":"X.Y","button":"Draw box"}"""));
        Assert.Equal(s.Button, withField.Button); Assert.Equal(s.Panel, withField.Panel); Assert.Equal(s.DialogTitle, withField.DialogTitle); Assert.Equal(s.Kind, withField.Kind);
    }

    [Fact]
    public void A_modeless_window_reads_window_and_a_palette_still_reads_palette()
    {
        var md = StepsMd(
            """{"n":1,"t":100,"type":"palette_click","palette":"Editor","set":"","paneType":"My.Form","button":"Save"}""",
            """{"n":2,"t":200,"type":"palette_click","palette":"Box","set":"Samples","button":"Draw box"}""");
        Assert.Contains("Editor window", md);
        Assert.DoesNotContain("Editor palette", md);
        Assert.Contains("Box palette", md);
    }

    [Fact]
    public void An_application_menu_item_reads_application_menu_and_an_old_ribbon_click_still_reads_the_ribbon()
    {
        var md = StepsMd(
            """{"n":1,"t":100,"type":"ribbon","id":"ID_Options","text":"Options","bar":"appmenu"}""",
            """{"n":2,"t":900,"type":"ribbon","id":"ID_Circle","text":"Circle","tab":"Home","panel":"Draw"}""");
        Assert.Contains("Application menu", md);
        Assert.Contains("Ribbon › Home tab › Draw panel", md);
    }

    [Theory]
    [InlineData(13, 28, 618, 1.0, true)]      // the application menu at 100 %
    [InlineData(-8, -8, 640, 1.0, true)]      // AutoCAD maximized: its frame starts outside the screen
    [InlineData(26, 56, 1236, 2.0, true)]     // 200 % scaling
    [InlineData(300, 28, 270, 1.0, false)]    // the QAT customize menu opens under its own button
    [InlineData(13, 800, 300, 1.0, false)]    // the workspace switcher opens at the bottom
    [InlineData(40, 150, 300, 1.0, false)]    // a ribbon drop-down under its button
    [InlineData(13, 28, 120, 1.0, false)]     // too short for the application menu
    [InlineData(-1900, 28, 618, 1.0, false)]  // a menu of a window further left on another monitor, measured from its own corner
    public void The_application_menu_is_told_by_where_it_opens(int left, int top, int height, double scale, bool expected) =>
        Assert.Equal(expected, AppMenuGeometry.IsAppMenu(left, top, height, scale));

    [Theory]
    [InlineData("Editor", "Editor", true, 0)]
    [InlineData("Editor", "Editor", false, 1)]
    [InlineData("Editor - Drawing1", "Editor - Drawing2", true, 2)]
    [InlineData("Editor - Drawing1", "Editor - Drawing2", false, 3)]
    [InlineData("Editor", "Other", true, -1)]
    public void Window_titles_rank_exact_then_type_then_loose(string recorded, string live, bool type, int rank) => Assert.Equal(rank, PaneClicks.TitleRank(recorded, live, type));

    [Fact]
    public void Add_in_decision_by_company_under_autocad_folders()
    {
        const string Acad = @"C:\Program Files\Autodesk\AutoCAD 2026";
        Assert.True(PaneClicks.IsAddIn("MyTools", Acad + @"\Plugins\MyTools.dll", false, Acad, "Contoso"));           // an add-in installed below the AutoCAD folder
        Assert.False(PaneClicks.IsAddIn("AcLayerPanel", Acad + @"\AcLayerPanel.dll", false, Acad, "Autodesk, Inc."));
        Assert.True(PaneClicks.IsAddIn("DevExpress.Xpf", @"C:\WINDOWS\Microsoft.NET\assembly\GAC_MSIL\DevExpress.Xpf\x.dll", false, Acad, "Developer Express Inc."));
        Assert.False(PaneClicks.IsAddIn("Foo", @"C:\WINDOWS\Microsoft.NET\assembly\GAC_MSIL\Foo\Foo.dll", false, Acad, "Microsoft Corporation"));
        Assert.False(PaneClicks.IsAddIn("AdskBundle", @"C:\Users\u\AppData\Roaming\Autodesk\ApplicationPlugins\Adsk.bundle\Contents\Adsk.dll", false, Acad, "Autodesk, Inc."));
        Assert.True(PaneClicks.IsAddIn("Retrace.Samples", @"C:\Users\u\AppData\Roaming\Autodesk\ApplicationPlugins\Retrace.Samples.bundle\Contents\Retrace.Samples.dll", false, Acad, "Retrace.Samples"));
        Assert.False(PaneClicks.IsAddIn("Unknown", Acad + @"\Unknown.dll", false, Acad, null));   // nothing known about the file: not an add-in's
        var why = PaneClicks.AddInDecision("AcLayerPanel", Acad + @"\AcLayerPanel.dll", false, Acad, "Autodesk, Inc.").Why;
        Assert.Contains("the AutoCAD folder", why);
        Assert.DoesNotContain("Program Files", why);   // never a full path in the log
    }

    [Fact]
    public void Wpf_leaf_needs_mouse_handling_and_no_input_control_above_it()
    {
        Assert.True(PaneClicks.WpfLeafClick(true, 0, declaresMouseHandling: true, insideInputControl: false));
        Assert.False(PaneClicks.WpfLeafClick(true, 0, declaresMouseHandling: false, insideInputControl: false));   // a label or icon subclass
        Assert.False(PaneClicks.WpfLeafClick(true, 0, declaresMouseHandling: true, insideInputControl: true));    // a template part of a list item, toggle or grid cell
        Assert.False(PaneClicks.WpfLeafClick(true, 2, declaresMouseHandling: true, insideInputControl: false));
        Assert.False(PaneClicks.WpfLeafClick(false, 0, declaresMouseHandling: true, insideInputControl: false));
    }

    [Theory]
    [InlineData("richedit60w", false)]
    [InlineData("directuihwnd", false)]
    [InlineData("shelldll_defview", false)]
    [InlineData("mdiclient", false)]
    [InlineData("rebarwindow32", false)]
    [InlineData("syslink", false)]
    [InlineData("gridctrl", true)]
    public void Standard_and_shell_classes_are_never_points(string seg, bool expected) => Assert.Equal(expected, PaneClicks.UnknownClassIsPoint(seg));

    [Fact]
    public void A_palette_click_keeps_a_claim_of_thirty_seconds_for_every_kind()
    {
        Assert.True(PaletteClaim.Claims(false, 20_000, 0, 5, 5));
        Assert.True(PaletteClaim.Claims(false, 30_000, 0, 6, 5));    // one more input: the click's own button-down is counted at the click
        Assert.False(PaletteClaim.Claims(false, 30_001, 0, 5, 5));
        Assert.False(PaletteClaim.Claims(false, 500, 0, 9, 5));      // typed keys advance the count: a typed command is never claimed
        Assert.False(PaletteClaim.Claims(true, 500, 0, 5, 5));
    }
}
