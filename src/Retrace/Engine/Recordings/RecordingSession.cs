// Recordings ingest, part 1: read Retrace session files (JSONL) and rebuild steps from the events.
// Schema: docs/recording-format.md
using System.Globalization;
using System.Text.Json;

namespace Retrace.Engine.Recordings;

/// <summary>A screenshot the recorder took for an event (a `shot` event): File as recorded (relative to the session file, '/'),
/// Abs the path on this machine.</summary>
public sealed record RecordingShot(string File, string What, string Abs);

public sealed class RecordingEvent
{
    public long T { get; init; }
    /// <summary>The recorder's 1-based event number (`n`), null in sessions recorded before it existed.</summary>
    public long? N { get; init; }
    public string Type { get; init; } = "";
    public string? Doc { get; init; }
    public JsonElement Raw { get; init; }

    public string? Str(string key) => Raw.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed class RecordingAnswer
{
    public string Kind { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Message { get; init; }
    public string? Keyword { get; init; }
    /// <summary>The prompt's options as the recorder wrote them (`keywords`), else empty: then they are parsed from the message.</summary>
    public List<string> Options { get; init; } = new();

    static readonly System.Text.RegularExpressions.Regex Bracket = Rx.Create(@"\[([^\]]*)\]", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>The prompt's keyword options: the recorded list, else the bracketed part of the message ("[3P/2P/Ttr (tan tan radius)]" -> 3P, 2P, Ttr).</summary>
    public IEnumerable<string> KeywordOptions()
    {
        if (Options.Count > 0) return Options;
        var list = new List<string>();
        if (Message == null) return list;
        foreach (System.Text.RegularExpressions.Match m in Bracket.Matches(Message))
            foreach (var part in m.Groups[1].Value.Split('/'))
            {
                var w = part.Trim().Split(' ', '(')[0];
                if (w.Length > 0) list.Add(w);
            }
        return list;
    }

    /// <summary>True for a Keyword result whose keyword is not one of the prompt's options (case-insensitive, or a unique prefix as AutoCAD
    /// accepts): then the tester typed a value at a prompt that also takes keywords (CIRCLE radius given "20").</summary>
    public bool IsTypedInput
    {
        get
        {
            if (Status != "Keyword" || string.IsNullOrEmpty(Keyword)) return false;
            if (Kind == "string") return true; // a text prompt has no options: whatever came back was typed (seen with spaces in R25.1)
            var opts = KeywordOptions().ToList();
            if (opts.Count == 0) return false; // options unknown: keep the recorder's word
            if (opts.Any(o => string.Equals(o, Keyword, StringComparison.OrdinalIgnoreCase))) return false;
            return !opts.Any(o => o.StartsWith(Keyword, StringComparison.OrdinalIgnoreCase));
        }
    }
    public JsonElement? Value { get; init; }
    /// <summary>Recorder's flag: true = a click/key press led to it, false = mouse-move noise, null = not recorded.</summary>
    public bool? Input { get; init; }
    /// <summary>"mouse" (a pick) or "keys" (typed, then Enter) for an `input: true` result; null in older sessions.</summary>
    public string? By { get; init; }
    /// <summary>Time of the result (ms since the session started).</summary>
    public long T { get; init; }
    /// <summary>Recorder's number of the Prompting event this result belongs to (nesting detection), null if not recorded.</summary>
    public long? Seq { get; init; }
    /// <summary>Screenshots taken for this prompt event.</summary>
    public List<RecordingShot> Shots { get; } = new();

    /// <summary>Short human text for the value: "(10, 20)", "3 objects (LINE, CIRCLE) by Window", "Enter", "[Close]".</summary>
    public string Describe()
    {
        switch (Status)
        {
            case "None": return "Enter";
            case "Cancel": return "Esc (cancelled)";
            case "Keyword": return IsTypedInput ? $"typed \"{Keyword}\"" : $"keyword [{Keyword}]";
            case "OK": break;
            default: return Status;
        }
        if (Value is not { } v) return "(no value)";
        switch (Kind)
        {
            case "point" or "corner":
                if (v.ValueKind != JsonValueKind.Array) break;
                return "(" + string.Join(", ", v.EnumerateArray().Select(x => Num(x.GetDouble()))) + ")";
            case "distance" or "double": return Num(v.GetDouble());
            case "angle": return Num(v.GetDouble() * 180.0 / Math.PI) + " deg";
            case "integer": return v.GetInt64().ToString(CultureInfo.InvariantCulture);
            case "string": return $"\"{v.GetString()}\"";
            case "entity" or "nestedEntity":
                return $"{S(v, "dxf") ?? "entity"} (handle {S(v, "handle")})";
            case "selection":
            {
                var n = v.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                var kinds = v.TryGetProperty("dxf", out var d) && d.ValueKind == JsonValueKind.Array
                    ? d.EnumerateArray().Select(x => x.GetString() ?? "?").GroupBy(x => x).OrderByDescending(g => g.Count()).Take(4).Select(g => g.Key).ToList()
                    : new List<string>();
                var method = S(v, "method");
                return $"{n} object{(n == 1 ? "" : "s")}" + (kinds.Count > 0 ? $" ({string.Join(", ", kinds)})" : "") + (method != null ? $" by {method}" : "");
            }
        }
        return v.ToString();
    }

    static string? S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static string Num(double d) => Math.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>Drawing digest recorded by RETRACERECORD (`state` event) and RETRACEMARK (`mark.state`): see the schema doc.</summary>
public sealed class RecordingState
{
    public int Count { get; init; }
    public SortedDictionary<string, int> ByType { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> ByLayer { get; } = new(StringComparer.Ordinal);
    public List<string> Layers { get; } = new();
    public string Hash { get; init; } = "";
    /// <summary>Digest key rules ("v"; missing = 1).</summary>
    public int Version { get; init; } = 1;

    public static RecordingState? From(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var s = new RecordingState { Count = e.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0, Hash = Str(e, "hash") ?? "",
            Version = e.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1 };
        Fill(e, "byType", s.ByType);
        Fill(e, "byLayer", s.ByLayer);
        if (e.TryGetProperty("layers", out var l) && l.ValueKind == JsonValueKind.Array)
            s.Layers.AddRange(l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        return s;
    }

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static void Fill(JsonElement e, string key, SortedDictionary<string, int> into)
    {
        if (!e.TryGetProperty(key, out var o) || o.ValueKind != JsonValueKind.Object) return;
        foreach (var p in o.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) into[p.Name] = p.Value.GetInt32();
    }

    /// <summary>"drawing: 2 LINE, 1 CIRCLE; layers: 0, DIM" ("drawing: empty; layers: 0").</summary>
    public string Summary()
    {
        var types = ByType.Count == 0 ? "empty" : string.Join(", ", ByType.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Value} {t.Key}"));
        return $"drawing: {types}; layers: {(Layers.Count == 0 ? "-" : string.Join(", ", Layers))}";
    }
}

/// <summary>A dialog field that changed while the dialog was open. Kind: edit | check | radio | combo | list | slider.</summary>
public sealed record RecordingDialogValue(string Label, string Kind, string Before, string After);

/// <summary>One thing the tester did: a ribbon click, a typed command, a dialog, a checkpoint note...</summary>
public sealed class RecordingStep
{
    /// <summary>ribbon | command | lisp | dialog | mark | doc</summary>
    public string Kind { get; init; } = "";
    public long T { get; init; }
    // ribbon
    public string? ItemId { get; set; }
    public string? Text { get; set; }
    public string? Tab { get; set; }
    /// <summary>The recorder's tab id ("ACAD.ID_TabHome"): tells apart tabs with the same title.</summary>
    public string? TabId { get; set; }
    public string? Panel { get; set; }
    public string? Macro { get; set; }
    public string? ItemType { get; set; }
    // command / lisp
    public string? Command { get; set; }
    public string? Lisp { get; set; }
    public string Outcome { get; set; } = "";       // "", cancelled, failed, unknown
    public List<string> Commands { get; } = new();  // every command this step started (ribbon macros can start several)
    public List<RecordingAnswer> Answers { get; } = new();
    // dialog
    public string? DialogTitle { get; set; }
    public string? WindowClass { get; set; }
    public bool OpenedByPrevious { get; set; }
    /// <summary>dialog: the command step that was running when it opened (DRAWGRID's options dialog). Pages show the dialog inside that step,
    /// between the answers given before and after it opened; it stays a step of its own here for replay and ingest.</summary>
    public RecordingStep? OpenedIn { get; set; }
    /// <summary>dialog: the fields the person changed (from the `dialog_values` event). Empty with <see cref="ValuesRead"/> null = not recorded.</summary>
    public List<RecordingDialogValue> Values { get; } = new();
    /// <summary>dialog: how many fields the recorder could read, null if the session has no `dialog_values` event for this dialog.</summary>
    public int? ValuesRead { get; set; }
    /// <summary>dialog: "ok" / "cancel" as the person closed it (the `dialog_values` event's `closedWith`); null = not recorded.</summary>
    public string? ClosedWith { get; set; }
    /// <summary>command / ribbon / lisp: dialogs opened while it ran (see <see cref="OpenedIn"/>).</summary>
    public List<RecordingStep> InnerDialogs { get; } = new();
    // mark / doc
    public string? Note { get; set; }
    /// <summary>mark: the drawing state at the checkpoint (null in sessions recorded before checkpoints carried state).</summary>
    public RecordingState? State { get; set; }
    // set by the joiner
    public JoinResult? Join { get; set; }
    public FormMatch? Form { get; set; }
    /// <summary>Screenshots for the ribbon click, command start, dialog or checkpoint.</summary>
    public List<RecordingShot> Shots { get; } = new();
    /// <summary>Screenshots for the end (or cancel) of the command.</summary>
    public List<RecordingShot> EndShots { get; } = new();
}

public sealed class RecordingSession
{
    public string Name { get; init; } = "";
    public string File { get; init; } = "";
    public Dictionary<string, JsonElement> Header { get; } = new();
    public List<RecordingEvent> Events { get; } = new();
    public List<RecordingStep> Steps { get; } = new();
    public long DurationMs => Events.Count == 0 ? 0 : Events[^1].T;
    public int BadLines { get; set; }
    /// <summary>Drawing state right after RETRACERECORD (the first `state` event), if recorded.</summary>
    public RecordingState? InitialState { get; set; }
    public string Slug { get; set; } = "";

    public string? HeaderStr(string k) => Header.TryGetValue(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
}

public static class RecordingReader
{
    static bool IsOwnRibbon(string? id, string? tabId) =>
        OwnCommands.IsOwnTab(tabId) || OwnCommands.IsOwnCommand(id);

    static readonly System.Text.RegularExpressions.Regex TypeName = Rx.Create(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Null for no macro, including a .NET type name that older recorders wrote for buttons that run code (Samples tab).</summary>
    static string? CleanMacro(string? macro) => string.IsNullOrWhiteSpace(macro) || TypeName.IsMatch(macro.Trim()) ? null : macro;

    /// <summary>The recordings for the given paths: each is a recording.jsonl, a test folder, or a tests folder (searched one level down for `*\recording.jsonl`).</summary>
    public static List<string> Expand(IEnumerable<string> inputs)
    {
        var files = new List<string>();
        foreach (var i in inputs)
        {
            if (Directory.Exists(i)) files.AddRange(TestFolders.FindAll(i));
            else if (File.Exists(i))
            {
                if (!TestFolders.IsRecording(i)) throw new FileNotFoundException($"not a recording (expected {TestFolders.RecordingFile} in a test folder): {i}");
                files.Add(i);
            }
            else throw new FileNotFoundException($"not a recording or folder: {i}");
        }
        return files.Distinct().ToList();
    }

    public static RecordingSession Load(string path)
    {
        // a recording is named after its test folder ("2026-10-10_0243_dtest")
        var name = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "").Name;
        var s = new RecordingSession { Name = name, File = path };
        // a recording being written is open for writing (FileShare.Read in SessionWriter): open with ReadWrite so reading it does not fail
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var root = JsonDocument.Parse(line).RootElement.Clone();
                if (root.ValueKind != JsonValueKind.Object) { s.BadLines++; continue; }
                var type = root.TryGetProperty("type", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString() ?? "" : "";
                if (type == "header")
                {
                    foreach (var p in root.EnumerateObject()) s.Header[p.Name] = p.Value.Clone();
                    continue;
                }
                s.Events.Add(new RecordingEvent
                {
                    T = root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0,
                    N = root.TryGetProperty("n", out var nv) && nv.ValueKind == JsonValueKind.Number && nv.TryGetInt64(out var nl) ? nl : null,
                    Type = type,
                    Doc = root.TryGetProperty("doc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                    Raw = root,
                });
            }
            catch (JsonException) { s.BadLines++; } // a crash can leave a torn last line
        }
        BuildSteps(s);
        return s;
    }

    /// <summary>Window within which a command that starts after a ribbon click is attributed to that click.</summary>
    public const long RibbonAttributionMs = 2000;

    /// <summary>AutoCAD's security prompt ("Security - Unsigned Executable File" and the like, or "File Loading - Security Concern"): a person's
    /// decision to load an untrusted dll or LISP file, not a step of the test. Same rule as the plugin's SecurityPrompt.IsTitle.</summary>
    public static bool IsSecurityPrompt(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var t = title.Trim();
        return t.StartsWith("Security - ", StringComparison.OrdinalIgnoreCase) || t.Equals("File Loading - Security Concern", StringComparison.OrdinalIgnoreCase)
            || t.EndsWith("Not in Trusted Folder", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes the current dialog step if it is a security prompt (from the steps and from the command that opened it); true if it did.</summary>
    static bool DropSecurityPrompt(RecordingSession s, ref RecordingStep? modal, Dictionary<long, RecordingStep> modalByN)
    {
        // a standard task dialog only (#32770; no class recorded: the title decides), so a plugin dialog titled "Security - Settings" stays a step
        if (modal == null || !IsSecurityPrompt(modal.DialogTitle) || !(string.IsNullOrEmpty(modal.WindowClass) || modal.WindowClass == "#32770")) return false;
        s.Steps.Remove(modal);
        var dropped = modal;
        foreach (var k in modalByN.Where(kv => kv.Value == dropped).Select(kv => kv.Key).ToList()) modalByN.Remove(k);
        modal.OpenedIn?.InnerDialogs.Remove(modal);
        modal = null;
        return true;
    }

    public static void BuildSteps(RecordingSession s)
    {
        RecordingStep? cur = null;
        RecordingStep? lastRibbon = null;
        bool ribbonConsumed = true;
        int depth = 0;
        bool inLisp = false;
        RecordingStep? modal = null;
        var modalByN = new Dictionary<long, RecordingStep>();   // event n of modal_enter / modal_title -> its dialog step
        string? firstDoc = null, lastDoc = null;
        var targets = new Dictionary<long, List<RecordingShot>>();
        foreach (var e in s.Events)
        {
            RecordingAnswer? added = null;
            switch (e.Type)
            {
                case "ribbon":
                    if (IsOwnRibbon(e.Str("id"), e.Str("tabId"))) break;   // a click on Retrace' own Stop / Checkpoint is not a step
                    cur = lastRibbon = new RecordingStep
                    {
                        Kind = "ribbon", T = e.T, ItemId = e.Str("id"), Text = e.Str("text") ?? e.Str("name"), Tab = e.Str("tab"), TabId = e.Str("tabId"), Panel = e.Str("panel"),
                        Macro = CleanMacro(e.Str("macro")), ItemType = e.Str("itemType"),
                    };
                    ribbonConsumed = false;
                    s.Steps.Add(cur);
                    break;
                case "cmd_start":
                {
                    var cmd = e.Str("command") ?? "?";
                    if (depth++ > 0) { cur?.Commands.Add(cmd); break; } // nested / transparent command: belongs to the running step
                    // AutoCAD Electrical reports some commands (UNITS) as unknown a moment before it runs them: drop that step
                    if (s.Steps.Count > 0 && s.Steps[^1] is { Kind: "command", Outcome: "unknown" } unk && e.T - unk.T <= 1000
                        && string.Equals(unk.Command, cmd, StringComparison.OrdinalIgnoreCase)) s.Steps.RemoveAt(s.Steps.Count - 1);
                    // origin: trust the recorder if it wrote one, else derive it (ribbon click <= 2 s before and macro mentions the command)
                    var origin = e.Str("origin") ?? (!ribbonConsumed && lastRibbon != null && e.T - lastRibbon.T <= RibbonAttributionMs && MacroMentions(lastRibbon.Macro, cmd) ? "ribbon" : "typed/other");
                    // built-in buttons carry no macro and older recorders then wrote "typed/other": a command within 1 s is the click's
                    if (origin != "ribbon" && !ribbonConsumed && lastRibbon is { Macro: null } && e.T - lastRibbon.T <= 1000) origin = "ribbon";
                    if (origin == "ribbon" && lastRibbon != null)
                    {
                        ribbonConsumed = true;
                        lastRibbon.Command ??= cmd;
                        lastRibbon.Commands.Add(cmd);
                        cur = lastRibbon;
                    }
                    else
                    {
                        cur = new RecordingStep { Kind = "command", T = e.T, Command = cmd };
                        cur.Commands.Add(cmd);
                        s.Steps.Add(cur);
                    }
                    break;
                }
                case "cmd_end": if (depth > 0) depth--; break;
                case "cmd_cancel": if (depth > 0) depth--; if (cur != null) cur.Outcome = "cancelled"; break;
                case "cmd_fail": if (depth > 0) depth--; if (cur != null) cur.Outcome = "failed"; break;
                case "cmd_unknown":
                    cur = new RecordingStep { Kind = "command", T = e.T, Command = e.Str("command"), Outcome = "unknown" };
                    s.Steps.Add(cur);
                    break;
                case "lisp_end" or "lisp_cancel": inLisp = false; break;
                case "lisp_start":
                    inLisp = true;
                    cur = new RecordingStep { Kind = "lisp", T = e.T, Lisp = e.Str("firstLine") };
                    s.Steps.Add(cur);
                    break;
                case "prompt":
                {
                    // Outside any command or LISP: a prompt of the recorder's own commands (older sessions recorded the checkpoint note).
                    if (cur == null || (depth == 0 && !inLisp)) break;
                    // Free input at a prompt that also takes keywords (CIRCLE's "radius or [Diameter]" given 10) comes back as a
                    // Keyword result; AutoCAD then re-asks the same message as a different kind. One typed answer covers both.
                    var prev = cur.Answers.Count > 0 ? cur.Answers[^1] : null;
                    if (prev is { Status: "Keyword" } && prev.Message == e.Str("message") && prev.Kind != e.Str("kind")) break;
                    cur.Answers.Add(added = new RecordingAnswer
                    {
                        Kind = e.Str("kind") ?? "?", Status = e.Str("status") ?? "?", Message = e.Str("message"), Keyword = e.Str("keyword"),
                        Options = e.Raw.TryGetProperty("keywords", out var kws) && kws.ValueKind == JsonValueKind.Array
                            ? kws.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new(),
                        Value = e.Raw.TryGetProperty("value", out var v) ? v : null,
                        Input = e.Raw.TryGetProperty("input", out var inp) && inp.ValueKind is JsonValueKind.True or JsonValueKind.False ? inp.GetBoolean() : null,
                        Seq = e.Raw.TryGetProperty("seq", out var sq) && sq.ValueKind == JsonValueKind.Number ? sq.GetInt64() : null,
                        By = e.Str("by"),
                        T = e.T,
                    });
                    break;
                }
                case "modal_enter":
                    modal = new RecordingStep { Kind = "dialog", T = e.T, DialogTitle = e.Str("title"), WindowClass = e.Str("windowClass"), OpenedByPrevious = cur != null };
                    s.Steps.Add(modal);
                    if (e.N is { } enterN) modalByN[enterN] = modal;
                    if (cur != null && (depth > 0 || inLisp)) { modal.OpenedIn = cur; cur.InnerDialogs.Add(modal); }
                    DropSecurityPrompt(s, ref modal, modalByN);
                    break;
                case "modal_title":
                    if (modal != null && !string.IsNullOrWhiteSpace(e.Str("title"))) { modal.DialogTitle = e.Str("title"); modal.WindowClass = e.Str("windowClass") ?? modal.WindowClass; }
                    if (modal != null && e.N is { } titleN) modalByN[titleN] = modal;
                    DropSecurityPrompt(s, ref modal, modalByN);
                    break;
                case "dialog_values":
                    if (e.Raw.TryGetProperty("for", out var forV) && forV.ValueKind == JsonValueKind.Number && forV.TryGetInt64(out var forN) && modalByN.TryGetValue(forN, out var vd))
                    {
                        vd.ClosedWith = e.Str("closedWith");
                        vd.ValuesRead = e.Raw.TryGetProperty("read", out var rd) && rd.ValueKind == JsonValueKind.Number && rd.TryGetInt32(out var rn) ? rn : 0;
                        vd.Values.Clear();
                        if (e.Raw.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
                            foreach (var f in fs.EnumerateArray())
                            {
                                string S(string k) => f.ValueKind == JsonValueKind.Object && f.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                                vd.Values.Add(new RecordingDialogValue(S("label"), S("kind"), S("before"), S("after")));
                            }
                    }
                    break;
                case "mark":
                    s.Steps.Add(new RecordingStep { Kind = "mark", T = e.T, Note = e.Str("note"), State = e.Raw.TryGetProperty("state", out var ms) ? RecordingState.From(ms) : null });
                    break;
                case "state": // the drawing as it was when recording started; not a step
                    if (s.InitialState == null && e.Raw.TryGetProperty("state", out var st0)) s.InitialState = RecordingState.From(st0);
                    break;
                case "doc_created":
                    s.Steps.Add(new RecordingStep { Kind = "doc", T = e.T, Note = $"New/opened drawing {e.Doc}" });
                    lastDoc = e.Doc;
                    break;
                case "doc_active":
                    firstDoc ??= e.Doc;
                    if (lastDoc != null && e.Doc != lastDoc && s.Steps.Count > 0) s.Steps.Add(new RecordingStep { Kind = "doc", T = e.T, Note = $"Switch to drawing {e.Doc}" });
                    lastDoc = e.Doc;
                    break;
            }
            // which list a later `shot` event for this event number goes to
            var tgt = e.Type switch
            {
                "ribbon" when IsOwnRibbon(e.Str("id"), e.Str("tabId")) => null,   // its picture is not a step's: it ends up in unused
                "ribbon" or "cmd_start" or "cmd_unknown" or "lisp_start" => cur?.Shots,
                "cmd_end" or "cmd_cancel" or "cmd_fail" or "lisp_end" or "lisp_cancel" => cur?.EndShots,
                "prompt" => added?.Shots,
                "modal_enter" or "modal_title" => modal?.Shots,
                "mark" => s.Steps.Count > 0 ? s.Steps[^1].Shots : null,
                _ => null,
            };
            if (e.N is { } num && tgt != null) targets[num] = tgt;
        }
        var dir = Path.GetDirectoryName(s.File) ?? "";
        foreach (var e in s.Events.Where(x => x.Type == "shot"))
        {
            var file = e.Str("file");
            if (string.IsNullOrWhiteSpace(file)) continue;
            if (!e.Raw.TryGetProperty("for", out var fv) || fv.ValueKind != JsonValueKind.Number || !fv.TryGetInt64(out var target)) continue;
            if (!targets.TryGetValue(target, out var list)) continue;
            var norm = file.Replace('\\', '/');
            list.Add(new RecordingShot(norm, e.Str("what") ?? "window", Path.Combine(dir, norm.Replace('/', Path.DirectorySeparatorChar))));
        }
        // In AutoCAD Electrical something requests a point on every mouse move while a command waits; the recorder flags
        // results that a click or key press led to. Where a step has such flags, the unflagged results are that noise.
        foreach (var st in s.Steps)
            if (st.Answers.Any(a => a.Input == true)) st.Answers.RemoveAll(a => a.Input == false);
        // A prompt nested inside the next one (GetCorner's inner point prompt) started later but ended first: higher seq. Drop it.
        foreach (var st in s.Steps)
            for (int i = st.Answers.Count - 2; i >= 0; i--)
                if (st.Answers[i].Seq is { } a && st.Answers[i + 1].Seq is { } b && a > b) st.Answers.RemoveAt(i);
    }

    /// <summary>True if the menu macro ("^C^C_line", "^C^C_-layer;m;") contains the command.</summary>
    public static bool MacroMentions(string? macro, string command)
    {
        if (string.IsNullOrEmpty(macro) || string.IsNullOrEmpty(command)) return false;
        return MacroCommands(macro).Contains(Norm(command), StringComparer.OrdinalIgnoreCase);
    }

    static string Norm(string c) => c.TrimStart('_', '.', '-', '*', '\'');

    /// <summary>Command names in a macro: tokens split on ^C, ;, space, pause, parens.</summary>
    public static List<string> MacroCommands(string? macro)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(macro)) return list;
        var m = macro.Replace("^C", " ", StringComparison.OrdinalIgnoreCase).Replace(";", " ");
        foreach (var tok in m.Split(new[] { ' ', '\t', '\r', '\n', '(', ')', '"', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = Norm(tok);
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    /// <summary>The command a macro starts: its first token ("^C^C_line" -> LINE), or null.</summary>
    public static string? MacroCommand(string? macro) => MacroCommands(macro).FirstOrDefault()?.ToUpperInvariant();
}
