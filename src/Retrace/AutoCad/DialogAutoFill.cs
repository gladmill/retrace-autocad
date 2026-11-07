using System.Runtime.InteropServices;
using static Retrace.AutoCad.DialogReader;

namespace Retrace.AutoCad;

/// <summary>
/// RETRACERUN: does one recorded dialog (<see cref="DialogSegments"/>) in the open modal dialog and closes it the way the recording did. A small state
/// machine the replayer ticks (UI thread, every 200 ms) while it would otherwise wait for a person. A dialog is done in segments: fill and verify the
/// values recorded before a click, press the button (posted, so the dialog runs it from its own message loop), then wait until either a nested modal
/// dialog appears (a child filler does that one from its own recording, then this one waits for the dialog to be enabled again) or nothing opens for about
/// a second; the next segment follows, the last one fills what changed after the last click and presses OK or Cancel. A dialog that cannot be handled
/// (not found, a value that did not go in, a button that is missing, a nested dialog the recording does not have or that does not open, no OK button,
/// still open after the click) ends in <see cref="Phase.GaveUp"/> and the person gets the pause message, which names what is left to do.
/// Win32 dialogs are handled on the tick itself (SendMessage to our own process is cheap); dialogs without child windows go through UI
/// Automation on a pool thread, never on the UI thread (a dialog with recorded clicks needs Win32 controls: there is no UI Automation click here).
/// </summary>
sealed partial class DialogAutoFiller
{
    public enum Phase { Seek, Verify, UiaRunning, UiaHold, Clicked, GaveUp, Pressed, Nested, Back }

    /// <summary>The dialog window is looked for this long (it may need a moment to become the foreground window).</summary>
    public static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Controls may not exist yet shortly after the dialog opened (Drawing Units): reading is retried this long.</summary>
    public static readonly TimeSpan ControlsTimeout = TimeSpan.FromMilliseconds(1500);
    /// <summary>After the close click the dialog must be gone within this time, else a validation message box is assumed.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);
    /// <summary>After a button press with no nested dialog in the recording, the dialog must stay quiet this long (no dialog opens) before the next segment.</summary>
    public static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(1);
    /// <summary>A nested dialog the recording has must open within this time of the press.</summary>
    public static readonly TimeSpan NestedOpenTimeout = TimeSpan.FromSeconds(8);
    /// <summary>After a nested dialog closed, the dialog must be enabled again within this time.</summary>
    static readonly TimeSpan BackTimeout = TimeSpan.FromSeconds(10);
    /// <summary>A click that should open a nested dialog and did not within this time is pressed once more with the mouse messages.</summary>
    static readonly TimeSpan MouseRetryAfter = TimeSpan.FromSeconds(2);
    static readonly TimeSpan BackSettle = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan UiaTimeout = TimeSpan.FromSeconds(6);
    /// <summary>A Tdoc run holds the close click for its picture (<see cref="BeforeClose"/>); after this long the dialog is closed anyway.</summary>
    static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(30);


    /// <summary>What happened in one segment.</summary>
    sealed class Run
    {
        public readonly List<FieldFillResult> Results;
        public List<DialogStatic>? Statics;
        public readonly List<NestedDialogResult> Nested = new();
        /// <summary>Values already read back and found right: a value on a tab is checked before the dialog leaves that tab, never again.</summary>
        public readonly HashSet<int> Checked = new();
        /// <summary>When each value was first tried on its own tab: its controls timeout runs from there, whatever tabs were shown meanwhile.</summary>
        public readonly DateTime[] Since;
        public bool Pressed;
        /// <summary>The tree ticks of this segment, applied through one applier per tree.</summary>
        public readonly DialogFill.TickSession Ticks;
        public Run(IReadOnlyList<FieldChange> values) { Results = values.Select(v => FieldFillResult.Missing(v.Label)).ToList(); Since = new DateTime[values.Count]; Ticks = new DialogFill.TickSession(values); }
    }

