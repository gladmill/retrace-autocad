using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Where the tab of a palette in its palette set is clicked (showDrivenUi), and the tries. Synthetic geometry only.</summary>
public class PaletteTabsTests
{
    [Fact]
    public void A_side_is_the_tab_strip_only_when_its_width_is_plausible_for_one()
    {
        Assert.Equal(266, PaletteTabs.StripX(279, 5, 253));        // client 279 wide, page 5..253: 26 px on the right, 5 on the left
        Assert.Equal(20, PaletteTabs.StripX(279, 40, 270));        // tabs on the left (40 px), 9 px on the right
        Assert.Null(PaletteTabs.StripX(279, 5, 273));              // 6 px each side: no strip (a set with one palette)
        Assert.Null(PaletteTabs.StripX(300, 30, 270));             // 30 px each side: both could be one: no guess
        Assert.Null(PaletteTabs.StripX(400, 5, 300));              // 100 px: a docked panel, not tabs
    }

    [Fact]
    public void The_plausible_width_scales_with_the_dpi()
    {
        Assert.Null(PaletteTabs.StripX(279, 5, 214));                          // 65 px at 100%: too wide
        Assert.NotNull(PaletteTabs.StripX(279, 5, 214, dpi: 192));             // 65 px at 200% (32 at 100%): a strip
        Assert.Null(PaletteTabs.StripX(279, 5, 266, dpi: 192));                // 13 px at 200%: too narrow
    }

    [Fact]
    public void Index_comes_from_the_page_ids_in_the_order_added()
    {
        Assert.Equal(0, PaletteTabs.IndexOf(2, new[] { 7, 6, 5, 4, 3, 2 }));
        Assert.Equal(5, PaletteTabs.IndexOf(7, new[] { 7, 6, 5, 4, 3, 2 }));
        Assert.Equal(-1, PaletteTabs.IndexOf(0, new[] { 0, 0, 0 }));
        Assert.Equal(-1, PaletteTabs.IndexOf(1, new[] { 1 }));
        Assert.Equal(-1, PaletteTabs.IndexOf(9, new[] { 2, 3 }));
    }

    /// <summary>A strip of <paramref name="tabs"/> equal tabs of <paramref name="tabH"/> px starting at <paramref name="first"/>, in a strip of <paramref name="height"/> px; a click outside any tab (or on one the strip cuts off) changes nothing. Returns the clicks made, -1 if the target was never shown.</summary>
    static (int Clicks, TabSearch Search) Run(int count, int target, int front, int height, int first, int tabH, bool reversed = false)
    {
        var s = new TabSearch(count, target, front);
        int shown = front;
        while (Next(s, height) is { } y)
        {
            int slot = (y - first) / tabH;
            int page = y < first || slot >= count || (slot + 1) * tabH + first > height ? -1 : reversed ? count - 1 - slot : slot;
            if (page >= 0) shown = page;
            s.Observe(y, page >= 0 ? shown : -1);
            if (shown == target) return (s.Clicks, s);
        }
        return (-1, s);
    }

    static int? Next(TabSearch s, int height) => s.Next(height);

    [Theory]
    [InlineData(6, 0, 5)]
    [InlineData(6, 5, 0)]
    [InlineData(6, 3, 5)]
    [InlineData(6, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    [InlineData(10, 7, 0)]
    public void The_guided_search_finds_the_tab_within_one_click_per_page(int count, int target, int front)
    {
        var (clicks, _) = Run(count, target, front, height: 510, first: 2, tabH: 510 / count - 3);
        Assert.InRange(clicks, 1, count);
    }

    [Fact]
    public void A_reversed_strip_is_found_from_the_second_tab_seen()
    {
        var (clicks, _) = Run(6, 1, 0, 510, 2, 81, reversed: true);
        Assert.InRange(clicks, 1, 6);
    }

    [Fact]
    public void A_tab_the_strip_cuts_off_is_never_found_and_the_clicks_stop_at_the_page_count()
    {
        // 6 tabs of 81 px in a strip that shows only 3
        var (clicks, s) = Run(6, 5, 0, 240, 2, 81);
        Assert.Equal(-1, clicks);
        Assert.Equal(6, s.Clicks);
        Assert.Null(s.Next(240));
    }

    [Fact]
    public void The_front_tab_is_known_again_only_after_a_click_has_shown_it()
    {
        var s = new TabSearch(6, 5, front: 0);
        Assert.Null(s.PointOf(0));
        s.Observe(100, 1);     // changed to page 1
        Assert.Equal(100, s.PointOf(1));
        s.Observe(20, 0);      // changed back to page 0
        Assert.Equal(20, s.PointOf(0));
        s.Observe(300, -1);    // nothing told
        Assert.Equal(3, s.Clicks);
    }

    [Fact]
    public void A_click_that_changes_nothing_is_not_repeated_and_gives_no_position()
    {
        var s = new TabSearch(4, 3, front: 0);
        var first = s.Next(400)!.Value;
        s.Observe(first, 0);   // still the front page: a miss, or the front tab itself
        Assert.Null(s.PointOf(0));
        var second = s.Next(400)!.Value;
        Assert.True(Math.Abs(second - first) >= 6);
    }

    [Fact]
    public void After_a_failed_search_the_original_tab_is_searched_for_with_what_was_learned()
    {
        var s = new TabSearch(6, 5, front: 0);
        s.Observe(100, 1); s.Observe(180, 2); s.Observe(260, 3);
        Assert.Equal(3, s.Clicks);
        s.Retarget(0);                                     // back to the first tab: clicks counted afresh
        Assert.Equal(0, s.Clicks);
        var y = s.Next(500)!.Value;
        Assert.True(y < 100);                              // aimed up the strip from the line through the tabs seen (80 px apart)
        s.Observe(y, 0);
        Assert.Equal(y, s.PointOf(0));
    }

    [Fact]
    public void A_tab_whose_position_is_known_is_clicked_there_first()
    {
        var s = new TabSearch(6, 5, front: 0);
        s.Observe(50, 3);
        s.Observe(300, 0);                                 // changed back to 0 at 300
        s.Retarget(3);
        Assert.Equal(50, s.Next(500));
    }

    [Fact]
    public void A_strip_too_small_gives_no_click()
    {
        Assert.Null(new TabSearch(3, 1, 0).Next(10));
    }

    [Fact]
    public void Attempts_are_due_before_the_first_and_after_the_wait_and_reset_for_a_new_step()
    {
        var a = new Attempts();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        var wait = TimeSpan.FromMilliseconds(450);
        Assert.True(a.Due(t, wait));
        Assert.Equal(0, a.Next(t));
        Assert.False(a.Due(t.AddMilliseconds(400), wait));
        Assert.True(a.Due(t.AddMilliseconds(500), wait));
        Assert.Equal(1, a.Next(t.AddMilliseconds(500)));
        a.Reset();
        Assert.True(a.Due(t.AddMilliseconds(501), wait));
        Assert.Equal(0, a.Next(t.AddMilliseconds(501)));
    }
}
