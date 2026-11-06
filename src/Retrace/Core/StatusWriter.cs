namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// Writes Retrace's status board file (see <see cref="StatusBoard"/>). A failed write never throws: the first failure of a row of failures goes to
/// <paramref name="logError"/>, and the first success re-arms the log. A failed write (a reader holding the file, a moment's trouble with the disk) is tried again at the
/// next call, so one failure never lets the heartbeat slip past the file's ttl. Only after <see cref="FailuresBeforeBackoff"/> failures in a row does the writer wait
/// (3 s, then 6 s, 9 s, at most <see cref="MaxBackoff"/>; forced writes too), so a folder that cannot be written (a share that is gone) costs the UI thread a failed attempt
/// every few seconds at most, not one every 200 ms. A success starts over. A write that would only repeat the last entry is skipped unless it is the heartbeat
/// (<c>force</c>): the file's `updated` has to move every few seconds, but a replay's 200 ms ticks do not each touch the disk. Not thread safe: the host calls it from the
/// UI thread only.
/// </summary>
public sealed class StatusWriter
{
    /// <summary>Failures in a row after which the writer starts to wait between tries.</summary>
    public const int FailuresBeforeBackoff = 3;
    /// <summary>The longest wait between two tries.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);
    static readonly TimeSpan BackoffStep = TimeSpan.FromSeconds(3);

    /// <summary>How long the writer waits after the <paramref name="failures"/>th failure in a row: none for the first two, then 3 s, 6 s, 9 s, 10 s.</summary>
    public static TimeSpan BackoffAfter(int failures) =>
        failures < FailuresBeforeBackoff ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, BackoffStep.Ticks * (failures - FailuresBeforeBackoff + 1)));

    readonly string _folder, _writer;
    readonly int _pid;
    readonly DateTime? _pidStart;
    readonly Func<DateTime> _utcNow;
    readonly Action<Exception> _logError;
    StatusEntry? _last;      // what the file holds now; null = nothing written yet, or the last write failed
    string? _sinceKey;
    DateTime _since, _retryAfter = DateTime.MinValue;
    int _failures;           // failed writes in a row
    bool _logged;
    bool _folderReady;       // the folder was there at the last write: it is created again only after a failure

    /// <param name="pidStart">when process <paramref name="pid"/> started (UTC), written as `pidStart`; null when it is not known</param>
    public StatusWriter(string folder, int pid, string writer, Action<Exception>? logError = null, Func<DateTime>? utcNow = null, DateTime? pidStart = null)
    {
        _folder = folder; _pid = pid; _writer = writer; _pidStart = pidStart;
        _logError = logError ?? (_ => { });
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string FilePath => Path.Combine(_folder, StatusBoard.FileName(_pid));

    /// <summary>Writes <paramref name="entry"/>. False when nothing was written: the same entry as before and not forced, the wait after repeated failures is not over, or this
    /// write failed. `since` moves when the state, the mode or the test changes. <paramref name="final"/> (the last word before Retrace unloads) tries even inside a wait.
    /// Never throws.</summary>
    public bool Publish(StatusEntry entry, bool force = false, bool final = false)
    {
        try
        {
            var now = _utcNow();
            var key = entry.State + "|" + entry.Mode + "|" + entry.Test;
            if (key != _sinceKey) { _sinceKey = key; _since = now; }
            if (!final && now < _retryAfter) return false;
            if (!force && _last != null && _last.Equals(entry)) return false;
            StatusBoard.WriteAtomic(_folder, StatusBoard.FileName(_pid), StatusBoard.Build(entry, _pid, _writer, now, _since, pidStart: _pidStart), _pid, createFolder: !_folderReady);
            _last = entry; _logged = false; _failures = 0; _retryAfter = DateTime.MinValue; _folderReady = true;
            return true;
        }
        catch (Exception ex)
        {
            _last = null;   // the file is not what we think it is: write again at the next chance
            _folderReady = false;
            _failures++;
            try { _retryAfter = _utcNow() + BackoffAfter(_failures); } catch (Exception) { _retryAfter = DateTime.UtcNow + BackoffAfter(_failures); }
            if (!_logged) { _logged = true; try { _logError(ex); } catch (Exception) { } }
            return false;
        }
    }

    /// <summary>The last word: state stopped. Used when Retrace unloads (<paramref name="final"/>: tried even inside a wait) and when the setting is turned off (the host
    /// calls it again until it returns true). True when it was written.</summary>
    public bool Stop(string text = "Retrace unloaded", bool final = false) => Publish(StatusBoard.StoppedEntry(text), force: true, final: final);
}
