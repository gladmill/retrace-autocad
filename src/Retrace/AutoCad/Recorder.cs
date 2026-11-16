using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Internal.Windows;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.AutoCad;

/// <summary>Subscribes to ribbon, command, prompt, dialog and document events and writes them to a SessionWriter.
/// Every handler is wrapped: a failure is logged to retrace.log and never reaches AutoCAD.</summary>
public sealed class Recorder
{
    public static readonly Recorder Instance = new();

    SessionWriter? _w;
    readonly RibbonMap _map = new();
    // SDI: opening a drawing replaces the drawing inside the same Document object, and the events hooked to the old drawing die with it. The drawing each hooked
    // document held when it was hooked: a different one means the hooks are dead, whatever order DocumentToBeDestroyed / DocumentCreated / DocumentActivated came in.
    readonly Dictionary<Document, IntPtr> _hookedDb = new();   // the documents hooked, with the drawing each held then
    readonly Dictionary<string, string?> _promptMsg = new();
    readonly Dictionary<string, string[]?> _promptKeywords = new();
    // one number per Prompting* event, stamped on its Prompted* results: AutoCAD fires many results per prompt as the mouse moves
    readonly Dictionary<string, int> _promptSeq = new();
    int _seq;
    // Prompts open right now. AutoCAD nests prompts (GetCorner runs a point prompt inside the corner prompt; GetDistance a point
    // prompt inside the distance prompt), each raising its own Prompted* event; only the outermost result is the user's answer.
    // Their kinds, outermost first: a point inside a corner prompt is GetCorner's inner point, a point or corner inside a selection prompt is a click or typed corner of that selection.
    readonly List<string> _kinds = new();
    int _openPrompts => _kinds.Count;
    // What was typed since the last prompt result or command start (see Core/TypedInput): the corners and keywords of a built-in selection.
    readonly TypedSelection _sel = new();
    // Real input vs noise: in AutoCAD Electrical 2026 something requests a point on every mouse move while a command waits
    // (each with its own Prompting event, echoing the command's message). A result counts as user input only if a click or
    // key press reached AutoCAD's message loop shortly before it and no earlier result has claimed that press.
    long _inputTick, _claimedTick, _lastInputMs = -100000;
    // Drag samples (a jig: AutoCAD raises a result for every mouse move and key press, and every millisecond or so in a jig that keeps sampling): a result of the same kind and message as the
    // one before, with nothing else recorded in between, is a candidate. It is claimed only by the input that ends the prompt (a mouse button, Enter, Space): a key typed on the way ("0" of "0,0")
    // would claim a sample with the mouse's resting position, and the person's Enter, the last sample, would be left unclaimed behind it. A candidate nothing claimed, at most
    // DragRuns.BurstGapMs after the one before, is flagged `drag: true`, unless nobody was at the mouse or keyboard since the one before (activity counter) or a LISP is running: a script, LISP or
    // menu macro that gives its own answers makes such bursts too, and they are not drag samples. The field is written (true or false) on every candidate, so a reader can tell a recording that
    // knows about drag samples from an older one (DragRuns).
    long _endTick;                 // the value of _inputTick when the newest input was one that ends a prompt: the left mouse button, Enter, Space (not the middle button (pan), not the right one (Enter or a menu, depending on the setup; the Enter it gives arrives as its own result))
    long _activity, _activityAtSample;   // counts mouse moves, buttons and key presses; its value at the newest sample
    bool _haveSample; string? _lastSampleKind, _lastSampleMsg, _lastSampleDoc; long _lastSampleMs;
    int _viewMouse, _viewMouseAtPoint;   // left button down / up in the drawing window, counted; and its value when the last click point was taken
    bool _lastInputMouse;  // the newest input was a mouse button (a pick), else a key (typed value + Enter)
    string? _claimedBy;     // "mouse" | "keys" for the last successful ClaimInput
    RibbonControl? _hookedRibbon;
    readonly List<RibbonTab> _hookedTabs = new();
    readonly List<INotifyCollectionChanged> _hookedPanelLists = new();
    bool _subscribed;
    System.Windows.Threading.DispatcherTimer? _modalTimer;

    // Screenshots. Handlers only queue a request (an integer and a string); the timer and Idle fulfil it after the UI has painted.
    readonly ShotService _shots = new();
    System.Windows.Threading.DispatcherTimer? _shotTimer;
    readonly DialogWatch _dlg = new();   // field values of the open modal dialogs
    readonly MenuWatch _menu = new();     // context menus (a right-click and the item chosen) in dialogs and palettes
    readonly PaletteWatch _pal = new();  // field values and button clicks in palette sets
    int _modalOpen;   // EnterModal minus LeaveModal: a dialog picture is only taken while one is open
    readonly ModalPairs _modals = new();   // which EnterModal a nested dialog found by its window had taken over (its LeaveModal closes that entry)

    // checkpoint names: where the last thing was done (a dialog, a ribbon button, a typed command) and how many checkpoints so far
    Checkpoints.CheckpointContext? _lastCtx;
    NativeAction? _lastNative;              // the last native palette action, for its checkpoint name once a native_value names it
    Checkpoints.CheckpointContext? _nativeCtx;   // its context: back in force after a window it opened closed by itself
    DrawingOpen.Tracker _startup = new();   // which LISP is a drawing's start-up (not the person's): no checkpoint name from it
    int _ctxCmdDepth, _ctxLispDepth;        // commands / LISP running, as the events say: a drawing opened inside one is that step's
    int _marks;
    // Behavior snapshot per document: the start (or first sight) of it, then its last checkpoint. What the next checkpoint in THAT document auto-detects against.
    readonly Dictionary<Document, BehaviorSnapshot> _snaps = new();
    // Documents the recording saw (activated / opened) that have no snapshot yet. Handlers only add here; OnIdle takes the snapshot (never in a handler).
    readonly HashSet<Document> _needSnap = new();

    // origin attribution: the last ribbon click that has not been matched to a command yet
    long _lastRibbonT = -100000;
    string? _lastRibbonId, _lastRibbonMacro;
    bool _lastRibbonConsumed = true;
    // the same for a button clicked in a palette set: a command that starts after it, with no other click or key press in between, is the
    // button's (SendStringToExecute), also after an awaited call; _lastPaletteTick is the input count at the click
    long _lastPaletteT = -100000, _lastPaletteTick;
    string? _lastPaletteButton;
    uint _downMsgTime;        // the system time (ms) the last left button-down was posted: when the person pressed, not when the UI thread got round to it
    int _downX, _downY;       // the screen point of the last left button-down, when and in which window (setting showClickArrows)
    bool _downInWpf;          // the last left button-down was in a WPF window (the ribbon, a drop-down menu), not in the drawing or a dialog: a ribbon click counts it as its press only then

    bool _lastPaletteConsumed = true;

    /// <summary>The checkpoint shortcut was pressed during a command: the checkpoint is taken when that command ends (see Commands.MarkFromKey).</summary>
    internal bool MarkPending { get; set; }
    /// <summary>What was at the cursor when the pending checkpoint was asked for.</summary>
    internal Checkpoints.CheckpointContext? PendingAt { get; set; }

    // Captured when a command starts. Prompt handlers must not call into the Editor/Document: with mouse picks that made
    // AutoCAD re-issue the prompt in a loop (LINE took no points, the cursor flickered until Esc; seen in R25.1).
    bool _inOwnCmd;
    int _ownModal;   // dialogs Retrace's own commands opened (Settings, the test picker) that are open now: not part of the test
    Matrix3d _ucs = Matrix3d.Identity;
    readonly RecordZoomDriver _rz = new();   // setting recordZoom: the view follows what each command creates (zoomed before that command's result picture)
    ViewSpec? _view;   // the command document's active viewport view: read when a command starts and by the timer while a prompt is open (never in a prompt handler); stored with picks and clicks only while still current
    string? _viewKey;  // ViewControl.ViewKey() at that read: a plain system variable read in the result handler tells whether the view on screen is still this one
    Document? _cmdDocument;   // the document of the running command
    string? _cmdDoc;
    string? _lastTabId;

    public bool IsRecording => _w != null;
    /// <summary>About the steps a replay will show, recorded since this recording started: ribbon clicks and tab switches, commands at the top level, LISP calls, checkpoints,
    /// palette steps, clicks in native palettes and drawings opened outside a command; not a drawing's start-up LISP, nor a command nested in another
    /// (<see cref="StatusBoard.CountsAsStep"/>, <see cref="StepTally"/>). Dialogs are not counted. For the status board ("Rec 12").</summary>
    internal int StepCount => _tally.Count;
    readonly StepTally _tally = new();
    /// <summary>The text of the prompt the user is answering now (without its leading line break), or null when none is open.</summary>
    internal string? OpenPrompt => _openPrompts > 0 && !string.IsNullOrWhiteSpace(_lastPromptMsg) ? _lastPromptMsg.TrimStart('\n', '\r') : null;
    string? _lastPromptMsg;
    /// <summary>A modal dialog is open (EnterModal seen, or a dialog is being watched).</summary>
    internal bool ModalOpen => _modalOpen > 0 || _dlg.Top != IntPtr.Zero;
    public SessionWriter? Writer => _w;

    // ---- start / stop ------------------------------------------------------------------------

