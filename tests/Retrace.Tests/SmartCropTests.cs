using System.Text.Json;
using Xunit;

namespace Retrace.Tests;

public class SmartCropTests
{
    // a 1920 x 1000 AutoCAD window at (0,0): tab row at y 60..84, panels at y 84..190
    static readonly PxRect Window = new(0, 0, 1920, 1000);
    static readonly PxRect RibbonBar = new(0, 50, 1920, 195);

    static List<PxRect> Row(int count, int width = 80, int left = 100)
        => Enumerable.Range(0, count).Select(i => new PxRect(left + i * (width + 4), 60, left + i * (width + 4) + width, 84)).ToList();

    static CropRects Ribbon(List<PxRect> row, int active, PxRect item, PxRect panel) => new(Window, item, panel, row, row[active], RibbonBar);

    static void AssertInside(PxRect inner, PxRect outer)
    {
        Assert.True(inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom, $"{inner} not inside {outer}");
    }

    [Fact]
    public void Ribbon_crop_holds_the_item_the_panel_the_active_tab_and_two_neighbours_each_side()
    {
        var row = Row(12);
        var item = new PxRect(520, 100, 560, 150);
        var panel = new PxRect(480, 86, 700, 192);
        var d = SmartCrop.Decide(PictureKind.Ribbon, Ribbon(row, 5, item, panel));
        Assert.Equal("ribbon", d.Reason);
        var r = d.Rect;
        foreach (var need in new[] { item, panel, row[3], row[4], row[5], row[6], row[7] }) AssertInside(need, r);
        Assert.True(r.Width >= SmartCrop.MinWidth && r.Height >= SmartCrop.MinHeight);
        AssertInside(r, Window);
        Assert.True(r.Left <= item.Left - SmartCrop.Margin || r.Left == 0);
    }

    [Fact]
    public void The_crop_is_a_true_size_rectangle_no_larger_than_needed_plus_the_minimum()
    {
        var row = Row(12);
        var d = SmartCrop.Decide(PictureKind.Ribbon, Ribbon(row, 5, new PxRect(520, 100, 560, 150), new PxRect(480, 86, 700, 192)));
        Assert.True(d.Rect.Width < 900);   // never the 1920 px window
    }

    [Fact]
    public void A_button_far_to_the_right_of_its_tab_name_widens_the_crop_to_both()
    {
        var row = Row(6);   // tab 1 at x 184..264
        var item = new PxRect(1500, 100, 1560, 150);
        var panel = new PxRect(1400, 86, 1700, 192);
        var d = SmartCrop.Decide(PictureKind.Ribbon, Ribbon(row, 1, item, panel));
        Assert.Equal("ribbon", d.Reason);
        Assert.True(d.Rect.Left <= row[0].Left && d.Rect.Right >= panel.Right);
        Assert.True(d.Rect.Width > 1400);
    }

    [Fact]
    public void A_tab_at_the_left_edge_has_no_left_neighbours_and_the_crop_stays_in_the_window()
    {
        var row = Row(8, left: 4);
        var d = SmartCrop.Decide(PictureKind.Ribbon, Ribbon(row, 0, new PxRect(10, 100, 50, 150), new PxRect(4, 86, 200, 192)));
        AssertInside(d.Rect, Window);
        Assert.Equal(0, d.Rect.Left);
        AssertInside(row[2], d.Rect);
    }

    [Fact]
    public void A_tab_at_the_right_edge_keeps_the_crop_in_the_window()
    {
        var row = Row(8, width: 230, left: 100).Select(h => h with { Left = h.Left - 60, Right = h.Right - 60 }).ToList();   // the last header ends 8 px from the window edge
        var item = new PxRect(1850, 100, 1910, 150);
        var d = SmartCrop.Decide(PictureKind.Ribbon, Ribbon(row, 7, item, new PxRect(1700, 86, 1915, 192)));
        AssertInside(d.Rect, Window);
        AssertInside(item, d.Rect);
        Assert.Equal(1920, d.Rect.Right);
    }

    [Fact]
    public void A_missing_item_gives_the_whole_ribbon_full_width_never_the_window()
    {
        var row = Row(8);
        var d = SmartCrop.Decide(PictureKind.Ribbon, new CropRects(Window, null, new PxRect(480, 86, 700, 192), row, row[2], RibbonBar));
        Assert.Equal("ribbon-whole", d.Reason);
        Assert.Equal(0, d.Rect.Left);
        Assert.Equal(1920, d.Rect.Right);
        AssertInside(RibbonBar, d.Rect);
        Assert.True(d.Rect.Height < Window.Height);
    }

    [Fact]
    public void Missing_tab_headers_or_an_active_tab_that_is_not_among_them_give_the_whole_ribbon()
    {
        var row = Row(8);
        var item = new PxRect(520, 100, 560, 150); var panel = new PxRect(480, 86, 700, 192);
        Assert.Equal("ribbon-whole", SmartCrop.Decide(PictureKind.Ribbon, new CropRects(Window, item, panel, null, null, RibbonBar)).Reason);
        Assert.Equal("ribbon-whole", SmartCrop.Decide(PictureKind.Ribbon, new CropRects(Window, item, panel, row, null, RibbonBar)).Reason);
        Assert.Equal("ribbon-whole", SmartCrop.Decide(PictureKind.Ribbon, new CropRects(Window, item, panel, row, new PxRect(1, 2, 3, 4), RibbonBar)).Reason);
        Assert.Equal("ribbon-whole", SmartCrop.Decide(PictureKind.Ribbon, new CropRects(Window, item, null, row, row[1], RibbonBar)).Reason);
    }

