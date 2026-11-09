namespace Retrace.Core;

// NO AutoCAD references in this file: it is linked into the engine test project (tests/Retrace.Tests) and unit-tested there.

/// <summary>Which drawing template a test's start drawing is made from (RETRACERUN, suite, Tdoc). The header's `template` is only a hint: the start drawing must match the recorded start state.</summary>
public static class TemplateChoice
{
    public const string Recorded = "the recording's template", Qnew = "the QNEW template", Default = "acad.dwt";

    /// <summary>One template that could start the test drawing, with where the path came from.</summary>
    public sealed record Candidate(string Path, string Source);

    /// <summary>The templates that exist, in priority order: <paramref name="recorded"/> (the header's `template`: a hint, the QNEW setting of the machine that recorded), <paramref name="qnew"/> (the QNEW
    /// template of this AutoCAD profile), <paramref name="fallback"/> (acad.dwt, what Retrace always used). A path that does not exist (<paramref name="exists"/>) or repeats an earlier one is left out.</summary>
    public static List<Candidate> Candidates(string? recorded, string? qnew, string? fallback, Func<string, bool> exists)
    {
        var list = new List<Candidate>();
        void Add(string? p, string source)
        {
            if (string.IsNullOrWhiteSpace(p) || list.Any(c => string.Equals(c.Path, p, StringComparison.OrdinalIgnoreCase))) return;
            if (exists(p)) list.Add(new Candidate(p, source));
        }
        Add(recorded, Recorded); Add(qnew, Qnew); Add(fallback, Default);
        return list;
    }

    /// <summary>The outcome of <see cref="Pick"/>: the template, how many layers of the recording's start state it lacks and how many it has that the start state did not (null = not compared).</summary>
    public sealed record Picked(Candidate Template, int? Missing, int? Extra)
    {
        public bool Exact => Missing == 0 && Extra == 0;
    }

    /// <summary>Layer names compared as AutoCAD does (case-insensitive), without layers of external references ("X|Y") and without "0" / "Defpoints", which every drawing has.</summary>
    public static HashSet<string> Names(IEnumerable<string> layers)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in layers)
            if (!string.IsNullOrWhiteSpace(n) && !n.Contains('|') && !n.Equals("0", StringComparison.Ordinal) && !n.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)) set.Add(n);
        return set;
    }

    /// <summary>The candidate whose layers best match the recording's start state (<paramref name="startLayers"/>, the digest's layer names): an exact match wins, else the fewest missing plus extra layers,
    /// ties to the earlier candidate. A candidate whose layers cannot be read (<paramref name="layersOf"/> returns null) only counts when none can be read. Without a start state (an older recording)
    /// nothing can be compared: the recorded template if it exists, else acad.dwt (the old behavior), never the QNEW template alone. Null when there is no candidate.</summary>
    public static Picked? Pick(IReadOnlyList<Candidate> candidates, IEnumerable<string>? startLayers, Func<string, IReadOnlyCollection<string>?> layersOf)
    {
        if (candidates.Count == 0) return null;
        if (startLayers == null)
        {
            var c = candidates.FirstOrDefault(x => x.Source == Recorded) ?? candidates.FirstOrDefault(x => x.Source == Default) ?? candidates[0];
            return new Picked(c, null, null);
        }
        var start = Names(startLayers);
        Picked? best = null; var bestScore = int.MaxValue;
        foreach (var c in candidates)
        {
            var layers = layersOf(c.Path);
            if (layers == null) continue;
            var have = Names(layers);
            var missing = start.Count(n => !have.Contains(n)); var extra = have.Count(n => !start.Contains(n));
            if (missing + extra < bestScore) { bestScore = missing + extra; best = new Picked(c, missing, extra); }
        }
        return best ?? new Picked(candidates[0], null, null);
    }

    /// <summary>A UNC path (\\server\share\...): a missing server can make a file check block for a long time.</summary>
    public static bool IsUnc(string? path) => path != null && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal));

    /// <summary>The `template` of a recording's header event, or null (older recordings, and recordings started in a drawing that was already a file).</summary>
    public static string? FromHeader(IEnumerable<System.Text.Json.JsonElement> events)
    {
        foreach (var e in events)
        {
            if (Plan.Str(e, "type") != "header") continue;
            return Plan.Str(e, "template") is { Length: > 0 } t ? t : null;
        }
        return null;
    }

    /// <summary>One line for the command line: "the QNEW template (C:\t\Company.dwt), its layers match the recording's start state" and, when the recording's own template was not the one used, that it was not.</summary>
    public static string Describe(Picked used, string? recorded)
    {
        var how = $"{used.Template.Source} ({used.Template.Path})";
        if (used.Missing is { } m && used.Extra is { } x)
            how += m == 0 && x == 0 ? ", its layers match the recording's start state" : $", the closest to the recording's start state ({m} layer{(m == 1 ? "" : "s")} missing, {x} extra)";
        if (!string.IsNullOrWhiteSpace(recorded) && used.Template.Source != Recorded && !string.Equals(recorded, used.Template.Path, StringComparison.OrdinalIgnoreCase))
            how += $"; the recording's template {recorded} was not used (missing here or a worse match)";
        return how;
    }
}
