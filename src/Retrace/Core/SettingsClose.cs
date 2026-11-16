using System.Text.Json;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>What the person answered when the Settings window asked "Save changes?".</summary>
public enum CloseAnswer { Save, DontSave, Cancel }

/// <summary>What closing the Settings window does.</summary>
public enum CloseAction { Close, SaveThenClose, StayOpen }

/// <summary>The rule for closing the Settings window with the X or Esc: nothing changed closes at once, otherwise the person is asked. The Cancel button is not part of it (a deliberate Cancel discards, as in Windows dialogs).</summary>
public static class SettingsClose
{
    /// <summary>The state of the window's controls as one text (<paramref name="parts"/>: values, ticks, lists, flattened). It is compared with the state at opening, so the check does not depend on
    /// whether the settings are valid: a window that opens with an invalid combination still asks.</summary>
    public static string State(IEnumerable<object?> parts) => string.Join("\u001f", parts.Select(Flat));

    static string Flat(object? p) => p switch
    {
        null => "~",
        string s => s,
        System.Collections.IEnumerable e => "[" + string.Join("\u001e", e.Cast<object?>().Select(Flat)) + "]",
        _ => p.ToString() ?? "",
    };

    /// <summary>Is the state now different from the one at opening? A value changed and changed back is not.</summary>
    public static bool Changed(string baseline, string now) => !string.Equals(baseline, now, StringComparison.Ordinal);

    /// <summary>Nothing changed: close, no question. Changed: Save saves and closes, Don't save closes, Cancel (or no answer) keeps the window open.</summary>
    public static CloseAction Decide(bool changed, CloseAnswer? answer)
    {
        if (!changed) return CloseAction.Close;
        return answer switch { CloseAnswer.Save => CloseAction.SaveThenClose, CloseAnswer.DontSave => CloseAction.Close, _ => CloseAction.StayOpen };
    }
}
