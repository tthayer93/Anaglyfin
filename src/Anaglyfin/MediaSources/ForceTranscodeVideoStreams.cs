using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Entities;

namespace Anaglyfin.MediaSources;

/// <summary>
/// Reports a version's media streams in the one form its playback can survive: a video
/// stream no client can ask the server to stream-copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the codec is the lever.</b> An Anaglyfin version is a conversion, so its whole
/// result depends on the server handing the command to FFmpeg as an encode. Jellyfin does
/// that decision in its own transcode pipeline, and there is no flag that decides it: for
/// a dynamic source the server overwrites whatever the provider reported for
/// <c>SupportsTranscoding</c> and <c>SupportsDirectStream</c> with the user's permissions,
/// and the transcode path that then chooses video copy never reads either of them. What it
/// reads is data - whether the codec this source reports for its video stream is one the
/// client's transcode profile can copy, with the request allowing video stream copy, which
/// a browser does by default. Report <c>h264</c> and the server is right to build
/// <c>-codec:v:0 copy</c>: a command that copies the picture is not an inert Anaglyfin
/// pipeline, it is the original file with no conversion in it at all.
/// </para>
/// <para>
/// So the version reports a codec no client's direct-play or transcode profile names:
/// <see cref="VideoCodec"/>. The lever is that nothing names it, and nothing else: the
/// server finds the video codec unsupported by the client's profile, which is enough to make
/// video copy impossible for every client and every permission, and it builds a real encode
/// command with a real encoder stack behind it. <c>mvc</c> is not the codec the file's video
/// track is encoded with - ffprobe reports <c>hevc</c> for an MVC track - so nothing here
/// claims to be reporting the wire format; it is a name the copy decision cannot match, and
/// that is the whole of its job. Audio is untouched: audio copy is the normal Jellyfin
/// behaviour and this class does not look at audio streams at all.
/// </para>
/// <para>
/// <b>Why a clone.</b> The streams a media source reports are the item's persisted stream
/// objects, handed out by reference by the server's own media-source API, and the item's
/// original version reports the very same objects. Changing a codec in place would rewrite
/// what the original version tells its clients - the one file on the server that must keep
/// playing exactly as it did - so the video stream is copied before it is re-labelled and
/// the list the version reports is a new list around that copy.
/// </para>
/// <para>
/// <b>Why only the first video stream.</b> That is the stream a transcode with no explicit
/// video-stream choice maps, and it is the stream the marker names with its
/// <c>video=&lt;index&gt;</c> parameter - by its <see cref="MediaStream.Index"/>, the number
/// the server spends on <c>-map 0:&lt;index&gt;</c> - so the wrapper's map handling and the
/// server's map agree on the same stream. A file carrying a second video stream keeps its
/// real codec on screen: were the server ever to pick that one and stream-copy it, the
/// wrapper would refuse the job loudly instead of silently converting a stream nobody asked
/// for.
/// </para>
/// </remarks>
public static class ForceTranscodeVideoStreams
{
    /// <summary>
    /// The codec a version reports for its video stream. Not the codec the file's video track
    /// carries - an MVC track is reported by ffprobe as <c>hevc</c> - but a codec name no
    /// client's direct-play list and no client's transcode profile contains, which is the
    /// only property the server's video-copy decision reads and therefore the only one that
    /// makes a version impossible to stream-copy.
    /// </summary>
    public const string VideoCodec = "mvc";

    private static readonly IReadOnlyList<MediaStream> NoStreams = Array.Empty<MediaStream>();

