using Retrace.Core;

namespace Retrace.Tests;

/// <summary>-RETRACETDOC: the list of tests typed at the command line, resolved against a synthetic Tests folder.</summary>
public class TdocTestListTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("retrace-test-tdoc").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    string Test(string folder, string started)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var rec = Path.Combine(dir, "recording.jsonl");
        File.WriteAllText(rec, $$"""{"type":"header","schema":1,"name":"{{folder}}","started":"{{started}}"}""" + "\n");
        return rec;
    }

    [Fact]
    public void Names_are_split_at_semicolons_and_line_breaks_and_trimmed()
    {
        var t = TdocTestList.Tokens(" a ; \"b c\" ;;\nd\r\n", out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "a", "b c", "d" }, t);
        Assert.Empty(TdocTestList.Tokens(null, out _));
        Assert.Empty(TdocTestList.Tokens(" ; ", out _));
    }

    [Fact]
    public void An_at_file_is_replaced_by_its_lines_without_blanks_and_comments()
    {
        var f = Path.Combine(_root, "list.txt");
        File.WriteAllText(f, "# the basics\nfirst\n\n  \"second\"  \n@nested\n");
        var t = TdocTestList.Tokens("x;@" + f + ";y", out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "x", "first", "second", "@nested", "y" }, t);   // a file inside a file is not read
    }

    [Fact]
    public void A_missing_at_file_is_an_error_and_the_other_names_stay()
    {
        var t = TdocTestList.Tokens("x;@" + Path.Combine(_root, "nope.txt"), out var err);
        Assert.NotNull(err);
        Assert.Contains("nope.txt", err);
        Assert.Equal(new[] { "x" }, t);
    }

    [Fact]
    public void A_name_finds_the_test_by_folder_name_by_name_without_date_or_as_last()
    {
        var older = Test("2026-10-01_0900_grid", "2026-10-01T09:00:00");
        var newer = Test("2026-10-02_0900_grid", "2026-10-02T09:00:00");
        var block = Test("2026-10-03_0900_block", "2026-10-03T09:00:00");
        var found = TdocTestList.Resolve(["2026-10-01_0900_GRID", "grid", "block", "last"], _root, out var problems);
        Assert.Empty(problems);
        Assert.Equal(new[] { older, newer, block, block }, found);   // "grid" = the newest of that name; the order given is kept
    }

    [Fact]
    public void A_path_to_the_folder_or_the_recording_or_relative_to_the_tests_folder_works()
    {
        var rec = Test("2026-10-01_0900_grid", "2026-10-01T09:00:00");
        var dir = Path.GetDirectoryName(rec)!;
        var found = TdocTestList.Resolve([dir, rec, Path.Combine("2026-10-01_0900_grid", "recording.jsonl")], _root, out var problems);
        Assert.Empty(problems);
        Assert.Equal(new[] { rec, rec, rec }, found);
    }

    [Fact]
    public void A_name_that_finds_nothing_is_reported_and_leaves_the_others()
    {
        var rec = Test("2026-10-01_0900_grid", "2026-10-01T09:00:00");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));   // a folder without a recording is not a test
        var found = TdocTestList.Resolve(["grid", "ghost", "empty", Path.Combine(_root, "nowhere")], _root, out var problems);
        Assert.Equal(new[] { rec }, found);
        Assert.Equal(3, problems.Count);
        Assert.Contains("ghost", problems[0]);
        Assert.All(problems, p => Assert.Contains(_root, p));
    }

    [Fact]
    public void Last_with_no_tests_is_a_problem()
    {
        var found = TdocTestList.Resolve(["last"], _root, out var problems);
        Assert.Empty(found);
        Assert.Single(problems);
    }
}
