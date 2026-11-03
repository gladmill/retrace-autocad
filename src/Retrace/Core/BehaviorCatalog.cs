namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>One behavior Retrace can verify at a checkpoint (see docs/recording-format.md, "Behavior checks").</summary>
/// <param name="Id">stable id used in the recording, the settings and the snapshot files; never renamed</param>
/// <param name="Label">what the UI shows</param>
/// <param name="Description">one line for the UI</param>
/// <param name="Noun">word that starts a compare line ("layer DIM frozen: ...")</param>
/// <param name="Canonical">true = item values are canonical field lists (<see cref="CanonicalValue"/>), so a compare names the differing field;
/// false = a value is plain text (a system variable, an attribute text) and is shown whole</param>
public sealed record BehaviorInfo(string Id, string Label, string Description, string Noun, bool Canonical);

/// <summary>The behaviors, in the order the UI lists them.</summary>
public static class BehaviorCatalog
{
    public const string Layers = "layers", Layouts = "layouts", Viewports = "viewports", XData = "xdata", XDicts = "xdicts",
        Symbols = "symbols", SysVars = "sysvars", Views = "views", PsAttributes = "psattributes", Appearance = "appearance", PaperSpace = "paperspace";

    public static readonly IReadOnlyList<BehaviorInfo> All = new BehaviorInfo[]
    {
        new(Layers, "Layer state", "On, frozen, locked, plot, color, linetype and lineweight of each layer.", "layer", true),
        new(Appearance, "Entity appearance", "Layer, color, linetype, linetype scale, lineweight, transparency and plot style of each entity in model space and on the layouts. Tick Record in Settings before recording to be able to check it.", "appearance", true),
        new(Layouts, "Layouts", "Each layout's tab order, page setup and a fingerprint of what is drawn on it.", "layout", true),
        new(PaperSpace, "Paper-space entities", "Each entity drawn on a layout (not the viewports), one by one: type, layer and geometry. Tick Record in Settings before recording to be able to check it.", "paperspace", true),
        new(Viewports, "Viewports", "Position, size, view and scale of the paper-space viewports.", "viewport", true),
        new(XData, "XData", "Extended data attached to entities, per application.", "xdata", true),
        new(XDicts, "Extension dictionaries", "Dictionaries attached to entities and the named object dictionary (Xrecords).", "xdict", true),
        new(Symbols, "Symbol tables", "Linetypes, text styles, dimension styles, block definitions, UCS and registered applications.", "symbols", true),
        new(SysVars, "System variables", "Drawing settings such as units, current layer and linetype scale.", "sysvar", false),
        new(Views, "Named views", "Saved views: center, size and direction.", "view", true),
        new(PsAttributes, "Attributes on paper-space inserts", "Attribute text of blocks inserted on layouts.", "attribute", false),
    };

    public static BehaviorInfo? Find(string? id, bool ignoreCase = false) =>
        All.FirstOrDefault(b => string.Equals(b.Id, id, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    public static bool IsKnown(string? id) => Find(id) != null;

    /// <summary>Behaviors that cost one item per entity (about 250 bytes each in every snapshot): while recording they are captured only when
    /// they are in the setting `recordBehaviors` (either mode) or, in manual mode, in `checkBehaviors`. Old recordings and the other behaviors are unaffected.</summary>
    public static readonly IReadOnlyList<string> OptIn = new[] { Appearance, PaperSpace };
    public static bool IsOptIn(string id) => OptIn.Contains(id);

    /// <summary>The behaviors to capture at a checkpoint while recording: every behavior, minus the opt-in ones that are in neither
    /// <paramref name="record"/> (the effective `recordBehaviors`) nor <paramref name="manualCheck"/> (the effective `checkBehaviors` in manual mode,
    /// empty in auto mode).</summary>
    public static List<string> ForRecording(IEnumerable<string> record, IEnumerable<string> manualCheck)
    {
        var on = new HashSet<string>(record.Concat(manualCheck), StringComparer.Ordinal);
        return All.Select(b => b.Id).Where(id => !IsOptIn(id) || on.Contains(id)).ToList();
    }

    /// <summary>The label of a behavior; an unknown id (from a newer recording) is its own label.</summary>
    public static string LabelOf(string id) => Find(id)?.Label ?? id;

    /// <summary>The default behaviors for manual mode (settings `checkBehaviors`).</summary>
    public static readonly IReadOnlyList<string> DefaultManual = new[] { Layers, Layouts, Symbols };
}

/// <summary>
/// Canonical item values: ordered `name=value` fields joined by `;`, e.g. `on=1;frozen=0;color=7`. In names and values `\` becomes `\\`,
/// `;` becomes `\;` and `=` becomes `\=`, so any text can be a name or a value. Field order is the order given (the capture code keeps it
/// fixed per behavior); a compare names the fields that differ.
/// </summary>
public static class CanonicalValue
{
    static readonly char[] Special = { '\\', ';', '=' };

    public static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(Special) < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            if (c is '\\' or ';' or '=') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>`name=value;name=value` from fields in order. A null value is written as an empty value.</summary>
    public static string Join(IEnumerable<(string Name, string? Value)> fields) =>
        string.Join(";", fields.Select(f => Escape(f.Name) + "=" + Escape(f.Value)));

    public static string Join(params (string Name, string? Value)[] fields) => Join((IEnumerable<(string, string?)>)fields);

    /// <summary>The fields of a canonical value, in order, unescaped. False (and null) when the text is not a canonical value: a field without
    /// `=`, an unescaped second `=`, or a trailing lone `\`. The empty text is a canonical value with no fields.</summary>
    public static bool TryParse(string? text, out List<(string Name, string Value)>? fields)
    {
        fields = null;
        if (text == null) return false;
        var list = new List<(string, string)>();
        var name = new System.Text.StringBuilder();
        var value = new System.Text.StringBuilder();
        var inValue = false;
        var any = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            any = true;
            if (c == '\\')
            {
                if (i + 1 >= text.Length) return false;
                (inValue ? value : name).Append(text[++i]);
            }
            else if (c == '=' && !inValue) inValue = true;
            else if (c == '=') return false;
            else if (c == ';')
            {
                if (!inValue) return false;
                list.Add((name.ToString(), value.ToString()));
                name.Clear(); value.Clear(); inValue = false; any = false;
            }
            else (inValue ? value : name).Append(c);
        }
        if (any)
        {
            if (!inValue) return false;
            list.Add((name.ToString(), value.ToString()));
        }
        fields = list;
        return true;
    }
}
