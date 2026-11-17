#if NETFRAMEWORK
// The .NET Framework 4.8 build (AutoCAD 2024) compiles the same source as the .NET 8 build. The compiler-support types (init, required, records,
// ranges, nullable attributes, module initializers, ...) come from PolySharp (Retrace.csproj); this file adds the runtime APIs the code calls that
// .NET Framework lacks, as extension members (C# 14), so the call sites stay as they are. Nothing here is in the .NET 8 build.
// Behaviour follows the .NET 8 methods for the arguments the code passes; unusual arguments (a negative count, ...) are not reproduced.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;

namespace System
{
    static class NetFrameworkExtensions
    {
        extension(Math)
        {
            public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
            public static long Clamp(long v, long min, long max) => v < min ? min : v > max ? max : v;
            public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
            public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
        }

        extension(Environment)
        {
            public static int ProcessId => Diagnostics.Process.GetCurrentProcess().Id;
            public static long TickCount64 => (long)GetTickCount64();   // Environment.TickCount wraps every 49.7 days; this does not
        }

        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

        extension(OperatingSystem)
        {
            public static bool IsWindows() => true;   // AutoCAD only runs on Windows
        }

        extension(double)
        {
            public static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
        }

        extension(char)
        {
            public static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
            public static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            public static bool IsAsciiLetterOrDigit(char c) => IsAsciiLetter(c) || IsAsciiDigit(c);
        }

        extension(Convert)
        {
            public static string ToHexString(byte[] bytes)
            {
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        extension(string)
        {
            public static string Join(char sep, IEnumerable<string> parts) => string.Join(sep.ToString(), parts);
            public static string Join(char sep, string[] parts) => string.Join(sep.ToString(), parts);
            public static string Concat(ReadOnlySpan<char> a, string b) => a.ToString() + b;
            public static string Concat(ReadOnlySpan<char> a, ReadOnlySpan<char> b) => a.ToString() + b.ToString();
            public static string Create(IFormatProvider? provider, FormattableString f) => f.ToString(provider);
        }

        extension(StringSplitOptions)
        {
            /// <summary>StringSplitOptions.TrimEntries: not a flag of the .NET Framework enum, so <see cref="Split"/> below takes it out and trims itself.</summary>
            public static StringSplitOptions TrimEntries => (StringSplitOptions)(1 << 30);
        }

        const StringSplitOptions TrimFlag = (StringSplitOptions)(1 << 30);

        // ---- string ----
        public static string[] Split(this string s, char sep, StringSplitOptions options) => SplitCore(s, new[] { sep }, int.MaxValue, options);
        public static string[] Split(this string s, char sep, int count, StringSplitOptions options) => SplitCore(s, new[] { sep }, count, options);
        public static string[] Split(this string s, string sep, StringSplitOptions options) => SplitCore(s, new[] { sep }, int.MaxValue, options);
        public static string[] Split(this string s, char[] sep, StringSplitOptions options) => SplitCore(s, sep, int.MaxValue, options);
        public static string[] Split(this string s, string[] sep, StringSplitOptions options) => SplitCore(s, sep, int.MaxValue, options);

        static string[] SplitCore(string s, object seps, int count, StringSplitOptions options)
        {
            bool trim = (options & TrimFlag) != 0;
            var plain = options & ~TrimFlag;
            var parts = seps is char[] c ? s.Split(c, count, plain) : s.Split((string[])seps, count, plain);
            if (!trim) return parts;
            var list = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0 && (plain & StringSplitOptions.RemoveEmptyEntries) != 0) continue;
                list.Add(t);
            }
            return list.ToArray();
        }

        public static bool Contains(this string s, char c) => s.IndexOf(c) >= 0;
        public static bool Contains(this string s, string v, StringComparison cmp) => s.IndexOf(v, cmp) >= 0;
        public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;
        public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;
        public static string Replace(this string s, string oldValue, string? newValue, StringComparison cmp)
        {
            if (oldValue.Length == 0) throw new ArgumentException("String cannot be of zero length.", nameof(oldValue));
            newValue ??= "";
            var sb = new StringBuilder();
            int pos = 0, i;
            while ((i = s.IndexOf(oldValue, pos, cmp)) >= 0) { sb.Append(s, pos, i - pos).Append(newValue); pos = i + oldValue.Length; }
            return sb.Append(s, pos, s.Length - pos).ToString();
        }
        public static string ReplaceLineEndings(this string s, string replacement) => s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", replacement);
        public static string ReplaceLineEndings(this string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
        public static string Trim(this string s, char c) => s.Trim(new[] { c });
        public static string TrimStart(this string s, char c) => s.TrimStart(new[] { c });
        public static string TrimEnd(this string s, char c) => s.TrimEnd(new[] { c });
        public static int IndexOf(this string s, char c, StringComparison cmp) => s.IndexOf(c.ToString(), cmp);

        public static string[] Split(this string s, string sep) => s.Split(new[] { sep }, StringSplitOptions.None);
        public static StringBuilder Append(this StringBuilder sb, IFormatProvider? provider, FormattableString f) => sb.Append(f.ToString(provider));
        public static System.Reflection.MethodInfo? GetMethod(this Type t, string name, System.Reflection.BindingFlags flags, Type[] types) =>
            t.GetMethod(name, flags, null, types, null);
        public static int Count(this ReadOnlySpan<char> s, char c) { int n = 0; foreach (var x in s) if (x == c) n++; return n; }
        public static T FirstOrDefault<T>(this IEnumerable<T> s, Func<T, bool> pred, T fallback) { foreach (var x in s) if (pred(x)) return x; return fallback; }

        extension(int)
        {
            public static bool TryParse(ReadOnlySpan<char> s, out int result) => int.TryParse(s.ToString(), out result);
            public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out int result) => int.TryParse(s.ToString(), style, provider, out result);
        }
        extension(Enum)
        {
            public static T Parse<T>(string value) where T : struct, Enum => (T)Enum.Parse(typeof(T), value);
        }

