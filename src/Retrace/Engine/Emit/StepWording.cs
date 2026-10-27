// The plain wording of recorded steps, shared by the guide's How-to pages and the single-session test script (steps.md).
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Retrace.Engine.Recordings;
using Retrace.Engine.Util;

namespace Retrace.Engine.Emit;

/// <summary>What differs between the pages that use <see cref="StepWording"/>: links, where pictures live, and the wording of checks.</summary>
public sealed class WordingContext
{
    /// <summary>Href for a ribbon step's label (a page of the guide or of the reference docs), null = plain label.</summary>
    public Func<RecordingStep, string?> RibbonHref { get; init; } = _ => null;
    /// <summary>Markdown path of a recorded screenshot as the page must link it, null = leave the shot out.</summary>
    public Func<RecordingShot, string?> ShotHref { get; init; } = _ => null;
    /// <summary>A picture to show under a dialog step that has no screenshot (alt text, path), e.g. a wireframe.</summary>
    public Func<RecordingStep, (string Alt, string Href)?> DialogPicture { get; init; } = _ => null;
    /// <summary>True in a test script: checkpoints read "... - the drawing should now have ..." (a tester compares), else "(the drawing now has ...)".</summary>
    public bool Script { get; init; }
    /// <summary>What the pictures being written are: click | command | dialog-before | dialog-after | end | check | answer:pick | answer:enter | answer:answer.
    /// <see cref="StepWording"/> sets it just before it asks <see cref="ShotHref"/>; the test script names its pictures by it.</summary>
    public string ShotRole { get; set; } = "";
}

