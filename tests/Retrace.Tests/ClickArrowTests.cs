using Retrace.Core;

namespace Retrace.Tests;

/// <summary>The click arrow: geometry on the screen and in a picture, the pixels, and the setting. Plus the name of an icon-only button (the fallback order and how a replay finds it).</summary>
public class ClickArrowTests
{
    static readonly PxRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void Arrow_comes_in_from_the_upper_left_at_45_degrees_and_is_about_80_long()
    {
        var a = ClickArrow.For(500, 400, Screen);
        Assert.Equal(new ArrowLine(443, 343, 500, 400), a);
        Assert.Equal(a.TipX - a.StartX, a.TipY - a.StartY);
        var len = Math.Sqrt(Math.Pow(a.TipX - a.StartX, 2) + Math.Pow(a.TipY - a.StartY, 2));
        Assert.InRange(len, 78, 82);
    }

    [Fact]
    public void Near_the_left_edge_the_arrow_comes_from_the_right_and_near_the_top_from_below()
    {
        var left = ClickArrow.For(20, 400, Screen);
        Assert.True(left.StartX > left.TipX);
        Assert.True(left.StartY < left.TipY);
        var top = ClickArrow.For(500, 10, Screen);
        Assert.True(top.StartX < top.TipX);
        Assert.True(top.StartY > top.TipY);
        var corner = ClickArrow.For(5, 5, Screen);
        Assert.True(corner.StartX > corner.TipX && corner.StartY > corner.TipY);
    }

    [Fact]
    public void Arrow_stays_inside_the_area_even_at_the_far_corner_and_for_a_point_outside()
    {
        foreach (var (x, y) in new[] { (1919, 1079), (0, 0), (5000, 5000), (-50, -50), (1919, 0), (0, 1079) })
        {
            var a = ClickArrow.For(x, y, Screen);
            Assert.True(Screen.Contains(a.StartX, a.StartY), $"start {a} for {x},{y}");
            Assert.True(Screen.Contains(a.TipX, a.TipY), $"tip {a} for {x},{y}");
        }
        var tiny = ClickArrow.For(10, 10, new PxRect(0, 0, 20, 20));
        Assert.True(new PxRect(0, 0, 20, 20).Contains(tiny.StartX, tiny.StartY));
    }

    [Fact]
    public void Arrow_uses_the_monitor_that_holds_the_point_and_flips_at_that_monitors_edge()
    {
        var monitors = new[] { new PxRect(0, 0, 1920, 1080), new PxRect(1920, 0, 3840, 1080), new PxRect(-1280, 200, 0, 1000) };
        Assert.Equal(monitors[1], ClickArrow.MonitorOf(2000, 500, monitors));
        Assert.Equal(monitors[2], ClickArrow.MonitorOf(-100, 300, monitors));
        Assert.Equal(monitors[1], ClickArrow.MonitorOf(4000, 500, monitors));   // outside all: the nearest
        Assert.Null(ClickArrow.MonitorOf(1, 1, Array.Empty<PxRect>()));
        // 40 px into the second monitor: the tail would reach the first, so it flips to the right of the point
        var a = ClickArrow.ForScreen(1960, 500, monitors)!.Value;
        Assert.Equal(1960, a.TipX);
        Assert.True(a.StartX > a.TipX);
        Assert.True(monitors[1].Contains(a.StartX, a.StartY));
        // well inside the second monitor: from the upper left, as everywhere
        var b = ClickArrow.ForScreen(2500, 500, monitors)!.Value;
        Assert.True(b.StartX < b.TipX && b.StartY < b.TipY);
        Assert.Null(ClickArrow.ForScreen(1, 1, Array.Empty<PxRect>()));
    }

    [Fact]
    public void Image_arrow_translates_the_screen_point_into_the_pictures_pixels()
    {
        // the picture shows the screen rectangle (1000,500)-(2000,1000) at half size
        var a = ClickArrow.ForImage(1600, 700, new PxRect(1000, 500, 2000, 1000), 500, 250)!.Value;
        Assert.Equal(300, a.TipX);
        Assert.Equal(100, a.TipY);
        Assert.True(a.StartX < a.TipX && a.StartY < a.TipY);
        // same size: the offset only
        var b = ClickArrow.ForImage(1400, 900, new PxRect(1000, 500, 2000, 1000), 1000, 500)!.Value;
        Assert.Equal((400, 400), (b.TipX, b.TipY));
    }

