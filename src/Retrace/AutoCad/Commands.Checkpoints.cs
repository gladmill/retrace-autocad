using Retrace.Engine;
using Retrace.Engine.Recordings;
using Retrace.Ui;
using System.Windows.Threading;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

// RETRACECHECKPOINTS: the window to rename the checkpoints of a test, also opened after RETRACESTOP. Same rules as the Open test window:
// never throw into AutoCAD, no IO on the UI thread beyond starting a process, the engine runs in the background.
public sealed partial class Commands
{
    readonly Dictionary<string, CheckpointsWindow> _checkpointsWins = new(StringComparer.OrdinalIgnoreCase);   // full recording path -> its window

    /// <summary>RETRACECHECKPOINTS: the checkpoints of the newest test.</summary>
    public void ReviewCheckpoints()
    {
        var ed = Ed;
        try
        {
            var dir = RetraceSettings.Load().Tests;
            var newest = TestFolders.Newest(dir);
            if (newest == null) { ed?.WriteMessage($"\nNo tests yet in {dir}.\n"); return; }
            OpenCheckpoints(newest, afterStop: false);
        }
        catch (Exception ex) { Log.Error("RETRACECHECKPOINTS", ex); ed?.WriteMessage($"\nRETRACECHECKPOINTS failed: {ex.Message}\n"); }
    }

    /// <summary>Shows the window for <paramref name="recording"/>: brings an open one for the same test to the front, else opens a new one
    /// (windows of other tests stay open, so unsaved names there are never lost or in the way; an open window with unsaved names is
    /// not reloaded).</summary>
    internal void OpenCheckpoints(string recording, bool afterStop)
    {
        try
        {
            var key = Path.GetFullPath(recording);
            // An open window for the same test is reloaded (it may predate checkpoints added since), unless that would drop unsaved names.
            if (_checkpointsWins.TryGetValue(key, out var cur)) { if (!cur.HasPendingEdits) cur.Load(); cur.Activate(); return; }
            var w = new CheckpointsWindow(this, recording, afterStop);
            w.Closed += (_, _) => { if (_checkpointsWins.TryGetValue(key, out var x) && ReferenceEquals(x, w)) _checkpointsWins.Remove(key); };
            _checkpointsWins[key] = w;
            AcApp.ShowModelessWindow(w);
        }
        catch (Exception ex) { Log.Error("checkpoints window", ex); _checkpointsWins.Remove(Path.GetFullPath(recording)); PrintLine("The checkpoints window could not open: " + ex.Message); }
    }

    /// <summary>Plugin shutdown: closes the windows without asking (unsaved names are dropped; recording.jsonl is only written by Save).</summary>
    void CloseCheckpoints()
    {
        foreach (var w in _checkpointsWins.Values.ToList()) try { w.CloseWithoutAsking(); } catch (Exception ex) { Log.Error("close checkpoints window", ex); }
        _checkpointsWins.Clear();
    }

    /// <summary>After RETRACESTOP wrote the steps: opens the window if the setting is on and the recording has checkpoints (checked off the UI thread).</summary>
    void ReviewAfterStop(string recording)
    {
        if (!RetraceSettings.Load().ReviewCheckpoints) return;
        var disp = Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            try { return File.ReadLines(recording).Any(l => l.Contains("\"type\":\"mark\"", StringComparison.Ordinal)); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result) disp.BeginInvoke(() => OpenCheckpoints(recording, afterStop: true));
        });
    }

    /// <summary>Renames in the background, then writes the steps (and Word / Excel when the settings ask for them or the files exist).
    /// <paramref name="done"/> runs on the window's thread with (ok, message). False when the job could not start (the message was shown).</summary>
    internal bool CheckpointsSave(string recording, IReadOnlyDictionary<long, string> names, Action<bool, string> done)
    {
        _checkpointsWins.TryGetValue(Path.GetFullPath(recording), out var win);
        var disp = win?.UiDispatcher ?? Dispatcher.CurrentDispatcher;
        void Done(bool ok, string msg) => disp.BeginInvoke(() => { try { done(ok, msg); } catch (Exception ex) { Log.Error("checkpoints saved", ex); } });
        if (Recorder.Instance.Writer is { } w && string.Equals(Path.GetFullPath(w.Path), Path.GetFullPath(recording), StringComparison.OrdinalIgnoreCase))
        { Done(false, "This test is still being recorded. Stop it first (RETRACESTOP)."); return false; }
        var copy = names.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (!EngineJobs.Start(_ => { var s = RetraceSettings.Load(); return EngineRunner.RenameCheckpoints(recording, copy, s.ExportDocx, s.ExportXlsx); }, r =>
            {
                if (r.Success)
                {
                    var files = string.Join(", ", new[] { r.Script, r.Docx, r.Xlsx }.Where(f => f != null).Select(f => Path.GetFileName(f!)));
                    var msg = files.Length > 0 ? $"Saved. {files} {(files.Contains(',') ? "were" : "was")} written again with the new names." : "Saved.";
                    Done(true, msg + (r.Warnings.Count > 0 ? " Note: " + string.Join(" ", r.Warnings) : ""));
                }
                else if (r.Cancelled) Done(false, "Cancelled. The names may not all be saved: open the window again to check.");
                else { Done(false, "Not saved: " + r.Error); PrintLine("RETRACECHECKPOINTS: " + r.Error); }
            }, out var err))
        { Done(false, "Not saved: " + err); return false; }
        return true;
    }

    internal void CheckpointsOpenSteps(string recording, Action<string> say)
    {
        var md = TestFolders.ScriptPath(recording);
        if (File.Exists(md)) { OpenScriptFile(md); say("Opened " + md); }
        else say("There is no steps.md yet.");
    }

    internal void CheckpointsOpenPicture(string file, Action<string> say)
    {
        if (!File.Exists(file)) { say("The picture is gone: " + file); return; }
        OpenPath(file, Path.GetDirectoryName(file) ?? "");
    }

    /// <summary>The Open test window's "Checkpoints" button.</summary>
    internal void TestCheckpoints(TestEntry e, Action<string> say)
    {
        if (!File.Exists(e.Recording)) { say("recording.jsonl not found in " + e.Folder); return; }
        OpenCheckpoints(e.Recording, afterStop: false);
    }
}
