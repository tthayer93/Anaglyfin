using System;
using System.Collections.Generic;
using System.IO;
using Anaglyfin.Markers;
using Anaglyfin.Profiles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Anaglyfin.MediaSources;

/// <summary>
/// What one profile version of one eligible file <em>is</em>, expressed as the media source the
/// server hands to a client.
/// </summary>
/// <remarks>
/// <para>
/// This is the one definition of an Anaglyfin version. The media source provider returns it as a
/// dynamic source; the version-item manager materialises it as a linked alternate-version item,
/// field for field, so that "the version the user picked" is the same object - same id, same
/// marker, same stream report - whichever of the two roads it travelled. Two definitions of one
/// version is how a version ends up playing differently depending on whether an item exists for
/// it yet.
/// </para>
/// <para>
/// Everything a version keeps is the file's: duration, container, size, bitrate, and the audio and
/// subtitle tracks at their original indices, so resume, seeking and the details page behave
/// across versions exactly as within one version. Everything it changes is the conversion's: the
/// id (derived, never borrowed - see <see cref="AnaglyfinMediaSourceProvider.BuildMediaSourceIdFromSource"/>),
/// the path (the profile marker, the only field the FFmpeg wrapper reads), the video codec (the
/// <c>mvc</c> lever that makes the version impossible to stream-copy, through
/// <see cref="ForceTranscodeVideoStreams"/>) and the encoded frame size (through
/// <see cref="ProfileVideoGeometry"/>).
/// </para>
/// <para>
/// What a version does <em>not</em> change, and does on purpose, is the stereo declaration:
/// <see cref="MediaSourceInfo.Video3DFormat"/> is written as <c>null</c>, because the server reads
/// that field as an instruction to convert a side-by-side source to 2D itself, which is the
/// opposite of what a version that has already converted the picture needs.
/// </para>
/// <para>
/// What it does declare, and declares for the server's benefit rather than a client's, is the video
/// type: <see cref="VersionVideoType"/> says that the media behind the marker is one file on the
/// server's disk, which is what the server's hardware encoder gate asks about. See that field for
/// the whole argument, including the transport and the hardware <em>decode</em> this declaration
/// deliberately does not reach.
/// </para>
/// </remarks>
public static class ProfileVersionSource
{
    /// <summary>
    /// The separator between a version's own file label and the profile it converts it with,
    /// used only when an item has more than one file to choose between.
    /// </summary>
    private const string SourceNameSeparator = " / ";

    /// <summary>
    /// The video type every Anaglyfin version declares itself to be: <see cref="VideoType.VideoFile"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is one decision stated once, written on the dynamic source (<see cref="Build"/>) and on
    /// the item a version is materialised as, and compared against that same value by the
    /// reconciliation that repairs an item which has drifted off it.
    /// </para>
    /// <para>
    /// <b>What the field is read for.</b> The server gates its hardware video encoders on it:
    /// <c>EncodingHelper.GetH26xOrAv1Encoder</c> hands out <c>h264_qsv</c>, <c>hevc_nvenc</c>,
    /// <c>h264_vaapi</c> and their families only <c>if (state.VideoType == VideoType.VideoFile)</c>,
    /// and a job that answers the gate with a disc is encoded with <c>libx264</c> on a machine with a
    /// working Quick Sync encoder - silently, with nothing in the log to say a choice was made.
    /// </para>
    /// <para>
    /// <b>Why state it when the default already says the same.</b> On the server this plugin builds
    /// against, declaring the type changes nothing about which encoder a version gets: the streaming
    /// path never writes the job's <c>VideoType</c> at all, and the enum has no "unspecified" member,
    /// so an unwritten field already answers <c>VideoFile</c> - because that is where the type lists
    /// it, which is not a decision anyone made. The answer is stated anyway: an enum member's position
    /// is what an unwritten field depends on, and one member inserted in front of it would move every
    /// version out of the hardware encoders' reach with no log line and no error - the shape of
    /// failure this repository has agreed to stop accepting, a product behaviour resting on a value
    /// somebody else's code produces by default. And the claim is read
    /// directly by code that looks at the answer rather than the default: the version list a client
    /// renders, which before the declaration was written showed Anaglyfin versions as sources with no
    /// video type on them at all; this plugin's own scanner, which tolerates a source naming no type
    /// and refuses one naming a disc (<see cref="MvcEligibleSourceScanner.IsTranscodableSource"/>);
    /// the server's ordering of an item's sources, which sorts a declared <c>VideoFile</c> ahead of
    /// everything else; and the item's own listing, which prints what the item claims. The plugin
    /// never names an encoder; this names only the kind of media a version is.
    /// </para>
    /// <para>
    /// <b>Why <c>VideoFile</c> is the honest answer.</b> A version converts one file that is on the
    /// server's disk: it is not a disc image, not a folder rip, and not a live stream. The one thing
    /// the declaration does <em>not</em> buy is the hardware <c>decode</c> of a version: the server
    /// offers that per reported codec, and the codec a version reports is <c>mvc</c> (see
    /// <see cref="ForceTranscodeVideoStreams"/>), which is in no server's default decoding-codec
    /// list. An administrator who does add it there loses nothing: the wrapper passes the server's
    /// accelerator arguments through untouched and FFmpeg-mvc opens the multiview stream with its own
    /// software decoder, whatever the input was asked to decode with. See
    /// <c>docs/architecture.md</c>, "Hardware acceleration passes through".
    /// </para>
    /// </remarks>
    public const VideoType VersionVideoType = VideoType.VideoFile;

