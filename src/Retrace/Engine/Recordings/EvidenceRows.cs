// Which evidence picture belongs on which row of the sheet. NO AutoCAD references in this file (linked into the engine test project).
using System.Globalization;
using Retrace.Engine.Emit;

namespace Retrace.Engine.Recordings;

/// <summary>The pictures taken for one replay step. <paramref name="Opened"/>: a dialog step's dialog, on the screen, before it was filled in. <paramref name="Filled"/>: that dialog as filled in, just before the button that closes it
/// was pressed. <paramref name="Ended"/>: AutoCAD after the step had run and settled (for the last dialog step of a command: after the command ended). <paramref name="Failed"/>: what AutoCAD showed when the test ended on this step. <paramref name="OpenedNoWindow"/>: the dialog never had a window on the screen, so no "open" picture was taken.</summary>
public sealed record StepShots(string? Opened = null, string? Filled = null, string? Ended = null, string? Failed = null, bool OpenedNoWindow = false);

/// <summary>What the workbook shows on one row: its picture (null = none) and a note about it.</summary>
public sealed record RowEvidence(string? Picture, string? Note);

/// <summary>
/// One sheet row gets the picture of the screen right after ITS action, never a later one. The rows of a numbered step are its own row (<c>N</c>: the click, the typed command, the palette click, the checkpoint) and one row per
/// sub-item (<c>N.1</c> ...: an answer at a prompt, a dialog the command opened, see <see cref="WordingBullet"/>). The replay runs the command in one go and pictures it at these moments only: the dialog is open (not filled yet), the
/// dialog is filled in (before its closing button), the step has ended. So:
///  - a dialog's row: the dialog as filled in (else as it opened, said in the note);
///  - the step's own row and the answers asked before the first dialog: the screen when that dialog is open (when the test ended before that, what the screen showed then); the answers between two dialogs: when the second is open; the answers after the last dialog (or all of them when the command
///    has no dialog): the screen after the command ended;
///  - of several rows that share one such moment (the answers are typed in one go) the last one gets the picture and the others say where it is; a picture is never put on a row before the moment it shows.
/// A row whose own picture was not taken stays empty and says so in Notes (a status bar click made while the command ran is not clicked by the replay: its note says that). When the sheet and the recording do not agree on the rows of a step (the row count, which sub-rows are dialogs), only the step's last row can be told: it gets the last end picture there is.
/// </summary>
public static class EvidenceRows
{
    /// <summary>The evidence of the rows of one numbered step, by row number (0 = the step's own row <c>N</c>, k = <c>N.k</c>). <paramref name="steps"/>: the replay steps of the step in order (its first, then the dialogs of its command);
    /// <paramref name="bullets"/>: its sub-items as the script wrote them; <paramref name="rowCount"/>: how many rows the sheet has for it (1 + sub-items); <paramref name="number"/>: the step's number, for the notes; <paramref name="subRowsFilled"/>: for each sub-row of the sheet whether it is a dialog filled in (<see cref="SheetRow.Filled"/>; null = not checked); a Dialog sub-item must sit exactly on such a row, else the sheet is not the script of this recording. <paramref name="nextDialogOpened"/>: the picture of the dialog the NEXT numbered step is (a click whose dialog the recording made a step of its own), taken when it opened: the screen right after this step's click, with the dialog open, which is all this step's own row can show.</summary>
    public static Dictionary<int, RowEvidence> Place(string number, IReadOnlyList<ReplayStepInfo> steps, IReadOnlyList<WordingBullet> bullets, int rowCount, string? nextDialogOpened = null, IReadOnlyList<bool>? subRowsFilled = null)
    {
        var map = new Dictionary<int, RowEvidence>();
        string Label(int row) => row == 0 ? number : $"{number}.{row}";
        var run = steps.Where(s => s.Kind != "tab").OrderBy(s => s.Index).ToList();
        if (run.Count == 0) { for (int r = 0; r < rowCount; r++) map[r] = new RowEvidence(null, null); return map; }
        var first = run[0];
        var dialogs = run.Skip(1).ToList();
        int dialogBullets = bullets.Count(b => b.Kind == WordingBulletKind.Dialog);
        bool topDialog = first.Kind == "dialog";
        var lastStep = run[^1];

        // the sheet and the recording disagree (rows, dialogs): only the last row can be told
        bool lines = rowCount == 1 + bullets.Count && !dialogs.Any(d => d.Kind != "dialog") && (topDialog ? dialogs.Count == 0 && bullets.Count == 0 : dialogBullets == dialogs.Count);
        if (lines && subRowsFilled != null)
        {
            if (subRowsFilled.Count != bullets.Count) lines = false;
            else for (int k = 0; k < bullets.Count && lines; k++) if ((bullets[k].Kind == WordingBulletKind.Dialog) != subRowsFilled[k]) lines = false;   // a dialog sub-item sits exactly on a dialog row
        }
        if (!lines)
        {
            var endPic = run.LastOrDefault(s => s.Ended != null)?.Ended ?? lastStep.Failed;
            for (int r = 0; r < rowCount - 1; r++) map[r] = new RowEvidence(null, r == 0 ? "No picture of this row alone: the sheet's rows and the recorded steps do not line up here." : null);
            map[rowCount - 1] = new RowEvidence(endPic, rowCount <= 1 ? null : endPic != null
                ? "The sheet's rows and the recorded steps do not line up here: this picture shows the screen after the whole step."
                : "The sheet's rows and the recorded steps do not line up here, and no picture was taken for this step.");
            return map;
        }

        if (topDialog) { map[0] = DialogRow(first, ownRow: true); return map; }

        // slots: slot s = the rows asked before dialog s+1 (s < m) or after the last one (s = m); row 0 is in slot 0. A dialog's row is not in a slot.
        int m = dialogs.Count;
        var slotRows = new List<int>[m + 1];
        for (int s = 0; s <= m; s++) slotRows[s] = [];
        slotRows[0].Add(0);
        int seen = 0;
        for (int k = 0; k < bullets.Count; k++)
        {
            int row = k + 1;
            switch (bullets[k].Kind)
            {
                case WordingBulletKind.Dialog:
                    map[row] = DialogRow(dialogs[seen], ownRow: false);
                    seen++;
                    break;
                case WordingBulletKind.Status:
                    map[row] = new RowEvidence(null, "The replay does not click this (it was done while the command ran): no picture.");
                    break;
                default:
                    slotRows[seen].Add(row);
                    break;
            }
        }
        for (int s = 0; s <= m; s++)
        {
            var rows = slotRows[s];
            if (rows.Count == 0) continue;
            string? pic = s < m ? dialogs[s].Opened : (m == 0 ? first : dialogs[m - 1]).Ended;
            if (pic == null && s == 0) pic = first.Failed;   // the test ended on the click (or while its dialog was pending): the failure picture is the slot's, whatever the dialog count
            if (pic == null && s == m && !topDialog) pic ??= nextDialogOpened;
            int target = rows[^1];
            foreach (var row in rows)
            {
                if (row == target)
                {
                    string? why = pic != null ? null : s < m && dialogs[s].OpenedNoWindow ? "No picture was taken: no dialog window was on the screen when the command went modal." : "No picture was taken for this row.";
                    map[row] = new RowEvidence(pic, why);
                    continue;
                }
                map[row] = new RowEvidence(null, pic == null ? null : row == 0
                    ? $"No picture of this row alone: the replay does this and the typing that follows in one go; the screen after the last of them is on row {Label(target)}."
                    : $"No picture of this row alone: the replay types this and the rows after it in one go; the screen after the last of them is on row {Label(target)}.");
            }
        }
        for (int r = 0; r < rowCount; r++) if (!map.ContainsKey(r)) map[r] = new RowEvidence(null, null);
        return map;
    }

