using Retrace.Engine.Scan.Commands;

namespace Retrace.Tests;

public class FlowAnalyzerTests
{
    private static SourceFile F(string text, Lang lang, string name) =>
        new() { AbsPath = "/" + name, RelPath = name, Lang = lang, Project = "p", Text = text };

    private static List<FlowStep> Flow(string method, int depth, params (string text, Lang lang, string name)[] files) =>
        new FlowAnalyzer(files.Select(f => F(f.text, f.lang, f.name)).ToList(), depth).Analyze(method);

    [Fact]
    public void PromptViaOptionsVariableWithRejectMessage()
    {
        var steps = Flow("N.C.Run", 4, ("""
            namespace N {
              public class C {
                public void Run() {
                  var ed = Application.DocumentManager.MdiActiveDocument.Editor;
                  var opts = new PromptEntityOptions("\nPick a block to tag: ");
                  opts.SetRejectMessage("That is not a block.");
                  opts.AddAllowedClass(typeof(BlockReference), true);
                  var res = ed.GetEntity(opts);
                  var k = new PromptKeywordOptions("\nProceed?");
                  k.Keywords.Add("Yes");
                  k.Keywords.Add("No");
                  ed.GetKeywords(k);
                  ed.WriteMessage($"\nDone {res.Status}");
                }
              }
            }
            """, Lang.CSharp, "a.cs"));
        Assert.Equal(FlowKind.Prompt, steps[0].Kind);
        Assert.Equal("Pick a block to tag:", steps[0].Text);
        Assert.Equal("BlockReference", steps[0].Extra["allowed"]);
        Assert.Equal("That is not a block.", steps[0].Extra["reject"]);
        Assert.Equal("Proceed?", steps[1].Text);
        Assert.Equal("Yes,No", steps[1].Extra["keywords"]);
        Assert.Equal(FlowKind.Message, steps[2].Kind);
        Assert.Equal("Done {res.Status}", steps[2].Text);
        Assert.Equal(8, steps[0].Source!.Line);
    }

    [Fact]
    public void DialogViaWrapperShow()
    {
        var steps = Flow("N.Cmds.Open", 4,
            ("namespace N { class Cmds { void Open() { var dialog = new Contoso.Tools.Forms.FormOptions(); Shop.Ui.Modal.Show(dialog); } } }", Lang.CSharp, "a.cs"),
            ("namespace Shop.Ui { class Modal { static void Show(System.Windows.Forms.Form f) { Application.ShowModalDialog(f); } } }", Lang.CSharp, "b.cs"));
        var d = Assert.Single(steps, s => s.Kind == FlowKind.Dialog);
        Assert.Equal("FormOptions", d.Target);
        Assert.Equal("Contoso.Tools.Forms.FormOptions", d.Extra["type"]);
    }

    [Fact]
    public void ShowDialogOnNewWindowAndSendCommand()
    {
        var steps = Flow("N.C.Go", 2, ("""
            namespace N { class C {
              void Go() {
                new SettingsWindow().ShowDialog();
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc.SendStringToExecute("_.LINE ", true, false, false);
              } } }
            """, Lang.CSharp, "a.cs"));
        Assert.Equal("SettingsWindow", steps.First(s => s.Kind == FlowKind.Dialog).Target);
        Assert.Equal("LINE", steps.First(s => s.Kind == FlowKind.SendCommand).Target);
    }

    [Fact]
    public void FollowsCallsAndGuardsRecursion()
    {
        var steps = Flow("N.C.A", 5, ("""
            namespace N { class C {
              void A() { B(); }
              void B() { ed.WriteMessage("in B"); A(); C2(); }
              void C2() { ed.WriteMessage("in C2"); }
            } }
            """, Lang.CSharp, "a.cs"));
        Assert.Contains(steps, s => s.Kind == FlowKind.Call && s.Target == "N.C.B" && s.Depth == 0);
        Assert.Contains(steps, s => s.Kind == FlowKind.Message && s.Text == "in B" && s.Depth == 1);
        Assert.Contains(steps, s => s.Extra.ContainsKey("recursive"));
        Assert.Contains(steps, s => s.Text == "in C2" && s.Depth == 2);
    }

    [Fact]
    public void DepthLimit()
    {
        var src = ("""
            namespace N { class C {
              void A() { B(); }
              void B() { C2(); }
              void C2() { ed.WriteMessage("deep"); }
            } }
            """, Lang.CSharp, "a.cs");
        Assert.DoesNotContain(Flow("N.C.A", 1, src), s => s.Text == "deep");
        Assert.Contains(Flow("N.C.A", 2, src), s => s.Text == "deep" && s.Depth == 2);
    }

    [Fact]
    public void VbDialog()
    {
        var steps = Flow("GreetCommands.ShowGreetForm", 3, ("""
            Public Class GreetCommands
                Public Sub ShowGreetForm()
                    Dim dlg As New GreetForm()
                    Application.ShowModalDialog(dlg)
                End Sub
            End Class
            """, Lang.VbNet, "a.vb"));
        Assert.Equal("GreetForm", Assert.Single(steps).Target);
    }

    [Fact]
    public void CppDoModalAndPrompts()
    {
        var steps = Flow("cmdRun", 3, ("""
            void helper() { acutPrintf(_T("\nhello %d"), 1); }
            void cmdRun()
            {
                CMyDlg dlg(CWnd::FromHandle(adsw_acadMainWnd()));
                ads_point pt;
                if (acedGetPoint(NULL, _T("\nPick point: "), pt) != RTNORM) return;
                helper();
                dlg.DoModal();
                acedCommand(RTSTR, _T("_LINE"), RTNONE);
            }
            """, Lang.Cpp, "a.cpp"));
        Assert.Equal("Pick point:", steps[0].Text);
        Assert.Equal(FlowKind.Prompt, steps[0].Kind);
        Assert.Contains(steps, s => s.Kind == FlowKind.Message && s.Text == "hello %d" && s.Depth == 1);
        Assert.Equal("CMyDlg", steps.First(s => s.Kind == FlowKind.Dialog).Target);
        Assert.Equal("LINE", steps.First(s => s.Kind == FlowKind.SendCommand).Target);
    }

    [Fact]
    public void PaletteAndJigAndSelection()
    {
        var steps = Flow("N.C.Go", 2, ("""
            namespace N { class TrellisPaletteSet : PaletteSet { }
              class C {
                void Go() {
                  var ed = doc.Editor;
                  var ps = new TrellisPaletteSet();
                  ps.Visible = true;
                  var jig = new EllipseJig();
                  ed.Drag(jig);
                  var pso = new PromptSelectionOptions();
                  pso.MessageForAdding = "\nSelect lines: ";
                  ed.GetSelection(pso);
                } } }
            """, Lang.CSharp, "a.cs"));
        Assert.Equal(FlowKind.Palette, steps[0].Kind);
        Assert.Equal("TrellisPaletteSet", steps[0].Target);
        Assert.Single(steps, s => s.Kind == FlowKind.Palette);
        Assert.Equal("EllipseJig", steps.First(s => s.Kind == FlowKind.Jig).Target);
        Assert.Equal("Select lines:", steps.First(s => s.Kind == FlowKind.Selection).Text);
    }
}
