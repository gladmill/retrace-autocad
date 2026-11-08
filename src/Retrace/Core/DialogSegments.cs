using System.Text;

namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// A recorded dialog as the replay does it: segments. Each click the person made is a segment (set these values, press this button), and the
// close is the last one (set the values changed since the last click, press OK or Cancel). A nested dialog belongs to the click that opened it.

/// <summary>One piece of a dialog replay. <see cref="Button"/> null = the closing segment. <see cref="Nested"/>: the dialogs pressing the button opens.
/// <see cref="Menu"/>: the click is a context menu choice (the button text is then the menu path). <see cref="Tab"/>: the tab to show before the button is pressed ("" = leave it): the click's own tab, for the closing segment the tab showing when the dialog was closed.</summary>
public sealed record DialogSegment(IReadOnlyList<FieldChange> Values, string? Button, int ButtonIndex, IReadOnlyList<Step> Nested, string Tab = "", string Kind = DialogClickKinds.Click, string Tree = "", string Path = "", string TreeLabel = "", bool OnDown = false, MenuChoice? Menu = null)
{
    public bool IsDoubleClick => Button != null && Kind == DialogClickKinds.DoubleClick;
    /// <summary>A button of a toolbar: <see cref="ButtonId"/> is its command id. (The id of a push button is information only.)</summary>
    public bool IsToolbar => Button != null && Kind == DialogClickKinds.Toolbar;
    public int ButtonId { get; init; }
    /// <summary>"press Add" / "double-click A › B in Tree".</summary>
    public string Phrase => Menu != null ? Menu.Phrase() : DialogClickKinds.Phrase(Kind, Button ?? "", Path, TreeLabel);
}

/// <summary>How far the replay got with one segment: what happened to each value, and whether its button was pressed.</summary>
public sealed record SegmentProgress(IReadOnlyList<FieldFillResult> Results, bool Pressed);

public static class DialogSegments
{
    /// <summary>What there is to do in a recorded dialog besides pressing the close button: the values of the close and of every click, and one per click.
    /// Fed to <see cref="FillMatcher.Decide"/> as the number of changes: a dialog whose only work is a click still needs the replay.</summary>
    public static int Work(Step dialog) => (dialog.Values?.Count ?? 0) + dialog.Clicks.Sum(c => c.Values.Count + 1);

    /// <summary>How the replay may handle the dialog (see <see cref="FillMatcher.Decide"/>), counting the clicks as work.</summary>
    public static AutoMode ModeOf(Step dialog) =>
        dialog.ClosedWith == null && dialog.Clicks.Count > 0 && (dialog.Values?.Count ?? 0) == 0
            ? AutoMode.None   // clicks, no known close and nothing set after the last one: how it ended is not known, so a person finishes it (never an OK nobody pressed)
            : FillMatcher.Decide(dialog.Values != null, Work(dialog), dialog.ValuesRead, dialog.ClosedWith);

    /// <summary>The segments of the dialog: one per click, in order, then the closing one. <paramref name="closingValues"/> false (closed with Cancel): the
    /// values changed after the last click were thrown away with the dialog, so the closing segment sets none. The values before a click were applied by it.</summary>
    public static List<DialogSegment> Of(Step dialog, bool closingValues)
    {
        var list = new List<DialogSegment>();
        foreach (var c in dialog.Clicks) list.Add(new DialogSegment(c.Values, c.Button, c.Index, c.Nested, c.Tab, c.Kind, c.Tree, c.Path, c.TreeLabel, c.OnDown, c.Menu) { ButtonId = c.Id });
        // closed with Cancel the page showing does not matter (and is not shown first); closed with OK the dialog is left from the page the person left it from
        list.Add(new DialogSegment(closingValues && dialog.Values != null ? dialog.Values : new List<FieldChange>(), null, 1, new List<Step>(), closingValues ? dialog.CloseTab ?? "" : ""));
        return list;
    }

    /// <summary>The last click of the recording closed the dialog: the recorder wrote <c>closedWith: "click"</c> because the dialog went away after that click with no other input to it
    /// (a Close button whose id is not IDCANCEL, the Yes / No of a message box). The replay presses that button and has no OK to press after it.</summary>
    public static bool LastClickCloses(Step dialog) => LastClickCloses(dialog.ClosedWith, dialog.Clicks.Count);

    /// <summary>The one rule, for every caller (the plan, the replay, the pause message, steps.md, the Tdoc text): a dialog with at least one click and <c>closedWith: "click"</c> was closed by its last click.</summary>
    public static bool LastClickCloses(string? closedWith, int clickCount) => clickCount > 0 && closedWith == "click";

    static bool LastClickCloses(IReadOnlyList<DialogSegment> segments, string? closedWith) => LastClickCloses(closedWith, segments.Count(s => s.Button != null));

    /// <summary>The status of segment <paramref name="index"/> in the result: "ok" for the ones done (all of them when the dialog <paramref name="completed"/>), for the one the replay was in when
    /// it gave up "failed" (or "person" when a person finished the step by hand), for the ones after it "notRun" (or "person").</summary>
    public static string StatusOf(int index, int current, bool completed, bool gaveUp, bool personFinished)
    {
        if (completed || index < current) return SegmentResult.Ok;
        if (personFinished) return SegmentResult.Person;
        return index == current && gaveUp ? SegmentResult.Failed : SegmentResult.NotRun;
    }

    /// <summary>true when any segment presses a button before the close.</summary>
    public static bool HasClicks(IReadOnlyList<DialogSegment> segments) => segments.Any(s => s.Button != null);

