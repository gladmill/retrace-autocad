namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// The ribbon's "Prompts" toggle: which form of a command a ribbon button runs for each <see cref="RetraceSettings.PromptStyle"/>, and the toggle's
/// text. Only the ribbon is routed this way; a command typed by hand is unchanged (RETRACESETTINGS still opens the window).
/// </summary>
public static class RibbonPrompts
{
    /// <summary>The ribbon button Ids that have a window form and a command-line form (<c>-NAME</c>). The other buttons ask through
    /// <c>Ask</c>, which follows promptStyle by itself, or have only one form.</summary>
    static readonly string[] Paired = { "RETRACESETTINGS", "RETRACETDOC", "RETRACEMAP", "RETRACESTOP" };

    /// <summary>True when the setting means the window form. "ui" in any case; anything else (also empty) is the command line, like <see cref="RetraceSettings.UiPrompts"/>.</summary>
    public static bool IsWindow(string? promptStyle) => RetraceSettings.IsUiStyle(promptStyle);

    /// <summary>True for a ribbon button that has a window form and a command-line form (see <see cref="Macro"/>).</summary>
    public static bool IsPaired(string? buttonId) => buttonId != null && Array.IndexOf(Paired, buttonId) >= 0;

    /// <summary>The macro a ribbon button sends: <paramref name="macro"/> as it is, or with the command-line form
    /// (<c>_RETRACESETTINGS</c> becomes <c>_-RETRACESETTINGS</c>) for a paired button when the style is the command line.</summary>
    public static string Macro(string? buttonId, string macro, string? promptStyle)
    {
        if (IsWindow(promptStyle) || !IsPaired(buttonId)) return macro;
        var window = "_" + buttonId;
        var at = macro.IndexOf(window, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return macro;
        var end = at + window.Length;
        if (end < macro.Length && !(macro[end] == ' ' || macro[end] == ';' || macro[end] == '\n')) return macro;   // another command that starts the same
        return macro.Insert(at + 1, "-");
    }

    /// <summary>The flipped style.</summary>
    public static string Flip(string? promptStyle) => IsWindow(promptStyle) ? PromptStyles.CommandLine : PromptStyles.Ui;

    /// <summary>The toggle's label (two lines, like the Checkpoint button).</summary>
    public static string ToggleText(string? promptStyle) => IsWindow(promptStyle) ? "Prompts:\nWindow" : "Prompts:\nCommand line";

    /// <summary>The toggle's tooltip: what it is, what the state is, what a click does.</summary>
    public static string ToggleTip(string? promptStyle) =>
        "How Retrace asks for names, files, folders and choices, and whether the buttons that have both forms (Settings, Training doc, Map, Stop) open a window or run their command-line form. "
        + (IsWindow(promptStyle)
            ? "Now: Window. Settings, Training doc, Map and Stop open their windows. Click for the command line."
            : "Now: Command line (the default). Settings, Training doc, Map and Stop run their command-line forms (-RETRACESETTINGS, -RETRACETDOC, -RETRACEMAP, -RETRACESTOP). Click for windows.")
        + " Saved as promptStyle in settings.json; a script, LISP or FILEDIA 0 always uses the command line.";
}
