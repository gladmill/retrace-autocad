using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

using Retrace.AutoCad;
using Retrace.Core;

namespace Retrace.Embedded;

/// <summary>
/// Retrace is one dll: Markdig, Microsoft.Web.WebView2.Core / .Wpf and the native
/// WebView2Loader.dll are embedded in it (Retrace.csproj, target EmbedDependencies) as resources named <c>Retrace.Deps.&lt;file&gt;</c> and
/// <c>Retrace.Deps.native.&lt;file&gt;</c>. AutoCAD does not probe the bundle folder, so this registers a resolver on the default load context
/// that loads them from those resources, and points WebView2's native loader at a copy extracted to <see cref="Paths.NativeCache"/>.
/// It is hooked by a module initializer, i.e. before any type of this dll runs: the engine (Markdig) and the UI (WebView2) need it
/// the moment they are JIT-compiled.
/// </summary>
static class Dependencies
{
    const string ResourcePrefix = "Retrace.Deps.";
    const string NativePrefix = ResourcePrefix + "native.";

    /// <summary>The native loader ships under a private name: AutoCAD makes the folders of its plugins visible to the processes it starts, and
    /// Autodesk's licensing agent (which outlives AutoCAD) loaded a plain "WebView2Loader.dll" from there by name and locked it against updates.
    /// So it is extracted under this name, to a folder per Retrace version (never overwritten, so a locked old copy cannot block an update).</summary>
    internal const string LoaderFile = "Retrace.WebView2Loader.dll";

    static readonly object Gate = new();
    static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);
    static readonly Assembly Self = typeof(Dependencies).Assembly;
#if NETFRAMEWORK
    static readonly string BundleDir = Path.GetDirectoryName(Self.Location) ?? "";
#endif
    static string[] _resources = [];
    static bool _installed, _nativeHooked;
    static string? _loaderPath;

    // CA2255: a module initializer in a library is exactly what is wanted here (it must run before any other code of this dll).
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Install()
#pragma warning restore CA2255
    {
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;
            _resources = Self.GetManifestResourceNames();
        }
#if NETFRAMEWORK
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
#else
        AssemblyLoadContext.Default.Resolving += OnResolving;
#endif
    }

#if NETFRAMEWORK
    /// <summary>
    /// AutoCAD 2024 (.NET Framework 4.8). An add-in has no app.config, so there are no binding redirects, and the framework binds an exact assembly
    /// version: another add-in's different System.Memory / System.Text.Json / ... never satisfies ours, it only makes ours fail to bind. This
    /// handler runs for what the runtime could not bind: (1) the dependencies embedded as resources (Markdig, WebView2), loaded from
    /// the resource; (2) any other assembly with a file of that name beside Retrace.dll (<c>Contents\2024\</c>: System.Text.Json and what it pulls,
    /// OpenXml), loaded from there when its version is at least the one asked for. Two versions of one name can live side by side, so a copy another
    /// add-in loaded is left alone and we use ours; the types never cross to the other add-in. What this cannot solve: AutoCAD's own
    /// acad.exe.config redirecting one of these names to a version that is not what Retrace was built with (the binding then never reaches this
    /// handler), and a version that the host loaded before and passes across to us.
    /// </summary>
    /// <summary>The request comes from Retrace.dll, from something Retrace loaded from the resources or from its own folder, or from no known assembly.</summary>
    static bool IsOurs(Assembly? requester)
    {
        if (requester == null || requester == Self) return true;
        if (string.IsNullOrEmpty(requester.Location)) { lock (Gate) return Loaded.ContainsValue(requester); }   // loaded from bytes: ours only if we loaded it (Loaded is written under Gate on other threads)
        return string.Equals(Path.GetDirectoryName(requester.Location), BundleDir, StringComparison.OrdinalIgnoreCase);
    }

    static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            var name = new AssemblyName(args.Name);
            var n = name.Name ?? "";
            if (n.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
            if (!IsOurs(args.RequestingAssembly)) return null;   // another add-in's failed bind is not ours to answer
            lock (Gate)
            {
                if (Loaded.TryGetValue(n, out var done)) return done;
                var resource = ResourcePrefix + n + ".dll";
                Assembly? asm = null;
                if (_resources.Contains(resource, StringComparer.Ordinal))
                {
                    using var stream = Self.GetManifestResourceStream(resource);
                    if (stream == null) return null;
                    var bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length) { int k = stream.Read(bytes, read, bytes.Length - read); if (k <= 0) break; read += k; }
                    asm = Assembly.Load(bytes);
                    if (n == "Microsoft.Web.WebView2.Core") HookNative(asm);
                }
                else
                {
                    var file = Path.Combine(BundleDir, n + ".dll");
                    if (!File.Exists(file)) return null;
                    var have = AssemblyName.GetAssemblyName(file).Version;
                    if (name.Version != null && have != null && have < name.Version)
                    { Log.Error("resolve " + n, new FileLoadException($"{n} {have} is beside Retrace.dll; Retrace needs {name.Version}")); return null; }
                    asm = Assembly.LoadFrom(file);
                }
                Loaded[n] = asm;
                return asm;
            }
        }
        catch (Exception ex) { Log.Error("resolve " + args.Name, ex); return null; }
    }
