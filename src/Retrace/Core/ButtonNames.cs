namespace Retrace.Core;

// NO AutoCAD, WPF or System.Drawing references in this file (linked into the engine test project).

/// <summary>The name of a palette push button as recorded (`button` of `palette_click`, with `buttonFrom` and `legacyIndex`) and how a replay finds it again.
/// <para>A button with text of its own, or (WPF) an explicit automation name, is recorded exactly as it always was: its <em>legacy</em> name, without `buttonFrom`. A button without such text gets the first of
/// the other sources that has a value (<see cref="Order"/>) and `buttonFrom` says which. A recording without `buttonFrom` is matched by the legacy name alone (so an old recording, "(button)" included, replays as
/// before and a new fallback name can never collide with another button's own text); one with `buttonFrom` only matches buttons without text of their own, by that source.</para></summary>
public static class ButtonNames
{
    public const string FromText = "text";
    public const string FromTooltip = "tooltip";
    /// <summary>The accessible name (WinForms `AccessibleName`, else MSAA).</summary>
    public const string FromAccessible = "accessible";
    /// <summary>WPF: the name the automation peer reports.</summary>
    public const string FromAutomation = "automation";
    /// <summary>The control's `Name` (WinForms) or `x:Name` (WPF).</summary>
    public const string FromName = "name";

    /// <summary>The legacy name of a button without any text.</summary>
    public const string Unnamed = "(button)";

    /// <summary>Is <paramref name="from"/> a fallback source (a name that did not come from the button's own text)?</summary>
    public static bool IsFallback(string? from) => from is FromTooltip or FromAccessible or FromAutomation or FromName;

    /// <summary>The first source in <see cref="Order"/> that gives a non-empty name (cleaned, see <see cref="Clean"/>), with the source's name; ("(button)", "") when none does.
    /// A source is asked only when the ones before it gave nothing. A source that throws counts as none.</summary>
    public static (string Name, string From) Pick(Func<string?>? text, Func<string?>? tooltip, Func<string?>? accessible, Func<string?>? automation, Func<string?>? name)
    {
        var sources = new[] { (FromText, text), (FromTooltip, tooltip), (FromAccessible, accessible), (FromAutomation, automation), (FromName, name) };
        foreach (var (from, get) in sources)
        {
            if (get == null) continue;
            string? raw;
            try { raw = get(); } catch (Exception) { continue; }
            var clean = Clean(raw);
            if (clean.Length > 0) return (clean, from);
        }
        return (Unnamed, "");
    }

    /// <summary>A fallback name: first line only, mnemonics and a trailing colon removed, trimmed, at most 80 characters.</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var first = raw.Replace("\r", "").Split('\n')[0];
        var t = DialogFieldsCore.CleanLabel(first);
        return t.Length <= 80 ? t : t[..80].TrimEnd();
    }

    /// <summary>`buttonFrom` as written to the recording: nothing for a name from the button's text (the legacy name), or when the button is unnamed.</summary>
    public static string? FromToRecord(string from) => IsFallback(from) ? from : null;

    /// <summary>Does the live button answer to the recorded name? Without a fallback `from`: its legacy name equals the recorded one (<paramref name="same"/>, which says how two names compare). With one: the
    /// button has no text of its own and the name from that source (<paramref name="fallback"/>, asked only then) equals the recorded one.</summary>
    public static bool Matches<T>(T button, string recorded, string? from, Func<T, string> legacy, Func<T, bool> hasOwnText, Func<T, string, string?> fallback, Func<string, string, bool> same)
    {
        if (!IsFallback(from)) return same(recorded, legacy(button));
        if (hasOwnText(button)) return false;
        var live = fallback(button, from!);
        return live != null && same(recorded, live);
    }

    /// <summary>The position of the <paramref name="index"/>-th (1 = first) button that answers to the recorded name; -1 when there are not that many.</summary>
    public static int Find<T>(IReadOnlyList<T> buttons, string recorded, string? from, int index, Func<T, string> legacy, Func<T, bool> hasOwnText, Func<T, string, string?> fallback, Func<string, string, bool> same)
    {
        int want = Math.Max(1, index), seen = 0;
        for (int i = 0; i < buttons.Count; i++)
            if (Matches(buttons[i], recorded, from, legacy, hasOwnText, fallback, same) && ++seen == want) return i;
        return -1;
    }

    /// <summary>Which of the buttons that answer to <paramref name="recorded"/> the one at <paramref name="at"/> is (1 = first), counted the way <see cref="Find{T}"/> counts; 1 when it is not in the list.</summary>
    public static int IndexAmong<T>(IReadOnlyList<T> buttons, int at, string recorded, string? from, Func<T, string> legacy, Func<T, bool> hasOwnText, Func<T, string, string?> fallback, Func<string, string, bool> same)
    {
        if (at < 0 || at >= buttons.Count) return 1;
        int n = 0;
        for (int i = 0; i <= at; i++) if (Matches(buttons[i], recorded, from, legacy, hasOwnText, fallback, same)) n++;
        return Math.Max(1, n);
    }

    /// <summary>`legacyIndex`: which of the buttons without text of their own the one at <paramref name="at"/> is (1 = first), as the "(button)" index always counted them; 1 when it is not in the list.</summary>
    public static int LegacyIndex<T>(IReadOnlyList<T> buttons, int at, Func<T, bool> hasOwnText)
    {
        if (at < 0 || at >= buttons.Count) return 1;
        int n = 0;
        for (int i = 0; i <= at; i++) if (!hasOwnText(buttons[i])) n++;
        return Math.Max(1, n);
    }

    /// <summary>The position of the <paramref name="legacyIndex"/>-th (1 = first) button without text of its own; -1 when there is none. Used when the name found nothing: the button is taken by position.</summary>
    public static int FindByLegacyIndex<T>(IReadOnlyList<T> buttons, int legacyIndex, Func<T, bool> hasOwnText)
    {
        if (legacyIndex < 1) return -1;
        int seen = 0;
        for (int i = 0; i < buttons.Count; i++) if (!hasOwnText(buttons[i]) && ++seen == legacyIndex) return i;
        return -1;
    }

    /// <summary>May a button be taken by position (`legacyIndex`)? Only when the palette still has as many buttons without text as when the test was recorded (<paramref name="recordedNoText"/>, `noTextCount`; 0 = not
    /// recorded, so never) and the recorded index is one of them.</summary>
    public static bool PositionAllowed(int legacyIndex, int recordedNoText, int liveNoText) =>
        recordedNoText > 0 && recordedNoText == liveNoText && legacyIndex >= 1 && legacyIndex <= liveNoText;

    /// <summary>The note a replay adds when a button was found by position because the name it was recorded under is not there: "button found by position (no. 2 without text); its name is now \"Refresh\" (was \"Save\")".</summary>
    public static string PositionNote(string recorded, string? liveName, int legacyIndex) =>
        $"button found by position (no. {legacyIndex} without text of its own); its name changed to \"{(string.IsNullOrEmpty(liveName) ? Unnamed : liveName)}\" (recorded as \"{recorded}\")";
}
