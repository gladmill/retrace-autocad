using Retrace.Engine.Scan.CodeRibbon;

namespace Retrace.Tests;

/// <summary>
/// A code button's CommandParameter that is plain text is the macro its handler sends ("^C^C_DRAWLINES "): the item keeps it as its Macro and
/// takes its first command, instead of guessing the command from the button's Id. Each test has a small invented fixture.
/// </summary>
public class CodeRibbonMacroTests
{
    private static List<RibbonItem> Items(string code, Lang lang = Lang.CSharp)
    {
        var f = new SourceFile { AbsPath = "/x/Ribbon", RelPath = lang == Lang.VbNet ? "Proj/Ribbon.vb" : "Proj/Ribbon.cs", Lang = lang, Project = "Proj", Text = code };
        var tab = Assert.Single(CodeRibbonReader.Read(new[] { f }));
        return tab.Panels.SelectMany(p => p.Rows).SelectMany(r => r.Items).ToList();
    }

    // The handler sends the Id when the parameter is blank, as Retrace's own handler does: the Id rule still applies to a button with no macro.
    private const string SendingHandler = """
        public class SendHandler : System.Windows.Input.ICommand {
          public void Execute(object p) { Doc().SendStringToExecute(((RibbonButton)p).CommandParameter + "", true, false, false); }
        }
        """;

    [Fact]
    public void A_literal_macro_names_the_command_not_the_button_id()
    {
        var items = Items("""
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tabs {
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "Draw", Id = "TAB_Draw" };
                  tab.Panels.Add(Only());
                  return tab;
                }
                static RibbonPanel Only() {
                  var src = new RibbonPanelSource { Title = "Lines" };
                  src.Items.Add(new RibbonButton { Text = "Lines", Id = "btnLines", CommandHandler = new SendHandler(), CommandParameter = "^C^C_DRAWLINES " });
                  var b = new RibbonButton();
                  b.Text = "Grid";
                  b.Id = "btnGrid";
                  b.CommandHandler = new SendHandler();
                  b.CommandParameter = "^C^C(c:drawgrid)";
                  src.Items.Add(b);
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            """ + SendingHandler + "}");
        Assert.Equal(("DRAWLINES", "^C^C_DRAWLINES "), (items[0].Command, items[0].Macro));
        Assert.Equal("btnLines", items[0].Id);
        Assert.Equal(("DRAWGRID", "^C^C(c:drawgrid)"), (items[1].Command, items[1].Macro));
    }

    [Fact]
    public void Without_a_macro_the_id_is_still_the_command_the_handler_sends()
    {
        var items = Items("""
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tabs {
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "Draw", Id = "TAB_Draw" };
                  tab.Panels.Add(Only());
                  return tab;
                }
                static RibbonPanel Only() {
                  var src = new RibbonPanelSource { Title = "Lines" };
                  src.Items.Add(new RibbonButton { Text = "Lines", Id = "DrawLines", CommandHandler = new SendHandler() });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            """ + SendingHandler + "}");
        var b = Assert.Single(items);
        Assert.Equal("DRAWLINES", b.Command);
        Assert.Null(b.Macro);
    }

    [Fact]
    public void A_macro_with_no_command_in_it_is_kept_and_leaves_the_command_alone()
    {
        var items = Items("""
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tabs {
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "Draw", Id = "TAB_Draw" };
                  tab.Panels.Add(Only());
                  return tab;
                }
                static RibbonPanel Only() {
                  var src = new RibbonPanelSource { Title = "Lines" };
                  src.Items.Add(new RibbonButton { Text = "Tool", Id = "btnTool", CommandParameter = "^C^C(mytool 1)" });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """);
        var b = Assert.Single(items);
        Assert.Equal("^C^C(mytool 1)", b.Macro);
        Assert.Null(b.Command);   // no handler class sends the Id here, and the macro names no command
    }