    public string Start(string? name)
    {
        if (_w != null) Stop();
        var header = new Dictionary<string, object?>
        {
            ["autocad"] = Try(() => AcApp.Version.ToString()),
            ["product"] = Try(() => AcApp.GetSystemVariable("PRODUCT")?.ToString()),
            ["locale"] = Try(() => AcApp.GetSystemVariable("LOCALE")?.ToString()),
            ["workspace"] = Try(() => AcApp.GetSystemVariable("WSCURRENT")?.ToString()),
            ["plugin"] = "Retrace " + (typeof(Recorder).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?"),
        };
        var atStart = RetraceSettings.Load();
        if (atStart.UnreadableKeys.Count > 0) Log.Info("settings.json: could not read " + string.Join(", ", atStart.UnreadableKeys) + " (wrong type); those keep their defaults");
        header[SettingsHeader.Field] = SettingsHeader.Snapshot(atStart);   // additive: every setting (but the folder paths) as it is at record start, so a sent recording shows how the test was made
        header[RecordZoomRules.HeaderField] = atStart.RecordZoom;  // additive: which view setting this recording was made with (a replay notes a difference)
        // a recording made in a new drawing: the template new drawings start from here, so RETRACERUN can start its drawing from the same one (it holds the layers etc.)
        Log.Safe("header template", () =>
        {
            var d = AcApp.DocumentManager.MdiActiveDocument;
            if (d != null && !d.IsNamedDrawing && Commands.QnewTemplate() is { } t) header["template"] = t;
        });
        _w = new SessionWriter(string.IsNullOrWhiteSpace(name) ? "test" : name!, header);
        BeginShots();
        _promptMsg.Clear();
        _kinds.Clear();
        _sel.Reset();
        _lastCtx = null; _startup = new(); _ctxCmdDepth = 0; _ctxLispDepth = 0;
        _marks = 0;
        _tally.Reset();
        _lastRibbonConsumed = true;
        // initial drawing state, right after the header; read here (command/idle context), never in an event handler
        Log.Safe("initial state", () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var digest = doc == null ? null : DrawingState.Read(doc);
            if (digest != null) Emit("state", CurDoc(), ("state", digest));
        });
        _snaps.Clear(); _needSnap.Clear();
        Log.Safe("initial behaviors", () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var snap = doc == null ? null : Capture(doc, RetraceSettings.Load());
            if (snap == null || doc == null) return;
            _snaps[doc] = snap;
            WriteSnapshot(snap, BehaviorSnapshot.StartPath(Path.GetDirectoryName(_w!.Path)!));
        });
        Subscribe();
        SnapshotDocs();
        StatusBoardHost.RecordingStarted();
        return _w.Path;
    }

    /// <summary>Unsubscribes everything and closes the file WITHOUT a session_end event (AutoCAD is closing).
    /// Returns the session path, or null if not recording.</summary>
    public string? Detach()
    {
        var w = _w;
        Unsubscribe();
        if (w == null) return null;
        _shots.FlushDeferred();
        _shots.Drain(2000);   // pictures taken so far get their `shot` events; pending requests are dropped
        _shots.End();
        _w = null;
        var path = w.Path;
        w.Dispose();
        StatusBoardHost.Changed();
        return path;
    }

    public (string Path, int Events, long Ms)? Stop()
    {
        var w = _w;
        if (w == null) return null;
        Log.Safe("Stop palette", () => { EmitPalette(_pal.Flush(w.ElapsedMs, drop: true)); NativeTick(true); });
        Log.Safe("Stop views", () => CheckPendingViews(ViewControl.ViewKey()));
        // the pictures still queued (the last command's result) are taken now, and their `shot` events must land before session_end
        Log.Safe("Stop shots", () => { _shots.Pump(w.ElapsedMs, force: true); _shots.Drain(); });
        // the counts of the basic check (setting `basicCheck`), always written: a recording made now can be basic-checked later
        Log.Safe("Stop end counts", () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null || DrawingState.ReadCounts(doc) is not { } c) return;
            w.Event(BasicCheckRules.EndCountsEvent, CurDoc(), ("modelspace", c.ModelSpace), ("layers", c.Layers), ("layouts", c.Layouts));
        });
        Log.Safe("Stop", () => w.Event("session_end", CurDoc()));
        Unsubscribe();
        _shots.End();
        _w = null;
        var r = (w.Path, w.Events, w.ElapsedMs);
        w.Dispose();
        StatusBoardHost.Changed();
        return r;
    }

    static string? Try(Func<string?> f) { try { return f(); } catch { return null; } }
    static string? CurDoc() { try { var d = AcApp.DocumentManager.MdiActiveDocument; return d == null ? null : Path.GetFileName(d.Name); } catch { return null; } }
    static string? DocName(Document? d) { try { return d == null ? null : Path.GetFileName(d.Name); } catch { return null; } }
    static string? DocPath(Document? d) { try { return d is { IsNamedDrawing: true } ? d.Name : null; } catch { return null; } }

    /// <summary>Writes one event and queues its screenshot request (if the type gets one). Returns the event's n, 0 if not written.</summary>
    int Emit(string type, string? doc, params (string, object?)[] f)
    {
        var w = _w;
        if (w == null) return 0;
        try
        {
            var n = w.Event(type, doc, f);
            if (type is not ("prompt" or "shot" or "view_stale" or "state")) _haveSample = false;   // something else happened: the next result starts a new prompt
            NoteContext(type, f);
            var input = type == "prompt" && f.Any(x => x.Item1 == "input" && x.Item2 is true);
            _shots.Request(ShotRules.For(type, n, input, w.ElapsedMs));
            return n;
        }
        catch (Exception ex) { Log.Error("Emit " + type, ex); return 0; }
    }

    /// <summary>Remembers where the last action happened, for the next checkpoint's automatic name. Data only.</summary>
    void NoteContext(string type, (string, object?)[] f)
    {
        string? F(string k) => f.FirstOrDefault(x => x.Item1 == k).Item2 as string;
        // the LISP AutoCAD runs while a drawing opens, and the commands inside it, are not what the person did: the readers leave
        // them out of the steps (DrawingOpen.StartupEvents, same tracker), so they name no checkpoint either
        var startupFlag = f.FirstOrDefault(x => x.Item1 == "startup").Item2 as bool?;
        if (_startup.Feed(type, _w?.ElapsedMs ?? 0, startupFlag)) return;
        _tally.Note(type == "context_menu" && F("palette") != null ? "palette_click" : type, F("origin"), _ctxCmdDepth, _ctxLispDepth, F("path"), F("id"), F("palette"), F("set"),
            _w?.ElapsedMs ?? -1, F("command"), f.FirstOrDefault(x => x.Item1 == "input").Item2 as bool?, f.FirstOrDefault(x => x.Item1 == "script").Item2 as bool?);   // before the depth moves; data only: the status board reads it on its timer
        switch (type)
        {
            case "cmd_start": _ctxCmdDepth++; break;
            case "cmd_end" or "cmd_cancel" or "cmd_fail": if (_ctxCmdDepth > 0) _ctxCmdDepth--; break;
            case "lisp_start": _ctxLispDepth++; break;
            case "lisp_end" or "lisp_cancel": if (_ctxLispDepth > 0) _ctxLispDepth--; break;
        }
        // the person did something else: a window that closes by itself from now on is not the native action's
        if (type is "ribbon" or "lisp_start" or "palette_values" or "palette_click" or "context_menu" or "doc_created" || (type == "cmd_start" && F("origin") != "palette")) _nativeCtx = null;
        switch (type)
        {
            // opened while no command or LISP ran: an "Open drawing" step (the readers' rule); inside OPEN it is that command's
            case "doc_created" when F("path") is { } opened && _ctxCmdDepth == 0 && _ctxLispDepth == 0:
                _lastCtx = new Checkpoints.CheckpointContext("open", null, Path.GetFileName(opened));
                break;
            case "ribbon": _lastCtx = new Checkpoints.CheckpointContext("ribbon", F("panel") ?? F("tab"), F("text") ?? F("id")); break;
            case "cmd_start" when F("origin") is not ("ribbon" or "palette"): _lastCtx = new Checkpoints.CheckpointContext("command", null, F("command")); break;
            case "lisp_start": _lastCtx = new Checkpoints.CheckpointContext("command", null, "lisp"); break;
            case "modal_title": _lastCtx = new Checkpoints.CheckpointContext("dialog", F("title"), null); break;
            // a window a native palette action opened that closed by itself is no step for the readers (NativeAction.ClosedByItself):
            // the native action stays the checkpoint's context
            case "dialog_values" when _nativeCtx != null && F("closedWith") == null && f.FirstOrDefault(x => x.Item1 == "read").Item2 is null or 0:
                _lastCtx = _nativeCtx;
                break;
            case "dialog_values": _lastCtx = new Checkpoints.CheckpointContext("dialog", F("title") ?? _lastCtx?.Container, F("closedWith")); break;
            case "palette_values": _lastCtx = new Checkpoints.CheckpointContext("palette", F("palette"), null); break;
            case "palette_click": _lastCtx = new Checkpoints.CheckpointContext("palette", F("palette"), F("button")); break;
            case "context_menu" when F("palette") != null: _lastCtx = new Checkpoints.CheckpointContext("palette", F("palette"), f.FirstOrDefault(x => x.Item1 == "path").Item2 is List<string> menuPath ? string.Join(TreePath.Separator, menuPath) : null); break;
            case "native_click" or "native_select":
            {
                // the readers' rule (Checkpoints.ContextOf): where and what; an open that follows names the checkpoint after the drawing
                var na = new NativeAction(F("control") ?? "", F("action") ?? "", f.FirstOrDefault(x => x.Item1 == "path").Item2 as string[] ?? Array.Empty<string>(),
                    f.FirstOrDefault(x => x.Item1 == "index").Item2 as int? ?? 1, 0, F("label"), F("value"), f.FirstOrDefault(x => x.Item1 == "entry").Item2 as int? ?? 0);
                _lastCtx = _nativeCtx = new Checkpoints.CheckpointContext("control", F("palette"), na.Target, na.Describe() + $" in the {F("palette")} palette");
                _lastNative = na;
                break;
            }
            case "native_value" when _lastNative != null && _nativeCtx != null && F("value") is { } value:
            {
                // the entry's name, now known: as the readers name the step (if nothing else happened since)
                _lastNative = _lastNative with { Value = value };
                var named = _nativeCtx with { Control = _lastNative.Target, Description = _lastNative.Describe() + $" in the {_nativeCtx.Container} palette" };
                if (ReferenceEquals(_lastCtx, _nativeCtx)) _lastCtx = named;
                _nativeCtx = named;
                break;
            }
        }
    }

    // ---- screenshots -------------------------------------------------------------------------------------------------

    /// <summary>Called from RETRACERECORD / the resume, i.e. command or init context, where reading the main window handle is fine.</summary>
    void BeginShots()
    {
        var w = _w!;
        _modalOpen = 0; _ownModal = 0; MarkPending = false; _lastPaletteConsumed = true;
        CheckpointKey.Reload();
        var settings = RetraceSettings.Load();
        var enabled = settings.Screenshots;
        _dlg.Clear(); _modals.Clear(); _episodes.Clear();
        _dlg.Enabled = settings.DialogValues;
        _pal.Clear(); NativePalette.Clear(); PaletteClickDetect.NewSession();
        _pal.Enabled = settings.DialogValues;
        _pal.Pictures = settings.Screenshots;
        _pal.Smart = _shots.Smart = settings.Screenshots && settings.SmartShots;   // screenshotMode smart: palette crops with surroundings, a ribbon click shows its tab row and panel
        _dlg.Clock = () => _w?.ElapsedMs ?? 0;
        _dlg.Confirmed = (n, h, pt, rect) =>
        {
            _shots.FlushDeferred();   // the click before OK has settled: the dialog shows what it did
            // the picture of the filled dialog points at the button that was clicked with the mouse (the point and the button's rectangle come from the dialog watch's own hook; a key press closing the dialog has none)
            if (_shots.Arrows) { if (pt is { } p) _shots.NoteClick(n, p.X, p.Y); else _shots.ForgetClick(n); }   // an Enter after an earlier mouse OK gets no arrow
            if (rect is { } r) _shots.NoteTarget(n, r.Left, r.Top, r.Right, r.Bottom);
            _shots.DialogAfter(n, h);
        };
        _dlg.WantRects = settings.Screenshots && settings.TestDocArrows;
        PaletteClickDetect.WantRects = settings.Screenshots && settings.TestDocArrows;
        ToolbarControl.ForgetAsked();   // with the arrows off no rectangle is read
        _dlg.Picked = settings.Screenshots ? p => Log.Safe("dialog pick", () => EmitDialogPick(p)) : null;   // with pictures off nothing is read for a pick
        _dlg.Clicked = c => Log.Safe("dialog click", () => EmitDialogClick(c));
        _dlg.Main = MainWindowHandle();
        _menu.Enabled = settings.DialogValues;
        _menu.Clock = () => _w?.ElapsedMs ?? 0;
        _menu.DialogTop = () => _dlg.Top;
        _menu.PaletteSnap = spot => { try { return _pal.PictureNow(spot); } catch (Exception ex) { Log.Error("menu picture", ex); return null; } };
        _menu.Chosen = (choice, armed) => Log.Safe("context menu", () => EmitContextMenu(choice, armed));
        _menu.Start();
        _dlg.NestedOpened = e => { var n = 0; Log.Safe("nested dialog", () => n = EmitNestedOpened(e)); return n; };
        _dlg.NestedClosed = r => Log.Safe("nested dialog closed", () => { EmitDialogValues(r); Emit("modal_leave", CurDoc()); });
        _rz.Begin(settings.RecordZoom, Ask.ScriptOrLispBits());
        _shots.Arrows = settings.TestDocArrows;   // the test document's own setting (the training document's showClickArrows is not read here)
        ButtonNaming.NewSession();
        _shots.Hold = YoungModal;
        _shots.Begin(w.Path, enabled, MainWindowHandle(), () => _modalOpen > 0, (n, file, what) =>
        {
            var ww = _w;   // pool thread: only the writer is touched
            if (ww != null) { try { ww.Event("shot", null, ("for", n), ("file", file), ("what", what)); } catch (Exception ex) { Log.Error("Emit shot", ex); } }
        });
    }

    static IntPtr MainWindowHandle()
    {
        try { return AcApp.MainWindow.Handle; } catch (Exception ex) { Log.Error("main window handle", ex); return IntPtr.Zero; }
    }

    /// <summary>Timer context: while a prompt is open (a pick or a click can come), keeps the view the next pick will be made in (it may have been zoomed or panned since the command started).
    /// The key (system variables that describe the view) is compared first; the Editor is asked only when it changed.</summary>
    void RefreshView(string? key)
    {
        if (_kinds.Count == 0 || _inOwnCmd || _modalOpen > 0 || key == null) return;
        if (_view != null && key == _viewKey) return;
        if (_cmdDocument is { } d && ViewControl.Read(d) is { } v) { _view = v; _viewKey = key; }
    }

    // A pick or click whose answer carries `view`: the handler (no AutoCAD call there) only notes the event number and the key of the last full read. The next timer tick compares that key with
    // the view system variables: if they differ, the view may have changed before the pick (a wheel zoom just before the click), and a `view_stale` event tells the readers to drop `view` of that answer.
    readonly List<(int N, string? Key)> _pendingViews = new();

    void NotePickView(int n, object? value)
    {
        if (n > 0 && value is Dictionary<string, object?> d && d.ContainsKey("view")) _pendingViews.Add((n, _viewKey));
    }

    /// <summary>Timer context (or the stop): the view of the picks since the last tick is checked against <paramref name="key"/>; with no key (unreadable) every one is marked stale.</summary>
    void CheckPendingViews(string? key)
    {
        if (_pendingViews.Count == 0) return;
        var items = _pendingViews.ToArray();
        _pendingViews.Clear();
        foreach (var (n, k) in items)
            if (key == null || k == null || k != key) Emit("view_stale", _cmdDoc, ("for", n));
    }

    /// <summary>Setting recordZoom: the zoom of a command that created objects comes before its result picture. The pictures wait for it while it is about to happen (at most a second).</summary>
    long _holdSince;

    void PumpShots(long nowMs)
    {
        if (_rz.Active)
        {
            Log.Safe("record zoom", () => _rz.Pump(() => CheckPendingViews(ViewControl.ViewKey())));   // picks still waiting for their view check are judged against the view they were made in
            if (_rz.Waiting) { if (_holdSince == 0) _holdSince = nowMs; if (nowMs - _holdSince < RecordZoomDriver.HoldMs) return; }   // a picture waits for the zoom a second at most
            else _holdSince = 0;
        }
        _shots.Pump(nowMs);
    }

    void StartShotTimer()
    {
        if (_shotTimer != null) return;
        var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) => Log.Safe("shot timer", () => { var w = _w; if (w != null) { PumpShots(w.ElapsedMs); _dlg.Tick(w.ElapsedMs); _menu.Tick(w.ElapsedMs); _pal.Tick(w.ElapsedMs); NativeTick(false); var vk = _pendingViews.Count > 0 || _kinds.Count > 0 ? ViewControl.ViewKey() : null; CheckPendingViews(vk); RefreshView(vk); } });
        _shotTimer = t;
        t.Start();
    }

    void SnapshotDocs()
    {
        foreach (Document d in AcApp.DocumentManager) HookDocument(d);
        Emit("doc_active", CurDoc());
    }

    // ---- subscriptions -----------------------------------------------------------------------

    void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        var dm = AcApp.DocumentManager;
        dm.DocumentActivated += OnDocActivated;
        dm.DocumentCreated += OnDocCreated;
        dm.DocumentToBeDestroyed += OnDocToBeDestroyed;
        AcApp.EnterModal += OnEnterModal;
        AcApp.LeaveModal += OnLeaveModal;
        AcApp.Idle += OnIdle; // used to attach to the ribbon once it exists
        ComponentManager.ItemExecuted += OnItemExecuted;
        AcApp.PreTranslateMessage += OnPreTranslate;
        TryHookRibbon();
        StartShotTimer();
    }

    void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        Log.Safe("Unsubscribe", () =>
        {
            var dm = AcApp.DocumentManager;
            dm.DocumentActivated -= OnDocActivated;
            dm.DocumentCreated -= OnDocCreated;
            dm.DocumentToBeDestroyed -= OnDocToBeDestroyed;
            AcApp.EnterModal -= OnEnterModal;
            AcApp.LeaveModal -= OnLeaveModal;
            AcApp.Idle -= OnIdle;
            ComponentManager.ItemExecuted -= OnItemExecuted;
            AcApp.PreTranslateMessage -= OnPreTranslate;
            foreach (var d in _hookedDb.Keys.ToList()) UnhookDocument(d);
            UnhookRibbon(); _snaps.Clear(); _needSnap.Clear();
            _modalTimer?.Stop();
            _modalTimer = null;
            _shotTimer?.Stop();
            _shotTimer = null;
            _rz.Clear();
            _dlg.Clear(); _modals.Clear(); _episodes.Clear();   // also removes the dialog message hook
            _menu.Stop();
            _pal.Clear(); NativePalette.Clear();
        });
    }

    void OnIdle(object? s, EventArgs e)
    {
        if (_hookedRibbon == null) TryHookRibbon();
        if (_w == null) return;
        if (_shots.NeedsMainWindow) Log.Safe("main window", () => _shots.SetMainWindow(MainWindowHandle()));
        Log.Safe("shot idle", () => PumpShots(_w.ElapsedMs));
        if (_needSnap.Count > 0) Log.Safe("first behavior snapshot", TakeNeededSnapshot);
    }

    /// <summary>Idle context: the first snapshot of the active document when the recording first saw it, taken only while no command runs
    /// (a checkpoint taken first has already made one).</summary>
    void TakeNeededSnapshot()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null || !_needSnap.Contains(doc)) return;
        if (_snaps.ContainsKey(doc)) { _needSnap.Remove(doc); return; }
        if (!string.IsNullOrEmpty(doc.CommandInProgress) || _w == null) return;
        _needSnap.Remove(doc);
        if (Capture(doc, RetraceSettings.Load()) is { } snap) _snaps[doc] = snap;
    }

    // ---- ribbon ------------------------------------------------------------------------------

    void TryHookRibbon()
    {
        Log.Safe("TryHookRibbon", () =>
        {
            var r = ComponentManager.Ribbon;
            if (r == null || ReferenceEquals(r, _hookedRibbon)) return;
            UnhookRibbon();
            _hookedRibbon = r;
            _map.Invalidate();
            (r.Tabs as INotifyCollectionChanged)?.CollectionChanged += OnTabsChanged;
            r.PropertyChanged += OnRibbonPropertyChanged;
            foreach (var t in r.Tabs) HookTab(t);
            _lastTabId = r.ActiveTab?.Id;
        });
    }

    void UnhookRibbon()
    {
        var r = _hookedRibbon;
        if (r == null) return;
        _hookedRibbon = null;
        Log.Safe("UnhookRibbon", () =>
        {
            (r.Tabs as INotifyCollectionChanged)?.CollectionChanged -= OnTabsChanged;
            r.PropertyChanged -= OnRibbonPropertyChanged;
            foreach (var t in _hookedTabs) { t.Activated -= OnTabActivated; }
            _hookedTabs.Clear();
            foreach (var pl in _hookedPanelLists) pl.CollectionChanged -= OnTabsChanged;
            _hookedPanelLists.Clear();
        });
    }

    void HookTab(RibbonTab t)
    {
        if (_hookedTabs.Contains(t)) return;
        _hookedTabs.Add(t);
        t.Activated += OnTabActivated; // fires when the tab becomes active; also covers contextual tabs
        if (t.Panels is INotifyCollectionChanged pl) { pl.CollectionChanged += OnTabsChanged; _hookedPanelLists.Add(pl); }
    }

    void OnTabsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        Log.Safe("OnTabsChanged", () =>
        {
            _map.Invalidate();
            var r = _hookedRibbon;
            if (r != null) foreach (var t in r.Tabs) HookTab(t);
        });
    }

    void OnRibbonPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "ActiveTab") Log.Safe("ActiveTab", () => EmitTab(_hookedRibbon?.ActiveTab));
    }

    void OnTabActivated(object? s, EventArgs e) => Log.Safe("TabActivated", () => EmitTab(s as RibbonTab));

    void EmitTab(RibbonTab? t)
    {
        if (t == null || t.Id == _lastTabId) return;
        _lastTabId = t.Id;
        if (Plan.IsOwnTab(t.Id)) return;   // going to the Retrace tab to stop or mark is not part of the test
        Emit("tab", CurDoc(), ("id", t.Id), ("title", t.Title ?? t.Name));
    }

    void OnItemExecuted(object? sender, RibbonItemExecutedEventArgs e)
    {
        Log.Safe("ItemExecuted", () =>
        {
            var item = e.Item;
            if (item == null) return;
            // only a text macro is a macro: buttons that run code (the Samples tab) carry an object here, whose ToString is its type name
            var macro = (item as RibbonCommandItem)?.CommandParameter as string;
            // the event args carry Tab/Panel directly; the id map is the fallback (and gives the path inside the panel)
            var loc = _map.Find(item.Id);
            if (loc == null) { _map.Invalidate(); loc = _map.Find(item.Id); }
            var tab = e.Tab; var panel = e.Panel;
            if (Plan.IsOwnRibbon(item.Id, tab?.Id ?? loc?.TabId)) return;   // Retrace' own buttons (Stop, Checkpoint...) are not part of the test
            _lastRibbonT = _w?.ElapsedMs ?? 0;
            _lastRibbonId = item.Id;
            _lastRibbonMacro = macro;
            _lastRibbonConsumed = false;
            // the picture of the click is taken now, with the button under the cursor and before the command's dialog covers the screen; the picture the scheduler would take 200 ms later is then not needed
            ShotService.Picture? clickPic = null;
            bool smartPic = false;
            // where the mouse was pressed for this click, read before the picture is taken (a drop-down closes, a palette opens)
            var down = Core.RibbonMark.FreshDown(_downX, _downY, _downMsgTime, unchecked((uint)Environment.TickCount), _downInWpf);
            if (_shots.Enabled)
            {
                var main = MainWindowHandle();
                // smart zoom: the button, its panel and the tab row, as a 1:1 crop (SmartCrop); when its rectangles cannot be read the whole ribbon, and without a ribbon the window as before
                if (_shots.Smart) { clickPic = SmartShots.RibbonPicture(item.Id, e.Parent?.Id, panel, main); smartPic = clickPic != null; }
                clickPic ??= ShotService.CaptureWindow(main)?.Pic;
            }
            var n0 = Emit("ribbon", CurDoc(),
                ("id", item.Id), ("text", RibbonMap.Label(item)), ("name", item.Name),
                ("macro", macro), ("itemType", item.GetType().Name),
                ("tabId", tab?.Id ?? loc?.TabId), ("tab", tab?.Title ?? loc?.TabTitle),
                ("panelId", panel?.Source?.Id ?? loc?.PanelId), ("panel", panel?.Source?.Title ?? loc?.PanelTitle), ("path", loc?.Path),
                ("parent", e.Parent == null ? null : RibbonMap.Label(e.Parent)),
                ("activeTab", _hookedRibbon?.ActiveTab?.Title));
            if (n0 > 0 && clickPic != null)
            {
                _shots.Suppress(n0);
                if (_shots.Arrows) NoteRibbonMark(n0, item, e.Parent, down, clickPic.HasOrigin ? new Core.PxRect(clickPic.Left, clickPic.Top, clickPic.Left + clickPic.SrcWidth, clickPic.Top + clickPic.SrcHeight) : null);
                _shots.Write(n0, "ribbon", clickPic, "window", keepSize: smartPic);
            }
        });
    }

    /// <summary>The green box and arrow of a ribbon click's picture (<see cref="Core.RibbonMark"/>): the clicked item's own rectangle, read now (the object the click ran, wherever it shows), else the button that holds it, else an
    /// arrow at the mouse-down point; when none is known nothing is drawn and retrace.log says why.</summary>
    void NoteRibbonMark(int n, RibbonItem item, RibbonItem? parent, (int X, int Y)? down, Core.PxRect? picture)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Core.PxRect? Rect(RibbonItem? it)
        {
            if (it == null) return null;
            var r = TdocTargets.ItemRectAnywhere(it, down);
            if (r == null && !string.IsNullOrEmpty(it.Id) && TdocTargets.RibbonItemRect(it.Id) is { Panel: false } byId) r = byId.Rect;   // the lookup by id, as before
            return r is { } s ? new Core.PxRect(s.Left, s.Top, s.Right, s.Bottom) : null;
        }
        // the button that holds the item is looked up only when the item's own rectangle is not used, and only when it is a button-like item (a row panel's rectangle is the whole row)
        var parentType = parent?.GetType().Name;
        var choice = Core.RibbonMark.Choose(Rect(item), () => parent != null && Core.RibbonMark.IsButtonLike(parentType) ? Rect(parent) : null, down, picture);
        switch (choice.Kind)
        {
            case Core.RibbonMarkKind.Box:
                _shots.NoteTarget(n, choice.Box.Left, choice.Box.Top, choice.Box.Right, choice.Box.Bottom);
                break;
            case Core.RibbonMarkKind.Point:
                _shots.NoteClick(n, choice.X, choice.Y);
                break;
        }
        // one line per ribbon click: what was marked, why not, how long the lookup took (the walk is over the visual trees of the ribbon and open pop-ups)
        var ms = clock.ElapsedMilliseconds;
        if (choice.Kind != Core.RibbonMarkKind.Box || ms > 30 || parent != null) Log.Info($"ribbon click {n} ({item.Id}): mark {choice.Kind.ToString().ToLowerInvariant()} ({choice.Reason}); mark lookup {ms} ms{(ms > 30 ? " (slow)" : "")}; item {item.GetType().Name}, parent {parentType ?? "none"}");
        else Log.Info($"ribbon click {n} ({item.Id}): mark box; mark lookup {ms} ms");
    }

    // ---- documents and commands --------------------------------------------------------------

    void OnDocActivated(object? s, DocumentCollectionEventArgs e) => Log.Safe("DocActivated", () => { HookDocument(e.Document); NeedSnapshot(e.Document); Emit("doc_active", DocName(e.Document)); });
    // path: the full path of a drawing opened from a file (null for a new, never saved one), so a replay can open it again
    void OnDocCreated(object? s, DocumentCollectionEventArgs e) => Log.Safe("DocCreated", () => { HookDocument(e.Document); NeedSnapshot(e.Document); Emit("doc_created", DocName(e.Document), ("path", DocPath(e.Document))); });
    void OnDocToBeDestroyed(object? s, DocumentCollectionEventArgs e) => Log.Safe("DocToBeDestroyed", () => { Emit("doc_closing", DocName(e.Document)); UnhookDocument(e.Document); _snaps.Remove(e.Document); _needSnap.Remove(e.Document); });

    /// <summary>Handler-safe: only notes that this document has no behavior snapshot yet (<see cref="OnIdle"/> takes it).</summary>
    void NeedSnapshot(Document? d)
    {
        if (d != null && !_snaps.ContainsKey(d)) _needSnap.Add(d);
    }

    void HookDocument(Document d)
    {
        if (d == null) return;
        if (_hookedDb.TryGetValue(d, out var held))
        {
            if (!SdiDocs.NeedsRehook(true, SdiDocs.DbId(d), held)) return;
            Log.Info($"recorder: {DocName(d)}: the drawing in this document was replaced (SDI); hooking it again");
            UnhookDocument(d);
            _snaps.Remove(d); _needSnap.Add(d);   // a new drawing: its own behavior snapshot, as DocumentToBeDestroyed would have discarded the old one
        }
        _hookedDb[d] = SdiDocs.DbId(d);
        d.CommandWillStart += OnCmdStart;
        d.CommandEnded += OnCmdEnded;
        d.CommandCancelled += OnCmdCancelled;
        d.CommandFailed += OnCmdFailed;
        d.UnknownCommand += OnUnknown;
        d.LispWillStart += OnLispStart;
        d.LispEnded += OnLispEnded;
        d.LispCancelled += OnLispCancelled;
        HookEditor(d.Editor);
    }


    void UnhookDocument(Document d)
    {
        if (d == null || !_hookedDb.Remove(d)) return;
        Log.Safe("UnhookDocument", () =>
        {
            d.CommandWillStart -= OnCmdStart;
            d.CommandEnded -= OnCmdEnded;
            d.CommandCancelled -= OnCmdCancelled;
            d.CommandFailed -= OnCmdFailed;
            d.UnknownCommand -= OnUnknown;
            d.LispWillStart -= OnLispStart;
            d.LispEnded -= OnLispEnded;
            d.LispCancelled -= OnLispCancelled;
            UnhookEditor(d.Editor);
        });
    }

    static bool IsOurs(string? cmd) => OwnCommands.IsOwnCommand(cmd);

    /// <summary>CMDACTIVE bit 4 at this moment: AutoCAD says a script (SCRIPT) is active. Written as `script` on cmd_start, lisp_start and cmd_unknown; null (left out) when the variable cannot be read.</summary>
    internal static bool? ScriptActive()
    {
        return Ask.ActiveBits() is { } active ? (active & 4) != 0 : null;
    }

    void OnCmdStart(object? s, CommandEventArgs e) => Log.Safe("CommandWillStart", () =>
    {
        var name = e.GlobalCommandName;
        if (IsOurs(name)) { _sel.OwnCommandStart(_ctxCmdDepth > 0 || _ctxLispDepth > 0); _inOwnCmd = true; return; }   // (typed inside a prompt, the keys of 'RETRACEMARK are in the selection buffer: unusable) leaves _openPrompts alone: an own command typed transparently ('RETRACESTATUS) runs inside the user's prompt
        _kinds.Clear();
        _rz.Start(s as Document);
        _sel.CommandStart(_ctxCmdDepth > 0 || _ctxLispDepth > 0);   // a transparent command inside a prompt spoils the outer selection's text
        _cmdDoc = DocName(s as Document);
        _claimedTick = _inputTick; // the click/Enter that started the command answers no prompt
        try { if (s is Document d) _ucs = d.Editor.CurrentUserCoordinateSystem; } catch { _ucs = Matrix3d.Identity; }
        _cmdDocument = s as Document;
        _view = ViewControl.Read(_cmdDocument); _viewKey = _view == null ? null : ViewControl.ViewKey();
        var (origin, via) = Attribute(name);
        Emit("cmd_start", DocName(s as Document), ("command", name), ("origin", origin), ("via", via), ("script", ScriptActive()));
    });

    void OnCmdEnded(object? s, CommandEventArgs e) => Log.Safe("CommandEnded", () => { if (IsOurs(e.GlobalCommandName)) { _inOwnCmd = false; TakePendingMark(); } else { _kinds.Clear(); _rz.End(s as Document); _rz.EndNow(s as Document, e.GlobalCommandName, null, () => CheckPendingViews(ViewControl.ViewKey())); Emit("cmd_end", DocName(s as Document), ("command", e.GlobalCommandName)); TakePendingMark(); } });
    void OnCmdCancelled(object? s, CommandEventArgs e) => Log.Safe("CommandCancelled", () => { if (IsOurs(e.GlobalCommandName)) { _inOwnCmd = false; TakePendingMark(); } else { _kinds.Clear(); _rz.End(s as Document); _rz.EndNow(s as Document, e.GlobalCommandName, null, () => CheckPendingViews(ViewControl.ViewKey())); Emit("cmd_cancel", DocName(s as Document), ("command", e.GlobalCommandName)); TakePendingMark(); } });
    void OnCmdFailed(object? s, CommandEventArgs e) => Log.Safe("CommandFailed", () => { if (IsOurs(e.GlobalCommandName)) { _inOwnCmd = false; TakePendingMark(); } else { _kinds.Clear(); _rz.End(s as Document); _rz.EndNow(s as Document, e.GlobalCommandName, null, () => CheckPendingViews(ViewControl.ViewKey())); Emit("cmd_fail", DocName(s as Document), ("command", e.GlobalCommandName)); TakePendingMark(); } });

    /// <summary>A command ended: a checkpoint asked for during it is taken now, once AutoCAD has finished ending it (MarkFromKey waits again if
    /// another command is still running).</summary>
    void TakePendingMark()
    {
        if (!MarkPending || _w == null) return;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            () => Log.Safe("pending checkpoint", () => { if (MarkPending) Commands.Instance.MarkFromKey(PendingAt); }));   // two ends in a row queue two calls
    }
    void OnUnknown(object? s, UnknownCommandEventArgs e) => Log.Safe("UnknownCommand", () => Emit("cmd_unknown", DocName(s as Document), ("command", e.GlobalCommandName), ("script", ScriptActive())));
    // startup: LISP AutoCAD runs while a drawing opens (no click or key since it opened), not the person's (DrawingOpen)
    void OnLispStart(object? s, LispWillStartEventArgs e) => Log.Safe("LispWillStart", () =>
    {
        _rz.Start(s as Document);
        _sel.LispStart(_ctxCmdDepth > 0 || _ctxLispDepth > 0);   // its own keys come before an ssget; typed inside a prompt (a replay's (handent ..)) it spoils the text
        _cmdDoc = DocName(s as Document);
        Emit("lisp_start", _cmdDoc, ("firstLine", e.FirstLine), ("startup", _startup.IsStartupLisp(_w?.ElapsedMs ?? 0, _lastInputMs)), ("script", ScriptActive()));
    });
    void OnLispEnded(object? s, EventArgs e) => Log.Safe("LispEnded", () => { _rz.End(s as Document); _rz.EndNow(s as Document, null, null, () => CheckPendingViews(ViewControl.ViewKey())); Emit("lisp_end", DocName(s as Document)); });
    void OnLispCancelled(object? s, EventArgs e) => Log.Safe("LispCancelled", () => { _rz.End(s as Document); _rz.EndNow(s as Document, null, null, () => CheckPendingViews(ViewControl.ViewKey())); Emit("lisp_cancel", DocName(s as Document)); });

    /// <summary>"ribbon" if a ribbon click happened within 2 s and its macro contains the command name (or it has no macro
    /// and the command started within 1 s), else "typed/other".</summary>
    (string Origin, string? Via) Attribute(string command)
    {
        var now = _w?.ElapsedMs ?? 0;
        // built-in CUI buttons carry no macro (CommandParameter is null): then the first command within 1 s is the click's
        if (!_lastRibbonConsumed && now - _lastRibbonT <= 2000
            && (MacroMentions(_lastRibbonMacro, command) || (_lastRibbonMacro == null && now - _lastRibbonT <= 1000)))
        {
            _lastRibbonConsumed = true;
            return ("ribbon", _lastRibbonId);
        }
        if (!_lastPaletteConsumed && now - _lastPaletteT <= 30000 && _inputTick <= _lastPaletteTick + 1)
        {
            _lastPaletteConsumed = true;
            return ("palette", _lastPaletteButton);
        }
        return ("typed/other", null);
    }

    /// <summary>True if the (^C^C_-prefixed, ;-separated) menu macro contains the command.</summary>
    public static bool MacroMentions(string? macro, string command)
    {
        if (string.IsNullOrEmpty(macro) || string.IsNullOrEmpty(command)) return false;
        var m = macro.Replace("^C", " ", StringComparison.OrdinalIgnoreCase).Replace(";", " ").Replace("'", " ");
        foreach (var raw in m.Split(new[] { ' ', '\t', '\r', '\n', '(', ')', '"', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.TrimStart('_', '.', '-', '*');
            if (t.Equals(command.TrimStart('_', '.', '-'), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---- modal dialogs -----------------------------------------------------------------------

    readonly List<ModalEpisode> _episodes = new();   // the modal states open now (EnterModal order): did a window other than the main window ever show in each?

    void OnEnterModal(object? s, EventArgs e) => Log.Safe("EnterModal", () =>
    {
        _shots.FlushDeferred();   // a click that opened this dialog: its picture is taken now, before the window shows (it would show the dialog later)
        _modalOpen++;
        if (_inOwnCmd || _ownModal > 0) { _ownModal++; return; }
        var token = _modals.Enter();
        var fg = Native.Foreground();
        var main = MainWindowHandle();
        bool mainInFront = main != IntPtr.Zero && Native.ForegroundWindow() == main;
        // `main`: the window in front IS AutoCAD's main window (readers tell a modal state with no dialog window of its own from a dialog by it, not by the title)
        var before = Native.VisibleHandles();   // a dialog of this modal state is a window that is not among these (a floating palette or a modeless form was there already)
        var nEnter = Emit("modal_enter", CurDoc(), ("title", fg?.Title), ("windowClass", fg?.Class), ("main", mainInFront));
        var watch = nEnter > 0 ? _dlg.Push(nEnter) : null;
        foreach (var older in _episodes) if (!older.WindowSeen) older.Abandon();   // the timer below replaces theirs: nothing more is known about them
        var episode = new ModalEpisode(nEnter, before) { StartMs = _w?.ElapsedMs ?? 0, InputTick = _inputTick, DlgInput = _dlg.InputCount, CanSeeInput = watch != null };
        _episodes.Add(episode);
        // the dialog is often not foreground yet at this instant: look again shortly after (every 100 ms for the first 1.5 s: the moment it is first seen decides whether it was a flash, see FlashDialog), and then every
        // 250 ms while the modal state is open (a slow dialog shows after a second or more)
        _modalTimer?.Stop();
        var disp = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, disp) { Interval = TimeSpan.FromMilliseconds(100) };
        _modalTimer = timer;
        timer.Tick += (_, _) =>
        {
            if ((_w?.ElapsedMs ?? 0) - episode.StartMs > 1500) timer.Interval = TimeSpan.FromMilliseconds(250);
            Log.Safe("ModalTitle", () =>
            {
                if (!_episodes.Contains(episode)) { timer.Stop(); return; }   // left already
                // announced late: the dialog is one a button of another dialog opened, found by its window before AutoCAD said so. It is that dialog, not a second one: an alias tells the readers,
                // and there is no title or picture for the announcement. (Only when the announced window IS that dialog: another dialog that opens over it is a dialog of its own.)
                var announced = Native.ShownForegroundOfThisProcess(main);   // a form that is never seen (fully transparent, empty) is no window
                if (watch != null && announced != IntPtr.Zero && _dlg.MergeInto(watch, announced) is { } into)
                {
                    timer.Stop(); episode.WindowFound();
                    _modals.Adopt(token);
                    Emit("modal_alias", CurDoc(), ("for", nEnter), ("of", into));
                    return;
                }
                // the dialog: the foreground window of this process that is not the main window, else (another program is in front) the front dialog window among this process's own windows
                var dlgWnd = announced != IntPtr.Zero ? announced : Native.DialogWindow(main, default, episode.Before);
                if (dlgWnd == IntPtr.Zero) return;   // none yet: look again (it is written as `modal_nowindow` at the leave if none ever shows)
                timer.Stop(); episode.WindowFound();
                episode.SeenMs ??= _w?.ElapsedMs;
                var shotFor = nEnter;   // the dialog picture belongs to modal_title when a title arrives, else to modal_enter
                var title = fg?.Title;
                var shown = dlgWnd == announced ? (Native.Foreground() ?? (Title: Native.TitleOf(dlgWnd), Class: Native.ClassOf(dlgWnd))) : (Title: Native.TitleOf(dlgWnd), Class: Native.ClassOf(dlgWnd));
                if (shown.Title != fg?.Title || shown.Class != fg?.Class)
                {
                    var nTitle = Emit("modal_title", CurDoc(), ("title", shown.Title), ("windowClass", shown.Class), ("main", false));
                    if (nTitle > 0) { shotFor = nTitle; title = shown.Title; episode.TitleN = nTitle; }
                }
                if (watch != null && _w != null)
                {
                    _dlg.Resolve(watch, dlgWnd, shotFor, title, _w.ElapsedMs);   // (throws before the hook is installed when the window cannot be read: HookOk stays false, the dialog is never a flash)
                    episode.HookOk = _dlg.HookActive;
                }
                if (shotFor > 0 && _w != null) _shots.Request(ShotRules.Dialog(shotFor, _w.ElapsedMs, dlgWnd.ToInt64()));   // a picture only of a real dialog window
            });
        };
        timer.Start();
    });

    /// <summary>A dialog window is on screen that may still turn out to be a flash (seen for under <see cref="FlashDialog.ThresholdMs"/>, nobody has touched it): the shot service holds its pictures.</summary>
    bool YoungModal()
    {
        var w = _w;
        if (w == null || _episodes.Count == 0) return false;
        var ep = _episodes[^1];
        if (ep.N <= 0 || Touched(ep)) return false;
        // a dialog window is on screen that the modal timer has not looked at yet (it looks every 100 ms): wait for that, the time it counts from is the moment it was seen
        // (not for ever, and not for a state whose window was found already: one that merged into a dialog a button opened can never be a flash)
        if (ep.SeenMs == null)
            return !ep.WindowSeen && ep.CanSeeInput && w.ElapsedMs - ep.StartMs < FlashDialog.UnseenHoldMs
                && FlashDialog.HoldBeforeSeen(ep.StartMs, w.ElapsedMs, Native.ShownForegroundOfThisProcess(MainWindowHandle()) != IntPtr.Zero, ep.WindowSeen, ep.CanSeeInput, false);
        return FlashDialog.Young(ep.SeenMs, w.ElapsedMs, false, ep.HookOk);
    }

    /// <summary>A click or key press since this modal state began (the recorder's own count, and the dialog hook's, which sees a WinForms dialog's own loop).</summary>
    bool Touched(ModalEpisode ep) => _inputTick != ep.InputTick || _dlg.InputCount != ep.DlgInput;

    void OnLeaveModal(object? s, EventArgs e) => Log.Safe("LeaveModal", () =>
    {
        if (_modalOpen > 0) _modalOpen--;
        if (_ownModal > 0) { _ownModal--; return; }
        ModalEpisode? episode = null;
        if (_episodes.Count > 0) { episode = _episodes[^1]; _episodes.RemoveAt(_episodes.Count - 1); }
        if (_episodes.Count == 0) _modalTimer?.Stop();
        if (_modals.Leave())
        {
            if (_w != null && _dlg.LeaveAdopted(_w.ElapsedMs) is { } adopted) { EmitDialogValues(adopted); Emit("modal_leave", CurDoc()); }
            return;
        }
        // a dialog that opened and closed by itself within a fraction of a second, with nobody touching it: no step, no picture, one line in the log (FlashDialog)
        bool flash = _w != null && episode is { N: > 0 } && FlashDialog.IsFlash(episode.SeenMs, _w.ElapsedMs, Touched(episode), episode.HookOk);
        Log.Safe("dialog values", () =>
        {
            var r = _w == null ? null : _dlg.Close(_w.ElapsedMs);
            if (r == null || flash) return;
            EmitDialogValues(r);
        });
        if (flash)
        {
            _shots.CancelDialog(episode!.N, episode.TitleN);
            Log.Info($"dialog shown for {_w!.ElapsedMs - episode.SeenMs} ms with no click or key (under {FlashDialog.ThresholdMs} ms): not recorded as a step, no picture");
            Emit("modal_flash", CurDoc(), ("for", episode.N), ("ms", _w.ElapsedMs - episode.SeenMs));
            Emit("modal_leave", CurDoc());
            return;
        }
        // no window other than the main window showed while this modal state was open: say so, just before its leave (a dialog that shows late is written as any dialog is, and no marker)
        if (episode is { WindowlessAtLeave: true, N: > 0 }) Emit("modal_nowindow", CurDoc(), ("for", episode.N));
        Emit("modal_leave", CurDoc());
    });

    void EmitDialogValues(DialogWatch.Result r) =>
        Emit("dialog_values", CurDoc(), ("for", r.ForN), ("title", r.Title), ("source", r.Source), ("parent", r.ParentN), ("fields", FieldsJson(r.Changes)), ("read", r.Read),
            ("closedWith", r.ClosedWith), ("button", r.ClosedBy), ("closeTab", r.CloseTab), ("statics", DialogStatics.ToJson(r.Statics)), ("truncated", r.Truncated), ("unsupported", WpfPresses.PartsToJson(r.Parts)));

    /// <summary>A dialog a button of another dialog opened (AutoCAD raised no <c>EnterModal</c> for it): its <c>modal_enter</c>, with the title it has, and its picture.
    /// Returns the event's number, which its <c>dialog_values</c> names as <c>for</c>.</summary>
    int EmitNestedOpened(DialogWatch.Entry e)
    {
        var w = _w;
        if (w == null || _inOwnCmd || _ownModal > 0) return 0;
        var n = Emit("modal_enter", CurDoc(), ("title", e.Title), ("windowClass", Native.ClassOf(e.Hwnd)));
        if (n > 0) _shots.Request(ShotRules.Dialog(n, w.ElapsedMs, e.Hwnd.ToInt64()));
        return n;
    }

    /// <summary>The <c>fields</c> of a dialog or palette event; <c>tab</c> only for a field on a tab (a dialog without tabs is written as before).</summary>
    static List<Dictionary<string, object?>> FieldsJson(IEnumerable<FieldChange> changes) =>
        changes.Select(c =>
        {
            var d = new Dictionary<string, object?> { ["key"] = c.Key, ["label"] = c.Label, ["kind"] = c.Kind, ["before"] = c.Before, ["after"] = c.After };
            if (c.Tab.Length > 0) d["tab"] = c.Tab;
            if (c.Items != null) d["items"] = ListSelection.ToJson(c.Items);
            return d;
        }).ToList();

    /// <summary>A push button (not OK / Cancel) pressed in a dialog: the fields changed so far, as they were just before its handler ran.</summary>
    void EmitDialogClick(DialogWatch.Click c)
    {
        if (c.Menu != null)
        {
            // a context menu choice in the dialog: a click with the menu in place of the button (`context_menu`, see docs/recording-format.md)
            Emit("context_menu", CurDoc(), new (string, object?)[] { ("for", c.ForN) }.Concat(c.Menu.ToFields()).Concat(new (string, object?)[]
                { ("fields", FieldsJson(c.Changes)), ("read", c.Read), ("tab", c.Tab.Length > 0 ? c.Tab : null), ("statics", DialogStatics.ToJson(c.Statics)) }).ToArray());
            return;
        }
        EmitDialogButton(c);
    }

    /// <summary>A tree / list item clicked in a dialog: its event (the picture is taken once the click has settled, so the list a tree selection filled is in it) and the item's rectangle for the frame and arrow.</summary>
    void EmitDialogPick(DialogWatch.Pick p)
    {
        var w = _w;
        if (w == null || !_shots.Enabled) return;
        _shots.FlushDeferred();   // the click before it has settled by now
        var n = Emit("dialog_pick", CurDoc(), ("for", p.ForN), ("kind", p.Kind), ("label", p.Label), ("item", p.Item), ("row", p.Row > 0 ? p.Row : null));
        if (n <= 0) return;
        if (_shots.Arrows && p.Rect is { } r) _shots.NoteTarget(n, r.Left, r.Top, r.Right, r.Bottom);
        _shots.DeferDialog(n, p.Dialog, w.ElapsedMs + ShotRules.ClickSettleMs, null, p.Reread is { } re ? () => re() is { } rr ? (rr.Left, rr.Top, rr.Right, rr.Bottom) : null : null);
    }

    void EmitDialogButton(DialogWatch.Click c)
    {
        _shots.FlushDeferred();   // the click before it has settled by now
        var n = EmitDialogButtonEvent(c);
        var w = _w;
        if (n <= 0 || w == null || !_shots.Enabled || c.Dialog == IntPtr.Zero || c.Kind == DialogClickKinds.DoubleClick) return;
        // the picture of the click: taken now, before the button's handler runs (a button that closes the dialog is shown as it was pressed), and again once the dialog has settled when it is still there
        if (c.Rect is { } r) _shots.NoteTarget(n, r.Left, r.Top, r.Right, r.Bottom);
        // a button that acts as the mouse goes down and opens a nested dialog: that dialog is over this one by now, so there is no press-time picture (the settled one is not taken either: the dialog is covered)
        _shots.DeferDialog(n, c.Dialog, w.ElapsedMs + ShotRules.ClickSettleMs, Native.IsEnabled(c.Dialog) ? _shots.CaptureDialogNow(c.Dialog) : null, null);
    }

    int EmitDialogButtonEvent(DialogWatch.Click c) =>
        Emit("dialog_click", CurDoc(), ("for", c.ForN), ("button", c.Button), ("id", c.Id), ("index", c.Index), ("fields", FieldsJson(c.Changes)), ("read", c.Read),
            ("tab", c.Tab.Length > 0 ? c.Tab : null), ("statics", DialogStatics.ToJson(c.Statics)),
            ("kind", c.Kind != DialogClickKinds.Click ? c.Kind : null), ("onDown", c.OnDown ? true : null), ("tree", c.Tree.Length > 0 ? c.Tree : null), ("path", c.Path.Length > 0 ? c.Path : null),
            ("treeLabel", c.TreeLabel.Length > 0 ? c.TreeLabel : null));

    // ---- prompts -----------------------------------------------------------------------------

    readonly Dictionary<Editor, List<Action>> _edUnhook = new();

    static void Hook<T>(List<Action> undo, T handler, Action<T> add, Action<T> remove)
    {
        add(handler);
        undo.Add(() => remove(handler));
    }

    void HookEditor(Editor ed)
    {
        if (_edUnhook.ContainsKey(ed)) return;
        var u = new List<Action>();
        _edUnhook[ed] = u;
        Hook<PromptPointOptionsEventHandler>(u, (s, e) => Pre("point", e.Options), h => ed.PromptingForPoint += h, h => ed.PromptingForPoint -= h);
        Hook<PromptPointResultEventHandler>(u, (s, e) => Post("point", e.Result, r => Wcs(((PromptPointResult)r).Value), ed), h => ed.PromptedForPoint += h, h => ed.PromptedForPoint -= h);
        Hook<PromptPointOptionsEventHandler>(u, (s, e) => Pre("corner", e.Options), h => ed.PromptingForCorner += h, h => ed.PromptingForCorner -= h);
        Hook<PromptPointResultEventHandler>(u, (s, e) => Post("corner", e.Result, r => Wcs(((PromptPointResult)r).Value), ed), h => ed.PromptedForCorner += h, h => ed.PromptedForCorner -= h);
        Hook<PromptDistanceOptionsEventHandler>(u, (s, e) => Pre("distance", e.Options), h => ed.PromptingForDistance += h, h => ed.PromptingForDistance -= h);
        Hook<PromptDoubleResultEventHandler>(u, (s, e) => Post("distance", e.Result, r => ((PromptDoubleResult)r).Value, ed), h => ed.PromptedForDistance += h, h => ed.PromptedForDistance -= h);
        Hook<PromptAngleOptionsEventHandler>(u, (s, e) => Pre("angle", e.Options), h => ed.PromptingForAngle += h, h => ed.PromptingForAngle -= h);
        Hook<PromptDoubleResultEventHandler>(u, (s, e) => Post("angle", e.Result, r => ((PromptDoubleResult)r).Value, ed), h => ed.PromptedForAngle += h, h => ed.PromptedForAngle -= h);
        Hook<PromptDoubleOptionsEventHandler>(u, (s, e) => Pre("double", e.Options), h => ed.PromptingForDouble += h, h => ed.PromptingForDouble -= h);
        Hook<PromptDoubleResultEventHandler>(u, (s, e) => Post("double", e.Result, r => ((PromptDoubleResult)r).Value, ed), h => ed.PromptedForDouble += h, h => ed.PromptedForDouble -= h);
        Hook<PromptIntegerOptionsEventHandler>(u, (s, e) => Pre("integer", e.Options), h => ed.PromptingForInteger += h, h => ed.PromptingForInteger -= h);
        Hook<PromptIntegerResultEventHandler>(u, (s, e) => Post("integer", e.Result, r => ((PromptIntegerResult)r).Value, ed), h => ed.PromptedForInteger += h, h => ed.PromptedForInteger -= h);
        Hook<PromptStringOptionsEventHandler>(u, (s, e) => Pre("string", e.Options), h => ed.PromptingForString += h, h => ed.PromptingForString -= h);
        Hook<PromptStringResultEventHandler>(u, (s, e) => Post("string", e.Result, r => r.StringResult, ed), h => ed.PromptedForString += h, h => ed.PromptedForString -= h);
        Hook<PromptKeywordOptionsEventHandler>(u, (s, e) => Pre("keyword", e.Options), h => ed.PromptingForKeyword += h, h => ed.PromptingForKeyword -= h);
        Hook<PromptStringResultEventHandler>(u, (s, e) => Post("keyword", e.Result, r => r.StringResult, ed), h => ed.PromptedForKeyword += h, h => ed.PromptedForKeyword -= h);
        Hook<PromptEntityOptionsEventHandler>(u, (s, e) => Pre("entity", e.Options), h => ed.PromptingForEntity += h, h => ed.PromptingForEntity -= h);
        Hook<PromptEntityResultEventHandler>(u, (s, e) => Post("entity", e.Result, r => Entity(((PromptEntityResult)r).ObjectId, ((PromptEntityResult)r).PickedPoint), ed), h => ed.PromptedForEntity += h, h => ed.PromptedForEntity -= h);
        Hook<PromptNestedEntityOptionsEventHandler>(u, (s, e) => Pre("nestedEntity", e.Options), h => ed.PromptingForNestedEntity += h, h => ed.PromptingForNestedEntity -= h);
        Hook<PromptNestedEntityResultEventHandler>(u, (s, e) => Post("nestedEntity", e.Result, r => Entity(((PromptNestedEntityResult)r).ObjectId, ((PromptNestedEntityResult)r).PickedPoint), ed), h => ed.PromptedForNestedEntity += h, h => ed.PromptedForNestedEntity -= h);
        Hook<PromptSelectionOptionsEventHandler>(u, (s, e) => PreSel(e.Options), h => ed.PromptingForSelection += h, h => ed.PromptingForSelection -= h);
        Hook<PromptSelectionResultEventHandler>(u, (s, e) => PostSel(e.Result, ed), h => ed.PromptedForSelection += h, h => ed.PromptedForSelection -= h);
    }

    void UnhookEditor(Editor ed)
    {
        if (!_edUnhook.Remove(ed, out var undo)) return;
        foreach (var a in undo) Log.Safe("UnhookEditor", a);
    }

    /// <summary>The window of the active drawing (its views are children of it); not the ribbon, a palette or the command line.</summary>
    static IntPtr DrawingWindow()
    {
        try { return AcApp.DocumentManager.MdiActiveDocument?.Window?.Handle ?? IntPtr.Zero; } catch { return IntPtr.Zero; }
    }

    // Runs for every UI-thread message: keep it allocation-free.
    void OnPreTranslate(object? s, PreTranslateMessageEventArgs e)
    {
        var m = e.Message.message;
        if (m == 0x0200) { _activity++; return; }   // WM_MOUSEMOVE: somebody is at the mouse (a script that answers by itself moves nothing)
        if (CheckpointKey.TryHandle(m, e.Message.wParam, e.Message.lParam)) { e.Handled = true; return; }   // the shortcut is not user input
        if (m == 0x0201) { _downX = e.Message.pt_x; _downY = e.Message.pt_y; _downMsgTime = (uint)e.Message.time; _downInWpf = System.Windows.Interop.HwndSource.FromHwnd(e.Message.hwnd) != null; }   // where the person clicked, for the arrow of the step's picture
        if (m is 0x0100 or 0x0201 or 0x0202 or 0x0203 && _pal.Enabled) PaletteInput(e.Message.hwnd, (uint)m, e.Message.wParam, e.Message.lParam);
        if (m == 0x0100 && _w != null && !_inOwnCmd) { if (Native.TypedChar((uint)(long)e.Message.wParam) is { } typedChar) _sel.Key(typedChar); }   // WM_KEYDOWN (no WM_CHAR reaches this handler)
        if (m is 0x0201 or 0x0202 && _w != null && !_inOwnCmd && Native.IsInside(DrawingWindow(), e.Message.hwnd))
        {
            _viewMouse++;
            if (m == 0x0201 && Native.ShiftOrCtrlDown()) _sel.ClickModifier();   // a deselect, sub-object or cycling pick: its point cannot stand for it
        }
        if (m is 0x0201 or 0x0203 or 0x0204) NativeInput(e.Message.hwnd, (uint)m, e.Message.lParam);
        // WM_KEYDOWN, WM_L/R/MBUTTONDOWN. Shift, Ctrl and Alt alone answer nothing (pressed for the checkpoint shortcut, they made an
        // AutoCAD Electrical mouse-move point look typed)
        if (m is 0x0201 or 0x0204 or 0x0207 || (m == 0x0100 && (long)e.Message.wParam is not (0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5))))
        {
            _inputTick++;
            _lastInputMs = _w?.ElapsedMs ?? 0;
            _lastInputMouse = m != 0x0100;
            _activity++;
            _endTick = Core.DragRuns.EndsPrompt((uint)m, (long)e.Message.wParam) ? _inputTick : 0;   // the left button, Enter or Space ends a prompt; any other key, the middle (pan) and the right button do not
        }
    }

    /// <summary>A click or key press: records the values changed in a palette the user just left, and a button clicked in a palette.</summary>
    void PaletteInput(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var w = _w;
        if (w == null) return;
        try
        {
            if (message == 0x0202) _upSinceTree = true;
            if (message == 0x0202 && !_pal.HasPending) { _pal.NoteInput(hwnd); return; }   // a button-up: no click of its own (but the one a toolbar item waits for)
            if (message == 0x0201 && _shots.HasQueuedPaletteShots) _shots.Repaint(_pal.CurrentPage);   // what the click before changed is painted first (its close-up shows the new label, not the old one)
            if (message is 0x0201 or 0x0202 or 0x0203) _shots.FlushDeferred();   // the click before this one has settled by now: its picture is taken before this click is handled
            if (message == 0x0201) _shots.FlushPaletteWindows(w.ElapsedMs, _pal.CurrentPage);       // and its window picture too: it must show that click's result, not this one's (a stalled UI thread took it late)
            var (changed, at, button, index, picture) = _pal.OnInput(hwnd, message, wParam, lParam, w.ElapsedMs);
            _pal.NoteInput(hwnd);
            EmitPalette(changed, withPicture: button == null);   // values and the click are one step: the click carries the picture
            if (at == null || button == null) return;
            if (_pal.Double is { } dbl)
            {
                Emit("palette_click", CurDoc(), ("palette", at.Palette), ("set", at.Set), ("button", button), ("kind", "dblclick"), ("tree", dbl.Tree), ("treeLabel", dbl.TreeLabel), ("path", dbl.Path));
                _lastPaletteT = w.ElapsedMs; _lastPaletteTick = _inputTick; _lastPaletteButton = button; _lastPaletteConsumed = false;
                return;
            }
            if (_pal.Intermediate is { } ic)
            {
                // a click on a tree item, a +/- button, an image, a list view item or a control with no name: one step of its own, after the values changed so far
                var fields = new List<(string, object?)> { ("palette", at.Palette), ("set", at.Set), ("button", button) };
                fields.AddRange(ic.ToFields());
                if (ic.Kind == PaletteClickKinds.Tree) NoteTreeClick(ic.Path, w.ElapsedMs, _downMsgTime);
                int k = Emit("palette_click", CurDoc(), fields.ToArray());
                if (message is 0x0201 or 0x0202 or 0x0203) _shots.NoteClick(k, _downX, _downY);   // a double-click that opens a node: the point of its first button-down
                PaletteClickPicture(k, hwnd, lParam, at, picture, ic.Kind == PaletteClickKinds.Image, ic.Rect);
                _lastPaletteT = w.ElapsedMs; _lastPaletteTick = _inputTick; _lastPaletteButton = button; _lastPaletteConsumed = false;
                return;
            }
            var from = _pal.ButtonFrom;
            int? legacy = _pal.ButtonLegacyIndex > 0 ? _pal.ButtonLegacyIndex : null;
            int? noText = _pal.ButtonNoTextCount > 0 ? _pal.ButtonNoTextCount : null;
            int n = index > 1 ? Emit("palette_click", CurDoc(), ("palette", at.Palette), ("set", at.Set), ("button", button), ("buttonFrom", from), ("index", index), ("legacyIndex", legacy), ("noTextCount", noText))
                              : Emit("palette_click", CurDoc(), ("palette", at.Palette), ("set", at.Set), ("button", button), ("buttonFrom", from), ("legacyIndex", legacy), ("noTextCount", noText));
            if (message == 0x0201) _shots.NoteClick(n, _downX, _downY);
            PaletteClickPicture(n, hwnd, lParam, at, picture);
            _lastPaletteT = w.ElapsedMs; _lastPaletteTick = _inputTick; _lastPaletteButton = button; _lastPaletteConsumed = false;
        }
        catch (Exception ex) { Log.Error("palette input", ex); }
    }

    string? _lastTreePath;
    long _lastTreeT;
    uint _lastTreeMsgTime;
    bool _upSinceTree = true;   // a left button-up was seen since the last recorded tree click

    /// <summary>A tree click on the item that was clicked less than 1.5 s ago is recorded like any other (it can be a second click of a person), but leaves one line in retrace.log that tells a person's second click
    /// from a phantom one: how long after the first, and whether the button came up in between (a real second click has an up first).</summary>
    void NoteTreeClick(string path, long nowMs, uint msgTime)
    {
        if (_lastTreePath == path && nowMs - _lastTreeT < 1500)
            Log.Info($"tree click on \"{path}\" again {nowMs - _lastTreeT} ms after the last one was recorded; the two presses were {unchecked(msgTime - _lastTreeMsgTime)} ms apart by the system clock (a press made while the UI thread was busy is recorded when it gets to it); " +
                     $"left button released in between: {_upSinceTree}; both are recorded");
        _lastTreePath = path; _lastTreeT = nowMs; _lastTreeMsgTime = msgTime; _upSinceTree = false;
    }

    /// <summary>The palette's close-up for a click, taken once the click has settled (a list a tree click filled is in it); <paramref name="before"/>, the close-up kept while the person worked, is the fallback when the palette
    /// is gone by then. The clicked item's rectangle gives the frame and arrow of this picture and of the window picture.</summary>
    void PaletteClickPicture(int n, IntPtr hwnd, IntPtr lParam, PaletteWatch.Spot at, ShotService.Picture? before, bool pictureBox = false, ScreenRect? itemRect = null)
    {
        var w = _w;
        if (w == null) return;
        if (_shots.Arrows && (itemRect ?? ClickRects.Of(hwnd, lParam, pictureBox)) is { } r) _shots.NoteTarget(n, r.Left, r.Top, r.Right, r.Bottom);
        int modalAtClick = _modalOpen;   // a modal state that was already open at the click does not cover the palette; one that opened after it does
        // a click that opens a modal dialog: the palette is under it by then (taken at the EnterModal, or the one from before the click is used)
        bool smart = _shots.Smart;
        if (!smart && PaletteInsideMain(at.Page))
        {
            // classic: one picture of the whole window (the scheduler's, which has the frame and arrow noted above), never a second close-up; the close-up from the press is held and written only if that picture does not come
            _shots.ExpectWindowPicture(n, w.ElapsedMs + ShotRules.SettleMs, before);
            return;
        }
        // smart zoom: this one picture (the palette and its surroundings) is the click's picture; the window picture the scheduler would take is not needed when this one is certain (the one kept before the click, or the palette shows now).
        // Classic with a palette floating outside AutoCAD's window: the window picture does not show it, so the palette's own frame is the one picture
        if (before != null || SmartShots.PaletteArea(at.Page, smart) != null) _shots.Suppress(n);
        _shots.DeferRect(n, "palette-closeup", "palette", w.ElapsedMs + ShotRules.ClickSettleMs, () => _modalOpen <= modalAtClick ? SmartShots.PaletteArea(at.Page, smart) : null, before, null, at.Page);
    }

    /// <summary>Classic mode: the palette's window lies inside AutoCAD's window, so the whole-window picture shows it (<see cref="ClassicPalettePicture"/>); true also when either window cannot be read (the window picture is the rule).</summary>
    bool PaletteInsideMain(IntPtr page) =>
        TdocTargets.Of(page) is not { } p || TdocTargets.Of(MainWindowHandle()) is not { } m
        || Core.ClassicPalettePicture.InsideWindow(new Core.PxRect(p.Left, p.Top, p.Right, p.Bottom), new Core.PxRect(m.Left, m.Top, m.Right, m.Bottom));

    /// <summary>A context menu item was chosen (<see cref="MenuWatch"/>): in a tracked dialog it is written as a click of that dialog; in a palette the values changed so far go first, then the
    /// choice, which belongs to the palette step like a button click (a command it starts is linked to it).</summary>
    void EmitContextMenu(MenuChoice choice, MenuWatch.Armed armed)
    {
        var w = _w;
        if (w == null) return;
        if (armed.Spot == null) { _dlg.CommitMenu(choice, w.ElapsedMs); return; }
        EmitPalette(_pal.Flush(w.ElapsedMs, drop: false), withPicture: false);
        var n = Emit("context_menu", CurDoc(), new (string, object?)[] { ("palette", armed.Spot.Palette), ("set", armed.Spot.Set) }.Concat(choice.ToFields()).ToArray());
        _shots.Request(ShotRules.Palette(n, w.ElapsedMs));   // the window, as for a palette click, and the palette's close-up taken at the right-click
        if (armed.Picture != null)
        {
            if (!_shots.Smart && PaletteInsideMain(armed.Spot.Page)) _shots.ExpectWindowPicture(n, w.ElapsedMs + ShotRules.SettleMs, armed.Picture);   // classic: the window picture; the close-up only if that does not come
            else { _shots.Suppress(n); _shots.Write(n, "palette-closeup", armed.Picture, "palette"); }   // smart zoom, or classic with a palette outside AutoCAD's window: the close-up is the one picture
        }   // smart zoom: the crop is the one picture
        _lastPaletteT = w.ElapsedMs; _lastPaletteTick = _inputTick; _lastPaletteButton = choice.ButtonText; _lastPaletteConsumed = false;
    }

    /// <summary>A click in a native palette (AutoCAD Electrical's Project Manager): a tree item or a push button (<see cref="NativePalette"/>).</summary>
    void NativeInput(IntPtr hwnd, uint message, IntPtr lParam)
    {
        if (_w == null) return;
        try { EmitNative(NativePalette.OnClick(hwnd, message, lParam)); }
        catch (Exception ex) { Log.Error("native palette input", ex); }
    }

    void EmitNative(NativePalette.Input? a)
    {
        var w = _w;
        if (a == null || w == null) return;
        var n = Emit(a.Control == "combo" ? "native_select" : "native_click", CurDoc(), ("palette", a.Where.Palette), ("set", a.Where.Set), ("control", a.Control),
            ("action", a.Action), ("labelFrom", a.LabelFrom), ("path", a.Path), ("index", a.Index), ("id", a.Id), ("label", a.Label), ("value", a.Value), ("entry", a.Entry > 0 ? a.Entry : null),
            ("statics", DialogStatics.ToJson(NativeStatics(a.Where.Page))));
        NativePalette.Written(a, n, w.ElapsedMs);
        // a command the click starts (a toolbar button, the entry picked from a right-click menu) belongs to this step, as with .NET palettes
        _lastPaletteT = w.ElapsedMs; _lastPaletteTick = _inputTick; _lastPaletteButton = a.Path?.LastOrDefault() ?? a.Label ?? a.Value; _lastPaletteConsumed = false;
    }

    /// <summary>The static texts the native palette page shows now (the click that is being written has not been handled yet); none when it cannot be read.</summary>
    static List<DialogStatic> NativeStatics(IntPtr page)
    {
        try { return Native.Exists(page) ? DialogReader.ReadStatics(page) : new List<DialogStatic>(); }
        catch (Exception ex) { Log.Error("native palette statics", ex); return new List<DialogStatic>(); }
    }

    /// <summary>Timer tick, checkpoint and Stop: a drop-down entry picked with the keyboard, and the name of an entry picked from a list that holds no text.</summary>
    void NativeTick(bool force)
    {
        var w = _w;
        if (w == null) return;
        var (select, value) = NativePalette.Tick(_modalOpen > 0, w.ElapsedMs, force);
        EmitNative(select);
        if (value is { } v) Emit("native_value", CurDoc(), ("for", v.For), ("value", v.Value));
    }

    void EmitPalette(PaletteWatch.Values? v, bool withPicture = true)
    {
        if (v == null) return;
        var fields = FieldsJson(v.Changes);
        var n = Emit("palette_values", CurDoc(), ("palette", v.Where.Palette), ("set", v.Where.Set), ("source", v.Source), ("fields", fields), ("read", v.Read));
        if (withPicture && v.Picture != null)
        {
            if (!_shots.Smart && PaletteInsideMain(v.Where.Page)) _shots.ExpectWindowPicture(n, (_w?.ElapsedMs ?? 0) + ShotRules.SettleMs, v.Picture);   // classic: the window picture; the close-up only if that does not come
            else { _shots.Suppress(n); _shots.Write(n, "palette-closeup", v.Picture, "palette"); }   // smart zoom, or classic with a palette outside AutoCAD's window: the close-up is the one picture
        }
    }

    /// <summary>true if a click/key press within 500 ms led to this result and no earlier result claimed it. Sets <see cref="_claimedBy"/>.</summary>
    bool ClaimInput(bool dragSample = false)
    {
        var now = _w?.ElapsedMs ?? 0;
        _claimedBy = null;
        if (_inputTick == _claimedTick || now - _lastInputMs > 500) return false;
        if (dragSample && _endTick != _inputTick) return false;   // a key typed on the way: the sample it would claim is not the answer
        _claimedTick = _inputTick;
        _claimedBy = _lastInputMouse ? "mouse" : "keys";
        return true;
    }

    void Pre(string kind, PromptOptions? o)
    {
        if (_w == null) return;
        Log.Safe("Prompting " + kind, () =>
        {
            // Only the message: reading o.Keywords here made AutoCAD reject mouse picks and re-ask in a loop (seen in R25.1).
            _promptMsg[kind] = o?.Message;
            _lastPromptMsg = o?.Message;
            _promptKeywords[kind] = KeywordsOf(o?.Message);
            _promptSeq[kind] = ++_seq;
            _kinds.Add(kind);
        });
    }

    /// <summary>Keywords as shown in the prompt text: "Specify ... or [3P/2P/Ttr (tan tan radius)]:" gives 3P, 2P, Ttr.</summary>
    public static string[]? KeywordsOf(string? message)
    {
        if (message == null) return null;
        int open = message.LastIndexOf('['), close = message.LastIndexOf(']');
        if (open < 0 || close < open) return null;
        var kws = message[(open + 1)..close].Split('/')
            .Select(k => k.Split(' ', '(')[0].Trim()).Where(k => k.Length > 0).ToArray();
        return kws.Length > 0 ? kws : null;
    }

    void PreSel(PromptSelectionOptions? o)
    {
        if (_w == null) return;
        Log.Safe("Prompting selection", () =>
        {
            _promptMsg["selection"] = o?.MessageForAdding;
            _lastPromptMsg = o?.MessageForAdding;
            _promptKeywords["selection"] = KeywordsOf(o?.MessageForAdding);
            _kinds.Add("selection");
        });
    }

    /// <summary>Closes one open prompt; true if another prompt is still open around it (the result is nested).</summary>
    bool ClosePrompt()
    {
        if (_kinds.Count > 0) _kinds.RemoveAt(_kinds.Count - 1);
        return _kinds.Count > 0;
    }

    void Post(string kind, PromptResult? r, Func<PromptResult, object?> value, Editor ed)
    {
        if (_w == null || r == null) return;
        if (!_inOwnCmd && r.Status == PromptStatus.OK && kind is "point" or "corner" && value(r) is double[] pt
            && !(kind == "point" && _kinds.Count >= 2 && _kinds[^2] == "corner"))   // GetCorner's inner point: the corner result that follows is the answer
        {
            // A point is typed (its line is already in the keys), or a click in the drawing window (a left button went down or up there since the last
            // click, and the newest input was the mouse), or neither, which spoils the selection's text (see TypedSelection.Point).
            var ppr = r as PromptPointResult;
            bool click = ppr != null && _lastInputMouse && _viewMouse != _viewMouseAtPoint;
            if (click) _viewMouseAtPoint = _viewMouse;
            _sel.Point(pt, ppr == null ? null : TypedSelection.PointLine(ppr.Value.X, ppr.Value.Y, ppr.Value.Z), click);
        }
        if (ClosePrompt()) return; // nested inside another open prompt
        if (_inOwnCmd) return; // e.g. RETRACEMARK's note: not part of the user's path
        Log.Safe("Prompted " + kind, () =>
        {
            object? v = null;
            if (r.Status == PromptStatus.OK) v = value(r);
            _promptMsg.TryGetValue(kind, out var msg);
            // The typed text belongs to the selection result that follows it. A point or corner that this prompt's text does not show to be
            // part of a selection ("Specify base point:") ends it, like every other kind of answer.
            // A top-level corner is selection input only at the "opposite corner" prompt of a selection; RECTANG's and a custom GetCorner's end the text.
            bool selectionInput = kind == "corner" ? (msg ?? "").Contains("opposite corner", StringComparison.OrdinalIgnoreCase)
                : kind == "point" && SelectionReplay.IsSelectionInput(new Answer(kind, "", null, null, msg));
            if (!selectionInput) _sel.OtherAnswer();
            _promptKeywords.TryGetValue(kind, out var kws);
            // a result of the same kind and message right after the one before is a candidate drag sample (see _endTick)
            bool ok = r.Status == PromptStatus.OK;
            var nowMs = _w?.ElapsedMs ?? 0;
            bool candidate = ok && _haveSample && _lastSampleKind == kind && _lastSampleMsg == msg && _lastSampleDoc == _cmdDoc;
            long prevMs = _lastSampleMs, activityAtPrev = _activityAtSample;
            _haveSample = ok; _lastSampleKind = kind; _lastSampleMsg = msg; _lastSampleDoc = _cmdDoc; _lastSampleMs = nowMs; _activityAtSample = _activity;
            var claimed = ClaimInput(candidate);
            bool burst = Core.DragRuns.IsDragSample(candidate, claimed, nowMs - prevMs, _activity != activityAtPrev, _ctxLispDepth > 0);
            var pn = Emit("prompt", _cmdDoc,
                ("kind", kind), ("seq", _promptSeq.TryGetValue(kind, out var q) ? q : null), ("input", claimed), ("by", _claimedBy), ("drag", candidate ? burst : null), ("message", msg?.Trim()), ("keywords", kws is { Length: > 0 } ? kws : null),
                ("status", r.Status.ToString()), ("value", v),
                ("keyword", r.Status == PromptStatus.Keyword ? r.StringResult : null));
            NotePickView(pn, v);
        });
    }

    void PostSel(PromptSelectionResult? r, Editor ed)
    {
        if (_w == null || r == null) return;
        var (typed, clicks) = _sel.Take();   // starts the text over
        if (ClosePrompt()) return; // nested inside another open prompt
        Log.Safe("Prompted selection", () =>
        {
            _promptMsg.TryGetValue("selection", out var msg);
            _promptKeywords.TryGetValue("selection", out var kws);
            object? value = null;
            if (r.Status == PromptStatus.OK && r.Value != null)
            {
                var ids = r.Value.GetObjectIds();
                var handles = ids.Select(HandleOf).ToArray();
                string? method = null;
                try { method = r.Value.Count > 0 ? r.Value[0].SelectionMethod.ToString() : null; } catch { }
                value = new Dictionary<string, object?>
                {
                    ["count"] = ids.Length,
                    ["handles"] = handles.Take(200).ToArray(),
                    ["dxf"] = ids.Take(200).Select(DxfOf).ToArray(),
                    ["method"] = method,
                };
                // `typed`: only when the typed text alone makes the whole selection. `clicks`: the same text with the mouse clicks as point lines, when a click took part
                // and the text makes the whole selection (no Shift / Ctrl click, no half-typed line); the replay uses it only where handles cannot express the selection.
                if (typed != null) ((Dictionary<string, object?>)value)["typed"] = typed;
                if (clicks != null)
                {
                    ((Dictionary<string, object?>)value)["clicks"] = clicks;
                    if (_view != null) ((Dictionary<string, object?>)value)["view"] = _view.ToJson();   // the view the clicks were made in: the replay shows it before it types them
                }
            }
            var sn = Emit("prompt", _cmdDoc,
                ("kind", "selection"), ("input", ClaimInput()), ("by", _claimedBy), ("message", msg?.Trim()), ("keywords", kws is { Length: > 0 } ? kws : null),
                ("status", r.Status.ToString()), ("value", value));
            NotePickView(sn, value);
        });
    }

    static double[] Pt(Point3d p) => new[] { p.X, p.Y, p.Z };

    /// <summary>PromptPointResult.Value is in the current UCS; the session stores WCS.</summary>
    double[] Wcs(Point3d p) => Pt(p.TransformBy(_ucs));

    static string HandleOf(ObjectId id) { try { return id.Handle.Value.ToString("X", CultureInfo.InvariantCulture); } catch { return "?"; } }
    static string DxfOf(ObjectId id) { try { return id.ObjectClass.DxfName; } catch { return "?"; } }

    /// <summary>An entity answer: the object, the pick point (as AutoCAD reports it) and the view it was picked in.</summary>
    object Entity(ObjectId id, Point3d picked)
    {
        var d = new Dictionary<string, object?>
        {
            ["handle"] = id.IsNull ? null : HandleOf(id),
            ["dxf"] = id.IsNull ? null : DxfOf(id),
            ["pick"] = Pt(picked),
        };
        if (_view != null) d["view"] = _view.ToJson();
        return d;
    }

    // ---- marks -------------------------------------------------------------------------------

    /// <summary>The behavior snapshot of the drawing (command / timer context only). Null when it could not be read at all. The opt-in behaviors
    /// are in it only when the settings ask for them (<see cref="RetraceSettings.BehaviorsToRecord"/>).</summary>
    public BehaviorSnapshot? Capture(Document doc, RetraceSettings settings) =>
        BehaviorCapture.Read(doc, settings, settings.BehaviorsToRecord());

    /// <summary>Snapshot files are a convenience for the Checkpoints window: a failure is logged and never stops the recording.</summary>
    static void WriteSnapshot(BehaviorSnapshot snap, string path)
    {
        if (!snap.Write(path, out var err)) Log.Info($"snapshot not written ({path}): {err}");
    }

    /// <summary>Writes a checkpoint. Its automatic name (see <see cref="Checkpoints.AutoName"/>) is stored as `autoName`, and as the `note`
    /// unless <paramref name="note"/> gives one. Returns the name used, or null when the event could not be written (nothing counted).</summary>
    /// <param name="at">what the checkpoint shortcut found at the cursor (<see cref="CursorContext"/>): names the checkpoint before the last action does</param>
    /// <param name="doc">the document the checkpoint is for; its behaviors are captured now (command / timer context) and compared with ITS previous
    /// snapshot (a drawing the recording has not seen before has none: its first checkpoint then has empty `checks`). Null = no checks.</param>
    public string? Mark(string? note, DrawingDigest? state, Checkpoints.CheckpointContext? at = null, Document? doc = null)
    {
        var settings = RetraceSettings.Load();
        var snapshot = doc == null ? null : Capture(doc, settings);
        // values set in a palette just before the checkpoint (the shortcut pressed while still in it) come first
        if (_w is { } w) Log.Safe("mark palette", () => { EmitPalette(_pal.Flush(w.ElapsedMs, drop: false)); NativeTick(true); });
        MarkPending = false;   // any checkpoint (the ribbon button too) answers a pending shortcut press
        var auto = Checkpoints.AutoName(_marks + 1, at ?? _lastCtx);
        var name = Checkpoints.Clean(note);
        if (name.Length == 0) name = auto;
        BehaviorChecks? checks = null;
        if (snapshot != null)
        {
            _snaps.TryGetValue(doc!, out var previous);
            checks = settings.AutoDetectBehavior ? BehaviorChecks.AutoPick(previous, snapshot, m => Log.Info(m)) : BehaviorChecks.ManualPick(settings.EffectiveCheckBehaviors(), snapshot);
        }
        var n = Emit("mark", CurDoc(), ("note", name), ("autoName", auto), ("state", state), ("checks", checks), ("near", at?.Description));
        if (n == 0) return null;
        _marks++;
        if (snapshot != null && _w is { } sw)
        {
            _snaps[doc!] = snapshot;
            _needSnap.Remove(doc!);
            WriteSnapshot(snapshot, BehaviorSnapshot.MarkPath(Path.GetDirectoryName(sw.Path)!, n));
        }
        return name;
    }
}
