using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcadStatus.Core;

namespace AcadStatus.Tests;

/// <summary>The whole chain without AutoCAD: one tick per second feeds snapshots to the builder, the write policy writes files, and the
/// reader merges them, as the add-in does. Mirrors the VM checks: alert dialog, prompt, busy loop.</summary>
public class ScenarioTests
{
    static readonly DateTime T0 = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    sealed class Rig : IDisposable
    {
        public readonly TempFolder Dir = new();
        public readonly AcadStatusBuilder Builder = new("AcadStatus 0.1.0", 4812);
        public readonly StatusFiles Files;
        public readonly DialogClock Clock = new();
        public string? LastKey;
        public DateTime? LastWrite;
        public int Writes;
        public string Path => System.IO.Path.Combine(Dir.Path, Builder.FileName);

        public Rig() { Files = new StatusFiles(Dir.Path); }

        public AcadStatusDoc Tick(int sec, int cmdActive = 0, string cmdNames = "", string? prompt = null, DialogSnap? dialog = null)
        {
            var now = T0.AddSeconds(sec);
            var since = Clock.Observe(dialog, now);
            var doc = Builder.Build(TestData.Snap(cmdActive: cmdActive, cmdNames: cmdNames, prompt: prompt, dialog: dialog, dialogSince: since), now);
            if (WritePolicy.ShouldWrite(LastKey, LastWrite, doc.ContentKey, now))
            {
                AtomicFile.Write(Path, doc.Json, 4812);
                LastKey = doc.ContentKey;
                LastWrite = now;
                Writes++;
            }
            return doc;
        }

        public MergeResult Merge(int sec, Func<int, bool>? alive = null) =>
            TestMerge.Merge(Files.Read().Entries, T0.AddSeconds(sec), alive ?? (_ => true));

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public void AnAlertGoesFromWaitingInputToBlockedAndBackToIdle()
    {
        using var rig = new Rig();
        var alert = TestData.Dialog(handle: 0xABC, texts: new[] { "status board test" }, buttons: new[] { "OK" });
        Assert.Equal("idle", rig.Tick(0).Entry.State);
        Assert.Equal("idle", rig.Tick(1).Entry.State);
        var states = new List<string>();
        for (int s = 2; s <= 22; s++) states.Add(rig.Tick(s, dialog: alert).Entry.State);
        Assert.Equal("waiting-input", states[0]);                  // t = 2, dialog just seen
        Assert.Equal("waiting-input", states[14]);                 // t = 16, open for 14 s
        Assert.Equal("blocked", states[15]);                       // t = 17, open for 15 s
        Assert.All(states.Skip(15), s => Assert.Equal("blocked", s));

        var file = rig.Merge(22).Shown.Single();
        Assert.Equal("blocked", file.State);
        Assert.Equal(Tone.Red, CompactText.ToneOf(file));
        Assert.Equal("Blocked 0:20", CompactText.TextOf(file, T0.AddSeconds(22)));   // since = when the dialog opened (t = 2)
        Assert.Equal(new[] { "OK" }, file.Entry.Acad!.Dialog!.Buttons);
        Assert.Equal(new[] { "status board test" }, file.Entry.Acad.Dialog.Texts);

        Assert.Equal("idle", rig.Tick(23).Entry.State);
        var after = rig.Merge(23).Shown.Single();
        Assert.Equal("idle", after.State);
        Assert.Null(after.Entry.Acad!.Dialog);
        Assert.Equal(Tone.Green, CompactText.ToneOf(after));
        Assert.Equal("", CompactText.TextOf(after, T0.AddSeconds(23)));
    }

    [Fact]
    public void ACommandAtAPromptShowsThePrompt()
    {
        using var rig = new Rig();
        rig.Tick(0);
        rig.Tick(1, cmdActive: 1, cmdNames: "LINE", prompt: "Specify first point:");
        var m = rig.Merge(1).Shown.Single();
        Assert.Equal("waiting-input", m.State);
        Assert.Equal("Specify first point:", m.Entry.Acad!.Prompt);
        Assert.Equal("Input: LINE", CompactText.TextOf(m, T0.AddSeconds(1)));
        Assert.Equal(Tone.Amber, CompactText.ToneOf(m));
        rig.Tick(2, cmdActive: 0);
        Assert.Equal("idle", rig.Merge(2).Shown.Single().State);
    }

    [Fact]
    public void ABusyLoopMakesTheFileStaleAndABusyEntryWhileTheProcessLives()
    {
        using var rig = new Rig();
        rig.Tick(0);
        rig.Tick(1);
        // the UI thread is busy from t = 2 to t = 22: no ticks, so no writes
        Assert.False(rig.Merge(9).Shown.Single().Stale);   // ttl is 10 s: a few missed ticks are not yet "no reply"
        var during = rig.Merge(14).Shown.Single();
        Assert.True(during.Stale);
        Assert.Equal("busy", during.State);
        Assert.Equal("No reply 0:14", CompactText.TextOf(during, T0.AddSeconds(14)));   // the last write was at t = 0 (t = 1 was inside the refresh)
        Assert.Equal(Tone.Red, CompactText.ToneOf(during));
        // the process is gone: the entry is dropped
        Assert.Empty(rig.Merge(14, _ => false).Shown);
        // the loop ends and the tick writes again
        rig.Tick(22);
        var after = rig.Merge(22).Shown.Single();
        Assert.False(after.Stale);
        Assert.Equal("idle", after.State);
    }

    [Fact]
    public void ALiveFileIsNeverStaleBetweenWrites()
    {
        using var rig = new Rig();
        for (int s = 0; s < 60; s++)
        {
            rig.Tick(s);
            Assert.False(rig.Merge(s).Shown.Single().Stale, "t=" + s);
        }
    }

    [Fact]
    public void WritesHappenOnChangeAndAtLeastEveryTwoSeconds()
    {
        using var rig = new Rig();
        for (int s = 0; s < 20; s++) rig.Tick(s);   // nothing changes
        Assert.InRange(rig.Writes, 9, 11);          // every 2 s: 10 writes in 20 s
        var before = rig.Writes;
        rig.Tick(20, cmdActive: 1, cmdNames: "REGEN");   // a change: written at once, though only 1 s after the last refresh
        Assert.Equal(before + 1, rig.Writes);
    }

    [Fact]
    public void ABlockedDialogCostsOneWriteEveryTwoSecondsNotOnePerTick()
    {
        using var rig = new Rig();
        var alert = TestData.Dialog();
        rig.Tick(0, dialog: alert);
        var start = rig.Writes;
        for (int s = 1; s <= 40; s++) rig.Tick(s, dialog: alert);
        var writes = rig.Writes - start;
        Assert.InRange(writes, 20, 22);   // one at the waiting-input -> blocked change, the rest the 2 s refresh
    }

    [Fact]
    public void TheTwoWritersOfOneFolderMerge()
    {
        using var rig = new Rig();
        rig.Tick(0, cmdActive: 1, cmdNames: "PLOT");
        File.WriteAllText(System.IO.Path.Combine(rig.Dir.Path, "retrace-4812.json"),
            TestData.Json("retrace", 4812, updated: T0, state: "needs-person", @short: "Needs person", summary: "needs a person: Plot"));
        File.WriteAllText(System.IO.Path.Combine(rig.Dir.Path, "acad-pilot-77.json"),
            TestData.Json("acad-pilot", 77, updated: T0.AddSeconds(-600), ttl: 30, state: "running", summary: "run"));
        var merged = rig.Merge(1, pid => pid != 77);
        Assert.Equal(new[] { "retrace-4812", "acad-4812" }, merged.Shown.Select(m => m.Entry.Id).ToArray());
        Assert.Equal("acad-pilot-77.json", Assert.Single(merged.Ignored).Entry.File);
        var v = CompactText.Build(merged, T0.AddSeconds(1));
        Assert.Equal("Needs person", v.Text);
        Assert.Equal(Tone.Amber, v.Tone);
        Assert.Equal(2, v.Tooltip.Split('\n').Length);
    }

    [Fact]
    public void StoppingWritesStoppedAndThePaneOfAnotherInstanceDropsItAfterItsTtl()
    {
        using var rig = new Rig();
        rig.Tick(0);
        AtomicFile.Write(rig.Path, rig.Builder.BuildStopped(T0.AddSeconds(3)).Json, 4812);
        var soon = rig.Merge(4).Shown.Single();
        Assert.Equal("stopped", soon.State);
        Assert.Equal(Tone.Grey, CompactText.ToneOf(soon));
        // stale and the process (acad.exe of the dying session) still alive: stopped, never busy
        var later = rig.Merge(30).Shown.Single();
        Assert.Equal("stopped", later.State);
        // gone: ignored
        Assert.Empty(rig.Merge(30, _ => false).Shown);
    }
}
