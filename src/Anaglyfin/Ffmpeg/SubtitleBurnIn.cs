using System;
using System.IO;
using System.Linq;

namespace Anaglyfin.Ffmpeg;

/// <summary>
/// A request to burn one embedded subtitle track into a profile output.
/// </summary>
/// <remarks>
/// <para>
/// The FFmpeg <c>subtitles</c> filter re-opens the film by filename to render its
/// text streams, so a burn-in carries the real source path rather than the Anaglyfin
/// marker. That path is expected to be plugin-provided - resolved from a media source
/// the server owns - never a string a client or a settings file supplied. This type
/// does not enforce where the string came from; it enforces that the value can only
/// ever denote a file and a track: a rooted path without control characters and a
/// non-negative stream ordinal.
/// </para>
/// <para>
/// The ordinal is the position of the track <em>among subtitle streams</em> (the
/// <c>si</c> option of the filter), not a global stream index, so it stays valid even
/// after the rewrite removes subtitle stream maps from the command.
/// </para>
/// </remarks>
public sealed record SubtitleBurnIn
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleBurnIn"/> record.
    /// </summary>
    /// <param name="sourcePath">
    /// Rooted path of the media file whose subtitle track is burned in. The file must
    /// be the same input the transcode reads, because the filter decodes the subtitle
    /// out of it a second time.
    /// </param>
    /// <param name="subtitleStreamOrdinal">
    /// Zero-based position of the subtitle track among the subtitle streams of the
    /// file, the value the filter receives as <c>si</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourcePath"/> is blank, contains a control character, or is not
    /// a rooted path. A relative path would resolve against the working directory of
    /// the FFmpeg process, which the plugin does not control.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="subtitleStreamOrdinal"/> is negative.
    /// </exception>
    public SubtitleBurnIn(string sourcePath, int subtitleStreamOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentOutOfRangeException.ThrowIfNegative(subtitleStreamOrdinal);

        // Control characters (newlines included) cannot survive into a filtergraph
        // argument safely and have no business in a real media path, so a value
        // carrying one is rejected instead of escaped.
        if (sourcePath.Any(char.IsControl))
        {
            throw new ArgumentException("A subtitle burn-in source path must not contain control characters.", nameof(sourcePath));
        }

        if (!Path.IsPathRooted(sourcePath))
        {
            throw new ArgumentException("A subtitle burn-in source path must be rooted, because the transcode working directory is not plugin-controlled.", nameof(sourcePath));
        }

        SourcePath = sourcePath;
        SubtitleStreamOrdinal = subtitleStreamOrdinal;
    }

    /// <summary>
    /// Gets the rooted path of the media file the subtitles are read from.
    /// </summary>
    public string SourcePath { get; init; }

    /// <summary>
    /// Gets the ordinal of the subtitle track among subtitle streams.
    /// </summary>
    public int SubtitleStreamOrdinal { get; init; }

    /// <inheritdoc />
    public override string ToString()
        => $"subtitles of '{SourcePath}' #{SubtitleStreamOrdinal}";
}
