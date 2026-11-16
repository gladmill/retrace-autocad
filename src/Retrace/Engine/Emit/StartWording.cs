using System.Text.RegularExpressions;
using Retrace.Engine.Recordings;
using Retrace.Engine.Util;

namespace Retrace.Engine.Emit;

/// <summary>The "Start with" sentence of steps.md (and so of the Word and Excel documents): a short, plain summary of the drawing the recording began in, never a list of layers
/// or object types. Words only: the recording (`state` event, header) and the replay's start checks are not changed.</summary>
public static class StartWording
{
    static readonly Regex Unsaved = Rx.Create(@"^Drawing\d*\.dwg$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>"a new, empty drawing (template acad.dwt)", "the drawing Plan.dwg, 42 objects (mostly lines and block references). The drawing has 5 layers. Open or build it before the first step",
    /// "the empty drawing Plan.dwg. Open it before the first step"; no final period.</summary>
    public static string Describe(RecordingSession s)
    {
        var template = FileName(s.HeaderStr("template"));
        var docName = FileName(s.Events.FirstOrDefault(e => e.Type == "state" && !string.IsNullOrWhiteSpace(e.Doc))?.Doc);
        bool isNew = template.Length > 0 || docName.Length == 0 || Unsaved.IsMatch(docName);
        var st = s.InitialState;
        var tpl = template.Length > 0 ? $" (template {Md.Inline(template)})" : "";
        if (st == null || st.Count == 0)
            return isNew ? "a new, empty drawing" + tpl : $"the empty drawing {Md.Inline(docName)}. Open it before the first step";
        var subject = isNew ? "a new drawing" + tpl : $"the drawing {Md.Inline(docName)}";
        var text = $"{subject}, {st.Count} object{(st.Count == 1 ? "" : "s")}";
        var mix = Mix(st);
        if (mix.Length > 0) text += $" ({mix})";
        // only the drawing's own layer count is said (not the layers that hold objects), and not Defpoints, which AutoCAD makes by itself
        int layers = st.Layers.Count(l => !l.Equals("Defpoints", StringComparison.OrdinalIgnoreCase));
        if (layers > 0) text += $". The drawing has {layers} layer{(layers == 1 ? "" : "s")}";
        return text + ". Open or build it before the first step";
    }

    /// <summary>"mostly lines and block references", "all lines", "lines and circles"; "" when the types are not known or there is one object. At most two kinds, never a full list;
    /// types with no plain name count together as "other objects".</summary>
    public static string Mix(RecordingState st)
    {
        if (st.Count <= 1) return "";
        var kinds = st.ByType.GroupBy(t => Plural(t.Key) ?? OtherObjects).Select(g => (Name: g.Key, N: g.Sum(t => t.Value))).OrderByDescending(k => k.N).ThenBy(k => k.Name, StringComparer.Ordinal).ToList();
        if (kinds.Count == 0) return "";
        if (kinds.Count == 1) return "all " + kinds[0].Name;
        if (kinds.Count == 2) return kinds[0].Name + " and " + kinds[1].Name;
        int total = kinds.Sum(k => k.N);
        return kinds[0].N * 10 >= total * 6 ? "mostly " + kinds[0].Name : $"mostly {kinds[0].Name} and {kinds[1].Name}";
    }

    const string OtherObjects = "other objects";

    /// <summary>The plain plural of a DXF name: LINE "lines", INSERT "block references", LWPOLYLINE "polylines"; null for a name with no plain word (a class name nobody reads).</summary>
    public static string? Plural(string dxf) => dxf.ToUpperInvariant() switch
    {
        "LINE" => "lines",
        "CIRCLE" => "circles",
        "ARC" => "arcs",
        "ELLIPSE" => "ellipses",
        "SPLINE" => "splines",
        "POINT" => "points",
        "LWPOLYLINE" or "POLYLINE" or "POLYLINE2D" or "POLYLINE3D" => "polylines",
        "INSERT" => "block references",
        "TEXT" or "MTEXT" => "text",
        "HATCH" => "hatches",
        "SOLID" => "solids",
        "VIEWPORT" => "viewports",
        "LEADER" or "MULTILEADER" or "MLEADER" => "leaders",
        var d when d.EndsWith("DIMENSION", StringComparison.Ordinal) || d.StartsWith("DIM", StringComparison.Ordinal) => "dimensions",
        _ => null,
    };

    static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var n = path.Replace('\\', '/').TrimEnd('/');
        return n[(n.LastIndexOf('/') + 1)..].Trim();
    }
}
