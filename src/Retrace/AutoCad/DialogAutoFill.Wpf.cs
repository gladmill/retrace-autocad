using Retrace.Core;
using static Retrace.AutoCad.DialogReader;

namespace Retrace.AutoCad;

/// <summary>
/// The WPF half of <see cref="DialogAutoFiller"/>: a modal WPF window of AutoCAD's process (one window, no child windows) is filled and its buttons are pressed in process, from the tick, through its visual tree
/// (<see cref="WpfDialogs"/>, <see cref="WpfPalette.Apply"/>): no UI Automation, so no pool thread and no read in flight when a button is pressed. The values of a segment are set, read back on the next tick,
/// and the segment's button is pressed from the dispatcher after the tick (a button that opens a modal window runs that window's loop from there). What follows a press is the same as for a Win32 dialog:
/// <see cref="Phase.Pressed"/> finds the nested dialog by its window (a WinForms or a WPF one, <see cref="ModalAbove"/>) and a child filler does it, whatever its kind. Used for a dialog recorded with source "wpf" or with clicks.
/// </summary>
sealed partial class DialogAutoFiller
{
    bool? _wpf;
    /// <summary>The tab controls, trees, grids and lists the recording saw (`unsupported`): named in each segment's note together with the ones the window shows at the segment's end.</summary>
    readonly List<WpfPresses.Unsupported> _recordedParts;
    /// <summary>What the window showed of them at the ends of the segments so far (by <see cref="WpfPresses.Unsupported.Key"/>); the note of every segment is the recorded and these together.</summary>
    readonly Dictionary<string, WpfPresses.Unsupported> _liveParts = new();
    /// <summary>The note of a segment of this dialog: what the recording saw and what the window showed (null when none; a recording that has parts says so even if the window was never found).</summary>
    string? PartsNote() => WpfPresses.UnsupportedNote(WpfPresses.Union(_recordedParts, _liveParts.Values));
    /// <summary>Why a queued press could not be done (set from the dispatcher, on the UI thread); <see cref="TickCore"/> gives up with it, in any phase.</summary>
    string? _pressFailure;
    /// <summary>Where the buttons pressed on the way were, for the boxes of a Tdoc picture.</summary>
    readonly List<(ScreenRect Rect, string Label)> _pressedWpf = new();

    /// <summary>The window is a WPF window and the recording is done in process (<see cref="WpfPresses.ReplayInProcess"/>). A message box is not.</summary>
    bool UsesWpf => _dlg != IntPtr.Zero && (_wpf ??= _mode != AutoMode.MessageOk && WpfPresses.ReplayInProcess(_source, HasClicks) && WpfDialogs.IsWindow(_dlg)) == true;

    /// <summary>True when the tick was dealt with here; false leaves the phases that do not depend on the kind of dialog (a press waiting for its nested dialog, a nested filler, coming back) to the common code.</summary>
    bool TickWpf(DateTime now)
    {
        switch (State)
        {
            case Phase.Seek: SeekWpf(now); return true;
            case Phase.Verify: VerifyWpf(now); return true;
            default: return false;
        }
    }

    void SeekWpf(DateTime now)
    {
        if (_segStart == DateTime.MinValue) _segStart = now;
        bool expired = now - _segStart > ControlsTimeout;
        var values = Seg.Values;
        if (values.Count == 0) { FinishWpf(WpfDialogs.Read(_dlg), now); return; }
        var scan = WpfDialogs.Read(_dlg);
        var map = FillMatcher.Match(values, scan.Fields.Select(LiveField.Of).ToList());
        var results = Cur.Results;
        string? tabHint = null;   // asked once, for the first value that is not found
        for (int i = 0; i < values.Count; i++)
        {
            if (results[i].Status != FieldFillResult.NotFound) continue;
            if (FillMatcher.IsNoOp(values[i])) { results[i] = FieldFillResult.Ok(values[i].Label, "nothing to choose"); continue; }
            if (map[i] < 0)
            {
                if (expired) results[i] = FieldFillResult.Fail(values[i].Label, "no matching control in the dialog" + (tabHint ??= WpfPresses.TabHint(WpfPresses.Union(_recordedParts, scan.ReadParts(statics: false).Unsupported))));
                continue;
            }
            // a value that did not go in is tried again until the controls timeout: a handler may still be filling the dialog
            var err = WpfPalette.Apply(scan.Elements[map[i]], scan.Fields[map[i]], values[i]);
            results[i] = err == null ? FieldFillResult.Ok(values[i].Label) : expired ? FieldFillResult.Fail(values[i].Label, err) : FieldFillResult.Missing(values[i].Label, err);
        }
        if (results.Any(r => r.Status == FieldFillResult.Failed) || results.All(r => r.Status != FieldFillResult.NotFound)) State = Phase.Verify;   // read back on the next tick, after the dialog handled its messages
    }

    void VerifyWpf(DateTime now)
    {
        var values = Seg.Values;
        var results = Cur.Results;
        var scan = WpfDialogs.Read(_dlg);
        var map = FillMatcher.Match(values, scan.Fields.Select(LiveField.Of).ToList());
        for (int i = 0; i < values.Count; i++)
            if (results[i].Status == FieldFillResult.Applied && !FillMatcher.IsNoOp(values[i]))
                results[i] = DialogFill.Verify(values[i], map[i] < 0 ? null : scan.Fields[map[i]].Value, results[i]);
        if (results.Any(r => r.Status != FieldFillResult.Applied)) { NoteSegment(scan, gaveUp: true); GiveUp(null); return; }
        FinishWpf(scan, now);
    }

