using System.Text.Json;
using System.Text.Json.Nodes;

namespace Retrace.Core;

/// <summary>
/// Drag samples. A jig (<c>Editor.Drag</c>, <c>acedDragGen</c>) asks the same point or angle prompt again for every sample, and AutoCAD raises a Prompted event with status OK for each: in bursts
/// of a few milliseconds when the mouse moves or a key is pressed, and every millisecond or so in a jig that keeps sampling (seen in AutoCAD 2026 with a .NET <c>EntityJig</c>: 17,000 results
/// in 10 s). The recorder writes each as a `prompt` event; this class says which of them is the person's answer, so the plan sees one answer per prompt, not thousands.
/// <para>An <b>episode</b> is a stretch of `prompt` events of the same kind and message, all status OK, each at most <see cref="EpisodeGapMs"/> after the one before (the recorder's own bookkeeping
/// events in between, pictures and view marks, do not break it; anything else does). Its answers end it in pieces: a <b>genuine</b> `input: true` member closes a piece, and the piece is that answer
/// plus the samples before it (which are dropped). A member is genuine unless it is a <b>spurious claim</b>: a key press claims the next result, and a jig makes one at once, so each key typed
/// on the way ("0", ",", "0" of "0,0") claimed a sample whose value was still the mouse resting where it was. Such a member (`by: "keys"`, the same value as the member before it, more members after
/// it) answers nothing; the person's Enter, the last sample, does. A claim is spurious only if the member after it follows within <see cref="SpuriousGapMs"/> (typing goes on): a person who types the same value
/// at a prompt that repeats (a getpoint loop: <c>5,5</c> Enter three times) takes seconds per answer, and each of their Enters is genuine. A member the recorder flagged `drag: true` is never an answer.</para>
/// <para>The tail of an episode after its last genuine answer holds no answer of its own. It ends in a keyword, Enter or Esc (a prompt of the same kind and message whose status is not OK): that
/// prompt is the answer, and the samples that carry drag evidence go (flagged, or in a burst); the others stay, a script's own answers (<c>(command "_.LINE" p1 p2 "")</c> keeps p2). It holds `drag: true` samples: they go, and a last unflagged unclaimed one stays as recorded (the script's). It is an older recording's burst
/// (a recording from before the recorder wrote `drag` at all, so the recorder cannot have meant "not a drag sample" for it; no claims, at least <see cref="MinBurst"/> results each within <see cref="BurstGapMs"/> of the one before) and the episode has no genuine answer at all: its last result is the
/// answer and counts as the person's (its `input` becomes true): the recorder could not tell the click from the samples, and the replay types it when the command reaches the prompt.</para>
/// </summary>
public static class DragRuns
{
    /// <summary>Longest time (ms) between two results of one episode. A jig that samples on events only is quiet while the person looks at the screen; one that keeps sampling is not.</summary>
    public const long EpisodeGapMs = 5000;
    /// <summary>Longest time (ms) between two results of one burst: samples come 1 to 10 ms apart, a typed answer to a plain prompt seconds after the one before.</summary>
    public const long BurstGapMs = 200;
    /// <summary>Results in a burst an older recording (no `drag` flag, no claim) needs for its last one to be taken as the answer.</summary>
    public const int MinBurst = 3;
    /// <summary>Longest time (ms) between a spurious key claim and the member after it. The claims of the keys typed in a jig follow one another 0.3 to 0.7 s apart (seen); a person's repeated answers at a repeating prompt take longer. (A recording made by the current recorder has no spurious claims: only Enter, Space and the left button claim.)</summary>
    public const long SpuriousGapMs = 1000;
    /// <summary>The type of the event that takes the place of a dropped sample: same position in the list (the callers' indexes stay valid), neutral to <see cref="Plan.ScriptChain"/>, read by nothing.</summary>
    public const string DroppedType = "drag_dropped";

