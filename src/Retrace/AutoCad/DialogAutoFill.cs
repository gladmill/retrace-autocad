using static Retrace.AutoCad.DialogReader;

namespace Retrace.AutoCad;

/// <summary>
/// RETRACERUN: fills one open modal dialog with the recorded values and closes it the way the recording did. A small state machine the
/// replayer ticks (UI thread, every 200 ms) while it would otherwise wait for a person; a dialog that cannot be handled (not found, a value
/// that did not go in, no OK button, still open after the click) ends in <see cref="Phase.GaveUp"/> and the person gets today's pause message.
/// Win32 dialogs are handled on the tick itself (SendMessage to our own process is cheap); dialogs without child windows go through UI
/// Automation on a pool thread, never on the UI thread.
/// </summary>
sealed class DialogAutoFiller
{
    public enum Phase { Seek, Verify, UiaRunning, UiaHold, Clicked, GaveUp }

    /// <summary>The dialog window is looked for this long (it may need a moment to become the foreground window).</summary>
    public static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Controls may not exist yet shortly after the dialog opened (Drawing Units): reading is retried this long.</summary>
    public static readonly TimeSpan ControlsTimeout = TimeSpan.FromMilliseconds(1500);
    /// <summary>After the close click the dialog must be gone within this time, else a validation message box is assumed.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan UiaTimeout = TimeSpan.FromSeconds(6);
    /// <summary>A Tdoc run holds the close click for its picture (<see cref="BeforeClose"/>); after this long the dialog is closed anyway.</summary>
    static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(30);

    readonly AutoMode _mode;
    readonly IReadOnlyList<FieldChange> _values;
    readonly string? _title;
    readonly string? _source;
    readonly IntPtr _main;
    IntPtr _dlg;
    DateTime _started = DateTime.MinValue, _found = DateTime.MinValue, _at = DateTime.MinValue;
    Task<DialogFill.UiaOutcome>? _task;
    DialogFill.UiaOutcome? _hold;

    public Phase State { get; private set; } = Phase.Seek;
    /// <summary>Parallel to the recorded values (empty when only Cancel is pressed).</summary>
    public List<FieldFillResult> Results { get; }
    /// <summary>Why it gave up, if it knows.</summary>
    public string? Note { get; private set; }
    public AutoMode Mode => _mode;

    /// <summary>Set by a Tdoc run: asked each tick, with the dialog filled in, before OK / Cancel is pressed. The close click waits until it
    /// returns true (the run takes its picture meanwhile). Null = no wait.</summary>
    public Func<DialogShot, bool>? BeforeClose { get; init; }

    /// <param name="main">AutoCAD's main window (never touched)</param>
    public DialogAutoFiller(AutoMode mode, IReadOnlyList<FieldChange>? values, string? title, string? source, IntPtr main)
    {
        _mode = mode; _title = title; _source = source; _main = main;
        _values = mode == AutoMode.Ok && values != null ? values : new List<FieldChange>();
        Results = _values.Select(v => FieldFillResult.Missing(v.Label)).ToList();
    }

    public bool Finished => State is Phase.Clicked or Phase.GaveUp;

    void GiveUp(string? note) { State = Phase.GaveUp; Note ??= note; }

    public void Tick(DateTime now)
    {
        try { TickCore(now); }
        catch (Exception ex) { Log.Error("dialog autofill", ex); GiveUp("auto-fill failed: " + ex.Message); }
    }