    readonly AutoMode _mode;
    readonly string? _title, _source, _closedWith;
    readonly IntPtr _main, _ignore;
    readonly List<DialogSegment> _segments;
    readonly List<Run> _runs;
    int _seg, _nestedUsed;
    IntPtr _dlg, _lastNested;
    DateTime _started = DateTime.MinValue, _at = DateTime.MinValue, _segStart = DateTime.MinValue, _pressedAt, _backSince;
    Task<DialogFill.UiaOutcome>? _task;
    DialogFill.UiaOutcome? _hold;
    DialogAutoFiller? _child;
    bool _uia;
    readonly List<(IntPtr Button, string Label)> _pressed = new();
    string _pressState = "";   // the field values and static texts when the button was pressed: a retry needs them unchanged
    bool _mouseTried;   // the button was pressed with the mouse messages already (the recording says it acts on mouse-down, or the retry below)
    // tabs: a value, a button or the close that sits on a tab that is not showing makes the dialog show it first (the page's controls exist on the next tick)
    const int MaxSwitches = 40;
    int _switches;
    string? _pendingTab;
    readonly List<(IntPtr Tab, int Item, string Text)> _tabsShown = new();   // the tab switches of the current segment (a Tdoc picture boxes them)

    public Phase State { get; private set; } = Phase.Seek;
    /// <summary>Parallel to the dialog's recorded values (in segment order; empty when only Cancel is pressed).</summary>
    public List<FieldFillResult> Results => _runs.SelectMany(r => r.Results).ToList();
    /// <summary>Why it gave up, if it knows.</summary>
    public string? Note { get; private set; }
    public AutoMode Mode => _mode;
    /// <summary>The recorded dialog as segments, and how far the replay got with each (for the pause message, <see cref="DialogSegments.FallbackMessage"/>).</summary>
    public IReadOnlyList<DialogSegment> Segments => _segments;
    public IReadOnlyList<SegmentProgress> Progress => _runs.Select(r => new SegmentProgress(r.Results, r.Pressed)).ToList();
    /// <summary>The dialog window once it was found.</summary>
    public IntPtr Window => _dlg;
    /// <summary>The dialog's title as it is now.</summary>
    public string ActualTitle => Native.Exists(_dlg) ? Native.TitleOf(_dlg) : _title ?? "";

    /// <summary>Set by a Tdoc run: asked each tick, with the dialog filled in, before OK / Cancel is pressed. The close click waits until it
    /// returns true (the run takes its picture meanwhile). Null = no wait. Only the dialog itself, never a nested one.</summary>
    public Func<DialogShot, bool>? BeforeClose { get; init; }

    /// <param name="mode">what <see cref="DialogSegments.ModeOf"/> decided</param>
    /// <param name="dialog">the recorded dialog (title, values, clicks, how it was closed)</param>
    /// <param name="main">AutoCAD's main window (never touched)</param>
    /// <param name="window">the dialog's window when it is known already (a nested dialog the parent saw open); else it is looked for</param>
    /// <param name="ignore">a window of Retrace's own that is never a dialog to fill (the replay panel)</param>
    public DialogAutoFiller(AutoMode mode, Step dialog, IntPtr main, IntPtr window = default, IntPtr ignore = default)
    {
        _mode = mode; _title = dialog.DialogTitle; _source = dialog.ValuesSource; _closedWith = dialog.ClosedWith; _main = main; _dlg = window; _ignore = ignore;
        _segments = DialogSegments.Of(dialog, closingValues: mode == AutoMode.Ok);
        _runs = _segments.Select(s => new Run(s.Values)).ToList();
    }

    public bool Finished => State is Phase.Clicked or Phase.GaveUp;
    /// <summary>The dialog was done and is gone: its close button was pressed, or its last click closed it (the replay stops ticking once the dialog has closed, so a press
    /// that closed the dialog is told by the window being gone).</summary>
    public bool Completed => State == Phase.Clicked || (State is Phase.Pressed or Phase.Back && !Native.Exists(_dlg) && IsClosingClick);
    /// <summary>The close button was pressed and the dialog is gone.</summary>
    public bool Closed => State == Phase.Clicked && !Native.Exists(_dlg);

    bool HasClicks => DialogSegments.HasClicks(_segments);
    DialogSegment Seg => _segments[_seg];
    Run Cur => _runs[_seg];

    MenuReplay? _menuRun;
    (MenuReplay Run, Retrace.Core.MenuChoice Menu)? _lastMenu;
    readonly HashSet<int> _unverified = new();   // the context menu choice of the current segment, and the last one done (its click runs queued: a problem shows up later)

    /// <summary>The replay ends or the dialog is dropped: a context menu this filler (or a nested one) opened is closed.</summary>
    public void Abandon() { _menuRun?.CloseAll(); _lastMenu?.Run.CloseAll(); _child?.Abandon(); }

