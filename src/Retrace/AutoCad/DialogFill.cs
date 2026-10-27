using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using static Retrace.AutoCad.DialogReader;

namespace Retrace.AutoCad;

/// <summary>
/// Writes recorded values into a live dialog from outside, the inverse of <see cref="DialogReader"/> (which shares its enumeration):
/// Win32 messages for child-window dialogs (UI thread, cheap), UI Automation patterns for the rest (POOL THREAD ONLY).
/// What to set where is decided by the pure <see cref="FillMatcher"/>. Nothing here throws.
/// </summary>
static class DialogFill
{
    const uint WM_SETTEXT = 0x000C, WM_COMMAND = 0x0111, WM_HSCROLL = 0x0114, WM_VSCROLL = 0x0115;
    const uint BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1, BM_CLICK = 0x00F5;
    const uint CB_SETCURSEL = 0x014E, CB_FINDSTRINGEXACT = 0x0158, LB_SETCURSEL = 0x0186, LB_FINDSTRINGEXACT = 0x01A2;
    const uint TBM_SETPOS = 0x0405, TBM_SETPOSNOTIFY = 0x0422, TBM_GETPOS = 0x0400;
    const int CBN_SELCHANGE = 1, CBN_EDITCHANGE = 5, CBN_SELENDOK = 9, LBN_SELCHANGE = 1, TB_THUMBPOSITION = 4;
    const uint SMTO_ABORTIFHUNG = 2, SetTimeoutMs = 500;
    const int GWL_STYLE = -16, IDOK = 1, IDCANCEL = 2;

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    static extern IntPtr SendStr(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendNum(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    static extern bool Post(IntPtr h, uint msg, IntPtr w, IntPtr l);

    static long Send(IntPtr h, uint msg, long w = 0, long l = 0)
    {
        SendNum(h, msg, (IntPtr)w, (IntPtr)l, SMTO_ABORTIFHUNG, SetTimeoutMs, out var r);
        return r.ToInt64();
    }

    static bool SendText(IntPtr h, uint msg, long w, string text, out long result)
    {
        var ok = SendStr(h, msg, (IntPtr)w, text, SMTO_ABORTIFHUNG, SetTimeoutMs, out var r) != IntPtr.Zero;
        result = r.ToInt64();
        return ok;
    }

    /// <summary>WM_COMMAND(code, id) to the control's parent, as the control itself would send it.</summary>
    static void Notify(IntPtr ctl, int code)
    {
        var parent = GetParent(ctl);
        if (parent == IntPtr.Zero) return;
        var id = GetDlgCtrlID(ctl) & 0xFFFF;
        SendNum(parent, WM_COMMAND, (IntPtr)((code << 16) | id), ctl, SMTO_ABORTIFHUNG, SetTimeoutMs, out _);
    }

    // ---- Win32 -----------------------------------------------------------------------------------------------------------

    /// <summary>Puts the recorded `after` value into the live field. Null = done, else why not. UI thread.</summary>
    internal static string? ApplyWin32(DialogField live, IReadOnlyList<IntPtr> hs, FieldChange c)
    {
        try
        {
            if (hs.Count == 0) return "no control";
            var h = hs[0];
            switch (live.Kind)
            {
                case "edit":
                    if (WinFormsControls.TrySetText(h, c.After)) return null;
                    return SendText(h, WM_SETTEXT, 0, c.After, out _) ? null : "the edit box did not answer";
                case "check":
                {
                    int want = c.After == "on" ? 1 : c.After == "off" ? 0 : -1;
                    if (want < 0) return $"cannot set a check box to \"{c.After}\"";
                    // WinForms draws most check boxes itself (BS_OWNERDRAW): BM_GETCHECK does not tell, so set Checked; others get a real click
                    if (((uint)GetWindowLong(h, GWL_STYLE) & 0xF) == 0xB && WinFormsControls.TrySetChecked(h, want == 1)) return null;
                    if (Send(h, BM_GETCHECK) == want) return null;
                    Send(h, BM_CLICK);
                    if (Send(h, BM_GETCHECK) != want) Send(h, BM_SETCHECK, want);   // a plain (non-auto) check box does not toggle by itself
                    return null;
                }
                case "radio":
                {
                    var want = FillMatcher.Expected(c);
                    foreach (var r in hs)
                    {
                        if (!FillMatcher.ValueMatches("radio", want, Text(r))) continue;
                        if (((uint)GetWindowLong(r, GWL_STYLE) & 0xF) == 0xB && WinFormsControls.TrySetChecked(r, true)) return null;
                        if (Send(r, BM_GETCHECK) != 1) Send(r, BM_CLICK);
                        return null;
                    }
                    return $"no radio button \"{want}\" in the group";
                }
                case "combo":
                {
                    var style = (long)(uint)GetWindowLong(h, GWL_STYLE) & 3;
                    SendText(h, CB_FINDSTRINGEXACT, -1, c.After, out var idx);
                    if ((int)idx >= 0)
                    {
                        Send(h, CB_SETCURSEL, (int)idx);
                        Notify(h, CBN_SELCHANGE);
                        Notify(h, CBN_SELENDOK);
                        return null;
                    }
                    if (style == 3) return $"no entry \"{c.After}\" in the list";
                    if (!SendText(h, WM_SETTEXT, 0, c.After, out _)) return "the combo box did not answer";
                    Notify(h, CBN_EDITCHANGE);
                    return null;
                }
                case "list":
                {
                    SendText(h, LB_FINDSTRINGEXACT, -1, c.After, out var idx);
                    if ((int)idx < 0) return $"no entry \"{c.After}\" in the list";
                    Send(h, LB_SETCURSEL, (int)idx);
                    Notify(h, LBN_SELCHANGE);
                    return null;
                }
                case "slider":
                {
                    if (!int.TryParse(c.After, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos)) return $"\"{c.After}\" is not a slider position";
                    Send(h, TBM_SETPOSNOTIFY, 0, pos);   // comctl32 v6 notifies by itself
                    if (Send(h, TBM_GETPOS) != pos)
                    {
                        Send(h, TBM_SETPOS, 1, pos);
                        var parent = GetParent(h);
                        var vertical = ((uint)GetWindowLong(h, GWL_STYLE) & 0x2) != 0;
                        if (parent != IntPtr.Zero)
                            SendNum(parent, vertical ? WM_VSCROLL : WM_HSCROLL, (IntPtr)((pos << 16) | TB_THUMBPOSITION), h, SMTO_ABORTIFHUNG, SetTimeoutMs, out _);
                    }
                    return null;
                }
                default:
                    return $"cannot set a {live.Kind}";
            }
        }
        catch (Exception ex) { Log.Error("dialog fill (win32)", ex); return ex.Message; }
    }

    /// <summary>Presses a push button the way a click does, but posted: the dialog runs its own validation and closes from its own loop.</summary>
    internal static bool PostClick(IntPtr button) => Post(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Presses OK or Cancel of a Win32 dialog. False when there is no such button and the dialog is not a stock #32770.</summary>
    internal static bool CloseWin32(IntPtr dialog, Win32Scan scan, bool ok, out string how)
    {
        int i = FillMatcher.PickButton(scan.Buttons.Select(b => (b.Id, b.Text)).ToList(), ok);
        if (i >= 0) { how = "button"; return PostClick(scan.Buttons[i].H); }
        if (Native.ClassOf(dialog) == "#32770") { how = "WM_COMMAND"; return Post(dialog, WM_COMMAND, (IntPtr)(ok ? IDOK : IDCANCEL), IntPtr.Zero); }
        how = "";
        return false;
    }

    // ---- UI Automation (POOL THREAD ONLY) --------------------------------------------------------------------------------

    internal sealed record UiaOutcome(List<FieldFillResult> Results, bool Clicked, string? Note);

    /// <summary>Fills a dialog without child windows through UI Automation, verifies by reading it again and, when all went in, invokes OK
    /// (or Cancel when <paramref name="ok"/> is false and <paramref name="values"/> is empty). Bounded by about 2 s of setting plus one read.</summary>
    internal static UiaOutcome FillUia(IntPtr dialog, IReadOnlyList<FieldChange> values, bool ok)
    {
        var results = values.Select(v => FieldFillResult.Missing(v.Label)).ToList();
        try
        {
            var sw = Stopwatch.StartNew();
            var scan = ScanUia(dialog);
            var map = FillMatcher.Match(values, scan.Fields.Select(f => new LiveField(f.Key, f.Label, f.Kind)).ToList());
            for (int i = 0; i < values.Count; i++)
            {
                if (map[i] < 0) continue;
                if (sw.ElapsedMilliseconds > 2000) { results[i] = FieldFillResult.Fail(values[i].Label, "ran out of time (2 s)"); continue; }
                if (FillMatcher.IsNoOp(values[i])) { results[i] = FieldFillResult.Ok(values[i].Label, "nothing to choose"); continue; }
                var err = ApplyUia(scan.Elements[map[i]], scan.Fields[map[i]], values[i]);
                results[i] = err == null ? FieldFillResult.Ok(values[i].Label) : FieldFillResult.Fail(values[i].Label, err);
            }
            if (values.Count > 0)
            {
                var after = ScanUia(dialog);
                var map2 = FillMatcher.Match(values, after.Fields.Select(f => new LiveField(f.Key, f.Label, f.Kind)).ToList());
                for (int i = 0; i < values.Count; i++)
                    if (results[i].Status == FieldFillResult.Applied && !FillMatcher.IsNoOp(values[i]))
                        results[i] = Verify(values[i], map2[i] < 0 ? null : after.Fields[map2[i]].Value, results[i]);
            }
            if (results.Any(r => r.Status != FieldFillResult.Applied)) return new(results, false, null);
            var buttons = ScanUia(dialog).Buttons;
            int b = FillMatcher.PickButton(buttons.Select(x => (0, x.Name)).ToList(), ok);
            if (b < 0) return new(results, false, $"could not find the {(ok ? "OK" : "Cancel")} button");
            var el = buttons[b].El;
            // Invoke may not return until the dialog is gone: never wait for it
            _ = Task.Run(() => { try { ((InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch (Exception ex) { Log.Error("dialog fill (uia invoke)", ex); } });
            return new(results, true, null);
        }
        catch (Exception ex) { Log.Error("dialog fill (uia)", ex); return new(results, false, ex.Message); }
    }

    /// <summary>Marks the result failed when the live value is not the expected one.</summary>
    internal static FieldFillResult Verify(FieldChange c, string? current, FieldFillResult applied)
    {
        if (current == null) return FieldFillResult.Fail(c.Label, "the field is gone after setting it");
        return FillMatcher.Satisfied(c, current) ? applied : FieldFillResult.Fail(c.Label, $"value is \"{current}\" after setting");
    }

    static string? ApplyUia(AutomationElement el, DialogField live, FieldChange c)
    {
        try
        {
            switch (live.Kind)
            {
                case "edit":
                    ((ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern)).SetValue(c.After);
                    return null;
                case "check":
                {
                    var want = c.After == "on" ? ToggleState.On : c.After == "off" ? ToggleState.Off : (ToggleState?)null;
                    if (want == null) return $"cannot set a check box to \"{c.After}\"";
                    var tp = (TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern);
                    for (int i = 0; i < 3 && tp.Current.ToggleState != want; i++) tp.Toggle();
                    return null;
                }
                case "radio":
                    ((SelectionItemPattern)el.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                    return null;
                case "slider":
                    if (!double.TryParse(c.After, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return $"\"{c.After}\" is not a number";
                    ((RangeValuePattern)el.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(v);
                    return null;
                case "combo":
                {
                    if (el.TryGetCurrentPattern(SelectionPattern.Pattern, out _))
                    {
                        el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec);
                        try
                        {
                            (ec as ExpandCollapsePattern)?.Expand();
                            var items = el.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                            foreach (AutomationElement it in items)
                                if (FillMatcher.ValueMatches("combo", c.After, it.Current.Name))
                                {
                                    ((SelectionItemPattern)it.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                                    return null;
                                }
                        }
                        finally { try { (ec as ExpandCollapsePattern)?.Collapse(); } catch (Exception) { } }
                    }
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && !((ValuePattern)vp).Current.IsReadOnly) { ((ValuePattern)vp).SetValue(c.After); return null; }
                    return $"no entry \"{c.After}\" in the list";
                }
                default:
                    return $"cannot set a {live.Kind}";
            }
        }
        catch (Exception ex) { return ex.Message; }
    }
}
