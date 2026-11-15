using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Draws the test document's frame and arrow onto a picture with the production drawing code, for looking at. Does nothing unless RETRACE_RENDER_RAW names a raw 32-bit BGRA file
/// (RETRACE_RENDER_W x RETRACE_RENDER_H) and RETRACE_RENDER_OUT a folder; the rectangles are RETRACE_RENDER_BOXES = "name:l,t,r,b;...".</summary>
public class ArrowRenderTests
{
    [Fact]
    public void Render_the_arrows_onto_a_picture_when_asked()
    {
        var raw = Environment.GetEnvironmentVariable("RETRACE_RENDER_RAW");
        var outDir = Environment.GetEnvironmentVariable("RETRACE_RENDER_OUT");
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(outDir)) return;
        int w = int.Parse(Environment.GetEnvironmentVariable("RETRACE_RENDER_W")!), h = int.Parse(Environment.GetEnvironmentVariable("RETRACE_RENDER_H")!);
        var bytes = File.ReadAllBytes(raw);
        foreach (var spec in Environment.GetEnvironmentVariable("RETRACE_RENDER_BOXES")!.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = spec[..spec.IndexOf(':')];
            var n = spec[(spec.IndexOf(':') + 1)..].Split(',').Select(int.Parse).ToArray();
            var px = (byte[])bytes.Clone();
            var box = new PxRect(n[0], n[1], n[2], n[3]);
            var arrow = ClickArrow.ForBoxQuiet(box, new PxRect(0, 0, w, h), w, h, px);   // decided on the picture before the frame is drawn, as ShotService does
            var ab = Annotate.ToPicture(n[0], n[1], n[2], n[3], 0, 0, w, h, w, h, 0)!.Value;
            Annotate.Draw(px, w, h, new[] { ab });
            if (arrow is { } a) Annotate.DrawArrow(px, w, h, a);
            Directory.CreateDirectory(outDir);
            Png.Write(Path.Combine(outDir, name + ".png"), w, h, px);
        }
    }
}
