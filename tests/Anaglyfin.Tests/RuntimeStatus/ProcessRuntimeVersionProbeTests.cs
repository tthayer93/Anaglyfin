using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Anaglyfin.RuntimeStatus;
using Xunit;

namespace Anaglyfin.Tests.RuntimeStatus;

/// <summary>
/// Real process probes, but never against a real FFmpeg. The child is a POSIX shell under a
/// deliberate command so these tests exercise the process boundary, the hard first wait, the bounded
/// post-exit stdout wait, and the process-tree kill without depending on FFmpeg being installed.
/// </summary>
public sealed class ProcessRuntimeVersionProbeTests
{
    private const string ShellPath = "/bin/sh";
    private const int LingeringSleepSeconds = 5;

    [Fact]
    public void AHealthyVersionBannerIsCapturedBoundedly()
    {
        if (!HasUsableShell())
        {
            return;
        }

        var probe = new ProcessRuntimeVersionProbe();
        var result = probe.Probe(
            ShellPath,
            new[] { "-c", "echo 'ffmpeg version 6.1.1-mvc-process'" },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.StandardOutputLine);
        Assert.Contains("ffmpeg version 6.1.1-mvc-process", result.StandardOutputLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AProcessThatNeverAnswersIsKilledAndReportedBounded()
    {
        if (!HasUsableShell())
        {
            return;
        }

        var probe = new ProcessRuntimeVersionProbe();
        var startedAt = Stopwatch.GetTimestamp();

        var result = probe.Probe(
            ShellPath,
            new[] { "-c", "sleep 30" },
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.True(result.TimedOut);
        Assert.False(result.Started);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            $"The probe waited too long for a non-answering process: {elapsed}.");
    }

    [Fact]
    public void AProcessWhoseStdoutAnswerAlreadyArrivesIsNotHeldByALingeringStdoutHandle()
    {
        if (!HasUsableShell())
        {
            return;
        }

        var probe = new ProcessRuntimeVersionProbe();
        var startedAt = Stopwatch.GetTimestamp();

        var result = probe.Probe(
            ShellPath,
            new[] { "-c", $"sleep {LingeringSleepSeconds} & echo 'ffmpeg version lingering-stdout'; exit 0" },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.True(
            elapsed < TimeSpan.FromSeconds(8),
            $"A lingering stdout handle held the probe too long: {elapsed}.");
        Assert.False(result.TimedOut);
        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.StandardOutputLine);
        Assert.Contains("ffmpeg version lingering-stdout", result.StandardOutputLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AProcessWithDelayedStderrStillReturnsItsStdoutAnswer()
    {
        if (!HasUsableShell())
        {
            return;
        }

        var probe = new ProcessRuntimeVersionProbe();
        var startedAt = Stopwatch.GetTimestamp();

        // The background sleep inherits stderr but not stdout, so stdout EOF arrives with the parent
        // while stderr can remain open. The valid stdout answer must still be returned.
        var result = probe.Probe(
            ShellPath,
            new[] { "-c", $"echo 'ffmpeg version delayed-stderr'; sleep {LingeringSleepSeconds} >/dev/null & exit 0" },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.True(
            elapsed < TimeSpan.FromSeconds(8),
            $"A lingering stderr handle held the probe too long: {elapsed}.");
        Assert.False(result.TimedOut);
        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.StandardOutputLine);
        Assert.Contains("ffmpeg version delayed-stderr", result.StandardOutputLine, StringComparison.Ordinal);
    }

    private static bool HasUsableShell()
        => !OperatingSystem.IsWindows() && File.Exists(ShellPath);
}
