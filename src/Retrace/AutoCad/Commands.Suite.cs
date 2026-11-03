using Autodesk.AutoCAD.EditorInput;
using Retrace.Ui;

namespace Retrace.AutoCad;

// RETRACESUITE: runs the ticked tests of the "Open test" window as one suite; -RETRACESUITE: the same from the command line (names, `all` or @file).
// The run itself is SuiteRun. Rules as for the other windows: never throw into AutoCAD; errors go to retrace.log and one line on the command line.
public sealed partial class Commands
{
    /// <summary>RETRACESUITE: with tests ticked in the open "Open test" window it runs them; otherwise it opens that window (tick tests, press
    /// Run suite). While a suite runs it says so (between two tests it ends the suite).</summary>
    public void Suite()
    {
        var ed = Ed;
        try
        {
            if (SuiteRun.Current != null) { Print("RETRACESUITE: " + SuiteRun.StopOrReport()); return; }
            var ticked = _openTestWin?.TickedRecordings() ?? [];
            if (ticked.Count > 0)
            {
                if (SuiteStart(ticked, Print)) CloseOpenTest();
                return;
            }
            OpenTest();
            _openTestWin?.Say("Tick the tests of the suite, then press Run suite.");
        }
        catch (Exception ex) { Log.Error("RETRACESUITE", ex); ed?.WriteMessage($"\nRETRACESUITE failed: {ex.Message}\n"); }
    }

    /// <summary>-RETRACESUITE: asks for the tests (names or paths separated by ';', `all` or Enter = every test, `last`, or @file with one per line)
    /// and starts the suite. No window. A name that finds no test stops it before anything runs. Progress and the summary go to the command line.</summary>
    public void SuiteCommandLine()
    {
        var ed = Ed; if (ed == null) return;
        try
        {
            if (SuiteRun.Current != null) { Print("RETRACESUITE: " + SuiteRun.StopOrReport()); return; }
            using var cl = Ask.CommandLineOnly();
            var l = Ask.String(ed, "\nTests (names or folders separated by ;, all = every test <all>, last = the newest, or @file with one per line): ");
            if (l.Status != PromptStatus.OK && l.Status != PromptStatus.None) return;
            var recordings = SuiteTestList.FromText(l.Status == PromptStatus.OK ? l.StringResult : "", RetraceSettings.Load().Tests, out var problems);
            if (problems.Count > 0)
            {
                ed.WriteMessage("\n-RETRACESUITE: nothing started.\n" + string.Join("\n", problems.Select(p => "  " + p)) + "\n");
                return;
            }
            if (!SuiteRun.Start(recordings, out var msg)) ed.WriteMessage($"\n-RETRACESUITE: {msg}\n");
        }
        catch (Exception ex) { Log.Error("-RETRACESUITE", ex); ed.WriteMessage($"\n-RETRACESUITE failed: {ex.Message}\n"); }
    }

    /// <summary>The Open test window's Run suite button. True when the suite started (the window closes).</summary>
    internal bool SuiteStart(IReadOnlyList<string> recordings, Action<string> say)
    {
        if (!SuiteRun.Start(recordings, out var msg)) { say(msg); return false; }
        return true;
    }
}
