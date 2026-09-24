using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.FFmpegWrapper;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Tests for the startup pass that clears the transcode slots a previous run left behind.
/// </summary>
/// <remarks>
/// <para>
/// The pass runs once, at startup, in a directory the deployment - not the plugin - chose, and its
/// whole contract is a pair: take what is certainly left over, leave everything else. Both halves are
/// asserted here against real files, because the failure this pass can cause is not a wrong count in
/// a log line but a running encode that loses its slot, and the only evidence that settles either way
/// is a directory an administrator can look at afterwards.
/// </para>
/// <para>
/// The pass is tested through <see cref="TranscodeSlotCleanupService.Clean"/> rather than only through
/// <see cref="IHostedService.StartAsync"/>, because the directory a test owns is how a test says
/// "sweep this one", and the environment variable a deployment uses to name the directory is process
/// state a test does not own. <see cref="IHostedService.StartAsync"/> is asserted separately, for the
/// one thing only it can show: that the server starts either way.
/// </para>
/// </remarks>
public sealed class TranscodeSlotCleanupServiceTests : IDisposable
{
    /// <summary>A process id no machine in this test run can have handed out.</summary>
    private const string DeadPid = "2147483646";

    private readonly TemporarySlotDirectory _slots = new();

    private readonly RecordingLogger _log = new();

    /// <summary>Removes the slot directory this test used.</summary>
    public void Dispose() => _slots.Dispose();