    void GiveUp(string? note) { State = Phase.GaveUp; Note ??= note; _menuRun?.CloseAll(); }

    public void Tick(DateTime now)
    {
        try { TickCore(now); }
        catch (Exception ex) { Log.Error("dialog autofill", ex); GiveUp("auto-fill failed: " + ex.Message); }
    }

    void TickCore(DateTime now)
    {
        if (State == Phase.GaveUp) return;
        if (_started == DateTime.MinValue) _started = now;
        if (_pressFailure is { } pressFailed) { _pressFailure = null; GiveUp(pressFailed); return; }   // a queued WPF press that could not be done (any phase, also after the close was pressed)
        if (State == Phase.Clicked)
        {
            if (Native.Exists(_dlg) && now - _at > CloseTimeout) { Note = "The dialog is still open after pressing " + (_mode == AutoMode.Cancel ? "Cancel" : "OK") + " (a message box?)."; State = Phase.GaveUp; }
            return;
        }
        if (_dlg == IntPtr.Zero)
        {
            var h = Native.ForegroundOfThisProcess();
            if (h != IntPtr.Zero && h != _main && FillMatcher.TitleMatches(_title, Native.TitleOf(h))) { _dlg = h; }
            else { if (now - _started > FindTimeout) GiveUp("could not find the dialog window"); return; }
        }
        if (!Native.Exists(_dlg))
        {
            // the button that was pressed closed the dialog (Yes / No of a message box, a Done button): that is how the recording ended too
            if (State is Phase.Pressed or Phase.Back && IsClosingClick) { State = Phase.Clicked; _at = now; return; }
            GiveUp("the dialog is gone");
            return;
        }

        if (UsesWpf && TickWpf(now)) return;   // a WPF dialog: in process (DialogAutoFill.Wpf.cs)

        switch (State)
        {
            case Phase.UiaRunning: TickUia(now); return;
            case Phase.UiaHold: TickHold(now); return;
            case Phase.Verify: VerifyAndFinish(now); return;
            case Phase.Pressed: TickPressed(now); return;
            case Phase.Nested: TickNested(now); return;
            case Phase.Back: TickBack(now); return;
        }
        Seek(now);
    }

    /// <summary>The recording says the last click closed the dialog (<c>closedWith: "click"</c>): pressing it here ends the dialog too, and nothing is pressed after it.</summary>
    bool IsClosingClick => Seg.Button != null && _seg == _segments.Count - 2 && DialogSegments.LastClickCloses(_closedWith, _segments.Count - 1);

