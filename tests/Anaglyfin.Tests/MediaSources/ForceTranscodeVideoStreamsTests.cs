using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Anaglyfin.MediaSources;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Anaglyfin.Tests.MediaSources;

/// <summary>
/// Covers the one transformation a version's stream report is allowed to make - the video
/// stream cloned and re-labelled - and the two things that must not happen on the way:
/// a field of the probed stream going missing, and a stream index being guessed.
/// </summary>
public class ForceTranscodeVideoStreamsTests
{
    [Fact]
    public void EveryFieldTheProbeFilledIsStillThereAfterTheReLabel()
    {
        // The version's report is what a client's version entry, the details page and the
        // server's own transcode conditions read. A field dropped on the way is not a crash
        // and not a wrong picture: it is a version that quietly describes a lesser file, so it
        // is asserted field by field rather than spot-checked. Codec is the one field the
        // version is allowed to answer differently.
        var probed = AProbedVideoStream();

        var reported = ForceTranscodeVideoStreams.WithForcedVideoCodec(new[] { probed }, out _);

        var clone = Assert.Single(reported);
        Assert.NotSame(probed, clone);
        Assert.Equal(ForceTranscodeVideoStreams.VideoCodec, clone.Codec);
        Assert.Equal("hevc", probed.Codec);

        var compared = 0;
        foreach (var field in ReadableFields())
        {
            if (string.Equals(field.Name, nameof(MediaStream.Codec), StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(field.Name, nameof(MediaStream.DisplayTitle), StringComparison.Ordinal))
            {
                // The one client-visible answer the re-label is allowed to reach: DisplayTitle
                // is computed out of the title, the resolution, the codec and the range, so the
                // version's title says "MVC" where the file's says "HEVC". Everything else in
                // that string came through: with the file's codec put back, the two titles are
                // the same text.
                var versionTitle = field.GetValue(clone);
                Assert.Contains(ForceTranscodeVideoStreams.VideoCodec.ToUpperInvariant(), (string?)versionTitle, StringComparison.Ordinal);

                clone.Codec = probed.Codec;
                Assert.Equal(field.GetValue(probed), field.GetValue(clone));
                clone.Codec = ForceTranscodeVideoStreams.VideoCodec;
                Assert.NotEqual(field.GetValue(probed), versionTitle);
                compared++;
                continue;
            }

            var expected = field.GetValue(probed);
            var actual = field.GetValue(clone);
            Assert.True(
                Equals(expected, actual),
                $"{field.Name}: the file reported '{expected}' and the version reported '{actual}'");
            compared++;
        }

        // The sweep has to have swept, and it has to have swept the derived HDR answers: a
        // reflection filter that quietly matched nothing would otherwise pass every assertion
        // above by asserting nothing.
        Assert.True(compared >= 50, $"Only {compared} fields of {nameof(MediaStream)} were compared.");
        Assert.Contains(
            ReadableFields(),
            field => string.Equals(field.Name, nameof(MediaStream.VideoRange), StringComparison.Ordinal));
    }

    [Fact]
    public void TheVersionAnswersTheHdrQuestionsTheSameWayTheProbedStreamDoes()
    {
        // VideoRange and VideoRangeType are the two answers that decide whether a client asks
        // for an HDR stream and whether the server tonemaps. They are computed out of the
        // colour and Dolby Vision fields rather than stored, so they are asserted here with
        // their expected values named: a clone that lost the fields behind them would answer
        // "SDR" for both, and a comparison of the clone against itself would never notice.
        var hdr10Plus = AProbedVideoStream();
        hdr10Plus.ColorTransfer = "smpte2084";
        hdr10Plus.ColorSpace = "bt2020nc";
        hdr10Plus.ColorPrimaries = "bt2020";
        hdr10Plus.BitDepth = 10;
        hdr10Plus.Hdr10PlusPresentFlag = true;

        Assert.Equal(VideoRange.HDR, hdr10Plus.VideoRange);
        Assert.Equal(VideoRangeType.HDR10Plus, hdr10Plus.VideoRangeType);

        var reported = ForceTranscodeVideoStreams.WithForcedVideoCodec(new[] { hdr10Plus }, out _);

        var clone = Assert.Single(reported);
        Assert.Equal(VideoRange.HDR, clone.VideoRange);
        Assert.Equal(VideoRangeType.HDR10Plus, clone.VideoRangeType);

        // And the ordinary case, because an "everything is HDR" clone would pass the above.
        var sdr = AProbedVideoStream();
        sdr.ColorTransfer = "bt709";
        sdr.ColorSpace = "bt709";
        sdr.ColorPrimaries = "bt709";
        sdr.BitDepth = 8;
        sdr.Hdr10PlusPresentFlag = null;

        Assert.Equal(VideoRange.SDR, sdr.VideoRange);
        Assert.Equal(VideoRangeType.SDR, sdr.VideoRangeType);

        Assert.Equal(
            VideoRange.SDR,
            Assert.Single(ForceTranscodeVideoStreams.WithForcedVideoCodec(new[] { sdr }, out _)).VideoRange);
    }

    [Fact]
    public void TheIndexTheHelperReportsIsTheStreamsOwnAndNotThePlaceItSitsInTheList()
    {
        // The number a "-map 0:<index>" spends on a stream is that stream's index inside the
        // file, which the probe writes into MediaStream.Index. A report out of file order -
        // because the file carries a stream the server does not report, or the list was
        // reordered for a client - separates the two numbers, and only one of them is the
        // server's.
        var audio = new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = "aac" };
        var video = new MediaStream { Type = MediaStreamType.Video, Index = 1, Codec = "hevc" };

        var reported = ForceTranscodeVideoStreams.WithForcedVideoCodec(new[] { video, audio }, out var videoStreamIndex);

        Assert.Equal(1, videoStreamIndex);
        Assert.Equal(ForceTranscodeVideoStreams.VideoCodec, reported[0].Codec);
        Assert.Equal(MediaStreamType.Video, reported[0].Type);
        Assert.Same(audio, reported[1]);
    }

