using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace AcadStatus.Core;

public sealed record ParseResult(StatusEntry? Entry, string? Error)
{
    public bool Ok => Entry != null;
}

/// <summary>Reads one status file the way CONTRACT.md asks of readers: unknown fields and unknown states are fine; a file that is not
/// JSON, not an object, lacks a required field or has a <c>v</c> that is not an integer >= 1 is skipped (an <see cref="ParseResult.Error"/>
/// says why). Never throws.</summary>
public static class StatusParser
{
    public const int MaxSummary = 200;

    static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 };

    /// <summary>The earliest <c>updated</c> a file may state; before it is a corrupt or hostile value.</summary>
    public static readonly DateTime MinUpdated = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>How far past now <c>updated</c> may be (clocks differ a little between machines and users, not by days).</summary>
    public static readonly TimeSpan MaxFuture = TimeSpan.FromDays(1);

    /// <summary>The longest <c>ttlSec</c> a file may state: a day. An entry whose <c>updated</c> plus ttl could not be added would abort the merge.</summary>
    public const int MaxTtlSec = 86400;

    /// <param name="nowUtc">The time <c>updated</c> is judged against (default: now). Tests pass their own.</param>
    public static ParseResult Parse(string fileName, string text, DateTime? nowUtc = null)
    {
        try
        {
            return ParseCore(fileName, text, nowUtc ?? DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            return new ParseResult(null, "unreadable: " + ex.Message);
        }
    }

    static ParseResult ParseCore(string fileName, string text, DateTime nowUtc)
    {
        var raw = text.TrimStart('﻿');
        if (raw.Trim().Length == 0) return Fail("empty file");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw, Options); }
        catch (JsonException ex) { return Fail("not valid JSON (" + FirstLine(ex.Message) + ")"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("not a JSON object");

            if (!Int(root, "v", out var v)) return Fail("missing required field v");
            if (v < 1) return Fail("v is not an integer >= 1");
            if (!Str(root, "source", out var source)) return Fail("missing required field source");
            if (!Str(root, "writer", out var writer)) return Fail("missing required field writer");
            if (!Int(root, "pid", out var pid)) return Fail("missing required field pid");
            if (!Str(root, "updated", out var updatedText)) return Fail("missing required field updated");
            if (!TryUtc(updatedText, out var updated)) return Fail("updated is not a date and time");
            if (updated < MinUpdated) return Fail("updated is before the year 2000");
            if (updated > nowUtc + MaxFuture) return Fail("updated is more than a day in the future");
            if (!Int(root, "ttlSec", out var ttl)) return Fail("missing required field ttlSec");
            if (ttl < 1 || ttl > MaxTtlSec) return Fail("ttlSec is outside 1 to 86400");
            if (!Str(root, "state", out var state) || state.Trim().Length == 0) return Fail("missing required field state");
            if (!Str(root, "summary", out var summary)) return Fail("missing required field summary");

            Str(root, "short", out var shortText);
            DateTime? since = Str(root, "since", out var sinceText) && TryUtc(sinceText, out var s) ? s : null;
            Str(root, "owner", out var owner);
            DateTime? pidStart = Str(root, "pidStart", out var pidStartText) && TryUtc(pidStartText, out var ps) ? ps : null;

            string? detailJson = null;
            AcadDetail? acad = null;
            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
            {
                detailJson = JsonSerializer.Serialize(detail);   // compact, one line
                if (string.Equals(source, "acad", StringComparison.Ordinal)) acad = ParseAcad(detail);
            }

            return new ParseResult(new StatusEntry(
                fileName, source, writer, pid, updated, ttl, state.Trim(), TextUtil.Clean(summary, MaxSummary),
                string.IsNullOrWhiteSpace(shortText) ? null : shortText!.Trim(), since,
                string.IsNullOrWhiteSpace(owner) ? null : owner!.Trim(), detailJson, acad, raw, pidStart), null);
        }
    }

    static ParseResult Fail(string why) => new(null, why);

    static string FirstLine(string s)
    {
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? s : s[..i];
    }

    static bool Str(JsonElement o, string name, out string value)
    {
        value = "";
        if (!o.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString() ?? "";
        return true;
    }

    /// <summary>An integer property: a JSON number without a fraction (1 and 1.0 both count). A string "1" does not.</summary>
    static bool Int(JsonElement o, string name, out int value)
    {
        value = 0;
        if (!o.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return false;
        if (p.TryGetInt32(out value)) return true;
        if (p.TryGetDouble(out var d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) { value = (int)d; return true; }
        return false;
    }

    static bool TryUtc(string text, out DateTime utc) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);

    static AcadDetail ParseAcad(JsonElement d)
    {
        int? docs = Int(d, "docs", out var n) ? n : null;
        int? cmdActive = Int(d, "cmdActive", out var c) ? c : null;
        DialogDetail? dialog = null;
        if (d.TryGetProperty("dialog", out var dlg) && dlg.ValueKind == JsonValueKind.Object)
        {
            Str(dlg, "title", out var title);
            Str(dlg, "class", out var cls);
            bool modal = dlg.TryGetProperty("modal", out var m) && m.ValueKind == JsonValueKind.True;
            DateTime? since = Str(dlg, "since", out var st) && TryUtc(st, out var s) ? s : null;
            dialog = new DialogDetail(title, cls, modal, Strings(dlg, "texts"), Strings(dlg, "buttons"), since);
        }
        Str(d, "doc", out var doc);
        Str(d, "cmdNames", out var cmdNames);
        // absent only when no prompt is pending (CONTRACT.md): "" is a pending prompt whose text is unknown
        string? prompt = Str(d, "prompt", out var promptText) ? promptText : null;
        return new AcadDetail(
            string.IsNullOrEmpty(doc) ? null : doc, docs, cmdActive, string.IsNullOrEmpty(cmdNames) ? null : cmdNames,
            prompt, dialog);
    }

    static IReadOnlyList<string> Strings(JsonElement o, string name)
    {
        var list = new List<string>();
        if (o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var e in a.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s) list.Add(s);
        return list;
    }
}
