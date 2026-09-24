using System;
using System.Globalization;
using System.IO;
using Anaglyfin.FFmpegWrapper;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// A throwaway slot directory, so a concurrency test observes a real directory without
/// touching the one the host would use.
/// </summary>
/// <remarks>
/// The guard's whole contract is about files on disk, so the tests read those files rather
/// than a counter the guard could keep about itself: "one slot file exists while the job
/// runs and none after it" is the assertion that would still be true if the guard were
/// rewritten tomorrow, and it is the assertion an administrator can repeat with
/// <c>ls</c>.
/// </remarks>
public sealed class TemporarySlotDirectory : IDisposable
{
    public TemporarySlotDirectory()
    {
        Location = Path.Combine(Path.GetTempPath(), "anaglyfin-wrapper-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Gets the directory path; it is not created until something asks for a slot.</summary>
    public string Location { get; }

    /// <summary>
    /// Gets the slot files that exist right now, ordered so a test can name a slot number.
    /// </summary>
    public string[] SlotFiles()
    {
        if (!Directory.Exists(Location))
        {
            return Array.Empty<string>();
        }

        var files = Directory.GetFiles(Location, "*" + WrapperConcurrencyGuard.SlotFileExtension);
        Array.Sort(files, StringComparer.Ordinal);

        return files;
    }

    /// <summary>
    /// Writes a slot file that no wrapper of this test run opened, to stand in for the
    /// leavings of a wrapper that died on other storage.
    /// </summary>
    /// <param name="slot">The slot number to impersonate.</param>
    /// <param name="recordedPid">The process id to record as the owner.</param>
    /// <param name="claimedAgo">How long ago the file claims to have been claimed.</param>
    /// <returns>The path written.</returns>
    /// <remarks>
    /// The mark this writes carries no boot identifier, because that is what a slot file looked like
    /// before the field existed: the two lines the guard has always read, which a test that is about
    /// the process id alone still writes this way.
    /// </remarks>
    public string WriteForeignSlotFile(int slot, string recordedPid, TimeSpan claimedAgo)
        => WriteForeignSlotFile(slot, recordedPid, claimedAgo, bootId: null);

    /// <summary>
    /// Writes a slot file that no wrapper of this test run opened, naming the boot or container
    /// instance that owns it as well as the process.
    /// </summary>
    /// <param name="slot">The slot number to impersonate.</param>
    /// <param name="recordedPid">The process id to record as the owner.</param>
    /// <param name="claimedAgo">How long ago the file claims to have been claimed.</param>
    /// <param name="bootId">
    /// The boot identifier to state, or <c>null</c> to state none and write the older two-line mark.
    /// </param>
    /// <returns>The path written.</returns>
    public string WriteForeignSlotFile(int slot, string recordedPid, TimeSpan claimedAgo, string? bootId)
    {
        Directory.CreateDirectory(Location);

        var path = SlotPath(slot);

        File.WriteAllText(path, FormatMark(recordedPid, bootId));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - claimedAgo);

        return path;
    }

    /// <summary>
    /// Writes a slot file whose mark is a test's own text, for the marks no writer of this build
    /// would produce: one with no owner in it, one with an owner that is not a number, one with a key
    /// this build has never seen.
    /// </summary>
    /// <param name="slot">The slot number to impersonate.</param>
    /// <param name="mark">The exact text to write.</param>
    /// <param name="claimedAgo">How old the file is to look.</param>
    /// <returns>The path written.</returns>
    public string WriteSlotFileWithMark(int slot, string mark, TimeSpan claimedAgo)
    {
        Directory.CreateDirectory(Location);

        var path = SlotPath(slot);

        File.WriteAllText(path, mark);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - claimedAgo);

        return path;
    }

    /// <summary>
    /// The path one numbered slot lives at in this directory.
    /// </summary>
    public string SlotPath(int slot)
        => TranscodeSlotStore.SlotPath(Location, slot);

    /// <summary>
    /// The mark of a slot this test's own process is holding: the pair of keys a wrapper writes for
    /// itself, in the order it writes them, with this build's boot identifier when the host states one.
    /// </summary>
    /// <param name="recordedPid">The process id to record, which a test may need to be somebody
    /// else's.</param>
    /// <param name="bootId">The boot identifier to state, or <c>null</c> to state none.</param>
    /// <returns>The mark text.</returns>
    public static string FormatMark(string recordedPid, string? bootId)
    {
        var claimed = string.Format(
            CultureInfo.InvariantCulture,
            "pid={0}\nclaimed={1:O}\n",
            recordedPid,
            DateTimeOffset.UtcNow);

        return string.IsNullOrEmpty(bootId)
            ? claimed
            : claimed + TranscodeSlotStore.BootKey + "=" + bootId + "\n";
    }

    /// <summary>
    /// A boot identifier that is not this machine's, for a mark that has to look like the leavings of
    /// a container that no longer exists.
    /// </summary>
    /// <remarks>
    /// Made out of the machine's own identifier rather than invented, so that the only thing the mark
    /// differs in is the one thing the test is about. A host that states no identifier of its own gets
    /// a value no mark could match, which is the same statement made the other way round.
    /// </remarks>
    public static string AnotherBootId()
        => (TranscodeSlotStore.CurrentBootId ?? "anaglyfin-test-host") + "-not";

    /// <summary>Removes the directory and everything in it.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Location))
            {
                Directory.Delete(Location, recursive: true);
            }
        }
        catch (IOException)
        {
            // A slot file a crashed test still holds open is not a failure to report.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