    [Fact]
    public void Nothing_known_about_the_ribbon_gives_the_window_as_before()
    {
        var d = SmartCrop.Decide(PictureKind.Ribbon, new CropRects(Window));
        Assert.True(d.IsWindow);
        Assert.Equal(Window, d.Rect);
    }

    [Fact]
    public void A_palette_crop_is_the_whole_palette_with_a_margin()
    {
        var pal = new PxRect(1500, 200, 1900, 900);   // docked at the right edge
        var d = SmartCrop.Decide(PictureKind.Palette, new CropRects(Window, Palette: pal));
        Assert.Equal("palette", d.Reason);
        AssertInside(pal, d.Rect);
        AssertInside(d.Rect, Window);
        Assert.True(d.Rect.Width >= SmartCrop.MinWidth);
        Assert.Equal(1920, d.Rect.Right);   // no room to the right: the extra comes from the left
        Assert.Equal(pal.Top - SmartCrop.Margin, d.Rect.Top);          // the margin is there on every side that has room
        Assert.Equal(pal.Bottom + SmartCrop.Margin, d.Rect.Bottom);
    }

    [Fact]
    public void A_small_palette_is_padded_to_the_minimum_size_with_real_surroundings()
    {
        var pal = new PxRect(600, 400, 800, 500);
        var d = SmartCrop.Decide(PictureKind.Palette, new CropRects(Window, Palette: pal));
        Assert.Equal(SmartCrop.MinWidth, d.Rect.Width);
        Assert.Equal(SmartCrop.MinHeight, d.Rect.Height);
        AssertInside(pal, d.Rect);
    }

    [Fact]
    public void A_floating_palette_outside_the_window_is_still_whole_in_its_crop()
    {
        var pal = new PxRect(1800, 100, 2200, 600);
        var d = SmartCrop.Decide(PictureKind.Palette, new CropRects(Window, Palette: pal));
        AssertInside(pal, d.Rect);
    }

    [Fact]
    public void A_floating_palette_on_another_monitor_is_padded_inside_that_monitor_not_the_main_window()
    {
        var monitor2 = new PxRect(1920, 0, 3840, 1080);
        var pal = new PxRect(3600, 200, 3830, 700);   // 10 px from the monitor's right edge
        var d = SmartCrop.Decide(PictureKind.Palette, new CropRects(Window, Palette: pal, PaletteMonitor: monitor2));
        AssertInside(pal, d.Rect);
        AssertInside(d.Rect, monitor2);
        Assert.Equal(3840, d.Rect.Right);   // no room to the right: the padding comes from the left, inside the monitor
        Assert.True(d.Rect.Left >= monitor2.Left);
        Assert.Equal(pal.Top - SmartCrop.Margin, d.Rect.Top);
    }

    [Fact]
    public void A_wide_ribbon_crop_starts_one_margin_left_of_the_first_neighbour_tab_and_leaves_the_tab_beyond_out()
    {
        var row = Row(12);   // headers 80 wide, 4 apart from x 100
        var item = new PxRect(1100, 100, 1160, 150);
        var panel = new PxRect(900, 86, 1210, 192);   // wide enough that the minimum size does not pad
        var r = SmartCrop.Decide(PictureKind.Ribbon, Ribbon(row, 5, item, panel)).Rect;
        Assert.Equal(row[3].Left - SmartCrop.Margin, r.Left);
        Assert.True(r.Left >= row[1].Right, "the tab two beyond the neighbours is not in the crop");
        Assert.Equal(panel.Right + SmartCrop.Margin, r.Right);
    }

    [Fact]
    public void An_edge_that_cuts_through_a_tab_name_or_panel_label_moves_outward_to_take_it_whole()
    {
        var row = Row(12);
        var item = new PxRect(1100, 100, 1160, 150);
        var panel = new PxRect(900, 86, 1210, 192);
        var plain = SmartCrop.Decide(PictureKind.Ribbon, Ribbon(row, 5, item, panel)).Rect;   // left edge at row[3].Left - 24 = 328: inside row[2] (268..348)
        Assert.True(plain.Left > row[2].Left && plain.Left < row[2].Right);
        var neighbourPanel = new PxRect(1180, 86, 1400, 192);                                   // right edge (1234) cuts through this panel
        var snapped = SmartCrop.Decide(PictureKind.Ribbon, Ribbon(row, 5, item, panel) with { Snap = row.Concat(new[] { neighbourPanel }).ToList() }).Rect;
        Assert.Equal(row[2].Left, snapped.Left);
        Assert.Equal(neighbourPanel.Right, snapped.Right);
        Assert.Equal(plain.Top, snapped.Top);
        Assert.Equal(plain.Bottom, snapped.Bottom);
    }

