using Retrace.Engine.Recordings;
namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). The pure rules for screenshots taken while recording:
// which events get one, what the files are called, and when a queued request may be fulfilled (throttle and de-duplication).
// Taking the picture itself is ShotService (Windows only).

public enum ShotTarget { Window, Dialog }

/// <param name="N">the `n` of the event the picture belongs to</param>
/// <param name="Kind">file name part: ribbon, cmd-start, prompt, cmd-end, cmd-cancel, cmd-fail, dialog, mark</param>
/// <param name="DueMs">session time (ms) from which it may be taken: the UI needs a moment to paint what the event caused</param>
/// <param name="Window">a dialog picture: the handle of the dialog window it is for (0 = the foreground window of the process, as before). Another dialog that opens meanwhile is not this one's picture.</param>
public sealed record ShotRequest(int N, string Kind, ShotTarget Target, long DueMs, long Window = 0);

public static class ShotNaming
{
    /// <summary>`012-ribbon.png`: n zero-padded to three digits (more digits when n needs them), then the kind.</summary>
    public static string FileName(int n, string kind) => $"{n:000}-{Clean(kind)}.png";

    /// <summary>Pictures folder next to <paramref name="sessionPath"/> (a recording.jsonl): `screenshots`.</summary>
    public static string FolderFor(string sessionPath) => TestFolders.ScreenshotsDir;

    /// <summary>The `shot` event's `file` for a picture of <paramref name="sessionPath"/>: `screenshots/012-ribbon.png`, relative to the recording, forward slashes.</summary>
    public static string RelativePathFor(string sessionPath, int n, string kind) => FolderFor(sessionPath) + "/" + FileName(n, kind);

    static string Clean(string kind)
    {
        var s = new string((kind ?? "").ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray()).Trim('-');
        return s.Length == 0 ? "shot" : s;
    }
}

public static class ShotRules
{
    /// <summary>How long after an event the picture is taken: AutoCAD repaints after the handler returns.</summary>
    public const int SettleMs = 200;
    public const int DialogSettleMs = 150;
    /// <summary>How long after a click inside a dialog or palette its picture is taken: the control has reacted (a list filled by a tree selection) and the window has been painted. The first of the
    /// timer and idle pumps at or after this time takes it; a click that comes sooner takes the one before it at once.</summary>
    public const int ClickSettleMs = 300;

    /// <summary>The picture request an event leads to, or null. <paramref name="input"/> is the prompt event's `input` flag.
    /// Prompt results only get one when a real click or key press led to them; a scripted command (no flags) is covered by its cmd_end.</summary>
    public static ShotRequest? For(string eventType, int n, bool input, long nowMs) => eventType switch
    {
        "ribbon" => new ShotRequest(n, "ribbon", ShotTarget.Window, nowMs + SettleMs),
        "cmd_start" => new ShotRequest(n, "cmd-start", ShotTarget.Window, nowMs + SettleMs),
        "prompt" when input => new ShotRequest(n, "prompt", ShotTarget.Window, nowMs + SettleMs),
        "cmd_end" => new ShotRequest(n, "cmd-end", ShotTarget.Window, nowMs + SettleMs),
        "cmd_cancel" => new ShotRequest(n, "cmd-cancel", ShotTarget.Window, nowMs + SettleMs),
        "cmd_fail" => new ShotRequest(n, "cmd-fail", ShotTarget.Window, nowMs + SettleMs),
        "mark" => new ShotRequest(n, "mark", ShotTarget.Window, nowMs + SettleMs),
        "palette_values" or "palette_click" => new ShotRequest(n, "palette", ShotTarget.Window, nowMs + SettleMs),
        _ => null,
    };

    /// <summary>The window picture of a palette step that ends in a context menu choice (`context_menu` in a palette), like a palette click's.</summary>
    public static ShotRequest Palette(int n, long nowMs) => new(n, "palette", ShotTarget.Window, nowMs + SettleMs);

