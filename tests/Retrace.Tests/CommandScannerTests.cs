using Retrace.Engine.Scan.Commands;

namespace Retrace.Tests;

public class CommandScannerTests
{
    private static SourceFile F(string text, Lang lang, string name = "x.cs", string project = "p") =>
        new() { AbsPath = "/" + name, RelPath = name, Lang = lang, Project = project, Text = text };

    private static List<CommandEntry> Scan(string text, Lang lang = Lang.CSharp) =>
        CommandScanner.Scan(new[] { F(text, lang, lang == Lang.VbNet ? "x.vb" : lang == Lang.Cpp ? "x.cpp" : "x.cs") });

    [Fact]
    public void CSharpOverloads()
    {
        var r = Scan("""
            namespace N {
              public class C {
                [CommandMethod("one")]
                public void A() { }
                [CommandMethod("two", CommandFlags.Session)]
                public void B() { }
                [CommandMethod("GRP", "three", CommandFlags.Modal | CommandFlags.UsePickSet)]
                public void C1() { }
                [Autodesk.AutoCAD.Runtime.CommandMethod("GRP", "four", "LOCALFOUR", CommandFlags.Modal)]
                public void D() { }
                [LispFunction("myfn")]
                public static object E(ResultBuffer rb) { return null; }
                [Obsolete, CommandMethod("five")]
                public void F2() { }
                // [CommandMethod("gone")]
                // public void G() { }
              }
            }
            """);
        Assert.Equal(new[] { "ONE", "TWO", "THREE", "FOUR", "MYFN", "FIVE" }, r.Select(x => x.Name));
        Assert.Equal("N.C.A", r[0].Method);
        Assert.Equal("Session", r[1].Flags);
        Assert.Equal("Modal | UsePickSet", r[2].Flags);
        Assert.Equal("GRP", r[2].Group);
        Assert.Equal("LOCALFOUR", r[3].LocalName);
        Assert.Equal(EntryKind.LispFunction, r[4].Kind);
        Assert.Equal("one", r[0].Extra["declared"]);
        Assert.Equal("N.C.F2", r[5].Method);
        Assert.Equal(3, r[0].Source.Line);
    }

    [Fact]
    public void VbAttributes()
    {
        var r = Scan("""
            Imports Autodesk.AutoCAD.Runtime
            Public Class Cmds
                <CommandMethod("HELLO")> _
                Public Sub Hello()
                End Sub
                <Autodesk.AutoCAD.Runtime.CommandMethod("DEMO", "GREETTEXT", Autodesk.AutoCAD.Runtime.CommandFlags.Modal)> _
                Public Function Txt() As Integer
                    Return 1
                End Function
            End Class
            """, Lang.VbNet);
        Assert.Equal(2, r.Count);
        Assert.Equal("Cmds.Hello", r[0].Method);
        Assert.Equal("GREETTEXT", r[1].Name);
        Assert.Equal("DEMO", r[1].Group);
        Assert.Equal("Modal", r[1].Flags);
        Assert.Equal(Lang.VbNet, r[1].Lang);
    }

    [Fact]
    public void CppAddCommandWithTextAndDefines()
    {
        var r = Scan("""
            #define GRP _T("MY_GROUP")
            static void cmdOne() { }
            class Dlg { public: static void cmdRun(); };
            void Dlg::cmdRun() { }
            void init() {
                acedRegCmds->addCommand(GRP, _T("MyCmd"), _T("MyCmdLoc"), ACRX_CMD_MODAL, cmdOne);
                cmdStack->addCommand(appname, L"Other", ACRX_T("OtherLoc"), ACRX_CMD_TRANSPARENT | ACRX_CMD_USEPICKSET, &Dlg::cmdRun);
                // acedRegCmds->addCommand(_T("G"), _T("Gone"), _T("Gone"), 0, cmdOne);
            }
            """, Lang.Cpp);
        Assert.Equal(2, r.Count);
        Assert.Equal("MYCMD", r[0].Name);
        Assert.Equal("MY_GROUP", r[0].Group);
        Assert.Equal("MyCmdLoc", r[0].LocalName);
        Assert.Equal("cmdOne", r[0].Method);
        Assert.Equal(EntryKind.ArxAddCommand, r[0].Kind);
        Assert.Equal("OTHER", r[1].Name);
        Assert.Equal("appname", r[1].Group);
        Assert.Equal("Dlg::cmdRun", r[1].Method);
        Assert.Equal("ACRX_CMD_TRANSPARENT | ACRX_CMD_USEPICKSET", r[1].Flags);
    }

    [Fact]
    public void AcedMacros()
    {
        var r = Scan("""
            ACED_ARXCOMMAND_ENTRY_AUTO(CMyApp, grp, _DOIT, DOIT, ACRX_CMD_TRANSPARENT, NULL)
            ACED_ADSSYMBOL_ENTRY_AUTO(CMyApp, myfn, false)
            """, Lang.Cpp);
        Assert.Equal(2, r.Count);
        Assert.Equal("DOIT", r[0].Name);
        Assert.Equal("DOIT", r[0].LocalName);
        Assert.Equal("CMyApp::grp_DOIT", r[0].Method);
        Assert.Equal(EntryKind.ArxCommandEntry, r[0].Kind);
        Assert.Equal(EntryKind.LispFunction, r[1].Kind);
    }

    [Fact]
    public void IgnoresCommentedOutCommands()
    {
        var r = Scan("""
            class C {
              /* [CommandMethod("X")] void Y() {} */
              [CommandMethod("A")] void M() { }
            }
            """);
        Assert.Single(r);
        Assert.Equal("A", r[0].Name);
    }
}
