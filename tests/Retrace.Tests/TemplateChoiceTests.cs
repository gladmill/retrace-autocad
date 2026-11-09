using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Which template a RETRACERUN drawing starts from. Synthetic paths.</summary>
public class TemplateChoiceTests
{
    static Func<string, bool> Has(params string[] files) => p => files.Contains(p);

    static readonly TemplateChoice.Candidate Rec = new(@"C:\t\Rec.dwt", TemplateChoice.Recorded), Qn = new(@"C:\t\Qnew.dwt", TemplateChoice.Qnew), Acad = new(@"C:\t\acad.dwt", TemplateChoice.Default);
    static readonly string[] Start = { "0", "Defpoints", "L_A", "L_B" };

    static Func<string, IReadOnlyCollection<string>?> Layers(Dictionary<string, string[]> map) => p => map.TryGetValue(p, out var l) ? l : null;

    [Fact]
    public void Candidates_are_the_existing_ones_in_priority_order_without_repeats()
    {
        var all = TemplateChoice.Candidates(@"C:\t\Rec.dwt", @"C:\t\Qnew.dwt", @"C:\t\acad.dwt", Has(@"C:\t\Rec.dwt", @"C:\t\Qnew.dwt", @"C:\t\acad.dwt"));
        Assert.Equal(new[] { Rec, Qn, Acad }, all);
        Assert.Equal(new[] { Qn, Acad }, TemplateChoice.Candidates(@"C:\t\Rec.dwt", @"C:\t\Qnew.dwt", @"C:\t\acad.dwt", Has(@"C:\t\Qnew.dwt", @"C:\t\acad.dwt")));
        Assert.Equal(new[] { Rec }, TemplateChoice.Candidates(@"C:\t\Rec.dwt", @"c:\T\REC.dwt", "", Has(@"C:\t\Rec.dwt", @"c:\T\REC.dwt")));   // QNEW = the header's template
        Assert.Empty(TemplateChoice.Candidates(null, "", null, Has()));
    }

    [Fact]
    public void The_candidate_whose_layers_match_the_start_state_wins_even_when_it_is_not_the_first()
    {
        // a recording from acad.dwt, replayed on a machine whose QNEW template holds company layers (finding: no extra layers)
        var layers = Layers(new() { [Qn.Path] = new[] { "0", "Defpoints", "L_A", "L_B", "C_1", "C_2" }, [Acad.Path] = new[] { "0", "Defpoints", "L_A", "L_B" } });
        var p = TemplateChoice.Pick(new[] { Qn, Acad }, Start, layers)!;
        Assert.Equal(Acad, p.Template); Assert.True(p.Exact);
        // and the other way: the start state is the company's
        var company = Layers(new() { [Qn.Path] = new[] { "0", "L_A", "L_B" }, [Acad.Path] = new[] { "0" } });
        Assert.Equal(Qn, TemplateChoice.Pick(new[] { Qn, Acad }, Start, company)!.Template);
    }

    [Fact]
    public void Without_an_exact_match_the_fewest_missing_plus_extra_wins_and_ties_go_to_the_earlier()
    {
        var layers = Layers(new() { [Rec.Path] = new[] { "0", "L_A", "X_1", "X_2" }, [Qn.Path] = new[] { "0", "L_A", "X_1" }, [Acad.Path] = new[] { "0" } });
        var p = TemplateChoice.Pick(new[] { Rec, Qn, Acad }, Start, layers)!;
        Assert.Equal(Qn, p.Template); Assert.Equal(1, p.Missing); Assert.Equal(1, p.Extra);   // Rec: 1 missing + 2 extra, Qn 1 + 1, Acad 2 + 0
        var tie = Layers(new() { [Qn.Path] = new[] { "0", "L_A", "L_B", "X" }, [Acad.Path] = new[] { "0", "L_A", "L_B", "Y" } });
        Assert.Equal(Qn, TemplateChoice.Pick(new[] { Qn, Acad }, Start, tie)!.Template);
    }

    [Fact]
    public void Layer_names_compare_case_insensitively_and_ignore_0_defpoints_and_xref_layers()
    {
        var layers = Layers(new() { [Qn.Path] = new[] { "0", "l_a", "L_B", "X|Y" } });
        Assert.True(TemplateChoice.Pick(new[] { Qn }, new[] { "0", "Defpoints", "L_A", "L_B" }, layers)!.Exact);
    }

    [Fact]
    public void An_unreadable_candidate_only_counts_when_none_can_be_read()
    {
        var layers = Layers(new() { [Acad.Path] = new[] { "0", "Z" } });
        Assert.Equal(Acad, TemplateChoice.Pick(new[] { Qn, Acad }, Start, layers)!.Template);
        var p = TemplateChoice.Pick(new[] { Qn, Acad }, Start, Layers(new()))!;
        Assert.Equal(Qn, p.Template); Assert.Null(p.Missing);
        Assert.Null(TemplateChoice.Pick(Array.Empty<TemplateChoice.Candidate>(), Start, layers));
    }

    [Fact]
    public void A_recording_without_a_start_state_gets_the_recorded_template_else_acad_dwt_never_qnew_alone()
    {
        var layers = Layers(new());
        Assert.Equal(Acad, TemplateChoice.Pick(new[] { Qn, Acad }, null, layers)!.Template);
        Assert.Equal(Rec, TemplateChoice.Pick(new[] { Rec, Qn, Acad }, null, layers)!.Template);
        Assert.Equal(Qn, TemplateChoice.Pick(new[] { Qn }, null, layers)!.Template);   // nothing else exists
    }

    [Fact]
    public void Unc_paths_are_recognised()
    {
        Assert.True(TemplateChoice.IsUnc(@"\\server\share\a.dwt")); Assert.False(TemplateChoice.IsUnc(@"C:\t\a.dwt")); Assert.False(TemplateChoice.IsUnc(null));
    }

    [Fact]
    public void Header_template_is_read_from_the_header_event_only()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
        Assert.Equal(@"C:\t\Rec.dwt", TemplateChoice.FromHeader(new[] { J("""{"type":"header","template":"C:\\t\\Rec.dwt"}"""), J("""{"type":"state"}""") }));
        Assert.Null(TemplateChoice.FromHeader(new[] { J("""{"type":"header","autocad":"25.1"}""") }));
        Assert.Null(TemplateChoice.FromHeader(new[] { J("""{"type":"header","template":""}""") }));
        Assert.Null(TemplateChoice.FromHeader(new[] { J("""{"type":"mark","template":"x"}""") }));
        Assert.Null(TemplateChoice.FromHeader(Array.Empty<JsonElement>()));
    }

    [Fact]
    public void Describe_says_how_well_the_template_matches_and_when_the_recorded_one_was_not_used()
    {
        Assert.Equal(@"the QNEW template (C:\t\Qnew.dwt), its layers match the recording's start state; the recording's template C:\t\R.dwt was not used (missing here or a worse match)",
            TemplateChoice.Describe(new(Qn, 0, 0), @"C:\t\R.dwt"));
        Assert.Equal(@"the QNEW template (C:\t\Qnew.dwt), the closest to the recording's start state (1 layer missing, 2 extra)", TemplateChoice.Describe(new(Qn, 1, 2), null));
        Assert.Equal(@"acad.dwt (C:\t\acad.dwt)", TemplateChoice.Describe(new(Acad, null, null), null));
        Assert.Equal(@"the recording's template (C:\t\Rec.dwt)", TemplateChoice.Describe(new(Rec, null, null), @"C:\t\Rec.dwt"));
    }
}
