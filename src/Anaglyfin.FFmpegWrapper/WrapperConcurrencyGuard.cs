using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Limits how many Anaglyfin transcodes the server may have running at the same time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a file and not a counter.</b> The wrapper is started once per playback, so each
/// concurrent Anaglyfin job is a different process and there is no shared memory to count
/// in. A slot is therefore a file whose <em>existence</em> is the claim: creating it is
/// atomic, so two wrappers racing for the same slot produce exactly one winner, and no
/// protocol, lock server or priority ordering is needed between processes that know
/// nothing about each other.
/// </para>
/// <para>
/// <b>Why the claim disappears by itself.</b> The slot file is opened with
/// <see cref="FileOptions.DeleteOnClose"/> and the handle is held for the whole transcode.
/// The operating system removes the file when the last handle closes, which happens when
/// the wrapper exits normally, when it is killed, and when the container it ran in goes
/// away. A crashed or SIGKILLed transcode therefore cannot leave the limit jammed, which
/// is the failure mode every other file-locking scheme has: an Anaglyfin playback that
/// never died is not distinguishable from one that did, so the release has to be the
/// kernel's job and not the wrapper's.
/// </para>
/// <para>
/// <b>Which leftovers are taken over, and how soon.</b> What survives a killed wrapper is a
/// file on storage that outlived the process - which is the ordinary outcome of a slot
/// directory on a mounted volume and a container that was recreated. Such a file is
/// evidence, and this class reads it as soon as it is asked: nobody can open the file
/// exclusively, so no wrapper holds it, and the owner its mark records is gone, either
/// because that process id no longer exists or because the mark belongs to a boot or
/// container that is not this one. Those two facts together are the whole of the takeover
/// test and they are answerable in milliseconds, so a slot whose owner is provably dead is
/// taken over at any age. Age is not the second opinion it used to be: it is now the only
/// evidence left for the case where the mark itself cannot be read, and a file that says
/// nothing is kept for <see cref="DefaultAbandonedAfter"/> before it is removed.
/// </para>
/// <para>
/// <b>Why a live owner is never second-guessed.</b> A slot that can be opened exclusively
/// and still names a process that exists is a slot the wrapper leaves alone, and so is one
/// whose mark cannot be read at all, until age says so. Taking over a slot that is in use
/// is the one thing this class must not do: a refusal that costs an administrator an
/// afternoon is a smaller failure than two MVC encodes on one machine. That is also why a
/// foreign mark is never read as this machine's process table - a process id out of another
/// instance answers no question here, and pretending otherwise is what refused a slot for a
/// day over a recycled number.
/// </para>
/// <para>
/// <b>Why a refusal waits a little first.</b> The limit is a capacity answer and refusing is
/// right, but the seconds around a stop-and-start are not a capacity question: Jellyfin stops
/// an encoder, the browser re-requests the same playlist within about a second and a wrapper
/// that refused immediately would report a full machine while the slot came free before the
/// request was over. So the claim is re-tried for a bounded while - <see cref="SlotWait"/>,
/// polled at <see cref="SlotPollInterval"/> - before it is answered with a refusal. The wait
/// borrows nothing: it does not queue an FFmpeg, it does not raise the limit, and it does not
/// let two encodes share a slot; it only declines to treat a two-second handover as a
/// full-time decision.
/// </para>
/// <para>
/// Only Anaglyfin jobs take a slot. A command the rewriter passed through is ordinary
/// playback that the server's own transcode scheduling already governs, and taking a slot
/// for it would let ordinary transcodes starve the 3D versions the limit exists to
/// protect - see <see cref="WrapperApplication"/>.
/// </para>
/// </remarks>
public sealed class WrapperConcurrencyGuard : IDisposable
{
    /// <summary>Extension of a slot file, so an administrator can spot them in the directory.</summary>
    public const string SlotFileExtension = TranscodeSlotStore.SlotFileExtension;

    /// <summary>Prefix of a slot file name; the slot number follows it.</summary>
    public const string SlotFileNamePrefix = TranscodeSlotStore.SlotFileNamePrefix;

