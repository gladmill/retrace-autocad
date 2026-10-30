using Retrace.Engine.Recordings;
using Retrace.Engine;
using System.Diagnostics;
using Retrace.Ui;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

// Ribbon-driven commands: RETRACERUN, RETRACESTEP, RETRACEDOCS, RETRACEGUIDE, RETRACESETTINGS, RETRACEFOLDER, RETRACESTEPS.
// Rules kept from testing: no Editor/Document access inside prompt handlers; digests only in command or idle/timer context;
// nothing here leaves a prompt or dialog open in an unattended path (RETRACEREPLAY shows no alert).
public sealed partial class Commands
{
    sealed class PendingTest
    {
        public required Document Doc;
        public required List<Step> Steps;
        public required string Path;
        public required DrawingDigest? Initial;
        public DateTime Created = DateTime.UtcNow;
        public TdocCapture? Tdoc;   // set by a Tdoc run (RETRACETDOC) for the test it sent here
    }

    PendingTest? _pending;
    bool _testIdleHooked;

    void ShutdownCommands()
    {
        if (_testIdleHooked) { AcApp.Idle -= OnTestIdle; _testIdleHooked = false; }
        _pending = null;
        TdocRun.Abort();
        CloseTdoc();
        CloseOpenTest();
        CloseCheckpoints();
        CloseMap();
        EngineJobs.Cancel();
    }

    static bool FileDialogsOn() => Ask.FileDialogsOn();

    // ---- session picking ------------------------------------------------------------------------------------------

