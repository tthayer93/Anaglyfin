using System;
using System.Globalization;
using System.IO;
using Anaglyfin.FFmpegWrapper;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Tests for the shared reading of the slot files: what a mark states, what a file proves about its
/// owner, and where the two processes that share a slot directory agree on it.
/// </summary>
/// <remarks>
/// <para>
/// The guard's tests and the plugin's cleanup tests both exercise this reading through behaviour, and
/// neither can state the cases that only a mark can: a file written by another container, a mark
/// written before the boot identifier existed, a mark with a key this build never saw. Those are the
/// cases a deployment meets once - after a restart, with a leftover in the directory - and they are
/// stated here directly.
/// </para>
/// <para>
/// Where a test needs to say which instance a reader is, it uses the overload that takes the boot
/// identifier rather than mutating the process it runs in: the machine's own identifier is one fact a
/// test cannot change and should not have to inherit to make its point.
/// </para>
/// </remarks>
public sealed class TranscodeSlotStoreTests : IDisposable
{
    /// <summary>A process id no machine in this test run can have handed out.</summary>
    private const string DeadPid = "2147483646";

    private readonly TemporarySlotDirectory _slots = new();

    /// <summary>Removes the directory this test wrote its marks into.</summary>
    public void Dispose() => _slots.Dispose();

    // ----- the directory both processes count in ----------------------------------------

    [Fact]
    public void TheSlotDirectoryOfAnUnconfiguredServerIsAbsoluteAndShared()
    {
        var resolved = TranscodeSlotStore.ResolveLockDirectory((string?)null);

        // Absolute, because the process reading it inherits its working directory from the server and
        // a limit that lives wherever the transcode happened to start is a limit nobody can find.
        Assert.True(Path.IsPathRooted(resolved));
        Assert.Equal(TranscodeSlotStore.DefaultLockDirectory, resolved);

        // And the same value the wrapper's own options type answers with, because a plugin sweeping a
        // directory the wrapper never wrote to is worse than no sweep at all.
        Assert.Equal(FFmpegWrapperOptions.DefaultLockDirectory, resolved);
    }

