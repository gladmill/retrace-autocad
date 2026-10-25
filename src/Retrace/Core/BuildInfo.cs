using System.Reflection;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>What build of Retrace this is. The one place that reads the version (the command line and the native-loader cache folder use it).</summary>
internal static class BuildInfo
{
    /// <summary>The informational version of Retrace.dll, else its assembly version, else "unknown".</summary>
    internal static string Version { get; } = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString() ?? "unknown";
}