    /// <summary>Smart zoom: a palette step whose window picture was left out because its close-up was expected must get one anyway when the close-up turns out to be impossible (the click collapsed an auto-hide palette,
    /// hid it, or opened a modal dialog over it, and nothing was kept before the click): the window picture is then taken in its place.</summary>
    public static bool NeedsWindowFallback(bool windowSuppressed, bool haveCloseup) => windowSuppressed && !haveCloseup;

    /// <summary>The dialog picture: for `modal_title` when one arrived, else for `modal_enter`.</summary>
    public static ShotRequest Dialog(int n, long nowMs, long window = 0) => new(n, "dialog", ShotTarget.Dialog, nowMs + DialogSettleMs, window);
}

/// <summary>
/// Holds the queued requests and decides which may be fulfilled now. Rules:
///  - nothing before its due time;
///  - at most one picture per target (window, dialog) every <see cref="MinGapMs"/>;
///  - a request that falls inside that gap and has the same kind as the last picture of the target is a duplicate and is dropped;
///    one of another kind waits for the next tick;
///  - a request that has waited more than <see cref="MaxWaitMs"/> past its due time is dropped (the screen no longer shows its event).
/// Single-threaded: used from the UI thread only.
/// </summary>
public sealed class ShotScheduler
{
    public const int MinGapMs = 250;
    public const int MaxWaitMs = 1500;

    readonly List<ShotRequest> _pending = new();
    readonly Dictionary<ShotTarget, (long At, string Kind)> _last = new();

    public int Pending => _pending.Count;

    /// <summary>Takes the queued requests of <paramref name="target"/> whose event is one of <paramref name="ns"/> out of the queue (the dialog turned out to be a flash: no picture of it); how many went.</summary>
    public int Cancel(ShotTarget target, params long[] ns) => _pending.RemoveAll(r => r.Target == target && ns.Contains(r.N));
    public int Dropped { get; private set; }

    public void Enqueue(ShotRequest r) => _pending.Add(r);

    /// <summary>Is a request of this kind queued (a cheap look, for the caller that only wants to do work when there is something to take).</summary>
    public bool HasPending(string kind) => _pending.Any(r => r.Kind == kind);

    /// <summary>Requests to fulfil now, in event order. <paramref name="force"/> ignores the due time (used when recording stops); <paramref name="early"/> ignores it too and also the gap rule: the next click is about to
    /// change the screen, each queued picture is of its own click and is taken whatever was taken a moment ago (it does not count as the picture the gap rule looks at, either).
    /// <paramref name="mayTake"/>: a request it says no to is held, and its due time moves to now, so the hold never makes it too old (the age limit counts from the end of the hold).</summary>
    public List<ShotRequest> TakeReady(long nowMs, bool force = false, Func<ShotRequest, bool>? mayTake = null, bool early = false)
    {
        var take = new List<ShotRequest>();
        foreach (var r in _pending.OrderBy(r => r.N).ToList())
        {
            if (!force && !early && r.DueMs > nowMs) continue;
            if (!force && mayTake != null && !mayTake(r))
            {
                int at = _pending.IndexOf(r);
                if (at >= 0 && r.DueMs < nowMs) _pending[at] = r with { DueMs = nowMs };
                continue;
            }
            if (nowMs - r.DueMs > MaxWaitMs) { _pending.Remove(r); Dropped++; continue; }
            if (early) { _pending.Remove(r); take.Add(r); continue; }
            if (_last.TryGetValue(r.Target, out var last) && nowMs - last.At < MinGapMs)
            {
                if (last.Kind == r.Kind) { _pending.Remove(r); Dropped++; }   // duplicate within the gap
                continue;                                                      // another kind: try again next tick
            }
            _pending.Remove(r);
            _last[r.Target] = (nowMs, r.Kind);
            take.Add(r);
        }
        return take;
    }

