using Retrace.Engine.Util;

namespace Retrace.Tests;

public class CodeTextTests
{
    private static CodeText Code(string text, Lang lang = Lang.CSharp) =>
        new(new SourceFile { AbsPath = "/x", RelPath = "x", Lang = lang, Project = "p", Text = text });

    [Fact]
    public void FindsCSharpMethodsWithClassAndLine()
    {
        var c = Code("""
            namespace A.B
            {
                public class Cmds
                {
                    // public void Commented() { }
                    [CommandMethod("HI")]
                    public static void Hello(string s = "x)")
                    {
                        if (s != null) { Foo(); }
                    }
                    private int Two() => 2;
                }
            }
            """);
        var ms = c.Methods();
        Assert.Equal(new[] { "A.B.Cmds.Hello", "A.B.Cmds.Two" }, ms.Select(m => m.QualifiedName));
        Assert.Equal(7, ms[0].Line);
    }

    [Fact]
    public void FindsVbAndCppFunctions()
    {
        var vb = Code("""
            Namespace N
            Public Class C
                ' Sub Fake()
                <CommandMethod("HELLO")> _
                Public Sub Hello()
                End Sub
            End Class
            End Namespace
            """, Lang.VbNet);
        Assert.Equal("N.C.Hello", Assert.Single(vb.Methods()).QualifiedName);

        var cpp = Code("""
            static void helloCmd()
            {
                acutPrintf(_T("hi"));
            }
            void CMyDlg::OnOK()
            {
                if (x) { y(); }
            }
            """, Lang.Cpp);
        Assert.Equal(new[] { "helloCmd", "CMyDlg::OnOK" }, cpp.Methods().Select(m => m.QualifiedName));
    }

    [Theory]
    [InlineData("\"abc\"", "abc")]
    [InlineData("_T(\"DEMO\")", "DEMO")]
    [InlineData("L\"wide\"", "wide")]
    [InlineData("@\"C:\\x\"", "C:\\x")]
    [InlineData("name", null)]
    public void Literals(string expr, string? expected) => Assert.Equal(expected, CodeText.Literal(expr));

    [Fact]
    public void SplitsTopLevelArgs() =>
        Assert.Equal(new[] { "\"a,b\"", "F(1, 2)", "new[] { 3, 4 }" }, CodeText.SplitArgs("\"a,b\", F(1, 2), new[] { 3, 4 }"));
}

public class CodeTextPerformanceTests
{
    private static CodeText Code(string text, Lang lang) =>
        new(new SourceFile { AbsPath = "/x", RelPath = "x", Lang = lang, Project = "p", Text = text });

    [Fact(Timeout = 5000)]
    public async Task VbAssemblyAttributesDoNotBacktrack() => await Task.Run(() =>
    {
        var text = string.Concat(Enumerable.Repeat("<Assembly: AssemblyTitle(\"x\")> \n", 12)) + "Public Class C\nSub A()\nEnd Sub\nEnd Class\n";
        Assert.Single(Code(text, Lang.VbNet).Methods());
    });

    [Fact(Timeout = 5000)]
    public async Task LongCppCallLineDoesNotBacktrack() => await Task.Run(() =>
    {
        var text = "void initApp()\n{\n    acedRegCmds->addCommand(_T(\"GARDEN_TOOLS\"), _T(\"WATERBEDS\"), _T(\"WATER_THE_BEDS\"), ACRX_CMD_TRANSPARENT, waterBeds);\n}\n";
        Assert.Equal("initApp", Assert.Single(Code(text, Lang.Cpp).Methods()).QualifiedName);
    });
}
