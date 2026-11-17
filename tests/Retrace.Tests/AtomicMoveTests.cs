namespace Retrace.Tests;

/// <summary>
/// File.Move(tmp, target, overwrite: true) is how results, status files and recordings are written so that a reader never sees half a file.
/// On .NET Framework (AutoCAD 2024) Compat/NetFramework.cs does that with one MoveFileEx(REPLACE_EXISTING); this checks the promise on both targets.
/// </summary>
public class AtomicMoveTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-atomic").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void A_reader_looping_on_the_target_never_sees_it_missing_or_partial()
    {
        var target = Path.Combine(_dir, "status.json");
        string Body(char c) => new string(c, 20000);
        File.WriteAllText(target, Body('a'));
        int missing = 0, partial = 0, reads = 0, writeErrors = 0;
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                    reads++;
                    if (text.Length != 20000 || text.Any(c => c != text[0])) partial++;
                }
                catch (FileNotFoundException) { missing++; }
                catch (DirectoryNotFoundException) { missing++; }
                catch (IOException) { /* the move holds the target for a moment on some systems: not a missing or partial file */ }
                catch (UnauthorizedAccessException) { }
            }
        });

        for (int i = 0; i < 1500; i++)
        {
            var tmp = target + "." + i + ".tmp";
            File.WriteAllText(tmp, Body((char)('b' + i % 20)));
            try { File.Move(tmp, target, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { writeErrors++; File.Delete(tmp); }
        }
        stop.Cancel();
        reader.Wait();

        Assert.True(reads > 50, "the reader ran " + reads + " times");
        Assert.Equal(0, missing);
        Assert.Equal(0, partial);
        Assert.True(writeErrors < 1500, "every move failed");
    }

    [Fact]
    public void Move_with_overwrite_replaces_and_reports_a_missing_source_like_dotnet8()
    {
        var a = Path.Combine(_dir, "a.txt"); var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, "new"); File.WriteAllText(b, "old");
        File.Move(a, b, overwrite: true);
        Assert.False(File.Exists(a));
        Assert.Equal("new", File.ReadAllText(b));
        Assert.Throws<FileNotFoundException>(() => File.Move(Path.Combine(_dir, "none.txt"), b, overwrite: true));
        Assert.Throws<DirectoryNotFoundException>(() => File.Move(b, Path.Combine(_dir, "no-such-dir", "x.txt"), overwrite: true));
        File.WriteAllText(a, "x");
        Assert.Throws<IOException>(() => File.Move(a, b, overwrite: false));
    }
}
