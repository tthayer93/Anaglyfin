using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Anaglyfin.Tests.MediaSources;

/// <summary>
/// Covers what a version says about the frame it encodes: the size each profile's conversion
/// lands on, the source sizes it cannot derive, and the two things that must not happen on the
/// way - the item's own stream written to, and any other field of the stream disturbed.
/// </summary>
/// <remarks>
/// The numbers here are not labels. The server defaults the <c>MaxWidth</c>/<c>MaxHeight</c> of a
/// request to the video stream this source reports, and the scale it writes from them runs
/// against whatever frame reaches it at run time, so a version whose report and whose encoder
/// disagree gets its own converted picture scaled into the wrong box. See
/// <see cref="ProfileVideoGeometry"/> for the reasoning and
/// <see cref="AnaglyfinMediaSourceProviderTests"/> for the same claims on the provider's output.
/// </remarks>
public class ProfileVideoGeometryTests
{
    private const int SourceWidth = 1920;

    private const int SourceHeight = 1080;

    // ----- the decision, per conversion family ---------------------------------------

    [Theory]
    [InlineData(ProfileKind.TwoDimensional, SourceWidth, SourceHeight)]
    [InlineData(ProfileKind.SideBySideFull, SourceWidth * 2, SourceHeight)]
    [InlineData(ProfileKind.SideBySideHalf, SourceWidth, SourceHeight)]
    [InlineData(ProfileKind.Stereo3DAnaglyph, SourceWidth, SourceHeight)]
    [InlineData(ProfileKind.CustomGrayscaleAnaglyph, SourceWidth, SourceHeight)]
    public void EveryFamilyAnswersWithTheFrameItsCommandEncodes(ProfileKind kind, int expectedWidth, int expectedHeight)
    {
        // Each of these numbers is the shape the profile's own filter chain produces, which is
        // what makes it checkable without running FFmpeg: full SBS is the decoder's doubled
        // native frame, and everything else either halves that frame again (half SBS) or works
        // inside the source's own size (2D, both anaglyph families).
        var frame = ProfileVideoGeometry.EncodedFrameSize(AProfileOf(kind), SourceWidth, SourceHeight);

        Assert.Equal((int?)expectedWidth, frame.Width);
        Assert.Equal((int?)expectedHeight, frame.Height);
    }

    [Fact]
    public void FullSideBySideIsTheOnlyFamilyThatMovesTheWidth()
    {
        // The height never moves: the eyes are laid out sideways, at full height, in every
        // profile this build offers.
        foreach (var kind in Enum.GetValues<ProfileKind>())
        {
            var frame = ProfileVideoGeometry.EncodedFrameSize(AProfileOf(kind), SourceWidth, SourceHeight);

            Assert.Equal(SourceHeight, frame.Height);
            Assert.Equal(kind == ProfileKind.SideBySideFull ? SourceWidth * 2 : SourceWidth, frame.Width);
        }
    }

    [Theory]
    [InlineData(null)] // a stream the probe never sized
    [InlineData(0)] // a stream that carries 0 rather than nothing
    [InlineData(-1)] // and a report that is not a size at all
    public void AWidthTheSourceNeverNamedIsNotDoubledIntoOne(int? sourceWidth)
    {
        // Doubling produces a number out of whatever it is given, including nothing. That number
        // then sizes a real scale of a real picture, which is the wrong way round: with no width
        // to work from the version says what the file says.
        var frame = ProfileVideoGeometry.EncodedFrameSize(AProfileOf(ProfileKind.SideBySideFull), sourceWidth, SourceHeight);

        Assert.Equal(sourceWidth, frame.Width);
        Assert.Equal(SourceHeight, frame.Height);
    }

    [Fact]
    public void AWidthIsDoubledEvenWhenTheSourceNamedNoHeight()
    {
        // The width the source did name is still the width the profile doubles; keeping the
        // height unknown is honest, and dropping it would be a second loss.
        var frame = ProfileVideoGeometry.EncodedFrameSize(AProfileOf(ProfileKind.SideBySideFull), SourceWidth, null);

        Assert.Equal(SourceWidth * 2, frame.Width);
        Assert.Null(frame.Height);
    }

    [Fact]
    public void AKindThisBuildCannotDescribeRestatesTheSource()
    {
        // An out-of-range kind is a hand-built or settings-written profile the catalog would
        // already have refused. Geometry is not the place that guesses a size for it: the source's
        // own numbers go out, and the refusal is the command builder's when the version is built.
        var frame = ProfileVideoGeometry.EncodedFrameSize(AProfileOf((ProfileKind)99), SourceWidth, SourceHeight);

        Assert.Equal(SourceWidth, frame.Width);
        Assert.Equal(SourceHeight, frame.Height);
    }

