using System.Text;

namespace Retrace.Core;

/// <summary>Which prompt a command asked for last: fed by the editor's Prompting* events (kind and message only; reading more of the options made AutoCAD reject picks, see the recorder).
/// There is no "answered" event to subscribe to: a jig raises a Prompted event for its first sample at once and then waits, so a result does not mean the prompt is over. A prompt counts as
/// the current one until the replay types an answer (the next one must be asked after that) or the command ends (the caller checks that a command is running).</summary>
public sealed class PromptWatch
{
    string? _kind, _message;
    DateTime _promptingAt = DateTime.MinValue;

    public void Prompting(string kind, string? message, DateTime now) { _kind = kind; _message = message; _promptingAt = now; }

    /// <summary>The newest prompt, null when none was asked for after <paramref name="after"/> (the time the replay typed its last answer).</summary>
    public (string Kind, string? Message)? Last => _kind == null ? null : (_kind, _message);

    public (string Kind, string? Message)? Open(DateTime after) => _kind == null || _promptingAt <= after ? null : (_kind, _message);
}

/// <summary>What a script chain's command is given once the click's dialogs are done: the answers a person gave while recording (point, angle, number, text, keyword, Enter), typed in order, each when
/// the command is waiting at a prompt of the same kind and message (<see cref="PromptWatch"/>), because the replay cannot know which prompt of the script is waiting any other way. An answer
/// that cannot be typed safely (a selection or an object pick: it needs the view and handles; Esc: it would end the script; text with a space: AutoCAD takes it as Enter) makes the whole group
/// <see cref="Unsafe"/>: nothing is typed and the replay stops for a person, as before. If the prompt does not come within <see cref="WaitSeconds"/> of the last progress, <see cref="DeliveryKind.TimedOut"/>.</summary>
public sealed class ChainDelivery
{
    public const double WaitSeconds = 20;
    /// <summary>Shortest time (s) between two typed answers: the first has to be taken before the next prompt can be told apart from the same one still open.</summary>
    public const double MinGapSeconds = 0.3;

    readonly IReadOnlyList<Answer> _answers;
    int _next;
    DateTime _progress, _lastTyped = DateTime.MinValue;
    DateTime _promptsAfter;   // only a prompt asked after this counts: the start (the dialog before the answers opened), then the newest typed answer

    /// <param name="promptsAfter">Only a prompt asked after this time counts for the first answer (the time the dialog the answers follow opened: the command is blocked while it is open, so
    /// whatever it asked before is an earlier prompt); <paramref name="now"/> when not given.</param>
    public ChainDelivery(IReadOnlyList<Answer> answers, DateTime now, DateTime? promptsAfter = null) { _answers = answers; _progress = now; _promptsAfter = promptsAfter ?? now; }

    public int Remaining => _answers.Count - _next;

    /// <summary>Why <paramref name="due"/> cannot be typed, or null when every answer can.</summary>
    public static string? Unsafe(IReadOnlyList<Answer> due)
    {
        foreach (var a in due)
        {
            // a recording with no `input` flag cannot say which prompts a person answered (every prompt looks like one): the script's own answers would be typed a second time
            if (a.Input != true) return $"{ReplayRules.AnswerText(a)} is not marked as a person's answer (the recording has no input flags)";
            if (a.Kind is "selection" or "entity" or "nestedEntity") return $"{ReplayRules.AnswerText(a)} is an object pick, which needs the view it was made in";
            if (a.Status == "Cancel") return $"{ReplayRules.AnswerText(a)} was Esc, which would end the script";
            if (a.Status is not ("OK" or "Keyword" or "None")) return $"{ReplayRules.AnswerText(a)} has status {a.Status}";
            var lines = Plan.TypedLines(a);
            if (lines == null) return $"{ReplayRules.AnswerText(a)} cannot be typed";
            if (a.Kind is "string" or "keyword" && lines.Any(l => l.IndexOf(' ') >= 0)) return $"{ReplayRules.AnswerText(a)} has a space, which AutoCAD takes as Enter";
        }
        return null;
    }

    /// <summary>The text that types <paramref name="a"/>: its lines, each followed by Enter.</summary>
    public static string TextOf(Answer a)
    {
        var sb = new StringBuilder();
        foreach (var l in Plan.TypedLines(a) ?? new List<string>()) sb.Append(l).Append('\n');
        return sb.ToString();
    }

    /// <summary>Whether the open prompt is the one <paramref name="a"/> answers: the same message (the recorder writes it trimmed), the same kind when either message is unknown.</summary>
    public static bool Matches(Answer a, (string Kind, string? Message) open) =>
        string.IsNullOrWhiteSpace(a.Message) || string.IsNullOrWhiteSpace(open.Message)
            ? a.Kind == open.Kind
            : string.Equals(a.Message.Trim(), open.Message.Trim(), StringComparison.OrdinalIgnoreCase);   // the message says which prompt; the kind may differ (an angle with a base point asks for a point inside)

    /// <summary>One look: the next answer to type now, wait, done, or give up.</summary>
    public DeliveryTick Tick(DateTime now, PromptWatch watch, bool commandActive = true)
    {
        if (_next >= _answers.Count) return new(DeliveryKind.Done, null, null);
        var a = _answers[_next];
        if (commandActive && (now - _lastTyped).TotalSeconds >= MinGapSeconds && watch.Open(_promptsAfter) is { } open && Matches(a, open))
        {
            _next++; _lastTyped = now; _promptsAfter = now; _progress = now;
            return new(DeliveryKind.Type, TextOf(a), a);
        }
        if ((now - _progress).TotalSeconds >= WaitSeconds) return new(DeliveryKind.TimedOut, null, a);
        return new(DeliveryKind.Wait, null, a);
    }

    /// <summary>What the replay says when the prompt for <paramref name="a"/> did not come: the existing pause (a person gives the rest).</summary>
    public string TimedOutMessage(Step s) =>
        $"the script \"{ReplayRules.ButtonName(s)}\" started did not ask for {ReplayRules.AnswerText(_answers[_next])} within {(int)WaitSeconds} s" + (Remaining > 1 ? $" ({Remaining} answers left to type)" : "") + "; give it, then press Next step";
}

public enum DeliveryKind { Wait, Type, Done, TimedOut }

public readonly record struct DeliveryTick(DeliveryKind Kind, string? Text, Answer? Answer);
