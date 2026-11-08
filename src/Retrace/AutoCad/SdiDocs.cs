using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>Opening drawings and typing text for a test, in both document modes (with SDI = 0 every method does what the code did before SDI support).
/// With the single document interface (SDI = 1) AutoCAD keeps one document object and opening a drawing replaces what it holds: the same
/// managed <see cref="Document"/> comes back with another drawing in it (checked in AutoCAD 2026: <c>DocumentToBeDestroyed</c>, then
/// <c>DocumentCreated</c> and <c>DocumentActivated</c> for the very same object; handlers added to the old drawing's events are gone, handlers on
/// <c>Application</c> and on the <c>DocumentManager</c> stay).
/// <c>Document.SendStringToExecute</c> into a drawing Retrace opened itself was, in several runs, never executed (after <c>DocumentCollection.Open</c> always;
/// after the COM <c>Documents.Open</c> sometimes with its first line lost); sent from inside a command it always ran. So under SDI drawings are opened through
/// COM and text is typed from inside the internal command RETRACETYPE.</summary>
static class SdiDocs
{
    /// <summary>The document mode now (SDI = 1 or 2)? The one place that reads the variable for a decision; a caller that knows the mode (a run reads it once) passes it on instead.</summary>
    public static bool IsSingleNow() => Retrace.Core.SdiGuard.IsSingle(Commands.SdiValue());

    /// <summary>Opens <paramref name="path"/> and returns its document (the active one after the open). SDI = 0: <c>DocumentCollection.Open</c>, as always. SDI: through COM
    /// <c>Documents.Open</c> (see the class summary), which replaces the drawing that is open. <paramref name="single"/> is the mode a run read once (null: read now).</summary>
    public static Document Open(string path, bool? single = null)
    {
        var dm = AcApp.DocumentManager;
        if (!(single ?? IsSingleNow())) return dm.Open(path, false);
        var acad = ((dynamic)Autodesk.AutoCAD.ApplicationServices.Application.AcadApplication);
        acad.Documents.Open(path);
        Log.Info($"opened {path} through COM Documents.Open (SDI is on)");
        return dm.MdiActiveDocument ?? throw new InvalidOperationException($"{System.IO.Path.GetFileName(path)} was opened but no drawing is active");
    }

    /// <summary>One send under SDI: <see cref="Pending"/> until the text is delivered or has failed. A replay keeps the tokens of its own sends and waits only for those.</summary>
    public sealed class SdiSend
    {
        public bool Pending { get; internal set; } = true;
        internal bool Cancelled;
        internal int Seq;   // the number of the text queued for RETRACETYPE, 0 while none
        /// <summary>The run that made this send is over: the text must not be typed any more. A RETRACETYPE that still comes for it finds nothing.</summary>
        public void Cancel()
        {
            Cancelled = true; Pending = false;
            if (Seq != 0) _items.Remove(Seq);
        }
    }

    /// <summary>The AutoCAD UI thread's dispatcher, captured when the plug-in starts (<see cref="Initialize"/>): callbacks and timers of this class run on it, never on whatever thread
    /// asks.</summary>
    static System.Windows.Threading.Dispatcher _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;