    /// <summary>
    /// How long an untouched slot file whose mark proves nothing has to sit before it may be
    /// treated as the leavings of a dead wrapper rather than of a running job.
    /// </summary>
    /// <remarks>
    /// Long on purpose: it has to be longer than the longest transcode it will ever see, because
    /// taking over a slot that is in use is the one thing this class must not do, and a file that
    /// states no readable owner is a file whose owner is unknown. A file that does state one and
    /// proves it gone is taken over without waiting for this at all.
    /// </remarks>
    public static readonly TimeSpan DefaultAbandonedAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// How often a wrapper that is waiting for a slot looks again.
    /// </summary>
    /// <remarks>
    /// Not configurable: it is the resolution of the wait rather than its length, and a value short
    /// enough to matter would be a loop over a directory shared with the other transcodes on the
    /// server.
    /// </remarks>
    public static readonly TimeSpan SlotPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>No wait at all, which is what a caller that wants an immediate answer asks for.</summary>
    public static readonly TimeSpan NoSlotWait = TimeSpan.Zero;

    /// <summary>
    /// Claim rounds per acquisition: one over the slots as they are, and one more only
    /// after abandoned files were cleared.
    /// </summary>
    private const int MaximumClaimRounds = 2;

    private readonly string _lockDirectory;
    private readonly int _maxConcurrentTranscodes;
    private readonly TimeSpan _abandonedAfter;
    private readonly TimeSpan _slotWait;

    private FileStream? _slot;

    /// <summary>
    /// Creates a guard over the directories, limit and wait of a wrapper configuration.
    /// </summary>
    /// <param name="options">The options of this wrapper invocation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public WrapperConcurrencyGuard(FFmpegWrapperOptions options)
        : this(LockDirectoryOf(options), LimitOf(options), DefaultAbandonedAfter, SlotWaitOf(options))
    {
    }

    /// <summary>
    /// Creates a guard over an explicit slot directory.
    /// </summary>
    /// <param name="lockDirectory">
    /// Directory the slot files live in. It is shared by every wrapper process that is
    /// supposed to share one limit, which is why the default is a machine-wide location
    /// rather than a per-process one.
    /// </param>
    /// <param name="maxConcurrentTranscodes">How many slots exist. At least one.</param>
    /// <param name="abandonedAfter">
    /// Overrides <see cref="DefaultAbandonedAfter"/>. Present for tests and for unusual
    /// storage; production does not pass it.
    /// </param>
    /// <param name="slotWait">
    /// How long a failed acquisition keeps trying before it answers "no slot". The deployment's
    /// wait arrives through the options constructor; a caller naming its own directory is a caller
    /// that wants the answer it asked for, so nothing is waited unless it is stated here.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="lockDirectory"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxConcurrentTranscodes"/> is below one, <paramref name="abandonedAfter"/>
    /// is not positive, or <paramref name="slotWait"/> is negative.
    /// </exception>
    public WrapperConcurrencyGuard(
        string lockDirectory,
        int maxConcurrentTranscodes,
        TimeSpan? abandonedAfter = null,
        TimeSpan? slotWait = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentTranscodes, 1);

        var takeOverAfter = abandonedAfter ?? DefaultAbandonedAfter;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(takeOverAfter, TimeSpan.Zero);

