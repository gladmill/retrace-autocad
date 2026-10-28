using System.Text;
using System.Text.Json;
using Retrace.Engine.Recordings;
using Retrace.Engine.Scan.Cuix;
using Retrace.Engine.Scan.Forms;

namespace Retrace.Engine.Emit;

/// <summary>Writes files under the output root, creating folders; paths are '/'-separated and relative.</summary>
public sealed class OutputSink(string root)
{
    public int Count { get; private set; }

    public void Write(string rel, string content)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(false));
        Count++;
    }
}

public static class Writer
{
    public static void WriteAll(EngineModel model, EngineOptions opt, CancellationToken ct = default)
    {
        var sink = new OutputSink(opt.Output);
        var ix = new ModelIndex(model, opt);

        foreach (var tab in ix.AllTabs)
            Asset(model, sink, ix.TabAsset[tab], () => TabPicture(model, ix, tab), ct);
        // once any tab was captured, say which were not (the others still show their wireframe)
        if (ix.Captures.Tabs.Count > 0)
            foreach (var tab in ix.AllTabs.Where(t => ix.Captures.For(t) == null))
                model.Warnings.Add($"ribbon: no capture of the tab {tab.Title} ({tab.Id}): it shows its wireframe. AutoCAD did not show it when the ribbon was captured (not in this product's workspaces, empty, or the setting captureAllWorkspaces is off).");
        foreach (var f in model.Forms)
            Asset(model, sink, ix.FormAsset[f], () => FormRenderer.Render(f, model.InputRoot), ct);

        ct.ThrowIfCancellationRequested();
        new SourceMapWriter(ix, sink).Write();
        ct.ThrowIfCancellationRequested();
        new ManualTestWriter(ix, sink).WriteAll();
        ct.ThrowIfCancellationRequested();
        var sessions = IngestSessions(model, opt);
        ct.ThrowIfCancellationRequested();
        new GuideWriter(ix, opt, sessions).WriteAll();
        ct.ThrowIfCancellationRequested();
        new DocsWriter(ix, sink).WriteAll(); // last, so README lists warnings from asset rendering
        sink.Write(DocsLayout.ModelFile, JsonSerializer.Serialize(model, SourceMapWriter.Json));
    }

    /// <summary>Reads the recordings, joins them to this run's own data/sourcemap.json, writes recordings/. Failures become warnings.</summary>
    private static IReadOnlyList<RecordingSession> IngestSessions(EngineModel model, EngineOptions opt)
    {
        if (opt.Sessions.Count == 0) return [];
        try
        {
            var files = Retrace.Engine.Recordings.RecordingReader.Expand(opt.Sessions);
            if (files.Count == 0) { model.Warnings.Add("sessions: no recording.jsonl found"); return []; }
            var index = Retrace.Engine.Recordings.SourceMapIndex.Load(opt.Output);
            // fixed order, so duplicate names get the same suffix on every run
            // one unreadable recording is a warning naming it; the others are kept
            var loaded = new List<RecordingSession>();
            foreach (var f in files)
            {
                try { loaded.Add(Retrace.Engine.Recordings.RecordingReader.Load(f)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { model.Warnings.Add($"sessions: {f}: skipped, {ex.GetType().Name}: {ex.Message}"); }
            }
            if (loaded.Count == 0) return [];
            var sessions = loaded
                .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.File, StringComparer.Ordinal).ToList();
            var analysis = Retrace.Engine.Recordings.RecordingAnalysis.Run(sessions, index);
            var hw = new Retrace.Engine.Recordings.RecordingWriter(analysis, opt.Output);
            hw.Write();
            foreach (var w in hw.Warnings.Distinct()) model.Warnings.Add(w);
            foreach (var s in sessions.Where(s => s.BadLines > 0)) model.Warnings.Add($"sessions: {s.Name}: {s.BadLines} unreadable line(s) skipped");
            return sessions;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            model.Warnings.Add($"sessions: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>The tab's capture from AutoCAD when there is one (a picture that cannot be read: a warning, and the wireframe), else its wireframe.</summary>
    private static string TabPicture(EngineModel model, ModelIndex ix, RibbonTab tab)
    {
        if (ix.Captures.For(tab) is { } cap)
        {
            try { return ix.Captures.TabSvg(tab, cap); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { model.Warnings.Add($"ribbon capture of {tab.Title}: {ex.Message}; showing the wireframe"); }
        }
        return RibbonRenderer.Render(tab);
    }

    private static void Asset(EngineModel model, OutputSink sink, string rel, Func<string> render, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { sink.Write(rel, render()); }
        catch (OperationCanceledException) { throw; }
        catch (NotImplementedException) { }
        catch (Exception ex) { model.Warnings.Add($"{rel}: render failed: {ex.GetType().Name}: {ex.Message}"); }
    }
}