    [Fact]
    public void TheDecisionNeedsBothTheProfileAndTheSource()
    {
        // The size is derived, never stored: no probe, no FFmpeg, no cache, and nothing that can
        // go stale when the file is re-scanned or a different title is offered.
        Assert.Equal(7680, ProfileVideoGeometry.EncodedFrameSize(AProfileOf(ProfileKind.SideBySideFull), 3840, 2160).Width);
        Assert.Equal(2560, ProfileVideoGeometry.EncodedFrameSize(AProfileOf(ProfileKind.SideBySideFull), 1280, 720).Width);
        Assert.Throws<ArgumentNullException>(() => ProfileVideoGeometry.EncodedFrameSize(null!, SourceWidth, SourceHeight));
    }

    // ----- the stream report ----------------------------------------------------------

    [Fact]
    public void TheSizedStreamIsTheFirstVideoStreamOfTheReport()
    {
        // The stream a transcode with no explicit video choice maps is the first video stream,
        // which is also the one the marker names and the codec lever re-labels. A report carrying
        // a second video stream keeps that one as it was reported: sizing a stream nobody plays is
        // how a version ends up describing a picture it is not producing.
        var first = new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = SourceWidth, Height = SourceHeight };
        var second = new MediaStream { Type = MediaStreamType.Video, Index = 3, Codec = "hevc", Width = SourceWidth, Height = SourceHeight };
        var audio = new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" };

        var reported = ProfileVideoGeometry.WithEncodedFrameSize(
            new[] { audio, first, second },
            AProfileOf(ProfileKind.SideBySideFull));

