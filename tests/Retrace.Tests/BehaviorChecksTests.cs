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
        Assert.Equal(new[] { "layers", "layouts", "viewports", "xdata", "xdicts", "symbols", "sysvars", "views", "psattributes" }, BehaviorCatalog.All.Select(b => b.Id));
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
}