public static class StepWording
{
    /// <summary>Markdown for one step, numbered n (null: not a step). Screenshots follow the step they belong to. A dialog that a
    /// running command opened is rendered inside that command's step (see <see cref="RecordingStep.OpenedIn"/>), so it returns null here.</summary>
    public static string? Render(RecordingStep st, int n, WordingContext ctx)
    {
        if (st.Kind == "dialog" && st.OpenedIn != null) return null;
        var sb = new StringBuilder();
        string line;
        switch (st.Kind)
        {
            case "ribbon":
                line = $"Click {RibbonLabel(st, ctx)}";
                break;
            case "command":
                line = $"Type {Md.Code((st.Command ?? "?").ToUpperInvariant())} on the command line and press Enter";
                break;
            case "lisp":
                line = $"Run the LISP expression {Md.Code(st.Lisp)}";
                break;
            case "dialog":
                line = DialogLine(st);
                break;
            case "palette":
                line = PaletteLine(st);
                break;
            case "mark":
                line = Check(st, ctx.Script);
                break;
            case "doc":
                line = Md.Cell(st.Note);
                break;
            default:
                return null;
        }
        sb.Append($"{n}. {line}\n");
        if (st.Kind == "mark" && !string.IsNullOrWhiteSpace(st.Description)) sb.Append("   - ").Append(Md.Cell(st.Description)).Append('\n');
        var alt = Alt(line);
        if (st.Kind == "dialog") DialogBody(sb, st, ctx, "   ");
        else if (st.Kind == "palette")
        {
            foreach (var l in ChangeLines(st)) sb.Append("   - ").Append(l).Append('\n');
            Shots(sb, st.Shots, alt, ctx, "   ", "click");
        }
        else Shots(sb, st.Shots, alt, ctx, "   ", st.Kind switch { "ribbon" => "click", "mark" => "check", _ => "command" });
        if (st.Kind is "ribbon" or "command" or "lisp" || (st.Kind == "palette" && st.Command != null))
        {
            int d = 0;
            foreach (var a in st.Answers)
            {
                while (d < st.InnerDialogs.Count && st.InnerDialogs[d].T <= a.T) InnerDialog(sb, st.InnerDialogs[d++], ctx);
                var text = AnswerText(a);
                sb.Append("   - ").Append(text).Append('\n');
                Shots(sb, a.Shots, Alt(text), ctx, "     ", a.By switch { "mouse" => "answer:pick", "keys" => "answer:enter", _ => "answer:answer" });
            }
            while (d < st.InnerDialogs.Count) InnerDialog(sb, st.InnerDialogs[d++], ctx);
            if (st.Outcome == "cancelled") sb.Append("   - The command is cancelled.\n");
            else if (st.Outcome == "failed") sb.Append("   - The command fails.\n");
            Shots(sb, st.EndShots, alt, ctx, "   ", "end");
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>"In the **Box** palette (Samples), make these changes, then click **Draw box**:"; the changes follow as sub-items.</summary>
    static string PaletteLine(RecordingStep st)
    {
        var where = $"In the **{Md.Cell(string.IsNullOrWhiteSpace(st.DialogTitle) ? "(untitled)" : st.DialogTitle)}** palette"
                    + (string.IsNullOrWhiteSpace(st.Panel) || st.Panel == st.DialogTitle ? "" : $" ({Md.Cell(st.Panel)})");
        var click = string.IsNullOrWhiteSpace(st.Text) ? null : $"click **{Md.Cell(st.Text)}**";
        if (ChangeLines(st).Count == 0) return click == null ? where + ", leave the values as they are" : $"{where}, {click}";
        return click == null ? where + ", make these changes:" : $"{where}, make these changes, then {click}:";
    }

    static string DialogLine(RecordingStep st) =>
        $"A dialog opens: **{Md.Cell(DialogTitle(st))}**. " + (st.ClosedWith == "cancel" ? "Close it with Cancel."
            : st.Values.Count > 0 && ChangeLines(st).Count > 0 ? "Make these changes, then confirm:" : "Fill it in and confirm.");

    /// <summary>The sub-items under a dialog line: what the person changed, or "Leave the values as they are." when the recorder read
    /// fields and none changed. Nothing for sessions without a `dialog_values` event.</summary>
    static void DialogValueLines(StringBuilder sb, RecordingStep dlg, string indent)
    {
        if (dlg.ClosedWith == "cancel") return;   // whatever was typed was thrown away
        var lines = ChangeLines(dlg);
        if (lines.Count == 0 && dlg.ValuesRead is > 0) lines.Add("Leave the values as they are.");
        if (lines.Count == 0) return;
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
        foreach (var l in lines) sb.Append(indent).Append("- ").Append(l).Append('\n');
    }

    static List<string> ChangeLines(RecordingStep dlg)
    {
        var lines = new List<string>();
        foreach (var v in dlg.Values)
            if (ChangeText(v, Md.Cell, Md.Code, bold: true) is { } t) lines.Add(t);
        return lines;
    }

    /// <summary>One change in words: "Set **Rows** to `5` (was `3`)", "Clear **Name** (was `x`)", "Tick **Snap**", "Untick **Snap**",
    /// "Choose **Metric**", "Choose `beauty` in **Category**"; null when it says nothing (a radio that became "off").</summary>
    public static string? ChangeText(RecordingDialogValue v, Func<string?, string> text, Func<string?, string> code, bool bold)
    {
        string B(string s) => bold ? $"**{text(s)}**" : text(s);
        switch (v.Kind)
        {
            case "check":
                if (v.After == "on") return $"Tick {B(v.Label)}";
                if (v.After == "off") return $"Untick {B(v.Label)}";
                return $"Set {B(v.Label)} to {code(v.After)} (was {code(v.Before)})";
            case "radio":
                var chosen = v.After is "on" ? v.Label : v.After is "off" or "" ? null : v.After;   // "on"/"off": one radio on its own; else the text of the checked radio in a group
                return chosen == null ? null : $"Choose {B(chosen)}";
            case "list" or "combo" when v.After.Length > 0:
                return $"Choose {code(v.After)} in {B(v.Label)}" + (v.Before.Length > 0 ? $" (was {code(v.Before)})" : "");
            default:
                return v.After.Length == 0
                    ? $"Clear {B(v.Label)}" + (v.Before.Length > 0 ? $" (was {code(v.Before)})" : "")
                    : $"Set {B(v.Label)} to {code(v.After)}" + (v.Before.Length > 0 ? $" (was {code(v.Before)})" : "");
        }
    }

    /// <summary>A dialog inside its command's step: a sub-item with its screenshot (or the context's picture).</summary>
    static void InnerDialog(StringBuilder sb, RecordingStep dlg, WordingContext ctx)
    {
        sb.Append("   - ").Append(DialogLine(dlg)).Append('\n');
        DialogBody(sb, dlg, ctx, "     ");
    }

    /// <summary>Under a dialog line: its picture as it opened, the changes, and (when the person changed something and the recorder took it)
    /// the picture as it looked when they pressed OK, so the reader can compare the two. Without changes only the first picture is shown.</summary>
    static void DialogBody(StringBuilder sb, RecordingStep dlg, WordingContext ctx, string indent)
    {
        var title = DialogTitle(dlg);
        var before = dlg.Shots.Where(s => s.What != "dialog-after").ToList();
        var after = dlg.Shots.Where(s => s.What == "dialog-after").Take(1).ToList();
        var changed = dlg.ClosedWith != "cancel" && ChangeLines(dlg).Count > 0;
        ctx.ShotRole = "dialog-before";
        var showAfter = changed && after.Any(s => ctx.ShotHref(s) != null);
        var pair = showAfter && before.Any(s => ctx.ShotHref(s) != null);
        if (pair) sb.Append($"\n{indent}Before:\n");
        Shots(sb, before, pair ? Alt($"{title} as it opens") : Alt(DialogLine(dlg)), ctx, indent, "dialog-before");
        if (before.Count == 0 && ctx.DialogPicture(dlg) is { } pic)
            sb.Append($"\n{indent}![{Md.Cell(pic.Alt)}]({pic.Href})\n\n");
        DialogValueLines(sb, dlg, indent);
        if (!showAfter) return;   // the after picture is shown even when the before picture is missing
        sb.Append($"\n{indent}After (what it should look like before you press OK):\n");
        Shots(sb, after, Alt($"{title} with the changes"), ctx, indent, "dialog-after");
    }

    /// <summary>The session's name: the header `name`, else the file name.</summary>
    public static string SessionName(RecordingSession s) => string.IsNullOrWhiteSpace(s.HeaderStr("name")) ? s.Name : s.HeaderStr("name")!.Trim();

    /// <summary>"Recorded 2026-10-08, with AutoCAD 2026 25.1.0.0, workspace Drafting" (no final period; empty if the header has none of it).</summary>
    public static string MetaLine(RecordingSession s)
    {
        var meta = new List<string>();
        var started = s.HeaderStr("started");
        if (!string.IsNullOrWhiteSpace(started)) meta.Add("Recorded " + (started.Length >= 10 ? started[..10] : started));
        var product = string.Join(" ", new[] { s.HeaderStr("product"), s.HeaderStr("autocad") }.Where(x => !string.IsNullOrEmpty(x)));
        if (product.Length > 0) meta.Add("with " + product);
        if (!string.IsNullOrWhiteSpace(s.HeaderStr("workspace"))) meta.Add($"workspace {s.HeaderStr("workspace")}");
        return Md.Cell(string.Join(", ", meta));
    }

    public static string DialogTitle(RecordingStep st) => string.IsNullOrWhiteSpace(st.DialogTitle) ? "(untitled dialog)" : st.DialogTitle;

    /// <summary>"Home › Draw › Line" (without markup).</summary>
    public static string RibbonPath(RecordingStep st)
    {
        var path = string.Join(" › ", new[] { st.Tab, st.Panel, st.Text ?? st.Join?.Entry.Title ?? st.ItemId }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return path.Length == 0 ? "(unnamed button)" : path;
    }

    private static string RibbonLabel(RecordingStep st, WordingContext ctx)
    {
        var label = $"**{Md.Cell(RibbonPath(st))}**";
        return ctx.RibbonHref(st) is { } href ? $"[{label}]({href})" : label;
    }

    private static string Check(RecordingStep st, bool script)
    {
        var note = string.IsNullOrWhiteSpace(st.Note) ? "the drawing looks as expected" : Md.Cell(st.Note);
        if (st.State == null) return $"**Check:** {note}";
        return script ? $"**Check:** {note} — {StateText(st.State, true)}" : $"**Check:** {note} ({StateText(st.State)})";
    }

    private static void Shots(StringBuilder sb, List<RecordingShot> shots, string alt, WordingContext ctx, string indent, string role)
    {
        ctx.ShotRole = role;
        foreach (var shot in shots)
            if (ctx.ShotHref(shot) is { } href)
                sb.Append($"\n{indent}![{alt}]({href})\n\n");
    }

    /// <summary>Step text as image alt text: no emphasis, code ticks or brackets; a link keeps only its text.</summary>
    public static string Alt(string text) =>
        Regex.Replace(Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1", RegexOptions.None, TimeSpan.FromSeconds(2)).Replace("*", "").Replace("`", "").Replace("[", "(").Replace("]", ")"), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(2)).Trim();

    /// <summary>"the drawing now has 1 LINE, 1 CIRCLE" / "the drawing is empty"; should: "the drawing should now have ..." / "should now be empty".</summary>
    public static string StateText(RecordingState state, bool should = false)
    {
        if (state.ByType.Count == 0) return should ? "the drawing should now be empty" : "the drawing is empty";
        return (should ? "the drawing should now have " : "the drawing now has ") + string.Join(", ",
            state.ByType.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Value} {t.Key}"));
    }

    private static readonly Regex Brackets = Rx.Create(@"\[[^\]]*\]|<[^>]*>", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>"Specify center point for circle or [3P/2P]:" -> "center point for circle"; null when nothing is left.</summary>
    public static string? PromptSubject(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var t = Brackets.Replace(message, " ").Trim().TrimEnd(':').Trim();
        t = Regex.Replace(t, @"^(specify|enter|select)\s+", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        t = Regex.Replace(t, @"\s+or\s*$", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)).Trim();
        t = Rx.Replace(t, @"\s+", " ");
        if (t.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) t = t[4..];
        return t.Length == 0 ? null : t;
    }

    static string Num(double d) => Math.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture);

    static bool TryNumber(RecordingAnswer a, out double d)
    {
        d = 0;
        return a.Value is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out d);
    }

    /// <summary>One prompt answer in plain words. Points are rounded to 4 decimals. A point the recorder saw typed (`by: keys`) reads
    /// "Type `x,y` for ..."; a picked one, or one from a session without `by`, reads "Pick ... (recorded at x, y)".</summary>
    public static string AnswerText(RecordingAnswer a)
    {
        var subject = PromptSubject(a.Message);
        switch (a.Status)
        {
            case "None": return "Press Enter";
            case "Cancel": return "Press Esc to cancel";
            case "Keyword" when a.IsTypedInput:
                // free input at a prompt that also takes keywords (CIRCLE radius given 20) comes back as a Keyword result
                return double.TryParse(a.Keyword, NumberStyles.Float, CultureInfo.InvariantCulture, out var typed)
                    ? $"Enter {Num(typed)}" + (subject != null ? $" for the {Md.Cell(subject)}" : "")
                    : $"Type {Md.Code(a.Keyword)}" + (subject != null ? $" for the {Md.Cell(subject)}" : "");
            case "Keyword": return string.IsNullOrWhiteSpace(a.Keyword) ? "Choose an option" : $"Choose the option **{Md.Cell(a.Keyword)}**";
            case "OK": break;
            default: return $"({Md.Cell(a.Status)})";
        }
        switch (a.Kind)
        {
            case "point" or "corner":
            {
                var what = subject != null ? "the " + subject : a.Kind == "corner" ? "the opposite corner" : "a point";
                if (a.Value is { ValueKind: JsonValueKind.Array } arr)
                {
                    var xy = arr.EnumerateArray().Take(2).Where(x => x.ValueKind == JsonValueKind.Number).Select(x => Num(x.GetDouble())).ToList();
                    if (xy.Count == 2 && a.By == "keys") return $"Type {Md.Code(string.Join(",", xy))} for {Md.Cell(what)}";
                    if (xy.Count == 2) return $"Pick {Md.Cell(what)} (recorded at {string.Join(", ", xy)})";
                }
                return $"Pick {Md.Cell(what)}";
            }
            case "distance" or "double" when TryNumber(a, out var d):
                return $"Enter {Num(d)}" + (subject != null ? $" for the {Md.Cell(subject)}" : "");
            case "integer" when TryNumber(a, out var i):
                return $"Enter {Num(i)}" + (subject != null ? $" for the {Md.Cell(subject)}" : "");
            case "angle" when TryNumber(a, out var rad):
                return $"Enter {Num(rad * 180.0 / Math.PI)} degrees" + (subject != null ? $" for the {Md.Cell(subject)}" : "");
            case "string":
            {
                var s = a.Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
                if (string.IsNullOrEmpty(s)) return "Press Enter";
                return $"Type {Md.Code(s)}" + (subject != null ? $" for the {Md.Cell(subject)}" : "");
            }
            case "selection":
                return "Select " + Md.Cell(a.Describe());
            case "entity" or "nestedEntity":
                return "Select the " + Md.Cell(a.Describe().Split(' ')[0]);
        }
        return Md.Cell(a.Describe());
    }
}