    /// <summary>Types <paramref name="text"/> into <paramref name="doc"/> (<c>Document.SendStringToExecute</c>). Every send of the replay, a suite, the test queue, the
    /// ribbon and the palettes goes through here; with SDI = 0 it is exactly that call. Under SDI, when nothing runs, the text goes through the internal command
    /// RETRACETYPE (see the class summary); a command in progress means the text answers it, and is sent directly.
    /// <paramref name="cancelFirst"/>: the text must start a command although something may be running (the test queue's RETRACERUN): ^C^C first (with SDI = 0 in the same string, as always).
    /// <paramref name="single"/> is the SDI state a run read once (null: read now). <paramref name="delivered"/> is called only on the SDI path, when the text was typed a moment after the call. <paramref name="failed"/> gets the reason when the text could not be delivered (null: it is printed on the command line).
    /// The token says whether the text has been delivered yet.</summary>
    public static SdiSend Send(Document doc, string text, bool activate, bool wrapUpInactiveDoc, bool echoCommand, Action<string>? failed = null, bool? single = null, Action? delivered = null, bool cancelFirst = false)
    {
        if (!(single ?? IsSingleNow()))
        {
            // SDI = 0: exactly this, nothing else (no variable, no name, no timing, no callback)
            doc.SendStringToExecute(cancelFirst ? "\u0003\u0003" + text : text, activate, wrapUpInactiveDoc, echoCommand);
            return new SdiSend { Pending = false };
        }
        // SDI: every drawing, the person's own too (the lost text was seen after a live switch to SDI and in drawings opened by a timer, so the mode is the test, not who opened it)
        if (IsCancelText(text)) return Cancel(doc);   // a cancel is typed into what runs, never through a command of its own
        if (IsIdle(doc)) return SendIdle(doc, text, activate, wrapUpInactiveDoc, echoCommand, failed, delivered);
        if (cancelFirst) return SendAfterCancel(doc, text, activate, wrapUpInactiveDoc, echoCommand, failed, delivered);
        doc.SendStringToExecute(text, activate, wrapUpInactiveDoc, echoCommand);   // busy: the text answers what runs (it cannot be typed through a command, one is running)
        return new SdiSend { Pending = false };
    }

    /// <summary>Only ^C / Esc characters: a cancel.</summary>
    public static bool IsCancelText(string text)
    {
        if (text.Length == 0) return false;
        foreach (var c in text) if (c != '\u0003' && c != '\u001b') return false;
        return true;
    }

    static string? SafeCommand(Document doc) { try { return doc.CommandInProgress; } catch (Exception) { return null; } }

    static bool CommandRuns(Document doc) { try { return !string.IsNullOrEmpty(doc.CommandInProgress); } catch (Exception) { return false; } }

