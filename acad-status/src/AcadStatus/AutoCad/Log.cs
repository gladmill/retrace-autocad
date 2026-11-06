using System;
using System.Collections.Generic;
using System.IO;

namespace AcadStatus.AutoCad;

/// <summary>Error log: <c>%LOCALAPPDATA%\Gladmill\AcadStatus\acadstatus.log</c>. A status tool must never break AutoCAD, so handlers log and
/// swallow; each distinct message is written once per session (a tick that fails every second logs one line).</summary>
static class Log
{
    static readonly object Lock = new();
    static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    static string? _path;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gladmill", "AcadStatus");

    public static string SettingsFile => Path.Combine(Folder, "settings.json");

    public static void Error(string where, Exception ex)
    {
        var text = ex.GetType().Name + ": " + ex.Message;
        Write(where + "|" + text, where + ": " + ex);
    }

    public static void Info(string text) => Write("info|" + text, "info: " + text);

    static void Write(string key, string line)
    {
        try
        {
            lock (Lock)
            {
                if (Seen.Count > 200 || !Seen.Add(key)) return;
                _path ??= Path.Combine(Folder, "acadstatus.log");
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > 512 * 1024) File.Delete(_path);
                File.AppendAllText(_path, DateTime.Now.ToString("o") + " " + line + "\n");
            }
        }
        catch { /* nothing more we can do */ }
    }

    public static void Safe(string where, Action a)
    {
        try { a(); } catch (Exception ex) { Error(where, ex); }
    }
}
