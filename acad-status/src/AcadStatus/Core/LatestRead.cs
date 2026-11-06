using System;
using System.Threading.Tasks;

namespace AcadStatus.Core;

/// <summary>Runs a piece of work on a background task, one at a time, and hands the latest finished result to whoever asks. The pane's tick
/// asks for the folder's entries every second; the listing and the file reads are done by a task, so the UI thread only picks up what the
/// last task found (at most one tick old) and starts the next. If the work is still running when it is kicked again, nothing new starts.</summary>
public sealed class LatestRead<T> where T : class
{
    readonly Func<T> _work;
    readonly object _gate = new();
    Task<T>? _running;
    T? _latest;

    public LatestRead(Func<T> work) { _work = work; }

    /// <summary>A task is running now.</summary>
    public bool InFlight { get { lock (_gate) { Collect(); return _running != null; } } }

    /// <summary>The result of the last task that finished well, or null before the first. Never waits.</summary>
    public T? Latest { get { lock (_gate) { Collect(); return _latest; } } }

    /// <summary>Starts the work unless it is already running. True when it started.</summary>
    public bool Kick()
    {
        lock (_gate)
        {
            Collect();
            if (_running != null) return false;
            _running = Task.Run(_work);
            return true;
        }
    }

    // a finished task: its result becomes the latest one (a failed task keeps the previous result), and the next may start
    void Collect()
    {
        if (_running is { IsCompleted: true } t)
        {
            if (t.IsCompletedSuccessfully) _latest = t.Result;
            else _ = t.Exception;   // observed, so a failed read is not reported as an unobserved task exception
            _running = null;
        }
    }
}
