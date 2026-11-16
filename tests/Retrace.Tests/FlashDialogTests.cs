using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>A dialog that opens and closes by itself within a fraction of a second is a flash (no step, no picture); a dialog a person answers, however fast, is not. Pictures wait until a dialog has painted.</summary>
public class FlashDialogTests
{
    [Theory]
    //          seen   now    input  hook   young
    [InlineData(1000L, 1100L, false, true, true)]     // seen 100 ms ago, untouched, the hook would have seen a press: may still be a flash
    [InlineData(1000L, 1499L, false, true, true)]
    [InlineData(1000L, 1500L, false, true, false)]    // on screen 500 ms: a dialog
    [InlineData(1000L, 3000L, false, true, false)]
    [InlineData(1000L, 1100L, true, true, false)]     // a click or key press came: the person's dialog, however short
    [InlineData(null, 1100L, false, true, false)]     // never seen: not this rule (a windowless state has its own)
    [InlineData(1000L, 1100L, false, false, false)]   // no input hook for this dialog (the read threw, or the system refused): a press would not have been seen, so never a flash
    public void Young_and_flash_follow_time_input_sight_and_the_hook(long? seen, long now, bool input, bool hook, bool expected)
    {
        Assert.Equal(expected, FlashDialog.Young(seen, now, input, hook));
        Assert.Equal(expected, FlashDialog.IsFlash(seen, now, input, hook));
    }

    [Theory]
    //          start now   shown  found  tracked input  hold
    [InlineData(1000L, 1200L, true, false, true, false, true)]    // a dialog window is up, the look has not reached it: wait for it
    [InlineData(1000L, 2400L, true, false, true, false, true)]
    [InlineData(1000L, 2500L, true, false, true, false, false)]   // not for ever: 1.5 s
    [InlineData(1000L, 1200L, false, false, true, false, false)]  // no dialog window on screen (a windowless state)
    [InlineData(1000L, 1200L, true, true, true, false, false)]    // the window was found (a dialog that merged into one a button opened can never be a flash)
    [InlineData(1000L, 1200L, true, false, false, false, false)]  // not tracked: no way to see a press
    [InlineData(1000L, 1200L, true, false, true, true, false)]    // somebody pressed something
    public void The_hold_before_the_window_is_seen(long start, long now, bool shown, bool found, bool tracked, bool input, bool hold) =>
        Assert.Equal(hold, FlashDialog.HoldBeforeSeen(start, now, shown, found, tracked, input));

    static List<JsonElement> Parse(IEnumerable<string> lines) => lines.Select(x => JsonDocument.Parse(x).RootElement.Clone()).ToList();

    [Fact]
    public void A_flash_of_a_start_up_lisp_does_not_end_the_skipping_and_a_dialog_still_does()
    {
        List<JsonElement> Events(bool flashed)
        {
            var l = new List<string>
            {
                """{"t":0,"n":1,"type":"doc_created"}""",
                """{"t":100,"n":2,"type":"lisp_start"}""",
                """{"t":200,"n":3,"type":"modal_enter","title":"","main":false}""",
                """{"t":300,"n":4,"type":"modal_title","title":"Splash","main":false}""",
            };
            if (flashed) l.Add("""{"t":400,"n":5,"type":"modal_flash","for":3}""");
            l.Add("""{"t":410,"n":6,"type":"modal_leave"}""");
            l.Add("""{"t":500,"n":7,"type":"lisp_end"}""");
            return Parse(l);
        }
        Assert.Contains(7L, DrawingOpen.StartupEvents(Events(true)));          // the LISP's end is still inside the start-up: skipped
        Assert.DoesNotContain(7L, DrawingOpen.StartupEvents(Events(false)));   // a real dialog is a person acting: the skipping stops there, as before
    }

