using Retrace.Engine.Recordings;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Retrace.Core;

/// <summary>Crash-safe JSONL writer: one event per line, flushed after every line.</summary>
public sealed class SessionWriter : IDisposable
{
    public const int SchemaVersion = 1;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly StreamWriter _out;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly object _lock = new();

    public string Path { get; }
    public string Name { get; }
    public int Events { get; private set; }
    /// <summary>Number of the last event written (the `n` field); 0 before the first event. Continues after a resume.</summary>
    public int LastN => _n;
    int _n;
    long _offset;
    public long ElapsedMs => _offset + _clock.ElapsedMilliseconds;

    SessionWriter(string path, long offset, int lastN = 0)
    {
        _n = lastN;
        Path = path;
        Name = TestFolders.NameOf(path);
        _offset = offset;
        _out = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }

    /// <summary>Re-opens an existing session file for appending; t continues from the last event in it.</summary>
    public static SessionWriter Resume(string path)
    {
        long last = 0;
        int maxN = 0, eventLines = 0;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                if (d.RootElement.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number) last = t.GetInt64();
                if (d.RootElement.TryGetProperty("type", out var ty) && ty.GetString() != "header") eventLines++;
                if (d.RootElement.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.Number) maxN = Math.Max(maxN, n.GetInt32());
            }
            catch (JsonException) { }
        }
        // sessions recorded before `n` existed have no numbers: continue after the number of events they hold
        return new SessionWriter(path, last, Math.Max(maxN, eventLines));
    }

    /// <param name="dir">the tests folder; null = the one from settings (tests pass a temp folder). The recording goes into a new folder
    /// `&lt;yyyy-MM-dd_HHmm&gt;_&lt;name&gt;\recording.jsonl` in it.</param>
    public SessionWriter(string name, Dictionary<string, object?> header, string? dir = null)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        if (safe.Length == 0) safe = "test";
        Name = safe;
        dir ??= RetraceSettings.Load().Tests;
        Directory.CreateDirectory(dir);
        var started = DateTime.Now;
        var folder = TestFolders.UniqueFolder(dir, TestFolders.FolderName(safe, started));
        Directory.CreateDirectory(folder);
        Path = System.IO.Path.Combine(folder, TestFolders.RecordingFile);
        _out = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        header["type"] = "header";
        header["schema"] = SchemaVersion;
        header["name"] = safe;
        header["started"] = started.ToString("o");
        WriteLine(header);
    }

    /// <summary>Appends one event: t (ms since start), n (1, 2, 3, ... per session) and type first, then the given fields.
    /// Returns n. Safe to call from any thread (screenshots are encoded in the background and then record a `shot` event).</summary>
    public int Event(string type, string? doc, params (string Key, object? Value)[] fields)
    {
        lock (_lock)
        {
            var n = ++_n;
            var e = new Dictionary<string, object?> { ["t"] = ElapsedMs, ["n"] = n, ["type"] = type };
            if (doc != null) e["doc"] = doc;
            foreach (var (k, v) in fields) if (v != null) e[k] = v;
            WriteLine(e);
            Events++;
            return n;
        }
    }

    void WriteLine(Dictionary<string, object?> e)
    {
        lock (_lock)
        {
            _out.WriteLine(JsonSerializer.Serialize(e, Json));
            _out.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock) { _out.Dispose(); }
    }
}