    /// <summary>The pause message when the replay does not try the dialog at all and the dialog has clicks: everything to do, in order. "A dialog opens: Items.
    /// Set Name to A, press Add, press Options... (a nested dialog "Item options": set Row spacing to 8, then close it with OK), then close it with OK (see f.md, step 4)."</summary>
    public static string ManualMessage(string? title, string? scriptFile, int? stepNumber, Step dialog)
    {
        var t = string.IsNullOrWhiteSpace(title) ? "(untitled dialog)" : title;
        var see = scriptFile != null && stepNumber != null ? $" (see {scriptFile}, step {stepNumber})" : "";
        var items = Todo(dialog);
        var close = dialog.ClosedWith == "cancel" ? "Cancel" : "OK";
        var then = LastClickCloses(dialog) ? "" : $", then close it with {close}";
        return items.Count == 0 ? $"A dialog opens: {t}. Close it with {close}{see}."
            : $"A dialog opens: {t}. {char.ToUpperInvariant(items[0][0])}{items[0][1..]}{string.Concat(items.Skip(1).Select(i => ", " + i))}{then}{see}.";
    }

    /// <summary>What to do in a recorded dialog, in order, as short phrases: the values set before each click, the click and the dialog it opened, then the values
    /// changed after the last click (none when it was closed with Cancel).</summary>
    static List<string> Todo(Step dialog)
    {
        var items = new List<(string Tab, string Phrase)>();
        foreach (var c in dialog.Clicks)
        {
            foreach (var v in c.Values)
                if (DialogFieldsCore.Describe(v) is { } d) items.Add((FillMatcher.TabOfChange(v), char.ToLowerInvariant(d[0]) + d[1..]));
            var press = c.Menu != null ? c.Menu.Phrase() : DialogClickKinds.Phrase(c.Kind, c.Button, c.Path, c.TreeLabel);
            var nested = c.Nested.Select(n =>
            {
                var inner = Todo(n);
                var closeNested = n.ClosedWith == "cancel" ? "Cancel" : "OK";
                var name = string.IsNullOrWhiteSpace(n.DialogTitle) ? "nested dialog" : $"nested dialog \"{n.DialogTitle}\"";
                if (LastClickCloses(n)) return $"{name}: {string.Join(", ", inner)}";   // its last click closed it: no OK to press after that click
                return inner.Count == 0 ? $"{name}: close it with {closeNested}" : $"{name}: {string.Join(", ", inner)}, then close it with {closeNested}";
            }).ToList();
            items.Add((c.Tab, nested.Count == 0 ? press : $"{press} ({string.Join("; ", nested)})"));
        }
        if (dialog.ClosedWith != "cancel" && dialog.Values != null)
            foreach (var v in dialog.Values)
                if (DialogFieldsCore.Describe(v) is { } d) items.Add((FillMatcher.TabOfChange(v), char.ToLowerInvariant(d[0]) + d[1..]));
        return TabWording.Phrases(items);
    }

    /// <summary>The pause message when the replay could not (fully) do the dialog itself. Without clicks it is <see cref="FillMatcher.FallbackMessage"/>.
    /// With clicks it also names the buttons left to press: "A dialog opens: Items. Filled 1 of 3 values; set Name to B, press Add yourself, then close it with OK
    /// (see f.md, step 4)." <paramref name="progress"/> parallels <paramref name="segments"/> (shorter: the rest was not reached). A segment whose button was pressed
    /// needs nothing more; in the others only the values that did not go in are named. <paramref name="note"/> is put first.</summary>
    public static string FallbackMessage(string? title, string? scriptFile, int? stepNumber, IReadOnlyList<DialogSegment> segments, IReadOnlyList<SegmentProgress> progress,
        string? closedWith, string? note)
    {
        if (!HasClicks(segments))
            return FillMatcher.FallbackMessage(title, scriptFile, stepNumber, segments.Count > 0 ? segments[^1].Values : null,
                progress.Count > 0 ? progress[^1].Results : new List<FieldFillResult>(), closedWith, note);
        var t = string.IsNullOrWhiteSpace(title) ? "(untitled dialog)" : title;
        var see = scriptFile != null && stepNumber != null ? $" (see {scriptFile}, step {stepNumber})" : "";
        var close = closedWith == "cancel" ? "Cancel" : "OK";
        var sb = new StringBuilder($"A dialog opens: {t}. ");
        if (!string.IsNullOrWhiteSpace(note)) sb.Append(note!.Trim().TrimEnd('.')).Append(". ");
        int total = 0, done = 0;
        var left = new List<(string Tab, string Phrase)>();
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var p = i < progress.Count ? progress[i] : null;
            for (int j = 0; j < seg.Values.Count; j++)
            {
                total++;
                if (p != null && j < p.Results.Count && p.Results[j].Status == FieldFillResult.Applied) { done++; continue; }
                var d = DialogFieldsCore.Describe(seg.Values[j]);
                if (d != null) left.Add((FillMatcher.TabOfChange(seg.Values[j]), char.ToLowerInvariant(d[0]) + d[1..]));
            }
            if (seg.Button != null && p?.Pressed != true) left.Add((seg.Tab, seg.Phrase));
        }
        // the last click closes the dialog: nothing to close afterwards
        var then = LastClickCloses(segments, closedWith) ? "" : $", then close it with {close}";
        var phrases = TabWording.Phrases(left);
        if (left.Count == 0) sb.Append(total == 0 ? $"Close it with {close}{see}." : $"Filled all {total} values; close it with {close}{see}.");
        else if (total == 0) sb.Append($"{char.ToUpperInvariant(phrases[0][0])}{string.Join(", ", phrases)[1..]} yourself{then}{see}.");
        else sb.Append($"Filled {done} of {total} values; {string.Join(", ", phrases)} yourself{then}{see}.");
        return sb.ToString();
    }
}
