using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

/// <summary>Clicks inside panes an add-in puts into AutoCAD: the chain classification, which assemblies are an add-in's, which clicks are steps, which window classes make a point click, the rate limit of the log lines, the claim of a command by a palette click.</summary>
public class PaneClicksTests
{
    static string[] Chain(params string[] c) => c;
    const string Acad = @"C:\Program Files\Autodesk\AutoCAD 2026";

    [Fact]
    public void Native_chain_is_never_a_pane()
    {
        Assert.Equal(PaneChain.Native, PaneClicks.Classify(Chain("AcDbWindow", "AfxMDIFrame140", "Afx:00400000:b:10003")));
        Assert.Equal(PaneChain.Native, PaneClicks.Classify(Chain("Edit", "#32770")));
    }

    [Fact]
    public void Winforms_in_a_palette_set_frame()
    {
        Assert.Equal(PaneChain.InPaletteSet, PaneClicks.Classify(Chain("WindowsForms10.Window.8.app.0.1", "WindowsForms10.Window.8.app.0.2", "AfxWnd140u", "AfxWnd140u", "AfxControlBar140u", "Afx:1")));
        Assert.Equal(PaneChain.InPaletteSet, PaneClicks.Classify(Chain("HwndWrapper[acad.exe;;1]", "AfxWnd140u", "AfxWnd140u", "AfxControlBar140u")));
    }

    [Fact]
    public void A_frame_of_another_shape_is_odd_not_native()
    {
        Assert.Equal(PaneChain.InOddFrame, PaneClicks.Classify(Chain("WindowsForms10.Window.8.app.0.1", "AfxWnd140u", "Afx:1")));
        Assert.Equal(PaneChain.InOddFrame, PaneClicks.Classify(Chain("WindowsForms10.Window.8.app.0.1", "AfxWnd140u")));
    }

    [Fact]
    public void Managed_content_without_a_frame_is_top_level()
    {
        Assert.Equal(PaneChain.ManagedTopLevel, PaneClicks.Classify(Chain("WindowsForms10.BUTTON.app.0.1", "WindowsForms10.Window.8.app.0.2", "Afx:00400000:b")));
        Assert.Equal(PaneChain.ManagedTopLevel, PaneClicks.Classify(Chain("HwndWrapper[acad.exe;;2]")));
    }

    [Theory]
    [InlineData("MyAddIn", @"C:\Addins\MyAddIn.dll", false, true)]
    [InlineData("DevExpress.XtraGrid.v21.2", @"D:\vendor\DevExpress.XtraGrid.dll", false, true)]
    [InlineData("AcadTools", @"C:\Addins\AcadTools.dll", false, true)]        // a prefix of AutoCAD's names is no reason
    [InlineData("SystemTools", @"C:\Addins\SystemTools.dll", false, true)]    // "System" only as an exact or dotted name
    [InlineData("PresentationHelpers", @"C:\Addins\PresentationHelpers.dll", false, true)]
    [InlineData("MyAddIn", "", false, true)]                                   // loaded from bytes
    [InlineData("AcLayerPanel", Acad + @"\AcLayerPanel.dll", false, false)]    // AutoCAD's own: by where it lives
    [InlineData("AdskVertical", Acad + @"\Vertical\Arch\AdskVertical.dll", false, false)]
    [InlineData("CefSharp.Wpf", Acad + @"\CefSharp.Wpf.dll", false, false)]
    [InlineData("System.Windows.Forms", @"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.0\System.Windows.Forms.dll", false, false)]
    [InlineData("Foo", @"C:\WINDOWS\Microsoft.NET\assembly\GAC_MSIL\Foo\Foo.dll", false, false)]
    [InlineData("DynamicProxyGenAssembly2", "", true, false)]
    [InlineData("Retrace", @"C:\x\Retrace.bundle\Contents\Retrace.dll", false, false)]
    [InlineData("AcadStatus", @"C:\x\AcadStatus.dll", false, false)]
    [InlineData("WindowsBase", "", false, false)]
    [InlineData("mscorlib", "", false, false)]
    [InlineData("", @"C:\x.dll", false, false)]
    public void Add_in_assemblies(string name, string location, bool dynamic, bool expected) => Assert.Equal(expected, PaneClicks.IsAddIn(name, location, dynamic, Acad));

    [Fact]
    public void A_plain_window_is_a_pane_only_with_add_in_content()
    {
        Assert.True(PaneClicks.IsPaneWindow(true, false, false));    // an add-in's own subclass
        Assert.True(PaneClicks.IsPaneWindow(false, true, true));     // a plain Form / Window filled with an add-in's controls
        Assert.False(PaneClicks.IsPaneWindow(false, true, false));   // Retrace's script viewer, a sign-in window: plain, no add-in content
        Assert.False(PaneClicks.IsPaneWindow(false, false, true));   // AutoCAD's or Retrace's own subclass
    }

