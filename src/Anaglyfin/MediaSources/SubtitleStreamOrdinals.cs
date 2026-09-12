using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Anaglyfin.MediaSources;

/// <summary>
/// Maps a client-facing subtitle stream index to the marker ordinal a burn-in needs.
/// </summary>
/// <remarks>
/// <para>
/// Two different numbers describe "which subtitle track" and neither is the other:
/// clients select a subtitle by <see cref="MediaStream.Index"/>, the stream's global
/// position among all streams of the file, while the marker's <c>subtitle</c> field
/// and the <c>si</c> option of the FFmpeg <c>subtitles</c> filter (see
/// <c>Anaglyfin.Ffmpeg.SubtitleBurnIn</c>) count <em>subtitle streams only</em>, in
/// ascending stream order. Handing a burn-in the raw client index would silently
/// burn the wrong track - or fail - whenever audio or video streams are interleaved
/// between subtitle tracks, which is the normal layout, not an edge case.
/// </para>
/// <para>
/// The provider does not attach an ordinal yet (the MVP ships subtitle-free markers);
/// this mapping is the contract in service form so that per-playback subtitle
/// selection cannot redefine it when it lands.
/// </para>
/// </remarks>
public static class SubtitleStreamOrdinals
{
    /// <summary>
    /// Converts a global subtitle stream index into its ordinal among subtitle streams.
    /// </summary>
    /// <param name="mediaStreams">The media streams of the source file.</param>
    /// <param name="subtitleStreamIndex">
    /// The global <see cref="MediaStream.Index"/> of the selected subtitle stream, as
    /// a client would send it. Negative values (<c>-1</c>, "unknown") never match.
    /// </param>
    /// <param name="burnInOrdinal">
    /// On success, the zero-based position of the selected stream among the subtitle
    /// streams of the file: <c>0</c> for the first subtitle track.
    /// </param>
    /// <returns>
    /// <c>true</c> when <paramref name="mediaStreams"/> contains a subtitle stream with
    /// that index; otherwise <c>false</c> and <paramref name="burnInOrdinal"/> is zero.
    /// </returns>
    /// <remarks>
    /// Streams whose own index is negative are not orderable and do not contribute to
    /// the count. Matching ignores casing concerns - there are none - and never throws:
    /// a null or short stream list is simply "no such track".
    /// </remarks>
    public static bool TryGetBurnInOrdinal(
        IReadOnlyList<MediaStream>? mediaStreams,
        int subtitleStreamIndex,
        out int burnInOrdinal)
    {
        burnInOrdinal = 0;

        if (mediaStreams is null || subtitleStreamIndex < 0)
        {
            return false;
        }

        var found = false;
        var subtitlesBefore = 0;

        foreach (var stream in mediaStreams)
        {
            if (stream is null || stream.Type != MediaStreamType.Subtitle || stream.Index < 0)
            {
                continue;
            }

            if (stream.Index == subtitleStreamIndex)
            {
                found = true;
            }
            else if (stream.Index < subtitleStreamIndex)
            {
                subtitlesBefore++;
            }
        }

        if (!found)
        {
            return false;
        }

        burnInOrdinal = subtitlesBefore;
        return true;
    }
}
