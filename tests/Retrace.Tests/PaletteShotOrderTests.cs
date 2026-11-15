using System.Text.Json;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

public class PaletteShotOrderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-shotorder").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    // a click that opens a message box: the window picture is written after the palette close-up, but the step lists the click picture first
    [Fact]
    public void A_palette_step_lists_its_click_picture_before_the_close_up()
    {
        var p = Path.Combine(_dir, "recording.jsonl");
        File.WriteAllLines(p, new[]
        {
            """{"type":"header","schema":1,"name":"t","started":"2026-11-06T10:00:00"}""",
            """{"t":1,"n":1,"type":"palette_click","doc":"D.dwg","palette":"P","set":"S","button":"Refresh","kind":"toolstrip","strip":"s","stripIndex":1,"position":1,"count":1}""",
            """{"t":2,"n":2,"type":"shot","for":1,"file":"screenshots/a-palette.png","what":"palette"}""",
            """{"t":3,"n":3,"type":"shot","for":1,"file":"screenshots/b-click.png","what":"window"}""",
        });
        var s = RecordingReader.Load(p);
        var step = Assert.Single(s.Steps, x => x.Kind == "palette");
        Assert.Equal(new[] { "window", "palette" }, step.Shots.Select(x => x.What));
    }
}
