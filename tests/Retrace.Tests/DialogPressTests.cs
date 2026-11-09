using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>What the recorder decides about presses in a dialog (pure parts): which window is a nested dialog, when a dialog was closed by a click, which press is a click, the tracked
/// dialogs as a stack (one entry per window, a late EnterModal taken over), and how the replay result tells what a person did.</summary>
public class DialogPressTests
{
    static List<Step> Build(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    // ---- the nested-dialog gate ------------------------------------------------------------------------------------------

    [Fact]
    public void A_window_over_a_dialog_with_no_click_before_it_is_no_nested_dialog()
    {
        var t = new ClickTrail();
        Assert.False(t.MayOpenNested(500));   // OK pressed, a validation box ("Name already exists"): no click was recorded
        t.Input();                            // the OK press and whatever follows is input
        Assert.False(t.MayOpenNested(900));
    }

    [Fact]
    public void A_window_right_after_a_click_is_its_nested_dialog_and_the_next_one_waits_for_it_to_close()
    {
        var t = new ClickTrail();
        t.Input();                            // the press goes down: input; the click is complete when the button comes up
        t.Clicked("New...", 1000);
        Assert.True(t.MayOpenNested(1300));
        Assert.True(t.MayOpenNested(1000 + DialogClickRules.NestedWithinMs));
        Assert.False(t.MayOpenNested(1001 + DialogClickRules.NestedWithinMs));   // long after: not that click's
        t.NestedOpened();
        Assert.False(t.MayOpenNested(1300));  // one at a time: it is open
        t.Clicked("New...", 5000);
        Assert.True(t.MayOpenNested(5100));   // a new click opens the gate again
    }

    [Fact]
    public void A_second_window_right_after_the_first_nested_dialog_closed_is_the_clicks_next_nested_dialog()
    {
        // Clear > "Remove all 3 items?" (answered 22 s later) > "3 items removed": each opens after the one before it closed, with no input to the dialog under them in between
        var t = new ClickTrail();
        t.Clicked("Clear", 1000);
        Assert.True(t.MayOpenNested(1100));
        t.NestedOpened();
        t.NestedClosed(23000);                                                  // Yes, 22 s after the click
        Assert.True(t.MayOpenNested(23100));                                    // the info box
        Assert.True(t.MayOpenNested(23000 + DialogClickRules.NestedWithinMs));
        Assert.False(t.MayOpenNested(23001 + DialogClickRules.NestedWithinMs)); // long after the last one closed
        t.NestedOpened(); t.NestedClosed(24000);
        Assert.True(t.MayOpenNested(24100));                                    // and a third, by the same rule
        t.Input();                                                              // the person did something in the dialog under them: nothing opens after that is theirs
        Assert.False(t.MayOpenNested(24100));
        Assert.False(new ClickTrail().MayOpenNested(100));                      // no click at all
    }

    [Fact]
    public void Other_input_after_a_click_closes_the_gate()
    {
        var t = new ClickTrail();
        t.Clicked("Add", 1000);
        t.Input();                            // a key, another press: a window that opens now is not the click's (Help, a double-click in a list, a validation box)
        Assert.False(t.MayOpenNested(1200));
    }

    [Fact]
    public void A_nested_dialog_is_attached_to_its_click_only_when_it_opened_within_the_bound_of_it_or_of_the_nested_dialog_before_it()
    {
        Assert.True(DialogClickRules.NestedBelongsTo(1000, null, 1300));
        Assert.True(DialogClickRules.NestedBelongsTo(1000, null, 1000 + DialogClickRules.NestedWithinMs));
        Assert.False(DialogClickRules.NestedBelongsTo(1000, null, 999));
        Assert.False(DialogClickRules.NestedBelongsTo(1000, null, 1001 + DialogClickRules.NestedWithinMs));
        // the click already had a nested dialog that closed at 23000: the next one counts from there
        Assert.True(DialogClickRules.NestedBelongsTo(1000, 23000, 23100));
        Assert.True(DialogClickRules.NestedBelongsTo(1000, 23000, 23000 + DialogClickRules.NestedWithinMs));
        Assert.False(DialogClickRules.NestedBelongsTo(1000, 23000, 23001 + DialogClickRules.NestedWithinMs));
        Assert.False(DialogClickRules.NestedBelongsTo(1000, 23000, 22999));
    }

    [Fact]
    public void A_duplicate_name_in_New_Text_Style_is_three_nested_dialogs_of_the_click_in_the_order_the_person_met_them()
    {
        // STYLE > New...: the name is taken, AutoCAD closes the dialog, shows "Name Already Exists" (answered with Close) and shows New Text Style again: each opens after the one before it
        // closed, with no input to Text Style in between, so each is a nested dialog of the New... click, in order
        var steps = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"STYLE","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Text Style"}""",
            """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"New...","fields":[],"read":12}""",
            """{"t":1100,"n":5,"type":"modal_enter","title":"New Text Style"}""",
            """{"t":3000,"n":6,"type":"dialog_values","for":5,"title":"New Text Style","source":"win32","parent":3,"fields":[{"key":"id:1362","label":"Style Name","kind":"edit","before":"style1","after":"Standard"}],"read":1,"closedWith":"ok"}""",
            """{"t":3010,"n":7,"type":"modal_leave"}""",
            """{"t":3300,"n":8,"type":"modal_enter","title":"Name Already Exists"}""",
            """{"t":8000,"n":9,"type":"dialog_click","for":8,"button":"Close","fields":[],"read":0}""",
            """{"t":8050,"n":10,"type":"dialog_values","for":8,"title":"Name Already Exists","source":"win32","parent":3,"fields":[],"read":0,"closedWith":"click","button":"Close"}""",
            """{"t":8300,"n":11,"type":"modal_enter","title":"New Text Style"}""",
            """{"t":15000,"n":12,"type":"dialog_values","for":11,"title":"New Text Style","source":"win32","parent":3,"fields":[{"key":"id:1362","label":"Style Name","kind":"edit","before":"Standard","after":"Notes3"}],"read":1,"closedWith":"ok"}""",
            """{"t":15010,"n":13,"type":"modal_leave"}""",
            """{"t":20000,"n":14,"type":"dialog_values","for":3,"title":"Text Style","source":"win32","fields":[],"read":12,"closedWith":"cancel"}""");
        var dlg = Assert.Single(steps, s => s.Kind == "dialog");
        var nested = dlg.Clicks.Single().Nested;
        Assert.Equal(new[] { "New Text Style", "Name Already Exists", "New Text Style" }, nested.Select(n => n.DialogTitle));
        Assert.Equal(new[] { "Standard", "Notes3" }, new[] { nested[0].Values!.Single().After, nested[2].Values!.Single().After });
        Assert.True(DialogSegments.LastClickCloses(nested[1]));
        Assert.Equal(3, DialogSegments.Of(dlg, closingValues: false)[0].Nested.Count);   // the replay meets them in that order
    }

    [Fact]
    public void Clear_then_Yes_then_an_info_box_are_two_nested_dialogs_of_one_click()
    {
        var steps = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWITEMS","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw items"}""",
            """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Clear","fields":[],"read":2}""",
            """{"t":1100,"n":5,"type":"modal_enter","title":"Remove items","windowClass":"#32770"}""",
            """{"t":23000,"n":6,"type":"dialog_click","for":5,"button":"Yes","fields":[],"read":0}""",
            """{"t":23050,"n":7,"type":"dialog_values","for":5,"title":"Remove items","source":"win32","parent":3,"fields":[],"read":0,"closedWith":"click","button":"Yes"}""",
            """{"t":23300,"n":8,"type":"modal_enter","title":"Items removed","windowClass":"#32770"}""",
            """{"t":26000,"n":9,"type":"dialog_values","for":8,"title":"Items removed","source":"win32","parent":3,"fields":[],"read":0,"closedWith":"ok"}""",
            """{"t":30000,"n":10,"type":"dialog_values","for":3,"title":"Draw items","source":"win32","fields":[],"read":2,"closedWith":"ok"}""");
        var dlg = Assert.Single(steps, s => s.Kind == "dialog");
        var nested = dlg.Clicks.Single().Nested;
        Assert.Equal(new[] { "Remove items", "Items removed" }, nested.Select(n => n.DialogTitle));
        Assert.Equal(AutoMode.MessageOk, DialogSegments.ModeOf(nested[1]));
        Assert.Equal("A dialog opens: Draw items. Press Clear (nested dialog \"Remove items\": press Yes; nested dialog \"Items removed\": close it with OK), then close it with OK.", DialogSegments.ManualMessage("Draw items", null, null, dlg));
        // a dialog that opens long after the last one closed is no part of the click
        var late = Build(
            """{"t":110,"n":2,"type":"modal_enter","title":"Draw items"}""",
            """{"t":1000,"n":4,"type":"dialog_click","for":2,"button":"Clear","fields":[],"read":2}""",
            """{"t":1100,"n":5,"type":"modal_enter","title":"Remove items"}""",
            """{"t":2000,"n":7,"type":"dialog_values","for":5,"title":"Remove items","source":"win32","parent":2,"fields":[],"read":0,"closedWith":"click","button":"Yes"}""",
            """{"t":2000,"n":8,"type":"modal_enter","title":"Items removed"}""",
            """{"t":5000,"n":9,"type":"dialog_values","for":8,"title":"Items removed","source":"win32","parent":2,"fields":[],"read":0,"closedWith":"ok"}""",
            """{"t":16000,"n":10,"type":"modal_enter","title":"Much later"}""",
            """{"t":16500,"n":11,"type":"dialog_values","for":10,"title":"Much later","source":"win32","parent":2,"fields":[],"read":0,"closedWith":"ok"}""");
        Assert.Equal(2, late.Single(s => s.Clicks.Count > 0).Clicks.Single().Nested.Count);
    }

    [Fact]
    public void A_validation_box_announced_long_after_the_last_click_stays_out_of_the_click_and_the_dialog_is_one_step()
    {
        // Add clicked at t=1000; much later OK, a message box over the dialog (announced, with parent), the name is fixed, OK again: ONE dialog step; the box is not the Add button's nested dialog
        var steps = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWITEMS","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Items"}""",
            """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Add","fields":[{"key":"name:n","label":"Name","kind":"edit","before":"","after":"A"}],"read":2}""",
            """{"t":30000,"n":5,"type":"modal_enter","title":"Items"}""",
            """{"t":31000,"n":6,"type":"dialog_values","for":5,"title":"Items","source":"win32","parent":3,"fields":[],"read":0}""",
            """{"t":40000,"n":7,"type":"dialog_values","for":3,"title":"Items","source":"win32","fields":[],"read":2,"closedWith":"ok"}""");
        var dialogs = steps.Where(s => s.Kind == "dialog").ToList();
        Assert.Empty(dialogs[0].Clicks.SelectMany(c => c.Nested));   // not attached to Add
        Assert.Equal("ok", dialogs[0].ClosedWith);
    }

    [Fact]
    public void A_recording_without_the_unprompted_window_is_one_dialog_step()
    {
        // what the recorder writes now: the validation box over OK is not recorded at all
        var steps = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"STYLE","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Text Style"}""",
            """{"t":9000,"n":4,"type":"dialog_values","for":3,"title":"Text Style","source":"win32","fields":[{"key":"id:1","label":"Name","kind":"edit","before":"a","after":"b"}],"read":2,"closedWith":"ok"}""");
        Assert.Single(steps, s => s.Kind == "dialog");
    }

    // ---- closing ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_dialog_that_went_away_after_its_last_click_with_no_other_input_was_closed_by_it_however_long_it_took()
    {
        var t = new ClickTrail();
        t.Clicked("Close", 5000);
        Assert.Equal(("click", "Close"), t.Final(null, 5400));
        Assert.Equal(("click", "Close"), t.Final(null, 6500));                                           // a button that takes 1.5 s to close the dialog
        Assert.Equal(("click", "Close"), t.Final(null, 5000 + DialogClickRules.ClosedByClickMaxMs));
        Assert.Equal((null, null), t.Final(null, 5001 + DialogClickRules.ClosedByClickMaxMs));          // a sanity cap: much later it went away some other way
    }

    [Fact]
    public void What_OK_or_Cancel_or_the_title_bar_said_wins_and_other_input_means_it_was_not_the_click()
    {
        var t = new ClickTrail();
        t.Clicked("Apply", 5000);
        Assert.Equal(("ok", null), t.Final("ok", 5200));
        Assert.Equal(("cancel", null), t.Final("cancel", 5200));
        t.Input();
        Assert.Equal((null, null), t.Final(null, 5200));
        Assert.Equal((null, null), new ClickTrail().Final(null, 100));   // no click at all
    }

    [Fact]
    public void A_dialog_closed_with_click_replays_by_pressing_that_click_and_nothing_after_it()
    {
        var steps = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"STYLE","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Text Style"}""",
            """{"t":4000,"n":4,"type":"dialog_click","for":3,"button":"Close","fields":[],"read":12}""",
            """{"t":4050,"n":5,"type":"dialog_values","for":3,"title":"Text Style","source":"win32","fields":[],"read":12,"closedWith":"click","button":"Close"}""");
        var dlg = steps.Single(s => s.Kind == "dialog");
        Assert.True(DialogSegments.LastClickCloses(dlg));
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(dlg));
        // an older recording: clicks and no word on how it closed: not guessed
        var old = Build(
            """{"t":100,"n":1,"type":"modal_enter","title":"Text Style"}""",
            """{"t":4000,"n":2,"type":"dialog_click","for":1,"button":"Close","fields":[],"read":12}""",
            """{"t":4050,"n":3,"type":"dialog_values","for":1,"title":"Text Style","source":"win32","fields":[],"read":12}""").Single(s => s.Kind == "dialog");
        Assert.False(DialogSegments.LastClickCloses(old));
        // ... and with no word on how it closed and nothing set after the last click a person finishes it: never an OK nobody pressed
        Assert.Equal(AutoMode.None, DialogSegments.ModeOf(old));
        // values set after the last click, though, are a change like any other (a closedWith left out with changed values counts as OK, as before clicks)
        var withValues = Build(
            """{"t":100,"n":1,"type":"modal_enter","title":"Text Style"}""",
            """{"t":4000,"n":2,"type":"dialog_click","for":1,"button":"Apply","fields":[],"read":12}""",
            """{"t":4050,"n":3,"type":"dialog_values","for":1,"title":"Text Style","source":"win32","fields":[{"key":"id:1","label":"Height","kind":"edit","before":"1","after":"2"}],"read":12}""").Single(s => s.Kind == "dialog");
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(withValues));
    }

    [Theory]
    [InlineData("click", 1, true)]
    [InlineData("click", 3, true)]
    [InlineData("click", 0, false)]     // no click, so nothing closed it
    [InlineData("ok", 2, false)]
    [InlineData("cancel", 2, false)]
    [InlineData(null, 2, false)]
    public void One_rule_says_whether_the_last_click_closed_the_dialog(string? closedWith, int clicks, bool closes)
    {
        Assert.Equal(closes, DialogSegments.LastClickCloses(closedWith, clicks));
        var step = new Step { Kind = "dialog", ClosedWith = closedWith };
        for (int i = 0; i < clicks; i++) step.Clicks.Add(new DialogClick { Button = "B" + i });
        Assert.Equal(closes, DialogSegments.LastClickCloses(step));
        var segments = DialogSegments.Of(step, closingValues: true);
        Assert.Equal(closes, DialogSegments.FallbackMessage("T", null, null, segments, new List<SegmentProgress>(), closedWith, null).Contains(", then close it with") == false && clicks > 0);
    }

    // ---- which press is a click ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(7L, 7L, 10, 10, 75, 25, true)]
    [InlineData(7L, 7L, 0, 0, 75, 25, true)]
    [InlineData(7L, 7L, 75, 10, 75, 25, false)]    // released outside the button (the button holds the mouse, so the up is still sent to it)
    [InlineData(7L, 7L, -3, 10, 75, 25, false)]
    [InlineData(7L, 8L, 10, 10, 75, 25, false)]    // up on another window
    [InlineData(0L, 0L, 10, 10, 75, 25, false)]
    public void A_press_is_a_click_only_when_the_button_comes_up_on_the_same_button(long down, long up, int x, int y, int w, int h, bool click) =>
        Assert.Equal(click, DialogClickRules.MouseUpCompletes(down, up, x, y, w, h));

    [Theory]
    [InlineData("&New...", 'N')]
    [InlineData("Set &Current", 'C')]
    [InlineData("A && B", null)]
    [InlineData("Save && &Close", 'C')]
    [InlineData("OK", null)]
    [InlineData("Trailing &", null)]
    [InlineData(null, null)]
    public void A_mnemonic_is_the_character_after_a_single_ampersand(string? text, char? expected) => Assert.Equal(expected, DialogClickRules.MnemonicOf(text));

    [Fact]
    public void A_mnemonic_presses_a_push_button_only_when_it_is_the_one_control_with_that_letter()
    {
        var controls = new List<(bool PushButton, bool Enabled, string? Text)>
        {
            (true, true, "&New..."), (true, true, "&Delete"), (true, false, "&Apply"), (false, true, "&Name:"), (false, true, "Style &Name:"),
            (false, true, "Show &preview"),
        };
        Assert.Equal(1, DialogClickRules.UniqueMnemonicButton('d', controls));      // Alt+D: Delete
        Assert.Null(DialogClickRules.UniqueMnemonicButton('N', controls));          // N is New... and two labels: the dialog manager cycles, not told apart
        Assert.Null(DialogClickRules.UniqueMnemonicButton('A', controls));          // the one with it is disabled
        Assert.Null(DialogClickRules.UniqueMnemonicButton('P', controls));          // a check box, not a push button
        Assert.Null(DialogClickRules.UniqueMnemonicButton('Z', controls));          // nobody
    }

    // ---- the tracked dialogs: one entry per window --------------------------------------------------------------------------

    sealed class Fake : IStackEntry
    {
        public long Window { get; set; }
        public bool Synthetic { get; init; }
        public bool Adopted { get; set; }
        public string Name = "";
    }

    [Fact]
    public void A_dialog_still_being_looked_for_and_the_innermost_one_found()
    {
        var s = new DialogStack<Fake>();
        Assert.Null(s.Innermost());
        var outer = new Fake { Name = "outer", Window = 10 };
        s.Push(outer);
        Assert.Same(outer, s.Innermost());
        Assert.False(s.Looking);
        var announced = new Fake { Name = "announced" };   // EnterModal, window not found yet
        s.Push(announced);
        Assert.True(s.Looking);
        Assert.Same(outer, s.Innermost());
        announced.Window = 11;
        Assert.False(s.Looking);
        Assert.Same(announced, s.Innermost());
        Assert.True(s.Has(11));
        Assert.False(s.Has(0));
    }

    [Fact]
    public void Nested_entries_whose_window_is_gone_leave_innermost_first_and_only_those()
    {
        var s = new DialogStack<Fake>();
        var outer = new Fake { Name = "outer", Window = 1 };
        var a = new Fake { Name = "a", Window = 2, Synthetic = true };
        var b = new Fake { Name = "b", Window = 3, Synthetic = true };
        s.Push(outer); s.Push(a); s.Push(b);
        var alive = new HashSet<long> { 1, 2 };
        Assert.Same(b, s.Innermost());
        Assert.Equal(new[] { "b" }, s.ReapGone(alive.Contains).Select(x => x.Name));
        Assert.Same(a, s.Innermost());   // input right after it closed goes to the dialog under it, not to the dead one
        Assert.Equal(new[] { "outer", "a" }, s.Items.Select(x => x.Name));
        alive.Clear();   // the outer window is gone too, but it is announced: it leaves by its LeaveModal, not here
        Assert.Equal(new[] { "a" }, s.ReapGone(alive.Contains).Select(x => x.Name));
        Assert.Equal(new[] { "outer" }, s.Items.Select(x => x.Name));
    }

    [Fact]
    public void A_leave_modal_closes_the_innermost_announced_dialog_never_one_found_by_its_window()
    {
        var s = new DialogStack<Fake>();
        var outer = new Fake { Name = "outer", Window = 1 };
        var inner = new Fake { Name = "inner", Window = 2 };
        var nested = new Fake { Name = "nested", Window = 3, Synthetic = true };
        s.Push(outer); s.Push(inner); s.Push(nested);
        // the nested one is still open (it leaves by itself); inner's window is gone: that is the one that left
        Assert.Equal("inner", s.PickForLeave(w => w != 2)!.Name);
        // none gone: the innermost announced one (its window may not be found yet)
        Assert.Equal("inner", s.PickForLeave(_ => true)!.Name);
        var unresolved = new Fake { Name = "unresolved" };
        s.Push(unresolved);
        Assert.Equal("unresolved", s.PickForLeave(_ => true)!.Name);
        Assert.Null(new DialogStack<Fake>().PickForLeave(_ => true));
    }

    [Fact]
    public void An_EnterModal_that_comes_first_is_resolved_normally_and_nothing_merges()
    {
        // order A: the dialog is announced (pushed, window not found), then its window is found; no nested entry tracks it
        var s = new DialogStack<Fake>();
        var outer = new Fake { Name = "outer", Window = 1 };
        s.Push(outer);
        var announced = new Fake { Name = "announced" };
        s.Push(announced);
        Assert.True(s.Looking);                      // a window found now is that one: the nested-dialog search waits
        Assert.Null(s.MergeInto(announced, 7));
        announced.Window = 7;                        // Resolve
        Assert.False(s.Looking);
        Assert.Same(announced, s.Innermost());
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void A_late_EnterModal_for_a_window_a_nested_entry_tracks_merges_into_it_and_leaves_no_second_entry()
    {
        // order B: the nested dialog was found by its window first; AutoCAD's EnterModal comes late, its entry is pushed, and when its window is looked for it is the nested entry's
        var s = new DialogStack<Fake>();
        s.Push(new Fake { Name = "outer", Window = 1 });
        var nested = new Fake { Name = "nested", Window = 7, Synthetic = true };
        s.Push(nested);
        var announced = new Fake { Name = "announced" };
        s.Push(announced);
        Assert.True(s.Looking);
        Assert.Same(nested, s.MergeInto(announced, 7));
        Assert.True(nested.Adopted);
        Assert.False(s.Contains(announced));         // popped: it must not block the nested-dialog search
        Assert.False(s.Looking);
        Assert.Equal(1, s.Items.Count(x => x.Window == 7));
        Assert.Same(nested, s.InnermostAdopted());   // its LeaveModal closes this entry
        Assert.Same(nested, s.Innermost());
    }

    [Fact]
    public void A_real_EnterModal_for_another_dialog_while_a_nested_one_is_in_front_leaves_the_nested_one_alone()
    {
        // S1 (found by its window) is in front; a real dialog D2 is announced and its window (9) is not S1's: no merge, D2 is its own entry, S1 stays open and tracked
        var s = new DialogStack<Fake>();
        s.Push(new Fake { Name = "outer", Window = 1 });
        var s1 = new Fake { Name = "S1", Window = 7, Synthetic = true };
        s.Push(s1);
        var d2 = new Fake { Name = "D2" };
        s.Push(d2);
        Assert.Null(s.MergeInto(d2, 9));
        d2.Window = 9;
        Assert.False(s1.Adopted);
        Assert.Same(d2, s.Innermost());
        Assert.Null(s.InnermostAdopted());
        // D2 leaves (LeaveModal): it is the one closed, S1 is not
        var alive = new HashSet<long> { 1, 7 };      // D2's window (9) is gone
        Assert.Same(d2, s.PickForLeave(alive.Contains));
        s.Remove(d2);
        Assert.Same(s1, s.Innermost());
        Assert.Empty(s.ReapGone(alive.Contains));    // S1 is still a window: not reaped
    }

    [Fact]
    public void Each_LeaveModal_closes_what_its_EnterModal_announced()
    {
        var p = new ModalPairs();
        var outer = p.Enter();   // the dialog
        var late = p.Enter();    // a nested one that was tracked already: found out when its window is looked for
        Assert.Equal(2, p.Open);
        p.Adopt(late);
        Assert.True(p.Leave());  // it leaves first: closed from its entry
        Assert.False(p.Leave()); // then the dialog: closed from the stack
        Assert.False(p.Leave()); // nothing open: not a taken-over one
        var a = p.Enter(); var b = p.Enter();
        p.Adopt(a);              // an announcement older than the newest one is the one taken over
        Assert.False(p.Leave()); // b leaves first: not adopted
        Assert.True(p.Leave());  // then a
        Assert.NotEqual(outer, late);
        p.Enter(); p.Clear();
        Assert.Equal(0, p.Open);
    }

    // ---- readers: the alias, the shared helpers ---------------------------------------------------------------------------------

    static readonly string[] AliasSession =
    {
        """{"t":100,"n":1,"type":"cmd_start","command":"DRAWITEMS","origin":"typed/other"}""",
        """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
        """{"t":200,"n":3,"type":"modal_title","title":"Draw items"}""",
        """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Options...","fields":[],"read":2}""",
        """{"t":1100,"n":5,"type":"modal_enter","title":"Item options","windowClass":"WindowsForms10.Window.8"}""",
        """{"t":1900,"n":6,"type":"modal_enter","title":"Autodesk AutoCAD 2026","windowClass":"AfxMDIFrame140u"}""",
        """{"t":2200,"n":7,"type":"modal_alias","for":6,"of":5}""",
        """{"t":4000,"n":8,"type":"dialog_values","for":5,"title":"Item options","source":"win32","parent":3,"fields":[{"key":"name:s","label":"Spacing","kind":"edit","before":"5","after":"8"}],"read":1,"closedWith":"ok"}""",
        """{"t":4010,"n":9,"type":"modal_leave"}""",
        """{"t":5000,"n":10,"type":"dialog_values","for":3,"title":"Draw items","source":"win32","fields":[],"read":2,"closedWith":"ok"}""",
        """{"t":5010,"n":11,"type":"modal_leave"}""",
    };

    [Fact]
    public void A_late_EnterModal_alias_leaves_one_dialog_step_in_the_plan()
    {
        var steps = Build(AliasSession);
        var dlg = steps.Single(s => s.Kind == "dialog");        // not a second step for the announcement (its title was the main window's)
        Assert.Equal("Draw items", dlg.DialogTitle);
        Assert.Equal("Item options", dlg.Clicks.Single().Nested.Single().DialogTitle);
        // without the alias, the announcement (the main window's title, no window of its own) is dropped by MainWindowModal: still one dialog step each, not a phantom third
        Assert.Equal(1, Build(AliasSession.Where(l => !l.Contains("modal_alias")).ToArray()).Count(s => s.Kind == "dialog"));
    }

    [Fact]
    public void A_late_EnterModal_alias_leaves_one_dialog_in_the_script_too()
    {
        var dir = Directory.CreateTempSubdirectory("retrace-test-alias").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "s"));
            var p = Path.Combine(dir, "s", "recording.jsonl");
            File.WriteAllLines(p, new[] { """{"type":"header","schema":1,"name":"t","started":"2026-11-06T10:00:00"}""" }.Concat(AliasSession));
            var md = Engine.Recordings.ScriptWriter.Build(Engine.Recordings.RecordingReader.Load(p), null, dir, new List<string>());
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(md, "A dialog opens:"));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(md, "A nested dialog opens:"));
            Assert.Contains("Set **Spacing** to `8` (was `5`)", md);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_alias_for_numbers_that_are_not_dialogs_changes_nothing()
    {
        var byN = new Dictionary<long, string> { [1] = "a", [2] = "b" };
        Assert.Null(DialogNesting.Alias(byN, 5, 1));
        Assert.Null(DialogNesting.Alias(byN, 1, 1));            // itself
        Assert.Equal("b", DialogNesting.Alias(byN, 2, 1));      // the duplicate comes back; 2 now means what 1 means
        Assert.Equal("a", byN[2]);
    }

    record Att(long T, long Closed);

    [Fact]
    public void Both_readers_attach_a_nested_dialog_by_one_rule()
    {
        var nested = new List<Att>();
        Assert.True(DialogNesting.Attach(nested, 1000, new Att(1500, 3000), x => x.T, x => x.Closed));
        Assert.True(DialogNesting.Attach(nested, 1000, new Att(3300, 8000), x => x.T, x => x.Closed));           // right after the one before it closed: the next of the click
        Assert.False(DialogNesting.Attach(nested, 1000, new Att(8000 + DialogClickRules.NestedWithinMs + 1, 20000), x => x.T, x => x.Closed));
        Assert.Equal(2, nested.Count);
        Assert.False(DialogNesting.Attach(new List<Att>(), 1000, new Att(1000 + DialogClickRules.NestedWithinMs + 1, 20000), x => x.T, x => x.Closed));
        Assert.True(DialogNesting.Attach(new List<Att> { new(1500, -1) }, 1000, new Att(9000, 9500), x => x.T, x => x.Closed));   // the one before it has no known close: counted from the click
        var fields = DialogNesting.FieldsOf(JsonDocument.Parse("""[{"key":"k","label":"L","kind":"edit","before":"a","after":"b"},5,{"label":"only"}]""").RootElement);
        Assert.Equal(new[] { ("k", "L", "edit", "a", "b"), ("", "only", "", "", "") }, fields.Select(f => (f.Key, f.Label, f.Kind, f.Before, f.After)));
        Assert.Empty(DialogNesting.FieldsOfEvent(JsonDocument.Parse("""{"type":"x"}""").RootElement));
    }

    // ---- a dialog of buttons and texts (a message box): the Yes of a nested box ---------------------------------------------

    [Theory]
    [InlineData(false, 0, false)]   // not enough empty reads yet
    [InlineData(false, 3, false)]
    [InlineData(false, 4, true)]    // no Win32 field in four reads: probably WPF, whatever buttons it has
    [InlineData(false, 9, true)]
    [InlineData(true, 9, false)]    // it showed a field once
    public void A_dialog_with_no_Win32_field_is_read_through_UI_Automation_after_four_empty_reads(bool fieldSeen, int empty, bool flips) =>
        Assert.Equal(flips, DialogClickRules.FlipToUia(fieldSeen, empty));

    [Fact]
    public void A_dialog_read_through_UI_Automation_still_has_the_clicks_recorded_in_it()
    {
        // 0 fields and 2 buttons (a message box): its fields are read through UI Automation (source uia), its buttons are Win32 and a click on one is recorded and planned like any
        Assert.True(DialogClickRules.FlipToUia(false, 4));
        var steps = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWITEMS","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw items"}""",
            """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Clear","fields":[],"read":2}""",
            """{"t":1100,"n":5,"type":"modal_enter","title":"Remove items","windowClass":"#32770"}""",
            """{"t":6000,"n":6,"type":"dialog_click","for":5,"button":"Yes","fields":[],"read":0}""",
            """{"t":6050,"n":7,"type":"dialog_values","for":5,"title":"Remove items","source":"uia","parent":3,"fields":[],"read":0,"closedWith":"click","button":"Yes"}""",
            """{"t":9000,"n":8,"type":"dialog_values","for":3,"title":"Draw items","source":"win32","fields":[],"read":2,"closedWith":"ok"}""");
        var box = steps.Single(s => s.Kind == "dialog").Clicks.Single().Nested.Single();
        Assert.Equal("uia", box.ValuesSource);
        Assert.Equal("Yes", box.Clicks.Single().Button);
        Assert.True(DialogSegments.LastClickCloses(box));
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(box));
    }

    [Fact]
    public void The_Yes_of_a_nested_message_box_answered_late_is_a_click_that_closed_it()
    {
        var steps = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWITEMS","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw items"}""",
            """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Clear","fields":[],"read":2}""",
            """{"t":1100,"n":5,"type":"modal_enter","title":"Remove items","windowClass":"#32770"}""",
            """{"t":6000,"n":6,"type":"dialog_click","for":5,"button":"Yes","fields":[],"read":0,"statics":[{"id":65535,"text":"Remove all 2 items?"}]}""",
            """{"t":6050,"n":7,"type":"dialog_values","for":5,"title":"Remove items","source":"win32","parent":3,"fields":[],"read":0,"closedWith":"click","button":"Yes","statics":[{"id":65535,"text":"Remove all 2 items?"}]}""",
            """{"t":9000,"n":8,"type":"dialog_values","for":3,"title":"Draw items","source":"win32","fields":[],"read":2,"closedWith":"ok"}""");
        var dlg = steps.Single(s => s.Kind == "dialog");
        var box = dlg.Clicks.Single().Nested.Single();
        Assert.Equal("Yes", box.Clicks.Single().Button);
        Assert.True(DialogSegments.LastClickCloses(box));
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(box));          // the click is the work, though nothing was read
        Assert.Equal("A dialog opens: Draw items. Press Clear (nested dialog \"Remove items\": press Yes), then close it with OK.", DialogSegments.ManualMessage("Draw items", null, null, dlg));
    }

    // ---- what Enter and a letter mean ---------------------------------------------------------------------------------------------

    // WM_GETDLGCODE: DLGC_BUTTON 0x2000, DLGC_WANTARROWS 0x1, DLGC_HASSETSEL 0x8, DLGC_WANTCHARS 0x80, DLGC_WANTALLKEYS (= DLGC_WANTMESSAGE) 0x4
    [Theory]
    [InlineData(0x2000 | 0x20, false)]         // a push button (DLGC_BUTTON | DLGC_UNDEFPUSHBUTTON): Enter presses the default button, or this one
    [InlineData(0x1 | 0x8 | 0x80, false)]      // a single-line edit (arrows, set-sel, chars): Enter goes on to the default button
    [InlineData(0x1 | 0x8 | 0x80 | 0x4, true)] // a multi-line edit that takes Return, a list view or tree view label edit, a grid cell in edit mode: it wants all keys
    [InlineData(0x1 | 0x80 | 0x4, true)]       // a combo box with its list open answers DLGC_WANTMESSAGE for Enter / Esc (the same bit)
    [InlineData(0, false)]
    public void Enter_presses_the_default_button_unless_the_control_asks_for_the_key(int dlgCode, bool controlKeepsEnter) =>
        Assert.Equal(controlKeepsEnter, DialogClickRules.EnterGoesToControl(dlgCode));

    [Theory]
    [InlineData(0x2000 | 0x20, false, false, true)]   // a push button has the focus: the letter is a mnemonic
    [InlineData(0x1 | 0x8 | 0x80, false, false, false)]   // an edit box takes characters: the letter is text
    [InlineData(0x1 | 0x80, false, false, false)]      // a list, a tree, a grid: they take characters (type-ahead)
    [InlineData(0x2000 | 0x20, true, false, false)]    // Ctrl+C, Ctrl+N ...: a shortcut
    [InlineData(0x2000 | 0x20, false, true, false)]    // Win+E ...
    [InlineData(0, false, false, true)]
    public void A_letter_without_Alt_is_a_mnemonic_when_the_focused_control_does_not_take_characters_and_no_Ctrl_or_Windows_key_is_held(int dlgCode, bool ctrl, bool win, bool mnemonic) =>
        Assert.Equal(mnemonic, DialogClickRules.LetterIsMnemonic(dlgCode, ctrl, win));

    // ---- after a closing click ---------------------------------------------------------------------------------------------------

    [Fact]
    public void After_a_click_the_recording_says_closed_the_dialog_the_replay_waits_and_gives_up_never_goes_on()
    {
        var q = TimeSpan.FromSeconds(1); var open = TimeSpan.FromSeconds(8); var close = TimeSpan.FromSeconds(10);
        DialogClickRules.AfterPress Rule(bool closing, bool nested, double s) => DialogClickRules.AfterPressRule(closing, nested, TimeSpan.FromSeconds(s), q, open, close);
        // a closing click: wait for the window to go (the caller ends the dialog), and when it stays, give up: not NextSegment (that would press OK)
        Assert.Equal(DialogClickRules.AfterPress.Wait, Rule(true, false, 0.5));
        Assert.Equal(DialogClickRules.AfterPress.Wait, Rule(true, false, 5));
        Assert.Equal(DialogClickRules.AfterPress.Wait, Rule(true, false, 10));
        Assert.Equal(DialogClickRules.AfterPress.GiveUpStillOpen, Rule(true, false, 10.5));
        // an ordinary click: quiet for a second, then the next segment
        Assert.Equal(DialogClickRules.AfterPress.Wait, Rule(false, false, 0.5));
        Assert.Equal(DialogClickRules.AfterPress.NextSegment, Rule(false, false, 1.5));
        // a nested dialog the recording has must open: 8 s, then give up
        Assert.Equal(DialogClickRules.AfterPress.Wait, Rule(false, true, 5));
        Assert.Equal(DialogClickRules.AfterPress.GiveUpNestedMissing, Rule(false, true, 8.5));
        // a closing click that also had a nested dialog: it is waited for first
        Assert.Equal(DialogClickRules.AfterPress.Wait, Rule(true, true, 5));
        Assert.Equal(DialogClickRules.AfterPress.GiveUpNestedMissing, Rule(true, true, 8.5));
    }

    // ---- what a nested dialog's OK did to the dialog under it ---------------------------------------------------------------------

    [Fact]
    public void A_nested_dialogs_OK_changing_the_dialog_under_it_is_not_the_persons_edit()
    {
        // Text Style > New...: the click is recorded, the baseline settles at the timer read; the nested dialog's OK then adds and selects the style and changes the font;
        // when the nested dialog closes the parent's baseline moves again, at the first input to the parent or its first read
        var tracker = new FieldTracker();
        var settle = new ClickSettle();
        DialogField Styles(string v) => new("id:1", "Styles", "list", v);
        DialogField Font(string v) => new("id:2", "Font Name", "combo", v);
        tracker.Observe(new[] { Styles("Standard"), Font("txt.shx") });
        tracker.Rebase(); settle.Clicked(1000);                      // New... clicked
        Assert.True(settle.OnRead(1150));                            // settled
        // the nested dialog is open for a while; its OK changes the parent
        tracker.Observe(new[] { Styles("Notes3"), Font("Arial") });
        settle.Rearm(9000);                                          // the nested dialog closed
        Assert.True(settle.OnInput());                               // the next input to the parent (the Close click): read first
        tracker.Observe(new[] { Styles("Notes3"), Font("Arial") }); tracker.Rebase();
        Assert.Empty(tracker.Changes());                             // steps.md shows no "Select Notes3 in Styles"
        // a person's edit after that is theirs
        tracker.Observe(new[] { Styles("Notes3"), Font("Verdana") });
        Assert.Equal(new[] { ("Font Name", "Arial", "Verdana") }, tracker.Changes().Select(c => (c.Label, c.Before, c.After)));
        // and without the re-arm the nested dialog's work would have been recorded as a change
        var without = new FieldTracker();
        without.Observe(new[] { Styles("Standard") }); without.Rebase();
        without.Observe(new[] { Styles("Notes3") });
        Assert.Single(without.Changes());
    }

    // ---- the result when a person finished the step ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 2, false, false, false, "ok")]
    [InlineData(2, 2, false, true, false, "failed")]
    [InlineData(3, 2, false, true, false, "notRun")]
    [InlineData(2, 2, false, false, false, "notRun")]
    [InlineData(2, 2, false, true, true, "person")]       // the replay gave up here and a person finished: not "failed"
    [InlineData(3, 2, false, true, true, "person")]       // and what was left was theirs: not "notRun"
    [InlineData(1, 2, false, true, true, "ok")]           // what the replay did before stays ok
    [InlineData(3, 2, true, false, true, "ok")]           // the replay did it all (the dialog is gone after its press): nobody finished anything
    public void A_segment_the_replay_did_not_do_but_a_person_did_is_the_persons(int index, int current, bool completed, bool gaveUp, bool person, string expected) =>
        Assert.Equal(expected, DialogSegments.StatusOf(index, current, completed, gaveUp, person));

    [Fact]
    public void The_person_status_is_in_the_result_file()
    {
        var r = new ReplayResult();
        r.Steps.Add(new StepResult { Index = 1, Kind = "dialog", WaitedForPerson = true, Segments = new List<SegmentResult>
        {
            new() { Button = "Add", Status = SegmentResult.Ok },
            new() { Button = "Options...", Status = SegmentResult.Person, Message = "pressing Options... opened the dialog \"Color\", which the recording does not have" },
            new() { Status = SegmentResult.Person },
        } });
        using var doc = JsonDocument.Parse(r.ToJson());
        var segs = doc.RootElement.GetProperty("steps")[0].GetProperty("segments");
        Assert.Equal(new[] { "ok", "person", "person" }, segs.EnumerateArray().Select(x => x.GetProperty("status").GetString()));
        Assert.Contains("does not have", segs[1].GetProperty("message").GetString());
    }

    // ---- the pause message for a nested dialog closed by its last click -----------------------------------------------------

    [Fact]
    public void The_message_does_not_ask_for_an_OK_after_a_nested_dialog_closed_by_its_last_click()
    {
        var steps = Build(
            """{"t":100,"n":1,"type":"modal_enter","title":"Outer"}""",
            """{"t":1000,"n":2,"type":"dialog_click","for":1,"button":"Pick...","fields":[],"read":1}""",
            """{"t":1100,"n":3,"type":"modal_enter","title":"Pick"}""",
            """{"t":1500,"n":4,"type":"dialog_click","for":3,"button":"Done","fields":[{"key":"id:1","label":"Size","kind":"edit","before":"1","after":"2"}],"read":1}""",
            """{"t":1550,"n":5,"type":"dialog_values","for":3,"title":"Pick","source":"win32","parent":1,"fields":[],"read":1,"closedWith":"click","button":"Done"}""",
            """{"t":3000,"n":6,"type":"dialog_values","for":1,"title":"Outer","source":"win32","fields":[],"read":1,"closedWith":"ok"}""");
        var dlg = steps.Single(s => s.Kind == "dialog");
        Assert.Equal("Done", dlg.Clicks[0].Nested.Single().Clicks[0].Button);
        Assert.Equal("A dialog opens: Outer. Press Pick... (nested dialog \"Pick\": set Size to 2, press Done), then close it with OK.", DialogSegments.ManualMessage("Outer", null, null, dlg));
        Assert.Equal(new[] { "In the Outer dialog:", "Click Pick....", "In the Pick dialog:", "Set Size to 2.", "Click Done.", "Click OK." }, TdocPlan.Instructions(dlg));
    }
}