    [Fact]
    public void A_macro_in_a_const_string_or_joined_from_literals_is_read()
    {
        var items = Items("""
            using Autodesk.Windows;
            namespace Demo.Ui {
              public static class Macros {
                public const string Grid = "^C^C_DRAWGRID ";
                public static readonly string Prefix = "^C^C_";
              }
              public class Tabs {
                private const string Circle = "^C^C_DRAWCIRCLE ";
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "Draw", Id = "TAB_Draw" };
                  tab.Panels.Add(Only());
                  return tab;
                }
                static RibbonPanel Only() {
                  var src = new RibbonPanelSource { Title = "Shapes" };
                  src.Items.Add(new RibbonButton { Text = "A", Id = "btnA", CommandParameter = Circle });
                  src.Items.Add(new RibbonButton { Text = "B", Id = "btnB", CommandParameter = Macros.Grid });
                  src.Items.Add(new RibbonButton { Text = "C", Id = "btnC", CommandParameter = Macros.Prefix + "DRAWRECT" + " " });
                  src.Items.Add(new RibbonButton { Text = "D", Id = "btnD", CommandParameter = ("^C^C_" + "DRAWLABEL ") });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """);
        Assert.Equal(new[] { "DRAWCIRCLE", "DRAWGRID", "DRAWRECT", "DRAWLABEL" }, items.Select(i => i.Command));
        Assert.Equal(new[] { "^C^C_DRAWCIRCLE ", "^C^C_DRAWGRID ", "^C^C_DRAWRECT", "^C^C_DRAWLABEL" }, items.Select(i => i.Macro));
    }

    [Fact]
    public void A_macro_handed_to_a_factory_method_is_read_through_its_parameter()
    {
        var items = Items("""
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tabs {
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "Draw", Id = "TAB_Draw" };
                  tab.Panels.Add(Only());
                  return tab;
                }
                static RibbonButton Button(string id, string label, string macro) {
                  var b = new RibbonButton();
                  b.Id = id;
                  b.Text = label;
                  b.CommandParameter = macro;
                  return b;
                }
                static RibbonPanel Only() {
                  var src = new RibbonPanelSource { Title = "Shapes" };
                  src.Items.Add(Button("btnOne", "One", "^C^C_FIRSTCMD "));
                  src.Items.Add(Button("btnTwo", "Two", "^C^C_SECONDCMD "));
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """);
        Assert.Equal(new[] { "FIRSTCMD", "SECONDCMD" }, items.Select(i => i.Command));
        Assert.Equal(new[] { "btnOne", "btnTwo" }, items.Select(i => i.Id));
    }

    [Fact]
    public void A_macro_set_after_the_button_was_made_by_a_factory_is_read()
    {
        var items = Items("""
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tools {
                public static RibbonButton Make(string label, string key) {
                  return new RibbonButton { Text = label, Id = key };
                }
              }
              public class Tabs {
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "Draw", Id = "TAB_Draw" };
                  tab.Panels.Add(Only());
                  return tab;
                }
                static RibbonPanel Only() {
                  var src = new RibbonPanelSource { Title = "Shapes" };
                  var hoe = Tools.Make("Hoe", "btnHoe");
                  hoe.CommandParameter = "^C^C_BREAKSOIL ";
                  src.Items.Add(hoe);
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """);
        var b = Assert.Single(items);
        Assert.Equal(("btnHoe", "BREAKSOIL", "^C^C_BREAKSOIL "), (b.Id, b.Command, b.Macro));
    }

    [Fact]
    public void VB_buttons_read_a_literal_macro_too()
    {
        var items = Items("""
            Imports Autodesk.Windows
            Namespace Demo.Ui
              Public Class Tabs
                Public Shared Function Make() As RibbonTab
                  Dim tab As New RibbonTab()
                  tab.Title = "Draw"
                  tab.Id = "TAB_Draw"
                  tab.Panels.Add(Only())
                  Return tab
                End Function
                Private Const Circle As String = "^C^C_VBCIRCLE "
                Private Shared Function Only() As RibbonPanel
                  Dim src As New RibbonPanelSource()
                  src.Title = "Shapes"
                  Dim a As New RibbonButton()
                  a.Id = "btnVbLine"
                  a.Text = "Line"
                  a.CommandParameter = "^C^C_VBLINE " & "1"
                  src.Items.Add(a)
                  Dim b As New RibbonButton()
                  b.Id = "btnVbCircle"
                  b.Text = "Circle"
                  b.CommandParameter = Circle
                  src.Items.Add(b)
                  Dim p As New RibbonPanel()
                  p.Source = src
                  Return p
                End Function
              End Class
            End Namespace
            """, Lang.VbNet);
        Assert.Equal(new[] { "btnVbLine", "btnVbCircle" }, items.Select(i => i.Id));
        Assert.Equal(new[] { "VBLINE", "VBCIRCLE" }, items.Select(i => i.Command));
        Assert.Equal(new[] { "^C^C_VBLINE 1", "^C^C_VBCIRCLE " }, items.Select(i => i.Macro));
    }