    /// <summary>The events with every drag episode reduced to its answer: a dropped sample becomes a stub of type <see cref="DroppedType"/> (same index), a promoted one is a copy with `input: true`.
    /// The list has the same length as <paramref name="events"/>. A recording without a drag episode comes back unchanged (same elements).</summary>
    public static List<JsonElement> Collapse(IReadOnlyList<JsonElement> events)
    {
        var result = new List<JsonElement>(events);
        bool knowsDrag = events.Any(e => Plan.Str(e, "type") == "prompt" && e.TryGetProperty("drag", out _));   // written by a recorder that tells samples apart: nothing is guessed
        var run = new List<int>();
        string? key = null;
        long lastT = 0;
        void Finish(bool endedByNonOk)
        {
            if (run.Count > 0) Decide(events, run, endedByNonOk, knowsDrag, result);
            run.Clear(); key = null;
        }
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var type = Plan.Str(e, "type");
            if (type != "prompt")
            {
                if (type is "shot" or "view_stale" or "state" or DroppedType) continue;
                Finish(false);
                continue;
            }
            var k = (Plan.Str(e, "kind") ?? "") + "\u0001" + (Plan.Str(e, "message") ?? "") + "\u0001" + (Plan.Str(e, "doc") ?? "");
            long t = Plan.Num(e, "t");
            bool ok = Plan.Str(e, "status") is null or "OK";
            if (run.Count > 0 && k == key && !ok) { Finish(true); continue; }   // the answer that ended the episode: a keyword, Enter, Esc
            if (run.Count > 0 && (k != key || t - lastT > EpisodeGapMs)) Finish(false);
            if (!ok) continue;
            run.Add(i); key = k; lastT = t;
        }
        Finish(false);
        return result;
    }

    /// <summary>Whether a window message ends a prompt (the recorder lets only such input claim a drag sample): the left mouse button down (0x0201), and Enter (0x0D) or Space (0x20) key down (0x0100).
    /// Any other key is typed on the way ("0" of "0,0"); the middle button pans; the right button is Enter or a shortcut menu depending on the setup, and when it is Enter the result it gives
    /// is a non-OK one of its own (<see cref="Collapse"/> takes that as the answer).</summary>
    public static bool EndsPrompt(uint message, long wParam) => message == 0x0201 || (message == 0x0100 && wParam is 0x0D or 0x20);

    /// <summary>Whether an unclaimed candidate result (same kind and message as the one before) is flagged as a drag sample: it came within <see cref="BurstGapMs"/>, somebody used the mouse or keyboard
    /// since the result before, and no LISP is running. A script, LISP or menu macro that gives its own answers (<c>(command "_.PLINE" p1 p2 p3 "")</c>) makes bursts with nobody at the input.</summary>
    public static bool IsDragSample(bool candidate, bool claimed, long msSincePrevious, bool inputSincePrevious, bool lispRunning) =>
        candidate && !claimed && msSincePrevious <= BurstGapMs && inputSincePrevious && !lispRunning;

    static bool Flagged(JsonElement e) => Plan.Bool(e, "drag") == true;
    static string Raw(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetRawText() : "";

    static void Decide(IReadOnlyList<JsonElement> ev, List<int> run, bool endedByNonOk, bool knowsDrag, List<JsonElement> result)
    {
        // pieces: the samples up to and including each genuine answer
        int from = 0;
        bool anyGenuine = false;
        HashSet<int>? dropped = null;
        void Drop(int idx) => (dropped ??= new()).Add(idx);
        for (int m = 0; m < run.Count; m++)
        {
            var e = ev[run[m]];
            if (Plan.Bool(e, "input") != true || Flagged(e)) continue;
            bool spurious = Plan.Str(e, "by") == "keys" && m > 0 && m < run.Count - 1 && Raw(e, "value") == Raw(ev[run[m - 1]], "value")
                && Plan.Num(ev[run[m + 1]], "t") - Plan.Num(e, "t") <= SpuriousGapMs;
            if (spurious) continue;
            anyGenuine = true;
            for (int q = from; q < m; q++) Drop(run[q]);
            from = m + 1;
        }
        if (from >= run.Count) { Apply(ev, dropped, null, result); return; }
        // the tail: no answer of its own
        var tail = run.GetRange(from, run.Count - from);
        if (tail.Any(i => Plan.Bool(ev[i], "input") == null && !Flagged(ev[i]))) { Apply(ev, dropped, null, result); return; }   // a recording that has no `input` flag: nothing says what is a sample
        // the members of a burst (MinBurst or more results, each within BurstGapMs of the one before)
        var inBurst = new HashSet<int>();
        for (int q = 0, start = 0; q < tail.Count; q++)
        {
            if (q > start && Plan.Num(ev[tail[q]], "t") - Plan.Num(ev[tail[q - 1]], "t") > BurstGapMs) start = q;
            if (q - start + 1 == MinBurst) for (int b = start; b <= q; b++) inBurst.Add(tail[b]);
            else if (q - start + 1 > MinBurst) inBurst.Add(tail[q]);
        }
        if (endedByNonOk)
        {
            // the keyword or Enter that follows is the answer; the samples before it go, the results with no sign of a drag (a script's answers) stay
            foreach (var i in tail) if (Flagged(ev[i]) || inBurst.Contains(i)) Drop(i);
            Apply(ev, dropped, null, result);
            return;
        }
        if (tail.Any(i => Flagged(ev[i])))
        {
            foreach (var i in tail) if (i != tail[^1] || Flagged(ev[i])) Drop(i);   // only an unflagged last result stays
            Apply(ev, dropped, null, result);
            return;
        }
        // an answer came before it, or a claim: as recorded; so is any recording whose recorder wrote `drag` (it flagged what it took for samples, and left out the script's own answers)
        if (anyGenuine || knowsDrag || tail.Any(i => Plan.Bool(ev[i], "input") != false)) { Apply(ev, dropped, null, result); return; }
        // an older recording: nothing in the episode was claimed or flagged. A burst of results is a drag: its last result is the answer.
        if (inBurst.Count == 0) { Apply(ev, dropped, null, result); return; }
        var last = tail[^1];
        foreach (var i in tail) if (i != last) Drop(i);
        Apply(ev, dropped, last, result);
    }

    /// <summary>Puts the stubs in for the dropped indexes, and the promoted copy (input true) for <paramref name="promote"/>.</summary>
    static void Apply(IReadOnlyList<JsonElement> ev, HashSet<int>? dropped, int? promote, List<JsonElement> result)
    {
        if (dropped != null) foreach (var i in dropped) result[i] = Stub(ev[i]);
        if (promote is { } p) result[p] = With(ev[p]);
    }

    static JsonElement Stub(JsonElement e)
    {
        using var doc = JsonDocument.Parse($"{{\"t\":{Plan.Num(e, "t")},\"n\":{Plan.Num(e, "n")},\"type\":\"{DroppedType}\"}}");
        return doc.RootElement.Clone();
    }

    static JsonElement With(JsonElement e)
    {
        var node = JsonNode.Parse(e.GetRawText())!.AsObject();
        node["input"] = true;
        node["collapsed"] = true;
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }
}
