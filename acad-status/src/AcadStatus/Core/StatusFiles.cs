using System;
using System.Collections.Generic;
using System.IO;

namespace AcadStatus.Core;

public sealed record Unreadable(string File, string Reason);

public sealed record FolderRead(IReadOnlyList<StatusEntry> Entries, IReadOnlyList<Unreadable> Skipped);

/// <summary>The status folder: where it is, which files a reader looks at, and the start-up clean-up (CONTRACT.md, "Folder" and "Files").</summary>
public sealed class StatusFiles
{
    public const string EnvVar = "ACAD_STATUS_DIR";
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>The folder: <c>ACAD_STATUS_DIR</c> when set, else <c>&lt;ProgramData&gt;\Gladmill\acad-status</c>.</summary>
    public static string Resolve(Func<string, string?> getEnv, Func<string> commonAppData)
    {
        var env = getEnv(EnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return Path.Combine(commonAppData(), "Gladmill", "acad-status");
    }

    public static string ResolveDefault() =>
        Resolve(Environment.GetEnvironmentVariable, () => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    /// <summary>Readers read only files named <c>^[a-z0-9][a-z0-9-]{0,39}\.json$</c>: so <c>&lt;source&gt;.v2.json</c> and <c>.tmp-</c> files are skipped.</summary>
    public static bool IsStatusFileName(string? name)
    {
        if (name == null || !name.EndsWith(".json", StringComparison.Ordinal)) return false;
        var stem = name.Length - 5;
        if (stem < 1 || stem > 40) return false;
        for (int i = 0; i < stem; i++)
        {
            char c = name[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || (c == '-' && i > 0);
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>The file of an in-process writer: <c>acad-4812.json</c>.</summary>
    public static string FileFor(string source, int pid) => source + "-" + pid + ".json";

    readonly string _dir;
    readonly Dictionary<string, (long Ticks, long Length, ParseResult Result)> _cache = new(StringComparer.Ordinal);

    readonly object _gate = new();

    public StatusFiles(string dir) { _dir = dir; }

    public string Dir => _dir;

    /// <summary>Reads every status file. A file whose size and write time did not change since the last call is not read again.
    /// <paramref name="skipFile"/>: a file to leave out (the caller's own, which it already holds in memory). Never throws. Safe to call from
    /// two threads (the pane reads on a background task, the clean-up on the UI thread): one read at a time shares the cache.</summary>
    public FolderRead Read(string? skipFile = null)
    {
        lock (_gate) return ReadCore(skipFile);
    }

    FolderRead ReadCore(string? skipFile)
    {
        var entries = new List<StatusEntry>();
        var skipped = new List<Unreadable>();
        string[] files;
        try
        {
            if (!Directory.Exists(_dir)) return new FolderRead(entries, skipped);
            files = Directory.GetFiles(_dir);
        }
        catch { return new FolderRead(entries, skipped); }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (!IsStatusFileName(name) || string.Equals(name, skipFile, StringComparison.Ordinal)) continue;
            seen.Add(name);
            try
            {
                var info = new FileInfo(path);
                var key = (info.LastWriteTimeUtc.Ticks, info.Length);
                if (!_cache.TryGetValue(name, out var c) || c.Ticks != key.Item1 || c.Length != key.Item2)
                {
                    c = (key.Item1, key.Item2, ParseFile(path, name, info.Length));
                    _cache[name] = c;
                }
                if (c.Result.Entry != null) entries.Add(c.Result.Entry);
                else skipped.Add(new Unreadable(name, c.Result.Error ?? "unreadable"));
            }
            catch (Exception ex)
            {
                // deleted while we looked, or locked: skip it this round
                skipped.Add(new Unreadable(name, "could not be read: " + ex.Message));
                _cache.Remove(name);
            }
        }
        foreach (var gone in new List<string>(_cache.Keys))
            if (!seen.Contains(gone)) _cache.Remove(gone);
        return new FolderRead(entries, skipped);
    }

    static ParseResult ParseFile(string path, string name, long length)
    {
        if (length > MaxFileBytes) return new ParseResult(null, "file is larger than " + MaxFileBytes / 1024 + " KB");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, new System.Text.UTF8Encoding(false), true);
        return StatusParser.Parse(name, reader.ReadToEnd());
    }

    /// <summary>The clean-up (on start and every 10 minutes): any status file whose <c>pid</c> is gone, or reused by another process
    /// (<c>pidStart</c>), and whose <c>updated</c> is over one hour old. Files that cannot be read are not touched. <paramref name="pidAlive"/>
    /// gets the pid, the start time the file states, and how long ago the file was updated.</summary>
    public static IReadOnlyList<string> CleanupCandidates(IEnumerable<StatusEntry> entries, DateTime nowUtc, Func<int, DateTime?, TimeSpan, bool> pidAlive)
    {
        var result = new List<string>();
        foreach (var e in entries)
        {
            var age = nowUtc - e.Updated;
            if (age <= TimeSpan.FromHours(1)) continue;
            bool alive;
            try { alive = e.Pid > 0 && pidAlive(e.Pid, e.PidStart, age); } catch { alive = true; }
            if (!alive) result.Add(e.File);
        }
        return result;
    }

    /// <summary>Deletes the clean-up candidates; returns the names deleted. Reads through this instance's cache. Never throws.</summary>
    public IReadOnlyList<string> Cleanup(DateTime nowUtc, Func<int, DateTime?, TimeSpan, bool> pidAlive)
    {
        var deleted = new List<string>();
        try
        {
            foreach (var name in CleanupCandidates(Read().Entries, nowUtc, pidAlive))
            {
                try { File.Delete(Path.Combine(_dir, name)); deleted.Add(name); }
                catch { /* in use or already gone */ }
            }
        }
        catch { /* the folder is not ours to worry about */ }
        return deleted;
    }
}
