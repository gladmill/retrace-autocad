using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using AcadStatus.AutoCad;
using AcadStatus.Core;

namespace AcadStatus.Ui;

/// <summary>The small floating window that opens when the pane is clicked: every entry (source, state, summary, age, owner, the dialog and
/// the prompt), refreshed once a second, and a "Copy for Claude" button. It closes on Esc, on losing focus and on a second click on the
/// pane. Not a palette: it docks nowhere and stays only while it has the focus.
/// <para>The text is rebuilt only when what it says changes. The ages that tick every second are replaced in place (<see cref="EntryBlock.Timing"/>),
/// so a window that nothing happens in costs no rebuild, no flicker and no new fonts.</para></summary>
sealed class DetailsForm : Form
{
    const int FormWidth = 470, MinHeight = 110, MaxHeight = 380, ButtonBar = 36;

    const int EM_GETSCROLLPOS = 0x04DD, EM_SETSCROLLPOS = 0x04DE, WM_SETREDRAW = 0x000B;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref NativePoint p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    readonly RichTextBox _text = new();
    readonly Button _copy = new();
    readonly Label _note = new();
    readonly Func<string> _dump;
    readonly Action<long> _onDeactivated;
    readonly Font _regular = new("Segoe UI", 9f);
    readonly Font _bold;
    readonly List<(int Start, string Text)> _timings = new();   // where the age of each block is in the text, and what it says now
    string _structureKey = "";
    int _clickX, _barTop;
    Rectangle _area;

