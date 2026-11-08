namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>Which drawing a Document holds, by the database's unmanaged pointer (zero: it could not be read). Two questions, with different answers for a pointer that could not be read.</summary>
public static class DbIdentity
{
    /// <summary>For restoring a view: the same drawing, or one that cannot be told apart (zero): a view is dropped only when another drawing is certain.</summary>
    public static bool SameForView(long current, long saved) => current == 0 || saved == 0 || current == saved;

    /// <summary>For hooking events: a document's events die with its drawing (SDI) and with <c>Unhook</c>. Hook again unless it is hooked now AND both pointers were read AND they are equal.
    /// An unreadable pointer, or a document that was unhooked (its drawing closed: <c>OnDocGone</c>), is never "the same".</summary>
    public static bool NeedsRehook(bool hooked, long current, long hookedFor) => !hooked || current == 0 || hookedFor == 0 || current != hookedFor;
}
