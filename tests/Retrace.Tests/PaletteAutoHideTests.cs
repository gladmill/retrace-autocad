using Retrace.Core;
using Retrace.Engine.Emit;

namespace Retrace.Tests;

/// <summary>A command (a ribbon / Quick Access Toolbar click) that opened a palette set which has Auto-hide on: what is on screen is boxed (the expanded palette, the rolled-up title strip of a floating set, the edge bar of an anchored docked set), a palette
/// that was already there and only expands under the mouse is not "opened", and nothing is boxed when the rectangle cannot be read. Normal palettes decide as before.</summary>
public class PaletteAutoHideTests
{
    static readonly PxRect Main = new(0, 0, 1920, 1080);
    static readonly PxRect Strip = new(50, 50, 72, 356);
    static readonly PxRect Expanded = new(50, 50, 356, 356);

    static PaletteState Page(long id, string name, bool shown, PxRect? rect = null, PxRect? strip = null, string set = "Retrace Panes") => new(id, name, set, shown, rect, strip);
    static PaletteState Bar(long id, PxRect r) => new(id, "(auto-hide bar)", "", false, r, null, true);

    [Fact]
    public void A_floating_set_that_was_closed_and_is_rolled_up_after_the_command_boxes_its_strip_and_names_the_set()
    {
        var before = new[] { Page(1, "Canvas", false), Page(2, "Grid", false) };
        var after = new[] { Page(1, "Canvas", false, strip: Strip), Page(2, "Grid", false, strip: Strip) };
        var d = PaletteOpening.Decide(before, after, Main);
        Assert.Equal("Retrace Panes", d.Opened!.Name);
        Assert.Equal(Strip, d.Opened.Rect);
        Assert.Equal("rolled", d.Opened.State);
        Assert.Contains("rolled up: strip 50,50 22x306 boxed", d.Reason);
    }

    [Fact]
    public void A_set_the_command_created_that_is_rolled_up_counts_as_opened()
    {
        var d = PaletteOpening.Decide([], [Page(1, "Canvas", false, strip: Strip)], Main);
        Assert.Equal("rolled", d.Opened!.State);
    }

    [Fact]
    public void A_set_expanded_at_the_end_is_boxed_as_the_open_palette_and_says_expanded_in_the_log()
    {
        var d = PaletteOpening.Decide([Page(1, "Canvas", false)], [Page(1, "Canvas", true, Expanded)], Main);
        Assert.Equal("Canvas", d.Opened!.Name);
        Assert.Equal("", d.Opened.State);
        Assert.Contains("expanded: 50,50 306x306 boxed", d.Reason);
    }

    [Fact]
    public void A_set_that_was_rolled_up_before_and_only_expands_when_the_mouse_is_on_it_is_not_opened()
    {
        var before = new[] { Page(1, "Canvas", false, strip: Strip), Page(2, "Grid", false, strip: Strip) };
        var after = new[] { Page(1, "Canvas", true, Expanded), Page(2, "Grid", false) };
        var d = PaletteOpening.Decide(before, after, Main);
        Assert.Null(d.Opened);
        Assert.Contains("already open", d.Reason);
    }

    [Fact]
    public void A_set_that_stays_rolled_up_from_before_the_command_is_not_opened_either()
    {
        var both = new[] { Page(1, "Canvas", false, strip: Strip) };
        Assert.Null(PaletteOpening.Decide(both, both, Main).Opened);
    }

    [Fact]
    public void A_rolled_up_strip_outside_the_window_or_with_no_size_is_never_boxed_and_the_reason_is_given()
    {
        var off = PaletteOpening.Decide([], [Page(1, "Canvas", false, strip: new PxRect(3000, 50, 3022, 356))], Main);
        Assert.Null(off.Opened);
        Assert.Contains("rolled up, strip outside AutoCAD's window", off.Reason);
        var empty = PaletteOpening.Decide([], [Page(1, "Canvas", false, strip: new PxRect(50, 50, 50, 356))], Main);
        Assert.Null(empty.Opened);
        Assert.Contains("strip rectangle empty", empty.Reason);
    }

    [Fact]
    public void A_docked_auto_hide_set_is_boxed_as_its_edge_bar_when_the_bar_is_new_and_one_set_could_be_it()
    {
        var bar = new PxRect(1255, 182, 1280, 839);
        var d = PaletteOpening.Decide([Page(5, "Other", true, Expanded, set: "Other set")], [Page(5, "Other", true, Expanded, set: "Other set"), Page(1, "Canvas", false), Page(2, "Grid", false), Bar(9, bar)], Main);
        Assert.Equal("Retrace Panes", d.Opened!.Name);
        Assert.Equal(bar, d.Opened.Rect);
        Assert.Equal("anchored", d.Opened.State);
        Assert.Contains("anchored, Auto-hide: edge bar", d.Reason);
    }

    [Fact]
    public void A_docked_set_is_not_guessed_when_several_sets_could_be_the_new_one_or_the_bar_was_there_before()
    {
        var bar = new PxRect(1255, 182, 1280, 839);
        var two = PaletteOpening.Decide([], [Page(1, "A", false, set: "S1"), Page(2, "B", false, set: "S2"), Bar(9, bar)], Main);
        Assert.Null(two.Opened);
        Assert.Contains("2 palette sets", two.Reason);
        var same = new[] { Page(1, "A", false), Bar(9, bar) };
        var there = PaletteOpening.Decide(same, same, Main);
        Assert.Null(there.Opened);
        Assert.Contains("no palette became visible", there.Reason);
        var none = PaletteOpening.Decide([], [Bar(9, bar)], Main);
        Assert.Null(none.Opened);
        Assert.Contains("no palette that was not there before", none.Reason);
    }

