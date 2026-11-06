using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace AcadStatus.Tests;

/// <summary>Core/ holds the logic the tests reach; it must stay free of AutoCAD, WinForms and WPF, and safe inside AutoCAD's process.</summary>
public class LayerRulesTests
{
    static string Root([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    static string[] CoreFiles() => Directory.GetFiles(Path.Combine(Root(), "src", "AcadStatus", "Core"), "*.cs", SearchOption.AllDirectories);

    static string Code(string file)
    {
        // comments and string contents do not count
        var text = File.ReadAllText(file);
        text = Regex.Replace(text, "//[^\n]*", "");
        text = Regex.Replace(text, "/\\*.*?\\*/", "", RegexOptions.Singleline);
        text = Regex.Replace(text, "@\"(?:[^\"]|\"\")*\"", "\"\"");
        text = Regex.Replace(text, "\"(?:[^\"\\\\\n]|\\\\.)*\"", "\"\"");
        return text;
    }

    [Fact]
    public void CoreHasFiles() => Assert.True(CoreFiles().Length >= 8);

    [Theory]
    [InlineData("Autodesk\\.")]
    [InlineData("System\\.Windows")]
    [InlineData("System\\.Drawing")]
    [InlineData("Microsoft\\.Win32")]
    [InlineData("DllImport")]
    public void CoreDoesNotUseAutoCadOrWindowsUi(string pattern)
    {
        foreach (var f in CoreFiles())
            Assert.False(Regex.IsMatch(Code(f), pattern), Path.GetFileName(f) + " uses " + pattern);
    }

    [Theory]
    [InlineData("Console\\.")]
    [InlineData("Environment\\.Exit")]
    [InlineData("Process\\.Start")]
    [InlineData("AppDomain\\.SetData")]
    [InlineData("Thread\\.Sleep\\(\\s*[0-9]{3,}")]   // a UI thread never sleeps for long
    public void CoreIsSafeInsideAutoCad(string pattern)
    {
        foreach (var f in CoreFiles())
            Assert.False(Regex.IsMatch(Code(f), pattern), Path.GetFileName(f) + " uses " + pattern);
    }

    [Fact]
    public void CoreNeverBuildsARegexWithoutATimeout()
    {
        // an unbounded match on the UI thread could hang AutoCAD: Core uses no Regex at all
        foreach (var f in CoreFiles())
            Assert.False(Regex.IsMatch(Code(f), "\\bRegex\\b"), Path.GetFileName(f));
    }

    [Fact]
    public void CoreFilesDeclareTheirUsingsBecauseTheTestsCompileThemWithoutImplicitOnes()
    {
        foreach (var f in CoreFiles())
            Assert.Contains("namespace AcadStatus.Core;", File.ReadAllText(f));
    }

    [Fact]
    public void ThePluginReferencesAutoCadWithoutCopyingItsDlls()
    {
        var csproj = File.ReadAllText(Path.Combine(Root(), "src", "AcadStatus", "AcadStatus.csproj"));
        Assert.Matches("PackageReference Include=\"AutoCAD\\.NET\" Version=\"25\\.1\\.0\" ExcludeAssets=\"runtime\"", csproj);
        Assert.Contains("net8.0-windows", csproj);
        Assert.Contains("<PlatformTarget>x64</PlatformTarget>", csproj);
        Assert.Contains("<EnableWindowsTargeting>true</EnableWindowsTargeting>", csproj);
    }

    [Fact]
    public void TheWriterNeverTouchesADialog()
    {
        // read-only look at the dialog: none of the messages or calls that change or press anything
        var scan = File.ReadAllText(Path.Combine(Root(), "src", "AcadStatus", "AutoCad", "DialogScan.cs"));
        var code = Regex.Replace(scan, "//[^\n]*", "");
        foreach (var banned in new[] { "PostMessage", "SendMessage", "BM_CLICK", "WM_CLOSE", "EndDialog", "DestroyWindow", "SetForegroundWindow", "keybd_event", "mouse_event", "SendInput", "SetWindowText", "ShowWindow" })
            Assert.DoesNotContain(banned, code);
    }

    [Fact]
    public void ThePaneKeepsToWhatAutoCad2026AcceptsOnAStatusBarPane()
    {
        // Found in the VM test: MinimumWidth / MaximumWidth raise a modal ".NET Assertion Failed" dialog, and a pane that is not a
        // Command pane never raises MouseDown.
        var pane = Code(Path.Combine(Root(), "src", "AcadStatus", "AutoCad", "StatusPane.cs"));
        Assert.DoesNotMatch("(Minimum|Maximum)Width", pane);
        Assert.Contains("PaneStyles.Command", pane);
        Assert.DoesNotContain("PaneStyles.Normal", pane);
    }

    [Fact]
    public void NoDatabaseTransactionsInThePlugin()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(Root(), "src", "AcadStatus"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            var code = Code(f);
            Assert.DoesNotContain("StartTransaction", code);
            Assert.DoesNotContain("LockDocument", code);
            Assert.DoesNotContain("TransactionManager", code);
        }
    }
}
