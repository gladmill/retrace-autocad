using Retrace.Core;

namespace Retrace.Tests;

/// <summary>SDI rules. <see cref="SdiGuard.Own"/> is process-wide state: every test sets its own registry first.</summary>
public class SdiGuardTests
{
    const string Temp = @"C:\Users\a\AppData\Local\Temp\";
    const string OwnDwg = Temp + @"retrace-sdi\Test-1a2b3c.dwg";

    static SdiOwnFiles Fresh() => SdiGuard.Own = new SdiOwnFiles(Temp);

    [Fact]
    public void With_SDI_off_a_test_may_always_open_its_drawing()
    {
        Fresh();
        Assert.Null(SdiGuard.Refusal(false, new[] { ("A.dwg", true), ("B.dwg", true) }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void With_SDI_on_a_clean_drawing_may_be_replaced(int sdi)
    {
        Fresh();
        var single = SdiGuard.IsSingle(sdi);
        Assert.True(single);
        Assert.Null(SdiGuard.Refusal(single, new[] { ("Drawing1.dwg", false) }));
        Assert.Null(SdiGuard.Refusal(single, Array.Empty<(string, bool)>()));
    }

    [Fact]
    public void With_SDI_on_a_drawing_with_unsaved_changes_stops_the_test_and_names_it()
    {
        Fresh();
        var m = SdiGuard.Refusal(true, new[] { ("Plan.dwg", true) });
        Assert.NotNull(m);
        Assert.Contains("Plan.dwg", m);
        Assert.Contains("SDI", m);
        Assert.Contains("unsaved", m);
    }

    [Fact]
    public void A_path_counts_as_own_only_when_this_process_created_it_below_the_temp_folder()
    {
        var own = Fresh();
        Assert.False(own.IsOwn(OwnDwg));                                  // not created yet
        own.Created(OwnDwg);
        Assert.True(own.IsOwn(OwnDwg));
        Assert.True(own.IsOwn(OwnDwg.ToUpperInvariant().Replace('\\', '/')));   // case and separators do not matter
        Assert.False(own.IsOwn(Temp + @"retrace-sdi\Test-other.dwg"));    // same folder, not created here
        own.Created(@"C:\Work\retrace-sdi\Plan.dwg");                     // outside the temp folder: ignored
        Assert.False(own.IsOwn(@"C:\Work\retrace-sdi\Plan.dwg"));
        Assert.False(SdiGuard.IsOwnTestDrawing(@"C:\Work\retrace-suite-x\Plan.dwg"));   // a folder name alone never qualifies
        Assert.False(own.IsOwn(""));
        Assert.False(own.IsOwn(null));
    }

    [Fact]
    public void A_runs_unsaved_changes_are_its_own_but_a_persons_after_the_run_are_not()
    {
        var own = Fresh();
        own.Created(OwnDwg);
        own.BeginRun();
        Assert.True(own.UnsavedIsOurs(OwnDwg));
        Assert.Null(SdiGuard.Refusal(true, new[] { (OwnDwg, true) }));        // the run made them
        own.EndRun(OwnDwg, saved: true);
        Assert.False(own.UnsavedIsOurs(OwnDwg));
        var m = SdiGuard.Refusal(true, new[] { (OwnDwg, true) });             // the person worked in it afterwards
        Assert.NotNull(m);
        Assert.Contains("Test-1a2b3c.dwg", m);
    }

    [Fact]
    public void Retraces_own_start_setup_is_not_a_persons_change_when_the_modified_flag_was_read_before_it()
    {
        // RETRACERUN under SDI: the copy is fresh and unmodified when the run begins (the flag is read before the start layers are set up), so it stays the test's own
        var own = Fresh();
        own.Created(OwnDwg);
        own.BeginRun(OwnDwg, modified: false);
        Assert.True(own.IsOwn(OwnDwg));
        Assert.Null(SdiGuard.Refusal(true, new[] { (OwnDwg, true) }));
        // an earlier run's leftover that is modified when a later run begins is a person's (the rule the flag read afterwards would have broken)
        var later = Fresh();
        later.Created(OwnDwg);
        later.BeginRun(OwnDwg, modified: false, nowUtc: DateTime.UtcNow.AddMinutes(10));
        later.EndRun(OwnDwg, saved: true);
        later.BeginRun(OwnDwg, modified: true);
        Assert.False(later.IsOwn(OwnDwg));
    }

    [Fact]
    public void When_the_end_of_run_save_fails_the_drawing_stops_counting_as_the_tests_own()
    {
        var own = Fresh();
        own.Created(OwnDwg);
        own.BeginRun();
        own.EndRun(OwnDwg, saved: false);
        Assert.False(own.IsOwn(OwnDwg));
        Assert.False(own.UnsavedIsOurs(OwnDwg));
        var m = SdiGuard.Refusal(true, new[] { (OwnDwg, true) });             // its unsaved changes are now a person's: the next test refuses
        Assert.NotNull(m);
        Assert.Contains("unsaved", m);
        Assert.True(SdiGuard.OpenStepNeedsCopy(true, OwnDwg));                // and it is not a copy Retrace may open or replace
    }

    [Fact]
    public void Files_below_a_folder_are_found_by_canonical_path()
    {
        string Long(string p) => p.Replace("CLAUDE~1", "claude-long");
        var own = new SdiOwnFiles(@"C:\T\", Long);
        Assert.True(own.IsUnder(@"C:\Users\CLAUDE~1\Temp\retrace-suite-a\x\Plan.dwg", @"c:/users/claude-long/temp/retrace-suite-a"));
        Assert.False(own.IsUnder(@"C:\Users\claude-long\Temp\retrace-suite-ab\Plan.dwg", @"C:\Users\claude-long\Temp\retrace-suite-a"));
        Assert.False(own.IsUnder("", "x"));
    }

    [Fact]
    public void A_persons_unsaved_drawing_is_refused_even_while_a_run_goes_on()
    {
        var own = Fresh();
        own.Created(OwnDwg);
        own.BeginRun();
        var m = SdiGuard.Refusal(true, new[] { (OwnDwg, true), (@"C:\Work\Plan.dwg", true) });
        Assert.NotNull(m);
        Assert.Contains("Plan.dwg", m);
        Assert.DoesNotContain("Test-1a2b3c", m);
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]   // SDI 0 never replaces a drawing
    public void A_drawing_is_saved_before_the_open_only_when_it_is_the_tests_own(int sdi, bool created, bool save)
    {
        var own = Fresh();
        if (created) own.Created(OwnDwg);
        Assert.Equal(save, SdiGuard.SaveOwnBeforeOpen(SdiGuard.IsSingle(sdi), OwnDwg));
    }

    [Theory]
    [InlineData(1, @"C:\Work\Plan.dwg", true)]                            // the person's file: a copy
    [InlineData(2, @"C:\Work\Plan.dwg", true)]
    [InlineData(1, @"C:\Work\retrace-sdi\Plan.dwg", true)]                // a folder name does not make it a copy
    [InlineData(1, OwnDwg, false)]                                         // already a copy made here
    [InlineData(0, @"C:\Work\Plan.dwg", false)]                            // SDI 0: the old behaviour
    [InlineData(1, "", false)]
    public void An_open_step_uses_a_temp_copy_under_SDI_unless_the_path_is_one(int sdi, string path, bool copy)
    {
        Fresh().Created(OwnDwg);
        Assert.Equal(copy, SdiGuard.OpenStepNeedsCopy(SdiGuard.IsSingle(sdi), path));
    }

    [Theory]
    [InlineData("OPEN", true)]
    [InlineData("-OPEN", true)]
    [InlineData("_.open", true)]
    [InlineData("QNEW", true)]
    [InlineData("_NEW", true)]
    [InlineData("OPENSHEETSET", true)]
    [InlineData("RECOVER", true)]
    [InlineData("_-recover", true)]
    [InlineData("RECOVERALL", true)]
    [InlineData("PARTIALOPEN", true)]
    [InlineData("-PARTIALOPEN", true)]
    [InlineData("XOPEN", true)]
    [InlineData("*OPEN", true)]
    [InlineData("CIRCLE", false)]
    [InlineData("CLOSE", false)]               // a test may end by closing its drawing
    [InlineData("OPENX", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Commands_that_open_a_drawing_are_recognised(string? cmd, bool opens) => Assert.Equal(opens, SdiGuard.OpensDrawingCommand(cmd));

    [Fact]
    public void Temp_files_are_stale_a_day_after_they_were_created_or_written()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(SdiGuard.IsStaleTemp(now.AddHours(-23), now.AddHours(-23), now));
        Assert.True(SdiGuard.IsStaleTemp(now.AddHours(-25), now.AddHours(-25), now));
        // a copy of an old template: old write time, new creation time: not stale
        Assert.False(SdiGuard.IsStaleTemp(now.AddMinutes(-1), now.AddDays(-300), now));
        // an old file somebody wrote to a minute ago: not stale
        Assert.False(SdiGuard.IsStaleTemp(now.AddDays(-5), now.AddMinutes(-1), now));
    }

    [Theory]
    [InlineData(@"C:\T\retrace-sdi\Test-aa.dwg", true)]
    [InlineData(@"C:\T\retrace-sdi\Test-aa.bak", true)]
    [InlineData(@"C:\T\retrace-sdi\Test-aa.dwl2", true)]
    [InlineData(@"C:\T\RETRACE-SDI\test-aa.dwl", true)]
    [InlineData(@"C:\T\retrace-sdi\Test-aab.dwg", false)]                  // another file whose name starts the same
    [InlineData(@"C:\T\retrace-sdi\Test-zz.dwg", false)]
    [InlineData(@"C:\T\retrace-sdi\ab12\Test-aa.dwg", false)]              // another folder
    public void Files_that_belong_to_an_open_drawing_are_kept_when_cleaning_up(string file, bool kept) =>
        Assert.Equal(kept, new SdiOwnFiles(@"C:\T\").BelongsToOpenDrawing(file, new[] { @"C:\T\retrace-sdi\Test-aa.dwg" }));

    [Fact]
    public void Short_and_long_forms_of_the_temp_path_are_the_same_file()
    {
        // AutoCAD reports C:\Users\CLAUDE~1\..., Retrace made the file under C:\Users\claude-long\...: both go through the canonical long form
        string Long(string p) => p.Replace("CLAUDE~1", "claude-long");
        var own = SdiGuard.Own = new SdiOwnFiles(@"C:\Users\CLAUDE~1\AppData\Local\Temp\", Long);
        own.Created(@"C:\Users\claude-long\AppData\Local\Temp\retrace-sdi\Test-aa.dwg");
        Assert.True(own.IsOwn(@"C:\Users\CLAUDE~1\AppData\Local\Temp\retrace-sdi\Test-aa.dwg"));
        Assert.True(own.SamePath(@"C:\Users\CLAUDE~1\AppData\Local\Temp\retrace-sdi\TEST-aa.dwg", @"c:/users/claude-long/appdata/local/temp/retrace-sdi/Test-aa.dwg"));
        Assert.False(own.SamePath(@"C:\Users\CLAUDE~1\AppData\Local\Temp\retrace-sdi\Test-bb.dwg", @"C:\Users\claude-long\AppData\Local\Temp\retrace-sdi\Test-aa.dwg"));
        Assert.False(own.SamePath("", "x"));
        Assert.True(own.BelongsToOpenDrawing(@"C:\Users\claude-long\AppData\Local\Temp\retrace-sdi\Test-aa.bak", new[] { @"C:\Users\CLAUDE~1\AppData\Local\Temp\retrace-sdi\Test-aa.dwg" }));
    }

    [Fact]
    public void A_temp_drawing_is_deleted_only_while_it_is_what_retrace_last_saw()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var written = now.AddDays(-3);
        var rec = SdiTempRecord.Of(written, 4096);
        Assert.True(SdiTempRecord.MayDelete(rec, written, written, 4096, now));                    // old and unchanged
        Assert.False(SdiTempRecord.MayDelete(rec, written, written.AddHours(5), 4096, now.AddDays(10)));   // somebody saved into it: other write time
        Assert.False(SdiTempRecord.MayDelete(rec, written, written, 5000, now));                   // other size
        Assert.False(SdiTempRecord.MayDelete(null, written, written, 4096, now));                  // no record: never
        Assert.False(SdiTempRecord.MayDelete(rec, now.AddHours(-1), written, 4096, now));          // recently created / touched: not yet
    }

    [Theory]
    [InlineData("638000000000000000 4096", true)]
    [InlineData("  638000000000000000   4096 ", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("abc 12", false)]
    [InlineData("1 2 3", false)]
    public void The_sidecar_text_round_trips_and_garbage_is_no_record(string? text, bool ok)
    {
        var r = SdiTempRecord.Parse(text);
        Assert.Equal(ok, r.HasValue);
        if (r is { } v) Assert.Equal(v, SdiTempRecord.Parse(v.Format()));
    }

    [Theory]
    [InlineData("Test-aa.bak", true)]
    [InlineData("TEST-AA.DWL2", true)]
    [InlineData("Test-aa.dwg.retrace", true)]
    [InlineData("Test-aab.bak", false)]
    [InlineData("Test-aa.dwg", false)]
    public void Companions_of_a_drawing_go_with_it(string name, bool companion) => Assert.Equal(companion, SdiTempRecord.IsCompanion(name, "Test-aa.dwg"));

    [Fact]
    public void A_failed_end_of_run_save_does_not_stop_a_suite_but_does_stop_a_retracerun()
    {
        var own = Fresh();
        own.Created(OwnDwg);
        own.BeginRun();
        own.EndRun(OwnDwg, saved: false, suiteRun: true);            // a suite: nobody touches the drawing between two tests
        Assert.True(own.IsOwn(OwnDwg));
        Assert.True(own.UnsavedIsOurs(OwnDwg));                      // the next test's PrepareForOpen may save it
        Assert.Null(SdiGuard.Refusal(true, new[] { (OwnDwg, true) }));
        own.BeginRun(); own.EndRun(OwnDwg, saved: true, suiteRun: true);   // saved later
        Assert.False(own.UnsavedIsOurs(OwnDwg));

        var other = Fresh();
        other.Created(OwnDwg);
        other.BeginRun();
        other.EndRun(OwnDwg, saved: false, suiteRun: false);         // a RETRACERUN: a person may work in it afterwards
        Assert.False(other.IsOwn(OwnDwg));
        Assert.NotNull(SdiGuard.Refusal(true, new[] { (OwnDwg, true) }));
    }

    [Fact]
    public void An_own_drawing_that_is_already_modified_when_a_run_begins_is_not_the_runs()
    {
        var own = Fresh();
        own.Created(OwnDwg);
        own.BeginRun(OwnDwg, modified: false); own.EndRun(OwnDwg, saved: true);   // the first run in it
        own.BeginRun(OwnDwg, modified: true);                        // a person's changes from an earlier run
        Assert.False(own.IsOwn(OwnDwg));
        Assert.False(own.UnsavedIsOurs(OwnDwg));
        Assert.NotNull(SdiGuard.Refusal(true, new[] { (OwnDwg, true) }));

        var clean = Fresh();
        clean.Created(OwnDwg);
        clean.BeginRun(OwnDwg, modified: false);
        Assert.True(clean.IsOwn(OwnDwg));
        Assert.True(clean.UnsavedIsOurs(OwnDwg));
    }

    [Fact]
    public void A_late_end_of_run_of_one_run_does_not_end_another_run()
    {
        var own = Fresh();
        own.Created(OwnDwg);
        own.BeginRun();                                              // test 1
        own.BeginRun();                                              // test 2 starts before test 1's end-of-run save came
        own.EndRun(OwnDwg, saved: true, suiteRun: true);             // test 1's late end
        Assert.True(own.UnsavedIsOurs(OwnDwg));                      // test 2 is still running
        own.EndRun(OwnDwg, saved: true, suiteRun: true);
        Assert.False(own.UnsavedIsOurs(OwnDwg));
    }

    [Theory]
    // commandInProgress, quiescent, modalOpen, verdictBoxIsTheModal -> may the end-of-run save happen
    [InlineData(false, true, false, false, true)]     // idle
    [InlineData(true, false, false, false, false)]    // a command runs
    [InlineData(true, false, true, true, false)]      // a command runs, verdict box or not: never during a command
    [InlineData(false, true, true, false, false)]     // somebody's dialog is open (even if AutoCAD claims to be quiescent)
    [InlineData(false, false, true, false, false)]    // a dialog that is not Retrace's verdict box, and the verdict box is not in front of it
    [InlineData(false, false, true, true, true)]      // only Retrace's own verdict box is open: the save does not wait for it
    [InlineData(false, true, true, true, true)]
    [InlineData(false, false, false, false, false)]   // not quiescent (input queued)
    public void The_end_of_run_save_waits_for_no_command_and_ignores_only_retraces_own_verdict_box(bool command, bool quiescent, bool modal, bool verdictOnly, bool canSave) =>
        Assert.Equal(canSave, SdiSavePolicy.CanSaveNow(command, quiescent, modal, verdictOnly));

    [Fact]
    public void At_the_end_of_a_suite_unsaved_drawings_of_its_runs_are_given_up()
    {
        var own = Fresh();
        own.Created(OwnDwg);
        own.BeginRun();
        own.EndRun(OwnDwg, saved: false, suiteRun: true);          // the last test's end-of-run save never happened
        Assert.True(own.UnsavedIsOurs(OwnDwg));
        own.SuiteEnded();
        Assert.False(own.UnsavedIsOurs(OwnDwg));                   // now a person's: a test refuses to replace it
        Assert.False(own.IsOwn(OwnDwg));
        Assert.NotNull(SdiGuard.Refusal(true, new[] { (OwnDwg, true) }));
    }

    [Fact]
    public void Incoming_names_are_made_canonical_only_when_they_have_a_short_name_part()
    {
        var calls = new List<string>();
        string Canon(string p) { calls.Add(p); return p.Replace("CLAUDE~1", "claude-long"); }
        var own = new SdiOwnFiles(@"C:\Users\claude-long\Temp\", Canon);
        own.Created(@"C:\Users\claude-long\Temp\retrace-sdi\Test-aa.dwg");
        calls.Clear();
        Assert.True(own.IsOwn(@"C:\Users\claude-long\Temp\retrace-sdi\Test-aa.dwg"));   // a long name: no canonical call
        Assert.Empty(calls);
        Assert.True(own.IsOwn(@"C:\Users\CLAUDE~1\Temp\retrace-sdi\Test-aa.dwg"));       // a short name: made canonical
        Assert.Single(calls);
    }

    [Fact]
    public void The_refusal_names_the_ways_out_and_does_not_suggest_closing_without_saving()
    {
        var m = SdiGuard.Refusal(true, new[] { ("Plan.dwg", true) })!;
        Assert.Contains("Save it", m);
        Assert.Contains("reopen its file", m);
        Assert.Contains("SDI to 0", m);
        Assert.DoesNotContain("without saving", m);
    }

    [Fact]
    public void A_drawing_start_up_code_changed_as_it_opened_stays_the_tests_own_but_a_leftover_does_not()
    {
        var own = Fresh();
        own.Created(OwnDwg);                                         // RETRACERUN just opened it (acaddoc.lsp / S::STARTUP then changed it)
        own.BeginRun(OwnDwg, modified: true);
        Assert.True(own.IsOwn(OwnDwg));
        Assert.True(own.UnsavedIsOurs(OwnDwg));
        Assert.Null(SdiGuard.Refusal(true, new[] { (OwnDwg, true) }));
        own.EndRun(OwnDwg, saved: true);

        // a later run in the same drawing, found modified: a leftover, a person's changes
        own.BeginRun(OwnDwg, modified: true);
        Assert.False(own.IsOwn(OwnDwg));
        Assert.NotNull(SdiGuard.Refusal(true, new[] { (OwnDwg, true) }));
    }

    [Theory]
    [InlineData("CLOSE", true)]
    [InlineData("_.close", true)]
    [InlineData("CLOSEALL", true)]
    [InlineData("QUIT", true)]
    [InlineData("-EXIT", true)]
    [InlineData("OPEN", false)]
    [InlineData("CLOSEUPX", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Commands_that_close_the_only_drawing_are_recognised(string? cmd, bool closes) => Assert.Equal(closes, SdiGuard.ClosesDrawingCommand(cmd));

    [Fact]
    public void A_copy_is_fresh_only_for_the_first_run_that_begins_in_it_soon_after_it_was_made()
    {
        var own = Fresh();
        own.Created(OwnDwg);
        own.BeginRun(OwnDwg, modified: true, nowUtc: DateTime.UtcNow.AddMinutes(10));   // began far too late: a leftover
        Assert.False(own.IsOwn(OwnDwg));

        var cleared = Fresh();
        cleared.Created(OwnDwg);
        cleared.ClearFresh(OwnDwg);                                                      // the replay never began (timeout, wrong drawing, start failed)
        cleared.BeginRun(OwnDwg, modified: true);
        Assert.False(cleared.IsOwn(OwnDwg));

        var soon = Fresh();
        soon.Created(OwnDwg);
        soon.BeginRun(OwnDwg, modified: true, nowUtc: DateTime.UtcNow.AddSeconds(30));
        Assert.True(soon.IsOwn(OwnDwg));
    }

    [Fact]
    public void A_late_failed_save_of_a_suite_drawing_after_the_suite_ended_disowns_it()
    {
        var own = Fresh();
        own.SuiteStarted();
        own.Created(OwnDwg);
        own.BeginRun(); own.SuiteEnded();                            // the suite ended (RestoreSdiLater), the last end-of-run save is still to come
        own.EndRun(OwnDwg, saved: false, suiteRun: true);            // ... and fails
        Assert.False(own.IsOwn(OwnDwg));
        Assert.NotNull(SdiGuard.Refusal(true, new[] { (OwnDwg, true) }));

        var again = Fresh();
        again.SuiteStarted(); again.Created(OwnDwg); again.BeginRun();
        again.EndRun(OwnDwg, saved: false, suiteRun: true);          // during the suite: the next test saves it
        Assert.True(again.UnsavedIsOurs(OwnDwg));
    }

    [Fact]
    public void A_copy_whose_run_never_began_is_given_up_without_touching_a_running_test()
    {
        var own = Fresh();
        own.Created(OwnDwg);
        own.Abandon(OwnDwg);
        Assert.False(own.IsOwn(OwnDwg));
        own.BeginRun();
        own.Created(Temp + @"retrace-sdi\Test-other.dwg");
        Assert.True(own.UnsavedIsOurs(Temp + @"retrace-sdi\Test-other.dwg"));   // the counter of the running test is intact
    }
}
