namespace Retrace.Core;

/// <summary>When RETRACESTOP opens the Checkpoints window. The scripted form (-RETRACESTOP) never does.</summary>
public static class StopReview
{
    /// <summary>Whether to open the window at all: not for a script, and only when the reviewCheckpoints setting is on.</summary>
    public static bool ShouldOpenWindow(bool scripted, bool reviewSetting) => !scripted && reviewSetting;

    /// <summary>True when a recording's lines hold a checkpoint (a `mark` event).</summary>
    public static bool HasCheckpoint(IEnumerable<string> lines) => lines.Any(l => l.Contains("\"type\":\"mark\"", StringComparison.Ordinal));
}
