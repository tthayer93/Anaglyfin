namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// What the argument rewriter decided to do with a received FFmpeg command line.
/// </summary>
/// <remarks>
/// <para>
/// The status is the wrapper's dispatch decision and the launcher (T5b) acts on it
/// verbatim: <see cref="PassedThrough"/> and <see cref="Rewritten"/> are the only
/// outcomes that may be executed, and everything else refuses the job. This mirrors the
/// marker contract's own split - ordinary FFmpeg input passes through untouched, a
/// marker that wanted to be a marker and failed is never ordinary input - so a forged or
/// broken marker can never end up in front of the real FFmpeg binary as a file to open.
/// </para>
/// <para>
/// The failure values are deliberately distinguished from one another rather than
/// collapsed into a single "failed": the reason a playback request was refused is what an
/// administrator has to see in the log to tell a mis-transported marker apart from a
/// command shape Anaglyfin cannot rewrite.
/// </para>
/// </remarks>
public enum WrapperRewriteStatus
{
    /// <summary>
    /// No input token of the command carried an Anaglyfin marker. The argument vector is
    /// returned unchanged so stock playback - including the server's own hardware decode
    /// choice - runs exactly as it would without the wrapper.
    /// </summary>
    PassedThrough,

    /// <summary>
    /// The marker input was replaced by the real source path and the profile's view,
    /// filter and subtitle arguments were applied.
    /// </summary>
    Rewritten,

    /// <summary>
    /// An input token carried text that presents itself as an Anaglyfin marker but is not
    /// a valid one (malformed, unknown profile id, missing or unusable source, bad
    /// subtitle ordinal). The job is refused; the token is never handed to FFmpeg.
    /// </summary>
    RejectedMarker,

    /// <summary>
    /// A valid marker named a profile this build cannot produce a command for: the
    /// catalog has no such profile, or the command builder refused it. Refused rather
    /// than degraded, because running the command unrewritten would play the marker URL
    /// as a media file.
    /// </summary>
    UnknownProfile,

    /// <summary>
    /// The profile has to own the command's video pipeline, but the command already
    /// carries a filtergraph this rewriter did not write. Two graphs cannot both feed one
    /// output stream, and merging foreign filter text is not something this wrapper does,
    /// so the job is refused.
    /// </summary>
    IncompatibleFilterGraph,

    /// <summary>
    /// The marker is present in a command shape whose profile arguments this rewriter
    /// cannot place safely: today, a marker that is not the first input (while every profile
    /// argument addresses input <c>0</c>), and more than one input carrying a valid marker
    /// (while only one of them can be resolved, leaving the rest for FFmpeg to open).
    /// </summary>
    UnsupportedCommandShape
}
