namespace Retrace.AutoCad;

/// <summary>Error log (`retrace.log` in the root folder). A recorder must never break AutoCAD, so handlers log and swallow.</summary>
static class Log
{
    static readonly object Lock = new();
    static string? _path;

    public static void Error(string where, Exception ex)
    {
        try
        {
            lock (Lock)
            {
                _path ??= Paths.LogFile();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, $"{DateTime.Now:o} {where}: {ex}\n");
            }
        }
        catch { /* nothing more we can do */ }
    }

    /// <summary>A plain note (not an error) in the same file.</summary>
    public static void Info(string text)
    {
        try
        {
            lock (Lock)
            {
                _path ??= Paths.LogFile();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, $"{DateTime.Now:o} info: {text}\n");
            }
        }
        catch { /* nothing more we can do */ }
    }

    public static void Safe(string where, Action a)
    {
        try { a(); } catch (Exception ex) { Error(where, ex); }
    }
}