        var wait = slotWait ?? NoSlotWait;
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);

        _lockDirectory = lockDirectory;
        _maxConcurrentTranscodes = maxConcurrentTranscodes;
        _abandonedAfter = takeOverAfter;
        _slotWait = wait;
    }

    /// <summary>Gets the limit this guard enforces.</summary>
    public int MaxConcurrentTranscodes => _maxConcurrentTranscodes;

    /// <summary>Gets the directory this guard creates slot files in.</summary>
    public string LockDirectory => _lockDirectory;

    /// <summary>
    /// Gets how long an acquisition that found every slot taken keeps trying before it refuses.
    /// </summary>
    public TimeSpan SlotWait => _slotWait;

    /// <summary>Gets the number of the slot this instance holds, or -1 while it holds none.</summary>
    public int HeldSlot { get; private set; } = -1;

    /// <summary>Gets a value indicating whether this instance currently holds a slot.</summary>
    public bool IsHolding => HeldSlot >= 0;

    /// <summary>
    /// Takes a slot for one Anaglyfin transcode, if the limit leaves one free.
    /// </summary>
    /// <returns>
    /// <c>true</c> when a slot is now held and the job may start; <c>false</c> when every
    /// slot stayed taken for the whole of <see cref="SlotWait"/>, in which case nothing was
    /// created and the caller must not start FFmpeg.
    /// </returns>
    /// <remarks>
    /// Nothing about a failed acquisition is temporary in the way a queue would fix: the limit is
    /// the administrator's answer to "how much of this machine may 3D encoding use", so the correct
    /// response is to refuse this playback and let the server report it, not to hold an FFmpeg that
    /// would only add to the load. What the bounded wait answers is narrower and more common: the
    /// moment between one job releasing a slot and the next one asking for it, which a refusal
    /// delivered inside it reports as a full machine that is not one.
    /// </remarks>
    /// <exception cref="InvalidOperationException">This instance already holds a slot.</exception>
    /// <exception cref="IOException">The slot directory could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The slot directory exists but may not be written to. Both failures travel rather
    /// than turning into <c>false</c>: a guard that cannot see the slots cannot promise
    /// that the limit holds, and refusing for a reason the administrator can read beats
    /// promising something it cannot keep.
    /// </exception>
    public bool TryAcquire()
    {
        if (IsHolding)
        {
            throw new InvalidOperationException(
                "This wrapper already holds an Anaglyfin transcode slot; one wrapper process starts one FFmpeg.");
        }

        Directory.CreateDirectory(_lockDirectory);

        var since = Stopwatch.StartNew();

        while (true)
        {
            for (var round = 0; round < MaximumClaimRounds; round++)
            {
                for (var slot = 0; slot < _maxConcurrentTranscodes; slot++)
                {
                    if (TryClaim(slot))
                    {
                        return true;
                    }
                }

                // Every slot looked taken. Only if something actually looked abandoned is
                // there anything to gain from looking again.
                if (!TryClearAbandonedSlots())
                {
                    break;
                }
            }

            var remaining = _slotWait - since.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(SlotPollInterval.TotalMilliseconds, remaining.TotalMilliseconds)));
        }
    }

    /// <summary>
    /// Gives the slot back, which also removes the slot file.
    /// </summary>
    /// <remarks>
    /// Releasing twice, or releasing without holding, is a no-op: the caller's
    /// <c>finally</c> block must never be the thing that fails a playback that already
    /// succeeded.
    /// </remarks>
    public void Release()
    {
        var slot = _slot;
        if (slot is null)
        {
            HeldSlot = -1;
            return;
        }

        _slot = null;
        HeldSlot = -1;

        // Closing the handle is the release, and DeleteOnClose is the removal; there is no
        // second step here to race another wrapper against.
        slot.Dispose();
    }

    /// <inheritdoc />
    /// <remarks>Same as <see cref="Release"/>; the slot is the only resource held.</remarks>
    public void Dispose() => Release();

    /// <summary>
    /// Tries to take one numbered slot.
    /// </summary>
    /// <param name="slot">The slot number.</param>
    /// <returns><c>true</c> when this instance now holds it.</returns>
    private bool TryClaim(int slot)
    {
        var path = SlotPath(slot);
        FileStream? claimed = null;

        try
        {
            claimed = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                // The existence of this file is the claim, and an exclusive create is what
                // makes it atomic. Readers are allowed so that the owner mark below can be
                // looked at while a job runs; nobody else can claim or rewrite the file, and
                // the exclusive open the abandon check performs is exactly what a live holder
                // here refuses.
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.DeleteOnClose);

            TranscodeSlotStore.WriteOwnerMark(claimed);

            _slot = claimed;
            HeldSlot = slot;
            claimed = null;

            return true;
        }
        catch (IOException)
        {
            // Either the file already exists - somebody else is running - or this
            // filesystem will not create it. Both mean "not this slot".
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            // Only the loser has anything left to close here; on the winner the handle is
            // now the slot itself, and closing it would release the job's own claim.
            claimed?.Dispose();
        }
    }

    /// <summary>
    /// Removes slot files that their own evidence says are ownerless, plus the ones age has
    /// decided for.
    /// </summary>
    /// <returns><c>true</c> when at least one file went away.</returns>
    private bool TryClearAbandonedSlots()
    {
        var cleared = false;

        foreach (var file in TranscodeSlotStore.EnumerateSlotFiles(_lockDirectory))
        {
            // The timestamp is taken before the question is asked, so that the removal can compare
            // what it is about to delete against the file the answer was about.
            DateTime observed;
            try
            {
                observed = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception exception) when (TranscodeSlotStore.IsFileFailure(exception))
            {
                continue;
            }

            if (IsAbandoned(file) && TryRemove(file, observed))
            {
                cleared = true;
            }
        }

        return cleared;
    }

    /// <summary>
    /// Decides whether a slot file is the leavings of a wrapper that is gone.
    /// </summary>
    /// <param name="path">The slot file to read.</param>
    /// <returns>
    /// <c>true</c> when the file's owner is provably gone, whatever the file's age, or when the file
    /// states nothing about its owner and has been sitting for <see cref="_abandonedAfter"/>.
    /// </returns>
    /// <remarks>
    /// The two answers are separated because they are different kinds of statement. What the file can
    /// prove on its own - nobody holds it, and the process or instance that claimed it is not coming
    /// back - is enough at any age, and waiting for a day would only extend the outage the leftover
    /// caused. What it cannot prove is not made up for by waiting: an unreadable mark stays somebody
    /// else's file until the abandon window says otherwise, which is the conservative half of the
    /// rule and the reason the window is a day.
    /// </remarks>
    private bool IsAbandoned(string path)
    {
        var evidence = TranscodeSlotStore.ReadEvidence(path);

        if (evidence == TranscodeSlotEvidence.DeadOwner)
        {
            return true;
        }

        if (evidence == TranscodeSlotEvidence.Held)
        {
            return false;
        }

        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (TranscodeSlotStore.IsFileFailure(exception))
        {
            return false;
        }

        return DateTime.UtcNow - lastWriteUtc >= _abandonedAfter;
    }

    /// <summary>
    /// Deletes an abandoned slot file, checking that it is still the one that was abandoned.
    /// </summary>
    /// <param name="path">The file to remove.</param>
    /// <param name="observedLastWriteUtc">
    /// The timestamp the abandonment was decided against, as it stood before this call.
    /// </param>
    /// <returns><c>true</c> when it is gone.</returns>
    /// <remarks>
    /// Two things are re-asked immediately before the unlink, and both are the same question in
    /// different clothes. The timestamp is compared against the one the decision was taken against:
    /// if another wrapper claimed this slot number in the meantime then the file at this path is
    /// new, and deleting it would take a running job's slot away from it. The judgement is then made
    /// again, because the takeover no longer waits for age, so the age comparison this method used to
    /// make is no longer the guard against that race - and a file that stopped being provably dead
    /// two hundred microseconds ago stopped being deletable with it.
    /// </remarks>
    private bool TryRemove(string path, DateTime observedLastWriteUtc)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(path) != observedLastWriteUtc)
            {
                return false;
            }

            if (!IsAbandoned(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string SlotPath(int slot)
        => TranscodeSlotStore.SlotPath(_lockDirectory, slot);

    private static string LockDirectoryOf(FFmpegWrapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.LockDirectory;
    }

    private static int LimitOf(FFmpegWrapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The options type already clamps, but the limit is the one thing this class is
        // not allowed to get wrong, so it clamps again rather than trusting its input.
        return Math.Max(FFmpegWrapperOptions.DefaultMaxConcurrentTranscodes, options.MaxConcurrentTranscodes);
    }

    private static TimeSpan SlotWaitOf(FFmpegWrapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The wait is the one knob a refusal is made of, so it is read as the options type read it:
        // an unusable value is the shipped default, and the shipped default is what a wrapper that
        // was configured with nothing waits.
        return options.SlotWait;
    }
}
