using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using Autodesk.AutoCAD.EditorInput;
using Retrace.Engine;
using Retrace.Engine.Map;
using Retrace.Ui;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

// RETRACEMAP: the Map window and what its buttons do, and -RETRACEMAP, the command-line form. Same rules as the Open test window: never throw
// into AutoCAD; errors go to retrace.log and one line on the command line; no disk work on the UI thread beyond starting a process; the
// engine runs in the background (EngineJobs). The window never touches AutoCAD: what it needs from AutoCAD comes through here.
public sealed partial class Commands
{
    MapWindow? _mapWin;
    bool _mapRunning;
    EngineResult? _lastMap;

    /// <summary>A map started here is running or waiting.</summary>
    internal bool MapRunning => _mapRunning;
    /// <summary>The last map finished in this AutoCAD session (also one that could not be written), null before the first. A window
    /// opened later shows it, so closing the window does not lose the table.</summary>
    internal EngineResult? LastMap => _lastMap;

    // ---- the commands -----------------------------------------------------------------------------------------------------

    /// <summary>RETRACEMAP: the Map window (one, modeless; brought to the front when it is open). With FILEDIA 0, or when a script or LISP
    /// runs the command, nobody can use a window: it asks on the command line, as -RETRACEMAP does.</summary>
    public void Map()
    {
        var ed = Ed;
        try
        {
            if (!FileDialogsOn() || Ask.ScriptRuns()) { MapCommandLine(); return; }
            if (_mapWin != null) { _mapWin.Reload(); _mapWin.BringToFront(); return; }
            var w = new MapWindow(this);
            w.Closed += (_, _) => { if (ReferenceEquals(_mapWin, w)) _mapWin = null; };
            _mapWin = w;
            AcApp.ShowModelessWindow(w);
        }
        catch (Exception ex) { Log.Error("RETRACEMAP", ex); _mapWin = null; ed?.WriteMessage($"\nRETRACEMAP failed: {ex.Message}\n"); }
    }

    /// <summary>-RETRACEMAP: the same map, asked on the command line (for scripts): the folders, then whether to map the live ribbon. The
    /// map runs in the background and prints its summary here when it is done.</summary>
    public void MapCommandLine()
    {
        var ed = Ed; if (ed == null) return;
        using var cl = Ask.CommandLineOnly();   // -RETRACEMAP is the command-line form
        try
        {
            var saved = MapFolders.Load();
            var last = saved.Checked;
            var a = Ask.String(ed, $"\nFolders to map, separated by ; <{(last.Count == 0 ? "none" : "ticked last time: " + string.Join("; ", last))}> (Enter keeps): ");
            if (a.Status == PromptStatus.Cancel) return;
            var typed = SplitFolders(a.StringResult);   // Enter on an empty line keeps the folders ticked last time
            var wanted = typed.Count > 0 ? typed : last.ToList();
            if (wanted.Count == 0) { ed.WriteMessage("\nNo folders; cancelled.\n"); return; }
            var folders = ExistingFolders(wanted, ed);
            if (folders.Count == 0) { ed.WriteMessage("\nNone of those folders exists; cancelled.\n"); return; }

            var lv = Ask.Keywords(ed, new PromptKeywordOptions($"\nAlso map the ribbon AutoCAD shows now [Yes/No] <{(saved.LiveRibbon ? "Yes" : "No")}>: ", "Yes No") { AllowNone = true });
            if (lv.Status == PromptStatus.Cancel) return;
            var live = lv.Status == PromptStatus.OK ? lv.StringResult == "Yes" : saved.LiveRibbon;

            var wait = EngineJobs.IsRunning;
            if (!StartMap(folders, live, saved.AllTabs, MapDoneCommandLine, out var error)) { ed.WriteMessage($"\nRETRACEMAP: {error}\n"); return; }
            ed.WriteMessage($"\nRETRACEMAP: {(wait ? "another Retrace job is running; the map is queued and made right after it" : "mapping")} " +
                            $"{folders.Count} folder{(folders.Count == 1 ? "" : "s")}{(live ? " and the ribbon AutoCAD shows now" : "")}. " +
                            "AutoCAD stays usable; the result is printed here when it is done.\n");
        }
        catch (Exception ex) { Log.Error("-RETRACEMAP", ex); ed.WriteMessage($"\n-RETRACEMAP failed: {ex.Message}\n"); }
    }

    // ---- starting a map ---------------------------------------------------------------------------------------------------