    /// <summary>Seek: read the dialog until the controls are there, set this segment's values.</summary>
    void Seek(DateTime now)
    {
        if (_segStart == DateTime.MinValue) _segStart = now;
        bool expired = now - _segStart > ControlsTimeout;
        var values = Seg.Values;
        // the recorded `source` is not trusted: a recorder read too early fell back to UI Automation on a plain Win32 dialog (Drawing Units)
        var scan = Scan(_dlg, readTicks: false);
        if (_mode == AutoMode.MessageOk)
        {
            // a message box: texts and buttons only. Anything else (fields, a tree (also a field now), a list view or grid the scan does not
            // count as fields, no Win32 buttons: WPF, a task dialog) goes to a person
            if (scan.Fields.Count > 0 || (scan.Buttons.Count > 0 && !Native.OnlyTextAndButtons(_dlg))) { GiveUp("the dialog has controls the recording could not read"); return; }
            if (scan.Buttons.Count == 0) { if (expired) GiveUp("no buttons to read in the dialog"); return; }
            Close(scan, now);
            return;
        }
        if (values.Count == 0)
        {
            if (scan.Buttons.Count == 0 && !expired) return;
            if (scan.Buttons.Count == 0 && scan.Fields.Count == 0)
            {
                if (HasClicks) GiveUp("the dialog has no controls Retrace can press a button in"); else StartUia(now);
                return;
            }
            Finish(scan, now);
            return;
        }
        // a dialog with Win32 tabs holds its fields on pages that may not show yet: no field on the page showing is no sign of a WPF dialog while a value carries a tab
        bool tabbed = scan.Tabs.Count > 0 && (values.Any(v => FillMatcher.TabOfChange(v).Length > 0) || Seg.Tab.Length > 0);
        if (scan.Fields.Count == 0 && !tabbed)
        {
            // no child windows to read: a WPF dialog (its buttons cannot be pressed from here)
            if (expired) { if (HasClicks) GiveUp("the dialog has no controls Retrace can fill and press buttons in"); else StartUia(now); }
            return;
        }
        var map = FillMatcher.Match(values, scan.Fields.Select(LiveField.Of).ToList());
        var results = Cur.Results;
        // each value's controls timeout runs from when it was first tried on its own tab; showing another tab does not restart it
        var items = new List<FillItem>();
        for (int i = 0; i < values.Count; i++)
        {
            var tab = map[i] < 0 && FillMatcher.IsNoOp(values[i]) ? "" : FillMatcher.TabOfChange(values[i]);
            bool pending = results[i].Status == FieldFillResult.NotFound;
            bool showing = tab.Length == 0 || TabPlan.Next(tab, scan.Tabs).Action == TabAction.Done;
            if (pending && showing && Cur.Since[i] == default) Cur.Since[i] = now;
            items.Add(new FillItem(tab, map[i] >= 0, pending, pending && map[i] >= 0 && results[i].Message != null, pending && showing && now - Cur.Since[i] > ControlsTimeout));
        }
        var plan = TabFill.Next(items, scan.Tabs, expired);
        bool failedNow = false;
        foreach (var i in TreeChecks.OrderForApply(values, plan.Set))
        {
            if (FillMatcher.IsNoOp(values[i])) { results[i] = FieldFillResult.Ok(values[i].Label, "nothing to choose"); continue; }
            var err = DialogFill.ApplyWin32(scan.Fields[map[i]], scan.Handles[map[i]], values[i], Cur.Ticks);
            // a value that did not go in is tried again (on its own tab) until its controls timeout: the program may still be filling the dialog (a DCL
            // popup_list gets its items right after the dialog shows; "no entry" was a race, not a missing item)
            results[i] = err == null ? FieldFillResult.Ok(values[i].Label)
                : items[i].Expired ? FieldFillResult.Fail(values[i].Label, err) : FieldFillResult.Missing(values[i].Label, err);
            if (err != null) failedNow = true;
        }
        foreach (var i in plan.Fail)
            results[i] = FieldFillResult.Fail(values[i].Label, plan.Problem ?? results[i].Message ?? "no matching control in the dialog");
        if (plan.Switch != null && !failedNow) { _pendingTab = plan.SwitchPath; State = Phase.Verify; return; }   // verified on the next tick, then the tab is shown
        if (plan.Fail.Count > 0 || results.Any(r => r.Status == FieldFillResult.Failed) || results.All(r => r.Status != FieldFillResult.NotFound)) State = Phase.Verify;   // verify on the next tick, after the dialog handled its messages
    }

    void VerifyAndFinish(DateTime now)
    {
        // a context menu choice is made (its command is posted): the dialog is the program's now (a Mark changed the list item the segment had set), so nothing is read back: wait for the menu to be gone
        if (_menuRun is { Committed: true }) { PressMenu(Scan(_dlg), now); return; }
        var values = Seg.Values;
        var results = Cur.Results;
        var scan = Scan(_dlg, readTicks: false);
        var map = FillMatcher.Match(values, scan.Fields.Select(LiveField.Of).ToList());
        for (int i = 0; i < values.Count; i++)
            // every value set so far that shows on the tab now is read back again (a later handler that put an earlier value back is caught); one on a tab left behind was read back once
            // when it was set, and the earlier tabs are not shown again just for this
            if (results[i].Status == FieldFillResult.Applied && !FillMatcher.IsNoOp(values[i]) && (map[i] >= 0 || !Cur.Checked.Contains(i)))
            {
                results[i] = DialogFill.Verify(values[i], map[i] < 0 ? null : DialogFill.LiveValue(scan, map[i], values[i]), results[i]);
                if (results[i].Status == FieldFillResult.Applied) Cur.Checked.Add(i);
            }
        if (_pendingTab != null && results.All(r => r.Status != FieldFillResult.Failed))
        {
            // the values on this tab are in and right: the tab the next value sits on is shown, and the values go on from there
            var tab = _pendingTab; _pendingTab = null;
            EnsureTab(scan, tab, now);
            if (State != Phase.GaveUp) State = Phase.Seek;
            return;
        }
        if (results.Any(r => r.Status != FieldFillResult.Applied)) { GiveUp(null); return; }
        Finish(scan, now);
    }

