using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AcadStatus.Core;

/// <summary>One entry as lines of text, for the details window and the dump. <see cref="Timing"/> is the only part that changes while the
/// entry stays as it is (its age, how long it has been blocked), so a window can update it in place without rebuilding everything.</summary>
public sealed record EntryBlock(Tone Tone, string Lead, string Timing, IReadOnlyList<string> Lines)
{
    public string Header => Lead + Timing;
}

/// <summary>The plain text of all entries: what the details window shows and what ACADSTATUS and "Copy for Claude" print.</summary>
public static class StatusDump
{
    /// <summary>Ignored and skipped files listed in a dump: a folder that has seen many AutoCAD sessions holds many dead files.</summary>
    public const int MaxListed = 5;

    static List<IgnoredEntry> NewestIgnored(MergeResult merged) =>
        merged.Ignored.OrderByDescending(i => i.Entry.Updated).ThenBy(i => i.Entry.File, StringComparer.Ordinal).ToList();

    public static EntryBlock Block(MergedEntry m, DateTime nowUtc)
    {
        var e = m.Entry;
        var age = m.NoReply ? "no reply for " + CompactText.Age(m.Age) : CompactText.Age(m.Age) + " ago";
        var state = m.NoReply ? "busy (stale " + e.State + ")" : m.State;
        var lead = e.Id + "  " + state + "  ";
        var timing = age;
        if (m.State == States.Blocked && e.Since is { } since) timing += "  blocked " + CompactText.Clock(nowUtc - since);
        var lines = new List<string>();
        if (e.Summary.Length > 0) lines.Add(e.Summary);
        var who = new List<string>();
        if (!string.IsNullOrEmpty(e.Owner)) who.Add("owner " + e.Owner);
        who.Add(e.Writer.Length > 0 ? e.Writer : e.Source);
        who.Add("pid " + e.Pid);
        who.Add("ttl " + e.TtlSec + "s");
        lines.Add(string.Join(", ", who));
        if (e.Acad is { } a) AddAcad(lines, a);
        else if (e.DetailJson != null) lines.Add("detail: " + TextUtil.Cap(e.DetailJson, 200));
        return new EntryBlock(CompactText.ToneOf(m), lead, timing, lines);
    }

    static void AddAcad(List<string> lines, AcadDetail a)
    {
        var where = new List<string>();
        if (a.Doc != null) where.Add("drawing " + a.Doc + (a.Docs is > 1 ? " (" + a.Docs + " open)" : ""));
        if (!string.IsNullOrEmpty(a.CmdNames)) where.Add("command " + a.CmdNames);
        if (a.CmdActive is { } c) where.Add("CMDACTIVE " + c);
        if (where.Count > 0) lines.Add(string.Join(", ", where));
        if (a.Prompt != null) lines.Add("prompt: " + (a.Prompt.Length > 0 ? a.Prompt : "(pending, text unknown)"));
        if (a.Dialog is { } d)
        {
            // a fixed time, not "open 12s": the lines of a block do not change while the entry does not
            var since = d.Since is { } s ? ", since " + s.ToString("HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) : "";
            lines.Add("dialog: \"" + d.Title + "\" (" + (d.Class.Length > 0 ? d.Class + ", " : "") + (d.Modal ? "modal" : "not modal") + since + ")");
            foreach (var t in d.Texts) lines.Add("  text: " + TextUtil.Cap(t, 300));
            if (d.Buttons.Count > 0) lines.Add("  buttons: " + string.Join(" | ", d.Buttons));
        }
    }

    /// <summary>The same text as one block per entry, with the ignored files and the unreadable ones at the end.</summary>
    public static string Text(MergeResult merged, IReadOnlyList<Unreadable> skipped, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        if (merged.Shown.Count == 0) sb.AppendLine("No status entries.");
        foreach (var m in merged.Shown)
        {
            var b = Block(m, nowUtc);
            sb.AppendLine(b.Header);
            foreach (var l in b.Lines) sb.Append("  ").AppendLine(l);
            sb.AppendLine();
        }
        var ignored = NewestIgnored(merged);
        foreach (var i in ignored.Take(MaxListed)) sb.AppendLine("ignored: " + i.Entry.File + " (" + i.Reason + ")");
        if (ignored.Count > MaxListed) sb.AppendLine("... and " + (ignored.Count - MaxListed) + " more ignored (stale, process gone)");
        foreach (var s in skipped.Take(MaxListed)) sb.AppendLine("skipped: " + s.File + " (" + s.Reason + ")");
        if (skipped.Count > MaxListed) sb.AppendLine("... and " + (skipped.Count - MaxListed) + " more skipped");
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>The dump for ACADSTATUS and "Copy for Claude": a header, the text, then the raw JSON of every file shown or ignored.</summary>
    public static string Full(string folder, MergeResult merged, IReadOnlyList<Unreadable> skipped, DateTime nowUtc, string? paneNote = null)
    {
        var sb = new StringBuilder();
        sb.Append("AcadStatus dump ").AppendLine(AcadStatusBuilder.Iso(nowUtc));
        sb.Append("Folder: ").AppendLine(folder);
        if (!string.IsNullOrEmpty(paneNote)) sb.Append("Pane: ").AppendLine(paneNote);
        if (merged.Top is { } top) sb.Append("Top: ").AppendLine(top.Entry.Id + " " + top.State);
        sb.AppendLine();
        sb.Append(Text(merged, skipped, nowUtc));
        sb.AppendLine();
        sb.AppendLine("--- raw JSON ---");
        foreach (var m in merged.Shown) Raw(sb, m.Entry);
        foreach (var i in NewestIgnored(merged).Take(MaxListed)) Raw(sb, i.Entry);
        return sb.ToString().TrimEnd() + "\n";
    }

    static void Raw(StringBuilder sb, StatusEntry e)
    {
        sb.Append("### ").AppendLine(e.File);
        sb.AppendLine(e.Raw.Trim());
        sb.AppendLine();
    }
}
