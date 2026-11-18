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

    /// <summary>Writes the PNG through a temp file (so nobody reads half a picture). Call from a pool thread.</summary>
    public static void WritePng(string path, byte[] pixels, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".tmp");
        try { Png.Write(tmp, w, h, pixels); File.Move(tmp, path, overwrite: true); }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
