// Recorded screenshots (recordings `shot` events) copied under the output root, so a page can link them with a relative path.
using Retrace.Engine.Recordings;

namespace Retrace.Engine.Emit;

/// <summary>Copies shots into one folder of the output (<paramref name="dirRel"/>, '/'-separated, relative to the root) under their
/// recorded file names (a clash between different sources gets -2, -3...). A copy only happens when the bytes differ, so reruns change nothing.</summary>
public sealed class ShotCopier(string outRoot, string dirRel, Action<string> warn)
{
    private readonly Dictionary<string, string?> _done = new(StringComparer.Ordinal);
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Path of the copy relative to the output root, or null (with a warning) when the file is missing.</summary>
    public string? Copy(RecordingShot shot)
    {
        if (_done.TryGetValue(shot.Abs, out var known)) return known;
        string? rel = null;
        try
        {
            if (!File.Exists(shot.Abs)) warn($"screenshot not found: {shot.Abs}");
            else
            {
                var name = shot.File[(shot.File.LastIndexOf('/') + 1)..];
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                var unique = name;
                for (int i = 2; !_names.Add(unique); i++) unique = $"{stem}-{i}{ext}";
                rel = $"{dirRel}/{unique}";
                var dest = Path.Combine(outRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var bytes = File.ReadAllBytes(shot.Abs);
                if (!File.Exists(dest) || !File.ReadAllBytes(dest).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(dest, bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn($"cannot copy screenshot {shot.Abs}: {ex.Message}");
            rel = null;
        }
        return _done[shot.Abs] = rel;
    }
}
