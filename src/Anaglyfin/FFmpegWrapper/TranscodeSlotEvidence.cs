namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// What the slot file of one Anaglyfin transcode can prove about the job that wrote it.
/// </summary>
/// <remarks>
/// <para>
/// The two processes that read slot files - the wrapper deciding whether a slot is free, and the
/// plugin's startup pass deciding which leftovers to remove - have different rights over a file
/// they cannot explain, which is why the answer is this three-way judgement rather than a boolean.
/// Both of them get their answer from <see cref="TranscodeSlotStore.ReadEvidence"/>, so the two
/// can never disagree about what a file means while disagreeing about what to do about it.
/// </para>
/// <para>
/// Nothing here is a probability. Each member states which of the two questions a slot file can
/// actually answer - is a process holding this file open, and is the owner its mark records gone -
/// and <see cref="Unproven"/> is the honest answer to the second one, not a hedge on the first.
/// </para>
/// </remarks>
public enum TranscodeSlotEvidence
{
    /// <summary>
    /// The slot is in use: a process holds the file open, or the owner its mark records is a
    /// process this machine can still see.
    /// </summary>
    Held,

    /// <summary>
    /// The slot is the leavings of a wrapper that is gone: no process holds the file open, and the
    /// owner its mark records cannot be running - either its process id is not a process any more,
    /// or the mark belongs to a different boot or container, whose processes cannot be ones this
    /// instance is waiting on.
    /// </summary>
    DeadOwner,

    /// <summary>
    /// No process holds the file open, but its mark proves nothing: it cannot be read, or it records
    /// no usable owner. Only age can decide a file like this, and only a caller holding an abandon
    /// window is entitled to apply one.
    /// </summary>
    Unproven
}
