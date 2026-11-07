namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// How two spellings of a command name are compared: <c>_UNITS</c>, <c>.units</c>, <c>-UNITS</c>, <c>'UNITS</c> and <c>*UNITS</c> are one command.
/// The prefixes are the language-independent <c>_</c>, the built-in <c>.</c>, the command-line form <c>-</c>, the transparent <c>'</c> and the repeat <c>*</c>.
/// One place, so the readers (<c>RecordingSession</c>: a <c>cmd_unknown</c> that a <c>cmd_start</c> of the same command follows; the commands of a ribbon macro)
/// and the replay (<see cref="UnknownCommandGrace"/>) agree.
/// </summary>
public static class CommandName
{
    /// <summary>The name without surrounding blanks and without its prefixes; the case is kept.</summary>
    public static string Strip(string? name) => (name ?? "").Trim().TrimStart('_', '.', '-', '*', '\'');

    /// <summary>The comparison form: <see cref="Strip"/> in upper case. Empty when there is no name.</summary>
    public static string Key(string? name) => Strip(name).ToUpperInvariant();

    /// <summary>True when both name the same command. Names with nothing left after the prefixes are never the same.</summary>
    public static bool Same(string? a, string? b)
    {
        var k = Key(a);
        return k.Length > 0 && k == Key(b);
    }
}
