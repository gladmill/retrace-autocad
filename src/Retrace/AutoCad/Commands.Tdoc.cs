using Autodesk.AutoCAD.EditorInput;
using Retrace.Ui;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

// RETRACETDOC: the window that picks the tests, and -RETRACETDOC, the same from the command line. The run itself is TdocRun. Rules as for the other
// windows: never throw into AutoCAD; errors go to retrace.log and one line on the command line; no disk reads on the UI thread (the window reads on a pool thread).
public sealed partial class Commands
{
    TdocWindow? _tdocWin;

    /// <summary>RETRACETDOC: the picker. While a run is active it says so (between two tests it ends the run).</summary>
    public void Tdoc()
    {
        var ed = Ed;
        try
        {
            if (TdocRun.Current != null) { Print("RETRACETDOC: " + TdocRun.StopOrReport()); return; }
            if (_tdocWin != null) { _tdocWin.Reload(); _tdocWin.Activate(); return; }
            var w = new TdocWindow(this);
            w.Closed += (_, _) => { if (ReferenceEquals(_tdocWin, w)) _tdocWin = null; };
            _tdocWin = w;
            AcApp.ShowModelessWindow(w);
        }
        catch (Exception ex) { Log.Error("RETRACETDOC", ex); _tdocWin = null; ed?.WriteMessage($"\nRETRACETDOC failed: {ex.Message}\n"); }
    }

    void CloseTdoc()
    {
        try { var w = _tdocWin; _tdocWin = null; w?.Close(); } catch (Exception ex) { Log.Error("close Tdoc window", ex); }
    }

    /// <summary>-RETRACETDOC: asks for the title, then the tests (names or paths separated by ';', or @file with one per line), and starts the run.
    /// A name that finds no test stops it before anything runs.</summary>
    public void TdocCommandLine()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            if (TdocRun.Current != null) { Print("RETRACETDOC: " + TdocRun.StopOrReport()); return; }
            using var cl = Ask.CommandLineOnly();
            var t = Ask.String(ed, "\nTitle of the training document <Training>: ");
            if (t.Status == PromptStatus.Cancel) return;
            var title = t.Status == PromptStatus.OK && t.StringResult.Trim().Length > 0 ? t.StringResult.Trim() : "Training";
            var l = Ask.String(ed, "\nTests (names or folders separated by ;, last = the newest, or @file with one per line): ");
            if (l.Status != PromptStatus.OK) { ed.WriteMessage("\nRETRACETDOC: no tests given.\n"); return; }
            var tests = RetraceSettings.Load().Tests;
            var names = TdocTestList.Tokens(l.StringResult, out var readError);
            var recordings = TdocTestList.Resolve(names, tests, out var problems);
            if (readError != null) problems.Insert(0, readError);
            if (problems.Count > 0)
            {
                ed.WriteMessage("\nRETRACETDOC: nothing started.\n" + string.Join("\n", problems.Select(p => "  " + p)) + "\n");
                return;
            }
            if (!TdocRun.Start(title, recordings, out var msg)) ed.WriteMessage($"\nRETRACETDOC: {msg}\n");
        }
        catch (Exception ex) { Log.Error("-RETRACETDOC", ex); ed.WriteMessage($"\n-RETRACETDOC failed: {ex.Message}\n"); }
    }

    /// <summary>The window's Start button. True when the run started (the window closes).</summary>
    internal bool TdocStart(string title, IReadOnlyList<string> recordings, Action<string> say)
    {
        if (!TdocRun.Start(title, recordings, out var msg)) { say(msg); return false; }
        return true;
    }
}
