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

using Retrace.Engine.Recordings;

namespace Retrace.AutoCad;

/// <summary>Subscribes to ribbon, command, prompt, dialog and document events and writes them to a SessionWriter.
/// Every handler is wrapped: a failure is logged to retrace.log and never reaches AutoCAD.</summary>
public sealed class Recorder
{
    public static readonly Recorder Instance = new();

    SessionWriter? _w;
    readonly RibbonMap _map = new();
    readonly HashSet<Document> _hooked = new();
    readonly Dictionary<string, string?> _promptMsg = new();
    readonly Dictionary<string, string[]?> _promptKeywords = new();
    // one number per Prompting* event, stamped on its Prompted* results: AutoCAD fires many results per prompt as the mouse moves
    readonly Dictionary<string, int> _promptSeq = new();
    int _seq;
    // Prompts open right now. AutoCAD nests prompts (GetCorner runs a point prompt inside the corner prompt; GetDistance a point
    // prompt inside the distance prompt), each raising its own Prompted* event; only the outermost result is the user's answer.
    int _openPrompts;
    // Real input vs noise: in AutoCAD Electrical 2026 something requests a point on every mouse move while a command waits
    // (each with its own Prompting event, echoing the command's message). A result counts as user input only if a click or
    // key press reached AutoCAD's message loop shortly before it and no earlier result has claimed that press.
    long _inputTick, _claimedTick, _lastInputMs = -100000;
    bool _lastInputMouse;   // the newest input was a mouse button (a pick), else a key (typed value + Enter)
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
    int _modalOpen;   // EnterModal minus LeaveModal: a dialog picture is only taken while one is open

    // checkpoint names: where the last thing was done (a dialog, a ribbon button, a typed command) and how many checkpoints so far
    Checkpoints.CheckpointContext? _lastCtx;
    int _marks;

    // origin attribution: the last ribbon click that has not been matched to a command yet
    long _lastRibbonT = -100000;
    string? _lastRibbonId, _lastRibbonMacro;
    bool _lastRibbonConsumed = true;

    // Captured when a command starts. Prompt handlers must not call into the Editor/Document: with mouse picks that made
    // AutoCAD re-issue the prompt in a loop (LINE took no points, the cursor flickered until Esc; seen in R25.1).
    bool _inOwnCmd;
    Matrix3d _ucs = Matrix3d.Identity;
    string? _cmdDoc;
    string? _lastTabId;