#endif

#if !NETFRAMEWORK
    static Assembly? OnResolving(AssemblyLoadContext ctx, AssemblyName name)
    {
        try
        {
            var n = name.Name ?? "";
            var resource = ResourcePrefix + n + ".dll";
            if (!_resources.Contains(resource, StringComparer.Ordinal)) return null;     // not one of ours
            lock (Gate)
            {
                if (Loaded.TryGetValue(n, out var done)) return done;
                // Someone else (AutoCAD, another plugin) already loaded this assembly: a second copy cannot live in the default context, so use
                // theirs, but only if it is at least as new as the version asked for; otherwise log it and fail the load. A reused assembly is
                // left alone: HookNative (the private native loader) is only installed on a copy loaded from our own resources, never on this one.
                var existing = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && string.Equals(a.GetName().Name, n, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    var have = existing.GetName().Version;
                    if (name.Version == null || (have != null && have >= name.Version)) { Loaded[n] = existing; return existing; }
                    Log.Error("resolve " + n, new FileLoadException($"{n} {have} is already loaded by another plugin; Retrace needs {name.Version}"));
                    return null;
                }

                using var stream = Self.GetManifestResourceStream(resource);
                if (stream == null) return null;
                var asm = ctx.LoadFromStream(stream);
                Loaded[n] = asm;
                if (n == "Microsoft.Web.WebView2.Core") HookNative(asm);
                return asm;
            }
        }
        catch (Exception ex) { Log.Error("resolve " + name.Name, ex); return null; }
    }

#endif

#if NETFRAMEWORK
    /// <summary>The .NET Framework build of WebView2.Core loads "WebView2Loader.dll" from a folder it is told: the extracted copy's (plain name here,
    /// because the framework build cannot be pointed at a renamed file; the folder is the per-version cache, not a plugin folder).</summary>
    static void HookNative(Assembly core)
    {
        if (_nativeHooked) return;
        _nativeHooked = true;
        try
        {
            var file = ExtractLoader();
            if (file == null) return;
            var env = core.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment", throwOnError: false);
            env?.GetMethod("SetLoaderDllFolderPath", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { Path.GetDirectoryName(file)! });
        }
        catch (Exception ex) { Log.Error("native hook", ex); }
    }
#else
    /// <summary>WebView2's managed code P/Invokes "WebView2Loader.dll"; load our privately named, extracted copy explicitly instead of probing.</summary>
    static void HookNative(Assembly core)
    {
        if (_nativeHooked) return;
        _nativeHooked = true;
        try
        {
            NativeLibrary.SetDllImportResolver(core, (libName, _, _) =>
            {
                if (!libName.StartsWith("WebView2Loader", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                var file = ExtractLoader();
                return file != null && NativeLibrary.TryLoad(file, out var h) ? h : IntPtr.Zero;
            });
        }
        catch (Exception ex) { Log.Error("native hook", ex); }
    }

#endif

    /// <summary>Writes the embedded loader to <c>cache\native\&lt;version&gt;\Retrace.WebView2Loader.dll</c> (through a temp file, then a move) unless a
    /// file of the same size is already there. Returns the path, or null if it could not be written.</summary>
    internal static string? ExtractLoader()
    {
        lock (Gate)
        {
            if (_loaderPath != null) return _loaderPath;
            try
            {
                using var stream = Self.GetManifestResourceStream(NativePrefix + "WebView2Loader.dll");
                if (stream == null) { Log.Error("native loader", new FileNotFoundException("embedded WebView2Loader.dll is missing")); return null; }
#if NETFRAMEWORK
                var dir = Path.Combine(Paths.NativeCache(), SafeName(BuildInfo.Version), "net48");
                var target = Path.Combine(dir, "WebView2Loader.dll");
#else
                var dir = Path.Combine(Paths.NativeCache(), SafeName(BuildInfo.Version));
                var target = Path.Combine(dir, LoaderFile);
#endif
                Directory.CreateDirectory(dir);
                if (!(File.Exists(target) && new FileInfo(target).Length == stream.Length))
                {
                    var tmp = Path.Combine(dir, LoaderFile + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        using (var o = File.Create(tmp)) stream.CopyTo(o);
                        try { File.Move(tmp, target, overwrite: true); }
                        catch (IOException) when (File.Exists(target) && new FileInfo(target).Length == stream.Length) { /* another AutoCAD session wrote (and holds) the same file */ }
                    }
                    finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
                }
                return _loaderPath = target;
            }
            catch (Exception ex) { Log.Error("extract native loader", ex); return null; }
        }
    }

    static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
