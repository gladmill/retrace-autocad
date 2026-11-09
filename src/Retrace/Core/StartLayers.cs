using System.Globalization;

namespace Retrace.Core;

// NO AutoCAD references in this file: it is linked into the engine test project (tests/Retrace.Tests) and unit-tested there.

/// <summary>One layer a test's start drawing lacks and the recording's start state had.</summary>
public sealed class LayerToCreate
{
    public string Name { get; init; } = "";
    /// <summary>True when the recording's start snapshot (snapshots/start.json) described the layer; false = name only (an older recording): AutoCAD's defaults.</summary>
    public bool Described { get; init; }
    public bool? On { get; init; }
    public bool? Frozen { get; init; }
    public bool? Locked { get; init; }
    public bool? Plot { get; init; }
    /// <summary>ACI number, "rgb:r,g,b" or "Book$Name" (see <see cref="ParseColor"/>); null = default.</summary>
    public string? Color { get; init; }
    /// <summary>Linetype name that exists in the drawing; null = Continuous.</summary>
    public string? Linetype { get; init; }
    /// <summary>Lineweight in hundredths of a millimetre (-3 = Default), null = leave the default.</summary>
    public int? Lineweight { get; init; }
    /// <summary>Alpha 0-255 of a layer transparency; null = none.</summary>
    public int? Transparency { get; init; }
    public string? Description { get; init; }
    /// <summary>Entities model space held on the layer at the start. They are not recreated, so a PURGE in the test can remove the (empty) layer that the recording kept.</summary>
    public int StartEntities { get; init; }
}