    [Fact]
    public void Winforms_click_needs_a_handler_or_an_add_in_leaf()
    {
        Assert.True(PaneClicks.WinFormsClickable(true, false));
        Assert.True(PaneClicks.WinFormsClickable(false, true, childControls: 0));
        Assert.False(PaneClicks.WinFormsClickable(false, true, childControls: 2));    // a container's blank area is no control
        Assert.False(PaneClicks.WinFormsClickable(false, false));
    }

    [Fact]
    public void The_control_a_page_is_counts_only_when_it_is_a_leaf()
    {
        Assert.True(PaneClicks.WinFormsClickable(false, true, topOfPage: true, childControls: 0));
        Assert.True(PaneClicks.WinFormsClickable(true, false, topOfPage: true, childControls: 0));
        Assert.False(PaneClicks.WinFormsClickable(false, true, topOfPage: true, childControls: 3));
        Assert.False(PaneClicks.WinFormsClickable(true, true, topOfPage: true, childControls: 1));
        Assert.False(PaneClicks.WinFormsClickable(false, false, topOfPage: true, childControls: 0));
    }

    [Theory]
    [InlineData("edit", false)]
    [InlineData("button", false)]
    [InlineData("syslistview32", false)]
    [InlineData("window", false)]
    [InlineData("static", false)]
    [InlineData("hwndwrapper[acad.exe;;1]", false)]
    [InlineData("afxwnd140u", false)]
    [InlineData("#32770", false)]
    [InlineData("", false)]
    [InlineData("chrome_widgetwin_1", false)]
    [InlineData("chrome_renderwidgethosthwnd", false)]
    [InlineData("intermediate d3d window", false)]
    [InlineData("internet explorer_server", false)]
    [InlineData("shell embedding", false)]
    [InlineData("atl:00007ff8", false)]
    [InlineData("cefbrowserwindow", false)]
    [InlineData("gridctrl", true)]
    [InlineData("thirdpartytree.1", true)]
    public void Unknown_window_classes_make_point_clicks(string seg, bool expected) => Assert.Equal(expected, PaneClicks.UnknownClassIsPoint(seg));

    [Theory]
    [InlineData("Retrace Pane Form", "Retrace Pane Form", true)]
    [InlineData("Editor - [Drawing1.dwg]", "Editor - [Drawing2.dwg]", true)]
    [InlineData("Editor - Drawing1", "Editor", true)]
    [InlineData("Editor", "Other - Drawing1", false)]
    [InlineData("", "Anything", false)]
    [InlineData("Editor", null, false)]
    public void Titles_match_before_a_document_suffix(string recorded, string? live, bool expected) => Assert.Equal(expected, PaneClicks.SameTitle(recorded, live));

    [Fact]
    public void Chain_text_is_capped()
    {
        Assert.Equal("A < B", PaneClicks.ChainText(Chain("A", "B")));
        var many = Enumerable.Range(1, 15).Select(i => "C" + i).ToList();
        var t = PaneClicks.ChainText(many);
        Assert.EndsWith("C10 < ...", t);
        Assert.DoesNotContain("C11", t);
    }

    [Fact]
    public void Log_gate_logs_a_reason_once()
    {
        var g = new PaneClicks.LogGate();
        Assert.True(g.Allow("a", 1000, out var s0)); Assert.Equal(0, s0);
        Assert.False(g.Allow("a", 5000, out _));
        Assert.True(g.Allow("b", 5000, out _));
    }

    [Fact]
    public void Log_gate_spaces_lines_and_counts_what_it_left_out()
    {
        var g = new PaneClicks.LogGate();
        Assert.True(g.Allow("a", 1000, out _));
        Assert.False(g.Allow("b", 1100, out _));
        Assert.False(g.Allow("c", 1200, out _));
        Assert.Equal(2, g.Skipped);
        Assert.True(g.Allow("d", 1500, out var skipped));
        Assert.Equal(2, skipped);
        Assert.Equal(0, g.Skipped);
        Assert.True(g.Allow("b", 2000, out _));
    }

    [Fact]
    public void Log_gate_has_a_cap_a_summary_count_and_a_reset()
    {
        var g = new PaneClicks.LogGate();
        long t = 0;
        for (int i = 0; i < PaneClicks.LogGate.MaxLines; i++) { t += 1000; Assert.True(g.Allow("k" + i, t, out _)); }
        Assert.False(g.Allow("one more", t + 10_000, out _));
        Assert.False(g.Allow("and another", t + 20_000, out _));
        Assert.Equal(2, g.Skipped);   // what the summary line on stop reports
        g.Reset();
        Assert.Equal(0, g.Skipped);
        Assert.True(g.Allow("one more", 1, out _));
        Assert.True(g.Allow("k0", 100_000, out _));
    }
}
