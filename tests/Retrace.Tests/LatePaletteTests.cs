using Retrace.Core;

namespace Retrace.Tests;

/// <summary>A palette that shows up after the command that a ribbon / Quick Access Toolbar click started has ended: caught within the time limit and boxed in a retaken end picture; the person's input first means no retake (a click inside the new
/// palette gets the box on its own picture); another command, the time limit, a palette that was already shown and several sets are decided here.</summary>
public class LatePaletteTests
{
    static readonly PxRect Main = new(0, 0, 1920, 1080);
    static readonly PxRect Box = new(0, 100, 360, 500);

    static PaletteState Page(long id, string name, bool shown, PxRect? rect = null, string set = "Late Set") => new(id, name, set, shown, rect);
    static PaletteState[] Hidden(string name = "Page", string set = "Late Set", long id = 1) => [new(id, name, set, false, null, Diag: "root 60 px or less")];
    static PaletteState[] Visible(string name = "Page", string set = "Late Set", long id = 1, PxRect? r = null) => [new(id, name, set, true, r ?? Box)];

    static LatePaletteWatch Watch(IReadOnlyList<PaletteState> before, long endedAt = 1000)
    {
        var w = new LatePaletteWatch();
        w.Begin(7, before, endedAt);
        return w;
    }

    [Fact]
    public void A_palette_that_appears_and_stays_is_retaken_once_it_was_seen_twice_at_the_same_place()
    {
        var w = Watch(Hidden());
        Assert.Null(w.Poll(Hidden(), Main, 1150));
        Assert.Null(w.Poll(Visible(), Main, 1300));        // first seen
        Assert.Null(w.Poll(Visible(), Main, 1350));        // too soon after the first sighting
        var o = w.Poll(Visible(), Main, 1450)!;
        Assert.Equal(LateKind.Retake, o.Kind);
        Assert.Equal("Page", o.Opened!.Name);
        Assert.Equal(450, o.ElapsedMs);
        Assert.Equal("end picture 7: palette Page appeared 450 ms after the command ended: picture retaken with the box", o.Line);
        Assert.False(w.Active);
    }

    [Fact]
    public void A_palette_that_moves_between_two_reads_is_not_retaken_until_it_stands_still()
    {
        var w = Watch(Hidden());
        Assert.Null(w.Poll(Visible(r: Box), Main, 1200));
        Assert.Null(w.Poll(Visible(r: new PxRect(0, 100, 380, 500)), Main, 1400));
        Assert.Equal(LateKind.Retake, w.Poll(Visible(r: new PxRect(0, 100, 380, 500)), Main, 1550)!.Kind);
    }

    [Fact]
    public void A_palette_that_shows_for_one_read_and_is_gone_again_is_not_retaken()
    {
        var w = Watch(Hidden());
        Assert.Null(w.Poll(Visible(), Main, 1200));
        Assert.Null(w.Poll(Hidden(), Main, 1400));
        Assert.Null(w.Poll(Visible(), Main, 1600));
        Assert.True(w.Active);
    }

    [Fact]
    public void Input_first_with_a_click_inside_the_new_palette_boxes_the_clicks_picture_and_does_not_retake()
    {
        var w = Watch(Hidden());
        var o = w.Input(Visible(), Main, 3300, 100, 200);
        Assert.Equal(LateKind.ClickBox, o.Kind);
        Assert.Equal("Page", o.Opened!.Name);
        Assert.Equal("end picture 7: palette Page appeared 2300 ms after the command ended: the person acted first: box drawn on the next click's picture", o.Line);
        Assert.False(w.Active);
    }

    [Fact]
    public void Input_first_with_a_click_outside_the_palette_or_a_key_gives_no_box()
    {
        var w = Watch(Hidden());
        var o = w.Input(Visible(), Main, 2000, 900, 900);
        Assert.Equal(LateKind.ActedFirst, o.Kind);
        Assert.Null(o.Opened);
        Assert.Contains("the person acted first (a click outside it)", o.Line);
        var w2 = Watch(Visible());   // already there
        Assert.Equal(LateKind.ActedFirst, w2.Input(Visible(), Main, 2000, 100, 200).Kind);
        var w3 = Watch(Hidden());
        var k = w3.Input(Visible(), Main, 1800, null, null);
        Assert.Equal(LateKind.ActedFirst, k.Kind);
    }

