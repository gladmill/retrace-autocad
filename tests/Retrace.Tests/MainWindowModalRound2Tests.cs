using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Review round 2 of the windowless-modal change: answers before the first dialog, the short-state rule, unsafe answers of a click, the marker alone, modeless windows, borderless dialogs, the windowless phase after a dialog. Synthetic data, generic names.</summary>
public class MainWindowModalRound2Tests
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    static WindowFacts W(long h, bool caption = true, bool enabled = true, string cls = "WindowsForms10.Window") =>
        new(h, true, enabled, caption, false, false, 300, 200, cls, false);

    // ---- 1: a windowless modal, the jig answers, THEN the real dialog ----

    /// <summary>click, a windowless modal state (4 s), point + angle answered by the person, then the real dialog opens and closes, then cmd_end.</summary>
    static List<string> HiddenThenDialog()
    {
        var l = MainWindowModalTests.Recording(false, true);
        var head = l.Where(x => !x.Contains("\"n\":18,") && !x.Contains("\"n\":20,") && !x.Contains("\"n\":24,") && !x.Contains("\"n\":25,") && !x.Contains("\"n\":26,") && !x.Contains("\"cmd_end\",\"command\":\"ADDITEM\"")).ToList();
        head.AddRange(new[]
        {
            """{"t":30000,"n":200,"type":"modal_enter","title":"Pick item","windowClass":"#32770","main":false}""",
            """{"t":33000,"n":201,"type":"dialog_values","for":200,"title":"Pick item","source":"win32","closedWith":"ok","fields":[],"read":1}""",
            """{"t":33010,"n":202,"type":"modal_leave"}""",
            """{"t":33100,"n":203,"type":"cmd_end","command":"ADDITEM"}""",
        });
        return head;
    }

    [Fact]
    public void Answers_given_between_the_click_and_the_first_dialog_are_delivered_with_the_click()
    {
        var steps = MainWindowModalTests.Build(HiddenThenDialog());
        Assert.Equal(new[] { "ribbon", "dialog" }, steps.Select(s => s.Kind));
        var before = ReplayRules.ChainAnswersAfterClick(steps, 0);
        Assert.Equal(new[] { "point", "angle" }, before.Select(a => a.Kind));
        Assert.Empty(ReplayRules.ChainAnswersAfter(steps[0].Answers, steps, 1));   // nothing is typed twice
    }

    // ---- 2: a short modal state with no window is a dialog only in a recording from before the markers ----

    [Fact]
    public void A_short_modal_state_with_no_window_is_no_dialog_unless_the_recording_is_old()
    {
        var m = new ModalState();
        m.Entered(T0);
        Assert.False(m.Left(T0.AddMilliseconds(200)).WasDialog);                      // the recorder would have written modal_nowindow
        Assert.Equal(0, m.Counted);
        m.Entered(T0.AddSeconds(1));
        Assert.True(m.Left(T0.AddSeconds(1).AddMilliseconds(200), quickCounts: true).WasDialog);   // old recording: it may have come and gone between ticks
    }

    [Fact]
    public void A_dialog_step_knows_whether_the_recorder_marked_its_window()
    {
        var steps = MainWindowModalTests.Build(MainWindowModalTests.Recording(false, true).Select(l => l.Replace("\"n\":18,\"type\":\"modal_enter\"", "\"n\":18,\"type\":\"modal_enter\",\"main\":true")));
        Assert.True(steps.First(s => s.Kind == "dialog").WindowMarked);
        Assert.False(MainWindowModalTests.Build(MainWindowModalTests.Recording(false)).First(s => s.Kind == "dialog").WindowMarked);
    }

    // ---- 3: unsafe answers of a click without a dialog ----

    [Fact]
    public void Unsafe_answers_of_a_click_with_no_dialog_stop_a_non_interactive_replay()
    {
        var steps = MainWindowModalTests.Build(MainWindowModalTests.Recording(false, true).Where(l => !l.Contains("\"n\":20,") && !l.Contains("\"for\":20,") && !l.Contains("\"n\":18,") && !l.Contains("\"n\":26,")));
        Assert.Null(ReplayRules.ChainAnswersNeedPerson(steps[0], dialogFollows: false));   // typeable: no stop
        var click = steps[0];
        click.Answers.Add(new Answer("selection", "OK", null, null, "Select objects:", true, null, click.ChainFromT + 1));
        var msg = ReplayRules.ChainAnswersNeedPerson(click, dialogFollows: false);
        Assert.NotNull(msg);
        Assert.Contains("selection", msg);
    }

    // ---- 4: the marker alone ----

    [Fact]
    public void A_nowindow_marker_makes_the_state_windowless_whatever_main_says()
    {
        var lines = new[]
        {
            """{"t":1000,"n":1,"type":"ribbon","id":"B","text":"Go","macro":"^C^C_ADDITEM","tab":"T","panel":"P"}""",
            """{"t":1010,"n":2,"type":"cmd_start","command":"ADDITEM","origin":"ribbon"}""",
            """{"t":1100,"n":3,"type":"modal_enter","title":"Some Window","windowClass":"X","main":false}""",
            """{"t":3900,"n":6,"type":"modal_nowindow","for":3}""",
            """{"t":4000,"n":7,"type":"modal_leave"}""",
            """{"t":4100,"n":8,"type":"cmd_end","command":"ADDITEM"}""",
        };
        Assert.DoesNotContain(MainWindowModalTests.Build(lines), s => s.Kind == "dialog");
    }

    // ---- 5, 6: modeless windows, borderless dialogs ----

    [Fact]
    public void A_window_that_was_open_before_the_modal_state_is_no_dialog_a_new_one_is()
    {
        var before = new HashSet<long> { 1, 5 };   // 1 main, 5 a floating palette
        Assert.Equal(0, ModalWindows.Topmost(new[] { W(5), W(1) }, 1, 0, before));
        Assert.Equal(7, ModalWindows.Topmost(new[] { W(5), W(7), W(1) }, 1, 0, before));
    }

    [Fact]
    public void A_new_borderless_window_is_a_dialog()
    {
        Assert.Equal(8, ModalWindows.Topmost(new[] { W(8, caption: false, cls: "WindowsForms10.Window") }, 1, 0, new HashSet<long> { 1 }));
        Assert.Equal(0, ModalWindows.Topmost(new[] { W(8, cls: "#32768") }, 1, 0, new HashSet<long> { 1 }));   // a menu is not
    }

    [Fact]
    public void The_episode_keeps_the_windows_open_at_its_start()
    {
        Assert.Contains(5L, new ModalEpisode(3, new HashSet<long> { 5 }).Before!);
    }

    // ---- 7: a windowless phase after a dialog closed inside the same outer state ----

    [Fact]
    public void A_dialog_open_now_is_not_windowless_and_one_that_closed_long_ago_is()
    {
        var m = new ModalState();
        m.Entered(T0);
        m.Observe(true, T0.AddSeconds(1));
        Assert.False(m.Windowless(T0.AddSeconds(5), true));    // the dialog is open now
        Assert.False(m.Windowless(T0.AddSeconds(2), false));   // closed a second ago
        Assert.True(m.Windowless(T0.AddSeconds(9), false));    // closed for 8 s: the outer state's hidden phase
    }
}
