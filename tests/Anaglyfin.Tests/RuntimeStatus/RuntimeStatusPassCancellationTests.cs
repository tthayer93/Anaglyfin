using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.RuntimeStatus;
using Xunit;

namespace Anaglyfin.Tests.RuntimeStatus;

/// <summary>
/// The lifetime of the two cancellation sources one probe pass owns: the source that cancels the
/// probing work and the source that cancels the whole-pass watchdog timer. Both are created together
/// and both are read by continuations that can run at the same instant - the pass that finishes while
/// the watchdog is firing - which is exactly where a pass can leak a source, release one twice, or
/// have a source pulled out from under the continuation still holding it. None of those may throw,
/// and none of them may be left to luck: the abandoned pass here is produced by a blocked probe and a
/// spent budget, not by a schedule the test hopes for.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="ObjectDisposedException"/> that escapes a continuation nobody awaits does not fail
/// the pass that threw it - it surfaces later, when the orphaned continuation task is finalized, as
/// an unobserved task exception. So every test here also reads a process-wide hook for exactly that
/// shape of exception after forcing a collection, and asserts the hook stayed empty.
/// </para>
/// <para>
/// Nothing in these tests waits on a deadline to decide a pass was abandoned: the blocked probe keeps
/// the pass alive until the watchdog is the only thing that can end it, and awaiting the pass is
/// itself the signal that the watchdog fired.
/// </para>
/// </remarks>
public sealed class RuntimeStatusPassCancellationTests : IDisposable
{
    private const string PluginVersion = "1.2.3";

    private readonly object _sync = new();
    private readonly List<Exception> _disposedSourceMisuse = new();
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _onUnobserved;

    public RuntimeStatusPassCancellationTests()
    {
        _onUnobserved = (_, args) =>
        {
            // Only the misuse this file is about: a cancellation source used after it was disposed.
            // Anything else an unrelated test leaked is left for its own owner to observe.
            foreach (var exception in Flatten(args.Exception))
            {
                if (exception is not ObjectDisposedException
                    || !exception.Message.Contains("CancellationTokenSource", StringComparison.Ordinal))
                {
                    continue;
                }

                lock (_sync)
                {
                    _disposedSourceMisuse.Add(exception);
                }

                args.SetObserved();
            }
        };

        TaskScheduler.UnobservedTaskException += _onUnobserved;
    }

    public void Dispose() => TaskScheduler.UnobservedTaskException -= _onUnobserved;

    [Fact]
    public async Task ThePassTheWatchdogAbandonsStillResolvesAndReleasesItsCancellations()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        probe.Entered = entered;
        probe.Release = release;