    [Fact]
    public void AStreamWithoutAnIndexIsStillRelabelledButNamesNothing()
    {
        // A stream that was never probed for an index answers -1. The version still has to
        // report an uncopiable codec - that is what keeps it off the copy path - but it has no
        // number to hand the wrapper, and inventing one from its position would have the
        // wrapper remove whatever stream did carry that number.
        var video = new MediaStream { Type = MediaStreamType.Video, Index = -1, Codec = "hevc" };

        var reported = ForceTranscodeVideoStreams.WithForcedVideoCodec(new[] { video }, out var videoStreamIndex);

        Assert.Equal(-1, videoStreamIndex);
        Assert.Equal(ForceTranscodeVideoStreams.VideoCodec, Assert.Single(reported).Codec);
    }

    [Fact]
    public void AReportWithNothingToRelabelIsHandedOnAsItWas()
    {
        // No video stream means nothing to re-label, and a version that copies the list for no
        // reason is a version that silently diverges from the original later.
        var audio = new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = "aac" };

        var reported = ForceTranscodeVideoStreams.WithForcedVideoCodec(new[] { audio }, out var videoStreamIndex);

        Assert.Equal(-1, videoStreamIndex);
        Assert.Same(audio, Assert.Single(reported));
    }

    /// <summary>
    /// Builds a video stream with a distinct non-default value in every field the model can
    /// hold, so that a field lost on the way is a field this test sees.
    /// </summary>
    /// <remarks>
    /// The values are written by reflection over the model rather than spelled out, so a
    /// field added by a server upgrade arrives here filled on its own and compared on its own.
    /// A field whose type this test cannot fill fails it loudly instead of passing untested.
    /// </remarks>
    private static MediaStream AProbedVideoStream()
    {
        var stream = new MediaStream { Type = MediaStreamType.Video };

        foreach (var field in ReadableFields())
        {
            if (!field.CanWrite
                || string.Equals(field.Name, nameof(MediaStream.Type), StringComparison.Ordinal))
            {
                // Read-only fields are the computed ones; the loop below compares their answers
                // instead of writing them.
                continue;
            }

            field.SetValue(stream, SampleValueFor(field));
        }

        // Two fields want values of their own rather than samples: the codec the file really
        // carries, and an index that is a legal stream number.
        stream.Codec = "hevc";
        stream.Index = 3;

        return stream;
    }

    /// <summary>
    /// Every field <c>MediaStream</c> exposes to a client, in a stable order. Read-only fields
    /// count: <c>VideoRange</c> and its neighbours are computed, which is exactly why they are
    /// worth checking - they are the answers a client acts on.
    /// </summary>
    private static PropertyInfo[] ReadableFields()
        => typeof(MediaStream)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(field => field.CanRead && field.GetIndexParameters().Length == 0)
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();

    private static object? SampleValueFor(PropertyInfo field)
    {
        var type = Nullable.GetUnderlyingType(field.PropertyType) ?? field.PropertyType;

        if (type == typeof(string))
        {
            return field.Name + "-probed";
        }

        if (type == typeof(int))
        {
            return 41;
        }

        if (type == typeof(long))
        {
            return 4_100_000L;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(float))
        {
            return 23.976f;
        }

        if (type == typeof(double))
        {
            return 4.1d;
        }

        if (type.IsEnum)
        {
            // Not the default value: a field dropped by a clone would otherwise reappear as
            // whatever the new instance happens to start out with, which is what the copy
            // under test would have it be anyway.
            var values = Enum.GetValues(type);
            var untouched = Activator.CreateInstance(type);
            foreach (var value in values)
            {
                if (!Equals(value, untouched))
                {
                    return value;
                }
            }

            return values.GetValue(0);
        }

        throw new NotSupportedException(
            $"MediaStream.{field.Name} is a {type.Name}, which this test cannot fill. Fill it explicitly: " +
            "an unfilled field is an untested one, and an untested one is what this test exists to catch.");
    }
}
