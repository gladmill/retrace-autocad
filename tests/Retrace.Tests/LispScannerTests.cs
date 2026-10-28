using Retrace.Engine.Emit;
using Retrace.Engine.Scan.Commands;

namespace Retrace.Tests;

/// <summary>AutoLISP commands and functions: (defun c:NAME), (defun NAME) and (vlax-add-cmd "NAME" 'fn). Synthetic LISP only.</summary>
public class LispScannerTests
{
    private static SourceFile F(string text, string name = "lisp/tools.lsp", string project = "lisp") =>
        new() { AbsPath = "/" + name, RelPath = name, Lang = Lang.Lisp, Project = project, Text = text };

    private static List<CommandEntry> Scan(string text) => LispScanner.Scan(new[] { F(text) });

    [Fact]
    public void A_c_colon_defun_is_a_command_named_in_upper_case_with_its_symbol_as_written()
    {
        var e = Assert.Single(Scan("(defun c:DrawGrid (/ pt)\n  (princ))"));
        Assert.Equal(EntryKind.LispCommand, e.Kind);
        Assert.Equal("DRAWGRID", e.Name);
        Assert.Equal("c:DrawGrid", e.Method);
        Assert.Equal(Lang.Lisp, e.Lang);
        Assert.Equal("lisp", e.Project);
        Assert.Equal(new SourceRef("lisp/tools.lsp", 1), e.Source);
        Assert.Equal("defun c:", e.Extra["form"]);
    }

    [Fact]
    public void A_C_colon_prefix_in_any_case_is_a_command()
    {
        var r = Scan("(defun C:Upper () 1)\n(defun c:lower () 2)");
        Assert.Equal(new[] { "UPPER", "LOWER" }, r.Select(x => x.Name));
        Assert.All(r, x => Assert.Equal(EntryKind.LispCommand, x.Kind));
        Assert.Equal(new[] { "C:Upper", "c:lower" }, r.Select(x => x.Method));
    }

    [Fact]
    public void A_plain_defun_is_a_function_with_its_case_kept()
    {
        var r = Scan("(defun MyHelper (a b)\n  (+ a b))\n(defun my-other () nil)");
        Assert.Equal(new[] { "MyHelper", "my-other" }, r.Select(x => x.Name));
        Assert.All(r, x => Assert.Equal(EntryKind.LispDefun, x.Kind));
        Assert.Equal(new[] { "MyHelper", "my-other" }, r.Select(x => x.Method));
        Assert.Equal("defun", r[0].Extra["form"]);
        Assert.Equal(new[] { 1, 3 }, r.Select(x => x.Source.Line));
    }

    [Fact]
    public void Defun_q_is_read_like_defun()
    {
        var r = Scan("(defun-q c:old-style () 1)\n(defun-q helper () 2)");
        Assert.Equal((EntryKind.LispCommand, "OLD-STYLE"), (r[0].Kind, r[0].Name));
        Assert.Equal((EntryKind.LispDefun, "helper"), (r[1].Kind, r[1].Name));
    }

    [Fact]
    public void The_keyword_may_be_in_any_case_and_the_name_on_the_next_line()
    {
        var r = Scan("(DEFUN\n   c:Shouted\n  ()\n  1)\n(Defun-Q Mixed () 1)");
        Assert.Equal(new[] { "SHOUTED", "Mixed" }, r.Select(x => x.Name));
        Assert.Equal(new[] { 1, 5 }, r.Select(x => x.Source.Line));   // the line of the "(", not of the name
    }

    [Fact]
    public void The_name_ends_at_the_parameter_list_with_no_space()
    {
        var r = Scan("(defun c:tight(/ a) (princ))\n(defun helper() 1)");
        Assert.Equal(new[] { "TIGHT", "helper" }, r.Select(x => x.Name));
    }

    [Fact]
    public void Error_handlers_are_defuns_named_with_stars()
    {
        var e = Assert.Single(Scan("(defun *error* (msg)\n  (princ msg))"));
        Assert.Equal(EntryKind.LispDefun, e.Kind);
        Assert.Equal("*error*", e.Name);
    }

