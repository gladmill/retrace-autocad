using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Engine;

/// <summary>The engine's entry points, called in-process by the plugin (and by the tests). Every method is synchronous, never throws and never
/// writes to the console: it returns an <see cref="EngineResult"/>. They are safe to run on a background thread (Core/EngineJobs does).
/// Only <see cref="BuildDocs"/> takes long enough to need the <see cref="CancellationToken"/>; it is checked between pipeline stages.</summary>
public static class EngineRunner
{
    /// <summary>Reference docs, the editable user guide and (with <see cref="EngineOptions.Sessions"/>) the recordings pages.</summary>
    public static EngineResult BuildDocs(EngineOptions opt, CancellationToken ct = default) => Guard(r =>
    {
        if (!Directory.Exists(opt.Input)) { r.Fail($"input folder not found: {opt.Input}"); return; }
        foreach (var sp in opt.Sessions)
            if (!Directory.Exists(sp) && !File.Exists(sp)) { r.Fail($"sessions path not found: {sp}"); return; }
        var model = Pipeline.Run(opt, ct);
        r.Output = opt.Output;
        r.Info($"input:    {opt.Input}");
        r.Info($"output:   {opt.Output}");
        r.Info($"projects: {model.Projects.Count}, files: {model.Files.Count}");
        r.Info($"ribbon:   {model.Cuix.Sum(c => c.Tabs.Count)} CUIX tabs, {model.CodeTabs.Count} code tabs");
        r.Info($"commands: {model.Commands.Count}, forms: {model.Forms.Count}");
        if (opt.Sessions.Count > 0) r.Info($"sessions: how-to pages in {Path.Combine(opt.Output, "guide", "howto")}, recordings in {Path.Combine(opt.Output, RecordingWriter.Root)}");
        foreach (var s in model.Skipped) r.Info($"skipped:  {s}");
        foreach (var w in model.Warnings) r.Warn(w);
    });

