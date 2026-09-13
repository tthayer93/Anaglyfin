using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anaglyfin.Profiles;
using MediaBrowser.Model.Entities;

namespace Anaglyfin.MediaSources;

/// <summary>
/// Reports the frame a profile actually encodes, in the stream a version publishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the reported size is a correctness matter and not a label.</b> A version's video
/// stream is the only geometry the server reads before it builds a transcode: with a client
/// that asks for no resolution and brings at least the bitrate the stream reports, the server
/// defaults its own <c>MaxWidth</c>/<c>MaxHeight</c> to this stream's <see cref="MediaStream.Width"/>
/// and <see cref="MediaStream.Height"/> (<c>StreamingHelpers.GetStreamingState</c>), and those
/// two numbers are what the software scale filter it writes into <c>-vf</c> clamps against. The
/// scale is computed at run time from the frame that reaches it (<c>iw</c>/<c>ih</c>), never
/// from the numbers below, so a version that reports the <em>source's</em> frame while its
/// profile encodes a different one gets the converted picture scaled to the wrong box: the
/// full-SBS version of a 1920x1080 source producing 3840x1080 while reporting 1920x1080 came
/// out of the server at 1920x540 - the right aspect ratio, half the pixels, and a version
/// whose label promised more than its command was allowed to deliver.
/// </para>
/// <para>
/// The rule below therefore reports the <em>encoded</em> frame, which is the frame the server's
/// scale is about. At native quality the server's scale then evaluates to an identity, which is
/// the whole point of the report; the scale itself stays on the command, because it is also how
/// a client's own resolution ceiling and the bitrate ladder are expressed.
/// </para>
/// <para>
/// <b>Why nothing here is a claim about the source.</b> The size the profile produces is
/// derived, never stored: it is a function of the profile's conversion and the size the item
/// reports, so it needs no probe, no FFmpeg, and no cache, and it cannot drift out of date when
/// the file is re-scanned. It is also the reason this is a separate decision from the codec
/// re-label in <see cref="ForceTranscodeVideoStreams"/>: that one is a lever the copy decision
/// reads, this one is a description of the picture, and the two have nothing in common except
/// that a version has to answer both differently from the original file.
/// </para>
/// <para>
/// <b>Clone, never mutate.</b> The streams a media source reports are the item's own persisted
/// objects, so the stream whose size changes here is a fresh copy of it (the same serialisation
/// round-trip <see cref="ForceTranscodeVideoStreams"/> uses, so a field of the model cannot go
/// missing on the way) and every other stream of the list is handed on as the item hands it
/// out. A list with nothing to correct is handed back untouched rather than copied for appearance.
/// </para>
/// </remarks>
public static class ProfileVideoGeometry
{
    private static readonly IReadOnlyList<MediaStream> NoStreams = Array.Empty<MediaStream>();

