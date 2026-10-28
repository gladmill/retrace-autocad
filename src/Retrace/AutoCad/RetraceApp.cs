using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Retrace.Ui;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

[assembly: ExtensionApplication(typeof(Retrace.AutoCad.RetraceApp))]
[assembly: CommandClass(typeof(Retrace.AutoCad.RetraceApp))]

namespace Retrace.AutoCad;

/// <summary>
/// The one entry point AutoCAD loads: the <see cref="IExtensionApplication"/> and every [CommandMethod]. Commands call <see cref="Commands"/> directly.
/// </summary>
public sealed class RetraceApp : IExtensionApplication
{
    public void Initialize()
    {
        try
        {
            try { RibbonInstaller.Install(); } catch (Exception ex) { Log.Error("ribbon install", ex); }
            Commands.Instance.Initialize();
        }
        catch (Exception ex) { Log.Error("Initialize", ex); }
    }

    public void Terminate()
    {
        // each step on its own: one failing must not skip the rest
        Log.Safe("Terminate: shutdown", () => Commands.Instance.Shutdown());
        Log.Safe("Terminate: palette", () => GuidePalette.Dispose());
        Log.Safe("Terminate: ribbon", () => RibbonInstaller.Uninstall());
    }

    /// <summary>Palette buttons run commands this way (the palette is modeless). False with a message when no drawing is open.</summary>
    internal static bool SendCommand(string command, out string error)
    {
        error = "";
        try
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) { error = "Open a drawing first: AutoCAD runs commands in a drawing."; return false; }
            doc.SendStringToExecute(command, true, false, false);
            return true;
        }
        catch (Exception ex) { Log.Error("send " + command, ex); error = ex.Message; return false; }
    }

    /// <summary>Runs a command body; a failure is logged and printed, never thrown into AutoCAD.</summary>
    static void Run(string name, Action call)
    {
        try { call(); }
        catch (Exception ex) { Log.Error(name, ex); Commands.Print($"{name} failed: {ex.Message}"); }
    }

    // ---- commands ----------------------------------------------------------------------------

    [CommandMethod("RETRACERECORD")] public static void Record() => Run("RETRACERECORD", Commands.Instance.Start);
    [CommandMethod("RETRACESTOP")] public static void Stop() => Run("RETRACESTOP", Commands.Instance.Stop);
    [CommandMethod("RETRACEMARK")] public static void Mark() => Run("RETRACEMARK", Commands.Instance.Mark);
    [CommandMethod("-RETRACEMARK")] public static void MarkAsk() => Run("-RETRACEMARK", Commands.Instance.MarkAsk);
    [CommandMethod("RETRACECHECKPOINTS")] public static void Checkpoints() => Run("RETRACECHECKPOINTS", Commands.Instance.ReviewCheckpoints);
    [CommandMethod("RETRACESTATUS")] public static void Status() => Run("RETRACESTATUS", Commands.Instance.Status);
    [CommandMethod("RETRACEREPLAY")] public static void Replay() => Run("RETRACEREPLAY", Commands.Instance.Replay);
    [CommandMethod("RETRACESCR")] public static void Scr() => Run("RETRACESCR", Commands.Instance.Export);

    // Commands behind the ribbon. RETRACERUN needs CommandFlags.Session: it adds and activates a new document.
    [CommandMethod("RETRACERUN", CommandFlags.Session)] public static void RunTest() => Run("RETRACERUN", Commands.Instance.Test);
    [CommandMethod("RETRACESTEP")] public static void StepThrough() => Run("RETRACESTEP", Commands.Instance.Step);
    [CommandMethod("RETRACEDOCS")] public static void Docs() => Run("RETRACEDOCS", Commands.Instance.BuildDocs);
    [CommandMethod("RETRACEGUIDE")] public static void Guide() => Run("RETRACEGUIDE", Commands.Instance.Guide);
    [CommandMethod("RETRACESETTINGS")] public static void SettingsDialog() => Run("RETRACESETTINGS", () => Commands.Instance.Settings(false));
    [CommandMethod("-RETRACESETTINGS")] public static void SettingsCommandLine() => Run("-RETRACESETTINGS", () => Commands.Instance.Settings(true));
    [CommandMethod("RETRACESTEPS")] public static void OpenSteps() => Run("RETRACESTEPS", Commands.Instance.OpenScript);
    [CommandMethod("RETRACEOPEN")] public static void OpenTestWindow() => Run("RETRACEOPEN", Commands.Instance.OpenTest);
    [CommandMethod("RETRACEMAP")] public static void MapRibbon() => Run("RETRACEMAP", Commands.Instance.Map);
    [CommandMethod("-RETRACEMAP")] public static void MapRibbonCommandLine() => Run("-RETRACEMAP", Commands.Instance.MapCommandLine);
    [CommandMethod("RETRACEFOLDER")] public static void Folder() => Run("RETRACEFOLDER", Commands.Instance.Folder);
    [CommandMethod("RETRACECAPTUREDCL", CommandFlags.Session)] public static void CaptureDcl() => Run("RETRACECAPTUREDCL", DclCapture.Run);
    [CommandMethod("RETRACECAPTURERIBBON")] public static void CaptureRibbon() => Run("RETRACECAPTURERIBBON", RibbonCapture.Run);

    /// <summary>RETRACECLOSEALL: closes every open drawing without saving, after one confirmation. Runs in the application context
    /// (CommandFlags.Session), which closing the active drawing needs. </summary>
    [CommandMethod("RETRACECLOSEALL", CommandFlags.Session)]
    public static void CloseAll()
    {
        try
        {
            var docs = AcApp.DocumentManager.Cast<Document>().ToList();
            if (docs.Count == 0) { Commands.Print("No drawings are open."); return; }
            var names = string.Join("\n", docs.Take(12).Select(d => "  " + System.IO.Path.GetFileName(d.Name))) + (docs.Count > 12 ? $"\n  ... and {docs.Count - 12} more" : "");
            var answer = System.Windows.MessageBox.Show($"Close all {docs.Count} open drawing{(docs.Count == 1 ? "" : "s")} WITHOUT saving?\n\n{names}\n\nUnsaved changes are lost.",
                "Retrace: close all drawings", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes) { Commands.Print("Close all drawings: cancelled."); return; }
            var busy = new List<Document>();
            foreach (var d in docs)
            {
                try { d.CloseAndDiscard(); }
                catch (System.Exception) { busy.Add(d); }   // "Drawing is busy": a command is still open in it (e.g. the one RETRACERUN started from)
            }
            if (busy.Count > 0) RetryBusy(busy, docs.Count);
        }
        catch (System.Exception ex) { Log.Error("RETRACECLOSEALL", ex); Commands.Print("RETRACECLOSEALL failed: " + ex.Message); }
    }

    /// <summary>Drawings that were busy: cancel what runs in them, then try again every half second, for up to 5 s.</summary>
    static void RetryBusy(List<Document> busy, int total)
    {
        int tries = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            tries++;
            foreach (var d in busy.ToList())
            {
                try
                {
                    if (!AcApp.DocumentManager.Cast<Document>().Contains(d)) { busy.Remove(d); continue; }
                    if (!string.IsNullOrEmpty(d.CommandInProgress))
                    {
                        if (AcApp.DocumentManager.MdiActiveDocument != d) AcApp.DocumentManager.MdiActiveDocument = d;
                        d.SendStringToExecute("\x1B\x1B", true, false, false);
                        continue;
                    }
                    d.CloseAndDiscard();
                    busy.Remove(d);
                }
                catch (System.Exception ex) { if (tries >= 10) Log.Error("RETRACECLOSEALL " + d.Name, ex); }
            }
            if (busy.Count == 0 || tries >= 10)
            {
                timer.Stop();
                if (busy.Count > 0) Commands.Print($"Closed {total - busy.Count} of {total} drawings; {string.Join(", ", busy.Select(d => System.IO.Path.GetFileName(d.Name)))} stayed open (busy). See retrace.log.");
            }
        };
        timer.Start();
    }
}
