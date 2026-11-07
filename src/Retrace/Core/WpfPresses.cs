using System.Text;

namespace Retrace.Core;

// NO Win32, WPF or AutoCAD references in this file (linked into the engine test project).
// What the recorder and the replay decide alike about the push buttons of a WPF dialog (a window with one HwndWrapper and no child windows): the text a button shows
// without its access key, which button a key presses, which one of equal texts a button is.

public static class WpfPresses
{
    /// <summary>The text of a WPF content string (a string Content or an AccessText) without its access key: the first single underscore is the key's marker and goes ("_Add" is "Add", "Save _as" is "Save as"), a
    /// doubled one is a literal underscore ("A__B" is "A_B"), any later single underscore stays. Never used on an automation name or an x:Name, which have no access key. Then <see cref="DialogFieldsCore.CleanLabel"/>.</summary>
    public static string StripAccess(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        bool markerSeen = false;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '_') { sb.Append(raw[i]); continue; }
            if (i + 1 < raw.Length && raw[i + 1] == '_') { sb.Append('_'); i++; continue; }   // "__" is a literal underscore
            if (!markerSeen) { markerSeen = true; continue; }                                  // the first single one marks the key after it
            sb.Append('_');
        }
        return DialogFieldsCore.CleanLabel(sb.ToString());
    }

    /// <summary>Are two button texts the same button, the recorded one (a recording from before the underscore was dropped has "_Add") and the live one? Both are compared after <see cref="StripAccess"/>, without regard to case.</summary>
    public static bool SameText(string? recorded, string? live) => string.Equals(StripAccess(recorded), StripAccess(live), StringComparison.OrdinalIgnoreCase);

    /// <summary>Is a WPF button a real push button, one a person presses to do something? Not a toggle (a field), not a repeat button, a column header, nor any button that is a part of another control's template: the
    /// arrows and page areas of a scroll bar or slider, the drop-down button of a combo box or date picker. <paramref name="typeChain"/>: the type names of the button, most derived first; <paramref name="templatedParentChains"/>:
    /// the same for each control whose template the button is part of, innermost first (empty when it is not in a template).</summary>
    public static bool IsPushButton(IReadOnlyList<string> typeChain, IReadOnlyList<IReadOnlyList<string>> templatedParentChains)
    {
        if (typeChain.Any(t => t is "ToggleButton" or "RepeatButton" or "DataGridColumnHeader" or "GridViewColumnHeader")) return false;
        return !templatedParentChains.Any(c => c.Any(t => t is "ScrollBar" or "Slider" or "ComboBox" or "DatePicker" or "Calendar" or "CalendarDayButton" or "Spinner"));
    }

    /// <summary>One button of a WPF dialog as the replay picks the one that closes it.</summary>
    public readonly record struct CloseCandidate(string Text, bool IsDefault, bool IsCancel);

    /// <summary>The button that closes a WPF dialog with OK (<paramref name="ok"/>) or Cancel: the window's default button (IsDefault) for OK and its cancel button (IsCancel) for Cancel first, then the button whose text is OK / Cancel;
    /// -1 when there is none (the caller closes a Cancel by closing the window).</summary>
    public static int PickClose(IReadOnlyList<CloseCandidate> buttons, bool ok)
    {
        for (int i = 0; i < buttons.Count; i++) if (ok ? buttons[i].IsDefault : buttons[i].IsCancel) return i;
        return FillMatcher.PickButton(buttons.Select(b => (0, StripAccess(b.Text))).ToList(), ok);
    }

    /// <summary>Which of the buttons with the same text the one at <paramref name="position"/> of <paramref name="texts"/> is (1 = the first), counting equal texts as <see cref="SameText"/> does, as the replay finds it.</summary>
    public static int IndexAmong(IReadOnlyList<string> texts, int position)
    {
        if (position < 0 || position >= texts.Count) return 1;
        int n = 0;
        for (int i = 0; i <= position; i++) if (SameText(texts[i], texts[position])) n++;
        return n;
    }

    /// <summary>The one place that finds a recorded button, for dialogs and palettes: the position in <paramref name="texts"/> of the <paramref name="index"/>-th (1 = first) button whose text is <paramref name="text"/> (<see cref="SameText"/>); -1 when there are not that many.</summary>
    public static int Find(IReadOnlyList<string> texts, string text, int index)
    {
        int want = Math.Max(1, index), seen = 0;
        for (int i = 0; i < texts.Count; i++)
            if (SameText(text, texts[i]) && ++seen == want) return i;
        return -1;
    }

    /// <summary>The source a dialog is read with and filled through: a WPF window is read in process ("wpf"); a recording that says "uia" (a dialog read through UI Automation, made before) keeps the UI Automation path
    /// unless it has clicks (which only the in-process path can press).</summary>
    public static bool ReplayInProcess(string? recordedSource, bool hasClicks) => recordedSource == "wpf" || hasClicks;
}