    /// <summary>The field values and the static texts of a scan, as one text: what "nothing changed since the press" compares.</summary>
    string PressState(Win32Scan scan) => string.Join("\u001f", scan.Fields.Select(f => f.Key + "=" + f.Value)) + "\u001e" + string.Join("\u001f", ReadStatics(_dlg).Select(t => t.Text));

    /// <summary>Makes the dialog show <paramref name="tab"/> (a path of tab texts, <see cref="TabPath"/>) before a value is set or a button pressed. True when it shows it already.
    /// False when a tab control was switched (the page's controls exist on the next tick: the caller looks again then), when the tab is not there yet (looked for until the controls time
    /// out), or when the replay gave up (a page refused to be left, no such tab, the tabs keep changing: <see cref="State"/> is then GaveUp).</summary>
    bool EnsureTab(Win32Scan scan, string? tab, DateTime now)
    {
        if (string.IsNullOrEmpty(tab)) return true;
        var step = TabPlan.Next(tab, scan.Tabs);
        switch (step.Action)
        {
            case TabAction.Done:
                return true;
            case TabAction.Missing:
                // the tab control of a nested level exists only once the page above it is shown, and pages are made when they are first shown: wait a while
                if (now - _segStart > ControlsTimeout) GiveUp(step.Problem);
                return false;
            default:
                if (++_switches > MaxSwitches) { GiveUp("the tabs keep changing"); return false; }
                var tabControl = scan.TabHandles[step.Tab];
                if (TabControl32.Select(tabControl, step.Item) is { } why) { GiveUp($"could not open the {step.Text} tab: {why}"); return false; }
                _tabsShown.Add((tabControl, step.Item, step.Text));
                _segStart = now;   // the page's controls get their time from here
                return false;
        }
    }

    /// <summary>The segment's values are in: press its button, or close the dialog if it is the last one.</summary>
    void Finish(Win32Scan scan, DateTime now)
    {
        if (Seg.Button == null) Close(scan, now);
        else PressButton(scan, now);
    }

    /// <summary>Posts a click on the segment's button (the <see cref="DialogSegment.ButtonIndex"/>-th enabled one with that text). A button that is not there or not
    /// enabled yet is looked for again on the next tick, until the controls time out (it may be enabled by the values just set).</summary>
    void PressButton(Win32Scan scan, DateTime now)
    {
        var seg = Seg;
        // the click was made on a tab: its button is on that page (or it was pressed with that tab showing)
        if (!EnsureTab(scan, seg.Tab, now)) { if (State != Phase.GaveUp) State = Phase.Verify; return; }   // looked at again on the next tick
        if (seg.Menu != null) { PressMenu(scan, now); return; }
        int want = Math.Max(1, seg.ButtonIndex), seen = 0;
        var target = IntPtr.Zero;
        if (seg.IsDoubleClick)
        {
            // a double-click on a tree item: the tree is the live field of the recorded tree
            var at = FillMatcher.Match(new[] { new FieldChange(seg.Tree, seg.TreeLabel, "tree", "", "", seg.Tab) }, scan.Fields.Select(LiveField.Of).ToList())[0];
            if (at >= 0 && scan.Handles[at].Count > 0) target = scan.Handles[at][0];
        }
        else
            foreach (var (h, _, text) in scan.Buttons)
                if (string.Equals(PaletteFill.ButtonLabel(text), seg.Button, StringComparison.OrdinalIgnoreCase) && ++seen == want) { target = h; break; }
        if (target == IntPtr.Zero)
        {
            if (now - _segStart > ControlsTimeout) GiveUp(seg.IsDoubleClick ? $"no tree \"{seg.TreeLabel}\" in the dialog" : $"no enabled button \"{seg.Button}\" in the dialog");
            else State = Phase.Verify;   // tried again on the next tick
            return;
        }
        // the last click of a dialog that no OK or Cancel closes: it is the dialog's close, so a Tdoc run takes its picture first (see Close)
        if (BeforeClose != null && IsClosingClick && !BeforeClose(ShotOf(scan, true, (target, seg.Button!)))) { State = Phase.Verify; return; }   // tried again on the next tick
        Cur.Statics = ReadStatics(_dlg);   // the static texts just before the press, read on request (the polls of Seek and Verify do not read them)
        if (seg.IsDoubleClick)
        {
            // the item is selected and the mouse messages are sent from the dispatcher after this tick (what its handler opens runs with the ticks going on)
            if (TreeControl.DoubleClick(target, seg.Path, a => DialogFill.Later("tree double-click", a)) is { } why) { GiveUp($"could not double-click {seg.Path}: {why}"); return; }
        }
        else if (!DialogFill.PostPress(target, seg.OnDown, out var pressWhy)) { GiveUp($"could not press {seg.Button}" + (pressWhy != null ? $": {pressWhy}" : "")); return; }
        Cur.Pressed = true;
        _pressed.Add((target, seg.Button!));
        _pressedAt = now; _nestedUsed = 0; _lastNested = IntPtr.Zero; _mouseTried = seg.OnDown || seg.IsDoubleClick || !DialogFill.IsWinForms(target); _pressState = PressState(scan);
        State = Phase.Pressed;
    }