    /// <summary>Starts the map in the background. UI thread only: with <paramref name="live"/> it copies the live ribbon and reads the
    /// customization files AutoCAD has loaded now, because both need AutoCAD. <paramref name="onDoneUi"/> runs on the UI thread, once, when
    /// the map has finished, failed or been cancelled. Once the map has started, its folders go to the top of the list and the two options are
    /// kept (<see cref="MapFolders"/>): the list records what was chosen, also for a map that is cancelled later. False with the reason when
    /// the map could not start (e.g. another one is running); then the list is not changed. If the live ribbon cannot
    /// be read, the folders are mapped without it and the result carries a warning that says so. Never throws.</summary>
    internal bool StartMap(IReadOnlyList<string> folders, bool live, bool allTabs, Action<EngineResult> onDoneUi, out string error)
    {
        error = "";
        try
        {
            if (folders.Count == 0) { error = "No folders to map."; return false; }
            var disp = Dispatcher.CurrentDispatcher;
            IReadOnlyList<LiveTab>? snapshot = null;
            IReadOnlyList<string> loaded = [];
            string? liveProblem = null;
            if (live)
            {
                try
                {
                    snapshot = LiveRibbonReader.Snapshot();
                    loaded = LoadedCustomization();
                    if (snapshot.Count == 0) { snapshot = null; loaded = []; liveProblem = "the ribbon is not loaded in AutoCAD, so only the folders were mapped"; }
                }
                catch (Exception ex)
                {
                    Log.Error("map: the live ribbon", ex);
                    snapshot = null; loaded = [];
                    liveProblem = $"the live ribbon could not be read ({ex.Message}), so only the folders were mapped";
                }
            }
            var opt = new RibbonMapOptions { Folders = folders, Live = snapshot, Customization = loaded, AllLiveTabs = allTabs, Output = Paths.Map() };
            if (!EngineJobs.MapRibbon(opt, r => disp.BeginInvoke(() => MapFinished(r, liveProblem, onDoneUi)), out error)) return false;
            _mapRunning = true;
            RememberMapFolders(folders, live, allTabs);
            return true;
        }
        catch (Exception ex) { Log.Error("map: start", ex); error = ex.Message; return false; }
    }

    /// <summary>The map is done (UI thread): remember it, then tell whoever started it. A Map window that is open is told too when the
    /// command line started the map: it was opened while the map ran and waits for it.</summary>
    void MapFinished(EngineResult r, string? liveProblem, Action<EngineResult> onDoneUi)
    {
        _mapRunning = false;
        if (liveProblem != null) r.Warn(liveProblem);
        if (r.RibbonMap != null) _lastMap = r;
        try { onDoneUi(r); }
        catch (Exception ex) { Log.Error("map: done", ex); }
        if (onDoneUi == MapDoneForWindow) return;   // that one already went to the window, if there is one
        try { _mapWin?.MapDone(r); }
        catch (Exception ex) { Log.Error("map: done, window", ex); }
    }

    /// <summary>The folders of this map go to the top of the list, ticked, and the other folders are unticked.</summary>
    void RememberMapFolders(IReadOnlyList<string> folders, bool live, bool allTabs)
    {
        try
        {
            var f = MapFolders.Load();
            f.Use(folders);
            f.LiveRibbon = live;
            f.AllTabs = allTabs;
            if (!f.Save(out var why)) PrintLine("RETRACEMAP: the folder list could not be saved: " + why);
        }
        catch (Exception ex) { Log.Error("map: remember folders", ex); }
    }

    /// <summary>A map started from the window is done: the window shows it; if the window was closed meanwhile, the command line does.</summary>
    internal void MapDoneForWindow(EngineResult r)
    {
        if (_mapWin is { } w) w.MapDone(r); else MapDoneCommandLine(r);
    }

    void MapDoneCommandLine(EngineResult r)
    {
        try { Print(MapSummary(r)); }
        catch (Exception ex) { Log.Error("map: summary", ex); }
    }

    /// <summary>What the command line prints when a map is done: the engine's summary lines, the warnings (the first five) and the files.</summary>
    static string MapSummary(EngineResult r)
    {
        if (r.Cancelled) return "RETRACEMAP: cancelled.";
        if (r.RibbonMap == null) return "RETRACEMAP FAILED: " + r.Error;
        var sb = new StringBuilder(r.Success ? "RETRACEMAP: done." : "RETRACEMAP: the map is made but could not be saved.");
        foreach (var m in r.Messages) sb.Append("\n  ").Append(m);
        if (r.Warnings.Count > 0)
        {
            sb.Append($"\n  {r.Warnings.Count} warning{(r.Warnings.Count == 1 ? "" : "s")}:");
            foreach (var w in r.Warnings.Take(5)) sb.Append("\n    ").Append(w);
            if (r.Warnings.Count > 5) sb.Append($"\n    ... and {r.Warnings.Count - 5} more");
        }
        if (r.Success && r.Output != null)
        {
            foreach (var name in new[] { RibbonMapWriter.MarkdownFile, RibbonMapWriter.CsvFile, RibbonMapWriter.JsonFile })
                sb.Append("\n  ").Append(Path.Combine(r.Output, name));
        }
        else if (r.Error != null) sb.Append("\n  ").Append(r.Error);
        return sb.ToString();
    }