    [Fact]
    public void A_flash_is_not_the_first_dialog_of_a_script_chain()
    {
        List<JsonElement> Events(bool flashed)
        {
            var l = new List<string>
            {
                """{"t":1000,"n":1,"type":"ribbon","id":"RUN_BTN","text":"Add","macro":"^C^C_SCRIPT c:/x/chain.scr","tabId":"T1","tab":"Samples","panel":"Items"}""",
                """{"t":1010,"n":2,"type":"cmd_start","command":"SCRIPT","origin":"ribbon","via":"RUN_BTN","script":false}""",
                """{"t":1020,"n":3,"type":"prompt","kind":"string","input":false,"message":"Enter script file...","status":"OK","value":"c:/x/chain.scr"}""",
                """{"t":1050,"n":4,"type":"cmd_end","command":"SCRIPT"}""",
                """{"t":2750,"n":5,"type":"cmd_start","command":"ADDITEM","origin":"typed/other","script":true}""",
                """{"t":2800,"n":6,"type":"modal_enter","title":"","main":false}""",
                """{"t":2900,"n":7,"type":"modal_title","title":"Flash","main":false}""",
            };
            if (flashed) l.Add("""{"t":3000,"n":8,"type":"modal_flash","for":6}""");
            l.Add("""{"t":3010,"n":9,"type":"modal_leave"}""");
            l.Add("""{"t":3100,"n":10,"type":"prompt","kind":"point","input":true,"by":"mouse","message":"Specify point:","status":"OK","value":[1,2,0]}""");
            l.Add("""{"t":3200,"n":11,"type":"cmd_end","command":"ADDITEM"}""");
            return Parse(l);
        }
        // a person's answer before any dialog splits the step (the replay could not give it); a dialog that flashed before it is no dialog
        Assert.NotEmpty(Plan.ScriptChain(Events(false)));
        Assert.Empty(Plan.ScriptChain(Events(true)));
    }

    static byte[] Pixels(int w, int h, Func<int, int, (byte B, byte G, byte R)> color)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { var c = color(x, y); int i = (y * w + x) * 4; px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
        return px;
    }

    [Fact]
    public void A_flat_dialog_body_is_blank_even_with_a_painted_title_bar_and_a_dithered_edge()
    {
        // title bar rows 0..31 have text colors; the body is flat 240 gray, with one slightly off pixel (below the two lowest bits: the same color)
        var px = Pixels(300, 200, (x, y) => y < 32 ? ((byte)(x % 7 * 30), (byte)90, (byte)10) : (x == 150 && y == 100 ? ((byte)241, (byte)241, (byte)241) : ((byte)240, (byte)240, (byte)240)));
        Assert.True(ShotImage.IsBlank(px, 300, 200));
    }

    [Fact]
    public void A_dialog_with_controls_is_not_blank()
    {
        var px = Pixels(300, 200, (x, y) => y < 32 ? ((byte)200, (byte)200, (byte)200)
            : x is > 40 and < 120 && y is > 80 and < 120 ? ((byte)255, (byte)255, (byte)255)      // a white tree
            : x is > 200 and < 260 && y is > 60 and < 90 ? ((byte)180, (byte)120, (byte)60)       // a button
            : ((byte)240, (byte)240, (byte)240));
        Assert.False(ShotImage.IsBlank(px, 300, 200));
    }

    [Fact]
    public void Two_colors_are_blank_a_third_is_content_and_a_bad_buffer_counts_as_blank()
    {
        Assert.True(ShotImage.IsBlank(Pixels(200, 200, (x, y) => x < 100 ? ((byte)10, (byte)10, (byte)10) : ((byte)240, (byte)240, (byte)240)), 200, 200));
        Assert.False(ShotImage.IsBlank(Pixels(200, 200, (x, y) => x < 60 ? ((byte)10, (byte)10, (byte)10) : x < 120 ? ((byte)100, (byte)10, (byte)10) : ((byte)240, (byte)240, (byte)240)), 200, 200));
        Assert.True(ShotImage.IsBlank(new byte[10], 200, 200));
    }

    [Fact]
    public void The_scheduler_keeps_a_held_request_queued_and_drops_it_only_when_it_is_too_old()
    {
        var s = new ShotScheduler();
        s.Enqueue(new ShotRequest(5, "prompt", ShotTarget.Window, 1000));
        bool hold = true;
        Assert.Empty(s.TakeReady(1200, mayTake: _ => !hold));
        Assert.Equal(1, s.Pending);
        hold = false;
        Assert.Single(s.TakeReady(1400, mayTake: _ => !hold));
    }

