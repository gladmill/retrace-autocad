using Retrace.Engine.Map;

namespace Retrace.Tests;

/// <summary>What a CUI macro or a ribbon button's CommandParameter runs: its commands, the LISP functions it calls, the files it loads.</summary>
public class MacroTextTests
{
    private static void Check(string? macro, string[] commands, string[]? lisp = null, string[]? loads = null, bool diesel = false)
    {
        var t = MacroText.Parse(macro);
        Assert.Equal(commands, t.Commands);
        Assert.Equal(lisp ?? [], t.LispFunctions);
        Assert.Equal(loads ?? [], t.Loads);
        Assert.Equal(diesel, t.Diesel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Nothing_in_gives_nothing_out(string? macro)
    {
        Assert.Same(MacroTargets.None, MacroText.Parse(macro));
        Assert.True(MacroTargets.None.IsEmpty);
    }

    [Fact]
    public void A_plain_command_macro_gives_its_first_command() => Check("^C^C_line ", ["LINE"]);

    [Theory]
    [InlineData("^C^C._-insert ", "-INSERT")]
    [InlineData("'_zoom", "ZOOM")]
    [InlineData("^C^C_zoom;_extents;", "ZOOM")]
    [InlineData("*^C^C_line", "LINE")]
    [InlineData("^C^C_+purge 1", "PURGE")]
    [InlineData("^C^C_ACAD.line", "LINE")]
    [InlineData("^C^C_-insert;blk;\\", "-INSERT")]
    public void The_first_command_is_read_as_the_cuix_reader_reads_it(string macro, string command) => Check(macro, [command]);

    [Fact]
    public void A_c_colon_call_is_a_command() => Check("^C^C(c:mytool)", ["MYTOOL"]);

    [Fact]
    public void A_lisp_call_that_is_not_a_c_colon_function_is_a_lisp_function() => Check("^C^C(mytool 1 2)", [], ["mytool"]);

    [Fact]
    public void A_load_goes_to_loads_and_the_word_after_the_expression_is_a_command() =>
        Check("^C^C(load \"tools.lsp\") mytool", ["MYTOOL"], loads: ["tools.lsp"]);

    [Fact]
    public void A_command_call_names_a_command_without_its_prefixes() => Check("^C^C(command \"_.zoom\" \"e\")", ["ZOOM"]);

    [Theory]
    [InlineData("(command \"_.zoom\" \"e\")", "ZOOM")]
    [InlineData("(command-s \"-layer\" \"m\" \"x\" \"\")", "-LAYER")]
    [InlineData("(vl-cmdf \"ACAD.LINE\" pt1 pt2 \"\")", "LINE")]
    [InlineData("(command \"+regen\")", "REGEN")]
    [InlineData("(COMMAND \"_Circle\" pt 5)", "CIRCLE")]
    public void Command_command_s_and_vl_cmdf_take_the_first_string(string macro, string command) => Check("^C^C" + macro, [command]);

    [Theory]
    [InlineData("(command)")]
    [InlineData("(command \"\")")]
    [InlineData("(command cmd \"zoom\")")]        // the first argument is a variable: the "zoom" after it is an option, not the command
    [InlineData("(command \"^C^C\")")]
    [InlineData("(command \"two words\")")]
    [InlineData("(command \"1234\")")]            // no letter
    public void Command_calls_without_a_command_word_name_nothing(string macro) => Check("^C^C" + macro, []);

    [Fact]
    public void Diesel_is_flagged_and_not_followed()
    {
        Check("$M=$(if,$(getvar,tilemode),^C^C_mspace)", [], diesel: true);
        Check("   $M=$(getvar,clayer)", [], diesel: true);
        Check("$(c:foo)", [], diesel: true);
    }

    [Fact]
    public void An_ai_function_is_the_users_lisp_because_it_is_defined_in_acads_mnl() => Check("^C^C(ai_tiledvp 1 nil)", [], ["ai_tiledvp"]);

    [Fact]
    public void An_expression_of_builtins_only_names_nothing() => Check("(princ)", []);

    [Theory]
    [InlineData("^C^C(setq a (getpoint) b (strcat \"x\" \"y\"))")]
    [InlineData("(if (and (> a 1) (not b)) (progn (princ \"x\") (prompt \"y\")))")]
    [InlineData("(foreach x '(a b c) (print x))")]
    [InlineData("(mapcar '(lambda (x) (* x 2)) lst)")]
    [InlineData("(vl-load-com)(vla-get-name (vlax-ename->vla-object e))")]
    [InlineData("(1+ x)(1- x)(+ 1 2)(* 2 3)(/ 4 2)(= a b)(/= a b)(<= a b)")]
    [InlineData("(repeat 3 (princ))(while (< a 3) (setq a (1+ a)))")]
    [InlineData("(entget (car (entsel)))(ssget \"X\")(getvar \"clayer\")(setvar \"osmode\" 0)")]
    [InlineData("(SETQ A 1)(PROGN (PRINC))(Alert \"hi\")")]
    public void Autolisp_built_ins_are_not_reported(string macro) => Check("^C^C" + macro, []);

    [Theory]
    [InlineData("load")] [InlineData("command")] [InlineData("command-s")] [InlineData("vl-cmdf")] [InlineData("princ")] [InlineData("prin1")]
    [InlineData("print")] [InlineData("prompt")] [InlineData("setq")] [InlineData("set")] [InlineData("setvar")] [InlineData("getvar")]
    [InlineData("if")] [InlineData("progn")] [InlineData("and")] [InlineData("or")] [InlineData("not")] [InlineData("cond")]
    [InlineData("repeat")] [InlineData("while")] [InlineData("foreach")] [InlineData("defun")] [InlineData("defun-q")] [InlineData("lambda")]
    [InlineData("quote")] [InlineData("function")] [InlineData("alert")] [InlineData("strcat")] [InlineData("list")] [InlineData("car")]
    [InlineData("cdr")] [InlineData("cadr")] [InlineData("cons")] [InlineData("apply")] [InlineData("mapcar")] [InlineData("eval")]
    [InlineData("read")] [InlineData("getstring")] [InlineData("getpoint")] [InlineData("getreal")] [InlineData("getint")]
    [InlineData("getkword")] [InlineData("initget")] [InlineData("findfile")] [InlineData("startapp")] [InlineData("menucmd")]
    public void Every_listed_autolisp_function_is_left_out_whatever_its_case(string name)
    {
        Check("^C^C(" + name + " 1)", []);
        Check("^C^C(" + name.ToUpperInvariant() + " 1)", []);
    }

    [Fact]
    public void Visual_lisp_functions_are_left_out_by_prefix()
    {
        Check("(vl-load-com)(vlax-get-acad-object)(vla-get-activedocument x)(vlr-reactor-p y)(VL-CMDF \"_line\")", ["LINE"]);
    }

    [Fact]
    public void Parameter_lists_and_quoted_lists_are_not_calls()
    {
        Check("(defun helper (a b / c) (mytool a))", [], ["mytool"]);
        Check("(setq lst '(alpha beta))", []);
        Check("(setq lst (quote (alpha beta)))", []);
        Check("(lambda (x y) (inner x))", [], ["inner"]);
        Check("(defun c:inline () (foo))", [], ["foo"]);   // a definition's own name is not a call; the body is
    }

    [Fact]
    public void The_clauses_of_a_cond_are_not_calls_but_their_bodies_are()
    {
        Check("(cond ((= a 1) (first-fn)) (flag (second-fn)) (t (third-fn)))", [], ["first-fn", "second-fn", "third-fn"]);
    }

    [Fact]
    public void Nested_calls_are_found_in_order_and_each_function_once_ignoring_case()
    {
        Check("(outer (inner 1) (Inner 2) (other (c:deep)))", ["DEEP"], ["outer", "inner", "other"]);
    }

    [Fact]
    public void Lisp_functions_keep_the_case_they_were_written_in() => Check("(MyTool)", [], ["MyTool"]);

    [Fact]
    public void Commands_are_distinct_ignoring_case_and_in_order_of_appearance()
    {
        Check("^C^C_zoom (c:zoom) (command \"_ZOOM\") (c:pan) (c:ZOOM)", ["ZOOM", "PAN"]);
    }

    [Fact]
    public void Loads_are_distinct_and_the_first_string_of_each_load_counts()
    {
        Check("(load \"a.lsp\")(load \"a.lsp\")(load \"b\" \"failed\")(load \"A.LSP\")(load name)(load)", [], loads: ["a.lsp", "b"]);
    }

    [Fact]
    public void A_load_with_a_path_keeps_it_as_written()
    {
        Check("(load \"C:\\\\tools\\\\a.lsp\")(load \"c:/tools/b.lsp\")", [], loads: ["C:\\tools\\a.lsp", "c:/tools/b.lsp"]);
    }

    [Fact]
    public void With_a_command_of_its_own_the_word_after_an_expression_is_an_argument()
    {
        Check("^C^C_line (list 0 0) 5,5 ", ["LINE"]);
        Check("^C^C_insert (strcat \"a\" \"b\") 1,1", ["INSERT"]);
    }

    [Fact]
    public void Only_the_first_command_word_after_an_expression_counts()
    {
        Check("(load \"x\") first (load \"y\") second", ["FIRST"], loads: ["x", "y"]);
    }

    [Fact]
    public void The_word_after_an_expression_is_read_like_a_first_command()
    {
        Check("^C^C(load \"x\");_-layer", ["-LAYER"], loads: ["x"]);   // a ; (Enter) ends the LISP input: the next word is the command
        Check("^C^C(load \"x\") _.pan", ["PAN"], loads: ["x"]);
        Check("^C^C(load \"x\") (foo)", [], ["foo"], ["x"]);       // a second expression, not a word
        Check("^C^C(vl-load-com)(c:foo)", ["FOO"]);
        Check("^C^C(load \"x\") 12", [], loads: ["x"]);            // no letter: not a command
    }

    [Fact]
    public void A_quoted_text_outside_an_expression_is_not_searched_for_lisp()
    {
        Check("^C^C_text 0,0 2 0 \"a (b) c\" ", ["TEXT"]);
    }

    [Fact]
    public void Unbalanced_input_takes_what_is_there_and_never_throws()
    {
        Check("^C^C(foo \"a", [], ["foo"]);
        Check("^C^C(foo (bar 1", [], ["foo", "bar"]);
        Check("^C^C)))(foo", [], ["foo"]);
        Check("^C^C(c:ok", ["OK"]);
        Check("(", []);
        Check("\"", []);
        Check("^C^C(command \"_zoom", ["ZOOM"]);
        Check("'", []);
        Check("^C^C(foo ') (bar)", [], ["foo", "bar"]);
    }

    [Fact]
    public void A_very_deep_macro_does_not_overflow_the_stack()
    {
        var deep = "^C^C" + new string('(', 200_000) + "foo" + new string(')', 200_000);
        Assert.Equal(new[] { "foo" }, MacroText.Parse(deep).LispFunctions);
        Assert.Empty(MacroText.Parse("^C^C" + new string('(', 200_000)).Commands);
    }

    [Fact]
    public void Random_text_never_throws()
    {
        var rng = new Random(7);
        const string alphabet = "()\"'; \t\r\n^C_.-+*\\$abcLDEFUN:cmdlo1";
        for (int i = 0; i < 3000; i++)
        {
            var text = string.Create(rng.Next(0, 80), rng, (span, r) => { for (int k = 0; k < span.Length; k++) span[k] = alphabet[r.Next(alphabet.Length)]; });
            var t = MacroText.Parse(text);
            Assert.NotNull(t.Commands);
            Assert.Equal(t.Commands.Count, t.Commands.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void A_command_after_the_lisp_that_loads_it_is_found_after_a_semicolon_too()
    {
        // the classic menu pattern: load the command's LISP if it is not there yet, then run it
        Check("^C^C^P(if (null c:3darray) (load \"3darray\"));3darray", ["3DARRAY"], loads: ["3darray"]);
        Check("^C^C(load \"tools\");;mytool", ["MYTOOL"], loads: ["tools"]);
    }

    [Fact]
    public void The_word_after_a_lisp_command_answers_its_prompt_and_is_no_command()
    {
        Check("^C^C(c:mytool) _Yes", ["MYTOOL"]);
        Check("^C^C(command \"_.zoom\") _e", ["ZOOM"]);
        Check("^C^C(vl-load-com)(c:foo) bar", ["FOO"]);
    }

    [Theory]
    [InlineData("^C^C$M=$(if,$(getvar,tilemode),_mspace,_pspace)")]
    [InlineData("*^C^C$M=$(getvar,clayer)")]
    [InlineData("^C^C ^P$M=$(getvar,clayer)")]
    public void A_diesel_macro_after_control_codes_is_diesel(string macro) => Check(macro, [], diesel: true);
}