    [Theory]
    [InlineData("(vlax-add-cmd \"MYCMD\" 'my-fn)", "MYCMD", "my-fn")]
    [InlineData("(vlax-add-cmd \"mycmd\" (function My-Fn) \"LOCAL\" 0)", "MYCMD", "My-Fn")]
    [InlineData("(vlax-add-cmd \"mycmd\" my-fn)", "MYCMD", "my-fn")]
    [InlineData("(vlax-add-cmd \"mycmd\" (quote my-fn))", "MYCMD", "my-fn")]
    [InlineData("(VLAX-ADD-CMD \"MyCmd\" 'my-fn \"MyCmd\" 4)", "MYCMD", "my-fn")]
    public void Vlax_add_cmd_registers_a_command_in_each_of_its_forms(string code, string name, string method)
    {
        var e = Assert.Single(Scan(code));
        Assert.Equal(EntryKind.LispCommand, e.Kind);
        Assert.Equal(name, e.Name);
        Assert.Equal(method, e.Method);
        Assert.Equal("vlax-add-cmd", e.Extra["form"]);
        Assert.Equal(1, e.Source.Line);
    }

    [Theory]
    [InlineData("(vlax-add-cmd \"MYCMD\")")]
    [InlineData("(vlax-add-cmd \"MYCMD\" \"text\")")]
    [InlineData("(vlax-add-cmd \"MYCMD\" (lambda () 1))")]
    [InlineData("(vlax-add-cmd \"MYCMD\" nil)")]
    public void Vlax_add_cmd_without_a_function_symbol_uses_the_command_name_as_the_method(string code)
    {
        var e = Assert.Single(Scan(code));
        Assert.Equal("MYCMD", e.Name);
        Assert.Equal("MYCMD", e.Method);
    }

    [Fact]
    public void A_vlax_add_cmd_whose_name_is_not_a_string_is_not_a_command()
    {
        Assert.Empty(Scan("(vlax-add-cmd name 'fn)\n(vlax-add-cmd)"));
    }

    [Fact]
    public void Line_comments_hide_a_definition()
    {
        var r = Scan("; (defun c:gone () 1)\n(defun c:kept () 1) ; (defun c:alsogone () 2)\n;;; (vlax-add-cmd \"GONE\" 'f)");
        Assert.Equal("KEPT", Assert.Single(r).Name);
    }

    [Fact]
    public void Block_comments_hide_a_definition_and_may_span_lines()
    {
        var r = Scan("""
            ;| (defun c:hidden () 1)
               (defun c:hidden2 () 2)
            |;
            (defun c:shown () 3)
            (defun c:inline ;| (defun c:nope () 1) |; () 4)
            """);
        Assert.Equal(new[] { "SHOWN", "INLINE" }, r.Select(x => x.Name));
    }

    [Fact]
    public void Line_numbers_stay_right_after_a_block_comment()
    {
        var r = Scan("(princ)\n;|\nline 3\nline 4\n|;\n(defun c:after () 1)\n\n(defun helper () 2)");
        Assert.Equal(new[] { 6, 8 }, r.Select(x => x.Source.Line));
    }

    [Fact]
    public void Line_numbers_count_the_newlines_of_a_string_and_of_windows_files()
    {
        var r = Scan("(setq s \"a\nb\nc\")\r\n(defun c:x () 1)\r\n(defun c:y () 2)");
        Assert.Equal(new[] { 4, 5 }, r.Select(x => x.Source.Line));
    }

    [Fact]
    public void A_definition_inside_a_string_is_not_found()
    {
        var r = Scan("(setq code \"(defun c:fake () 1)\")\n(princ \"; not a comment (defun c:alsofake ()\")\n(defun c:real () 1)");
        Assert.Equal("REAL", Assert.Single(r).Name);
    }