    [Fact]
    public void An_unreadable_bar_is_no_box()
    {
        var d = PaletteOpening.Decide([], [Page(1, "A", false), Bar(9, new PxRect(5000, 0, 5025, 600))], Main);
        Assert.Null(d.Opened);
        Assert.Contains("unreadable or outside the window", d.Reason);
    }

    [Fact]
    public void Normal_palettes_still_decide_as_before()
    {
        var d = PaletteOpening.Decide([new PaletteState(1, "Pick", "Set", false, null)], [new PaletteState(1, "Pick", "Set", true, new PxRect(1500, 100, 1900, 900))], Main);
        Assert.Equal("Pick", d.Opened!.Name);
        Assert.Equal("", d.Opened.State);
        Assert.Null(PaletteOpening.Decide([new PaletteState(1, "Pick", "Set", true, new PxRect(1500, 100, 1900, 900))], [new PaletteState(1, "Pick", "Set", true, new PxRect(1500, 100, 1900, 900))], Main).Opened);   // already open: none
        var hidden = PaletteOpening.Decide([new PaletteState(1, "Pick", "Set", false, null)], [new PaletteState(1, "Pick", "Set", false, null)], Main);
        Assert.Contains("closed, collapsed or auto-hidden: Pick", hidden.Reason);
    }

    [Fact]
    public void The_caption_says_what_is_on_screen()
    {
        Assert.Equal("The **Retrace Panes** palette opens.", StepWording.OpensCaption("Retrace Panes"));
        Assert.Equal("The **Retrace Panes** palette opens (rolled up: hover it to expand).", StepWording.OpensCaption("Retrace Panes", "rolled"));
        Assert.Equal("The **Retrace Panes** palette opens (anchored at the edge of the window: hover its tab to expand).", StepWording.OpensCaption("Retrace Panes", "anchored"));
        Assert.True(StepWording.IsOpensCaption("The Retrace Panes palette opens (rolled up: hover it to expand)."));
        Assert.True(StepWording.IsOpensCaption("The Retrace Panes palette opens (anchored at the edge of the window: hover its tab to expand)."));
        Assert.False(StepWording.IsOpensCaption("The palette opens (hover it)."));
    }

    [Fact]
    public void A_set_that_existed_hidden_before_and_is_rolled_up_now_is_opened()
    {
        // create once, toggle Visible: the pages were in the list before (hidden)
        var before = new[] { Page(1, "Canvas", false), Page(2, "Grid", false) };
        var after = new[] { Page(1, "Canvas", false, strip: Strip), Page(2, "Grid", false, strip: Strip) };
        Assert.Equal("rolled", PaletteOpening.Decide(before, after, Main).Opened!.State);
    }

    [Fact]
    public void A_docked_set_that_existed_hidden_before_is_a_candidate_for_the_new_bar()
    {
        var bar = new PxRect(1255, 182, 1280, 839);
        PaletteState Hidden(long id, string name, string diag) => new(id, name, "Retrace Panes", false, null, Diag: diag);
        var d = PaletteOpening.Decide([Hidden(1, "Canvas", "a"), Hidden(2, "Grid", "a")], [Hidden(1, "Canvas", "b"), Hidden(2, "Grid", "b"), Bar(9, bar)], Main);
        Assert.Equal("anchored", d.Opened!.State);
    }

    [Fact]
    public void A_set_expanded_at_the_click_and_rolled_up_at_the_end_is_not_opened()
    {
        var before = new[] { Page(1, "Canvas", true, Expanded), Page(2, "Grid", false) };   // the front page shown, the back page hidden
        var after = new[] { Page(1, "Canvas", false, strip: Strip), Page(2, "Grid", false, strip: Strip) };
        Assert.Null(PaletteOpening.Decide(before, after, Main).Opened);
    }

    [Fact]
    public void A_docked_set_that_was_open_before_is_not_a_candidate_for_a_new_bar()
    {
        var bar = new PxRect(1255, 182, 1280, 839);
        var d = PaletteOpening.Decide([Page(1, "Canvas", true, Expanded)], [Page(1, "Canvas", false), Bar(9, bar)], Main);
        Assert.Null(d.Opened);
    }

    [Fact]
    public void A_set_that_stayed_closed_with_the_same_geometry_is_not_a_candidate_for_the_new_bar()
    {
        var bar = new PxRect(1255, 182, 1280, 839);
        var closed = new PaletteState(7, "Command Macros", "Command Macros", false, null, Diag: "page rect 587,93,937,716");
        var hiddenBefore = new PaletteState(1, "Canvas", "Retrace Panes", false, null, Diag: "page rect 3,3,257,297");
        var dockedAfter = new PaletteState(1, "Canvas", "Retrace Panes", false, null, Diag: "page rect 24,3,278,297");
        var d = PaletteOpening.Decide([closed, hiddenBefore], [closed, dockedAfter, Bar(9, bar)], Main);
        Assert.Equal("Retrace Panes", d.Opened!.Name);
        Assert.Equal("anchored", d.Opened.State);
    }
}
