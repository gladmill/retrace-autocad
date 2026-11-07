using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The static texts of a window ("3 components found"): what the recorder logs next to a click or a close, and what the replay result
// holds for a dialog step. Machine-readable only: nothing here reaches steps.md, the docs, Word / Excel or Tdoc.

/// <summary>A static text (or read-only edit text) the window shows: the id of its control and its text.</summary>
public sealed record DialogStatic([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("text")] string Text);

public static class DialogStatics
{
    /// <summary>Each text is cut at this many characters.</summary>
    public const int MaxTextLength = 200;
    /// <summary>At most this many statics are kept (the first ones, in window order).</summary>
    public const int MaxCount = 100;

    /// <summary>The statics from the texts of the window's static and read-only edit controls, in window order: mnemonic ampersands taken out
    /// (<c>&amp;&amp;</c> stays one <c>&amp;</c>), empty texts left out, each text cut at <see cref="MaxTextLength"/>, at most <see cref="MaxCount"/>.</summary>
    public static List<DialogStatic> Build(IEnumerable<(int Id, string? Text)> texts)
    {
        var list = new List<DialogStatic>();
        foreach (var (id, raw) in texts)
        {
            if (list.Count >= MaxCount) break;
            var t = StripMnemonics(raw).Trim();
            if (t.Length == 0) continue;
            list.Add(new DialogStatic(id, t.Length <= MaxTextLength ? t : t[..MaxTextLength]));
        }
        return list;
    }

    /// <summary>"&amp;Name" gives "Name", "A &amp;&amp; B" gives "A &amp; B".</summary>
    public static string StripMnemonics(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('&') < 0) return s ?? "";
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '&') { sb.Append(s[i]); continue; }
            if (i + 1 < s.Length && s[i + 1] == '&') { sb.Append('&'); i++; }
        }
        return sb.ToString();
    }

    /// <summary>The recorder's JSON for a list: <c>[{"id":1003,"text":"3 components found"}]</c>; null for an empty list (the field is left out).</summary>
    public static List<Dictionary<string, object?>>? ToJson(IReadOnlyList<DialogStatic>? statics) =>
        statics == null || statics.Count == 0 ? null : statics.Select(s => new Dictionary<string, object?> { ["id"] = s.Id, ["text"] = s.Text }).ToList();

    /// <summary>Reads a `statics` array; anything that is not an array of objects with a text is skipped. Never throws.</summary>
    public static List<DialogStatic> FromJson(JsonElement e)
    {
        var list = new List<DialogStatic>();
        if (e.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in e.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) continue;
            var id = s.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) ? n : 0;
            list.Add(new DialogStatic(id, t.GetString() ?? ""));
        }
        return list;
    }
}