    /// <summary>The segment's click is a context menu choice: right-click the control (selecting the tree or list item first), pick the item by its text, check the menu closed (<see cref="MenuReplay"/>).
    /// Pending: looked at again on the next tick. Done: the same as after a pressed button (a nested dialog the item opens is handled by <see cref="TickPressed"/>).</summary>
    void PressMenu(Win32Scan scan, DateTime now)
    {
        var seg = Seg;
        _menuRun ??= new MenuReplay(seg.Menu!, _dlg);
        switch (_menuRun.Step(now, scan))
        {
            case MenuReplay.Status.Pending:
                State = Phase.Verify;   // tried again on the next tick
                return;
            case MenuReplay.Status.Failed:
                GiveUp($"{seg.Menu!.Phrase()}: {_menuRun.Message}");
                return;
        }
        _lastMenu = (_menuRun, seg.Menu!); if (_menuRun.Unverified) _unverified.Add(_seg); _menuRun = null;
        Cur.Statics = ReadStatics(_dlg);
        Cur.Pressed = true;
        _pressedAt = now; _nestedUsed = 0; _lastNested = IntPtr.Zero;
        State = Phase.Pressed;
    }

    /// <summary>After a press: a nested dialog opens (done by a child filler), or nothing does for <see cref="QuietTime"/> and the next segment follows.</summary>
    void TickPressed(DateTime now)
    {
        var seg = Seg;
        if (_lastMenu is { } lm && lm.Run.LateProblem is { } late) { GiveUp($"{lm.Menu.Phrase()}: {late}"); return; }
        var nested = FindNested();
        if (nested != IntPtr.Zero)
        {
            if (_nestedUsed >= seg.Nested.Count) { GiveUp($"pressing {seg.Button} opened the dialog \"{Native.TitleOf(nested)}\", which the recording does not have"); return; }
            var rec = seg.Nested[_nestedUsed];
            var mode = DialogSegments.ModeOf(rec);
            if (mode == AutoMode.None) { GiveUp($"the recording has no values for the dialog \"{rec.DialogTitle}\" that {seg.Button} opens"); return; }
            if (!FillMatcher.TitleMatches(rec.DialogTitle, Native.TitleOf(nested)))
            {
                GiveUp($"pressing {seg.Button} opened the dialog \"{Native.TitleOf(nested)}\", the recording has \"{rec.DialogTitle}\"");
                return;
            }
            _child = new DialogAutoFiller(mode, rec, _main, nested, _ignore);
            _nestedUsed++; _lastNested = nested;
            State = Phase.Nested;
            return;
        }
        bool more = _nestedUsed < seg.Nested.Count;
        // the Click of the button did not open what the recording has within 2 s (a recording from before `onDown`, whose button acts on the mouse going down): the mouse messages are sent once, but
        // only when nothing happened since the press (the dialog is enabled, no window opened, no field or static text changed): never a second press of a button whose first one did something
        if (!_mouseTried && more && _nestedUsed == 0 && now - _pressedAt > MouseRetryAfter && _pressed.Count > 0 && Native.Exists(_pressed[^1].Button))
        {
            _mouseTried = true;
            var scanNow = Scan(_dlg, readTicks: false);
            if (DialogClickRules.MayRetryPress(Native.IsEnabled(_dlg), nested != IntPtr.Zero, PressState(scanNow) != _pressState) && DialogFill.PostMouseClick(_pressed[^1].Button, out _))
            {
                Log.Info($"pressing {seg.Button} did nothing; pressing it again with the mouse messages");
                return;
            }
        }
        // (TickCore ends the dialog the moment its window is gone: for a click the recording says closed it, that is the end)
        switch (DialogClickRules.AfterPressRule(IsClosingClick, more, now - _pressedAt, QuietTime, NestedOpenTimeout, CloseTimeout))
        {
            case DialogClickRules.AfterPress.Wait: return;
            case DialogClickRules.AfterPress.GiveUpStillOpen: GiveUp($"the dialog is still open after pressing {seg.Button}, which closed it in the recording"); return;
            case DialogClickRules.AfterPress.GiveUpNestedMissing: GiveUp($"pressing {seg.Button} did not open the dialog \"{seg.Nested[_nestedUsed].DialogTitle}\""); return;
        }
        _lastMenu = null;   // a menu belongs to its own segment
        _seg++; _segStart = now; _nestedUsed = 0; _tabsShown.Clear(); _switches = 0;   // the limit is per segment
        State = Phase.Seek;
    }

