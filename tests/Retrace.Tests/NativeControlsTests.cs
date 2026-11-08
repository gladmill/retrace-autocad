using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Emit;

namespace Retrace.Tests;

/// <summary>The Win32 common controls an MFC window holds besides edit boxes, lists and trees: the text of a date, a calendar day, an IP address and a hot key (and back), the label of a toolbar
/// button and which one a recorded click means, the window classes that are flagged as unsupported, the class summary of the diagnostic, and the plan of a toolbar click. Synthetic data only.</summary>
public class NativeControlsTests
{
    // ---- date and time picker ------------------------------------------------------------------------------------------

    [Fact]
    public void A_date_picker_shows_the_date_and_a_time_format_picker_the_time()
    {
        var t = new SysTime(2026, 3, 4, 9, 5, 7);
        Assert.Equal("2026-03-04", DateText.Format(t, time: false));
        Assert.Equal("09:05:07", DateText.Format(t, time: true));
        Assert.False(DateText.IsTime(0x0));    // DTS_SHORTDATEFORMAT
        Assert.False(DateText.IsTime(0x4));    // DTS_LONGDATEFORMAT
        Assert.False(DateText.IsTime(0xC));    // DTS_SHORTDATECENTURYFORMAT
        Assert.True(DateText.IsTime(0x9));     // DTS_TIMEFORMAT | DTS_UPDOWN
        Assert.True(DateText.IsTime(0x8));
    }

    [Fact]
    public void Applying_a_date_keeps_the_time_of_day_and_the_other_way_round()
    {
        var now = new SysTime(2026, 1, 5, 9, 30, 15);
        Assert.True(DateText.TryApply(false, "2027-02-28", now, out var d, out var none));
        Assert.False(none);
        Assert.Equal(new SysTime(2027, 2, 28, 9, 30, 15), d);
        Assert.True(DateText.TryApply(true, "17:45:00", now, out var t, out _));
        Assert.Equal(new SysTime(2026, 1, 5, 17, 45, 0), t);
        Assert.True(DateText.TryApply(true, "7:05", now, out t, out _));
        Assert.Equal(7, t.Hour);
    }

    [Theory]
    [InlineData(false, "2026-02-30")]
    [InlineData(false, "14.03.2026")]
    [InlineData(false, "tomorrow")]
    [InlineData(true, "25:00:00")]
    [InlineData(true, "noon")]
    public void A_text_that_is_no_date_or_time_is_refused(bool time, string text) =>
        Assert.False(DateText.TryApply(time, text, new SysTime(2026, 1, 1, 0, 0, 0), out _, out _));

    [Fact]
    public void An_empty_date_means_no_date_chosen()
    {
        Assert.True(DateText.TryApply(false, "", new SysTime(2026, 1, 1, 0, 0, 0), out _, out var none));
        Assert.True(none);
    }