    public bool IsRecording => _w != null;
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
        _w = new SessionWriter(string.IsNullOrWhiteSpace(name) ? "test" : name!, header);
        BeginShots();
        _promptMsg.Clear();
        _openPrompts = 0;
        _lastCtx = null;
        _marks = 0;
        _lastRibbonConsumed = true;
        // initial drawing state, right after the header; read here (command/idle context), never in an event handler
        Log.Safe("initial state", () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var digest = doc == null ? null : DrawingState.Read(doc);
            if (digest != null) Emit("state", CurDoc(), ("state", digest));
        });
        Subscribe();
        SnapshotDocs();
        return _w.Path;
    }

    /// <summary>Unsubscribes everything and closes the file WITHOUT a session_end event (AutoCAD is closing).
    /// Returns the session path, or null if not recording.</summary>
    public string? Detach()
    {
        var w = _w;
        Unsubscribe();
        if (w == null) return null;
        _shots.Drain(2000);   // pictures taken so far get their `shot` events; pending requests are dropped
        _shots.End();
        _w = null;
        var path = w.Path;
        w.Dispose();
        return path;
    }

    public (string Path, int Events, long Ms)? Stop()
    {
        var w = _w;
        if (w == null) return null;
        // the pictures still queued (the last command's result) are taken now, and their `shot` events must land before session_end
        Log.Safe("Stop shots", () => { _shots.Pump(w.ElapsedMs, force: true); _shots.Drain(); });
        Log.Safe("Stop", () => w.Event("session_end", CurDoc()));
        Unsubscribe();
        _shots.End();
        _w = null;
        var r = (w.Path, w.Events, w.ElapsedMs);
        w.Dispose();
        return r;
    }

    static string? Try(Func<string?> f) { try { return f(); } catch { return null; } }
    static string? CurDoc() { try { var d = AcApp.DocumentManager.MdiActiveDocument; return d == null ? null : Path.GetFileName(d.Name); } catch { return null; } }
    static string? DocName(Document? d) { try { return d == null ? null : Path.GetFileName(d.Name); } catch { return null; } }

    /// <summary>Writes one event and queues its screenshot request (if the type gets one). Returns the event's n, 0 if not written.</summary>
    int Emit(string type, string? doc, params (string, object?)[] f)
    {
        var w = _w;
        if (w == null) return 0;
        try
        {
            var n = w.Event(type, doc, f);
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
        switch (type)
        {
            case "ribbon": _lastCtx = new Checkpoints.CheckpointContext("ribbon", F("panel") ?? F("tab"), F("text") ?? F("id")); break;
            case "cmd_start" when F("origin") != "ribbon": _lastCtx = new Checkpoints.CheckpointContext("command", null, F("command")); break;
            case "lisp_start": _lastCtx = new Checkpoints.CheckpointContext("command", null, "lisp"); break;
            case "modal_title": _lastCtx = new Checkpoints.CheckpointContext("dialog", F("title"), null); break;
            case "dialog_values": _lastCtx = new Checkpoints.CheckpointContext("dialog", F("title") ?? _lastCtx?.Container, F("closedWith")); break;
        }
    }

    // ---- screenshots -------------------------------------------------------------------------------------------------

    /// <summary>Called from RETRACERECORD / the resume, i.e. command or init context, where reading the main window handle is fine.</summary>
    void BeginShots()
    {
        var w = _w!;
        _modalOpen = 0;
        var settings = RetraceSettings.Load();
        var enabled = settings.Screenshots;
        _dlg.Clear();
        _dlg.Enabled = settings.DialogValues;
        _dlg.Clock = () => _w?.ElapsedMs ?? 0;
        _dlg.Confirmed = (n, h) => _shots.DialogAfter(n, h);
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

    void StartShotTimer()
    {
        if (_shotTimer != null) return;
        var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) => Log.Safe("shot timer", () => { var w = _w; if (w != null) { _shots.Pump(w.ElapsedMs); _dlg.Tick(w.ElapsedMs); } });
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
            foreach (var d in _hooked.ToList()) UnhookDocument(d);
            UnhookRibbon();
            _modalTimer?.Stop();
            _modalTimer = null;
            _shotTimer?.Stop();
            _shotTimer = null;
            _dlg.Clear();   // also removes the dialog message hook
        });
    }

    void OnIdle(object? s, EventArgs e)
    {
        if (_hookedRibbon == null) TryHookRibbon();
        if (_w == null) return;
        if (_shots.NeedsMainWindow) Log.Safe("main window", () => _shots.SetMainWindow(MainWindowHandle()));
        Log.Safe("shot idle", () => _shots.Pump(_w.ElapsedMs));
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
            Emit("ribbon", CurDoc(),
                ("id", item.Id), ("text", RibbonMap.Label(item)), ("name", item.Name),
                ("macro", macro), ("itemType", item.GetType().Name),
                ("tabId", tab?.Id ?? loc?.TabId), ("tab", tab?.Title ?? loc?.TabTitle),
                ("panelId", panel?.Source?.Id ?? loc?.PanelId), ("panel", panel?.Source?.Title ?? loc?.PanelTitle), ("path", loc?.Path),
                ("parent", e.Parent == null ? null : RibbonMap.Label(e.Parent)),
                ("activeTab", _hookedRibbon?.ActiveTab?.Title));
        });
    }

    // ---- documents and commands --------------------------------------------------------------

    void OnDocActivated(object? s, DocumentCollectionEventArgs e) => Log.Safe("DocActivated", () => { HookDocument(e.Document); Emit("doc_active", DocName(e.Document)); });
    void OnDocCreated(object? s, DocumentCollectionEventArgs e) => Log.Safe("DocCreated", () => { HookDocument(e.Document); Emit("doc_created", DocName(e.Document)); });
    void OnDocToBeDestroyed(object? s, DocumentCollectionEventArgs e) => Log.Safe("DocToBeDestroyed", () => { Emit("doc_closing", DocName(e.Document)); UnhookDocument(e.Document); });

    void HookDocument(Document d)
    {
        if (d == null || !_hooked.Add(d)) return;
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
        if (d == null || !_hooked.Remove(d)) return;
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

    void OnCmdStart(object? s, CommandEventArgs e) => Log.Safe("CommandWillStart", () =>
    {
        var name = e.GlobalCommandName;
        _openPrompts = 0;
        if (IsOurs(name)) { _inOwnCmd = true; return; }
        _cmdDoc = DocName(s as Document);
        _claimedTick = _inputTick; // the click/Enter that started the command answers no prompt
        try { if (s is Document d) _ucs = d.Editor.CurrentUserCoordinateSystem; } catch { _ucs = Matrix3d.Identity; }
        var (origin, via) = Attribute(name);
        Emit("cmd_start", DocName(s as Document), ("command", name), ("origin", origin), ("via", via));
    });

    void OnCmdEnded(object? s, CommandEventArgs e) => Log.Safe("CommandEnded", () => { _openPrompts = 0; if (IsOurs(e.GlobalCommandName)) _inOwnCmd = false; else Emit("cmd_end", DocName(s as Document), ("command", e.GlobalCommandName)); });
    void OnCmdCancelled(object? s, CommandEventArgs e) => Log.Safe("CommandCancelled", () => { _openPrompts = 0; if (IsOurs(e.GlobalCommandName)) _inOwnCmd = false; else Emit("cmd_cancel", DocName(s as Document), ("command", e.GlobalCommandName)); });
    void OnCmdFailed(object? s, CommandEventArgs e) => Log.Safe("CommandFailed", () => { _openPrompts = 0; if (IsOurs(e.GlobalCommandName)) _inOwnCmd = false; else Emit("cmd_fail", DocName(s as Document), ("command", e.GlobalCommandName)); });
    void OnUnknown(object? s, UnknownCommandEventArgs e) => Log.Safe("UnknownCommand", () => Emit("cmd_unknown", DocName(s as Document), ("command", e.GlobalCommandName)));
    void OnLispStart(object? s, LispWillStartEventArgs e) => Log.Safe("LispWillStart", () => { _cmdDoc = DocName(s as Document); Emit("lisp_start", _cmdDoc, ("firstLine", e.FirstLine)); });
    void OnLispEnded(object? s, EventArgs e) => Log.Safe("LispEnded", () => Emit("lisp_end", DocName(s as Document)));
    void OnLispCancelled(object? s, EventArgs e) => Log.Safe("LispCancelled", () => Emit("lisp_cancel", DocName(s as Document)));

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

    void OnEnterModal(object? s, EventArgs e) => Log.Safe("EnterModal", () =>
    {
        _modalOpen++;
        var fg = Native.Foreground();
        var nEnter = Emit("modal_enter", CurDoc(), ("title", fg?.Title), ("windowClass", fg?.Class));
        var watch = nEnter > 0 ? _dlg.Push(nEnter) : null;
        // the dialog is often not foreground yet at this instant: look again shortly after
        _modalTimer?.Stop();
        var disp = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, disp) { Interval = TimeSpan.FromMilliseconds(300) };
        _modalTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log.Safe("ModalTitle", () =>
            {
                var f2 = Native.Foreground();
                var shotFor = nEnter;   // the dialog picture belongs to modal_title when a title arrives, else to modal_enter
                var title = fg?.Title;
                if (f2 != null && (f2.Value.Title != fg?.Title || f2.Value.Class != fg?.Class))
                {
                    var nTitle = Emit("modal_title", CurDoc(), ("title", f2.Value.Title), ("windowClass", f2.Value.Class));
                    if (nTitle > 0) { shotFor = nTitle; title = f2.Value.Title; }
                }
                if (watch != null && _w != null)
                {
                    var dlgWnd = Native.ForegroundOfThisProcess();   // the dialog: the foreground window of this process that is not the main window
                    if (dlgWnd != IntPtr.Zero && dlgWnd != MainWindowHandle()) _dlg.Resolve(watch, dlgWnd, shotFor, title, _w.ElapsedMs);
                }
                if (shotFor > 0 && _w != null) _shots.Request(ShotRules.Dialog(shotFor, _w.ElapsedMs));
            });
        };
        timer.Start();
    });

    void OnLeaveModal(object? s, EventArgs e) => Log.Safe("LeaveModal", () =>
    {
        if (_modalOpen > 0) _modalOpen--;
        Log.Safe("dialog values", () =>
        {
            var r = _w == null ? null : _dlg.Close(_w.ElapsedMs);
            if (r == null) return;
            var fields = r.Changes.Select(c => new Dictionary<string, object?> { ["key"] = c.Key, ["label"] = c.Label, ["kind"] = c.Kind, ["before"] = c.Before, ["after"] = c.After }).ToList();
            Emit("dialog_values", CurDoc(), ("for", r.ForN), ("title", r.Title), ("source", r.Source), ("fields", fields), ("read", r.Read), ("closedWith", r.ClosedWith));
        });
        Emit("modal_leave", CurDoc());
    });

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

    // Runs for every UI-thread message: keep it allocation-free.
    void OnPreTranslate(object? s, PreTranslateMessageEventArgs e)
    {
        var m = e.Message.message;
        if (m is 0x0100 or 0x0201 or 0x0204 or 0x0207) // WM_KEYDOWN, WM_L/R/MBUTTONDOWN
        {
            _inputTick++;
            _lastInputMs = _w?.ElapsedMs ?? 0;
            _lastInputMouse = m != 0x0100;
        }
    }

    /// <summary>true if a click/key press within 500 ms led to this result and no earlier result claimed it. Sets <see cref="_claimedBy"/>.</summary>
    bool ClaimInput()
    {
        var now = _w?.ElapsedMs ?? 0;
        _claimedBy = null;
        if (_inputTick == _claimedTick || now - _lastInputMs > 500) return false;
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
            _promptKeywords[kind] = KeywordsOf(o?.Message);
            _promptSeq[kind] = ++_seq;
            _openPrompts++;
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
            _promptKeywords["selection"] = KeywordsOf(o?.MessageForAdding);
            _openPrompts++;
        });
    }

    /// <summary>Closes one open prompt; true if another prompt is still open around it (the result is nested).</summary>
    bool ClosePrompt()
    {
        if (_openPrompts > 0) _openPrompts--;
        return _openPrompts > 0;
    }

    void Post(string kind, PromptResult? r, Func<PromptResult, object?> value, Editor ed)
    {
        if (_w == null || r == null) return;
        if (ClosePrompt()) return; // nested inside another open prompt
        if (_inOwnCmd) return; // e.g. RETRACEMARK's note: not part of the user's path
        Log.Safe("Prompted " + kind, () =>
        {
            object? v = null;
            if (r.Status == PromptStatus.OK) v = value(r);
            _promptMsg.TryGetValue(kind, out var msg);
            _promptKeywords.TryGetValue(kind, out var kws);
            Emit("prompt", _cmdDoc,
                ("kind", kind), ("seq", _promptSeq.TryGetValue(kind, out var q) ? q : null), ("input", ClaimInput()), ("by", _claimedBy), ("message", msg?.Trim()), ("keywords", kws is { Length: > 0 } ? kws : null),
                ("status", r.Status.ToString()), ("value", v),
                ("keyword", r.Status == PromptStatus.Keyword ? r.StringResult : null));
        });
    }

    void PostSel(PromptSelectionResult? r, Editor ed)
    {
        if (_w == null || r == null) return;
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
            }
            Emit("prompt", _cmdDoc,
                ("kind", "selection"), ("input", ClaimInput()), ("by", _claimedBy), ("message", msg?.Trim()), ("keywords", kws is { Length: > 0 } ? kws : null),
                ("status", r.Status.ToString()), ("value", value));
        });
    }

    static double[] Pt(Point3d p) => new[] { p.X, p.Y, p.Z };

    /// <summary>PromptPointResult.Value is in the current UCS; the session stores WCS.</summary>
    double[] Wcs(Point3d p) => Pt(p.TransformBy(_ucs));

    static string HandleOf(ObjectId id) { try { return id.Handle.Value.ToString("X", CultureInfo.InvariantCulture); } catch { return "?"; } }
    static string DxfOf(ObjectId id) { try { return id.ObjectClass.DxfName; } catch { return "?"; } }

    static object Entity(ObjectId id, Point3d picked) => new Dictionary<string, object?>
    {
        ["handle"] = id.IsNull ? null : HandleOf(id),
        ["dxf"] = id.IsNull ? null : DxfOf(id),
        ["pick"] = Pt(picked),
    };

    // ---- marks -------------------------------------------------------------------------------

    /// <summary>Writes a checkpoint. Its automatic name (see <see cref="Checkpoints.AutoName"/>) is stored as `autoName`, and as the `note`
    /// unless <paramref name="note"/> gives one. Returns the name used, or null when the event could not be written (nothing counted).</summary>
    public string? Mark(string? note, DrawingDigest? state)
    {
        var auto = Checkpoints.AutoName(_marks + 1, _lastCtx);
        var name = Checkpoints.Clean(note);
        if (name.Length == 0) name = auto;
        if (Emit("mark", CurDoc(), ("note", name), ("autoName", auto), ("state", state)) == 0) return null;
        _marks++;
        return name;
    }
}
