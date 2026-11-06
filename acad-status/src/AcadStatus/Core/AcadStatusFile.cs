using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AcadStatus.Core;

/// <summary>One <c>acad-&lt;pid&gt;.json</c> to write. <see cref="ContentKey"/> is built from the fields, without <c>updated</c>, to tell whether
/// anything changed; <see cref="Entry"/> is the same content as the merge reads it (so the pane never parses its own file back);
/// <see cref="Json"/> is the file text, rendered the first time it is asked for (only when the file is written or dumped).</summary>
public sealed class AcadStatusDoc
{
    readonly Func<string> _render;
    string? _json;

    public AcadStatusDoc(string contentKey, StatusEntry entry, Func<string> render)
    {
        ContentKey = contentKey; Entry = entry; _render = render;
    }

    public string ContentKey { get; }
    /// <summary>The entry as a reader would parse it from the file (state, short, summary, since, detail), but with no raw text
    /// (<see cref="StatusEntry.Raw"/> is empty). The one place those values live.</summary>
    public StatusEntry Entry { get; }
    public string Json => _json ??= _render();
}

/// <summary>Builds the <c>acad</c> file (CONTRACT.md, "detail of acad") from snapshots. Keeps when the state began.</summary>
public sealed class AcadStatusBuilder
{
    public const string Source = "acad";
    /// <summary>The writer refreshes every 2 s at most (see <see cref="WritePolicy"/>); the ttl leaves room for a few missed ticks.</summary>
    public const int TtlSec = 10;

    readonly string _writer;
    readonly int _pid;
    readonly DateTime? _pidStart;
    string? _stateKey;
    DateTime _since;

    /// <param name="pidStart">When this process started (UTC), written as <c>pidStart</c> so readers can tell the pid has been reused.</param>
    public AcadStatusBuilder(string writer, int pid, DateTime? pidStart = null) { _writer = writer; _pid = pid; _pidStart = pidStart; }

    public string FileName => StatusFiles.FileFor(Source, _pid);

    public AcadStatusDoc Build(Snapshot s, DateTime nowUtc)
    {
        var state = StateRules.Decide(s, nowUtc);
        // blocked and waiting on a dialog began when the dialog opened; every other state when it was first seen here
        var key = state + "|" + (s.Dialog?.Handle ?? 0);
        if (key != _stateKey)
        {
            _stateKey = key;
            _since = s.Dialog != null && s.DialogSince is { } d ? d : nowUtc;
        }
        var (summary, shortText) = Compose(state, s);
        return Make(state, shortText, summary, _since, s, nowUtc);
    }

    /// <summary>The file written when AutoCAD closes.</summary>
    public AcadStatusDoc BuildStopped(DateTime nowUtc)
    {
        _stateKey = States.Stopped;
        _since = nowUtc;
        return Make(States.Stopped, null, "AutoCAD closed", nowUtc, null, nowUtc);
    }

    AcadStatusDoc Make(string state, string? shortText, string summary, DateTime since, Snapshot? s, DateTime nowUtc)
    {
        AcadDetail? detail = null;
        if (s != null)
        {
            DialogDetail? dialog = s.Dialog is { } d
                ? new DialogDetail(d.Title, d.Class, d.Modal, d.Texts, d.Buttons, s.DialogSince ?? since)
                : null;
            // prompt: null when none is pending (the field is then absent), "" when one is pending but its text is unknown (CONTRACT.md)
            detail = new AcadDetail(
                string.IsNullOrEmpty(s.Doc) ? null : s.Doc, s.Docs, s.CmdActive, string.IsNullOrEmpty(s.CmdNames) ? null : s.CmdNames,
                s.Prompt, dialog);
        }
        var entry = new StatusEntry(FileName, Source, _writer, _pid, nowUtc, TtlSec, state, summary, string.IsNullOrEmpty(shortText) ? null : shortText,
            since, null, null, detail, "", _pidStart);
        return new AcadStatusDoc(ContentKey(state, shortText, summary, since, detail), entry,
            () => RenderJson(state, shortText, summary, since, detail, nowUtc));
    }