    /// <summary>The row of a dialog: the dialog as filled in; else what the screen showed when the test ended on it; else as it opened, and the note says so.</summary>
    static RowEvidence DialogRow(ReplayStepInfo d, bool ownRow)
    {
        if (d.Filled == null && d.Failed == null && d.Opened == null && d.OpenedNoWindow) return new RowEvidence(ownRow ? d.Ended : null, "No picture of the dialog was taken: no dialog window was on the screen when the command went modal.");
        if (d.Filled != null) return new RowEvidence(d.Filled, null);
        if (d.Failed != null) return new RowEvidence(d.Failed, "The test ended here: the picture shows the screen then.");
        if (d.Opened != null) return new RowEvidence(d.Opened, "The dialog could not be pictured filled in (the replay did not fill it in, or it closed too soon): this picture shows it as it opened.");
        if (ownRow && d.Ended != null) return new RowEvidence(d.Ended, "The dialog was not on the screen when it was to be pictured (it did not open, or closed at once): this picture shows the screen after the step.");
        return new RowEvidence(null, "No picture of the dialog was taken.");
    }

    /// <summary>The step number a row label belongs to ("3" and "3.2" both 3), 0 when it is not a number.</summary>
    public static int MainOf(string label) => int.TryParse(label.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>How many rows the sheet has for each numbered step (index = step number - 1, length = <paramref name="steps"/>).</summary>
    public static int[] RowCounts(StepsDoc doc, int steps)
    {
        var c = new int[steps];
        foreach (var row in doc.Rows) if (MainOf(row.Label) is var n and >= 1 && n <= steps) c[n - 1]++;
        return c;
    }

    /// <summary>What the sheet looks like per numbered step: the row count and, for the sub-rows, whether each is a dialog filled in. Null when the sheet has a different number of steps than the recording has numbered ones (a steps.md that is
    /// hand-edited or from an older version): then no row can be told from another and the pictures keep to the step's own row.</summary>
    public static (int[] RowCounts, List<bool[]> SubRowsFilled)? Shape(StepsDoc doc, int numberedSteps)
    {
        if (doc.Steps.Count != numberedSteps) return null;
        var counts = RowCounts(doc, numberedSteps);
        var filled = new List<bool[]>();
        for (int n = 1; n <= numberedSteps; n++)
            filled.Add(doc.Rows.Where(r => MainOf(r.Label) == n).Skip(1).Select(r => r.Filled).ToArray());
        return (counts, filled);
    }
}
