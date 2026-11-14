using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

public class RibbonFileDiaTests
{
    [Fact]
    public void On_shows_the_plain_value()
    {
        Assert.Equal("FILEDIA: 1", RibbonFileDia.Text(1));
        Assert.Equal("FILEDIA: 2", RibbonFileDia.Text(2));
        Assert.Equal("filedia", RibbonFileDia.Icon(1));
        Assert.DoesNotContain("off", RibbonFileDia.Text(1));
    }

    [Fact]
    public void Off_warns_in_text_icon_and_tip()
    {
        var text = RibbonFileDia.Text(0);
        Assert.StartsWith("FILEDIA: 0", text);
        Assert.Contains("⚠", text);
        Assert.Contains("file dialogs off", text);
        Assert.Equal("warn", RibbonFileDia.Icon(0));
        var tip = RibbonFileDia.Tip(0);
        Assert.Contains("command line", tip);
        Assert.Contains("Run test", tip);
        Assert.Contains("FILEDIA, then 1", tip);
        Assert.Contains("Click to set FILEDIA to 1", tip);
    }

    [Fact]
    public void On_tip_says_what_a_click_does()
    {
        Assert.Contains("Click to set FILEDIA to 0", RibbonFileDia.Tip(1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    public void Flip_toggles_between_0_and_1(int from, int to) => Assert.Equal(to, RibbonFileDia.Flip(from));
}
