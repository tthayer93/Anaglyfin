using System;
using System.Globalization;
using System.IO;
using Anaglyfin.FFmpegWrapper;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Contract tests for the concurrency guard: one slot per Anaglyfin transcode, visible on
/// disk while the job runs, gone when the wrapper is.
/// </summary>
/// <remarks>
/// <para>
/// Everything is asserted against the slot directory rather than against the guard's own
/// properties, because the guarantee being tested is between processes that share nothing
/// but that directory: two guards here stand for two wrapper processes, exactly as the
/// server would start them. The guard's properties are checked as well, since
/// <c>ls</c> is what an administrator has when a 3D film refuses to start.
/// </para>
/// <para>
/// Every test is deterministic and stays inside its own temp directory: no clocks are
/// waited on (an old slot is written with an old timestamp rather than aged), and no
/// process is started to be killed (the operating system's part - removing the file when
/// the last handle closes - is asserted as the release of that handle).
/// </para>
/// </remarks>
public sealed class WrapperConcurrencyGuardTests : IDisposable
{
    /// <summary>
    /// A process id no machine in this test run can have handed out, so a slot recording it
    /// is provably ownerless.
    /// </summary>
    private const string DeadPid = "2147483646";

    private readonly TemporarySlotDirectory _slots = new();

    /// <summary>Removes the slot directory this test used.</summary>
    public void Dispose() => _slots.Dispose();

    [Fact]
    public void TheFirstJobClaimsTheOnlySlotAndLeavesAFileBehind()
    {
        using var guard = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);

        Assert.True(guard.TryAcquire());

        Assert.True(guard.IsHolding);
        Assert.Equal(0, guard.HeldSlot);
        Assert.Equal(1, guard.MaxConcurrentTranscodes);

        var file = Assert.Single(_slots.SlotFiles());
        Assert.StartsWith(WrapperConcurrencyGuard.SlotFileNamePrefix, Path.GetFileName(file));

        // The file says who owns it, which is the difference between "one job is running"
        // and an unexplained file that has to be deleted to find out.
        Assert.Contains(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            File.ReadAllText(file),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondJobIsRefusedWhileTheOnlySlotIsHeld()
    {
        using var running = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);
        using var waiting = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);

        Assert.True(running.TryAcquire());

