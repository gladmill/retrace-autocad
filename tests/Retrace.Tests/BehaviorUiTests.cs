using System.Text.Json;
using Retrace.Engine;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>The logic behind the behavior-check UI: Settings rows and system-variable edits, the Checkpoints window's rows and ticks, rewriting
/// `checks` in a mark line, reading them back, and recordings.json. Synthetic data only; the WPF windows themselves are not tested here.</summary>
public class BehaviorUiTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("retrace-test-behavior-ui").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    // ---- Settings ----

    [Fact]
    public void Settings_rows_list_every_behavior_and_are_disabled_while_auto_detect_is_on()
    {
        var on = BehaviorSettingsModel.Rows(true, new[] { "layers" });
        Assert.Equal(BehaviorCatalog.All.Select(b => b.Id), on.Select(r => r.Id));
        Assert.All(on, r => { Assert.False(r.Enabled); Assert.False(string.IsNullOrWhiteSpace(r.Description)); });
        Assert.Equal(new[] { "layers" }, on.Where(r => r.Ticked).Select(r => r.Id));

        var off = BehaviorSettingsModel.Rows(false, new[] { "LAYERS", "sysvars", "bogus", "layers" });
        Assert.All(off, r => Assert.True(r.Enabled));
        Assert.Equal(new[] { "layers", "sysvars" }, off.Where(r => r.Ticked).Select(r => r.Id));
        Assert.NotEqual(BehaviorSettingsModel.Explanation(true), BehaviorSettingsModel.Explanation(false));
    }

    [Fact]
    public void Ticks_become_the_setting_in_catalog_order_without_duplicates()
    {
        Assert.Equal(new[] { "layers", "symbols", "sysvars" }, BehaviorSettingsModel.FromTicks(new[] { "sysvars", "SYMBOLS", "layers", "layers", "nope" }));
        Assert.Empty(BehaviorSettingsModel.FromTicks(Array.Empty<string>()));
        var s = new RetraceSettings();
        Assert.Equal(s.CheckBehaviors, BehaviorSettingsModel.FromTicks(BehaviorSettingsModel.Rows(false, s.CheckBehaviors).Where(r => r.Ticked).Select(r => r.Id)));
    }

    [Fact]
    public void Saving_keeps_check_behavior_ids_this_build_does_not_know()
    {
        var existing = new[] { "layers", "Holograms", "views", "  ", "future-thing" };
        var saved = BehaviorSettingsModel.FromTicks(new[] { "sysvars" }, existing);
        Assert.Equal(new[] { "sysvars", "Holograms", "future-thing" }, saved);   // catalog ids follow the ticks; unknown ones stay as they were
        Assert.Equal(new[] { "sysvars" }, BehaviorSettingsModel.FromTicks(new[] { "sysvars" }, null));
        // the rows use the settings' own normalization (case, trim, duplicates)
        Assert.Equal(new[] { "layers", "views" }, BehaviorSettingsModel.Rows(false, new[] { " LAYERS ", "views", "layers", "x" }).Where(r => r.Ticked).Select(r => r.Id));
    }

    [Fact]
    public void Editing_a_clone_leaves_the_original_settings_alone()
    {
        var s = new RetraceSettings();
        var c = s.Clone();
        SysvarListEdit.Add(c.SysvarsAdd, c.SysvarsRemove, "myvar", out _);
        SysvarListEdit.Remove(c.SysvarsAdd, c.SysvarsRemove, "LTSCALE");
        c.CheckBehaviors.Add("views");
        Assert.Empty(s.SysvarsAdd);
        Assert.Empty(s.SysvarsRemove);
        Assert.DoesNotContain("views", s.CheckBehaviors);
    }

    [Fact]
    public void Adding_and_removing_system_variables_keeps_both_lists_minimal()
    {
        var add = new List<string>(); var remove = new List<string>();
        Assert.True(SysvarListEdit.Add(add, remove, " mytest ", out _));
        Assert.Equal(new[] { "MYTEST" }, add);
        Assert.Contains("MYTEST", SysVarDefaults.Effective(add, remove));

        Assert.False(SysvarListEdit.Add(add, remove, "MyTest", out var dup)); Assert.Contains("already", dup);
        Assert.False(SysvarListEdit.Add(add, remove, "LTSCALE", out _));          // a default that is on the list
        Assert.Single(add);

        Assert.True(SysvarListEdit.Remove(add, remove, "ltscale"));              // a default: goes to remove
        Assert.Equal(new[] { "LTSCALE" }, remove);
        Assert.DoesNotContain("LTSCALE", SysVarDefaults.Effective(add, remove));
        Assert.True(SysvarListEdit.Add(add, remove, "LTSCALE", out _));          // back: remove shrinks, add stays as is
        Assert.Empty(remove);
        Assert.Single(add);

        Assert.True(SysvarListEdit.Remove(add, remove, "MYTEST"));               // an addition: just dropped
        Assert.Empty(add); Assert.Empty(remove);
        Assert.False(SysvarListEdit.Remove(add, remove, "MYTEST"));
        Assert.Equal(SysVarDefaults.Default.Length, SysVarDefaults.Effective(add, remove).Count);

        SysvarListEdit.Remove(add, remove, "CLAYER"); SysvarListEdit.Add(add, remove, "X", out _);
        SysvarListEdit.Reset(add, remove);
        Assert.Empty(add); Assert.Empty(remove);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("my var")]
    [InlineData("a;b")]
    [InlineData("é")]
    public void Bad_system_variable_names_are_refused(string text)
    {
        var add = new List<string>(); var remove = new List<string>();
        Assert.False(SysvarListEdit.Add(add, remove, text, out var msg));
        Assert.NotEqual("", msg);
        Assert.Empty(add);
    }

    [Fact]
    public void The_system_variable_summary_counts_changes()
    {
        Assert.Contains("default list", SysvarListEdit.Summary(null, null));
        var s = SysvarListEdit.Summary(new[] { "MYVAR", "LTSCALE" }, new[] { "CLAYER", "NOTADEFAULT" });
        Assert.Contains("1 added", s); Assert.Contains("1 removed", s);
    }

    // ---- Checkpoints window rows and ticks ----

    static BehaviorSnapshot Snap() => new BehaviorSnapshot().Also(s =>
    {
        s.Set("layers", new Dictionary<string, string> { ["0"] = "on=1", ["DIM"] = "on=0" });
        s.Set("sysvars", new Dictionary<string, string> { ["CLAYER"] = "DIM" });
    });

    static BehaviorChecks AutoLayers()
    {
        var c = new BehaviorChecks { Mode = BehaviorChecks.ModeAuto };
        c.Behaviors["layers"] = new BehaviorCheck { Source = "auto", Scope = "items", Items = { ["DIM"] = "on=0" } };
        return c;
    }

    static CheckpointChecksEditor Editor(BehaviorChecks c, BehaviorSnapshot? snap, Action? onLoad = null) =>
        new(c, () => { onLoad?.Invoke(); return snap; });

    [Fact]
    public void Rows_show_verified_behaviors_with_their_source_and_disable_those_without_stored_data()
    {
        var rows = Editor(AutoLayers(), Snap()).Rows();
        Assert.Equal(BehaviorCatalog.All.Count, rows.Count);
        var layers = rows.Single(r => r.Id == "layers");
        Assert.True(layers.Ticked); Assert.True(layers.Enabled); Assert.Equal("auto", layers.Source);
        var sys = rows.Single(r => r.Id == "sysvars");
        Assert.False(sys.Ticked); Assert.True(sys.Enabled); Assert.Null(sys.Source);
        var views = rows.Single(r => r.Id == "views");   // not in the snapshot: cannot be added
        Assert.False(views.Ticked); Assert.False(views.Enabled); Assert.Equal(CheckpointChecksEditor.NoSnapshotTip, views.Tooltip);

        var noFile = Editor(AutoLayers(), null).Rows();   // no snapshot file at all
        Assert.True(noFile.Single(r => r.Id == "layers").Enabled);       // a verified one can still be taken off
        Assert.All(noFile.Where(r => !r.Ticked), r => { Assert.False(r.Enabled); Assert.NotNull(r.Tooltip); });
    }

    [Fact]
    public void The_snapshot_is_read_only_when_first_needed_and_once()
    {
        var loads = 0;
        var ed = Editor(AutoLayers(), Snap(), () => loads++);
        Assert.Equal(0, loads);
        _ = ed.Summary(); _ = ed.Edits(); Assert.True(ed.Tick("layers", false));   // unticking needs no snapshot
        Assert.Equal(0, loads);
        ed.Rows(); ed.Rows(); ed.Tick("sysvars", true);
        Assert.Equal(1, loads);
    }

    [Fact]
    public void A_behavior_from_a_newer_recording_stays_listed_after_untick_and_can_be_ticked_again()
    {
        var c = AutoLayers();
        var h = new BehaviorCheck { Source = "manual", Scope = "all" };
        c.Behaviors["holograms"] = h;
        var ed = Editor(c, null);
        Assert.True(ed.Rows().Single(r => r.Id == "holograms").Ticked);
        Assert.True(ed.Tick("holograms", false));
        var after = ed.Rows().Single(r => r.Id == "holograms");
        Assert.False(after.Ticked); Assert.True(after.Enabled);
        Assert.DoesNotContain("holograms", ed.Current().Behaviors.Keys);
        Assert.True(ed.Dirty);
        Assert.True(ed.Tick("holograms", true));
        Assert.Same(h, ed.Current().Behaviors["holograms"]);
        Assert.False(ed.Dirty);
        Assert.Empty(ed.Edits());
    }

    [Fact]
    public void Ticking_adds_a_new_behavior_as_manual_all_and_reticking_an_original_restores_it_exactly()
    {
        var c = AutoLayers();
        var ed = Editor(c, Snap());
        Assert.True(ed.Tick("sysvars", true));
        var sys = ed.Current().Behaviors["sysvars"];
        Assert.Equal(("manual", "all"), (sys.Source, sys.Scope));
        Assert.Equal("DIM", sys.Items["CLAYER"]);
        Assert.True(ed.Dirty);
        Assert.False(c.Behaviors.ContainsKey("sysvars"));                 // the input is not changed

        Assert.True(ed.Tick("layers", false));                            // untick the auto one, then tick it again
        Assert.True(ed.Tick("layers", true));
        var layers = ed.Current().Behaviors["layers"];
        Assert.Equal(("auto", "items"), (layers.Source, layers.Scope));   // restored, not manual / all
        Assert.Equal(new[] { "DIM" }, layers.Items.Keys);
        Assert.Equal(new[] { "sysvars" }, ed.Edits().Keys);               // only the new one is an edit

        Assert.False(ed.Tick("views", true));                             // not in the snapshot: refused, unchanged
        Assert.DoesNotContain("views", ed.Current().Behaviors.Keys);
        Assert.False(Editor(c, null).Tick("sysvars", true));              // no snapshot at all

        ed.Tick("sysvars", false);
        Assert.False(ed.Dirty);                                           // ticked and unticked again: no net change
        Assert.Contains("Layer state (auto)", ed.Summary());
        ed.Tick("layers", false);
        Assert.Equal("none besides the entities", ed.Summary());
    }

    [Fact]
    public void After_a_save_the_sent_edits_are_the_new_baseline()
    {
        var ed = Editor(AutoLayers(), Snap());
        ed.Tick("sysvars", true); ed.Tick("layers", false);
        var sent = ed.Edits();
        ed.Tick("symbols", false);   // not verified, no-op
        ed.MarkSaved(sent);
        Assert.False(ed.Dirty);
        ed.Tick("layers", true);     // the baseline no longer has it: comes back as manual from the snapshot
        Assert.Equal("manual", ed.Current().Behaviors["layers"].Source);
    }

    // ---- rewriting the mark line / reading back ----

    const string Header = """{"type":"header","schema":3,"name":"checks test","started":"2026-10-26T19:40:00+00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""";

    string Recording(bool withChecks = true)
    {
        var dir = Path.Combine(_root, "2026-10-26_1940_checks-test");
        Directory.CreateDirectory(dir);
        var checks = withChecks
            ? ""","checks":{"mode":"auto","behaviors":{"layers":{"source":"auto","scope":"items","items":{"DIM":"on=0","TMP":null}},"symbols":{"source":"manual","scope":"weird","future":7,"items":{"STYLE:A":"x"}},"holograms":{"source":"manual","scope":"all","items":{}}}}"""
            : "";
        var lines = new[]
        {
            Header,
            """{"t":5,"n":1,"type":"state","state":{"count":0,"byType":{},"byLayer":{},"layers":["0"],"hash":"e3","v":2}}""",
            """{"t":100,"n":2,"type":"cmd_start","command":"LAYER","origin":"typed/other"}""",
            """{"t":200,"n":3,"type":"cmd_end","command":"LAYER"}""",
            """{"t":300,"n":4,"type":"mark","note":"","autoName":"checkpoint1_cmd_layer","state":{"count":0,"byType":{},"byLayer":{},"layers":["0","DIM"],"hash":"aa","v":2}""" + checks + "}",
            """{"t":400,"n":5,"type":"mark","note":"","autoName":"checkpoint2","state":{"count":0,"byType":{},"byLayer":{},"layers":["0","DIM"],"hash":"aa","v":2}}""",
            """{"t":500,"n":6,"type":"session_end"}""",
        };
        var rec = Path.Combine(dir, "recording.jsonl");
        File.WriteAllText(rec, string.Join("\n", lines) + "\n");
        return rec;
    }

    [Fact]
    public void Read_returns_the_checks_and_the_full_snapshot_path_without_reading_the_file()
    {
        var rec = Recording();
        var dir = Path.GetDirectoryName(rec)!;
        var list = Checkpoints.Read(rec);
        Assert.NotNull(list[0].Checks);
        Assert.Equal(BehaviorSnapshot.MarkPath(dir, 4), list[0].Snapshot);
        Assert.True(Path.IsPathRooted(list[0].Snapshot));
        Assert.False(File.Exists(list[0].Snapshot));                       // the path is given either way
        Assert.Null(list[1].Checks);                                       // the second mark has no checks
        Assert.Null(list[1].Snapshot);
    }

    static CheckEdits Edits(params (string Id, BehaviorCheck? Check)[] e)
    {
        var d = new CheckEdits();
        foreach (var (id, c) in e) d[id] = c;
        return d;
    }

    [Fact]
    public void Update_rewrites_only_the_touched_behaviors_and_keeps_everything_else_as_written()
    {
        var rec = Recording();
        var before = File.ReadAllLines(rec);
        var sys = new BehaviorChecks().WithManual("sysvars", Snap()).Behaviors["sysvars"];
        Assert.Equal(1, Checkpoints.Update(rec, null, new Dictionary<long, CheckEdits> { [4] = Edits(("sysvars", sys), ("layers", null)) }));
        var after = File.ReadAllLines(rec);
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
            if (i != 4) Assert.Equal(before[i], after[i]);
        Assert.Contains("\"hash\":\"aa\"", after[4]);                      // the digest stays
        Assert.False(File.Exists(rec + ".tmp"));
        // untouched behaviors: exact original JSON, unknown scope and unknown field included
        Assert.Contains("""\"symbols\":{\"source\":\"manual\",\"scope\":\"weird\",\"future\":7,\"items\":{\"STYLE:A\":\"x\"}}""".Replace("\\\"", "\""), after[4]);
        Assert.Contains("\"holograms\":{\"source\":\"manual\",\"scope\":\"all\",\"items\":{}}", after[4]);
        Assert.DoesNotContain("\"layers\"", after[4].Substring(after[4].IndexOf("\"checks\"", StringComparison.Ordinal)));

        using var doc = JsonDocument.Parse(after[4]);
        var bs = doc.RootElement.GetProperty("checks").GetProperty("behaviors");
        Assert.Equal("weird", bs.GetProperty("symbols").GetProperty("scope").GetString());
        Assert.Equal(7, bs.GetProperty("symbols").GetProperty("future").GetInt32());
        Assert.Equal("manual", bs.GetProperty("sysvars").GetProperty("source").GetString());
        Assert.Equal("DIM", bs.GetProperty("sysvars").GetProperty("items").GetProperty("CLAYER").GetString());
        Assert.False(bs.TryGetProperty("layers", out _));
    }

    [Fact]
    public void Names_and_check_edits_are_saved_together_and_removing_everything_keeps_an_empty_checks_value()
    {
        var rec = Recording();
        var all = Checkpoints.Read(rec)[0].Checks!.Behaviors.Keys.ToDictionary(k => k, k => (BehaviorCheck?)null);
        var edits = new CheckEdits(); foreach (var kv in all) edits[kv.Key] = kv.Value;
        Assert.Equal(2, Checkpoints.Update(rec, new Dictionary<long, string> { [5] = "Second" }, new Dictionary<long, CheckEdits> { [4] = edits }));
        var list = Checkpoints.Read(rec);
        Assert.Equal("Second", list[1].Name);
        Assert.NotNull(list[0].Checks);
        Assert.True(list[0].Checks!.IsEmpty);
    }

    [Fact]
    public void Update_with_no_net_change_writes_nothing_and_a_wrong_event_changes_nothing()
    {
        var rec = Recording();
        var bytes = File.ReadAllBytes(rec);
        Assert.Equal(0, Checkpoints.Update(rec, null, new Dictionary<long, CheckEdits> { [4] = new CheckEdits() }));
        Assert.Equal(0, Checkpoints.Update(rec, null, new Dictionary<long, CheckEdits> { [4] = Edits(("views", null)) }));   // removing what is not there
        Assert.Equal(bytes, File.ReadAllBytes(rec));
        var ex = Assert.Throws<InvalidDataException>(() => Checkpoints.Update(rec, null, new Dictionary<long, CheckEdits> { [4] = Edits(("layers", null)), [3] = new CheckEdits() }));
        Assert.Contains("3", ex.Message);
        Assert.Equal(bytes, File.ReadAllBytes(rec));
    }

    [Fact]
    public void RenameCheckpoints_with_check_edits_saves_them_and_writes_the_steps_again_without_mentioning_them()
    {
        var rec = Recording();
        var r = EngineRunner.RenameCheckpoints(rec, new Dictionary<long, string>(), false, false, new Dictionary<long, CheckEdits> { [4] = Edits(("layers", null)) });
        Assert.True(r.Success, r.Error);
        Assert.DoesNotContain("layers", Checkpoints.Read(rec)[0].Checks!.Behaviors.Keys);
        var steps = File.ReadAllText(Path.Combine(Path.GetDirectoryName(rec)!, "steps.md"));
        Assert.DoesNotContain("behavior", steps, StringComparison.OrdinalIgnoreCase);
    }

    // ---- recordings.json ----

    [Fact]
    public void Recordings_json_gets_the_checks_per_checkpoint_with_counts_and_null_for_older_marks()
    {
        var rec = Recording();
        var outDir = Path.Combine(_root, "out");
        Assert.True(EngineRunner.AnalyzeRecordings([rec], outDir).Success);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "data", "recordings.json")));
        var cps = json.RootElement.GetProperty("sessions")[0].GetProperty("checkpoints");
        Assert.Equal(2, cps.GetArrayLength());
        var checks = cps[0].GetProperty("checks");
        Assert.Equal("auto", checks.GetProperty("mode").GetString());
        var layers = checks.GetProperty("behaviors").GetProperty("layers");
        Assert.Equal("auto", layers.GetProperty("source").GetString());
        Assert.Equal("items", layers.GetProperty("scope").GetString());
        Assert.Equal(2, layers.GetProperty("items").GetInt32());
        Assert.Equal(1, layers.GetProperty("absent").GetInt32());
        Assert.False(cps[1].TryGetProperty("checks", out var none) && none.ValueKind != JsonValueKind.Null);   // left out (or null) for a mark without checks
    }

    [Fact]
    public void A_recording_without_checks_reads_as_before()
    {
        var rec = Recording(withChecks: false);
        Assert.All(Checkpoints.Read(rec), c => { Assert.Null(c.Checks); Assert.Null(c.Snapshot); });
        Assert.Equal("No behavior checks (older recording).", CheckpointChecksEditor.NoChecksText);
    }
}

static class SnapshotExt
{
    public static BehaviorSnapshot Also(this BehaviorSnapshot s, Action<BehaviorSnapshot> f) { f(s); return s; }
}
