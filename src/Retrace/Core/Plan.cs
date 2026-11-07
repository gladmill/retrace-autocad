using System.Globalization;
using Retrace.Engine.Recordings;
using System.Text;
using System.Text.Json;

namespace Retrace.Core;

/// <param name="Input">recorder's flag: true = a click/key press led to it, false = mouse-move noise, null = not recorded</param>
/// <param name="T">time of the result (ms since the session started)</param>
public sealed record Answer(string Kind, string Status, JsonElement? Value, string? Keyword, string? Message, bool? Input = null, long? Seq = null, long T = 0);

public sealed class Step
{
    public string Kind = "";            // ribbon | command | lisp | dialog | palette | mark | tab | open
    public string Title = "";           // human readable one-liner
    public string? ItemId, Macro, Tab, Panel, Path;
    public string? Command;             // command started by this step (global name)
    public string? Lisp;
    public string? Drawing;             // open: the full path of the drawing to open; palette (native): the drawing its double-click opened
    public NativeAction? Native;        // palette: what was done in a native palette (tree item, toolbar button, drop-down); null = .NET palette
    public string? DialogTitle;         // dialog: its title; palette: the palette (its tab in the palette set; Panel = the set)
    public string? Button;              // palette: the button clicked, null when only values changed
    public int ButtonIndex = 1;         // palette: which of the palette's buttons with that text (1 = the first)
    /// <summary>palette: "click" or "dblclick" (a double-click on an item of a tree: <see cref="ClickTree"/> key, <see cref="ClickPath"/> item path, <see cref="ClickTreeLabel"/>; <see cref="Button"/> is the item's text).</summary>
    public string ClickKind = DialogClickKinds.Click;
    public string ClickTree = "";
    public string ClickPath = "";
    public string ClickTreeLabel = "";
    /// <summary>palette: the context menu choice that ends the step (a `context_menu` event); <see cref="Button"/> then holds the menu path, so a command it starts is linked to the step.</summary>
    public MenuChoice? Menu;
    public string? Note;
    public DrawingDigest? State;        // mark: the drawing state recorded at the checkpoint (null in older sessions)
    /// <summary>mark: what the checkpoint verifies beyond <see cref="State"/> (schema 3); null = none (older sessions, or checks not recorded). Replay: <see cref="BehaviorChecks.Compare"/>.</summary>
    public BehaviorChecks? Checks;
    /// <summary>mark: the `n` of the mark event; names the snapshot file <see cref="BehaviorSnapshot.MarkPath"/>. 0 = unknown.</summary>
    public int EventN;
    public long T;
    /// <summary>dialog: when it closed (the time of its `dialog_values`), -1 = not known. A nested dialog of the same click opens within a window after it.</summary>
    public long ClosedT = -1;
    public bool HadDialog;
    /// <summary>dialog / palette: the fields the person changed while it was open; null = the session has no `dialog_values` event for it.</summary>
    public List<FieldChange>? Values;
    /// <summary>dialog: how many fields the recorder could read.</summary>
    public int ValuesRead;
    /// <summary>dialog: "ok" / "cancel" as the recording closed it, "click" when its last click closed it (a Close button, the Yes of a message box); null = not recorded.</summary>
    public string? ClosedWith;
    /// <summary>dialog: "win32" or "uia" as the recorder read it; null = not recorded.</summary>
    public string? ValuesSource;
    /// <summary>dialog: the tab showing when it was closed (`closeTab` of its `dialog_values`, <see cref="TabPath"/>); null = it has no tabs, or the recording is from before tabs were read. The replay shows it before the closing press.</summary>
    public string? CloseTab;
    /// <summary>dialog: the push buttons (not OK / Cancel) the person clicked while it was open, in order. <see cref="Values"/> then holds only the
    /// changes made after the last click. Empty for a dialog the recorder saw no click in, and for every recording from before `dialog_click`.</summary>
    public List<DialogClick> Clicks = new();
    public List<Answer> Answers = new();
}

/// <summary>The kinds of `dialog_click`.</summary>
public static class DialogClickKinds
{
    public const string Click = "click", DoubleClick = "dblclick";
    /// <summary>The kind a `kind` property names: only "dblclick" is another one; a missing or unknown kind is a button click (every recording from before double-clicks).</summary>
    public static string Of(string? kind) => kind == DoubleClick ? DoubleClick : Click;

    /// <summary>The words for a click in a pause message or a Tdoc sentence: "press Add", "double-click Parts › Fasteners in Tree".</summary>
    public static string Phrase(string kind, string button, string path, string treeLabel) =>
        kind == DoubleClick ? DoubleClickWords(path, treeLabel, p => p, t => t, capital: false) : "press " + button;

    static string DoubleClickRest(string path, string treeLabel, Func<string, string> pathText, Func<string, string> tree) =>
        " " + pathText(path) + (treeLabel.Length > 0 ? " in " + tree(treeLabel) : "");

    /// <summary>"Double-click `A › B` in **Items**" with the caller's markup (steps.md, Tdoc: plain).</summary>
    public static string DoubleClickWords(string path, string treeLabel, Func<string, string> pathText, Func<string, string> tree, bool capital = true) =>
        (capital ? "Double-click" : "double-click") + DoubleClickRest(path, treeLabel, pathText, tree);
}