    /// <summary>
    /// Builds one profile version of one eligible media source.
    /// </summary>
    /// <param name="item">The item the version will be offered under.</param>
    /// <param name="source">The eligible source this version converts.</param>
    /// <param name="profile">The profile this version offers.</param>
    /// <param name="labelSource">
    /// Whether the version must name the file it converts, because the item has more than one file
    /// to choose between.
    /// </param>
    /// <returns>The version as a media source, ready to offer or to materialise.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="item"/>, <paramref name="source"/> or <paramref name="profile"/> is <c>null</c>.
    /// </exception>
    public static MediaSourceInfo Build(BaseItem item, MvcEligibleSource source, StereoProfile profile, bool labelSource)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(profile);

        var original = source.Original;

        // The version's video has to arrive as an encode, and the codec it reports is the only
        // thing in its report the server's copy decision actually reads, so the stream list is
        // built through ForceTranscodeVideoStreams: the video stream cloned and re-labelled, the
        // source's own report left as it was. See that type.
        var reportedStreams = ForceTranscodeVideoStreams.WithForcedVideoCodec(
            original?.MediaStreams,
            out int videoStreamIndex);

        // A version also converts the picture, so the frame it encodes is its own and not the
        // file's. The size the clone reports is what that frame is, because the server sizes its
        // own scale from the numbers on this stream and the scale it writes is clamped against the
        // frame that reaches it at run time. See ProfileVideoGeometry.
        reportedStreams = ProfileVideoGeometry.WithEncodedFrameSize(reportedStreams, profile);

        // MVP: markers carry no subtitle ordinal, so every version plays without burned-in
        // subtitles (the stock pipeline's subtitle selection is not applied to marker sources
        // either). The marker contract - and ProfileMarker.Create - already carry the optional
        // field; wiring a per-playback subtitle choice to it, mapped through
        // SubtitleStreamOrdinals, is the follow-up task's job.
        //
        // The video index travels with it because the server maps the video stream by number
        // (-map 0:<index>, the stream's own MediaStream.Index) and that number is something only
        // the builder of the version can name: the wrapper has no way to tell a numeric video map
        // from a numeric audio map out of argv, and no way to know that the list it was handed
        // skips a stream the file has.
        var marker = ProfileMarker.Create(
            profile.Id,
            source.SourcePath,
            subtitleOrdinal: null,
            videoStreamIndex: videoStreamIndex < 0 ? null : videoStreamIndex);