    [Fact]
    public void ADefaultedSlotDirectoryIsUnderTheHostsTempLocation()
    {
        Assert.StartsWith(Path.GetTempPath(), TranscodeSlotStore.DefaultLockDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void AVariableNamingASlotDirectoryIsReadAsGivenAndArelativeOneIsAnchored()
    {
        Assert.Equal(
            "/var/lib/anaglyfin/slots",
            TranscodeSlotStore.ResolveLockDirectory("  /var/lib/anaglyfin/slots  "));

        // Anchored at the default rather than at the caller's working directory, and anchored the same
        // way the wrapper anchors it: two readers resolving a relative value against their own
        // directories is two slot directories.
        Assert.Equal(
            Path.Combine(TranscodeSlotStore.DefaultLockDirectory, "per-worker"),
            TranscodeSlotStore.ResolveLockDirectory("per-worker"));
    }

    [Fact]
    public void TheSlotDirectoryIsReadFromTheVariableBothProcessesAreToldToUse()
    {
        var resolved = TranscodeSlotStore.ResolveLockDirectory(
            name => name == TranscodeSlotStore.LockDirectoryEnvironmentVariable
                ? "/config/anaglyfin/lock"
                : null);

        Assert.Equal("/config/anaglyfin/lock", resolved);

        // The name the wrapper's own options type publishes is this one, so a deployment that set it
        // for the wrapper has set it for the plugin too.
        Assert.Equal(FFmpegWrapperOptions.LockDirectoryEnvironmentVariable, TranscodeSlotStore.LockDirectoryEnvironmentVariable);
    }

    [Fact]
    public void ADirectoryThatIsNotThereYetHasNoSlotsToRead()
    {
        // Called at startup on a server that may never have run an Anaglyfin job, and on storage that
        // may not be mounted at all: the answer is an empty list, not an exception a startup has to
        // survive.
        Assert.Empty(TranscodeSlotStore.EnumerateSlotFiles(Path.Combine(_slots.Location, "never-created")));
    }

    [Fact]
    public void TheSlotFilesOfADirectoryAreTheFilesNamedForSlots()
    {
        _slots.WriteForeignSlotFile(0, DeadPid, TimeSpan.FromDays(3));
        _slots.WriteForeignSlotFile(2, DeadPid, TimeSpan.FromDays(3));

        // The settings document and a temporary half-written file are not slots, whatever else they
        // are named like, and a sweep that read them would delete them.
        File.WriteAllText(Path.Combine(_slots.Location, "anaglyfin-wrapper-settings.json"), "{}");
        File.WriteAllText(Path.Combine(_slots.Location, "notes.txt"), "not a slot");

        var found = TranscodeSlotStore.EnumerateSlotFiles(_slots.Location);

        Assert.Equal(2, found.Length);
        Assert.All(found, file => Assert.StartsWith(TranscodeSlotStore.SlotFileNamePrefix, Path.GetFileName(file), StringComparison.Ordinal));
    }

    // ----- what a mark states ------------------------------------------------------------

    [Fact]
    public void TheMarkOfAClaimStatesTheOwnerTheClaimAndTheBoot()
    {
        var claimed = new DateTimeOffset(2026, 9, 24, 18, 25, 0, TimeSpan.Zero);

        var mark = TranscodeSlotStore.FormatOwnerMark(4242, claimed, "boot-from-the-previous-container");

        var lines = mark.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Equal("pid=4242", lines[0]);
        Assert.Equal("claimed=2026-09-24T18:25:00.0000000+00:00", lines[1]);
        Assert.Equal("boot=boot-from-the-previous-container", lines[2]);
    }

    [Fact]
    public void AHostThatStatesNoBootIdentifierWritesTheMarkItAlwaysWrote()
    {
        var mark = TranscodeSlotStore.FormatOwnerMark(4242, DateTimeOffset.UtcNow, bootId: null);

        // No empty key, and no trailing line: the two lines are the whole document, which is what an
        // earlier build of either reader expects.
        Assert.Equal(2, mark.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("boot=", mark, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstTwoLinesOfAMarkAreItsOwnerAndItsClaim()
    {
        var claimed = new DateTimeOffset(2026, 9, 24, 18, 25, 0, TimeSpan.Zero);

        var mark = TranscodeSlotStore.FormatOwnerMark(4242, claimed, "boot");

        // The order is part of the compatibility: an older reader stops at what it knows, so what it
        // knows has to come first.
        Assert.StartsWith("pid=4242\nclaimed=", mark, StringComparison.Ordinal);
    }

    [Fact]
    public void AMarkWrittenBeforeTheBootIdentifierExistsStillStatesItsOwner()
    {
        // This is the upgrade case: the file in the directory was written by the build that was
        // running yesterday, and refusing it would mean an inherited leftover that can never be
        // recognised as a leftover.
        Assert.True(TranscodeSlotStore.TryReadOwner("pid=99\nclaimed=2026-09-24T18:25:00.0000000+00:00\n", out var owner));

        Assert.Equal(99, owner.ProcessId);
        Assert.Null(owner.BootId);

        // Read as this instance's own, which is the judgement those two lines always had.
        Assert.True(owner.IsFromBoot("some-boot-this-machine-states"));
        Assert.True(owner.IsFromBoot(null));
    }

    [Fact]
    public void ABootIdentifierIsMatchedAgainstTheInstanceThatWroteIt()
    {
        Assert.True(TranscodeSlotStore.TryReadOwner("pid=99\nclaimed=x\nboot=Boot-AAA\n", out var owner));

        Assert.Equal("Boot-AAA", owner.BootId);
        Assert.True(owner.IsFromBoot("boot-aaa"));
        Assert.False(owner.IsFromBoot("boot-bbb"));
    }

    [Fact]
    public void AKeyThisBuildNeverSawCostsTheMarkNothing()
    {
        // The mark is diagnostics on a file whose lock is the handle. A reader that gave up at a line
        // it had not met would turn the day this format grows a key into the day nobody could read
        // the directory any more.
        Assert.True(
            TranscodeSlotStore.TryReadOwner("pid=99\nclaimed=x\nboot=boot-aaa\ncpu=that-was-busy\n", out var owner));

        Assert.Equal(99, owner.ProcessId);
        Assert.Equal("boot-aaa", owner.BootId);
    }

    [Fact]
    public void AMarkThatStatesNoProcessIdStatesNothingAnyoneCanActOn()
    {
        Assert.False(TranscodeSlotStore.TryReadOwner("claimed=2026-09-24T18:25:00.0000000+00:00\n", out var owner));
        Assert.Equal(0, owner.ProcessId);

        // A wrapper that died between creating the file and writing its mark, and a file somebody
        // edited by hand, are the same statement: nobody is named.
        Assert.False(TranscodeSlotStore.TryReadOwner(string.Empty, out _));
        Assert.False(TranscodeSlotStore.TryReadOwner("pid=not-a-number\nclaimed=x\n", out _));
    }

    [Fact]
    public void AWriterOfThisBuildProducesAMarkThisBuildReadsBack()
    {
        var slotPath = _slots.SlotPath(0);
        Directory.CreateDirectory(_slots.Location);

        using (var claimed = new FileStream(
                   slotPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read,
                   bufferSize: 4096,
                   FileOptions.DeleteOnClose))
        {
            TranscodeSlotStore.WriteOwnerMark(claimed);

            Assert.True(TranscodeSlotStore.TryReadOwner(File.ReadAllText(slotPath), out var owner));

            // What the guard wrote about itself is what a reader sees: this process, on this boot.
            Assert.Equal(Environment.ProcessId, owner.ProcessId);
            Assert.Equal(TranscodeSlotStore.CurrentBootId, owner.BootId);
        }
    }

    [Fact]
    public void ABootIdentifierIsReadOnceAndIsUsable()
    {
        var stated = TranscodeSlotStore.CurrentBootId;

        if (stated is null)
        {
            // A host that states nothing is a legal answer, and it is the one a mark is judged
            // against: no mark may then be called foreign.
            return;
        }

        Assert.Equal(stated, stated.Trim());
        Assert.Equal(stated, TranscodeSlotStore.CurrentBootId);
    }

    // ----- what a slot file proves -------------------------------------------------------

    [Fact]
    public void AFileNobodyHoldsAndWhoseOwnerIsGoneProvesItsJobIsOver()
    {
        var path = _slots.WriteForeignSlotFile(0, DeadPid, TimeSpan.FromSeconds(5));

        Assert.Equal(TranscodeSlotEvidence.DeadOwner, TranscodeSlotStore.ReadEvidence(path));
    }

    [Fact]
    public void AFileNobodyHoldsWhoseOwnerStillExistsProvesItsJobIsRunning()
    {
        var path = _slots.WriteForeignSlotFile(
            0,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(5));

        Assert.Equal(TranscodeSlotEvidence.Held, TranscodeSlotStore.ReadEvidence(path));
    }

    [Fact]
    public void AFileAnotherHandleIsOpenOnIsHeldWhateverItsMarkSays()
    {
        var path = _slots.WriteForeignSlotFile(0, DeadPid, TimeSpan.FromDays(3));

        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // The owner the mark names is provably dead and the file is three days old, and none of that
        // is worth asking about: a handle is open on the file, so a job is using the slot. This is
        // the evidence that keeps a takeover from deleting a running job's claim, and it outranks the
        // mark.
        Assert.Equal(TranscodeSlotEvidence.Held, TranscodeSlotStore.ReadEvidence(path));
    }

    [Fact]
    public void AFileWrittenByAnotherBootIsNotJudgedAgainstThisMachinesProcesses()
    {
        // The mark names this very process, which is a live wrapper on the container that wrote the
        // file and a coincidence on this one. Read as this machine's process table it would refuse a
        // slot for as long as whatever runs as this number keeps running; read as what it is, it is a
        // leftover of an instance that no longer exists.
        var path = _slots.WriteForeignSlotFile(
            0,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(5),
            bootId: "the-previous-container");

        Assert.Equal(TranscodeSlotEvidence.DeadOwner, TranscodeSlotStore.ReadEvidence(path, "this-container"));
    }

    [Fact]
    public void AFileWrittenOnThisBootIsJudgedAgainstThisMachinesProcesses()
    {
        // The other side of the same pair: the boot matches, so the number in the mark is a number
        // this machine handed out and the answer it gives is the answer.
        var path = _slots.WriteForeignSlotFile(
            0,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(5),
            bootId: "this-container");

        Assert.Equal(TranscodeSlotEvidence.Held, TranscodeSlotStore.ReadEvidence(path, "this-container"));
    }

    [Fact]
    public void AReaderThatKnowsNoBootJudgesEveryMarkByTheProcessItNames()
    {
        var live = _slots.WriteForeignSlotFile(
            0,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(5),
            bootId: "some-other-instance");

        var dead = _slots.WriteForeignSlotFile(1, "4242", TimeSpan.FromSeconds(5), bootId: "some-other-instance");

        // Without an identifier of its own there is nothing to compare a mark against, and the fallback
        // is the judgement that was made before the field existed. This is also why the identifier is
        // read from the kernel rather than assumed: a reader that could not tell its own boot would
        // read every foreign mark as its own.
        Assert.Equal(TranscodeSlotEvidence.Held, TranscodeSlotStore.ReadEvidence(live, null));
        Assert.Equal(TranscodeSlotEvidence.DeadOwner, TranscodeSlotStore.ReadEvidence(dead, null));
    }

    [Fact]
    public void AFileWhoseMarkStatesNoOwnerProvesNothing()
    {
        var path = _slots.WriteSlotFileWithMark(0, "claimed=whatever\n", TimeSpan.FromDays(3));

        // Not "held" - nothing is proved to be running - and not "dead owner" - nothing is proved to
        // have stopped. It is the one verdict a caller has to bring its own evidence to, which is why
        // the guard brings an age and the startup pass brings nothing.
        Assert.Equal(TranscodeSlotEvidence.Unproven, TranscodeSlotStore.ReadEvidence(path));
    }

    [Fact]
    public void AFileThatIsNotThereIsNotThereToBeTakenOver()
    {
        // The name a slot number had a moment ago, and the file is gone. A caller that was about to act
        // on it is told there is nothing to act on, which is the same answer as a file somebody else
        // is holding and for the same reason: this caller has no file in front of it to decide about.
        Assert.Equal(TranscodeSlotEvidence.Held, TranscodeSlotStore.ReadEvidence(_slots.SlotPath(7)));
    }

    // ----- the process question ----------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AProcessIdThatIsNotAProcessProvesNobodyIsDead(int processId)
    {
        // Not an answer at all, and an unanswerable question is answered as if somebody were still
        // running: the mistake this class is not allowed to make is the other one.
        Assert.True(TranscodeSlotStore.IsProcessLive(processId));
    }

    [Fact]
    public void ThisProcessIsALiveProcess()
    {
        Assert.True(TranscodeSlotStore.IsProcessLive(Environment.ProcessId));
    }

    [Fact]
    public void AProcessIdNobodyCouldHaveHandedOutIsNotAlive()
    {
        Assert.False(TranscodeSlotStore.IsProcessLive(int.Parse(DeadPid, CultureInfo.InvariantCulture)));
    }
}
