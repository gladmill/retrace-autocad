using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>A picture of AutoCAD's main window for the evidence workbook (the tester's camera and an automated replay): the screen as it is, work area only, so palettes and dialogs that belong to AutoCAD are in it and the
/// task bar is not. The caller hides Retrace's own windows first. Same capture as the step pictures (<see cref="ShotService.CaptureWindow"/>); scaled down to 1280 px on the long side (the width the workbook stores).</summary>
static class EvidenceShot
{
    /// <summary>Takes the picture now (UI thread) and returns the pixels, or null when the window cannot be captured (minimized, hidden).</summary>
    public static (byte[] Pixels, int W, int H)? Take(IntPtr main)
    {
        try
        {
            if (main != IntPtr.Zero) ScreenSpace.Reference = main;
            if (ShotService.CaptureWindow(main, workArea: true) is not { } w) return null;
            var (nw, nh) = ShotImage.FitSize(w.Pic.Width, w.Pic.Height, 1280);   // the width the workbook stores (ExportXlsx.MaxStoredWidth): never scaled twice
            return nw == w.Pic.Width && nh == w.Pic.Height ? (w.Pic.Pixels, nw, nh) : (ShotImage.Scale(w.Pic.Pixels, w.Pic.Width, w.Pic.Height, nw, nh), nw, nh);
        }
        catch (Exception ex) { Log.Error("evidence capture", ex); return null; }
    }

    /// <summary>The picture for a dialog moment. The dialog is brought onto the screen first when it lies partly off it (as the Tdoc does). <paramref name="aloneIfSmall"/>: when the dialog is smaller than about half of AutoCAD's window in both
    /// directions, the DIALOG ALONE is the picture (its values can be read in the sheet's cell); else AutoCAD's window, widened to the union with the dialog when the dialog is not inside it. Returns what was chosen: "dialog", "window" or "union".</summary>
    public static ((byte[] Pixels, int W, int H) Pic, string What)? TakeWithDialog(IntPtr main, IntPtr dialog, bool aloneIfSmall)
    {
        try
        {
            if (main != IntPtr.Zero) ScreenSpace.Reference = main;
            if (dialog == IntPtr.Zero || !Native.Exists(dialog)) return Take(main) is { } w0 ? (w0, "window") : null;
            Native.MoveOntoScreen(dialog);
            var d = PaletteReveal.RectOf(dialog);
            if (ShotService.CaptureWindow(main, workArea: true) is not { } win) return null;
            int ml = win.Left, mt = win.Top, mr = ml + win.Pic.SrcWidth, mb = mt + win.Pic.SrcHeight;
            if (aloneIfSmall && d.Width > 0 && d.Height > 0 && d.Width * 2 <= win.Pic.SrcWidth && d.Height * 2 <= win.Pic.SrcHeight && ShotService.CaptureWindow(dialog) is { } own)
                return (Scaled(own.Pic), "dialog");
            bool inside = d.Left >= ml && d.Top >= mt && d.Right <= mr && d.Bottom <= mb;
            if (inside) return (Scaled(win.Pic), "window");
            var u = ShotService.CaptureRect(Math.Min(ml, d.Left), Math.Min(mt, d.Top), Math.Max(mr, d.Right), Math.Max(mb, d.Bottom), main);
            return u == null ? (Scaled(win.Pic), "window") : (Scaled(u), "union");
        }
        catch (Exception ex) { Log.Error("evidence dialog capture", ex); return null; }
    }

    static (byte[] Pixels, int W, int H) Scaled(ShotService.Picture p)
    {
        var (nw, nh) = ShotImage.FitSize(p.Width, p.Height, 1280);
        return nw == p.Width && nh == p.Height ? (p.Pixels, nw, nh) : (ShotImage.Scale(p.Pixels, p.Width, p.Height, nw, nh), nw, nh);
    }

    /// <summary>Writes the PNG through a temp file (so nobody reads half a picture). Call from a pool thread.</summary>
    public static void WritePng(string path, byte[] pixels, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".tmp");
        try { Png.Write(tmp, w, h, pixels); File.Move(tmp, path, overwrite: true); }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
