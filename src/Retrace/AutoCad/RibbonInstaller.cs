using System.Windows;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>Creates the tab once the ribbon exists and again if AutoCAD rebuilds the ribbon (workspace switch). Checked on Idle, at most once a second.</summary>
public static class RibbonInstaller
{
    static bool _hooked;
    static long _lastCheck;
    static readonly RestoreFlag _maybeMissing = new();        // set by the event handlers (they do no work), taken by the next Idle
    static readonly RestoreGuard _guard = new();
    static readonly DistinctOnce _failureLog = new();
    static RibbonControl? _watched;                            // the ribbon whose tab collection we listen to
    static bool _busy;                                         // our own changes to the tab collection raise no flag
    static string? _lastFailure;
    static string? _pending;                                   // the reason of a check that is waiting (for the ribbon to settle, or for a capture to end)
    static long _lastChange;                                   // when the tab collection last changed (set by the handler)
    static long _activateUntil;                                // RETRACERIBBON asked for the tab to be active; honoured by the next repair until this tick
    static readonly LastTabTracker _lastTab = new();           // the tab that was active before the Retrace tab (setting recordStartTab = last)
    static bool _everBuilt;                                    // the tab has been built once in this session: a later build is a repair


    public static void Install()
    {
        if (_hooked) return;
        _hooked = true;
        AcApp.Idle += OnIdle;
        try
        {
            AcApp.SystemVariableChanged += OnSysVar;
        }
        catch (System.Exception ex) { Log.Error("filedia hooks", ex); }
        try { ComponentManager.ItemInitialized += OnItemInitialized; }
        catch (System.Exception ex) { Log.Error("ribbon hooks", ex); }
    }

    public static void Uninstall()
    {
        if (!_hooked) return;
        _hooked = false;
        AcApp.Idle -= OnIdle;
        try
        {
            AcApp.SystemVariableChanged -= OnSysVar;
        }
        catch (System.Exception) { }
        try { ComponentManager.ItemInitialized -= OnItemInitialized; } catch (System.Exception) { }
        Unwatch();
        try
        {
            var rc = ComponentManager.Ribbon;
            var tab = rc?.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
            if (tab != null) rc!.Tabs.Remove(tab);
        }
        catch (System.Exception) { }
    }

