using System.Collections.Generic;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Turns a received FFmpeg argument vector into one the real FFmpeg-mvc binary can run.
/// </summary>
/// <remarks>
/// <para>
/// This is the decision half of the FFmpeg wrapper: given the argv Jellyfin built for a
/// playback request, it answers with the argv to execute - either the received one
/// untouched, or the one with the Anaglyfin marker resolved and the profile's view,
/// filter and subtitle arguments applied. Starting the process, enforcing concurrency and
/// reporting failures belong to the launcher, not here, which keeps the whole rewrite
/// testable without executing anything.
/// </remarks>
/// <remarks>
/// <para>
/// The dispatch rule is inherited unchanged from the marker contract
/// (<see cref="Anaglyfin.Markers.MarkerParseStatus"/>): a token that is not a marker is
/// ordinary FFmpeg traffic and passes through, a token that presents itself as a marker
/// and fails to parse is never ordinary traffic and refuses the job, and only a valid
/// marker rewrites the command. Never running a rejected marker is what makes it safe for
/// this binary to be installed as the server's FFmpeg.
/// </para>
/// <para>
/// Implementations must be pure over the argument vector: no process, no filesystem and
/// no network access, and no mutation of the caller's list.
/// </para>
/// </remarks>
public interface IWrapperArgumentRewriter
{
    /// <summary>
    /// Rewrites one received FFmpeg argument vector.
    /// </summary>
    /// <param name="arguments">
    /// The argument vector as received, without the executable token. Leading tokens the
    /// rewriter does not recognise are carried through untouched, so a caller may pass the
    /// argv it received verbatim.
    /// </param>
    /// <returns>
    /// The decision. <see cref="WrapperRewriteResult.Arguments"/> is the vector to execute
    /// when <see cref="WrapperRewriteResult.IsSuccess"/> holds and is empty otherwise.
    /// </returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    WrapperRewriteResult Rewrite(IReadOnlyList<string> arguments);
}
