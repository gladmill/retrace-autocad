namespace Retrace.Tests;

public class InputScannerTests
{
    [Theory]
    [InlineData("service-key.txt", true)]
    [InlineData("api_key.json", true)]
    [InlineData(".env", true)]
    [InlineData("Keyboard.cs", false)]
    [InlineData("MonkeyPatch.cs", false)]
    [InlineData("Commands.cs", false)]
    public void SecretNames(string name, bool secret) => Assert.Equal(secret, InputScanner.LooksSecret(name));

    [Fact]
    public void AssignsFilesToNearestProjectAndSkipsSecrets()
    {
        var root = Directory.CreateTempSubdirectory("retrace-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Plugin", "Tabs"));
            Directory.CreateDirectory(Path.Combine(root, "Plugin", "bin"));
            File.WriteAllText(Path.Combine(root, "Plugin", "Plugin.csproj"), "<Project/>");
            File.WriteAllText(Path.Combine(root, "Plugin", "Tabs", "Tab.cs"), "class Tab {}");
            File.WriteAllText(Path.Combine(root, "Plugin", "bin", "Gen.cs"), "class Gen {}");
            File.WriteAllText(Path.Combine(root, "my-key.txt"), "SECRET");
            File.WriteAllText(Path.Combine(root, "acad.dcl"), "x : dialog {}");

            var m = InputScanner.Scan(root);

            var tab = Assert.Single(m.Files, f => f.RelPath == "Plugin/Tabs/Tab.cs");
            Assert.Equal("Plugin", tab.Project);
            Assert.DoesNotContain(m.Files, f => f.RelPath.Contains("bin/"));
            Assert.DoesNotContain(m.Files, f => f.Text.Contains("SECRET"));
            Assert.Contains(m.Skipped, s => s.StartsWith("my-key.txt"));
            Assert.Equal("(root)", Assert.Single(m.Files, f => f.Lang == Lang.Dcl).Project);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Lisp_files_are_read_only_when_asked_and_compiled_ones_are_listed_but_never_read()
    {
        var root = Directory.CreateTempSubdirectory("retrace-test-lisp").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "lisp"));
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            Directory.CreateDirectory(Path.Combine(root, "net"));
            File.WriteAllText(Path.Combine(root, "lisp", "tools.lsp"), "(defun c:tool () 1)");
            File.WriteAllText(Path.Combine(root, "lisp", "TOOLS.MNL"), "(defun c:menu () 1)");
            File.WriteAllText(Path.Combine(root, "lisp", "tools.fas"), "COMPILED");
            File.WriteAllText(Path.Combine(root, "lisp", "pack.vlx"), "COMPILED");
            File.WriteAllText(Path.Combine(root, "lisp", "api-key.lsp"), "(setq k \"SECRET\")");
            File.WriteAllText(Path.Combine(root, "bin", "gen.lsp"), "(defun c:gen () 1)");
            File.WriteAllText(Path.Combine(root, "net", "Cmds.cs"), "class C {}");

            // Build docs: no LISP at all
            var docs = InputScanner.Scan(root);
            Assert.Equal(new[] { "net/Cmds.cs" }, docs.Files.Select(f => f.RelPath));

            var m = InputScanner.Scan(root, lisp: true);
            var byName = m.Files.ToDictionary(f => f.RelPath);
            Assert.Equal(Lang.Lisp, byName["lisp/tools.lsp"].Lang);
            Assert.Equal("(defun c:tool () 1)", byName["lisp/tools.lsp"].Text);
            Assert.Equal("lisp", byName["lisp/tools.lsp"].Project);
            Assert.Equal(Lang.Lisp, byName["lisp/TOOLS.MNL"].Lang);   // the menu LISP of a .cuix of the same name
            Assert.Equal((Lang.Other, ""), (byName["lisp/tools.fas"].Lang, byName["lisp/tools.fas"].Text));
            Assert.Equal((Lang.Other, ""), (byName["lisp/pack.vlx"].Lang, byName["lisp/pack.vlx"].Text));
            Assert.DoesNotContain("lisp/api-key.lsp", byName.Keys);   // the name looks like a secret: never opened
            Assert.Contains(m.Skipped, s => s.StartsWith("lisp/api-key.lsp"));
            Assert.DoesNotContain(m.Files, f => f.Text.Contains("SECRET") || f.Text.Contains("COMPILED"));
            Assert.DoesNotContain("bin/gen.lsp", byName.Keys);        // the usual skipped folders
            Assert.Equal(Lang.CSharp, byName["net/Cmds.cs"].Lang);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_cancelled_token_stops_a_scan_between_files()
    {
        var root = Directory.CreateTempSubdirectory("retrace-test-scan-cancel").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "a.cs"), "class A {}");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => InputScanner.Scan(root, ct: cts.Token));
            Assert.Single(InputScanner.Scan(root).Files);   // and without a token it reads as before
        }
        finally { Directory.Delete(root, true); }
    }
}