        try
        {
            var service = new RuntimeStatusService(
                probe,
                harness.ReadVariable,
                harness.Root,
                PluginVersion,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(2),
                () => DateTimeOffset.UnixEpoch,
                TimeSpan.FromSeconds(1));

            var abandonedPass = service.GetStatusAsync(CancellationToken.None);

            // Hold the pass inside its very first probe, so the watchdog is the only thing that can
            // end it and the pass cannot finish early enough to dodge the race.
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the blocked pass never reached its probe.");
            Assert.Equal(1, probe.CallCount);
            Assert.False(abandonedPass.IsCompleted, "the pass finished before the watchdog was allowed to.");

            // Awaiting the pass is the signal that the budget was spent. It is also the instant the
            // race used to live in: the same cancellation that releases the blocked probe completes
            // the pass, so the pass continuation and the watchdog continuation reach for the two
            // shared cancellation sources together.
            var snapshot = await abandonedPass;

            Assert.Equal(RuntimeStatusLevel.Warning, snapshot.OverallStatus);
            Assert.Equal(PluginVersion, snapshot.PluginVersion);
            Assert.Empty(snapshot.Binaries);
            var reported = Assert.Single(snapshot.Environment);
            Assert.Equal("runtime-status", reported.Name);
            Assert.Equal(RuntimeItemState.ProbeFailed, reported.State);
            Assert.Contains("did not finish within 1000 ms", reported.Detail ?? string.Empty, StringComparison.Ordinal);

            Assert.True(
                SpinWait.SpinUntil(() => probe.CancellationSeen, TimeSpan.FromSeconds(5)),
                "the watchdog never cancelled the service-owned pass token.");

            // The abandoned pass is over. Let its blocked probe go, and ask again: the abandoned pass
            // leaves neither a cached answer nor a held slot behind, so this is a fresh pass and the
            // healthy snapshot a working deployment is owed.
            release.Set();
            var fresh = await service.GetStatusAsync(CancellationToken.None);
            Assert.Equal(RuntimeStatusLevel.Ok, fresh.OverallStatus);
            Assert.NotEmpty(fresh.Binaries);
            Assert.NotSame(snapshot, fresh);

            AssertNoCancellationSourceUsedAfterDisposal();
        }
        finally
        {
            release.Set();
            entered.Dispose();
            release.Dispose();
        }
    }

    [Fact]
    public async Task AQuicklyCompletedPassStillResolvesAndReleasesItsCancellations()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        var service = new RuntimeStatusService(
            probe,
            harness.ReadVariable,
            harness.Root,
            PluginVersion);

        // A probe that answers at once lets the pass finish while the watchdog is still being armed:
        // the pass continuation used to release both cancellation sources itself in exactly this
        // window, with the timer creation and the watchdog continuation still to come. Repeated rather
        // than run once, so the ordering is provoked rather than waited for; every round still has to
        // be the built, healthy snapshot.
        for (var round = 0; round < 25; round++)
        {
            var snapshot = await service.RefreshAsync(CancellationToken.None);

            Assert.Equal(RuntimeStatusLevel.Ok, snapshot.OverallStatus);
            Assert.Equal(PluginVersion, snapshot.PluginVersion);
            Assert.Equal(
                new[]
                {
                    RuntimeStatusService.WrapperKey,
                    RuntimeStatusService.FfmpegMvcKey,
                    RuntimeStatusService.OfficialFfmpegKey,
                    RuntimeStatusService.FfprobeKey
                },
                snapshot.Binaries.Select(binary => binary.Key).ToArray());
            Assert.All(snapshot.Binaries, binary => Assert.Equal(RuntimeItemState.Ok, binary.State));
        }

        AssertNoCancellationSourceUsedAfterDisposal();
    }

    /// <summary>
    /// Forces the finalization that lets an escaped continuation exception surface, then fails on any
    /// cancellation source that was used after it had been disposed.
    /// </summary>
    private void AssertNoCancellationSourceUsedAfterDisposal()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        lock (_sync)
        {
            Assert.Empty(_disposedSourceMisuse);
        }
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                foreach (var nested in Flatten(inner))
                {
                    yield return nested;
                }
            }
        }

        if (exception.InnerException is { } innerException)
        {
            foreach (var nested in Flatten(innerException))
            {
                yield return nested;
            }
        }
    }

    private static void SetupHealthy(RuntimeStatusHarness harness, FakeRuntimeVersionProbe probe)
    {
        var wrapper = harness.WriteExecutable("bin", "ffmpeg");
        harness.WriteExecutable("bin", "ffmpeg-mvc");
        harness.WriteExecutable("bin", "ffmpeg-official");
        harness.WriteExecutable("bin", "ffprobe");
        harness.EnsureDirectory("locks");
        harness.WriteFile("settings", "anaglyfin-wrapper-settings.json");

        harness.Env[RuntimeStatusService.JellyfinFFmpegEnvironmentVariable] = wrapper;
        harness.Env[FFmpegWrapperOptions.RealFFmpegEnvironmentVariable] = harness.PathOf("bin", "ffmpeg-mvc");
        harness.Env[FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable] = harness.PathOf("bin", "ffmpeg-official");
        harness.Env[TranscodeSlotStore.LockDirectoryEnvironmentVariable] = harness.PathOf("locks");
        harness.Env[WrapperSettingsFile.EnvironmentVariable] = harness.PathOf("settings", "anaglyfin-wrapper-settings.json");

        probe.Default = RuntimeVersionProbeResult.Ran(0, "ffmpeg version 6.1.1-mvc11-jf2 Copyright");
        probe.ByPath[wrapper] = RuntimeVersionProbeResult.Ran(0, "anaglyfin-wrapper " + PluginVersion);
    }
}
