#if NETFRAMEWORK
// On .NET Framework (AutoCAD 2024) System.IO.Path throws on characters that .NET 8 accepts: ArgumentException for " < > | and control characters in
// Combine, GetFullPath, GetFileName, GetDirectoryName, GetExtension, IsPathRooted ..., NotSupportedException for a ':' in the middle of a path.
// The code parses names out of files it does not control (.rc, .cuix, recordings, settings): one bad name must skip that one item (the file
// APIs then report "not found" as on .NET 8), not abort a whole scan. So in the .NET Framework build the name `Path` is this class (the
// global using below): the members the code calls, with the .NET 8 results for every input and no exception for an invalid character.
// Only members the code uses are here; add one when the compiler asks (a missing member is a compile error, never a silent fallback).
global using Path = Retrace.Compat.NetPath;

namespace Retrace.Compat
{
    static class NetPath
    {
        public const char DirectorySeparatorChar = '\\';
        public const char AltDirectorySeparatorChar = '/';
        public const char VolumeSeparatorChar = ':';
        public const char PathSeparator = ';';

        static readonly char[] Seps = { '\\', '/' };

        static bool IsSep(char c) => c == '\\' || c == '/';
        static bool IsDrive(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        // ---- the sets .NET 8 reports on Windows ----
        public static char[] GetInvalidPathChars()
        {
            var l = new List<char> { '|', '\0' };
            for (char c = (char)1; c < 32; c++) l.Add(c);
            return l.ToArray();
        }

        public static char[] GetInvalidFileNameChars()
        {
            var l = new List<char> { '"', '<', '>', '|', '\0' };
            for (char c = (char)1; c < 32; c++) l.Add(c);
            l.AddRange(new[] { ':', '*', '?', '\\', '/' });
            return l.ToArray();
        }

        public static string GetTempPath() => System.IO.Path.GetTempPath();
        public static string GetRandomFileName() => System.IO.Path.GetRandomFileName();
        public static string GetTempFileName() => System.IO.Path.GetTempFileName();

        // ---- Combine: .NET 8 rules (a rooted part replaces what came before; one separator between parts) ----
        public static string Combine(string path1, string path2) => CombineCore(path1, path2);
        public static string Combine(string path1, string path2, string path3) => CombineCore(path1, path2, path3);
        public static string Combine(string path1, string path2, string path3, string path4) => CombineCore(path1, path2, path3, path4);
        public static string Combine(params string[] paths) => CombineCore(paths);

        static string CombineCore(params string[] paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            var result = "";
            foreach (var p in paths)
            {
                if (p == null) throw new ArgumentNullException(nameof(paths));
                if (p.Length == 0) continue;
                if (result.Length == 0 || IsPathRooted(p)) { result = p; continue; }
                var last = result[result.Length - 1];
                result = IsSep(last) || last == ':' ? result + p : result + "\\" + p;
            }
            return result;
        }

        // ---- the pieces of a path ----
        static int RootLength(string path)
        {
            if (path.Length >= 2 && IsDrive(path[0]) && path[1] == ':') return path.Length >= 3 && IsSep(path[2]) ? 3 : 2;
            if (path.Length >= 1 && IsSep(path[0]))
            {
                if (path.Length >= 2 && IsSep(path[1]))
                {
                    // \\server\share (and \\?\ forms): the root runs through the share
                    int i = 2, segments = 0;
                    while (i < path.Length && segments < 2)
                    {
                        while (i < path.Length && !IsSep(path[i])) i++;
                        segments++;
                        if (segments < 2 && i < path.Length) i++;
                    }
                    return i;
                }
                return 1;
            }
            return 0;
        }

        static string Normalize(string s) => s.Replace('/', '\\');

        public static bool IsPathRooted(string? path) =>
            path != null && ((path.Length >= 1 && IsSep(path[0])) || (path.Length >= 2 && IsDrive(path[0]) && path[1] == ':'));

        public static string? GetPathRoot(string? path)
        {
            if (path == null || path.Trim(' ').Length == 0) return null;
            var n = RootLength(path);
            return n == 0 ? "" : Normalize(path.Substring(0, n));
        }

        public static string? GetFileName(string? path)
        {
            if (path == null) return null;
            int start = path.LastIndexOfAny(Seps) + 1;
            if (start < 2 && path.Length >= 2 && IsDrive(path[0]) && path[1] == ':') start = 2;
            return path.Substring(start);
        }

        public static string? GetDirectoryName(string? path)
        {
            if (path == null || path.Trim(' ').Length == 0) return null;
            int root = RootLength(path);
            int end = path.Length;
            if (end <= root) return null;
            while (end > root && !IsSep(path[--end])) { }
            while (end > root && IsSep(path[end - 1])) end--;
            return Normalize(path.Substring(0, end));
        }

        public static string GetExtension(string? path)
        {
            if (path == null) return null!;
            for (int i = path.Length - 1; i >= 0; i--)
            {
                char c = path[i];
                if (c == '.') return i != path.Length - 1 ? path.Substring(i) : "";
                if (IsSep(c) || c == ':') break;
            }
            return "";
        }

        public static bool HasExtension(string? path) => path != null && GetExtension(path).Length > 0;

        public static string? GetFileNameWithoutExtension(string? path)
        {
            var name = GetFileName(path);
            if (name == null) return null;
            int dot = name.LastIndexOf('.');
            return dot < 0 ? name : name.Substring(0, dot);
        }

        public static string? ChangeExtension(string? path, string? extension)
        {
            if (path == null) return null;
            int sub = path.Length;
            for (int i = path.Length - 1; i >= 0; i--)
            {
                char c = path[i];
                if (c == '.') { sub = i; break; }
                if (IsSep(c) || c == ':') break;
            }
            var head = path.Substring(0, sub);
            if (extension == null) return head;
            return extension.StartsWith(".") ? head + extension : head + "." + extension;
        }

        // ---- full paths ----
        public static string GetFullPath(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (path.Length == 0 || path.IndexOf('\0') >= 0) throw new ArgumentException("The path is empty or holds a null character.", nameof(path));
            try { return System.IO.Path.GetFullPath(path); }
            catch (ArgumentException) { }       // " < > | or a control character
            catch (NotSupportedException) { }   // a ':' in the middle
            return ManualFullPath(path);
        }

        public static string GetFullPath(string path, string basePath)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (basePath == null) throw new ArgumentNullException(nameof(basePath));
            if (!IsPathRooted(basePath)) throw new ArgumentException("The base path is not fully qualified.", nameof(basePath));
            return GetFullPath(IsPathRooted(path) ? path : Combine(basePath, path));
        }

