using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AcadStatus.Core;

/// <summary>Dot colour of the pane. Green idle, blue running, amber waiting-input and needs-person, red blocked / busy / error,
/// grey for the rest (unknown states, nothing to show, stopped).</summary>
public enum Tone { Green, Blue, Amber, Red, Grey }

/// <summary>What the pane shows: a dot, at most <see cref="CompactText.MaxChars"/> characters, and one tooltip line per entry.</summary>
public sealed record PaneView(Tone Tone, string Text, string Tooltip);

/// <summary>The short texts of the pane and the tooltip.</summary>
public static class CompactText
{
    public const int MaxChars = 18;
    const int MaxTooltipLines = 10;

    public static Tone ToneOf(MergedEntry e) => e.Unknown ? Tone.Grey : e.State switch
    {
        States.Idle => Tone.Green,
        States.Running => Tone.Blue,
        States.WaitingInput or States.NeedsPerson => Tone.Amber,
        States.Blocked or States.Busy or States.Error => Tone.Red,
        _ => Tone.Grey,
    };

    /// <summary>"12s", "2m05s", "1h02m", "3d04h": how old something is.</summary>
    public static string Age(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var s = (long)t.TotalSeconds;
        if (s < 60) return s + "s";
        if (s < 3600) return string.Format(CultureInfo.InvariantCulture, "{0}m{1:00}s", s / 60, s % 60);
        if (s < 86400) return string.Format(CultureInfo.InvariantCulture, "{0}h{1:00}m", s / 3600, s % 3600 / 60);
        return string.Format(CultureInfo.InvariantCulture, "{0}d{1:00}h", s / 86400, s % 86400 / 3600);
    }

    /// <summary>"2:31" (minutes:seconds), "1:02:03" from an hour on.</summary>
    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var s = (long)t.TotalSeconds;
        return s >= 3600
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", s / 3600, s % 3600 / 60, s % 60)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", s / 60, s % 60);
    }

    /// <summary>The text beside the dot. Empty for idle. Never longer than <see cref="MaxChars"/>.</summary>
    public static string TextOf(MergedEntry e, DateTime nowUtc)
    {
        if (e.NoReply) return TextUtil.Cap("No reply " + Clock(e.Age), MaxChars);
        if (e.Unknown) return TextUtil.Cap(e.State, MaxChars);
        switch (e.State)
        {
            case States.Idle:
                return "";
            case States.Blocked:
            {
                var label = string.IsNullOrWhiteSpace(e.Entry.Short) ? "Blocked" : e.Entry.Short!;
                if (e.Entry.Since is not { } since) return TextUtil.Cap(label, MaxChars);
                var clock = Clock(nowUtc - since);
                return TextUtil.Cap(label, MaxChars - clock.Length - 1) + " " + clock;
            }
            default:
                if (!string.IsNullOrWhiteSpace(e.Entry.Short)) return TextUtil.Cap(e.Entry.Short, MaxChars);
                return e.State switch
                {
                    States.NeedsPerson => "Needs person",
                    States.WaitingInput => "Input",
                    States.Busy => "Busy",
                    States.Error => "Error",
                    States.Running => "Running",
                    States.Stopped => "Stopped",
                    _ => TextUtil.Cap(e.State, MaxChars),
                };
        }
    }

    /// <summary>"acad-4812: waiting-input - Input: OFFSET"; blocked adds how long, a stale entry says so.</summary>
    public static string TooltipLine(MergedEntry e, DateTime nowUtc)
    {
        var sb = new StringBuilder(e.Entry.Id).Append(": ");
        if (e.NoReply) sb.Append("no reply for ").Append(Age(e.Age)).Append(" (last: ").Append(e.Entry.State).Append(')');
        else
        {
            sb.Append(e.State);
            if (e.State == States.Blocked && e.Entry.Since is { } since) sb.Append(' ').Append(Clock(nowUtc - since));
            else if (e.Stale) sb.Append(" (stale ").Append(Age(e.Age)).Append(')');
        }
        if (!string.IsNullOrWhiteSpace(e.Entry.Summary)) sb.Append(" - ").Append(TextUtil.Cap(e.Entry.Summary, 120));
        return sb.ToString();
    }

    public static PaneView Build(MergeResult merged, DateTime nowUtc)
    {
        if (merged.Top is not { } top)
            return new PaneView(Tone.Grey, "", "AcadStatus: no status entries");
        var lines = new List<string>();
        foreach (var e in merged.Shown)
        {
            if (lines.Count == MaxTooltipLines) { lines.Add("... and " + (merged.Shown.Count - MaxTooltipLines) + " more"); break; }
            lines.Add(TooltipLine(e, nowUtc));
        }
        return new PaneView(ToneOf(top), TextOf(top, nowUtc), string.Join("\n", lines));
    }
}
