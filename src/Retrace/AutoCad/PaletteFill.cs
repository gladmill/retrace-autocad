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
    public static string? TakePressFailure()
    {
        var f = _pressFailure; _pressFailure = null;
        if (f == null && _lateMenu?.LateProblem is { } late) { _lateMenu = null; return late; }   // a context menu item's click is queued: its problem shows up later
        return f;
    }

    static MenuReplay? _lateMenu, _activeMenu;
    static bool _menuFinal;

    /// <summary>true once after a context menu replay failed for good: the step is not retried, the reason goes to a person (the replay asks, <see cref="Replayer"/>).</summary>
    public static bool TakeMenuFinal() { var f = _menuFinal; _menuFinal = false; return f; }

    /// <summary>A replay starts: nothing of an earlier replay's context menus is left.</summary>
    public static void Reset() { _activeMenu?.CloseAll(); _activeMenu = null; _lateMenu = null; _menuFinal = false; }

    /// <summary>The step is given up or replaced, or the replay ends: a context menu it opened is closed (a menu is never left open).</summary>
    public static void Abandon() { _activeMenu?.CloseAll(); _activeMenu = null; }

    /// <summary>Queues a press; <paramref name="press"/> returns false when it could not press.</summary>
    static void Later(string button, Func<bool> press) => System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
        () => { if (!press()) _pressFailure = $"could not press {button} (disabled, or it failed: see retrace.log)"; });

    /// <summary>Does the step; null when done (the button press may still be queued), else what stopped it (tried again, then a person).</summary>
    public static string? Run(Retrace.Core.Step s, FillRun run)
    {
        var page = FindPage(s);
        if (page == IntPtr.Zero) return $"the {s.DialogTitle} palette is not open";
        var hidden = !IsWindowVisible(page);
        var values = s.Values ?? new List<FieldChange>();
        var win32 = DialogReader.Scan(page, hidden, readTicks: false);
        bool wpf = win32.Fields.Count == 0 && win32.Buttons.Count == 0 && WpfPalette.Host(page) != IntPtr.Zero;
        if (values.Count > 0)
        {
            var problem = wpf ? FillWpf(page, hidden, values, s.DialogTitle) : FillWin32(page, hidden, win32, values, s.DialogTitle, run);
            if (problem != null) return problem;
        }
        if (s.Menu != null)
        {
            // the step ends with a context menu choice instead of a button (see MenuReplay); tried again on every retry until it is done
            run.Menu ??= new MenuReplay(s.Menu, page, hidden);
            _activeMenu = run.Menu;
            switch (run.Menu.Step(DateTime.Now, wpf ? null : win32))
            {
                case MenuReplay.Status.Done: _lateMenu = run.Menu; run.Menu = null; _activeMenu = null; return null;
                case MenuReplay.Status.Failed: var why = run.Menu.Message; run.Menu = null; _activeMenu = null; _menuFinal = true; return why;   // its own reason, not retried
                default: return run.Menu.Message ?? "waiting for the context menu";
            }
        }
        if (string.IsNullOrEmpty(s.Button)) return null;
        if (s.ClickKind == Retrace.Core.DialogClickKinds.DoubleClick)
        {
            // a double-click on a tree item: the tree is the live field of the recorded tree; the item is selected and the double-click sent from the dispatcher after this tick
            if (wpf) return "a double-click on a tree item needs a WinForms palette";
                        var at = FillMatcher.Match(new[] { new FieldChange(s.ClickTree, s.ClickTreeLabel, "tree", "", "") }, win32.Fields.Select(LiveField.Of).ToList())[0];
            if (at < 0 || win32.Handles[at].Count == 0) return $"no tree \"{s.ClickTreeLabel}\" in the {s.DialogTitle} palette (yet)";
            return TreeControl.DoubleClick(win32.Handles[at][0], s.ClickPath, a => DialogFill.Later("palette tree double-click", a)) is { } why ? $"could not double-click {s.ClickPath}: {why}" : null;
        }
        int want = Math.Max(1, s.ButtonIndex), seen = 0;
        if (wpf)
        {
            // the one finder of a recorded WPF button (a recording from before the access-key underscore was dropped compares equal)
            var wb = WpfPalette.ScanPage(page, hidden).Buttons;
            int at = Retrace.Core.WpfPresses.Find(wb.Select(x => x.Text).ToList(), s.Button, want);
            if (at >= 0) { var wbtn = wb[at].Button; Later(s.Button, () => WpfPalette.Press(wbtn)); return null; }
        }
        else
        {
            foreach (var (h, _, text) in DialogReader.Scan(page, hidden, readTicks: false).Buttons)
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

    /// <summary>What one palette step has done so far, kept by the replay for the step's retries (a value leaves the screen with its tab, so the values already set and read back are remembered here).</summary>
    public sealed class FillRun { internal readonly HashSet<int> Done = new(); internal DialogFill.TickSession? Ticks; internal MenuReplay? Menu; }

    static string? FillWin32(IntPtr page, bool hidden, DialogReader.Win32Scan scan, List<FieldChange> values, string? title, FillRun run)
    {
        var map = FillMatcher.Match(values, scan.Fields.Select(LiveField.Of).ToList());
        // the one planner (Core/TabFill, as for a dialog); a palette has no timeout of its own: the replay retries the step for a while, so a value that waits is a problem to report
        var items = values.Select((v, i) => new FillItem(map[i] < 0 && FillMatcher.IsNoOp(v) ? "" : FillMatcher.TabOfChange(v), map[i] >= 0, !run.Done.Contains(i) && !FillMatcher.IsNoOp(v))).ToList();
        var plan = TabFill.Next(items, scan.Tabs, expired: false);
        run.Ticks ??= new DialogFill.TickSession(values);
        foreach (var i in TreeChecks.OrderForApply(values, plan.Set))
        {
            if (FillMatcher.Satisfied(values[i], DialogFill.LiveValue(scan, map[i], values[i]))) continue;   // already shows it: setting it again would fire its handlers again
            if (DialogFill.ApplyWin32(scan.Fields[map[i]], scan.Handles[map[i]], values[i], run.Ticks) is { } err) return $"{values[i].Label}: {err}";
        }
        if (plan.Set.Count > 0)
        {
            var after = DialogReader.Scan(page, hidden, readTicks: false);
            var map2 = FillMatcher.Match(values, after.Fields.Select(LiveField.Of).ToList());
            foreach (var i in plan.Set)
            {
                var r = DialogFill.Verify(values[i], map2[i] < 0 ? null : DialogFill.LiveValue(after, map2[i], values[i]), FieldFillResult.Ok(values[i].Label));
                if (r.Status != FieldFillResult.Applied) return $"{values[i].Label}: {r.Message}";
                run.Done.Add(i);
            }
            scan = after;
        }
        if (plan.WaitFor >= 0) return $"no field \"{values[plan.WaitFor].Label}\" in the {title} palette";
        if (plan.Switch == null) return null;
        // the target is worked out again on the rescan, by tab path: the controls and indexes of the first scan may be gone
        var next = TabPlan.Next(plan.SwitchPath, scan.Tabs);
        if (next.Action == TabAction.Missing) return next.Problem;
        if (next.Action == TabAction.Done) return $"the {plan.SwitchPath} tab shows now";   // not done: the retry sets the rest
        if (TabControl32.Select(scan.TabHandles[next.Tab], next.Item) is { } why) return $"could not open the {next.Text} tab: {why}";
        return $"opening the {next.Text} tab";   // the retry (half a second later) sets the values on it
    }

    static string? FillWpf(IntPtr page, bool hidden, List<FieldChange> values, string? title)
    {
        var scan = WpfPalette.ScanPage(page, hidden);
        var map = FillMatcher.Match(values, scan.Fields.Select(LiveField.Of).ToList());
        for (int i = 0; i < values.Count; i++)
        {
            if (FillMatcher.IsNoOp(values[i])) continue;
            if (map[i] < 0) return $"no field \"{values[i].Label}\" in the {title} palette";
            if (FillMatcher.Satisfied(values[i], scan.Fields[map[i]].Value)) continue;
            if (WpfPalette.Apply(scan.Elements[map[i]], scan.Fields[map[i]], values[i]) is { } err) return $"{values[i].Label}: {err}";
        }
        var after = WpfPalette.ScanPage(page, hidden);
        map = FillMatcher.Match(values, after.Fields.Select(LiveField.Of).ToList());
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
            var scan = DialogReader.Scan(page, hidden, readTicks: false);
            if (scan.Fields.Count == 0 && scan.Buttons.Count == 0) return boxes;   // WPF: its controls have no windows
            var values = s.Values ?? new List<FieldChange>();
            var map = FillMatcher.Match(values, scan.Fields.Select(LiveField.Of).ToList());
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
