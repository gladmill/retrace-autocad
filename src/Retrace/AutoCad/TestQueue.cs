using System.Windows.Threading;

namespace Retrace.AutoCad;

/// <summary>What runs a <see cref="TestQueue"/> (a Tdoc run, a suite run): it says things, hears about skipped tests and the end.</summary>
interface ITestQueueOwner
{
    void Say(string message);
    /// <summary>The test at <paramref name="index"/> was sent to RETRACERUN but did not start (<paramref name="why"/>); the queue goes on.</summary>
    void Skipped(int index, string path, string why);
    /// <summary>The last test is done, or the queue was stopped: write what there is.</summary>
    void Complete();
    /// <summary>The queue cannot go on (no drawing is open to type the next test into): the run ends here, and the owner says why.</summary>
    void Aborted(string reason);
    /// <summary>Asked before each test and before the end, until it says Go (a suite closes the drawings of the test before). Asked again every
    /// 500 ms while it says Wait; Halt means the owner has ended the run itself.</summary>
    QueueGate Gate();
}

enum QueueGate { Go, Wait, Halt }

/// <summary>
/// The queue of tests behind a Tdoc run and a suite run: it types `^C^C_RETRACERUN ` into the active drawing for one test at a time (Commands.Test asks
/// <see cref="TakeExpected"/> for the recording instead of asking a person), waits for the replayer to start (a 90 s watchdog skips a test whose
/// command failed before the replay began), and posts the next test when the owner says the one before is finished. One queue at a time. UI thread only.
/// </summary>
sealed class TestQueue
{
    public static TestQueue? Active { get; private set; }

    public enum State { Idle, Sent, Running }

    /// <summary>A test that was sent to RETRACERUN must start within this time, else it is skipped.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    readonly ITestQueueOwner _owner;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _taken;

    public TestQueue(IReadOnlyList<string> recordings, ITestQueueOwner owner)
    {
        Recordings = recordings; _owner = owner;
        _watch.Tick += (_, _) => Log.Safe("queue watch", Watch);
    }

    public IReadOnlyList<string> Recordings { get; }
    public int Index { get; private set; } = -1;
    /// <summary>The test being run (or sent, or the last one between two tests) as a number, 1-based; 1 before the first is sent. With <see cref="Total"/> for the status board ("Suite 2/3").</summary>
    public int Position => Math.Max(Index + 1, 1);
    public int Total => Recordings.Count;
    public State Current { get; private set; } = State.Idle;
    public string? SentPath { get; private set; }
    public DateTime SentAt { get; private set; }
    /// <summary>No more tests are started (Stop).</summary>
    public bool Stopped { get; set; }

    public void Begin() { Active = this; _watch.Start(); Next(); }

    /// <summary>Ends the queue: no test is started any more.</summary>
    public void End()
    {
        if (Active == this) Active = null;
        _watch.Stop();
    }

    /// <summary>For <see cref="Commands.Test"/>: the recording of the test the queue just sent to RETRACERUN (once), else null.</summary>
    public static string? TakeExpected()
    {
        var q = Active;
        if (q == null || q.Current != State.Sent || q._taken) return null;
        q._taken = true;
        return q.SentPath;
    }

    /// <summary>A test was sent to RETRACERUN and has not been taken yet: a RETRACERUN now is the queue's own.</summary>
    public static bool Expecting => Active is { Current: State.Sent, _taken: false };

    /// <summary>True when <paramref name="path"/> is the test the queue sent and is waiting to see start.</summary>
    public bool IsSent(string path) => Current == State.Sent && string.Equals(path, SentPath, StringComparison.OrdinalIgnoreCase);

    public void MarkRunning() => Current = State.Running;

    /// <summary>The test that was sent could not be started (the recording is gone, the new drawing never became active...): skip it.</summary>
    public static void StartFailed(string why)
    {
        if (Active is { Current: State.Sent } q) q.Skip(why);
    }

    /// <summary>The replayer ended the test: the next one is posted.</summary>
    public void Finished()
    {
        Current = State.Idle;
        Post(Next);
    }

    void Skip(string why)
    {
        _owner.Skipped(Index, SentPath ?? "", why);
        Current = State.Idle;
        Post(Next);
    }

    void Post(Action a) => _ui.BeginInvoke(DispatcherPriority.Background, () => Log.Safe("queue next", a));

    public void Next()
    {
        if (Active != this) return;
        switch (_owner.Gate())
        {
            case QueueGate.Halt: return;
            case QueueGate.Wait:
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                t.Tick += (_, _) => { t.Stop(); Log.Safe("queue gate", Next); };
                t.Start();
                return;
        }
        if (Active != this) return;
        if (Stopped || ++Index >= Recordings.Count) { _owner.Complete(); return; }
        var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (doc == null) { Index--; _owner.Aborted("no drawing is open"); return; }
        SentPath = Recordings[Index]; _taken = false; SentAt = DateTime.Now; Current = State.Sent;
        // Test() asks TakeExpected for the recording; this is what the Run test button types, ^C^C first: the test before may have left a
        // prompt open (a step that failed), and the text would answer it instead of starting the next test
        doc.SendStringToExecute("\u0003\u0003_RETRACERUN ", true, false, false);
        StatusBoardHost.Changed();   // the status board: "Suite i/n" moves to the next test
    }

    void Watch()
    {
        if (Current == State.Sent && Replayer.Current == null && DateTime.Now - SentAt > StartTimeout) Skip("it did not start within 90 s");
    }
}
