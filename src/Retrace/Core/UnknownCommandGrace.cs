namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>What <see cref="UnknownCommandGrace.Poll"/> says.</summary>
public enum UnknownCommandVerdict
{
    /// <summary>Nothing reported (or already dealt with).</summary>
    None,
    /// <summary>An unknown command that the replay sent is awaited: the replay holds still (no next step).</summary>
    Waiting,
    /// <summary>The command started after being reported as unknown. Returned once, so the replay logs it once.</summary>
    Recovered,
    /// <summary>The replay ends with ERROR (<see cref="UnknownCommandPoll.Message"/>). Stays failed.</summary>
    Failed,
}

/// <summary>The answer of one poll. <see cref="Command"/> and <see cref="Waited"/> are set for Recovered (the command as AutoCAD reported it, and
/// how long it took to start); <see cref="Message"/> for Failed ("unknown command X").</summary>
public readonly record struct UnknownCommandPoll(UnknownCommandVerdict Verdict, string? Command = null, TimeSpan Waited = default, string? Message = null);

/// <summary>
/// What a replay does when AutoCAD raises <c>Document.UnknownCommand</c>. AutoCAD reports some commands as unknown a moment before it runs them
/// (UNITS on a fresh start of AutoCAD 2026 raises it for the first UNITS of a session, then starts and opens its dialog, probably because the command
/// is loaded on first use; the recording shows <c>cmd_unknown</c> right before <c>cmd_start</c>). A replay that turned every unknown command into ERROR
/// failed the first UNITS of a session.
/// <para>The replay tells this class every command it sends (<see cref="Sent"/>: typed, clicked, or started by a palette click; never what a person
/// types). An unknown command that names a command sent in the last <see cref="SentTtl"/> and not started yet gets a grace: until a command of the same
/// name starts, or <see cref="Window"/> has passed, whichever is first. A start ends the wait even when the tick that would have seen it came late (a
/// command that loads on first use can probably hold the UI thread for longer than the window). Every other unknown command is an error at once, as
/// before, and so is a second, different one while one is awaited (a repeat of the awaited name is ignored).</para>
/// Plain data fed from the event handlers (<see cref="Unknown"/>, <see cref="Started"/>: field writes only) and from the code that sends a command, and
/// read by the replay's timer (<see cref="Poll"/>); the time is passed in so a test controls it.
/// </summary>
public sealed class UnknownCommandGrace
{
    /// <summary>How long an unknown command may take to start.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(2);
    /// <summary>How long a sent command counts as "just sent" if it does not start.</summary>
    public static readonly TimeSpan DefaultSentTtl = TimeSpan.FromSeconds(5);

    readonly List<(string Key, DateTime At)> _sent = new();   // commands the replay sent that have not started yet
    string? _awaited;         // the unknown command being waited for, as AutoCAD reported it
    string _awaitedKey = "";
    DateTime _since;          // when it was reported
    UnknownCommandPoll? _recovered;   // returned once by Poll
    string? _failure;

    public UnknownCommandGrace(TimeSpan? window = null, TimeSpan? sentTtl = null)
    {
        Window = window ?? DefaultWindow;
        SentTtl = sentTtl ?? DefaultSentTtl;
    }

    public TimeSpan Window { get; }
    public TimeSpan SentTtl { get; }

    /// <summary>The replay sent <paramref name="command"/> (a step's command; null or blank for a step with none, e.g. a LISP call: nothing is noted).
    /// Sending it again refreshes the time. The entry goes when that command starts, or <see cref="SentTtl"/> after it was sent.</summary>
    public void Sent(string? command, DateTime now)
    {
        var key = CommandName.Key(command);
        if (key.Length == 0) return;
        Prune(now);
        _sent.RemoveAll(x => x.Key == key);
        _sent.Add((key, now));
    }

    /// <summary>AutoCAD reported <paramref name="name"/> (the event's <c>GlobalCommandName</c>) as unknown.</summary>
    public void Unknown(string? name, DateTime now)
    {
        if (_failure != null) return;
        var key = CommandName.Key(name);
        if (_awaited != null)
        {
            if (key != _awaitedKey) Fail(name);   // another command: an error at once; a repeat of the awaited one is ignored (the wait is not extended)
            return;
        }
        Prune(now);
        if (key.Length == 0 || !_sent.Exists(x => x.Key == key)) { Fail(name); return; }
        _awaited = name; _awaitedKey = key; _since = now;
    }

    /// <summary>A command started (<c>CommandWillStart</c>, its <c>GlobalCommandName</c>): it is no longer "just sent", and the one awaited ends the wait.</summary>
    public void Started(string? name, DateTime now)
    {
        var key = CommandName.Key(name);
        if (key.Length == 0) return;
        _sent.RemoveAll(x => x.Key == key);
        if (_awaited == null || _failure != null || key != _awaitedKey) return;
        _recovered = new UnknownCommandPoll(UnknownCommandVerdict.Recovered, _awaited, now - _since);
        _awaited = null; _awaitedKey = "";
    }

    /// <summary>Called by the replay's timer. Failed stays Failed; Recovered is returned once; Waiting turns Failed when the window has passed.</summary>
    public UnknownCommandPoll Poll(DateTime now)
    {
        if (_failure != null) return new UnknownCommandPoll(UnknownCommandVerdict.Failed, Message: _failure);
        if (_recovered is { } r) { _recovered = null; return r; }
        Prune(now);
        if (_awaited == null) return new UnknownCommandPoll(UnknownCommandVerdict.None);
        if (now - _since < Window) return new UnknownCommandPoll(UnknownCommandVerdict.Waiting, _awaited);
        Fail(_awaited);
        return new UnknownCommandPoll(UnknownCommandVerdict.Failed, Message: _failure);
    }

    void Fail(string? name) { _failure = $"unknown command {name}"; _awaited = null; _awaitedKey = ""; }

    void Prune(DateTime now) => _sent.RemoveAll(x => now - x.At >= SentTtl);
}
