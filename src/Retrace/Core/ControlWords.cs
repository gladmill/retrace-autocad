using System.Text.RegularExpressions;

namespace Retrace.Core;

// NO AutoCAD, WPF or System.Drawing references in this file (linked into the engine test project).

/// <summary>How a control is named in the words of a test document: a control with no label of its own is recorded as "#1012" (its control id); a reader
/// is told "the tree" / "the list" instead. Text only: the recording keeps the id. Shared by steps.md and the Tdoc text.</summary>
public static class ControlWords
{
    static readonly Regex IdOnly = new(@"^#\d+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True when the only name of the control is its "#&lt;id&gt;".</summary>
    public static bool IsIdOnly(string? label) => label != null && IdOnly.IsMatch(label.Trim());

    /// <summary>"the tree" for a tree, "the list" for a list view or list box, "the drop-down list" for a combo box, "the check box", "the option", else "the field".</summary>
    public static string Generic(string? kind) => (kind ?? "") switch
    {
        "tree" or "treecheck" => "the tree",
        "listview" or "list" => "the list",
        "combo" => "the drop-down list",
        "check" => "the check box",
        "radio" or "radio1" => "the option",
        _ => "the field",
    };

    /// <summary>The control's words: <paramref name="formatted"/> (the label, usually in bold) when it has a real name, else "the tree" / "the list" ...; <paramref name="formatted"/> is only called then.</summary>
    public static string Name(string? label, string? kind, Func<string, string> formatted) =>
        IsIdOnly(label) || string.IsNullOrWhiteSpace(label) ? Generic(kind) : formatted(label!);
}
