using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace Retrace.AutoCad;

/// <summary>A smaller copy of a picture for the Excel sheet (<see cref="Retrace.Engine.Recordings.ExportXlsx.Rescale"/>), made with WPF's imaging (no new package). The engine itself has no image codec.
/// Safe on any thread: the bitmaps are frozen and nothing touches a window.</summary>
public static class ImageScale
{
    /// <summary>Called once at start-up; the exporter only uses it for a picture wider than <see cref="Retrace.Engine.Recordings.ExportXlsx.MaxStoredWidth"/>.</summary>
    public static void Install() => Retrace.Engine.Recordings.ExportXlsx.Rescale = Shrink;

    /// <summary>The PNG of <paramref name="file"/> scaled to <paramref name="maxWidth"/> px wide (aspect kept), or null when it is not wider than that.</summary>
    public static (byte[] Png, int Width, int Height)? Shrink(string file, int maxWidth)
    {
        using var fs = File.OpenRead(file);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= maxWidth) return null;
        double k = maxWidth / (double)frame.PixelWidth;
        var scaled = new TransformedBitmap(frame, new ScaleTransform(k, k));
        scaled.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return (ms.ToArray(), scaled.PixelWidth, scaled.PixelHeight);
    }
}