    /// <summary>
    /// The options the stream clone is round-tripped through: the same ones
    /// <see cref="ForceTranscodeVideoStreams"/> clones with, including the tolerance of
    /// <c>NaN</c> and <c>Infinity</c> that a frame rate or rotation can carry.
    /// </summary>
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>
    /// Gets the frame size one profile encodes, given the size the source reports.
    /// </summary>
    /// <param name="profile">The profile the version offers.</param>
    /// <param name="sourceWidth">The width the item's own video stream reports.</param>
    /// <param name="sourceHeight">The height the item's own video stream reports.</param>
    /// <returns>
    /// The width and height a version of this profile puts on the wire. For every family but
    /// full SBS that is the pair the source reported, either because the conversion lands on
    /// that size anyway or - for a source that named no width at all - because the source's own
    /// numbers are the best answer available: inventing a doubled one out of nothing would be a
    /// guess the server then scales a real picture against.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <c>null</c>.</exception>
    /// <remarks>
    /// One entry per conversion family, and the reasoning for each is the shape its command
    /// chain produces (see <see cref="Ffmpeg.FfmpegProfileArgumentBuilder"/>):
    /// <list type="table">
    /// <listheader><description>Profile</description><description>Encoded frame</description></listheader>
    /// <item>
    /// <description>2D base</description>
    /// <description>The decoder's default base view, which is the source's own frame: one
    /// eye, the source's size.</description>
    /// </item>
    /// <item>
    /// <description>Full SBS</description>
    /// <description>The native all-view frame, which lays both eyes side by side at full
    /// height: double the source's width, the source's height.</description>
    /// </item>
    /// <item>
    /// <description>Half SBS</description>
    /// <description>The native all-view frame halved in width, which lands back on the
    /// source's size - the doubled frame it is scaled from is inside the profile chain and
    /// never reaches the encoder, let alone the server's scale.</description>
    /// </item>
    /// <item>
    /// <description>Built-in anaglyph</description>
    /// <description><c>stereo3d=sbsl:</c> combines the eyes into one frame of the source's
    /// size; it is a per-pixel mix, not a layout change.</description>
    /// </item>
    /// <item>
    /// <description>Custom anaglyph</description>
    /// <description>Crops each eye out of the doubled frame back to the source's size, tints
    /// them, and screens them together at that size: the doubling is inside the graph, and
    /// the graph's output is the source's size again.</description>
    /// </item>
    /// </list>
    /// </remarks>
    public static (int? Width, int? Height) EncodedFrameSize(StereoProfile profile, int? sourceWidth, int? sourceHeight)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Kind switch
        {
            // The one profile whose encoded frame is wider than the frame the source reports.
            // Zero and negative answer "no usable width", which is what an unprobed stream's
            // 0 really claims rather than a real width to double.
            ProfileKind.SideBySideFull => sourceWidth > 0
                ? (sourceWidth.Value * 2, sourceHeight)
                : (sourceWidth, sourceHeight),

            // The conversions that pass the source's own frame size through: 2D takes the
            // base view whole, half SBS halves a frame that was built by doubling, and both
            // anaglyph families mix the eyes inside the source's frame.
            ProfileKind.TwoDimensional
                or ProfileKind.SideBySideHalf
                or ProfileKind.Stereo3DAnaglyph
                or ProfileKind.CustomGrayscaleAnaglyph => (sourceWidth, sourceHeight),

            // A kind this build cannot describe is a kind it must not describe with a size it
            // invented: the caller reports the source's own numbers, which is the answer the
            // original file gives, and the loud complaint is the builder's to make when the
            // version's command is assembled.
            _ => (sourceWidth, sourceHeight)
        };
    }

    /// <summary>
    /// Gets the stream list one version reports, with its video stream sized to the frame
    /// that version encodes.
    /// </summary>
    /// <param name="reported">
    /// The streams the item's own source reports, already carrying the version's forced video
    /// codec. Never modified.
    /// </param>
    /// <param name="profile">The profile this version offers.</param>
    /// <returns>
    /// A list whose first video stream is a clone of the reported one reporting the profile's
    /// encoded frame size, or <paramref name="reported"/> itself when there is nothing to
    /// correct - no stream list, no video stream in it, a profile whose frame is the size the
    /// source reports, or a source that named no width to double.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <c>null</c>.</exception>
    /// <remarks>
    /// The one field written on the clone is the size, and it is only written when
    /// <see cref="EncodedFrameSize"/> can name a different one. Everything else a client or the
    /// server's transcode conditions read - the forced codec the version already carries, bit
    /// depth, frame rate, colour transfer, the computed HDR answers - is exactly what the item
    /// reported, so a version differs from the file by its picture and not by its paperwork.
    /// </remarks>
    public static IReadOnlyList<MediaStream> WithEncodedFrameSize(
        IReadOnlyList<MediaStream>? reported,
        StereoProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (reported is null || reported.Count == 0)
        {
            return NoStreams;
        }

        // The same stream the marker names and the codec lever re-labels: the first video
        // stream, which is the one a transcode with no explicit video-stream choice maps.
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
            return reported;
        }

        var video = reported[firstVideoIndex];
        var frame = EncodedFrameSize(profile, video.Width, video.Height);

        // The answer is the size the stream already carries, so the version's report is already
        // right and the list it came in on is handed straight back: no clone, no new list, and
        // nothing that could quietly diverge from the original later.
        if (frame.Width == video.Width && frame.Height == video.Height)
        {
            return reported;
        }

        var versions = new List<MediaStream>(reported.Count);
        for (var index = 0; index < reported.Count; index++)
        {
            versions.Add(index == firstVideoIndex ? CloneWithFrameSize(reported[index], frame) : reported[index]);
        }

        return versions;
    }

    /// <summary>
    /// Reports one video stream at a given encoded frame size.
    /// </summary>
    /// <param name="source">The stream the item's own source reports. Not modified.</param>
    /// <param name="frame">The frame size the version encodes.</param>
    /// <returns>
    /// A copy of the stream whose size is the frame's, and whose every other field is the
    /// stream's.
    /// </returns>
    private static MediaStream CloneWithFrameSize(MediaStream source, (int? Width, int? Height) frame)
    {
        // Round-tripped through the model's own JSON rather than hand-copied, for the reason
        // ForceTranscodeVideoStreams gives: a field list fails by reporting less, quietly.
        var clone = JsonSerializer.Deserialize<MediaStream>(JsonSerializer.Serialize(source, CloneOptions), CloneOptions)!;

        clone.Width = frame.Width;
        clone.Height = frame.Height;

        // AspectRatio and IsAnamorphic deliberately stay as the file reported them. Neither
        // takes part in the server's scaling (the scale it writes is relative to the frame
        // reaching it, and the sample aspect ratio travels with that frame, not with this
        // stream), so rewriting them would change no pixel - and the stereo flag that does
        // change pixels, MediaSourceInfo.Video3DFormat, is a field of the source rather than
        // of the stream, and is left unset by the provider for the reason given there.
        return clone;
    }
}
