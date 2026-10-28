namespace Retrace.Engine.Util;

/// <summary>Ids as the live ribbon reports them against ids as the source has them: AutoCAD puts the customization group in front of a CUIX
/// id ("ACAD.ID_TabHome" for "ID_TabHome"). One rule for joining recordings and ribbon captures to the scanned tabs.</summary>
public static class LiveIds
{
    /// <summary>The live id equals <paramref name="id"/>, with or without a group in front (case-insensitive, trimmed).</summary>
    public static bool Matches(string? live, string? id)
    {
        if (string.IsNullOrWhiteSpace(live) || string.IsNullOrWhiteSpace(id)) return false;
        var r = live.Trim(); var want = id.Trim();
        if (string.Equals(r, want, StringComparison.OrdinalIgnoreCase)) return true;
        var dot = r.LastIndexOf('.');
        return dot > 0 && string.Equals(r[(dot + 1)..], want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two texts are the same, trimmed and case-insensitive; null and blank match nothing.</summary>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