    static readonly TimeSpan CancelWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>Under SDI: ^C^C into <paramref name="doc"/>, a reliable way. Plain <c>SendStringToExecute</c> is not (its text is lost in the cases the class summary names, a cancel
    /// too), so the escape goes through COM <c>SendCommand</c> from a callback of its own (the caller's timer tick goes on). Then it is checked that the command is gone
    /// (<c>CommandInProgress</c> empty) within <see cref="CancelWait"/>, and sent once more if it is not. The token is pending until that is settled. Nothing is sent when a dialog is open
    /// (a keystroke cannot reach the command behind it) or nothing runs.</summary>
    public static SdiSend Cancel(Document doc)
    {
        var token = new SdiSend();
        _ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => CancelStep(doc, token, 1)));
        return token;
    }

    static void CancelStep(Document doc, SdiSend token, int attempt)
    {
        var waiting = false;
        try
        {
            if (token.Cancelled || ViewRestorer.IsShutdown || !CommandRuns(doc)) return;
            if (IsModalOpen()) { Log.Info("SDI: a dialog is open: no cancel typed (it could not reach the command)"); return; }
            try { SendByCom(doc, "\u0003\u0003"); } catch (Exception ex) { Log.Info($"SDI: the cancel through COM failed ({ex.Message})"); }
            waiting = true;
            var since = DateTime.UtcNow;
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => Log.Safe("SDI cancel check", () =>
            {
                if (token.Cancelled || ViewRestorer.IsShutdown || !CommandRuns(doc)) { timer.Stop(); token.Pending = false; return; }
                if (DateTime.UtcNow - since < CancelWait) return;
                timer.Stop();
                if (attempt == 1) { Log.Info("SDI: a command still runs 1.5 s after the cancel: sending it again"); CancelStep(doc, token, 2); }
                else { Log.Info("SDI: a command still runs after two cancels"); token.Pending = false; }
            });
            timer.Start();
        }
        catch (Exception ex) { Log.Info($"SDI: cancel not sent ({ex.GetType().Name}: {ex.Message})"); }
        finally { if (!waiting) token.Pending = false; }
    }

    /// <summary>Types the text under SDI through RETRACETYPE once AutoCAD is idle, however busy it is now (retried for 10 s, then <paramref name="failed"/>): never a plain
    /// SendStringToExecute. The text may not be typed yet when this returns: the token says so until it is delivered or failed, and <paramref name="delivered"/> is called at the moment it was.</summary>
    public static SdiSend SendIdle(Document doc, string text, bool activate, bool wrapUpInactiveDoc, bool echoCommand, Action<string>? failed = null, Action? delivered = null)
    {
        var token = new SdiSend();
        SendWhenIdle(doc, text, activate, wrapUpInactiveDoc, echoCommand, failed, delivered, DateTime.UtcNow, token);
        return token;
    }

    /// <summary>Under SDI, text that has to follow a cancel (the test queue's next RETRACERUN after a test that left a command open): the <see cref="Cancel"/> goes first (verified, sent once
    /// more if it did not take), then the text through <see cref="SendIdle"/>, which waits until nothing runs. One ordered send with one failure callback.</summary>
    static SdiSend SendAfterCancel(Document doc, string text, bool activate, bool wrapUpInactiveDoc, bool echoCommand, Action<string>? failed = null, Action? delivered = null)
    {
        Cancel(doc);
        return SendIdle(doc, text, activate, wrapUpInactiveDoc, echoCommand, failed, delivered);
    }

    /// <summary>The identity of the drawing in a Document (SDI replaces the drawing inside the same object); zero when it cannot be read.</summary>
    public static IntPtr DbId(Document d) { try { return d.Database.UnmanagedObject; } catch (Exception) { return IntPtr.Zero; } }

    /// <summary>The same drawing: by the database's unmanaged object (the managed wrapper can be a new object each time it is asked for). A drawing that cannot be read counts as the same:
    /// nothing is dropped or hooked again on a guess.</summary>
    public static bool SameDb(IntPtr a, IntPtr b) => Retrace.Core.DbIdentity.SameForView((long)a, (long)b);
    public static bool NeedsRehook(bool hooked, IntPtr current, IntPtr hookedFor) => Retrace.Core.DbIdentity.NeedsRehook(hooked, (long)current, (long)hookedFor);

    /// <summary>Unsaved changes in the drawing. When that cannot be read the answer is "modified": never risk a person's changes.</summary>
    public static bool IsModified(Document d) { try { return !(bool)((dynamic)d.GetAcadDocument()).Saved; } catch (Exception) { return true; } }

    /// <summary>No command, no prompt and no dialog: the only moment text may be typed through a command of its own.</summary>
    public static bool IsIdle(Document doc) => ViewRestorer.Quiet(doc) && !IsModalOpen();

    /// <summary>A dialog is open now (read live: the main window is disabled, or CMDACTIVE has the dialog bit).</summary>
    internal static bool IsModalOpen()
    {
        try
        {
            if (!Native.IsEnabled(AcApp.MainWindow.Handle)) return true;
            return ((Ask.ActiveBits() ?? 0) & 8) != 0;
        }
        catch (Exception) { return false; }
    }

    static readonly TimeSpan SendGiveUp = TimeSpan.FromSeconds(10);

    sealed class PendingText
    {
        public Document Doc = null!; public string Text = ""; public bool Activate, Wrap, Echo;
        public bool Typed;       // RETRACETYPE took it and typed it
        public bool Cancelled;   // the send gave up: a RETRACETYPE that still comes for it drops it and types nothing
    }

    static readonly Dictionary<int, PendingText> _items = new();   // by sequence number: each RETRACETYPE types only the text it was sent for
    static int _seq;

    // From a callback of its own: the COM call can run the command before it returns (then it has typed the text), a dialog included, and the caller (the replay's timer tick) has to go
    // on meanwhile to fill it. Idle is checked again right before the call; when AutoCAD is busy it is tried again 250 ms later, for 10 s; text is never typed into a running prompt.
    // When the command has not run by the time the call returns (AutoCAD was still taking a cancel in), the send waits for it for up to 5 s, then fails and marks its item cancelled.
    static void SendWhenIdle(Document doc, string text, bool activate, bool wrap, bool echo, Action<string>? failed, Action? delivered, DateTime since, SdiSend token)
    {
        void Finish(string? failure)
        {
            token.Pending = false;
            if (token.Cancelled) return;   // the run is over: neither callback
            try { if (failure != null) Fail(failed, failure); else delivered?.Invoke(); }
            catch (Exception ex) { Log.Error("SDI send callback", ex); }
        }
        _ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            var wait = false;   // the token stays pending: a timer finishes it
            string? failure = null;
            // the one way to try again: later on the timer, the token staying pending
            void RetryLater()
            {
                wait = true;
                var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(250) };
                t.Tick += (_, _) => { t.Stop(); SendWhenIdle(doc, text, activate, wrap, echo, failed, delivered, since, token); };
                t.Start();
            }
            try
            {
                if (token.Cancelled) { token.Pending = false; return; }
                if (!IsIdle(doc))
                {
                    if (DateTime.UtcNow - since > SendGiveUp) { failure = "SDI: AutoCAD stayed busy for 10 s, so the typed text was not sent: " + text.Replace('\n', '|').Trim(); return; }
                    if (string.Equals(SafeCommand(doc), "RETRACETYPE", StringComparison.OrdinalIgnoreCase)) Cancel(doc);   // one left at its prompt by a COM call that threw: cancelled, nothing is typed
                    RetryLater();
                    return;
                }
                var item = new PendingText { Doc = doc, Text = text, Activate = activate, Wrap = wrap, Echo = echo };
                var seq = ++_seq;
                _items[seq] = item;
                token.Seq = seq;
                try { SendByCom(doc, "RETRACETYPE\n" + seq + "\n"); }
                catch (Exception ex)
                {
                    _items.Remove(seq);                               // a RETRACETYPE that still runs for it finds nothing and types nothing
                    if (item.Typed) return;                           // it threw after the text was typed: delivered
                    if (DateTime.UtcNow - since <= SendGiveUp) { RetryLater(); return; }   // AutoCAD rejected the call (busy, RPC_E_CALL_REJECTED): again, until the give-up time
                    failure = $"SDI: the typed text could not be delivered (RETRACETYPE through COM failed: {ex.Message})";
                    return;
                }
                if (item.Typed) return;
                // queued, not run yet
                wait = true;
                var start = DateTime.UtcNow;
                var poll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(100) };
                poll.Tick += (_, _) => Log.Safe("SDI send wait", () =>
                {
                    if (token.Cancelled) { poll.Stop(); }
                    else if (item.Typed) { poll.Stop(); Finish(null); }
                    else if (DateTime.UtcNow - start > TimeSpan.FromSeconds(5)) { poll.Stop(); item.Cancelled = true; _items.Remove(seq); Finish("SDI: the typed text could not be delivered (RETRACETYPE did not run within 5 s)"); }
                });
                poll.Start();
            }
            catch (Exception ex) { failure = $"SDI: the typed text could not be delivered ({ex.GetType().Name}: {ex.Message})"; }
            finally
            {
                if (!wait) Finish(failure);
            }
        }));
    }

    static void Fail(Action<string>? failed, string why)
    {
        Log.Info(why);
        if (failed != null) failed(why); else Commands.Print(why);
    }

    /// <summary>The command RETRACETYPE (internal; <see cref="SendIdle"/> starts it as "RETRACETYPE", Enter, the send's number): types the text of that send, from inside a command. A send that
    /// gave up in the meantime (cancelled) is dropped and nothing is typed.</summary>
    public static void TypePending()
    {
        var ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;
        var r = ed.GetString(new Autodesk.AutoCAD.EditorInput.PromptStringOptions("") { AllowSpaces = false });
        if (r.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || !int.TryParse(r.StringResult, out var seq) || !_items.Remove(seq, out var t)) return;
        if (t.Cancelled) { Log.Info("SDI: a cancelled send was dropped"); return; }
        t.Typed = true;
        t.Doc.SendStringToExecute(t.Text, t.Activate, t.Wrap, t.Echo);
    }

    /// <summary>Types <paramref name="text"/> through the COM <c>SendCommand</c>. It runs the command before it returns (in process), so call it from a callback of
    /// its own, never from a timer tick that has to go on while a dialog is open.</summary>
    public static void SendByCom(Document doc, string text) => ((dynamic)doc.GetAcadDocument()).SendCommand(text);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
    static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint size);

    /// <summary>The long form of a path (8.3 names expanded: a user name of more than 8 characters shows as <c>CLAUDE~1</c> in the temp path); the path itself when it does not exist.</summary>
    public static string LongPath(string path)
    {
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var n = GetLongPathName(path, sb, (uint)sb.Capacity);
            if (n > 0 && n < sb.Capacity) return sb.ToString();
            // a file that does not exist yet: the long form of its folder
            var dir = Path.GetDirectoryName(path);
            return dir != null && dir != path ? Path.Combine(LongPath(dir), Path.GetFileName(path)) : path;
        }
        catch (Exception) { return path; }
    }

    /// <summary>Plugin start-up: the registry of the test's own files compares canonical long paths (<see cref="LongPath"/>).</summary>
    public static void Initialize()
    {
        _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;   // plug-in start-up runs on the UI thread
        Retrace.Core.SdiGuard.Own = new Retrace.Core.SdiOwnFiles(LongPath(Path.GetTempPath()), LongPath);
    }

    /// <summary>Writes what Retrace sees of <paramref name="file"/> now (write time and size) to its sidecar: from now on it may be deleted when old, until somebody
    /// else saves into it (<see cref="Retrace.Core.SdiTempRecord"/>).</summary>
    public static void Record(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists) return;
            File.WriteAllText(Retrace.Core.SdiTempRecord.SidecarPath(file), Retrace.Core.SdiTempRecord.Of(fi.LastWriteTimeUtc, fi.Length).Format());
        }
        catch (Exception) { }
    }

    /// <summary>The one way a test copy is made (the template, a recorded drawing, a suite's copy): into <paramref name="dir"/>, with the write time of now (a copy keeps
    /// the time of what it was copied from, and would look a day old at once), registered as this session's own file (<see cref="Retrace.Core.SdiOwnFiles"/>) with a sidecar record. <paramref name="track"/> is false for a suite's copy with SDI = 0: nothing of this.</summary>
    public static string CopyInto(string dir, string source, string fileName, bool track = true)
    {
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, fileName);
        File.Copy(source, copy, true);
        if (!track) return copy;   // SDI = 0 (a suite's copy): the copy as it always was
        try { File.SetLastWriteTimeUtc(copy, DateTime.UtcNow); } catch (Exception) { }
        Retrace.Core.SdiGuard.Own.Created(copy);
        Record(copy);
        return copy;
    }

    /// <summary>A copy of the drawing template in Retrace's SDI temp folder (<c>retrace-sdi</c>), which opens as a plain drawing. SDI has no <c>Documents.Add</c>.</summary>
    public static string NewTemplateCopy()
    {
        var template = Commands.FindTemplate();
        if (string.IsNullOrEmpty(template) || !File.Exists(template)) throw new InvalidOperationException("SDI is on and no drawing template (acad.dwt) was found to open a drawing from");
        return CopyInto(Path.Combine(Path.GetTempPath(), Retrace.Core.SdiGuard.OwnFolder), template, "Test-" + Guid.NewGuid().ToString("N")[..6] + ".dwg");
    }

    /// <summary>A copy of a recorded drawing for an "open" step under SDI (<see cref="Retrace.Core.SdiGuard.OpenStepNeedsCopy"/>), same file name, in its own
    /// folder below <c>retrace-sdi</c>: the test never changes the person's file.</summary>
    public static string TempCopyOf(string path) =>
        CopyInto(Path.Combine(Path.GetTempPath(), Retrace.Core.SdiGuard.OwnFolder, Guid.NewGuid().ToString("N")[..8]), path, Path.GetFileName(path));

    /// <summary>Deletes the drawings Retrace's tests left below <paramref name="root"/> once they are a day old, with what AutoCAD keeps beside them (.bak, .dwl, the sidecar)
    /// and the folders that stay empty. Only a drawing that has a record and still looks as Retrace left it (write time and size): one somebody saved into later, one
    /// without a record, and one that is open in this AutoCAD are never touched (<see cref="Retrace.Core.SdiTempRecord.MayDelete"/>). Records of drawings that are gone
    /// are deleted when old.</summary>
    public static void CleanStale(string root, IReadOnlyList<string> open)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var now = DateTime.UtcNow;
            var own = Retrace.Core.SdiGuard.Own;
            // folders that are stale NOW, before anything is deleted in them (deleting changes their write time): only these may go when empty afterwards
            var staleDirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Where(d => Retrace.Core.SdiGuard.IsStaleTemp(Directory.GetCreationTimeUtc(d), Directory.GetLastWriteTimeUtc(d), now)).ToList();
            foreach (var side in Directory.EnumerateFiles(root, "*" + Retrace.Core.SdiTempRecord.SidecarExtension, SearchOption.AllDirectories).ToList())
            {
                var drawing = side[..^Retrace.Core.SdiTempRecord.SidecarExtension.Length];
                try
                {
                    if (!File.Exists(drawing)) { if (Retrace.Core.SdiGuard.IsStaleTemp(File.GetCreationTimeUtc(side), File.GetLastWriteTimeUtc(side), now)) File.Delete(side); continue; }
                    if (own.BelongsToOpenDrawing(drawing, open)) continue;
                    var fi = new FileInfo(drawing);
                    if (!Retrace.Core.SdiTempRecord.MayDelete(Retrace.Core.SdiTempRecord.Parse(File.ReadAllText(side)), fi.CreationTimeUtc, fi.LastWriteTimeUtc, fi.Length, now)) continue;
                    // the drawing first; its companions only when that worked (a drawing that is still held stays whole)
                    File.Delete(drawing);
                    var dir = Path.GetDirectoryName(drawing)!; var name = Path.GetFileName(drawing);
                    foreach (var f in Directory.EnumerateFiles(dir)) if (Retrace.Core.SdiTempRecord.IsCompanion(Path.GetFileName(f), name)) try { File.Delete(f); } catch (Exception) { }
                    try { File.Delete(side); } catch (Exception) { }
                }
                catch (Exception) { }
            }
            foreach (var d in staleDirs.OrderByDescending(x => x.Length))
                try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (Exception) { }
        }
        catch (Exception) { }
    }

    /// <summary>Whether a suite copy folder holds sidecar records (it was made by an SDI session): then the record rule decides what goes, not its age.</summary>
    public static bool HasSidecar(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*" + Retrace.Core.SdiTempRecord.SidecarExtension, SearchOption.AllDirectories).Any(); }
        catch (Exception) { return false; }
    }

    /// <summary>A folder of an SDI session's copies: the record rule and the open-drawing check, then the folder goes when it was stale and is empty. Pure file system: the open drawings'
    /// names are given (taken on the UI thread), so it can run on a pool thread.</summary>
    public static void CleanStaleCopyFolder(string dir, IReadOnlyList<string> open)
    {
        var wasStale = Retrace.Core.SdiGuard.IsStaleTemp(Directory.GetCreationTimeUtc(dir), Directory.GetLastWriteTimeUtc(dir), DateTime.UtcNow);
        CleanStale(dir, open);
        try { if (wasStale && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception) { }
    }

    /// <summary>The names of the drawings open now: read on the UI thread, for the cleanup that walks the file system on another.</summary>
    public static List<string> OpenNames()
    {
        var open = new List<string>();
        try { foreach (Document d in AcApp.DocumentManager) open.Add(d.Name); } catch (Exception) { }
        return open;
    }

    /// <summary>The SDI test drawings' folder, when it exists: the walk runs on a pool thread with the open drawings taken here.</summary>
    public static void CleanStaleSdi()
    {
        var root = Path.Combine(Path.GetTempPath(), Retrace.Core.SdiGuard.OwnFolder);
        if (!Directory.Exists(root)) return;
        var open = OpenNames();
        System.Threading.Tasks.Task.Run(() => CleanStale(root, open));
    }

    static bool _startCleanHooked;

    /// <summary>Plug-in start-up (and a suite's start, see <c>SuiteRun</c>; not after every replay): the cleanup waits for AutoCAD's first idle moment (the start-up itself stays quick), then runs once.</summary>
    public static void CleanStaleOnFirstIdle()
    {
        // nothing new at AutoCAD start-up for SDI = 0 users: only when SDI is on or the SDI temp folder exists
        if (_startCleanHooked || !(IsSingleNow() || Directory.Exists(Path.Combine(Path.GetTempPath(), Retrace.Core.SdiGuard.OwnFolder)))) return;
        _startCleanHooked = true;
        EventHandler? h = null;
        h = (_, _) => { AcApp.Idle -= h; Log.Safe("SDI start-up cleanup", CleanStaleSdi); };
        AcApp.Idle += h;
    }


    static bool _tempNoticePrinted;

    /// <summary>Once per session, when a replay under SDI leaves a test drawing open: it is a temp copy, and a person has to be told where it is and how to keep work.</summary>
    public static void NoticeTempDrawing(Document? doc)
    {
        try
        {
            if (_tempNoticePrinted || doc == null || !Retrace.Core.SdiGuard.IsOwnTestDrawing(doc.Name)) return;
            _tempNoticePrinted = true;
            Commands.Print("This test drawing is a temp copy in %TEMP%\\retrace-sdi; use Save As to keep your work.");
        }
        catch (Exception) { }
    }

    /// <summary>A blank drawing under SDI: the template copy, opened.</summary>
    public static Document OpenBlankCopy() => Open(NewTemplateCopy(), true);

    /// <summary>Under SDI, before a test opens a drawing: why it must not (the drawing that would be replaced has unsaved changes of a person, see
    /// <see cref="Retrace.Core.SdiGuard"/>), else null after saving the test's own earlier drawing. With SDI = 0 always null and nothing is done.</summary>
    public static string? PrepareForOpen(bool? single = null)
    {
        if (!(single ?? IsSingleNow())) return null;
        var open = new List<(string, bool)>();
        foreach (Document d in AcApp.DocumentManager)
        {
            open.Add((d.Name, IsModified(d)));
        }
        if (Retrace.Core.SdiGuard.Refusal(true, open) is { } refusal) return refusal;
        return SaveOwnDrawings(true);
    }

    /// <summary>Under SDI, saves the drawing a test opened itself earlier (<see cref="Retrace.Core.SdiGuard.SaveOwnBeforeOpen"/>, a temp file nobody works in): opening
    /// the next one over a changed drawing asks to save it, and a question nobody answers stops the run. (Closing it without saving is no way: under SDI
    /// <c>CloseAndDiscard</c> and the COM <c>Close</c> end in AutoCAD quitting.) Null when done, else why it was not.</summary>
    public static string? SaveOwnDrawings(bool? single = null)
    {
        var sdi = single ?? IsSingleNow();
        foreach (var d in AcApp.DocumentManager.Cast<Document>().ToList())
        {
            string name; try { name = d.Name; } catch (Exception) { continue; }
            if (!Retrace.Core.SdiGuard.SaveOwnBeforeOpen(sdi, name)) continue;
            if (!IsModified(d)) continue;
            try { ((dynamic)d.GetAcadDocument()).Save(); Record(name); Log.Info($"SDI: saved the test's own drawing {name} before the next one opens"); }
            catch (Exception ex) { Log.Info($"SDI: {name} not saved ({ex.Message})"); return $"SDI is on and the test's own drawing {System.IO.Path.GetFileName(name)} could not be saved before the next drawing opens ({ex.Message})"; }
        }
        return null;
    }

    /// <summary>The run that was to begin in the test copy <paramref name="expected"/> did not (timeout, wrong drawing, Start failed, an exception): the copy is treated like a run that ended. It is
    /// no longer fresh; if it is open it is saved as soon as AutoCAD is idle (<see cref="EndRunLater"/>: a failed save disowns it, or leaves it a suite's), if it is not open it is given up.
    /// Never leaves an own copy with changes and no run. Nothing under SDI = 0 (<paramref name="expected"/> is null then).</summary>
    public static void AbandonCopy(string? expected, bool suiteRun)
    {
        if (string.IsNullOrWhiteSpace(expected)) return;
        var own = Retrace.Core.SdiGuard.Own;
        own.ClearFresh(expected);
        Document? open = null;
        try { foreach (Document d in AcApp.DocumentManager) { if (own.SamePath(d.Name, expected)) { open = d; break; } } } catch (Exception) { }
        if (open == null) { own.Abandon(expected); return; }
        own.BeginRun();   // EndRunLater ends a run: this one balances it
        EndRunLater(open, suiteRun);
    }

    /// <summary>The end of a replay under SDI: the test's own drawing it ran in is saved, so that anything changed in it afterwards is a person's. Never during a command: it waits,
    /// on a timer, until nothing runs in the drawing (no command, no prompt, no dialog; the cancel a Stop or an error sent has taken effect), up to a minute, and saves then. If AutoCAD
    /// stays busy or the save fails, <see cref="Retrace.Core.SdiOwnFiles.EndRun"/> says so: a RETRACERUN's drawing is disowned (its changes are a person's), a suite's stays the
    /// suite's and its next test saves it. A drawing that is not the test's own is left alone. Nothing is sent to AutoCAD here (a command a person has to finish is theirs).</summary>
    public static void EndRunLater(Document? doc, bool suiteRun, TimeSpan? settle = null)
    {
        if (ViewRestorer.IsShutdown) return;   // the plug-in is unloading: no COM call, no timer
        string? name = null; try { name = doc?.Name; } catch (Exception) { }
        if (doc == null || name == null || !Retrace.Core.SdiGuard.IsOwnTestDrawing(name)) { Retrace.Core.SdiGuard.Own.EndRun(name, true, suiteRun); return; }
        // the usual case: the replay has stopped and nothing runs: save now, before the verdict box of RETRACERUN opens
        // (settle: a drawing Retrace has just opened outside a run gets a moment first, its start-up code (acaddoc.lsp) may still be changing it)
        if (settle == null && CanSaveNow(doc)) { SaveEnd(doc, name, suiteRun); return; }
        var since = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Log.Safe("SDI end of run", () =>
        {
            if (ViewRestorer.IsShutdown) { timer.Stop(); return; }
            if (settle is { } wait && DateTime.UtcNow - since < wait) return;
            // the drawing is gone (the next test replaced it: its open saved this one first)
            string? now = null; Document? cur = null;
            try { foreach (Document d in AcApp.DocumentManager) if (ReferenceEquals(d, doc)) cur = d; now = cur?.Name; } catch (Exception) { }
            if (cur == null || !Retrace.Core.SdiGuard.Own.SamePath(now, name)) { timer.Stop(); Retrace.Core.SdiGuard.Own.EndRun(name, true, suiteRun); return; }
            if (!CanSaveNow(cur))
            {
                if (DateTime.UtcNow - since < TimeSpan.FromSeconds(60) + (settle ?? TimeSpan.Zero)) return;   // still busy: the next tick, never during a command
                timer.Stop(); Log.Info($"SDI: {name} was still busy a minute after the run: not saved"); Retrace.Core.SdiGuard.Own.EndRun(name, false, suiteRun); return;
            }
            timer.Stop();
            SaveEnd(cur, name, suiteRun);
        });
        timer.Start();
    }

    /// <summary>Retrace's own verdict box of RETRACERUN is open (set by the replay around it): the save may happen with it open, it is not a person's dialog.</summary>
    public static bool VerdictBoxOpen { get; set; }

    static bool CanSaveNow(Document d)
    {
        try
        {
            var modal = IsModalOpen();
            // Retrace's own verdict box excuses only itself: it must be the dialog in front, not just a flag while somebody else's dialog is open
            var verdictOnly = modal && VerdictBoxOpen && Native.ForegroundDialogStartsWith("Retrace test:");
            return Retrace.Core.SdiSavePolicy.CanSaveNow(!string.IsNullOrEmpty(d.CommandInProgress), d.Editor.IsQuiescent, modal, verdictOnly);
        }
        catch (Exception) { return false; }
    }

    static void SaveEnd(Document cur, string name, bool suiteRun)
    {
        var saved = true;
        try
        {
            if (IsModified(cur)) { ((dynamic)cur.GetAcadDocument()).Save(); Record(name); }
        }
        catch (Exception ex) { saved = false; Log.Info($"SDI: {name} not saved at the end of the run ({ex.Message})"); }
        Retrace.Core.SdiGuard.Own.EndRun(name, saved, suiteRun);
    }
}
