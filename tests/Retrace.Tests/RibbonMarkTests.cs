using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

/// <summary>The marks of a ribbon click's picture: the button's own rectangle (a box with an arrow), else an arrow at the mouse-down point, else nothing with a reason for the log.</summary>
public class RibbonMarkTests
{
    static readonly PxRect Button = new(100, 60, 160, 110);
    static readonly PxRect Parent = new(90, 55, 175, 115);

    static RibbonMarkChoice Ch(PxRect? item, PxRect? parent, (int X, int Y)? down, PxRect? picture = null) => RibbonMark.Choose(item, () => parent, down, picture);

    [Fact]
    public void The_buttons_own_rectangle_gets_the_box_when_it_holds_the_mouse_down_point()
    {
        var c = Ch(Button, Parent, (120, 80));
        Assert.Equal(RibbonMarkKind.Box, c.Kind);
        Assert.Equal(Button, c.Box);
    }

    [Fact]
    public void Without_a_mouse_down_point_the_rectangle_is_taken_as_found()
    {
        var c = Ch(Button, null, null);
        Assert.Equal(RibbonMarkKind.Box, c.Kind);
        Assert.Equal(Button, c.Box);
    }

    // a split button's main click, a drop-down's item that is not shown: the button that holds it is what was clicked
    [Fact]
    public void An_item_that_is_not_shown_falls_back_to_the_button_that_holds_it()
    {
        var c = Ch(null, Parent, (120, 80));
        Assert.Equal(RibbonMarkKind.Box, c.Kind);
        Assert.Equal(Parent, c.Box);
    }

    // a drop-down's menu closes before the click is reported: the picture shows the split button, the click was on the menu item below it
    [Fact]
    public void The_container_button_is_used_whatever_the_mouse_down_point()
    {
        var c = Ch(null, Parent, (130, 200));
        Assert.Equal(RibbonMarkKind.Box, c.Kind);
        Assert.Equal(Parent, c.Box);
    }

    [Fact]
    public void A_rectangle_that_does_not_hold_the_mouse_down_point_is_not_the_clicked_button()
    {
        // an id used on two tabs: the lookup found the copy on the other (hidden) tab, which is nowhere near the click
        var other = new PxRect(700, 60, 760, 110);
        var c = Ch(other, null, (120, 80));
        Assert.Equal(RibbonMarkKind.Point, c.Kind);
        Assert.Equal((120, 80), (c.X, c.Y));
        Assert.Contains("does not hold the mouse-down point", c.Reason);
    }

    [Fact]
    public void When_no_rectangle_is_found_the_arrow_goes_to_the_mouse_down_point()
    {
        var c = Ch(null, null, (210, 140));
        Assert.Equal(RibbonMarkKind.Point, c.Kind);
        Assert.Equal((210, 140), (c.X, c.Y));
        Assert.Contains("not found", c.Reason);
    }

    [Fact]
    public void With_neither_nothing_is_drawn_and_the_reason_is_given()
    {
        var c = Ch(null, null, null);
        Assert.Equal(RibbonMarkKind.None, c.Kind);
        Assert.Contains("no mark", c.Reason);
        Assert.Contains("mouse-down", c.Reason);
    }

    [Fact]
    public void An_empty_rectangle_is_not_used()
    {
        var c = Ch(new PxRect(10, 10, 10, 40), null, (12, 20));
        Assert.Equal(RibbonMarkKind.Point, c.Kind);
    }

    [Fact]
    public void The_edge_of_the_button_still_counts()
    {
        Assert.Equal(RibbonMarkKind.Box, Ch(Button, null, (Button.Left - RibbonMark.Reach, Button.Top)).Kind);
        Assert.Equal(RibbonMarkKind.Point, Ch(Button, null, (Button.Left - RibbonMark.Reach - 1, Button.Top)).Kind);
    }

    [Fact]
    public void A_mouse_down_is_fresh_only_shortly_before_the_click_and_only_in_a_ribbon_window()
    {
        Assert.Equal((5, 6), RibbonMark.FreshDown(5, 6, 10_000, 10_300, true));
        Assert.Equal((5, 6), RibbonMark.FreshDown(5, 6, 10_000, 10_000 + RibbonMark.DownFreshMs, true));
        Assert.Null(RibbonMark.FreshDown(5, 6, 10_000, 10_000 + RibbonMark.DownFreshMs + 1, true));   // a keyboard activation long after the last press
        Assert.Null(RibbonMark.FreshDown(5, 6, 0, 500, true));                                         // no press yet
        Assert.Equal((5, 6), RibbonMark.FreshDown(5, 6, uint.MaxValue - 100, 200, true));              // the millisecond clock wrapped
        Assert.Null(RibbonMark.FreshDown(5, 6, 10_000, 10_300, false));                                // the last press was in the drawing: a keytip run button has no press of its own
        Assert.True(RibbonMark.DownFreshMs <= 1000);
    }

    // smart zoom: the crop may not show the rectangle that was found; the next candidate is tried, not a mark that is rejected later
    [Fact]
    public void A_rectangle_outside_the_picture_is_unusable_and_the_container_is_tried()
    {
        var crop = new PxRect(0, 40, 400, 200);
        var outside = new PxRect(900, 60, 960, 110);
        var c = Ch(outside, Parent, null, crop);
        Assert.Equal(RibbonMarkKind.Box, c.Kind);
        Assert.Equal(Parent, c.Box);
    }

