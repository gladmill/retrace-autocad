using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using AcadStatus.Core;
using AcadStatus.Ui;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadStatus.AutoCad;

/// <summary>Everything that runs while AutoCAD runs: one timer on the UI thread (1 s) that looks at AutoCAD, writes <c>acad-&lt;pid&gt;.json</c>,
/// merges all files of the folder and updates the pane and the details window. A System.Windows.Forms.Timer keeps firing inside modal
/// loops (a LISP <c>(alert ...)</c>), which is the point: it is when a dialog is open that the file must say so. Every tick is in try/catch
/// and does no database work; nothing here touches, closes or presses anything in a dialog.</summary>
sealed class StatusService : IDisposable
{
    public static StatusService? Current { get; private set; }

    readonly string _dir;
    readonly int _pid = Environment.ProcessId;
    readonly StatusFiles _files;
    readonly AcadStatusBuilder _builder;
    readonly PromptWatch _prompts = new();
    readonly DialogClock _dialogClock = new();
    readonly StatusPane _pane = new();
    readonly LivenessCache _liveness = new(ProcessProbe.Probe);
    readonly ClickGuard _clickGuard = new();
    readonly WriteRetry _writeRetry = new();
    readonly LatestRead<FolderRead> _folder;
    System.Windows.Forms.Timer? _timer;
    DetailsForm? _details;

    string? _lastKey;
    DateTime? _lastWrite, _lastCleanup;
    MergeResult _merged = new(Array.Empty<MergedEntry>(), Array.Empty<IgnoredEntry>());
    IReadOnlyList<Unreadable> _skipped = Array.Empty<Unreadable>();
    bool _paneWanted = true, _inUi, _disposed;

    public string Folder => _dir;

    StatusService(string dir)
    {
        _dir = dir;
        _files = new StatusFiles(dir);
        _builder = new AcadStatusBuilder("AcadStatus " + VersionText(), _pid, ProcessProbe.OwnStart());
        // the listing and the reads of the folder run on a background task, one at a time; our own file is left out (it is in memory)
        _folder = new LatestRead<FolderRead>(() => _files.Read(_builder.FileName));
    }

    static string VersionText()
    {
        var v = typeof(StatusService).Assembly.GetName().Version;
        return v == null ? "0" : v.Major + "." + v.Minor + "." + v.Build;
    }

    public static StatusService Start()
    {
        var s = new StatusService(StatusFiles.ResolveDefault());
        Current = s;
        s.Begin();
        return s;
    }