    /// <summary>The ribbon map (<see cref="Map.RibbonMapper"/>): every ribbon item with the code it runs, from the folders in
    /// <paramref name="opt"/> and the live ribbon; written to <see cref="Map.RibbonMapOptions.Output"/> (ribbon-map.md / .csv / .json) when
    /// it is set. <see cref="EngineResult.RibbonMap"/> holds the map, <see cref="EngineResult.Output"/> the output folder. Fails when no
    /// folder exists; a map that cannot be written is still returned, with the error.</summary>
    public static EngineResult MapRibbon(Map.RibbonMapOptions opt, CancellationToken ct = default) => Guard(r =>
    {
        var map = Map.RibbonMapper.Build(opt, ct);
        foreach (var w in map.Warnings) r.Warn(w);
        if (map.Folders.Count == 0) { r.Fail("none of the folders exists"); return; }
        r.RibbonMap = map;
        r.Info($"folders:  {map.Folders.Count}, files: {map.FilesRead}");
        r.Info($"ribbon:   {map.Rows.Count} items: {map.Mapped} mapped to code, {map.NotFound} not found, {map.NoAction} with no action");
        r.Info(map.LiveMapped ? $"live:     on, {map.LiveTabsLeftOut} tabs left out" : "live:     off");
        if (opt.Output == null) return;
        r.Output = opt.Output;
        try
        {
            Map.RibbonMapWriter.Write(map, opt.Output);
            r.Info($"output:   {opt.Output}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            r.Fail($"could not write the map: {ex.Message}");
        }
    });

    /// <summary>One recording -> steps.md next to it (pictures renamed after the steps), and optionally steps.docx / steps.xlsx made from it.
    /// <paramref name="sourceMap"/>: an earlier BuildDocs output folder (or its data\sourcemap.json), to link ribbon steps to the reference pages.</summary>
    public static EngineResult WriteSteps(string recording, bool docx = false, bool xlsx = false, string? sourceMap = null) => Guard(r =>
    {
        List<string> files;
        try { files = RecordingReader.Expand([recording]); }
        catch (FileNotFoundException ex) { r.Fail(ex.Message); return; }
        if (files.Count != 1 || Directory.Exists(recording)) { r.Fail("WriteSteps takes exactly one recording file"); return; }
        var index = LoadMap(sourceMap, r);
        if (r.Error != null) return;
        var warnings = new List<string>();
        var script = ScriptWriter.WriteFor(files[0], index, warnings, out var bad);
        if (bad > 0) r.Warn($"{bad} unreadable line(s) skipped");
        foreach (var w in warnings) r.Warn(w);
        r.Script = script;
        r.Info($"script:   {script}");
        if (docx || xlsx) Exporter.Export(script, docx, xlsx, r);
    });

    /// <summary>Renames checkpoints in a recording (event n -> name; empty = its automatic name), then writes its steps.md again, and steps.docx /
    /// steps.xlsx when <paramref name="docx"/> / <paramref name="xlsx"/> ask for them or they exist. A Word or Excel file changed after Retrace
    /// wrote it (a tester may have filled in results) is kept and named in a warning. Fails only when the names could not be saved; when
    /// writing the steps or files fails afterwards, the names are saved and the result carries a warning.</summary>
    public static EngineResult RenameCheckpoints(string recording, IReadOnlyDictionary<long, string> names, bool docx = false, bool xlsx = false) => Guard(r =>
    {
        if (!File.Exists(recording)) { r.Fail($"not found: {recording}"); return; }
        int changed;
        try { changed = Checkpoints.Rename(recording, names); }
        catch (InvalidDataException ex) { r.Fail(ex.Message); return; }
        r.Info($"renamed:  {changed} checkpoint(s)");
        var dir = Path.GetDirectoryName(recording) ?? "";
        var md = Path.Combine(dir, TestFolders.StepsFile);
        bool Want(string file, bool setting)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) return setting;
            // Without a stamp (written by an older version), a file newer than steps.md was edited.
            var own = Exporter.WrittenByRetrace(path)
                      ?? !(File.Exists(md) && File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(md).AddMinutes(2));
            if (own) return true;
            Exporter.MarkKept(path);
            r.Warn($"{file} was changed after Retrace wrote it, so it was kept (results filled in?). Open test makes a new one.");
            return false;
        }
        bool d = Want("steps.docx", docx), x = Want("steps.xlsx", xlsx);
        EngineResult steps;
        try { steps = WriteSteps(recording, d, x); }
        catch (Exception ex) { r.Warn($"the names are saved, but the steps were not written again: {ex.Message}"); return; }
        r.Script = steps.Script; r.Docx = steps.Docx; r.Xlsx = steps.Xlsx;
        foreach (var m in steps.Messages) r.Info(m);
        foreach (var w in steps.Warnings) r.Warn(w);
        if (steps.Error != null) r.Warn($"the names are saved, but the steps were not written again: {steps.Error}");
    });

    /// <summary>Word and / or Excel version of an existing script. <paramref name="path"/> is a recording.jsonl, a test folder or a steps.md
    /// (a recording without a script gets one first). Neither flag set means both.</summary>
    public static EngineResult Export(string path, bool docx, bool xlsx, Action<string, string>? replace = null) => Guard(r =>
    {
        if (!docx && !xlsx) docx = xlsx = true;
        var warnings = new List<string>();
        var steps = Exporter.ResolveSteps(path, warnings, out var error);
        foreach (var w in warnings) r.Warn(w);
        if (steps == null) { r.Fail(error ?? $"not found: {path}"); return; }
        r.Script = steps;
        Exporter.Export(steps, docx, xlsx, r, replace);
    });

    /// <summary>The Tdoc training document: the Word file for a `tdoc.json` (<see cref="TrainingManifest"/>). Pictures are looked up beside the manifest;
    /// a missing one becomes a note in the document and a warning here. Written through a temp file; when <paramref name="docxPath"/> is open in
    /// Word, `... (new).docx` is written instead (warning). <see cref="EngineResult.Docx"/> is the file written.</summary>
    public static EngineResult WriteTrainingDoc(string manifestPath, string docxPath) => Guard(r =>
    {
        var manifest = TrainingManifest.Read(manifestPath);
        if (manifest == null) { r.Fail($"cannot read the training manifest: {manifestPath}"); return; }
        var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "";
        var target = Path.GetFullPath(docxPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
            (int Embedded, int Missing) counts = default;
            var path = Exporter.WriteAtomic(target, t => counts = TrainingDocx.Build(manifest, folder, t), out var fell);
            if (fell) r.Warn($"{Path.GetFileName(target)} is open in Word; wrote {Path.GetFileName(path)} instead (close the old one to get the usual name)");
            if (counts.Missing > 0) r.Warn($"{counts.Missing} picture file(s) not found beside {Path.GetFileName(manifestPath)}; the document has a note in their place");
            r.Docx = path;
            r.Info($"docx:     {path} ({manifest.Tests.Count} test(s), {counts.Embedded} picture(s))");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException
                                       or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            r.Fail($"cannot write {Path.GetFileName(target)}: {ex.Message}");
        }
    });

    /// <summary>The recordings report: <c>&lt;output&gt;/recordings</c> (index.md with the most used buttons, hot sequences and ribbon coverage,
    /// and one page per test) and <c>&lt;output&gt;/data/recordings.json</c>. <paramref name="sourceMap"/> enables the coverage section.</summary>
    public static EngineResult AnalyzeRecordings(IReadOnlyList<string> inputs, string output, string? sourceMap = null, int ngram = 4, int top = 25) => Guard(r =>
    {
        if (inputs.Count == 0) { r.Fail("no recordings given"); return; }
        List<string> files;
        try { files = RecordingReader.Expand(inputs); }
        catch (FileNotFoundException ex) { r.Fail(ex.Message); return; }
        if (files.Count == 0) { r.Fail("no recording.jsonl found"); return; }
        var index = LoadMap(sourceMap, r);
        if (r.Error != null) return;

        var sessions = files.Select(RecordingReader.Load).ToList();
        var analysis = RecordingAnalysis.Run(sessions, index, ngram, top);
        var outRoot = Path.GetFullPath(output);
        var writer = new RecordingWriter(analysis, outRoot);
        var count = writer.Write();

        r.Output = Path.Combine(outRoot, DocsLayout.Recordings);
        r.Info($"sessions: {sessions.Count}, ribbon clicks: {sessions.Sum(s => s.Steps.Count(x => x.Kind == "ribbon"))}, commands: {analysis.Commands.Sum(c => c.Total)}");
        if (index != null)
        {
            var done = analysis.Coverage.Sum(c => c.Exercised.Count);
            var all = analysis.Coverage.Sum(c => c.Total);
            r.Info($"coverage: {done} of {all} sourcemap buttons exercised; {analysis.UnmatchedRibbon.Count} clicks not in the sourcemap");
        }
        foreach (var w in writer.Warnings) r.Warn(w);
        foreach (var s in sessions.Where(s => s.BadLines > 0)) r.Warn($"{s.Name}: {s.BadLines} unreadable line(s) skipped");
        r.Info($"output:   {r.Output} ({count} files)");
    });

    static SourceMapIndex? LoadMap(string? map, EngineResult r)
    {
        if (map == null) return null;
        try { return SourceMapIndex.Load(map); }
        catch (Exception ex) when (ex is FileNotFoundException or System.Text.Json.JsonException or KeyNotFoundException)
        { r.Fail($"cannot read sourcemap: {ex.Message}"); return null; }
    }

    /// <summary>Runs a job; whatever it throws becomes the result's Error (the host must survive an engine bug), cancelling becomes Cancelled.</summary>
    static EngineResult Guard(Action<EngineResult> job)
    {
        var r = new EngineResult();
        try { job(r); }
        catch (OperationCanceledException) { r.Cancelled = true; }
        catch (Exception ex) { r.Error ??= $"{ex.GetType().Name}: {ex.Message}"; }
        return r;
    }
}
