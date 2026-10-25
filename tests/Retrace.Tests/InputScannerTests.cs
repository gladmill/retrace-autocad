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
}