    // ---- month calendar ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_calendar_value_is_a_day_or_a_range()
    {
        var a = new SysTime(2026, 3, 14, 0, 0, 0); var b = new SysTime(2026, 3, 18, 0, 0, 0);
        Assert.Equal("2026-03-14", CalendarText.Format(a, null));
        Assert.Equal("2026-03-14", CalendarText.Format(a, a));   // a range of one day is one day
        Assert.Equal("2026-03-14..2026-03-18", CalendarText.Format(a, b));
        Assert.True(CalendarText.TryParse("2026-03-14..2026-03-18", out var from, out var to));
        Assert.Equal((a, b), (from, to));
        Assert.True(CalendarText.TryParse("2026-03-14", out from, out to));
        Assert.Equal(from, to);
        Assert.True(CalendarText.IsMulti(0x2));
        Assert.False(CalendarText.IsMulti(0x10));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-03-18..2026-03-14")]
    [InlineData("2026-03-14..2026-03-15..2026-03-16")]
    [InlineData("March 14")]
    public void A_calendar_text_that_is_no_day_or_range_is_refused(string text) => Assert.False(CalendarText.TryParse(text, out _, out _));

    [Fact]
    public void Two_times_on_the_same_day_are_the_same_day()
    {
        var a = new SysTime(2026, 3, 14, 0, 0, 0);
        Assert.True(a.SameDay(new SysTime(2026, 3, 14, 23, 59, 59)));
        Assert.False(a.SameDay(new SysTime(2026, 3, 15, 0, 0, 0)));
        Assert.False(a.SameDay(new SysTime(2027, 3, 14, 0, 0, 0)));
        Assert.Equal("2026-03-14", CalendarText.Format(a, new SysTime(2026, 3, 14, 12, 0, 0)));   // a range of one day is one day, whatever the time
    }

    // ---- IP address ----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_ip_address_reads_from_its_four_fields_and_parses_back()
    {
        Assert.Equal("10.0.0.5", IpText.Format(new[] { "10", "0", "0", "5" }));
        Assert.Equal("", IpText.Format(new[] { "", "", "", "" }));
        Assert.Equal("192.168..", IpText.Format(new[] { "192", "168", "", "" }));   // partly filled reads as it shows
        Assert.True(IpText.TryParse("10.0.0.5", out var b, out var blank));
        Assert.False(blank);
        Assert.Equal(new byte[] { 10, 0, 0, 5 }, b);
        Assert.False(IpText.TryParse("", out _, out blank));
        Assert.True(blank);   // clear the control
        Assert.False(IpText.TryParse("192.168..", out _, out blank));
        Assert.False(blank);
        Assert.False(IpText.TryParse("10.0.0.256", out _, out _));
        Assert.False(IpText.TryParse("10.0.0", out _, out _));
    }

    // ---- hot key -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_hot_key_word_is_virtual_key_low_and_modifiers_high()
    {
        Assert.Equal("", HotkeyText.Format(0));
        Assert.Equal("A", HotkeyText.Format('A'));
        Assert.Equal("Ctrl+Shift+A", HotkeyText.Format('A' | ((HotkeyText.Control | HotkeyText.Shift) << 8)));
        Assert.Equal("Ctrl+Alt+F5", HotkeyText.Format(0x74 | ((HotkeyText.Control | HotkeyText.Alt) << 8)));
        Assert.Equal("Ctrl", HotkeyText.Format(HotkeyText.Control << 8));   // modifiers only: no trailing '+'
        Assert.Equal("Ctrl+Ext+Delete", HotkeyText.Format(0x2E | ((HotkeyText.Control | HotkeyText.Ext) << 8)));
        Assert.Equal("VK07", HotkeyText.Format(0x07));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("Ctrl+Shift+A")]
    [InlineData("Ctrl+Alt+F5")]
    [InlineData("Shift+Num+")]
    [InlineData("Ctrl+Ext+Delete")]
    [InlineData("Ctrl")]
    [InlineData("Alt+Shift")]
    [InlineData("VK07")]
    public void A_hot_key_text_round_trips(string text)
    {
        Assert.True(HotkeyText.TryParse(text, out var word));
        Assert.Equal(text.Replace("Alt+Shift", "Shift+Alt"), HotkeyText.Format(word));
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Banana")]
    [InlineData("A+B")]
    [InlineData("VKZZ")]
    public void A_hot_key_text_that_is_no_key_is_refused(string text) => Assert.False(HotkeyText.TryParse(text, out _));

    // ---- toolbar buttons -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_toolbar_button_is_named_by_its_text_else_its_tooltip_else_its_command_id()
    {
        Assert.Equal("Icons", ToolbarLabel.Choose("&Icons", "Show the products as icons\nIcon view", 2001));   // its own text first
        Assert.Equal("Icon view", ToolbarLabel.Choose("", "Show the products as icons\nIcon view", 2001));    // the tooltip part of an MFC prompt string
        Assert.Equal("Open", ToolbarLabel.Choose(null, "Open", 7));
        Assert.Equal("toolbar button 2001", ToolbarLabel.Choose("", "", 2001));
    }

    [Fact]
    public void A_recorded_toolbar_click_finds_its_button_by_command_id_in_any_toolbar_and_by_nothing_else()
    {
        // the command ids of two toolbars one after the other: (toolbar 0) 2001, 2002; (toolbar 1) 3001, 3002
        var buttons = new List<int> { 2001, 2002, 3001, 3002 };
        Assert.Equal(3, ToolbarLabel.FindById(buttons, 3002));      // an id in the second toolbar is found although the first one has none
        Assert.Equal(0, ToolbarLabel.FindById(buttons, 2001));
        Assert.Equal(-1, ToolbarLabel.FindById(buttons, 9999));     // no toolbar has it: not found, whatever the button is called (the replay waits, then stops for a person)
        Assert.Equal(-1, ToolbarLabel.FindById(buttons, 0));        // a step with no id (a hand-written one) finds nothing
        Assert.Equal(-1, ToolbarLabel.FindById(buttons, -5));
        Assert.Equal(-1, ToolbarLabel.FindById(new List<int>(), 2001));
    }

    [Fact]
    public void A_check_button_is_put_in_the_state_a_click_leaves_before_the_command_is_posted()
    {
        // the replay sets ExpectedChecked with TB_CHECKBUTTON (as the toolbar does for a click), posts the command, and looks at the state again
        Assert.Null(ToolbarPress.ExpectedChecked(0x00, false));            // a plain button: nothing is set
        Assert.Null(ToolbarPress.ExpectedChecked(0x10, true));             // BTNS_AUTOSIZE only
        Assert.Equal(true, ToolbarPress.ExpectedChecked(0x02, false));     // BTNS_CHECK, unchecked: becomes checked
        Assert.Equal(false, ToolbarPress.ExpectedChecked(0x02, true));     // checked: becomes unchecked
        Assert.Equal(true, ToolbarPress.ExpectedChecked(0x06, true));      // BTNS_CHECKGROUP: the pressed one is checked (the toolbar unchecks its group)
        // what is set is what is expected, so a program that undoes it is caught by the second look
        foreach (var (style, before) in new[] { (0x02, false), (0x02, true), (0x06, false), (0x06, true) })
        {
            var set = ToolbarPress.ExpectedChecked(style, before)!.Value;
            Assert.Null(ToolbarPress.CheckProblem(style, before, set));
            Assert.NotNull(ToolbarPress.CheckProblem(style, before, !set));
        }
    }

    [Fact]
    public void A_drop_down_toolbar_button_is_left_to_a_person_and_a_check_button_must_end_right()
    {
        const int none = 0, arrows = ToolbarPress.ExDrawDropDownArrows;
        Assert.Equal("a drop-down toolbar button: press it yourself", ToolbarPress.StopFor(0x08, none));   // BTNS_DROPDOWN on a toolbar with no drop-down arrows: its whole face opens the menu
        Assert.NotNull(ToolbarPress.StopFor(0x80, none));                                                  // BTNS_WHOLEDROPDOWN
        Assert.NotNull(ToolbarPress.StopFor(0x80, arrows));                                                // whole-face, whatever the extended style
        Assert.NotNull(ToolbarPress.StopFor(0x08 | 0x10, none));                                           // with BTNS_AUTOSIZE
        Assert.Null(ToolbarPress.StopFor(0x08, arrows));                                                   // a split button: its main part is a command (WM_COMMAND), the arrow is the menu
        Assert.Null(ToolbarPress.StopFor(0x08 | 0x10, arrows));
        Assert.Null(ToolbarPress.StopFor(0, none));
        Assert.Null(ToolbarPress.StopFor(0x02, none));                                                     // a check button is pressed, then looked at
        Assert.Null(ToolbarPress.ExpectedChecked(0, true));                                          // a plain button has no state
        Assert.True(ToolbarPress.ExpectedChecked(0x02, false));                                      // BTNS_CHECK toggles
        Assert.False(ToolbarPress.ExpectedChecked(0x02, true));
        Assert.True(ToolbarPress.ExpectedChecked(0x06, true));                                       // BTNS_CHECKGROUP ends checked
        Assert.True(ToolbarPress.ExpectedChecked(0x06, false));
        Assert.Null(ToolbarPress.CheckProblem(0x02, false, true));                                   // toggled on: right
        Assert.Null(ToolbarPress.CheckProblem(0x02, true, false));
        Assert.Contains("press it yourself", ToolbarPress.CheckProblem(0x02, false, false)!);        // the command did not leave it as a click does
        Assert.Contains("unchecked", ToolbarPress.CheckProblem(0x06, false, false)!);
        Assert.Null(ToolbarPress.CheckProblem(0, false, true));                                      // no check state: nothing to verify
    }

    [Fact]
    public void A_toolbar_hit_counts_only_inside_the_button_rectangle()
    {
        // TB_HITTEST answers 0 for the first button and for some points beside it: the rectangle decides
        Assert.True(ToolbarLabel.Inside(5, 5, 2, 2, 20, 18));
        Assert.True(ToolbarLabel.Inside(2, 2, 2, 2, 20, 18));      // left and top are in
        Assert.False(ToolbarLabel.Inside(20, 5, 2, 2, 20, 18));    // right and bottom are out
        Assert.False(ToolbarLabel.Inside(5, 18, 2, 2, 20, 18));
        Assert.False(ToolbarLabel.Inside(1, 5, 2, 2, 20, 18));
    }

    [Fact]
    public void A_toolbar_click_is_a_dialog_click_of_kind_toolbar_with_the_command_id()
    {
        Assert.Equal(DialogClickKinds.Toolbar, DialogClickKinds.Of("toolbar"));
        Assert.Equal(DialogClickKinds.DoubleClick, DialogClickKinds.Of("dblclick"));
        Assert.Equal(DialogClickKinds.Click, DialogClickKinds.Of("other"));
        Assert.Equal(DialogClickKinds.Click, DialogClickKinds.Of(null));
        var events = new[]
        {
            """{"t":10,"n":1,"type":"modal_enter","title":"Frame","windowClass":"AfxFrameOrView140u"}""",
            """{"t":20,"n":2,"type":"dialog_click","for":1,"button":"Icon view","id":2001,"index":1,"kind":"toolbar","fields":[],"read":2}""",
            """{"t":30,"n":3,"type":"dialog_values","for":1,"closedWith":"ok","fields":[{"key":"id:1002","label":"Name","kind":"edit","before":"Sample","after":"B"}],"read":3}""",
            """{"t":31,"n":4,"type":"modal_leave"}""",
        };
        var docs = events.Select(e => JsonDocument.Parse(e)).ToList();
        try
        {
            var step = Plan.Build(docs.Select(d => d.RootElement)).Single(s => s.Kind == "dialog");
            var click = Assert.Single(step.Clicks);
            Assert.Equal(DialogClickKinds.Toolbar, click.Kind);
            Assert.Equal(2001, click.Id);
            var segs = DialogSegments.Of(step, closingValues: true);
            Assert.True(segs[0].IsToolbar);
            Assert.False(segs[0].IsDoubleClick);
            Assert.Equal(2001, segs[0].ButtonId);
            Assert.Equal("press Icon view", segs[0].Phrase);
            Assert.False(segs[1].IsToolbar);
        }
        finally { foreach (var d in docs) d.Dispose(); }
    }

    // ---- parts that cannot be read -------------------------------------------------------------------------------------

    [Fact]
    public void The_hash_of_an_mfc_feature_pack_class_is_cut()
    {
        Assert.Equal("Afx:TabWnd", NativeParts.StableClass("Afx:TabWnd:400000:8:10003:0"));
        Assert.Equal("Afx:TabWnd", NativeParts.StableClass("Afx:TabWnd"));
        Assert.Equal("Button", NativeParts.StableClass("Button"));
        Assert.Equal("AfxFrameOrView140u", NativeParts.StableClass("AfxFrameOrView140u"));
        // every hex or numeric segment goes, wherever it is: AutoCAD's own unnamed Afx windows are one class
        Assert.Equal("Afx", NativeParts.StableClass("Afx:00007FF72ABD0000:8:0000000000010003:0000000000000000:0000000000000000"));
        Assert.Equal("Afx", NativeParts.StableClass("Afx:00007FF72ABD0000:b:0000000000010003:0000000000000006:000000000AD409F5"));
        Assert.Equal("Afx:ToolBar", NativeParts.StableClass("Afx:ToolBar:482a0000:8:10003:10"));
        Assert.Equal("Afx:PropList:Name", NativeParts.StableClass("Afx:PropList:482a0000:Name:10"));    // a segment that is a name stays
        Assert.Equal("Afx:Tab", NativeParts.StableClass("Afx:Tab:12:8"));
        Assert.Equal("Afx", NativeParts.StableClass("Afx:12:8:9"));
        Assert.Equal("afx:TabWnd", NativeParts.StableClass("afx:TabWnd:ABC:0"));
    }

    [Fact]
    public void Custom_drawn_feature_pack_windows_are_unsupported_parts_and_nothing_else_is()
    {
        Assert.Equal(new WpfPresses.Unsupported(NativeParts.MfcTabs, "Afx:TabWnd"), NativeParts.Classify("Afx:TabWnd:400000:8:10003:0"));
        Assert.Equal(NativeParts.MfcPropertyGrid, NativeParts.Classify("Afx:PropList:400000:8:10003:1")!.Value.Kind);
        Assert.Equal(NativeParts.MfcToolbar, NativeParts.Classify("Afx:ToolBar:400000:8:10003:1")!.Value.Kind);
        Assert.Null(NativeParts.Classify("ListBox"));                  // a CCheckListBox is an owner-drawn ListBox like any other: nothing tells it from outside (TODO.md), so no list box is flagged
        Assert.Null(NativeParts.Classify("Button"));                   // CMFCButton / CMFCColorButton are Button windows
        Assert.Null(NativeParts.Classify("Edit"));                     // CMFCEditBrowseCtrl is an Edit
        Assert.Null(NativeParts.Classify("AfxFrameOrView140u"));
        Assert.Null(NativeParts.Classify("SysTabControl32"));
    }

    [Fact]
    public void Classes_are_told_by_their_exact_name_and_no_window_is_asked_anything()
    {
        var bar = "Afx:ControlBar:400000:8:10003:0";
        Assert.Null(NativeParts.Classify(bar));                                                       // a docking bar or pane is a container; a toolbar in it is a ToolbarWindow32 child
        Assert.Null(NativeParts.Classify("Afx:Tab:400000:8:10003:0"));                                // no bare "tab": only the class Feature Pack registers
        Assert.Null(NativeParts.Classify("Afx:TabWndX:400000:8:10003:0"));
        Assert.Null(NativeParts.Classify("Afx:PropGrid:1:2:3:4"));
        Assert.Equal(NativeParts.MfcTabs, NativeParts.Classify("afx:tabwnd:1:2:3:4")!.Value.Kind);    // case does not matter, as for any window class
        Assert.Equal(NativeParts.MfcToolbar, NativeParts.Classify("Afx:ToolBar:1:2:3:4")!.Value.Kind);
        Assert.True(NativeParts.IsToolbarClass("ToolbarWindow32"));
        Assert.True(NativeParts.IsToolbarClass("toolbarwindow32"));
        Assert.False(NativeParts.IsToolbarClass("Afx:ToolBar:1:2:3:4"));                              // a CMFCToolBar is not a Win32 toolbar: no TB_ message is sent to it
        Assert.False(NativeParts.IsToolbarClass(bar));
    }

    [Fact]
    public void Unsupported_native_parts_are_named_with_their_class_like_wpf_parts()
    {
        var parts = new List<WpfPresses.Unsupported> { NativeParts.Classify("Afx:TabWnd:1:2:3:4")!.Value, NativeParts.Classify("Afx:PropList:1:2:3:4")!.Value };
        var note = WpfPresses.UnsupportedNote(parts)!;
        Assert.StartsWith("unverified: the dialog has ", note);
        Assert.Contains("CMFCTabCtrl, window class \"Afx:TabWnd\"", note);
        Assert.Contains("CMFCPropertyGridCtrl", note);
        Assert.Contains("not recorded or replayed", note);
        // the same json as a WPF part, and back
        var json = JsonSerializer.Serialize(WpfPresses.PartsToJson(parts));
        Assert.Equal("""[{"kind":"mfc-tabs","name":"Afx:TabWnd"},{"kind":"mfc-propertygrid","name":"Afx:PropList"}]""", json);
        var back = WpfPresses.PartsFromJson(JsonDocument.Parse(json).RootElement);
        Assert.Equal(parts, back);
        // a WPF part keeps its wording
        Assert.Contains("a tree \"PartsTree\"", WpfPresses.UnsupportedNote(new[] { new WpfPresses.Unsupported("tree", "PartsTree") })!);
    }

    [Fact]
    public void The_class_summary_cuts_feature_pack_hashes_and_counts_each_class()
    {
        var s = ClassSummary.Format(new[] { "Button", "Button", "Edit", "Afx:TabWnd:1:2:3:4", "Afx:TabWnd:5:6:7:8", "SysListView32", "ToolbarWindow32" });
        Assert.Equal("Afx:TabWnd x2, Button x2, Edit x1, SysListView32 x1, ToolbarWindow32 x1", s);
        Assert.Equal("Button x1", ClassSummary.Format(new[] { "Button", "Edit" }, max: 1));
        Assert.Equal("", ClassSummary.Format(Array.Empty<string>()));
    }

    // ---- recordings made before ComboBoxEx and the IP address control were fields of their own ------------------------------------

    [Fact]
    public void An_old_combo_change_on_the_inner_combo_of_a_comboboxex_finds_the_comboex_field()
    {
        // the inner combo box has the control id of the ComboBoxEx32: the old key is the new key, the old kind was "combo"
        var live = new[] { new LiveField("id:1004", "Unit", "comboex"), new LiveField("id:1009", "Kind", "combo") };
        var old = new[] { new FieldChange("id:1004", "Unit", "combo", "mm", "cm"), new FieldChange("id:1009", "Kind", "combo", "Bolt", "Nut") };
        Assert.Equal(new[] { 0, 1 }, FillMatcher.Match(old, live));
        Assert.True(FillMatcher.KindsMatch("combo", "comboex"));
        Assert.False(FillMatcher.KindsMatch("comboex", "combo"));
        Assert.False(FillMatcher.KindsMatch("edit", "comboex"));
        Assert.True(FillMatcher.Satisfied(old[0], "CM"));
        // the same by a label key
        Assert.Equal(new[] { 0 }, FillMatcher.Match(new[] { new FieldChange("label:Unit#1", "Unit", "combo", "mm", "cm") }, live));
    }

    [Fact]
    public void A_comboex_value_is_compared_as_a_number_like_a_combo_value()
    {
        var c = new FieldChange("id:1004", "Count", "comboex", "5", "20");
        Assert.True(FillMatcher.Satisfied(c, "20.0"));
        Assert.True(FillMatcher.ValueMatches("comboex", "20", "20.00"));
        Assert.False(FillMatcher.Satisfied(c, "21"));
        Assert.True(FillMatcher.ValueMatches("comboex", "cm", "CM"));
    }

    [Fact]
    public void A_property_grid_and_a_toolbar_hold_their_windows_but_a_tab_control_holds_the_programs_own()
    {
        Assert.True(NativeParts.HoldsOnlyItsOwnParts(NativeParts.Classify("Afx:PropList:1:2:3:4")!.Value));   // the in-place editor of a property grid is no field
        Assert.True(NativeParts.HoldsOnlyItsOwnParts(NativeParts.Classify("Afx:ToolBar:1:2:3:4")!.Value));
        Assert.False(NativeParts.HoldsOnlyItsOwnParts(NativeParts.Classify("Afx:TabWnd:1:2:3:4")!.Value));    // the controls on a page of it are the program's and are read as any control is
    }

    [Fact]
    public void Old_edit_changes_of_the_inner_boxes_of_an_ip_address_stay_unmatched_and_a_person_finishes_them()
    {
        // before the IP control was one field its four inner boxes were four edit fields: such a change finds no live field, as an unmatched field always did
        var live = new[] { new LiveField("id:1015", "Address", IpText.Kind), new LiveField("id:1002", "Name", "edit") };
        var old = new[]
        {
            new FieldChange("label:Address#1", "Address", "edit", "", "10"), new FieldChange("label:Address#2", "Address", "edit", "", "0"),
            new FieldChange("id:1015", "Address", "edit", "", "5"), new FieldChange("id:1002", "Name", "edit", "Sample", "B"),
        };
        Assert.Equal(new[] { -1, -1, -1, 1 }, FillMatcher.Match(old, live));
        Assert.False(FillMatcher.KindsMatch("edit", IpText.Kind));
    }

    // ---- values in the dialog ------------------------------------------------------------------------------------------

    [Fact]
    public void New_kinds_are_fields_of_their_own_and_are_matched_by_kind()
    {
        var raw = new[]
        {
            new RawControl(1010, "Start", DateText.Kind, "2026-03-04"), new RawControl(1011, "Day", CalendarText.Kind, "2026-03-14"), new RawControl(1015, "Address", IpText.Kind, "10.0.0.5"),
            new RawControl(1016, "Shortcut", HotkeyText.Kind, "Ctrl+A"), new RawControl(1004, "Unit", "comboex", "mm"),
        };
        var fields = DialogFieldsCore.Build(raw);
        Assert.Equal(new[] { "id:1010", "id:1011", "id:1015", "id:1016", "id:1004" }, fields.Select(f => f.Key));
        var changes = DialogFieldsCore.Diff(fields, fields.Select(f => f with { Value = f.Kind == IpText.Kind ? "10.0.0.6" : f.Value }).ToList());
        var change = Assert.Single(changes);
        Assert.Equal("Set Address to 10.0.0.6", DialogFieldsCore.Describe(change));
        var recorded = new[] { new FieldChange("id:1016", "Shortcut", HotkeyText.Kind, "Ctrl+A", "Ctrl+Shift+B") };
        Assert.Equal(new[] { 3 }, FillMatcher.Match(recorded, fields.Select(LiveField.Of).ToList()));
        Assert.True(FillMatcher.Satisfied(recorded[0], "ctrl+shift+b"));
        Assert.False(FillMatcher.Satisfied(recorded[0], "Ctrl+Shift+C"));
        // a recorded date is not matched with a live field of another kind
        var wrongKind = new[] { new FieldChange("id:1010", "Start", "edit", "", "2026-03-05") };
        Assert.Equal(new[] { -1 }, FillMatcher.Match(wrongKind, fields.Select(LiveField.Of).ToList()));
    }
}
