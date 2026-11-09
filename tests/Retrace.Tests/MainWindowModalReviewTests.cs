using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Review round 1 of the windowless-modal change: the replay's modal state, the recorder's episode, the window rule, the recording markers (`main`, `modal_nowindow` at the leave). Synthetic recordings, generic names.</summary>
public class MainWindowModalReviewTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    // ---- finding 1, 3, 4: the replay's modal state ----

    [Fact]
    public void A_modal_state_is_not_windowless_before_the_grace_and_is_after_it()
    {
        var m = new ModalState();
        m.Entered(T0);
        Assert.False(m.Windowless(T0.AddMilliseconds(500), false));   // a slow dialog may still show
        Assert.True(m.Windowless(T0.AddMilliseconds(ModalState.GraceMs), false));
    }

    [Fact]
    public void A_window_open_or_seen_makes_the_state_a_dialog_not_windowless()
    {
        var m = new ModalState();
        m.Entered(T0);
        Assert.False(m.Windowless(T0.AddSeconds(5), true));
        Assert.True(m.Observe(true, T0));       // counted as a dialog the first time
        Assert.False(m.Observe(true, T0));
        Assert.False(m.Windowless(T0.AddMilliseconds(500), false));   // the dialog closed a moment ago: a slow dialog may be next
        Assert.True(m.Windowless(T0.AddMilliseconds(ModalState.GraceMs), false));   // closed for good inside the same outer state: windowless again
    }

    [Fact]
    public void A_windowless_modal_state_does_not_complete_the_next_dialog_steps_wait()
    {
        var m = new ModalState(); var d = new DialogWait();
        // the click's script opens a hidden modal state that lasts 3 s, then the real dialog's step begins
        m.Entered(T0);
        m.Observe(false, T0);
        var (countedNow, wasDialog) = m.Left(T0.AddSeconds(3));
        Assert.False(countedNow); Assert.False(wasDialog);
        d.Begin(T0.AddSeconds(3), m.Depth > 0 && m.WindowSeen);
        Assert.Equal(DialogWaitPhase.WaitingForOpen, d.Phase);
        Assert.Equal(0, m.Counted);
        // the real dialog opens, a tick sees its window, then it closes
        m.Entered(T0.AddSeconds(4));
        if (m.Observe(true, T0)) d.ModalEntered();
        Assert.Equal(DialogWaitPhase.WaitingForPerson, d.Phase);
        var left = m.Left(T0.AddSeconds(9));
        Assert.True(left.WasDialog);
        d.ModalLeft(m.Depth);
        Assert.Equal(DialogWaitPhase.Done, d.Phase);
    }

    [Fact]
    public void A_dialog_that_came_and_went_between_ticks_still_counts()
    {
        var m = new ModalState();
        m.Entered(T0);
        var (countedNow, wasDialog) = m.Left(T0.AddMilliseconds(200), quickCounts: true);
        Assert.True(countedNow); Assert.True(wasDialog);
        Assert.Equal(1, m.Counted);
    }

    [Fact]
    public void A_slow_dialog_is_a_dialog_when_its_window_shows_after_the_grace()
    {
        var m = new ModalState();
        m.Entered(T0);
        Assert.True(m.Windowless(T0.AddSeconds(2), false));      // nothing yet: treated as windowless for now
        Assert.True(m.Observe(true, T0));                            // the window shows at 2.5 s
        Assert.False(m.Windowless(T0.AddSeconds(3), true));
        Assert.True(m.Left(T0.AddSeconds(6)).WasDialog);
    }

    // ---- finding 2: the recorder's episode ----

    [Fact]
    public void An_episode_in_which_a_window_showed_late_is_not_windowless()
    {
        var e = new ModalEpisode(27);
        e.WindowFound();                                         // found by a later look, 800 ms after the enter
        Assert.False(e.WindowlessAtLeave);
    }

    [Fact]
    public void An_episode_with_no_window_to_the_end_is_windowless_and_a_superseded_one_is_unknown()
    {
        Assert.True(new ModalEpisode(27).WindowlessAtLeave);
        var superseded = new ModalEpisode(28); superseded.Abandon();
        Assert.False(superseded.WindowlessAtLeave);
    }

    // ---- finding 8: no window is decided from the process's windows, not from the foreground ----

    static WindowFacts W(long h, bool visible = true, bool enabled = true, bool caption = true, bool tool = false, bool transparent = false, int w = 300, int hgt = 200, string cls = "WindowsForms10.Window", bool retrace = false) =>
        new(h, visible, enabled, caption, tool, transparent, w, hgt, cls, retrace);

    [Fact]
    public void A_dialog_window_of_the_process_is_found_whatever_is_in_front()
    {
        var windows = new[] { W(1, cls: "Afx:Frame"), W(7) };   // 1 = main window; the list says nothing about the foreground
        Assert.Equal(7, ModalWindows.Topmost(windows, mainWindow: 1));
        Assert.True(ModalWindows.Topmost(windows, 1) != 0);
    }

    [Fact]
    public void The_main_window_alone_is_no_dialog_window()
    {
        Assert.False(ModalWindows.Topmost(new[] { W(1) }, 1) != 0);
        Assert.False(ModalWindows.Topmost(Array.Empty<WindowFacts>(), 1) != 0);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("disabled")]
    [InlineData("tool")]
    [InlineData("transparent")]
    [InlineData("empty")]
    [InlineData("replay")]
    [InlineData("retrace")]
    public void Windows_that_are_no_dialog(string kind)
    {
        var w = kind switch
        {
            "hidden" => W(7, visible: false), "disabled" => W(7, enabled: false), "tool" => W(7, tool: true), "transparent" => W(7, transparent: true),
            "empty" => W(7, w: 0, hgt: 0), "replay" => W(9), _ => W(7, retrace: true),
        };
        Assert.False(ModalWindows.Topmost(new[] { W(1), w }, mainWindow: 1, replayWindow: 9) != 0);
    }

    [Fact]
    public void A_standard_dialog_without_a_title_bar_style_is_a_dialog_and_the_front_one_wins()
    {
        var windows = new[] { W(8, caption: false, cls: "#32770"), W(7) };
        Assert.Equal(8, ModalWindows.Topmost(windows, 1));
        Assert.True(ModalWindows.Topmost(new[] { W(8, caption: false, cls: "Static") }, 1) != 0);   // a borderless dialog is a dialog
    }

    [Fact]
    public void A_form_that_is_never_seen_is_not_shown_even_when_it_is_in_front()
    {
        Assert.False(ModalWindows.IsShown(W(7, transparent: true)));   // opacity 0
        Assert.False(ModalWindows.IsShown(W(7, w: 0, hgt: 0)));
        Assert.False(ModalWindows.IsShown(W(7, visible: false)));
        Assert.True(ModalWindows.IsShown(W(7, caption: false, cls: "WindowsForms10.Window")));   // a borderless dialog is a window
    }

    // ---- finding 7, 9: untitled dialogs, product names with digits, the recorder's `main` ----

    const string MainTitle = "AutoCAD Mechanical 2026 - [Drawing2.dwg]";

    static List<string> Episode(string enterExtra, string? titleLine, bool noWindow, bool values = false)
    {
        var l = new List<string>
        {
            """{"t":1000,"n":1,"type":"ribbon","id":"B","text":"Go","macro":"^C^C_ADDITEM","tab":"T","panel":"P"}""",
            """{"t":1010,"n":2,"type":"cmd_start","command":"ADDITEM","origin":"ribbon"}""",
            $$"""{"t":1100,"n":3,"type":"modal_enter","title":"{{MainTitle}}","windowClass":"AfxMDIFrame140u"{{enterExtra}}}""",
        };
        if (titleLine != null) l.Add(titleLine);
        if (values) l.Add("""{"t":3000,"n":5,"type":"dialog_values","for":4,"title":"x","source":"win32","closedWith":"ok","fields":[],"read":1}""");
        if (noWindow) l.Add("""{"t":3900,"n":6,"type":"modal_nowindow","for":3}""");
        l.Add("""{"t":4000,"n":7,"type":"modal_leave"}""");
        l.Add("""{"t":4100,"n":8,"type":"cmd_end","command":"ADDITEM"}""");
        return l;
    }

    static string[] Kinds(IEnumerable<string> lines) => MainWindowModalTests.Build(lines).Select(s => s.Kind).ToArray();

    [Theory]
    [InlineData("#32770")]
    [InlineData("WindowsForms10.Window.8.app")]
    [InlineData("AfxFrameOrView140s")]
    public void An_untitled_real_dialog_is_another_window_in_a_recording_without_main(string cls)
    {
        var lines = Episode("", $$"""{"t":1900,"n":4,"type":"modal_title","title":"","windowClass":"{{cls}}"}""", noWindow: false);
        Assert.Contains("dialog", Kinds(lines));
    }

    [Fact]
    public void An_untitled_window_of_the_main_frames_own_class_is_still_the_main_window()
    {
        var lines = Episode("", """{"t":1900,"n":4,"type":"modal_title","title":"","windowClass":"AfxMDIFrame140u"}""", noWindow: false);
        Assert.DoesNotContain("dialog", Kinds(lines));
    }

    [Theory]
    [InlineData("AutoCAD Plant 3D 2026 - [x.dwg]", true)]
    [InlineData("AutoCAD Map 3D 2026 - [x.dwg]", true)]
    [InlineData("Autodesk Civil 3D 2026 - [x.dwg]", true)]
    [InlineData("AutoCAD Electrical 2026", true)]
    [InlineData("AutoCAD Plant 3D 2026", true)]
    [InlineData("AutoCAD Mechanical 2026 - [Drawing2.dwg]", true)]
    [InlineData("Autodesk Civil 3D 2026", true)]
    [InlineData("Pick item", false)]
    [InlineData("Select 3D 2026 option", false)]
    [InlineData("AutoCAD", false)]
    public void Product_names_with_digits(string title, bool main) => Assert.Equal(main, MainWindowModal.IsRecorded(title, "AfxMDIFrame140u"));

    [Fact]
    public void A_recording_with_main_is_judged_by_the_recorders_marker_not_the_title()
    {
        // the recorder says the window was the main window and no other window showed: windowless
        Assert.DoesNotContain("dialog", Kinds(Episode(",\"main\":true", null, noWindow: true)));
        // main window in front, a title that does not look like one (a product name the rule does not know), still windowless by the marker
        var odd = Episode(",\"main\":true", null, noWindow: true).Select(l => l.Replace(MainTitle, "Some Vertical 9 - [x.dwg]")).ToList();
        Assert.DoesNotContain("dialog", Kinds(odd));
        // no marker at the leave: a window showed (recorder looked to the end): a dialog, whatever the title
        Assert.Contains("dialog", Kinds(Episode(",\"main\":true", null, noWindow: false)));
    }

    [Fact]
    public void A_slow_dialog_recorded_with_a_late_modal_title_stays_a_dialog_step()
    {
        var lines = Episode(",\"main\":true", """{"t":2000,"n":4,"type":"modal_title","title":"Slow dialog","windowClass":"#32770","main":false}""", noWindow: false, values: true);
        var steps = MainWindowModalTests.Build(lines);
        Assert.Equal(new[] { "ribbon", "dialog" }, steps.Select(s => s.Kind));
        Assert.Equal("Slow dialog", steps[1].DialogTitle);
    }

    // ---- finding 6: a main-titled dialog with recorded values is a dialog the plan keeps ----

    [Fact]
    public void A_dialog_with_the_main_windows_title_and_recorded_values_stays_a_dialog_step()
    {
        var lines = Episode("", null, noWindow: false, values: true).Select(l => l.Replace("\"for\":4,", "\"for\":3,")).ToList();
        var steps = MainWindowModalTests.Build(lines);
        Assert.Contains(steps, s => s.Kind == "dialog");
    }

    // ---- finding 5: a chain whose only modal state is windowless stays the click's, with its answers ----

    static List<string> ChainWithOnlyWindowless()
    {
        var l = MainWindowModalTests.Recording(false, true);
        // drop the real dialog: events 20 (modal_title), 24, 25 and the first episode's pair
        return l.Where(x => !x.Contains("\"n\":20,") && !x.Contains("\"for\":20,") && !x.Contains("\"n\":18,") && !x.Contains("\"n\":26,")).ToList();
    }

    [Fact]
    public void A_chain_with_only_a_windowless_modal_keeps_its_commands_and_answers_in_the_click_step()
    {
        var steps = MainWindowModalTests.Build(ChainWithOnlyWindowless());
        Assert.Equal(new[] { "ribbon" }, steps.Select(s => s.Kind));   // not ribbon + command (ADDITEM) + ...: the click owns the script's commands
        var click = steps[0];
        Assert.True(click.ChainClick);
        var due = ReplayRules.ChainAnswersAfterClick(steps, 0);
        Assert.Equal(new[] { "point", "angle" }, due.Select(a => a.Kind));
        Assert.True(due.All(a => a.Input == true));
        Assert.Null(ChainDelivery.Unsafe(due));
        Assert.Null(ReplayRules.ChainAnswersNeedPerson(click, dialogFollows: false));   // nothing a person must do: the replay types them
    }

    [Fact]
    public void Answers_after_a_dialog_are_not_also_delivered_after_the_click()
    {
        var steps = MainWindowModalTests.Build(MainWindowModalTests.Recording(false));
        Assert.Equal(new[] { "ribbon", "dialog" }, steps.Select(s => s.Kind));
        Assert.Empty(ReplayRules.ChainAnswersAfterClick(steps, 0));
        Assert.NotNull(ReplayRules.ChainAnswersNeedPerson(steps[0], dialogFollows: true));
    }
}
