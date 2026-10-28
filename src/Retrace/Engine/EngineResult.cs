namespace Retrace.Engine;

/// <summary>What an engine job did. The plugin reads these fields; nothing is parsed out of text.</summary>
public sealed class EngineResult
{
    /// <summary>Why it failed (the one-line reason to show); null when it worked or was cancelled.</summary>
    public string? Error { get; internal set; }
    public bool Cancelled { get; internal set; }
    public bool Success => Error == null && !Cancelled;

    /// <summary>Progress and summary lines ("projects: 3, files: 40"), in order. For the log and the command line; not for logic.</summary>
    public List<string> Messages { get; } = [];
    /// <summary>Things that went wrong without stopping the job (a scanner that failed, an unreadable line, a file that was open and got a "(new)" name).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>The docs output folder (BuildDocs) or the folder the recordings report went to (AnalyzeRecordings).</summary>
    public string? Output { get; internal set; }
    /// <summary>The steps.md a recording was turned into (WriteSteps) or exported from (Export).</summary>
    public string? Script { get; internal set; }
    /// <summary>The Word / Excel file written; the "(new)" name when the real one was open.</summary>
    public string? Docx { get; internal set; }
    public string? Xlsx { get; internal set; }
    /// <summary>The ribbon map (MapRibbon), also when it could not be written; null for the other jobs or when it failed.</summary>
    public Map.RibbonMapResult? RibbonMap { get; internal set; }

    internal void Info(string line) => Messages.Add(line);
    internal void Warn(string line) { if (!Warnings.Contains(line)) Warnings.Add(line); }
    internal EngineResult Fail(string error) { Error = error; return this; }

    /// <summary>Messages, warnings and the error as lines (the old command-line output), for logs.</summary>
    public IEnumerable<string> Lines()
    {
        foreach (var m in Messages) yield return m;
        foreach (var w in Warnings) yield return "warning:  " + w;
        if (Error != null) yield return "error:    " + Error;
        if (Cancelled) yield return "cancelled";
    }
}
