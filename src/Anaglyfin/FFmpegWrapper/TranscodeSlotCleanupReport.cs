using System;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// What one startup sweep of the transcode slot directory did.
/// </summary>
/// <param name="Seen">How many slot files it looked at.</param>
/// <param name="Removed">How many of them it removed.</param>
/// <param name="LockDirectory">
/// The directory it swept, which is the directory the deployment's variable pointed at and the one the
/// line about the pass has to name: when somebody set that variable wrongly, where the slots actually
/// live is the question an administrator is left with.
/// </param>
/// <param name="Failure">
/// What stopped the sweep, or <c>null</c> when nothing did.
/// </param>
/// <remarks>
/// A result and not an exception: the caller is a server starting up, and what it needs from a
/// cleanup that could not finish is how much of it happened. The failure travels as a value so that
/// reporting it and swallowing it are one decision in one place, made by
/// <see cref="TranscodeSlotCleanupService.Clean"/>.
/// </remarks>
public readonly record struct TranscodeSlotCleanupReport(
    int Seen,
    int Removed,
    string LockDirectory,
    Exception? Failure);
