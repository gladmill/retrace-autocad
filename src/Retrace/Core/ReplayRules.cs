namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). Small rules of the replayer that need no AutoCAD to test.
public static class ReplayRules
{
    /// <summary>True when nothing is left to run from step index <paramref name="i"/> (0-based): there are no steps, or only dialog steps. A CLOSE
    /// that ends a drawing can be followed by the "Save changes?" dialog the drawing's own command showed: those belong to the closed drawing.</summary>
    public static bool NothingLeftToRun(IReadOnlyList<Step> steps, int i)
    {
        for (var k = Math.Max(0, i); k < steps.Count; k++)
            if (steps[k].Kind != "dialog") return false;
        return true;
    }

    /// <summary>A title too generic to tell one dialog from another: AutoCAD's own name (an error or alert box carries it) or the main window's title.</summary>
    public static bool GenericTitle(string? title, string? mainWindowTitle)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return true;
        if (t.Equals("AutoCAD", StringComparison.OrdinalIgnoreCase) || t.StartsWith("AutoCAD - [", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Autodesk AutoCAD", StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(mainWindowTitle) && t.Equals(mainWindowTitle.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A dialog is open while step <paramref name="i"/> (0-based, not yet sent) is not a dialog step. In a recording made before <see cref="Plan.ScriptChain"/>, a ribbon click
    /// whose LISP runs a SCRIPT file was planned as the click plus a step per command; the click ran them all, and its last command opened this dialog. Returns the index of the
    /// dialog step to go on with, or null (today's behaviour) unless it is safe: the step just sent is a ribbon click, every step from <paramref name="i"/> up to the dialog step is a
    /// command or LISP step with no answer a person gave, the step right before the dialog step opened a dialog (it is the command that owns it), and the first dialog step
    /// after them is the open dialog: its title is equal to the open one (never a generic one such as "AutoCAD" or the main window's title: an error box a click raised would match), and, when the
    /// recording has the window class, the class is equal too.</summary>
    public static int? DialogAfterChain(IReadOnlyList<Step> steps, int i, string openTitle, string? openClass, string? mainWindowTitle)
    {
        if (i <= 0 || i >= steps.Count || steps[i - 1].Kind != "ribbon") return null;
        for (var k = i; k < steps.Count; k++)
        {
            var s = steps[k];
            if (s.Kind == "dialog")
            {
                if (k == i || !steps[k - 1].HadDialog) return null;
                if (GenericTitle(s.DialogTitle, mainWindowTitle) || GenericTitle(openTitle, mainWindowTitle)) return null;
                if (!FillMatcher.TitleMatches(s.DialogTitle, openTitle)) return null;
                if (!string.IsNullOrEmpty(s.DialogClass) && !string.Equals(NormalizeClass(s.DialogClass), NormalizeClass(openClass), StringComparison.Ordinal)) return null;
                return k;
            }
            if (s.Kind is not ("command" or "lisp") || s.Answers.Any(a => a.Input != false)) return null;
        }
        return null;
    }

    /// <summary>The steps after the dialog steps that follow dialog step <paramref name="dialogAt"/> which the click's script already ran, when the net (<see cref="DialogAfterChain"/>) fired: command steps that start
    /// <see cref="Plan.ContinuesChain"/> after the previous command ended (the command that owned the dialogs, then each skipped one). A step that holds an answer a person gave cannot be told from a
    /// command of the script that a person answered in: <c>Unsafe</c> then says why the replay must stop for a person instead of typing a script command twice.</summary>
    public static (List<int> Skip, string? Unsafe) LaterScriptCommands(IReadOnlyList<Step> steps, int dialogAt)
    {
        var skip = new List<int>();
        var k = dialogAt;
        while (k < steps.Count && steps[k].Kind == "dialog") k++;
        long prevEnd = dialogAt > 0 ? steps[dialogAt - 1].EndT : -1;
        for (; k < steps.Count; k++)
        {
            var s = steps[k];
            if (prevEnd < 0 || s.Kind is not ("command" or "lisp") || !Plan.ContinuesChain(s.Origin, s.Script, s.T, prevEnd)) break;
            if (s.Answers.Any(a => a.Input != false))
                return (skip, $"step {k + 1} ({s.Command}) looks like a command of the click's script, and holds answers a person gave: Retrace cannot tell whether to type it again. Do the rest of the test by hand from there, or record it again");
            skip.Add(k);
            prevEnd = s.EndT;
        }
        return (skip, null);
    }

    /// <summary>A window class without the parts that change from run to run: a WPF window's `HwndWrapper[acad.exe;;guid]` is `HwndWrapper`, a WinForms `WindowsForms10.Window.8.app.0.141b42a_r6_ad1` is
    /// `WindowsForms10.Window`, an MFC `Afx:00400000:b:...` (it carries addresses) is `Afx` (`AfxFrameOrView140s` and the like stay as they are); a Win32 class (`#32770`) is kept.</summary>
    public static string NormalizeClass(string? windowClass)
    {
        var c = (windowClass ?? "").Trim();
        if (c.StartsWith("HwndWrapper[", StringComparison.Ordinal)) return "HwndWrapper";
        if (c.StartsWith("WindowsForms10.", StringComparison.Ordinal))
        {
            var parts = c.Split('.');
            return parts.Length >= 2 ? parts[0] + "." + parts[1] : c;
        }
        if (c.StartsWith("Afx:", StringComparison.Ordinal)) return "Afx";
        return c;
    }

    /// <summary>The answers of a chain click (<see cref="Step.ChainClick"/>: a person's, given after its first dialog opened) that are due after dialog step <paramref name="dialogIndex"/> closed: those recorded
    /// from that dialog's opening to the next dialog step's opening (the last dialog of the chain: all that follow it). An answer the command needed between dialog A and dialog B is typed after A.</summary>
    public static List<Answer> ChainAnswersAfter(IReadOnlyList<Answer> answers, IReadOnlyList<Step> steps, int dialogIndex)
    {
        if (dialogIndex < 0 || dialogIndex >= steps.Count || steps[dialogIndex].Kind != "dialog") return new();
        // input a person gave while the dialog was open belongs to its own fill, not to the pause after it: count from the close
        long from = steps[dialogIndex].ClosedT >= 0 ? steps[dialogIndex].ClosedT : steps[dialogIndex].T;
        long to = NextDialogT(steps, dialogIndex);
        return answers.Where(a => a.T >= from && a.T < to).ToList();
    }

    /// <summary>The answers of a chain click that the click's own delivery types, as its script asks for them: with no dialog step after the click (its only modal state had no dialog window) all of them; with a dialog step
    /// after it, those a person gave before that dialog opened (a windowless modal state, then the jig, then the dialog). The ones after the dialog are <see cref="ChainAnswersAfter"/>'s. Empty when the step is no chain click.</summary>
    public static List<Answer> ChainAnswersAfterClick(IReadOnlyList<Step> steps, int clickIndex)
    {
        if (clickIndex < 0 || clickIndex >= steps.Count) return new();
        var s = steps[clickIndex];
        if (!s.ChainClick || s.Kind != "ribbon") return new();
        long to = NextDialogT(steps, clickIndex);
        return s.ChainAnswers.Where(a => a.Input != false && a.T < to).ToList();
    }

    /// <summary>When the dialog step after step <paramref name="i"/> opened, <c>long.MaxValue</c> when step i + 1 is no dialog step.</summary>
    public static long NextDialogT(IReadOnlyList<Step> steps, int i) => i + 1 < steps.Count && steps[i + 1].Kind == "dialog" ? steps[i + 1].T : long.MaxValue;

    /// <summary>What the replay says when it reaches the point of a chain where a person gave input while recording and Retrace cannot type it (<see cref="ChainDelivery.Unsafe"/>: an object pick, Esc, text with a space)
    /// or the prompt for it did not come in time (<see cref="ChainDelivery.TimedOutMessage"/>): the script the click started asks for it. The person gives it, then continues.</summary>
    public static string ChainPauseMessage(Step s, IReadOnlyList<Answer> due) =>
        $"the script \"{ButtonName(s)}\" started asks for input a person gave while recording ({AnswerText(due[0])}" + (due.Count > 1 ? $", and {due.Count - 1} more" : "") + "); give it, then press Next step";

    /// <summary>The button of a "Click ..." step, as the person sees it (<c>Retrace Samples &gt; Dialogs &gt; Chain...</c>).</summary>
    public static string ButtonName(Step s) => s.Title.StartsWith("Click ", StringComparison.Ordinal) ? s.Title[6..] : s.Title;

    /// <summary>What a person's answer was, by its kind: <c>point at "Specify insertion point:" [1,2,0]</c>, <c>keyword at "..." Yes</c>, <c>text at ...</c>, <c>distance</c>, <c>angle</c>, <c>number</c>, <c>selection</c>.</summary>
    public static string AnswerText(Answer a)
    {
        var noun = a.Kind switch
        {
            "point" or "corner" => "point", "distance" => "distance", "angle" => "angle", "double" or "integer" => "number",
            "keyword" => "keyword", "string" => "text", "entity" or "nestedEntity" or "selection" => "selection", _ => a.Kind,
        };
        var prompt = string.IsNullOrWhiteSpace(a.Message) ? "" : $" at \"{a.Message.Trim()}\"";
        var value = a.Value is { } v ? " " + v : a.Keyword != null ? " " + a.Keyword : "";
        return noun + prompt + value;
    }

    /// <summary>The bits of CMDACTIVE that say a script (4) or AutoLISP (32) is active.</summary>
    public const int ScriptOrLispBits = 4 | 32;

    /// <summary>The script / LISP bits that came on after <paramref name="baseline"/> (the bits set when the replay started or the step was sent): a replay that runs under a script or LISP wrapper has them set the whole run
    /// and must not wait on them; only what a step started counts. 0 when nothing new is active or the variable could not be read.</summary>
    public static int NewScriptBits(int? bitsNow, int baseline) => (bitsNow ?? 0) & ScriptOrLispBits & ~baseline;

    /// <summary>What a non-interactive replay (RETRACEREPLAY Step / Auto) says about a <see cref="Step.ChainClick"/> step with answers: it cannot type them after the dialogs (a dialog there is a person's),
    /// so it stops before the click. With no dialog step after the click (<paramref name="dialogFollows"/> false: a chain whose modal state had no dialog window) the replay types the answers itself
    /// (<see cref="ChainAnswersAfterClick"/>) and stops only for answers it cannot type (<see cref="ChainDelivery.Unsafe"/>). Null when the step has no answers to lose.</summary>
    public static string? ChainAnswersNeedPerson(Step s, bool dialogFollows = true)
    {
        var answers = s.ChainAnswers.Where(a => a.Input != false).ToList();
        if (!s.ChainClick || answers.Count == 0) return null;
        var list = string.Join("; ", answers.Select(AnswerText));
        if (!dialogFollows)
            return ChainDelivery.Unsafe(answers) is { } why ? $"\"{s.Title}\" runs a script that asks for input a person gave while recording ({list}), and RETRACEREPLAY cannot type it: {why}. Give it by hand, or record it again" : null;
        return $"\"{s.Title}\" runs a script that opens dialogs, and the person gave {answers.Count} answer(s) after the first one ({list}); RETRACEREPLAY cannot type them after a dialog. Replay this test with RETRACERUN, or give the answers by hand";
    }

}