    /// <param name="onDeactivated">Called with the identity of the Windows message being handled when the window loses focus (see <see cref="ClickGuard"/>).</param>
    public DetailsForm(Func<string> dump, Action<long> onDeactivated)
    {
        _dump = dump;
        _onDeactivated = onDeactivated;
        _bold = new Font(_regular, FontStyle.Bold);
        Text = "AutoCAD status";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        KeyPreview = true;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = _regular;
        ClientSize = new Size(FormWidth, MinHeight);

        _text.ReadOnly = true;
        _text.BorderStyle = BorderStyle.None;
        _text.BackColor = SystemColors.Window;
        _text.ScrollBars = RichTextBoxScrollBars.Vertical;
        _text.DetectUrls = false;
        _text.TabStop = false;
        _text.Dock = DockStyle.Fill;

        var bar = new Panel { Dock = DockStyle.Bottom, Height = ButtonBar, BackColor = SystemColors.Control };
        _copy.Text = "Copy for Claude";
        _copy.AutoSize = true;
        _copy.Location = new Point(8, 5);
        _copy.Click += (_, _) => CopyDump();
        _note.AutoSize = true;
        _note.ForeColor = SystemColors.GrayText;
        _note.Location = new Point(140, 10);
        bar.Controls.Add(_copy);
        bar.Controls.Add(_note);
        Controls.Add(_text);
        Controls.Add(bar);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };
        Deactivate += (_, _) =>
        {
            if (!Visible || IsDisposed) return;
            _onDeactivated(MessageClock.Id());   // now, while the message that caused it is the one being handled
            BeginInvoke(new Action(() => { if (!IsDisposed && !ContainsFocus) Close(); }));
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _bold.Dispose();
            _regular.Dispose();
        }
    }

    /// <summary>Places the window above the status bar, centred on the click, inside the working area of the screen the click is on.</summary>
    public void PlaceAbove(Point click, int barTop)
    {
        _clickX = click.X;
        _barTop = barTop;
        _area = Screen.FromPoint(click).WorkingArea;
        Reanchor();
    }

    /// <summary>Sets the location from the click and the bar (again after a change of height: the bottom edge stays where it is).</summary>
    void Reanchor()
    {
        if (_area.IsEmpty) return;
        var (x, y) = Placement.Above(_clickX, _barTop, Width, Height, _area.Left, _area.Top, _area.Right, _area.Bottom);
        Location = new Point(x, y);
    }

    void CopyDump()
    {
        try
        {
            var why = ClipboardText.Set(_dump(), Handle);
            _note.Text = why == null ? "Copied." : "Could not copy: " + why;
            if (why != null) Log.Info("copy for Claude: " + why);
        }
        catch (Exception ex)
        {
            _note.Text = "Could not copy: " + ex.Message;
            Log.Error("copy for Claude", ex);
        }
    }

    /// <summary>Shows the entries. Rebuilds the text when what it says changed; otherwise only the ages are replaced.</summary>
    public void SetEntries(MergeResult merged, IReadOnlyList<Unreadable> skipped, DateTime nowUtc)
    {
        var blocks = new List<EntryBlock>();
        foreach (var m in merged.Shown) blocks.Add(StatusDump.Block(m, nowUtc));
        var key = StructureKey(blocks, merged, skipped);
        if (key == _structureKey) { UpdateTimings(blocks); return; }
        _structureKey = key;
        Rebuild(blocks, merged, skipped);
    }

    /// <summary>Everything the text says except the ages.</summary>
    static string StructureKey(List<EntryBlock> blocks, MergeResult merged, IReadOnlyList<Unreadable> skipped)
    {
        var key = new StringBuilder();
        foreach (var b in blocks) { key.Append((int)b.Tone).Append(b.Lead).Append('\n'); foreach (var l in b.Lines) key.Append(l).Append('\n'); }
        foreach (var i in merged.Ignored) key.Append("i:").Append(i.Entry.File).Append(i.Why).Append('\n');   // not the age: it ticks
        foreach (var s in skipped) key.Append("s:").Append(s.File).Append(s.Reason).Append('\n');   // the reason too: a file can go from "half written" to "bad JSON"
        return key.ToString();
    }

    /// <summary>Runs a change of the text with painting off and the scroll position kept.</summary>
    void Batch(Action change)
    {
        var scroll = new NativePoint();
        SendMessage(_text.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);
        SendMessage(_text.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try { change(); }
        finally
        {
            SendMessage(_text.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);
            SendMessage(_text.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            _text.Invalidate();
        }
    }

    void Rebuild(List<EntryBlock> blocks, MergeResult merged, IReadOnlyList<Unreadable> skipped)
    {
        int lines = 0;
        Batch(() =>
        {
            _text.Clear();
            _timings.Clear();
            if (blocks.Count == 0) { Append("No status entries.\n", SystemColors.GrayText, false); lines++; }
            foreach (var b in blocks)
            {
                var c = TonePalette.Of(b.Tone);
                Append("● ", Color.FromArgb(c.R, c.G, c.B), true);
                Append(b.Lead, SystemColors.WindowText, true);
                _timings.Add((_text.TextLength, b.Timing));
                Append(b.Timing + "\n", SystemColors.WindowText, true);
                lines++;
                foreach (var l in b.Lines) { Append("   " + l + "\n", SystemColors.WindowText, false); lines += 1 + l.Length / 70; }
            }
            var rest = merged.Ignored.Count + skipped.Count;
            if (rest > 0)
            {
                var parts = new List<string>();
                foreach (var i in merged.Ignored) parts.Add("ignored " + i.Entry.File + " (" + i.Why + ")");
                foreach (var s in skipped) parts.Add("skipped " + s.File + " (" + s.Reason + ")");
                const int show = 3;
                Append(string.Join("\n", parts.GetRange(0, Math.Min(show, parts.Count))) + (parts.Count > show ? "\n... and " + (parts.Count - show) + " more" : "") + "\n", SystemColors.GrayText, false);
                lines += Math.Min(show, parts.Count) + (parts.Count > show ? 1 : 0);
            }
            _text.SelectionStart = 0;
        });

        // grow or shrink with the text, keeping the bottom edge where it is
        int h = Math.Max(MinHeight, Math.Min(MaxHeight, lines * 17 + 18 + ButtonBar));
        if (h != ClientSize.Height)
        {
            ClientSize = new Size(FormWidth, h);
            Reanchor();
        }
    }

    /// <summary>Replaces the ages that changed, in place (the text before and after, its colours and its scroll position stay).</summary>
    void UpdateTimings(List<EntryBlock> blocks)
    {
        if (blocks.Count != _timings.Count) return;
        bool any = false;
        for (int i = 0; i < blocks.Count; i++) if (_timings[i].Text != blocks[i].Timing) { any = true; break; }
        if (!any) return;
        Batch(() =>
        {
            int shift = 0;   // how far the text after the ranges changed so far has moved
            for (int i = 0; i < blocks.Count; i++)
            {
                var (start, old) = _timings[i];
                start += shift;
                var now = blocks[i].Timing;
                if (old != now)
                {
                    _text.Select(start, old.Length);
                    _text.SelectedText = now;
                    shift += now.Length - old.Length;
                }
                _timings[i] = (start, now);
            }
            _text.SelectionStart = 0;
            _text.SelectionLength = 0;
        });
    }

    void Append(string text, Color color, bool bold)
    {
        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.SelectionColor = color;
        _text.SelectionFont = bold ? _bold : _regular;
        _text.AppendText(text);
    }
}
