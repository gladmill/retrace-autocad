using Retrace.Engine.Recordings;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). The test list of -RETRACETDOC.
public static class TdocTestList
{
    /// <summary>The names in a list typed at the command line: separated by ';' (or line breaks), quotes and blanks trimmed. `@file` is replaced by the
    /// lines of that file (blank lines and lines starting with '#' skipped; a file inside a file is not read). <paramref name="error"/> says why a
    /// file could not be read.</summary>
    public static List<string> Tokens(string? text, out string? error)
    {
        error = null;
        var list = new List<string>();
        foreach (var raw in Split(text))
        {
            if (!raw.StartsWith('@')) { list.Add(raw); continue; }
            var file = raw[1..].Trim().Trim('"');
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    var t = line.Trim().Trim('"');
                    if (t.Length > 0 && !t.StartsWith('#')) list.Add(t);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = $"cannot read {file}: {ex.Message}";
            }
        }
        return list;
    }

    static IEnumerable<string> Split(string? text) =>
        (text ?? "").Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().Trim('"').Trim()).Where(x => x.Length > 0);

    /// <summary>The recording.jsonl of each name, in the order given. A name is `last` (the newest test), a test folder's name (`2026-10-10_0243_dtest`),
    /// a test's name without its date (`dtest`; the newest of that name), or a path to a test folder or its recording.jsonl (a relative one is looked up
    /// under <paramref name="testsRoot"/>). A name that finds nothing goes to <paramref name="problems"/>.</summary>
    public static List<string> Resolve(IEnumerable<string> names, string testsRoot, out List<string> problems)
    {
        problems = [];
        var found = new List<string>();
        var all = TestFolders.FindRecordings(testsRoot);
        foreach (var name in names)
        {
            var hit = One(name, testsRoot, all);
            if (hit == null) problems.Add($"no test \"{name}\" in {testsRoot}");
            else found.Add(hit);
        }
        return found;
    }

    static string? One(string name, string root, List<string> all)
    {
        if (name.Equals("last", StringComparison.OrdinalIgnoreCase)) return all.FirstOrDefault();
        foreach (var candidate in Candidates(name, root))
        {
            if (File.Exists(candidate) && TestFolders.IsRecording(candidate)) return candidate;
            var inside = Path.Combine(candidate, TestFolders.RecordingFile);
            if (Directory.Exists(candidate) && File.Exists(inside)) return inside;
        }
        var byFolder = all.FirstOrDefault(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), name, StringComparison.OrdinalIgnoreCase));
        if (byFolder != null) return byFolder;
        return all.FirstOrDefault(f => string.Equals(TestList.NameOf(Path.GetFileName(Path.GetDirectoryName(f)) ?? ""), name, StringComparison.OrdinalIgnoreCase));
    }

    static IEnumerable<string> Candidates(string name, string root)
    {
        if (name.IndexOfAny(['\\', '/', ':']) < 0) yield break;
        if (Path.IsPathRooted(name)) yield return name;
        else yield return Path.Combine(root, name);
    }
}
