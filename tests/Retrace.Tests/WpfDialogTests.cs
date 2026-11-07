using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Buttons of a WPF dialog (a window with no child windows): the text without its access key, the access key, which of equal texts a button is, how a
/// recording that mixes WPF and Win32 dialogs nests and replays as segments. Synthetic data only.</summary>
public class WpfDialogTests
{
    [Theory]
    [InlineData("_Add", "Add")]
    [InlineData("Add _Row", "Add Row")]
    [InlineData("A__B", "A_B")]
    [InlineData("a_b_c", "ab_c")]            // only the first single underscore is the access key's marker
    [InlineData("_A__B_c", "A_B_c")]
    [InlineData("Options...", "Options...")]
    [InlineData("_Name:", "Name")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void StripAccess_drops_the_underscore_that_marks_the_key(string? raw, string text) => Assert.Equal(text, WpfPresses.StripAccess(raw));

    [Fact]
    public void Equal_texts_are_told_apart_by_their_place_among_them()
    {
        var texts = new[] { "Add", "Options...", "add", "OK" };
        Assert.Equal(1, WpfPresses.IndexAmong(texts, 0));
        Assert.Equal(2, WpfPresses.IndexAmong(texts, 2));
        Assert.Equal(1, WpfPresses.IndexAmong(texts, 3));
        Assert.Equal(1, WpfPresses.IndexAmong(texts, 9));   // not there: the first
        Assert.Equal(2, WpfPresses.Find(texts, "Add", 2));
        Assert.Equal(0, WpfPresses.Find(texts, "ADD", 0));   // 0 counts as the first
        Assert.Equal(-1, WpfPresses.Find(texts, "Add", 3));
        Assert.Equal(-1, WpfPresses.Find(texts, "Nope", 1));
    }

    [Fact]
    public void A_recording_says_wpf_and_clicks_are_replayed_in_process_an_older_uia_recording_keeps_its_path()
    {
        Assert.True(WpfPresses.ReplayInProcess("wpf", false));
        Assert.True(WpfPresses.ReplayInProcess("uia", true));
        Assert.False(WpfPresses.ReplayInProcess("uia", false));
        Assert.False(WpfPresses.ReplayInProcess("win32", false));
        Assert.False(WpfPresses.ReplayInProcess(null, false));
    }

    // ---- a WPF main form between WinForms ones: the plan and the segments --------------------------------------------------

    static List<Step> Build(params string[] lines) => Plan.Build(lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.Clone()));

    const string Header = """{"type":"cmd_start","t":0,"n":1,"name":"MIXED"}""";

