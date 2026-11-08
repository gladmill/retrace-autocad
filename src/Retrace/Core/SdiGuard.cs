namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>The single document interface (system variable SDI = 1): AutoCAD keeps one drawing open, and opening or adding another one
/// replaces it, asking to save it first. A test that opens its own drawing (RETRACERUN, a suite, a Tdoc run) must never let that happen to
/// a drawing with unsaved changes of a person. A drawing a test opened itself (a copy in Retrace's temp folders) is the test's own: it is
/// saved before the next one is opened (so AutoCAD asks nothing; closing it instead would end AutoCAD), and its temp files are deleted when a day old.</summary>
public static class SdiGuard
{
    /// <summary>The folder names Retrace's own test drawings live in (temp copies of the template and of recorded drawings).</summary>
    public const string OwnFolder = "retrace-sdi";
    /// <summary>A suite's copies of recorded drawings live in <c>retrace-suite-&lt;guid&gt;</c> (the one constant, used by the suite and by <see cref="IsOwnTestDrawing"/>).</summary>
    public const string SuiteCopyFolderPrefix = "retrace-suite-";

    /// <summary>A temp file or folder of a test is deleted when this old.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    /// <summary>A temp file is stale when a day has passed since it was created or last written, whichever is later (a copy keeps the write time of what it was
    /// copied from, so the creation time counts too).</summary>
    public static bool IsStaleTemp(DateTime createdUtc, DateTime lastWriteUtc, DateTime nowUtc) => nowUtc - (createdUtc > lastWriteUtc ? createdUtc : lastWriteUtc) > StaleAfter;

    /// <summary>The drawing files this session created (see <see cref="SdiOwnFiles"/>); the plugin registers every copy it makes.</summary>
    public static SdiOwnFiles Own { get; set; } = new(System.IO.Path.GetTempPath());

    /// <summary>Under SDI an "open" step (RETRACERUN, Tdoc, a suite, a replay) never opens the person's own file: the drawing that is open is replaced, and the test
    /// would change the file the next open meets with unsaved changes. It opens a temp copy, unless the path already is one.</summary>
    public static bool OpenStepNeedsCopy(bool single, string? path) => single && !string.IsNullOrEmpty(path) && !IsOwnTestDrawing(path);

    /// <summary>Commands that replace the open drawing under SDI by opening a file or starting a new drawing, with any prefix and case (<see cref="CommandName.Key"/>): a recorded one typed
    /// during a replay would open the person's file in place of the open one. (CLOSE is not one: a test may end by closing its drawing.) An early warning: any drawing that opens
    /// during a replay and is not Retrace's own open step stops the run anyway.</summary>
    public static bool OpensDrawingCommand(string? command) =>
        CommandName.Key(command) is "OPEN" or "QNEW" or "NEW" or "RECOVER" or "RECOVERALL" or "PARTIALOPEN" or "XOPEN" or "OPENSHEETSET";

    /// <summary>Commands that close the drawing: CLOSE, CLOSEALL, QUIT, EXIT. With SDI there is one drawing, and closing it ends AutoCAD (checked: CloseAndDiscard and COM Close end in QUIT), so a
    /// replay does not type them.</summary>
    public static bool ClosesDrawingCommand(string? command) => CommandName.Key(command) is "CLOSE" or "CLOSEALL" or "QUIT" or "EXIT";

    /// <summary>SDI = 1 (single document interface). 2 is "disabled by the system" (a running application needs it): treated as single too.</summary>
    public static bool IsSingle(int sdi) => sdi != 0;

    /// <summary>True when <paramref name="path"/> is a drawing this session's tests created themselves (<see cref="SdiOwnFiles"/>): a file this process made below the
    /// temp folder. A folder name alone never qualifies.</summary>
    public static bool IsOwnTestDrawing(string? path) => Own.IsOwn(path);

    /// <summary>Why a test must not open its own drawing now, or null when it may. With SDI 0 the new drawing opens beside the others, as always.
    /// With SDI on, the drawing that is open would be replaced: when it holds unsaved changes the test stops with this message instead.
    /// Unsaved changes in the drawing of the run that is going on, or of one that could not be saved when it ended (<see cref="SdiOwnFiles.UnsavedIsOurs"/>), are the
    /// test's own and do not count; changes a person made in a test drawing after its run ended do.</summary>
    /// <param name="open">the drawings that are open: full name (or just the file name), and whether each has unsaved changes</param>
    public static string? Refusal(bool single, IEnumerable<(string Name, bool Modified)> open)
    {
        if (!single) return null;
        var modified = open.Where(d => d.Modified && !Own.UnsavedIsOurs(d.Name)).Select(d => System.IO.Path.GetFileName(d.Name)).ToList();
        if (modified.Count == 0) return null;
        return $"SDI is on (single document interface) and {string.Join(", ", modified)} has unsaved changes: the test opens its own drawing, which would replace it. "
             + "Save it, or reopen its file to discard the changes, or set SDI to 0, then run the test again.";
    }

    /// <summary>What to do with the drawing that is open before a test opens another one under SDI: <c>Save</c> a test's own drawing (it would otherwise ask
    /// to save), <c>Keep</c> any other (it is replaced as it is: it holds no unsaved changes, <see cref="Refusal"/> has checked).</summary>
    public static bool SaveOwnBeforeOpen(bool single, string? openName) => single && IsOwnTestDrawing(openName);
}
