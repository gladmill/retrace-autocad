using System.Text.Json;

namespace Retrace.Tests;

/// <summary>The Map window's folder list: every folder ever given, newest first, which are ticked, and the two options.</summary>
public class MapFoldersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("retrace-mapfolders").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private static string[] Names(MapFolders f) => f.Folders.Select(x => x.Path).ToArray();

    [Fact]
    public void A_missing_file_is_an_empty_list_with_the_defaults()
    {
        var f = MapFolders.Load(_root);
        Assert.Empty(f.Folders);
        Assert.Empty(f.Checked);
        Assert.True(f.LiveRibbon);
        Assert.False(f.AllTabs);
    }

    [Fact]
    public void Added_folders_go_to_the_top_ticked()
    {
        var f = MapFolders.Load(_root);
        f.Add(@"C:\a");
        f.Add(@"C:\b");
        f.Add(@"C:\c");
        Assert.Equal(new[] { @"C:\c", @"C:\b", @"C:\a" }, Names(f));
        Assert.All(f.Folders, x => Assert.True(x.Checked));
        Assert.Equal(new[] { @"C:\c", @"C:\b", @"C:\a" }, f.Checked);
    }

    [Fact]
    public void Adding_a_listed_folder_again_moves_it_to_the_top_ticked_with_the_spelling_just_given()
    {
        var f = MapFolders.Load(_root);
        f.Add(@"C:\a"); f.Add(@"C:\b"); f.Add(@"C:\c");
        f.SetChecked(@"C:\a", false);
        f.Add(@"c:\A");
        Assert.Equal(new[] { @"c:\A", @"C:\c", @"C:\b" }, Names(f));
        Assert.True(f.Folders[0].Checked);
    }

    [Fact]
    public void Use_puts_the_mapped_folders_first_in_the_order_given_and_unticks_all_others()
    {
        var f = MapFolders.Load(_root);
        f.Add(@"C:\old1"); f.Add(@"C:\old2"); f.Add(@"C:\x"); f.Add(@"C:\y");
        f.Use(new[] { @"C:\old2", @"C:\new", @"C:\X", @"C:\old2" });
        Assert.Equal(new[] { @"C:\old2", @"C:\new", @"C:\X", @"C:\y", @"C:\old1" }, Names(f));   // the first given ends first, a repeat counts once
        Assert.Equal(new[] { true, true, true, false, false }, f.Folders.Select(x => x.Checked));
        Assert.Equal(new[] { @"C:\old2", @"C:\new", @"C:\X" }, f.Checked);
    }

    [Fact]
    public void Use_with_nothing_unticks_every_folder_and_blank_paths_are_ignored()
    {
        var f = MapFolders.Load(_root);
        f.Add(@"C:\a"); f.Add(@"C:\b");
        f.Use(new[] { "", "  ", "\"\"" });
        Assert.Equal(new[] { @"C:\b", @"C:\a" }, Names(f));
        Assert.Empty(f.Checked);
    }

    [Fact]
    public void Remove_forgets_a_folder_and_says_whether_it_was_listed()
    {
        var f = MapFolders.Load(_root);
        f.Add(@"C:\a"); f.Add(@"C:\b");
        Assert.True(f.Remove(@"c:\A\"));
        Assert.False(f.Remove(@"C:\a"));
        Assert.Equal(new[] { @"C:\b" }, Names(f));
    }

    [Fact]
    public void SetChecked_ticks_and_unticks_one_folder_and_ignores_an_unlisted_one()
    {
        var f = MapFolders.Load(_root);
        f.Add(@"C:\a"); f.Add(@"C:\b");
        f.SetChecked(@"C:\a", false);
        f.SetChecked(@"C:\nothere", true);
        Assert.Equal(new[] { @"C:\b" }, f.Checked);
        f.SetChecked(@"C:\A", true);
        Assert.Equal(new[] { @"C:\b", @"C:\a" }, f.Checked);
        Assert.Equal(2, f.Folders.Count);
    }

    [Fact]
    public void There_is_no_limit_on_the_number_of_folders()
    {
        var f = MapFolders.Load(_root);
        for (int i = 0; i < 250; i++) f.Add($@"C:\f{i}");
        Assert.True(f.Save(out _));
        var back = MapFolders.Load(_root);
        Assert.Equal(250, back.Folders.Count);
        Assert.Equal(@"C:\f249", back.Folders[0].Path);
        Assert.Equal(@"C:\f0", back.Folders[^1].Path);
    }

    [Fact]
    public void The_list_and_the_options_round_trip_through_the_file()
    {
        var f = MapFolders.Load(_root);
        f.Add(@"C:\a"); f.Add(@"D:\ü ö\b");
        f.SetChecked(@"C:\a", false);
        f.LiveRibbon = false;
        f.AllTabs = true;
        Assert.True(f.Save(out var error), error);
        Assert.Equal("", error);

        var back = MapFolders.Load(_root);
        Assert.Equal(new[] { (@"D:\ü ö\b", true), (@"C:\a", false) }, back.Folders.Select(x => (x.Path, x.Checked)));
        Assert.False(back.LiveRibbon);
        Assert.True(back.AllTabs);
        Assert.False(File.Exists(Retrace.Core.Paths.MapFoldersFile(_root) + ".tmp"));
    }

    [Fact]
    public void The_file_has_the_documented_shape_and_stays_readable()
    {
        var f = MapFolders.Load(_root);
        f.Add(@"C:\src\ü");
        f.Save(out _);
        var text = File.ReadAllText(Retrace.Core.Paths.MapFoldersFile(_root));
        Assert.Contains("ü", text);   // not \u00FC
        using var doc = JsonDocument.Parse(text);
        var first = doc.RootElement.GetProperty("folders")[0];
        Assert.Equal(@"C:\src\ü", first.GetProperty("path").GetString());
        Assert.True(first.GetProperty("checked").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("liveRibbon").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("allTabs").GetBoolean());
    }

    [Fact]
    public void Save_creates_the_root_folder()
    {
        var root = Path.Combine(_root, "not", "there", "yet");
        var f = MapFolders.Load(root);
        f.Add(@"C:\a");
        Assert.True(f.Save(out _));
        Assert.True(File.Exists(Retrace.Core.Paths.MapFoldersFile(root)));
    }

    [Fact]
    public void Save_says_why_when_the_file_cannot_be_written()
    {
        // the root is a file: its folder cannot be made
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "x");
        var f = MapFolders.Load(Path.Combine(blocker, "sub"));
        f.Add(@"C:\a");
        Assert.False(f.Save(out var error));
        Assert.NotEmpty(error);
        // the list in memory is still there
        Assert.Equal(new[] { @"C:\a" }, Names(f));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"folders\"")]
    [InlineData("{\"folders\": 5}")]
    [InlineData("{\"folders\": null}")]
    public void A_broken_file_is_an_empty_list_with_the_defaults(string json)
    {
        File.WriteAllText(Retrace.Core.Paths.MapFoldersFile(_root), json);
        var f = MapFolders.Load(_root);
        Assert.Empty(f.Folders);
        Assert.True(f.LiveRibbon);
        Assert.False(f.AllTabs);
        f.Add(@"C:\a");   // and it can be used and saved over
        Assert.True(f.Save(out _));
        Assert.Single(MapFolders.Load(_root).Folders);
    }

    [Fact]
    public void Broken_entries_are_skipped_and_the_rest_is_kept()
    {
        File.WriteAllText(Retrace.Core.Paths.MapFoldersFile(_root), """
            { "folders": [
                null, 7, "C:\\str", [],
                { "path": null, "checked": true },
                { "path": "", "checked": true },
                { "path": "   ", "checked": true },
                { "path": 5 },
                { "checked": true },
                { "path": "C:\\keep1", "checked": true },
                { "path": "\"C:\\keep2\\\"" },
                { "path": "C:\\KEEP1", "checked": false },
                { "path": "C:\\keep3", "checked": "yes" },
                { "path": "C:\\keep4", "checked": true, "extra": 1 },
              ],
              "liveRibbon": "maybe", "allTabs": true }
            """);
        var f = MapFolders.Load(_root);
        Assert.Equal(new[] { (@"C:\keep1", true), (@"C:\keep2", false), (@"C:\keep3", false), (@"C:\keep4", true) }, f.Folders.Select(x => (x.Path, x.Checked)));
        Assert.True(f.LiveRibbon);   // not a boolean: the default
        Assert.True(f.AllTabs);
    }

    [Fact]
    public void Two_paths_that_differ_only_in_case_are_one_folder_and_the_first_wins()
    {
        File.WriteAllText(Retrace.Core.Paths.MapFoldersFile(_root),
            """{ "folders": [ { "path": "C:\\Src", "checked": true }, { "path": "c:\\SRC\\", "checked": false }, { "path": "C:\\other", "checked": false } ] }""");
        var f = MapFolders.Load(_root);
        Assert.Equal(new[] { (@"C:\Src", true), (@"C:\other", false) }, f.Folders.Select(x => (x.Path, x.Checked)));
    }

    [Fact]
    public void An_old_file_without_the_options_has_the_defaults()
    {
        File.WriteAllText(Retrace.Core.Paths.MapFoldersFile(_root), """{ "folders": [ { "path": "C:\\a", "checked": true } ] }""");
        var f = MapFolders.Load(_root);
        Assert.True(f.LiveRibbon);
        Assert.False(f.AllTabs);
    }

    [Fact]
    public void Comments_and_trailing_commas_in_a_hand_edited_file_are_tolerated()
    {
        File.WriteAllText(Retrace.Core.Paths.MapFoldersFile(_root), "{ // mine\n \"folders\": [ { \"path\": \"C:\\\\a\", \"checked\": true, }, ], }");
        Assert.Equal(new[] { @"C:\a" }, Names(MapFolders.Load(_root)));
    }

    [Theory]
    [InlineData(@"C:\src\", @"C:\src")]
    [InlineData("\"C:\\src\"", @"C:\src")]
    [InlineData("\"C:\\src\\\"", @"C:\src")]
    [InlineData("  \"C:\\my src\\\"  ", @"C:\my src")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\\", @"C:\")]
    [InlineData("C:/", "C:/")]
    [InlineData("C:", "C:")]
    [InlineData("\"C:\\\"", @"C:\")]
    [InlineData("/Users/x/src/", "/Users/x/src")]
    [InlineData("/Users/x/src//", "/Users/x/src")]
    [InlineData("/", "/")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    [InlineData(@"C:\a b\c", @"C:\a b\c")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("\"\"", "")]
    public void Normalize_trims_quotes_and_the_trailing_separator_but_keeps_a_drive_root(string input, string expected) =>
        Assert.Equal(expected, MapFolders.Normalize(input));

    [Fact]
    public void Paths_are_normalized_when_they_are_added()
    {
        var f = MapFolders.Load(_root);
        f.Add("\"C:\\src\\\"");
        f.Add("   ");
        f.Add("/Users/x/src/");
        Assert.Equal(new[] { "/Users/x/src", @"C:\src" }, Names(f));
        f.Add(@"C:\SRC\");   // the same folder
        Assert.Equal(new[] { @"C:\SRC", "/Users/x/src" }, Names(f));
    }

    [Fact]
    public void The_folders_property_is_a_snapshot()
    {
        var f = MapFolders.Load(_root);
        f.Add(@"C:\a");
        var before = f.Folders;
        f.Add(@"C:\b");
        Assert.Single(before);
        Assert.Equal(2, f.Folders.Count);
    }
}
