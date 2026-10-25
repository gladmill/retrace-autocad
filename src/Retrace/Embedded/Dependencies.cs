using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

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
        AssemblyLoadContext.Default.Resolving += OnResolving;
    }

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
                var dir = Path.Combine(Paths.NativeCache(), SafeName(BuildInfo.Version));
                var target = Path.Combine(dir, LoaderFile);
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