/// <summary>A push button clicked in a dialog (a `dialog_click` event): the values changed since the dialog opened or since the click before,
/// and the dialogs the click opened (a nested dialog is attached to the click that opened it; it is not a step of its own).</summary>
public sealed class DialogClick
{
    public string Button = "";
    /// <summary>"click" (a push button) or "dblclick" (a double-click on a tree item: <see cref="Tree"/> and <see cref="Path"/> say which; <see cref="Button"/> is the item's own text).</summary>
    public string Kind = DialogClickKinds.Click;
    /// <summary>The button acts as the mouse goes down: the recorder wrote the click before the button came up (its sub form opened first). The replay then presses it with the mouse (`onDown` of the event).</summary>
    public bool OnDown;
    /// <summary>dblclick: the key of the tree field (its `tree`), and the item's path (`path`, <see cref="TreePath"/>).</summary>
    public string Tree = "", Path = "";
    /// <summary>dblclick: the label of the tree (`treeLabel`), for the wording.</summary>
    public string TreeLabel = "";
    /// <summary>The control id the recorder saw (WinForms ids change between runs); information only.</summary>
    public int Id;
    /// <summary>Which of the dialog's enabled buttons with that text it is (1 = the first).</summary>
    public int Index = 1;
    public long T;
    /// <summary>The tab the click was made on (`tab` of its `dialog_click`, <see cref="TabPath"/>): the page its button sits on, or the one showing for a button outside the tabs; "" = none or not recorded.
    /// The replay shows it before the button is pressed.</summary>
    public string Tab = "";
    public List<FieldChange> Values = new();
    public int Read;
    /// <summary>A context menu choice instead of a button (a `context_menu` event; <see cref="Button"/> is then the menu path): the replay right-clicks the target and picks the item.</summary>
    public MenuChoice? Menu;
    /// <summary>Nested dialogs this click opened, in the order they opened; each is a <see cref="Step"/> of kind "dialog" with its own values and clicks.</summary>
    public List<Step> Nested = new();
}

/// <summary>Reads a session file and groups its events into replayable steps. No AutoCAD dependency, so it is easy to test.</summary>
public static class Plan
{
    /// <summary>The Retrace ribbon tab: switching to it (to stop or mark) is not part of a test.</summary>
    public static bool IsOwnTab(string? tabId) => OwnCommands.IsOwnTab(tabId);

    /// <summary>A button of the Retrace tab (its ids are the plugin command names).</summary>
    public static bool IsOwnRibbon(string? id, string? tabId) => IsOwnTab(tabId) || OwnCommands.IsOwnCommand(id);

    static readonly System.Text.RegularExpressions.Regex TypeName = Rx.Create(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>A recorded macro, or null when there is none. Older recorders wrote the CommandParameter's ToString for buttons that run
    /// code (the Samples tab), i.e. a .NET type name like "Autodesk.Windows.RibbonButton": that is not a macro.</summary>
    public static string? CleanMacro(string? macro) => string.IsNullOrWhiteSpace(macro) || TypeName.IsMatch(macro.Trim()) ? null : macro;

    public static List<JsonElement> ReadEvents(string path)
    {
        var list = new List<JsonElement>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { list.Add(JsonDocument.Parse(line).RootElement.Clone()); } catch (JsonException) { /* torn last line after a crash */ }
        }
        return list;
    }