    void TickNested(DateTime now)
    {
        var c = _child!;
        c.Tick(now);
        if (c.State == Phase.GaveUp)
        {
            Cur.Nested.Add(new NestedDialogResult { Title = c.ActualTitle, Segments = c.SegmentResults() });
            Note ??= $"in the nested dialog \"{c.ActualTitle}\": {c.Note ?? "it could not be done"}";
            GiveUp(null);
            return;
        }
        if (!c.Closed) return;
        Cur.Nested.Add(new NestedDialogResult { Title = c.ActualTitle, Segments = c.SegmentResults() });
        _child = null;
        State = Phase.Back; _backSince = now;
    }

    /// <summary>The nested dialog is gone: wait until this one takes input again (it is disabled while another dialog is open), then see whether the button opens another.</summary>
    void TickBack(DateTime now)
    {
        var other = FindNested();
        if (other != IntPtr.Zero && other != _lastNested) { State = Phase.Pressed; return; }   // a second dialog: handled like the first
        if (other == IntPtr.Zero && Native.IsEnabled(_dlg) && now - _backSince >= BackSettle) { _pressedAt = now; State = Phase.Pressed; return; }
        if (now - _backSince > BackTimeout) GiveUp("the dialog did not take input again after the nested dialog closed");
    }

    /// <summary>The modal dialog that opened on top of this one, or zero (see <see cref="ModalAbove"/>; also the only candidate, the dialog being disabled).</summary>
    IntPtr FindNested() => ModalAbove.Find(_dlg, _main, _ignore, onlyCandidate: true);

    void Close(Win32Scan scan, DateTime now)
    {
        bool ok = _mode != AutoMode.Cancel;
        // closed with OK from the tab the person closed it from (the page that checks its input is the one showing)
        if (!EnsureTab(scan, Seg.Tab, now)) { if (State != Phase.GaveUp) State = Phase.Verify; return; }
        if (BeforeClose != null && !BeforeClose(ShotOf(scan, ok))) return;   // tried again on the next tick
        Cur.Statics = ReadStatics(_dlg);
        if (DialogFill.CloseWin32(_dlg, scan, ok, out _)) { State = Phase.Clicked; _at = DateTime.Now; }
        else GiveUp($"could not find the {(ok ? "OK" : "Cancel")} button");
    }

