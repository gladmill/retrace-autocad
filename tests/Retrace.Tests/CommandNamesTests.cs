using System.Text.RegularExpressions;
using Retrace.Engine.Recordings;
using Retrace.Engine.Scan.CodeRibbon;

namespace Retrace.Tests;

/// <summary>The command names the plugin registers and the ribbon runs (read from the source: AutoCad/ is not part of this assembly),
/// and what counts as the plugin's own command.</summary>
public class CommandNamesTests
{
    static readonly string[] Expected =
    {
        "-RETRACEMARK", "-RETRACESETTINGS", "RETRACECAPTUREDCL", "RETRACECAPTURERIBBON", "RETRACECHECKPOINTS", "RETRACECLOSEALL", "RETRACEDOCS", "RETRACEFOLDER", "RETRACEGUIDE", "RETRACEMARK", "RETRACEOPEN",
        "RETRACERECORD", "RETRACEREPLAY", "RETRACERUN", "RETRACESCR", "RETRACESETTINGS", "RETRACESTATUS", "RETRACESTEP", "RETRACESTEPS", "RETRACESTOP",
    };

    static string Source(string file) => File.ReadAllText(Path.Combine(LayerRulesTests.SourceRoot(), "AutoCad", file));

    static string SamplesTabPath() => Path.Combine(LayerRulesTests.SourceRoot(), "..", "..", "samples", "Retrace.Samples", "SamplesTab.cs");

    /// <summary>What Build docs reads from these files: the code-ribbon scanner's tabs.</summary>
    static List<RibbonTab> ScanTabs(params string[] paths) => CodeRibbonReader.Read(paths.Select(p => new SourceFile
    {
        AbsPath = p, RelPath = Path.GetFileName(p), Lang = Lang.CSharp, Project = "Retrace", Text = File.ReadAllText(p),
    }).ToList());

    static List<RibbonItem> Buttons(RibbonTab tab) => Linker.Walk(tab).Select(x => x.item).Where(i => i.Kind == RibbonItemKind.Button).ToList();