    [Fact]
    public void A_semicolon_in_a_string_does_not_start_a_comment_and_an_escaped_quote_does_not_end_the_string()
    {
        var r = Scan("(princ \"a ; b \\\" ; c\") (defun c:after () 1)\n(princ \"back\\\\\") (defun helper () 2)");
        Assert.Equal(new[] { "AFTER", "helper" }, r.Select(x => x.Name));
    }

    [Fact]
    public void Every_definition_is_kept_so_a_name_defined_twice_is_two_entries()
    {
        var files = new[] { F("(defun c:dup () 1)\n(defun c:dup () 2)", "lisp/a.lsp"), F("(defun c:dup () 3)", "lisp/b.lsp") };
        var r = LispScanner.Scan(files);
        Assert.Equal(3, r.Count);
        Assert.Equal(new[] { ("lisp/a.lsp", 1), ("lisp/a.lsp", 2), ("lisp/b.lsp", 1) }, r.Select(x => (x.Source.File, x.Source.Line)));
    }

    [Fact]
    public void The_same_file_given_twice_gives_each_definition_once()
    {
        var f = F("(defun c:one () 1)\n(defun two () 2)");
        Assert.Equal(2, LispScanner.Scan(new[] { f, f }).Count);
    }

    [Fact]
    public void Only_lisp_files_are_read()
    {
        var cs = new SourceFile { AbsPath = "/x.cs", RelPath = "x.cs", Lang = Lang.CSharp, Project = "p", Text = "(defun c:notlisp () 1)" };
        var fas = new SourceFile { AbsPath = "/x.fas", RelPath = "x.fas", Lang = Lang.Other, Project = "p", Text = "" };
        Assert.Empty(LispScanner.Scan(new[] { cs, fas, F("") }));
    }

    [Fact]
    public void A_defun_with_no_name_or_an_empty_command_name_is_skipped_and_nothing_throws()
    {
        Assert.Empty(Scan("(defun)\n(defun (x) 1)\n(defun c: () 1)\n(defun"));
        Assert.Equal("c:foo", Assert.Single(Scan("(defun c:foo")).Method);   // an unclosed file still names the function
    }

    [Fact]
    public void A_nested_definition_is_found_and_so_is_one_after_unbalanced_parentheses()
    {
        var r = Scan("(if t (defun c:inner () 1))\n(setq x (list 1 2\n(defun c:late () 2)");
        Assert.Equal(new[] { "INNER", "LATE" }, r.Select(x => x.Name));
    }

    [Fact]
    public void A_keyword_that_only_starts_with_defun_is_not_a_definition()
    {
        Assert.Empty(Scan("(defunx foo () 1)\n(defun-q-list foo)\n(my-defun foo () 1)"));
    }

    // ---- the docs, were a model to hold LISP entries (Build docs does not scan LISP; the map does) ---------------------------------------

    [Fact]
    public void The_docs_say_how_to_start_a_lisp_command_and_a_lisp_function()
    {
        var m = new EngineModel { InputRoot = "/in" };
        m.Commands.AddRange(Scan("(defun c:mytool () 1)\n(defun helper () 2)"));
        var ix = new ModelIndex(m, new EngineOptions { Input = "/in", Output = "/out" });
        Assert.Equal("Type MYTOOL at the command line and press Enter", ix.ReachCommand(m.Commands[0])[0]);
        Assert.Equal("Type (helper) at the command line and press Enter", ix.ReachCommand(m.Commands[1])[0]);
    }

    [Fact]
    public void A_manual_test_for_a_lisp_command_asks_for_the_lisp_file_not_an_assembly()
    {
        var m = new EngineModel { InputRoot = "/in" };
        m.Commands.AddRange(Scan("(defun c:mytool () 1)"));
        var output = Directory.CreateTempSubdirectory("retrace-test-lisp-docs").FullName;
        try
        {
            Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = output });
            var plan = File.ReadAllText(Path.Combine(output, "test-plans", "commands", "lisp.md"));
            Assert.Contains("The lisp LISP file is loaded", plan);
            Assert.DoesNotContain("assembly is loaded", plan);
            Assert.Contains("Type MYTOOL at the command line", plan);
        }
        finally { Directory.Delete(output, true); }
    }
}
