namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// Toolbars of a palette: the buttons of a WinForms `ToolStrip` (windowless items, one window for the whole strip) and of a `ToolbarWindow32` (a Win32 toolbar, also the WinForms `ToolBar`).
// A click on one is a `palette_click` of kind `toolstrip` / `toolbar` (PaletteClickKinds). This is the pure part: how a button is named (the ButtonNames conventions), which live button a recorded click means, and the words.

/// <summary>A button of a toolbar as the replay and the recorder list it: its `Name` (ToolStrip only), own text, tool tip, accessible name, whether it is enabled and its command id (ToolbarWindow32 only).</summary>
public readonly record struct ToolCandidate(string Name, string Text, string Tooltip, string Accessible, bool Enabled, int Cmd = 0);

/// <summary>Naming, finding and wording of toolbar buttons.</summary>
public static class PaletteToolbar
{
    /// <summary>The recorded name of a button and where it came from (`button`, `buttonFrom`): its own text, else its tool tip, else its accessible name, else its `Name`; ("(button)", "") when none gives one.
    /// An MFC tool tip "status text\ntooltip" gives the part after the last line break.</summary>
    public static (string Name, string From) Display(ToolCandidate c) =>
        ButtonNames.Pick(() => c.Text, () => LastLine(c.Tooltip), () => c.Accessible, null, () => c.Name);

    static string LastLine(string? tip)
    {
        var t = (tip ?? "").Replace("\r", "");
        var nl = t.LastIndexOf('\n');
        return nl >= 0 ? t[(nl + 1)..] : t;
    }

    /// <summary>Which of the buttons with the same recorded name the one at <paramref name="at"/> is (1 = the first).</summary>
    public static int Occurrence(IReadOnlyList<ToolCandidate> live, int at)
    {
        if (at < 0 || at >= live.Count) return 1;
        var name = Display(live[at]).Name;
        int n = 0;
        for (int i = 0; i <= at; i++) if (string.Equals(Display(live[i]).Name, name, StringComparison.OrdinalIgnoreCase)) n++;
        return Math.Max(1, n);
    }

    /// <summary>The live button a recorded click means: At (-1 when none), a Problem when there is none or the one found is not the recorded one.</summary>
    public readonly record struct Found(int At, string? Note, string? Problem);

    public static bool IsNamed(string recordedName) => recordedName.Length > 0 && recordedName != ButtonNames.Unnamed;

    /// <summary>Finds the button, and never another one: a replay must not click a toolbar icon other than the recorded one (a toolbar button can delete or upload things).
    /// The command id (a toolbar; only when exactly one button has it) and the item's `Name` (only when exactly one has it) name a candidate, which is accepted only when its display name is still the recorded one (it may now mean something else).
    /// Then the recorded name: the <paramref name="occurrence"/>-th button that answers to it. A recording that has a name and finds no button of that name fails. Only a recording with no name at all (an icon nothing names: `(button)`, no `itemName`)
    /// takes the button at its recorded place, and only when the toolbar still has as many buttons as when it was recorded and that button has no name either. A recorded command id that is not on the toolbar fails: no fallback to the name or the place.</summary>
    public static Found Find(IReadOnlyList<ToolCandidate> live, string recordedName, int occurrence, string itemName, int position, int count, int cmd)
    {
        bool named = IsNamed(recordedName);
        string? Verify(int i)
        {
            var now = Display(live[i]).Name;
            if (named) return string.Equals(now, recordedName, StringComparison.OrdinalIgnoreCase) ? null : $"the toolbar button called \"{recordedName}\" in the test is called \"{now}\" now (it may do something else): not clicked";
            return now == ButtonNames.Unnamed ? null : $"the toolbar button that had no name in the test is called \"{now}\" now (it may do something else): not clicked";
        }
        var byCmd = new List<int>();
        if (cmd > 0) for (int i = 0; i < live.Count; i++) if (live[i].Cmd == cmd) byCmd.Add(i);
        if (byCmd.Count == 1) return Verify(byCmd[0]) is { } why ? new Found(-1, null, why) : new Found(byCmd[0], null, null);
        // a recorded command id that is not on the toolbar (or is on several buttons) fails: no fallback to the name (a namesake would be clicked) and none to the place
        if (cmd > 0) return new Found(-1, null, $"toolbar button with command id {cmd}{(named ? $" (\"{recordedName}\")" : "")} not found in the palette (yet)");
        var byName = new List<int>();
        if (itemName.Length > 0) for (int i = 0; i < live.Count; i++) if (string.Equals(live[i].Name, itemName, StringComparison.OrdinalIgnoreCase)) byName.Add(i);
        if (byName.Count == 1) return Verify(byName[0]) is { } why2 ? new Found(-1, null, why2) : new Found(byName[0], null, null);
        if (named)
        {
            int want = Math.Max(1, occurrence), seen = 0;
            for (int i = 0; i < live.Count; i++)
                if (string.Equals(Display(live[i]).Name, recordedName, StringComparison.OrdinalIgnoreCase) && ++seen == want) return new Found(i, null, null);
            return new Found(-1, null, $"toolbar button \"{recordedName}\" not found in the palette (yet)");
        }
        if (itemName.Length == 0 && position >= 1 && count > 0 && count == live.Count && position <= live.Count && Verify(position - 1) is null)
            return new Found(position - 1, $"toolbar button no. {position} found by position: this button has no name", null);
        return new Found(-1, null, $"no toolbar button no. {position} without a name in the palette (yet)");
    }

    /// <summary>The words of a toolbar click: "Click toolbar button **Download**", "Click toolbar button no. 3" (no name), "Choose **PDF** from toolbar button **Export**" (a drop-down).</summary>
    public static string Words(string button, PaletteHit? hit, Func<string, string> code, Func<string, string> bold, bool capital)
    {
        string C(string s) => capital ? char.ToUpperInvariant(s[0]) + s[1..] : s;
        bool unnamed = string.IsNullOrWhiteSpace(button) || button == ButtonNames.Unnamed;
        string what = unnamed ? "toolbar button no. " + Math.Max(1, hit?.Position ?? 1) : "toolbar button " + bold(button);
        if (hit is { Menu.Length: > 0 }) return C("choose ") + bold(string.Join(" › ", TreePath.Split(hit.Menu))) + " from " + what;
        return C("click ") + what;
    }
}
