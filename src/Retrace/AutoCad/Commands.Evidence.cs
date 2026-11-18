using Autodesk.AutoCAD.EditorInput;
using Retrace.Core;
using Retrace.Engine.Recordings;
using Retrace.Ui;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

// RETRACEEVIDENCE: the Evidence window (camera button per step, a filled-in workbook at the end); -RETRACEEVIDENCE: replays a test with a picture after every step and writes that workbook (script runs).
// Rules as for the other windows: never throw into AutoCAD; errors go to retrace.log and one line on the command line.
public sealed partial class Commands
{
    EvidenceWindow? _evidenceWin;
    static string? _evidencePath;   // the recording the next RETRACERUN should take evidence for (set with the queued test)

    /// <summary>RETRACEEVIDENCE: the window. <paramref name="testFolder"/>: the test to open it on (null: the newest, or the one it showed last).</summary>
    public void Evidence() => OpenEvidence(null);

    internal void OpenEvidence(string? testFolder)
    {
        var ed = Ed;
        try
        {
            if (_evidenceWin != null) { if (testFolder != null) _evidenceWin.ChooseTest(testFolder); _evidenceWin.Activate(); return; }
            var w = new EvidenceWindow(testFolder);
            w.Closed += (_, _) => { if (ReferenceEquals(_evidenceWin, w)) _evidenceWin = null; };
            _evidenceWin = w;
            AcApp.ShowModelessWindow(AcApp.MainWindow.Handle, w, false);   // not persisted: WindowPlacement centres it on AutoCAD every time
        }
        catch (Exception ex) { Log.Error("RETRACEEVIDENCE", ex); _evidenceWin = null; ed?.WriteMessage($"\nRETRACEEVIDENCE failed: {ex.Message}\n"); }
    }

    void CloseEvidence()
    {
        try { var w = _evidenceWin; _evidenceWin = null; w?.Close(); } catch (Exception ex) { Log.Error("close evidence window", ex); }
    }

    /// <summary>-RETRACEEVIDENCE: asks for the test (a name, a folder, a path to its recording.jsonl, or last = the newest) and replays it like RETRACERUN, with a picture after every step; the workbook is written when it ends.
    /// No verdict box. Starts nothing when the name finds no test.</summary>
    public void EvidenceCommandLine()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            using var cl = Ask.CommandLineOnly();
            var l = Ask.String(ed, "\nTest to replay with evidence (name, folder or path; last = the newest <last>): ");
            if (l.Status != PromptStatus.OK && l.Status != PromptStatus.None) return;
            var names = TdocTestList.Tokens(l.Status == PromptStatus.OK && l.StringResult.Trim().Length > 0 ? l.StringResult : "last", out var readError);
            var found = TdocTestList.Resolve(names.Take(1), RetraceSettings.Load().Tests, out var problems);
            if (readError != null) problems.Insert(0, readError);
            if (problems.Count > 0 || found.Count == 0)
            {
                ed.WriteMessage("\n-RETRACEEVIDENCE: nothing started.\n" + string.Join("\n", problems.Select(p => "  " + p)) + "\n");
                return;
            }
            if (RunRecording(found[0], msg => ed.WriteMessage($"\n-RETRACEEVIDENCE: {msg}\n"), evidence: true))
                ed.WriteMessage($"\n-RETRACEEVIDENCE: replaying {TestList.NameOf(TestFolders.NameOf(found[0]))}; the workbook is written when it ends.\n");
        }
        catch (Exception ex) { Log.Error("-RETRACEEVIDENCE", ex); ed.WriteMessage($"\n-RETRACEEVIDENCE failed: {ex.Message}\n"); }
    }

    /// <summary>For the top of <see cref="Test"/>: the recording an evidence run asked for, if any; clears the request (it never outlives one RETRACERUN, whatever that does).</summary>
    static string? TakeEvidenceWanted() { var want = _evidencePath; _evidencePath = null; return want; }

    /// <summary>The evidence capture of a test that was started for evidence: only when its recording is the one queued by the Evidence run (<paramref name="queued"/>, not one a suite or Tdoc run named or a person picked)
    /// and no suite or Tdoc run is active; else null.</summary>
    static EvidenceCapture? MakeEvidence(string? wanted, string? queued, string path, List<Step> steps)
    {
        if (wanted == null || queued == null || TestQueue.Active != null) return null;
        var same = string.Equals(Path.GetFullPath(wanted), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFullPath(queued), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        return same ? new EvidenceCapture(path, steps) : null;
    }
}