        Assert.False(waiting.TryAcquire());
        Assert.False(waiting.IsHolding);
        Assert.Equal(-1, waiting.HeldSlot);
        Assert.Single(_slots.SlotFiles());
    }

    [Fact]
    public void AReleasedSlotIsClaimedByTheNextJob()
    {
        var first = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);
        Assert.True(first.TryAcquire());
        Assert.Single(_slots.SlotFiles());

        first.Release();

        Assert.False(first.IsHolding);
        Assert.Empty(_slots.SlotFiles());

        using var second = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);

        Assert.True(second.TryAcquire());
        Assert.True(second.IsHolding);

        first.Dispose();
    }

    [Fact]
    public void DisposingAGuardReleasesTheJobItWasHolding()
    {
        using (var guard = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1))
        {
            Assert.True(guard.TryAcquire());
            Assert.Single(_slots.SlotFiles());
        }

        // Closing the handle is the whole release, including for a wrapper that never
        // reached its own cleanup - which is also what makes a killed wrapper unable to
        // jam the limit.
        Assert.Empty(_slots.SlotFiles());

        using var next = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);
        Assert.True(next.TryAcquire());
    }

    [Fact]
    public void ReleasingTwiceIsSafeAndTheNextJobStillGetsTheSlot()
    {
        using var first = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);
        using var second = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());

        // Releasing twice is what a finally block that also disposes looks like, and it
        // must not be the thing that fails a playback that already finished.
        first.Release();
        first.Release();

        Assert.True(second.TryAcquire());
    }

    [Fact]
    public void ALimitOfTwoRunsTwoJobsInDifferentSlotsAndTurnsAwayTheThird()
    {
        using var first = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 2);
        using var second = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 2);
        using var third = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 2);

        Assert.True(first.TryAcquire());
        Assert.Equal(0, first.HeldSlot);

        Assert.True(second.TryAcquire());

        // The free slot, not the first one: a job never takes a number somebody else is
        // already holding.
        Assert.Equal(1, second.HeldSlot);
        Assert.Contains(SlotFile(0), _slots.SlotFiles(), StringComparer.Ordinal);
        Assert.Contains(SlotFile(1), _slots.SlotFiles(), StringComparer.Ordinal);

        Assert.False(third.TryAcquire());

        first.Release();

        Assert.True(third.TryAcquire());
        Assert.Equal(0, third.HeldSlot);
    }

    [Fact]
    public void OneGuardCannotHoldTwoSlots()
    {
        using var guard = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 2);

        Assert.True(guard.TryAcquire());

        // One wrapper process is one FFmpeg. Asking for a second slot is a bug in the
        // caller, and answering quietly would turn the limit into a number nobody can trust.
        Assert.Throws<InvalidOperationException>(() => guard.TryAcquire());
    }

    [Fact]
    public void TheSlotDirectoryIsCreatedWhenTheFirstJobNeedsIt()
    {
        var location = Path.Combine(_slots.Location, "deeper", "slots");
        using var guard = new WrapperConcurrencyGuard(location, maxConcurrentTranscodes: 1);

        Assert.True(guard.TryAcquire());
        Assert.True(Directory.Exists(location));
    }

    [Fact]
    public void TheConfigurationOfAWrapperInvocationConfiguresTheGuard()
    {
        var options = new FFmpegWrapperOptions
        {
            LockDirectory = _slots.Location,
            MaxConcurrentTranscodes = 3
        };

        using var guard = new WrapperConcurrencyGuard(options);

        Assert.Equal(3, guard.MaxConcurrentTranscodes);
        Assert.Equal(_slots.Location, guard.LockDirectory);
        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void ALimitOfZeroIsReadAsTheShippedDefaultRatherThanAsNoJobEverAgain()
    {
        var options = new FFmpegWrapperOptions
        {
            LockDirectory = _slots.Location,
            MaxConcurrentTranscodes = 0
        };

        using var guard = new WrapperConcurrencyGuard(options);

        // A limit of zero would refuse every Anaglyfin playback; the safe reading of a
        // mistyped variable is the default the product ships.
        Assert.Equal(1, guard.MaxConcurrentTranscodes);
        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void AGuardWithoutASlotDirectoryIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new WrapperConcurrencyGuard(" ", maxConcurrentTranscodes: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void ALimitBelowOneIsRejectedAtConstruction(int maxConcurrentTranscodes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WrapperConcurrencyGuard("/tmp/anaglyfin-never-created", maxConcurrentTranscodes));
    }

    // ----- a slot file that outlived its wrapper ---------------------------------------

    [Fact]
    public void AFileThatWasClaimedMomentsAgoIsTreatedAsARunningJob()
    {
        var path = _slots.WriteForeignSlotFile(0, DeadPid, TimeSpan.FromMinutes(1));

        using var guard = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);

        // A fresh file is somebody's encode: an MVC transcode runs for hours, and guessing
        // otherwise is how two encodes end up on one machine.
        Assert.False(guard.TryAcquire());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AnOldFileOwnedByALiveProcessIsStillSomebodyElses()
    {
        var path = _slots.WriteForeignSlotFile(
            0,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            TimeSpan.FromDays(3));

        using var guard = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);

        // Past the abandon window is not yet evidence: the recorded owner has to be gone too.
        Assert.False(guard.TryAcquire());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AnOldFileOwnedByNobodyIsTakenOver()
    {
        var path = _slots.WriteForeignSlotFile(0, DeadPid, TimeSpan.FromDays(3));

        using var guard = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);

        // Old, unheld and ownerless is the leavings of a wrapper that died on storage which
        // outlived it - the one case the kernel cannot clean up by itself.
        Assert.True(guard.TryAcquire());
        Assert.Equal(0, guard.HeldSlot);

        // The file at that path is a fresh claim and not the abandoned one: it names this
        // process, so the takeover is visible in the directory afterwards.
        Assert.Equal(path, Assert.Single(_slots.SlotFiles()));
        Assert.Contains(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            File.ReadAllText(path),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheAbandonWindowIsConfigurableForStorageThatOutlivesItsJobs()
    {
        _slots.WriteForeignSlotFile(0, DeadPid, TimeSpan.FromMinutes(30));

        using var impatient = new WrapperConcurrencyGuard(
            _slots.Location,
            maxConcurrentTranscodes: 1,
            abandonedAfter: TimeSpan.FromMinutes(1));

        // The default window is deliberately longer than any transcode; a deployment whose
        // slot storage outlives its containers can shorten it to what its own storage does.
        Assert.True(impatient.TryAcquire());
        Assert.Equal(0, impatient.HeldSlot);
    }

    [Fact]
    public void AnAbandonWindowOfNothingMeaningfulIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WrapperConcurrencyGuard("/tmp/anaglyfin-never-created", 1, TimeSpan.Zero));
    }

    private string SlotFile(int slot)
        => Path.Combine(
            _slots.Location,
            WrapperConcurrencyGuard.SlotFileNamePrefix
            + slot.ToString(CultureInfo.InvariantCulture)
            + WrapperConcurrencyGuard.SlotFileExtension);
}