    /// <summary>A request taken by <see cref="TakeReady"/> at <paramref name="nowMs"/> could not be fulfilled yet (the dialog has not painted): back into the queue, and the gap rule does not count it as a picture of its target.</summary>
    public void Untake(ShotRequest r, long nowMs)
    {
        _pending.Add(r);
        if (_last.TryGetValue(r.Target, out var l) && l.At == nowMs && l.Kind == r.Kind) _last.Remove(r.Target);
    }

    /// <summary>Forget everything: queued requests, the last picture per target and the drop count. A new recording starts its clock at 0,
    /// so a stale `_last` from the previous recording would put every request inside the gap and drop it.</summary>
    public void Clear()
    {
        _pending.Clear();
        _last.Clear();
        Dropped = 0;
    }
}

/// <summary>Pixel work that needs no Windows API: shrinking a picture so its longest side is at most a limit.</summary>
public static class ShotImage
{
    public const int MaxSide = 1400;

    /// <summary>The size after scaling down to <paramref name="maxSide"/> (unchanged when it already fits).</summary>
    public static (int W, int H) FitSize(int w, int h, int maxSide = MaxSide)
    {
        if (w <= 0 || h <= 0) return (Math.Max(w, 0), Math.Max(h, 0));
        var longest = Math.Max(w, h);
        if (longest <= maxSide) return (w, h);
        var f = (double)maxSide / longest;
        return (Math.Max(1, (int)Math.Round(w * f)), Math.Max(1, (int)Math.Round(h * f)));
    }

    /// <summary>A dialog picture that has not been painted yet: inside the frame (a title bar's height and a few pixels of border left out) the picture holds at most two colors (the dialog's flat background, at most a
    /// stray pixel). A painted dialog has text, buttons or lines, far more. Such a picture is not kept: the dialog is looked at again a moment later.</summary>
    public static bool IsBlank(byte[] bgra, int w, int h)
    {
        if (w <= 0 || h <= 0 || bgra.Length < w * h * 4) return true;
        int top = Math.Min(48, h / 4), side = Math.Min(8, w / 8), bottom = Math.Min(8, h / 8);
        Span<int> seen = stackalloc int[3];
        int count = 0;
        for (int y = top; y < h - bottom; y++)
            for (int x = side; x < w - side; x++)
            {
                int i = (y * w + x) * 4;
                int c = (bgra[i] >> 2) | (bgra[i + 1] >> 2) << 8 | (bgra[i + 2] >> 2) << 16;   // blue, green, red; the low two bits dropped: a dither or gradient step is not content
                bool known = false;
                for (int k = 0; k < count; k++) if (seen[k] == c) { known = true; break; }
                if (known) continue;
                if (count == 2) return false;   // a third color: something is drawn
                seen[count++] = c;
            }
        return true;
    }

    /// <summary>Box-filter downscale of a 32-bit BGRA picture to the given size (area average, so text stays readable).</summary>
    public static byte[] Scale(byte[] bgra, int w, int h, int nw, int nh)
    {
        if (nw == w && nh == h) return bgra;
        var o = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            int y0 = (int)((long)y * h / nh), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * h / nh));
            for (int x = 0; x < nw; x++)
            {
                int x0 = (int)((long)x * w / nw), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * w / nw));
                long b = 0, g = 0, r = 0; int cnt = 0;
                for (int yy = y0; yy < y1 && yy < h; yy++)
                    for (int xx = x0; xx < x1 && xx < w; xx++)
                    {
                        int i = (yy * w + xx) * 4;
                        b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; cnt++;
                    }
                int d = (y * nw + x) * 4;
                if (cnt == 0) cnt = 1;
                o[d] = (byte)(b / cnt); o[d + 1] = (byte)(g / cnt); o[d + 2] = (byte)(r / cnt); o[d + 3] = 255;
            }
        }
        return o;
    }
}
