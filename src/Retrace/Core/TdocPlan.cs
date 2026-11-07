using System.Text.Json;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). What the Tdoc training document says about a recorded step:
// the sentences for the reader and the legend lines for the numbered boxes on its pictures.

/// <summary>Where a legend line's box is looked for on the screen.</summary>
public enum CalloutRole { RibbonItem, CommandLine, Control, Close }

/// <summary>One box a step would like on its picture.</summary>
public sealed record CalloutPlan(CalloutRole Role, string Text);

/// <summary>A legend line with a candidate's text. <see cref="Located"/>: its box was found on the screen.</summary>
public sealed record CalloutSpec(string Text, bool Located);

/// <summary>A legend line that got a box: its number, its text and which candidate it was (index into the list given to <see cref="TdocPlan.Number"/>).</summary>
public sealed record NumberedCallout(int Number, string Text, int Index);

public static class TdocPlan
{
    /// <summary>The kind written to tdoc.json: ribbon | command | lisp | dialog | palette | mark | open | tab | other.</summary>
    public static string KindOf(Step s) => s.Kind is "ribbon" or "command" or "lisp" or "dialog" or "palette" or "mark" or "open" or "tab" ? s.Kind : "other";

    /// <summary>The heading of a step: the recorded title, except for a dialog ("Dialog opens: ... fill it in by hand" is a note for the tester)
    /// and a checkpoint.</summary>
    public static string StepTitle(Step s)
    {
        switch (s.Kind)
        {
            case "dialog":
                return string.IsNullOrWhiteSpace(s.DialogTitle) ? "Dialog" : $"{DialogName(s)} dialog";
            case "mark":
                return string.IsNullOrWhiteSpace(s.Note) ? "Checkpoint" : "Checkpoint: " + s.Note.Trim();
            default:
                return string.IsNullOrWhiteSpace(s.Title) ? s.Kind : s.Title.Trim();
        }
    }

    // ---- instructions -----------------------------------------------------------------------------------------------------

    /// <summary>The sentences for the reader, in order.</summary>
    public static List<string> Instructions(Step s)
    {
        var list = new List<string>();
        switch (s.Kind)
        {
            case "ribbon":
            {
                var label = RibbonLabel(s) ?? "the button";
                list.Add(string.IsNullOrWhiteSpace(s.Tab) ? $"Click {label}." : $"Click {label} on the {s.Tab.Trim()} tab.");
                list.AddRange(AnswerLines(s.Answers, s));
                break;
            }
            case "command":
                list.Add($"Type {s.Command ?? "the command"} at the command line and press Enter.");
                list.AddRange(AnswerLines(s.Answers, s));
                break;
            case "lisp":
                list.Add($"Type {LispTyped(s)} at the command line and press Enter.");
                list.AddRange(AnswerLines(s.Answers, s));
                break;
            case "dialog":
                list.AddRange(DialogLines(s));
                break;
            case "palette":
                list.AddRange(PaletteLines(s));
                break;
            case "open":
                list.Add($"Open {FileName(s.Drawing)}.");
                break;
            case "tab":
                list.Add($"Click the {s.Tab} tab of the ribbon.");
                break;
            case "mark":
                list.Add(string.IsNullOrWhiteSpace(s.Note) ? "Checkpoint." : "Checkpoint: " + s.Note.Trim());
                break;
            default:
                if (!string.IsNullOrWhiteSpace(s.Title)) list.Add(s.Title.Trim().TrimEnd('.') + ".");
                break;
        }
        return list;
    }

    /// <summary>The button's label as the person saw it: the last part of "Click Insert > Block > Create Block"; the item id when the title has none.</summary>
    public static string? RibbonLabel(Step s)
    {
        var title = s.Title ?? "";
        if (title.StartsWith("Click ", StringComparison.Ordinal))
        {
            var rest = title[6..];
            var prefix = string.Concat(new[] { s.Tab, s.Panel }.Where(x => !string.IsNullOrEmpty(x)).Select(x => x + " > "));
            if (prefix.Length > 0 && rest.StartsWith(prefix, StringComparison.Ordinal)) rest = rest[prefix.Length..];
            else if (rest.LastIndexOf(" > ", StringComparison.Ordinal) is var i and >= 0) rest = rest[(i + 3)..];
            if (rest.Trim().Length > 0) return rest.Trim();
        }
        return string.IsNullOrWhiteSpace(s.ItemId) ? null : s.ItemId.Trim();
    }