    [Fact]
    public void When_nothing_is_inside_the_picture_the_mouse_down_arrow_is_next_and_then_nothing()
    {
        var crop = new PxRect(0, 40, 400, 200);
        var outside = new PxRect(900, 60, 960, 110);
        var c = Ch(outside, outside, (120, 80), crop);
        Assert.Equal(RibbonMarkKind.Point, c.Kind);
        Assert.Equal(RibbonMarkKind.None, Ch(outside, outside, (920, 80), crop).Kind);   // the point is outside too
    }

    [Fact]
    public void The_container_is_looked_up_only_when_the_items_own_box_is_not_used()
    {
        int asked = 0;
        var c = RibbonMark.Choose(Button, () => { asked++; return Parent; }, (120, 80));
        Assert.Equal(RibbonMarkKind.Box, c.Kind);
        Assert.Equal(0, asked);
        RibbonMark.Choose(null, () => { asked++; return Parent; }, (120, 80));
        Assert.Equal(1, asked);
    }

    [Theory]
    [InlineData("RibbonSplitButton", true)]
    [InlineData("RibbonButton", true)]
    [InlineData("RibbonList", true)]
    [InlineData("RibbonRowPanel", false)]   // the row a plain button sits in: its rectangle is the whole row
    [InlineData("RibbonRowBreak", false)]
    [InlineData("RibbonPanelBreak", false)]
    [InlineData("RibbonSeparator", false)]
    [InlineData("RibbonFoldPanel", false)]
    [InlineData("", false)]
    public void Only_a_button_like_item_stands_for_the_button_that_holds_the_click(string type, bool expected) => Assert.Equal(expected, RibbonMark.IsButtonLike(type));
}

/// <summary>Classic mode: a palette step always ends with one picture, the window's, or the held close-up when that does not come.</summary>
public class PictureFallbackTests
{
    [Fact]
    public void A_step_whose_window_picture_came_has_no_fallback()
    {
        var f = new PictureFallback();
        f.Expect(7, 1000);
        f.Written(7);
        Assert.Empty(f.Due(100_000, force: false));
        Assert.Equal(0, f.Pending);
    }

    [Fact]
    public void A_step_whose_window_picture_did_not_come_gets_the_held_close_up_after_the_grace()
    {
        var f = new PictureFallback();
        f.Expect(7, 1000);   // the window request is due at 1000 and is dropped after ShotScheduler.MaxWaitMs
        Assert.Empty(f.Due(1000 + ShotScheduler.MaxWaitMs, force: false));   // still within what the scheduler may do
        Assert.Equal(new[] { 7 }, f.Due(1000 + PictureFallback.GraceMs, force: false));
        Assert.Empty(f.Due(500_000, force: false));   // once
    }

    [Fact]
    public void The_grace_is_longer_than_the_schedulers_age_limit()
    {
        Assert.True(PictureFallback.GraceMs > ShotScheduler.MaxWaitMs);
    }

    [Fact]
    public void Stopping_the_recording_settles_every_open_step_in_order()
    {
        var f = new PictureFallback();
        f.Expect(9, 5000); f.Expect(3, 4000); f.Expect(5, 4500); f.Written(5);
        Assert.Equal(new[] { 3, 9 }, f.Due(0, force: true));
    }

    // the scheduler drops a request that waited too long (a handler blocked the UI thread): this is the case the fallback is for
    [Fact]
    public void A_window_request_dropped_for_age_never_writes_and_so_the_fallback_is_what_ends_the_step()
    {
        var sched = new ShotScheduler();
        sched.Enqueue(new ShotRequest(7, "palette", ShotTarget.Window, 1000));
        var taken = sched.TakeReady(1000 + ShotScheduler.MaxWaitMs + 1);
        Assert.Empty(taken);
        Assert.Equal(1, sched.Dropped);
        var f = new PictureFallback();
        f.Expect(7, 1000);
        Assert.Equal(new[] { 7 }, f.Due(1000 + ShotScheduler.MaxWaitMs + 1 + PictureFallback.GraceMs, force: false));
    }
}

public class ClassicPalettePictureTests
{
    static readonly PxRect Main = new(0, 0, 1920, 1040);

    [Fact]
    public void A_palette_inside_the_window_is_in_the_window_picture() => Assert.True(ClassicPalettePicture.InsideWindow(new PxRect(50, 60, 350, 610), Main));

    [Fact]
    public void A_palette_floating_on_another_monitor_is_not() => Assert.False(ClassicPalettePicture.InsideWindow(new PxRect(2100, 60, 2400, 610), Main));

    [Fact]
    public void A_palette_that_sticks_out_of_the_window_is_not() => Assert.False(ClassicPalettePicture.InsideWindow(new PxRect(1800, 60, 2100, 610), Main));

    [Fact]
    public void An_empty_rectangle_is_not() => Assert.False(ClassicPalettePicture.InsideWindow(new PxRect(10, 10, 10, 50), Main));
}
