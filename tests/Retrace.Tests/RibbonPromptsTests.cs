using Retrace.Core;

namespace Retrace.Tests;

public class RibbonPromptsTests
{
    [Theory]
    [InlineData("RETRACESETTINGS", "^C^C_RETRACESETTINGS ", "commandLine", "^C^C_-RETRACESETTINGS ")]
    [InlineData("RETRACESETTINGS", "^C^C_RETRACESETTINGS ", "ui", "^C^C_RETRACESETTINGS ")]
    [InlineData("RETRACETDOC", "^C^C_RETRACETDOC ", "commandLine", "^C^C_-RETRACETDOC ")]
    [InlineData("RETRACEMAP", "^C^C_RETRACEMAP ", "commandLine", "^C^C_-RETRACEMAP ")]
    [InlineData("RETRACEMAP", "^C^C_RETRACEMAP ", "UI", "^C^C_RETRACEMAP ")]
    [InlineData("RETRACEMAP", "^C^C_RETRACEMAP ", "", "^C^C_-RETRACEMAP ")]
    [InlineData("RETRACEMAP", "^C^C_RETRACEMAP ", null, "^C^C_-RETRACEMAP ")]
    [InlineData("RETRACESTOP", "^C^C_RETRACESTOP ", "commandLine", "^C^C_-RETRACESTOP ")]
    [InlineData("RETRACESTOP", "^C^C_RETRACESTOP ", "ui", "^C^C_RETRACESTOP ")]
    [InlineData("RETRACESTATUS", "^C^C_RETRACESTATUS ", "commandLine", "^C^C_RETRACESTATUS ")]
    [InlineData("RETRACEMAP", "^C^C_RETRACEMAP ", " \"ui\" ", "^C^C_RETRACEMAP ")]
    [InlineData("RETRACEOPEN", "^C^C_RETRACEOPEN ", "commandLine", "^C^C_RETRACEOPEN ")]
    [InlineData("RETRACEFOLDER_TESTS", "^C^C_RETRACEFOLDER Tests ", "commandLine", "^C^C_RETRACEFOLDER Tests ")]
    [InlineData("RETRACERUN", "^C^C_RETRACERUN ", "commandLine", "^C^C_RETRACERUN ")]
    [InlineData("RETRACESETTINGS", "^C^C_-RETRACESETTINGS ", "commandLine", "^C^C_-RETRACESETTINGS ")]
    [InlineData("RETRACESETTINGS", "^C^C_RETRACESETTINGSX ", "commandLine", "^C^C_RETRACESETTINGSX ")]
    [InlineData(null, "^C^C_RETRACEMAP ", "commandLine", "^C^C_RETRACEMAP ")]
    public void Button_and_style_give_the_macro(string? id, string macro, string? style, string expected)
        => Assert.Equal(expected, RibbonPrompts.Macro(id, macro, style));

    [Fact]
    public void Flip_toggles_and_text_follows_the_style()
    {
        Assert.Equal(PromptStyles.Ui, RibbonPrompts.Flip(PromptStyles.CommandLine));
        Assert.Equal(PromptStyles.Ui, RibbonPrompts.Flip(null));
        Assert.Equal(PromptStyles.CommandLine, RibbonPrompts.Flip("ui"));
        Assert.Contains("Window", RibbonPrompts.ToggleText("ui"));
        Assert.Contains("Command line", RibbonPrompts.ToggleText("commandLine"));
        Assert.Contains("Now: Window", RibbonPrompts.ToggleTip("ui"));
        var cl = RibbonPrompts.ToggleTip("commandLine");
        Assert.Contains("Now: Command line (the default)", cl);
        foreach (var name in new[] { "Settings", "Training doc", "Map", "Stop", "-RETRACESETTINGS", "-RETRACETDOC", "-RETRACEMAP", "-RETRACESTOP" }) Assert.Contains(name, cl);
    }

    [Fact]
    public void Style_test_is_the_settings_one()
    {
        foreach (var v in new string?[] { "ui", "UI", " ui ", "\"ui\"", "commandLine", "", null, "window" })
            Assert.Equal(new RetraceSettings { PromptStyle = v! }.UiPrompts, RibbonPrompts.IsWindow(v));
        Assert.True(RibbonPrompts.IsPaired("RETRACESTOP"));
        Assert.False(RibbonPrompts.IsPaired("RETRACERECORD"));
        Assert.False(RibbonPrompts.IsPaired(null));
    }
}
