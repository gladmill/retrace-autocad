using Retrace.Engine;

namespace Retrace.Core;

// NO AutoCAD references (testable on its own). The engine runs inside the plugin: this runs engine jobs ONE AT A TIME, in the order they
// were started (a FIFO queue), on a thread-pool thread, so AutoCAD stays usable, and hands each typed EngineResult to a callback. The callback
// runs on that thread-pool thread: marshal to the UI / AutoCAD thread yourself (the callers do it with Dispatcher.BeginInvoke).

public static class EngineJobs
{
    sealed class Item(Func<CancellationToken, EngineResult> job, Action<EngineResult> onDone, string? kind)
    {
        public readonly Func<CancellationToken, EngineResult> Job = job;
        public readonly Action<EngineResult> OnDone = onDone;
        public readonly string? Kind = kind;
        public readonly CancellationTokenSource Cts = new();
    }

    const string DocsKind = "docs";

    static readonly object Gate = new();
    static readonly Queue<Item> Waiting = new();
    static Item? _current;
    static bool _worker;

    /// <summary>True while a job runs or waits in the queue.</summary>
    public static bool IsRunning { get { lock (Gate) return _worker || Waiting.Count > 0; } }

    /// <summary>Starts <paramref name="job"/> without blocking: it runs at once, or after the jobs started before it (a FIFO queue; read
    /// <see cref="IsRunning"/> before the call to tell which). Always accepts, unless <paramref name="kind"/> is given and a job of that kind
    /// is already running or queued (then false, with a message). The job gets a token that is cancelled by <see cref="Cancel"/>; the engine
    /// checks it between stages. onDone is always called exactly once per accepted job (an exception thrown by the job becomes a failed result,
    /// a job dropped by Cancel gets a cancelled result) and must not throw.</summary>
    public static bool Start(Func<CancellationToken, EngineResult> job, Action<EngineResult> onDone, out string error, string? kind = null)
    {
        error = "";
        lock (Gate)
        {
            if (kind != null && ((_current != null && _current.Kind == kind) || Waiting.Any(q => q.Kind == kind)))
            { error = "The same job is already running or waiting."; return false; }
            Waiting.Enqueue(new Item(job, onDone, kind));
            if (!_worker) { _worker = true; Task.Run(Loop); }
            return true;
        }
    }

    static void Loop()
    {
        while (true)
        {
            Item it;
            lock (Gate)
            {
                if (Waiting.Count == 0) { _worker = false; _current = null; return; }
                it = Waiting.Dequeue();
                _current = it;
            }
            EngineResult result;
            try { result = it.Job(it.Cts.Token); }
            catch (OperationCanceledException) { result = Cancelled(); }
            catch (Exception ex) { result = Failed($"{ex.GetType().Name}: {ex.Message}"); }
            lock (Gate) { _current = null; }
            it.Cts.Dispose();
            try { it.OnDone(result); } catch (Exception) { }
        }
    }

    /// <summary>Cancels the running job at its next checkpoint and drops every job still waiting (their onDone gets a cancelled result).
    /// Used at shutdown.</summary>
    public static void Cancel()
    {
        List<Item> dropped;
        lock (Gate)
        {
            try { _current?.Cts.Cancel(); } catch (ObjectDisposedException) { }
            dropped = [.. Waiting];
            Waiting.Clear();
        }
        if (dropped.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var d in dropped)
            {
                d.Cts.Dispose();
                try { d.OnDone(Cancelled()); } catch (Exception) { }
            }
        });
    }

    static EngineResult Cancelled() => new() { Cancelled = true };
    static EngineResult Failed(string error) => new() { Error = error };

    // ---- the jobs, built from the settings --------------------------------------------------------------------------

    /// <summary>Reference docs + editable user guide + How-to pages from recordings. Captures / tests folders that do not exist are left out.
    /// The settings' folders are already absolute (relative ones are resolved against the root folder, see <see cref="RetraceSettings"/>).
    /// Never throws: an invalid path is left as written and the engine reports it as a failed result.</summary>
    public static EngineOptions DocsOptions(RetraceSettings s) => new()
    {
        Input = Full(s.Source),
        Output = Full(s.Docs),
        Captures = SafeExists(s.Captures) ? [Full(s.Captures)] : [],
        Sessions = SafeExists(s.Tests) ? [Full(s.Tests)] : [],
    };

    static string Full(string p) { try { return Path.GetFullPath(p); } catch (Exception) { return p; } }
    static bool SafeExists(string p) { try { return Directory.Exists(p); } catch (Exception) { return false; } }

    /// <summary>Queues a docs build. Refused (false) while another docs build is running or waiting; WriteSteps and Export always queue.</summary>
    public static bool BuildDocs(RetraceSettings s, Action<EngineResult> onDone, out string error)
    {
        var opt = DocsOptions(s);
        if (!Start(ct => EngineRunner.BuildDocs(opt, ct), onDone, out error, DocsKind)) { error = "The docs are already being built."; return false; }
        return true;
    }

    /// <summary>One recording -> steps.md next to it (+ Word / Excel). Queued behind any running job.</summary>
    public static bool WriteSteps(string recording, bool docx, bool xlsx, Action<EngineResult> onDone, out string error) =>
        Start(_ => EngineRunner.WriteSteps(recording, docx, xlsx), onDone, out error);

    /// <summary>Word or Excel file from an existing script (a recording.jsonl, a test folder or a steps.md). Queued behind any running job.</summary>
    public static bool Export(string path, bool docx, bool xlsx, Action<EngineResult> onDone, out string error) =>
        Start(_ => EngineRunner.Export(path, docx, xlsx), onDone, out error);
}
