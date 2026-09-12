using System;
using System.Collections.Generic;
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
/// <see cref="VideoCodec"/>. The server then finds the video codec unsupported, which is
/// both honest about a dual-view MVC file and enough to make video copy impossible for
/// every client and every permission, and it builds a real encode command with a real
/// encoder stack behind it. Audio is untouched: audio copy is the normal Jellyfin
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
/// <c>video=&lt;index&gt;</c> parameter, so the wrapper's map handling and the server's map
/// agree on the same stream. A file carrying a second video stream keeps its real codec on
/// screen: were the server ever to pick that one and stream-copy it, the wrapper would
/// refuse the job loudly instead of silently converting a stream nobody asked for.
/// </para>
/// </remarks>
public static class ForceTranscodeVideoStreams
{
    /// <summary>
    /// The codec a version reports for its video stream: multiview MVC, which is what the
    /// file actually holds and which no client's codec list contains, so no client can be
    /// served a copy of it.
    /// </summary>
    public const string VideoCodec = "mvc";

    private static readonly IReadOnlyList<MediaStream> NoStreams = Array.Empty<MediaStream>();

    /// <summary>
    /// Builds the stream list one version reports, and names the video stream it reports.
    /// </summary>
    /// <param name="reported">
    /// The streams the item's own source reports, or null when the version has no stream
    /// information at all. Never modified.
    /// </param>
    /// <param name="videoStreamIndex">
    /// The index of the re-labelled video stream inside the returned list, or
    /// <c>-1</c> when there is no video stream to name.
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

        videoStreamIndex = firstVideoIndex;

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
        // bit depth, frame rate, language, Dolby Vision metadata - stays as the file's own
        // probe reported it.
        reported.Codec = VideoCodec;

        return reported;
    }

    /// <summary>
    /// Copies every field of a reported stream so the version can change one of them.
    /// </summary>
    /// <remarks>
    /// Field-by-field rather than by reference: the copy must be as faithful as the
    /// original, because this is what a client's version entry, the details page and the
    /// server's own transcode conditions read - a video stream that lost its resolution,
    /// bit depth or frame rate on the way would change which encode the server builds
    /// while claiming to describe the same picture. The codec is copied like everything
    /// else and then replaced by the caller, so that a stream missing a field never
    /// reaches a client.
    /// </remarks>
    private static MediaStream Clone(MediaStream source)
        => new()
        {
            Codec = source.Codec,
            CodecTag = source.CodecTag,
            Language = source.Language,
            ColorRange = source.ColorRange,
            ColorSpace = source.ColorSpace,
            ColorTransfer = source.ColorTransfer,
            ColorPrimaries = source.ColorPrimaries,
            DvVersionMajor = source.DvVersionMajor,
            DvVersionMinor = source.DvVersionMinor,
            DvProfile = source.DvProfile,
            DvLevel = source.DvLevel,
            RpuPresentFlag = source.RpuPresentFlag,
            ElPresentFlag = source.ElPresentFlag,
            BlPresentFlag = source.BlPresentFlag,
            DvBlSignalCompatibilityId = source.DvBlSignalCompatibilityId,
            Rotation = source.Rotation,
            Comment = source.Comment,
            TimeBase = source.TimeBase,
            CodecTimeBase = source.CodecTimeBase,
            Title = source.Title,
            Hdr10PlusPresentFlag = source.Hdr10PlusPresentFlag,
            LocalizedUndefined = source.LocalizedUndefined,
            LocalizedDefault = source.LocalizedDefault,
            LocalizedForced = source.LocalizedForced,
            LocalizedExternal = source.LocalizedExternal,
            LocalizedHearingImpaired = source.LocalizedHearingImpaired,
            LocalizedLanguage = source.LocalizedLanguage,
            LocalizedOriginal = source.LocalizedOriginal,
            NalLengthSize = source.NalLengthSize,
            IsInterlaced = source.IsInterlaced,
            IsAVC = source.IsAVC,
            ChannelLayout = source.ChannelLayout,
            BitRate = source.BitRate,
            BitDepth = source.BitDepth,
            RefFrames = source.RefFrames,
            PacketLength = source.PacketLength,
            Channels = source.Channels,
            SampleRate = source.SampleRate,
            IsDefault = source.IsDefault,
            IsForced = source.IsForced,
            IsHearingImpaired = source.IsHearingImpaired,
            IsOriginal = source.IsOriginal,
            Height = source.Height,
            Width = source.Width,
            AverageFrameRate = source.AverageFrameRate,
            RealFrameRate = source.RealFrameRate,
            Profile = source.Profile,
            Type = source.Type,
            AspectRatio = source.AspectRatio,
            Index = source.Index,
            Score = source.Score,
            IsExternal = source.IsExternal,
            DeliveryMethod = source.DeliveryMethod,
            DeliveryUrl = source.DeliveryUrl,
            IsExternalUrl = source.IsExternalUrl,
            SupportsExternalStream = source.SupportsExternalStream,
            Path = source.Path,
            PixelFormat = source.PixelFormat,
            Level = source.Level,
            IsAnamorphic = source.IsAnamorphic
        };
}
