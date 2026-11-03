using System.Text.Json;
using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

/// <summary>A system variable typed at the command line runs as SETVAR (the recording holds only the value prompt), and a string answer with a space
/// (a layout name) must reach the prompt with its space. Synthetic copies of recordings made in AutoCAD 2026.</summary>
public class SetvarAndSpacesTests
{
    static List<Step> Build(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    static List<Step> Setvar(string name, string current, string valueJson) => Build(
        """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"SETVAR","origin":"typed/other"}""",
        "{\"t\":20,\"n\":2,\"type\":\"prompt\",\"kind\":\"string\",\"seq\":1,\"input\":true,\"by\":\"keys\",\"message\":\"Enter new value for " + name + " <\\\"" + current + "\\\">:\",\"status\":\"Keyword\",\"keyword\":" + valueJson + "}",
        """{"t":30,"n":3,"type":"cmd_end","doc":"a.dwg","command":"SETVAR"}""");

    [Fact]
    public void Typed_CTAB_replays_with_the_variable_name_then_the_value_and_the_space_kept()
    {
        var steps = Setvar("CTAB", "Model", "\"A-101 Plan\"");
        Assert.Equal("(command \"_SETVAR\" \"CTAB\" \"A-101 Plan\")\n", Plan.InputText(steps[0]));
        Assert.Null(Plan.Problem(steps[0]));
    }

    [Fact]
    public void Typed_CLAYER_without_a_space_replays_as_plain_lines_with_the_name()
    {
        var steps = Setvar("CLAYER", "0", "\"DIM\"");
        Assert.Equal("_SETVAR\nCLAYER\nDIM\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void Typed_LTSCALE_is_its_own_command_and_needs_no_name()
    {
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"LTSCALE","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"double","seq":1,"input":true,"by":"keys","message":"Enter new linetype scale factor <1.0000>:","status":"OK","value":2.5}""",
            """{"t":30,"n":3,"type":"cmd_end","doc":"a.dwg","command":"LTSCALE"}""");
        Assert.Equal("_LTSCALE\n2.5\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void A_name_prompt_that_was_recorded_is_not_typed_twice()
    {
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"SETVAR","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"string","seq":1,"input":true,"message":"Enter variable name or [?]:","status":"Keyword","keyword":"CTAB"}""",
            """{"t":21,"n":3,"type":"prompt","kind":"string","seq":2,"input":true,"message":"Enter new value for CTAB <\"Model\">:","status":"Keyword","keyword":"Model"}""",
            """{"t":30,"n":4,"type":"cmd_end","doc":"a.dwg","command":"SETVAR"}""");
        Assert.Equal("_SETVAR\nCTAB\nModel\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void A_setvar_whose_recording_names_no_variable_is_reported_and_types_nothing()
    {
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"SETVAR","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"string","seq":1,"input":true,"message":"Enter value:","status":"Keyword","keyword":"A-101 Plan"}""",
            """{"t":30,"n":3,"type":"cmd_end","doc":"a.dwg","command":"SETVAR"}""");
        Assert.Contains("which system variable", Plan.Problem(steps[0]));
        Assert.Null(Plan.InputText(steps[0]));
    }

    [Fact]
    public void Layout_name_with_a_space_goes_in_a_command_call_with_the_other_answers_as_they_were()
    {
        var steps = Build(
            """{"t":10,"n":1,"type":"cmd_start","doc":"a.dwg","command":"LAYOUT","origin":"typed/other"}""",
            """{"t":20,"n":2,"type":"prompt","kind":"keyword","seq":1,"input":true,"message":"Enter layout option [Copy/Delete/New/Template/Rename/SAveas/Set/?] <set>:","status":"Keyword","keyword":"Set"}""",
            """{"t":30,"n":3,"type":"prompt","kind":"string","seq":2,"input":true,"message":"Enter layout to make current <A-101 Plan>:","status":"Keyword","keyword":"A-103 Details"}""",
            """{"t":40,"n":4,"type":"cmd_end","doc":"a.dwg","command":"LAYOUT"}""");
        Assert.Equal("(command \"_LAYOUT\" \"Set\" \"A-103 Details\")\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void Quotes_and_backslashes_in_a_spaced_string_are_escaped()
    {
        var steps = Setvar("CTAB", "Model", "\"A \\\"B\\\\ C\"");   // JSON for the text: A "B\ C
        Assert.Equal("(command \"_SETVAR\" \"CTAB\" \"A \\\"B\\\\ C\")\n", Plan.InputText(steps[0]));
    }

    [Fact]
    public void A_step_that_opened_a_dialog_with_a_spaced_answer_is_not_typed_but_reported()
    {
        var steps = Setvar("CTAB", "Model", "\"A-101 Plan\"");
        steps[0].HadDialog = true;
        Assert.Contains("space", Plan.Problem(steps[0]));
        Assert.Null(Plan.InputText(steps[0]));
        Assert.Null(Plan.InputText(steps[0], includeStart: false));
        Assert.Null(Plan.InputAroundDialog(steps[0], 25).First);
    }

    [Fact]
    public void The_script_export_has_the_name_and_a_command_line_for_the_spaced_value()
    {
        var scr = ScriptExport.Build(Setvar("CTAB", "Model", "\"A-101 Plan\""), "x.jsonl");
        Assert.Contains("(command \"_SETVAR\" \"CTAB\" \"A-101 Plan\")", scr);
    }

    [Fact]
    public void The_instructions_name_the_variable_first()
    {
        var a = Setvar("CTAB", "Model", "\"A-101 Plan\"")[0].Answers;
        var lines = TdocPlan.AnswerLines(a, Setvar("CTAB", "Model", "\"A-101 Plan\"")[0]).ToList();
        Assert.Equal("At the prompt 'Enter variable name', type CTAB.", lines[0]);
        Assert.Contains("A-101 Plan", lines[1]);
    }

    static Answer Str(string message, string text) => new("string", "Keyword", null, text, message, true);

    static Step Ribbon(string macro, string command, params Answer[] answers) => new() { Kind = "ribbon", Macro = macro, Command = command, Title = "t", Answers = answers.ToList() };

    [Fact]
    public void A_string_that_looks_like_lisp_or_has_parentheses_is_quoted_not_evaluated()
    {
        var step = new Step { Kind = "command", Command = "LAYOUT", Answers = { Str("Enter layout option:", "Set"), Str("Enter layout to make current:", "(Old) Ground Floor") } };
        Assert.Equal("(command \"_LAYOUT\" \"Set\" \"(Old) Ground Floor\")\n", Plan.InputText(step));
    }

    [Fact]
    public void An_entity_answer_stays_a_lisp_expression_inside_the_command_call()
    {
        var ent = new Answer("entity", "OK", JsonDocument.Parse("""{"handle":"2F","dxf":"LINE"}""").RootElement.Clone(), null, "Select object:", true);
        var step = new Step { Kind = "command", Command = "RENAME", Answers = { ent, Str("Enter new name:", "A B") } };
        Assert.Equal("(command \"_RENAME\" (handent \"2F\") \"A B\")\n", Plan.InputText(step));
    }

    [Fact]
    public void A_lisp_step_with_a_spaced_answer_is_reported_not_typed()
    {
        var step = new Step { Kind = "lisp", Lisp = "(C:ZZ)", Answers = { Str("Name:", "A B") } };
        Assert.Contains("space", Plan.Problem(step));
        Assert.Null(Plan.InputText(step));
    }

    [Fact]
    public void A_macro_that_types_the_name_does_not_get_it_twice()
    {
        var value = Str("Enter new value for CTAB <\"Model\">:", "Model");
        Assert.Equal("\u0003\u0003ctab\nModel\n", Plan.InputText(Ribbon("^C^Cctab;", "SETVAR", value)));
        Assert.Equal("\u0003\u0003_setvar ctab\nModel\n", Plan.InputText(Ribbon("^C^C_setvar ctab;", "SETVAR", value)));
        Assert.Null(Plan.Problem(Ribbon("^C^C_SETVAR;CTAB;", "SETVAR", value)));
    }

    [Fact]
    public void A_macro_without_the_name_gets_it_from_the_value_prompt()
    {
        var value = Str("Enter new value for CTAB <\"Model\">:", "Model");
        Assert.Equal("\u0003\u0003_setvar\nCTAB\nModel\n", Plan.InputText(Ribbon("^C^C_setvar;", "SETVAR", value)));
    }

    [Fact]
    public void A_ribbon_macro_with_a_spaced_answer_sends_the_escapes_first_then_the_command_call()
    {
        var step = Ribbon("^C^C_layout;s;", "LAYOUT", Str("Enter layout to make current <A-101 Plan>:", "A-103 Details"));
        Assert.Equal("\u0003\u0003(command \"_layout\" \"s\" \"A-103 Details\")\n", Plan.InputText(step));
    }

    [Fact]
    public void A_macro_with_lisp_or_quotes_and_a_spaced_answer_is_reported()
    {
        Assert.Contains("space", Plan.Problem(Ribbon("^C^C(c:foo \"x\");", "FOO", Str("Name:", "A B"))));
    }

    [Fact]
    public void A_recorded_name_prompt_in_another_language_replays_as_before()
    {
        var step = new Step { Kind = "command", Command = "SETVAR", Answers = { Str("Variablenname eingeben oder [?]:", "CTAB"), Str("Neuen Wert fuer CTAB eingeben <\"Model\">:", "Layout1") } };
        Assert.Null(Plan.Problem(step));
        Assert.Equal("_SETVAR\nCTAB\nLayout1\n", Plan.InputText(step));
    }

    [Fact]
    public void A_single_unrecognised_prompt_is_reported_and_the_script_says_manual()
    {
        var step = new Step { Kind = "command", Command = "SETVAR", Title = "Type command SETVAR", Answers = { Str("Neuen Wert eingeben:", "Layout1") } };
        Assert.Contains("which system variable", Plan.Problem(step));
        Assert.Contains("; MANUAL: SETVAR:", ScriptExport.Build(new List<Step> { step }, "x"));
    }

    [Fact]
    public void The_guide_line_for_the_name_is_left_out_when_the_macro_types_it()
    {
        var value = Str("Enter new value for CTAB <\"Model\">:", "Model");
        var step = Ribbon("^C^Cctab;", "SETVAR", value);
        Assert.DoesNotContain(TdocPlan.AnswerLines(step.Answers, step), l => l.Contains("variable name"));
    }
}