/// <summary>What <see cref="StartLayers.Plan"/> decided: layers to create and the warnings to show.</summary>
public sealed class StartLayerPlan
{
    public List<LayerToCreate> Create { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>The layer that was current (CLAYER) at the start (null when unknown, or when it is created frozen and cannot be current): made current whenever the drawing's current layer differs,
    /// created or pre-existing, so a PURGE in the test keeps it as it did in the recording (the current layer cannot be purged).</summary>
    public string? CurrentLayer { get; set; }
    public bool IsEmpty => Create.Count == 0;
}

/// <summary>
/// Makes a test's own start drawing hold the layers the recording's start drawing held (RETRACERUN, a suite or Tdoc run: the copy of the template).
/// The recording's start state lists every layer name (the digest) and, when its behavior snapshot was captured, each layer's flags, color, linetype,
/// lineweight, transparency and description (`snapshots/start.json`, behavior `layers`). Without this a test recorded in a drawing that already held layers
/// (created by an earlier run of the thing under test) starts in a template that lacks them, and a PURGE in the middle, or a layer check at the end,
/// behaves differently from the recording. Never used for RETRACEREPLAY: that drawing is a person's.
/// </summary>
public static class StartLayers
{
    /// <summary>Layers of <paramref name="initial"/> that <paramref name="presentLayers"/> (names, case-insensitive: AutoCAD) lacks, with the snapshot's properties when it has them.
    /// Layers from an external reference ("X|Y") and names AutoCAD rejects (<c>&lt; &gt; / \ " : ; ? * | , = `</c>) are left out. A linetype the drawing lacks (<paramref name="linetypeExists"/>)
    /// becomes Continuous, with one warning per linetype.</summary>
    public static StartLayerPlan Plan(DrawingDigest initial, BehaviorSnapshot? startSnapshot, IEnumerable<string> presentLayers, Func<string, bool> linetypeExists)
    {
        var plan = new StartLayerPlan();
        var have = new HashSet<string>(presentLayers, StringComparer.OrdinalIgnoreCase);
        var items = startSnapshot?.Items(BehaviorCatalog.Layers);
        var lostLinetypes = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        var xrefLayers = new List<string>();
        foreach (var name in initial.Layers.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (have.Contains(name)) continue;
            if (name.Contains('|')) { xrefLayers.Add(name); continue; }
            if (!IsUsableName(name)) { skipped.Add(name); continue; }
            have.Add(name);
            string? value = null;
            var described = items != null && items.TryGetValue(name, out value);
            var f = described && CanonicalValue.TryParse(value, out var fields) && fields != null
                ? fields.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal)
                : null;
            // not in start.json (older recording, snapshot not captured): the digest's layer state (schema 2) before the name only
            if (f == null && initial.LayerState != null && initial.LayerState.TryGetValue(name, out var props)) f = FromProps(props);
            described = f != null;
            string? linetype = null;
            if (f != null && f.TryGetValue("linetype", out var lt) && lt.Length > 0)
            {
                if (lt.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) { }
                else if (linetypeExists(lt)) linetype = lt;
                else { if (!lostLinetypes.TryGetValue(lt, out var l)) lostLinetypes[lt] = l = new(); l.Add(name); }
            }
            plan.Create.Add(new LayerToCreate
            {
                Name = name,
                Described = described,
                On = Bool(f, "on"), Frozen = Bool(f, "frozen"), Locked = Bool(f, "locked"), Plot = Bool(f, "plot"),
                Color = f != null && f.TryGetValue("color", out var c) && c.Length > 0 ? c : null,
                Linetype = linetype,
                Lineweight = f != null && f.TryGetValue("lineweight", out var w) ? LineweightHundredths(w) : null,
                Transparency = f != null && f.TryGetValue("transparency", out var t) && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var a) && a is >= 0 and <= 255 ? a : null,
                Description = f != null && f.TryGetValue("description", out var d) && d.Length > 0 ? d : null,
                StartEntities = initial.ByLayer.GetValueOrDefault(name),
            });
        }
        var clayer = StartClayer(startSnapshot, initial);
        // a layer created frozen cannot be current
        if (clayer != null && plan.Create.FirstOrDefault(l => string.Equals(l.Name, clayer, StringComparison.OrdinalIgnoreCase)) is not { Frozen: true }) plan.CurrentLayer = clayer;
        foreach (var (lt, layers) in lostLinetypes)
            plan.Warnings.Add($"linetype {lt} is not in the drawing: Continuous used for layer{(layers.Count == 1 ? "" : "s")} {Join(layers)}");
        if (xrefLayers.Count > 0) plan.Warnings.Add($"layer{(xrefLayers.Count == 1 ? "" : "s")} {Join(xrefLayers)} belong{(xrefLayers.Count == 1 ? "s" : "")} to an external reference and cannot be created");
        if (skipped.Count > 0) plan.Warnings.Add($"layer{(skipped.Count == 1 ? "" : "s")} {Join(skipped)} could not be created (not a usable layer name)");
        var withEntities = plan.Create.Where(l => l.StartEntities > 0).Select(l => l.Name).ToList();
        if (withEntities.Count > 0)
            plan.Warnings.Add($"layer{(withEntities.Count == 1 ? "" : "s")} {Join(withEntities)} held entities in the recorded start drawing; created empty, so a PURGE in the test may remove {(withEntities.Count == 1 ? "it" : "them")}");
        return plan;
    }

    /// <summary>The layer that was current at the recording's start: CLAYER of the start snapshot, else the digest's current layer (schema 2); null when neither is known.</summary>
    public static string? StartClayer(BehaviorSnapshot? startSnapshot, DrawingDigest initial)
    {
        if (startSnapshot?.Items(BehaviorCatalog.SysVars) is { } sv && sv.TryGetValue("CLAYER", out var cl) && !string.IsNullOrWhiteSpace(cl)) return cl;
        return string.IsNullOrWhiteSpace(initial.CurrentLayer) ? null : initial.CurrentLayer;
    }

    /// <summary>The layers of the drawing the start state does not have and that may be removed (a purge decides): not "0", "Defpoints", not an external reference's layer. Order as given.</summary>
    public static List<string> ExtraLayers(DrawingDigest initial, IEnumerable<string> presentLayers)
    {
        var want = new HashSet<string>(initial.Layers, StringComparer.OrdinalIgnoreCase);
        return presentLayers.Where(n => !want.Contains(n) && !n.Contains('|') && n != "0" && !n.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>True when some start layer is missing from <paramref name="presentLayers"/> (any name, usable or not): only then does start.json have to be read.</summary>
    public static bool AnyMissing(DrawingDigest initial, IEnumerable<string> presentLayers)
    {
        var have = new HashSet<string>(presentLayers, StringComparer.OrdinalIgnoreCase);
        return initial.Layers.Any(n => !have.Contains(n));
    }

    static Dictionary<string, string> FromProps(LayerProps p)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        void B(string k, bool? v) { if (v is { } b) d[k] = b ? "1" : "0"; }
        B("on", p.On); B("frozen", p.Frozen); B("locked", p.Locked); B("plot", p.Plot);
        if (!string.IsNullOrEmpty(p.Color)) d["color"] = p.Color;
        if (!string.IsNullOrEmpty(p.Linetype)) d["linetype"] = p.Linetype;
        if (!string.IsNullOrEmpty(p.Lineweight)) d["lineweight"] = p.Lineweight;
        return d;
    }

    /// <summary>One line for the command line and the result: "created 3 layers (A, B, C)", the names shortened after 8.</summary>
    public static string Describe(IReadOnlyList<string> created)
    {
        if (created.Count == 0) return "";
        var shown = created.Take(8).ToList();
        return $"created {created.Count} layer{(created.Count == 1 ? "" : "s")} the recording's start drawing had ({string.Join(", ", shown)}{(created.Count > shown.Count ? ", ..." : "")})";
    }

    static string Join(List<string> names) => names.Count <= 8 ? string.Join(", ", names) : string.Join(", ", names.Take(8)) + $", ... ({names.Count} in all)";

    static readonly char[] BadNameChars = { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' };

    /// <summary>A name AutoCAD accepts for a layer (not empty, none of the characters its symbol names refuse; "X|Y" is a layer of an external reference).</summary>
    public static bool IsUsableName(string name) => !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(BadNameChars) < 0 && name.IndexOfAny(new[] { '\r', '\n' }) < 0;

    static bool? Bool(Dictionary<string, string>? f, string key) =>
        f != null && f.TryGetValue(key, out var v) ? v == "1" ? true : v == "0" ? false : null : null;

    /// <summary>Millimetres text of <see cref="DrawingDigest.LineweightText"/> back to hundredths of a millimetre: "0.25" -> 25, "Default" -> -3. "ByLayer" / "ByBlock" (not
    /// valid for a layer) and anything unreadable -> null.</summary>
    public static int? LineweightHundredths(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Equals("Default", StringComparison.OrdinalIgnoreCase)) return -3;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) || mm < 0 || mm > 3) return null;
        return (int)Math.Round(mm * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>A layer color text (<c>DrawingState.ColorText</c>): "7" (ACI), "rgb:255,0,0" or "Book$Name". Kind "aci" (Index 1-255; 0 and 256 are not layer colors), "rgb" (R, G, B) or "book" (Book, Name);
    /// null when it is none of them.</summary>
    public static ColorSpec? ParseColor(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith("rgb:", StringComparison.Ordinal))
        {
            var p = text[4..].Split(',');
            if (p.Length == 3 && byte.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r) && byte.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var g)
                && byte.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var b)) return new ColorSpec("rgb", 0, r, g, b, "", "");
            return null;
        }
        var i = text.IndexOf('$');
        if (i > 0 && i < text.Length - 1) return new ColorSpec("book", 0, 0, 0, 0, text[..i], text[(i + 1)..]);
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var aci) && aci is >= 1 and <= 255) return new ColorSpec("aci", aci, 0, 0, 0, "", "");
        return null;
    }
}

/// <summary>A parsed layer color (see <see cref="StartLayers.ParseColor"/>).</summary>
public sealed record ColorSpec(string Kind, int Index, byte R, byte G, byte B, string Book, string Name);
