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
            ed.WriteMessage($"\nRetrace recording started: {path}\nUse RETRACEMARK for checkpoints, RETRACESTOP to finish.\n");
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
            if (r != null) BuildScript(r.Value.Path, openWhenDone: false);
        }
        catch (Exception ex) { Log.Error("RETRACESTOP", ex); }
    }

    public void Mark()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            if (!Recorder.Instance.IsRecording) { ed.WriteMessage("\nNot recording. Use RETRACERECORD.\n"); return; }
            var r = ed.GetString(new PromptStringOptions("\nNote / expected result (Enter = checkpoint without note): ") { AllowSpaces = true });
            if (r.Status != PromptStatus.OK && r.Status != PromptStatus.None) return; // Esc: no checkpoint
            // read the drawing here, in the command, after the prompt returned (never inside a prompt event handler)
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var state = doc == null ? null : DrawingState.Read(doc);
            Recorder.Instance.Mark(r.Status == PromptStatus.OK ? (r.StringResult ?? "").Trim() : "", state);
            ed.WriteMessage(state == null ? "\nCheckpoint saved (drawing state could not be read).\n" : $"\nCheckpoint saved: {state.Summary()}\n");
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
