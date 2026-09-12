using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

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
/// <b>Why leftover files are treated as busy.</b> A slot file that is still young is
/// assumed to belong to a running job, and only a file that is both older than
/// <see cref="DefaultAbandonedAfter"/> and provably ownerless - not held by any handle,
/// and recording a process that no longer exists - is cleared. The asymmetry is
/// deliberate: refusing one job because a directory was never cleaned is an
/// administrator's afternoon, while a limit that silently allows two MVC encodes is the
/// machine thrashing during playback. The takeover path exists for the case the kernel
/// cannot handle, namely a slot directory on storage that outlives the machine which
/// wrote to it.
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
    public const string SlotFileExtension = ".lock";

    /// <summary>Prefix of a slot file name; the slot number follows it.</summary>
    public const string SlotFileNamePrefix = "anaglyfin-transcode-";

    /// <summary>
    /// How long an untouched slot file has to sit before it may be treated as the leavings
    /// of a dead wrapper rather than of a running job.
    /// </summary>
    /// <remarks>
    /// Long on purpose: it has to be longer than the longest transcode it will ever see,
    /// because taking over a slot that is in use is the one thing this class must not do.
    /// An MVC encode of a feature film with software decoding runs for hours; a day is
    /// beyond that.
    /// </remarks>
    public static readonly TimeSpan DefaultAbandonedAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// Claim rounds per acquisition: one over the slots as they are, and one more only
    /// after abandoned files were cleared.
    /// </summary>
    private const int MaximumClaimRounds = 2;

    private readonly string _lockDirectory;
    private readonly int _maxConcurrentTranscodes;
    private readonly TimeSpan _abandonedAfter;

    private FileStream? _slot;

    /// <summary>
    /// Creates a guard over the directories and limit of a wrapper configuration.
    /// </summary>
    /// <param name="options">The options of this wrapper invocation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public WrapperConcurrencyGuard(FFmpegWrapperOptions options)
        : this(LockDirectoryOf(options), LimitOf(options), DefaultAbandonedAfter)
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
    /// <exception cref="ArgumentException"><paramref name="lockDirectory"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxConcurrentTranscodes"/> is below one, or <paramref name="abandonedAfter"/>
    /// is not positive.
    /// </exception>
    public WrapperConcurrencyGuard(string lockDirectory, int maxConcurrentTranscodes, TimeSpan? abandonedAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentTranscodes, 1);

        var takeOverAfter = abandonedAfter ?? DefaultAbandonedAfter;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(takeOverAfter, TimeSpan.Zero);

        _lockDirectory = lockDirectory;
        _maxConcurrentTranscodes = maxConcurrentTranscodes;
        _abandonedAfter = takeOverAfter;
    }

    /// <summary>Gets the limit this guard enforces.</summary>
    public int MaxConcurrentTranscodes => _maxConcurrentTranscodes;

    /// <summary>Gets the directory this guard creates slot files in.</summary>
    public string LockDirectory => _lockDirectory;

    /// <summary>Gets the number of the slot this instance holds, or -1 while it holds none.</summary>
    public int HeldSlot { get; private set; } = -1;

    /// <summary>Gets a value indicating whether this instance currently holds a slot.</summary>
    public bool IsHolding => HeldSlot >= 0;

    /// <summary>
    /// Takes a slot for one Anaglyfin transcode, if the limit leaves one free.
    /// </summary>
    /// <returns>
    /// <c>true</c> when a slot is now held and the job may start; <c>false</c> when every
    /// slot is taken, in which case nothing was created and the caller must not start
    /// FFmpeg.
    /// </returns>
    /// <remarks>
    /// Nothing about a failed acquisition is temporary in the way a retry loop would fix:
    /// the limit is the administrator's answer to "how much of this machine may 3D
    /// encoding use", so the correct response is to refuse this playback and let the
    /// server report it, not to queue an FFmpeg that would only add to the load.
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

        return false;
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

            WriteOwnerMark(claimed);

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
    /// Writes who owns the slot, for whoever ends up reading the directory.
    /// </summary>
    /// <param name="slot">The freshly claimed slot handle.</param>
    private static void WriteOwnerMark(FileStream slot)
    {
        try
        {
            var mark = string.Format(
                CultureInfo.InvariantCulture,
                "pid={0}\nclaimed={1:O}\n",
                Environment.ProcessId,
                DateTimeOffset.UtcNow);

            var bytes = Encoding.UTF8.GetBytes(mark);
            slot.Write(bytes, 0, bytes.Length);
            slot.Flush();
        }
        catch (IOException)
        {
            // The mark is diagnostics. The handle is the lock, and it is held.
        }
    }

    /// <summary>
    /// Removes slot files that are old, unheld and ownerless.
    /// </summary>
    /// <returns><c>true</c> when at least one file went away.</returns>
    private bool TryClearAbandonedSlots()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(_lockDirectory, "*" + SlotFileExtension);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var cleared = false;
        foreach (var file in files)
        {
            if (IsAbandoned(file) && TryRemove(file))
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
    /// <c>true</c> only when the file is old, opens without contest, and names no live
    /// process. Anything short of that counts as a running job.
    /// </returns>
    private bool IsAbandoned(string path)
    {
        // Evidence one: a wrapper holding the slot keeps a handle open on it, so an
        // exclusive open here fails while that handle lives.
        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(path);
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        // Evidence two: age. A slot claimed moments ago is a running transcode whatever
        // the filesystem's locking opinions are, and this is the check that makes that
        // true on a filesystem where the probe above is not.
        if (DateTime.UtcNow - lastWriteUtc < _abandonedAfter)
        {
            return false;
        }

        // Evidence three: the recorded owner. An unreadable mark cannot prove anybody is
        // alive, so it is abandoned once it is old; a mark naming a live process is not.
        return !IsRecordedOwnerLive(path);
    }

    /// <summary>
    /// Reads the mark of a slot file and asks the operating system about that process.
    /// </summary>
    private bool IsRecordedOwnerLive(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot read it, cannot prove it dead, and cannot delete it either.
            return true;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = line.Split('=', 2);
            if (pair.Length != 2 || !string.Equals(pair[0].Trim(), "pid", StringComparison.Ordinal))
            {
                continue;
            }

            if (!int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                return false;
            }

            return IsProcessLive(pid);
        }

        return false;
    }

    /// <summary>
    /// Asks whether a recorded process still exists.
    /// </summary>
    /// <param name="pid">The process id a slot file recorded.</param>
    /// <returns><c>true</c> when it exists or the question cannot be answered.</returns>
    /// <remarks>
    /// Unknown answers count as alive. The wrapper cannot inspect other containers, so a
    /// pid it cannot resolve may simply be somebody else's namespace; erring towards "busy"
    /// keeps the guarantee that matters, which is never two Anaglyfin encodes over one
    /// slot.
    /// </remarks>
    private static bool IsProcessLive(int pid)
    {
        if (pid <= 0)
        {
            return true;
        }

        if (pid == Environment.ProcessId)
        {
            // A slot that names this very process belongs to a sibling guard in it, which
            // is the situation the tests exercise; it is never abandoned.
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Deletes an abandoned slot file, checking that it is still the one that was abandoned.
    /// </summary>
    /// <param name="path">The file to remove.</param>
    /// <returns><c>true</c> when it is gone.</returns>
    /// <remarks>
    /// The timestamp is compared again immediately before the delete: if another wrapper
    /// claimed this slot number in the meantime then the file at this path is new, and
    /// deleting it would take a running job's slot away from it.
    /// </remarks>
    private bool TryRemove(string path)
    {
        DateTime observed;
        try
        {
            observed = File.GetLastWriteTimeUtc(path);
            if (DateTime.UtcNow - observed < _abandonedAfter)
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
        => Path.Combine(
            _lockDirectory,
            SlotFileNamePrefix + slot.ToString(CultureInfo.InvariantCulture) + SlotFileExtension);

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
}
