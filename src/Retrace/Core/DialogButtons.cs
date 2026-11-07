namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// Which dialog button a name (Win32 text or UI Automation Name) or a UI Automation AutomationId stands for.

public static class DialogButtons
{
    /// <summary>"ok" | "cancel" | null. Looks at the visible name first, then at the automation id. Case, "&amp;" (accelerator),
    /// spaces and punctuation are ignored; a leading "btn" / "button" and a trailing "button" in an id are ignored too ("btnOk", "OkButton").</summary>
    public static string? Role(string? name, string? automationId = null) => FromName(name) ?? FromName(automationId);

    /// <summary>The Help button of a dialog (text "Help", mnemonic and dots ignored): it opens a window of its own, so a recording leaves it out.</summary>
    public static bool IsHelp(string? name) => FromLetters(name) == "help";

    static string? FromLetters(string? s) => string.IsNullOrWhiteSpace(s) ? null : new string(s.Where(char.IsLetter).ToArray()).ToLowerInvariant();

    static string? FromName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var k = new string(s.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        foreach (var affix in new[] { "btn", "button" })
            if (k.StartsWith(affix, StringComparison.Ordinal) && k.Length > affix.Length) { k = k[affix.Length..]; break; }
        if (k.EndsWith("button", StringComparison.Ordinal) && k.Length > 6) k = k[..^6];
        return k switch { "ok" or "okay" => "ok", "cancel" => "cancel", _ => null };
    }
}
