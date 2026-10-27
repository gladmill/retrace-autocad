using Retrace.Engine.Recordings;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// What the commands do. Owns the autostart hook and the state the command bodies share; <see cref="RetraceApp"/> holds the [CommandMethod]s
/// and calls in here. Shutdown() undoes everything Initialize()/Start() attached.
/// </summary>
public sealed partial class Commands
{
    public static Commands Instance { get; } = new();

    bool _autostartHooked;

    public string Version => BuildInfo.Version;

    /// <summary>Writes a line to the AutoCAD command line of the active drawing.</summary>
    internal static void Print(string message)
    {
        try { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + message + "\n"); } catch (Exception) { }
    }

    public void Initialize()
    {
        RetraceSettings.EnsureHome();
        try
        {
            if (Environment.GetEnvironmentVariable("RETRACE_AUTOSTART") == "1")
            {
                // start on the first idle tick, when documents and the ribbon exist
                AcApp.Idle += OnAutostartIdle;
                _autostartHooked = true;
            }
        }
        catch (Exception ex) { Log.Error("Initialize", ex); }
    }

    void OnAutostartIdle(object? s, EventArgs e)
    {
        AcApp.Idle -= OnAutostartIdle;
        _autostartHooked = false;
        Log.Safe("autostart", () => Recorder.Instance.Start("auto"));
    }

    public void Shutdown()
    {
        Log.Safe("Shutdown", () =>
        {
            if (_autostartHooked) { AcApp.Idle -= OnAutostartIdle; _autostartHooked = false; }
            Replayer.StopCurrent();
            ShutdownCommands();
            Recorder.Instance.Detach();
        });
    }

    static Editor? Ed => AcApp.DocumentManager.MdiActiveDocument?.Editor;

    public void Start()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            var o = new PromptStringOptions("\nTest name <test>: ") { AllowSpaces = true };
            var r = ed.GetString(o);
            if (r.Status == PromptStatus.Cancel) return;
            var path = Recorder.Instance.Start(r.Status == PromptStatus.OK ? r.StringResult : null);
            ed.WriteMessage($"\nRetrace recording started: {path}\nClick Checkpoint (RETRACEMARK) at each result worth checking; RETRACESTOP to finish.\n");
        }
        catch (Exception ex) { Log.Error("RETRACERECORD", ex); ed.WriteMessage($"\nRETRACERECORD failed: {ex.Message}\n"); }
    }

    public void Stop()
    {
        var ed = Ed;
        try
        {
            var r = Recorder.Instance.Stop();
            ed?.WriteMessage(r == null ? "\nNot recording.\n" : $"\nRetrace recording stopped: {r.Value.Events} events, {r.Value.Ms / 1000.0:0.#} s\n  {r.Value.Path}\n");
            if (r != null) BuildScript(r.Value.Path, openWhenDone: false, reviewWhenDone: true);
        }
        catch (Exception ex) { Log.Error("RETRACESTOP", ex); }
    }

    /// <summary>RETRACEMARK: a checkpoint at once, with no prompt, so it does not interrupt the test. It is named automatically
    /// (checkpoint2_draw_grid_button_ok); the names are reviewed and changed after the recording stops.</summary>
    public void Mark() => MarkCore(ask: false);

    /// <summary>-RETRACEMARK: the same, but asks for the name on the command line (Enter = the automatic name). For scripts.</summary>
    public void MarkAsk() => MarkCore(ask: true);

    /// <summary>The checkpoint shortcut (<see cref="CheckpointKey"/>): the same checkpoint as RETRACEMARK, taken outside any command. Pressed
    /// during a command, it is taken when that command ends (the recorder calls this again): a replay can only check the drawing between
    /// steps, and the command keeps running undisturbed.</summary>
    /// <param name="at">what was at the cursor when the key was pressed (<see cref="CursorContext"/>): the checkpoint is named after it</param>
    public void MarkFromKey(Checkpoints.CheckpointContext? at = null)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        var rec = Recorder.Instance;
        if (doc == null || !rec.IsRecording) return;
        if (!string.IsNullOrEmpty(doc.CommandInProgress))
        {
            if (!rec.MarkPending) doc.Editor.WriteMessage($"\nCheckpoint: taken when {doc.CommandInProgress} ends.\n" + (rec.OpenPrompt ?? ""));
            rec.MarkPending = true;
            rec.PendingAt = at;
            return;
        }
        rec.MarkPending = false;
        var state = DrawingState.Read(doc);
        var name = Recorder.Instance.Mark(null, state, at);
        var msg = name == null ? "Checkpoint NOT saved: the recording could not be written (see retrace.log)."
                : state == null ? $"Checkpoint saved: {name} (drawing state could not be read)." : $"Checkpoint saved: {name} ({state.Summary()}).";
        doc.Editor.WriteMessage("\n" + msg + "\n");
        doc.Editor.PostCommandPrompt();
    }

    void MarkCore(bool ask)
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            if (!Recorder.Instance.IsRecording) { ed.WriteMessage("\nNot recording. Use RETRACERECORD.\n"); return; }
            string? note = null;
            if (ask)
            {
                var r = ed.GetString(new PromptStringOptions($"\nCheckpoint name (Enter = automatic): ") { AllowSpaces = true });
                if (r.Status != PromptStatus.OK && r.Status != PromptStatus.None) return; // Esc: no checkpoint
                note = r.Status == PromptStatus.OK ? r.StringResult : null;
            }
            // read the drawing here, in the command (never inside a prompt event handler)
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var state = doc == null ? null : DrawingState.Read(doc);
            var name = Recorder.Instance.Mark(note, state);
            if (name == null) { ed.WriteMessage("\nCheckpoint NOT saved: the recording could not be written (see retrace.log).\n"); return; }
            ed.WriteMessage(state == null ? $"\nCheckpoint saved: {name} (drawing state could not be read).\n" : $"\nCheckpoint saved: {name} ({state.Summary()}). You can rename it when you stop.\n");
        }
        catch (Exception ex) { Log.Error("RETRACEMARK", ex); }
    }

    public void Status()
    {
        var ed = Ed; if (ed == null) return;
        var s = RetraceSettings.Load();
        var w = Recorder.Instance.Writer;
        ed.WriteMessage((w == null
            ? "\nNot recording.\n"
            : $"\nRecording \"{w.Name}\": {w.Events} events, {w.ElapsedMs / 1000.0:0.#} s\n  {w.Path}\n")
            + $"Retrace {Version}\n"
            + $"Root folder:  {s.RootDir}\n"
            + $"Settings:     {s.FilePath}\n"
            + $"Docs folder:  {s.Docs}\n"
            + $"Tests folder: {s.Tests}\n"
            + ExportLine(s));
    }

    static string ExportLine(RetraceSettings s) =>
        $"Word export: {(s.ExportDocx ? "on" : "off")}, Excel export: {(s.ExportXlsx ? "on" : "off")} (RETRACESETTINGS)\n";

    /// <summary>Asks for a recording: Enter = file picker, "last" = newest in the Tests folder, or a path.</summary>
    static string? AskSession(Editor ed)
    {
        var tests = RetraceSettings.Load().Tests;
        var r = ed.GetString(new PromptStringOptions("\nRecording (Enter = browse, 'last' = newest test): ") { AllowSpaces = true });
        if (r.Status != PromptStatus.OK && r.Status != PromptStatus.None) return null;
        var s = r.StringResult?.Trim().Trim('"') ?? "";
        if (s.Equals("last", StringComparison.OrdinalIgnoreCase)) return TestFolders.Newest(tests);
        if (s.Length > 0) return s;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose recording.jsonl in a test folder", Filter = "Retrace recordings (*.jsonl)|*.jsonl", InitialDirectory = Directory.Exists(tests) ? tests : null };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public void Replay()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            var path = AskSession(ed);
            if (path == null || !File.Exists(path)) { ed.WriteMessage("\nNo recording chosen.\n"); return; }
            var k = new PromptKeywordOptions("\nReplay mode [Auto/Step] <Step>: ", "Auto Step") { AllowNone = true };
            k.Keywords.Default = "Step";
            var kr = ed.GetKeywords(k);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            var mode = kr.Status == PromptStatus.OK && kr.StringResult == "Auto" ? ReplayMode.Auto : ReplayMode.Step;
            var events = Plan.ReadEvents(path);
            var steps = Plan.Build(events).Where(s => s.Kind != "tab" || mode == ReplayMode.Step).ToList();
            if (Recorder.Instance.IsRecording) ed.WriteMessage("\nNote: a recording is running; the replayed actions will be recorded too.\n");
            if (!Replayer.Start(steps, mode, path, Plan.InitialState(events), out var msg)) ed.WriteMessage($"\n{msg}\n");
            else ed.WriteMessage($"\n{msg} Use the Retrace replay panel.\n");
        }
        catch (Exception ex) { Log.Error("RETRACEREPLAY", ex); ed.WriteMessage($"\nRETRACEREPLAY failed: {ex.Message}\n"); }
    }

    public void Export()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            var path = AskSession(ed);
            if (path == null || !File.Exists(path)) { ed.WriteMessage("\nNo recording chosen.\n"); return; }
            var steps = Plan.Build(Plan.ReadEvents(path));
            var scr = System.IO.Path.ChangeExtension(path, ".scr");
            File.WriteAllText(scr, ScriptExport.Build(steps, System.IO.Path.GetFileName(path)));
            ed.WriteMessage($"\nScript written: {scr}\nRun it with the SCRIPT command.\n");
        }
        catch (Exception ex) { Log.Error("RETRACESCR", ex); ed.WriteMessage($"\nRETRACESCR failed: {ex.Message}\n"); }
    }
}
