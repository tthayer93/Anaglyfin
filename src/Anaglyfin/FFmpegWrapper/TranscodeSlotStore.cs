using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// The slot files an Anaglyfin transcode is counted by: where they live, what they say, and what
/// that says about the job that wrote them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is in the plugin's assembly and shared.</b> A slot is a contract between processes
/// that share nothing else. The wrapper creates one, writes its owner into it, and holds it open for
/// the whole transcode; the wrapper's own abandon check reads them back, and so does the plugin's
/// startup pass, which is the only thing that cleans a directory a restart left behind. Two readers
/// of one file format, in two assemblies, is two chances to disagree about what a file means - so
/// the format, the directory, and the judgement out of the file live here and are read from here.
/// The plugin project is where the shared half of the wrapper lives for the same reason
/// <see cref="WrapperArgumentRewriter"/> does; everything this type touches is plain .NET, and the
/// wrapper still never loads a server assembly through it.
/// </para>
/// <para>
/// <b>The file format.</b> Three lines, written under the handle that is the claim:
/// <c>pid=&lt;process id&gt;</c>, <c>claimed=&lt;UTC timestamp&gt;</c>, and
/// <c>boot=&lt;boot identifier&gt;</c>. The last of the three is the one that was added later, and
/// the reader answers its absence rather than refusing the file: a slot written by the build that
/// was running yesterday is a slot whose two lines mean exactly what they meant yesterday. An
/// unknown key is ignored for the same reason - the mark is diagnostics, and a reader that threw up
/// at a line it did not recognise would turn a format addition into a lock directory nobody can
/// clean.
/// </para>
/// <para>
/// <b>Why a boot identifier at all.</b> A process id is only a process id within one instance of a
/// machine. A container that restarts hands the same numbers out again, and the deployment this
/// format was written for keeps its slot directory on storage that outlives the container - so
/// without the boot identifier, a leftover naming <c>pid=42</c> is judged against whatever the
/// server happens to be running as 42 now, and a slot can be refused for a day because a number
/// was reused. The identifier does not make a foreign pid readable - it makes it unreadable, which
/// is the honest answer, and the one that lets the leftover be recognised as one.
/// </para>
/// <para>
/// <b>Nothing here is a policy.</b> This type decides what a slot file proves and says so; what a
/// caller does with <see cref="TranscodeSlotEvidence.Unproven"/> - whether it waits, refuses, or
/// takes the file over on age - belongs to the caller, because the wrapper and the plugin's startup
/// pass answer that one differently.
/// </para>
/// </remarks>
public static class TranscodeSlotStore
{
    /// <summary>
    /// Name of the variable holding the directory the concurrency slots are created in - the one
    /// setting both processes have to agree on, since a slot directory each process reads its own
    /// copy of is two limits that add up to none.
    /// </summary>
    public const string LockDirectoryEnvironmentVariable = "ANAGLYFIN_LOCK_DIR";

    /// <summary>Extension of a slot file, so an administrator can spot them in the directory.</summary>
    public const string SlotFileExtension = ".lock";

    /// <summary>Prefix of a slot file name; the slot number follows it.</summary>
    public const string SlotFileNamePrefix = "anaglyfin-transcode-";

    /// <summary>The search pattern that finds the slot files in a slot directory.</summary>
    public const string SlotFilePattern = "*" + SlotFileExtension;

    /// <summary>The mark key naming the owner's process id.</summary>
    public const string OwnerProcessIdKey = "pid";

    /// <summary>The mark key naming the time the slot was claimed.</summary>
    public const string ClaimedKey = "claimed";

    /// <summary>The mark key naming the boot or container instance that wrote the mark.</summary>
    public const string BootKey = "boot";

    /// <summary>
    /// The file a Linux kernel states its boot identifier in. The wrapper and the plugin both read
    /// it through this type, so both answer the same question about the same file.
    /// </summary>
    public const string BootIdentifierPath = "/proc/sys/kernel/random/boot_id";

    /// <summary>
    /// The upper bound the kernel's boot identifier is expected to fit in. A file that states
    /// something far longer is not a boot identifier, and a mark carrying a line the reader has to
    /// treat as an identifier of unknown length is a mark whose key it cannot trust.
    /// </summary>
    private const int MaxBootIdentifierLength = 128;

    private static readonly Lazy<string?> CachedBootId = new(ReadBootIdentifier);