    [Fact]
    public void Image_arrow_is_clamped_to_the_picture_and_flips_at_its_edges()
    {
        var src = new PxRect(100, 100, 600, 400);
        // a click outside the picture ends on its edge
        var outside = ClickArrow.ForImage(5000, 5000, src, 500, 300)!.Value;
        Assert.Equal((499, 299), (outside.TipX, outside.TipY));
        var before = ClickArrow.ForImage(-10, -10, src, 500, 300)!.Value;
        Assert.Equal((0, 0), (before.TipX, before.TipY));
        Assert.True(before.StartX > 0 && before.StartY > 0);
        // a click at the picture's left edge: the tail goes to the right, inside the picture
        var edge = ClickArrow.ForImage(105, 250, src, 500, 300)!.Value;
        Assert.True(edge.StartX > edge.TipX);
        foreach (var arrow in new[] { outside, before, edge })
        {
            Assert.InRange(arrow.StartX, 0, 499); Assert.InRange(arrow.StartY, 0, 299);
        }
        Assert.Null(ClickArrow.ForImage(1, 1, new PxRect(0, 0, 0, 0), 10, 10));
        Assert.Null(ClickArrow.ForImage(1, 1, src, 0, 10));
    }

    [Fact]
    public void Image_arrow_of_a_small_picture_is_shorter_but_never_below_the_minimum_for_a_big_one()
    {
        var small = ClickArrow.ForImage(100, 100, new PxRect(0, 0, 200, 200), 100, 100)!.Value;
        Assert.InRange(Math.Abs(small.TipX - small.StartX), 1, 40);
        var big = ClickArrow.ForImage(400, 400, new PxRect(0, 0, 800, 800), 800, 800)!.Value;
        Assert.InRange(Math.Abs(big.TipX - big.StartX), 55, 58);
    }

    [Fact]
    public void DrawArrow_paints_green_at_the_tip_and_along_the_shaft_and_nothing_far_away()
    {
        int w = 200, h = 200;
        var px = new byte[w * h * 4];
        Annotate.DrawArrow(px, w, h, new ArrowLine(40, 40, 120, 120));
        bool Green(int x, int y) { int i = (y * w + x) * 4; return px[i] == 0x4C && px[i + 1] == 0xB1 && px[i + 2] == 0x22 && px[i + 3] == 255; }
        Assert.True(Green(118, 118) || Green(117, 117) || Green(119, 119), "head near the tip");
        Assert.True(Green(60, 60), "shaft");
        Assert.True(Green(80, 80), "shaft");
        Assert.Equal(0, px[(190 * w + 10) * 4 + 3]);   // far from the arrow: untouched
        Assert.Equal(0, px[(10 * w + 190) * 4 + 3]);
    }

    [Fact]
    public void DrawArrow_clips_to_the_picture_and_ignores_bad_input()
    {
        var px = new byte[50 * 50 * 4];
        Annotate.DrawArrow(px, 50, 50, new ArrowLine(-30, -30, 60, 60));   // reaches outside the picture
        Annotate.DrawArrow(px, 50, 50, new ArrowLine(10, 10, 10, 10));     // no length
        Annotate.DrawArrow(new byte[4], 50, 50, new ArrowLine(0, 0, 10, 10));   // buffer too small
        Annotate.DrawArrow(null!, 50, 50, new ArrowLine(0, 0, 10, 10));
        Assert.Contains(px, b => b == 255);
    }