    [Fact]
    public void WinForms_to_WPF_to_WinForms_nests_to_any_depth_and_each_level_keeps_its_source_and_clicks()
    {
        var steps = Build(
            Header,
            """{"t":100,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":110,"n":3,"type":"modal_title","title":"Main (WinForms)"}""",
            """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Details...","index":1,"fields":[{"key":"id:1","label":"Name","kind":"edit","before":"","after":"A"}],"read":1}""",
            """{"t":1100,"n":5,"type":"modal_enter","title":"Details (WPF)"}""",
            """{"t":1500,"n":6,"type":"dialog_click","for":5,"button":"Add","index":2,"id":0,"fields":[{"key":"name:Qty","label":"Qty","kind":"edit","before":"1","after":"5"}],"read":2}""",
            """{"t":1600,"n":7,"type":"dialog_click","for":5,"button":"More...","index":1,"fields":[],"read":2}""",
            """{"t":1700,"n":8,"type":"modal_enter","title":"More (WinForms)"}""",
            """{"t":1900,"n":9,"type":"dialog_values","for":8,"title":"More (WinForms)","source":"win32","parent":5,"fields":[{"key":"id:2","label":"Depth","kind":"edit","before":"1","after":"9"}],"read":1,"closedWith":"ok"}""",
            """{"t":2000,"n":10,"type":"dialog_values","for":5,"title":"Details (WPF)","source":"wpf","parent":3,"fields":[{"key":"name:Note","label":"Note","kind":"edit","before":"","after":"x"}],"read":3,"closedWith":"ok"}""",
            """{"t":3000,"n":11,"type":"dialog_values","for":3,"title":"Main (WinForms)","source":"win32","fields":[],"read":2,"closedWith":"ok"}""");
        var main = steps.Single(s => s.Kind == "dialog");
        Assert.Equal("Main (WinForms)", main.DialogTitle);
        var wpf = main.Clicks.Single().Nested.Single();
        Assert.Equal(("Details (WPF)", "wpf"), (wpf.DialogTitle, wpf.ValuesSource));
        Assert.Equal(new[] { "Add", "More..." }, wpf.Clicks.Select(c => c.Button));
        Assert.Equal(new[] { 2, 1 }, wpf.Clicks.Select(c => c.Index));
        var inner = wpf.Clicks[1].Nested.Single();
        Assert.Equal(("More (WinForms)", "win32"), (inner.DialogTitle, inner.ValuesSource));
        Assert.Equal("9", inner.Values!.Single().After);
        // the replay does it in segments: the WPF dialog's two clicks, then its close, with the sub-sub form under the second click
        var segments = DialogSegments.Of(wpf, closingValues: true);
        Assert.Equal(new string?[] { "Add", "More...", null }, segments.Select(s => s.Button));
        Assert.Equal("5", segments[0].Values.Single().After);
        Assert.Single(segments[1].Nested);
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(wpf));
    }

    [Fact]
    public void A_WPF_dialog_with_only_clicks_is_work_for_the_replay()
    {
        var steps = Build(
            Header,
            """{"t":100,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":110,"n":3,"type":"modal_title","title":"Panel"}""",
            """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Reset","index":1,"fields":[],"read":2}""",
            """{"t":2000,"n":5,"type":"dialog_values","for":3,"title":"Panel","source":"wpf","fields":[],"read":2,"closedWith":"ok"}""");
        var d = steps.Single(s => s.Kind == "dialog");
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(d));
        Assert.Equal("wpf", d.ValuesSource);
        Assert.Equal("Reset", d.Clicks.Single().Button);
    }

    [Fact]
    public void An_old_palette_recording_with_the_underscore_still_matches_the_live_text()
    {
        Assert.True(WpfPresses.SameText("_Add", "Add"));
        Assert.True(WpfPresses.SameText("Add", "_Add"));
        Assert.True(WpfPresses.SameText("my_btn", "my_btn"));   // an automation name, shown as it is on both sides
        Assert.False(WpfPresses.SameText("Add", "Delete"));
        Assert.Equal(1, WpfPresses.Find(new[] { "Insert", "Add" }, "_Add", 1));
    }

    static IReadOnlyList<string> C(params string[] names) => names;

    [Fact]
    public void Only_real_push_buttons_are_presses()
    {
        var none = new List<IReadOnlyList<string>>();
        Assert.True(WpfPresses.IsPushButton(C("Button", "ButtonBase", "ContentControl"), none));
        Assert.False(WpfPresses.IsPushButton(C("CheckBox", "ToggleButton", "ButtonBase"), none));
        Assert.False(WpfPresses.IsPushButton(C("RepeatButton", "ButtonBase"), none));
        Assert.False(WpfPresses.IsPushButton(C("DataGridColumnHeader", "ButtonBase"), none));
        Assert.False(WpfPresses.IsPushButton(C("GridViewColumnHeader", "ButtonBase"), none));
        // a button that is part of another control's template: a scroll bar's line button, a combo box's drop-down toggle, a date picker's button
        Assert.False(WpfPresses.IsPushButton(C("Button", "ButtonBase"), new[] { C("ScrollBar", "RangeBase") }));
        Assert.False(WpfPresses.IsPushButton(C("Button", "ButtonBase"), new[] { C("DatePicker", "Control"), C("Window") }));
        Assert.False(WpfPresses.IsPushButton(C("Button", "ButtonBase"), new[] { C("Slider", "RangeBase") }));
        // a button in the template of some other custom control is a push button
        Assert.True(WpfPresses.IsPushButton(C("Button", "ButtonBase"), new[] { C("MyToolbar", "Control") }));
    }

    [Fact]
    public void The_close_button_is_the_default_or_cancel_button_before_the_one_with_the_text()
    {
        var b = new[] { new WpfPresses.CloseCandidate("Apply", true, false), new WpfPresses.CloseCandidate("Close", false, true), new WpfPresses.CloseCandidate("OK", false, false), new WpfPresses.CloseCandidate("Cancel", false, false) };
        Assert.Equal(0, WpfPresses.PickClose(b, true));    // IsDefault "Apply", not the button called OK
        Assert.Equal(1, WpfPresses.PickClose(b, false));   // IsCancel "Close", not the button called Cancel
        var plain = new[] { new WpfPresses.CloseCandidate("OK", false, false), new WpfPresses.CloseCandidate("_Cancel", false, false) };
        Assert.Equal(0, WpfPresses.PickClose(plain, true));
        Assert.Equal(1, WpfPresses.PickClose(plain, false));
        Assert.Equal(-1, WpfPresses.PickClose(new[] { new WpfPresses.CloseCandidate("Add", false, false) }, false));   // none: the caller closes the window
    }

    [Fact]
    public void A_click_with_no_input_before_it_is_from_code_whatever_the_clock_says()
    {
        var c = new WpfClickCorrelator();
        var a = new object();
        Assert.False(c.IsPress(a, 0));
        Assert.False(c.IsPress(a, long.MaxValue));   // no overflow: "no input yet" is not a very old time
        Assert.False(c.IsPress(a, 1_000_000));
    }

    [Fact]
    public void A_mouse_press_counts_for_the_same_button_however_long_it_was_held()
    {
        var c = new WpfClickCorrelator();
        object add = new(), other = new();
        c.MouseDown(add);
        Assert.True(c.IsPress(add, 2500));        // held 2.5 s, still down: a ClickMode Press button or a slow release
        c.MouseUp(3000);
        Assert.True(c.IsPress(add, 3010));        // the Click that follows the button-up
        Assert.False(c.IsPress(add, 3020));       // used up: a second Click is from code
        c.MouseDown(add); c.MouseUp(4000);
        Assert.False(c.IsPress(other, 4010));     // another button was not pressed
        Assert.False(c.IsPress(add, 9000));       // long after the button-up
        c.MouseDown(null); c.MouseUp(10000);      // a press that was not on a button
        Assert.False(c.IsPress(add, 10010));
    }

    [Fact]
    public void A_key_counts_while_it_is_down_and_a_moment_after_it_comes_up()
    {
        var c = new WpfClickCorrelator();
        var b = new object();
        c.KeyDown(1000);
        Assert.True(c.IsPress(b, 4000));          // Space held for 3 s, the Click comes at the key-up
        c.KeyUp(4000);
        Assert.True(c.IsPress(b, 4300));
        Assert.False(c.IsPress(b, 4000 + WpfClickCorrelator.KeyGraceMs + 1));
        c.KeyDown(5000);
        c.MouseDown(null);                        // a mouse press ends a key whose key-up was never seen
        Assert.False(c.IsPress(b, 9000));
    }
}
