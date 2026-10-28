namespace Retrace.Tests;

/// <summary>The prompt window's recent places and the prompt style setting.</summary>
public class RecentPlacesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("retrace-recent").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Newest_first_once_at_most_ten_and_kept_per_kind()
    {
        var r = RecentPlaces.Load(_root);
        for (int i = 0; i < 12; i++) r.Add("folder", $@"C:\f{i}");
        r.Add("folder", @"c:\F5");   // again, other case: moves to the front
        r.Add("recording", @"C:\t\recording.jsonl");
        var again = RecentPlaces.Load(_root);
        var folders = again.List("folder", _ => true);
        Assert.Equal(RecentPlaces.Max, folders.Count);
        Assert.Equal(@"c:\F5", folders[0]);
        Assert.Equal(@"C:\f11", folders[1]);
        Assert.DoesNotContain(@"C:\f0", folders);
        Assert.Equal(new[] { @"C:\t\recording.jsonl" }, again.List("recording", _ => true));
    }

    [Fact]
    public void Places_that_are_gone_are_not_listed_and_a_broken_file_is_empty()
    {
        var r = RecentPlaces.Load(_root);
        r.Add("folder", _root);
        r.Add("folder", Path.Combine(_root, "gone"));
        Assert.Equal(new[] { _root }, RecentPlaces.Load(_root).List("folder"));
        File.WriteAllText(Paths.RecentFile(_root), "{not json");
        Assert.Empty(RecentPlaces.Load(_root).List("folder"));
        File.WriteAllText(Paths.RecentFile(_root), "{\"folder\": null, \"recording\": [null, \"" + _root.Replace("\\", "\\\\") + "\"]}");   // hand-edited
        var r2 = RecentPlaces.Load(_root);
        Assert.Empty(r2.List("folder"));
        Assert.Equal(new[] { _root }, r2.List("recording"));
        r2.Add("folder", _root);   // no crash on the list that was null
    }

    [Theory]
    [InlineData("ui", true)]
    [InlineData(" UI ", true)]
    [InlineData("commandLine", false)]
    [InlineData("", false)]
    [InlineData("window", false)]
    public void Prompt_style(string value, bool ui) => Assert.Equal(ui, new RetraceSettings { PromptStyle = value }.UiPrompts);

    [Fact]
    public void The_command_line_is_the_default() => Assert.False(new RetraceSettings().UiPrompts);
}