    /// <summary>"C:\a; \"C:\b c\"" gives two folders: split at ';', trimmed, quotes and a trailing separator removed, each once.</summary>
    static List<string> SplitFolders(string? typed) =>
        (typed ?? "").Split(';').Select(MapFolders.Normalize).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The folders that exist; the others are printed and left out. A path that does not answer within three seconds (a network
    /// drive that is away) counts as missing, so AutoCAD never waits longer.</summary>
    static List<string> ExistingFolders(List<string> wanted, Editor ed)
    {
        var checks = wanted.Select(p => Task.Run(() => Path.IsPathRooted(p) && Directory.Exists(p))).ToArray();
        try { Task.WaitAll(checks, TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        var found = new List<string>();
        for (var i = 0; i < wanted.Count; i++)
        {
            if (checks[i].IsCompletedSuccessfully && checks[i].Result) found.Add(wanted[i]);
            else ed.WriteMessage($"\nNot found, left out: {wanted[i]}\n");
        }
        return found;
    }

    // ---- button actions (called on the UI thread from the window) ----------------------------------------------------------

    /// <summary>Plugin shutdown: closes the window and forgets the last map.</summary>
    void CloseMap()
    {
        try { var w = _mapWin; _mapWin = null; _lastMap = null; w?.Close(); } catch (Exception ex) { Log.Error("close map window", ex); }
    }

    /// <summary>Opens a place from the map. A file opens in the editor of the settings at its line. A button inside a .cuix cannot be opened
    /// at a line (the file is a zip): Explorer shows the file instead and <paramref name="say"/> tells where the button is. The file is checked
    /// and the program started on a thread-pool thread, because a folder on a network drive that is away can take long to answer;
    /// <paramref name="say"/> must accept a call from that thread (MapWindow.Say does).</summary>
    internal void OpenSource(MapSource src, Action<string> say)
    {
        var disp = Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(src.Path)) { say("Not found: " + src.Path); return; }
                if (src.Part != null)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{src.Path}\"") { UseShellExecute = true });
                    say($"{Path.GetFileName(src.Path)} is a CUIX file: the button is in {src.Part}{(src.Line > 0 ? $", line {src.Line}" : "")}. Open it with CUI.");
                    return;
                }
                var editor = RetraceSettings.Load().Editor;
                try
                {
                    Process.Start(new ProcessStartInfo(editor, EditorLaunch.Arguments(editor, src.Path, src.Line)) { UseShellExecute = true });
                    var file = Path.GetFileName(src.Path);
                    if (src.Line <= 0) say($"Opened {file} in {editor}");
                    else if (EditorLaunch.JumpsToLine(editor)) say($"Opened {file} at line {src.Line} in {editor}");
                    else say($"Opened {file} in {editor}; the code is at line {src.Line} (this editor cannot open a file at a line; VS Code and Notepad++ can).");
                }
                catch (Exception ex)
                {
                    Log.Error("open in editor " + src.Path, ex);
                    disp.BeginInvoke(() => PrintLine($"Could not start the editor ({editor}): {ex.Message}. Change it in RETRACESETTINGS. Trying the default app."));
                    OpenPath(src.Path, Path.GetDirectoryName(src.Path)!);
                    say("The editor did not start; tried the default app.");
                }
            }
            catch (Exception ex) { Log.Error("open source " + src, ex); say("Could not open it: " + ex.Message); }
        });
    }

    /// <summary>Opens a file of the last map (<see cref="RibbonMapWriter.MarkdownFile"/>, <see cref="RibbonMapWriter.CsvFile"/>) in the
    /// default app, or the map folder itself when <paramref name="name"/> is empty.</summary>
    internal void OpenMapFile(string name, Action<string> say)
    {
        try
        {
            var dir = Paths.Map();
            if (name.Length == 0)
            {
                if (!Directory.Exists(dir)) { say("There is no map yet. Press Map first."); return; }
                OpenFolderPath(dir);
                say("Opened " + dir);
                return;
            }
            var file = Path.Combine(dir, name);
            if (!File.Exists(file)) { say("There is no " + name + " yet. Press Map first."); return; }
            try
            {
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                say("Opened " + file);
            }
            catch (Exception ex)
            {
                Log.Error("open map file " + file, ex);
                OpenFolderPath(dir);
                say($"No program opens {name}; the folder is open instead.");
            }
        }
        catch (Exception ex) { Log.Error("open map file " + name, ex); say("Could not open it: " + ex.Message); }
    }
}