    [Fact]
    public void A_hold_never_makes_a_request_too_old_the_age_limit_counts_from_the_end_of_the_hold()
    {
        var s = new ShotScheduler();
        s.Enqueue(new ShotRequest(6, "prompt", ShotTarget.Window, 5000));
        bool hold = true;
        for (long t = 5000; t < 5000 + 3 * ShotScheduler.MaxWaitMs; t += 250) Assert.Empty(s.TakeReady(t, mayTake: _ => !hold));   // held far longer than the age limit
        Assert.Equal(1, s.Pending);
        hold = false;
        Assert.Single(s.TakeReady(5000 + 3 * ShotScheduler.MaxWaitMs, mayTake: _ => !hold));   // taken, not dropped for its age
        Assert.Equal(0, s.Dropped);
        s.Enqueue(new ShotRequest(7, "prompt", ShotTarget.Window, 20000));
        Assert.Empty(s.TakeReady(20000 + ShotScheduler.MaxWaitMs + 1));                          // a request nobody holds is still dropped when old
        Assert.Equal(1, s.Dropped);
    }

    [Fact]
    public void A_dialog_request_keeps_the_window_it_is_for()
    {
        var r = ShotRules.Dialog(5, 1000, 4242);
        Assert.Equal(4242, r.Window);
        Assert.Equal(0, ShotRules.Dialog(5, 1000).Window);
        var s = new ShotScheduler();
        s.Enqueue(r);
        Assert.Equal(4242, s.TakeReady(1200).Single().Window);
    }

    [Fact]
    public void Force_ignores_the_hold_so_a_stopping_recording_still_gets_its_pictures()
    {
        var s = new ShotScheduler();
        s.Enqueue(new ShotRequest(5, "dialog", ShotTarget.Dialog, 1000));
        Assert.Single(s.TakeReady(1100, force: true, mayTake: _ => false));
    }

    [Fact]
    public void A_dialog_picture_that_found_a_blank_dialog_is_asked_for_again_inside_the_gap()
    {
        var s = new ShotScheduler();
        var r = new ShotRequest(5, "dialog", ShotTarget.Dialog, 1000);
        s.Enqueue(r);
        Assert.Single(s.TakeReady(1100));
        s.Untake(r, 1100);                       // blank: not painted yet
        Assert.Single(s.TakeReady(1200));        // 100 ms later it is tried again, not dropped as a duplicate of the attempt
        Assert.Equal(0, s.Pending);
    }

    [Fact]
    public void Early_takes_every_queued_palette_picture_before_its_due_time_each_for_its_own_click_and_leaves_the_gap_rule_alone()
    {
        var s = new ShotScheduler();
        s.Enqueue(new ShotRequest(10, "palette", ShotTarget.Window, 6000));
        s.Enqueue(new ShotRequest(13, "palette", ShotTarget.Window, 6100));
        Assert.Empty(s.TakeReady(5900));                                                             // not due: nothing
        Assert.Empty(s.TakeReady(5900, early: true, mayTake: _ => false));                           // held: nothing
        var got = s.TakeReady(5900, early: true, mayTake: r => r.Kind == "palette");
        Assert.Equal(new[] { 10, 13 }, got.Select(r => r.N));                                        // two clicks, two pictures: none dropped as a duplicate
        Assert.Equal(0, s.Dropped);
        // the early pictures did not stamp the gap: the next click's own request, due 100 ms later, is taken
        s.Enqueue(new ShotRequest(15, "palette", ShotTarget.Window, 6000));
        Assert.Single(s.TakeReady(6000));
    }

    [Fact]
    public void HasPending_looks_for_a_kind_without_taking_anything()
    {
        var s = new ShotScheduler();
        Assert.False(s.HasPending("palette"));
        s.Enqueue(new ShotRequest(1, "prompt", ShotTarget.Window, 1000));
        Assert.False(s.HasPending("palette"));
        s.Enqueue(new ShotRequest(2, "palette", ShotTarget.Window, 1000));
        Assert.True(s.HasPending("palette"));
        Assert.Equal(2, s.Pending);
    }

    [Fact]
    public void Cancel_removes_only_the_dialog_pictures_of_the_flash()
    {
        var s = new ShotScheduler();
        s.Enqueue(new ShotRequest(15, "dialog", ShotTarget.Dialog, 5000));
        s.Enqueue(new ShotRequest(16, "dialog", ShotTarget.Dialog, 5000));
        s.Enqueue(new ShotRequest(15, "cmd-end", ShotTarget.Window, 5000));
        Assert.Equal(1, s.Cancel(ShotTarget.Dialog, 15, 0));
        Assert.Equal(2, s.Pending);
    }

    // ---- readers ------------------------------------------------------------------------------------------------------------------