        var mediaSource = new MediaSourceInfo
        {
            // Request plumbing and nothing else: the DynamicHLS routes parse this one as a GUID
            // (see BuildMediaSourceIdFromSource), while which conversion a version asks for
            // travels in the marker below - the wrapper reads that, never this. Folded from the
            // source's own identity, so two files of one item never share a version id - and, for
            // the version-item manager, the primary key of the item this version is materialised
            // as.
            Id = AnaglyfinMediaSourceProvider.BuildMediaSourceIdFromSource(source.SourceId, source.SourcePath, profile.Id),

            // The version label clients show verbatim in their version pickers.
            Name = VersionName(source, profile, labelSource),

            // The marker stands in for the path: it is what FFmpeg-mvc (via the wrapper) is handed
            // as input, and the only field of this object the wrapper is allowed to read.
            Path = marker.ToMediaSourcePath(),

            // The marker is URL-shaped, and every consumer between builder and wrapper treats
            // URL-shaped inputs as HTTP; declaring anything else would make the server
            // second-guess the path's protocol.
            Protocol = MediaProtocol.Http,

            // Transcoding is where the wrapper can rewrite the command, so it is the only
            // transport these versions support. The server may still revoke the transcoding flag
            // by user permission afterwards; that is permissions working, not this builder lying.
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,

            // What the server's hardware encoder gate asks about, stated instead of left to the
            // type's default: a source that names a disc is a source the server encodes with
            // libx264 whatever its settings say. It names no encoder, it changes no transport (the
            // path is still a marker behind the Http protocol, and the reported codec is still one
            // no client can copy), and it buys no hardware decode: see VersionVideoType.
            VideoType = VersionVideoType,

            // ffprobe cannot read a marker URL, so the stream list copied from the source this
            // version is built from is authoritative.
            SupportsProbing = false,

            // The media sits on the server's own filesystem; only its marker looks like a URL, and
            // nothing is ever fetched from it.
            IsRemote = false,

            // No open token, no live stream: the source is inert until the transcode pipeline
            // reads its marker.
            RequiresOpening = false,
            RequiresClosing = false,
            LiveStreamId = null,
            OpenToken = null,
            IsInfiniteStream = false,
            Type = MediaSourceType.Default,

            // Duration and container fidelity to the source this version converts.
            RunTimeTicks = original?.RunTimeTicks ?? item.RunTimeTicks,
            Container = original?.Container,
            Size = original?.Size,
            Bitrate = original?.Bitrate,

            // Written as unset rather than simply left out, because this one is a decision and not
            // an oversight; see the type remarks.
            Video3DFormat = null,

            // Never null: ForceTranscodeVideoStreams answers a missing or empty report with an
            // empty list, and an explicit null here would break consumers that enumerate it. This
            // is the version's own list - the video stream in it is a clone, so the source this
            // version converts keeps the objects it was probed with.
            Formats = original?.Formats ?? Array.Empty<string>(),
            MediaStreams = reportedStreams,
        };

        return mediaSource;
    }

    /// <summary>
    /// Labels one version for a client's version picker.
    /// </summary>
    /// <param name="source">The eligible source this version converts.</param>
    /// <param name="profile">The profile this version offers.</param>
    /// <param name="labelSource">Whether the label has to name the file as well as the profile.</param>
    /// <returns>The version's display label.</returns>
    /// <remarks>
    /// With one eligible file the profile's display name is the whole label, exactly as it has
    /// always been: "3D Full Side-by-Side" says everything there is to say when there is one thing
    /// to convert. With several, the label names the file first ("3D mvc / 3D Full
    /// Side-by-Side"), because two identical labels over two different originals are a version
    /// picker the user cannot choose from - and the answer they choose is what gets encoded.
    /// </remarks>
    public static string VersionName(MvcEligibleSource source, StereoProfile profile, bool labelSource)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(profile);

        if (!labelSource)
        {
            return profile.DisplayName;
        }

        var sourceName = source.SourceName;
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            // A source with no name of its own still has a file, and the file name is the label a
            // user recognises from their own library.
            sourceName = Path.GetFileNameWithoutExtension(source.SourcePath);
        }

        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return profile.DisplayName;
        }

        return string.Concat(sourceName.Trim(), SourceNameSeparator, profile.DisplayName);
    }

    /// <summary>
    /// Decides whether a name is a label this builder writes, so a caller can tell its own text
    /// from somebody else's.
    /// </summary>
    /// <param name="name">The name an item carries.</param>
    /// <param name="profiles">The profiles to test against - the catalog's, not the enabled ones.</param>
    /// <returns>
    /// <c>true</c> when the text is a profile's label, alone or behind the file name the label
    /// builder puts in front of it.
    /// </returns>
    /// <remarks>
    /// Tested against every profile the catalog knows rather than the one this version is built for,
    /// because the name being asked about may have been written by a different profile - or by a
    /// library that has since moved on to another one. Whatever wrote it, the text is Anaglyfin's to
    /// rewrite; a name that matches none of them came from a user, and no version label is a reason
    /// to overwrite what somebody typed.
    /// </remarks>
    public static bool IsVersionLabel(string? name, IEnumerable<StereoProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var profile in profiles)
        {
            if (string.Equals(name, profile.DisplayName, StringComparison.Ordinal)
                || name.EndsWith(SourceNameSeparator + profile.DisplayName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the item id a version's media source id is the "N" form of.
    /// </summary>
    /// <param name="versionSourceId">A source id built by <see cref="Build"/>.</param>
    /// <returns>The id the version item carries, which is the id the static source is keyed by.</returns>
    /// <remarks>
    /// A static media source is keyed by the id of the item behind it, so the version's media
    /// source id <em>is</em> its item id once it exists as one. Reading it back rather than
    /// re-deriving it is what keeps the two spellings from ever drifting apart.
    /// </remarks>
    /// <exception cref="FormatException">The id is not a GUID, which a built version always is.</exception>
    public static Guid GetVersionItemId(string versionSourceId)
        => Guid.Parse(versionSourceId);
}