        // ---- collections ----
        public static void Deconstruct<TK, TV>(this KeyValuePair<TK, TV> kv, out TK key, out TV value) { key = kv.Key; value = kv.Value; }

        public static TV? GetValueOrDefault<TK, TV>(this IReadOnlyDictionary<TK, TV> d, TK key) => d.TryGetValue(key, out var v) ? v : default;
        public static TV GetValueOrDefault<TK, TV>(this IReadOnlyDictionary<TK, TV> d, TK key, TV fallback) => d.TryGetValue(key, out var v) ? v : fallback;
        public static bool TryAdd<TK, TV>(this IDictionary<TK, TV> d, TK key, TV value) { if (d.ContainsKey(key)) return false; d.Add(key, value); return true; }
        public static bool Remove<TK, TV>(this Dictionary<TK, TV> d, TK key, out TV value) where TK : notnull
        {
            if (!d.TryGetValue(key, out value!)) return false;
            d.Remove(key);
            return true;
        }
        public static bool TryDequeue<T>(this Queue<T> q, out T value) { if (q.Count > 0) { value = q.Dequeue(); return true; } value = default!; return false; }
        public static bool TryPop<T>(this Stack<T> q, out T value) { if (q.Count > 0) { value = q.Pop(); return true; } value = default!; return false; }

        // ---- Regex: MatchCollection is IEnumerable<Match> on .NET 8, not on .NET Framework ----
        public static IEnumerable<System.Text.RegularExpressions.Match> AsEnumerable(this System.Text.RegularExpressions.MatchCollection m) => m.Cast<System.Text.RegularExpressions.Match>();
        public static IEnumerable<T> Select<T>(this System.Text.RegularExpressions.MatchCollection m, Func<System.Text.RegularExpressions.Match, T> f) => m.AsEnumerable().Select(f);
        public static IEnumerable<System.Text.RegularExpressions.Match> Where(this System.Text.RegularExpressions.MatchCollection m, Func<System.Text.RegularExpressions.Match, bool> f) => m.AsEnumerable().Where(f);
        public static int Count(this System.Text.RegularExpressions.MatchCollection m, Func<System.Text.RegularExpressions.Match, bool> f) => m.AsEnumerable().Count(f);
        public static bool Any(this System.Text.RegularExpressions.MatchCollection m, Func<System.Text.RegularExpressions.Match, bool> f) => m.AsEnumerable().Any(f);

        // ---- Linq ----
        public static IEnumerable<T> Order<T>(this IEnumerable<T> s) => s.OrderBy(x => x);
        public static IEnumerable<T> Order<T>(this IEnumerable<T> s, IComparer<T>? c) => s.OrderBy(x => x, c);
        public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> s, int n)
        {
            var q = new Queue<T>();
            foreach (var x in s) { if (n <= 0) yield break; q.Enqueue(x); if (q.Count > n) q.Dequeue(); }
            foreach (var x in q) yield return x;
        }
        public static IEnumerable<T> SkipLast<T>(this IEnumerable<T> s, int n)
        {
            var q = new Queue<T>();
            foreach (var x in s) { q.Enqueue(x); if (q.Count > n) yield return q.Dequeue(); }
        }
        public static IEnumerable<T> DistinctBy<T, K>(this IEnumerable<T> s, Func<T, K> key, IEqualityComparer<K>? cmp = null)
        {
            var seen = new HashSet<K>(cmp);
            foreach (var x in s) if (seen.Add(key(x))) yield return x;
        }
        public static T? MinBy<T, K>(this IEnumerable<T> s, Func<T, K> key)
        {
            var cmp = Comparer<K>.Default; T? best = default; K bestKey = default!; bool any = false;
            foreach (var x in s) { var k = key(x); if (!any || cmp.Compare(k, bestKey) < 0) { best = x; bestKey = k; any = true; } }
            return best;
        }
        public static T? MaxBy<T, K>(this IEnumerable<T> s, Func<T, K> key)
        {
            var cmp = Comparer<K>.Default; T? best = default; K bestKey = default!; bool any = false;
            foreach (var x in s) { var k = key(x); if (!any || cmp.Compare(k, bestKey) > 0) { best = x; bestKey = k; any = true; } }
            return best;
        }

