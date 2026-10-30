using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>
/// RETRACERUN: does a recorded palette step. Finds the palette (by palette set title and palette name), sets the recorded values that the
/// palette does not show yet (so a retry does not fire a combo's change handler again), checks them, and presses the recorded button.
/// WinForms palettes through their child windows (a click is posted, so the palette runs it from its own message loop); WPF palettes in
/// process through their visual tree (<see cref="WpfPalette"/>). A docked palette with auto-hide is hidden until the mouse goes over its tab:
/// its controls are set and pressed without showing it. A press that runs code at once (WPF, hidden WinForms) is queued, never run inside the
/// replay's own timer tick. UI thread only.
/// </summary>
static class PaletteFill
{
    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>A button's text as recorded: an icon-only button (no text) is "(button)".</summary>
    public static string ButtonLabel(string? text) { var t = DialogFieldsCore.CleanLabel(text); return t.Length > 0 ? t : "(button)"; }

    static string? _pressFailure;

    /// <summary>A queued press that could not be done (the button got disabled meanwhile, an exception), once; the replay stops with it.</summary>
    public static string? TakePressFailure() { var f = _pressFailure; _pressFailure = null; return f; }

    /// <summary>Queues a press; <paramref name="press"/> returns false when it could not press.</summary>
    static void Later(string button, Func<bool> press) => System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
        () => { if (!press()) _pressFailure = $"could not press {button} (disabled, or it failed: see retrace.log)"; });

    /// <summary>Does the step; null when done (the button press may still be queued), else what stopped it (tried again, then a person).</summary>
    public static string? Run(Retrace.Core.Step s)
    {
        var page = FindPage(s);
        if (page == IntPtr.Zero) return $"the {s.DialogTitle} palette is not open";
        var hidden = !IsWindowVisible(page);
        var values = s.Values ?? new List<FieldChange>();
        var win32 = DialogReader.Scan(page, hidden);
        bool wpf = win32.Fields.Count == 0 && win32.Buttons.Count == 0 && WpfPalette.Host(page) != IntPtr.Zero;
        if (values.Count > 0)
        {
            var problem = wpf ? FillWpf(page, hidden, values, s.DialogTitle) : FillWin32(page, hidden, win32, values, s.DialogTitle);
            if (problem != null) return problem;
        }
        if (string.IsNullOrEmpty(s.Button)) return null;
        int want = Math.Max(1, s.ButtonIndex), seen = 0;
        if (wpf)
        {
            foreach (var (b, text) in WpfPalette.ScanPage(page, hidden).Buttons)
                if (string.Equals(text, s.Button, StringComparison.OrdinalIgnoreCase) && ++seen == want) { Later(s.Button, () => WpfPalette.Press(b)); return null; }
        }
        else
        {
            foreach (var (h, _, text) in DialogReader.Scan(page, hidden).Buttons)
                if (string.Equals(ButtonLabel(text), s.Button, StringComparison.OrdinalIgnoreCase) && ++seen == want)
                {
                    if (!hidden) return DialogFill.PostClick(h) ? null : $"could not press {s.Button}";
                    if (WinFormsControls.KindOf(h) != "button") return $"could not press {s.Button} (the palette is hidden)";
                    Later(s.Button, () => WinFormsControls.TryClick(h));
                    return null;
                }
        }
        return $"no button \"{s.Button}\" in the {s.DialogTitle} palette (yet)";
    }

    static string? FillWin32(IntPtr page, bool hidden, DialogReader.Win32Scan scan, List<FieldChange> values, string? title)
    {
        var map = FillMatcher.Match(values, scan.Fields.Select(f => new LiveField(f.Key, f.Label, f.Kind)).ToList());
        for (int i = 0; i < values.Count; i++)
        {
            if (FillMatcher.IsNoOp(values[i])) continue;
            if (map[i] < 0) return $"no field \"{values[i].Label}\" in the {title} palette";
            if (FillMatcher.Satisfied(values[i], scan.Fields[map[i]].Value)) continue;   // already shows it: setting it again would fire its handlers again
            if (DialogFill.ApplyWin32(scan.Fields[map[i]], scan.Handles[map[i]], values[i]) is { } err) return $"{values[i].Label}: {err}";
        }
        var after = DialogReader.Scan(page, hidden);
        map = FillMatcher.Match(values, after.Fields.Select(f => new LiveField(f.Key, f.Label, f.Kind)).ToList());
        for (int i = 0; i < values.Count; i++)
        {
            if (FillMatcher.IsNoOp(values[i])) continue;
            var r = DialogFill.Verify(values[i], map[i] < 0 ? null : after.Fields[map[i]].Value, FieldFillResult.Ok(values[i].Label));
            if (r.Status != FieldFillResult.Applied) return $"{values[i].Label}: {r.Message}";
        }
        return null;
    }

    static string? FillWpf(IntPtr page, bool hidden, List<FieldChange> values, string? title)
    {
        var scan = WpfPalette.ScanPage(page, hidden);
        var map = FillMatcher.Match(values, scan.Fields.Select(f => new LiveField(f.Key, f.Label, f.Kind)).ToList());
        for (int i = 0; i < values.Count; i++)
        {
            if (FillMatcher.IsNoOp(values[i])) continue;
            if (map[i] < 0) return $"no field \"{values[i].Label}\" in the {title} palette";
            if (FillMatcher.Satisfied(values[i], scan.Fields[map[i]].Value)) continue;
            if (WpfPalette.Apply(scan.Elements[map[i]], scan.Fields[map[i]], values[i]) is { } err) return $"{values[i].Label}: {err}";
        }
        var after = WpfPalette.ScanPage(page, hidden);
        map = FillMatcher.Match(values, after.Fields.Select(f => new LiveField(f.Key, f.Label, f.Kind)).ToList());
        for (int i = 0; i < values.Count; i++)
        {
            if (FillMatcher.IsNoOp(values[i])) continue;
            var r = DialogFill.Verify(values[i], map[i] < 0 ? null : after.Fields[map[i]].Value, FieldFillResult.Ok(values[i].Label));
            if (r.Status != FieldFillResult.Applied) return $"{values[i].Label}: {r.Message}";
        }
        return null;
    }

    /// <summary>Tdoc: the controls of a WinForms palette that the step sets, and its button, for the numbered boxes of the palette's picture. Nothing
    /// for a WPF or native palette, or one that is not open. UI thread.</summary>
    public static List<DialogBox> TdocBoxes(Retrace.Core.Step s)
    {
        var boxes = new List<DialogBox>();
        try
        {
            var page = FindPage(s);
            if (page == IntPtr.Zero || s.Native != null) return boxes;
            var hidden = !IsWindowVisible(page);
            var scan = DialogReader.Scan(page, hidden);
            if (scan.Fields.Count == 0 && scan.Buttons.Count == 0) return boxes;   // WPF: its controls have no windows
            var values = s.Values ?? new List<FieldChange>();
            var map = FillMatcher.Match(values, scan.Fields.Select(f => new LiveField(f.Key, f.Label, f.Kind)).ToList());
            for (int i = 0; i < values.Count; i++)
                if (map[i] >= 0 && !FillMatcher.IsNoOp(values[i]) && scan.Handles[map[i]].Count > 0)
                    boxes.Add(new DialogBox(scan.Handles[map[i]][0], default, TdocPlan.FieldCallout(values[i])));
            if (!string.IsNullOrEmpty(s.Button))
            {
                int want = Math.Max(1, s.ButtonIndex), seen = 0;
                foreach (var (h, _, text) in scan.Buttons)
                    if (string.Equals(ButtonLabel(text), s.Button, StringComparison.OrdinalIgnoreCase) && ++seen == want)
                    {
                        boxes.Add(new DialogBox(h, default, "Click " + s.Button));
                        break;
                    }
            }
        }
        catch (Exception ex) { Log.Error("tdoc palette boxes", ex); }
        return boxes;
    }

    /// <summary>The window of the step's palette, or zero.</summary>
    public static IntPtr FindPage(Retrace.Core.Step s) => Find(s.Panel, s.DialogTitle);

    /// <summary>The palette <paramref name="palette"/> (shown, or auto-hidden) of the palette set titled <paramref name="set"/> in this process, or zero.</summary>
    static IntPtr Find(string? set, string? palette)
    {
        var found = IntPtr.Zero;
        uint me = (uint)Environment.ProcessId;
        EnumWindows((top, _) =>
        {
            GetWindowThreadProcessId(top, out var pid);
            if (pid != me) return true;
            EnumChildWindows(top, (h, _) =>
            {
                if (Native.TitleOf(h) != palette || !DialogReader.ClassOf(h).StartsWith("AfxWnd", StringComparison.Ordinal)) return true;
                if (!IsPage(h, set)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>true if <paramref name="h"/> is a palette window of the set titled <paramref name="set"/> (palette, set, control bar).</summary>
    static bool IsPage(IntPtr h, string? set)
    {
        var p = DialogReader.GetParent(h);
        return Native.TitleOf(p) == set && DialogReader.ClassOf(p).StartsWith("AfxWnd", StringComparison.Ordinal)
               && DialogReader.ClassOf(DialogReader.GetParent(p)).StartsWith("AfxControlBar", StringComparison.Ordinal);
    }
}
