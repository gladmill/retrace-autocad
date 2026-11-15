using Xunit;

namespace Retrace.Tests;

public class MarkGuardTests
{
    static readonly PxRect Pic = new(100, 50, 500, 450);

    [Fact]
    public void A_rectangle_inside_the_picture_is_accepted() => Assert.True(MarkGuard.Accept(Pic, new PxRect(120, 60, 180, 90), null));

    [Fact]
    public void A_rectangle_sticking_out_is_drawn_clipped()
    {
        // a long label wider than the palette, a row of a report-view list wider than the list
        Assert.True(MarkGuard.Accept(Pic, new PxRect(90, 60, 180, 90), null));
        Assert.Equal(new PxRect(100, 60, 180, 90), MarkGuard.Visible(Pic, new PxRect(90, 60, 180, 90)));
        Assert.Equal(new PxRect(300, 60, 500, 90), MarkGuard.Visible(Pic, new PxRect(300, 60, 900, 90)));   // a third visible (>= 25 %)
        Assert.Equal(new PxRect(450, 60, 500, 90), MarkGuard.Visible(Pic, new PxRect(450, 60, 2000, 90)));   // 3 % but 50 x 30 px: sizable
        Assert.True(MarkGuard.Accept(Pic, new PxRect(300, 60, 900, 90), (400, 70)));
    }

    [Fact]
    public void A_rectangle_almost_entirely_outside_is_refused()
    {
        Assert.False(MarkGuard.Accept(Pic, new PxRect(600, 60, 700, 90), null));        // far to the right
        Assert.False(MarkGuard.Accept(Pic, new PxRect(0, 0, 50, 40), null));            // nothing in the picture
        Assert.Null(MarkGuard.Visible(Pic, new PxRect(495, 60, 2000, 70)));              // 5 x 10 px of 1505 x 10: a sliver
        Assert.Null(MarkGuard.Visible(Pic, new PxRect(498, 440, 560, 470)));            // a corner
    }

    [Fact]
    public void A_rectangle_far_from_the_click_is_refused()
    {
        Assert.False(MarkGuard.Accept(Pic, new PxRect(120, 60, 180, 90), (400, 400)));
        Assert.False(MarkGuard.Accept(Pic, new PxRect(300, 60, 900, 90), (150, 70)));   // the click is in the part that is clipped away
    }

    [Fact]
    public void The_slack_edges_of_the_click_check()
    {
        var r = new PxRect(120, 60, 180, 90);
        Assert.True(MarkGuard.Accept(Pic, r, (118, 75)));    // 2 px left of the rectangle
        Assert.False(MarkGuard.Accept(Pic, r, (117, 75)));
        Assert.True(MarkGuard.Accept(Pic, r, (181, 75)));    // Right is exclusive: 180 + 2 - 1
        Assert.False(MarkGuard.Accept(Pic, r, (182, 75)));
        Assert.True(MarkGuard.Accept(Pic, r, (150, 58)));
        Assert.False(MarkGuard.Accept(Pic, r, (150, 57)));
        Assert.True(MarkGuard.Accept(Pic, r, (150, 91)));
        Assert.False(MarkGuard.Accept(Pic, r, (150, 92)));
        Assert.False(MarkGuard.Accept(Pic, r, (99, 75)));    // outside the picture whatever the rectangle
    }

    [Fact]
    public void A_click_on_a_tree_items_icon_counts_with_the_row_reach()
    {
        var label = new PxRect(140, 60, 260, 78);            // the label; the icon is 20 px to its left
        Assert.False(MarkGuard.Accept(Pic, label, (122, 69)));
        Assert.True(MarkGuard.Accept(Pic, label, (122, 69), reach: MarkGuard.RowReach));
        Assert.False(MarkGuard.Accept(Pic, label, (122, 85), reach: MarkGuard.RowReach));   // another row stays refused
        Assert.False(MarkGuard.Accept(Pic, label, (400, 69), reach: MarkGuard.RowReach));   // far from the label
    }

    [Fact]
    public void A_rectangle_off_by_rounding_is_accepted() => Assert.True(MarkGuard.Accept(Pic, new PxRect(99, 49, 501, 451), null));

    [Fact]
    public void An_empty_rectangle_is_refused() => Assert.False(MarkGuard.Accept(Pic, new PxRect(120, 60, 120, 90), null));

    [Fact]
    public void A_click_must_be_in_the_rectangle()
    {
        var r = new PxRect(120, 60, 180, 90);
        Assert.True(MarkGuard.Accept(Pic, r, (150, 70)));
        Assert.False(MarkGuard.Accept(Pic, r, (300, 300)));   // the rectangle was read in another space than the click
    }

    [Fact]
    public void A_click_alone_must_be_in_the_picture()
    {
        Assert.True(MarkGuard.Accept(Pic, null, (150, 70)));
        Assert.False(MarkGuard.Accept(Pic, null, (10, 10)));
        Assert.False(MarkGuard.Accept(Pic, null, null));
    }

    [Fact]
    public void A_rectangle_with_its_click_outside_the_picture_is_refused() => Assert.False(MarkGuard.Accept(Pic, new PxRect(120, 60, 180, 90), (600, 70)));

    [Fact]
    public void A_strip_item_must_be_inside_the_strip()
    {
        var strip = new PxRect(0, 100, 400, 130);
        Assert.True(MarkGuard.ItemInStrip(strip, new PxRect(60, 102, 90, 128)));
        Assert.False(MarkGuard.ItemInStrip(strip, new PxRect(60, 90, 90, 118)));
        Assert.False(MarkGuard.ItemInStrip(strip, new PxRect(380, 102, 420, 128)));
    }
}
