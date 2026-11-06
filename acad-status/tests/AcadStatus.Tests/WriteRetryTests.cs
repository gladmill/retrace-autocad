using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class WriteRetryTests
{
    static readonly DateTime T0 = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AFreshWriterMayAttempt() => Assert.True(new WriteRetry().MayAttempt(T0));

    [Fact]
    public void AFailedReplaceIsTriedAgainAtTheNextRefreshUntilSeveralFailInARow()
    {
        var r = new WriteRetry();
        // the first and the second failure: no waiting, the next tick tries again
        for (int i = 1; i < WriteRetry.FailuresBeforeBackoff; i++)
        {
            r.Failed(T0.AddSeconds(i));
            Assert.True(r.MayAttempt(T0.AddSeconds(i)), "failure " + i);
            Assert.True(r.MayAttempt(T0.AddSeconds(i + 1)));
        }
        Assert.Equal(WriteRetry.FailuresBeforeBackoff - 1, r.Failures);
        // the third in a row starts the back-off
        r.Failed(T0.AddSeconds(10));
        Assert.False(r.MayAttempt(T0.AddSeconds(10)));
        Assert.False(r.MayAttempt(T0.AddSeconds(11.9)));
        Assert.True(r.MayAttempt(T0.AddSeconds(12)));
    }

    [Fact]
    public void TheBackoffStartsAtTheThirdFailureAsTheDocumentationSays() => Assert.Equal(3, WriteRetry.FailuresBeforeBackoff);

    [Fact]
    public void ThenItBacksOffTwoFourEightAndTenSecondsAtMost()
    {
        var r = new WriteRetry();
        var now = T0;
        for (int i = 1; i < WriteRetry.FailuresBeforeBackoff; i++) r.Failed(now);   // two failures: still no waiting
        var waits = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            r.Failed(now);
            double w = 0;   // how long until it may attempt again
            while (!r.MayAttempt(now.AddSeconds(w))) w += 0.5;
            waits.Add(w);
            now = now.AddSeconds(w);
        }
        Assert.Equal(new[] { 2.0, 4.0, 8.0, 10.0, 10.0, 10.0 }, waits);
    }

    [Fact]
    public void ASuccessStartsOver()
    {
        var r = new WriteRetry();
        for (int i = 0; i < 8; i++) r.Failed(T0);
        Assert.False(r.MayAttempt(T0.AddSeconds(1)));
        r.Succeeded();
        Assert.True(r.MayAttempt(T0.AddSeconds(1)));
        Assert.Equal(0, r.Failures);
        r.Failed(T0);   // the first failure after a success waits for nothing again
        Assert.True(r.MayAttempt(T0));
    }

    [Fact]
    public void WhileBackedOffItDoesNotAttempt()
    {
        var r = new WriteRetry();
        for (int i = 0; i < WriteRetry.FailuresBeforeBackoff; i++) r.Failed(T0);
        Assert.False(r.MayAttempt(T0));
        Assert.False(r.MayAttempt(T0.AddSeconds(1.9)));
        Assert.True(r.MayAttempt(T0.AddSeconds(2)));
    }

    [Fact]
    public void AClockThatWentFarBackDoesNotLockTheWriterOut()
    {
        var r = new WriteRetry();
        for (int i = 0; i < 10; i++) r.Failed(T0);
        Assert.True(r.MayAttempt(T0.AddHours(-2)));
    }

    [Fact]
    public void AWriterThatKeepsFailingAttemptsOnTheScheduleAndRecoversAtOnce()
    {
        // one tick a second; the replace fails for 40 s, then works
        var r = new WriteRetry();
        var attempts = new List<int>();
        for (int s = 0; s < 60; s++)
        {
            var now = T0.AddSeconds(s);
            if (!r.MayAttempt(now)) continue;
            attempts.Add(s);
            if (s < 40) r.Failed(now); else r.Succeeded();
        }
        // two immediate retries, the third failure waits 2 s, then the waits double up to 10 s; the first attempt after the outage succeeds
        Assert.Equal(new[] { 0, 1, 2, 4, 8, 16, 26, 36 }, attempts.Where(a => a < 40).ToArray());
        Assert.Equal(0, r.Failures);
    }

    [Fact]
    public void TheMaximumIsTenSeconds() => Assert.Equal(10, WriteRetry.MaxBackoffSec);
}

public class SharingRuleTests
{
    static string Src([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "AcadStatus"));

    [Fact]
    public void TheReaderOpensWithShareReadWriteDeleteAndClosesAtOnce()
    {
        var text = File.ReadAllText(Path.Combine(Src(), "Core", "StatusFiles.cs"));
        Assert.Contains("FileShare.ReadWrite | FileShare.Delete", text);
        Assert.Contains("using var fs = new FileStream(", text);
        Assert.Contains("ReadToEnd()", text);   // in one go
        Assert.DoesNotContain("File.OpenRead", text);
        Assert.DoesNotContain("File.ReadAllText", text);   // the default share mode would not allow a replace while it is open
    }

    [Fact]
    public void AWriterThatNeverRetriesInsideTheCallStillNeverFailsBecauseOfOurReader()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "acad-1.json");
        AtomicFile.Write(path, TestData.Json("acad", 1), 1);
        var files = new StatusFiles(dir.Path);
        int failures = 0, reads = 0;
        var writer = new System.Threading.Thread(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                try { AtomicFile.Write(path, TestData.Json("acad", 1, state: i % 2 == 0 ? "idle" : "running"), 1); }
                catch (IOException) { failures++; }
                catch (UnauthorizedAccessException) { failures++; }
            }
        });
        writer.Start();
        while (writer.IsAlive)
        {
            files.Read();   // opens, reads and closes the file, over and over, while it is replaced
            reads++;
        }
        writer.Join();
        Assert.True(reads > 0);
        Assert.Equal(0, failures);
    }

    [Fact]
    public void TheServiceTriesAFailedWriteAgainAndNeverLetsItFailTheTick()
    {
        var text = File.ReadAllText(Path.Combine(Src(), "AutoCad", "StatusService.cs"));
        Assert.Contains("_writeRetry.MayAttempt(", text);
        Assert.Contains("_writeRetry.Failed(", text);
        Assert.Contains("_writeRetry.Succeeded(", text);
        // a failed write leaves _lastKey and _lastWrite alone, so the next refresh tries again
        var write = text[text.IndexOf("void Write(", StringComparison.Ordinal)..];
        Assert.True(write.IndexOf("catch (Exception", StringComparison.Ordinal) < write.IndexOf("_lastKey = ", StringComparison.Ordinal));
    }
}
