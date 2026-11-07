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

    /// <summary>A part of a WPF dialog that is not recorded or replayed: its kind ("tabs" a TabControl, "tree" a TreeView, "grid" a DataGrid, "list" any other selection control that is no field), its name (x:Name or
    /// automation name, may be empty) and <paramref name="Page"/>, the tab page it is on (the selected pages' headers above it; empty when it is on none). An unnamed part is who it is by its kind and page: several unnamed
    /// lists on one page are one warning.</summary>
    public readonly record struct Unsupported(string Kind, string Name, string Page = "")
    {
        /// <summary>Who it is: kind and name; an unnamed part by its kind and tab page (stable where an object identity or an index path is not: a rebuilt template, a list that grew).</summary>
        public string Key => string.IsNullOrEmpty(Name) ? Kind + "||" + Page : Kind + "|" + Name;
    }

    /// <summary>The recorder's JSON for the parts a WPF dialog had (`unsupported` of its `dialog_values`: <c>[{"kind":"tree","name":"PartsTree"}]</c>); null for none. An unnamed part has its <c>page</c> (the tab page) when it is on one; no null is written.</summary>
    public static List<Dictionary<string, object?>>? PartsToJson(IReadOnlyCollection<Unsupported>? parts) =>
        parts == null || parts.Count == 0 ? null : parts.Select(p =>
        {
            var d = new Dictionary<string, object?> { ["kind"] = p.Kind };
            if (!string.IsNullOrEmpty(p.Name)) d["name"] = p.Name;
            else if (!string.IsNullOrEmpty(p.Page)) d["page"] = p.Page;
            return d;
        }).ToList();

    /// <summary>Reads an `unsupported` array; anything that is not an array of objects with a `kind` is skipped (a recording from before has none). Never throws.</summary>
    public static List<Unsupported> PartsFromJson(System.Text.Json.JsonElement e)
    {
        var list = new List<Unsupported>();
        if (e.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
        foreach (var p in e.EnumerateArray())
        {
            if (p.ValueKind != System.Text.Json.JsonValueKind.Object || !p.TryGetProperty("kind", out var k) || k.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            string Of(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
            list.Add(new Unsupported(k.GetString() ?? "", Of("name"), Of("page")));
        }
        return list;
    }

    /// <summary>The parts of the recording and the live window together, each once (<see cref="Unsupported.Key"/>): the recording saw parts on pages that are not showing now, the window shows parts the recording never saw.</summary>
    public static List<Unsupported> Union(IEnumerable<Unsupported>? recorded, IEnumerable<Unsupported> live)
    {
        var seen = new HashSet<string>();
        return (recorded ?? Enumerable.Empty<Unsupported>()).Concat(live).Where(p => seen.Add(p.Key)).ToList();
    }

    /// <summary>The note a replay result carries for a WPF dialog that has parts Retrace does not follow, naming each: <c>unverified: the dialog has a tree "PartsTree" and a tab control "MainTabs", which are not recorded or
    /// replayed (what a person chose there is not repeated)</c>; null when there is none. Tab controls, trees, grids and lists are named; any other kind is named neutrally ("a &lt;kind&gt;").</summary>
    public static string? UnsupportedNote(IReadOnlyList<Unsupported> parts) => UnsupportedParts(parts) is { } body ? "unverified: " + body : null;

    /// <summary>The note without its "unverified: " prefix (the log line): <c>the dialog has a tree "PartsTree", which is not recorded or replayed (...)</c>; null when there is none.</summary>
    public static string? UnsupportedParts(IReadOnlyList<Unsupported> parts)
    {
        if (parts.Count == 0) return null;
        static string One(Unsupported u) => (u.Kind switch { "tabs" => "a tab control", "tree" => "a tree", "grid" => "a grid", "list" => "a list", _ => "a " + u.Kind }) + (string.IsNullOrEmpty(u.Name) ? "" : " \"" + u.Name + "\"");
        var names = parts.Select(One).Distinct().ToList();
        var list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        return $"the dialog has {list}, which {(names.Count == 1 ? "is" : "are")} not recorded or replayed (what a person chose or switched there is not repeated)";
    }

    /// <summary>The reason a value found no control in a WPF dialog, when that may be because it sits on a page of a tab control: the replay does not switch WPF tabs. Empty when the dialog has no tab control.</summary>
    public static string TabHint(IReadOnlyList<Unsupported> parts) =>
        parts.Any(p => p.Kind == "tabs") ? " (it may be on a page of the dialog's tab control that is not showing: WPF tabs are not switched)" : "";

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