/// <summary>When the baseline moves past what a clicked button's handler did (item: a person's quick typing is never absorbed).</summary>
public class ClickSettleTests
{
    [Fact]
    public void The_first_input_after_a_click_settles_before_it_is_dispatched_and_the_timer_read_then_does_nothing()
    {
        var s = new ClickSettle();
        s.Clicked(1000);
        Assert.True(s.Pending);
        Assert.False(s.OnRead(1050));    // too soon after the click
        Assert.True(s.OnInput());        // the person typed: read first
        Assert.False(s.OnRead(1300));    // already settled
        Assert.False(s.OnInput());
    }

    [Fact]
    public void A_timer_read_100_ms_after_the_click_settles_and_a_later_input_does_nothing()
    {
        var s = new ClickSettle();
        s.Clicked(1000);
        Assert.False(s.OnRead(1099));
        Assert.True(s.OnRead(1100));
        Assert.False(s.OnInput());
        s.Clicked(2000);                 // the next click arms it again
        Assert.True(s.OnInput());
    }

    [Fact]
    public void Quick_typing_after_Add_is_not_absorbed_into_the_baseline()
    {
        // Name = "A", Add clicked: the handler clears the box. The person types "B" at once, before any timer read.
        var tracker = new FieldTracker();
        DialogField Name(string v) => new("name:itemBox", "Item", "edit", v);
        var settle = new ClickSettle();
        tracker.Observe(new[] { Name("A") });
        // the click is recorded with its changes ("" -> "A"); the baseline moves to the values read; the handler then clears the box
        tracker.Observe(new[] { Name("") });                 // ... the dialog now shows ""
        tracker.Rebase();                                    // (Commit: baseline = last read; here "")
        settle.Clicked(1000);
        // the first key goes to the box: read BEFORE the key is dispatched: the box still holds ""
        if (settle.OnInput()) { tracker.Observe(new[] { Name("") }); tracker.Rebase(); }
        tracker.Observe(new[] { Name("B") });                // the key reached the box
        // the next click records the change since the baseline: "" -> "B", not nothing
        Assert.Equal(new[] { ("", "B") }, tracker.Changes().Select(c => (c.Before, c.After)));
    }