    [Fact]
    public void Input_first_with_no_palette_yet_ends_the_watch_and_says_so()
    {
        var w = Watch(Hidden());
        var o = w.Input(Hidden(), Main, 1600, null, null);
        Assert.Equal(LateKind.ActedFirst, o.Kind);
        Assert.Equal("end picture 7: the person acted first (an input, 600 ms after the command ended): no palette had appeared", o.Line);
    }

    [Fact]
    public void Another_command_or_a_dialog_ends_the_watch_without_a_retake()
    {
        var w = Watch(Hidden());
        var o = w.Interrupted("another command started (LINE)", 1400);
        Assert.Equal(LateKind.ActedFirst, o.Kind);
        Assert.Contains("another command started (LINE)", o.Line);
        Assert.False(w.Active);
        Assert.Null(w.Poll(Visible(), Main, 1600));   // nothing after the end
    }

    [Fact]
    public void Nothing_within_the_limit_ends_with_the_time_in_the_line()
    {
        var w = Watch(Hidden());
        Assert.Null(w.Poll(Hidden(), Main, 1000 + LatePaletteWatch.LimitMs - 1));
        var o = w.Poll(Hidden(), Main, 1000 + LatePaletteWatch.LimitMs)!;
        Assert.Equal(LateKind.TimedOut, o.Kind);
        Assert.Equal($"end picture 7: nothing appeared within {LatePaletteWatch.LimitMs} ms after the command ended", o.Line);
    }

    [Fact]
    public void A_palette_shown_at_the_click_is_never_opened_late()
    {
        var w = Watch(Visible());
        Assert.Null(w.Poll(Visible(), Main, 1200));
        Assert.Null(w.Poll(Visible(), Main, 1400));
        Assert.True(w.Active);
    }

    [Fact]
    public void Of_several_sets_the_one_that_appeared_is_boxed_not_the_one_that_was_open()
    {
        var before = new[] { Page(1, "Open", true, new PxRect(900, 100, 1200, 500), "Old Set"), Page(2, "Page", false) };
        var after = new[] { Page(1, "Open", true, new PxRect(900, 100, 1200, 500), "Old Set"), Page(2, "Page", true, Box) };
        var w = Watch(before);
        Assert.Null(w.Poll(after, Main, 1200));
        var o = w.Poll(after, Main, 1350)!;
        Assert.Equal("Page", o.Opened!.Name);
    }

    [Fact]
    public void A_palette_outside_the_window_or_with_an_empty_rectangle_is_never_boxed_late()
    {
        var w = Watch(Hidden());
        Assert.Null(w.Poll(Visible(r: new PxRect(-4000, 0, -3600, 400)), Main, 1200));
        Assert.Null(w.Poll(Visible(r: new PxRect(-4000, 0, -3600, 400)), Main, 1400));
        var w2 = Watch(Hidden());
        Assert.Null(w2.Poll([new PaletteState(1, "Page", "Late Set", true, new PxRect(5, 5, 5, 5))], Main, 1200));
        Assert.Null(w2.Poll([new PaletteState(1, "Page", "Late Set", true, new PxRect(5, 5, 5, 5))], Main, 1400));
    }

    [Fact]
    public void A_rolled_up_set_that_appears_late_is_caught_as_a_strip()
    {
        var strip = new PxRect(50, 50, 72, 356);
        var w = Watch(Hidden());
        PaletteState[] now = [new(1, "Page", "Late Set", false, null, strip)];
        Assert.Null(w.Poll(now, Main, 1200));
        var o = w.Poll(now, Main, 1400)!;
        Assert.Equal("rolled", o.Opened!.State);
    }

    [Fact]
    public void The_poll_is_due_every_150_ms_only_while_watching()
    {
        var w = Watch(Hidden());
        Assert.False(w.Due(1100));
        Assert.True(w.Due(1150));
        w.Poll(Hidden(), Main, 1150);
        Assert.False(w.Due(1200));
        w.Clear();
        Assert.False(w.Due(5000));
    }
}