    void Begin()
    {
        Log.Safe("create status folder", EnsureFolder);
        Log.Safe("start-up clean-up", () => CleanUp(DateTime.UtcNow));
        Log.Safe("prompt watch", _prompts.Start);
        _paneWanted = PaneSetting.Load(Log.SettingsFile);
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    void EnsureFolder()
    {
        if (Directory.Exists(_dir)) return;
        Directory.CreateDirectory(_dir);
        // Several Windows users may run AutoCAD on one machine: let every user write here (best effort; the creator already can).
        try
        {
            var info = new DirectoryInfo(_dir);
            var acl = info.GetAccessControl();
            var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                users, System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        catch (Exception ex) { Log.Error("status folder permissions", ex); }
    }

    /// <summary>Deletes status files of processes that are gone (or whose pid another process has taken) and were last updated over an hour
    /// ago: on start and every ten minutes (CONTRACT.md, "Files").</summary>
    void CleanUp(DateTime now)
    {
        _lastCleanup = now;
        var deleted = _files.Cleanup(now, (pid, start, age) => _liveness.IsAlive(pid, start, now, age));
        if (deleted.Count > 0) Log.Info("cleaned up " + deleted.Count + " old status file(s)");
    }

    // ---- the tick ------------------------------------------------------------------------------

    /// <summary>One tick. The look at AutoCAD and the file write come first and are never guarded against re-entry: updating the pane or the
    /// window can open a nested modal loop (AutoCAD's ".NET Assertion Failed" dialog, a message box from StatusBar.Update), and the ticks that
    /// run inside it must still write the file, because a dialog is open then, which is what the file has to say. Only the part that touches
    /// the pane and the window is guarded (<see cref="_inUi"/>): a nested tick skips it.</summary>
    void Tick()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        Snapshot? snap = null;
        Log.Safe("look at AutoCAD", () => snap = Sample(now));
        if (snap != null) Log.Safe("write status", () => Write(snap, now));
        if (CleanupSchedule.Due(_lastCleanup, now)) Log.Safe("clean-up", () => CleanUp(now));

        if (_inUi) return;
        _inUi = true;
        try
        {
            // The folder is read and merged only for something that shows it: the pane, or an open details window. The dump merges on demand.
            if (NeedsMerge)
            {
                Log.Safe("merge", () => Merge(now, false));
                Log.Safe("pane", () => UpdatePane(now));
                Log.Safe("details window", () => UpdateDetails(now));
            }
        }
        finally { _inUi = false; }
    }

    bool NeedsMerge => _paneWanted || (_details is { IsDisposed: false, Visible: true });

    Snapshot Sample(DateTime now)
    {
        string? doc = null;
        int docs = 0, cmdActive = 0;
        string cmdNames = "";
        Autodesk.AutoCAD.ApplicationServices.Document? active = null;
        try
        {
            var dm = AcApp.DocumentManager;
            docs = dm.Count;
            active = dm.MdiActiveDocument;
            if (active != null) doc = Path.GetFileName(active.Name);
        }
        catch (Exception ex) { Log.Error("document", ex); }
        try { cmdActive = Convert.ToInt32(CoreApp.GetSystemVariable("CMDACTIVE")); } catch (Exception ex) { Log.Error("CMDACTIVE", ex); }
        try { cmdNames = CoreApp.GetSystemVariable("CMDNAMES") as string ?? ""; } catch (Exception ex) { Log.Error("CMDNAMES", ex); }
        string? prompt = null;
        try { prompt = _prompts.PromptOf(active, cmdActive); } catch (Exception ex) { Log.Error("prompt", ex); }
        DialogSnap? dialog = null;
        try { dialog = DialogScan.Find(MainWindowHandle(), DetailsHandle()); } catch (Exception ex) { Log.Error("dialog scan", ex); }
        var since = _dialogClock.Observe(dialog, now);
        return new Snapshot(doc, docs, cmdActive, cmdNames, prompt, dialog, since);
    }

    static IntPtr MainWindowHandle()
    {
        try { return CoreApp.MainWindow.Handle; } catch { return IntPtr.Zero; }
    }

    IntPtr DetailsHandle()
    {
        var d = _details;
        return d != null && !d.IsDisposed && d.IsHandleCreated ? d.Handle : IntPtr.Zero;
    }

    AcadStatusDoc? _own;

    void Write(Snapshot snap, DateTime now)
    {
        var doc = _builder.Build(snap, now);
        _own = doc;
        if (!_writeRetry.MayAttempt(now)) return;   // backing off after several failures in a row
        if (!WritePolicy.ShouldWrite(_lastKey, _lastWrite, doc.ContentKey, now)) return;
        try
        {
            var path = Path.Combine(_dir, _builder.FileName);
            try { AtomicFile.Write(path, doc.Json, _pid); }
            catch (DirectoryNotFoundException)
            {
                // the folder was ensured once at start (with its permissions); only when it is gone again is it ensured again, then written
                EnsureFolder();
                AtomicFile.Write(path, doc.Json, _pid);
            }
        }
        catch (Exception ex)
        {
            // a failed replace (a reader that holds the file, a full disk) is tried again at the next refresh; it never fails the tick
            _writeRetry.Failed(now);
            Log.Error("write status", ex);
            return;
        }
        _writeRetry.Succeeded();
        _lastKey = doc.ContentKey;
        _lastWrite = now;
    }

    /// <summary>Ranks the folder's entries and ours. The listing and the reads of the folder are done by a background task (one at a time);
    /// the tick merges what the last task found, at most a tick old, and starts the next. <paramref name="withRaw"/> is the dump: it wants the
    /// folder as it is now, so it reads in place, and our own entry carries its file text.</summary>
    void Merge(DateTime now, bool withRaw)
    {
        FolderRead? read;
        if (withRaw) read = _files.Read(_builder.FileName);
        else { read = _folder.Latest; _folder.Kick(); }
        read ??= new FolderRead(Array.Empty<StatusEntry>(), Array.Empty<Unreadable>());   // the very first tick: the other files appear on the next
        var entries = new List<StatusEntry>(read.Entries);
        // our own entry comes from memory, not from its file: the pane still works when the folder cannot be written
        if (_own != null) entries.Add(withRaw ? _own.Entry with { Raw = _own.Json } : _own.Entry);
        _merged = StatusMerge.Merge(entries, now, (pid, start, age) => _liveness.IsAlive(pid, start, now, age));
        _skipped = read.Skipped;
    }

    void UpdatePane(DateTime now)
    {
        if (!_paneWanted) return;
        if (!_pane.IsAdded) { _pane.Add(); }
        _pane.Apply(CompactText.Build(_merged, now));
    }

    void UpdateDetails(DateTime now)
    {
        var d = _details;
        if (d == null) return;
        if (d.IsDisposed) { _details = null; return; }
        if (d.Visible) d.SetEntries(_merged, _skipped, now);
    }

    // ---- the pane and the details window --------------------------------------------------------

    public bool PaneShown => _paneWanted;

    /// <summary>ACADSTATUSPANE: shows or hides the pane (the choice is kept for the next start). Returns whether it is shown now.</summary>
    public bool TogglePane()
    {
        _paneWanted = !_paneWanted;
        Log.Safe("save pane setting", () =>
        {
            Directory.CreateDirectory(Log.Folder);
            PaneSetting.Save(Log.SettingsFile, _paneWanted);
        });
        if (!_paneWanted) { CloseDetails(); _pane.Remove(); }
        else Tick();
        return _paneWanted;
    }

    public void StartPaneClicks() => _pane.Clicked += OnPaneClick;

    void OnPaneClick(System.Drawing.Point click, int barTop)
    {
        var open = _details;
        if (open != null && !open.IsDisposed && open.Visible) { _clickGuard.Reset(); open.Close(); return; }
        // The click that made the open window lose focus closed it, and its mouse-down may arrive here afterwards: the same Windows message
        // is the same click, not a new one (ClickGuard compares message identity, not elapsed time).
        if (!_clickGuard.IsNewClick(MessageClock.Id())) return;
        var form = new DetailsForm(DumpText, id => _clickGuard.Deactivated(id));
        _details = form;
        form.FormClosed += (_, _) => { if (ReferenceEquals(_details, form)) _details = null; };
        var now = DateTime.UtcNow;
        form.SetEntries(_merged, _skipped, now);
        form.PlaceAbove(click, barTop);
        AcApp.ShowModelessDialog(MainWindowHandle(), form, false);
    }

    void CloseDetails()
    {
        var d = _details;
        _details = null;
        if (d != null && !d.IsDisposed) Log.Safe("close details", d.Close);
    }

    // ---- the dump ------------------------------------------------------------------------------

    /// <summary>The plain-text dump of all entries and their raw JSON: what ACADSTATUS prints and "Copy for Claude" copies.</summary>
    public string DumpText()
    {
        var now = DateTime.UtcNow;
        Log.Safe("merge for dump", () => Merge(now, true));
        return StatusDump.Full(_dir, _merged, _skipped, now, _paneWanted ? _pane.Note : "hidden (ACADSTATUSPANE)");
    }

    // ---- stop ----------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Safe("stop timer", () => { _timer?.Stop(); _timer?.Dispose(); });
        Log.Safe("write stopped", () =>
        {
            var doc = _builder.BuildStopped(DateTime.UtcNow);
            AtomicFile.Write(Path.Combine(_dir, _builder.FileName), doc.Json, _pid);
        });
        CloseDetails();
        Log.Safe("remove pane", _pane.Dispose);
        Log.Safe("prompt watch stop", _prompts.Dispose);
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