    static string DialogName(Step s) => s.DialogTitle?.Trim() ?? "";

    static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "the drawing";
        var n = path.Replace('\\', '/').TrimEnd('/');
        return n[(n.LastIndexOf('/') + 1)..];
    }

    static IEnumerable<string> DialogLines(Step s)
    {
        var t = string.IsNullOrWhiteSpace(s.DialogTitle) ? "the dialog" : $"the {DialogName(s)} dialog";
        var cancel = s.ClosedWith == "cancel";
        if (cancel && s.Clicks.Count == 0) { yield return $"In {t}, click Cancel."; yield break; }
        // no dialog_values for it: what was set is not known, but the clicks the recorder saw are (a dialog with no click of its own is "fill in the fields")
        if (s.Values == null && s.Clicks.Count == 0) { yield return $"In {t}, fill in the fields, then click OK."; yield break; }
        var changes = cancel || s.Values == null ? new List<(string Tab, string Line)>()
            : s.Values.Select(v => (Tab: FillMatcher.TabOfChange(v), Line: DialogFieldsCore.Describe(v))).Where(x => x.Line != null).Select(x => (x.Tab, x.Line!.TrimEnd('.') + ".")).ToList();
        if (changes.Count == 0 && s.Clicks.Count == 0)
        {
            yield return s.ValuesRead > 0 ? $"In {t}, leave the values as they are and click OK." : $"In {t}, click OK.";
            yield break;
        }
        yield return $"In {t}:";
        // a dialog with buttons clicked in it: the changes made before each click, the click, and the dialog it opened (Tdoc's picture shows the final fill).
        // In a dialog with tabs the sentences are grouped by the tab they were made on ("On the Text tab:")
        var tabs = new TabLines();
        foreach (var click in s.Clicks)
        {
            foreach (var v in click.Values)
                if (DialogFieldsCore.Describe(v) is { } d)
                    foreach (var line in tabs.Add(FillMatcher.TabOfChange(v), d.TrimEnd('.') + ".")) yield return line;
            foreach (var line in tabs.Add(click.Tab, click.Menu != null ? click.Menu.Wording(false) + "." : click.Kind == DialogClickKinds.DoubleClick ? DialogClickKinds.DoubleClickWords(click.Path, click.TreeLabel, x => x, x => x) + "." : $"Click {click.Button}.")) yield return line;
            foreach (var nested in click.Nested)
                foreach (var line in DialogLines(nested)) yield return line;
            if (click.Nested.Count > 0) tabs.Reset();
        }
        foreach (var (tab, text) in changes)
            foreach (var line in tabs.Add(tab, text)) yield return line;
        // the dialog is closed from the page it was closed from (a recording from before tabs has none: the tab of the last sentence stays)
        if (!DialogSegments.LastClickCloses(s))
            foreach (var line in tabs.Add(cancel ? tabs.Current : s.CloseTab ?? tabs.Current, cancel ? "Click Cancel." : "Click OK.")) yield return line;
    }

    static IEnumerable<string> PaletteLines(Step s)
    {
        var name = string.IsNullOrWhiteSpace(s.DialogTitle) ? "palette" : $"{s.DialogTitle.Trim()} palette";
        if (s.Native != null)
        {
            yield return $"In the {name}, {s.Native.Describe()}.";
            if (s.Drawing != null) yield return $"{FileName(s.Drawing)} opens.";
            yield break;
        }
        var changes = (s.Values ?? []).Select(v => (Tab: FillMatcher.TabOfChange(v), Line: DialogFieldsCore.Describe(v))).Where(x => x.Line != null).Select(x => (x.Tab, x.Line!.TrimEnd('.') + ".")).ToList();
        var button = string.IsNullOrWhiteSpace(s.Button) ? null : s.Button.Trim();
        var press = s.Menu != null ? s.Menu.Wording(false, null, "the palette") + "." : $"Click {button}.";
        if (changes.Count == 0 && button == null) { yield return $"Look at the {name}."; yield break; }
        if (changes.Count == 0) { yield return s.Menu != null ? $"In the {name}: {press}" : $"In the {name}, " + (s.ClickKind == DialogClickKinds.DoubleClick ? DialogClickKinds.DoubleClickWords(s.ClickPath, s.ClickTreeLabel, x => x, x => x, capital: false) : $"click {button}") + "."; yield break; }
        yield return $"In the {name}:";
        var tabs = new TabLines();
        foreach (var (tab, text) in changes)
            foreach (var line in tabs.Add(tab, text)) yield return line;
        if (button != null) yield return s.Menu != null ? press : s.ClickKind == DialogClickKinds.DoubleClick ? DialogClickKinds.DoubleClickWords(s.ClickPath, s.ClickTreeLabel, x => x, x => x) + "." : $"Click {button}.";
    }

    // ---- answers at the command line ---------------------------------------------------------------------------------------

    /// <summary>"At the prompt 'Specify first point', type 10,10." for each recorded answer a person could give.</summary>
    public static IEnumerable<string> AnswerLines(IEnumerable<Answer> answers, Step? step = null)
    {
        bool first = true;
        var effective = SelectionReplay.Effective(answers.ToList());
        foreach (var a in effective)
        {
            // SETVAR: the recording holds only the value prompt, which names the variable; a person is asked for the name first
            // (the same decision as the replay's, Plan.SetvarNameToType)
            if (first && step != null && Plan.SetvarNameToType(step, effective) is { } sysvar) yield return $"At the prompt 'Enter variable name', type {sysvar}.";
            first = false;
            var action = AnswerAction(a);
            if (action == null) continue;
            var prompt = CleanPrompt(a.Message);
            yield return prompt.Length > 0 ? $"At the prompt '{prompt}', {action}." : char.ToUpperInvariant(action[0]) + action[1..] + ".";
        }
    }

    /// <summary>What to do at a prompt, as a phrase ("type 10,10", "press Enter"); null for an answer a person cannot give by typing.</summary>
    public static string? AnswerAction(Answer a)
    {
        if (a.Status == "None") return "press Enter";
        if (a.Status == "Cancel") return "press Esc";
        if (a.Status == "Keyword") return string.IsNullOrWhiteSpace(a.Keyword) ? "press Enter" : $"type {a.Keyword}";
        if (a.Status != "OK") return null;
        switch (a.Kind)
        {
            case "entity" or "nestedEntity": return "select the object";
            case "selection":
                // the text typed for it, as the replay types it; otherwise a person picks the objects
                if (a.Value is { } sv && SelectionReplay.TypedLines(sv) is { } typedLines)
                {
                    var words = typedLines.Where(l => l.Length > 0).ToList();
                    var closes = typedLines[^1].Length == 0;
                    return "type " + string.Join(", then ", words) + (closes ? " and press Enter" : "");
                }
                return "select the objects and press Enter";
        }
        List<string>? lines;
        try { lines = Plan.TypedLines(a); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException) { return null; }
        if (lines == null || lines.Count == 0) return null;
        return lines[0].Length == 0 ? "press Enter" : $"type {lines[0]}";
    }

    /// <summary>The prompt text without line breaks, its leading "Command:" noise and the closing colon.</summary>
    public static string CleanPrompt(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var last = message.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        return last.TrimEnd(':', ' ');
    }

    static readonly System.Text.RegularExpressions.Regex LispCommand = Engine.Util.Rx.Create(@"^\(\s*c:([A-Za-z0-9_\-]+)\s*\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>What a person types for a LISP step: a command defined in LISP is recorded as "(C:KITDIALOG)" but typed as KITDIALOG.</summary>
    public static string LispTyped(Step s)
    {
        var lisp = (s.Lisp ?? "").Trim();
        if (lisp.Length == 0) return "the LISP expression";
        var m = LispCommand.Match(lisp);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : lisp;
    }

    /// <summary>The command line as the picture's box tells it: "Command line: type LINE, then 10,10, then 20,20". Null when the step types nothing.</summary>
    public static string? CommandLineText(Step s)
    {
        const int Max = 6;
        var parts = new List<string>();
        if (s.Kind == "command") parts.Add($"type {s.Command ?? "the command"}");
        else if (s.Kind == "lisp") parts.Add($"type {LispTyped(s)}");
        foreach (var a in SelectionReplay.Effective(s.Answers))
            if (AnswerAction(a) is { } act) parts.Add(act.StartsWith("type ", StringComparison.Ordinal) && parts.Count > 0 ? act[5..] : act);
        if (parts.Count == 0) return null;
        var shown = parts.Take(Max).ToList();
        return "Command line: " + string.Join(", then ", shown) + (parts.Count > Max ? ", ..." : "");
    }

    // ---- legend lines --------------------------------------------------------------------------------------------------------

    /// <summary>The boxes a ribbon, command or LISP step would like on its picture, in the order they are numbered.</summary>
    public static List<CalloutPlan> StepCallouts(Step s)
    {
        var plans = new List<CalloutPlan>();
        if (s.Kind == "ribbon" && RibbonLabel(s) is { } label) plans.Add(new CalloutPlan(CalloutRole.RibbonItem, "Click " + label));
        if (s.Kind is "ribbon" or "command" or "lisp" && CommandLineText(s) is { } cl) plans.Add(new CalloutPlan(CalloutRole.CommandLine, cl));
        return plans;
    }

    /// <summary>The legend line of a dialog or palette field: "Name: KitBlock", "Snap: on", "Metric" (a radio button), "Name: (empty)".</summary>
    public static string FieldCallout(FieldChange c)
    {
        var label = DialogFieldsCore.CleanLabel(c.Label);
        string text;
        switch (c.Kind)
        {
            case "radio":
                text = c.After is "on" or "off" or "" ? label : c.After;
                break;
            case "check":
                text = $"{label}: {c.After}";
                break;
            case "treecheck":
                text = $"{TreeChecks.PathOf(c)}: {c.After}";
                break;
            default:
                text = c.After.Length == 0 ? $"{label}: (empty)" : $"{label}: {c.After}";
                break;
        }
        return text.Length > 70 ? text[..67] + "..." : text;
    }

    /// <summary>The legend line of the button that closes a dialog.</summary>
    /// <param name="shown">the button's own text when it is known: a LISP alert's only button reads "OK" but has Cancel's id</param>
    public static string CloseCallout(bool ok, string? shown = null) =>
        DialogFieldsCore.CleanLabel(shown ?? "") is { Length: > 0 } t ? t : ok ? "OK" : "Cancel";

    /// <summary>The caption of a picture, or null for none.</summary>
    public static string? Caption(Step s) => s.Kind switch
    {
        "dialog" => string.IsNullOrWhiteSpace(s.DialogTitle) ? "The dialog, filled in" : $"The {DialogName(s)} dialog, filled in",
        "palette" => string.IsNullOrWhiteSpace(s.DialogTitle) ? "The palette" : $"The {s.DialogTitle.Trim()} palette",
        "mark" => "The drawing at this checkpoint",
        _ => null,
    };

    /// <summary>Numbers the legend lines whose box was found: 1, 2, 3 in order, skipping the ones that were not (a legend line without a box would
    /// point at nothing).</summary>
    public static List<NumberedCallout> Number(IReadOnlyList<CalloutSpec> specs)
    {
        var list = new List<NumberedCallout>();
        for (int i = 0; i < specs.Count; i++)
            if (specs[i].Located) list.Add(new NumberedCallout(list.Count + 1, specs[i].Text, i));
        return list;
    }
}
