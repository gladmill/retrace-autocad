using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Behavior checks (schema 3): canonical values, snapshot, diff, picking, compare, storage, plan, settings. Synthetic data only.</summary>
public class BehaviorChecksTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-behavior").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    static BehaviorSnapshot Snap(params (string Id, (string K, string V)[] Items)[] bs)
    {
        var s = new BehaviorSnapshot();
        foreach (var (id, items) in bs) s.Set(id, items.Select(i => new KeyValuePair<string, string>(i.K, i.V)));
        return s;
    }

    static string Layer(string on = "1", string frozen = "0") => CanonicalValue.Join(("on", on), ("frozen", frozen), ("color", "7"));

    // ---- canonical values ----

    [Theory]
    [InlineData("plain")]
    [InlineData("a;b")]
    [InlineData("a=b")]
    [InlineData(@"back\slash")]
    [InlineData(@"\;=\\;;==")]
    [InlineData("")]
    public void Canonical_values_round_trip_any_text(string text)
    {
        var joined = CanonicalValue.Join(("na;me=x", text), ("second", text + "!"));
        Assert.True(CanonicalValue.TryParse(joined, out var f));
        Assert.Equal(new[] { ("na;me=x", text), ("second", text + "!") }, f!.ToArray());
    }

    [Fact]
    public void Canonical_format_and_non_canonical_text()
    {
        Assert.Equal("on=1;frozen=0", CanonicalValue.Join(("on", "1"), ("frozen", "0")));
        Assert.Equal(@"a\;b=c\=d", CanonicalValue.Join(("a;b", "c=d")));
        Assert.True(CanonicalValue.TryParse("", out var none)); Assert.Empty(none!);
        Assert.False(CanonicalValue.TryParse("DIM", out _));
        Assert.False(CanonicalValue.TryParse("a=b;c", out _));
        Assert.False(CanonicalValue.TryParse("a=b=c", out _));
        Assert.False(CanonicalValue.TryParse(@"a=b\", out _));
    }

    // ---- diff ----

    [Fact]
    public void Diff_reports_added_removed_changed_and_skips_behaviors_missing_on_one_side()
    {
        var prev = Snap(("layers", new[] { ("0", "a"), ("OLD", "x"), ("SAME", "s"), ("CH", "1") }), ("sysvars", new[] { ("CLAYER", "0") }), ("views", new[] { ("V", "v") }));
        var cur = Snap(("layers", new[] { ("0", "a"), ("NEW", "n"), ("SAME", "s"), ("CH", "2") }), ("sysvars", new[] { ("CLAYER", "0") }), ("xdata", new[] { ("k", "v") }));
        var d = BehaviorSnapshot.Diff(prev, cur);
        Assert.Equal(new[] { "layers", "sysvars" }, d.Keys);   // views only before, xdata only now: not captured on one side
        Assert.Equal(new[] { "NEW" }, d["layers"].Added);
        Assert.Equal(new[] { "OLD" }, d["layers"].Removed);
        Assert.Equal(new[] { "CH" }, d["layers"].Changed);
        Assert.True(d["sysvars"].IsEmpty);
    }

    // ---- picking ----

    [Fact]
    public void Auto_pick_takes_changes_with_current_values_and_removed_as_null()
    {
        var prev = Snap(("layers", new[] { ("OLD", "x"), ("CH", "1"), ("SAME", "s") }), ("sysvars", new[] { ("CLAYER", "0") }));
        var cur = Snap(("layers", new[] { ("NEW", "n"), ("CH", "2"), ("SAME", "s") }), ("sysvars", new[] { ("CLAYER", "0") }));
        var c = BehaviorChecks.AutoPick(prev, cur);
        Assert.Equal("auto", c.Mode);
        Assert.Equal(new[] { "layers" }, c.Behaviors.Keys);   // unchanged sysvars: no check
        var l = c.Behaviors["layers"];
        Assert.Equal("auto", l.Source); Assert.Equal("items", l.Scope);
        Assert.Equal(new KeyValuePair<string, string?>[] { new("CH", "2"), new("NEW", "n"), new("OLD", null) }, l.Items.ToArray());
        Assert.True(BehaviorChecks.AutoPick(null, cur).IsEmpty);
    }

    [Fact]
    public void Manual_pick_checks_the_whole_behavior()
    {
        var cur = Snap(("layers", new[] { ("0", "a"), ("DIM", "b") }), ("sysvars", new[] { ("CLAYER", "0") }));
        var c = BehaviorChecks.ManualPick(new[] { "layers", "layers", "nonsense", "xdata" }, cur);
        Assert.Equal("manual", c.Mode);
        Assert.Equal(new[] { "layers" }, c.Behaviors.Keys);   // duplicate, unknown id and not-captured behavior dropped
        Assert.Equal("manual", c.Behaviors["layers"].Source); Assert.Equal("all", c.Behaviors["layers"].Scope);
        Assert.Equal(new[] { "0", "DIM" }, c.Behaviors["layers"].Items.Keys);
    }

    [Fact]
    public void Override_adds_a_manual_behavior_from_the_stored_snapshot_and_removes_one()
    {
        var stored = Snap(("layers", new[] { ("0", "a") }), ("sysvars", new[] { ("CLAYER", "0") }));
        var auto = BehaviorChecks.AutoPick(Snap(("layers", new[] { ("0", "z") })), stored);
        var added = auto.WithManual("sysvars", stored);
        Assert.Equal(new[] { "layers", "sysvars" }, added.Behaviors.Keys);
        Assert.Equal("manual", added.Behaviors["sysvars"].Source); Assert.Equal("all", added.Behaviors["sysvars"].Scope);
        Assert.Equal("auto", added.Behaviors["layers"].Source);
        Assert.Equal(new[] { "layers" }, auto.Behaviors.Keys);   // original untouched
        var replaced = auto.WithManual("layers", stored);
        Assert.Equal("all", replaced.Behaviors["layers"].Scope);
        Assert.Equal(new[] { "sysvars" }, added.Without("layers").Behaviors.Keys);
        Assert.Equal(added.Behaviors.Keys, added.WithManual("views", stored).Behaviors.Keys);   // never captured: unchanged
    }

    // ---- compare ----

    static BehaviorChecks Items(string id, params (string K, string? V)[] items)
    {
        var c = new BehaviorChecks();
        var b = new BehaviorCheck { Scope = "items" };
        foreach (var (k, v) in items) b.Items[k] = v;
        c.Behaviors[id] = b;
        return c;
    }

    [Fact]
    public void Compare_items_scope_names_fields_and_plain_values()
    {
        var actual = Snap(("layers", new[] { ("DIM", Layer(frozen: "0")), ("EXTRA", "x") }), ("sysvars", new[] { ("CLAYER", "0") }),
            ("layouts", new[] { ("Sheet1", "order=1;count=5;hash=h") }), ("xdata", new[] { ("e1|ACAD", "d") }));
        var checks = Items("layers", ("DIM", Layer(frozen: "1")), ("GONE", Layer()), ("EXTRA", null));
        checks.Behaviors["sysvars"] = Items("sysvars", ("CLAYER", "DIM")).Behaviors["sysvars"];
        checks.Behaviors["layouts"] = Items("layouts", ("Sheet1", "order=1;count=4;hash=h")).Behaviors["layouts"];
        checks.Behaviors["xdata"] = Items("xdata", ("e1|ACAD", "d"), ("e2|ACAD", "d")).Behaviors["xdata"];
        Assert.Equal(new[]
        {
            "layer DIM frozen: expected 1, got 0", "layer EXTRA unexpected", "layer GONE missing",
            "layout Sheet1 count: expected 4, got 5",
            "sysvar CLAYER: expected DIM, got 0",
            "xdata e2|ACAD missing",
        }, BehaviorChecks.Compare(checks, actual));
        Assert.Empty(BehaviorChecks.Compare(Items("layers", ("DIM", Layer(frozen: "0"))), actual));   // items scope ignores other items
    }

    [Fact]
    public void Compare_all_scope_fails_on_missing_unexpected_and_changed()
    {
        var stored = Snap(("symbols", new[] { ("LTYPE:DASHED", "a=1"), ("STYLE:Standard", "f=x"), ("STYLE:Notes", "f=y") }));
        var checks = BehaviorChecks.ManualPick(new[] { "symbols" }, stored);
        Assert.Empty(BehaviorChecks.Compare(checks, stored));
        var actual = Snap(("symbols", new[] { ("LTYPE:DASHED", "a=2"), ("STYLE:Standard", "f=x"), ("STYLE:Other", "f=z") }));
        Assert.Equal(new[] { "symbols LTYPE:DASHED a: expected 1, got 2", "symbols STYLE:Notes missing", "symbols STYLE:Other unexpected" },
            BehaviorChecks.Compare(checks, actual));
    }

    [Fact]
    public void Compare_handles_field_set_changes_and_non_canonical_values()
    {
        var checks = Items("symbols", ("S|1", "a=1;b=2"), ("S|2", "plain"));
        var actual = Snap(("symbols", new[] { ("S|1", "a=1;c=3"), ("S|2", "other") }));
        Assert.Equal(new[] { "symbols S|1 b: expected 2, got (none)", "symbols S|1 c: expected (none), got 3", "symbols S|2: expected plain, got other" },
            BehaviorChecks.Compare(checks, actual));
    }

    [Fact]
    public void Compare_old_recordings_pass_on_an_unchanged_drawing()
    {
        // a 0.3.0 viewport value: `on` was read through the session-dependent Viewport.On (0 on a layout not yet current) and there is no `off`.
        // The new capture has `off` and no `on`. The old, meaningless on=0 must be ignored, and `off` (new) is not checked against the old recording.
        var old = Items("viewports", ("A|1", "width=3;on=0;locked=0"));
        Assert.Empty(BehaviorChecks.Compare(old, Snap(("viewports", new[] { ("A|1", "width=3;off=0;locked=0") }))));
        Assert.Empty(BehaviorChecks.Compare(old, Snap(("viewports", new[] { ("A|1", "width=3;off=1;locked=0") }))));   // off is not in the old recording: not checked
        // an old layout value without psltscale
        var oldLayout = Items("layouts", ("A", "tab=1;count=2"));
        Assert.Empty(BehaviorChecks.Compare(oldLayout, Snap(("layouts", new[] { ("A", "tab=1;count=2;psltscale=1") }))));
        // manual mode (scope all) is the same
        var all = BehaviorChecks.ManualPick(new[] { "layouts", "viewports" }, Snap(("layouts", new[] { ("A", "tab=1;count=2") }), ("viewports", new[] { ("A|1", "width=3;on=0;locked=0") })));
        Assert.Empty(BehaviorChecks.Compare(all, Snap(("layouts", new[] { ("A", "tab=1;count=2;psltscale=1") }), ("viewports", new[] { ("A|1", "width=3;off=0;locked=0") }))));
        // but what else differs is still reported, with the tolerated fields left out of the lines
        Assert.Equal(new[] { "viewport A|1 width: expected 3, got 4" },
            BehaviorChecks.Compare(old, Snap(("viewports", new[] { ("A|1", "width=4;off=0;locked=0") }))));
        Assert.Equal(new[] { "layout A count: expected 2, got 3" },
            BehaviorChecks.Compare(oldLayout, Snap(("layouts", new[] { ("A", "tab=1;count=3;psltscale=0") }))));
    }

    [Fact]
    public void Compare_new_fields_are_compared_when_recorded_and_never_pass_quietly_when_unreadable()
    {
        var rec = Items("viewports", ("A|1", "width=3;off=0;locked=0"));
        Assert.Equal(new[] { "viewport A|1 off: expected 0, got 1" },
            BehaviorChecks.Compare(rec, Snap(("viewports", new[] { ("A|1", "width=3;off=1;locked=0") }))));
        // entget unavailable now: the field is missing from the capture
        Assert.Equal(new[] { "viewport A|1 off: could not be read" },
            BehaviorChecks.Compare(rec, Snap(("viewports", new[] { ("A|1", "width=3;locked=0") }))));
        var lay = Items("layouts", ("A", "tab=1;psltscale=0"));
        Assert.Equal(new[] { "layout A psltscale: expected 0, got 1" },
            BehaviorChecks.Compare(lay, Snap(("layouts", new[] { ("A", "tab=1;psltscale=1") }))));
        Assert.Equal(new[] { "layout A psltscale: could not be read" },
            BehaviorChecks.Compare(lay, Snap(("layouts", new[] { ("A", "tab=1") }))));
    }

    [Fact]
    public void Compare_tolerance_is_limited_to_the_named_fields()
    {
        // any other field missing on either side still shows (none), in layouts and viewports too
        Assert.Equal(new[] { "viewport A|1 locked: expected 0, got (none)" },
            BehaviorChecks.Compare(Items("viewports", ("A|1", "width=3;locked=0")), Snap(("viewports", new[] { ("A|1", "width=3") }))));
        Assert.Equal(new[] { "viewport A|1 extra: expected (none), got 1" },
            BehaviorChecks.Compare(Items("viewports", ("A|1", "width=3")), Snap(("viewports", new[] { ("A|1", "width=3;extra=1") }))));
        Assert.Equal(new[] { "layout A hash: expected (none), got x" },
            BehaviorChecks.Compare(Items("layouts", ("A", "tab=1")), Snap(("layouts", new[] { ("A", "tab=1;hash=x") }))));
        // `on` is only dropped for viewports, `off` / `psltscale` only where they belong
        Assert.Equal(new[] { "layout A on: expected 1, got (none)" },
            BehaviorChecks.Compare(Items("layouts", ("A", "tab=1;on=1")), Snap(("layouts", new[] { ("A", "tab=1") }))));
    }

    [Fact]
    public void Dxf_groups_give_the_saved_viewport_and_layout_flags()
    {
        // what entget returns for a layout: the first 70 (plot settings) must not be taken
        var layout = new (int, object?)[] { (0, "LAYOUT"), (100, "AcDbPlotSettings"), (1, ""), (70, (short)688), (100, "AcDbLayout"), (1, "A-101"), (70, (short)1), (71, (short)2) };
        Assert.Equal(1, DxfGroups.LayoutFlags(layout));
        Assert.True(DxfGroups.LayoutPsltscale(1));
        Assert.False(DxfGroups.LayoutPsltscale(0));
        Assert.True(DxfGroups.LayoutPsltscale(3));   // bit 2 is LIMCHECK
        Assert.False(DxfGroups.LayoutPsltscale(2));
        Assert.Null(DxfGroups.LayoutFlags(new (int, object?)[] { (0, "LAYOUT"), (100, "AcDbPlotSettings"), (70, (short)688) }));

        var vp = new (int, object?)[] { (0, "VIEWPORT"), (100, "AcDbEntity"), (90, 5), (100, "AcDbViewport"), (68, (short)0), (69, (short)3), (90, 950304), (1, "") };
        Assert.Equal(950304, DxfGroups.ViewportFlags(vp));   // not the 90 of another subclass
        Assert.True(DxfGroups.ViewportOff(950304));          // 0x20000 set
        Assert.False(DxfGroups.ViewportOff(819232));
        Assert.False(DxfGroups.ViewportOff(835616));
        Assert.Null(DxfGroups.ViewportFlags(new (int, object?)[] { (0, "VIEWPORT"), (100, "AcDbViewport") }));
    }

    [Fact]
    public void Compare_reports_a_behavior_that_could_not_be_read_once()
    {
        var checks = Items("xdata", ("a", "1"), ("b", "2"));
        Assert.Equal(new[] { "XData: could not be read" }, BehaviorChecks.Compare(checks, Snap(("layers", new[] { ("0", "x") }))));
    }

    // ---- JSON and files ----

    [Fact]
    public void Checks_json_round_trips_in_ordinal_order_with_null_items()
    {
        var c = BehaviorChecks.AutoPick(Snap(("layers", new[] { ("b", "1"), ("a", "1"), ("Z", "1") })), Snap(("layers", new[] { ("b", "2"), ("a", "1") })));
        var json = c.ToJson();
        Assert.Equal("""{"mode":"auto","behaviors":{"layers":{"source":"auto","scope":"items","items":{"Z":null,"b":"2"}}}}""", json);
        var back = BehaviorChecks.FromJson(json)!;
        Assert.Equal(json, back.ToJson());
        Assert.Null(back.Behaviors["layers"].Items["Z"]);
    }

    [Fact]
    public void Checks_reading_is_tolerant()
    {
        Assert.Null(BehaviorChecks.FromJson("[]"));
        Assert.Null(BehaviorChecks.FromJson("not json"));
        var c = BehaviorChecks.FromJson("""{"mode":"x","behaviors":{"layers":7,"views":{"scope":"zzz","items":{"a":"1","b":5,"c":null}}}}""")!;
        Assert.Equal("auto", c.Mode);
        Assert.Equal(new[] { "views" }, c.Behaviors.Keys);
        Assert.Equal("items", c.Behaviors["views"].Scope);
        Assert.Equal(new[] { "a", "c" }, c.Behaviors["views"].Items.Keys);
    }

    [Fact]
    public void Snapshot_json_round_trips_sorted_and_files_are_written_beside_the_recording()
    {
        var s = Snap(("sysvars", new[] { ("b", "2"), ("A", "1"), ("a", "3") }), ("layers", new[] { ("0", "x=1") }));
        Assert.Equal("""{"v":1,"behaviors":{"layers":{"0":"x=1"},"sysvars":{"A":"1","a":"3","b":"2"}}}""", s.ToJson());
        Assert.Equal(s.ToJson(), BehaviorSnapshot.FromJson(s.ToJson())!.ToJson());

        Assert.Equal(Path.Combine(_dir, "snapshots", "start.json"), BehaviorSnapshot.StartPath(_dir));
        Assert.Equal(Path.Combine(_dir, "snapshots", "mark-12.json"), BehaviorSnapshot.MarkPath(_dir, 12));
        Assert.True(s.Write(BehaviorSnapshot.MarkPath(_dir, 12), out var err), err);
        Assert.Equal(s.ToJson(), BehaviorSnapshot.Read(BehaviorSnapshot.MarkPath(_dir, 12))!.ToJson());
        Assert.Null(BehaviorSnapshot.Read(BehaviorSnapshot.MarkPath(_dir, 99)));
        File.WriteAllText(BehaviorSnapshot.StartPath(_dir), "{broken");
        Assert.Null(BehaviorSnapshot.Read(BehaviorSnapshot.StartPath(_dir)));
    }

    // ---- plan and result ----

    static List<Step> Build(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    [Fact]
    public void Plan_parses_mark_checks_and_old_marks_without_them()
    {
        var steps = Build(
            """{"t":1,"n":4,"type":"mark","note":"new","checks":{"mode":"auto","behaviors":{"layers":{"source":"auto","scope":"items","items":{"DIM":"on=1","OLD":null}}}}}""",
            """{"t":2,"n":5,"type":"mark","note":"old"}""");
        Assert.Equal(4, steps[0].EventN);
        Assert.Equal(new[] { "DIM", "OLD" }, steps[0].Checks!.Behaviors["layers"].Items.Keys);
        Assert.Null(steps[1].Checks);
    }

    [Fact]
    public void Checkpoint_result_carries_check_differences_and_old_json_is_unchanged()
    {
        var plain = new CheckpointResult { Index = 1, Pass = true };
        Assert.DoesNotContain("check", JsonSerializer.Serialize(plain));
        plain.ApplyChecks(null, new BehaviorSnapshot());
        Assert.True(plain.Pass);

        var r = new CheckpointResult { Index = 1, Pass = true };
        r.ApplyChecks(Items("sysvars", ("CLAYER", "DIM")), Snap(("sysvars", new[] { ("CLAYER", "0") })));
        Assert.False(r.Pass);
        Assert.Equal(new[] { "sysvar CLAYER: expected DIM, got 0" }, r.CheckDifferences);
        Assert.Equal("auto", r.CheckMode);
        Assert.Contains("\"checkDifferences\"", JsonSerializer.Serialize(r));

        var ok = new CheckpointResult { Pass = true };
        ok.ApplyChecks(Items("sysvars", ("CLAYER", "0")), Snap(("sysvars", new[] { ("CLAYER", "0") })));
        Assert.True(ok.Pass); Assert.Empty(ok.CheckDifferences!);
    }

    [Fact]
    public void Empty_checks_verify_nothing_and_keep_a_null_pass()
    {
        var notVerified = new CheckpointResult { Index = 1 };   // Pass null: the mark has no digest
        notVerified.ApplyChecks(new BehaviorChecks(), new BehaviorSnapshot());
        Assert.Null(notVerified.Pass);
        Assert.Empty(notVerified.CheckedBehaviors!);
        var digestFailed = new CheckpointResult { Pass = false };
        digestFailed.ApplyChecks(new BehaviorChecks(), new BehaviorSnapshot());
        Assert.False(digestFailed.Pass);
        var real = new CheckpointResult();
        real.ApplyChecks(Items("sysvars", ("CLAYER", "0")), Snap(("sysvars", new[] { ("CLAYER", "0") })));
        Assert.True(real.Pass);   // non-empty checks that match verify a mark without a digest
    }

    [Fact]
    public void Sysvars_compare_only_names_recorded_and_still_read()
    {
        var stored = Snap(("sysvars", new[] { ("CLAYER", "0"), ("LTSCALE", "1"), ("OLDVAR", "5") }));
        var checks = BehaviorChecks.ManualPick(new[] { "sysvars" }, stored);
        Assert.Equal("all", checks.Behaviors["sysvars"].Scope);
        // LTSCALE no longer in the user's list, OLDVAR unknown here, NEWVAR is new: none of that is a difference
        var actual = Snap(("sysvars", new[] { ("CLAYER", "0"), ("NEWVAR", "9") }));
        Assert.Empty(BehaviorChecks.Compare(checks, actual));
        // a changed value still fails
        var changed = Snap(("sysvars", new[] { ("CLAYER", "DIM"), ("NEWVAR", "9") }));
        Assert.Equal(new[] { "sysvar CLAYER: expected 0, got DIM" }, BehaviorChecks.Compare(checks, changed));
        // other behaviors keep the strict all-scope rules
        var syms = BehaviorChecks.ManualPick(new[] { "symbols" }, Snap(("symbols", new[] { ("STYLE:A", "") })));
        Assert.Equal(new[] { "symbols STYLE:A missing", "symbols STYLE:B unexpected" }, BehaviorChecks.Compare(syms, Snap(("symbols", new[] { ("STYLE:B", "") }))));
    }

    // ---- settings ----

    [Fact]
    public void Settings_defaults_and_effective_lists()
    {
        var s = new RetraceSettings();
        Assert.True(s.AutoDetectBehavior);
        Assert.Equal(new[] { "layers", "layouts", "symbols" }, s.CheckBehaviors);
        Assert.Empty(s.SysvarsAdd); Assert.Empty(s.SysvarsRemove);
        Assert.Equal(SysVarDefaults.Default, s.EffectiveSysvars());
        Assert.Contains("CLAYER", SysVarDefaults.Default);
        Assert.DoesNotContain("PSLTSCALE", SysVarDefaults.Default);   // per paper layout: the layouts behavior has it
        Assert.Contains("PSLTSCALE", SysVarDefaults.Effective(new[] { "psltscale" }, null), StringComparer.OrdinalIgnoreCase);   // a user can still add it
        foreach (var volatile_ in new[] { "DATE", "CDATE", "TDUSRTIMER", "TDINDWG", "TDUPDATE", "DBMOD", "VIEWCTR", "VIEWSIZE", "VIEWDIR", "LASTPOINT", "CMDACTIVE" })
            Assert.DoesNotContain(volatile_, SysVarDefaults.Default);
        Assert.Equal(SysVarDefaults.Default.Length, SysVarDefaults.Default.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        s.SysvarsAdd = new() { "myvar", "clayer", " " };   // CLAYER already there: not twice
        s.SysvarsRemove = new() { "ltscale", "PDMODE" };   // without case
        var eff = s.EffectiveSysvars();
        Assert.Contains("myvar", eff);
        Assert.Single(eff, n => n.Equals("CLAYER", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("LTSCALE", eff); Assert.DoesNotContain("PDMODE", eff);

        s.CheckBehaviors = new() { "sysvars", "bogus", "sysvars", "layers" };
        Assert.Equal(new[] { "sysvars", "layers" }, s.EffectiveCheckBehaviors());
    }

    [Fact]
    public void Settings_round_trip_and_old_files_get_the_defaults()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Paths.SettingsFile(root), """{"screenshots":false}""");
        var old = RetraceSettings.Load(root);
        Assert.True(old.AutoDetectBehavior);
        Assert.Equal(3, old.CheckBehaviors.Count);
        old.AutoDetectBehavior = false; old.CheckBehaviors = new() { "views" }; old.SysvarsAdd = new() { "X" };
        Assert.True(old.Save(out var err), err);
        var back = RetraceSettings.Load(root);
        Assert.False(back.AutoDetectBehavior);
        Assert.Equal(new[] { "views" }, back.CheckBehaviors);
        Assert.Equal(new[] { "X" }, back.SysvarsAdd);
    }

    [Fact]
    public void Catalog_ids_are_the_stable_ones()
    {
        Assert.Equal(new[] { "layers", "appearance", "layouts", "paperspace", "viewports", "xdata", "xdicts", "symbols", "sysvars", "views", "psattributes" }, BehaviorCatalog.All.Select(b => b.Id));
        Assert.All(BehaviorCatalog.All, b => { Assert.NotEmpty(b.Label); Assert.NotEmpty(b.Description); });
    }

    // ---- review fixes ----

    [Fact]
    public void Compare_skips_a_behavior_id_this_build_does_not_know()
    {
        var checks = Items("future", ("a", "1"));
        Assert.Empty(BehaviorChecks.Compare(checks, Snap(("layers", new[] { ("0", "x") }))));
        Assert.Empty(BehaviorChecks.Compare(checks, new BehaviorSnapshot()));
    }

    [Fact]
    public void ApplyChecks_with_no_digest_passes_when_checks_match_and_fails_otherwise()
    {
        var actual = Snap(("sysvars", new[] { ("CLAYER", "0") }));
        var ok = new CheckpointResult { Pass = null };
        ok.ApplyChecks(Items("sysvars", ("CLAYER", "0")), actual);
        Assert.True(ok.Pass);
        var bad = new CheckpointResult { Pass = null };
        bad.ApplyChecks(Items("sysvars", ("CLAYER", "DIM")), actual);
        Assert.False(bad.Pass);
        var none = new CheckpointResult { Pass = null };
        none.ApplyChecks(null, actual);
        Assert.Null(none.Pass);
    }

    [Fact]
    public void Clone_copies_the_lists()
    {
        var s = new RetraceSettings { SysvarsAdd = new() { "A" }, SysvarsRemove = new() { "B" } };
        var c = s.Clone();
        c.CheckBehaviors.Add("views"); c.SysvarsAdd.Add("X"); c.SysvarsRemove.Clear();
        Assert.Equal(new[] { "layers", "layouts", "symbols" }, s.CheckBehaviors);
        Assert.Equal(new[] { "A" }, s.SysvarsAdd);
        Assert.Equal(new[] { "B" }, s.SysvarsRemove);
    }

    [Fact]
    public void Check_behavior_ids_match_without_case_and_come_back_as_catalog_ids()
    {
        var s = new RetraceSettings { CheckBehaviors = new() { "SysVars", " Layers ", "LAYERS", "nope", "PsAttributes" } };
        Assert.Equal(new[] { "sysvars", "layers", "psattributes" }, s.EffectiveCheckBehaviors());
    }

    [Fact]
    public void Reader_defaults_a_missing_or_unknown_source_to_auto()
    {
        var c = BehaviorChecks.FromJson("""{"behaviors":{"layers":{"items":{}},"views":{"source":"zzz","items":{}},"xdata":{"source":"manual","items":{}}}}""")!;
        Assert.Equal("auto", c.Behaviors["layers"].Source);
        Assert.Equal("auto", c.Behaviors["views"].Source);
        Assert.Equal("manual", c.Behaviors["xdata"].Source);
    }

    [Theory]
    [InlineData("\"n\":7", 7)]
    [InlineData("\"n\":\"7\"", 0)]
    [InlineData("\"n\":1.5", 0)]
    [InlineData("\"n\":99999999999", 0)]
    [InlineData("\"n\":-3", 0)]
    [InlineData("\"x\":1", 0)]
    public void Plan_reads_the_mark_n_safely(string field, int expected)
    {
        var steps = Build("{\"t\":1," + field + ",\"type\":\"mark\"}");
        Assert.Equal(expected, steps[0].EventN);
    }

    [Fact]
    public void Snapshot_write_leaves_no_temp_file()
    {
        var p = BehaviorSnapshot.MarkPath(_dir, 3);
        Assert.True(new BehaviorSnapshot().Write(p, out _));
        Assert.True(new BehaviorSnapshot().Write(p, out _));   // overwrite
        Assert.Equal(new[] { p }, Directory.GetFiles(BehaviorSnapshot.Dir(_dir)));
    }

    [Fact]
    public void Default_sysvars_leave_out_what_people_toggle_without_a_command()
    {
        foreach (var n in new[] { "ORTHOMODE", "SNAPMODE", "GRIDMODE", "CTAB", "TILEMODE", "UCSNAME", "POLARMODE", "OSMODE", "DYNMODE", "LWDISPLAY", "ANNOALLVISIBLE" })
            Assert.DoesNotContain(n, SysVarDefaults.Default);
        Assert.Contains("CLAYER", SysVarDefaults.Default);
    }

    // ---- appearance and paperspace ----

    static BehaviorKeys.Look Look(string color = "ByLayer", string linetype = "ByLayer", string lw = "ByLayer", string tr = "ByLayer", string? plot = null) =>
        new("WALLS", color, linetype, "1", lw, tr, plot, "7", "Continuous", "0.25", "0");

    [Fact]
    public void Appearance_value_resolves_only_what_is_bylayer()
    {
        Assert.Equal("layer=WALLS;color=ByLayer;linetype=ByLayer;ltscale=1;lineweight=ByLayer;transparency=ByLayer;rcolor=7;rlinetype=Continuous;rlineweight=0.25;rtransparency=0",
            BehaviorKeys.AppearanceValue(Look()));
        Assert.Equal("layer=WALLS;color=1;linetype=DASHED;ltscale=1;lineweight=0.5;transparency=127;plotstyle=Thin",
            BehaviorKeys.AppearanceValue(Look("1", "DASHED", "0.5", "127", "Thin")));
        Assert.Equal("layer=WALLS;color=ByBlock;linetype=ByLayer;ltscale=1;lineweight=ByLayer;transparency=ByLayer;rlinetype=Continuous;rlineweight=0.25;rtransparency=0",
            BehaviorKeys.AppearanceValue(Look("ByBlock")));
    }

    [Fact]
    public void Layerless_key_drops_the_layer_only_when_it_matches()
    {
        Assert.Equal("LINE|0,0,0-5,0,0|", BehaviorKeys.LayerlessKey("LINE|WALLS|0,0,0-5,0,0|", "WALLS"));
        Assert.Equal("LINE|WALLS|x|", BehaviorKeys.LayerlessKey("LINE|WALLS|x|", "DIM"));
        // the dxf part is whatever the key starts with: a dimension subclass or a block-reference subclass keyed as INSERT
        Assert.Equal("ARC_DIMENSION|at=1,1,0|text=", BehaviorKeys.LayerlessKey("ARC_DIMENSION|DIM|at=1,1,0|text=", "DIM"));
        Assert.Equal("INSERT|at=0,0,0|block=B", BehaviorKeys.LayerlessKey("INSERT|0|at=0,0,0|block=B", "0"));
        Assert.Equal("LINE|WALLS2|x", BehaviorKeys.LayerlessKey("LINE|WALLS2|x", "WALLS"));   // a layer that only starts like it
        Assert.Equal("nobar", BehaviorKeys.LayerlessKey("nobar", "0"));
        Assert.Equal("ByLayer", BehaviorKeys.TransparencyText(false, 0, false));
        Assert.Equal("ByBlock", BehaviorKeys.TransparencyText(false, 0, true));
        Assert.Equal("127", BehaviorKeys.TransparencyText(true, 127, false));
        Assert.Null(BehaviorKeys.NamedPlotStyle("ByLayer"));
        Assert.Null(BehaviorKeys.NamedPlotStyle(""));
        Assert.Equal("Thin", BehaviorKeys.NamedPlotStyle("Thin"));
    }

    [Fact]
    public void Appearance_difference_names_the_entity_and_the_field()
    {
        const string id = "PS:A-101 Plan:LINE|0,0,0-5,0,0|#3";
        var was = BehaviorKeys.AppearanceValue(Look("1"));
        var now = BehaviorKeys.AppearanceValue(Look("3"));
        var checks = BehaviorChecks.ManualPick(new[] { "appearance" }, Snap(("appearance", new[] { (id, was) })));
        var lines = BehaviorChecks.Compare(checks, Snap(("appearance", new[] { (id, now) })));
        Assert.Equal(new[] { $"appearance {id} color: expected 1, got 3" }, lines);
    }

    [Fact]
    public void Paperspace_difference_names_the_entity()
    {
        var checks = BehaviorChecks.ManualPick(new[] { "paperspace" }, Snap(("paperspace", new[] { ("PS:A:LINE|0|0,0,0-1,0,0|", "") })));
        Assert.Empty(BehaviorChecks.Compare(checks, Snap(("paperspace", new[] { ("PS:A:LINE|0|0,0,0-1,0,0|", "") }))));
        var lines = BehaviorChecks.Compare(checks, Snap(("paperspace", new[] { ("PS:A:LINE|0|0,0,0-1,0,0.5|", "") })));
        Assert.Equal(new[] { "paperspace PS:A:LINE|0|0,0,0-1,0,0| missing", "paperspace PS:A:LINE|0|0,0,0-1,0,0.5| unexpected" }, lines);
    }

    [Fact]
    public void Checks_recorded_before_the_new_behaviors_ignore_them()
    {
        var checks = BehaviorChecks.ManualPick(new[] { "layers" }, Snap(("layers", new[] { ("0", Layer()) })));
        var actual = Snap(("layers", new[] { ("0", Layer()) }), ("appearance", new[] { ("MS:x", "layer=0") }), ("paperspace", new[] { ("PS:a:y", "") }));
        Assert.Empty(BehaviorChecks.Compare(checks, actual));
        var prev = Snap(("layers", new[] { ("0", Layer()) }));
        Assert.True(BehaviorChecks.AutoPick(prev, actual).IsEmpty);   // not captured before: not "everything added"
        Assert.True(BehaviorChecks.AutoPick(actual, actual).IsEmpty);
        var changed = Snap(("appearance", new[] { ("MS:x", "layer=1") }), ("paperspace", new[] { ("PS:a:y", ""), ("PS:a:z", "") }));
        Assert.Equal(new[] { "appearance", "paperspace" }, BehaviorChecks.AutoPick(actual, changed).Behaviors.Keys.OrderBy(k => k));
    }

    // ---- auto pick of appearance, opt-in capture ----

    static string Ap(string layer = "WALLS", string color = "ByLayer", string rcolor = "1") =>
        $"layer={layer};color={color};linetype=ByLayer;ltscale=1;lineweight=ByLayer;transparency=ByLayer" + (color == "ByLayer" ? $";rcolor={rcolor}" : "") + ";rlinetype=Continuous;rlineweight=0.25;rtransparency=0";

    static string Own(string layer = "WALLS", string color = "ByLayer") => BehaviorKeys.OwnLook(Ap(layer, color));

    [Fact]
    public void Own_look_drops_the_resolved_fields_only()
    {
        Assert.Equal("layer=WALLS;color=ByLayer;linetype=ByLayer;ltscale=1;lineweight=ByLayer;transparency=ByLayer", Own());
        Assert.Equal("plain text", BehaviorKeys.OwnLook("plain text"));
    }

    [Fact]
    public void Auto_pick_of_appearance_keeps_added_and_changed_own_look_and_leaves_the_layers_alone()
    {
        const string e1 = "MS:LINE|0,0,0-5,0,0|", e2 = "MS:LINE|0,5,0-5,5,0|";
        var before = Snap(("appearance", new[] { (e1, Ap()), (e2, Ap()) }));
        // the layer's color changed: every ByLayer entity's rcolor differs and nothing else: layers owns it
        Assert.True(BehaviorChecks.AutoPick(before, Snap(("appearance", new[] { (e1, Ap(rcolor: "3")), (e2, Ap(rcolor: "3")) }))).IsEmpty);
        // a new red hatch: an added item, kept with its own fields only
        const string hatch = "MS:HATCH|0,0,0-4,4,0|";
        var drawn = Snap(("appearance", new[] { (e1, Ap()), (e2, Ap()), (hatch, Ap(color: "1")) }));
        var picked = BehaviorChecks.AutoPick(before, drawn).Behaviors["appearance"];
        Assert.Equal(new[] { hatch }, picked.Items.Keys);
        Assert.Equal(Own(color: "1"), picked.Items[hatch]);
        Assert.Equal("auto", picked.Source); Assert.Equal("items", picked.Scope);
        // a MOVE: the old id is removed (dropped), the new id is added (small item, own fields)
        const string moved = "MS:LINE|0,6,0-5,6,0|";
        var move = BehaviorChecks.AutoPick(before, Snap(("appearance", new[] { (e1, Ap()), (moved, Ap()) }))).Behaviors["appearance"];
        Assert.Equal(new[] { moved }, move.Items.Keys);
        Assert.DoesNotContain(move.Items, kv => kv.Value == null);
        // MOVE plus color change: picked with the new color
        var both = BehaviorChecks.AutoPick(before, Snap(("appearance", new[] { (e1, Ap()), (moved, Ap(color: "1")) }))).Behaviors["appearance"];
        Assert.Equal(Own(color: "1"), both.Items[moved]);
        // an entity's own look changed in place (layer, color): picked, own fields only
        var own = BehaviorChecks.AutoPick(before, Snap(("appearance", new[] { (e1, Ap(color: "1")), (e2, Ap(layer: "DIM")) }))).Behaviors["appearance"];
        Assert.Equal(new[] { e1, e2 }, own.Items.Keys);
        Assert.Equal(Own(layer: "DIM"), own.Items[e2]);
        // only a removal: nothing
        Assert.True(BehaviorChecks.AutoPick(before, Snap(("appearance", new[] { (e1, Ap()) }))).IsEmpty);
        // manual picks are unchanged: the whole map with the resolved fields
        Assert.Equal(Ap(), BehaviorChecks.ManualPick(new[] { "appearance" }, before).Behaviors["appearance"].Items[e1]);
        // paperspace still owns added / removed
        var ps0 = Snap(("paperspace", new[] { ("PS:a:x", "") }));
        var ps1 = Snap(("paperspace", new[] { ("PS:a:y", "") }));
        Assert.Equal(new[] { "PS:a:x", "PS:a:y" }, BehaviorChecks.AutoPick(ps0, ps1).Behaviors["paperspace"].Items.Keys);
    }

    [Fact]
    public void An_auto_pick_of_too_many_appearance_items_leaves_appearance_out_and_says_so()
    {
        BehaviorSnapshot Many(int n) => Snap(("appearance", Enumerable.Range(0, n).Select(k => ($"MS:LINE|{k},0,0-{k},1,0|", Ap())).ToArray()), ("paperspace", new[] { ("PS:a:x", "") }));
        var before = Snap(("appearance", Array.Empty<(string, string)>()), ("paperspace", Array.Empty<(string, string)>()));
        var logged = new List<string>();
        var atLimit = BehaviorChecks.AutoPick(before, Many(BehaviorChecks.MaxAutoAppearanceItems), logged.Add);
        Assert.Equal(BehaviorChecks.MaxAutoAppearanceItems, atLimit.Behaviors["appearance"].Items.Count);
        Assert.Empty(logged);
        var over = BehaviorChecks.AutoPick(before, Many(BehaviorChecks.MaxAutoAppearanceItems + 1), logged.Add);
        Assert.False(over.Behaviors.ContainsKey("appearance"));
        Assert.True(over.Behaviors.ContainsKey("paperspace"));   // the other behaviors are unaffected
        Assert.Equal(new[] { "appearance: 2001 items over the 2000 limit, not checked at this checkpoint" }, logged);
        // a manual pick has no limit
        Assert.Equal(2001, BehaviorChecks.ManualPick(new[] { "appearance" }, Many(2001)).Behaviors["appearance"].Items.Count);
    }

    [Fact]
    public void An_auto_picked_appearance_item_compares_its_own_fields_and_ignores_the_layers()
    {
        const string id = "MS:CIRCLE|0,0,0-4,4,0|";
        var recorded = BehaviorChecks.AutoPick(Snap(("appearance", new[] { ("MS:x", Ap()) })), Snap(("appearance", new[] { ("MS:x", Ap()), (id, Ap(color: "1")) })));
        // replayed the same: pass. The layer color is not in the recording, so a layer change later does not fail it
        Assert.Empty(BehaviorChecks.Compare(recorded, Snap(("appearance", new[] { (id, Ap(color: "1")) }))));
        Assert.Empty(BehaviorChecks.Compare(recorded, Snap(("appearance", new[] { (id, Ap(color: "1", rcolor: "9")) }))));
        // replayed ByLayer (the circle came out ByLayer instead of red): fails on color, and the resolved fields the capture now has are no extra line
        var lines = BehaviorChecks.Compare(recorded, Snap(("appearance", new[] { (id, Ap()) })));
        Assert.Equal(new[] { $"appearance {id} color: expected 1, got ByLayer" }, lines);
        // a manual map that has a resolved field still compares it; if the capture lacks it that is a plain difference
        var manual = BehaviorChecks.ManualPick(new[] { "appearance" }, Snap(("appearance", new[] { (id, Ap()) })));
        Assert.Equal(new[] { $"appearance {id} rcolor: expected 1, got 9" }, BehaviorChecks.Compare(manual, Snap(("appearance", new[] { (id, Ap(rcolor: "9")) }))));
        Assert.Contains($"appearance {id} color: expected ByLayer, got 1", BehaviorChecks.Compare(manual, Snap(("appearance", new[] { (id, Ap(color: "1")) }))));
        Assert.Contains($"appearance {id} rcolor: expected 1, got (none)", BehaviorChecks.Compare(manual, Snap(("appearance", new[] { (id, Ap(color: "1")) }))));
    }

    [Fact]
    public void The_opt_in_behaviors_are_recorded_only_when_asked_for()
    {
        var none = BehaviorCatalog.ForRecording(Array.Empty<string>(), Array.Empty<string>());
        Assert.DoesNotContain("appearance", none);
        Assert.DoesNotContain("paperspace", none);
        Assert.Equal(BehaviorCatalog.All.Count - 2, none.Count);
        var one = BehaviorCatalog.ForRecording(new[] { "appearance" }, Array.Empty<string>());
        Assert.Contains("appearance", one);
        Assert.DoesNotContain("paperspace", one);
        Assert.Equal(BehaviorCatalog.All.Count, BehaviorCatalog.ForRecording(new[] { "paperspace" }, new[] { "appearance" }).Count);
        Assert.Contains("Record", BehaviorCatalog.Find("paperspace")!.Description);
        Assert.Contains("Record", BehaviorCatalog.Find("appearance")!.Description);
    }

    [Fact]
    public void Settings_decide_what_is_recorded_separately_from_what_is_checked()
    {
        // default: nothing opt-in, either mode
        Assert.DoesNotContain("appearance", new RetraceSettings().BehaviorsToRecord());
        Assert.Empty(new RetraceSettings().RecordBehaviors);
        // record ticked: recorded in auto mode, where checkBehaviors is not what is verified
        var auto = new RetraceSettings { AutoDetectBehavior = true, RecordBehaviors = new() { "Appearance", "layers", "bogus", "appearance" } };
        Assert.Equal(new[] { "appearance" }, auto.EffectiveRecordBehaviors());
        Assert.Contains("appearance", auto.BehaviorsToRecord());
        Assert.DoesNotContain("paperspace", auto.BehaviorsToRecord());
        // a name in checkBehaviors does not record in auto mode ...
        var autoCheck = new RetraceSettings { AutoDetectBehavior = true, CheckBehaviors = new() { "paperspace" } };
        Assert.DoesNotContain("paperspace", autoCheck.BehaviorsToRecord());
        // ... but in manual mode it does (it could not be verified otherwise)
        var manual = new RetraceSettings { AutoDetectBehavior = false, CheckBehaviors = new() { "paperspace" } };
        Assert.Contains("paperspace", manual.BehaviorsToRecord());
        Assert.DoesNotContain("appearance", manual.BehaviorsToRecord());
        // an old settings file has no recordBehaviors: empty
        var old = System.Text.Json.JsonSerializer.Deserialize<RetraceSettings>("{\"autoDetectBehavior\":true,\"checkBehaviors\":[\"layers\"]}")!;
        Assert.Empty(old.EffectiveRecordBehaviors());
        // round trip and clone
        var s = new RetraceSettings { RecordBehaviors = new() { "paperspace" } };
        Assert.Equal(new[] { "paperspace" }, System.Text.Json.JsonSerializer.Deserialize<RetraceSettings>(System.Text.Json.JsonSerializer.Serialize(s))!.RecordBehaviors);
        Assert.Equal(new[] { "paperspace" }, s.Clone().RecordBehaviors);
    }
}