    void FinishWpf(WpfPalette.Scan scan, DateTime now)
    {
        if (Seg.Button == null) CloseWpf(scan, now); else PressWpf(scan, now);
    }

    /// <summary>Presses the segment's button (the <see cref="DialogSegment.ButtonIndex"/>-th enabled one with that text) from the dispatcher. A button that is not there or not enabled yet is looked for again on the next tick.</summary>
    void PressWpf(WpfPalette.Scan scan, DateTime now)
    {
        var seg = Seg;
        int at = WpfPresses.Find(WpfDialogs.Texts(scan), seg.Button!, seg.ButtonIndex);
        if (at < 0)
        {
            if (now - _segStart > ControlsTimeout) { NoteSegment(scan, gaveUp: true); GiveUp($"no enabled button \"{seg.Button}\" in the dialog"); }
            else State = Phase.Verify;   // tried again on the next tick
            return;
        }
        var button = scan.Buttons[at].Button;
        if (BeforeClose != null && IsClosingClick && !BeforeClose(ShotOfWpf(scan, true, scan.Buttons[at].Text, button))) { State = Phase.Verify; return; }
        if (WpfDialogs.RectOf(button) is { } rect) _pressedWpf.Add((rect, seg.Button!));
        WpfDialogs.PressLater(button, seg.Button!, StillWanted, why => _pressFailure ??= why);
        NoteSegment(scan);
        Cur.Pressed = true;
        _pressedAt = now; _nestedUsed = 0; _lastNested = IntPtr.Zero;
        State = Phase.Pressed;
    }

    void CloseWpf(WpfPalette.Scan scan, DateTime now)
    {
        bool ok = _mode != AutoMode.Cancel;
        int b = WpfPresses.PickClose(scan.Buttons.Select(x => new WpfPresses.CloseCandidate(x.Text, WpfDialogs.IsDefault(x.Button), WpfDialogs.IsCancel(x.Button))).ToList(), ok);
        if (b < 0)
        {
            // the button may not be there yet (enabled by the values just set): looked for again on the next tick, until the controls time out
            if (now - _segStart <= ControlsTimeout) { State = Phase.Verify; return; }
            // a Cancel with no button for it closes the window the way its close box does
            NoteSegment(scan);   // this ends the segment, by the close box or by giving up
            if (!ok && WpfDialogs.PostClose(_dlg)) { State = Phase.Clicked; _at = now; return; }
            GiveUp($"could not find the {(ok ? "OK" : "Cancel")} button");
            return;
        }
        if (BeforeClose != null && !BeforeClose(ShotOfWpf(scan, ok, scan.Buttons[b].Text, scan.Buttons[b].Button))) return;   // tried again on the next tick
        NoteSegment(scan);
        WpfDialogs.PressLater(scan.Buttons[b].Button, ok ? "OK" : "Cancel", StillWanted, why => _pressFailure ??= why);
        State = Phase.Clicked; _at = now;
    }

    /// <summary>The static texts just before the press or the close (read on request, with the window as it is now) and what the window has that is not followed, for the segment's result. On every way a segment ends.</summary>
    void NoteSegment(WpfPalette.Scan scan, bool gaveUp = false)
    {
        // a segment that gives up keeps the texts taken just before its press or close, if there were any
        bool keep = gaveUp && Cur.Statics != null;
        var parts = scan.ReadParts(statics: !keep);
        if (!keep) Cur.Statics = parts.Statics;
        foreach (var p in parts.Unsupported) _liveParts[p.Key] = p;
    }

    bool _stopped;
    /// <summary>The replay was stopped or moved on: a press still queued on the dispatcher is not made.</summary>
    public void Cancel() { _stopped = true; _child?.Cancel(); _menuRun?.CloseAll(); _lastMenu?.Run.CloseAll(); }   // (a context menu it opened is closed too)
    bool StillWanted() => !_stopped && State != Phase.GaveUp;

    /// <summary>The filled controls and the button that closes it, for a Tdoc picture (screen rectangles: a WPF window has no child windows to find again).</summary>
    DialogShot ShotOfWpf(WpfPalette.Scan scan, bool ok, string closeText, System.Windows.Controls.Primitives.ButtonBase close)
    {
        var values = Seg.Values;
        var results = Cur.Results;
        var map = FillMatcher.Match(values, scan.Fields.Select(LiveField.Of).ToList());
        var filled = new List<DialogBox>();
        for (int i = 0; i < values.Count; i++)
            if (map[i] >= 0 && results[i].Status == FieldFillResult.Applied && !FillMatcher.IsNoOp(values[i]) && WpfDialogs.RectOf(scan.Elements[map[i]]) is { } r)
                filled.Add(new DialogBox(IntPtr.Zero, r, TdocPlan.FieldCallout(values[i])));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rect, label) in _pressedWpf)
            if (seen.Add(label)) filled.Add(new DialogBox(IntPtr.Zero, rect, "Click " + label));
        var box = WpfDialogs.RectOf(close) is { } cr ? new DialogBox(IntPtr.Zero, cr, IsClosingClick ? TdocPlan.CloseCallout(ok, Seg.Button ?? closeText) : TdocPlan.CloseCallout(ok, closeText)) : null;
        return new DialogShot(_dlg, filled, box);
    }
}
