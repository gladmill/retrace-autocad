// Word / Excel files from a steps.md (EngineRunner.Export, and the docx / xlsx options of EngineRunner.WriteSteps).
namespace Retrace.Engine.Recordings;

public static class Exporter
{
    public const string DocxName = "steps.docx";
    public const string XlsxName = "steps.xlsx";

    /// <summary>The script an export reads: a steps.md as given; for a folder or a recording.jsonl its script (written first if it does not exist yet).</summary>
    public static string? ResolveSteps(string input, List<string> warnings, out string? error)
    {
        error = null;
        input = Path.GetFullPath(input);
        if (Directory.Exists(input))
        {
            var steps = Path.Combine(input, TestFolders.StepsFile);
            if (File.Exists(steps)) return steps;
            input = Path.Combine(input, TestFolders.RecordingFile);
        }
        if (input.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(input)) { error = $"not found: {input}"; return null; }
            var steps = ScriptWriter.PathFor(input);
            return File.Exists(steps) ? steps : ScriptWriter.WriteFor(input, null, warnings, out _);
        }
        if (File.Exists(input)) return input;
        error = $"not found: {input}";
        return null;
    }

    /// <summary>`steps (new).docx`: where the export goes when the real file is locked (open in Word / Excel).</summary>
    public static string FallbackPath(string target) =>
        Path.Combine(Path.GetDirectoryName(target) ?? "", Path.GetFileNameWithoutExtension(target) + " (new)" + Path.GetExtension(target));

    /// <summary>Writes <paramref name="target"/> through a temp file in the same folder and replaces the target in one move. If the target is locked
    /// it writes <see cref="FallbackPath"/> instead (<paramref name="fellBack"/>). <paramref name="replace"/> is the move (a seam for tests).</summary>
    public static string WriteAtomic(string target, Action<string> write, out bool fellBack, Action<string, string>? replace = null)
    {
        replace ??= (from, to) => File.Move(from, to, overwrite: true);
        var dir = Path.GetDirectoryName(target) ?? "";
        var tmp = Path.Combine(dir, "." + Path.GetFileName(target) + ".tmp");
        fellBack = false;
        try
        {
            write(tmp);
            try { replace(tmp, target); return target; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var alt = FallbackPath(target);
                fellBack = true;
                replace(tmp, alt);
                return alt;
            }
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    // ---- which Word / Excel files Retrace wrote --------------------------------------------------------------------------
    // Next to steps.docx / steps.xlsx, a hidden ".steps.xlsx.retrace" holds the size and time of the file as Retrace wrote it, or "kept"
    // once Retrace kept a file someone changed. Saving checkpoint names rewrites steps.md, so its time can't tell an edited file apart.

    static string StampPath(string file) => Path.Combine(Path.GetDirectoryName(file) ?? "", "." + Path.GetFileName(file) + ".retrace");
    static string Fingerprint(string file) { var i = new FileInfo(file); return $"{i.Length} {i.LastWriteTimeUtc.Ticks}"; }

    static void WriteStamp(string file, string text)
    {
        var stamp = StampPath(file);
        try
        {
            if (File.Exists(stamp)) File.SetAttributes(stamp, FileAttributes.Normal);
            File.WriteAllText(stamp, text);
            if (OperatingSystem.IsWindows()) File.SetAttributes(stamp, FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // no stamp: the next save falls back to file times
    }

    /// <summary>Marks <paramref name="file"/> as written by Retrace just now.</summary>
    public static void MarkWritten(string file) { if (File.Exists(file)) WriteStamp(file, Fingerprint(file)); }

    /// <summary>Marks <paramref name="file"/> as someone's: later saves keep it too.</summary>
    public static void MarkKept(string file) => WriteStamp(file, "kept");

    /// <summary>true: <paramref name="file"/> is unchanged since Retrace wrote it; false: changed since, or kept before; null: no stamp (older version).</summary>
    public static bool? WrittenByRetrace(string file)
    {
        var stamp = StampPath(file);
        try { return File.Exists(stamp) ? File.ReadAllText(stamp).Trim() == Fingerprint(file) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Writes the chosen files next to <paramref name="stepsMd"/>, recording what it did in <paramref name="r"/> (Docx / Xlsx paths, notes, Error).
    /// Returns false on failure. <paramref name="replace"/> is the move onto the real name (a seam for tests).</summary>
    public static bool Export(string stepsMd, bool docx, bool xlsx, EngineResult r, Action<string, string>? replace = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(stepsMd)) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(stepsMd);
        StepsDoc doc;
        try { doc = StepsDoc.Load(stepsMd); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { r.Fail($"cannot read {stepsMd}: {ex.Message}"); return false; }
        var errors = new List<string>();
        foreach (var (want, ext, label) in new[] { (docx, ".docx", "docx"), (xlsx, ".xlsx", "xlsx") })
        {
            if (!want) continue;
            var target = Path.Combine(folder, baseName + ext);
            try
            {
                string path = WriteAtomic(target, ext == ".docx" ? t => ExportDocx.Write(doc, t) : t => ExportXlsx.Write(doc, folder, t), out var fell, replace);
                if (!fell) MarkWritten(path);
                if (fell) r.Warn($"{Path.GetFileName(target)} is open in {(ext == ".docx" ? "Word" : "Excel")}; wrote {Path.GetFileName(path)} instead (close the old one to get the usual name)");
                r.Info($"{label + ":",-9} {path}");
                if (ext == ".docx") r.Docx = path; else r.Xlsx = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
            {
                errors.Add($"cannot write {Path.GetFileName(target)}: {ex.Message}");
            }
        }
        if (errors.Count > 0) r.Fail(string.Join("; ", errors));
        return errors.Count == 0;
    }
}
