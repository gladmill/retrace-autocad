using System;
using System.Collections.Generic;
using System.IO;
using AcadStatus.Core;

namespace AcadStatus.Tests;

/// <summary>Builders shared by the tests. Synthetic data only.</summary>
static class TestData
{
    public static readonly DateTime Now = new(2026, 11, 5, 10, 14, 3, DateTimeKind.Utc);

    public static string Iso(DateTime t) => AcadStatusBuilder.Iso(t);

    /// <summary>A valid status file as JSON text; <paramref name="extra"/> is inserted before the closing brace (start it with a comma).</summary>
    public static string Json(string source = "retrace", int pid = 4812, DateTime? updated = null, int ttl = 15, string state = "running",
        string summary = "Suite 3/7", string? @short = null, string? since = null, string? owner = null, string? extra = null, int v = 1,
        string writer = "Retrace 0.6.8", DateTime? pidStart = null)
    {
        var parts = new List<string>
        {
            $"\"v\": {v}", $"\"source\": \"{source}\"", $"\"writer\": \"{writer}\"", $"\"pid\": {pid}",
            $"\"updated\": \"{Iso(updated ?? Now)}\"", $"\"ttlSec\": {ttl}", $"\"state\": \"{state}\"", $"\"summary\": \"{summary}\"",
        };
        if (pidStart != null) parts.Add($"\"pidStart\": \"{Iso(pidStart.Value)}\"");
        if (@short != null) parts.Add($"\"short\": \"{@short}\"");
        if (since != null) parts.Add($"\"since\": \"{since}\"");
        if (owner != null) parts.Add($"\"owner\": \"{owner}\"");
        return "{ " + string.Join(", ", parts) + (extra ?? "") + " }";
    }

    public static StatusEntry Entry(string source = "retrace", int pid = 4812, DateTime? updated = null, int ttl = 15, string state = "running",
        string summary = "Suite 3/7", string? @short = null, DateTime? since = null, string? owner = null, string? file = null, DateTime? pidStart = null)
    {
        var json = Json(source, pid, updated, ttl, state, summary, @short, since == null ? null : Iso(since.Value), owner, pidStart: pidStart);
        var r = StatusParser.Parse(file ?? source + "-" + pid + ".json", json);
        Assert.True(r.Ok, r.Error);
        return r.Entry!;
    }

    public static MergedEntry Merged(string state, string? @short = null, DateTime? since = null, string source = "retrace", int pid = 4812)
    {
        var e = Entry(source, pid, Now, 15, state, "summary", @short, since);
        var m = TestMerge.Merge(new[] { e }, Now, _ => true);
        return Assert.Single(m.Shown);
    }

    public static DialogSnap Dialog(long handle = 100, string title = "AutoCAD Message", string[]? texts = null, string[]? buttons = null, bool modal = true) =>
        new(handle, title, "#32770", modal, texts ?? new[] { "; error: bad argument type: numberp: nil" }, buttons ?? new[] { "OK" });

    public static Snapshot Snap(string? doc = "fx-all.dwg", int docs = 1, int cmdActive = 0, string cmdNames = "", string? prompt = null,
        DialogSnap? dialog = null, DateTime? dialogSince = null) => new(doc, docs, cmdActive, cmdNames, prompt, dialog, dialogSince);

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "acadstatus-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>A temp folder that is deleted at the end of the test.</summary>
sealed class TempFolder : IDisposable
{
    public string Path { get; } = TestData.TempDir();

    public string Write(string name, string text)
    {
        var p = System.IO.Path.Combine(Path, name);
        File.WriteAllText(p, text);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* the OS cleans temp folders */ }
    }
}
