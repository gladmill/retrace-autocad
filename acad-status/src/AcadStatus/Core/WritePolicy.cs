using System;
using System.IO;
using System.Text.Json;

namespace AcadStatus.Core;

/// <summary>When the writer writes: when the content changed, and when <see cref="RefreshSec"/> seconds have passed since the last write.
/// The timer ticks every second and a tick can come a little early, so 1.5 s means a refresh on every second tick: the gap between two
/// writes is about 2 s, never 3 s. <c>updated</c> is written to the whole second, so a reader sees it up to a second older than it is;
/// with ttlSec 10 that leaves room for several missed ticks before a live file reads as stale.</summary>
public static class WritePolicy
{
    public const double RefreshSec = 1.5;

    public static bool ShouldWrite(string? lastKey, DateTime? lastWriteUtc, string newKey, DateTime nowUtc)
    {
        if (lastKey == null || lastWriteUtc == null) return true;
        if (!string.Equals(lastKey, newKey, StringComparison.Ordinal)) return true;
        if (nowUtc < lastWriteUtc.Value) return true;   // the clock went backward: waiting for it to catch up would leave the file stale
        return (nowUtc - lastWriteUtc.Value).TotalSeconds >= RefreshSec;
    }
}

/// <summary>What the writer does when a write fails (CONTRACT.md, "Files"): a failed replace is tried again at the next refresh, which is the
/// next tick, and the writer backs off, at most 10 s, only after several failures in a row. A success starts over.</summary>
public sealed class WriteRetry
{
    /// <summary>The failure in a row that starts the back-off: the third. The first two are retried at the next refresh.</summary>
    public const int FailuresBeforeBackoff = 3;
    public const double MaxBackoffSec = 10;

    int _failures;
    DateTime? _notBefore;

    /// <summary>Failures in a row so far.</summary>
    public int Failures => _failures;

    /// <summary>May the writer try now. Always, until it has failed <see cref="FailuresBeforeBackoff"/> times in a row.</summary>
    public bool MayAttempt(DateTime nowUtc) => _notBefore is not { } t || nowUtc >= t || t - nowUtc > TimeSpan.FromSeconds(MaxBackoffSec);

    public void Succeeded()
    {
        _failures = 0;
        _notBefore = null;
    }

    /// <summary>A write failed: after the third failure in a row the next attempt waits 2 s, after the fourth 4, then 8, and 10 s at most.</summary>
    public void Failed(DateTime nowUtc)
    {
        _failures++;
        if (_failures < FailuresBeforeBackoff) return;
        var wait = Math.Min(MaxBackoffSec, Math.Pow(2, _failures - FailuresBeforeBackoff + 1));
        _notBefore = nowUtc.AddSeconds(wait);
    }
}

/// <summary>User settings: only whether the pane is shown. A missing or broken file means "shown".</summary>
public static class PaneSetting
{
    public static bool Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return !(doc.RootElement.ValueKind == JsonValueKind.Object
                     && doc.RootElement.TryGetProperty("pane", out var p) && p.ValueKind == JsonValueKind.False);
        }
        catch { return true; }
    }

    public static void Save(string path, bool shown)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);   // rare (a user's click), unlike the status file's
        AtomicFile.Write(path, shown ? "{ \"pane\": true }\n" : "{ \"pane\": false }\n", Environment.ProcessId);
    }
}
