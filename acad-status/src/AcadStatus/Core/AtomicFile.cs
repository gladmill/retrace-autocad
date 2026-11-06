using System;
using System.IO;
using System.Text;

namespace AcadStatus.Core;

/// <summary>Atomic write (CONTRACT.md, "Files"): the text goes to <c>&lt;file&gt;.tmp-&lt;pid&gt;</c> in the same folder, UTF-8 without BOM,
/// and is moved over the file, so a reader sees the old file or the new one, never half of one.
/// <para>One try, no sleeping: this runs on AutoCAD's UI thread once a second. A replace that fails (a reader that does not share delete, a
/// full disk) throws, the temp file is removed, and the caller tries again at its next refresh (<see cref="WriteRetry"/>). The folder is not
/// created here: the caller ensures it once, with its permissions, and again only when this throws <see cref="DirectoryNotFoundException"/>.</para></summary>
public static class AtomicFile
{
    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Throws when the file could not be replaced (the temp file is removed then), and a <see cref="DirectoryNotFoundException"/>
    /// when the folder is missing.</summary>
    public static void Write(string path, string text, int pid)
    {
        var tmp = path + ".tmp-" + pid;
        File.WriteAllBytes(tmp, Utf8.GetBytes(text));
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* nothing more to do */ }
            throw;
        }
    }
}
