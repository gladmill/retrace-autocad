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
    /// <summary>When set, <see cref="StepWording.Render"/> adds one entry per sub-item (sheet row N.1, N.2 ...) it writes under the step, in order: what it is and when it happened. The evidence run reads it to
    /// know which row is an answer and which a dialog; nothing else sets it.</summary>
    public List<WordingBullet>? Bullets { get; init; }
}

/// <summary>What a sub-item under a step is: an answer at a prompt, a dialog the command opened, a status bar click made while it ran, the closing line ("The command is cancelled"). <paramref name="T"/>: its time in the recording.</summary>
public enum WordingBulletKind { Answer, Dialog, Status, Outcome }
public sealed record WordingBullet(WordingBulletKind Kind, long T);

public static class StepWording
{
    /// <summary>Markdown for one step, numbered n (null: not a step). Screenshots follow the step they belong to. A dialog that a
    /// running command opened is rendered inside that command's step (see <see cref="RecordingStep.OpenedIn"/>), so it returns null here.</summary>
    public static string? Render(RecordingStep st, int n, WordingContext ctx)
    {
        if (st.Kind == "dialog" && st.OpenedIn != null) return null;
        var sb = new StringBuilder();
        string line;
        string? where = null;   // script only: the location line under the action ("Ribbon › Home tab › Draw panel")
        string? tail = null;    // script only: the click that ends a palette step with changes
        bool script = ctx.Script;
        switch (st.Kind)
        {
            case "ribbon":
                line = script ? $"Click {RibbonLabel(st, ctx, shortLabel: true)}" : $"Click {RibbonLabel(st, ctx)}";
                if (script) where = RibbonWhere(st);
                break;
            case "command":
                line = script ? $"Type {Md.Code((st.Command ?? "?").ToUpperInvariant())} and press Enter" : $"Type {Md.Code((st.Command ?? "?").ToUpperInvariant())} on the command line and press Enter";
                if (script) where = "Command line";
                break;
            case "lisp":
                line = $"Run the LISP expression {Md.Code(st.Lisp)}";
                if (script) where = "Command line";
                break;
            case "dialog":
                line = DialogLine(st, script: script);
                break;
            case "statusbar" when st.StatusBar != null:
                line = Retrace.Core.StatusBarWording.Phrase(st.StatusBar, x => $"**{Md.Cell(x)}**");
                if (script) where = "Status bar";
                break;
            case "palette":
                if (script) (line, where, tail) = PaletteScript(st);
                else line = PaletteLine(st);
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
        if (where != null) sb.Append($"\n   *{Md.Inline(where)}*\n\n");
        if (st.Kind == "mark" && !string.IsNullOrWhiteSpace(st.Description)) sb.Append("   - ").Append(Md.Cell(st.Description)).Append('\n');
        var alt = Alt(line);
        if (st.Kind == "dialog") { DialogBody(sb, st, ctx, "   "); if (st.StartsCommand != null) sb.Append("   - The command ").Append(Md.Code(st.StartsCommand.ToUpperInvariant())).Append(" starts by itself when the dialog closes (do not type it)\n"); }
        else if (st.Kind == "palette")
        {
            foreach (var l in ChangeLines(st, script)) sb.Append("   - ").Append(l).Append('\n');
            if (tail != null) sb.Append("   - ").Append(tail).Append('\n');
            Shots(sb, st.Shots, alt, ctx, "   ", "click");
        }
        else Shots(sb, st.Shots, alt, ctx, "   ", st.Kind switch { "ribbon" or "statusbar" => "click", "mark" => "check", _ => "command" });
        if (st.Kind is "ribbon" or "command" or "lisp" || (st.Kind is "palette" or "statusbar" && st.Command != null))
        {
            int d = 0;
            bool afterSelectionInput = false;
            int ic = 0;
            void InlineUpTo(long t) { while (ic < st.InlineStatusClicks.Count && st.InlineStatusClicks[ic].T <= t) { ctx.Bullets?.Add(new(WordingBulletKind.Status, st.InlineStatusClicks[ic].T)); sb.Append("   - ").Append(StatusInline(st.InlineStatusClicks[ic++].Text)).Append('\n'); } }
            foreach (var a in st.Answers)
            {
                InlineUpTo(a.T);
                while (d < st.InnerDialogs.Count && st.InnerDialogs[d].T <= a.T) { ctx.Bullets?.Add(new(WordingBulletKind.Dialog, st.InnerDialogs[d].T)); InnerDialog(sb, st.InnerDialogs[d++], ctx); }
                // the selection result after the corners / points that made it only echoes them (the same rule as Core/SelectionReplay.Effective)
                if (a.Kind == "selection" && a.Status == "OK" && afterSelectionInput) { afterSelectionInput = false; continue; }
                if (a.Kind == "selection" && a.Status == "Error" && a.Input != true) continue;
                afterSelectionInput = a.Kind == "corner" || (a.Kind == "point" && a.Message is { } pm && (pm.Contains("select", StringComparison.OrdinalIgnoreCase) || pm.Contains("corner", StringComparison.OrdinalIgnoreCase)));
                var text = AnswerText(a);
                ctx.Bullets?.Add(new(WordingBulletKind.Answer, a.T));
                sb.Append("   - ").Append(text).Append('\n');
                Shots(sb, a.Shots, Alt(text), ctx, "     ", a.By switch { "mouse" => "answer:pick", "keys" => "answer:enter", _ => "answer:answer" });
            }
            while (d < st.InnerDialogs.Count) { ctx.Bullets?.Add(new(WordingBulletKind.Dialog, st.InnerDialogs[d].T)); InnerDialog(sb, st.InnerDialogs[d++], ctx); }
            InlineUpTo(long.MaxValue);
            if (st.Outcome is "cancelled" or "failed") ctx.Bullets?.Add(new(WordingBulletKind.Outcome, long.MaxValue));
            if (st.Outcome == "cancelled") sb.Append("   - The command is cancelled.\n");
            else if (st.Outcome == "failed") sb.Append("   - The command fails.\n");
            Shots(sb, st.EndShots, alt, ctx, "   ", "end");
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>A status bar click made inside a running command ("Click Sample TB: edit on the status bar" as it is in the plan), as a sub-item: lower-case first letter, with bold markup for the label.</summary>
    static string StatusInline(string phrase) => phrase + " (while the command is running; a replay does not click it: do it by hand)";

    /// <summary>"In the **Box** palette (Samples), make these changes, then click **Draw box**:"; the changes follow as sub-items.</summary>
    static string PaletteLine(RecordingStep st)
    {
        var where = $"In the **{Md.Cell(string.IsNullOrWhiteSpace(st.DialogTitle) ? "(untitled)" : st.DialogTitle)}** palette"
                    + (string.IsNullOrWhiteSpace(st.Panel) || st.Panel == st.DialogTitle ? "" : $" ({Md.Cell(st.Panel)})");
        if (st.Native != null)   // a native palette: one action, no values
            return $"{where}, {st.Native.Describe(Md.Cell, x => $"**{x}**")}" + (st.OpensDrawing != null ? $". It opens {Md.Code(st.OpensDrawing)}" : "");
        var click = st.Menu != null ? st.Menu.Phrase(true, Md.Cell, "the palette") : string.IsNullOrWhiteSpace(st.Text) && !Retrace.Core.PaletteClickKinds.IsIntermediate(st.ClickKind) ? null
            : st.ClickKind != Retrace.Core.DialogClickKinds.Click ? Retrace.Core.PaletteClickKinds.Phrase(st.ClickKind, st.Text, st.ClickPath, st.ClickTreeLabel, st.ClickIndex, st.ClickLegacyIndex, st.Hit, Md.Code, t => $"**{Md.Inline(t)}**", capital: false, plain: Md.Inline)
            : $"click **{Md.Cell(st.Text)}**";
        // a tree click leads with the action and the item; the palette follows ("Click **B24** under A › B, in the Catalog tree of the **Box** palette (Samples)")
        if (ChangeLines(st).Count == 0 && st.Menu == null && st.Native == null && Retrace.Core.PaletteClickKinds.OnTree(st.ClickKind) && st.ClickKind != Retrace.Core.DialogClickKinds.DoubleClick && click != null)
            return char.ToUpperInvariant(click[0]) + click[1..] + " of the " + where["In the ".Length..];
        if (ChangeLines(st).Count == 0) return click == null ? where + ", leave the values as they are" : $"{where}, {click}";
        return click == null ? where + ", make these changes:" : $"{where}, make these changes, then {click}:";
    }

    /// <summary>"A dialog opens: **Title**. Make these changes, then confirm:"; <paramref name="nested"/>: "A nested dialog opens: ..." (a dialog a button of another one
    /// opened; the wording differs so the replay's count of "A dialog opens:" lines in a script, <see cref="Retrace.Core.ScriptRef"/>, stays the count of dialog steps).</summary>
    static string DialogLine(RecordingStep st, bool nested = false, bool script = false)
    {
        if (script)   // the short action line; the changes and the closing click follow as sub-items (DialogValueLines)
        {
            // an untitled dialog is "the dialog", not a bold "(untitled dialog)"; the replay's line count still sees it ("Fill in the dialog", "Cancel the dialog")
            var name = string.IsNullOrWhiteSpace(st.DialogTitle) ? "" : $"**{Md.Cell(st.DialogTitle)}** ";
            return st.ClosedWith == "cancel" && st.Clicks.Count == 0 ? $"Cancel the {(nested ? "nested " : "")}{name}dialog" : $"Fill in the {(nested ? "nested " : "")}{name}dialog";
        }
        var head = $"A {(nested ? "nested " : "")}dialog opens: **{Md.Cell(DialogTitle(st))}**. ";
        var cancel = st.ClosedWith == "cancel";
        if (st.Clicks.Count > 0)
        {
            // the last click may have closed the dialog (a Close button, the Yes of a message box): no OK was pressed
            if (DialogSegments.LastClickCloses(st.ClosedWith, st.Clicks.Count)) return head + "Make these changes (the last click closes the dialog):";
            return head + (cancel ? "Make these changes, then close it with Cancel:" : "Make these changes, then confirm:");
        }
        return head + (cancel ? "Close it with Cancel." : st.Values.Count > 0 && ChangeLines(st).Count > 0 ? "Make these changes, then confirm:" : "Fill it in and confirm.");
    }

    /// <summary>The sub-items under a dialog line: the clicks (the changes made before each, then the click, and the dialog it opened as a sub-item), then the
    /// changes made after the last click, or "Leave the values as they are." when the recorder read fields and none changed. Nothing for sessions without
    /// a `dialog_values` event.</summary>
    static void DialogValueLines(StringBuilder sb, RecordingStep dlg, string indent, WordingContext ctx)
    {
        DialogValueLinesCore(sb, dlg, indent, ctx);
        // the script ends the list with the click that closes the dialog ("Click **OK**"), so the whole list can be followed without looking at the picture
        if (ctx.Script && CloseLine(dlg) is { } close)
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            sb.Append(indent).Append("- ").Append(close).Append('\n');
        }
    }

    /// <summary>"Click **OK**" / "Click **Cancel**"; null when the dialog's last click closed it, or it was cancelled with nothing done in it.</summary>
    static string? CloseLine(RecordingStep dlg)
    {
        if (DialogSegments.LastClickCloses(dlg.ClosedWith, dlg.Clicks.Count)) return null;
        if (dlg.ClosedWith == "cancel") return dlg.Clicks.Count > 0 ? "Click **Cancel**" : null;
        return "Click **OK**";
    }

    static void DialogValueLinesCore(StringBuilder sb, RecordingStep dlg, string indent, WordingContext ctx)
    {
        if (HasPictures(dlg)) { PicturedLines(sb, dlg, indent, ctx); return; }
        if (dlg.Clicks.Count > 0) ClickLines(sb, dlg, indent, ctx);   // a click was done in the dialog, whatever closed it
        if (dlg.ClosedWith == "cancel") return;   // whatever was typed after the last click was thrown away
        var lines = ChangeLines(dlg, ctx.Script);
        if (lines.Count == 0 && dlg.ValuesRead is > 0 && dlg.Clicks.Count == 0) lines.Add("Leave the values as they are.");
        if (lines.Count == 0) return;
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
        foreach (var l in lines) sb.Append(indent).Append("- ").Append(l).Append('\n');
    }

    /// <summary>True when the recorder took a picture of a click inside the dialog (a tree / list item, a button): the dialog's changes are then written one bullet per click, each with its picture.</summary>
    static bool HasPictures(RecordingStep dlg) => dlg.Picks.Any(p => p.Shots.Count > 0) || dlg.Clicks.Any(c => c.Shots.Count > 0);

    /// <summary>One sub-item per click in the order the person made them (a tree or list item they clicked, then the button), each followed by its picture: the dialog as it looked after that click.
    /// A value that a picked item set is not written a second time with the click after it. The changes after the last click follow.</summary>
    static void PicturedLines(StringBuilder sb, RecordingStep dlg, string indent, WordingContext ctx)
    {
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
        int next = 0;
        var segment = new List<RecordingDialogPick>();
        void TakePicks(long upTo, bool all)
        {
            segment.Clear();
            while (next < dlg.Picks.Count && (all || dlg.Picks[next].T <= upTo))
            {
                var pick = dlg.Picks[next++];
                segment.Add(pick);
                var text = PickText(pick);
                sb.Append(indent).Append("- ").Append(text).Append('\n');
                Shots(sb, pick.Shots, Alt(text), ctx, indent + "  ", "dialog-click");
            }
        }
        foreach (var c in dlg.Clicks)
        {
            TakePicks(c.T, all: false);
            var picked = segment.ToList();
            ClickOne(sb, c, indent, ctx, v => picked.Any(p => Explains(p, v)));
        }
        if (dlg.ClosedWith == "cancel") return;   // what was clicked after the last click was thrown away: not written as done (the dialog line says it was closed with Cancel)
        TakePicks(0, all: true);
        var rest = segment.ToList();
        var items = new List<(string Tab, string Text)>();
        foreach (var v in dlg.Values)
            if (!rest.Any(p => Explains(p, v)) && ChangeText(v, Md.Cell, Md.Code, bold: true, showWas: !ctx.Script) is { } t) items.Add((v.Tab, t));
        var lines = TabbedLines(items, join: false);
        if (lines.Count == 0 && dlg.Clicks.Count == 0 && dlg.Picks.Count == 0 && dlg.ValuesRead is > 0) lines.Add("Leave the values as they are.");
        foreach (var l in lines) sb.Append(indent).Append("- ").Append(l).Append('\n');
    }

    /// <summary>The value a picked item set: the same control, and the value it ended on is that very item (a click on A, then an arrow key to B, ends on B: that value is not A's, and stays with the next click).
    /// A pick whose text could not be read (a row number only) explains the control's value.</summary>
    static bool Explains(RecordingDialogPick p, RecordingDialogValue v)
    {
        if (p.Kind != v.Kind || !string.Equals(p.Label, v.Label, StringComparison.OrdinalIgnoreCase)) return false;
        if (p.Item.Length == 0) return true;
        return p.Kind == "listview" ? Retrace.Core.ListSelection.Names(v.After) is { Count: 1 } names && names[0] == p.Item : v.After == p.Item;
    }

    /// <summary>"Select `Root › Child` in the tree" for a tree item the person clicked, "Select `Bolts` in the list" for a list item; "Select row 3 in the list" when the item's text could not be read.</summary>
    static string PickText(RecordingDialogPick p)
    {
        if (p.Item.Length > 0) return ChangeText(new RecordingDialogValue(p.Label, p.Kind, "", p.Item), Md.Cell, Md.Code, bold: true) ?? $"Click `{Md.Cell(p.Item)}`";
        var where = Retrace.Core.ControlWords.Name(p.Label, p.Kind, l => $"**{Md.Cell(l)}**");
        return p.Row > 0 ? $"Select row {p.Row} in {where}" : $"Select an item in {where}";
    }

    /// <summary>One sub-item per click: "Set **Name** to `A`, click **Add**" (changes made since the click before, then the click); a dialog the click opened
    /// follows as a sub-item of it. In a dialog with tabs the changes and the click are grouped by tab, one sub-item per run: "On the **Text** tab: set **Name** to `A`, click **Add**".</summary>
    static void ClickLines(StringBuilder sb, RecordingStep dlg, string indent, WordingContext ctx)
    {
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
        foreach (var c in dlg.Clicks) ClickOne(sb, c, indent, ctx, null);
    }

    /// <summary>One click's sub-item(s), its picture(s) and the dialogs it opened. <paramref name="skip"/>: values left out (a picked item already wrote them).</summary>
    static void ClickOne(StringBuilder sb, RecordingDialogClick c, string indent, WordingContext ctx, Func<RecordingDialogValue, bool>? skip)
    {
        var items = new List<(string Tab, string Text)>();
        foreach (var v in c.Values)
            if ((skip == null || !skip(v)) && ChangeText(v, Md.Cell, Md.Code, bold: true, showWas: !ctx.Script) is { } t) items.Add((v.Tab, t));
        items.Add((c.Tab, c.Menu != null ? c.Menu.Wording(true, Md.Cell) : c.Kind == Retrace.Core.DialogClickKinds.DoubleClick
            ? Retrace.Core.DialogClickKinds.DoubleClickWords(c.Path, c.TreeLabel, Md.Code, t => $"**{Md.Cell(t)}**")
            : $"Click **{Md.Cell(c.Button)}**"));
        var lines = TabbedLines(items, join: true);
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(indent).Append("- ").Append(lines[i]).Append('\n');
            if (i == lines.Count - 1 && c.Shots.Count > 0) Shots(sb, c.Shots, Alt(lines[i]), ctx, indent + "  ", "dialog-click");
        }
        foreach (var nested in c.Nested)
        {
            sb.Append(indent).Append("  - ").Append(DialogLine(nested, nested: true, script: ctx.Script)).Append('\n');
            DialogBody(sb, nested, ctx, indent + "    ");
        }
    }

    /// <summary>The sentences of <paramref name="items"/> as lines. Items on no tab: one line each (<paramref name="join"/> false, the closing changes of a dialog) or all the consecutive ones in one line
    /// ("Set **Name** to `A`, click **Add**", the changes before a click). Consecutive items on a tab together in one line, after "On the **Text** tab:": a tab path is bold as a whole
    /// ("On the **Fit › Text placement** tab"). The sentences after the first of a line start with a lower-case letter.</summary>
    static List<string> TabbedLines(IReadOnlyList<(string Tab, string Text)> items, bool join)
    {
        var lines = new List<string>();
        foreach (var run in TabWording.Runs(items, x => x.Tab))
        {
            string Low(string t) => char.ToLowerInvariant(t[0]) + t[1..];
            var texts = run.Items.Select(x => x.Text).ToList();
            if (run.Tab.Length == 0 && !join) { lines.AddRange(texts); continue; }
            var joined = string.Join(", ", texts.Select((t, i) => run.Tab.Length == 0 && i == 0 ? t : Low(t)));
            lines.Add(run.Tab.Length == 0 ? joined : $"On the **{Md.Cell(run.Tab)}** tab: {joined}");
        }
        return lines;
    }

    static List<string> ChangeLines(RecordingStep dlg, bool script = false)
    {
        var items = new List<(string Tab, string Text)>();
        foreach (var v in dlg.Values)
            if (ChangeText(v, Md.Cell, Md.Code, bold: true, showWas: !script) is { } t) items.Add((v.Tab, t));
        return TabbedLines(items, join: false);
    }

    /// <summary>One change in words: "Set **Rows** to `5` (was `3`)", "Clear **Name** (was `x`)", "Tick **Snap**", "Untick **Snap**",
    /// "Choose **Metric**", "Choose `beauty` in **Category**", "Select `A › B` in **Parts** (was `A`)"; null when it says nothing (a radio that became "off").</summary>
    public static string? ChangeText(RecordingDialogValue v, Func<string?, string> text, Func<string?, string> code, bool bold, bool showWas = true)
    {
        string Was(string before) => showWas && before.Length > 0 ? $" (was {code(before)})" : "";
        string B(string s) => Retrace.Core.ControlWords.Name(s, v.Kind, l => bold ? $"**{text(l)}**" : text(l));   // "#1012" (no name of its own) reads "the tree" / "the list"
        switch (v.Kind)
        {
            case "check":
                if (v.After == "on") return $"Tick {B(v.Label)}";
                if (v.After == "off") return $"Untick {B(v.Label)}";
                return $"Set {B(v.Label)} to {code(v.After)}{Was(v.Before)}";
            case "radio":
                var chosen = v.After is "on" ? v.Label : v.After is "off" or "" ? null : v.After;   // "on"/"off": one radio on its own; else the text of the checked radio in a group
                return chosen == null ? null : $"Choose {B(chosen)}";
            case "treecheck":
            {
                var (tree, path) = Retrace.Core.TreeChecks.SplitLabel(v.Label);
                return Retrace.Core.TreeChecks.Sentence(v.After, tree, path, B, code);
            }
            case "tree":
                return v.After.Length == 0
                    ? $"Clear the selection in {B(v.Label)}" + Was(v.Before)
                    : $"Select {code(v.After)} in {B(v.Label)}" + Was(v.Before);
            case "listview":
                return Retrace.Core.ListSelection.Sentence(v.After, v.Label, code, B, v.Items) + Was(v.Before);
            case "list" or "combo" when v.After.Length > 0:
                return $"Choose {code(v.After)} in {B(v.Label)}" + Was(v.Before);
            default:
                return v.After.Length == 0
                    ? $"Clear {B(v.Label)}" + Was(v.Before)
                    : $"Set {B(v.Label)} to {code(v.After)}" + Was(v.Before);
        }
    }

    /// <summary>A dialog inside its command's step: a sub-item with its screenshot (or the context's picture).</summary>
    static void InnerDialog(StringBuilder sb, RecordingStep dlg, WordingContext ctx)
    {
        sb.Append("   - ").Append(DialogLine(dlg, script: ctx.Script)).Append('\n');
        DialogBody(sb, dlg, ctx, "     ");
    }

    /// <summary>Under a dialog line: its picture as it opened, the changes, and (when the person changed something and the recorder took it)
    /// the picture as it looked when they pressed OK, so the reader can compare the two. Without changes only the first picture is shown.</summary>
    static void DialogBody(StringBuilder sb, RecordingStep dlg, WordingContext ctx, string indent)
    {
        var title = DialogTitle(dlg);
        // the script lists what to change right under the step, before the pictures (unless a picture belongs to each click)
        bool listFirst = ctx.Script && !HasPictures(dlg);
        if (listFirst) DialogValueLines(sb, dlg, indent, ctx);
        var before = dlg.Shots.Where(s => s.What != "dialog-after").ToList();
        var after = dlg.Shots.Where(s => s.What == "dialog-after").Take(1).ToList();
        // the picture "as it looked when OK was pressed": not for a dialog thrown away with Cancel, nor one its last click closed (no OK was pressed)
        var changed = dlg.ClosedWith != "cancel" && !DialogSegments.LastClickCloses(dlg.ClosedWith, dlg.Clicks.Count) && (ChangeLines(dlg, ctx.Script).Count > 0 || dlg.Clicks.Count > 0);
        ctx.ShotRole = "dialog-before";
        var showAfter = changed && after.Any(s => ctx.ShotHref(s) != null);
        var pair = showAfter && before.Any(s => ctx.ShotHref(s) != null);
        if (pair) sb.Append($"\n{indent}Before:\n");
        Shots(sb, before, pair ? Alt($"{title} as it opens") : Alt(DialogLine(dlg, script: ctx.Script)), ctx, indent, "dialog-before");
        if (before.Count == 0 && ctx.DialogPicture(dlg) is { } pic)
            sb.Append($"\n{indent}![{Md.Cell(pic.Alt)}]({pic.Href})\n\n");
        if (!listFirst) DialogValueLines(sb, dlg, indent, ctx);
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
        var path = string.Join(" › ", new[] { st.Tab, st.Panel, FirstNamed(st) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return path.Length == 0 ? "(unnamed button)" : path;
    }

    /// <summary>The button's own text ("Line"), without its tab and panel.</summary>
    public static string RibbonItem(RecordingStep st)
    {
        return FirstNamed(st) ?? "(unnamed button)";
    }

    /// <summary>The first of the button's text, the joined title and the item id that is not blank (an icon-only button records `"text":""`).</summary>
    static string? FirstNamed(RecordingStep st) =>
        new[] { st.Text, st.Join?.Entry.Title, st.ItemId }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    /// <summary>The location line of a ribbon click in a script: "Ribbon › Home tab › Draw panel" ("Ribbon › Home tab" when the panel was not recorded, "Ribbon" with neither); "Quick Access Toolbar" for a click on that bar.</summary>
    public static string RibbonWhere(RecordingStep st) =>
        st.Bar == "appmenu" ? "Application menu"
        : st.Bar == "qat" ? "Quick Access Toolbar"   // a Quick Access Toolbar button has no tab or panel to name (the recording's `bar`)
        : string.Join(" › ", new[] { "Ribbon", Named(st.Tab, "tab"), Named(st.Panel, "panel") }.Where(x => x != null));

    static string? Named(string? name, string noun) => string.IsNullOrWhiteSpace(name) ? null : $"{name.Trim()} {noun}";

    /// <summary>"Pick palette": the palette a step happens in, without markup (the palette set is left out: it names nothing a tester sees).</summary>
    static string PaletteName(RecordingStep st) => string.IsNullOrWhiteSpace(st.DialogTitle) ? "Palette" : string.IsNullOrWhiteSpace(st.Panel) ? $"{st.DialogTitle.Trim()} window" : $"{st.DialogTitle.Trim()} palette";

    /// <summary>A palette step in a script: the short action, the location line, and the click that ends a step with changes (the changes come between, as sub-items).
    /// "Click **B12**" / "Pick palette › Catalog tree › Cabinets › Base"; "Fill in the **Box** palette" / "Box palette" with "Click **Draw box**" last.</summary>
    static (string Line, string? Where, string? Tail) PaletteScript(RecordingStep st)
    {
        var palette = PaletteName(st);
        var name = $"**{Md.Cell(string.IsNullOrWhiteSpace(st.DialogTitle) ? "(untitled)" : st.DialogTitle)}**";
        if (st.Native != null)
        {
            var d = st.Native.Describe(Md.Cell, x => $"**{x}**");
            return (char.ToUpperInvariant(d[0]) + d[1..] + (st.OpensDrawing != null ? $". It opens {Md.Code(st.OpensDrawing)}" : ""), palette, null);
        }
        bool changes = ChangeLines(st, true).Count > 0;
        string? click = null, clickWhere = null;
        if (st.Menu != null) click = st.Menu.Wording(true, Md.Cell, "the palette");
        else if (!string.IsNullOrWhiteSpace(st.Text) || Retrace.Core.PaletteClickKinds.IsIntermediate(st.ClickKind))
        {
            string Bold(string t) => $"**{Md.Inline(t)}**";
            if (Retrace.Core.PaletteClickKinds.OnTree(st.ClickKind))
            {
                var levels = Retrace.Core.PaletteClickKinds.ClickLevels(st.ClickPath).Select(l => l.Trim()).ToList();
                var verb = st.ClickKind switch { Retrace.Core.PaletteClickKinds.Expand => "Expand", Retrace.Core.PaletteClickKinds.Collapse => "Collapse", Retrace.Core.DialogClickKinds.DoubleClick => "Double-click", _ => "Click" };
                click = $"{verb} {Bold(levels.Count > 0 ? levels[^1] : (string.IsNullOrWhiteSpace(st.Text) ? Retrace.Core.PaletteClickKinds.BlankItem : st.Text!))}";
                var tree = Retrace.Core.TreeWording.Label(st.ClickTreeLabel);
                clickWhere = string.Join(" › ", new[] { palette, tree.Length > 0 ? tree + " tree" : "tree" }.Concat(levels.Take(Math.Max(0, levels.Count - 1))));
            }
            else if (st.ClickKind != Retrace.Core.DialogClickKinds.Click)
                click = Retrace.Core.PaletteClickKinds.Phrase(st.ClickKind, st.Text ?? "", st.ClickPath, st.ClickTreeLabel, st.ClickIndex, st.ClickLegacyIndex, st.Hit, Md.Code, Bold, capital: true, plain: Md.Inline);
            else click = $"Click {Bold(st.Text ?? "")}";
        }
        if (changes) return ($"Fill in the {name} palette", clickWhere ?? palette, click);   // a closing tree click keeps its path in the location line
        if (click == null) return ($"Leave the {name} palette as it is", palette, null);
        return (click, clickWhere ?? palette, null);
    }

    private static string RibbonLabel(RecordingStep st, WordingContext ctx, bool shortLabel = false)
    {
        var label = $"**{Md.Cell(shortLabel ? RibbonItem(st) : RibbonPath(st))}**";
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
            {
                sb.Append($"\n{indent}![{alt}]({href})\n\n");
                if (shot.Opened is { } palette) sb.Append(indent).Append(OpensCaption(palette, shot.OpenedState)).Append("\n\n");   // an end picture with a palette that opened in it
            }
    }

    /// <summary>The caption under the end picture of a command that opened a palette: "The **Pick** palette opens."</summary>
    public static string OpensCaption(string palette, string? state = null) => state switch
    {
        "rolled" => $"The **{Md.Cell(palette)}** palette opens (rolled up: hover it to expand).",
        "anchored" => $"The **{Md.Cell(palette)}** palette opens (anchored at the edge of the window: hover its tab to expand).",
        _ => $"The **{Md.Cell(palette)}** palette opens.",
    };

    /// <summary>True for a line <see cref="OpensCaption"/> wrote (plain text, markup removed); the Excel sheet puts it in the row's Expected.</summary>
    public static bool IsOpensCaption(string text) => text.StartsWith("The ", StringComparison.Ordinal)
        && (text.EndsWith(" palette opens.", StringComparison.Ordinal) || text.EndsWith(" palette opens (rolled up: hover it to expand).", StringComparison.Ordinal) || text.EndsWith(" palette opens (anchored at the edge of the window: hover its tab to expand).", StringComparison.Ordinal));

    /// <summary>Step text as image alt text: no emphasis, code ticks or brackets; a link keeps only its text.</summary>
    public static string Alt(string text) =>
        Regex.Replace(Regex.Replace(Regex.Replace(text, @"\\([\\*`\[\]<_])", "$1", RegexOptions.None, TimeSpan.FromSeconds(2)), @"\[([^\]]*)\]\([^)]*\)", "$1", RegexOptions.None, TimeSpan.FromSeconds(2)).Replace("*", "").Replace("`", "").Replace("[", "(").Replace("]", ")"), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(2)).Trim();

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
            case "Error" when a.Kind == "selection": return "Press Enter";   // the Enter that ended a selection (recorded as an error result)
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
                // the text typed for it, as the replay types it (Core/TypedInput): "w", "-1,5", "30,30", Enter
                if (a.Value is { ValueKind: JsonValueKind.Object } sv && sv.TryGetProperty("typed", out var tp) && tp.ValueKind == JsonValueKind.String
                    && tp.GetString() is { Length: > 0 } tt && tt.EndsWith('\n'))
                {
                    var lines = tt.Split('\n').SkipLast(1).ToList();
                    var shown = lines.Where(l => l.Length > 0).Select(Md.Code);
                    return "Type " + string.Join(", then ", shown) + (lines.Count > 0 && lines[^1].Length == 0 ? " and press Enter" : "");
                }
                return "Select " + Md.Cell(a.Describe());
            case "entity" or "nestedEntity":
                return "Select the " + Md.Cell(a.Describe().Split(' ')[0]);
        }
        return Md.Cell(a.Describe());
    }
}
