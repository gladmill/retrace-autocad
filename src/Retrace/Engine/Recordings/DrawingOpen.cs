using System.Text.Json;

namespace Retrace.Engine.Recordings;

/// <summary>What AutoCAD runs by itself while it opens or creates a drawing: acaddoc.lsp, S::STARTUP, and in AutoCAD Electrical its
/// start-up LISP with the commands it runs (UNDO Control None, UNDO All). The recorder sees them as LISP and commands like any other,
/// but they are not steps: replaying them fails (S::STARTUP-LOAD is no command) or does what opening the drawing does anyway.
/// The recorder marks such LISP with <c>"startup": true</c> on its <c>lisp_start</c> (it knows whether the person clicked or typed since
/// the drawing opened); recordings made before that are judged by time (<see cref="StartupMs"/>).</summary>
public static class DrawingOpen
{
    /// <summary>Older recordings: how long after a drawing opens LISP still counts as its start-up; the person acting (a click, a
    /// command outside LISP, a checkpoint) ends it earlier.</summary>
    public const long StartupMs = 10_000;

    /// <summary>Event types that are the person acting: they end a drawing's start-up, and also end skipping a start-up LISP whose end
    /// was never recorded (the drawing closed while it ran), so one lost event cannot hide the rest of a recording.</summary>
    static bool IsPersonActing(string? type) =>
        type is "ribbon" or "tab" or "palette_values" or "palette_click" or "native_click" or "native_select" or "modal_enter" or "mark" or "doc_created";

    /// <summary>Follows a recording event by event and says which events belong to a drawing's start-up LISP. One rule for the recorder
    /// (checkpoint names) and the readers (steps, replay).</summary>
    public sealed class Tracker
    {
        long _openedAt = -1;   // t of the last doc_created while its start-up may still run; -1 = none
        int _lisp;             // depth of start-up LISP being skipped

        /// <summary>Recorder: whether a LISP starting now (at <paramref name="t"/>) is start-up LISP, given when the person last
        /// clicked or pressed a key. Call before writing its lisp_start, then <see cref="Feed"/> that event.</summary>
        public bool IsStartupLisp(long t, long lastInputT) =>
            _lisp > 0 || (_openedAt >= 0 && t - _openedAt <= StartupMs && lastInputT <= _openedAt);

        /// <summary>One event, in file order; true when it is part of start-up LISP (skip it). <paramref name="startupFlag"/>: the
        /// lisp_start's recorded <c>startup</c>, null when the recording has none.</summary>
        public bool Feed(string? type, long t, bool? startupFlag = null)
        {
            if (_lisp > 0)
            {
                if (IsPersonActing(type)) _lisp = 0;   // its end was lost: stop skipping, and treat this event normally
                else
                {
                    if (type == "lisp_start") _lisp++;
                    else if (type is "lisp_end" or "lisp_cancel") _lisp--;
                    return true;
                }
            }
            if (type == "doc_created") { _openedAt = t; return false; }
            if (type == "lisp_start")
            {
                var startup = startupFlag ?? (_openedAt >= 0 && t - _openedAt <= StartupMs);
                if (startup) { _lisp = 1; return true; }
                _openedAt = -1;   // the person's LISP: the drawing is open
                return false;
            }
            if (IsPersonActing(type) || type is "cmd_start" or "cmd_unknown") _openedAt = -1;
            return false;
        }
    }

    /// <summary>The event numbers (<c>n</c>) of the LISP that ran while a drawing opened, with everything inside it (its commands
    /// and their prompts). Readers skip these events.</summary>
    public static HashSet<long> StartupEvents(IEnumerable<JsonElement> events)
    {
        var skip = new HashSet<long>();
        var tracker = new Tracker();
        foreach (var e in events)
        {
            var type = e.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() : null;
            long t = e.TryGetProperty("t", out var tv) && tv.ValueKind == JsonValueKind.Number ? tv.GetInt64() : 0;
            bool? flag = e.TryGetProperty("startup", out var sv) && sv.ValueKind is JsonValueKind.True or JsonValueKind.False ? sv.GetBoolean() : null;
            if (tracker.Feed(type, t, flag) && e.TryGetProperty("n", out var nv) && nv.ValueKind == JsonValueKind.Number) skip.Add(nv.GetInt64());
        }
        return skip;
    }

    /// <summary>The drawing's full path from a <c>doc_created</c> event, or null when it has none (a new drawing never saved, or an
    /// older recording, which wrote only the file name).</summary>
    public static string? PathOf(JsonElement docCreated)
    {
        var p = docCreated.TryGetProperty("path", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return p != null && IsWindowsFullPath(p) ? p : null;
    }

    /// <summary>C:\... or \\server\...: checked by hand, because Path.IsPathRooted on the Mac or Linux test machine does not know drive letters.</summary>
    public static bool IsWindowsFullPath(string p) =>
        (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/') || p.StartsWith(@"\\", StringComparison.Ordinal);
}
