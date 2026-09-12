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
    public string WriteForeignSlotFile(int slot, string recordedPid, TimeSpan claimedAgo)
    {
        Directory.CreateDirectory(Location);

        var path = Path.Combine(
            Location,
            WrapperConcurrencyGuard.SlotFileNamePrefix
            + slot.ToString(CultureInfo.InvariantCulture)
            + WrapperConcurrencyGuard.SlotFileExtension);

        File.WriteAllText(path, $"pid={recordedPid}\nclaimed=not-checked\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - claimedAgo);

        return path;
    }

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