        // ---- Task ----
        extension(Threading.Tasks.Task t)
        {
            public bool IsCompletedSuccessfully => t.Status == Threading.Tasks.TaskStatus.RanToCompletion;
        }

        // ---- IO ----
        extension(File)
        {
            /// <summary>As .NET 8 on Windows: one MoveFileEx call with MOVEFILE_REPLACE_EXISTING, so a reader of the destination sees the old file or the new one,
            /// never none (AtomicFile, StatusBoard.WriteAtomic). WRITE_THROUGH and COPY_ALLOWED make a move to another volume finish before it returns.
            /// The errors are the ones File.Move throws (FileNotFound, DirectoryNotFound, UnauthorizedAccess, otherwise IOException).</summary>
            public static void Move(string source, string dest, bool overwrite)
            {
                if (!overwrite) { File.Move(source, dest); return; }
                if (source == null) throw new ArgumentNullException(nameof(source));
                if (dest == null) throw new ArgumentNullException(nameof(dest));
                var src = Path.GetFullPath(source);
                var dst = Path.GetFullPath(dest);
                if (!MoveFileEx(src, dst, 0x1 | 0x2 | 0x8)) ThrowMoveError(Marshal.GetLastWin32Error(), src, dst);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string existing, string replacement, int flags);

        static void ThrowMoveError(int error, string src, string dst)
        {
            switch (error)
            {
                case 2: throw new FileNotFoundException("Could not find file '" + src + "'.", src);
                case 3: throw new DirectoryNotFoundException("Could not find a part of the path '" + (File.Exists(src) ? dst : src) + "'.");
                case 5: throw new UnauthorizedAccessException("Access to the path '" + dst + "' is denied.");
                default: throw new IOException("The file '" + src + "' could not be moved to '" + dst + "' (Win32 error " + error + ").", unchecked((int)(0x80070000 | (uint)error)));
            }
        }

        public static void Write(this Stream s, ReadOnlySpan<byte> data)
        {
            var buf = data.ToArray();
            s.Write(buf, 0, buf.Length);
        }
        public static int Read(this Stream s, Span<byte> data)
        {
            var buf = new byte[data.Length];
            int n = s.Read(buf, 0, buf.Length);
            buf.AsSpan(0, n).CopyTo(data);
            return n;
        }

        // ---- crypto ----
        extension(SHA256)
        {
            public static byte[] HashData(byte[] data) { using var h = SHA256.Create(); return h.ComputeHash(data); }
        }
    }
}

namespace System.IO.Compression
{
    /// <summary>ZLibStream (.NET 6+) for compression: the zlib header, a raw deflate stream, and the Adler-32 trailer.</summary>
    sealed class ZLibStream : Stream
    {
        readonly Stream _inner;
        readonly DeflateStream _deflate;
        readonly bool _leaveOpen;
        uint _a = 1, _b;
        bool _done;

        public ZLibStream(Stream stream, CompressionLevel level, bool leaveOpen = false)
        {
            _inner = stream; _leaveOpen = leaveOpen;
            // 0x78 = deflate, 32K window; the second byte makes (CMF*256+FLG) a multiple of 31 and carries the level hint
            byte flg = level switch { CompressionLevel.Fastest => 0x01, CompressionLevel.NoCompression => 0x01, _ => 0x9C };
            stream.WriteByte(0x78); stream.WriteByte(flg);
            _deflate = new DeflateStream(stream, level, leaveOpen: true);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _deflate.Write(buffer, offset, count);
            for (int i = 0; i < count; i++) { _a = (_a + buffer[offset + i]) % 65521; _b = (_b + _a) % 65521; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_done)
            {
                _done = true;
                _deflate.Dispose();
                var adler = (_b << 16) | _a;
                _inner.WriteByte((byte)(adler >> 24)); _inner.WriteByte((byte)(adler >> 16)); _inner.WriteByte((byte)(adler >> 8)); _inner.WriteByte((byte)adler);
                if (!_leaveOpen) _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _deflate.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

namespace System.Collections.Generic
{
    /// <summary>ReferenceEqualityComparer (.NET 5+): .NET Framework has none that this assembly may use.</summary>
    sealed class ReferenceEqualityComparer : IEqualityComparer<object?>, System.Collections.IEqualityComparer
    {
        public static ReferenceEqualityComparer Instance { get; } = new();
        ReferenceEqualityComparer() { }
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object? obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
#endif