    [Fact]
    public void Setting_is_off_by_default_and_round_trips()
    {
        Assert.False(new RetraceSettings().ShowClickArrows);
        var root = Path.Combine(Path.GetTempPath(), "retrace-arrows-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new RetraceSettings { RootDir = root, ShowClickArrows = true };
            Assert.True(s.Save(out var err), err);
            Assert.Contains("\"showClickArrows\": true", File.ReadAllText(Paths.SettingsFile(root)));
            Assert.True(RetraceSettings.Load(root).ShowClickArrows);
            Assert.True(s.Clone().ShowClickArrows);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // ---- the name of an icon-only button -----------------------------------------------------------------------------

    static Func<string?>? V(string? s) => () => s;

    [Fact]
    public void Button_name_takes_the_first_source_that_has_one_in_a_fixed_order()
    {
        Assert.Equal(("Save", "text"), ButtonNames.Pick(V("&Save"), V("tip"), V("acc"), V("auto"), V("btnSave")));
        Assert.Equal(("Open the file", "tooltip"), ButtonNames.Pick(V(""), V("Open the file\r\nCtrl+O"), V("acc"), V("auto"), V("btnOpen")));
        Assert.Equal(("acc", "accessible"), ButtonNames.Pick(V(" "), V(null), V("acc"), V("auto"), V("btn")));
        Assert.Equal(("auto", "automation"), ButtonNames.Pick(V(null), V(null), V(null), V("auto"), V("btn")));
        Assert.Equal(("btnRefresh", "name"), ButtonNames.Pick(V(""), V(""), V(""), V(null), V("btnRefresh")));
        Assert.Equal(("(button)", ""), ButtonNames.Pick(V(""), V(""), null, null, V("")));
    }

    [Fact]
    public void Button_name_asks_a_source_only_when_the_ones_before_it_gave_nothing_and_survives_a_throwing_source()
    {
        int asked = 0;
        Func<string?> counting = () => { asked++; return "x"; };
        Assert.Equal(("Add", "text"), ButtonNames.Pick(V("Add"), counting, counting, counting, counting));
        Assert.Equal(0, asked);
        Func<string?> boom = () => throw new InvalidOperationException();
        Assert.Equal(("n", "name"), ButtonNames.Pick(boom, boom, boom, boom, V("n")));
    }

    [Fact]
    public void Button_name_is_one_short_line_and_buttonFrom_is_written_only_for_the_fallback_sources()
    {
        Assert.Equal("Line one", ButtonNames.Clean("Line one\nline two"));
        Assert.Equal(80, ButtonNames.Clean(new string('a', 200)).Length);
        Assert.Null(ButtonNames.FromToRecord("text"));
        Assert.Null(ButtonNames.FromToRecord(""));
        Assert.Equal("tooltip", ButtonNames.FromToRecord("tooltip"));
        Assert.False(ButtonNames.IsFallback("text"));
        Assert.False(ButtonNames.IsFallback(null));
        Assert.True(ButtonNames.IsFallback("name"));
    }

    // a palette: Save (text), two icon buttons with the tool tip "Refresh" (no text), Close (text), "Refresh" (a text button named like the tool tip), an icon button with only a Name
    sealed record B(string Legacy, string Own, string? Tip, string? Name);
    static readonly List<B> Live = new()
    {
        new("Save", "Save", null, null), new("(button)", "", "Refresh", "btnA"), new("(button)", "", "Refresh", "btnB"),
        new("Close", "Close", null, null), new("Refresh", "Refresh", null, null), new("(button)", "", null, "btnC"),
    };
    static string? Src(B b, string from) => from == "tooltip" ? b.Tip : from == "name" ? b.Name : null;
    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static int Find(string recorded, string? from, int index) => ButtonNames.Find(Live, recorded, from, index, b => b.Legacy, b => b.Own.Length > 0, Src, Same);

    [Fact]
    public void A_recording_without_buttonFrom_matches_only_by_the_legacy_name_so_a_fallback_name_never_collides_with_a_text_button()
    {
        Assert.Equal(4, Find("Refresh", null, 1));      // the button whose own text is Refresh, not the icon buttons whose tool tip is
        Assert.Equal(-1, Find("Refresh", null, 2));
        Assert.Equal(0, Find("save", null, 1));
        // an old "(button)" recording: the Nth button the old code called "(button)", whatever tool tips they have now
        Assert.Equal(1, Find("(button)", null, 1));
        Assert.Equal(2, Find("(button)", null, 2));
        Assert.Equal(5, Find("(button)", null, 3));
    }

    [Fact]
    public void A_recording_with_buttonFrom_matches_only_buttons_without_text_by_that_source_and_index_counts_among_them()
    {
        Assert.Equal(1, Find("Refresh", "tooltip", 1));
        Assert.Equal(2, Find("refresh", "tooltip", 2));
        Assert.Equal(-1, Find("Refresh", "tooltip", 3));   // the text button called Refresh is not one of them
        Assert.Equal(5, Find("btnC", "name", 1));
        Assert.Equal(-1, Find("Save", "tooltip", 1));      // a text button is never matched by a fallback name
        Assert.Equal(2, ButtonNames.IndexAmong(Live, 2, "Refresh", "tooltip", b => b.Legacy, b => b.Own.Length > 0, Src, Same));
        Assert.Equal(1, ButtonNames.IndexAmong(Live, 4, "Refresh", null, b => b.Legacy, b => b.Own.Length > 0, Src, Same));
    }

    [Fact]
    public void Legacy_index_counts_the_buttons_without_text_and_finds_a_button_by_position_when_its_name_changed()
    {
        Assert.Equal(1, ButtonNames.LegacyIndex(Live, 1, b => b.Own.Length > 0));
        Assert.Equal(2, ButtonNames.LegacyIndex(Live, 2, b => b.Own.Length > 0));
        Assert.Equal(3, ButtonNames.LegacyIndex(Live, 5, b => b.Own.Length > 0));
        Assert.Equal(2, ButtonNames.FindByLegacyIndex(Live, 2, b => b.Own.Length > 0));
        Assert.Equal(-1, ButtonNames.FindByLegacyIndex(Live, 4, b => b.Own.Length > 0));
        Assert.Equal(-1, ButtonNames.FindByLegacyIndex(Live, 0, b => b.Own.Length > 0));
        // the tool tip depends on state ("Show" -> "Hide"): the name finds nothing, the position does
        Assert.Equal(-1, Find("Show details", "tooltip", 1));
        var note = ButtonNames.PositionNote("Show details", "Hide details", 2);
        Assert.Contains("by position", note);
        Assert.Contains("Hide details", note);
        Assert.Contains("Show details", note);
    }

    [Fact]
    public void Palette_step_reads_buttonFrom_and_legacyIndex_and_shows_the_name()
    {
        var lines = new[]
        {
            "{\"t\":0,\"n\":1,\"type\":\"palette_values\",\"palette\":\"Tools\",\"set\":\"Kit\",\"source\":\"win32\",\"fields\":[],\"read\":0}",
            "{\"t\":50,\"n\":2,\"type\":\"palette_click\",\"palette\":\"Tools\",\"set\":\"Kit\",\"button\":\"Refresh list\",\"buttonFrom\":\"tooltip\",\"index\":2,\"legacyIndex\":3}",
            "{\"t\":90,\"n\":3,\"type\":\"palette_click\",\"palette\":\"Tools\",\"set\":\"Kit\",\"button\":\"(button)\"}",
        };
        var steps = Plan.Build(lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.Clone()));
        var first = steps.First(s => s.Kind == "palette");
        Assert.Equal("Refresh list", first.Button);
        Assert.Equal("tooltip", first.ButtonFrom);
        Assert.Equal(2, first.ButtonIndex);
        Assert.Equal(3, first.ButtonLegacyIndex);
        Assert.Contains("click Refresh list", first.Title);
        var old = steps.Last(s => s.Kind == "palette");
        Assert.Null(old.ButtonFrom);
        Assert.Equal(0, old.ButtonLegacyIndex);
        Assert.Equal("(button)", old.Button);
    }

    [Fact]
    public void A_click_that_confirms_a_dialog_by_key_has_no_point_and_the_wpf_correlator_tells_a_mouse_press_from_a_key()
    {
        var c = new WpfClickCorrelator();
        var ok = new object();
        c.MouseDown(ok);
        Assert.True(c.IsMousePress(ok, 100));
        Assert.True(c.IsPress(ok, 100));
        var other = new WpfClickCorrelator();
        other.MouseDown(ok);
        other.KeyDown(120);
        Assert.False(other.IsMousePress(ok, 130));   // a key is in play: not a mouse press
        Assert.True(other.IsPress(ok, 130));
        Assert.False(new WpfClickCorrelator().IsMousePress(ok, 5));
    }

    [Fact]
    public void Position_is_used_only_when_the_palette_has_as_many_buttons_without_text_as_when_recorded()
    {
        Assert.True(ButtonNames.PositionAllowed(2, 4, 4));
        Assert.False(ButtonNames.PositionAllowed(2, 4, 5));    // a button was added: the position means something else
        Assert.False(ButtonNames.PositionAllowed(2, 4, 3));
        Assert.False(ButtonNames.PositionAllowed(2, 0, 4));    // an older recording has no count
        Assert.False(ButtonNames.PositionAllowed(0, 4, 4));
        Assert.False(ButtonNames.PositionAllowed(5, 4, 4));
    }

    [Fact]
    public void Palette_step_reads_noTextCount()
    {
        var lines = new[]
        {
            "{\"t\":0,\"n\":1,\"type\":\"palette_values\",\"palette\":\"Tools\",\"set\":\"Kit\",\"source\":\"win32\",\"fields\":[],\"read\":0}",
            "{\"t\":50,\"n\":2,\"type\":\"palette_click\",\"palette\":\"Tools\",\"set\":\"Kit\",\"button\":\"Refresh\",\"buttonFrom\":\"tooltip\",\"legacyIndex\":2,\"noTextCount\":4}",
        };
        var steps = Plan.Build(lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.Clone()));
        Assert.Equal(4, steps.First(x => x.Kind == "palette").ButtonNoTextCount);
    }

    [Fact]
    public void A_mouse_press_after_a_key_outranks_it_and_the_two_questions_share_one_rule()
    {
        var c = new WpfClickCorrelator();
        var ok = new object();
        c.KeyDown(10); c.KeyUp(20);
        c.MouseDown(ok);                                  // later than the key
        Assert.True(c.IsMousePress(ok, 100));
        Assert.True(c.IsPress(ok, 100));
        var k = new WpfClickCorrelator();
        k.MouseDown(ok); k.MouseUp(10);
        k.KeyDown(50);                                    // a key after the mouse: the key decides
        Assert.False(k.IsMousePress(ok, 60));
        Assert.True(k.IsPress(ok, 60));
        var other = new WpfClickCorrelator();
        other.MouseDown(ok);
        Assert.False(other.IsMousePress(new object(), 5));
        Assert.False(other.IsPress(new object(), 5));
    }
}
