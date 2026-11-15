using System.Text.Json;
using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

public class PostTestZoomTests
{
    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("none", "none")]
    [InlineData("n", "none")]
    [InlineData("No", "none")]
    [InlineData("off", "none")]
    [InlineData("0", "none")]
    [InlineData("e", "extents")]
    [InlineData("l", "lastEntity")]
    [InlineData("zoom extents", "extents")]
    [InlineData("last_object", "lastEntity")]
    [InlineData("bogus", "none")]
    [InlineData("extents", "extents")]
    [InlineData(" \"Extents\" ", "extents")]
    [InlineData("1", "extents")]
    [InlineData("lastEntity", "lastEntity")]
    [InlineData("LASTENTITY", "lastEntity")]
    [InlineData("last", "lastEntity")]
    [InlineData("2", "lastEntity")]
    public void Normalize_reads_known_values_and_falls_back_to_none(string? text, string expected) =>
        Assert.Equal(expected, PostTestZoomModes.Normalize(text));

    [Theory]
    [InlineData("n", true)] [InlineData("none", true)] [InlineData("E", true)] [InlineData("last", true)] [InlineData("bogus", false)] [InlineData("3", false)]
    public void Normalize_says_whether_the_text_is_known(string text, bool known)
    {
        PostTestZoomModes.Normalize(text, out var k);
        Assert.Equal(known, k);
    }

    [Fact]
    public void Setting_defaults_to_none_and_reads_tolerantly()
    {
        Assert.Equal("none", new RetraceSettings().PostTestZoom);
        var s = JsonSerializer.Deserialize<RetraceSettings>("{\"postTestZoom\":\"Last\"}")!;
        Assert.Equal("lastEntity", s.PostTestZoom);
        Assert.Equal("none", JsonSerializer.Deserialize<RetraceSettings>("{\"postTestZoom\":\"sideways\"}")!.PostTestZoom);
        Assert.Equal("none", JsonSerializer.Deserialize<RetraceSettings>("{}")!.PostTestZoom);
    }

    [Fact]
    public void Fit_adds_margin_and_keeps_the_viewport_shape()
    {
        var w = ZoomWindow.Fit(0, 0, 100, 50, aspect: 2, margin: 0.1, minSize: 1)!.Value;
        Assert.Equal(50, w.CenterX, 6); Assert.Equal(25, w.CenterY, 6);
        Assert.Equal(140, w.Width, 6); Assert.Equal(70, w.Height, 6);
    }

    [Fact]
    public void Fit_grows_the_short_side_for_a_tall_box()
    {
        var w = ZoomWindow.Fit(0, 0, 10, 100, aspect: 2, margin: 0, minSize: 1)!.Value;
        Assert.Equal(100, w.Height, 6); Assert.Equal(200, w.Width, 6); Assert.Equal(5, w.CenterX, 6);
    }

    [Fact]
    public void Fit_gives_a_point_the_minimum_window()
    {
        var w = ZoomWindow.Fit(5, 5, 5, 5, aspect: 1.5, margin: 0.15, minSize: 20)!.Value;
        Assert.Equal(30, w.Width, 6); Assert.Equal(20, w.Height, 6);
        Assert.Equal(5, w.CenterX, 6); Assert.Equal(5, w.CenterY, 6);
    }

    [Theory]
    [InlineData(1e20, 1e20, -1e20, -1e20)]   // the extents of an empty drawing
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, 0, double.PositiveInfinity, 1)]
    [InlineData(2, 0, 1, 1)]
    public void Fit_refuses_a_degenerate_box(double x0, double y0, double x1, double y1) =>
        Assert.Null(ZoomWindow.Fit(x0, y0, x1, y1, 1.5, 0.1, 1));

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)]
    public void Fit_refuses_a_bad_aspect(double aspect) => Assert.Null(ZoomWindow.Fit(0, 0, 1, 1, aspect, 0.1, 1));
}