    public static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool? Bool(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>Where a step has results flagged as real input, the unflagged ones are mouse-move noise (see Recorder).
    /// Steps without flags (scripted input, older recordings) are left alone.</summary>
    /// <summary>Older recordings also logged prompts nested inside another one (GetCorner runs a point prompt inside the corner
    /// prompt). A nested prompt started later than its outer one but ended first, so its seq is higher than the next answer's.</summary>
    public static void DropNested(IEnumerable<List<Answer>> answerLists)
    {
        foreach (var l in answerLists)
            for (int i = l.Count - 2; i >= 0; i--)
                if (l[i].Seq is { } a && l[i + 1].Seq is { } b && a > b) l.RemoveAt(i);
    }

    public static void DropInputNoise(IEnumerable<List<Answer>> answerLists)
    {
        foreach (var l in answerLists)
            if (l.Any(a => a.Input == true)) l.RemoveAll(a => a.Input == false);
    }

    /// <summary>The drawing state recorded right after the header (the first `state` event), or null (older sessions).</summary>
    public static DrawingDigest? InitialState(IEnumerable<JsonElement> events)
    {
        foreach (var e in events)
            if (Str(e, "type") == "state" && e.TryGetProperty("state", out var st)) return DrawingDigest.FromJson(st);
        return null;
    }

    /// <summary>The event number `n` as a positive int; 0 when missing, not an integer or out of range. Never throws.</summary>
    static int EventNumber(JsonElement e) => e.TryGetProperty("n", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i > 0 ? i : 0;

    public static long Num(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    public static List<Step> Build(IEnumerable<JsonElement> events)
    {
        var steps = new List<Step>();
        Step? cur = null;
        int depth = 0;
        bool inLisp = false;
        string? lastModalTitle = null;
        Step? modalStep = null;
        Step? palette = null;   // the last palette step: a click right after its values joins it
        var nativeDialogs = new HashSet<Step>();   // dialog steps opened right after a native palette action (NativeAction.ModalMs)
        // per dialog: the command that was running at its modal_enter and that command's HadDialog before it (restored if the dialog is dropped: a security prompt, a window that closed by itself). Kept per
        // dialog, not per latest modal_enter: a nested dialog's modal_enter must not overwrite what the dialog it was opened from will restore.
        var modalInfo = new Dictionary<Step, (Step? Owner, bool HadBefore)>();
        var modalByN = new Dictionary<long, Step>();   // event n of modal_enter / modal_title -> its dialog step
        var nativeByN = new Dictionary<long, Step>();  // event n of native_select -> its step (a native_value names the entry later)
        var list = events as IReadOnlyCollection<JsonElement> ?? events.ToList();
        // `view_stale` events: the recorder found that the view of a pick or click had changed since it was read; the answer of that event keeps no `view` (handles)
        var staleViews = new HashSet<long>();
        foreach (var x in list)
            if (Str(x, "type") == "view_stale" && x.TryGetProperty("for", out var sf) && sf.ValueKind == JsonValueKind.Number && sf.TryGetInt64(out var sn)) staleViews.Add(sn);
        var startup = DrawingOpen.StartupEvents(list);   // LISP AutoCAD ran by itself while a drawing opened: not steps
        foreach (var e in list)
        {
            var type = Str(e, "type");
            var t = Num(e, "t");
            if (startup.Contains(Num(e, "n"))) continue;
            switch (type)
            {
                case "doc_created":
                {
                    // a drawing opened while no command ran: from a palette (AutoCAD Electrical's Project Manager), Explorer or a
                    // plugin. A replay opens the same file. Opened by a command (OPEN, NEW), the command step replays it.
                    if (depth > 0 || inLisp || DrawingOpen.PathOf(e) is not { } path) break;
                    // opened by double-clicking it in a native palette (the Project Manager): the palette step opens it
                    if (palette is { Native.Action: "dblclick", Drawing: null } && steps.Count > 0 && ReferenceEquals(steps[^1], palette) && t - palette.T <= DrawingOpen.StartupMs)
                    {
                        palette.Drawing = path;
                        palette.Title += $" (opens {System.IO.Path.GetFileName(path.Replace('\\', '/'))})";
                        break;
                    }
                    cur = null;
                    steps.Add(new Step { Kind = "open", T = t, Drawing = path, Title = $"Open drawing {System.IO.Path.GetFileName(path.Replace('\\', '/'))}" });
                    break;
                }
                case "tab":
                    if (IsOwnTab(Str(e, "id"))) break;
                    steps.Add(new Step { Kind = "tab", T = t, Tab = Str(e, "title"), Title = $"Activate ribbon tab \"{Str(e, "title") ?? Str(e, "id")}\"" });
                    break;
                case "ribbon":
                {
                    if (IsOwnRibbon(Str(e, "id"), Str(e, "tabId"))) break;   // older recordings: a click on Retrace' own Stop / Checkpoint
                    var text = Str(e, "text") ?? Str(e, "id") ?? "?";
                    cur = new Step
                    {
                        Kind = "ribbon", T = t, ItemId = Str(e, "id"), Macro = CleanMacro(Str(e, "macro")), Tab = Str(e, "tab"), Panel = Str(e, "panel"), Path = Str(e, "path"),
                        Title = "Click " + string.Join(" > ", new[] { Str(e, "tab"), Str(e, "panel"), text }.Where(s => !string.IsNullOrEmpty(s))),
                    };
                    steps.Add(cur);
                    break;
                }
                case "cmd_start":
                {
                    var cmd = Str(e, "command") ?? "?";
                    if (depth++ > 0) break; // nested / transparent: part of the running step
                    // a palette button that runs a command (SendStringToExecute): the command and its answers belong to the palette step
                    if (Str(e, "origin") == "palette" && palette is { Command: null, Button: not null } && steps.Count > 0 && ReferenceEquals(steps[^1], palette))
                    {
                        palette.Command = cmd;
                        cur = palette;
                        break;
                    }
                    // older recorders tagged commands of macro-less (built-in) buttons "typed/other": a command within 1 s is the click's
                    if (cur is { Kind: "ribbon", Command: null } && (Str(e, "origin") == "ribbon" || (cur.Macro == null && t - cur.T <= 1000))) { cur.Command = cmd; break; }
                    cur = new Step { Kind = "command", T = t, Command = cmd, Title = $"Type command {cmd}" };
                    steps.Add(cur);
                    break;
                }
                case "cmd_end" or "cmd_cancel" or "cmd_fail":
                    if (depth > 0) depth--;
                    break;
                case "lisp_end" or "lisp_cancel":
                    inLisp = false;
                    break;
                case "lisp_start":
                    inLisp = true;
                    cur = new Step { Kind = "lisp", T = t, Lisp = Str(e, "firstLine"), Title = $"Run LISP {Str(e, "firstLine")}" };
                    steps.Add(cur);
                    break;
                case "prompt":
                {
                    // Outside any command or LISP: a prompt of our own commands (older sessions recorded the checkpoint note).
                    if (cur == null || (depth == 0 && !inLisp)) break;
                    // Free input at a prompt that also takes keywords (CIRCLE's "radius or [Diameter]" given 10) comes back as a
                    // Keyword result; AutoCAD then re-asks the same message as a different kind. The typed text already answers it.
                    var prev = cur.Answers.Count > 0 ? cur.Answers[^1] : null;
                    if (prev is { Status: "Keyword" } && prev.Message == Str(e, "message") && prev.Kind != Str(e, "kind")) break;
                    cur.Answers.Add(new Answer(Str(e, "kind") ?? "?", Str(e, "status") ?? "?", e.TryGetProperty("value", out var v) ? (staleViews.Contains(Num(e, "n")) ? ViewSpec.WithoutView(v) : v.Clone()) : null, Str(e, "keyword"), Str(e, "message"), Bool(e, "input"), e.TryGetProperty("seq", out var sq) && sq.ValueKind == JsonValueKind.Number ? sq.GetInt64() : null, t));
                    break;
                }
                case "modal_enter":
                case "modal_title":
                    // a window a native palette action opened: dropped at its dialog_values if it closed by itself (NativeAction.ModalMs)
                    bool afterNative = type == "modal_enter" && palette is { Native: not null } && steps.Count > 0 && ReferenceEquals(steps[^1], palette) && t - palette.T <= NativeAction.ModalMs;
                    if (type == "modal_enter")
                    {
                        modalStep = new Step { Kind = "dialog", T = t, DialogTitle = Str(e, "title"), Title = "" };
                        steps.Add(modalStep);
                        modalInfo[modalStep] = (cur, cur?.HadDialog ?? false);
                        if (cur != null) cur.HadDialog = true;
                        if (afterNative) nativeDialogs.Add(modalStep);
                    }
                    else if (modalStep != null && !string.IsNullOrEmpty(Str(e, "title"))) modalStep.DialogTitle = Str(e, "title");
                    // AutoCAD's security prompt (loading an untrusted dll or LISP file) is a person's decision, not a step of the test: drop it.
                    // A replay that meets it stops with what to do (Replayer); a file that is trusted by now never shows it again.
                    if (modalStep != null && SecurityPrompt.IsRecorded(modalStep.DialogTitle, Str(e, "windowClass")))
                    {
                        steps.Remove(modalStep);
                        foreach (var k in modalByN.Where(kv => kv.Value == modalStep).Select(kv => kv.Key).ToList()) modalByN.Remove(k);
                        if (modalInfo.Remove(modalStep, out var dropped) && dropped.Owner != null) dropped.Owner.HadDialog = dropped.HadBefore;
                        modalStep = null; lastModalTitle = null;
                        break;
                    }
                    lastModalTitle = modalStep?.DialogTitle;
                    if (modalStep != null) modalStep.Title = $"Dialog opens: \"{lastModalTitle}\" - fill it in by hand";
                    if (modalStep != null && e.TryGetProperty("n", out var nv) && nv.ValueKind == JsonValueKind.Number && nv.TryGetInt64(out var nn)) modalByN[nn] = modalStep;
                    break;
                case "dialog_values":
                    if (e.TryGetProperty("for", out var fv) && fv.ValueKind == JsonValueKind.Number && fv.TryGetInt64(out var fn) && modalByN.TryGetValue(fn, out var ds))
                    {
                        // opened by a native palette action and closed by itself ("Updating..."): not a step
                        if (nativeDialogs.Contains(ds) && NativeAction.ClosedByItself(e))
                        {
                            steps.Remove(ds);
                            if (ReferenceEquals(ds, modalStep) && modalInfo.Remove(ds, out var closed) && closed.Owner != null) closed.Owner.HadDialog = closed.HadBefore;
                            break;
                        }
                        ds.ClosedWith = Str(e, "closedWith");
                        ds.ClosedT = t;
                        ds.ValuesSource = Str(e, "source");
                        ds.CloseTab = Str(e, "closeTab") is { Length: > 0 } closeTab ? closeTab : null;
                        ds.ValuesRead = e.TryGetProperty("read", out var rv) && rv.ValueKind == JsonValueKind.Number && rv.TryGetInt32(out var ri) ? ri : 0;
                        ds.Values = DialogNesting.FieldsOfEvent(e);
                        // a dialog that opened inside another one (`parent`) belongs to the click that opened it: not a step of its own
                        if (Num(e, "parent") is > 0 and var parentN && modalByN.TryGetValue(parentN, out var outer) && !ReferenceEquals(outer, ds) && outer.Clicks.Count > 0
                            && DialogNesting.Attach(outer.Clicks[^1].Nested, outer.Clicks[^1].T, ds, x => x.T, x => x.ClosedT))
                        {
                            steps.Remove(ds);
                            if (ReferenceEquals(modalStep, ds)) modalStep = outer;
                        }
                    }
                    break;
                case "modal_alias":
                    // an EnterModal that came late for a dialog tracked already (found by its window): its step is that dialog's, not a second one
                    if (DialogNesting.Alias(modalByN, Num(e, "for"), Num(e, "of")) is { } duplicate)
                    {
                        steps.Remove(duplicate);
                        if (ReferenceEquals(modalStep, duplicate)) modalStep = modalByN[Num(e, "of")];
                    }
                    break;
                case "dialog_click":
                    if (modalByN.TryGetValue(Num(e, "for"), out var clicked))
                        clicked.Clicks.Add(new DialogClick
                        {
                            Button = Str(e, "button") ?? "", Kind = DialogClickKinds.Of(Str(e, "kind")), OnDown = e.TryGetProperty("onDown", out var od) && od.ValueKind == JsonValueKind.True, Tree = Str(e, "tree") ?? "", Path = Str(e, "path") ?? "", TreeLabel = Str(e, "treeLabel") ?? "", Id = (int)Num(e, "id"), Index = Num(e, "index") is > 0 and var bi0 ? (int)bi0 : 1, T = t, Tab = Str(e, "tab") ?? "",
                            Values = DialogNesting.FieldsOfEvent(e), Read = (int)Num(e, "read"),
                        });
                    break;
                case "context_menu":
                {
                    if (MenuChoice.From(e) is not { } menu) break;
                    var fieldsOfMenu = DialogNesting.FieldsOfEvent(e);
                    if (Num(e, "for") > 0)
                    {
                        // in a dialog: a click like a button's, with the menu in place of the button
                        if (modalByN.TryGetValue(Num(e, "for"), out var menuDialog))
                            menuDialog.Clicks.Add(new DialogClick { Button = menu.ButtonText, Menu = menu, Id = menu.Id, T = t, Tab = Str(e, "tab") ?? "", Values = fieldsOfMenu, Read = (int)Num(e, "read") });
                        break;
                    }
                    string? menuPalette = Str(e, "palette"), menuSet = Str(e, "set");
                    bool joinMenu = palette != null && steps.Count > 0 && ReferenceEquals(steps[^1], palette) && palette.Native == null && palette.Button == null && palette.DialogTitle == menuPalette && palette.Panel == menuSet;
                    if (!joinMenu) { palette = new Step { Kind = "palette", T = t, DialogTitle = menuPalette, Panel = menuSet }; steps.Add(palette); }
                    palette!.Menu = menu; palette.Button = menu.ButtonText;
                    palette.ValuesSource ??= Str(e, "source");
                    if (fieldsOfMenu.Count > 0) { palette.Values ??= new List<FieldChange>(); palette.Values.AddRange(fieldsOfMenu); }
                    var menuChanges = palette.Values?.Count ?? 0;
                    palette.Title = $"Palette \"{menuPalette}\": " + (menuChanges > 0 ? $"{menuChanges} value(s), then " : "") + menu.Phrase(place: "the palette");
                    break;
                }
                case "native_click":
                case "native_select":
                {
                    if (NativeAction.From(e) is not { } na) break;
                    string? name = Str(e, "palette"), set = Str(e, "set");
                    var title = $"Palette \"{name}\": {na.Describe()}";
                    // a double-click arrives as a click, then the double-click, on the same item: one step
                    if (na.Action == "dblclick" && palette is { Native: { Control: "tree", Action: "click" } prev } && steps.Count > 0 && ReferenceEquals(steps[^1], palette)
                        && prev.Path.SequenceEqual(na.Path) && palette.DialogTitle == name && t - palette.T <= 2000)
                    {
                        palette.Native = na; palette.Title = title; palette.Button = na.Target;
                        break;
                    }
                    // Button: what a command the click starts is linked to (origin "palette", below)
                    palette = new Step { Kind = "palette", T = t, DialogTitle = name, Panel = set, Native = na, Button = na.Target, Title = title };
                    steps.Add(palette);
                    nativeByN[Num(e, "n")] = palette;
                    break;
                }
                case "native_value":
                    if (nativeByN.TryGetValue(Num(e, "for"), out var ns) && ns.Native != null && Str(e, "value") is { } nativeValue)
                    {
                        ns.Native = ns.Native with { Value = nativeValue };
                        ns.Button = ns.Native.Target;
                        ns.Title = $"Palette \"{ns.DialogTitle}\": {ns.Native.Describe()}";
                    }
                    break;
                case "palette_values":
                case "palette_click":
                {
                    string? name = Str(e, "palette"), set = Str(e, "set");
                    bool join = type == "palette_click" && palette != null && steps.Count > 0 && ReferenceEquals(steps[^1], palette)
                                && palette.Button == null && palette.DialogTitle == name && palette.Panel == set;
                    if (!join) { palette = new Step { Kind = "palette", T = t, DialogTitle = name, Panel = set }; steps.Add(palette); }
                    if (type == "palette_click")
                    {
                        palette!.Button = Str(e, "button");
                        palette.ClickKind = DialogClickKinds.Of(Str(e, "kind")); palette.ClickTree = Str(e, "tree") ?? ""; palette.ClickPath = Str(e, "path") ?? ""; palette.ClickTreeLabel = Str(e, "treeLabel") ?? "";
                        palette.ButtonIndex = e.TryGetProperty("index", out var bi) && bi.ValueKind == JsonValueKind.Number && bi.TryGetInt32(out var bn) ? bn : 1;
                    }
                    else
                    {
                        palette!.ValuesSource = Str(e, "source");
                        palette.ValuesRead = e.TryGetProperty("read", out var prv) && prv.ValueKind == JsonValueKind.Number && prv.TryGetInt32(out var pri) ? pri : 0;
                        palette.Values ??= new List<FieldChange>();
                        if (e.TryGetProperty("fields", out var pfa) && pfa.ValueKind == JsonValueKind.Array)
                            foreach (var f in pfa.EnumerateArray())
                                palette.Values.Add(new FieldChange(Str(f, "key") ?? "", Str(f, "label") ?? "", Str(f, "kind") ?? "", Str(f, "before") ?? "", Str(f, "after") ?? "", Str(f, "tab") ?? ""));
                    }
                    var changes = palette.Values?.Count ?? 0;
                    palette.Title = $"Palette \"{name}\": " + (changes > 0 ? $"{changes} value(s)" : "") + (changes > 0 && palette.Button != null ? ", then " : "")
                                    + (palette.Button != null ? (palette.ClickKind == DialogClickKinds.DoubleClick ? DialogClickKinds.Phrase(palette.ClickKind, palette.Button, palette.ClickPath, palette.ClickTreeLabel) : $"click {palette.Button}") : changes == 0 ? "no changes" : "");
                    break;
                }
                case "mark":
                {
                    var note = Str(e, "note") ?? "";
                    var state = e.TryGetProperty("state", out var st) ? DrawingDigest.FromJson(st) : null;
                    var checks = e.TryGetProperty("checks", out var ck) ? BehaviorChecks.FromJson(ck) : null;
                    steps.Add(new Step { Kind = "mark", T = t, Note = note, State = state, Checks = checks, EventN = EventNumber(e), Title = note.Length == 0 ? "Checkpoint" : "Check: " + note });
                    break;
                }
            }
        }
        DropInputNoise(steps.Select(s => s.Answers)); // first: in mouse recordings the inner prompt may hold the click flag
        DropNested(steps.Select(s => s.Answers));
        return steps;
    }

    // ---- turning recorded answers into command-line input ------------------------------------

    static string Inv(double d) => d.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>One answer as the text to type, or null if it cannot be typed. Each returned line is followed by Enter.</summary>
    /// <param name="handleExists">optional check used only for the handle fallback: (handle, recorded dxf name) -> is it in the replay drawing</param>
    public static List<string>? TypedLines(Answer a, Func<string, string?, bool>? handleExists = null, PickReplay? pick = null)
    {
        if (a.Status == "None") return new List<string> { "" };          // Enter
        if (a.Status == "Cancel") return new List<string> { "\u0003" };  // Esc/^C
        if (a.Status == "Keyword") return new List<string> { a.Keyword ?? "" };
        if (a.Status != "OK" || a.Value is not { } v) return null;
        switch (a.Kind)
        {
            case "point" or "corner":
                if (v.ValueKind != JsonValueKind.Array) return null;
                var c = v.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                if (c.Length < 2) return null;
                return new List<string> { c.Length > 2 && Math.Abs(c[2]) > 1e-12 ? $"{Inv(c[0])},{Inv(c[1])},{Inv(c[2])}" : $"{Inv(c[0])},{Inv(c[1])}" };
            case "distance" or "double":
                return new List<string> { Inv(v.GetDouble()) };
            case "angle":
                return new List<string> { Inv(v.GetDouble() * 180.0 / Math.PI) }; // recorded in radians; typed in degrees (assumes AUNITS 0)
            case "integer":
                return new List<string> { v.GetInt64().ToString(CultureInfo.InvariantCulture) };
            case "string":
                return new List<string> { v.GetString() ?? "" };
            case "entity" or "nestedEntity":
                if (Str(v, "handle") is { } h && (handleExists == null || handleExists(h, Str(v, "dxf"))))
                {
                    // where the object was picked matters at a command line prompt (BREAK, TRIM, FILLET, nested picks): the recorded point, with the recorded view
                    // shown, when it still hits this object; else the handle (see PickReplay)
                    if (pick?.EntityPoint(a.Kind == "nestedEntity", h, Str(v, "dxf"), v, a.Message ?? "") is { } pt) return new List<string> { pt };
                    return new List<string> { $"(handent \"{h}\")" };
                }
                return null;
            case "selection":
                // as it was made: the text typed for it (window corners, keywords). A mouse selection replays from its handles (checked against the
                // drawing); its clicks only where handles cannot say what was selected (attributes of -ATTEDIT: the insert's handle repeated)
                if (SelectionReplay.TypedLines(v) is { } typedLines) { pick?.OtherSelection(); return typedLines; }
                if (SelectionReplay.UseClicks(v))
                {
                    pick?.ClicksNeedView(v);   // the clicks are typed as before; the view they were made in is shown first where it can be
                    pick?.OtherSelection();
                    return SelectionReplay.ClickLines(v);
                }
                if (pick != null)
                {
                    // TRIM / EXTEND: the click picks the part. Every selection answer is counted by the pick replay (a single click in SingleClick, any other in OtherSelection)
                    bool counted = false;
                    var oneClick = SelectionReplay.ClickLines(v);
                    var clickLines = oneClick == null ? null : pick.SingleClick(v, oneClick, out counted);
                    if (!counted) pick.OtherSelection();
                    if (clickLines != null) return clickLines;
                }
                return SelectionReplay.HandleLines(v, handleExists);
        }
        return null;
    }

    static readonly System.Text.RegularExpressions.Regex SetvarValuePrompt = Rx.Create(@"^\s*Enter new value for\s+([A-Za-z0-9_$.]+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    static readonly System.Text.RegularExpressions.Regex SetvarNamePrompt = Rx.Create(@"variable name", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    static bool IsSetvar(Step s) => string.Equals(s.Command, "SETVAR", StringComparison.OrdinalIgnoreCase) && s.Kind is "command" or "ribbon";

    /// <summary>The words of a step's start text: lines and spaces split (a space is Enter in a macro), Esc characters and a leading `_` `.` `'` taken off, empty words dropped.</summary>
    static List<string> StartWords(string start) =>
        start.Split('\n', ' ', ';').Select(w => w.Replace("\u0003", "").Trim().TrimStart('_', '.', '\'')).Where(w => w.Length > 0).ToList();

    /// <summary>The start text of a step (command or converted macro, each line followed by Enter), or null for a step that has none.</summary>
    static string? StartText(Step s)
    {
        if (s.Kind == "ribbon")
        {
            var m = MacroToInput(s.Macro);
            if (m == null && s.Command != null) m = "_" + s.Command;
            return m == null ? null : m + "\n";
        }
        if (s.Kind == "command") return "_" + s.Command + "\n";
        if (s.Kind == "lisp") return s.Lisp + "\n";
        return null;
    }

    /// <summary>true when the start of the step already types the variable (a macro `^C^Cctab;` or `^C^C_setvar ctab;`).</summary>
    static bool StartTypesName(Step s, string sysvar) =>
        s.Kind == "ribbon" && StartText(s) is { } st && StartWords(st).Any(w => string.Equals(w, sysvar, StringComparison.OrdinalIgnoreCase));

    /// <summary>The system variable a SETVAR step sets, when the recording holds only the value prompt (`Enter new value for CTAB &lt;"Model"&gt;:`).
    /// AutoCAD runs a system variable typed at the command line (`CTAB`) as SETVAR; its "Enter variable name" prompt raises no event, so the
    /// recording has the value prompt only, and that prompt's text names the variable. Only the English text is recognised; other languages
    /// are not guessed (<see cref="Problem"/> says so). Null: not a SETVAR step, or the first answer is not that prompt.</summary>
    public static string? SetvarName(Step s, IReadOnlyList<Answer> effective)
        => !IsSetvar(s) || effective.Count == 0 ? null : SetvarValuePromptName(effective[0].Message);

    /// <summary>The variable named by a SETVAR value prompt text, or null.</summary>
    public static string? SetvarValuePromptName(string? message)
    {
        var m = SetvarValuePrompt.Match(message ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The name that the replay types before a SETVAR value: <see cref="SetvarName"/> unless the step's start already types it.</summary>
    public static string? SetvarNameToType(Step s, IReadOnlyList<Answer> effective)
        => SetvarName(s, effective) is { } n && !StartTypesName(s, n) ? n : null;

    /// <summary>true when a string answer holds a space (a layout name): sent as typed text, the space would be taken as Enter.</summary>
    static bool HasSpacedString(IReadOnlyList<Answer> effective) =>
        effective.Any(a => a.Kind is "string" or "keyword" && a.Status is "OK" or "Keyword" && TypedLines(a) is { } l && l.Any(x => x.IndexOf(' ') >= 0));

    /// <summary>The words of the start text as arguments of a LISP `(command ...)` call, with the Esc characters in front of it; null when this
    /// step cannot be sent that way: only a step of a built-in command that runs in (command) the same way (not a LISP step, not a command that
    /// opens a dialog, nothing left to reorder). A macro line with a quote, a LISP expression or an Esc inside is not touched.</summary>
    static (string Esc, List<string> Words)? CommandCallStart(Step s)
    {
        if (s.HadDialog || s.Kind is not ("command" or "ribbon") || StartText(s) is not { } st) return null;
        int n = 0; while (n < st.Length && st[n] == '\u0003') n++;
        var rest = st[n..];
        if (rest.Contains('\u0003') || rest.Contains('"') || rest.Contains('(') || rest.Contains('\\')) return null;
        var words = new List<string>();
        foreach (var line in rest.TrimEnd('\n').Split('\n'))
            if (line.Length == 0) words.Add("");
            else foreach (var w in line.Split(' ', StringSplitOptions.RemoveEmptyEntries)) words.Add(w);
        return words.Count == 0 ? null : (new string('\u0003', n), words);
    }

    /// <summary>Why a step cannot be replayed as recorded, or null; the step is then not typed (typing it would put text where it does not belong).
    /// 1. A SETVAR with nothing that names the variable: the first answer is not a value prompt in English, and the step has no more than that one
    ///    answer (a recorded name prompt, in any language, comes first and then the value) and the macro does not type the name.
    /// 2. A string answer with a space in a step that cannot be sent as a `(command ...)` call (see <see cref="CommandCallStart"/>).</summary>
    public static string? Problem(Step s)
    {
        var eff = SelectionReplay.Effective(s.Answers);
        if (IsSetvar(s) && eff.Count > 0 && SetvarName(s, eff) == null && !SetvarNamePrompt.IsMatch(eff[0].Message ?? "") && eff.Count < 2
            && !(s.Kind == "ribbon" && StartText(s) is { } st && StartWords(st).Count > 1))   // a macro `_setvar;ctab;` types the name
            return "SETVAR: the recording does not say which system variable was set, so its value is not typed (it would go where the variable name goes). Do this step by hand.";
        if (HasSpacedString(eff) && CommandCallStart(s) == null)
            return "A string answer has a space (a layout name?), and AutoCAD takes a space typed by Retrace as Enter here (a LISP command, a macro or a command with a dialog cannot be sent another way). Do this step by hand.";
        return null;
    }

    /// <summary>One typed line as an argument of a LISP `(command ...)` call: an empty line (Enter) is "", a (handent ..) expression made for an entity
    /// or a selection goes in as it is, anything else, a string or a keyword, is a string literal, so what it holds stays text.</summary>
    static string LispArg((string Line, bool Expr) x)
    {
        if (x.Expr) return x.Line;
        return "\"" + x.Line.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>The command-line text that replays a step: command or converted macro, then the answers. Lines are joined with \n (= Enter).
    /// Null when <see cref="Problem"/> says the step cannot be replayed.
    /// AutoCAD takes a space sent through SendStringToExecute as Enter, also at a prompt that takes text (checked with SETVAR CTAB and LAYOUT Set), so a
    /// string answer such as the layout name `A-101 Plan` would arrive as `A-101` and then `Plan`; and a LISP expression typed at such a prompt is
    /// taken as text. A step with such an answer is sent as one LISP form, `(command "_LAYOUT" "Set" "A-101 Plan")`, whose strings keep their spaces.</summary>
    public static string? InputText(Step s, bool includeStart = true, Func<string, string?, bool>? handleExists = null, PickReplay? pick = null)
    {
        if (Problem(s) != null) return null;
        if (pick != null) pick.Command = s.Command;
        var start = includeStart ? StartText(s) : "";
        if (start == null) return null;
        var answerLines = new List<(string Line, bool Expr)>();   // one Enter-terminated line each; Expr = a (handent ..) made for an entity or a selection
        bool cancel = false;
        var effective = SelectionReplay.Effective(s.Answers);
        // CTAB typed at the command line: SETVAR's value prompt is all that was recorded; the variable name goes first
        if (SetvarNameToType(s, effective) is { } sysvar) answerLines.Add((sysvar, false));
        foreach (var a in effective)
        {
            var typed = TypedLines(a, handleExists, pick);
            if (typed == null) break; // stop at the first untypable answer; the rest is up to the tester
            // ^C cancels on its own; an Enter after it would repeat the command (seen replaying LINE ... Esc, R25.1)
            if (a.Status == "Cancel") { cancel = true; break; }
            foreach (var l in typed) answerLines.Add((l, a.Kind is "entity" or "nestedEntity" or "selection" && l.StartsWith("(handent ", StringComparison.Ordinal)));
        }
        var sb = new StringBuilder();
        // a string with a space: the whole step as one (command ...) call; the Esc characters of a ^C^C macro go in front of it
        if (includeStart && HasSpacedString(effective) && CommandCallStart(s) is { } call)
        {
            sb.Append(call.Esc).Append("(command ").Append(string.Join(" ", call.Words.Select(w => LispArg((w, false))).Concat(answerLines.Select(LispArg)))).Append(")\n");
        }
        else
        {
            sb.Append(start);
            foreach (var x in answerLines) sb.Append(x.Line).Append('\n');
        }
        if (cancel) sb.Append('\u0003');
        return sb.ToString();
    }

    /// <summary>Input for a step whose command opens a dialog (recorded at <paramref name="dialogT"/>): First = the start plus the answers
    /// given before the dialog opened, After = the answers given after it (null if none). After is typed only once the dialog was
    /// confirmed; sent up front it would wait in AutoCAD's input queue and run as a command if the dialog is cancelled.</summary>
    public static (string? First, string? After) InputAroundDialog(Step s, long dialogT, Func<string, string?, bool>? handleExists = null, PickReplay? pick = null)
    {
        Step With(IEnumerable<Answer> answers) => new()
        {
            Kind = s.Kind, Title = s.Title, ItemId = s.ItemId, Macro = s.Macro, Tab = s.Tab, Panel = s.Panel, Path = s.Path,
            Command = s.Command, Lisp = s.Lisp, T = s.T, HadDialog = s.HadDialog, Answers = answers.ToList(),
        };
        var after = With(s.Answers.Where(a => a.T >= dialogT));
        // the answers before the dialog are built first, with the same pick replay; whatever the dialog did may have changed the drawing, so every pick after it uses handles
        var first = InputText(With(s.Answers.Where(a => a.T < dialogT)), handleExists: handleExists, pick: pick);
        pick?.AfterPossibleEdit();
        var rest = after.Answers.Count == 0 ? null : InputText(after, includeStart: false, handleExists: handleExists, pick: pick);
        return (first, string.IsNullOrEmpty(rest) ? null : rest);
    }

    /// <summary>Menu macro -> typed input: ^C -> Esc, ; -> Enter, \ (pause) dropped (the recorded answers fill it). Null for empty macros.</summary>
    public static string? MacroToInput(string? macro)
    {
        if (string.IsNullOrWhiteSpace(macro)) return null;
        var m = macro.Replace("^C", "\u0003", StringComparison.OrdinalIgnoreCase).Replace(";", "\n").Replace("\\", "").Replace("^M", "\n", StringComparison.OrdinalIgnoreCase);
        return m.TrimEnd('\n');
    }
}