    [Fact]
    public void Snapping_never_leaves_the_window_never_moves_far_and_ignores_rectangles_beside_the_crop()
    {
        var crop = new PxRect(300, 0, 900, 360);
        var big = new PxRect(100, 50, 400, 150);                      // 200 px to take in: allowed
        Assert.Equal(100, SmartCrop.SnapEdges(crop, new[] { big }, Window).Left);
        var huge = new PxRect(-200, 50, 400, 150);                    // 500 px: too far, and outside the window
        Assert.Equal(300, SmartCrop.SnapEdges(crop, new[] { huge }, Window).Left);
        var below = new PxRect(100, 400, 400, 500);                   // does not overlap the crop vertically
        Assert.Equal(crop, SmartCrop.SnapEdges(crop, new[] { below }, Window));
        var wideRight = new PxRect(800, 50, 1950, 150);               // would end beyond the window
        Assert.Equal(900, SmartCrop.SnapEdges(crop, new[] { wideRight }, Window).Right);
        Assert.Equal(crop, SmartCrop.SnapEdges(crop, null, Window));
    }

    [Theory]
    [InlineData(true, false, true)]    // window picture left out, no close-up came: take the window picture
    [InlineData(true, true, false)]    // the close-up is the picture
    [InlineData(false, false, false)]  // classic: the window picture was taken by the scheduler
    [InlineData(false, true, false)]
    public void A_smart_palette_step_whose_closeup_is_impossible_gets_the_window_picture(bool suppressed, bool haveCloseup, bool expected)
        => Assert.Equal(expected, ShotRules.NeedsWindowFallback(suppressed, haveCloseup));

    [Fact]
    public void A_palette_that_is_not_known_gives_the_window()
    {
        Assert.True(SmartCrop.Decide(PictureKind.Palette, new CropRects(Window)).IsWindow);
    }

    [Theory]
    [InlineData(PictureKind.Dialog)]
    [InlineData(PictureKind.Drawing)]
    public void Dialog_and_drawing_pictures_stay_the_whole_window(PictureKind kind)
    {
        var d = SmartCrop.Decide(kind, new CropRects(Window, new PxRect(1, 1, 5, 5), new PxRect(1, 1, 50, 50), Row(3), Row(3)[0], RibbonBar, new PxRect(1, 1, 50, 50)));
        Assert.True(d.IsWindow);
        Assert.Equal(Window, d.Rect);
    }

    [Fact]
    public void A_window_smaller_than_the_minimum_is_not_exceeded()
    {
        var small = new PxRect(0, 0, 500, 300);
        var d = SmartCrop.Decide(PictureKind.Palette, new CropRects(small, Palette: new PxRect(10, 10, 100, 100)));
        Assert.Equal(small, d.Rect);
    }

    [Fact]
    public void Window_at_negative_coordinates_second_monitor()
    {
        var w = new PxRect(-1920, 0, 0, 1000);
        var d = SmartCrop.Decide(PictureKind.Palette, new CropRects(w, Palette: new PxRect(-300, 100, -10, 800)));
        AssertInside(d.Rect, w);
        Assert.Equal(0, d.Rect.Right);
    }

    [Theory]
    [InlineData(null, "classic")]
    [InlineData("", "classic")]
    [InlineData("classic", "classic")]
    [InlineData("Smart", "smart")]
    [InlineData(" \"smart zoom\" ", "smart")]
    [InlineData("smart-zoom", "smart")]
    [InlineData("nonsense", "classic")]
    public void The_mode_is_read_leniently(string? text, string expected) => Assert.Equal(expected, ScreenshotModes.Normalize(text));

    [Fact]
    public void Unknown_mode_text_is_reported_as_unknown()
    {
        ScreenshotModes.Normalize("sideways", out var known);
        Assert.False(known);
        ScreenshotModes.Normalize("smart", out known);
        Assert.True(known);
    }

    [Fact]
    public void The_setting_defaults_to_smart_and_reads_leniently_from_a_file()
    {
        Assert.Equal("smart", new RetraceSettings().ScreenshotMode);
        Assert.True(new RetraceSettings().SmartShots);
        Assert.Equal("smart", JsonSerializer.Deserialize<RetraceSettings>("{}")!.ScreenshotMode);   // a file without the key gets the default
        Assert.Equal("classic", JsonSerializer.Deserialize<RetraceSettings>("{\"screenshotMode\":\"classic\"}")!.ScreenshotMode);   // a file that holds classic stays classic
        Assert.False(JsonSerializer.Deserialize<RetraceSettings>("{\"screenshotMode\":\"classic\"}")!.SmartShots);
        var s = JsonSerializer.Deserialize<RetraceSettings>("{\"screenshotMode\":\"SMART\"}")!;
        Assert.Equal("smart", s.ScreenshotMode);
        Assert.True(s.SmartShots);
        Assert.Equal("classic", JsonSerializer.Deserialize<RetraceSettings>("{\"screenshotMode\":\"sideways\"}")!.ScreenshotMode);
        Assert.Contains("\"screenshotMode\": \"smart\"", JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }
}