        Assert.Same(audio, reported[0]);
        Assert.NotSame(first, reported[1]);
        Assert.Equal(3840, reported[1].Width);
        Assert.Same(second, reported[2]);
        Assert.Equal(SourceWidth, second.Width);
    }

    [Theory]
    [InlineData(ProfileKind.TwoDimensional)]
    [InlineData(ProfileKind.SideBySideHalf)]
    [InlineData(ProfileKind.Stereo3DAnaglyph)]
    [InlineData(ProfileKind.CustomGrayscaleAnaglyph)]
    public void AVersionThatSizesNothingGetsTheStreamsItCameWithUnchanged(ProfileKind kind)
    {
        // No size to write means no work to do. Copying the list anyway would be a version
        // diverging from the original source one quiet field at a time, later.
        var video = AProbedVideoStream();
        var streams = new[] { video };

        var reported = ProfileVideoGeometry.WithEncodedFrameSize(streams, AProfileOf(kind));

        Assert.Same(streams, reported);
        Assert.Same(video, Assert.Single(reported));
    }

    [Fact]
    public void NothingButTheSizeMovesOnTheVersionSStream()
    {
        // The clone is the model's own JSON round-trip, so the exhaustive proof that the
        // round-trip loses no field lives in ForceTranscodeVideoStreamsTests, which clones with
        // the same serializer and options. What is asserted here is the other half: that writing
        // a size onto that clone left every answer in it alone - including the two fields a
        // doubled width makes a lie of if they are forgotten, and the computed answers (VideoRange
        // and its Dolby Vision neighbours) that a careless copy drops.
        var probed = AProbedVideoStream();

        var clone = Assert.Single(ProfileVideoGeometry.WithEncodedFrameSize(
            new[] { probed },
            AProfileOf(ProfileKind.SideBySideFull)));

        Assert.NotSame(probed, clone);
        Assert.Equal(3840, clone.Width);
        Assert.Equal(SourceHeight, clone.Height);

        var compared = 0;
        foreach (var field in typeof(MediaStream)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(field => field.CanRead && field.GetIndexParameters().Length == 0)
                     .OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            if (field.Name is nameof(MediaStream.Width)
                or nameof(MediaStream.Height)
                or nameof(MediaStream.DisplayTitle))
            {
                // DisplayTitle is computed out of the resolution and the codec, so it is the one
                // client-visible string the size is expected to reach - and the model resolves a
                // resolution into a badge rather than a number, which is the cosmetic price of the
                // report: a full-SBS version of a 1080p film is offered to clients as 4K, because
                // its frame really is 3840 wide.
                Assert.Contains("1080p", probed.DisplayTitle, StringComparison.Ordinal);
                Assert.Contains("4K", clone.DisplayTitle, StringComparison.Ordinal);
                continue;
            }

            Assert.True(
                Equals(field.GetValue(probed), field.GetValue(clone)),
                $"{field.Name}: the file reported '{field.GetValue(probed)}' and the version reported '{field.GetValue(clone)}'");
            compared++;
        }

        Assert.True(compared >= 50, $"Only {compared} fields of {nameof(MediaStream)} were compared.");
    }

    [Fact]
    public void TheItemSOwnStreamKeepsTheFrameItWasProbedWith()
    {
        // The list this is handed can be - and in the provider's path is - built around the very
        // stream objects the item's own version serves to clients. Writing the doubled width onto
        // that object would tell every client, and every later transcode of the original file,
        // that the film is twice as wide as it is.
        var probed = AProbedVideoStream();

        var reported = ProfileVideoGeometry.WithEncodedFrameSize(
            new[] { probed },
            AProfileOf(ProfileKind.SideBySideFull));

        Assert.Equal(SourceWidth, probed.Width);
        Assert.NotSame(probed, Assert.Single(reported));

        // Asking twice gives the same answer rather than a doubling of it.
        var again = ProfileVideoGeometry.WithEncodedFrameSize(reported, AProfileOf(ProfileKind.SideBySideFull));

        Assert.Equal(3840, Assert.Single(again).Width);
    }

    [Fact]
    public void AVersionWithoutStreamsReportsNoStreams()
    {
        // An item whose media sources cannot be enumerated right now costs fidelity, not a crash:
        // the version is still offered, with no stream list at all.
        Assert.Empty(ProfileVideoGeometry.WithEncodedFrameSize(null, AProfileOf(ProfileKind.SideBySideFull)));
        Assert.Empty(ProfileVideoGeometry.WithEncodedFrameSize(Array.Empty<MediaStream>(), AProfileOf(ProfileKind.SideBySideFull)));

        Assert.Throws<ArgumentNullException>(() => ProfileVideoGeometry.WithEncodedFrameSize(new[] { AProbedVideoStream() }, null!));
    }

    [Fact]
    public void AReportWithNoVideoStreamIsHandedOnAsItWas()
    {
        var audio = new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = "aac" };
        var streams = new[] { audio };

        Assert.Same(streams, ProfileVideoGeometry.WithEncodedFrameSize(streams, AProfileOf(ProfileKind.SideBySideFull)));
    }

    // ----- fixtures -------------------------------------------------------------------

    /// <summary>
    /// A profile of one family, carrying the details its family needs (a code for the built-in
    /// anaglyph, colours for the custom one) so the fixture is a profile the catalog would accept.
    /// </summary>
    private static StereoProfile AProfileOf(ProfileKind kind)
        => new()
        {
            Id = kind switch
            {
                ProfileKind.TwoDimensional => ProfileIds.TwoDBase,
                ProfileKind.SideBySideFull => ProfileIds.SideBySideFull,
                ProfileKind.SideBySideHalf => ProfileIds.SideBySideHalf,
                ProfileKind.Stereo3DAnaglyph => ProfileIds.AnaglyphRedCyanDubois,
                ProfileKind.CustomGrayscaleAnaglyph => ProfileIds.CustomGrayscale,

                // Any allowlisted id will do for a family this build does not know; the point of
                // the fixture is the family.
                _ => ProfileIds.TwoDBase
            },
            DisplayName = "Fixture " + kind,
            Kind = kind,
            Stereo3DOutputCode = kind == ProfileKind.Stereo3DAnaglyph ? "arcd" : null,
            LeftEyeColor = kind == ProfileKind.CustomGrayscaleAnaglyph ? ProfileCatalog.DefaultCustomLeftEyeColor : null,
            RightEyeColor = kind == ProfileKind.CustomGrayscaleAnaglyph ? ProfileCatalog.DefaultCustomRightEyeColor : null
        };

    /// <summary>
    /// A 1080p HDR10+ video stream with something distinctive in the fields a version is not
    /// supposed to touch, so that a field disturbed by the size rewrite is a field this test sees.
    /// </summary>
    private static MediaStream AProbedVideoStream()
        => new()
        {
            Type = MediaStreamType.Video,
            Index = 0,
            Codec = "hevc",
            CodecTag = "hev1",
            Width = SourceWidth,
            Height = SourceHeight,
            AspectRatio = "1.78",
            IsAnamorphic = false,
            BitDepth = 10,
            BitRate = 40_000_000,
            RealFrameRate = 23.976f,
            AverageFrameRate = 23.976f,
            Profile = "main10",
            Level = 150d,
            PixelFormat = "yuv420p10le",
            ColorSpace = "bt2020nc",
            ColorTransfer = "smpte2084",
            ColorPrimaries = "bt2020",
            ColorRange = "tv",
            Hdr10PlusPresentFlag = true,
            RefFrames = 4,
            Rotation = 0,
            Language = "eng",
            Title = "MVC base",
            Comment = "3D MVC",
            TimeBase = "1/1000",
            IsInterlaced = false,
            IsDefault = true
        };
}
