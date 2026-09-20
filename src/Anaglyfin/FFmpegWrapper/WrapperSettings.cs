using Anaglyfin.Configuration;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// What the settings document handed to one wrapper invocation.
/// </summary>
/// <remarks>
/// <para>
/// This is the read side of <see cref="WrapperSettingsFile"/>, and it is deliberately not a copy
/// of the plugin's settings: it holds only the two things a wrapper can act on, in the shape the
/// reader managed to recover from the file. The writer states both; a reader that could not read
/// one of them says so with <c>null</c> rather than inventing a value, which is what lets the two
/// sections fail independently of each other.
/// </para>
/// <para>
/// Nothing here is a decision. The wrapper combines <see cref="MaxConcurrentTranscodes"/> with
/// what its own environment says (<see cref="FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable"/>),
/// and the depth is used as it stands because the deployment has no channel for it.
/// </para>
/// </remarks>
/// <param name="SubtitleDepth">
/// The depth request the document states, or <see cref="Configuration.SubtitleDepthSettings.Disabled"/>
/// when it states none this build can read.
/// </param>
/// <param name="MaxConcurrentTranscodes">
/// The limit the document states, or <c>null</c> when it states none. <c>null</c> is not "no
/// limit" and not "one": it is "this document has no opinion", which is what a version 1 document
/// and a malformed <c>transcoding</c> section both mean.
/// </param>
public sealed record WrapperSettings(SubtitleDepthSettings SubtitleDepth, int? MaxConcurrentTranscodes)
{
    /// <summary>
    /// Gets the answer for a wrapper that was pointed at nothing readable: no depth asked for, and
    /// no limit stated, which leaves both settings to their own configured defaults.
    /// </summary>
    public static WrapperSettings Unstated { get; } = new(SubtitleDepthSettings.Disabled, null);
}