    [Fact]
    public void Registered_commands_are_exactly_the_documented_list()
    {
        var names = Regex.Matches(Source("RetraceApp.cs"), @"\[CommandMethod\(""([^""]+)""").Select(m => m.Groups[1].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(Expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);
        Assert.All(names, n => Assert.StartsWith("RETRACE", n.TrimStart('-')));
    }

    [Fact]
    public void Ribbon_buttons_run_registered_commands_and_have_plain_labels()
    {
        var tab = Source("RetraceTab.cs");
        var buttons = Regex.Matches(tab, @"\bButton\(""([^""]+)"", ""([^""]+)"", ""[a-z]+"", ""\^C\^C_([A-Z]+)( [A-Za-z]+)? "", ""[^""]*""\)").ToList();
        Assert.Equal(17, buttons.Count);
        Assert.All(buttons, b =>
        {
            Assert.Contains(b.Groups[3].Value, Expected);               // the macro runs a registered command
            Assert.StartsWith(b.Groups[3].Value, b.Groups[1].Value);    // and the button id starts with it
            Assert.True(OwnCommands.IsOwnCommand(b.Groups[1].Value));
        });
        Assert.Equal(buttons.Count, buttons.Select(b => b.Groups[1].Value).Distinct().Count());   // ids are unique
        var labels = buttons.Select(b => b.Groups[2].Value).ToList();
        Assert.Equal(
            new[] { "Record", "Checkpoint", "Stop", "Name checkpoints", "Steps", "Status", "Run test", "Step", "Guide", "Build docs", "Settings", "Open test", "Tests", "Captures", "Docs", "Capture DCL dialogs", "Close all drawings" },
            labels);
        // the folder buttons pass the keyword the command asks for
        Assert.Contains(buttons, b => b.Groups[3].Value == "RETRACEFOLDER" && b.Groups[4].Value == " Tests");
        Assert.Contains(buttons, b => b.Groups[3].Value == "RETRACEFOLDER" && b.Groups[4].Value == " Captures");
        Assert.Contains(buttons, b => b.Groups[3].Value == "RETRACEFOLDER" && b.Groups[4].Value == " Docs");
    }

    [Fact]
    public void Both_ribbon_tabs_are_readable_by_the_code_ribbon_scanner()
    {
        var root = Path.Combine(LayerRulesTests.SourceRoot(), "AutoCad");
        var own = Assert.Single(ScanTabs(Path.Combine(root, "RetraceTab.cs"), Path.Combine(root, "RibbonCommandHandler.cs")));
        Assert.Equal(OwnCommands.TabId, own.Id);
        Assert.Equal("Retrace", own.Title);
        Assert.Equal(new[] { "Record", "Test", "Guide", "Open", "Tools" }, own.Panels.Select(p => p.Title));
        Assert.Equal(new[] { 6, 2, 3, 4, 2 }, own.Panels.Select(p => Linker.Walk(own).Count(x => x.panel == p && x.item.Kind == RibbonItemKind.Button)));
        var buttons = Buttons(own);
        Assert.Equal(
            new[] { "Record", "Checkpoint", "Stop", "Name checkpoints", "Steps", "Status", "Run test", "Step", "Guide", "Build docs", "Settings", "Open test", "Tests", "Captures", "Docs", "Capture DCL dialogs", "Close all drawings" },
            buttons.Select(b => b.Text));
        Assert.All(buttons, b => Assert.Equal(b.Id.ToUpperInvariant(), b.Command));   // the handler sends a command

        var samples = Assert.Single(ScanTabs(SamplesTabPath()));
        Assert.Equal("SAMPLES_TAB", samples.Id);
        Assert.Equal("Retrace Samples", samples.Title);
        Assert.Equal(new[] { "Draw", "Dialogs" }, samples.Panels.Select(p => p.Title));
        Assert.Equal(new[] { "DRAWLINE", "DRAWCIRCLE", "DRAWRECT", "DRAWLABEL", "DRAWGRID" }, Buttons(samples).Select(b => b.Command));
        Assert.Equal(new[] { "Line", "Circle", "Rectangle", "Label", "Grid..." }, Buttons(samples).Select(b => b.Text));
    }

    [Theory]
    [InlineData("RETRACERECORD", true)]
    [InlineData("retracestop", true)]
    [InlineData("RETRACEFOLDER_TESTS", true)]
    [InlineData("'_RETRACESTATUS", true)]
    [InlineData("'RETRACESTATUS", true)]
    [InlineData("DRAWLINE", false)]
    [InlineData("DRAWGRID", false)]
    [InlineData("LINE", false)]
    [InlineData("SAMPLES_TAB", false)]
    [InlineData(null, false)]
    public void Own_commands_are_the_RETRACE_prefix(string? name, bool own) => Assert.Equal(own, OwnCommands.IsOwnCommand(name));

    [Fact]
    public void The_sample_plugin_is_not_filtered_out_as_the_plugins_own()
    {
        Assert.True(OwnCommands.IsOwnTab("RETRACE_TAB"));
        var tabId = Assert.Single(ScanTabs(SamplesTabPath())).Id;   // the Id the tab is created with
        Assert.NotEmpty(tabId);
        Assert.False(OwnCommands.IsOwnTab(tabId));
        Assert.False(OwnCommands.IsOwnCommand(tabId));
        // a ribbon click of the samples tab is a step of a test, a click on the Retrace tab is not
        Assert.True(Retrace.Core.Plan.IsOwnRibbon("RETRACESTOP", "RETRACE_TAB"));
        Assert.False(Retrace.Core.Plan.IsOwnRibbon("DRAWLINE", tabId));
    }

    [Fact]
    public void Command_names_in_messages_exist()
    {
        // every RETRACE... word in the plugin's user-facing text is a registered command (or the ribbon tab / env var)
        var known = Expected.Select(e => e.TrimStart('-')).ToHashSet(StringComparer.Ordinal);
        known.UnionWith(new[] { "RETRACE_TAB", "RETRACE_AUTOSTART" });
        var bad = new List<string>();
        foreach (var f in Directory.GetFiles(LayerRulesTests.SourceRoot(), "*.cs", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\bRETRACE[A-Z_]*\b"))
                if (!known.Contains(m.Value) && !m.Value.StartsWith("RETRACE_FOLDER", StringComparison.Ordinal) && m.Value != "RETRACEFOLDER_TESTS" && m.Value != "RETRACEFOLDER_CAPTURES" && m.Value != "RETRACEFOLDER_DOCS" && m.Value != "RETRACE")
                    bad.Add($"{Path.GetFileName(f)}: {m.Value}");
        Assert.Empty(bad.Distinct());
    }
}
