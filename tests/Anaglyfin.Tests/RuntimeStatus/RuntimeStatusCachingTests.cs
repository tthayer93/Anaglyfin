using System;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.RuntimeStatus;
using Xunit;

namespace Anaglyfin.Tests.RuntimeStatus;

/// <summary>
/// The cache and the single-flight window: a status refresh is one probe pass, reused for a while
/// and shared by every caller that arrives while it runs, so a page that refreshes cannot fork a
/// binary per request. The clock is a fake, so nothing here races the wall clock.
/// </summary>
public sealed class RuntimeStatusCachingTests
{
    private const string PluginVersion = "1.2.3";

    [Fact]
    public async Task TheSnapshotIsReusedUntilItExpires()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new RuntimeStatusService(
            probe,
            harness.ReadVariable,
            harness.Root,
            PluginVersion,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2),
            () => now);

        var first = await service.GetStatusAsync(CancellationToken.None);
        var probesAfterFirst = probe.CallCount;
        Assert.True(probesAfterFirst > 0, "the first pass should have probed the binaries.");
        Assert.Equal(now, first.CheckedAtUtc);

        // A second request inside the window returns the very same snapshot and asks no binary.
        var second = await service.GetStatusAsync(CancellationToken.None);
        Assert.Same(first, second);
        Assert.Equal(probesAfterFirst, probe.CallCount);

        // Past the window, a fresh pass runs and restamps the snapshot.
        now = now.AddSeconds(31);
        var third = await service.GetStatusAsync(CancellationToken.None);
        Assert.NotSame(first, third);
        Assert.True(probe.CallCount > probesAfterFirst, "an expired snapshot should re-probe.");
        Assert.Equal(now, third.CheckedAtUtc);
    }

    [Fact]
    public async Task ARefreshIgnoresTheCacheAndTakesAFreshPass()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new RuntimeStatusService(
            probe,
            harness.ReadVariable,
            harness.Root,
            PluginVersion,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2),
            () => now);

        var cached = await service.GetStatusAsync(CancellationToken.None);
        var probesAfterCached = probe.CallCount;

        await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(probesAfterCached, probe.CallCount);

        var refreshed = await service.RefreshAsync(CancellationToken.None);
        Assert.True(probe.CallCount > probesAfterCached, "a refresh must re-probe despite the cache.");
        Assert.NotSame(cached, refreshed);
    }

    [Fact]
    public async Task ConcurrentRequestsJoinOneProbePassRatherThanForkAnother()
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
                () => DateTimeOffset.UnixEpoch);

            var first = service.GetStatusAsync(CancellationToken.None);

            // Hold the first pass inside its very first probe, then arrive with a second request.
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the probe pass never started.");
            var second = service.GetStatusAsync(CancellationToken.None);

            // While the first pass is still blocked on its first probe, no second pass has begun.
            Assert.Equal(1, probe.CallCount);

            release.Set();
            var firstResult = await first;
            var secondResult = await second;

            // Both callers got the one snapshot the single pass produced.
            Assert.Same(firstResult, secondResult);
        }
        finally
        {
            release.Set();
            entered.Dispose();
            release.Dispose();
        }
    }

    [Fact]
    public async Task ACancellingCallerDoesNotCancelTheSharedProbePassOrPoisonTheCache()
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
                () => DateTimeOffset.UnixEpoch);

            using var firstCaller = new CancellationTokenSource();
            var first = service.GetStatusAsync(firstCaller.Token);

            // Hold the shared pass inside its very first probe, then disconnect the first caller.
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the probe pass never started.");
            Assert.Equal(1, probe.CallCount);

            firstCaller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

            // The disconnected caller only stopped its own wait. The shared pass is still running,
            // still single-flight, and still owed to anybody else asking for the same snapshot.
            var second = service.GetStatusAsync(CancellationToken.None);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, probe.CallCount);

            release.Set();
            var secondResult = await second;
            Assert.Equal(RuntimeStatusLevel.Ok, secondResult.OverallStatus);
            var probesAfterPass = probe.CallCount;
            Assert.True(probesAfterPass > 1, "the shared pass should have finished its probes.");

            // The cache holds the completed healthy snapshot, not the cancelled caller's view.
            var cached = await service.GetStatusAsync(CancellationToken.None);
            Assert.Same(secondResult, cached);
            Assert.Equal(probesAfterPass, probe.CallCount);
        }
        finally
        {
            release.Set();
            entered.Dispose();
            release.Dispose();
        }
    }

    [Fact]
    public async Task AStuckProbePassIsCancelledReleasedAndDoesNotPoisonTheCache()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        probe.Entered = entered;
        probe.Release = release;
        var now = DateTimeOffset.UnixEpoch;

        try
        {
            var service = new RuntimeStatusService(
                probe,
                harness.ReadVariable,
                harness.Root,
                PluginVersion,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(20),
                () => now,
                TimeSpan.FromSeconds(2));

            var stuckPass = service.GetStatusAsync(CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the stuck pass never started.");

            var watchdogTimeout = Task.Delay(TimeSpan.FromSeconds(10));
            var completedFirst = await Task.WhenAny(stuckPass, watchdogTimeout);
            Assert.True(completedFirst == stuckPass, "the watchdog did not release the stuck pass.");

            var stuckResult = await stuckPass;
            Assert.Equal(RuntimeStatusLevel.Warning, stuckResult.OverallStatus);
            Assert.Empty(stuckResult.Binaries);
            var failed = Assert.Single(stuckResult.Environment);
            Assert.Equal(RuntimeItemState.ProbeFailed, failed.State);
            Assert.True(probe.CancellationSeen, "the watchdog did not cancel the service-owned pass token.");

            // A late/abandoned pass must not leave a cached or in-flight result behind. The next
            // request has to start a fresh pass: call count 1 is the cancelled pass's first probe,
            // and call count 2 can only come from a fresh pass started after the slot was released.
            var freshPass = service.GetStatusAsync(CancellationToken.None);
            Assert.True(
                SpinWait.SpinUntil(() => probe.CallCount == 2, TimeSpan.FromSeconds(5)),
                "the abandoned pass left a cached or in-flight result behind.");

            release.Set();
            var freshResult = await freshPass;
            Assert.Equal(RuntimeStatusLevel.Ok, freshResult.OverallStatus);
            Assert.NotSame(stuckResult, freshResult);

            // The pass slot is not permanently pinned: after cache expiry, a later request can still
            // complete normally.
            now = now.AddSeconds(31);
            var later = await service.GetStatusAsync(CancellationToken.None);
            Assert.Equal(RuntimeStatusLevel.Ok, later.OverallStatus);
        }
        finally
        {
            release.Set();
            entered.Dispose();
            release.Dispose();
        }
    }

    [Fact]
    public void NullDependenciesAreRejectedAtTheDoor()
    {
        Assert.Throws<ArgumentNullException>(() => new RuntimeStatusService(null!, _ => null, "/tmp", "1.0.0"));
        Assert.Throws<ArgumentNullException>(() => new RuntimeStatusService(new FakeRuntimeVersionProbe(), null!, "/tmp", "1.0.0"));
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