    /// <summary>The filled controls and the close button of a Win32 dialog, for a Tdoc picture. Only the last segment's values are shown (the earlier ones were
    /// replaced or consumed); the buttons pressed on the way get a box of their own.</summary>
    DialogShot ShotOf(Win32Scan scan, bool ok, (IntPtr Button, string Label)? closingClick = null)
    {
        var values = Seg.Values;
        var results = Cur.Results;
        var filled = new List<DialogBox>();
        var map = FillMatcher.Match(values, scan.Fields.Select(LiveField.Of).ToList());
        var treesBoxed = new HashSet<IntPtr>();
        for (int i = 0; i < values.Count; i++)
            if (map[i] >= 0 && results[i].Status == FieldFillResult.Applied && !FillMatcher.IsNoOp(values[i]) && scan.Handles[map[i]].Count > 0
                && (!TreeChecks.Is(values[i].Kind) || treesBoxed.Add(scan.Handles[map[i]][0])))   // the ticks of one tree get one box
                filled.Add(new DialogBox(scan.Handles[map[i]][0], default, TdocPlan.FieldCallout(values[i])));
        // the tabs clicked on the way: a box on the tab (a field on a tab that is not showing now has no box; the instruction sentences name its tab)
        var tabBoxes = new List<DialogBox>();
        foreach (var (tab, item, text) in _tabsShown)
            if (scan.TabHandles.Contains(tab) && TabControl32.ItemScreenRect(tab, item) is { } tr)
                tabBoxes.Add(new DialogBox(IntPtr.Zero, new ScreenRect(tr.L, tr.T, tr.R, tr.B), "Click the " + text + " tab"));
        filled.InsertRange(0, tabBoxes);
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (h, label) in _pressed)
            if (Native.Exists(h) && seenLabels.Add(label)) filled.Add(new DialogBox(h, default, "Click " + label));
        if (closingClick is { } cc) return new DialogShot(_dlg, filled, new DialogBox(cc.Button, default, TdocPlan.CloseCallout(ok, cc.Label)));
        int b = FillMatcher.PickButton(scan.Buttons.Select(x => (x.Id, x.Text)).ToList(), ok);
        return new DialogShot(_dlg, filled, b < 0 ? null : new DialogBox(scan.Buttons[b].H, default, TdocPlan.CloseCallout(ok, scan.Buttons[b].Text)));
    }

    /// <summary>What the replay did, segment by segment, for the replay's result file. <paramref name="personFinished"/>: the replay gave up and a person finished the step by hand,
    /// so the segment it gave up in and the ones after it are the person's ("person"), not failed or not run.</summary>
    public List<SegmentResult> SegmentResults(bool personFinished = false)
    {
        var list = new List<SegmentResult>();
        var done = Completed;
        for (int i = 0; i < _segments.Count; i++)
        {
            var run = _runs[i];
            var status = DialogSegments.StatusOf(i, _seg, done, State == Phase.GaveUp, personFinished);
            var failed = status is SegmentResult.Failed || (status == SegmentResult.Person && i == _seg && State == Phase.GaveUp);
            list.Add(new SegmentResult
            {
                Button = _segments[i].Button, Status = status,
                Message = failed ? Note ?? string.Join("; ", run.Results.Where(r => r.Status != FieldFillResult.Applied).Select(r => r.Label + " " + r.Status + (r.Message != null ? " (" + r.Message + ")" : ""))) : _unverified.Contains(i) ? "unverified: the menu command changed no field, text or window the replay could see" : null,
                Fields = run.Results.Count > 0 && status is not (SegmentResult.NotRun or SegmentResult.Person) ? run.Results : null,
                Statics = run.Statics is { Count: > 0 } ? run.Statics : null,
                Nested = run.Nested.Count > 0 ? run.Nested : null,
            });
            if (failed && list[^1].Message is { Length: 0 }) list[^1].Message = null;
        }
        return list;
    }

    /// <summary>True when the dialog was handled through UI Automation (no segments, no static texts).</summary>
    public bool UsedUia => _uia;

    void StartUia(DateTime now)
    {
        var dlg = _dlg; var values = Seg.Values.Select(v => v with { Tab = "", Key = TabPath.Unqualified(v.Key) }).ToList(); bool ok = _mode != AutoMode.Cancel; bool hold = BeforeClose != null;     // UI Automation sees no tabs
        _uia = true;
        _task = Task.Run(() => DialogFill.FillUia(dlg, values, ok, hold));
        _at = now; State = Phase.UiaRunning;
    }

    void TickUia(DateTime now)
    {
        if (_task == null) { GiveUp("auto-fill failed"); return; }
        if (!_task.IsCompleted)
        {
            if (now - _at > UiaTimeout) GiveUp("UI Automation took too long");
            return;
        }
        if (_task.IsFaulted) { GiveUp("UI Automation failed: " + _task.Exception?.GetBaseException().Message); return; }
        var o = _task.Result;
        var results = Cur.Results;
        for (int i = 0; i < o.Results.Count && i < results.Count; i++) results[i] = o.Results[i];
        if (o.Clicked) { State = Phase.Clicked; _at = now; }
        else if (o.Click != null && o.Shot != null) { _hold = o; State = Phase.UiaHold; _at = now; }
        else GiveUp(o.Note);
    }

    /// <summary>UI Automation filled the dialog but has not pressed the button: the Tdoc run takes its picture, then the press is made.</summary>
    void TickHold(DateTime now)
    {
        var o = _hold!;
        if (!BeforeClose!(o.Shot!) && now - _at < HoldTimeout) return;
        _ = Task.Run(o.Click!);   // Invoke may not return until the dialog is gone: never wait for it
        State = Phase.Clicked; _at = now;
    }
}
