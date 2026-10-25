namespace Retrace.Core;

/// <summary>
/// AutoCAD's security prompts: with SECURELOAD 1 (the default), loading a dll or LISP file from a folder that is not a trusted location
/// stops AutoCAD at a modal task dialog until a person answers it ("Security - Unsigned Executable File": Always Load / Load Once /
/// Do Not Load; "Security - Signed Executable File" style titles for signed files; "File Loading - Security Concern" in older releases).
/// They are a decision for a person, never a test step: a recording does not replay them, and a replay that meets one stops with
/// <see cref="Message"/> instead of waiting at it. AutoCAD-free (unit-tested); the window search is <see cref="Native.OpenSecurityPrompt"/>.
/// </summary>
static class SecurityPrompt
{
    /// <summary>True for the title of an AutoCAD security prompt.</summary>
    public static bool IsTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var t = title.Trim();
        return t.StartsWith("Security - ", StringComparison.OrdinalIgnoreCase)
            || t.Equals("File Loading - Security Concern", StringComparison.OrdinalIgnoreCase)
            || t.EndsWith("Not in Trusted Folder", StringComparison.OrdinalIgnoreCase);   // the signed-file prompt as some releases title it
    }

    /// <summary>A recorded dialog (modal_title) is a security prompt: the title matches and it is a standard task dialog (#32770), so a
    /// plugin's own dialog that happens to be called "Security - Settings" stays a step. No window class recorded: the title decides.</summary>
    public static bool IsRecorded(string? title, string? windowClass) =>
        IsTitle(title) && (string.IsNullOrEmpty(windowClass) || windowClass == "#32770");

    /// <summary>What the replay says when it stops at a security prompt (command line, replay window and result file).</summary>
    public static string Message(string title) =>
        $"blocked by AutoCAD's security prompt \"{title.Trim()}\": AutoCAD is asking whether to load a file from a folder that is not a trusted location. " +
        "Answer it in AutoCAD (Load Once, or Always Load if you trust the file), or add the file's folder to Options > Files > Trusted Locations, then run the test again";
}
