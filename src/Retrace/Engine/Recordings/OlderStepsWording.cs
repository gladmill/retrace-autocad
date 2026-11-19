// The sentences an older version wrote into steps.md that said "recorded", read as today's wording. NO AutoCAD references in this file (linked into the engine test project).
using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Recordings;

/// <summary>A steps.md written by an older version holds a few program-written sentences with the word "recorded" in them. The file on disk keeps them until the steps are written again; <see cref="StepsDoc"/> reads
/// them as today's wording, so every document built from an old steps.md (Word, Excel, the evidence workbook) is clean.
/// Only these known sentences are converted, each anchored to a whole line or to the fixed end of a line (a number pattern for the picks), so text a person wrote (a check note, a typed value, a prompt name inside a Pick bullet) is left as it is. One exception that cannot be told apart: a Pick or Select bullet whose own text ends in exactly the old suffix ("(recorded at 1, 2)") is converted too:
/// <list type="number">
/// <item>the line "Do the steps in order. A picture under a step shows what the screen looked like when the test was recorded." -> "... looked like at that step."</item>
/// <item>the first line before "## Steps" that starts "Recorded " and a date (yyyy-mm-dd): "Recorded 2026-10-10, with ..." -> "Created 2026-10-10, with ..."</item>
/// <item>"**Start with:** a drawing like the one recorded at the start (" -> "... like the one at the start of the test ("</item>
/// <item>the line "_No steps were recorded._" -> "_This test has no steps._"</item>
/// <item>a list item "Pick ..." ending in " (recorded at X, Y)" or "(recorded at X, Y, Z)" (numbers only) -> " (picked at X, Y)"; the same suffix inside the alt text of a picture "![Pick ... (recorded at X, Y)](...)"</item>
/// <item>a list item "Select the items in ... yourself" ending in " (the selection could not be recorded)" -> " (the selection was not stored in the test)"</item>
/// </list>
/// <see cref="ToOlder"/> is the other way round, only for the fingerprint of the steps (<c>EvidenceData</c>): pictures stamped against the older wording stay valid when the file is written again.</summary>
public static class OlderStepsWording
{
    const string Num = @"[-+]?\d+(?:\.\d+)?";
    const string Nums = Num + ", " + Num + "(?:, " + Num + ")?";
    const RegexOptions O = RegexOptions.Multiline | RegexOptions.CultureInvariant;

    const string IntroOld = "Do the steps in order. A picture under a step shows what the screen looked like when the test was recorded.";
    const string IntroNew = "Do the steps in order. A picture under a step shows what the screen looked like at that step.";

    static readonly Regex Intro = Rx.Create("^" + Regex.Escape(IntroOld) + @"(?=[ \t]*\r?$)", O);
    static readonly Regex IntroBack = Rx.Create("^" + Regex.Escape(IntroNew) + @"(?=[ \t]*\r?$)", O);
    static readonly Regex DateOld = Rx.Create(@"^Recorded (?=\d{4}-\d{2}-\d{2}\b)", O);
    static readonly Regex DateNew = Rx.Create(@"^Created (?=\d{4}-\d{2}-\d{2}\b)", O);
    static readonly Regex StartOld = Rx.Create(@"^(\*\*Start with:\*\* a drawing like the one )recorded at the start( \()", O);
    static readonly Regex StartNew = Rx.Create(@"^(\*\*Start with:\*\* a drawing like the one )at the start of the test( \()", O);
    static readonly Regex NoStepsOld = Rx.Create(@"^_No steps were recorded\._(?=[ \t]*\r?$)", O);
    static readonly Regex NoStepsNew = Rx.Create(@"^_This test has no steps\._(?=[ \t]*\r?$)", O);
    static readonly Regex PickOld = Rx.Create(@"^([ \t]*(?:[-*]|\d+\.)[ \t]+Pick .*) \(recorded at (" + Nums + @")\)(?=[ \t]*\r?$)", O);
    static readonly Regex PickNew = Rx.Create(@"^([ \t]*(?:[-*]|\d+\.)[ \t]+Pick .*) \(picked at (" + Nums + @")\)(?=[ \t]*\r?$)", O);
    static readonly Regex AltOld = Rx.Create(@"^([ \t]*!\[Pick [^\]\r\n]*?) \(recorded at (" + Nums + @")\)(\]\()", O);
    static readonly Regex AltNew = Rx.Create(@"^([ \t]*!\[Pick [^\]\r\n]*?) \(picked at (" + Nums + @")\)(\]\()", O);
    static readonly Regex SelOld = Rx.Create(@"^([ \t]*(?:[-*]|\d+\.)[ \t]+Select the items in .+ yourself) \(the selection could not be recorded\)(?=[ \t]*\r?$)", O);
    static readonly Regex SelNew = Rx.Create(@"^([ \t]*(?:[-*]|\d+\.)[ \t]+Select the items in .+ yourself) \(the selection was not stored in the test\)(?=[ \t]*\r?$)", O);

    /// <summary>The text of a steps.md with the older wording read as today's (see the class summary). Text without those sentences comes back as it is.</summary>
    public static string ToCurrent(string md)
    {
        if (string.IsNullOrEmpty(md) || !md.Contains("ecorded", StringComparison.Ordinal)) return md ?? "";
        int steps = md.IndexOf("## Steps", StringComparison.Ordinal);
        var head = steps >= 0 ? md[..steps] : md;
        var rest = steps >= 0 ? md[steps..] : "";
        head = Intro.Replace(head, IntroNew);
        head = DateOld.Replace(head, "Created ", 1);
        head = StartOld.Replace(head, "$1at the start of the test$2");
        rest = NoStepsOld.Replace(rest, "_This test has no steps._");
        rest = PickOld.Replace(rest, "$1 (picked at $2)");
        rest = AltOld.Replace(rest, "$1 (picked at $2)$3");
        rest = SelOld.Replace(rest, "$1 (the selection was not stored in the test)");
        return head + rest;
    }

    /// <summary>The way round: today's wording put back as the older versions wrote it, so the fingerprint of an older text can be recognised in a steps.md written again.</summary>
    public static string ToOlder(string md)
    {
        if (string.IsNullOrEmpty(md)) return md ?? "";
        int steps = md.IndexOf("## Steps", StringComparison.Ordinal);
        var head = steps >= 0 ? md[..steps] : md;
        var rest = steps >= 0 ? md[steps..] : "";
        head = IntroBack.Replace(head, IntroOld);
        head = DateNew.Replace(head, "Recorded ", 1);
        head = StartNew.Replace(head, "$1recorded at the start$2");
        rest = NoStepsNew.Replace(rest, "_No steps were recorded._");
        rest = PickNew.Replace(rest, "$1 (recorded at $2)");
        rest = AltNew.Replace(rest, "$1 (recorded at $2)$3");
        rest = SelNew.Replace(rest, "$1 (the selection could not be recorded)");
        return head + rest;
    }
}
