namespace Retrace.Tests;

/// <summary>Engine/ and Core/ are compiled into this test assembly and must stay free of AutoCAD, WPF and WebView2: the layer rules.</summary>
public class LayerRulesTests
{
    static readonly string[] Forbidden = { "using Autodesk", "using System.Windows", "using Microsoft.Web" };

    internal static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Retrace");
            if (Directory.Exists(Path.Combine(candidate, "Engine")) && Directory.Exists(Path.Combine(candidate, "Core"))) return candidate;
        }
        throw new DirectoryNotFoundException("src/Retrace not found above " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("Engine")]
    [InlineData("Core")]
    public void Layer_has_no_Autodesk_or_Windows_usings(string layer)
    {
        var files = Directory.GetFiles(Path.Combine(SourceRoot(), layer), "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var hits = new List<string>();
        foreach (var f in files)
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
                foreach (var bad in Forbidden)
                    if (lines[i].TrimStart().StartsWith(bad, StringComparison.Ordinal))
                        hits.Add($"{Path.GetRelativePath(SourceRoot(), f)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(hits.Count == 0, "Forbidden usings:\n" + string.Join("\n", hits));
    }

    // The engine runs inside AutoCAD: no console, no process exit, no process-wide regex timeout, and every regex goes through Rx (explicit timeout).
    static readonly System.Text.RegularExpressions.Regex[] InProcessForbidden =
    [
        new(@"\bConsole\.", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)),
        new(@"Environment\.Exit|SetData\(|Process\.Start|ProcessStartInfo", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)),
        new(@"new\s+(System\.Text\.RegularExpressions\.)?Regex\(|(?<![\w.])Regex\.(Match|Matches|IsMatch|Replace|Split)\(", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)),
    ];

    [Theory]
    [InlineData("Engine")]
    [InlineData("Core")]
    public void Layer_runs_in_process_safely(string layer)
    {
        var hits = new List<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(SourceRoot(), layer), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f) == "Rx.cs") continue;
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].Split("//")[0];
                // a Regex.Replace(..., TimeSpan) call already carries its own timeout
                if (code.Contains("TimeSpan") && !code.Contains("new Regex(")) continue;
                if (InProcessForbidden.Any(r => r.IsMatch(code))) hits.Add($"{Path.GetRelativePath(SourceRoot(), f)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(hits.Count == 0, "Not allowed inside AutoCAD (use a TextWriter / EngineResult, and Rx for regexes):\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Rx_gives_every_regex_the_shared_timeout()
    {
        Assert.Equal(RegexTimeout.Default, Rx.Create("a").MatchTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), Rx.Create("a", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).MatchTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), RegexTimeout.Default);
    }

    [Fact]
    public void A_runaway_pattern_times_out_instead_of_hanging()
    {
        var evil = Rx.Create(@"^(a+)+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
        Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() => evil.IsMatch(new string('a', 40) + "!"));
    }
}
