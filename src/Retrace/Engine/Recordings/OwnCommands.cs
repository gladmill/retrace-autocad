namespace Retrace.Engine.Recordings;

/// <summary>
/// What marks a command, ribbon button or ribbon tab as the plugin's own: those clicks and prompts are not part of a recorded user path.
/// One place, so the recorder, the replayer, the plan and the engine agree.
/// </summary>
public static class OwnCommands
{
    /// <summary>Every command of the plugin starts with this (case-insensitive).</summary>
    public const string Prefix = "RETRACE";

    /// <summary>Id of the plugin's ribbon tab.</summary>
    public const string TabId = "RETRACE_TAB";

    /// <summary>Leading `-` (command-line form), `_` (language-independent) and `.` (built-in) are ignored: `-RETRACEMARK` is ours too.</summary>
    public static bool IsOwnCommand(string? name) => name != null && name.TrimStart('-', '_', '.').StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsOwnTab(string? tabId) => string.Equals(tabId, TabId, StringComparison.OrdinalIgnoreCase);
}