    /// <summary>File dialog starting in the tests folder; with FILEDIA=0 a command-line prompt ('last' = newest); with promptStyle "ui"
    /// the prompt window, as RETRACEREPLAY asks (<see cref="AskSession"/>).</summary>
    static string? PickSession(Editor ed)
    {
        var dir = RetraceSettings.Load().Tests;
        Directory.CreateDirectory(dir);
        if (Ask.InWindow()) return AskSession(ed);
        if (!FileDialogsOn())
        {
            var r = Ask.String(ed, "\nRecording ('last' = newest): ");
            if (r.Status != PromptStatus.OK) return null;
            var s = r.StringResult.Trim().Trim('"');
            if (s.Equals("last", StringComparison.OrdinalIgnoreCase)) return TestFolders.Newest(dir);
            return s.Length == 0 ? null : s;
        }
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose recording.jsonl in a test folder", Filter = "Retrace recordings (*.jsonl)|*.jsonl", InitialDirectory = dir };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    // ---- RETRACERUN / step through -------------------------------------------------------------------------------

    public void Test()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            // a Tdoc run that sent this RETRACERUN skips its test at once instead of waiting out its 90 s watchdog
            if (Replayer.Current != null) { ed.WriteMessage("\nA replay is already running.\n"); TdocRun.StartFailed("a replay was already running"); return; }
            if (Recorder.Instance.IsRecording) { ed.WriteMessage("\nStop the recording first (RETRACESTOP): a test opens a new drawing and replays into it.\n"); TdocRun.StartFailed("a recording was running"); return; }
            var path = TdocRun.TakeExpected() ?? TakeQueuedTest() ?? PickSession(ed);   // a Tdoc run names its test; the "Open test" window queues its recording; else ask
            if (path == null || !File.Exists(path)) { ed.WriteMessage("\nNo recording chosen; test cancelled.\n"); TdocRun.StartFailed("the recording was not found"); return; }
            var events = Plan.ReadEvents(path);
            var steps = Plan.Build(events).Where(s => s.Kind != "tab").ToList();
            var initial = Plan.InitialState(events);

            // this command runs with CommandFlags.Session, which is what allows adding and activating a document
            var dm = AcApp.DocumentManager;
            var doc = dm.Add(FindTemplate());
            dm.MdiActiveDocument = doc;
            _pending = new PendingTest { Doc = doc, Steps = steps, Path = path, Initial = initial, Tdoc = TdocRun.CaptureFor(path, steps) };
            if (!_testIdleHooked) { AcApp.Idle += OnTestIdle; _testIdleHooked = true; }
            // the new drawing is active now: the old one's editor rejects messages (eNotApplicable)
            doc.Editor.WriteMessage($"\nRETRACERUN: new drawing opened; the test starts when it is ready ({steps.Count} steps, Auto).\n");
        }
        catch (Exception ex)
        {
            Log.Error("RETRACERUN", ex);
            try { (AcApp.DocumentManager.MdiActiveDocument?.Editor ?? ed).WriteMessage($"\nRETRACERUN failed: {ex.Message}\n"); } catch { }
            TdocRun.StartFailed(ex.Message);   // no-op unless a Tdoc run is waiting for this test
        }
    }

    static string FindTemplate()
    {
        try { return HostApplicationServices.Current.FindFile("acad.dwt", HostApplicationServices.WorkingDatabase, FindFileHint.Default); }
        catch (Exception) { return ""; }
    }

    /// <summary>Starts the replay once the new document is the active one and AutoCAD is quiet (idle callback: reading the drawing is allowed here).</summary>
    void OnTestIdle(object? s, EventArgs e)
    {
        var p = _pending;
        try
        {
            if (p == null) { AcApp.Idle -= OnTestIdle; _testIdleHooked = false; return; }
            if (DateTime.UtcNow - p.Created > TimeSpan.FromSeconds(60))
            {
                AcApp.Idle -= OnTestIdle; _testIdleHooked = false; _pending = null;
                Print("RETRACERUN: the new drawing did not become active within 60 s; test not started.");
                if (p.Tdoc != null) TdocRun.StartFailed("the new drawing did not become active within 60 s");
                return;
            }
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !ReferenceEquals(doc, p.Doc) || !string.IsNullOrEmpty(doc.CommandInProgress) || !doc.Editor.IsQuiescent) return;
            AcApp.Idle -= OnTestIdle; _testIdleHooked = false; _pending = null;
            // a Tdoc run shows no verdict box between its tests and hears when the test is over
            var tdoc = p.Tdoc;
            if (!Replayer.Start(p.Steps, ReplayMode.Auto, p.Path, p.Initial, out var msg, alert: tdoc == null, interactive: true, tdoc: tdoc, finished: tdoc == null ? null : r => TdocRun.Finished(tdoc, r)))
            {
                Print("RETRACERUN: " + msg);
                if (tdoc != null) TdocRun.StartFailed(msg);
            }
            else Print("RETRACERUN: " + msg);
        }
        catch (Exception ex)
        {
            Log.Error("RETRACERUN idle", ex);
            AcApp.Idle -= OnTestIdle; _testIdleHooked = false; _pending = null;
            Print("RETRACERUN failed: " + ex.Message);
            if (p?.Tdoc != null) TdocRun.StartFailed(ex.Message);
        }
    }

    public void Step()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            var path = PickSession(ed);
            if (path == null || !File.Exists(path)) { ed.WriteMessage("\nNo recording chosen.\n"); return; }
            var events = Plan.ReadEvents(path);
            var steps = Plan.Build(events);
            if (Recorder.Instance.IsRecording) ed.WriteMessage("\nNote: a recording is running; the replayed actions will be recorded too.\n");
            if (!Replayer.Start(steps, ReplayMode.Step, path, Plan.InitialState(events), out var msg)) ed.WriteMessage($"\n{msg}\n");
            else ed.WriteMessage($"\n{msg} Use the Retrace replay panel.\n");
        }
        catch (Exception ex) { Log.Error("RETRACESTEP", ex); ed.WriteMessage($"\nRETRACESTEP failed: {ex.Message}\n"); }
    }

    // ---- settings -------------------------------------------------------------------------------------------------

    public void Settings(bool commandLine)
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            RetraceSettings.EnsureHome();
            var s = RetraceSettings.Load();
            if (commandLine || !FileDialogsOn())
            {
                using var cl = Ask.CommandLineOnly();   // -RETRACESETTINGS is the command-line form
                ed.WriteMessage("\nRetrace settings (" + s.FilePath + ")\n" + Describe(s));
                var n = s.Clone();
                var src = AskPath(ed, "Source folder (code to document)", s.Source, "");
                if (src == null) return;
                var docs = AskPath(ed, "Docs folder", s.Docs, s.DefaultDocs); if (docs == null) return;
                var tests = AskPath(ed, "Tests folder", s.Tests, s.DefaultTests); if (tests == null) return;
                var cap = AskPath(ed, "Captures folder (DCL captures)", s.Captures, s.DefaultCaptures); if (cap == null) return;
                var edt = AskPath(ed, "Editor for \"Open in editor\" (program path)", s.Editor, RetraceSettings.DefaultEditor(), Ask.Of.File); if (edt == null) return;
                n.SourceFolder = src == "." ? "" : src;
                n.DocsFolder = RetraceSettings.Normalize(docs, s.DefaultDocs);
                n.TestsFolder = RetraceSettings.Normalize(tests, s.DefaultTests);
                n.CapturesFolder = RetraceSettings.Normalize(cap, s.DefaultCaptures);
                n.EditorPath = RetraceSettings.Normalize(edt, RetraceSettings.DefaultEditor());
                var shots = Ask.Keywords(ed, new PromptKeywordOptions($"\nScreenshots while recording [Yes/No] <{(s.Screenshots ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (shots.Status == PromptStatus.Cancel) return;
                n.Screenshots = shots.Status == PromptStatus.OK ? shots.StringResult == "Yes" : s.Screenshots;
                var dv = Ask.Keywords(ed, new PromptKeywordOptions($"\nRecord changed dialog values [Yes/No] <{(s.DialogValues ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (dv.Status == PromptStatus.Cancel) return;
                n.DialogValues = dv.Status == PromptStatus.OK ? dv.StringResult == "Yes" : s.DialogValues;
                var af = Ask.Keywords(ed, new PromptKeywordOptions($"\nRETRACERUN fills dialogs itself [Yes/No] <{(s.AutoFillDialogs ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (af.Status == PromptStatus.Cancel) return;
                n.AutoFillDialogs = af.Status == PromptStatus.OK ? af.StringResult == "Yes" : s.AutoFillDialogs;
                var rc = Ask.Keywords(ed, new PromptKeywordOptions($"\nShow the checkpoints to rename after Stop [Yes/No] <{(s.ReviewCheckpoints ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (rc.Status == PromptStatus.Cancel) return;
                n.ReviewCheckpoints = rc.Status == PromptStatus.OK ? rc.StringResult == "Yes" : s.ReviewCheckpoints;
                var su = Ask.Keywords(ed, new PromptKeywordOptions($"\nRun test opens hidden palettes while it drives them [Yes/No] <{(s.ShowDrivenUi ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (su.Status == PromptStatus.Cancel) return;
                n.ShowDrivenUi = su.Status == PromptStatus.OK ? su.StringResult == "Yes" : s.ShowDrivenUi;
                var xd = Ask.Keywords(ed, new PromptKeywordOptions($"\nAlso write a Word file (steps.docx) [exportDocx] [Yes/No] <{(s.ExportDocx ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (xd.Status == PromptStatus.Cancel) return;
                n.ExportDocx = xd.Status == PromptStatus.OK ? xd.StringResult == "Yes" : s.ExportDocx;
                var xx = Ask.Keywords(ed, new PromptKeywordOptions($"\nAlso write an Excel file (steps.xlsx) [Yes/No] <{(s.ExportXlsx ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (xx.Status == PromptStatus.Cancel) return;
                n.ExportXlsx = xx.Status == PromptStatus.OK ? xx.StringResult == "Yes" : s.ExportXlsx;
                var tp = Ask.String(ed, $"\nRibbon tab position: afterHome, first, last or after:<tab id or title> <{RibbonTabPosition.Normalize(s.RibbonTabPosition)}> (Enter keeps, . resets): ");
                if (tp.Status == PromptStatus.Cancel) return;
                var tpv = (tp.StringResult ?? "").Trim();
                if (tpv.Length > 0) n.RibbonTabPosition = tpv == "." || tpv.Equals("default", StringComparison.OrdinalIgnoreCase) ? "" : RibbonTabPosition.Normalize(tpv);
                var ck = Ask.String(ed, $"\nCheckpoint shortcut, e.g. Ctrl+Shift+K or F8, None = off <{(KeyChord.IsOff(s.CheckpointKey) ? "None" : s.CheckpointKey)}> (Enter keeps, . resets): ");
                if (ck.Status == PromptStatus.Cancel) return;
                var ckv = (ck.StringResult ?? "").Trim();
                if (ckv.Length > 0)
                {
                    if (ckv == "." || ckv.Equals("default", StringComparison.OrdinalIgnoreCase)) n.CheckpointKey = KeyChord.Default;
                    else if (KeyChord.IsOff(ckv)) n.CheckpointKey = "None";
                    else if (KeyChord.TryParse(ckv, out var chord, out var ckErr)) n.CheckpointKey = chord.Format();
                    else { ed.WriteMessage($"\nCheckpoint shortcut not changed: {ckErr}\n"); }
                }
                var ps = Ask.Keywords(ed, new PromptKeywordOptions($"\nRetrace asks for names, files and folders in [Window/CommandLine] <{(s.UiPrompts ? "Window" : "CommandLine")}>: ", "Window CommandLine") { AllowNone = true });
                if (ps.Status == PromptStatus.Cancel) return;
                if (ps.Status == PromptStatus.OK) n.PromptStyle = ps.StringResult == "Window" ? PromptStyles.Ui : PromptStyles.CommandLine;
                // last, so scripts written for the questions before it keep working
                var lc = Ask.Keywords(ed, new PromptKeywordOptions($"\nDocs also cover the CUIX files AutoCAD has loaded [Yes/No] <{(s.LoadedCustomization ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (lc.Status == PromptStatus.Cancel) return;
                n.LoadedCustomization = lc.Status == PromptStatus.OK ? lc.StringResult == "Yes" : s.LoadedCustomization;
                var cr = Ask.Keywords(ed, new PromptKeywordOptions($"\nBuild Docs captures every ribbon tab first [Yes/No] <{(s.CaptureRibbon ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (cr.Status == PromptStatus.Cancel) return;
                n.CaptureRibbon = cr.Status == PromptStatus.OK ? cr.StringResult == "Yes" : s.CaptureRibbon;
                var aw = Ask.Keywords(ed, new PromptKeywordOptions($"\nThe capture also visits the other workspaces [Yes/No] <{(s.CaptureAllWorkspaces ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (aw.Status == PromptStatus.Cancel) return;
                n.CaptureAllWorkspaces = aw.Status == PromptStatus.OK ? aw.StringResult == "Yes" : s.CaptureAllWorkspaces;
                var tf = Ask.Keywords(ed, new PromptKeywordOptions($"\nTdoc brings the UI to the front before each picture [Yes/No] <{(s.TdocBringToFront ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (tf.Status == PromptStatus.Cancel) return;
                n.TdocBringToFront = tf.Status == PromptStatus.OK ? tf.StringResult == "Yes" : s.TdocBringToFront;
                var ta = Ask.Keywords(ed, new PromptKeywordOptions($"\nTdoc draws green numbered boxes where the user gives input [Yes/No] <{(s.TdocAnnotate ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
                if (ta.Status == PromptStatus.Cancel) return;
                n.TdocAnnotate = ta.Status == PromptStatus.OK ? ta.StringResult == "Yes" : s.TdocAnnotate;
                var td = Ask.String(ed, $"\nTdoc pause before each picture in milliseconds, 0 to {RetraceSettings.MaxTdocStepDelayMs} <{s.TdocStepDelayMs}> (Enter keeps): ");
                if (td.Status == PromptStatus.Cancel) return;
                var tdv = (td.StringResult ?? "").Trim();
                if (tdv.Length > 0)
                {
                    if (int.TryParse(tdv, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms)) n.TdocStepDelayMs = ms;
                    else ed.WriteMessage($"\nTdoc pause not changed: \"{tdv}\" is not a number.\n");
                }
                SaveAndReport(ed, n);
                return;
            }
            var w = new SettingsWindow(s);
            AcApp.ShowModalWindow(w);
            if (w.Saved) SaveAndReport(ed, w.Result);
            else ed.WriteMessage("\nSettings unchanged.\n");
        }
        catch (Exception ex) { Log.Error("RETRACESETTINGS", ex); ed.WriteMessage($"\nRETRACESETTINGS failed: {ex.Message}\n"); }
    }

    static void SaveAndReport(Editor ed, RetraceSettings n)
    {
        if (!n.Save(out var err)) { ed.WriteMessage($"\nCould not save settings: {err}\n"); return; }
        n.EnsureFolders();
        RibbonInstaller.ApplyPosition(n.RibbonTabPosition);
        CheckpointKey.Reload();
        RibbonInstaller.ShowCheckpointKey();
        ed.WriteMessage("\nSettings saved.\n" + Describe(n));
    }

    /// <summary>Enter keeps the current value; '.' or 'default' resets to the default (for the source folder: clears it).</summary>
    static string? AskPath(Editor ed, string label, string current, string dflt, Ask.Of kind = Ask.Of.Folder)
    {
        var r = Ask.String(ed, $"\n{label} <{(current.Length == 0 ? "not set" : current)}> (Enter keeps, . resets): ", kind,
            "path:" + new string(label.TakeWhile(c => c != '(').ToArray()).Trim().ToLowerInvariant(),   // recent places of this question only
            kind == Ask.Of.File ? "Programs (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*" : null, initial: current);
        if (r.Status == PromptStatus.Cancel) return null;
        var t = (r.StringResult ?? "").Trim().Trim('"');
        if (t.Length == 0) return current;
        if (t == "." || t.Equals("default", StringComparison.OrdinalIgnoreCase)) return dflt.Length == 0 ? "." : dflt;
        return t;
    }

    static string Describe(RetraceSettings s)
    {
        static string Mark(string p) => p.Length == 0 ? "(not set)" : Directory.Exists(p) || File.Exists(p) ? "exists" : "missing (created on use)";
        return $"  Source folder    : {(s.Source.Length == 0 ? "(not set; RETRACEDOCS asks)" : s.Source)}  [{Mark(s.Source)}]\n" +
               $"  Docs folder      : {s.Docs}  [{Mark(s.Docs)}]\n" +
               $"  Tests folder     : {s.Tests}  [{Mark(s.Tests)}]\n" +
               $"  Captures folder  : {s.Captures}  [{Mark(s.Captures)}]\n" +
               $"  Editor           : {s.Editor}{(string.IsNullOrWhiteSpace(s.EditorPath) ? " (default)" : "")}\n" +
               $"  Screenshots      : {(s.Screenshots ? "on" : "off")} (while recording)\n" +
               $"  Dialog values    : {(s.DialogValues ? "on" : "off")} (changed fields of dialogs, while recording)\n" +
               $"  Fill dialogs     : {(s.AutoFillDialogs ? "on" : "off")} (RETRACERUN fills a dialog with the recorded values and closes it)\n" +
               $"  Review checks    : {(s.ReviewCheckpoints ? "on" : "off")} (after Stop, the checkpoints window opens to rename them)\n" +
               $"  Show driven UI   : {(s.ShowDrivenUi ? "on" : "off")} (Run test opens a rolled-up or auto-hidden palette while it drives it)\n" +
               $"  Word export      : {(s.ExportDocx ? "on" : "off")} (steps.docx next to steps.md)\n" +
               $"  Excel export     : {(s.ExportXlsx ? "on" : "off")} (steps.xlsx next to steps.md)\n" +
               $"  Ribbon tab       : {RibbonTabPosition.Normalize(s.RibbonTabPosition)} (afterHome, first, last, after:<tab id or title>)\n" +
               $"  Capture ribbon   : {(s.CaptureRibbon ? "on (Build Docs captures every ribbon tab first: pictures, not wireframes)" : "off (the last captures, else wireframes)")}\n" +
               $"  All workspaces   : {(s.CaptureAllWorkspaces ? "on (the capture also visits the other workspaces; unsaved workspace changes are lost)" : "off")}\n" +
               $"  Loaded CUIX      : {(s.LoadedCustomization ? "documented too (the main .cuix, partials, enterprise file AutoCAD has loaded)" : "not documented")}\n" +
               $"  Prompts          : {(s.UiPrompts ? "window (on top, Browse and recent places; scripts, LISP and FILEDIA 0 use the command line)" : "command line")}\n" +
               $"  Tdoc front       : {(s.TdocBringToFront ? "on" : "off")} (RETRACETDOC brings AutoCAD, the dialog or the palette to the front before each picture)\n" +
               $"  Tdoc boxes       : {(s.TdocAnnotate ? "on" : "off")} (green numbered boxes on the pictures where the user gives input)\n" +
               $"  Tdoc pause       : {s.TdocStepDelayMs} ms (before each picture, so the screen has repainted)\n" +
               $"  Checkpoint key   : {(s.CheckpointChord(out var warn) is { } kc ? kc.Format() : "off")} (while recording; Ctrl+Shift+K, F8, None = off){(warn.Length > 0 ? "\n  WARNING: " + warn : "")}\n";
    }

    // ---- docs and guide --------------------------------------------------------------------------------------------

    /// <summary>RETRACEDOCS: the engine builds the docs (guide, reference, test plans, recordings) in the background.</summary>
    public void BuildDocs()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            var s = RetraceSettings.Load();
            if (!Directory.Exists(s.Source))
            {
                var picked = AskSourceFolder(ed, s.Source);
                if (picked == null) { ed.WriteMessage("\nRETRACEDOCS: no source folder; cancelled.\n"); return; }
                s.SourceFolder = picked;
                s.Save(out _);
            }
            if (!Directory.Exists(s.Source)) { ed.WriteMessage($"\nRETRACEDOCS: source folder not found: {s.Source}\n"); return; }
            s.EnsureFolders();
            var loaded = s.LoadedCustomization ? LoadedCustomization() : [];   // read now, in the command (COM)
            // the ribbon as AutoCAD draws it, so the docs show pictures, not wireframes: every tab is shown in turn first (a few minutes
            // for AutoCAD Electrical's ~70 tabs), then the engine builds in the background
            if (s.CaptureRibbon)
            {
                if (RibbonCapture.Running) { ed.WriteMessage("\nRETRACEDOCS: a ribbon capture is running; run it again when that is done.\n"); return; }
                if (Recorder.Instance.IsRecording)
                    ed.WriteMessage("\nRETRACEDOCS: a test is being recorded, so the ribbon is not captured now (its tab changes would be recorded); the last captures are used.\n");
                else
                {
                    var spaces = s.CaptureAllWorkspaces ? OtherWorkspaces(ed) : [];
                    var why = RibbonCapture.StartForDocs(s.Captures, ed, msg => { Print("RETRACEDOCS: " + msg); StartDocsBuild(s, loaded); }, out var tabs, spaces);
                    if (why == null)
                    {
                        var secs = tabs * 1.1 + spaces.Count * 30;
                        ed.WriteMessage($"\nRETRACEDOCS: capturing the {tabs} ribbon tabs{(spaces.Count > 0 ? $", then those of {spaces.Count} more workspace(s)" : "")} first " +
                                        $"(about {Math.Max(1, (int)Math.Ceiling(secs / 60))} min; leave AutoCAD as it is), then building the docs.\n");
                        return;
                    }
                    ed.WriteMessage($"\nRETRACEDOCS: the ribbon is not captured ({why}); the last captures are used.\n");
                }
            }
            StartDocsBuild(s, loaded);
        }
        catch (Exception ex) { Log.Error("RETRACEDOCS", ex); ed.WriteMessage($"\nRETRACEDOCS failed: {ex.Message}\n"); }
    }

    /// <summary>The workspaces of the main and enterprise customization files (their WorkspaceRoot only), for the ribbon capture to switch
    /// through. Empty, with a message, when WSAUTOSAVE is on: switching would save the temporary state into the user's customization file.</summary>
    static List<string> OtherWorkspaces(Editor ed)
    {
        try
        {
            if (Convert.ToInt32(AcApp.GetSystemVariable("WSAUTOSAVE")) != 0)
            {
                ed.WriteMessage("\nRETRACEDOCS: WSAUTOSAVE is on, so the other workspaces are not visited (switching would save into your customization file).\n");
                return [];
            }
        }
        catch (Exception ex) { Log.Error("WSAUTOSAVE", ex); return []; }
        var names = new List<string>();
        foreach (var f in LoadedCustomization())   // menu groups' files whatever loadedCustomization says: only their workspace names are read
            try { names.AddRange(Retrace.Engine.Scan.Cuix.CuixReader.WorkspaceNames(f)); }
            catch (Exception ex) { Log.Info($"workspaces of {f}: {ex.Message}"); }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Queues the docs build (engine, background thread) and says so.</summary>
    void StartDocsBuild(RetraceSettings s, IReadOnlyList<string> loaded)
    {
        var disp = Dispatcher.CurrentDispatcher;
        var index = s.GuideIndex;
        var wait = EngineJobs.IsRunning;
        if (!EngineJobs.BuildDocs(s, r => disp.BeginInvoke(() => GuideDone(r, index)), out var runErr, loaded)) { Print($"RETRACEDOCS: {runErr}"); return; }
        if (loaded.Count > 0) Print($"RETRACEDOCS: also documenting the {loaded.Count} customization file(s) AutoCAD has loaded: {string.Join(", ", loaded.Select(Path.GetFileName))}");
        Print($"RETRACEDOCS: {(wait ? "another Retrace job is running; the docs are queued and built right after it" : "building the docs")} from {s.Source}\n  into {s.Docs}\n  AutoCAD stays usable; a message appears here when it is done.");
    }

    /// <summary>The customization files AutoCAD has loaded: each menu group's file (the main .cuix and its partials, read over COM) and the
    /// enterprise file (ENTERPRISEMENU), as full paths that exist. A name without a folder is looked up on AutoCAD's support path. One menu
    /// group that cannot be read is logged and skipped. UI thread (COM).</summary>
    static List<string> LoadedCustomization()
    {
        var found = new List<string>();
        var db = AcApp.DocumentManager.MdiActiveDocument?.Database;
        void Add(string? p)
        {
            p = p?.Trim();
            if (string.IsNullOrEmpty(p) || p == ".") return;   // ENTERPRISEMENU "." = none
            // a menu group's file name may come without its extension, or without its folder
            foreach (var c in new[] { p, p + ".cuix", p + ".cui" })
            {
                if (!c.EndsWith(".cuix", StringComparison.OrdinalIgnoreCase) && !c.EndsWith(".cui", StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.IsPathRooted(c) ? c : Find(c);
                if (full == null || !File.Exists(full)) continue;
                if (!found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
                return;
            }
        }
        string? Find(string name)
        {
            try { return db == null ? null : HostApplicationServices.Current.FindFile(name, db, FindFileHint.Default); }
            catch (Exception) { return null; }   // not on the support path
        }
        try
        {
            foreach (dynamic g in ((dynamic)Autodesk.AutoCAD.ApplicationServices.Application.AcadApplication).MenuGroups)
            {
                try { Add((string?)g.MenuFileName); }
                catch (Exception ex) { Log.Error("loaded customization: a menu group", ex); }
            }
        }
        catch (Exception ex) { Log.Error("loaded customization: menu groups", ex); }
        try { Add(AcApp.GetSystemVariable("ENTERPRISEMENU") as string); }
        catch (Exception ex) { Log.Error("loaded customization: ENTERPRISEMENU", ex); }
        return found;
    }

    static string? AskSourceFolder(Editor ed, string current)
    {
        if (Ask.InWindow())
        {
            var a = Ask.String(ed, "\nFolder with the code, CUIX and DCL files to document: ", Ask.Of.Folder, "folder", initial: Directory.Exists(current) ? current : "");
            var p = a.Status == PromptStatus.OK ? a.StringResult.Trim().Trim('"') : "";
            return p.Length == 0 ? null : p;
        }
        if (FileDialogsOn())
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with the code, CUIX and DCL files to document" };
            if (Directory.Exists(current)) d.InitialDirectory = current;
            return d.ShowDialog() == true ? d.FolderName : null;
        }
        var r = ed.GetString(new PromptStringOptions("\nFolder with the code, CUIX and DCL files to document: ") { AllowSpaces = true });
        var t = r.Status == PromptStatus.OK ? r.StringResult.Trim().Trim('"') : "";
        return t.Length == 0 ? null : t;
    }

    /// <summary>Runs on the UI thread (dispatcher), outside any command: prints, and offers to open the guide.</summary>
    void GuideDone(EngineResult r, string index)
    {
        try
        {
            if (r.Success)
            {
                // No "open it now?" box: it popped up unasked, could sit hidden behind AutoCAD, and a box still open at exit kept acad.exe running.
                Print("RETRACEDOCS: done.\n" + string.Join("\n", r.Messages.TakeLast(4).Concat(r.Warnings.Take(10).Select(w => "warning: " + w)).Select(l => "  " + l)) + $"\n  Guide: {index}\n  Open it with RETRACEGUIDE.");
            }
            else if (r.Cancelled) Print("RETRACEDOCS: cancelled.");
            else Print($"RETRACEDOCS FAILED: {r.Error}");
        }
        catch (Exception ex) { Log.Error("GuideDone", ex); }
    }

    /// <summary>RETRACEGUIDE: the guide palette inside AutoCAD (reads and edits the guide).</summary>
    public void Guide()
    {
        var ed = Ed;
        try
        {
            // the Guide palette is the normal way; shell-open the guide only if the palette cannot be shown (e.g. no WebView2 runtime)
            try { GuidePalette.Show(); return; }
            catch (Exception ex) { Log.Error("guide palette", ex); }
            var s = RetraceSettings.Load();
            var index = s.GuideIndex;
            if (File.Exists(index)) { OpenPath(index, System.IO.Path.GetDirectoryName(index)!); ed?.WriteMessage($"\nOpening {index}\n"); }
            else
            {
                ed?.WriteMessage($"\nNo guide yet at {index}. Run RETRACEDOCS first.\n");
                if (Directory.Exists(s.Docs)) OpenFolderPath(s.Docs);
            }
        }
        catch (Exception ex) { Log.Error("RETRACEGUIDE", ex); ed?.WriteMessage($"\nRETRACEGUIDE failed: {ex.Message}\n"); }
    }

    // ---- steps (steps.md next to the recording) ---------------------------------------------------------------

    /// <summary>Runs the engine in the background (EngineJobs.WriteSteps). Never throws; the session is kept whatever happens.
    /// openWhenDone: show the script when it is written (RETRACESTEPS for a session without one).</summary>
    void BuildScript(string sessionPath, bool openWhenDone, bool reviewWhenDone = false)
    {
        try
        {
            var s = RetraceSettings.Load();
            bool docx = s.ExportDocx, xlsx = s.ExportXlsx;
            var md = TestFolders.ScriptPath(sessionPath);
            var disp = Dispatcher.CurrentDispatcher;
            var wait = EngineJobs.IsRunning;
            if (!EngineJobs.WriteSteps(sessionPath, docx, xlsx, r => disp.BeginInvoke(() => ScriptDone(r, sessionPath, md, openWhenDone, reviewWhenDone)), out var runErr))
            { Print($"Steps not written: {runErr} Run RETRACESTEPS in a moment."); return; }
            Print(wait ? "Another Retrace job is running; the steps are queued and written right after it..." : "Writing the steps in the background...");
        }
        catch (Exception ex) { Log.Error("BuildScript", ex); Print("Steps not written: " + ex.Message); }
    }

    void ScriptDone(EngineResult r, string sessionPath, string md, bool openWhenDone, bool reviewWhenDone = false)
    {
        try
        {
            if (r.Script != null && File.Exists(r.Script))
            {
                Print("Steps: " + r.Script);
                if (r.Docx != null) Print("  Word:  " + r.Docx);
                if (r.Xlsx != null) Print("  Excel: " + r.Xlsx);
                foreach (var w in r.Warnings) Print("  warning: " + w);
                if (!r.Success) Print($"  Word / Excel export failed: {r.Error}");
                if (openWhenDone) OpenScriptFile(r.Script);
                if (reviewWhenDone) ReviewAfterStop(sessionPath);
            }
            else if (r.Cancelled) Print("Steps cancelled. The recording is kept: " + sessionPath);
            else Print($"Steps FAILED: {r.Error}. The recording is kept: {sessionPath}");
        }
        catch (Exception ex) { Log.Error("ScriptDone", ex); }
    }

    /// <summary>RETRACESTEPS: the steps of the newest test; written first if they do not exist yet.</summary>
    public void OpenScript()
    {
        var ed = Ed;
        try
        {
            var dir = RetraceSettings.Load().Tests;
            var newest = TestFolders.Newest(dir);
            if (newest == null) { ed?.WriteMessage($"\nNo tests yet in {dir}.\n"); return; }
            var md = TestFolders.ScriptPath(newest);
            if (File.Exists(md)) OpenScriptFile(md);
            else BuildScript(newest, openWhenDone: true);
        }
        catch (Exception ex) { Log.Error("RETRACESTEPS", ex); ed?.WriteMessage($"\nRETRACESTEPS failed: {ex.Message}\n"); }
    }

    /// <summary>The script viewer (a window with the page and its pictures); the default app for .md if it cannot be shown.</summary>
    void OpenScriptFile(string md)
    {
        try
        {
            if (ScriptViewer.Open(md)) { Print("Opened " + md); return; }
        }
        catch (Exception ex) { Log.Error("script viewer", ex); }
        OpenPath(md, System.IO.Path.GetDirectoryName(md)!);
    }

    /// <summary>RETRACEFOLDER [Root/Tests/Captures/Docs]: opens one of Retrace's folders in Explorer (asks when the keyword was not typed).</summary>
    public void Folder()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            var k = new PromptKeywordOptions("\nFolder to open [Root/Tests/Captures/Docs] <Tests>: ", "Root Tests Captures Docs") { AllowNone = true };
            k.Keywords.Default = "Tests";
            var kr = Ask.Keywords(ed, k);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            OpenFolder(kr.Status == PromptStatus.OK ? kr.StringResult : "Tests");
        }
        catch (Exception ex) { Log.Error("RETRACEFOLDER", ex); ed.WriteMessage($"\nRETRACEFOLDER failed: {ex.Message}\n"); }
    }

    /// <summary>Opens a folder by keyword: Root, Tests, Captures or Docs (the effective folders from the settings).</summary>
    public void OpenFolder(string which)
    {
        var ed = Ed;
        try
        {
            var s = RetraceSettings.Load();
            var dir = which switch
            {
                "Root" => s.RootDir,
                "Captures" => s.Captures,
                "Docs" => s.Docs,
                _ => s.Tests,
            };
            Directory.CreateDirectory(dir);
            OpenFolderPath(dir);
            ed?.WriteMessage($"\n{dir}\n");
        }
        catch (Exception ex) { Log.Error("open folder", ex); ed?.WriteMessage($"\nCould not open the folder: {ex.Message}\n"); }
    }

    /// <summary>Default app for the file; if that fails or nothing is associated, the folder in Explorer.</summary>
    static void OpenPath(string file, string folder)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("shell execute " + file, ex); OpenFolderPath(folder); }
    }

    static void OpenFolderPath(string dir) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
}
