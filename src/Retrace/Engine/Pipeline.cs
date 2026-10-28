using Retrace.Engine.Scan;
using Retrace.Engine.Scan.CodeRibbon;
using Retrace.Engine.Scan.Commands;
using Retrace.Engine.Scan.Cuix;
using Retrace.Engine.Emit;
using Retrace.Engine.Scan.Forms;

namespace Retrace.Engine;

public sealed class EngineOptions
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    /// <summary>How many calls deep the flow analyzer follows from an entry point.</summary>
    public int MaxFlowDepth { get; init; } = 4;
    /// <summary>Also write manual test suites for every stock CUIX ribbon tab (large).</summary>
    public bool CuixTests { get; init; } = true;
    /// <summary>Folders of real dialog captures (&lt;Stem&gt;__&lt;Name&gt;.png + .json with a formId); the guide uses them instead of wireframes.</summary>
    public IReadOnlyList<string> Captures { get; init; } = [];
    /// <summary>Recorded Retrace sessions (.jsonl files or folders): also writes recordings/ and the guide's how-to pages.</summary>
    public IReadOnlyList<string> Sessions { get; init; } = [];
    /// <summary>More .cuix / .cui files to document besides those under <see cref="Input"/>: in AutoCAD, the customization files it has
    /// loaded (the main file, its partials, the enterprise file). Files under Input are read once.</summary>
    public IReadOnlyList<string> Customization { get; init; } = [];
    /// <summary>Also read the partial customization files that read .cuix / .cui files name in their headers.</summary>
    public bool FollowPartials { get; init; }
}

public static class Pipeline
{
    /// <summary>Scan + analyze + link. No output written.</summary>
    public static EngineModel Analyze(EngineOptions opt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var model = InputScanner.Scan(opt.Input);
        var files = model.Files;
        ct.ThrowIfCancellationRequested();

        // a loose part of an unzipped .cuix (RibbonRoot.cui, MenuGroup.cui...) is not a legacy .cui: left out
        foreach (var f in files.Where(f => f.Lang == Lang.Cuix && (!f.AbsPath.EndsWith(".cui", StringComparison.OrdinalIgnoreCase) || CuixReader.IsLegacyCui(f.AbsPath))))
            Try(model, f.RelPath, () => model.Cuix.Add(CuixReader.Read(f.AbsPath, f.RelPath)), ct);
        Try(model, "customization files", () => ReadMoreCustomization(model, opt, files, ct), ct);

        Try(model, "code ribbon", () => model.CodeTabs.AddRange(CodeRibbonReader.Read(files)), ct);
        Try(model, "commands", () => model.Commands.AddRange(CommandScanner.Scan(files)), ct);
        Try(model, "forms", () => model.Forms.AddRange(FormScanner.Scan(files)), ct);

        Try(model, "flows", () =>
        {
            var flows = new FlowAnalyzer(files, opt.MaxFlowDepth);
            Linker.KeepDeclaredHandlers(model, flows.Declares);
            Linker.AddRibbonHandlers(model);
            foreach (var c in model.Commands)
            {
                if (c.Flow.Count == 0) c.Flow.AddRange(flows.Analyze(c.Method));
                if (!flows.Knows(c.Method)) c.Extra["methodNotFound"] = "true";
                ct.ThrowIfCancellationRequested();
            }
        }, ct);
        ct.ThrowIfCancellationRequested();
        Linker.Link(model);
        return model;
    }

    public static EngineModel Run(EngineOptions opt, CancellationToken ct = default)
    {
        var model = Analyze(opt, ct);
        Writer.WriteAll(model, opt, ct);
        return model;
    }

