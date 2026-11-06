using System;

namespace AcadStatus.Core;

/// <summary>The states of CONTRACT.md and their display priority.</summary>
public static class States
{
    public const string Blocked = "blocked", NeedsPerson = "needs-person", WaitingInput = "waiting-input", Busy = "busy",
        Error = "error", Running = "running", Idle = "idle", Stopped = "stopped";

    static readonly string[] Known = { Blocked, NeedsPerson, WaitingInput, Busy, Error, Running, Idle, Stopped };

    /// <summary>Trimmed, lower case: how a state is compared.</summary>
    public static string Normalize(string? state) => (state ?? "").Trim().ToLowerInvariant();

    public static bool IsKnown(string normalized) => Array.IndexOf(Known, normalized) >= 0;

    /// <summary>Display priority, higher first: blocked, needs-person, waiting-input, busy, error, running, [unknown], idle, stopped.
    /// An unknown state sits just above idle.</summary>
    public static int Rank(string normalized) => normalized switch
    {
        Blocked => 80,
        NeedsPerson => 70,
        WaitingInput => 60,
        Busy => 50,
        Error => 40,
        Running => 30,
        Idle => 10,
        Stopped => 0,
        _ => 20,
    };
}