    /// <summary>DRAWFLASH as the recorder wrote it: a dialog the person answered, a point, then the same dialog again for a moment (title, empty values, leave), and the end.</summary>
    static List<string> Recording(bool flashMarked)
    {
        var l = new List<string>
        {
            """{"t":1000,"n":1,"type":"ribbon","id":"X","text":"Go","macro":"^C^CDRAWFLASH","tabId":"T1","tab":"S","panel":"P"}""",
            """{"t":1010,"n":2,"type":"cmd_start","command":"DRAWFLASH","origin":"ribbon","via":"X","script":false}""",
            """{"t":1100,"n":3,"type":"modal_enter","title":"","main":false}""",
            """{"t":1400,"n":4,"type":"modal_title","title":"Choose part","windowClass":"WindowsForms10.Window.8.app.0.141b42a_r8_ad1","main":false}""",
            """{"t":2400,"n":5,"type":"dialog_click","for":4,"button":"Add","id":0,"fields":[]}""",
            """{"t":2410,"n":6,"type":"dialog_values","for":4,"title":"Choose part","source":"win32","closedWith":"click","fields":[],"read":5}""",
            """{"t":2411,"n":7,"type":"modal_leave"}""",
            """{"t":3000,"n":8,"type":"prompt","kind":"point","input":true,"by":"mouse","message":"Insertion point:","status":"OK","value":[1,2,0]}""",
            """{"t":3100,"n":9,"type":"modal_enter","title":"Autodesk AutoCAD 2026 - [Drawing1.dwg]","main":true}""",
            """{"t":3500,"n":10,"type":"modal_title","title":"Choose part","windowClass":"WindowsForms10.Window.8.app.0.141b42a_r8_ad1","main":false}""",
            """{"t":3560,"n":11,"type":"dialog_values","for":10,"title":"Choose part","source":"win32","fields":[],"read":5}""",
        };
        if (flashMarked) l.Add("""{"t":3561,"n":12,"type":"modal_flash","for":9,"ms":300}""");
        l.Add("""{"t":3562,"n":13,"type":"modal_leave"}""");
        l.Add("""{"t":3600,"n":14,"type":"cmd_end","command":"DRAWFLASH"}""");
        return l;
    }

    static List<Step> Build(IEnumerable<string> lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    [Fact]
    public void An_old_recording_of_the_same_flash_still_has_two_dialog_steps()
    {
        Assert.Equal(new[] { "ribbon", "dialog", "dialog" }, Build(Recording(flashMarked: false)).Select(s => s.Kind));
    }

    [Fact]
    public void A_flash_marked_by_the_recorder_is_no_step_even_with_a_title_and_values_and_the_first_dialog_stays()
    {
        var steps = Build(Recording(flashMarked: true));
        Assert.Equal(new[] { "ribbon", "dialog" }, steps.Select(s => s.Kind));
        Assert.Equal("Choose part", steps[1].DialogTitle);
        Assert.Single(steps[1].Clicks);   // the answered dialog keeps its click
    }

    [Fact]
    public void The_steps_md_has_one_dialog_for_the_command_with_a_flash()
    {
        var s = new RecordingSession { Name = "t" };
        foreach (var l in Recording(true))
        {
            var root = JsonDocument.Parse(l).RootElement.Clone();
            s.Events.Add(new RecordingEvent { T = root.GetProperty("t").GetInt64(), N = root.TryGetProperty("n", out var nn) ? nn.GetInt64() : null, Type = root.GetProperty("type").GetString()!, Raw = root });
        }
        RecordingReader.BuildSteps(s);
        var md = ScriptWriter.Build(s, null, Path.GetTempPath(), new List<string>());
        Assert.NotNull(Retrace.Core.ScriptRef.DialogStepNumber(md, 1));   // the replay counts dialog steps through this
        Assert.Null(Retrace.Core.ScriptRef.DialogStepNumber(md, 2));
    }

    [Fact]
    public void A_flash_marker_for_another_dialog_does_not_touch_this_one()
    {
        var l = Recording(false);
        l.Insert(l.Count - 2, """{"t":3561,"n":12,"type":"modal_flash","for":3,"ms":10}""");   // names the FIRST dialog's enter: that one is dropped, the second stays
        Assert.Equal(new[] { "ribbon", "dialog" }, Build(l).Select(s => s.Kind));
    }
}