    /// <summary>Reads the customization files given in <see cref="EngineOptions.Customization"/>, and with <see cref="EngineOptions.FollowPartials"/>
    /// every partial customization file a read file names in its header (relative to that file's folder), each once. A partial not found
    /// there is a warning, unless a file of that name was read from elsewhere (AutoCAD found it on its support path). Files whose name
    /// looks like a secret are skipped, as the input scanner does. A file whose name another read file has is named by the first folder
    /// that tells them apart ("AutoCAD Electrical 2026/acad.cuix"), and its tabs are filed under that name.</summary>
    private static void ReadMoreCustomization(EngineModel model, EngineOptions opt, List<SourceFile> files, CancellationToken ct)
    {
        static string Key(string p) { try { return Path.GetFullPath(p).ToUpperInvariant(); } catch { return p.ToUpperInvariant(); } }
        var inputs = files.Where(f => f.Lang == Lang.Cuix).ToList();
        var done = new HashSet<string>(inputs.Select(f => Key(f.AbsPath)));
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // file name -> the first file read with it
        foreach (var f in inputs) byName.TryAdd(Path.GetFileName(f.AbsPath), f.AbsPath);
        var labels = new HashSet<string>(model.Cuix.Select(c => c.File), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, string? From)>(opt.Customization.Select(p => (p, (string?)null)));
        foreach (var p in opt.Customization) byName.TryAdd(Path.GetFileName(p), p);   // named partials found here are not missing
        void Partials(CuixModel c, string abs)
        {
            if (!opt.FollowPartials) return;
            foreach (var p in c.PartialMenus) queue.Enqueue((Path.IsPathRooted(p) ? p : Path.Combine(Path.GetDirectoryName(abs) ?? "", p), c.File));
        }
        foreach (var f in inputs) if (model.Cuix.FirstOrDefault(c => c.File == f.RelPath) is { } c) Partials(c, f.AbsPath);
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, from) = queue.Dequeue();
            var name = Path.GetFileName(path);
            var cui = path.EndsWith(".cui", StringComparison.OrdinalIgnoreCase);
            if (!cui && !path.EndsWith(".cuix", StringComparison.OrdinalIgnoreCase)) continue;
            if (!done.Add(Key(path))) continue;
            if (!File.Exists(path))
            {
                if (from != null && !(byName.TryGetValue(name, out var other) && !string.Equals(Key(other), Key(path), StringComparison.Ordinal)))
                    model.Warnings.Add($"{from}: partial customization file not found: {name}");
                continue;
            }
            if (InputScanner.LooksSecret(name)) { model.Warnings.Add($"{name}: skipped (the name looks like a secret)"); continue; }
            if (cui && !CuixReader.IsLegacyCui(path)) continue;
            string label = name;
            if (byName.TryGetValue(name, out var first) && !string.Equals(Key(first), Key(path), StringComparison.Ordinal) || labels.Contains(name))
                label = Distinct(path, first ?? path);
            byName.TryAdd(name, path);
            for (int n = 2; !labels.Add(label); n++) label = $"{Path.GetFileNameWithoutExtension(name)}-{n}{Path.GetExtension(name)}";
            var read = path; var as_ = label;
            Try(model, label, () => { var c = CuixReader.Read(read, as_, as_ == name ? null : as_); model.Cuix.Add(c); Partials(c, read); }, ct);
        }
    }

    /// <summary>The first folder of <paramref name="path"/> that differs from <paramref name="other"/>'s, with the file name:
    /// "AutoCAD Electrical 2026/acad.cuix" against ".../AutoCAD 2026/.../acad.cuix". No full path, so no user name.</summary>
    internal static string Distinct(string path, string other)
    {
        // split by hand on both separators: the tests run where Path does not know Windows paths
        static string[] Parts(string p) => p.Split('\\', '/').Where(x => x.Length > 0).ToArray();
        var a = Parts(path); var b = Parts(other);
        var file = a.Length > 0 ? a[^1] : path;
        for (int i = 0; i < a.Length - 1; i++)
            if (i >= b.Length - 1 || !string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return $"{a[i]}/{file}";
        return a.Length > 1 ? $"{a[^2]}/{file}" : file;
    }

    // A scanner that fails (an exception, or a regex that ran out of time: RegexMatchTimeoutException) becomes a warning; cancelling does not.
    private static void Try(EngineModel model, string what, Action a, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { a(); }
        catch (OperationCanceledException) { throw; }
        catch (NotImplementedException) { model.Warnings.Add($"{what}: not implemented yet"); }
        catch (Exception ex) { model.Warnings.Add($"{what}: {ex.GetType().Name}: {ex.Message}"); }
    }
}