    [Fact]
    public void TheSlotsLeftByWrappersThatAreGoneAreClearedAtStartup()
    {
        // The deployment this pass exists for: a container was recreated over a slot directory on the
        // persisted volume, and the two files its wrappers did not finish releasing are refusing every
        // 3D playback on the server.
        var old = _slots.WriteForeignSlotFile(0, DeadPid, TimeSpan.FromHours(13));
        var recent = _slots.WriteForeignSlotFile(1, DeadPid, TimeSpan.FromSeconds(30));

        var report = Service().Clean(_slots.Location);

        Assert.Equal(2, report.Seen);
        Assert.Equal(2, report.Removed);
        Assert.Null(report.Failure);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(recent));
        Assert.Empty(_slots.SlotFiles());
    }

    [Fact]
    public void TheSlotOfAWrapperThatIsStillRunningIsLeftAlone()
    {
        var held = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);
        Assert.True(held.TryAcquire());

        try
        {
            var report = Service().Clean(_slots.Location);

            // The file is held open by a live process, and clearing it would put a second MVC encode on
            // the machine while the first one is still writing. This is the assertion the pass is
            // allowed no exception to.
            Assert.Equal(1, report.Seen);
            Assert.Equal(0, report.Removed);
            Assert.NotEmpty(_slots.SlotFiles());

            Assert.True(held.IsHolding);
        }
        finally
        {
            held.Release();
        }
    }

    [Fact]
    public void ASlotNamingAProcessTheMachineCanStillSeeIsLeftAlone()
    {
        // No handle is open on this file - it is a leftover a test wrote - and the number in its mark
        // is a live process on this machine. A leftover whose owner is alive cannot be told apart from
        // a job by anything this pass is allowed to look at, so it stays.
        var path = _slots.WriteForeignSlotFile(
            0,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            TimeSpan.FromHours(2));

        var report = Service().Clean(_slots.Location);

        Assert.Equal(1, report.Seen);
        Assert.Equal(0, report.Removed);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ASlotWhoseMarkCannotBeReadIsLeftAloneAtStartup()
    {
        // Age is the only evidence that could decide this file, and the startup pass has none to offer:
        // deciding "a day is long enough" about a file in a directory the deployment shares with
        // somebody else is the guard's call to make, at the moment a job asks. The pass leaves it, and
        // the first 3D playback after this startup is answered by the guard's own rules.
        var path = _slots.WriteSlotFileWithMark(0, "claimed=nobody-finished-writing-this\n", TimeSpan.FromDays(3));

        var report = Service().Clean(_slots.Location);

        Assert.Equal(1, report.Seen);
        Assert.Equal(0, report.Removed);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ASlotWrittenByAnotherContainerIsClearedEvenThoughItsNumberIsAliveHere()
    {
        // The mark names this very process. On the container that wrote it that was a live wrapper;
        // here it is a number that was handed out again, and treating it as this machine's process is
        // the misreading that kept a leftover refusing playbacks after a restart.
        var path = _slots.WriteForeignSlotFile(
            0,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            TimeSpan.FromMinutes(5),
            bootId: TemporarySlotDirectory.AnotherBootId());

        var report = Service().Clean(_slots.Location);

        Assert.Equal(1, report.Removed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ADirectoryStandingInASlotsPlaceIsNeitherASlotNorAReasonToStop()
    {
        var directory = Path.Combine(_slots.Location, WrapperConcurrencyGuard.SlotFileNamePrefix + "0" + WrapperConcurrencyGuard.SlotFileExtension);
        Directory.CreateDirectory(directory);

        var report = Service().Clean(_slots.Location);

        // Whatever the sweep makes of it - one file it could not read, or a name the directory listing
        // did not even offer - a pass that deleted it would have deleted whatever was inside, and a
        // pass that threw over it would have stopped the server. Neither happens.
        Assert.Equal(0, report.Removed);
        Assert.Null(report.Failure);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void ADirectoryThatWasNeverCreatedHasNothingToClear()
    {
        var location = Path.Combine(_slots.Location, "never-created");

        var report = Service().Clean(location);

        Assert.Equal(0, report.Seen);
        Assert.Equal(0, report.Removed);
        Assert.Null(report.Failure);
        Assert.False(Directory.Exists(location));
    }

    [Fact]
    public void APathThatIsNotADirectoryIsAnsweredAsADirectoryWithNoSlotsInIt()
    {
        var path = Path.Combine(_slots.Location, "not-a-directory");
        Directory.CreateDirectory(_slots.Location);
        File.WriteAllText(path, "a file where a slot directory was expected");

        var report = Service().Clean(path);

        Assert.Equal(0, report.Seen);
        Assert.Equal(0, report.Removed);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ThePassStartsWithADirectoryItCannotUseAndTheServerStartsAnyway()
    {
        // The whole point of the shape this service has: it is on the server's startup path, and the
        // thing it reads is a directory on a volume the deployment - not the plugin - mounted. A
        // cleanup that cannot run costs an administrator a stale file and nothing else.
        var service = Service();

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void APassThatClearedSomethingSaysSoInOneLine()
    {
        _slots.WriteForeignSlotFile(0, DeadPid, TimeSpan.FromHours(3));
        _slots.WriteForeignSlotFile(1, DeadPid, TimeSpan.FromHours(3));

        Service().Clean(_slots.Location);

        // One line, with both numbers in it: a startup that quietly changed a directory the
        // deployment owns is a startup nobody can diagnose afterwards from the log.
        var line = Assert.Single(_log.Messages);

        Assert.Contains("Anaglyfin", line, StringComparison.Ordinal);
        Assert.Contains("2", line, StringComparison.Ordinal);
    }

    [Fact]
    public void APassThatClearedNothingSaysSoOnceAndQuietly()
    {
        // Every server start runs this pass, and one information line a boot about a directory that was
        // already empty is how a startup log stops being read.
        Service().Clean(_slots.Location);

        var line = Assert.Single(_log.Messages);

        Assert.Contains("no stale", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePassReportsTheDirectoryItSwept()
    {
        var report = Service().Clean(_slots.Location);

        // The line has to name which directory the deployment's variable pointed at, because when
        // somebody set that variable wrongly the counts in the line are the only clue where the slots
        // they are complaining about actually live.
        Assert.Equal(_slots.Location, report.LockDirectory);
    }

    [Fact]
    public void TheDirectoryTheServiceSweepsIsTheOneTheWrapperCountsIn()
    {
        // Both processes resolve the same variable through the same code, and this is the assertion that
        // keeps them doing it: a sweep of a directory the wrapper never writes to is a pass that finds
        // nothing and reports that everything is fine.
        var resolved = TranscodeSlotStore.ResolveLockDirectory(
            name => name == FFmpegWrapperOptions.LockDirectoryEnvironmentVariable
                ? _slots.Location
                : null);

        Assert.Equal(_slots.Location, resolved);
        Assert.Equal(FFmpegWrapperOptions.DefaultLockDirectory, TranscodeSlotStore.DefaultLockDirectory);
    }

    private TranscodeSlotCleanupService Service() => new(_log);

    /// <summary>
    /// A logger that keeps the messages, so "one line about what the pass did" is an assertion and not
    /// a hope that the host's logging is configured to show it.
    /// </summary>
    private sealed class RecordingLogger : ILogger<TranscodeSlotCleanupService>
    {
        public List<string> Messages { get; } = [];

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
