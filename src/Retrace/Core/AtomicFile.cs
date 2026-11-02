namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>Writes a text file so a reader never sees half of it: a temp file next to the target, moved over it. The temp file is removed if the write fails.</summary>
internal static class AtomicFile
{
    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }
}
