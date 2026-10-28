// Walks the input folder: decides which files to read, which project each belongs to,
// and which to skip (build output, binaries, anything that looks like a secret).
using System.Text.RegularExpressions;

namespace Retrace.Engine.Scan;

public static class InputScanner
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".git", "packages", "node_modules", "Debug", "Release", "x64", "ipch", "TestResults",
    };

    private static readonly Dictionary<string, Lang> Langs = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = Lang.CSharp, [".vb"] = Lang.VbNet,
        [".cpp"] = Lang.Cpp, [".cxx"] = Lang.Cpp, [".cc"] = Lang.Cpp, [".c"] = Lang.Cpp,
        [".h"] = Lang.Cpp, [".hpp"] = Lang.Cpp, [".hxx"] = Lang.Cpp, [".inl"] = Lang.Cpp,
        [".xaml"] = Lang.Xaml, [".rc"] = Lang.Rc, [".dcl"] = Lang.Dcl, [".cuix"] = Lang.Cuix, [".cui"] = Lang.Cuix,
    };

    private static readonly string[] ProjectExts = { ".csproj", ".vbproj", ".vcxproj" };

    /// <summary>Names that may hold credentials. Never opened, only listed as skipped.</summary>
    private static readonly Regex SecretName = Rx.Create(
        @"(secret|password|passwd|credential|token|apikey|api[-_]key|(^|[-_.])key([-_.]|$)|\.env$|\.pem$|\.pfx$|\.p12$|\.snk$|id_rsa)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksSecret(string fileName) => SecretName.IsMatch(fileName);

    public static EngineModel Scan(string inputRoot)
    {
        inputRoot = Path.GetFullPath(inputRoot);
        var model = new EngineModel { InputRoot = inputRoot };
        var projectDirs = new Dictionary<string, SourceProject>(StringComparer.OrdinalIgnoreCase);

        foreach (var abs in Walk(inputRoot))
        {
            var rel = Rel(inputRoot, abs);
            var name = Path.GetFileName(abs);
            var ext = Path.GetExtension(abs);
            if (LooksSecret(name))
            {
                model.Skipped.Add(rel + " (name looks like a secret; not opened)");
                continue;
            }
            if (ProjectExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(abs)!;
                if (!projectDirs.ContainsKey(dir))
                    projectDirs[dir] = new SourceProject
                    {
                        Name = Path.GetFileNameWithoutExtension(abs),
                        Dir = Rel(inputRoot, dir),
                        ProjectFile = rel,
                        Lang = ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ? Lang.VbNet
                             : ext.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase) ? Lang.Cpp : Lang.CSharp,
                    };
                continue;
            }
            if (!Langs.TryGetValue(ext, out var lang)) continue;

            string text = "";
            if (lang != Lang.Cuix)
            {
                try { text = File.ReadAllText(abs); }
                catch (Exception ex) { model.Warnings.Add($"{rel}: could not read ({ex.Message})"); continue; }
            }
            model.Files.Add(new SourceFile { AbsPath = abs, RelPath = rel, Lang = lang, Project = "", Text = text });
        }

        // Assign each file to its nearest project folder; loose files get a pseudo-project named after their top folder.
        var files = new List<SourceFile>();
        foreach (var f in model.Files)
        {
            var proj = NearestProject(Path.GetDirectoryName(f.AbsPath)!, inputRoot, projectDirs);
            string projName;
            if (proj != null) projName = proj.Name;
            else
            {
                var top = f.RelPath.Contains('/') ? f.RelPath[..f.RelPath.IndexOf('/')] : "(root)";
                var key = Path.Combine(inputRoot, top == "(root)" ? "" : top);
                if (!projectDirs.TryGetValue(key, out proj))
                    projectDirs[key] = proj = new SourceProject { Name = top, Dir = top == "(root)" ? "" : top, Lang = f.Lang };
                projName = proj.Name;
            }
            proj.Files.Add(f.RelPath);
            files.Add(new SourceFile { AbsPath = f.AbsPath, RelPath = f.RelPath, Lang = f.Lang, Project = projName, Text = f.Text });
        }
        model.Files.Clear();
        model.Files.AddRange(files);
        model.Projects.AddRange(projectDirs.Values.Where(p => p.Files.Count > 0).OrderBy(p => p.Dir, StringComparer.OrdinalIgnoreCase));
        return model;
    }

    private static SourceProject? NearestProject(string dir, string root, Dictionary<string, SourceProject> projects)
    {
        for (var d = dir; d != null && d.Length >= root.Length; d = Path.GetDirectoryName(d))
            if (projects.TryGetValue(d, out var p)) return p;
        return null;
    }

    private static IEnumerable<string> Walk(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.Ordinal).ToList();
                dirs = Directory.EnumerateDirectories(dir).OrderByDescending(x => x, StringComparer.Ordinal).ToList();
            }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (!SkipDirs.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }

    public static string Rel(string root, string abs) => Path.GetRelativePath(root, abs).Replace('\\', '/');
}