    [Fact]
    public void A_handler_object_as_the_parameter_keeps_the_handler_logic()
    {
        const string ribbon = """
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tabs {
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "T", Id = "TAB_T" };
                  tab.Panels.Add(Only());
                  return tab;
                }
                static RibbonPanel Only() {
                  var src = new RibbonPanelSource { Title = "P" };
                  src.Items.Add(new RibbonButton { Text = "Go", Id = "btnGo", CommandParameter = new Handler("Demo.Asm", "Demo.Ui.Actions", "Go") });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
              public static class Actions { public static void Go() { } }
            }
            """;
        var b = Assert.Single(Items(ribbon));
        Assert.Equal("Demo.Ui.Actions.Go", b.HandlerMethod);
        Assert.Null(b.Macro);
        Assert.Null(b.Command);
    }

    [Fact]
    public void An_interpolated_or_computed_macro_is_ignored()
    {
        var items = Items("""
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tabs {
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "Draw", Id = "TAB_Draw" };
                  tab.Panels.Add(Only(Pick()));
                  return tab;
                }
                static RibbonPanel Only(string name) {
                  var src = new RibbonPanelSource { Title = "Shapes" };
                  src.Items.Add(new RibbonButton { Text = "A", Id = "btnA", CommandHandler = new SendHandler(), CommandParameter = $"^C^C_{name} " });
                  src.Items.Add(new RibbonButton { Text = "B", Id = "btnB", CommandHandler = new SendHandler(), CommandParameter = string.Format("^C^C_{0} ", name) });
                  src.Items.Add(new RibbonButton { Text = "C", Id = "btnC", CommandHandler = new SendHandler(), CommandParameter = "^C^C_" + name });
                  src.Items.Add(new RibbonButton { Text = "D", Id = "btnD", CommandHandler = new SendHandler(), CommandParameter = Macro.For(name) });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            """ + SendingHandler + "}");
        Assert.All(items, i => Assert.Null(i.Macro));
        // so the Id rule stands, as before
        Assert.Equal(new[] { "BTNA", "BTNB", "BTNC", "BTND" }, items.Select(i => i.Command));
    }

    [Fact]
    public void The_button_itself_as_the_parameter_is_not_a_macro()
    {
        // the pattern of samples/Retrace.Samples: b.CommandParameter = b
        var items = Items("""
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tabs {
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "Draw", Id = "TAB_Draw" };
                  tab.Panels.Add(Only());
                  return tab;
                }
                static RibbonButton Button(string command, string label) {
                  var b = new RibbonButton();
                  b.Id = command;
                  b.Text = label;
                  b.CommandHandler = new SendHandler();
                  b.CommandParameter = b;
                  return b;
                }
                static RibbonPanel Only() {
                  var src = new RibbonPanelSource { Title = "Shapes" };
                  src.Items.Add(Button("DRAWLINE", "Line"));
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            """ + SendingHandler + "}");
        var b = Assert.Single(items);
        Assert.Null(b.Macro);
        Assert.Equal("DRAWLINE", b.Command);
    }