    [Fact]
    public void Absorbing_the_handlers_clearing_needs_a_read_after_it_and_before_the_typing()
    {
        // with the handler's clearing seen only after the click's baseline, the timer read makes "" the baseline: the clearing is not the person's change
        var tracker = new FieldTracker();
        DialogField Name(string v) => new("name:itemBox", "Item", "edit", v);
        var settle = new ClickSettle();
        tracker.Observe(new[] { Name("A") });
        tracker.Rebase();                                    // Commit at the click: baseline "A"
        settle.Clicked(1000);
        tracker.Observe(new[] { Name("") });                 // the handler cleared the box
        Assert.True(settle.OnRead(1150)); tracker.Rebase();  // the timer's read: the clearing is the baseline now
        Assert.Empty(tracker.Changes());
    }
}

/// <summary>A tree field (kind `tree`, a path of item texts) segments like any other field: the selection made before a click is that click's.</summary>
public class TreeClickSegmentTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-treeclick").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    static readonly string[] Session =
    {
        """{"t":100,"n":1,"type":"cmd_start","command":"DRAWPARTS","origin":"typed/other"}""",
        """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
        """{"t":200,"n":3,"type":"modal_title","title":"Parts"}""",
        """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Add","fields":[{"key":"name:CatalogTree","label":"Catalog","kind":"tree","before":"","after":"Parts › Fasteners › Bolts"}],"read":2}""",
        """{"t":2000,"n":5,"type":"dialog_click","for":3,"button":"Add","fields":[{"key":"name:CatalogTree","label":"Catalog","kind":"tree","before":"Parts › Fasteners › Bolts","after":"Parts › Frames"}],"read":2}""",
        """{"t":3000,"n":6,"type":"dialog_values","for":3,"title":"Parts","source":"win32","fields":[],"read":2,"closedWith":"ok"}""",
        """{"t":3010,"n":7,"type":"modal_leave"}""",
        """{"t":3020,"n":8,"type":"cmd_end","command":"DRAWPARTS"}""",
    };

    [Fact]
    public void A_tree_selection_before_a_click_is_that_clicks_segment()
    {
        var dlg = Plan.Build(Session.Select(l => JsonDocument.Parse(l).RootElement.Clone())).Single(s => s.Kind == "dialog");
        Assert.Equal(new[] { "Add", "Add" }, dlg.Clicks.Select(c => c.Button));
        Assert.Equal(new[] { ("tree", "Parts › Fasteners › Bolts"), ("tree", "Parts › Frames") }, dlg.Clicks.Select(c => (c.Values.Single().Kind, c.Values.Single().After)));
        var segs = DialogSegments.Of(dlg, closingValues: true);
        Assert.Equal(new string?[] { "Add", "Add", null }, segs.Select(s => s.Button));
        Assert.Equal("Parts › Fasteners › Bolts", segs[0].Values.Single().After);
        Assert.Equal("Parts › Frames", segs[1].Values.Single().After);
        Assert.Empty(segs[2].Values);
        Assert.Equal(AutoMode.Ok, DialogSegments.ModeOf(dlg));
        Assert.Equal("A dialog opens: Parts. Select Parts › Fasteners › Bolts in Catalog, press Add, select Parts › Frames in Catalog, press Add, then close it with OK.",
            DialogSegments.ManualMessage("Parts", null, null, dlg));
    }

    [Fact]
    public void The_script_says_the_selection_then_the_click_for_each_segment()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "s"));
        var p = Path.Combine(_dir, "s", "recording.jsonl");
        File.WriteAllLines(p, new[] { """{"type":"header","schema":1,"name":"t","started":"2026-11-06T10:00:00"}""" }.Concat(Session));
        var md = Engine.Recordings.ScriptWriter.Build(Engine.Recordings.RecordingReader.Load(p), null, _dir, new List<string>());
        Assert.Contains("     - Select `Parts › Fasteners › Bolts` in **Catalog**, click **Add**\n", md);
        Assert.Contains("     - Select `Parts › Frames` in **Catalog** (was `Parts › Fasteners › Bolts`), click **Add**\n", md);
    }
}
