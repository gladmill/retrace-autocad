using System.Text.Json;

namespace Retrace.Tests;

/// <summary>The AutoCAD status board (Core/StatusBoard, StatusWriter, StartOrigin): the texts, the JSON of the contract, the folder, the atomic write, the writer's
/// rules. The hooks in AutoCad/ cannot run here; they only collect the facts these tests feed in.</summary>
public class StatusBoardTests
{
    static string Temp() => Path.Combine(Path.GetTempPath(), "retrace-status-" + Guid.NewGuid().ToString("N"));
    static void Clean(string dir) { try { Directory.Delete(dir, true); } catch (IOException) { } }
    static readonly DateTime T0 = new(2026, 11, 5, 10, 14, 3, DateTimeKind.Utc);

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---- folder, file name, writer name -------------------------------------------------------------------------------------------------

    [Fact]
    public void Folder_is_ACAD_STATUS_DIR_when_set_else_ProgramData_Gladmill_acad_status()
    {
        Assert.Equal("/x/board", StatusBoard.Folder(n => n == "ACAD_STATUS_DIR" ? "/x/board" : null, "/pd"));
        Assert.Equal("/x/board", StatusBoard.Folder(n => "  \"/x/board\" ", "/pd"));
        Assert.Equal(Path.Combine("/pd", "Gladmill", "acad-status"), StatusBoard.Folder(_ => null, "/pd"));
        Assert.Equal(Path.Combine("/pd", "Gladmill", "acad-status"), StatusBoard.Folder(_ => "  ", "/pd"));
        Assert.Equal(Path.Combine(@"C:\ProgramData", "Gladmill", "acad-status"), StatusBoard.Folder(_ => null, ""));
    }

    [Fact]
    public void File_name_has_the_process_id_and_fits_the_readers_pattern()
    {
        Assert.Equal("retrace-4812.json", StatusBoard.FileName(4812));
        Assert.Matches(@"^[a-z0-9][a-z0-9-]{0,39}\.json$", StatusBoard.FileName(4812));
    }

    [Theory]
    [InlineData("0.6.3", "Retrace 0.6.3")]
    [InlineData("0.6.3+abc123def", "Retrace 0.6.3")]
    [InlineData("0.6.3-beta.1+9f", "Retrace 0.6.3-beta.1")]
    [InlineData("", "Retrace")]
    [InlineData(null, "Retrace")]
    public void Writer_name_is_the_product_and_version_without_build_metadata(string? version, string expected) =>
        Assert.Equal(expected, StatusBoard.WriterName(version));

    // ---- caps ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cap_makes_one_line_and_cuts_with_dots()
    {
        Assert.Equal("a b c", StatusBoard.Cap("  a\r\nb\t\tc \n", 50));
        Assert.Equal("", StatusBoard.Cap(null, 10));
        Assert.Equal("", StatusBoard.Cap("   ", 10));
        Assert.Equal("1234567890", StatusBoard.Cap("1234567890", 10));
        var cut = StatusBoard.Cap("12345678901", 10);
        Assert.Equal(10, cut.Length);
        Assert.Equal("1234567...", cut);
        Assert.Equal(18, StatusBoard.Cap(new string('x', 100), StatusBoard.MaxShort).Length);
    }

    [Fact]
    public void Cap_never_cuts_a_surrogate_pair_in_half()
    {
        var text = new string('a', 6) + "\U0001F600\U0001F600\U0001F600";   // the cut at 7 characters would fall inside the first emoji
        var cut = StatusBoard.Cap(text, 10);
        Assert.True(cut.Length <= 10);
        Assert.False(char.IsHighSurrogate(cut[^4]) && !char.IsLowSurrogate(cut[^3]));
        Assert.EndsWith("...", cut);
        Assert.DoesNotContain('\uFFFD', cut);
        for (var i = 0; i < cut.Length; i++)
            if (char.IsHighSurrogate(cut[i])) Assert.True(i + 1 < cut.Length && char.IsLowSurrogate(cut[i + 1]));
    }

