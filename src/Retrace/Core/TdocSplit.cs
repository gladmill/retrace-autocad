namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>One step of the training document. <see cref="Shown"/> is what the text and the boxes are made from; <see cref="Index"/> is the replay step it belongs to.
/// <see cref="After"/>: answers a command's person gave after one of its dialogs closed, as a step of their own: <see cref="Index"/> is the dialog step they follow, <see cref="Group"/> and <see cref="GroupEnd"/> the
/// command's first and last dialog step (the replay types all of them once a dialog of the command is done), <see cref="Last"/> says no dialog of the command follows them, and <see cref="Full"/> is the command's step
/// with all its answers. <see cref="Lead"/>: the first answers of the command are here (the part before its first dialog has none), so a SETVAR's variable name goes here.</summary>
public sealed record TdocEntry(Step Shown, int Index, bool After, int Group = -1, bool Last = false, Step? Full = null, bool Lead = false, int GroupEnd = -1);

/// <summary>
/// The steps of a Tdoc, in the order a person does them. A command that opens dialogs (a ribbon click, a typed command) keeps all its prompts in one recorded step, but the person
/// answered some before the first dialog opened, some between two dialogs and the rest after the last one closed: the replay types them that way (<see cref="Plan.InputAroundDialog"/>). The
/// document says the same: the command's step holds the answers before the first dialog, each dialog step follows, and the answers given after a dialog closed come as a step of their own
/// right after it ("INSERTPART: answer the prompts"), pictured when the command has ended (the last one) or when the next dialog has opened.
/// </summary>
public static class TdocSplit
{
    /// <summary>The Tdoc's steps for the replay's <paramref name="steps"/>. Nothing changes for a step with no dialog step after it, or with no printable answer after its first dialog opened.</summary>
    public static List<TdocEntry> Entries(IReadOnlyList<Step> steps)
    {
        var list = new List<TdocEntry>();
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            int last = i;
            while (last + 1 < steps.Count && steps[last + 1].Kind == "dialog") last++;
            if (last == i || s.Kind is not ("ribbon" or "command" or "lisp") || s.ChainClick) { list.Add(new TdocEntry(s, i, false)); continue; }

            // the answers by the dialog they follow: slot 0 = before the first dialog, slot k = after dialog k (by the time each dialog step opened, as InputAroundDialog splits at the first)
            int n = last - i;
            var slots = new List<Answer>[n + 1];
            for (int k = 0; k <= n; k++) slots[k] = [];
            foreach (var a in s.Answers)
            {
                int k = 0;
                while (k < n && a.T >= steps[i + 1 + k].T) k++;
                slots[k].Add(a);
            }
            bool Printable(List<Answer> l) => l.Count > 0 && TdocPlan.AnswerLines(l).Any();
            if (!Enumerable.Range(1, n).Any(k => Printable(slots[k]))) { list.Add(new TdocEntry(s, i, false)); continue; }

            list.Add(new TdocEntry(s.WithAnswers(slots[0]), i, false, Full: s));
            bool lead = slots[0].Count == 0;
            for (int k = 1; k <= n; k++)
            {
                list.Add(new TdocEntry(steps[i + k], i + k, false));
                if (!Printable(slots[k])) continue;
                list.Add(new TdocEntry(s.WithAnswers(slots[k]), i + k, true, Group: i + 1, Last: k == n, Full: s, Lead: lead, GroupEnd: last));
                lead = false;
            }
            i = last;
        }
        return list;
    }

    /// <summary>The heading of the step that holds the answers after a dialog.</summary>
    public static string ContinuationTitle(Step s) =>
        string.IsNullOrWhiteSpace(s.Command) ? "Answer the prompts" : $"{s.Command.Trim().ToUpperInvariant()}: answer the prompts";

    /// <summary>The sentences of the continuation step: the prompts, with no "Click" or "Type" start.</summary>
    public static List<string> ContinuationInstructions(TdocEntry e) => TdocPlan.AnswerLines(e.Shown.Answers, e.Lead ? e.Full : null, e.Full?.Answers).ToList();

    /// <summary>The sentences of a Tdoc step.</summary>
    public static List<string> Instructions(TdocEntry e) =>
        e.After ? ContinuationInstructions(e)
        : e.Full != null ? [.. TdocPlan.Instructions(e.Shown, e.Full.Answers)]
        : TdocPlan.Instructions(e.Shown);

    /// <summary>The heading of a Tdoc step.</summary>
    public static string Title(TdocEntry e) => e.After ? ContinuationTitle(e.Shown) : TdocPlan.StepTitle(e.Shown);

    /// <summary>The kind written to tdoc.json: a continuation is a "command" step.</summary>
    public static string Kind(TdocEntry e) => e.After ? "command" : TdocPlan.KindOf(e.Shown);
}
