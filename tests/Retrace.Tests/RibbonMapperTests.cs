using System.IO.Compression;
using System.Security;
using System.Text.Json;
using Retrace.Engine.Map;

namespace Retrace.Tests;

/// <summary>
/// The ribbon map end to end on invented folders: a .cuix and a legacy .cui, LISP, C#, VB and C++ code, a ribbon built in code, and a live
/// ribbon snapshot with the customization file AutoCAD "loaded". No client code, no AutoCAD.
/// </summary>
public class RibbonMapperTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("retrace-map").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Menu => Path.Combine(_root, "menu");
    private string Src => Path.Combine(_root, "src");
    private string Loaded => Path.Combine(_root, "autocad", "acad.cuix");

    // ---- fixtures ---------------------------------------------------------------------------------------------------------------

    private record Btn(string Id, string Label, string Macro);

    private static string MenuGroup(IEnumerable<Btn> buttons) =>
        "<MenuGroup Name=\"G\"><MacroGroup Name=\"M\">" +
        string.Concat(buttons.Select(b => $"<MenuMacro UID=\"M_{b.Id}\"><Macro type=\"Any\"><Name>{b.Label}</Name><Command>{SecurityElement.Escape(b.Macro)}</Command></Macro></MenuMacro>")) +
        "</MacroGroup></MenuGroup>";

    private static string RibbonRoot(string tabId, string tab, string panel, IEnumerable<Btn> buttons) =>
        $"<RibbonRoot><RibbonPanelSourceCollection><RibbonPanelSource UID=\"P_{tabId}\" Text=\"{panel}\"><RibbonRow UID=\"R_{tabId}\">" +
        string.Concat(buttons.Select(b => $"\n<RibbonCommandButton UID=\"{b.Id}\" Text=\"{b.Label}\" MenuMacroID=\"M_{b.Id}\"/>")) +
        $"\n</RibbonRow></RibbonPanelSource></RibbonPanelSourceCollection><RibbonTabSourceCollection><RibbonTabSource UID=\"{tabId}\" Text=\"{tab}\">" +
        $"<RibbonPanelSourceReference PanelId=\"P_{tabId}\"/></RibbonTabSource></RibbonTabSourceCollection></RibbonRoot>";

    private static void Cuix(string path, string tabId, string tab, string panel, params Btn[] buttons)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string n, string c) { using var w = new StreamWriter(zip.CreateEntry(n).Open()); w.Write(c); }
        Add("MenuGroup.cui", MenuGroup(buttons));
        Add("RibbonRoot.cui", RibbonRoot(tabId, tab, panel, buttons));
    }

    private static void Cui(string path, string tabId, string tab, string panel, params Btn[] buttons)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<?xml version=\"1.0\"?>\n<CustSection>\n{MenuGroup(buttons)}\n{RibbonRoot(tabId, tab, panel, buttons)}\n</CustSection>\n");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private const string ToolsLsp = """
        ; invented tools
        (defun c:mylisp ()
          (princ "mylisp")
          (princ))

        (defun c:other () (princ))
        """;

    private const string HelpersLsp = "(defun c:helpcmd () (princ))\n";

    private const string NetCmds = """
        using Autodesk.AutoCAD.Runtime;
        namespace Contoso.Tools
        {
            public class Cmds
            {
                [CommandMethod("NETCMD")]
                public void DrawGrid() { }
            }
        }
        """;

    private const string VbCmds = """
        Imports Autodesk.AutoCAD.Runtime
        Namespace Contoso.Tools
            Public Class VbCmds
                <CommandMethod("VBCMD")> _
                Public Sub DrawVb()
                End Sub
            End Class
        End Namespace
        """;

    private const string ArxCmds = """
        #include "stdafx.h"
        static void cmdArx() { }
        void initApp()
        {
            acedRegCmds->addCommand(_T("GRP"), _T("ARXCMD"), _T("ARXCMD"), ACRX_CMD_MODAL, cmdArx);
        }
        """;

    private const string RibbonCode = """
        using Autodesk.Windows;
        namespace Contoso.Ui
        {
            public class CodeTab
            {
                public static RibbonTab Build()
                {
                    var tab = new RibbonTab { Title = "Code Tab", Id = "CODE_TAB" };
                    tab.Panels.Add(Panel());
                    return tab;
                }
                static RibbonPanel Panel()
                {
                    var src = new RibbonPanelSource { Title = "Code Panel" };
                    src.Items.Add(new RibbonButton { Text = "VB command", Id = "btnVb", CommandParameter = "^C^C_VBCMD ", CommandHandler = new Sender() });
                    src.Items.Add(new RibbonButton { Text = "Grid", Id = "btnGrid", CommandHandler = new GridHandler() });
                    src.Items.Add(new RibbonButton { Text = "Lisp", Id = "btnLisp", CommandParameter = "^C^C(c:mylisp)" });
                    src.Items.Add(new RibbonButton { Text = "Nothing", Id = "btnNothing" });
                    var p = new RibbonPanel(); p.Source = src; return p;
                }
            }
            public class Sender : System.Windows.Input.ICommand
            {
                public void Execute(object parameter) { Doc.SendStringToExecute(Macro(parameter), true, false, false); }
            }
            public class GridHandler : System.Windows.Input.ICommand
            {
                public void Execute(object parameter) { }
            }
        }
        """;

    private static readonly Btn[] ContosoButtons =
    [
        new("btn_mylisp", "My lisp", "^C^CMYLISP "),
        new("btn_other", "Other", "^C^C(c:other)"),
        new("btn_net", "Net", "^C^C_NETCMD "),
        new("btn_vb", "Vb", "^C^C_VBCMD "),
        new("btn_arx", "Arx", "^C^C_ARXCMD "),
        new("btn_help", "Help", "^C^C(load \"helpers\") HELPCMD"),
        new("btn_diesel", "Diesel", "$M=$(getvar,clayer)"),
        new("btn_missing", "Missing", "^C^C_MISSING "),
    ];

    private void MakeFolders()
    {
        Cuix(Path.Combine(Menu, "contoso.cuix"), "ID_TabContoso", "Contoso", "Tools", ContosoButtons);
        Cui(Path.Combine(Menu, "old.cui"), "ID_TabLegacy", "Legacy", "Old", new Btn("btn_old", "Old net", "^C^C_NETCMD "));
        Write(Path.Combine(Src, "lisp", "tools.lsp"), ToolsLsp);
        Write(Path.Combine(Src, "lisp", "helpers.lsp"), HelpersLsp);
        Write(Path.Combine(Src, "Net", "Cmds.cs"), NetCmds);
        Write(Path.Combine(Src, "Vb", "Cmds.vb"), VbCmds);
        Write(Path.Combine(Src, "Arx", "cmds.cpp"), ArxCmds);
        Write(Path.Combine(Src, "Net", "Ribbon.cs"), RibbonCode);
        Cuix(Loaded, "ID_TabHome", "Home", "Draw", new Btn("ID_Line", "Line", "^C^C_line "));
    }

    private static LiveItem Item(string? id, string? text, string type = "RibbonButton", string? macro = null, string? handler = null, string? param = null, params LiveItem[] children) =>
        new(id, text, null, type, macro, param, handler, children);

    private static List<LiveTab> LiveRibbon() =>
    [
        // the code tab, as AutoCAD shows it: the same ids
        new("CODE_TAB", "Code Tab", true, false,
        [
            new LivePanel(null, "Code Panel",
            [
                Item("btnVb", "VB command", macro: "^C^C_VBCMD ", handler: "Contoso.Ui.Sender"),
                Item("btnGrid", "Grid", handler: "Contoso.Ui.GridHandler"),
                Item("btnLisp", "Lisp", macro: "^C^C(c:mylisp)"),
            ]),
        ]),
        // a CUIX tab: AutoCAD puts the customization group in front of the ids
        new("CONTOSO.ID_TabContoso", "Contoso", true, false,
        [
            new LivePanel("CONTOSO.P_ID_TabContoso", "Tools", [Item("CONTOSO.btn_net", "Net"), Item("CONTOSO.btn_vb", "Vb")]),
        ]),
        // a tab a plugin adds at run time
        new("PLUGIN_TAB", "Plugin", true, false,
        [
            new LivePanel(null, "Plugin Panel",
            [
                Item("PluginGrid", "Plugin grid", handler: "Contoso.Ui.GridHandler"),
                Item("NETCMD", "Net command"),
                Item("External", "Third party", handler: "Thirdparty.Lib.Handler", param: "System.String"),
                Item("Split", "Split", "RibbonSplitButton", children: [Item("SplitNet", "Net member", macro: "^C^C_NETCMD "), Item("SplitNone", "Empty member")]),
                new LiveItem(null, null, null, "RibbonRowPanel", null, null, null, [Item("InRow", "In a row", macro: "^C^C_VBCMD ")]),
                new LiveItem(null, null, null, "RibbonSeparator", null, null, null, []),
                new LiveItem(null, "A label", null, "RibbonLabel", null, null, null, []),
            ]),
        ]),
        // AutoCAD's own Home tab: its buttons carry no macro, the loaded acad.cuix knows it
        new("ACAD.ID_TabHome", "Home", true, false,
        [
            new LivePanel(null, "Draw", [Item("ACAD.ID_Line", "Line", handler: "Autodesk.AutoCAD.Ribbon.Something")]),
        ]),
    ];

    private RibbonMapResult Build(bool live = true, bool allTabs = false, params string[] folders)
    {
        MakeFolders();
        return RibbonMapper.Build(new RibbonMapOptions
        {
            Folders = folders.Length > 0 ? folders : [Menu, Src],
            Live = live ? LiveRibbon() : null,
            Customization = [Loaded, Path.Combine(Menu, "contoso.cuix")],
            AllLiveTabs = allTabs,
        });
    }

    private static MapRow Find(RibbonMapResult r, string tab, string item) =>
        Assert.Single(r.Rows, x => x.Tab == tab && x.Item == item);

    private static int LineOf(string text, string needle)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++) if (lines[i].Contains(needle)) return i + 1;
        throw new InvalidOperationException(needle);
    }

    // ---- folders ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Folders_are_full_paths_once_each_a_missing_one_and_a_nested_one_are_warnings()
    {
        var nested = Path.Combine(Src, "lisp");
        var missing = Path.Combine(_root, "missing");
        var r = Build(true, false, Menu + Path.DirectorySeparatorChar, Src, nested, missing, Menu.ToUpperInvariant());
        Assert.Equal(new[] { Menu, Src }, r.Folders);   // trailing separator trimmed, the repeat dropped
        Assert.Contains($"folder not found: {missing}", r.Warnings);
        Assert.Contains($"{nested} is inside {Src}: read once", r.Warnings);
        // lisp/ was read once: one definition of MYLISP, not two
        Assert.Single(Find(r, "Contoso", "My lisp").Sources);
    }

    [Fact]
    public void An_inner_folder_given_before_its_outer_one_is_still_read_once_by_the_outer()
    {
        var nested = Path.Combine(Src, "lisp");
        var r = Build(true, false, nested, Src);
        Assert.Equal(new[] { Src }, r.Folders);
        Assert.Contains($"{nested} is inside {Src}: read once", r.Warnings);
    }

    [Fact]
    public void A_folder_whose_name_starts_like_another_is_not_inside_it()
    {
        MakeFolders();
        var other = Src + "2";
        Write(Path.Combine(other, "x.lsp"), "(defun c:x () 1)");
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [Src, other] });
        Assert.Equal(new[] { Src, other }, r.Folders);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("is inside"));
    }

    [Fact]
    public void With_no_folder_left_there_are_no_rows_and_a_warning()
    {
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [Path.Combine(_root, "nope"), ""], Live = LiveRibbon() });
        Assert.Empty(r.Folders);
        Assert.Empty(r.Rows);
        Assert.Contains(r.Warnings, w => w.StartsWith("folder not found: "));
        Assert.True(r.Warnings.Count >= 2);
    }

    [Fact]
    public void A_cancelled_token_stops_the_map()
    {
        MakeFolders();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => RibbonMapper.Build(new RibbonMapOptions { Folders = [Menu, Src] }, cts.Token));
    }

    // ---- the folders alone ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cuix_rows_run_the_commands_and_functions_their_macros_name_and_are_found_in_the_code()
    {
        var r = Build(live: false);
        Assert.False(r.LiveMapped);
        Assert.All(r.Rows, x => Assert.Null(x.Live));

        var lisp = Find(r, "Contoso", "My lisp");
        Assert.Equal(("cuix", MapStatus.Mapped, "command MYLISP", "macro", "button"), (lisp.Origin, lisp.Status, lisp.Runs, lisp.FoundBy, lisp.Kind));
        var def = Assert.Single(lisp.Sources);
        Assert.Equal(("command", "MYLISP", Lang.Lisp, "defun c:", "c:mylisp"), (def.What, def.Name, def.Lang, def.Entry, def.Symbol));
        Assert.Equal(("lisp/tools.lsp", LineOf(ToolsLsp, "defun c:mylisp")), (def.Source.Short, def.Source.Line));
        Assert.Equal(Path.Combine(Src, "lisp", "tools.lsp"), def.Source.Path);
        Assert.Equal("lisp", def.Project);

        Assert.Equal("command OTHER", Find(r, "Contoso", "Other").Runs);
        Assert.Equal(("lisp/tools.lsp", LineOf(ToolsLsp, "defun c:other")), (Find(r, "Contoso", "Other").Sources[0].Source.Short, Find(r, "Contoso", "Other").Sources[0].Source.Line));

        var net = Find(r, "Contoso", "Net");
        Assert.Equal(MapStatus.Mapped, net.Status);
        var netDef = Assert.Single(net.Sources);
        Assert.Equal(("[CommandMethod]", Lang.CSharp, "Contoso.Tools.Cmds.DrawGrid", "Net/Cmds.cs", LineOf(NetCmds, "[CommandMethod")),
            (netDef.Entry, netDef.Lang, netDef.Symbol, netDef.Source.Short, netDef.Source.Line));

        var vb = Assert.Single(Find(r, "Contoso", "Vb").Sources);
        Assert.Equal(("<CommandMethod>", Lang.VbNet, "Contoso.Tools.VbCmds.DrawVb", "Vb/Cmds.vb"), (vb.Entry, vb.Lang, vb.Symbol, vb.Source.Short));

        var arx = Assert.Single(Find(r, "Contoso", "Arx").Sources);
        Assert.Equal(("addCommand", Lang.Cpp, "cmdArx", "Arx/cmds.cpp", LineOf(ArxCmds, "addCommand")), (arx.Entry, arx.Lang, arx.Symbol, arx.Source.Short, arx.Source.Line));
    }

    [Fact]
    public void A_macro_that_loads_a_file_and_names_a_command_is_found_in_both()
    {
        var help = Find(Build(live: false), "Contoso", "Help");
        Assert.Equal("command HELPCMD, loads helpers", help.Runs);
        Assert.Equal(MapStatus.Mapped, help.Status);
        Assert.Equal("macro", help.FoundBy);
        Assert.Equal(new[] { "command", "file" }, help.Sources.Select(s => s.What));
        Assert.Equal(("HELPCMD", "lisp/helpers.lsp", 1), (help.Sources[0].Name, help.Sources[0].Source.Short, help.Sources[0].Source.Line));
        Assert.Equal(("helpers", "helpers.lsp", Lang.Lisp, "file"), (help.Sources[1].Name, help.Sources[1].Symbol, help.Sources[1].Lang, help.Sources[1].Entry));
        Assert.Equal(("lisp/helpers.lsp", 0), (help.Sources[1].Source.Short, help.Sources[1].Source.Line));   // a file has no line
        Assert.Null(help.Note);
    }

    [Fact]
    public void A_command_no_folder_defines_is_not_found_and_a_diesel_macro_is_not_followed()
    {
        var r = Build(live: false);
        var missing = Find(r, "Contoso", "Missing");
        Assert.Equal((MapStatus.NotFound, "command MISSING", "macro", "command MISSING: not in these folders"), (missing.Status, missing.Runs, missing.FoundBy, missing.Note));
        Assert.Empty(missing.Sources);

        var diesel = Find(r, "Contoso", "Diesel");
        Assert.Equal((MapStatus.NoAction, "", null, "DIESEL macro: not followed"), (diesel.Status, diesel.Runs, diesel.FoundBy, diesel.Note));
        Assert.Equal("$M=$(getvar,clayer)", diesel.Macro);
    }

    [Fact]
    public void A_legacy_cui_file_is_a_cui_tab_with_its_own_origin()
    {
        var old = Find(Build(live: false), "Legacy", "Old net");
        Assert.Equal(("cui", MapStatus.Mapped, "command NETCMD"), (old.Origin, old.Status, old.Runs));
        Assert.Equal("old.cui", old.DefinedIn!.Short);
        Assert.Null(old.DefinedIn.Part);
        Assert.True(old.DefinedIn.Line > 1);
        Assert.Equal(Menu, old.DefinedIn.Folder);
    }

    [Fact]
    public void Where_a_cuix_item_is_defined_is_its_element_in_the_zip()
    {
        var net = Find(Build(live: false), "Contoso", "Net");
        Assert.Equal("contoso.cuix", net.DefinedIn!.Short);
        Assert.Equal("RibbonRoot.cui", net.DefinedIn.Part);
        Assert.Equal(Path.Combine(Menu, "contoso.cuix"), net.DefinedIn.Path);
        Assert.Equal(LineOf(RibbonRoot("ID_TabContoso", "Contoso", "Tools", ContosoButtons), "UID=\"btn_net\""), net.DefinedIn.Line);
        Assert.Equal("^C^C_NETCMD", net.Macro);   // as the cuix reader gives it: trimmed
        Assert.Equal("btn_net", net.ItemId);
        Assert.Equal("ID_TabContoso", net.TabId);
        Assert.Equal("Tools", net.Panel);
    }

    [Fact]
    public void Rows_come_tab_by_tab_cuix_and_cui_tabs_first_then_code_tabs()
    {
        var r = Build(live: false);
        var tabs = r.Rows.Select(x => x.Tab).Distinct().ToList();
        Assert.Equal(new[] { "Contoso", "Legacy", "Code Tab" }, tabs);
        // each tab's rows are together
        Assert.Equal(tabs.Count, r.Rows.Select(x => x.Tab).Aggregate(new List<string>(), (l, t) => { if (l.Count == 0 || l[^1] != t) l.Add(t); return l; }).Count);
        Assert.Equal(new[] { "My lisp", "Other", "Net", "Vb", "Arx", "Help", "Diesel", "Missing" }, r.Rows.Where(x => x.Tab == "Contoso").Select(x => x.Item));
    }

    [Fact]
    public void A_code_button_with_a_literal_macro_runs_its_command_not_its_id()
    {
        var r = Build(live: false);
        var vb = Find(r, "Code Tab", "VB command");
        Assert.Equal(("code", "button", "command VBCMD", "command parameter", MapStatus.Mapped), (vb.Origin, vb.Kind, vb.Runs, vb.FoundBy, vb.Status));
        Assert.Equal("^C^C_VBCMD ", vb.Macro);
        Assert.Equal("btnVb", vb.ItemId);
        Assert.Equal("Vb/Cmds.vb", Assert.Single(vb.Sources).Source.Short);
        Assert.Equal("Net/Ribbon.cs", vb.DefinedIn!.Short);
        Assert.Equal(LineOf(RibbonCode, "Id = \"btnVb\""), vb.DefinedIn.Line);

        var lisp = Find(r, "Code Tab", "Lisp");
        Assert.Equal(("command MYLISP", MapStatus.Mapped), (lisp.Runs, lisp.Status));
    }

    [Fact]
    public void A_code_button_with_nothing_to_run_is_no_action_without_a_live_ribbon()
    {
        var grid = Find(Build(live: false), "Code Tab", "Grid");
        Assert.Equal((MapStatus.NoAction, "no macro", ""), (grid.Status, grid.Note, grid.Runs));
        var nothing = Find(Build(live: false), "Code Tab", "Nothing");
        Assert.Equal((MapStatus.NoAction, "no macro"), (nothing.Status, nothing.Note));
    }

    [Fact]
    public void Warnings_name_the_folder_and_the_files_skipped_as_secrets()
    {
        MakeFolders();
        Write(Path.Combine(Src, "Net", "api-key.cs"), "class K {}");
        File.WriteAllBytes(Path.Combine(Src, "Net", "broken.cuix"), [1, 2, 3, 4]);
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [Menu, Src] });
        Assert.Contains(r.Warnings, w => w.StartsWith("src: skipped Net/api-key.cs"));
        Assert.Contains(r.Warnings, w => w.StartsWith("src: Net/broken.cuix:"));
        Assert.True(r.FilesRead >= 9);
    }

    // ---- the live ribbon --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Rows_the_live_ribbon_shows_are_flagged_and_the_others_are_not()
    {
        var r = Build();
        Assert.True(r.LiveMapped);
        // the cuix tab: the group in front of the ids does not matter, panel and items are matched by title and id
        Assert.True(Find(r, "Contoso", "Net").Live);
        Assert.True(Find(r, "Contoso", "Vb").Live);
        Assert.False(Find(r, "Contoso", "Arx").Live);
        Assert.False(Find(r, "Legacy", "Old net").Live);
        // the code tab: same ids
        Assert.True(Find(r, "Code Tab", "VB command").Live);
        Assert.True(Find(r, "Code Tab", "Grid").Live);
        Assert.True(Find(r, "Code Tab", "Lisp").Live);
        Assert.False(Find(r, "Code Tab", "Nothing").Live);
        // nothing is added to a tab for items it already has
        Assert.Single(r.Rows, x => x.Tab == "Contoso" && x.Item == "Net");
    }

    [Fact]
    public void A_live_handler_class_is_what_a_code_button_with_no_macro_runs()
    {
        var r = Build();
        var grid = Find(r, "Code Tab", "Grid");
        Assert.Equal((MapStatus.Mapped, "class Contoso.Ui.GridHandler", "command handler class"), (grid.Status, grid.Runs, grid.FoundBy));
        var def = Assert.Single(grid.Sources);
        Assert.Equal(("class", Lang.CSharp, "class", "Contoso.Ui.GridHandler.Execute", "Net/Ribbon.cs"), (def.What, def.Lang, def.Entry, def.Symbol, def.Source.Short));
        Assert.Equal(LineOf(RibbonCode, "public void Execute(object parameter) { }"), def.Source.Line);   // GridHandler's Execute, not Sender's
    }

    [Fact]
    public void A_live_handler_class_is_left_out_when_the_macro_already_maps_to_code()
    {
        // the handler only sends the macro: one class behind every button, it would repeat itself on each row
        var vb = Find(Build(), "Code Tab", "VB command");
        Assert.Equal("command VBCMD", vb.Runs);
        Assert.Equal("command parameter", vb.FoundBy);
        Assert.Equal(new[] { "command" }, vb.Sources.Select(s => s.What));
    }

    [Fact]
    public void A_cuix_row_with_no_macro_takes_the_live_items_macro()
    {
        MakeFolders();
        Cuix(Path.Combine(Menu, "contoso.cuix"), "ID_TabContoso", "Contoso", "Tools", new Btn("btn_net", "Net", ""));
        var live = new List<LiveTab> { new("CONTOSO.ID_TabContoso", "Contoso", true, false, [new LivePanel(null, "Tools", [Item("CONTOSO.btn_net", "Net", macro: "^C^C_NETCMD ")])]) };
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [Menu, Src], Live = live });
        var net = Find(r, "Contoso", "Net");
        Assert.Equal(("^C^C_NETCMD ", MapStatus.Mapped, "command NETCMD"), (net.Macro, net.Status, net.Runs));
        Assert.Equal("macro", net.FoundBy);   // the row comes from a cuix file
    }

    [Fact]
    public void A_live_only_tab_is_listed_when_one_of_its_items_maps_and_its_rows_say_where_they_come_from()
    {
        var r = Build();
        var plugin = r.Rows.Where(x => x.Tab == "Plugin").ToList();
        Assert.NotEmpty(plugin);
        Assert.All(plugin, x => Assert.Equal(("live", true, null), (x.Origin, x.Live, x.DefinedIn)));

        var grid = Find(r, "Plugin", "Plugin grid");
        Assert.Equal((MapStatus.Mapped, "class Contoso.Ui.GridHandler", "command handler class", "Plugin Panel"), (grid.Status, grid.Runs, grid.FoundBy, grid.Panel));
        Assert.Equal("Net/Ribbon.cs", grid.Sources[0].Source.Short);

        var guess = Find(r, "Plugin", "Net command");
        Assert.Equal((MapStatus.Mapped, "command NETCMD", "button id (a guess)"), (guess.Status, guess.Runs, guess.FoundBy));
        Assert.Equal("Net/Cmds.cs", Assert.Single(guess.Sources).Source.Short);

        var external = Find(r, "Plugin", "Third party");
        Assert.Equal((MapStatus.NotFound, "class Thirdparty.Lib.Handler"), (external.Status, external.Runs));   // System.String is not one of ours
        Assert.Equal("class Thirdparty.Lib.Handler: not in these folders", external.Note);
    }

    [Fact]
    public void Split_button_members_are_rows_of_their_own_and_containers_separators_and_labels_are_not()
    {
        var r = Build();
        var items = r.Rows.Where(x => x.Tab == "Plugin").Select(x => x.Item).ToList();
        Assert.Equal(new[] { "Plugin grid", "Net command", "Third party", "Split", "Split › Net member", "Split › Empty member", "In a row" }, items);
        Assert.Equal("split button", Find(r, "Plugin", "Split").Kind);
        Assert.Equal(MapStatus.NoAction, Find(r, "Plugin", "Split").Status);
        Assert.Equal("the live ribbon tells no command (an AutoCAD button?)", Find(r, "Plugin", "Split › Empty member").Note);
        var member = Find(r, "Plugin", "Split › Net member");
        Assert.Equal(("command NETCMD", "command parameter", "^C^C_NETCMD "), (member.Runs, member.FoundBy, member.Macro));
        Assert.Equal("command VBCMD", Find(r, "Plugin", "In a row").Runs);
    }

    [Fact]
    public void A_tab_only_the_loaded_file_and_the_live_ribbon_have_is_left_out_when_nothing_in_it_maps()
    {
        var r = Build();
        Assert.DoesNotContain(r.Rows, x => x.Tab == "Home");
        Assert.Equal(1, r.LiveTabsLeftOut);
    }

    [Fact]
    public void With_all_live_tabs_on_that_tab_is_listed_with_the_macro_from_the_loaded_file()
    {
        var r = Build(allTabs: true);
        Assert.Equal(0, r.LiveTabsLeftOut);
        var line = Find(r, "Home", "Line");
        Assert.Equal(("loaded cuix", true, MapStatus.NotFound, "command LINE", "macro"), (line.Origin, line.Live, line.Status, line.Runs, line.FoundBy));
        Assert.Equal("command LINE: not in these folders", line.Note);
        Assert.Equal("^C^C_line", line.Macro);
        Assert.Equal(Loaded, line.DefinedIn!.Path);
        Assert.Null(line.DefinedIn.Folder);
        Assert.Equal("RibbonRoot.cui", line.DefinedIn.Part);
        Assert.Equal(Loaded, line.DefinedIn.Short);   // outside the chosen folders: the full path
        Assert.Equal("ID_Line", line.ItemId);
        Assert.Equal("Draw", line.Panel);
        Assert.Empty(line.Sources);
    }

    [Fact]
    public void Tabs_from_the_folders_are_always_listed_and_loaded_files_are_read_only_with_the_live_ribbon()
    {
        var r = Build(live: false);
        Assert.Equal(0, r.LiveTabsLeftOut);
        Assert.DoesNotContain(r.Rows, x => x.Tab is "Home" or "Plugin");
        var withLive = Build();
        Assert.Contains(withLive.Rows, x => x.Tab == "Contoso");
        Assert.DoesNotContain(withLive.Rows, x => x.Origin == "loaded cuix");   // Home was left out, and contoso.cuix is read once, from its folder
    }

    [Fact]
    public void A_live_item_found_on_another_panel_by_a_unique_id_is_the_same_row()
    {
        MakeFolders();
        var live = new List<LiveTab> { new("OTHER", "Moved", true, false, [new LivePanel(null, "Elsewhere", [Item("CONTOSO.btn_net", "Net renamed")])]) };
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [Menu, Src], Live = live });
        Assert.True(Find(r, "Contoso", "Net").Live);
        Assert.DoesNotContain(r.Rows, x => x.Tab == "Moved");   // no new row: the item is the one in contoso.cuix
        Assert.Equal(1, r.LiveTabsLeftOut);                       // and the live tab holds nothing else
    }

    [Fact]
    public void Dialog_launchers_are_rows_with_the_macro_they_run()
    {
        MakeFolders();
        var withLauncher = """
            <RibbonRoot><RibbonPanelSourceCollection><RibbonPanelSource UID="P_L" Text="Tools"><DialogBoxLauncher UID="DL" CommandID="M_launch"/>
            <RibbonRow UID="R_L"><RibbonCommandButton UID="btn_l" Text="Net" MenuMacroID="M_btn_l"/></RibbonRow></RibbonPanelSource></RibbonPanelSourceCollection>
            <RibbonTabSourceCollection><RibbonTabSource UID="ID_TabL" Text="Launch"><RibbonPanelSourceReference PanelId="P_L"/></RibbonTabSource></RibbonTabSourceCollection></RibbonRoot>
            """;
        var menu = """
            <MenuGroup Name="G"><MacroGroup Name="M">
            <MenuMacro UID="M_btn_l"><Macro type="Any"><Name>Net</Name><Command>^C^C_NETCMD </Command></Macro></MenuMacro>
            <MenuMacro UID="M_launch"><Macro type="Any"><Name>Settings</Name><Command>^C^C_VBCMD </Command></Macro></MenuMacro>
            </MacroGroup></MenuGroup>
            """;
        var path = Path.Combine(Menu, "launch.cuix");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string n, string c) { using var w = new StreamWriter(zip.CreateEntry(n).Open()); w.Write(c); }
            Add("MenuGroup.cui", menu);
            Add("RibbonRoot.cui", withLauncher);
        }
        var live = new List<LiveTab> { new("ID_TabL", "Launch", true, false, [new LivePanel(null, "Tools", [Item("btn_l", "Net")], Item(null, null, "RibbonCommandItem"))]) };
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [Menu, Src], Live = live });
        var tab = r.Rows.Where(x => x.Tab == "Launch").ToList();
        Assert.Equal(new[] { "Net", "dialog launcher" }, tab.Select(x => x.Item));
        var launcher = tab[1];
        Assert.Equal(("dialog launcher", "cuix", MapStatus.Mapped, "command VBCMD", true), (launcher.Kind, launcher.Origin, launcher.Status, launcher.Runs, launcher.Live));
        Assert.Equal("Vb/Cmds.vb", Assert.Single(launcher.Sources).Source.Short);
        Assert.Equal("launch.cuix", launcher.DefinedIn!.Short);
    }

    [Fact]
    public void A_live_item_without_an_id_is_matched_by_its_label_within_the_matched_panel()
    {
        MakeFolders();
        var live = new List<LiveTab> { new("CONTOSO.ID_TabContoso", "Contoso", true, false, [new LivePanel(null, "tools", [Item(null, "ARX"), Item(null, " Not  there ")])]) };
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [Menu, Src], Live = live });
        Assert.True(Find(r, "Contoso", "Arx").Live);       // the same label, case and spaces aside; the panel title matched too
        Assert.False(Find(r, "Contoso", "Net").Live);
        var extra = Find(r, "Contoso", "Not there");        // no such item in the file: a live item of the Contoso tab
        Assert.Equal(("live", true, MapStatus.NoAction), (extra.Origin, extra.Live, extra.Status));
        Assert.Equal(r.Rows.Where(x => x.Tab == "Contoso").Last(), extra);   // it follows the tab's own rows
    }

    [Fact]
    public void Fold_panels_are_walked_like_row_panels_and_breaks_are_not_rows()
    {
        MakeFolders();
        LiveItem Typed(string type, params LiveItem[] kids) => new(null, null, null, type, null, null, null, kids);
        var live = new List<LiveTab>
        {
            new("T", "Folds", true, false,
            [
                new LivePanel(null, "P",
                [
                    Typed("RibbonFoldPanel", Item("a", "In fold", macro: "^C^C_NETCMD "), Typed("RibbonRowBreak"), Item("b", "Second row")),
                    Typed("RibbonPanelBreak"),
                    Typed("RibbonRowPanel", Typed("RibbonFoldPanel", Item("c", "Deep"))),
                ]),
            ]),
        };
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [Menu, Src], Live = live, AllLiveTabs = true });
        Assert.Equal(new[] { "In fold", "Second row", "Deep" }, r.Rows.Where(x => x.Tab == "Folds").Select(x => x.Item));
    }

    [Fact]
    public void A_code_tab_is_found_by_its_title_when_the_live_id_differs()
    {
        MakeFolders();
        var live = new List<LiveTab> { new("SOMETHING.ELSE", " code tab ", true, false, [new LivePanel(null, "Code Panel", [Item("btnLisp", "Lisp")])]) };
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [Menu, Src], Live = live });
        Assert.True(Find(r, "Code Tab", "Lisp").Live);
    }

    // ---- the files ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_writer_makes_three_files_that_agree_with_the_map()
    {
        var r = Build(allTabs: true);
        var outDir = Path.Combine(_root, "out", "map");
        var paths = RibbonMapWriter.Write(r, outDir);
        Assert.Equal(new[] { "ribbon-map.md", "ribbon-map.csv", "ribbon-map.json" }, paths.Select(Path.GetFileName));
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));

        using var json = JsonDocument.Parse(File.ReadAllText(paths[2]));
        Assert.Equal("retrace/ribbon-map@1", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(r.Rows.Count, json.RootElement.GetProperty("rows").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("liveMapped").GetBoolean());
        Assert.Equal(r.FilesRead, json.RootElement.GetProperty("filesRead").GetInt32());

        var md = File.ReadAllText(paths[0]);
        Assert.StartsWith("# Ribbon map\n", md);
        Assert.Contains("from 2 folders and the ribbon AutoCAD showed.", md);
        Assert.Contains("## Contoso", md);

        var csvBytes = File.ReadAllBytes(paths[1]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csvBytes[..3]);
        Assert.Equal(r.Rows.Count + 1, ReadCsv(File.ReadAllText(paths[1]).TrimStart('﻿')).Count);
    }

    // a small RFC 4180 reader, to read the csv back
    private static List<List<string>> ReadCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new(); i++; }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    [Fact]
    public void Retraces_own_ribbon_is_mapped_to_its_own_command_methods()
    {
        // real code, not a fixture: the plugin's source folder holds its ribbon (RetraceTab.cs) and its commands (RetraceApp.cs)
        var r = RibbonMapper.Build(new RibbonMapOptions { Folders = [LayerRulesTests.SourceRoot()] });
        var buttons = r.Rows.Where(x => x.Tab == "Retrace" && x.Origin == "code").ToList();
        Assert.True(buttons.Count >= 17);
        Assert.All(buttons, b =>
        {
            Assert.Equal((MapStatus.Mapped, "command parameter", "AutoCad/RetraceApp.cs", Lang.CSharp), (b.Status, b.FoundBy, b.Sources[0].Source.Short, b.Sources[0].Lang));
            Assert.StartsWith("command RETRACE", b.Runs);
        });
        var record = buttons.Single(b => b.Item == "Record");
        Assert.Equal(("command RETRACERECORD", "Retrace.AutoCad.RetraceApp.Record", "[CommandMethod]"), (record.Runs, record.Sources[0].Symbol, record.Sources[0].Entry));
        // the folder buttons say which folder in the macro, not in the id: all three run RETRACEFOLDER
        Assert.Equal(new[] { "command RETRACEFOLDER" }, buttons.Where(b => b.Item is "Tests" or "Captures" or "Docs").Select(b => b.Runs).Distinct());
    }

    [Fact]
    public void The_map_is_the_same_when_made_twice()
    {
        MakeFolders();
        string Dump(RibbonMapResult r) => string.Join("\n", r.Rows.Select(x => $"{x.Tab}|{x.Panel}|{x.Item}|{x.Status}|{x.Runs}|{x.FoundBy}|{x.Live}|{string.Join(",", x.Sources.Select(s => s.Source))}"));
        var opt = new RibbonMapOptions { Folders = [Menu, Src], Live = LiveRibbon(), Customization = [Loaded], AllLiveTabs = true };
        Assert.Equal(Dump(RibbonMapper.Build(opt)), Dump(RibbonMapper.Build(opt)));
    }

    [Fact]
    public void The_engine_runner_writes_the_map_and_tells_what_it_found()
    {
        MakeFolders();
        var outDir = Path.Combine(_root, "result");
        var res = Retrace.Engine.EngineRunner.MapRibbon(new RibbonMapOptions { Folders = [Menu, Src, Path.Combine(_root, "gone")], Live = LiveRibbon(), Customization = [Loaded], Output = outDir });
        Assert.True(res.Success, res.Error);
        Assert.NotNull(res.RibbonMap);
        Assert.Equal(outDir, res.Output);
        Assert.Contains(res.Warnings, w => w.StartsWith("folder not found"));
        Assert.Equal($"folders:  2, files: {res.RibbonMap!.FilesRead}", res.Messages[0]);
        var map = res.RibbonMap;
        Assert.Equal($"ribbon:   {map.Rows.Count} items: {map.Mapped} mapped to code, {map.NotFound} not found, {map.NoAction} with no action", res.Messages[1]);
        Assert.Equal("live:     on, 1 tabs left out", res.Messages[2]);
        Assert.Equal($"output:   {outDir}", res.Messages[3]);
        Assert.True(File.Exists(Path.Combine(outDir, "ribbon-map.md")));
    }

    [Fact]
    public void The_engine_runner_says_live_off_and_fails_when_no_folder_exists_or_the_map_cannot_be_written()
    {
        MakeFolders();
        var off = Retrace.Engine.EngineRunner.MapRibbon(new RibbonMapOptions { Folders = [Menu] });
        Assert.True(off.Success);
        Assert.Contains("live:     off", off.Messages);
        Assert.Null(off.Output);

        var none = Retrace.Engine.EngineRunner.MapRibbon(new RibbonMapOptions { Folders = [Path.Combine(_root, "gone")] });
        Assert.False(none.Success);
        Assert.Equal("none of the folders exists", none.Error);
        Assert.Null(none.RibbonMap);

        // the output folder is a file: the map is still returned, with the error
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "x");
        var bad = Retrace.Engine.EngineRunner.MapRibbon(new RibbonMapOptions { Folders = [Menu], Output = Path.Combine(blocker, "map") });
        Assert.False(bad.Success);
        Assert.StartsWith("could not write the map: ", bad.Error);
        Assert.NotNull(bad.RibbonMap);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(Retrace.Engine.EngineRunner.MapRibbon(new RibbonMapOptions { Folders = [Menu] }, cts.Token).Cancelled);
    }
}