        /// <summary>What .NET 8 returns for a path with characters .NET Framework refuses: made absolute against the current directory, "." and ".." resolved, one separator kind.</summary>
        static string ManualFullPath(string path)
        {
            var cwd = System.IO.Directory.GetCurrentDirectory();
            string abs;
            if (path.Length >= 2 && IsDrive(path[0]) && path[1] == ':')
                abs = path.Length >= 3 && IsSep(path[2]) ? path
                    : (char.ToUpperInvariant(cwd[0]) == char.ToUpperInvariant(path[0]) ? Combine(cwd, path.Substring(2)) : path.Substring(0, 2) + "\\" + path.Substring(2));
            else if (path.Length >= 1 && IsSep(path[0]) && !(path.Length >= 2 && IsSep(path[1]))) abs = cwd.Substring(0, RootLength(cwd) > 0 ? Math.Min(2, cwd.Length) : 0) + path;
            else if (IsPathRooted(path)) abs = path;
            else abs = Combine(cwd, path);

            int root = RootLength(abs);
            var rootText = Normalize(abs.Substring(0, root));
            var parts = new List<string>();
            foreach (var seg in abs.Substring(root).Split(Seps))
            {
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(seg);
            }
            var body = string.Join("\\", parts);
            if (rootText.Length > 0 && !rootText.EndsWith("\\") && body.Length > 0) rootText += "\\";
            return rootText + body;
        }

        public static string GetRelativePath(string relativeTo, string path)
        {
            // as .NET: both made absolute, compared segment by segment (case-insensitive: Windows); another drive gives the full path
            var from = GetFullPath(relativeTo);
            var to = GetFullPath(path);
            if (!string.Equals(GetPathRoot(from), GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) return to;
            var f = from.Substring(GetPathRoot(from)!.Length).Split(Seps, StringSplitOptions.RemoveEmptyEntries);
            var t = to.Substring(GetPathRoot(to)!.Length).Split(Seps, StringSplitOptions.RemoveEmptyEntries);
            int common = 0;
            while (common < f.Length && common < t.Length && string.Equals(f[common], t[common], StringComparison.OrdinalIgnoreCase)) common++;
            var parts = new List<string>();
            for (int i = common; i < f.Length; i++) parts.Add("..");
            for (int i = common; i < t.Length; i++) parts.Add(t[i]);
            return parts.Count == 0 ? "." : string.Join("\\", parts);
        }
    }
}
#endif
