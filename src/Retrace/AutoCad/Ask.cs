using Autodesk.AutoCAD.EditorInput;
using Retrace.Core;
using Retrace.Ui;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// Every question Retrace's own commands ask: a name, a file, a folder or a choice. With the setting promptStyle "commandLine" (the default)
/// on the command line, as before; with "ui" in <see cref="PromptWindow"/>, a window on top of AutoCAD (files and folders with Browse... and
/// recent places). A command run from a script or LISP, or with FILEDIA 0, always gets the command line, so scripted runs keep working.
/// The answer comes back as the command line would give it (OK with the text, None for an empty Enter, Cancel), so callers read it the same way.
/// </summary>
static class Ask
{
    public enum Of { Text, File, Folder }

    /// <summary>The answer: <see cref="Status"/> OK (text or keyword in <see cref="StringResult"/>), None (Enter on an empty line) or Cancel.</summary>
    public sealed record Answer(PromptStatus Status, string StringResult);

    static int _commandLineOnly;
    static (DateTime Written, bool Ui) _setting = (DateTime.MinValue, false);

    /// <summary>Ask on the command line until disposed, whatever the setting: the command-line forms (-RETRACESETTINGS, -RETRACEMARK).</summary>
    public static IDisposable CommandLineOnly() { _commandLineOnly++; return new Scope(); }
    sealed class Scope : IDisposable { bool _done; public void Dispose() { if (!_done) { _done = true; _commandLineOnly--; } } }

    /// <summary>FILEDIA is on (file dialogs allowed). An error counts as on.</summary>
    public static bool FileDialogsOn()
    {
        try { return Convert.ToInt32(AcApp.GetSystemVariable("FILEDIA")) != 0; } catch (Exception) { return true; }
    }

    /// <summary>The promptStyle setting, read again only when settings.json changed.</summary>
    static bool UiSetting()
    {
        try
        {
            var file = Paths.SettingsFile();
            var written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (written != _setting.Written) _setting = (written, RetraceSettings.Load().UiPrompts);
            return _setting.Ui;
        }
        catch (Exception ex) { Log.Error("Ask setting", ex); return false; }
    }

    /// <summary>Whether to ask in the window: the setting says "ui", FILEDIA is on, no script or LISP runs the command (CMDACTIVE 4 / 32),
    /// and no command-line form asked for the command line.</summary>
    public static bool InWindow()
    {
        if (_commandLineOnly > 0 || !UiSetting() || !FileDialogsOn()) return false;
        try { return (Convert.ToInt32(AcApp.GetSystemVariable("CMDACTIVE")) & (4 | 32)) == 0; }
        catch (Exception ex) { Log.Error("Ask.InWindow", ex); return false; }
    }

    /// <summary>A line of text, a file or a folder. <paramref name="recent"/>: the recent-places list a file or folder answer goes to (and comes
    /// from), one per question (e.g. "recording", "folder:docs"). <paramref name="filter"/>: the file dialog's filter. <paramref name="initial"/>:
    /// the text the box starts with. <paramref name="browseFrom"/>: where Browse... starts when the box and the recent places give no folder.</summary>
    public static Answer String(Editor ed, string message, Of kind = Of.Text, string? recent = null, string? filter = null, string initial = "", string? browseFrom = null)
    {
        if (!InWindow())
        {
            var r = ed.GetString(new PromptStringOptions(message) { AllowSpaces = true });
            return new Answer(r.Status, r.StringResult ?? "");
        }
        var places = kind != Of.Text && recent != null ? RecentPlaces.Load() : null;
        var w = new PromptWindow("Retrace", message, kind switch { Of.File => PromptWindow.Kind.File, Of.Folder => PromptWindow.Kind.Folder, _ => PromptWindow.Kind.Text },
            initial, recent: places?.List(recent!), filter: filter, browseFrom: browseFrom);
        var text = Show(w);
        if (text == null) return new Answer(PromptStatus.Cancel, "");
        var t = text.Trim();
        if (t.Length == 0) return new Answer(PromptStatus.None, "");
        var path = t.Trim('"');
        if (places != null && (File.Exists(path) || Directory.Exists(path))) places.Add(recent!, path);
        return new Answer(PromptStatus.OK, t);
    }

    /// <summary>A choice among the prompt's keywords; the window shows one button per keyword, the default first.</summary>
    public static Answer Keywords(Editor ed, PromptKeywordOptions o)
    {
        if (!InWindow())
        {
            var r = ed.GetKeywords(o);
            return new Answer(r.Status, r.StringResult ?? "");
        }
        var names = o.Keywords.Cast<Keyword>().Where(k => k.Visible && k.Enabled).Select(k => k.GlobalName).ToList();
        // the default: the prompt's own, else the one the prompt text shows as "<No>" (what Enter takes on the command line)
        var def = !string.IsNullOrEmpty(o.Keywords.Default) ? o.Keywords.Default : DefaultIn(o.Message, names);
        if (!string.IsNullOrEmpty(def) && names.Remove(def)) names.Insert(0, def);
        var choice = Show(new PromptWindow("Retrace", o.Message, PromptWindow.Kind.Choice, choices: names));
        return choice == null ? new Answer(PromptStatus.Cancel, "") : new Answer(PromptStatus.OK, choice);
    }

    /// <summary>The keyword a prompt text names as its default, "... [Yes/No] &lt;No&gt;: " gives "No"; null when it names none of <paramref name="names"/>.</summary>
    static string? DefaultIn(string? message, IEnumerable<string> names)
    {
        if (message == null) return null;
        int close = message.LastIndexOf('>'), open = close > 0 ? message.LastIndexOf('<', close) : -1;
        if (open < 0) return null;
        var d = message[(open + 1)..close].Trim();
        return names.FirstOrDefault(n => string.Equals(n, d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Shows the window modal to AutoCAD (AutoCAD's own call: it disables the main window and keeps focus right) and returns its result.</summary>
    static string? Show(PromptWindow w)
    {
        AcApp.ShowModalWindow(AcApp.MainWindow.Handle, w, false);
        return w.Result;
    }
}