    // Handlers only mark "the tab may be missing" and note when the ribbon last changed; the work is done by the next Idle tick (AGENTS.md: no work in AutoCAD event handlers).
    static void OnTabsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_busy) return;
        _lastChange = Environment.TickCount64;
        if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove or System.Collections.Specialized.NotifyCollectionChangedAction.Replace or System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            _maybeMissing.Set(RestoreReason.CollectionChanged);
    }

    // Only remembers an id (no work in AutoCAD event handlers): the tab that is active, unless it is the Retrace tab.
    static void OnRibbonProperty(object? s, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == "ActiveTab") NoteActive(s as RibbonControl); }

    static void NoteActive(RibbonControl? rc)
    {
        // not while the ribbon capture switches tabs itself or a replay clicks through them: those are not the person's tabs
        if (RibbonCapture.Running || Replayer.Current != null) return;
        try { _lastTab.Note((rc ?? _watched)?.ActiveTab?.Id, RetraceTab.TabId); } catch (System.Exception) { }
    }

    static void OnItemInitialized(object? s, EventArgs e) { if (!_busy) _maybeMissing.Set(RestoreReason.ItemInitialized); }   // fires for every ribbon item: only asks for a check

    static void Unwatch()
    {
        var old = _watched;
        _watched = null;
        if (old == null) return;
        try { (old.Tabs as System.Collections.Specialized.INotifyCollectionChanged)!.CollectionChanged -= OnTabsChanged; } catch (System.Exception) { }
        try { old.PropertyChanged -= OnRibbonProperty; } catch (System.Exception) { }
    }

    /// <summary>Listens to the tab collection of <paramref name="rc"/>, and stops listening to the one before. True when this is a different ribbon than the one watched before.</summary>
    static bool Watch(RibbonControl rc)
    {
        if (ReferenceEquals(_watched, rc)) return false;
        var hadOne = _watched != null;
        Unwatch();
        try { if (rc.Tabs is System.Collections.Specialized.INotifyCollectionChanged n) n.CollectionChanged += OnTabsChanged; _watched = rc; rc.PropertyChanged += OnRibbonProperty; NoteActive(rc); }
        catch (System.Exception ex) { Log.Error("ribbon watch", ex); }
        return hadOne;
    }

    static void OnIdle(object? s, EventArgs e)
    {
        try
        {
            var now = Environment.TickCount64;
            if (_startPending is { } st && Recorder.Instance.IsRecording) { _startPending = null; DoRecordStart(st); }   // setting recordStartTab, asked for by RETRACERECORD
            else _startPending = null;
            if (_watched != null) NoteActive(_watched);                     // the active tab, read on every Idle (a property read): a quick tab visit is not missed
            if (_fileDiaDirty) { _fileDiaDirty = false; RefreshFileDia(); }   // a FILEDIA change: show it at once
            if (_activateUntil != 0 && now > _activateUntil) _activateUntil = 0;   // RETRACERIBBON's "make it active" request expires
            var taken = _maybeMissing.Take();                                // an event said the tab may be gone: look now, not at the next second
            if (taken != null) _pending ??= taken;
            var due = now - _lastCheck >= 1000;
            if (_pending == null && !due) return;
            if (due) _lastCheck = now;
            if (RibbonCapture.Running) return;                               // RETRACECAPTURERIBBON switches workspaces itself: repair after it, uncounted
            var rc = ComponentManager.Ribbon;
            if (rc == null) return;                                          // no ribbon yet: the pending request stays, the next second looks again
            if (Watch(rc)) _pending ??= RestoreReason.RibbonReplaced;
            if (rc.Tabs.Any(t => t.Id == RetraceTab.TabId))
            {
                _pending = null;
                if (due) { RefreshFromSettings(false); RefreshFileDia(); }   // settings.json changed elsewhere: follow it; FILEDIA: a cheap read, catches any missed event
                return;
            }
            // The tab is not in the collection. Only that is repaired by itself (a hidden tab or a closed ribbon is the user's choice).
            if (RibbonRestoreRules.Auto(Look(rc)) == RibbonAction.None) { _pending = null; return; }
            if (_everBuilt && !RestoreSettle.Ready(now, _lastChange)) return;   // AutoCAD is still changing the ribbon: wait until it is quiet (one rebuild = one repair)
            var reason = _pending ?? RestoreReason.Periodic;
            _pending = null;
            if (_everBuilt)   // the first build at start-up is no repair and is not counted
                switch (_guard.Try(now, reason == RestoreReason.CollectionChanged))
                {
                    case RestoreGuard.Gate.Stopped: return;
                    case RestoreGuard.Gate.JustStopped:
                        Log.Info(RestoreLog.Stopped(_guard.Trips, _guard.PauseMs, _lastFailure));
                        return;
                }
            _lastCheck = now;
            if (TryBuild(rc, out _))
            {
                if (_everBuilt) Log.Info("ribbon: Retrace tab restored: " + reason);
                _everBuilt = true;
                if (_activateUntil != 0)   // RETRACERIBBON asked for the tab to be the active one and the ribbon dropped it again
                {
                    _activateUntil = 0;
                    var tab = rc.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
                    if (tab != null) { _busy = true; try { tab.IsActive = true; } finally { _busy = false; } }
                }
                RefreshFromSettings(true);
                RefreshFileDia();
            }
        }
        catch (System.Exception ex) { Log.Error("ribbon create", ex); }
    }

    /// <summary>Builds the tab, adds it to <paramref name="rc"/>, shows the checkpoint key and places it. On a failure the text is kept for the stop line and RETRACERIBBON, and logged once per distinct text.</summary>
    static bool TryBuild(RibbonControl rc, out string? failure)
    {
        failure = null;
        _busy = true;
        try
        {
            var tab = RetraceTab.Build();
            rc.Tabs.Add(tab);
            ShowCheckpointKey(tab);
            Place(rc, tab, RibbonTabPosition.ReadSettingFromDisk());
            _lastFailure = null;
            _failureLog.IsNew(null);
            return true;
        }
        catch (System.Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            _lastFailure = failure;
            if (_failureLog.IsNew(failure)) Log.Error("ribbon create", ex);
            return false;
        }
        finally { _busy = false; }
    }

    /// <summary>The ribbon's own state, not the AutoCAD window's: RIBBONSTATE (0 = closed, also when the window is minimized the ribbon is open).</summary>
    static bool RibbonOpen(RibbonControl rc)
    {
        try { return Convert.ToInt32(AcApp.GetSystemVariable("RIBBONSTATE")) != 0; }
        catch (System.Exception) { }
        try { return rc.IsVisible; } catch (System.Exception) { return true; }
    }

    static RibbonFinding Look(RibbonControl? rc)
    {
        if (rc == null) return RibbonFinding.NoRibbon;
        var mine = rc.Tabs.Where(t => t.Id == RetraceTab.TabId).ToList();
        return RibbonRestoreRules.Find(true, RibbonOpen(rc), mine.Count, mine.All(t => t.IsVisible));
    }

    /// <summary>
    /// RETRACERIBBON: looks at the ribbon, repairs what is wrong (opens a closed ribbon, removes stale or hidden Retrace tabs, builds a fresh one,
    /// places it, makes it active) and returns one or two plain lines for the command line. Safe to run again and again. Never throws.
    /// </summary>
    public static string RestoreFromCommand()
    {
        try
        {
            var earlierFailure = _lastFailure;
            _guard.Reset();
            _maybeMissing.Take();
            _pending = null;
            var rc = ComponentManager.Ribbon;
            var finding = Look(rc);
            var action = RibbonRestoreRules.Manual(finding);
            var opened = false;
            if (action.HasFlag(RibbonAction.OpenRibbon))
            {
                try
                {
                    AcApp.DocumentManager.MdiActiveDocument?.Editor.Command("_.RIBBON");
                    opened = true;
                }
                catch (System.Exception ex) { Log.Error("RETRACERIBBON open", ex); }
                rc = ComponentManager.Ribbon;
            }
            if (rc == null) return "The ribbon is not there and AutoCAD did not open it; type RIBBON, then RETRACERIBBON.";
            Watch(rc);
            string? failure = null;
            _busy = true;
            try
            {
                if (action.HasFlag(RibbonAction.RemoveStale))
                    foreach (var t in rc.Tabs.Where(t => t.Id == RetraceTab.TabId).ToList()) rc.Tabs.Remove(t);
            }
            finally { _busy = false; }
            if (action.HasFlag(RibbonAction.Build) && !TryBuild(rc, out failure)) return RibbonRestoreRules.Message(finding, opened, failure);
            _everBuilt = true;
            var tab = rc.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
            if (tab != null)
            {
                _busy = true;
                try { tab.IsVisible = true; tab.IsActive = true; }
                finally { _busy = false; }
                RefreshFromSettings(true);
                RefreshFileDia();
            }
            // a ribbon that was just opened may load its workspace tabs later and drop the tab again: the automatic repair then makes it the active one
            _activateUntil = Environment.TickCount64 + 8000;
            Log.Info("ribbon: RETRACERIBBON: " + finding + (action == RibbonAction.None ? ", nothing to repair" : ", repaired"));
            var msg = RibbonRestoreRules.Message(finding, opened, null);
            if (earlierFailure != null) msg += "\nThe automatic repair had failed before: " + earlierFailure;
            return msg;
        }
        catch (System.Exception ex)
        {
            Log.Error("RETRACERIBBON", ex);
            return "RETRACERIBBON failed: " + ex.Message;
        }
    }

    /// <summary>Moves the Retrace tab to where settings.json says (key `ribbonTabPosition`). Called after Settings are saved. Never throws.</summary>
    public static void ApplyPosition(string? setting = null)
    {
        try
        {
            var rc = ComponentManager.Ribbon;
            var tab = rc?.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
            if (rc != null && tab != null) Place(rc, tab, setting ?? RibbonTabPosition.ReadSettingFromDisk());
        }
        catch (System.Exception ex) { Log.Error("ribbon move", ex); }
    }

    /// <summary>
    /// Shows the checkpoint shortcut on the Checkpoint button: a second line under the label and a sentence in the tooltip. Done here after the
    /// tab is built, so RetraceTab.Build keeps the plain literal shape the code-ribbon scanner reads. Called when the tab is made and when
    /// Settings are saved. Never throws.
    /// </summary>
    public static void ShowCheckpointKey(RibbonTab? tab = null, RetraceSettings? settings = null)
    {
        try
        {
            tab ??= ComponentManager.Ribbon?.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
            if (tab == null) return;
            var key = (settings ?? RetraceSettings.Load()).CheckpointChord(out _)?.Format();
            foreach (var panel in tab.Panels)
                foreach (var item in panel.Source.Items)
                {
                    if (item is not RibbonButton b || b.Id != "RETRACEMARK") continue;
                    const string marker = " Shortcut: ";
                    b.Text = key == null ? "Checkpoint" : "Checkpoint\n" + key;
                    var tip = (b.Description ?? "");
                    var at = tip.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0) tip = tip.Substring(0, at);
                    if (key != null) tip += marker + key + " (change it in Settings).";
                    b.Description = tip;
                    b.ToolTip = tip;
                }
        }
        catch (System.Exception ex) { Log.Error("ribbon checkpoint key", ex); }
    }

    static volatile bool _fileDiaDirty;

    // Handlers do no work: they only mark the indicator stale; the next Idle shows the value (AGENTS.md: no work in AutoCAD event handlers).
    static void OnSysVar(object? s, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
    {
        try
        {
            if (string.Equals(e.Name, "FILEDIA", StringComparison.OrdinalIgnoreCase)) _fileDiaDirty = true;
            else if (string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase)) { _lastChange = Environment.TickCount64; _maybeMissing.Set(RestoreReason.WorkspaceChanged); }
        }
        catch (System.Exception) { }
    }

    /// <summary>Makes the FILEDIA indicator show the variable's value now (also after a workspace change, which rebuilds the tab). Does nothing when the shown value is current. Never throws.</summary>
    public static void RefreshFileDia()
    {
        try
        {
            var cur = Ask.FileDia();
            var tab = ComponentManager.Ribbon?.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
            if (tab == null) return;
            foreach (var panel in tab.Panels)
                foreach (var item in panel.Source.Items)
                {
                    if (item is not RibbonToggleButton b || b.Id != RetraceTab.FileDiaId) continue;
                    if (b.IsChecked == true) b.IsChecked = false;   // a toggle flips itself on a click; this is an indicator, never pressed
                    if (cur is not { } v || b.Text == RibbonFileDia.Text(v)) return;
                    b.Image = RibbonIcons.Make(RibbonFileDia.Icon(v), 16);
                    b.LargeImage = RibbonIcons.Make(RibbonFileDia.Icon(v), 32);
                    b.Text = RibbonFileDia.Text(v);
                    var tip = RibbonFileDia.Tip(v);
                    b.ToolTip = tip;
                    b.Description = tip;
                }
        }
        catch (System.Exception ex) { Log.Error("ribbon filedia", ex); }
    }

    static DateTime _settingsSeen = DateTime.MinValue;

    /// <summary>The promptStyle the Prompts toggle shows now (the default until settings.json has been read).</summary>
    public static string ShownPromptStyle { get; private set; } = PromptStyles.CommandLine;

    /// <summary>Makes the ribbon show what settings.json says: the Prompts toggle (pressed = window), the checkpoint shortcut on the Checkpoint button and the
    /// tab's place. With <paramref name="force"/> always, else only when the file was written since the last look (so it follows the Settings window,
    /// -RETRACESETTINGS and a hand edit). A file that cannot be read changes nothing and is not marked as seen, so the next look tries again. Never throws.</summary>
    public static void RefreshFromSettings(bool force)
    {
        try
        {
            var file = Paths.SettingsFile();
            var written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (!force && written == _settingsSeen) return;
            if (!RetraceSettings.TryLoad(out var s)) return;
            ShowPromptStyle(s.PromptStyle);
            ShowCheckpointKey(null, s);
            ApplyPosition(s.RibbonTabPosition);
            _settingsSeen = written;
        }
        catch (System.Exception ex) { Log.Error("ribbon refresh", ex); }
    }

    /// <summary>Shows <paramref name="style"/> on the Prompts toggle: pressed = window, text and tooltip. Never throws.</summary>
    public static void ShowPromptStyle(string? style)
    {
        try
        {
            var tab = ComponentManager.Ribbon?.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
            if (tab == null) return;
            ShownPromptStyle = RibbonPrompts.IsWindow(style) ? PromptStyles.Ui : PromptStyles.CommandLine;
            foreach (var panel in tab.Panels)
                foreach (var item in panel.Source.Items)
                {
                    if (item is not RibbonToggleButton b || b.Id != RetraceTab.PromptToggleId) continue;
                    b.IsChecked = RibbonPrompts.IsWindow(style);
                    b.Text = RibbonPrompts.ToggleText(style);
                    var tip = RibbonPrompts.ToggleTip(style);
                    b.ToolTip = tip;
                    b.Description = tip;
                }
        }
        catch (System.Exception ex) { Log.Error("ribbon prompt style", ex); }
    }

    /// <summary>What a recording start needs to know about the ribbon, read when the command begins (before the name prompt): was the Retrace tab the active one, and which tab was active before it.</summary>
    internal readonly record struct RecordStartState(bool OnOwnTab, string? LastTabId);

    internal static RecordStartState CaptureForRecordStart()
    {
        try
        {
            var rc = ComponentManager.Ribbon;
            if (rc == null) return new RecordStartState(false, null);
            NoteActive(rc);
            return new RecordStartState(string.Equals(rc.ActiveTab?.Id, RetraceTab.TabId, StringComparison.OrdinalIgnoreCase), _lastTab.Id);
        }
        catch (System.Exception ex) { Log.Error("record start tab capture", ex); return new RecordStartState(false, null); }
    }

    /// <summary>Setting recordStartTab: once a recording has really started, show the Home tab, the tab that was active before the Retrace tab, or stay. It switches only when the Retrace tab was the active tab when the
    /// command began (a typed RETRACERECORD with the Retrace tab active cannot be told from a click on Record, and does switch; a typed command or the Quick Access Toolbar with another tab active does not) and is still the active tab
    /// at the next Idle, and not from a script or LISP, not during a replay, not with the ribbon closed or minimized. The switch is told to the recorder first, so it is no step and takes no picture. Never throws.</summary>
    internal static void ApplyRecordStart(RecordStartState state)
    {
        try
        {
            var mode = RecordStartTab.Normalize(RetraceSettings.Load().RecordStartTab);
            if (mode == RecordStartTab.Stay) return;
            if (!state.OnOwnTab) { Log.Info("record start tab: the Retrace tab was not the active tab, nothing switched"); return; }
            if (Replayer.Current != null || Ask.ScriptKnown()) { Log.Info("record start tab: a replay or script runs, nothing switched"); return; }
            _startPending = state;   // the switch itself waits for the next Idle, when RETRACERECORD has ended and the ribbon is at rest
        }
        catch (System.Exception ex) { Log.Error("record start tab", ex); }
    }

    static RecordStartState? _startPending;

    static void DoRecordStart(RecordStartState state)
    {
        RibbonControl? rc = null;
        try
        {
            var mode = RecordStartTab.Normalize(RetraceSettings.Load().RecordStartTab);
            rc = ComponentManager.Ribbon;
            if (rc == null || !RibbonOpen(rc)) { Log.Info("record start tab: no ribbon or it is closed, nothing switched"); return; }
            if (RibbonMinimized(rc, out var minimizedMode)) { Log.Info($"record start tab: the ribbon is minimized ({minimizedMode}), nothing switched"); return; }
            var active = rc.ActiveTab?.Id;
            if (!RecordStartTab.StillOnOwnTab(active, RetraceTab.TabId)) { Log.Info($"record start tab: the active tab is {active ?? "none"}, no longer the Retrace tab: the person chose, nothing switched"); return; }
            var facts = rc.Tabs.Select(t => new TabFact(t.Id ?? "", t.Title ?? "", t.IsVisible, t.IsContextualTab)).ToList();
            var choice = RecordStartTab.Choose(mode, facts, RetraceTab.TabId, state.LastTabId, state.OnOwnTab);
            Log.Info($"record start tab ({mode}): {choice.Rule}" + (choice.TabId == null ? "" : " -> " + choice.TabId));
            if (choice.TabId == null) return;
            var target = rc.Tabs.FirstOrDefault(t => string.Equals(t.Id, choice.TabId, StringComparison.OrdinalIgnoreCase));
            if (target == null || ReferenceEquals(rc.ActiveTab, target)) return;
            Recorder.Instance.NoteTabSwitch(target.Id);   // before the switch: the recorder then reads the new active tab as already known, not as a step of the test
            _busy = true;
            try { rc.ActiveTab = target; }
            finally { _busy = false; }
            if (!ReferenceEquals(rc.ActiveTab, target)) { try { target.IsActive = true; } catch (System.Exception ex) { Log.Error("record start tab set", ex); } }
            Log.Info("record start tab: active tab is now " + (rc.ActiveTab?.Id ?? "none"));
        }
        catch (System.Exception ex) { Log.Error("record start tab", ex); }
        finally
        {
            // whatever happened, the recorder holds the tab that is really active: a switch that failed must not make it swallow the person's next click on the tab it was told about
            try { if (rc != null) Recorder.Instance.NoteTabSwitch(RecordStartTab.RecorderSyncId(rc.ActiveTab?.Id)); } catch (System.Exception) { }
        }
    }

    /// <summary>True when the ribbon is minimized: RibbonControl.CurrentAutoHideMode (Autodesk.Internal.Windows.RibbonAutoHideMode: None, HidePanelBar, HidePanel, CollapsePanels) is anything but None, i.e.
    /// minimized to tabs, to panel titles or to panel buttons. The property is read by name (it is not in the documented surface); a ribbon whose mode cannot be read counts as not minimized.</summary>
    static bool RibbonMinimized(RibbonControl rc, out string mode)
    {
        mode = "";
        try
        {
            var p = rc.GetType().GetProperty("CurrentAutoHideMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (p == null || p.GetIndexParameters().Length != 0) { Log.Info("record start tab: RibbonControl.CurrentAutoHideMode not found"); return false; }
            mode = p.GetValue(rc)?.ToString() ?? "";
            return mode.Length > 0 && !mode.Equals("None", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Exception) { return false; }
    }

    /// <summary>The ribbon tabs the user can anchor to (visible, not contextual, not Retrace): (id, title). Empty if there is no ribbon.</summary>
    public static IReadOnlyList<(string Id, string Title)> AnchorTabs()
    {
        try
        {
            var rc = ComponentManager.Ribbon;
            if (rc == null) return Array.Empty<(string, string)>();
            return rc.Tabs.Where(t => t.Id != RetraceTab.TabId && t.IsVisible && !t.IsContextualTab && !string.IsNullOrWhiteSpace(t.Title))
                .Select(t => (t.Id ?? "", t.Title ?? "")).ToList();
        }
        catch (System.Exception) { return Array.Empty<(string, string)>(); }
    }

    static void Place(RibbonControl rc, RibbonTab tab, string? setting)
    {
        var others = rc.Tabs.Where(t => !ReferenceEquals(t, tab)).Select(t => (Id: t.Id ?? "", Title: t.Title ?? "")).ToList();
        var index = RibbonTabPosition.ChooseIndex(setting, others, RetraceTab.TabId);
        if (rc.Tabs.IndexOf(tab) == index) return;
        rc.Tabs.Remove(tab);
        rc.Tabs.Insert(Math.Min(index, rc.Tabs.Count), tab);
    }
}