    /// <summary>
    /// The options the stream clone is round-tripped through.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonNumberHandling.AllowNamedFloatingPointLiterals"/> is not decoration:
    /// the default writer refuses <c>NaN</c> and <c>Infinity</c>, and a frame rate or
    /// rotation carried by a hand-built or oddly probed source can hold one. Throwing here
    /// would cost the item every Anaglyfin version, which is the wrong price for a number
    /// the clone is only passing through.
    /// </remarks>
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>
    /// Builds the stream list one version reports, and names the video stream it reports.
    /// </summary>
    /// <param name="reported">
    /// The streams the item's own source reports, or null when the version has no stream
    /// information at all. Never modified.
    /// </param>
    /// <param name="videoStreamIndex">
    /// The <see cref="MediaStream.Index"/> of the re-labelled video stream - the number a
    /// <c>-map 0:&lt;index&gt;</c> of this source's video carries - or <c>-1</c> when there is
    /// no video stream, or the reported one names no index for it.
    /// </param>
    /// <returns>
    /// <paramref name="reported"/> itself when it holds no video stream to re-label,
    /// otherwise a new list in which the first video stream is the
    /// <see cref="VideoCodec"/> clone of it and every other stream is the same object the
    /// item reports.
    /// </returns>
    /// <remarks>
    /// Clone-and-relabel rather than mutate is what lets the original library version and
    /// every Anaglyfin version be served from one item without either seeing the other's
    /// report.
    /// </remarks>
    public static IReadOnlyList<MediaStream> WithForcedVideoCodec(
        IReadOnlyList<MediaStream>? reported,
        out int videoStreamIndex)
    {
        videoStreamIndex = -1;

        if (reported is null || reported.Count == 0)
        {
            return NoStreams;
        }

        var firstVideoIndex = -1;
        for (var index = 0; index < reported.Count; index++)
        {
            if (reported[index] is { Type: MediaStreamType.Video })
            {
                firstVideoIndex = index;
                break;
            }
        }

        if (firstVideoIndex < 0)
        {
            // Nothing to re-label: an item whose report carries no video stream keeps its
            // streams by reference exactly as the original version reports them.
            return reported;
        }

        var firstVideo = reported[firstVideoIndex];

        // What the marker has to name is the stream's own index and not the position this
        // list happens to hold it at: they are the same number for a report the probe wrote
        // in file order and different for anything else, and it is the first one that a
        // -map 0:<index> spends. An unprobed or hand-built stream reporting -1 names nothing,
        // and this does not guess a position for it - the codec re-label below still happens,
        // because that is what keeps the version encoding, and only the map identification is
        // left out.
        videoStreamIndex = firstVideo.Index >= 0 ? firstVideo.Index : -1;

        var forcing = new List<MediaStream>(reported.Count);
        for (var index = 0; index < reported.Count; index++)
        {
            forcing.Add(index == firstVideoIndex ? CloneForForceTranscode(reported[index]) : reported[index]);
        }

        return forcing;
    }

    /// <summary>
    /// Reports one video stream as a version of it that cannot be stream-copied.
    /// </summary>
    /// <param name="source">The stream the item's own source reports. Not modified.</param>
    /// <returns>A copy of it whose only difference from the original is its codec.</returns>
    private static MediaStream CloneForForceTranscode(MediaStream source)
    {
        var reported = Clone(source);

        // The one field a version reports differently, and the one the server's video-copy
        // decision reads. Everything a player or the transcode conditions look at - size,
        // bit depth, frame rate, language, Dolby Vision metadata, HDR range - stays as the
        // file's own probe reported it.
        reported.Codec = VideoCodec;

        return reported;
    }

    /// <summary>
    /// Copies every field of a reported stream so the version can change one of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A serialisation round-trip rather than a hand-written field list, because a field
    /// list fails quietly in one direction only: forget a field and the clone still builds,
    /// still plays, and simply reports one thing less than the file does. Dropping
    /// <c>VideoRange</c> or a colour transfer that way would change which encode the server
    /// builds - and which HDR route the client takes - while the version claims to describe
    /// the same picture. Writing the model's own JSON and reading it back into a fresh
    /// instance copies whatever the model has today, including fields added by a server
    /// upgrade this code was not written against.
    /// </para>
    /// <para>
    /// The derived read-only members (<c>VideoRange</c>, <c>VideoRangeType</c>,
    /// <c>VideoDoViTitle</c>) are computed from the fields this copies, so they answer on the
    /// clone exactly as they do on the original; the codec is copied like everything else and
    /// then replaced by the caller.
    /// </para>
    /// </remarks>
    private static MediaStream Clone(MediaStream source)
        => JsonSerializer.Deserialize<MediaStream>(JsonSerializer.Serialize(source, CloneOptions), CloneOptions)!;
}
