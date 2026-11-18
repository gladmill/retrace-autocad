using Retrace.Core;

namespace Retrace.Tests;

public class WindowSizesTests
{
    [Fact]
    public void Round_trip_and_other_windows_are_kept()
    {
        var root = Path.Combine(Path.GetTempPath(), "ws-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(WindowSizes.Read("MapWindow", root));
            Assert.True(WindowSizes.Write("MapWindow", 1000.4, 700, root));
            Assert.True(WindowSizes.Write("TdocWindow", 800, 500, root));
            Assert.Equal((1000d, 700d), WindowSizes.Read("MapWindow", root));
            Assert.Equal((800d, 500d), WindowSizes.Read("TdocWindow", root));
            Assert.False(WindowSizes.Write("X", 10, 500, root));   // not a sane size
            Assert.Null(WindowSizes.Read("X", root));
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"A\":[1,2,3],\"B\":\"x\",\"C\":[\"a\",\"b\"],\"D\":[5,5]}")]
    public void Broken_or_insane_input_is_an_empty_list(string? json) => Assert.Empty(WindowSizes.Parse(json));

    [Fact]
    public void Valid_entries_survive_next_to_bad_ones() => Assert.Equal((900d, 600d), WindowSizes.Parse("{\"A\":[1,2],\"B\":[900,600]}")["B"]);
}
