namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// The ribbon's FILEDIA indicator (Guide panel, right of the Prompts toggle): the label, the tooltip and what a click sets for each value of the
/// FILEDIA system variable. With FILEDIA 0 Retrace asks every file question on the command line, so the button warns.
/// </summary>
public static class RibbonFileDia
{
    /// <summary>True when file dialogs are on (any value but 0).</summary>
    public static bool IsOn(int value) => value != 0;

    /// <summary>The value a click sets: 0 becomes 1, anything else becomes 0.</summary>
    public static int Flip(int value) => value == 0 ? 1 : 0;

    /// <summary>The button's label (two lines for the warning).</summary>
    public static string Text(int value) => IsOn(value) ? "FILEDIA: " + value : "FILEDIA: 0\n⚠ file dialogs off";

    /// <summary>The icon name: a warning mark for FILEDIA 0.</summary>
    public static string Icon(int value) => IsOn(value) ? "filedia" : "warn";

    /// <summary>The tooltip: what the value means, what a click does.</summary>
    public static string Tip(int value) => IsOn(value)
        ? "FILEDIA is " + value + ": file dialogs are on, so Retrace can open its file pickers (Run test, Open test, folders). "
          + "Click to set FILEDIA to 0, which turns every file dialog off (Retrace then asks on the command line). Not available while a command is running."
        : "FILEDIA is 0: file dialogs are off. Retrace's Run test, Open test and other file questions are asked on the command line (for example \"Recording ('last' = newest):\") instead of in a file picker. "
          + "Click to set FILEDIA to 1 (or type FILEDIA, then 1). Not available while a command is running.";
}
