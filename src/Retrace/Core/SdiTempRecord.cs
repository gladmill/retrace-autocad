namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>What Retrace last saw of a temp test drawing: its write time and size, kept in a small sidecar file (<c>Test-xxxxxx.dwg.retrace</c>) written when Retrace creates
/// or saves the drawing. A drawing is deleted when old only while it still looks like that: a file somebody else saved into (its write time or size differs), or one with no
/// record, is never deleted.</summary>
public readonly record struct SdiTempRecord(long WriteTicksUtc, long Size)
{
    public const string SidecarExtension = ".retrace";

    public static string SidecarPath(string file) => file + SidecarExtension;

    public static SdiTempRecord Of(DateTime writeUtc, long size) => new(writeUtc.ToUniversalTime().Ticks, size);

    public string Format() => $"{WriteTicksUtc} {Size}";

    public static SdiTempRecord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var p = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return p.Length == 2 && long.TryParse(p[0], out var t) && long.TryParse(p[1], out var s) ? new SdiTempRecord(t, s) : null;
    }

    public bool Matches(DateTime writeUtc, long size) => writeUtc.ToUniversalTime().Ticks == WriteTicksUtc && size == Size;

    /// <summary>May the drawing be deleted: it has a record, it is unchanged since (write time and size), and it is a day old (<see cref="SdiGuard.IsStaleTemp"/>).</summary>
    public static bool MayDelete(SdiTempRecord? record, DateTime createdUtc, DateTime lastWriteUtc, long size, DateTime nowUtc) =>
        record is { } r && r.Matches(lastWriteUtc, size) && SdiGuard.IsStaleTemp(createdUtc, lastWriteUtc, nowUtc);

    /// <summary>Whether <paramref name="name"/> (a file name) is a file AutoCAD keeps beside the drawing with file name <paramref name="drawingName"/>: .bak, .dwl, .dwl2, .sv$ or
    /// the sidecar. Deleted together with the drawing.</summary>
    public static bool IsCompanion(string name, string drawingName)
    {
        var stem = Path.GetFileNameWithoutExtension(drawingName);
        if (stem.Length == 0) return false;
        if (name.Equals(drawingName + SidecarExtension, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var ext in new[] { ".bak", ".dwl", ".dwl2", ".sv$" })
            if (name.Equals(stem + ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
