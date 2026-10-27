using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Shift+K", "Ctrl+Shift+K")]
    [InlineData("shift+ctrl+k", "Ctrl+Shift+K")]
    [InlineData(" CTRL + f7 ", "Ctrl+F7")]
    [InlineData("F8", "F8")]
    [InlineData("f24", "F24")]
    [InlineData("Ctrl+1", "Ctrl+1")]
    [InlineData("shift+control+z", "Ctrl+Shift+Z")]
    [InlineData("Shift+F1", "Shift+F1")]
    public void Parses_and_normalises(string text, string expected)
    {
        Assert.True(KeyChord.TryParse(text, out var c, out var err), err);
        Assert.Equal(expected, c.Format());
        Assert.True(KeyChord.TryParse(c.Format(), out var again, out _));
        Assert.Equal(c, again);
    }

    [Fact]
    public void Virtual_key_codes_are_windows_codes()
    {
        KeyChord.TryParse("Ctrl+Shift+K", out var k, out _);
        Assert.Equal((true, true, false, 0x4B), (k.Ctrl, k.Shift, k.Alt, k.Vk));
        KeyChord.TryParse("Ctrl+F7", out var f, out _);
        Assert.Equal(0x76, f.Vk);
        KeyChord.TryParse("F1", out var f1, out _);
        Assert.Equal(0x70, f1.Vk);
        KeyChord.TryParse("F24", out var f24, out _);
        Assert.Equal(0x87, f24.Vk);
        KeyChord.TryParse("Ctrl+0", out var d, out _);
        Assert.Equal(0x30, d.Vk);
    }

    [Theory]
    [InlineData("K")]               // a letter alone would eat typing
    [InlineData("Shift+K")]
    [InlineData("Alt+F7")]          // letting Alt go opens the ribbon key tips
    [InlineData("Ctrl+Alt+K")]
    [InlineData("5")]
    [InlineData("Ctrl")]            // no key
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+K")]
    [InlineData("Win+K")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Ctrl+KK")]
    [InlineData("")]
    public void Rejects_with_a_reason(string text)
    {
        Assert.False(KeyChord.TryParse(text, out _, out var err));
        Assert.NotEmpty(err);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" none ", true)]
    [InlineData("NONE", true)]
    [InlineData(null, true)]
    [InlineData("F8", false)]
    [InlineData("nonsense", false)]
    public void Off_values(string? text, bool off) => Assert.Equal(off, KeyChord.IsOff(text));

    [Fact]
    public void Resolve_gives_off_the_chord_or_the_default_with_a_warning()
    {
        Assert.Null(KeyChord.Resolve("None", out var w0));
        Assert.Equal("", w0);
        Assert.Equal("F8", KeyChord.Resolve("f8", out var w1)!.Value.Format());
        Assert.Equal("", w1);
        var bad = KeyChord.Resolve("Ctrl+Nope", out var w2);
        Assert.Equal(KeyChord.Default, bad!.Value.Format());
        Assert.Contains("Ctrl+Nope", w2);
        Assert.Contains(KeyChord.Default, w2);
    }

    [Fact]
    public void The_default_setting_is_ctrl_shift_k_and_a_bad_stored_value_falls_back()
    {
        var s = new RetraceSettings();
        Assert.Equal("Ctrl+Shift+K", s.CheckpointKey);
        Assert.Equal("Ctrl+Shift+K", s.CheckpointChord(out var w)!.Value.Format());
        Assert.Equal("", w);
        s.CheckpointKey = "None";
        Assert.Null(s.CheckpointChord(out _));
        s.CheckpointKey = "K";
        Assert.Equal("Ctrl+Shift+K", s.CheckpointChord(out var w2)!.Value.Format());
        Assert.NotEqual("", w2);
    }

    [Fact]
    public void A_settings_file_without_the_key_gets_the_default()
    {
        var root = Path.Combine(Path.GetTempPath(), "retrace-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Paths.SettingsFile(root), "{ \"screenshots\": false }");
            Assert.Equal("Ctrl+Shift+K", RetraceSettings.Load(root).CheckpointKey);
            File.WriteAllText(Paths.SettingsFile(root), "{ \"checkpointKey\": \"F8\" }");
            Assert.Equal("F8", RetraceSettings.Load(root).CheckpointKey);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