    [Fact]
    public void Retraces_own_tab_buttons_all_carry_their_macro()
    {
        // RetraceTab.cs and the sample tab are read by their tests (CommandNamesTests); this is the shape they use: the macro goes in
        // through the factory's parameter, and the folder buttons name the command in the macro, not in the Id.
        var items = Items("""
            using Autodesk.Windows;
            namespace Retrace.AutoCad {
              public static class RetraceTab {
                public static RibbonTab Build() {
                  var tab = new RibbonTab();
                  tab.Id = "RETRACE_TAB";
                  tab.Title = "Retrace";
                  tab.Panels.Add(OpenPanel());
                  return tab;
                }
                static RibbonPanel OpenPanel() {
                  var src = new RibbonPanelSource();
                  src.Title = "Open";
                  src.Items.Add(Button("RETRACEFOLDER_TESTS", "Tests", "open", "^C^C_RETRACEFOLDER Tests ", "Open the tests folder."));
                  src.Items.Add(Button("RETRACEFOLDER_DOCS", "Docs", "open", "^C^C_RETRACEFOLDER Docs ", "Open the docs folder."));
                  var panel = new RibbonPanel();
                  panel.Source = src;
                  return panel;
                }
                static RibbonButton Button(string id, string label, string icon, string macro, string tip) {
                  var b = new RibbonButton();
                  b.Id = id;
                  b.Text = label;
                  b.ToolTip = tip;
                  b.CommandParameter = macro;
                  b.CommandHandler = new RibbonCommandHandler();
                  return b;
                }
              }
              public sealed class RibbonCommandHandler {
                public void Execute(object parameter) { doc.SendStringToExecute(macro, true, false, false); }
              }
            }
            """);
        Assert.Equal(new[] { "RETRACEFOLDER", "RETRACEFOLDER" }, items.Select(i => i.Command));
        Assert.Equal(new[] { "^C^C_RETRACEFOLDER Tests ", "^C^C_RETRACEFOLDER Docs " }, items.Select(i => i.Macro));
    }

    // The handler reads the parameter as a key and runs a method: it sends no command.
    private const string KeyHandler = """
        public class KeyHandler : System.Windows.Input.ICommand {
          public void Execute(object p) { switch (((RibbonButton)p).CommandParameter as string) { case "Grid": Grids.Draw(); break; } }
        }
        """;

    [Fact]
    public void Plain_text_is_a_macro_only_when_it_reads_as_one_or_the_handler_sends_commands()
    {
        var items = Items("""
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tabs {
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "Draw", Id = "TAB_Draw" };
                  tab.Panels.Add(Only());
                  return tab;
                }
                static RibbonPanel Only() {
                  var src = new RibbonPanelSource { Title = "Grids" };
                  src.Items.Add(new RibbonButton { Text = "Key", Id = "btnKey", CommandHandler = new KeyHandler(), CommandParameter = "Grid" });
                  src.Items.Add(new RibbonButton { Text = "Sent", Id = "btnSent", CommandHandler = new SendHandler(), CommandParameter = "Grid" });
                  src.Items.Add(new RibbonButton { Text = "Macro", Id = "btnMacro", CommandHandler = new KeyHandler(), CommandParameter = "DRAWGRID " });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            """ + KeyHandler + SendingHandler + "}");
        Assert.Equal((null, null), (items[0].Macro, items[0].Command));          // a key: no macro, no made-up command GRID
        Assert.Equal(("Grid", "GRID"), (items[1].Macro, items[1].Command));      // the handler sends it
        Assert.Equal(("DRAWGRID ", "DRAWGRID"), (items[2].Macro, items[2].Command));   // the space that ends a command: a macro
    }

    [Fact]
    public void A_lisp_only_macro_replaces_the_guess_from_the_id()
    {
        var items = Items("""
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tabs {
                public static RibbonTab Make() {
                  var tab = new RibbonTab { Title = "Draw", Id = "TAB_Draw" };
                  tab.Panels.Add(Only());
                  return tab;
                }
                static RibbonPanel Only() {
                  var src = new RibbonPanelSource { Title = "Tools" };
                  src.Items.Add(new RibbonButton { Text = "Tool", Id = "ID_MyTool", CommandHandler = new SendHandler(), CommandParameter = "^C^C(mytool) " });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            """ + SendingHandler + "}");
        var b = Assert.Single(items);
        Assert.Equal("^C^C(mytool) ", b.Macro);
        Assert.Null(b.Command);   // the handler sends the macro, not the Id: there is no command ID_MYTOOL
    }
}