    /// <summary>
    /// Gets the directory used when <see cref="LockDirectoryEnvironmentVariable"/> is unset.
    /// </summary>
    /// <remarks>
    /// Under the temp path, because a slot is a runtime artefact and not state worth keeping: the
    /// operating system removes it with the process that created it, so a default location on a
    /// rebooted or cleaned machine can only ever be empty. An absolute, shared location is what a
    /// multi-container deployment configures - and the wrapper's process and the plugin's process
    /// inherit their temp path from the same server, so the default is shared too.
    /// </remarks>
    public static string DefaultLockDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "anaglyfin", "ffmpeg-wrapper");

    /// <summary>
    /// Gets this instance's boot identifier, or <c>null</c> when the machine does not state one.
    /// </summary>
    /// <remarks>
    /// Read once and remembered: the answer cannot change while this process lives, and a slot
    /// directory is read more often than a boot happens.
    /// </remarks>
    public static string? CurrentBootId => CachedBootId.Value;

    /// <summary>
    /// Reads the slot directory one channel stated.
    /// </summary>
    /// <param name="configuredValue">The raw value, if any.</param>
    /// <returns>An absolute directory path.</returns>
    /// <remarks>
    /// A relative value is anchored at the temp location instead of being used as written: the
    /// wrapper inherits its working directory from whatever started it, and Jellyfin starts
    /// transcode helpers in its transcode temp. A concurrency limit that silently lives in a
    /// directory nobody chose is a limit nobody can find - and a plugin's startup cleanup pass that
    /// resolved a relative value against its own working directory would clean a directory the
    /// wrapper was never using.
    /// </remarks>
    public static string ResolveLockDirectory(string? configuredValue)
    {
        var text = string.IsNullOrWhiteSpace(configuredValue) ? null : configuredValue.Trim();

        if (text is null)
        {
            return DefaultLockDirectory;
        }

        return Path.IsPathRooted(text) ? text : Path.Combine(DefaultLockDirectory, text);
    }

    /// <summary>
    /// Reads the slot directory out of an environment view.
    /// </summary>
    /// <param name="readVariable">
    /// Reads one environment variable; null when it is not set. Taken as a delegate so the caller
    /// does not have to change the process it is testing.
    /// </param>
    /// <returns>The directory both processes will count slots in.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="readVariable"/> is null.</exception>
    public static string ResolveLockDirectory(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        return ResolveLockDirectory(readVariable(LockDirectoryEnvironmentVariable));
    }

    /// <summary>
    /// The file one numbered slot lives in.
    /// </summary>
    /// <param name="lockDirectory">The slot directory.</param>
    /// <param name="slot">The slot number.</param>
    /// <returns>The path the claim is written to.</returns>
    /// <exception cref="ArgumentException"><paramref name="lockDirectory"/> is blank.</exception>
    public static string SlotPath(string lockDirectory, int slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);

        return Path.Combine(
            lockDirectory,
            SlotFileNamePrefix + slot.ToString(CultureInfo.InvariantCulture) + SlotFileExtension);
    }

    /// <summary>
    /// Lists the slot files of a directory.
    /// </summary>
    /// <param name="lockDirectory">The directory to read; it does not have to exist.</param>
    /// <returns>
    /// The slot files, or nothing. A directory that is absent, unreadable or in the middle of being
    /// created holds nothing this caller could have judged anyway, so the failure is answered with
    /// an empty list rather than with an exception the caller would only have to swallow.
    /// </returns>
    public static string[] EnumerateSlotFiles(string lockDirectory)
    {
        try
        {
            return Directory.GetFiles(lockDirectory, SlotFilePattern);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Writes who owns a freshly claimed slot.
    /// </summary>
    /// <param name="slot">The handle of the slot that was just claimed, still held.</param>
    /// <remarks>
    /// Written under the handle that is the claim, so a reader either sees the owner or sees nothing
    /// in a file nobody holds. The failure is swallowed for the reason the mark exists: the handle is
    /// the lock and it is held, so a mark that could not be written costs the deployment a
    /// diagnostic and not a slot.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="slot"/> is null.</exception>
    public static void WriteOwnerMark(FileStream slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        try
        {
            var bytes = Encoding.UTF8.GetBytes(
                FormatOwnerMark(Environment.ProcessId, DateTimeOffset.UtcNow, CurrentBootId));

            slot.Write(bytes, 0, bytes.Length);
            slot.Flush();
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Renders one owner mark.
    /// </summary>
    /// <param name="processId">The owner's process id.</param>
    /// <param name="claimedUtc">When the slot was claimed.</param>
    /// <param name="bootId">
    /// The boot identifier to state, or <c>null</c> to state none - which is what a machine that
    /// does not state one writes, and what a mark written before the field looked like.
    /// </param>
    /// <returns>The mark text, one key per line.</returns>
    /// <remarks>
    /// The two lines this format always had come first and in their original spelling, with the boot
    /// identifier appended after them: a reader from an earlier build stops at the <c>pid</c> line it
    /// knows and never reaches the rest, so appending is what keeps an upgrade from being a lock
    /// directory its own old build cannot read.
    /// </remarks>
    public static string FormatOwnerMark(int processId, DateTimeOffset claimedUtc, string? bootId)
    {
        var mark = string.Format(
            CultureInfo.InvariantCulture,
            "{0}={1}\n{2}={3:O}\n",
            OwnerProcessIdKey,
            processId,
            ClaimedKey,
            claimedUtc);

        return string.IsNullOrWhiteSpace(bootId)
            ? mark
            : mark + BootKey + "=" + bootId.Trim() + "\n";
    }

    /// <summary>
    /// Reads the owner a mark states.
    /// </summary>
    /// <param name="text">The mark text.</param>
    /// <param name="owner">The owner it states, when it states one.</param>
    /// <returns>
    /// <c>true</c> when the mark names a process id. Unknown keys are passed over and the keys are
    /// read in any order, because the only thing a slot mark has to be is readable by whoever ends up
    /// looking at the directory.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static bool TryReadOwner(string text, out TranscodeSlotOwner owner)
    {
        ArgumentNullException.ThrowIfNull(text);

        int? processId = null;
        string? bootId = null;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = line.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            var key = pair[0].Trim();
            var value = pair[1].Trim();

            if (processId is null && key.Equals(OwnerProcessIdKey, StringComparison.Ordinal))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    processId = parsed;
                }

                continue;
            }

            if (bootId is null && key.Equals(BootKey, StringComparison.Ordinal)
                && value.Length > 0 && value.Length <= MaxBootIdentifierLength)
            {
                bootId = value;
            }
        }

        owner = new TranscodeSlotOwner(processId ?? 0, bootId);

        return processId is not null;
    }

    /// <summary>
    /// Decides what a slot file proves.
    /// </summary>
    /// <param name="path">The slot file to read.</param>
    /// <returns>
    /// <see cref="TranscodeSlotEvidence.Held"/> when the file is in use or its owner is alive,
    /// <see cref="TranscodeSlotEvidence.DeadOwner"/> when the file is unheld and its owner cannot be
    /// running, and <see cref="TranscodeSlotEvidence.Unproven"/> when the mark answers nothing.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Evidence one is the kernel's.</b> A wrapper holds its slot open for the whole transcode, so
    /// a handle this method can open exclusively is proof that no wrapper holds the file - the
    /// question a process id cannot answer, because a file a live process is holding can name a
    /// process id that has since been recycled.
    /// </para>
    /// <para>
    /// <b>Evidence two is the mark's.</b> With nobody holding the file, its owner is either a process
    /// that still exists, a process that does not, or a statement about an instance of the machine
    /// this process cannot inspect. Only the middle one is proof of anything positive; a foreign
    /// boot's mark is not treated as this machine's process table - which is what it used to be -
    /// because a number read out of somebody else's instance is not an answer about this one.
    /// </para>
    /// <para>
    /// The probe is closed before the mark is read from a second open, and both are re-asked by a
    /// caller that is about to delete the file: the moment between "nobody holds this" and "this is
    /// gone" is exactly the moment another wrapper can claim the slot in.
    /// </para>
    /// </remarks>
    public static TranscodeSlotEvidence ReadEvidence(string path)
        => ReadEvidence(path, CurrentBootId);

    /// <summary>
    /// Decides what a slot file proves, against a boot identifier the caller names as its own.
    /// </summary>
    /// <param name="path">The slot file to read.</param>
    /// <param name="currentBootId">
    /// The boot identifier to treat as this instance's, or <c>null</c> for a reader that does not know
    /// its own and therefore judges every mark's process id on its face.
    /// </param>
    /// <returns>What the file proves; see <see cref="ReadEvidence(string)"/>.</returns>
    /// <remarks>
    /// The parameterless overload is the one a running process uses, because a running process knows
    /// its own boot or does not and has nothing else to answer with. This one exists so that the
    /// mismatch half of the judgement - the half a deployment only meets when a container was
    /// recreated under it - can be stated rather than hoped for by whoever is testing it.
    /// </remarks>
    public static TranscodeSlotEvidence ReadEvidence(string path, string? currentBootId)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (IOException)
        {
            // Either a wrapper is holding it, or the file is gone - which is the same answer for a
            // caller that was about to act on it.
            return TranscodeSlotEvidence.Held;
        }
        catch (UnauthorizedAccessException)
        {
            return TranscodeSlotEvidence.Held;
        }

        return ReadOwnerEvidence(path, currentBootId);
    }

    /// <summary>
    /// Asks the operating system whether a recorded process still exists.
    /// </summary>
    /// <param name="processId">The process id a slot file recorded.</param>
    /// <returns><c>true</c> when it exists or the question cannot be answered.</returns>
    /// <remarks>
    /// Unknown answers count as alive, and only for a mark that was read as this instance's own: the
    /// wrapper cannot inspect other containers, so a pid it cannot resolve may simply be somebody
    /// else's namespace, and erring towards "busy" keeps the guarantee that matters, which is never
    /// two Anaglyfin encodes over one slot. A process id of zero or below is not a process at all
    /// and so proves nothing either.
    /// </remarks>
    public static bool IsProcessLive(int processId)
    {
        if (processId <= 0)
        {
            return true;
        }

        if (processId == Environment.ProcessId)
        {
            // A slot that names this very process belongs to a sibling guard in it, which is the
            // situation the tests exercise; it is never abandoned.
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
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
    /// Reads the machine's own boot identifier.
    /// </summary>
    /// <remarks>
    /// Nothing is assumed about the spelling beyond the trimming, and nothing is invented when the
    /// file is not there: a machine that states no identifier gets marks that state none, which is
    /// the format this type reads as "this instance's own" - the same answer a Windows host or a
    /// container with that path masked has always had.
    /// </remarks>
    private static string? ReadBootIdentifier()
    {
        try
        {
            if (!File.Exists(BootIdentifierPath))
            {
                return null;
            }

            var text = File.ReadAllText(BootIdentifierPath).Trim();

            return text.Length == 0 || text.Length > MaxBootIdentifierLength ? null : text;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the owner evidence of a file nobody holds open.
    /// </summary>
    private static TranscodeSlotEvidence ReadOwnerEvidence(string path, string? currentBootId)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            // Unreadable, or gone between the probe and this read. Proves nobody is dead, and also
            // nothing about anybody being alive, so it is left to a caller holding an age.
            return TranscodeSlotEvidence.Unproven;
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot be read, cannot be proved ownerless, and cannot be deleted by the process that
            // cannot read it. Counted as in use, which is the answer that leaves the file alone.
            return TranscodeSlotEvidence.Held;
        }

        if (!TryReadOwner(text, out var owner))
        {
            return TranscodeSlotEvidence.Unproven;
        }

        // A mark from another boot or container names a process id of that instance and not of this
        // one. It is not this machine's live process - that is the misreading the boot identifier was
        // added to stop, and it is a misreading that refuses a slot until an administrator clears the
        // directory by hand - and nobody holds the file, so the instance that wrote it is gone.
        if (!owner.IsFromBoot(currentBootId))
        {
            return TranscodeSlotEvidence.DeadOwner;
        }

        return IsProcessLive(owner.ProcessId) ? TranscodeSlotEvidence.Held : TranscodeSlotEvidence.DeadOwner;
    }

    /// <summary>
    /// Whether a filesystem failure is one a slot is answered by doing nothing about.
    /// </summary>
    /// <remarks>
    /// A slot directory is a directory on somebody's storage: absent, full, read-only, or a mount
    /// that moved. None of those is a decision to be made here, and none is worth a transcode. The
    /// callers that sweep a slot directory answer it the same way, and the list is public so that
    /// they read one definition rather than each keeping their own idea of which exceptions a
    /// directory is allowed to produce.
    /// </remarks>
    public static bool IsFileFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException;
}
