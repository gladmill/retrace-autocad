namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). The bookkeeping behind a suite run's step timeout.

/// <summary>Which step is running and since when. A step "runs" from the moment the replay starts it until the next step starts, so a command that
/// was sent and never settles is blamed on that step, not on the one after it. Starting the step that already runs does not restart the clock.</summary>
public sealed class StepClock
{
    public StepClock(TimeSpan limit) { Limit = limit; }

    public TimeSpan Limit { get; }
    /// <summary>The 1-based number of the step that runs; 0 before the first one.</summary>
    public int Step { get; private set; }
    public DateTime Since { get; private set; }

    /// <summary>Step number <paramref name="step"/> (1-based) starts now. The clock restarts only when it is another step than the one running.</summary>
    public void Begin(int step, DateTime now)
    {
        if (step == Step) return;
        Step = step; Since = now;
    }

    /// <summary>True when the running step has taken longer than the limit.</summary>
    public bool Expired(DateTime now) => Step > 0 && now - Since >= Limit;
}

/// <summary>The first free file name of a series: `name`, `name_2`, `name_3`... Shared by the result files of a replay and of a suite.</summary>
public static class UniquePath
{
    /// <summary><paramref name="make"/> gives the path for number 1, 2, 3...; the first that does not exist is returned.</summary>
    public static string Find(Func<int, string> make, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        for (var i = 1; ; i++)
        {
            var p = make(i);
            if (!exists(p)) return p;
        }
    }

    /// <summary>`stem` + extension for number 1, `stem_2` + extension after that.</summary>
    public static string Numbered(string dir, string stem, string ext, int i) => Path.Combine(dir, i == 1 ? stem + ext : $"{stem}_{i}{ext}");
}