    /// <summary>Everything the file says except <c>updated</c>, as one string.</summary>
    static string ContentKey(string state, string? shortText, string summary, DateTime since, AcadDetail? a)
    {
        const char Sep = '\u0001';
        var sb = new StringBuilder();
        sb.Append(state).Append(Sep).Append(shortText).Append(Sep).Append(summary).Append(Sep).Append(since.Ticks / TimeSpan.TicksPerSecond);
        if (a == null) return sb.ToString();
        sb.Append(Sep).Append(a.Doc).Append(Sep).Append(a.Docs).Append(Sep).Append(a.CmdActive).Append(Sep).Append(a.CmdNames).Append(Sep).Append(a.Prompt == null ? "-" : "p" + a.Prompt);   // "" (pending, no text) is not "none"
        if (a.Dialog is { } d)
        {
            sb.Append(Sep).Append(d.Title).Append(Sep).Append(d.Class).Append(Sep).Append(d.Modal).Append(Sep).Append(d.Since?.Ticks / TimeSpan.TicksPerSecond);
            foreach (var t in d.Texts) sb.Append(Sep).Append('t').Append(t);
            foreach (var b in d.Buttons) sb.Append(Sep).Append('b').Append(b);
        }
        return sb.ToString();
    }

    /// <summary>Every string of the file goes through this: Utf8JsonWriter throws on a lone surrogate, and a file that cannot be rendered is a
    /// file that stops being written (readers would see the writer as busy). Text that comes from AutoCAD windows can hold anything.</summary>
    static string S(string? text) => TextUtil.Sanitize(text);

    string RenderJson(string state, string? shortText, string summary, DateTime since, AcadDetail? a, DateTime updated)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("source", Source);
            w.WriteString("writer", S(_writer));
            w.WriteNumber("pid", _pid);
            if (_pidStart is { } start) w.WriteString("pidStart", Iso(start));
            w.WriteString("updated", Iso(updated));
            w.WriteNumber("ttlSec", TtlSec);
            w.WriteString("state", S(state));
            if (!string.IsNullOrEmpty(shortText)) w.WriteString("short", S(shortText));
            w.WriteString("summary", S(summary));
            w.WriteString("since", Iso(since));
            if (a != null)
            {
                w.WriteStartObject("detail");
                if (a.Doc != null) w.WriteString("doc", S(a.Doc));
                w.WriteNumber("docs", a.Docs ?? 0);
                w.WriteNumber("cmdActive", a.CmdActive ?? 0);
                w.WriteString("cmdNames", S(a.CmdNames));
                if (a.Prompt != null) w.WriteString("prompt", S(a.Prompt));
                if (a.Dialog is { } d)
                {
                    w.WriteStartObject("dialog");
                    w.WriteString("title", S(d.Title));
                    w.WriteString("class", S(d.Class));
                    w.WriteBoolean("modal", d.Modal);
                    w.WriteStartArray("texts");
                    foreach (var t in d.Texts) w.WriteStringValue(S(t));
                    w.WriteEndArray();
                    w.WriteStartArray("buttons");
                    foreach (var b in d.Buttons) w.WriteStringValue(S(b));
                    w.WriteEndArray();
                    w.WriteString("since", Iso(d.Since ?? since));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>UTC ISO 8601 with Z, to the second.</summary>
    public static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The summary line (at most 200 characters) and the pane text (at most 18) for a state.</summary>
    public static (string Summary, string? Short) Compose(string state, Snapshot s)
    {
        var cmd = StateRules.FirstCommand(s.CmdNames);
        switch (state)
        {
            case States.Blocked:
            case States.WaitingInput when s.Dialog != null:
            {
                var d = s.Dialog!;
                var head = state == States.Blocked ? "Blocked by dialog" : "Dialog";
                var wait = state == States.Blocked ? "" : " waits for an answer";
                var text = TextUtil.Clean(d.Texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)), 100);
                var buttons = d.Buttons.Count > 0 ? " [" + string.Join("/", d.Buttons.Take(4)) + "]" : "";
                var summary = head + " \"" + TextUtil.Clean(d.Title, 60) + "\"" + wait + (text.Length > 0 ? ": " + text : "") + buttons;
                return (TextUtil.Clean(summary, StatusParser.MaxSummary), state == States.Blocked ? "Blocked" : "Dialog");
            }
            case States.WaitingInput:
            {
                var prompt = s.Prompt ?? "";
                var summary = "Waiting for input" + (cmd.Length > 0 ? " in " + cmd : "") + (prompt.Length > 0 ? ": " + prompt : "");
                return (TextUtil.Clean(summary, StatusParser.MaxSummary), TextUtil.Cap(cmd.Length > 0 ? "Input: " + cmd : "Input", CompactText.MaxChars));
            }
            case States.Running:
            {
                var names = string.IsNullOrWhiteSpace(s.CmdNames) ? "a command" : s.CmdNames.Trim();
                return (TextUtil.Clean("Running " + names, StatusParser.MaxSummary), TextUtil.Cap(cmd.Length > 0 ? "Running: " + cmd : "Running", CompactText.MaxChars));
            }
            default:
                return (TextUtil.Clean("Idle" + (string.IsNullOrEmpty(s.Doc) ? "" : ", " + s.Doc), StatusParser.MaxSummary), null);
        }
    }
}
