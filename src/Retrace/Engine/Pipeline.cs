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

        foreach (var f in files.Where(f => f.Lang == Lang.Cuix))
            Try(model, f.RelPath, () => model.Cuix.Add(CuixReader.Read(f.AbsPath, f.RelPath)), ct);

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
