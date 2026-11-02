namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// The system variables the `sysvars` behavior reads by default. Only variables saved in the drawing that a test can change on purpose:
/// units, current object settings, dimension / text / table / leader styles, scales. Not the variables a person toggles without a command (F-keys, status bar, layout tabs).
/// Left out on purpose: anything volatile or not about the drawing (DATE, CDATE, TDUSRTIMER, TDINDWG, TDUPDATE, DBMOD, VIEWCTR, VIEWSIZE,
/// VIEWDIR, LASTPOINT, CMDACTIVE, cursor, selection and other temporary variables, file paths and names). The capture code skips a name
/// that AutoCAD does not know (a variable of another release or product) instead of failing.
/// The effective list is default + settings `sysvarsAdd` - settings `sysvarsRemove` (names compared without case): see <see cref="Effective"/>.
/// </summary>
public static class SysVarDefaults
{
    public static readonly string[] Default =
    {
        // units
        "INSUNITS", "LUNITS", "LUPREC", "AUNITS", "AUPREC", "ANGBASE", "ANGDIR", "MEASUREMENT", "UNITMODE",
        // current object settings
        "CLAYER", "CELTYPE", "CECOLOR", "CELWEIGHT", "CELTSCALE", "ELEVATION", "THICKNESS",
        // linetype scale and display
        "LTSCALE", "PSLTSCALE", "MSLTSCALE", "LWUNITS", "LWDEFAULT",
        // styles
        "TEXTSTYLE", "TEXTSIZE", "DIMSTYLE", "CMLSTYLE", "CMLSCALE", "CMLJUST", "CMLEADERSTYLE", "CTABLESTYLE",
        // annotation scale ("ANNOSCALE" is the older name some products use)
        "CANNOSCALE", "ANNOSCALE",
        // the dimension variables that carry the current dimension style's overrides
        "DIMSCALE", "DIMTXT", "DIMASZ", "DIMEXE", "DIMEXO", "DIMGAP", "DIMDEC", "DIMLUNIT", "DIMAUNIT", "DIMADEC", "DIMTXSTY", "DIMASSOC",
        // points, polylines, splines, hatch, fills
        "PDMODE", "PDSIZE", "PLINEWID", "PLINEGEN", "SPLFRAME", "SPLINETYPE", "SPLINESEGS", "FILLMODE", "MIRRTEXT",
        // snap and grid spacing (the on/off modes, ORTHOMODE, POLARMODE, OSMODE, DYNMODE, CTAB, TILEMODE and UCSNAME are left out: people toggle
        // them with F-keys or tab clicks; add them with sysvarsAdd to check them)
        "SNAPUNIT", "GRIDUNIT", "PSTYLEMODE",
    };

    /// <summary>Default + <paramref name="add"/> - <paramref name="remove"/>, no blanks, no duplicates (first spelling wins, compared without case),
    /// in order: the default list first, then the added names.</summary>
    public static List<string> Effective(IEnumerable<string>? add, IEnumerable<string>? remove)
    {
        var removed = new HashSet<string>((remove ?? Enumerable.Empty<string>()).Select(s => (s ?? "").Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var n in Default.Concat(add ?? Enumerable.Empty<string>()))
        {
            var name = (n ?? "").Trim();
            if (name.Length == 0 || removed.Contains(name) || !seen.Add(name)) continue;
            result.Add(name);
        }
        return result;
    }
}
