using Retrace.Engine.Recordings;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). The test list of -RETRACESUITE.
public static class SuiteTestList
{
    /// <summary>Every test of the tests folder, oldest first (a suite runs in the order the tests were recorded).</summary>
    public static List<string> All(string testsRoot) => TestFolders.FindRecordings(testsRoot).AsEnumerable().Reverse().ToList();

    /// <summary>The recordings for what was typed: empty or `all` = <see cref="All"/>; else the names, paths and `@file` of <see cref="TdocTestList"/>
    /// in the order given. A name that finds nothing goes to <paramref name="problems"/>. A duplicate runs once.</summary>
    public static List<string> FromText(string? text, string testsRoot, out List<string> problems)
    {
        var names = TdocTestList.Tokens(text, out var readError);
        List<string> found;
        if (readError == null && (names.Count == 0 || (names.Count == 1 && names[0].Equals("all", StringComparison.OrdinalIgnoreCase)))) { problems = []; return All(testsRoot); }
        found = TdocTestList.Resolve(names.Where(n => !n.Equals("all", StringComparison.OrdinalIgnoreCase)), testsRoot, out problems);
        if (names.Any(n => n.Equals("all", StringComparison.OrdinalIgnoreCase)))
            found = All(testsRoot).Concat(found).ToList();
        if (readError != null) problems.Insert(0, readError);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