    void TickCore(DateTime now)
    {
        if (State == Phase.GaveUp) return;
        if (_started == DateTime.MinValue) _started = now;
        if (State == Phase.Clicked)
        {
            if (now - _at > CloseTimeout) { Note = "The dialog is still open after pressing " + (_mode == AutoMode.Cancel ? "Cancel" : "OK") + " (a message box?)."; State = Phase.GaveUp; }
            return;
        }
        if (_dlg == IntPtr.Zero)
        {
            var h = Native.ForegroundOfThisProcess();
            if (h != IntPtr.Zero && h != _main && FillMatcher.TitleMatches(_title, Native.TitleOf(h))) { _dlg = h; _found = now; }
            else { if (now - _started > FindTimeout) GiveUp("could not find the dialog window"); return; }
        }
        if (!Native.Exists(_dlg)) { GiveUp("the dialog is gone"); return; }

        if (State == Phase.UiaRunning) { TickUia(now); return; }
        if (State == Phase.UiaHold) { TickHold(now); return; }
        if (State == Phase.Verify) { VerifyAndClose(); return; }

        // Seek: read the dialog until the controls are there
        bool expired = now - _found > ControlsTimeout;
        // the recorded `source` is not trusted: a recorder read too early fell back to UI Automation on a plain Win32 dialog (Drawing Units)
        var scan = Scan(_dlg);
        if (_mode == AutoMode.MessageOk)
        {
            // a message box: texts and buttons only. Anything else (fields, a list view or tree the scan does not count as fields, no Win32
            // buttons: WPF, a task dialog) goes to a person
            if (scan.Fields.Count > 0 || (scan.Buttons.Count > 0 && !Native.OnlyTextAndButtons(_dlg))) { GiveUp("the dialog has controls the recording could not read"); return; }
            if (scan.Buttons.Count == 0) { if (expired) GiveUp("no buttons to read in the dialog"); return; }
            Close(scan);
            return;
        }
        if (_mode == AutoMode.Cancel || _values.Count == 0)
        {
            if (scan.Buttons.Count == 0 && !expired) return;
            if (scan.Buttons.Count == 0 && scan.Fields.Count == 0) { StartUia(now); return; }
            Close(scan);
            return;
        }
        if (scan.Fields.Count == 0)
        {
            if (expired) StartUia(now);   // no child windows to read: a WPF dialog
            return;
        }
        var map = FillMatcher.Match(_values, scan.Fields.Select(f => new LiveField(f.Key, f.Label, f.Kind)).ToList());
        for (int i = 0; i < _values.Count; i++)
        {
            var r = Results[i];
            if (r.Status != FieldFillResult.NotFound) continue;
            if (map[i] < 0) continue;
            if (FillMatcher.IsNoOp(_values[i])) { Results[i] = FieldFillResult.Ok(_values[i].Label, "nothing to choose"); continue; }
            var err = DialogFill.ApplyWin32(scan.Fields[map[i]], scan.Handles[map[i]], _values[i]);
            // a value that did not go in is tried again until the controls time out: the program may still be filling the dialog (a DCL
            // popup_list gets its items right after the dialog shows; "no entry" was a race, not a missing item)
            Results[i] = err == null ? FieldFillResult.Ok(_values[i].Label)
                : expired ? FieldFillResult.Fail(_values[i].Label, err) : FieldFillResult.Missing(_values[i].Label, err);
        }
        if (expired || Results.All(r => r.Status != FieldFillResult.NotFound)) State = Phase.Verify;   // verify on the next tick, after the dialog handled its messages
    }

    void VerifyAndClose()
    {
        var scan = Scan(_dlg);
        var map = FillMatcher.Match(_values, scan.Fields.Select(f => new LiveField(f.Key, f.Label, f.Kind)).ToList());
        for (int i = 0; i < _values.Count; i++)
            if (Results[i].Status == FieldFillResult.Applied && !FillMatcher.IsNoOp(_values[i]))
                Results[i] = DialogFill.Verify(_values[i], map[i] < 0 ? null : scan.Fields[map[i]].Value, Results[i]);
        if (Results.Any(r => r.Status != FieldFillResult.Applied)) { GiveUp(null); return; }
        Close(scan);
    }

    void Close(Win32Scan scan)
    {
        bool ok = _mode != AutoMode.Cancel;
        if (BeforeClose != null && !BeforeClose(ShotOf(scan, ok))) return;   // tried again on the next tick
        if (DialogFill.CloseWin32(_dlg, scan, ok, out _)) { State = Phase.Clicked; _at = DateTime.Now; }
        else GiveUp($"could not find the {(ok ? "OK" : "Cancel")} button");
    }

    /// <summary>The filled controls and the close button of a Win32 dialog, for a Tdoc picture.</summary>
    DialogShot ShotOf(Win32Scan scan, bool ok)
    {
        var filled = new List<DialogBox>();
        var map = FillMatcher.Match(_values, scan.Fields.Select(f => new LiveField(f.Key, f.Label, f.Kind)).ToList());
        for (int i = 0; i < _values.Count; i++)
            if (map[i] >= 0 && Results[i].Status == FieldFillResult.Applied && !FillMatcher.IsNoOp(_values[i]) && scan.Handles[map[i]].Count > 0)
                filled.Add(new DialogBox(scan.Handles[map[i]][0], default, TdocPlan.FieldCallout(_values[i])));
        int b = FillMatcher.PickButton(scan.Buttons.Select(x => (x.Id, x.Text)).ToList(), ok);
        return new DialogShot(_dlg, filled, b < 0 ? null : new DialogBox(scan.Buttons[b].H, default, TdocPlan.CloseCallout(ok, scan.Buttons[b].Text)));
    }

    void StartUia(DateTime now)
    {
        var dlg = _dlg; var values = _values; bool ok = _mode != AutoMode.Cancel; bool hold = BeforeClose != null;
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
        for (int i = 0; i < o.Results.Count && i < Results.Count; i++) Results[i] = o.Results[i];
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