    // ---- what the board says --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Recording_is_running_with_the_steps_so_far()
    {
        var e = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Record, Test = "smoke040", RecordedSteps = 12, Owner = "user" });
        Assert.Equal("running", e.State);
        Assert.Equal("Rec 12", e.Short);
        Assert.Equal("Recording smoke040: 12 steps", e.Summary);
        Assert.Equal("record", e.Mode);
        Assert.Equal(12, e.Step);
        Assert.Equal("user", e.Owner);
        Assert.Equal("Recording smoke040: 1 step", StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Record, Test = "smoke040", RecordedSteps = 1 }).Summary);
        Assert.Equal("Rec 0", StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Record }).Short);
    }

    [Fact]
    public void Replay_is_running_with_the_step_and_its_command()
    {
        var e = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "smoke032", Step = 3, Steps = 8, StepText = "BREAK" });
        Assert.Equal("running", e.State);
        Assert.Equal("Step 3/8", e.Short);
        Assert.Equal("Replay smoke032: step 3/8 (BREAK)", e.Summary);
        Assert.Equal(("replay", "smoke032", 3, 8, "BREAK"), (e.Mode, e.Test, e.Step, e.Steps, e.StepText));
        Assert.Null(e.Owner);
        Assert.Equal("Replay smoke032: step 3/8", StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "smoke032", Step = 3, Steps = 8 }).Summary);
    }

    [Fact]
    public void Replay_waiting_for_a_dialog_it_will_fill_stays_running_and_says_so()
    {
        var e = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "t", Step = 2, Steps = 5, StepText = "PLOT", Note = "filling dialog Plot" });
        Assert.Equal("running", e.State);
        Assert.Equal("Replay t: step 2/5 (PLOT), filling dialog Plot", e.Summary);
        Assert.Null(e.NeedsPerson);
    }

    [Fact]
    public void A_step_that_needs_a_person_makes_the_state_needs_person_with_the_reason()
    {
        var e = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "t", Step = 4, Steps = 5, StepText = "PLOT", NeedsPerson = "a dialog Retrace cannot fill: Plot" });
        Assert.Equal("needs-person", e.State);
        Assert.Equal("Needs person", e.Short);
        Assert.Equal("Replay t: step 4/5 (PLOT) needs a person: a dialog Retrace cannot fill: Plot", e.Summary);
        Assert.Equal("a dialog Retrace cannot fill: Plot", e.NeedsPerson);
        Assert.Equal(("replay", 4, 5), (e.Mode, e.Step, e.Steps));
    }

    [Fact]
    public void Suite_shows_the_test_and_step_like_the_contract_example()
    {
        var e = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Suite, QueueIndex = 3, QueueCount = 7, Test = "smoke040", Step = 4, Steps = 5, StepText = "BREAK", Owner = "suite" });
        Assert.Equal("running", e.State);
        Assert.Equal("Suite 3/7", e.Short);
        Assert.Equal("Suite 3/7: smoke040, step 4/5 (BREAK)", e.Summary);
        Assert.Equal(("suite", 3, 7, "suite"), (e.Mode, e.SuiteIndex, e.SuiteCount, e.Owner));
        Assert.Equal(("smoke040", 4, 5), (e.Test, e.Step, e.Steps));
    }

    [Fact]
    public void Suite_between_tests_and_tdoc_and_a_suite_that_waits_for_a_person()
    {
        var between = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Suite, QueueIndex = 2, QueueCount = 2 });
        Assert.Equal("Suite 2/2: between tests", between.Summary);
        Assert.Null(between.Test);
        var tdoc = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Tdoc, QueueIndex = 1, QueueCount = 3, Test = "a", Step = 2, Steps = 9, StepText = "LINE", Owner = "tdoc" });
        Assert.Equal("Tdoc 1/3", tdoc.Short);
        Assert.Equal("Tdoc 1/3: a, step 2/9 (LINE)", tdoc.Summary);
        Assert.Equal("tdoc", tdoc.Mode);
        var stuck = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Suite, QueueIndex = 1, QueueCount = 2, NeedsPerson = "a dialog is open after the start" });
        Assert.Equal("needs-person", stuck.State);
        Assert.Equal("Suite 1/2: between tests needs a person: a dialog is open after the start", stuck.Summary);
        Assert.Equal("Suite 2/2", StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Suite, QueueIndex = 9, QueueCount = 2 }).Short);   // an index past the end is clamped
        Assert.Equal("Suite 1/1", StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Suite }).Short);
    }

    [Fact]
    public void Idle_says_how_the_last_run_ended()
    {
        var none = StatusBoard.Compose(new StatusFacts());
        Assert.Equal(("idle", "Idle", "idle"), (none.State, none.Summary, none.Mode));
        Assert.Null(none.Short);
        var pass = StatusBoard.Compose(new StatusFacts { Last = "Last: smoke032 PASS (2/2 checkpoints)", Owner = "user" });
        Assert.Equal("idle", pass.State);
        Assert.Equal("Last: smoke032 PASS (2/2 checkpoints)", pass.Summary);
        Assert.Null(pass.Owner);   // nothing is running: nobody owns it
        var error = StatusBoard.Compose(new StatusFacts { Last = "Last: smoke032 ERROR: step 3 timed out", LastIsError = true });
        Assert.Equal(("error", "Last: smoke032 ERROR: step 3 timed out"), (error.State, error.Summary));
        Assert.Equal("Test ERROR", error.Short);
        var person = StatusBoard.Compose(new StatusFacts { Last = "Last: t INCOMPLETE: needs a person: dialog Plot", LastNeedsPerson = true, NeedsPerson = "needs a person: dialog Plot" });
        Assert.Equal("needs-person", person.State);
        Assert.Equal("needs a person: dialog Plot", person.NeedsPerson);
        Assert.Equal("idle", person.Mode);
    }

    [Fact]
    public void Summary_and_short_never_exceed_the_contract_and_stay_on_one_line()
    {
        var huge = string.Join("\n", Enumerable.Repeat("a very long line of text", 40));
        foreach (var mode in new[] { StatusMode.Record, StatusMode.Replay, StatusMode.Suite, StatusMode.Tdoc, StatusMode.Idle })
            foreach (var person in new string?[] { null, huge })
            {
                var e = StatusBoard.Compose(new StatusFacts { Mode = mode, Test = huge, Step = 99999, Steps = 99999, StepText = huge, QueueIndex = 99999, QueueCount = 99999,
                    RecordedSteps = 99999, Note = huge, NeedsPerson = person, Last = huge, LastIsError = person == null, Owner = huge });
                Assert.InRange(e.Summary.Length, 1, StatusBoard.MaxSummary);
                Assert.DoesNotContain('\n', e.Summary);
                if (e.Short != null) Assert.InRange(e.Short.Length, 1, StatusBoard.MaxShort);
            }
    }

    [Fact]
    public void A_step_number_is_never_past_the_last_step()
    {
        var e = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "t", Step = 9, Steps = 5 });
        Assert.Equal("Step 9/9", e.Short);   // the total follows the step: the board never says 9/5
        var zero = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "t", Step = 0, Steps = 0 });
        Assert.Equal("Step 1/1", zero.Short);
    }

    // ---- how a run ended ------------------------------------------------------------------------------------------------------------------

    static ReplayResult Result(string verdict, string message = "", params bool?[] checks)
    {
        var r = new ReplayResult { Session = "2026-11-04_0109_smoke040", Verdict = verdict, Message = message };
        for (var i = 0; i < checks.Length; i++) r.Checkpoints.Add(new CheckpointResult { Index = i + 1, Pass = checks[i] });
        return r;
    }

    [Fact]
    public void Last_replay_names_the_test_without_its_date_and_counts_checkpoints()
    {
        Assert.Equal("Last: smoke040 PASS (2/2 checkpoints)", StatusBoard.LastReplay(Result("PASS", "", true, true)));
        Assert.Equal("Last: smoke040 PASS (2/2 checkpoints)", StatusBoard.LastReplay(Result("PASS", "a note that PASS does not show", true, true)));
        Assert.Equal("Last: smoke040 FAIL (1/2 checkpoints): checkpoint 2 differs", StatusBoard.LastReplay(Result("FAIL", "checkpoint 2 differs", true, false)));
        Assert.Equal("Last: smoke040 ERROR: step 3 timed out", StatusBoard.LastReplay(Result("ERROR", "step 3 timed out")));
    }

    [Fact]
    public void The_reason_a_person_is_needed_is_the_step_message_else_the_run_message()
    {
        Assert.Equal("stopped by the user", StatusBoard.PersonReason(Result("INCOMPLETE", "stopped by the user")));
        var dialog = Result("INCOMPLETE", "needs a person: dialog Plot");
        dialog.Steps.Add(new StepResult { Index = 4, Kind = "dialog", Status = "needs-person" });
        Assert.Equal("needs a person: dialog Plot", StatusBoard.PersonReason(dialog));
        var blocked = Result("INCOMPLETE", "outer message");
        blocked.Steps.Add(new StepResult { Index = 2, Kind = "security-prompt", Status = "blocked", Message = "AutoCAD asks whether to load a file: answer it" });
        Assert.Equal("AutoCAD asks whether to load a file: answer it", StatusBoard.PersonReason(blocked));
        Assert.Equal("a step needs a person", StatusBoard.PersonReason(Result("INCOMPLETE")));
    }

    [Fact]
    public void A_replay_that_stopped_for_a_person_is_needs_person_while_a_dialog_is_open()
    {
        // the replayer's "a dialog has been open for 2 s" stop has no dialog step and no step result: the replayer's own flag says why it stopped
        var noStep = Result("INCOMPLETE", "needs a person: dialog (untitled)");
        var open = StatusBoard.EndOfReplay(noStep, stoppedForPerson: true, dialogOpen: true);
        Assert.True(open.NeedsPerson);
        Assert.False(open.IsError);
        Assert.Equal("needs a person: dialog (untitled)", open.Reason);
        Assert.Equal("Last: smoke040 INCOMPLETE: needs a person: dialog (untitled)", open.Last);
        var closed = StatusBoard.EndOfReplay(noStep, stoppedForPerson: true, dialogOpen: false);
        Assert.False(closed.NeedsPerson);   // the dialog is gone by now: only the "Last:" line
        Assert.Null(closed.Reason);
        Assert.False(StatusBoard.EndOfReplay(Result("INCOMPLETE", "stopped by the user"), stoppedForPerson: false, dialogOpen: true).NeedsPerson);   // a dialog open for another reason
        var error = StatusBoard.EndOfReplay(Result("ERROR", "step 3 timed out"), stoppedForPerson: true, dialogOpen: true);
        Assert.True(error.IsError);
        Assert.False(error.NeedsPerson);   // an error stays an error
        var pass = StatusBoard.EndOfReplay(Result("PASS", "", true), stoppedForPerson: false, dialogOpen: false);
        Assert.Equal(("Last: smoke040 PASS (1/1 checkpoints)", false, false, (string?)null), (pass.Last, pass.IsError, pass.NeedsPerson, pass.Reason));
    }

    static SuiteResult Suite(string stopReason, params string[] verdicts)
    {
        var s = new SuiteResult { StopReason = stopReason };
        for (var i = 0; i < verdicts.Length; i++) s.Tests.Add(new SuiteTest { Name = "t" + i, Verdict = verdicts[i] });
        return s;
    }

    [Fact]
    public void A_suite_with_an_error_test_ends_in_error_and_one_stopped_at_an_open_dialog_stays_needs_person()
    {
        var ok = StatusBoard.EndOfSuite(Suite("", "PASS", "PASS"), blockedByDialog: false, dialogOpen: false);
        Assert.Equal(("Last suite PASS: 2 PASS (2 tests)", false, false, (string?)null), (ok.Last, ok.IsError, ok.NeedsPerson, ok.Reason));
        var withError = StatusBoard.EndOfSuite(Suite("", "PASS", "ERROR", "FAIL"), blockedByDialog: false, dialogOpen: false);
        Assert.True(withError.IsError);
        Assert.Equal("Last suite FAIL: 1 ERROR, 1 FAIL, 1 PASS (3 tests)", withError.Last);
        const string why = "AutoCAD did not answer: a dialog stayed open after t1";
        var stuck = StatusBoard.EndOfSuite(Suite(why, "PASS", "NEEDS-PERSON", "NOT-RUN"), blockedByDialog: true, dialogOpen: true);
        Assert.True(stuck.NeedsPerson);
        Assert.Equal(why, stuck.Reason);
        Assert.StartsWith("Last suite STOPPED: ", stuck.Last);
        var gone = StatusBoard.EndOfSuite(Suite(why, "PASS", "NOT-RUN"), blockedByDialog: true, dialogOpen: false);
        Assert.False(gone.NeedsPerson);   // closed by the time the suite ended
        Assert.False(StatusBoard.EndOfSuite(Suite("stopped by the user", "PASS", "NOT-RUN"), blockedByDialog: false, dialogOpen: true).NeedsPerson);   // another stop, an unrelated dialog
        // an error and a dialog: the person comes first (the idle entry shows needs-person)
        var both = StatusBoard.EndOfSuite(Suite(why, "ERROR"), blockedByDialog: true, dialogOpen: true);
        Assert.True(both.NeedsPerson && both.IsError);
        var idle = StatusBoard.Compose(new StatusFacts { Last = both.Last, LastIsError = both.IsError, LastNeedsPerson = both.NeedsPerson, NeedsPerson = both.Reason });
        Assert.Equal("needs-person", idle.State);
    }

    [Fact]
    public void Last_suite_and_last_tdoc_carry_the_counts_in_the_same_order()
    {
        var suite = new SuiteResult();
        suite.Tests.Add(new SuiteTest { Name = "st-pass1", Verdict = "PASS" });
        suite.Tests.Add(new SuiteTest { Name = "st-pass2", Verdict = "PASS" });
        Assert.Equal("Last suite PASS: 2 PASS (2 tests)", StatusBoard.LastSuite(suite));
        suite.Tests.Add(new SuiteTest { Name = "st-fail", Verdict = "FAIL" });
        Assert.Equal("Last suite FAIL: 1 FAIL, 2 PASS (3 tests)", StatusBoard.LastSuite(suite));
        Assert.Equal("Last Tdoc: 3 tests (1 FAIL, 2 PASS), 24 pictures", StatusBoard.LastTdoc(new[] { "PASS", "FAIL", "PASS" }, 24, false));
        Assert.Equal("Last Tdoc: 1 test (1 PASS), 1 picture, stopped", StatusBoard.LastTdoc(new[] { "PASS" }, 1, true));
        Assert.Equal("Last Tdoc: 0 tests, 0 pictures", StatusBoard.LastTdoc(Array.Empty<string>(), 0, false));
        // one order for both: the suite summary's (by name, ordinal)
        var all = new[] { "PASS", "NOT-RUN", "FAIL", "NEEDS-PERSON", "ERROR", "INCOMPLETE", "PASS" };
        var expected = "1 ERROR, 1 FAIL, 1 INCOMPLETE, 1 NEEDS-PERSON, 1 NOT-RUN, 2 PASS";
        Assert.Equal(expected, Suite("", all).CountsText());
        Assert.Equal(expected, SuiteResult.CountsText(all));
        // the summary file's `counts` and the text come from the one ordered list
        Assert.Equal(SuiteResult.OrderedCounts(all).Select(kv => kv.Key), Suite("", all).Counts.Keys);
        Assert.Equal(new[] { "ERROR", "FAIL", "INCOMPLETE", "NEEDS-PERSON", "NOT-RUN", "PASS" }, SuiteResult.OrderedCounts(all).Select(kv => kv.Key));
        Assert.Equal(2, Suite("", all).Counts["PASS"]);
        Assert.Contains("(" + expected + ")", StatusBoard.LastTdoc(all, 3, false));
        Assert.Contains(": " + expected + " (", StatusBoard.LastSuite(Suite("", all)));
    }

    [Fact]
    public void A_tdoc_run_with_an_error_test_ends_in_error()
    {
        Assert.False(StatusBoard.EndOfTdoc(new[] { "PASS", "FAIL" }, 4, false).IsError);
        var e = StatusBoard.EndOfTdoc(new[] { "PASS", "ERROR" }, 4, true);
        Assert.True(e.IsError);
        Assert.False(e.NeedsPerson);
        Assert.Equal("Last Tdoc: 2 tests (1 ERROR, 1 PASS), 4 pictures, stopped", e.Last);
    }

    [Fact]
    public void A_tdoc_run_counts_the_tests_it_did_not_get_to_as_not_run()
    {
        // two ran, one was skipped by the queue (NOT-RUN is added by the run), two were never reached (the run was stopped)
        var verdicts = new[] { "PASS", SuiteTest.NotRun, "FAIL" };
        var padded = StatusBoard.PadNotRun(verdicts, 5);
        Assert.Equal(new[] { "PASS", "NOT-RUN", "FAIL", "NOT-RUN", "NOT-RUN" }, padded);
        Assert.Equal("Last Tdoc: 5 tests (1 FAIL, 3 NOT-RUN, 1 PASS), 7 pictures, stopped", StatusBoard.EndOfTdoc(padded, 7, true).Last);
        Assert.Same(verdicts, StatusBoard.PadNotRun(verdicts, 3));   // all there: nothing added
        Assert.Same(verdicts, StatusBoard.PadNotRun(verdicts, 2));
        Assert.Equal(new[] { "NOT-RUN", "NOT-RUN" }, StatusBoard.PadNotRun(Array.Empty<string>(), 2));
    }

    [Fact]
    public void A_board_that_cannot_work_out_its_entry_writes_an_error_entry_so_the_file_does_not_go_stale()
    {
        var e = StatusBoard.ErrorEntry(new InvalidOperationException("C:\\secret\\drawing name.dwg"));
        Assert.Equal("error", e.State);
        Assert.Equal("status board error: InvalidOperationException", e.Summary);   // the type, never the message (it could hold a path or a name)
        Assert.Equal("Board error", e.Short);
        Assert.Equal("idle", e.Mode);
        var root = Parse(StatusBoard.Build(e, 7, "Retrace 0.6.3", T0, T0));
        Assert.Equal("error", root.GetProperty("state").GetString());
        Assert.DoesNotContain("secret", root.GetProperty("summary").GetString());
        // and it is an entry like any other for the writer: it is written, and the next good one replaces it
        var dir = Temp();
        try
        {
            var w = new StatusWriter(dir, 7, "Retrace 0.6.3", utcNow: () => T0);
            Assert.True(w.Publish(e));
            Assert.Equal("error", Parse(File.ReadAllText(w.FilePath)).GetProperty("state").GetString());
            Assert.True(w.Publish(StatusBoard.Compose(new StatusFacts())));
            Assert.Equal("idle", Parse(File.ReadAllText(w.FilePath)).GetProperty("state").GetString());
        }
        finally { Clean(dir); }
    }

    // ---- what Rec counts --------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ribbon", null, 0, true)]
    [InlineData("ribbon", null, 1, true)]
    [InlineData("cmd_start", "typed", 0, true)]
    [InlineData("cmd_start", null, 0, true)]
    [InlineData("cmd_start", "typed", 1, false)]       // nested in a running command (transparent, or run by one)
    [InlineData("cmd_start", "ribbon", 0, false)]      // the command of a ribbon click: the click was counted
    [InlineData("cmd_start", "palette", 0, false)]     // the command of a palette button: the palette click was counted
    [InlineData("lisp_start", null, 0, true)]
    [InlineData("mark", null, 0, true)]
    [InlineData("palette_click", null, 0, true)]
    [InlineData("palette_values", null, 0, true)]       // fields changed in a palette: a palette step
    [InlineData("native_click", null, 0, true)]         // a click in a native palette (Project Manager, Properties): a palette step
    [InlineData("native_select", null, 0, true)]
    [InlineData("prompt", null, 0, false)]
    [InlineData("cmd_end", null, 0, false)]
    [InlineData("lisp_end", null, 0, false)]
    [InlineData("modal_enter", null, 0, false)]
    [InlineData("state", null, 0, false)]
    public void Rec_counts_the_events_that_become_replay_steps(string type, string? origin, int depth, bool counts) =>
        Assert.Equal(counts, StatusBoard.CountsAsStep(type, origin, depth));

    [Theory]
    [InlineData(@"C:\work\plan.dwg", 0, 0, true)]      // a drawing opened while no command and no LISP ran: an "Open drawing" step
    [InlineData(@"\\server\share\plan.dwg", 0, 0, true)]
    [InlineData(@"C:\work\plan.dwg", 1, 0, false)]     // opened by a command (OPEN, NEW): that command's step opens it
    [InlineData(@"C:\work\plan.dwg", 0, 1, false)]     // opened by LISP
    [InlineData("plan.dwg", 0, 0, false)]              // no full path (an older recorder): the plan has no step for it
    [InlineData(null, 0, 0, false)]                    // a new drawing never saved
    public void Rec_counts_a_drawing_opened_outside_a_command_as_an_open_step(string? path, int commandDepth, int lispDepth, bool counts) =>
        Assert.Equal(counts, StatusBoard.CountsAsStep("doc_created", null, commandDepth, lispDepth, path));

    [Theory]
    [InlineData("ID_TabInsert", true)]                  // "Activate ribbon tab": a step in the plan
    [InlineData("RETRACE_TAB", false)]                  // going to Retrace's own tab to stop or mark is not
    [InlineData("retrace_tab", false)]
    [InlineData(null, true)]
    public void Rec_counts_a_ribbon_tab_switch_except_to_the_own_tab(string? tabId, bool counts) =>
        Assert.Equal(counts, StatusBoard.CountsAsStep("tab", null, 0, 0, null, tabId));

    [Fact]
    public void A_palette_click_right_after_its_values_is_one_step_with_them_as_in_the_plan()
    {
        var t = new StepTally();
        void Note(string type, string? palette = null, string? set = null, string? origin = null, int depth = 0) => t.Note(type, origin, depth, 0, null, null, palette, set);
        Note("palette_values", "Parts", "Tools");
        Note("palette_click", "Parts", "Tools");    // joins the values: one step
        Assert.Equal(1, t.Count);
        Note("palette_click", "Parts", "Tools");    // a second click is a step of its own (nothing to join)
        Assert.Equal(2, t.Count);
        Note("palette_values", "Parts", "Tools");
        Note("prompt");                              // not a step: the join stays possible
        Note("palette_click", "Parts", "Tools");
        Assert.Equal(3, t.Count);
        Note("palette_values", "Parts", "Tools");
        Note("palette_click", "Other", "Tools");    // another palette: not joined
        Assert.Equal(5, t.Count);
        Note("palette_values", "Parts", "Tools");
        Note("cmd_start", origin: "typed");          // another step in between: the click that follows is not joined
        Note("palette_click", "Parts", "Tools");
        Assert.Equal(8, t.Count);
        Note("palette_values", "Parts", "Tools");
        Note("palette_values", "Parts", "Tools");   // values twice: two steps, as the plan makes them
        Assert.Equal(10, t.Count);
        t.Reset();
        Assert.Equal(0, t.Count);
        Note("palette_click", "Parts", "Tools");    // the join does not outlive a reset
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void The_tally_counts_the_other_events_by_CountsAsStep()
    {
        var t = new StepTally();
        t.Note("ribbon", null, 0, 0, null, null, null, null);
        t.Note("cmd_start", "ribbon", 0, 0, null, null, null, null);   // the ribbon's command: already counted
        t.Note("cmd_start", "typed", 0, 0, null, null, null, null);
        t.Note("cmd_start", "typed", 1, 0, null, null, null, null);    // nested
        t.Note("tab", null, 0, 0, null, "ID_TabInsert", null, null);
        t.Note("tab", null, 0, 0, null, "RETRACE_TAB", null, null);
        t.Note("doc_created", null, 0, 0, @"C:\work\a.dwg", null, null, null);
        t.Note("mark", null, 0, 0, null, null, null, null);
        Assert.Equal(5, t.Count);
    }

    [Fact]
    public void Rec_does_not_count_a_path_on_any_other_event()
    {
        Assert.False(StatusBoard.CountsAsStep("doc_active", null, 0, 0, @"C:\work\plan.dwg"));
        Assert.False(StatusBoard.CountsAsStep("state", null, 0, 0, @"C:\work\plan.dwg"));
    }

    // ---- the JSON -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Build_writes_every_required_field_of_the_contract()
    {
        var e = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Suite, QueueIndex = 3, QueueCount = 7, Test = "smoke040", Step = 4, Steps = 5, StepText = "BREAK", Owner = "suite" });
        var since = T0.AddSeconds(-23);
        var pidStart = new DateTime(2026, 11, 5, 9, 58, 12, 345, DateTimeKind.Utc);
        var root = Parse(StatusBoard.Build(e, 4812, "Retrace 0.6.3", T0, since, pidStart: pidStart));
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("retrace", root.GetProperty("source").GetString());
        Assert.Equal("Retrace 0.6.3", root.GetProperty("writer").GetString());
        Assert.Equal(4812, root.GetProperty("pid").GetInt32());
        Assert.Equal(JsonValueKind.String, root.GetProperty("pidStart").ValueKind);
        Assert.Equal("2026-11-05T09:58:12Z", root.GetProperty("pidStart").GetString());   // UTC ISO 8601 with Z, to the second, like `updated`
        Assert.Equal("2026-11-05T10:14:03Z", root.GetProperty("updated").GetString());
        Assert.Equal(15, root.GetProperty("ttlSec").GetInt32());
        Assert.Equal("running", root.GetProperty("state").GetString());
        Assert.Equal("Suite 3/7", root.GetProperty("short").GetString());
        Assert.Equal("Suite 3/7: smoke040, step 4/5 (BREAK)", root.GetProperty("summary").GetString());
        Assert.Equal("suite", root.GetProperty("owner").GetString());
        Assert.Equal("2026-11-05T10:13:40Z", root.GetProperty("since").GetString());
        var d = root.GetProperty("detail");
        Assert.Equal("suite", d.GetProperty("mode").GetString());
        Assert.Equal("smoke040", d.GetProperty("test").GetString());
        Assert.Equal(4, d.GetProperty("step").GetInt32());
        Assert.Equal(5, d.GetProperty("steps").GetInt32());
        Assert.Equal("BREAK", d.GetProperty("stepText").GetString());
        Assert.Equal(3, d.GetProperty("suite").GetProperty("index").GetInt32());
        Assert.Equal(7, d.GetProperty("suite").GetProperty("count").GetInt32());
        Assert.False(d.TryGetProperty("needsPerson", out _));
    }

    [Fact]
    public void Build_leaves_out_what_is_not_known_and_keeps_the_mode()
    {
        var root = Parse(StatusBoard.Build(StatusBoard.Compose(new StatusFacts()), 7, "Retrace 0.6.3", T0, null));
        Assert.False(root.TryGetProperty("short", out _));
        Assert.False(root.TryGetProperty("owner", out _));
        Assert.False(root.TryGetProperty("since", out _));
        Assert.False(root.TryGetProperty("pidStart", out _));   // the start time could not be read: the field is left out, not guessed
        Assert.Equal("idle", root.GetProperty("state").GetString());
        var d = root.GetProperty("detail");
        Assert.Equal("idle", d.GetProperty("mode").GetString());
        Assert.Equal(1, d.EnumerateObject().Count());
    }

    [Fact]
    public void Build_writes_the_reason_a_person_is_needed()
    {
        var e = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "t", Step = 1, Steps = 2, NeedsPerson = "a dialog Retrace cannot fill: Plot" });
        var root = Parse(StatusBoard.Build(e, 7, "Retrace 0.6.3", T0, T0));
        Assert.Equal("needs-person", root.GetProperty("state").GetString());
        Assert.Equal("a dialog Retrace cannot fill: Plot", root.GetProperty("detail").GetProperty("needsPerson").GetString());
    }

    [Fact]
    public void Build_keeps_non_ascii_and_escapes_quotes_in_one_line_of_valid_json()
    {
        var e = StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "Zeichnung \"Ä\" ü", Step = 1, Steps = 1, StepText = "tab\there" });
        var json = StatusBoard.Build(e, 7, "Retrace 0.6.3", T0, T0);
        Assert.DoesNotContain('\n', json);
        Assert.Equal("Zeichnung \"Ä\" ü", Parse(json).GetProperty("detail").GetProperty("test").GetString());
        Assert.Contains("Ä", json);   // UTF-8, not \u00C4
    }

    [Fact]
    public void Utc_is_the_contracts_iso_8601_with_Z()
    {
        Assert.Equal("2026-11-05T10:14:03Z", StatusBoard.Utc(T0));
        Assert.Equal("2026-11-05T10:14:03Z", StatusBoard.Utc(new DateTime(2026, 11, 5, 2, 14, 3, DateTimeKind.Local).ToUniversalTime()));
    }

    // ---- the atomic write -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WriteAtomic_creates_the_folder_replaces_the_file_and_leaves_no_temp_file()
    {
        var dir = Path.Combine(Temp(), "Gladmill", "acad-status");
        try
        {
            StatusBoard.WriteAtomic(dir, "retrace-1.json", "{\"a\":1}", 1);
            Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(dir, "retrace-1.json")));
            StatusBoard.WriteAtomic(dir, "retrace-1.json", "{\"a\":2}", 1);
            Assert.Equal("{\"a\":2}", File.ReadAllText(Path.Combine(dir, "retrace-1.json")));
            Assert.Equal(new[] { "retrace-1.json" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
            var bytes = File.ReadAllBytes(Path.Combine(dir, "retrace-1.json"));
            Assert.NotEqual(0xEF, bytes[0]);   // no byte order mark
        }
        finally { Clean(Path.GetDirectoryName(Path.GetDirectoryName(dir))!); }
    }

    [Fact]
    public void WriteAtomic_that_fails_throws_and_leaves_the_old_file_alone()
    {
        var dir = Temp();
        try
        {
            StatusBoard.WriteAtomic(dir, "retrace-1.json", "old", 1);
            Directory.CreateDirectory(Path.Combine(dir, "retrace-2.json"));   // a folder where the file should go: the move fails
            Assert.ThrowsAny<Exception>(() => StatusBoard.WriteAtomic(dir, "retrace-2.json", "new", 2));
            Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "retrace-1.json")));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
        }
        finally { Clean(dir); }
    }

    // ---- the writer -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Writer_writes_retrace_pid_json_and_skips_a_repeat_unless_forced()
    {
        var dir = Temp();
        try
        {
            var now = T0;
            var w = new StatusWriter(dir, 4812, "Retrace 0.6.3", utcNow: () => now);
            var idle = StatusBoard.Compose(new StatusFacts());
            Assert.True(w.Publish(idle));
            Assert.Equal(Path.Combine(dir, "retrace-4812.json"), w.FilePath);
            Assert.Equal("2026-11-05T10:14:03Z", Parse(File.ReadAllText(w.FilePath)).GetProperty("updated").GetString());
            now = T0.AddSeconds(1);
            Assert.False(w.Publish(idle));   // same entry: nothing written
            Assert.Equal("2026-11-05T10:14:03Z", Parse(File.ReadAllText(w.FilePath)).GetProperty("updated").GetString());
            now = T0.AddSeconds(3);
            Assert.True(w.Publish(idle, force: true));   // the heartbeat: `updated` moves
            Assert.Equal("2026-11-05T10:14:06Z", Parse(File.ReadAllText(w.FilePath)).GetProperty("updated").GetString());
            Assert.True(w.Publish(StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Record, Test = "t", RecordedSteps = 1 })));   // a change is written at once
            Assert.Equal("Rec 1", Parse(File.ReadAllText(w.FilePath)).GetProperty("short").GetString());
            Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void The_writer_puts_the_process_start_time_in_every_file_including_stopped()
    {
        var dir = Temp();
        try
        {
            var start = new DateTime(2026, 11, 5, 9, 58, 12, DateTimeKind.Utc);
            var w = new StatusWriter(dir, 4812, "Retrace 0.6.3", utcNow: () => T0, pidStart: start);
            Assert.True(w.Publish(StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "t", Step = 1, Steps = 2 })));
            Assert.Equal("2026-11-05T09:58:12Z", Parse(File.ReadAllText(w.FilePath)).GetProperty("pidStart").GetString());
            Assert.True(w.Stop());
            var stopped = Parse(File.ReadAllText(w.FilePath));
            Assert.Equal("stopped", stopped.GetProperty("state").GetString());
            Assert.Equal("2026-11-05T09:58:12Z", stopped.GetProperty("pidStart").GetString());
            var none = new StatusWriter(dir, 4813, "Retrace 0.6.3", utcNow: () => T0);   // start time unknown
            Assert.True(none.Publish(StatusBoard.Compose(new StatusFacts())));
            Assert.False(Parse(File.ReadAllText(none.FilePath)).TryGetProperty("pidStart", out _));
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void Since_moves_when_the_state_the_mode_or_the_test_changes_and_not_otherwise()
    {
        var dir = Temp();
        try
        {
            var now = T0;
            var w = new StatusWriter(dir, 1, "Retrace 0.6.3", utcNow: () => now);
            string Since() => Parse(File.ReadAllText(w.FilePath)).GetProperty("since").GetString()!;
            StatusEntry Replay(string test, int step) => StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = test, Step = step, Steps = 9 });
            w.Publish(Replay("a", 1));
            Assert.Equal("2026-11-05T10:14:03Z", Since());
            now = T0.AddSeconds(10);
            w.Publish(Replay("a", 2));   // another step of the same test
            Assert.Equal("2026-11-05T10:14:03Z", Since());
            now = T0.AddSeconds(20);
            w.Publish(Replay("b", 1));   // another test
            Assert.Equal("2026-11-05T10:14:23Z", Since());
            now = T0.AddSeconds(30);
            w.Publish(StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "b", Step = 1, Steps = 9, NeedsPerson = "x" }));   // another state
            Assert.Equal("2026-11-05T10:14:33Z", Since());
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void A_failed_write_never_throws_is_logged_once_and_retried_quietly()
    {
        var root = Temp();
        Directory.CreateDirectory(root);
        var blocked = Path.Combine(root, "not-a-folder");
        File.WriteAllText(blocked, "x");   // the status folder would have to be created under a file: every write fails
        try
        {
            var logged = new List<Exception>();
            var now = T0;
            var w = new StatusWriter(Path.Combine(blocked, "acad-status"), 1, "Retrace 0.6.3", logged.Add, () => now);
            var e = StatusBoard.Compose(new StatusFacts());
            Assert.False(w.Publish(e));
            for (var i = 0; i < 6; i++)   // a row of failures, each after the wait it asked for
            {
                now = now.Add(StatusWriter.MaxBackoff).AddSeconds(1);
                Assert.False(w.Publish(e, force: true));
                Assert.False(w.Publish(StatusBoard.Compose(new StatusFacts { Last = "Last: t PASS " + i })));
            }
            Assert.Single(logged);   // once for the whole row
        }
        finally { Clean(root); }
    }

    [Fact]
    public void The_wait_after_failures_grows_to_ten_seconds_and_only_starts_with_the_third_failure()
    {
        Assert.Equal(3, StatusWriter.FailuresBeforeBackoff);
        Assert.Equal(TimeSpan.Zero, StatusWriter.BackoffAfter(1));
        Assert.Equal(TimeSpan.Zero, StatusWriter.BackoffAfter(2));
        Assert.Equal(TimeSpan.FromSeconds(3), StatusWriter.BackoffAfter(3));
        Assert.Equal(TimeSpan.FromSeconds(6), StatusWriter.BackoffAfter(4));
        Assert.Equal(TimeSpan.FromSeconds(9), StatusWriter.BackoffAfter(5));
        Assert.Equal(TimeSpan.FromSeconds(10), StatusWriter.BackoffAfter(6));
        Assert.Equal(StatusWriter.MaxBackoff, StatusWriter.BackoffAfter(500));
        Assert.True(StatusWriter.MaxBackoff <= TimeSpan.FromSeconds(10));   // the contract's limit
    }

    [Fact]
    public void One_failed_write_is_retried_at_the_next_call_so_the_heartbeat_does_not_slip()
    {
        // a reader that holds the file for a moment makes the replace fail: the next call (a tick, the heartbeat) writes it, no wait
        var root = Temp();
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, "board");
        try
        {
            var now = T0;
            var w = new StatusWriter(folder, 1, "Retrace 0.6.3", utcNow: () => now);
            var e = StatusBoard.Compose(new StatusFacts());
            Assert.True(w.Publish(e));
            File.Delete(w.FilePath);
            Directory.CreateDirectory(w.FilePath);   // the replace fails
            now = T0.AddSeconds(3);
            Assert.False(w.Publish(e, force: true));   // the heartbeat fails once
            Directory.Delete(w.FilePath);               // the reader let go
            now = T0.AddSeconds(3).AddMilliseconds(200);
            Assert.True(w.Publish(e, force: true));    // 200 ms later: written, not 10 s later
            Assert.Equal("2026-11-05T10:14:06Z", Parse(File.ReadAllText(w.FilePath)).GetProperty("updated").GetString());
            // a second failure in a row is retried at once as well
            File.Delete(w.FilePath);
            Directory.CreateDirectory(w.FilePath);
            now = T0.AddSeconds(6);
            Assert.False(w.Publish(e, force: true));
            now = T0.AddSeconds(6).AddMilliseconds(200);
            Assert.False(w.Publish(e, force: true));
            Directory.Delete(w.FilePath);
            now = T0.AddSeconds(6).AddMilliseconds(400);
            Assert.True(w.Publish(e, force: true));
        }
        finally { Clean(root); }
    }

    [Fact]
    public void Three_failures_in_a_row_make_the_writer_wait_and_a_success_starts_the_count_again()
    {
        var root = Temp();
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, "board");
        try
        {
            var now = T0;
            var w = new StatusWriter(folder, 1, "Retrace 0.6.3", utcNow: () => now);
            var e = StatusBoard.Compose(new StatusFacts());
            Assert.True(w.Publish(e));
            File.Delete(w.FilePath);
            Directory.CreateDirectory(w.FilePath);   // every replace fails from here
            for (var i = 0; i < 3; i++) { now = T0.AddMilliseconds(200 * (i + 1)); Assert.False(w.Publish(e, force: true)); }   // the third failure, at T0 + 600 ms
            Directory.Delete(w.FilePath);             // the cause is gone, but the writer does not know
            var third = T0.AddMilliseconds(600);
            now = third.AddMilliseconds(200);
            Assert.False(w.Publish(StatusBoard.Compose(new StatusFacts { Last = "Last: a PASS" })));   // the replayer's next tick: no disk access
            Assert.False(w.Publish(e, force: true));                                                     // the heartbeat neither
            Assert.False(File.Exists(w.FilePath));
            now = third + StatusWriter.BackoffAfter(3) - TimeSpan.FromMilliseconds(1);
            Assert.False(w.Publish(e, force: true));
            Assert.False(File.Exists(w.FilePath));
            now = third + StatusWriter.BackoffAfter(3);
            Assert.True(w.Publish(e));   // after the wait the next write tries again, also an entry that did not change since
            Assert.True(File.Exists(w.FilePath));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp-*"));
            // the success started the count again: one failure is retried at once
            File.Delete(w.FilePath);
            Directory.CreateDirectory(w.FilePath);
            now = now.AddSeconds(3);
            Assert.False(w.Publish(e, force: true));
            Directory.Delete(w.FilePath);
            now = now.AddMilliseconds(200);
            Assert.True(w.Publish(e, force: true));
        }
        finally { Clean(root); }
    }

    [Fact]
    public void A_final_write_ignores_the_wait()
    {
        var root = Temp();
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, "board");
        try
        {
            var now = T0;
            var w = new StatusWriter(folder, 1, "Retrace 0.6.3", utcNow: () => now);
            Directory.CreateDirectory(Path.Combine(folder, "retrace-1.json"));
            for (var i = 0; i < 3; i++) Assert.False(w.Publish(StatusBoard.Compose(new StatusFacts { Last = "Last: " + i })));   // three failures: it waits now
            Directory.Delete(Path.Combine(folder, "retrace-1.json"));
            now = T0.AddSeconds(1);
            Assert.False(w.Stop("Status board turned off"));                    // not final: waits like the rest
            Assert.False(File.Exists(w.FilePath));
            Assert.True(w.Stop(final: true));                                   // Retrace unloading: the last word is tried at once
            Assert.Equal("stopped", Parse(File.ReadAllText(w.FilePath)).GetProperty("state").GetString());
        }
        finally { Clean(root); }
    }

    [Fact]
    public void A_stopped_write_that_failed_is_tried_again_at_every_tick_until_it_is_written()
    {
        // what the host does when the setting is turned off: Stop again at every tick until it returns true
        var root = Temp();
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, "board");
        try
        {
            var now = T0;
            var w = new StatusWriter(folder, 1, "Retrace 0.6.3", utcNow: () => now);
            Assert.True(w.Publish(StatusBoard.Compose(new StatusFacts())));
            File.Delete(w.FilePath);
            Directory.CreateDirectory(w.FilePath);   // the replace fails until the folder is removed
            var done = -1;
            for (var tick = 0; tick < 10 && done < 0; tick++)   // the 3 s ticks
            {
                now = T0.AddSeconds(3 * tick);
                if (tick == 3 && Directory.Exists(w.FilePath)) Directory.Delete(w.FilePath);   // the cause goes away at T0 + 9 s
                if (w.Stop("Status board turned off")) done = tick;
            }
            Assert.Equal(3, done);   // failures at ticks 0, 1, 2 (a wait of 3 s after the third), written at tick 3
            Assert.Equal("Status board turned off", Parse(File.ReadAllText(w.FilePath)).GetProperty("summary").GetString());
        }
        finally { Clean(root); }
    }

    [Fact]
    public void The_folder_is_created_for_the_first_write_and_again_only_after_a_failure()
    {
        var root = Temp();
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, "board");
        try
        {
            var w = new StatusWriter(folder, 1, "Retrace 0.6.3", utcNow: () => T0);
            var e = StatusBoard.Compose(new StatusFacts());
            Assert.True(w.Publish(e));   // created
            Directory.Delete(folder, true);
            Assert.False(w.Publish(e, force: true));   // it was there at the last write: not created again for this one, so it fails
            Assert.True(w.Publish(e, force: true));    // after the failure it is created again
            Assert.True(File.Exists(w.FilePath));
        }
        finally { Clean(root); }
    }

    [Fact]
    public void After_a_failed_write_the_same_entry_is_written_again_and_the_log_is_armed_again()
    {
        var root = Temp();
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, "board");
        try
        {
            var logged = new List<Exception>();
            var now = T0;
            Directory.CreateDirectory(Path.Combine(folder, "retrace-1.json"));   // a folder in the file's place: the replace fails
            var w = new StatusWriter(folder, 1, "Retrace 0.6.3", logged.Add, () => now);
            var e = StatusBoard.Compose(new StatusFacts());
            Assert.False(w.Publish(e));
            Assert.Single(logged);
            Directory.Delete(Path.Combine(folder, "retrace-1.json"));
            now = T0.AddMilliseconds(200);
            Assert.True(w.Publish(e));   // not skipped as "the same as before": the file never got it
            Assert.True(File.Exists(w.FilePath));
            Directory.Delete(folder, true);
            File.WriteAllText(folder, "x");   // fails again
            now = now.AddSeconds(3);
            Assert.False(w.Publish(e, force: true));
            Assert.Equal(2, logged.Count);   // a new failure after a success is logged again
        }
        finally { Clean(root); }
    }

    [Fact]
    public void A_logger_that_throws_does_not_break_the_writer()
    {
        var root = Temp();
        Directory.CreateDirectory(root);
        var blocked = Path.Combine(root, "f");
        File.WriteAllText(blocked, "x");
        try
        {
            var w = new StatusWriter(Path.Combine(blocked, "b"), 1, "Retrace 0.6.3", _ => throw new InvalidOperationException("log failed"));
            Assert.False(w.Publish(StatusBoard.Compose(new StatusFacts())));
        }
        finally { Clean(root); }
    }

    [Fact]
    public void Stop_writes_state_stopped()
    {
        var dir = Temp();
        try
        {
            var w = new StatusWriter(dir, 9, "Retrace 0.6.3");
            w.Publish(StatusBoard.Compose(new StatusFacts { Mode = StatusMode.Replay, Test = "t", Step = 1, Steps = 2 }));
            Assert.True(w.Stop());
            var root = Parse(File.ReadAllText(w.FilePath));
            Assert.Equal("stopped", root.GetProperty("state").GetString());
            Assert.Equal("Retrace unloaded", root.GetProperty("summary").GetString());
            Assert.Equal("retrace", root.GetProperty("source").GetString());
            Assert.Equal(9, root.GetProperty("pid").GetInt32());
            Assert.True(w.Stop("Status board turned off"));
            Assert.Equal("Status board turned off", Parse(File.ReadAllText(w.FilePath)).GetProperty("summary").GetString());
        }
        finally { Clean(dir); }
    }

    // ---- who started it ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_click_on_the_ribbon_button_makes_the_next_run_of_that_command_the_users()
    {
        var o = new StartOrigin();
        Assert.False(o.Take(T0, "RETRACERUN"));   // no click: no owner
        o.Click("^C^C_RETRACERUN ", T0);
        Assert.False(o.Take(T0.AddSeconds(1), "RETRACERECORD"));   // another command: not this click's
        Assert.True(o.Take(T0.AddSeconds(2), "retracerun"));   // any case
        Assert.False(o.Take(T0.AddSeconds(3), "RETRACERUN"));   // used up
    }

    [Theory]
    [InlineData("^C^C_RETRACESTEPS ", "RETRACESTEP", false)]   // another command that starts with the same letters
    [InlineData("^C^C_RETRACESTEP ", "RETRACESTEPS", false)]
    [InlineData("^C^C_RETRACESTEP ", "RETRACESTEP", true)]
    [InlineData("^C^C_RETRACESTEPS ", "RETRACESTEPS", true)]
    [InlineData("_RETRACERUN ", "RETRACERUN", true)]            // the Open test window's
    [InlineData("RETRACERUN", "RETRACERUN", true)]
    [InlineData("^C^C_-RETRACESTOP ", "RETRACESTOP", true)]     // the recorder's rule for a macro: a dash form names the same command (none of the four that start a run has one)
    [InlineData("^C^C_RETRACERUN;", "RETRACERUN", true)]
    [InlineData("^C^C_.RETRACERUN ", "RETRACERUN", true)]
    [InlineData("^C^C_MYRETRACERUN ", "RETRACERUN", false)]
    [InlineData("^C^C_RETRACERECORD ", "RETRACERUN", false)]
    public void A_click_is_matched_on_the_whole_command_name(string macro, string command, bool credited)
    {
        var o = new StartOrigin();
        o.Click(macro, T0);
        Assert.Equal(credited, o.Take(T0.AddSeconds(1), command));
    }

    [Fact]
    public void A_click_whose_command_never_started_is_not_credited_to_a_later_invocation_of_another_kind_or_a_late_one()
    {
        var o = new StartOrigin();
        // the host asks at the start of every RETRACERUN / RETRACERECORD / RETRACEREPLAY / RETRACESTEP: the click is used up by the first that matches...
        o.Click("^C^C_RETRACERUN ", T0);
        Assert.True(o.Take(T0.AddSeconds(1), "RETRACERUN"));
        Assert.False(o.Take(T0.AddSeconds(2), "RETRACERUN"));   // ...so a typed or COM one right after has no owner
        // a click the command never answered (refused, Esc at the picker) is not live for long
        o.Click("^C^C_RETRACERUN ", T0);
        Assert.False(o.Take(T0 + StartOrigin.Window + TimeSpan.FromMilliseconds(1), "RETRACERUN"));
        Assert.True(StartOrigin.Window <= TimeSpan.FromSeconds(30));
        // a newer click replaces an older one
        o.Click("^C^C_RETRACERECORD ", T0);
        o.Click("^C^C_RETRACESTEP ", T0.AddSeconds(2));
        Assert.False(o.Take(T0.AddSeconds(3), "RETRACERECORD"));
        Assert.True(o.Take(T0.AddSeconds(3), "RETRACESTEP"));
    }

    [Fact]
    public void An_old_click_or_a_clock_that_went_back_gives_no_owner()
    {
        var o = new StartOrigin();
        o.Click("^C^C_RETRACERUN ", T0);
        Assert.False(o.Take(T0 + StartOrigin.Window + TimeSpan.FromSeconds(1), "RETRACERUN"));
        o.Click("_RETRACERUN ", T0);
        Assert.False(o.Take(T0.AddSeconds(-5), "RETRACERUN"));
        o.Click("   ", T0);
        Assert.False(o.Take(T0, "RETRACERUN"));
    }

    // ---- the setting ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Status_board_setting_is_on_by_default_and_off_when_the_file_says_so()
    {
        Assert.True(new RetraceSettings().StatusBoard);
        var root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Paths.SettingsFile(root), "{ \"screenshots\": false }");   // key missing: on
            Assert.True(RetraceSettings.Load(root).StatusBoard);
            File.WriteAllText(Paths.SettingsFile(root), "{ \"statusBoard\": false }");
            Assert.False(RetraceSettings.Load(root).StatusBoard);
            var s = new RetraceSettings { RootDir = root, StatusBoard = false };
            Assert.True(s.Save(out var err), err);
            Assert.Contains("\"statusBoard\": false", File.ReadAllText(Paths.SettingsFile(root)));
            Assert.False(s.Clone().StatusBoard);
        }
        finally { Clean(root); }
    }

    [Fact]
    public void TryLoad_says_whether_the_settings_file_was_understood()
    {
        var root = Temp();
        try
        {
            Directory.CreateDirectory(root);
            Assert.True(RetraceSettings.TryLoad(out var missing, root));   // no file: the defaults, nothing wrong
            Assert.True(missing.StatusBoard);
            File.WriteAllText(Paths.SettingsFile(root), "{ \"statusBoard\": false }");
            Assert.True(RetraceSettings.TryLoad(out var off, root));
            Assert.False(off.StatusBoard);
            Assert.Equal(root, off.RootDir);
            File.WriteAllText(Paths.SettingsFile(root), "{ \"statusBoard\": false, ");   // half a file: a hand edit or a write in progress
            Assert.False(RetraceSettings.TryLoad(out var broken, root));
            Assert.True(broken.StatusBoard);   // the defaults: the caller must not take them for the user's choice
            Assert.Equal(root, broken.RootDir);
            Assert.True(RetraceSettings.Load(root).StatusBoard);   // Load keeps giving the defaults for it
            File.WriteAllText(Paths.SettingsFile(root), "null");
            Assert.False(RetraceSettings.TryLoad(out _, root));
            File.WriteAllText(Paths.SettingsFile(root), "{ \"statusBoard\": \"maybe\" }");   // the wrong type
            Assert.False(RetraceSettings.TryLoad(out _, root));
        }
        finally { Clean(root); }
    }
}
