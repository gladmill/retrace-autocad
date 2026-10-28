using System.IO.Compression;
using System.Security;
using Retrace.Engine.Map;

namespace Retrace.Tests;

/// <summary>How the ribbon map finds the code a name stands for: commands, LISP functions, methods, classes and LISP files, over several
/// folders. Each test builds a few invented files in a temp folder.</summary>
public class RibbonMapDefinitionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("retrace-mapdefs").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Dir(string name) => Path.Combine(_root, name);

    private void Put(string folder, string relPath, string text)
    {
        var path = Path.Combine(Dir(folder), relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A .cuix in folder "menu" with one tab "T", panel "P" and a button for each macro (labelled by its index).</summary>
    private void Menu(params string[] macros)
    {
        var path = Path.Combine(Dir("menu"), "t.cuix");
        Directory.CreateDirectory(Dir("menu"));
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string n, string c) { using var w = new StreamWriter(zip.CreateEntry(n).Open()); w.Write(c); }
        Add("MenuGroup.cui", "<MenuGroup Name=\"G\"><MacroGroup Name=\"M\">" +
            string.Concat(macros.Select((m, i) => $"<MenuMacro UID=\"M{i}\"><Macro type=\"Any\"><Name>B{i}</Name><Command>{SecurityElement.Escape(m)}</Command></Macro></MenuMacro>")) + "</MacroGroup></MenuGroup>");
        Add("RibbonRoot.cui", "<RibbonRoot><RibbonPanelSourceCollection><RibbonPanelSource UID=\"P\" Text=\"P\"><RibbonRow UID=\"R\">" +
            string.Concat(macros.Select((_, i) => $"\n<RibbonCommandButton UID=\"b{i}\" Text=\"B{i}\" MenuMacroID=\"M{i}\"/>")) +
            "</RibbonRow></RibbonPanelSource></RibbonPanelSourceCollection><RibbonTabSourceCollection><RibbonTabSource UID=\"T\" Text=\"T\"><RibbonPanelSourceReference PanelId=\"P\"/></RibbonTabSource></RibbonTabSourceCollection></RibbonRoot>");
    }

    private RibbonMapResult Map(params string[] folders) =>
        RibbonMapper.Build(new RibbonMapOptions { Folders = folders.Select(Dir).ToList() });

    private static LiveItem Item(string id, string? handler = null, string? param = null) => new(id, id, null, "RibbonButton", null, param, handler, []);

    /// <summary>A live-only tab with one item for each of <paramref name="items"/>, listed whatever they map to.</summary>
    private RibbonMapResult MapLive(string[] folders, params LiveItem[] items) => RibbonMapper.Build(new RibbonMapOptions
    {
        Folders = folders.Select(Dir).ToList(),
        Live = [new LiveTab("LIVE", "Live", true, false, [new LivePanel(null, "Panel", items)])],
        AllLiveTabs = true,
    });

    private static MapRow Row(RibbonMapResult r, string item) => Assert.Single(r.Rows, x => x.Item == item);

    // ---- commands and LISP -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_definitions_of_one_name_come_in_folder_order_then_file_path_then_line()
    {
        const string cmd = "namespace N { class C { [CommandMethod(\"SAME\")] public void A() { }\n [CommandMethod(\"SAME\")] public void B() { } } }";
        Put("one", "z/Late.cs", cmd);
        Put("one", "a/Early.cs", cmd);
        Put("two", "First.cs", cmd);
        Menu("^C^C_SAME ");
        var r = Map("two", "menu", "one");   // the user's order: two before one
        var where = Row(r, "B0").Sources.Select(s => (s.Source.Short, s.Source.Line)).ToList();
        Assert.Equal(new[] { ("First.cs", 1), ("First.cs", 2), ("a/Early.cs", 1), ("a/Early.cs", 2), ("z/Late.cs", 1), ("z/Late.cs", 2) }, where);
    }

    [Fact]
    public void Lisp_commands_functions_and_their_forms_are_told_apart()
    {
        Put("src", "a.lsp", "(defun c:First () 1)\n(defun helper (x) x)\n(vlax-add-cmd \"Second\" 'sec-fn)");
        Put("src", "Fn.cs", "class F { [LispFunction(\"NetFn\")] public static object Go(object rb) => null; }");
        Menu("^C^C_FIRST ", "^C^C_SECOND ", "^C^C(helper 1)", "^C^C(NETFN)", "^C^C(unknown-fn)");
        var r = Map("menu", "src");
        var first = Assert.Single(Row(r, "B0").Sources);
        Assert.Equal(("command", "FIRST", "defun c:", "c:First"), (first.What, first.Name, first.Entry, first.Symbol));
        var second = Assert.Single(Row(r, "B1").Sources);
        Assert.Equal(("command", "SECOND", "vlax-add-cmd", "sec-fn", 3), (second.What, second.Name, second.Entry, second.Symbol, second.Source.Line));
        var helper = Assert.Single(Row(r, "B2").Sources);
        Assert.Equal(("lisp", "helper", "defun", "helper", Lang.Lisp), (helper.What, helper.Name, helper.Entry, helper.Symbol, helper.Lang));
        Assert.Equal("LISP (helper)", Row(r, "B2").Runs);
        var net = Assert.Single(Row(r, "B3").Sources);
        Assert.Equal(("lisp", "NetFn", "[LispFunction]", Lang.CSharp), (net.What, net.Name, net.Entry, net.Lang));
        Assert.Equal((MapStatus.NotFound, "LISP (unknown-fn): not in these folders"), (Row(r, "B4").Status, Row(r, "B4").Note));
    }

    [Fact]
    public void A_commands_local_name_finds_it_too()
    {
        Put("src", "Cmds.cs", "class C { [CommandMethod(\"GRP\", \"DRAWGRID\", \"ZEICHNE\")] public void A() { } }");
        Menu("^C^C_ZEICHNE ", "^C^C_DRAWGRID ");
        var r = Map("menu", "src");
        Assert.Equal(("ZEICHNE", "C.A"), (Row(r, "B0").Sources[0].Name, Row(r, "B0").Sources[0].Symbol));
        Assert.Equal("DRAWGRID", Row(r, "B1").Sources[0].Name);
    }

    [Fact]
    public void A_cpp_command_whose_name_the_scanner_could_not_read_is_not_a_command()
    {
        Put("src", "c.cpp", "static void f() { }\nvoid init() { acedRegCmds->addCommand(group, cmdName, cmdName, ACRX_CMD_MODAL, f); }");
        Menu("^C^C_CMDNAME ");
        Assert.Equal(MapStatus.NotFound, Row(Map("menu", "src"), "B0").Status);
    }

    [Fact]
    public void A_load_finds_the_lsp_the_mnl_and_a_compiled_fas_whatever_path_is_written()
    {
        Put("src", "lisp/plain.lsp", "(princ)");
        Put("src", "lisp/compiled.fas", "BINARY");
        Put("src", "lisp/pack.vlx", "BINARY");
        Put("src", "acad.mnl", "(princ)");
        Menu("^C^C(load \"plain\")", "^C^C(load \"compiled\")", "^C^C(load \"C:/tools/pack.vlx\")", "^C^C(load \"d:\\\\x\\\\ACAD\")", "^C^C(load \"nothere\")");
        var r = Map("menu", "src");
        var plain = Assert.Single(Row(r, "B0").Sources);
        Assert.Equal(("file", "plain", "plain.lsp", Lang.Lisp, "lisp/plain.lsp", 0), (plain.What, plain.Name, plain.Symbol, plain.Lang, plain.Source.Short, plain.Source.Line));
        var compiled = Assert.Single(Row(r, "B1").Sources);
        Assert.Equal(("compiled.fas", Lang.Other), (compiled.Symbol, compiled.Lang));   // listed, never read
        Assert.Equal(("pack.vlx", Lang.Other), (Row(r, "B2").Sources[0].Symbol, Row(r, "B2").Sources[0].Lang));
        Assert.Equal(("acad.mnl", Lang.Lisp), (Row(r, "B3").Sources[0].Symbol, Row(r, "B3").Sources[0].Lang));
        Assert.Equal((MapStatus.NotFound, "loads nothere: not in these folders"), (Row(r, "B4").Status, Row(r, "B4").Note));
    }

    [Fact]
    public void Compiled_lisp_is_never_read_for_commands()
    {
        Put("src", "x.fas", "(defun c:inside () 1)");
        Menu("^C^C_INSIDE ");
        Assert.Equal(MapStatus.NotFound, Row(Map("menu", "src"), "B0").Status);
    }

    [Fact]
    public void A_lisp_command_inside_a_comment_or_a_string_is_not_a_definition()
    {
        Put("src", "a.lsp", ";| (defun c:hidden () 1) |;\n; (defun c:alsohidden () 2)\n(setq s \"(defun c:instring () 3)\")\n(defun c:shown () 4)");
        Menu("^C^C_HIDDEN ", "^C^C_ALSOHIDDEN ", "^C^C_INSTRING ", "^C^C_SHOWN ");
        var r = Map("menu", "src");
        Assert.Equal(new[] { MapStatus.NotFound, MapStatus.NotFound, MapStatus.NotFound, MapStatus.Mapped }, new[] { "B0", "B1", "B2", "B3" }.Select(i => Row(r, i).Status));
        Assert.Equal(4, Row(r, "B3").Sources[0].Source.Line);
    }

    // ---- methods and classes -----------------------------------------------------------------------------------------------------------

    private const string Ribbon = """
        using Autodesk.Windows;
        namespace Demo.Ui {
          public class Tabs {
            public static RibbonTab Make() {
              var t = new RibbonTab { Title = "Code", Id = "TAB_CODE" };
              t.Panels.Add(Only());
              return t;
            }
            static RibbonPanel Only() {
              var src = new RibbonPanelSource { Title = "P" };
              src.Items.Add(new RibbonButton { Text = "Go", CommandParameter = new Options("Demo.Asm", "Demo.Ui.Actions", "Go") });
              src.Items.Add(new RibbonButton { Text = "Gone", CommandParameter = new Options("Demo.Asm", "Demo.Ui.Actions", "Missing") });
              var p = new RibbonPanel(); p.Source = src; return p;
            }
          }
        }
        """;

    [Fact]
    public void A_handler_object_may_name_a_method_in_another_chosen_folder()
    {
        Put("ui", "Tabs.cs", Ribbon);
        Put("logic", "Actions.cs", "namespace Demo.Ui {\n  public static class Actions {\n    public static void Go() { }\n  }\n}");
        var both = Map("ui", "logic");
        var go = Row(both, "Go");
        Assert.Equal((MapStatus.Mapped, "method Demo.Ui.Actions.Go", "handler object"), (go.Status, go.Runs, go.FoundBy));
        var def = Assert.Single(go.Sources);
        Assert.Equal(("method", "Demo.Ui.Actions.Go", "Actions.cs", 3), (def.What, def.Entry == "method" ? def.Symbol : "", def.Source.Short, def.Source.Line));
        Assert.Equal(Dir("logic"), def.Source.Folder);
        // a method no folder declares is dropped, with a warning, as the docs pipeline does
        Assert.Equal((MapStatus.NoAction, "no macro"), (Row(both, "Gone").Status, Row(both, "Gone").Note));
        Assert.Contains(both.Warnings, w => w.Contains("1 button(s) pass a CommandParameter object that names no method") && w.Contains("Demo.Ui.Actions.Missing"));
        // with the ui folder alone nothing declares Go either
        Assert.Equal(MapStatus.NoAction, Row(Map("ui"), "Go").Status);
    }

    [Fact]
    public void A_class_is_found_by_its_full_name_and_its_execute_method_is_the_place()
    {
        Put("src", "H.cs", """
            namespace Contoso.Ui
            {
                public class Outer
                {
                    public class Inner : System.Windows.Input.ICommand
                    {
                        public bool CanExecute(object p) => true;
                        public void Execute(object p) { }
                    }
                }
                public class NoExecute { }
            }
            namespace Other { public class Same { public void Execute(object p) { } } }
            """);
        Put("src", "G.cs", "public class Plain { public void Execute(object p) { } }");
        var r = MapLive(["src"],
            Item("a", handler: "Contoso.Ui.Outer+Inner"),
            Item("b", handler: "Contoso.Ui.NoExecute"),
            Item("c", handler: "Other.Same"),
            Item("d", handler: "Contoso.Same"),
            Item("e", handler: "Plain"),
            Item("f", handler: "Thirdparty.Lib.Plain"));
        var inner = Assert.Single(Row(r, "a").Sources);
        Assert.Equal(("class", "Contoso.Ui.Outer.Inner.Execute", 8), (inner.What, inner.Symbol, inner.Source.Line));
        Assert.Equal("class Contoso.Ui.Outer+Inner", Row(r, "a").Runs.Replace("Outer.Inner", "Outer+Inner"));
        var none = Assert.Single(Row(r, "b").Sources);
        Assert.Equal(("Contoso.Ui.NoExecute", 11), (none.Symbol, none.Source.Line));   // no Execute: the declaration
        Assert.Equal("Other.Same.Execute", Assert.Single(Row(r, "c").Sources).Symbol);
        Assert.Equal(MapStatus.NotFound, Row(r, "d").Status);   // another namespace: a class of that name elsewhere is not it
        Assert.Equal("Plain.Execute", Assert.Single(Row(r, "e").Sources).Symbol);   // a class in no namespace is a class in no namespace
        Assert.Equal(MapStatus.NotFound, Row(r, "f").Status);                     // and not the one of a library, in C# (VB has a root namespace)
    }

    [Fact]
    public void A_vb_class_is_matched_without_the_projects_root_namespace()
    {
        Put("src", "Handler.vb", "Namespace Ui\n  Public Class GridHandler\n    Public Sub Execute(p As Object)\n    End Sub\n  End Class\nEnd Namespace\nPublic Class Plain\n  Public Sub Execute(p As Object)\n  End Sub\nEnd Class");
        var r = MapLive(["src"], Item("a", handler: "MyPlugin.Ui.GridHandler"), Item("b", handler: "MyPlugin.Plain"), Item("c", handler: "Plain"));
        Assert.Equal(("Ui.GridHandler.Execute", Lang.VbNet, 3), (Row(r, "a").Sources[0].Symbol, Row(r, "a").Sources[0].Lang, Row(r, "a").Sources[0].Source.Line));
        Assert.Equal("Plain.Execute", Row(r, "b").Sources[0].Symbol);
        Assert.Equal("Plain.Execute", Row(r, "c").Sources[0].Symbol);
    }

    [Fact]
    public void A_partial_class_with_execute_in_another_file_points_at_execute()
    {
        Put("src", "A.cs", "namespace N { public partial class H { int x; } }");
        Put("src", "B.cs", "namespace N { public partial class H {\n  public void Execute(object p) { }\n} }");
        var r = MapLive(["src"], Item("a", handler: "N.H"));
        var def = Assert.Single(Row(r, "a").Sources);
        Assert.Equal(("N.H.Execute", "B.cs", 2), (def.Symbol, def.Source.Short, def.Source.Line));
    }

    [Fact]
    public void A_parameter_object_is_a_class_target_and_autocad_and_dotnet_types_are_not()
    {
        Put("src", "P.cs", "namespace N {\n  public class Options { }\n  public class H { public void Execute(object p) { } }\n}");
        var r = MapLive(["src"],
            Item("a", handler: "N.H", param: "N.Options"),
            Item("b", handler: "Autodesk.AutoCAD.Ribbon.RibbonCommandHandler", param: "System.String"),
            Item("c", handler: "System.Windows.Input.RelayCommand", param: "Microsoft.Foo"),
            Item("d", handler: "MS.Internal.Thing"));
        Assert.Equal(("class N.H, class N.Options", "command handler class"), (Row(r, "a").Runs, Row(r, "a").FoundBy));
        Assert.Equal(new[] { "N.H.Execute", "N.Options" }, Row(r, "a").Sources.Select(s => s.Symbol));
        foreach (var id in new[] { "b", "c", "d" })
        {
            Assert.Equal((MapStatus.NoAction, "", "the live ribbon tells no command (an AutoCAD button?)"), (Row(r, id).Status, Row(r, id).Runs, Row(r, id).Note));
            Assert.Null(Row(r, id).FoundBy);
        }
    }

    [Fact]
    public void An_id_is_a_guess_only_when_nothing_else_names_a_target_and_the_command_exists()
    {
        Put("src", "C.cs", "class C { [CommandMethod(\"DRAWGRID\")] public void A() { } }");
        var r = MapLive(["src"], Item("ACAD.drawgrid"), Item("NoSuchCommand"), Item("DRAWGRID", handler: "Other.NotHere"));
        Assert.Equal(("command DRAWGRID", "button id (a guess)", MapStatus.Mapped), (Row(r, "ACAD.drawgrid").Runs, Row(r, "ACAD.drawgrid").FoundBy, Row(r, "ACAD.drawgrid").Status));
        Assert.Equal(MapStatus.NoAction, Row(r, "NoSuchCommand").Status);
        // the handler class is a target, so the id is not guessed
        Assert.Equal(("class Other.NotHere", MapStatus.NotFound), (Row(r, "DRAWGRID").Runs, Row(r, "DRAWGRID").Status));
    }

    // ---- targets ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_first_target_that_was_found_gives_found_by_and_a_missing_one_is_noted_on_a_mapped_row()
    {
        Put("src", "C.cs", "class C { [CommandMethod(\"HAVE\")] public void A() { } }");
        Menu("^C^C(load \"gone\") HAVE", "^C^C_NOTHAVE (c:have)", "^C^C_NOTHAVE ");
        var r = Map("menu", "src");
        var a = Row(r, "B0");
        Assert.Equal(("command HAVE, loads gone", MapStatus.Mapped, "macro", "loads gone: not in these folders"), (a.Runs, a.Status, a.FoundBy, a.Note));
        var b = Row(r, "B1");
        Assert.Equal(("command NOTHAVE, command HAVE", MapStatus.Mapped, "macro", "command NOTHAVE: not in these folders"), (b.Runs, b.Status, b.FoundBy, b.Note));
        Assert.Equal(MapStatus.NotFound, Row(r, "B2").Status);
    }

    [Fact]
    public void A_macro_that_names_nothing_is_no_action_and_says_so()
    {
        Menu("^C^C", "(princ)", "", "^C^C_zoom");
        var r = Map("menu");
        Assert.Equal(("the macro names no command or function", MapStatus.NoAction), (Row(r, "B0").Note, Row(r, "B0").Status));
        Assert.Equal("the macro names no command or function", Row(r, "B1").Note);
        Assert.Equal(("no macro", MapStatus.NoAction), (Row(r, "B2").Note, Row(r, "B2").Status));
        Assert.Equal(("command ZOOM", MapStatus.NotFound), (Row(r, "B3").Runs, Row(r, "B3").Status));
    }

    [Fact]
    public void Sources_are_listed_once_even_when_two_targets_find_the_same_place()
    {
        Put("src", "a.lsp", "(defun c:twice () 1)");
        Menu("^C^C_TWICE (c:twice)");
        Assert.Single(Row(Map("menu", "src"), "B0").Sources);
    }

    // ---- bad input ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_live_ribbon_with_missing_pieces_does_not_throw()
    {
        Menu("^C^C_zoom");
        var live = new List<LiveTab>
        {
            new(null, null, false, true, []),
            new("X", "", true, false, [new LivePanel(null, null, []), new LivePanel("", "  ", [new LiveItem(null, null, null, "", null, null, null, []), new LiveItem("", "", "", "RibbonSplitButton", "", "", "", [])])]),
            new("ID_TabHome", "T", true, false, [new LivePanel("P", "P", [Item("b0")], null)]),
        };
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [Dir("menu")], Live = live, AllLiveTabs = true });
        Assert.NotNull(Row(r, "B0").Live);   // shown in AutoCAD: its id is in the live ribbon
        Assert.Contains(r.Rows, x => x.Item == "(RibbonSplitButton)");
    }

    [Fact]
    public void A_folder_holding_only_unreadable_things_gives_warnings_not_an_exception()
    {
        Put("odd", "broken.cuix", "this is not a zip");
        Put("odd", "old.cui", "<CustSection><not closed>");
        Put("odd", "x.cs", "class {{{ ((( \"unterminated");
        Put("odd", "x.vb", "Sub ((( End");
        Put("odd", "x.cpp", "class A : {{{{ ((((");
        Put("odd", "x.lsp", "(defun ((( \"unterminated");
        var r = Map("odd");
        Assert.Empty(r.Rows);
        Assert.Contains(r.Warnings, w => w.StartsWith("odd: broken.cuix"));
    }

    [Fact]
    public void A_cuix_buttons_cli_command_is_looked_up_when_its_macro_names_none()
    {
        var path = Path.Combine(Dir("menu"), "cli.cuix");
        Directory.CreateDirectory(Dir("menu"));
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string n, string c) { using var w = new StreamWriter(zip.CreateEntry(n).Open()); w.Write(c); }
            Add("MenuGroup.cui", "<MenuGroup Name=\"G\"><MacroGroup Name=\"M\"><MenuMacro UID=\"M0\"><Macro type=\"Any\"><Name>Tool</Name>" +
                "<Command>^C^C(ct-start)</Command><CLICommand>MYTOOL</CLICommand></Macro></MenuMacro></MacroGroup></MenuGroup>");
            Add("RibbonRoot.cui", "<RibbonRoot><RibbonPanelSourceCollection><RibbonPanelSource UID=\"P\" Text=\"P\"><RibbonRow UID=\"R\">" +
                "<RibbonCommandButton UID=\"b0\" Text=\"Tool\" MenuMacroID=\"M0\"/></RibbonRow></RibbonPanelSource></RibbonPanelSourceCollection>" +
                "<RibbonTabSourceCollection><RibbonTabSource UID=\"T\" Text=\"T\"><RibbonPanelSourceReference PanelId=\"P\"/></RibbonTabSource></RibbonTabSourceCollection></RibbonRoot>");
        }
        Put("src", "tools.lsp", "(defun c:mytool () (princ))\n");

        var row = Row(Map("menu", "src"), "Tool");
        Assert.Equal(MapStatus.Mapped, row.Status);
        Assert.Equal("LISP (ct-start), command MYTOOL", row.Runs);
        Assert.Equal("macro", row.FoundBy);
        var def = Assert.Single(row.Sources);
        Assert.Equal(("command", "MYTOOL", Lang.Lisp, "defun c:", "tools.lsp", 1), (def.What, def.Name, def.Lang, def.Entry, def.Source.Short, def.Source.Line));
        Assert.Equal("LISP (ct-start): not in these folders", row.Note);
    }

    [Fact]
    public void A_live_handler_class_is_the_code_when_the_macros_command_is_not_in_the_folders()
    {
        Put("src", "Sender.cs", "namespace Demo.Handlers {\n  public class Sender {\n    public void Execute(object p) { }\n  }\n}");
        var item = new LiveItem("btn", "Via handler", null, "RibbonButton", "^C^C_NOPE ", null, "Demo.Handlers.Sender", []);
        var row = Row(MapLive(["src"], item), "Via handler");
        Assert.Equal((MapStatus.Mapped, "command NOPE, class Demo.Handlers.Sender", "command handler class"), (row.Status, row.Runs, row.FoundBy));
        Assert.Equal(("class", "Demo.Handlers.Sender.Execute", 3), (row.Sources[0].What, row.Sources[0].Symbol, row.Sources[0].Source.Line));
        Assert.Equal("command NOPE: not in these folders", row.Note);
    }
}
