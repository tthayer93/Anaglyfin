using System;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Who a slot file says owns the transcode it was claimed for.
/// </summary>
/// <param name="ProcessId">
/// The process id the slot recorded, which is a process of the machine the mark's
/// <see cref="BootId"/> names and of no other. A process id is only meaningful next to a boot:
/// the same number is handed out again by the next boot and by the next container, so reading one
/// without asking which instance handed it out is how a leftover is mistaken for a running job -
/// or a running job mistaken for a leftover.
/// </param>
/// <param name="BootId">
/// The boot identifier the mark carried, or <c>null</c> when it carried none. A mark written by a
/// build that predates the field states the two lines it always stated, and a missing boot
/// identifier is read the way those two lines were always read: as this instance's own.
/// </param>
/// <remarks>
/// Written and read by <see cref="TranscodeSlotStore"/>, which is the only place the file format of
/// a slot is spelled.
/// </remarks>
public readonly record struct TranscodeSlotOwner(int ProcessId, string? BootId)
{
    /// <summary>
    /// Gets whether the mark names the same instance of this machine as the one reading it.
    /// </summary>
    /// <remarks>
    /// The answer this instance gives itself, which is <see cref="IsFromBoot"/> with this process's
    /// own boot identifier.
    /// </remarks>
    public bool IsFromThisBoot => IsFromBoot(TranscodeSlotStore.CurrentBootId);

    /// <summary>
    /// Says whether this mark's boot identifier is one particular instance of a machine.
    /// </summary>
    /// <param name="currentBootId">
    /// The boot identifier of the instance asking, or <c>null</c> when it does not know its own.
    /// </param>
    /// <returns>
    /// <c>true</c> when the mark states no boot at all, when the reader states none, or when the two
    /// match.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The first two answers are the compatibility the field was added without breaking: a mark
    /// written before the field existed states two lines and means them the way they always meant -
    /// this instance's own - and a machine that cannot state its identifier cannot exclude a mark
    /// either, so it falls back to reading the process id on its face.
    /// </para>
    /// <para>
    /// Compared case-insensitively because the mark is a text file an administrator may have opened
    /// and re-saved, and the only thing a mismatch claims is that the two lines were written by
    /// different instances.
    /// </para>
    /// </remarks>
    public bool IsFromBoot(string? currentBootId)
        => BootId is null
           || currentBootId is null
           || string.Equals(BootId.Trim(), currentBootId.Trim(), StringComparison.OrdinalIgnoreCase);
}
