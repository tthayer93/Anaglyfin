using System;
using System.Collections.Generic;
using System.Linq;
using Anaglyfin.Configuration;
using Anaglyfin.Ffmpeg;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.Markers;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Contract tests for the wrapper argument rewriter: which commands pass through
/// untouched, which are refused, and - the part that has to be exact - what a rewritten
/// Jellyfin command looks like token for token.
/// </summary>
/// <remarks>
/// <para>
/// Every expectation is written as a complete verbatim argument vector, because these are
/// the command lines a real FFmpeg-mvc will be handed: an insertion in the wrong place, a
/// leftover <c>-map 0:v</c> beside the profile's own map, or a silently dropped audio map
/// is a broken playback, not a cosmetic diff. No FFmpeg is executed and no process is
/// started - the rewriter is pure, so the command is the whole observable behaviour.
/// </para>
/// <para>
/// The fixture command is the shape Jellyfin builds for an HLS transcode: global flags, one
/// input, one video and one audio map, encoder and muxer choices, and the playlist as the
/// output. A second fixture drops the maps entirely, because that is the other shape a
/// transcode arrives in and the one where an inserted video map quietly takes the audio
/// away. The executable token is not part of an argument vector - the launcher supplies the
/// binary it execs - so the vectors below start at the first option.
/// </para>
/// </remarks>
public class WrapperArgumentRewriterTests
{
    /// <summary>The real file a marker stands in for; the rewrite has to put this in place of the marker.</summary>
    private const string SourcePath = "/movies/Movie (2010)/Movie.2010.3D.mkv";

    /// <summary>The burn-in filter the builder produces for <see cref="SourcePath"/> at ordinal 0.</summary>
    private const string BurnInZero = "subtitles=filename='/movies/Movie (2010)/Movie.2010.3D.mkv':si=0";

    private const string BurnInTwo = "subtitles=filename='/movies/Movie (2010)/Movie.2010.3D.mkv':si=2";

    /// <summary>The custom grayscale graph reading the unnamed first-input video stream.</summary>
    private static string CustomGraph => CustomGraphStartingAt("[0:v]");

    /// <summary>A filtergraph the wrapper did not write, in the shape Jellyfin builds for a two-pin half-SBS chain.</summary>
    private const string ForeignGraph =
        "[0:v:view:0]scale=iw/2:ih:flags=bicubic[l];[0:v:view:1]scale=iw/2:ih:flags=bicubic[r];[l][r]hstack=inputs=2,format=yuv420p[v]";

    private static readonly ProfileCatalog Catalog = new();

    private readonly WrapperArgumentRewriter _rewriter = new(Catalog, FfmpegProfileArgumentBuilder.Shared);

    /// <summary>Marker texts that must never reach FFmpeg, with the parser status each one earns.</summary>
    public static TheoryData<string, MarkerParseStatus> BrokenMarkers => new()
    {
        // Structure that only nearly spells a marker, or does not spell one at all.
        { "http://127.0.0.1/anaglyfin/profile", MarkerParseStatus.MalformedMarker },
        { "http://127.0.0.1/anaglyfin/profile/anaglyph_arcd", MarkerParseStatus.MalformedMarker },
        { "http://127.0.0.1/anaglyfin/profile/anaglyph_arcd?sourc=%2Fmovies%2FFilm.mkv", MarkerParseStatus.MalformedMarker },

        // Well-formed text naming something off the allowlist.
        { "http://127.0.0.1/anaglyfin/profile/side_by_side_too?source=%2Fmovies%2FFilm.mkv", MarkerParseStatus.UnknownProfile },

        // A marker with no real file behind it, or one that is not a rooted path.
        { "http://127.0.0.1/anaglyfin/profile/sbs_full?subtitle=0", MarkerParseStatus.MissingSource },
        { "http://127.0.0.1/anaglyfin/profile/sbs_full?source=", MarkerParseStatus.MissingSource },
        { "http://127.0.0.1/anaglyfin/profile/sbs_full?source=relative%2Ffilm.mkv", MarkerParseStatus.InvalidSource },

        // A subtitle ordinal that is not a track number.
        { "http://127.0.0.1/anaglyfin/profile/sbs_full?source=%2Fmovies%2FFilm.mkv&subtitle=-1", MarkerParseStatus.InvalidSubtitleOrdinal },
        { "http://127.0.0.1/anaglyfin/profile/sbs_full?source=%2Fmovies%2FFilm.mkv&subtitle=0;scale=2:2", MarkerParseStatus.InvalidSubtitleOrdinal }
    };

    // ----- pass-through ------------------------------------------------------------

    [Fact]
    public void AnOrdinaryLibraryPathPassesThroughTokenForToken()
    {
        var arguments = JellyfinLikeCommand("/library/movie.mkv");

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.PassedThrough, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Null(result.ProfileId);
        Assert.Equal(arguments.ToArray(), result.Arguments);
    }

    [Theory]
    [InlineData("-hide_banner", "-version")]
    [InlineData("-hide_banner", "-i", "/library/movie.mkv", "-c", "copy", "/out/movie.mkv")]
    [InlineData("-i", "/library/a.mkv", "-i", "/library/b.mkv", "-map", "0:v", "-map", "1:v", "out.m3u8")]
    [InlineData("-i", "https://cdn.example/segment.mp4", "-f", "hls", "playlist.m3u8")]
    [InlineData("-i", "https://127.0.0.1/anaglyfin/profile/anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&subtitle=0")]
    public void CommandsAnaglyfinHasNoStakeInAreHandedBackUnchanged(params string[] arguments)
    {
        var result = _rewriter.Rewrite(arguments);

        // The last case is marker-shaped text behind the wrong scheme: the address is part
        // of the marker contract, so this is somebody else's URL and the parser says so
        // rather than guessing from the path alone.
        Assert.Equal(WrapperRewriteStatus.PassedThrough, result.Status);
        Assert.Equal(arguments.ToArray(), result.Arguments);
    }

    [Fact]
    public void AnEmptyVectorIsNothingToDoAndNotAFailure()
    {
        var result = _rewriter.Rewrite(Array.Empty<string>());

        Assert.Equal(WrapperRewriteStatus.PassedThrough, result.Status);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void TheReturnedPassThroughVectorIsASnapshotTheCallerCanMutate()
    {
        var arguments = JellyfinLikeCommand("/library/movie.mkv");

        var result = _rewriter.Rewrite(arguments);
        arguments[4] = "/library/replaced.mkv";
        arguments.RemoveAt(arguments.Count - 1);

        Assert.Equal("/library/movie.mkv", result.Arguments[4]);
        Assert.Equal("playlist.m3u8", result.Arguments[result.Arguments.Count - 1]);
    }

    [Fact]
    public void AReWrittenVectorLeavesTheCallersVectorAlone()
    {
        var arguments = JellyfinLikeCommand(Marker(ProfileIds.SideBySideFull));

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal(Marker(ProfileIds.SideBySideFull), arguments[4]);
        Assert.Contains("0:v", arguments);
    }

    [Fact]
    public void OnlyInputTokensAreClassifiedNotEveryArgument()
    {
        // The marker text is only what the provider writes into a media source path, so
        // only an input token can be one. Here the marker rides in a metadata value.
        var arguments = new List<string>
        {
            "-hide_banner", "-i", "/library/movie.mkv", "-metadata", "comment=" + Marker(ProfileIds.SideBySideFull), "out.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.PassedThrough, result.Status);
        Assert.Equal(arguments.ToArray(), result.Arguments);
    }

    // ----- fail closed -------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BrokenMarkers))]
    public void AMarkerThatWantedToBeAMarkerAndFailedRefusesTheJob(string marker, MarkerParseStatus expectedStatus)
    {
        var arguments = JellyfinLikeCommand(marker);

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.RejectedMarker, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Equal(expectedStatus, result.MarkerStatus);

        // Nothing is handed back to execute: a launcher that forgot to check the status
        // still cannot put the marker in front of FFmpeg.
        Assert.Empty(result.Arguments);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Theory]
    [MemberData(nameof(BrokenMarkers))]
    public void ARefusalNamesTheProblemWithoutEchoingTheCommand(string marker, MarkerParseStatus expectedStatus)
    {
        var result = _rewriter.Rewrite(JellyfinLikeCommand(marker));
        var error = result.Error ?? string.Empty;

        Assert.Equal(expectedStatus, result.MarkerStatus);

        // Marker text arrives from a playback request; the log line must not become a
        // mirror of it.
        Assert.DoesNotContain("127.0.0.1", error, StringComparison.Ordinal);
        Assert.DoesNotContain("source=", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Film.mkv", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenMarkerOutranksAValidOneInTheSameCommand()
    {
        // Two inputs, one good marker and one broken one, cannot both be the caller's
        // intent; the safe reading is that neither is.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull),
            "-i", "http://127.0.0.1/anaglyfin/profile/anaglyph_arcd",
            "-map", "0:v", "out.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.RejectedMarker, result.Status);
        Assert.Equal(MarkerParseStatus.MalformedMarker, result.MarkerStatus);
        Assert.Empty(result.Arguments);
    }

    [Theory]
    [InlineData(ProfileIds.SideBySideFull, ProfileIds.AnaglyphRedCyanDubois)]
    [InlineData(ProfileIds.SideBySideHalf, ProfileIds.SideBySideHalf)]
    public void TwoValidMarkersRefuseTheJobBecauseOnlyOneOfThemCanBeResolved(string firstProfileId, string secondProfileId)
    {
        // The provider writes one marker per alternate source, so a command carrying two of
        // them was not assembled by the provider. Rewriting the first and running anyway
        // would leave the second marker token in the vector for FFmpeg to open as a media
        // file - the exact outcome the marker contract exists to prevent - and no profile
        // choice can be read out of the pair either.
        var arguments = new List<string>
        {
            "-i", Marker(firstProfileId),
            "-i", Marker(secondProfileId),
            "-map", "0:v", "-map", "0:a", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);
        var error = result.Error ?? string.Empty;

        Assert.Equal(WrapperRewriteStatus.UnsupportedCommandShape, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Arguments);
        Assert.DoesNotContain("127.0.0.1", error, StringComparison.Ordinal);
        Assert.DoesNotContain("source=", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Movie.2010.3D.mkv", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AGraphThatNamesOnlyViewsOfTheSourceRefusesEvenTheGraphProfile()
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-i", Marker(ProfileIds.CustomGrayscale),
            "-filter_complex", ForeignGraph, "-map", "[v]", "-map", "0:a",
            "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        // This graph reaches the source picture only through view specifiers, which a composed
        // decode refuses, and it never names the stream the profile converts: there is no label
        // here to retarget onto the profile's output, and grafting graphs would need a filter
        // parser the wrapper does not have on purpose.
        Assert.Equal(WrapperRewriteStatus.IncompatibleFilterGraph, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Arguments);
    }

    [Theory]
    [InlineData(ProfileIds.SideBySideFull)]
    [InlineData(ProfileIds.SideBySideHalf)]
    [InlineData(ProfileIds.AnaglyphRedCyanDubois)]
    public void TheSameGraphRefusesTheLinearProfilesBecauseItReadsNoStreamTheyConvert(string profileId)
    {
        // A graph whose only source references are view specifiers cannot be retargeted onto a
        // linear profile's output: the merge writes one chain and moves labels, it does not
        // parse filter text, and a view specifier is exactly what the composed decode refuses.
        var arguments = new List<string>
        {
            "-hide_banner", "-i", Marker(profileId),
            "-filter_complex", ForeignGraph, "-map", "[v]", "-map", "0:a",
            "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.IncompatibleFilterGraph, result.Status);
    }

    [Fact]
    public void AFilterGraphReadFromAScriptFileIsJustAsForeign()
    {
        // The refusal here is not the graph's contents but their absence from the vector: a
        // merge retargets the labels of the graph it can read, and a file this wrapper does not
        // open says nothing about which streams it reads or what it feeds the encoder.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideHalf),
            "-filter_complex_script", "/tmp/graph.txt", "-map", "[v]", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.IncompatibleFilterGraph, result.Status);
    }

    [Fact]
    public void AMarkerThatIsNotTheFirstInputIsRefusedBecauseProfilesAddressInputZero()
    {
        var arguments = new List<string>
        {
            "-i", "/library/other.mkv", "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "1:v", "-map", "1:a", "out.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.UnsupportedCommandShape, result.Status);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void ANullVectorIsAProgrammingErrorRatherThanARefusal()
    {
        Assert.Throws<ArgumentNullException>(() => _rewriter.Rewrite(null!));
    }

    // ----- full side-by-side -------------------------------------------------------

    [Fact]
    public void FullSideBySideAsksTheInputForEveryViewAndKeepsTheServersVideoMap()
    {
        var result = _rewriter.Rewrite(JellyfinLikeCommand(Marker(ProfileIds.SideBySideFull)));

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal(ProfileIds.SideBySideFull, result.ProfileId);
        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "warning",

                // The composed all-view request belongs to the input it configures, so it is
                // written in front of the option that opens this file rather than as a map in
                // the output segment.
                "-view_ids", "-1",
                "-i", SourcePath,

                // Full SBS is the composed frame itself: the composed request is the only thing
                // written. No profile-specific video map or filter competes with what the server
                // mapped, and nothing switches the server's subtitle choice off - a conversion
                // says nothing about the text on the picture.

                // The command's ordinary video map is the profile's output carrier and stays,
                // followed by everything else exactly where the server put it.
                "-map", "0:v", "-map", "0:a", "-c:v", "libx264", "-c:a", "copy",
                "-f", "hls", "-hls_time", "6", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void TheMarkerNeverSurvivesIntoTheRewrittenCommand()
    {
        var marker = Marker(ProfileIds.SideBySideFull);

        var result = _rewriter.Rewrite(JellyfinLikeCommand(marker));

        Assert.DoesNotContain(marker, result.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("127.0.0.1", string.Join(" ", result.Arguments), StringComparison.Ordinal);
        Assert.Equal(SourcePath, InputOf(result.Arguments));
    }

    // ----- composed view input ---------------------------------------------------------

    [Fact]
    public void AnExistingCorrectComposedViewRequestIsLeftAloneAndNotDuplicated()
    {
        var arguments = new List<string>
        {
            "-view_ids", "-1",
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "0:v", "-map", "0:a", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "0:v", "-map", "0:a", "playlist.m3u8"
            },
            result.Arguments);
        Assert.Single(result.Arguments, token => token == "-view_ids");
    }

    [Fact]
    public void AnExistingWrongComposedViewRequestIsRepairedInPlace()
    {
        var arguments = new List<string>
        {
            "-view_ids", "0",
            "-i", Marker(ProfileIds.SideBySideHalf),
            "-map", "0:v", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p",
                "-map", "0:v", "playlist.m3u8"
            },
            result.Arguments);
        Assert.Single(result.Arguments, token => token == "-view_ids");
    }

    [Fact]
    public void AComposedViewRequestGluedToItsOptionIsRepairedInPlace()
    {
        var arguments = new List<string>
        {
            "-view_ids=2",
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois),
            "-map", "0:v", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids=-1",
                "-i", SourcePath,
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p",
                "-map", "0:v", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AComposedViewOptionWithoutAValueRefusesTheRewriteRatherThanGuess()
    {
        var arguments = new List<string>
        {
            "-view_ids",
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "0:v", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.UnsupportedCommandShape, result.Status);
        Assert.Empty(result.Arguments);
    }

    [Theory]
    [InlineData(ProfileIds.SideBySideFull)]
    [InlineData(ProfileIds.SideBySideHalf)]
    [InlineData(ProfileIds.AnaglyphRedCyanDubois)]
    [InlineData(ProfileIds.CustomGrayscale)]
    public void ARewrittenCommandNeverCarriesAViewSpecifierAlongsideTheComposedInput(string profileId)
    {
        var arguments = new List<string>
        {
            "-i", Marker(profileId),
            "-map", "0:v:view:all", "-map", "0:a", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Contains("-view_ids", result.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("0:v:view", string.Join(' ', result.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void AViewSpecifierOptionalMapKeepsItsOptionalSuffixOnTheMarkerVideoStream()
    {
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideHalf, videoStreamIndex: 7),
            "-map", "0:v:vidx:1?", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p",
                "-map", "0:7?", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);
    }

    // ----- hardware acceleration passes through --------------------------------------

    // A server with hardware acceleration on writes two decisions into one command line: the device
    // and encoder the picture is encoded on, and the accelerator it attaches to an input. Neither one
    // is Anaglyfin's to change. The FFmpeg-mvc build that opens a marker input decides the decode per
    // decoder context - an MVC or multiview stream is decoded in software by that build, an ordinary
    // stream keeps the hardware decode the server asked for - and the hardware encode works
    // independently of that answer. A rewrite therefore lands its own fragments around the server's
    // arguments and deletes none of them, in the marker input's scope or anywhere else.
    //
    // The commands below are written in the shape Jellyfin's EncodingHelper builds them in, one token
    // per argument: the device initialisation and the upload filters belonging to the encode, the
    // accelerator selection belonging to the decode, and the marker input standing among them. What
    // every expectation below has to show is that the only token appearing on the input side of that
    // "-i" which the server did not write is the composed view request.

    [Fact]
    public void TheQuickSyncDecodeArgumentsSurviveTheRewriteWhereTheServerWroteThem()
    {
        // The command a Quick Sync server builds for a version it is encoding on the GPU: the qsv
        // device opened for the encoder and the filter graph, the qsv accelerator attached to the
        // input, and the upload of the decoded frame to that device. Every one of those reaches
        // FFmpeg; the profile adds its composed view request and nothing else.
        var arguments = new List<string>
        {
            "-analyzeduration", "3000000",
            "-probesize", "10000000",
            "-init_hw_device", "qsv=qsv:/dev/dri/renderD128",
            "-filter_hw_device", "qsv",
            "-hwaccel", "qsv",
            "-hwaccel_output_format", "qsv",
            "-i", Marker(ProfileIds.SideBySideFull, videoStreamIndex: 0),
            "-map", "0:0", "-map", "0:1",
            "-c:v:0", "h264_qsv", "-preset:v", "veryfast", "-global_quality", "23",
            "-vf", "format=nv12,hwupload=derive_device=qsv,scale_qsv=1920:1080:async_depth=1",
            "-f", "hls", "-hls_time", "6", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal(
            new[]
            {
                "-analyzeduration", "3000000",
                "-probesize", "10000000",

                // Device initialisation, filter device, and the accelerator selection of this input:
                // three arguments the server chose, none of them this wrapper's to second-guess.
                // FFmpeg-mvc opens the multiview stream of this particular input with its own
                // software decoder whatever "-hwaccel" says, and an ordinary stream in the same
                // command keeps the hardware decode the server asked for.
                "-init_hw_device", "qsv=qsv:/dev/dri/renderD128",
                "-filter_hw_device", "qsv",
                "-hwaccel", "qsv",
                "-hwaccel_output_format", "qsv",

                // The composed request is the one token added here, and it still lands immediately
                // in front of the option that opens this file - after everything the server wrote in
                // that scope, not in place of it.
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "0:0", "-map", "0:1",
                "-c:v:0", "h264_qsv", "-preset:v", "veryfast", "-global_quality", "23",
                "-vf", "format=nv12,hwupload=derive_device=qsv,scale_qsv=1920:1080:async_depth=1",
                "-f", "hls", "-hls_time", "6", "playlist.m3u8"
            },
            result.Arguments);

        // No software fallback written over the server's encoder either: Anaglyfin names no codec,
        // so the hardware encode the server picked is what the command still carries.
        Assert.DoesNotContain("libx264", string.Join(' ', result.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCudaDecodeArgumentsAndTheCudaEncodeBothSurviveTheRewrite()
    {
        // NVENC with CUDA decode is where the server's two decisions sit closest together: it writes
        // the accelerator, its output format, its flags and a thread cap in one breath, and the
        // profile's chain has to land in front of the upload chain without disturbing any of them.
        var arguments = new List<string>
        {
            "-init_hw_device", "cuda=cuda:0",
            "-filter_hw_device", "cuda",
            "-hwaccel", "cuda",
            "-hwaccel_output_format", "cuda",
            "-hwaccel_flags", "+unsafe_output",
            "-threads", "1",
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois, videoStreamIndex: 0),
            "-map", "0:0",
            "-c:v", "h264_nvenc", "-preset", "p4",
            "-vf", "scale=1920:1080:flags=area,format=nv12,hwupload=extra_hw_frames=64",
            "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-init_hw_device", "cuda=cuda:0",
                "-filter_hw_device", "cuda",
                "-hwaccel", "cuda",
                "-hwaccel_output_format", "cuda",
                "-hwaccel_flags", "+unsafe_output",

                // The thread cap came in beside the accelerator and is an input choice of the
                // server's; the accelerator itself is one too.
                "-threads", "1",
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "0:0",
                "-c:v", "h264_nvenc", "-preset", "p4",

                // The profile's conversion in front, the server's upload chain behind it, in one
                // chain: exactly what the server's hardware encode is fed, and exactly the chain it
                // wrote for itself.
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p,scale=1920:1080:flags=area,format=nv12,hwupload=extra_hw_frames=64",
                "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void TheVaapiDeviceAndItsDecodeArgumentsAllSurviveTheRewrite()
    {
        // VA-API spells its device selection twice over in one command, and Jellyfin writes both:
        // "-vaapi_device" as the legacy form of the encode's device, and "-hwaccel" for the decode.
        // cp17 removed the second family and kept the first, on the reasoning that only the encode
        // matters here; the corrected rule keeps the whole command, because which of these two
        // FFmpeg-mvc honours is a question about the decoder it is opening, not about the profile in
        // the path.
        var arguments = new List<string>
        {
            "-vaapi_device", "/dev/dri/renderD128",
            "-init_hw_device", "vaapi=vaapi:/dev/dri/renderD128",
            "-filter_hw_device", "vaapi",
            "-hwaccel", "vaapi",
            "-hwaccel_output_format", "vaapi",
            "-i", Marker(ProfileIds.SideBySideHalf, videoStreamIndex: 0),
            "-map", "0:0", "-map", "0:1",
            "-c:v", "h264_vaapi", "-bf", "0",
            "-vf", "format=nv12,hwupload=extra_hw_frames=64,scale_vaapi=w=1920:h=1080",
            "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-vaapi_device", "/dev/dri/renderD128",
                "-init_hw_device", "vaapi=vaapi:/dev/dri/renderD128",
                "-filter_hw_device", "vaapi",
                "-hwaccel", "vaapi",
                "-hwaccel_output_format", "vaapi",
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "0:0", "-map", "0:1",
                "-c:v", "h264_vaapi", "-bf", "0",
                "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p,format=nv12,hwupload=extra_hw_frames=64,scale_vaapi=w=1920:h=1080",
                "playlist.m3u8"
            },
            result.Arguments);
    }

    [Theory]
    [InlineData("-hwaccel", "qsv")]
    [InlineData("-hwaccel=qsv", null)]
    [InlineData("-hwaccel_output_format", "qsv")]
    [InlineData("-hwaccel_output_format:v", "qsv")]
    [InlineData("-hwaccel_device", "0")]
    [InlineData("-hwaccel_device=0", null)]
    [InlineData("-hwaccel_args", "-extra_hw_frames 64")]
    [InlineData("-hwaccel_flags", "+allow_profile_mismatch")]
    [InlineData("-vaapi_device", "/dev/dri/renderD128")]
    [InlineData("-init_hw_device", "vaapi=vaapi:/dev/dri/renderD128")]
    [InlineData("-filter_hw_device", "vaapi")]
    public void EverySpellingOfAHardwareAcceleratorOptionSurvivesTheRewriteExactly(string option, string? value)
    {
        // One option at a time, in every spelling a server or an administrator can write it in -
        // bare, with its value glued to it, with a stream specifier behind its name - because the
        // rule is that none of them is removed, and a rule stated as a name list is exactly the rule
        // that comes back when somebody reintroduces a name list. The whole expected vector is
        // written out each time: the server's argument, its value, the composed request, the
        // rewritten input, and nothing else on the input side of the "-i".
        var arguments = new List<string> { option };

        if (value is not null)
        {
            arguments.Add(value);
        }

        arguments.Add("-i");
        arguments.Add(Marker(ProfileIds.SideBySideFull));
        arguments.Add("-map");
        arguments.Add("0:v");
        arguments.Add("playlist.m3u8");

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);

        // The received option and its value, then the composed request, then the rewritten input:
        // an option glued to its value is one token and takes the second slot for itself.
        var expected = value is null
            ? new[] { option, "-view_ids", "-1", "-i", SourcePath, "-map", "0:v", "playlist.m3u8" }
            : new[] { option, value, "-view_ids", "-1", "-i", SourcePath, "-map", "0:v", "playlist.m3u8" };

        Assert.Equal(expected, result.Arguments);

        // The input the whole rewrite addresses is still the input it was, and still the marker's
        // real file rather than the marker.
        Assert.Single(result.Arguments, token => token == WrapperArgumentRewriter.InputFileArgument);
        Assert.Equal(SourcePath, InputOf(result.Arguments));
    }

    [Fact]
    public void TheTwoDimensionalBaseProfileWritesNothingIntoAHardwareAcceleratedCommand()
    {
        // The profile that converts nothing writes no filter and no view request, and it is also the
        // clearest case for the pass-through: on this command the rewrite is the marker replacement
        // and nothing else, so every accelerator argument the server wrote - for the decode or for
        // the encode - is standing where it stood when it arrived.
        var arguments = new List<string>
        {
            "-vaapi_device", "/dev/dri/renderD128",
            "-hwaccel", "vaapi",
            "-hwaccel_output_format", "vaapi",
            "-i", Marker(ProfileIds.TwoDBase, videoStreamIndex: 0),
            "-map", "0:0", "-map", "0:1",
            "-c:v", "h264_vaapi",
            "-vf", "format=nv12,hwupload=extra_hw_frames=64",
            "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal(
            new[]
            {
                "-vaapi_device", "/dev/dri/renderD128",
                "-hwaccel", "vaapi",
                "-hwaccel_output_format", "vaapi",
                "-i", SourcePath,
                "-map", "0:0", "-map", "0:1",
                "-c:v", "h264_vaapi",
                "-vf", "format=nv12,hwupload=extra_hw_frames=64",
                "playlist.m3u8"
            },
            result.Arguments);
        Assert.DoesNotContain(WrapperArgumentRewriter.ComposedViewInputArgument, result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void AHwuploadTheServerWroteIntoItsOwnGraphSurvivesTheMergedConversion()
    {
        // The merge rewrites which label the server's chains read; it does not rewrite the chains.
        // The upload that feeds the hardware encoder is in the middle of one of them here, and the
        // frame it uploads is the profile's converted picture by the time it runs. The input's
        // accelerator option is not part of that graph and is not part of the merge either.
        var arguments = new List<string>
        {
            "-init_hw_device", "qsv=qsv:/dev/dri/renderD128",
            "-filter_hw_device", "qsv",
            "-hwaccel", "qsv",
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois, videoStreamIndex: 0),
            "-filter_complex", "[0:0]scale=1920:1080:flags=area,format=nv12,hwupload=derive_device=qsv[v]",
            "-map", "[v]", "-map", "0:1", "-c:v", "h264_qsv", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-init_hw_device", "qsv=qsv:/dev/dri/renderD128",
                "-filter_hw_device", "qsv",
                "-hwaccel", "qsv",
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex",
                "[0:0]stereo3d=sbsl:arcd,format=yuv420p[anaglyfin_profile];"
                + "[anaglyfin_profile]scale=1920:1080:flags=area,format=nv12,hwupload=derive_device=qsv[v]",
                "-map", "[v]", "-map", "0:1", "-c:v", "h264_qsv", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AHardwareAcceleratedCommandWithNoMarkerKeepsItsHardwareDecode()
    {
        // Ordinary library playback is the server's own business, decode included: no marker, no
        // rewrite, and the accelerator selection of an item Anaglyfin has nothing to say about is
        // handed to FFmpeg exactly as the server wrote it. This is the half of the rule the server
        // notices most: an ordinary 2D stream that stops being hardware decoded because a plugin
        // touched the command line is a regression on every item in the library.
        var arguments = new List<string>
        {
            "-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi",
            "-i", "/library/movie.mkv", "-c:v", "hevc_vaapi", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.PassedThrough, result.Status);
        Assert.Equal(arguments.ToArray(), result.Arguments);
    }

    [Fact]
    public void AHwAcceleratedCommandThatCopiesItsVideoIsStillRefused()
    {
        // Passing the hardware arguments through does not turn a copy into an encode: the server
        // that asked for a copy asked for no converted picture on this output, and that refusal is
        // taken before anything is spliced, accelerator arguments present or not.
        var arguments = new List<string>
        {
            "-init_hw_device", "qsv=qsv:/dev/dri/renderD128",
            "-hwaccel", "qsv",
            "-hwaccel_output_format", "qsv",
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "0:v", "-map", "0:a",
            "-c:v", "copy", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.ServerChoseVideoCopy, result.Status);
        Assert.Empty(result.Arguments);
    }

    // ----- linear profile filters ----------------------------------------------------

    [Fact]
    public void HalfSideBySideInsertsItsScaleFilterWhenTheCommandHasNoVideoFilter()
    {
        var result = _rewriter.Rewrite(JellyfinLikeCommand(Marker(ProfileIds.SideBySideHalf)));

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p",
                "-map", "0:v", "-map", "0:a", "-c:v", "libx264", "-c:a", "copy",
                "-f", "hls", "-hls_time", "6", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void RedCyanRunsItsConversionInFrontOfTheVideoFilterChainTheServerAlreadyAskedFor()
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois),
            "-vf", "scale=1920:1080",
            "-map", "0:v", "-map", "0:a", "-c:v", "libx264",
            "-f", "hls", "-hls_time", "6", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-view_ids", "-1",
                "-i", SourcePath,

                // One -vf, comma-chained at the position the server wrote it, with the profile
                // conversion first and the server's filters behind it. The server's ordinary
                // video map stays: the conversion runs on the composed frames delivered by that
                // stream, so it is the profile's output carrier rather than a competing map.
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p,scale=1920:1080",
                "-map", "0:v", "-map", "0:a", "-c:v", "libx264",
                "-f", "hls", "-hls_time", "6", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void TheLastVideoFilterChainOnTheCommandIsTheOneTheProfileExtends()
    {
        // FFmpeg keeps the last value of a repeated option, so extending the first one would be
        // writing into a chain that never runs.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois),
            "-vf", "scale=1920:1080", "-vf", "format=nv12",
            "-map", "0:v", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf", "scale=1920:1080",
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p,format=nv12",
                "-map", "0:v", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Theory]
    [InlineData(ProfileIds.AnaglyphRedCyanDubois, "stereo3d=sbsl:arcd,format=yuv420p")]
    [InlineData(ProfileIds.SideBySideHalf, "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p")]
    public void TheProfileConvertsBeforeTheServerScales(string profileId, string profileFilter)
    {
        // The order this asserts is the whole reason the wrapper touches the chain at all. The
        // server writes its scale from the ceilings the request settled on, and those default to
        // the width and height this source reports - the converted frame - while the scale itself
        // is evaluated at run time against whatever frame reaches it. With the conversion behind
        // the chain, a full-SBS version reaches that scale as 3840x1080 and leaves it as
        // 1920x540: the server shrank a picture the profile had not produced yet, and the
        // profile's own work then landed on the shrunken one.
        var serverChain = "fps=24,scale=trunc(min(max(iw\\,ih*1.7778)\\,min(1920\\,1080*1.7778))/2)*2:trunc(min(max(iw/1.7778\\,ih)\\,min(1920/1.7778\\,1080))/2)*2,format=yuv420p";

        var arguments = new List<string>
        {
            "-i", Marker(profileId),
            "-map", "0:v", "-map", "0:a",
            "-vf", serverChain,
            "-c:v", "libx264", "-b:v", "8000k", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        var merged = ValueAfter(result.Arguments, WrapperArgumentRewriter.VideoFilterArgument);

        // The profile's conversion is the head of the chain, the server's chain survives behind
        // it byte for byte, and the two are one option rather than a repeated -vf whose first
        // value FFmpeg would ignore.
        Assert.StartsWith(profileFilter + ",", merged, StringComparison.Ordinal);
        Assert.Equal(profileFilter + "," + serverChain, merged);
        Assert.Single(result.Arguments, token => token == WrapperArgumentRewriter.VideoFilterArgument);

        // The quality arguments living outside -vf are the server's and stay untouched.
        Assert.Contains("-b:v", result.Arguments, StringComparer.Ordinal);
        Assert.Contains("8000k", result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void AProfileThatConvertsNothingLeavesTheServersChainAlone()
    {
        // 2D base has no chain of its own to put in front, so the server's filters run exactly as
        // written - the same decoder frame they were sized for.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.TwoDBase),
            "-vf", "scale=1920:1080,subtitles=filename='/movies/eng.srt'",
            "-map", "0:v", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-vf", "scale=1920:1080,subtitles=filename='/movies/eng.srt'",
                "-map", "0:v", "playlist.m3u8"
            },
            result.Arguments);
    }

    // ----- subtitles -----------------------------------------------------------------

    [Fact]
    public void ASuppressedSubtitleTrackBecomesOneSnAndNoSubtitleMaps()
    {
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois, subtitleOrdinal: 2),
            "-map", "0:v", "-map", "0:a", "-map", "0:s:0", "-map", "0:s",
            "-c:v", "libx264", "-c:s", "copy", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p," + BurnInTwo, "-sn",
                "-map", "0:v", "-map", "0:a", "-c:v", "libx264", "-c:s", "copy",
                "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AConvertedPictureWithoutABurnInLeavesTheSubtitleSelectionToTheServer()
    {
        // Converting the picture is not an answer to the question of what text is on it. The
        // server picked these subtitle streams, mapped them, and hands them to the client - and
        // a wrapper that muted them anyway was the bug this rule retires: nothing double-renders
        // the text, because nothing here renders it at all.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "0:v", "-map", "0:a", "-map", "0:s", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.DoesNotContain("-sn", result.Arguments, StringComparer.Ordinal);
        Assert.Contains("0:s", result.Arguments, StringComparer.Ordinal);
        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "0:v", "-map", "0:a", "-map", "0:s", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AnExclusionMapIsNobodysSubtitleStreamAndSurvivesSubtitleSuppression()
    {
        // "-map -0:s" is the server taking subtitle streams out of the output, which is the
        // same direction -sn travels: removing that map would answer "no subtitles" by putting
        // them back. An exclusion names no stream for this rewriter to own, so it stays - as do
        // an audio exclusion and the bare whole-file one. A positive subtitle map beside them
        // is a stream the server did select, and still gives way.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull, subtitleOrdinal: 0),
            "-map", "0:v", "-map", "0:a", "-map", "0:s:0",
            "-map", "-0:s", "-map", "-0:a", "-map", "-0",
            "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf", BurnInZero, "-sn",
                "-map", "0:v", "-map", "0:a",
                "-map", "-0:s", "-map", "-0:a", "-map", "-0",
                "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AnExclusionOfTheVideoTypeIsStillTheProfilesPictureBeingTakenAway()
    {
        // The one exclusion a profile-owned pipeline does remove. What this rewriter inserts
        // lands immediately after the last input, ahead of the server's own maps, so a
        // "-map -0:v" left standing would subtract the views the profile had just mapped and
        // run FFmpeg against an output with no video in it.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "0:a", "-map", "-0:v", "-c:v", "libx264", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "0:a", "-c:v", "libx264", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AnSnAlreadyOnTheCommandIsNotDuplicated()
    {
        // The burn-in asks for suppression, but the server had already said it: a second -sn
        // on one output is a duplicate, not a second decision.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull, subtitleOrdinal: 0), "-sn", "-map", "0:v", "-map", "0:a", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(1, result.Arguments.Count(argument => argument == "-sn"));
    }

    // ----- custom grayscale ------------------------------------------------------------

    [Fact]
    public void CustomGrayscaleInsertsItsGraphItsMapAndItsSubtitleSuppression()
    {
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.CustomGrayscale, subtitleOrdinal: 0),
            "-map", "0:v", "-map", "0:a", "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(ProfileIds.CustomGrayscale, result.ProfileId);
        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", CustomGraph,
                "-map", "[anaglyfin_custom]",

                // The graph output is its own mapped stream, so the burn-in is a plain
                // -vf behind the map instead of another stage inside the graph. The server's
                // video map gives way to the label; audio remains the server's choice.
                "-vf", BurnInZero, "-sn",
                "-map", "0:a", "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void TheCustomGrayscaleGraphIsNeverReorderedIntoTheServersChain()
    {
        // The linear profiles get in front of the server's chain because their conversion IS a
        // stage of that chain. This profile's conversion is a filtergraph of its own, and a graph
        // has no position inside somebody else's linear chain: it is written as its own
        // -filter_complex option, the server's -vf is left byte for byte alone, and nothing is
        // quietly re-plumbed on the assumption that two graphs can be interleaved. (FFmpeg is
        // blunter still than the ordering question: a stream fed from a complex graph takes no
        // simple -vf at all - "Simple and complex filtering cannot be used together for the same
        // stream" - which is a limitation of this profile's shape rather than of the merge, and
        // is why its burn-in travels behind the mapped label as its own stage.)
        var serverChain = "scale=trunc(min(max(iw\\,ih*1.7778)\\,min(1920\\,1080*1.7778))/2)*2:trunc(min(max(iw/1.7778\\,ih)\\,min(1920/1.7778\\,1080))/2)*2";

        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.CustomGrayscale),
            "-map", "0:v", "-map", "0:a",
            "-vf", serverChain,
            "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);

        // The graph stands alone, and it carries no text from the server's chain.
        var graph = ValueAfter(result.Arguments, WrapperArgumentRewriter.FilterComplexArgument);
        Assert.Equal(CustomGraph, graph);
        Assert.DoesNotContain("scale=trunc", graph, StringComparison.Ordinal);

        // The server's chain survives unchanged, and the profile's map still precedes it.
        Assert.Equal(serverChain, ValueAfter(result.Arguments, WrapperArgumentRewriter.VideoFilterArgument));
        Assert.True(
            result.Arguments.ToList().IndexOf("-map") < result.Arguments.ToList().IndexOf("-vf"),
            string.Join(' ', result.Arguments));
    }

    // ----- 2D base ---------------------------------------------------------------------

    [Fact]
    public void PlainTwoDimensionalOnlyResolvesTheMarkerAndChangesNothingElse()
    {
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.TwoDBase),
            "-map", "0:v", "-map", "0:a", "-map", "0:s",
            "-vf", "scale=1920:1080",
            "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal(ProfileIds.TwoDBase, result.ProfileId);
        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-map", "0:v", "-map", "0:a", "-map", "0:s",
                "-vf", "scale=1920:1080",
                "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void PlainTwoDimensionalKeepsAFilterGraphItDidNotAskFor()
    {
        // The 2D version owns nothing: the server's own pipeline still delivers its own
        // picture, so there is no reason to refuse the command.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.TwoDBase),
            "-filter_complex", ForeignGraph, "-map", "[v]", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal(ForeignGraph, result.Arguments[3]);
    }

    // ----- stream selection on a map-less command ------------------------------------------

    [Fact]
    public void AMapLessLinearProfileCommandKeepsFFmpegsAutomaticStreamSelection()
    {
        var result = _rewriter.Rewrite(MapLessTranscode(Marker(ProfileIds.SideBySideFull)));

        // A -map is how FFmpeg is told which streams an output carries. This profile converts
        // the composed picture delivered by the stream that selection already chose, so it
        // writes no stream name at all and does not disturb the automatic video+audio pick -
        // and with no burn-in to render, not an output option at all.
        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,

                // The server's encoder, muxer and output choices, untouched.
                "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AMapLessGraphProfileAlsoNamesTheAudioItWouldLeaveBehind()
    {
        var result = _rewriter.Rewrite(MapLessTranscode(Marker(ProfileIds.CustomGrayscale)));

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);

        var mapOptionIndexes = Enumerable.Range(0, result.Arguments.Count)
            .Where(index => result.Arguments[index] == "-map")
            .ToArray();

        // Exactly two maps: the graph output and the audio. The rewrite invents no third
        // stream and, with no burn-in to render, no subtitle decision of its own.
        Assert.Equal(2, mapOptionIndexes.Length);
        Assert.Equal("[anaglyfin_custom]", result.Arguments[mapOptionIndexes[0] + 1]);
        Assert.Equal("0:a?", result.Arguments[mapOptionIndexes[1] + 1]);
        Assert.DoesNotContain("-sn", result.Arguments, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(ProfileIds.SideBySideFull)]
    [InlineData(ProfileIds.SideBySideHalf)]
    [InlineData(ProfileIds.AnaglyphRedCyanDubois)]
    public void AMapLessLinearProfileInventsNoMapForFFmpegsAutomaticSelection(string profileId)
    {
        var result = _rewriter.Rewrite(MapLessTranscode(Marker(profileId)));

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);

        // A linear conversion rides the stream that FFmpeg picked automatically. Naming any
        // stream here would replace that pick, including its audio, and is not required by a
        // filter that runs on whatever video the output already has.
        Assert.DoesNotContain("-map", result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void AMapLessCommandRewrittenThroughAFilterGraphNamesItsAudioToo()
    {
        var result = _rewriter.Rewrite(MapLessTranscode(Marker(ProfileIds.CustomGrayscale)));

        // The graph profile maps a label instead of a view specifier, but the audio question
        // is the same question: a named video stream leaves FFmpeg nothing else to put in
        // the output.
        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", CustomGraph,
                "-map", "[anaglyfin_custom]", "-map", "0:a?",
                "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void PlainTwoDimensionalOnAMapLessCommandStillNamesNoStreamAtAll()
    {
        var result = _rewriter.Rewrite(MapLessTranscode(Marker(ProfileIds.TwoDBase)));

        // Nothing is inserted, so nothing displaces FFmpeg's own selection and the audio
        // that selection delivers is already in the output. An invented map here would be
        // the rewrite deciding streams for a command it did not touch.
        Assert.Equal(
            new[] { "-i", SourcePath, "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "playlist.m3u8" },
            result.Arguments);
        Assert.DoesNotContain("-map", result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void ACommandThatAlreadyChoseItsAudioIsNotGivenASecondAudioMap()
    {
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "0:v", "-map", "0:a",
            "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        // Maps on the command are the server's own stream selection. This linear profile runs
        // on the stream the server named and therefore keeps the video map too; no optional
        // audio map is invented next to the server's own - the command already said which
        // streams it wants.
        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "0:v", "-map", "0:a",
                "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
        Assert.DoesNotContain("0:a?", result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void ACommandThatMappedOnlyVideoKeepsMappingOnlyVideo()
    {
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois),
            "-map", "0:v", "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        // A map carrying no audio is a command that already chose a video-only output. The
        // linear profile rides that same stream rather than naming another one; undoing the
        // server's choice is not this rewriter's call, so inventing "-map 0:a?" here would
        // change what the server asked to mux.
        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p",
                "-map", "0:v",
                "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
        Assert.DoesNotContain("0:a?", result.Arguments, StringComparer.Ordinal);
    }

    // ----- multi-input commands and argument placement -----------------------------------

    [Fact]
    public void ProfileArgumentsLandAfterTheLastInputAndNeverInsideAnotherInputsOptions()
    {
        // "-f srt" belongs to the subtitle input that follows it, and an output option
        // written before an input would be read as that input's option. So the profile's
        // arguments go after the last input, and the input-scope options before it are
        // left exactly where they were.
        var arguments = new List<string>
        {
            "-hide_banner",
            "-i", Marker(ProfileIds.SideBySideFull),
            "-f", "srt", "-i", "/movies/Movie (2010)/Movie.en.srt",
            "-map", "0:v", "-map", "0:a", "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-hide_banner",
                "-view_ids", "-1",
                "-i", SourcePath,
                "-f", "srt", "-i", "/movies/Movie (2010)/Movie.en.srt",
                "-map", "0:v", "-map", "0:a", "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AudioEncoderMuxerAndOutputArgumentsSurviveAProfileRewrite()
    {
        var result = _rewriter.Rewrite(JellyfinLikeCommand(Marker(ProfileIds.AnaglyphRedCyanDubois)));

        var tail = new[] { "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "-hls_time", "6", "playlist.m3u8" };

        Assert.Equal(tail, result.Arguments.Skip(result.Arguments.Count - tail.Length).ToArray());
        Assert.Contains("-map", result.Arguments, StringComparer.Ordinal);
        Assert.Contains("0:a", result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void MapsOfStreamsTheProfileDoesNotCompeteWithAreKept()
    {
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "0:v", "-map", "0:a:0", "-map", "0:V", "-map", "v", "-map", "0", "-map", "-0",
            "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        // A linear profile maps no video of its own, so the server's ordinary video map and
        // its other stream choices stay exactly as written. A whole-file map ("0") and its
        // exclusion ("-0") name no stream type at all, so removing them would drop audio as
        // readily as video.
        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "0:v", "-map", "0:a:0", "-map", "0:V", "-map", "v",
                "-map", "0", "-map", "-0",
                "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AViewSpecifierVideoMapBecomesTheOrdinaryStreamTheComposedDecoderFills()
    {
        // FFmpeg-mvc refuses a view specifier on an input configured through -view_ids, so the
        // map left behind after the composed input is written cannot keep its view selection.
        // The marker named no stream index, so the rewrite keeps the same video type without
        // the detail.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois),
            "-map", "0:v:view:all", "-map", "0:a", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p",
                "-map", "0:v", "-map", "0:a", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void ALinearProfileLeavesASomebodyElsesLabelledVideoMapToTheServer()
    {
        // A label is not a map of the marker input's ordinary stream, and a linear profile
        // inserts no competing video map. It is not the wrapper's job to guess whose label it
        // is without the graph that produced it; the profile only asks its input for the
        // composed view and converts the picture that stream carries.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "[v]", "-map", "0:a", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "[v]", "-map", "0:a", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void ACommandWhoseInputOptionCarriesNoValueIsPassedThrough()
    {
        // There is nothing to classify, and this is not the wrapper's typo to fix: FFmpeg's
        // own argument check reports it.
        var arguments = new List<string> { "-hide_banner", "-loglevel", "warning", "-i" };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.PassedThrough, result.Status);
        Assert.Equal(arguments.ToArray(), result.Arguments);
    }

    // ----- the server chose video copy ---------------------------------------------------

    [Theory]
    [InlineData("-c", "copy")]
    [InlineData("-codec", "copy")]
    [InlineData("-vcodec", "copy")]
    [InlineData("-c:v", "copy")]
    [InlineData("-c:v:0", "copy")]
    [InlineData("-codec:v", "copy")]
    [InlineData("-codec:v:0", "copy")]
    [InlineData("-c:V", "copy")]
    [InlineData("-c:2", "copy")]
    public void AProfileThatOwnsThePipelineRefusesACommandThatCopiesItsVideo(string option, string value)
    {
        // Every spelling of "do not encode this" reaches the same decision. Running the
        // command anyway would put "-view_ids -1" and a profile filter chain onto a command
        // whose video is streamed through the muxer untouched - a player would be shown the
        // plain base view of the film under a version label that promised an anaglyph, and
        // nothing in the log would say so.
        var arguments = CommandCopyingVideo(option, value, Marker(ProfileIds.SideBySideFull));

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.ServerChoseVideoCopy, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Arguments);
        Assert.False(string.IsNullOrEmpty(result.Error));

        // The refusal is about the shape of the command, not its secrets: no marker text and
        // no media path in the line an administrator will read.
        Assert.DoesNotContain("127.0.0.1", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("source=", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Movie.2010.3D.mkv", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStreamCopyCommandTheServerBuildsForAVersionsSourceIsRefused()
    {
        // The shape Jellyfin 12 actually writes when it decides to stream-copy a dynamic
        // source's video: the map is numeric, the copy option is the long "-codec:v:0"
        // spelling, and the encode arguments (bitrate, preset, keyframes) are simply absent,
        // because the server only emits them in its encode branch. There is nothing here for
        // a profile to convert, and nothing here to replace "copy" with either - writing an
        // encoder stack would be the wrapper making Jellyfin's encoding decisions for it.
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois, videoStreamIndex: 0),
            "-map", "0:0", "-map", "0:1",
            "-codec:v:0", "copy", "-start_at_zero",
            "-codec:a:0", "copy",
            "-f", "hls", "-hls_time", "6", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.ServerChoseVideoCopy, result.Status);
        Assert.Empty(result.Arguments);
    }

    [Theory]
    [InlineData("-c:a", "copy")]
    [InlineData("-c:a:0", "copy")]
    [InlineData("-codec:a", "copy")]
    [InlineData("-c:s", "copy")]
    [InlineData("-c:d", "copy")]
    [InlineData("-c:t", "copy")]
    public void ACopyOfSomebodyElsesStreamIsNotAreasonToRefuseAVersion(string option, string value)
    {
        // Audio copy is what normal Jellyfin playback does with an AAC track, and the product
        // requirement is that an Anaglyfin version differs in picture and not in delivery.
        // A specifier that names a non-video type proves the copy cannot be the profile's.
        var arguments = CommandWithCodecOption(option, value, Marker(ProfileIds.SideBySideFull));

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.True(
            CarriesOptionAndValue(result.Arguments, option, value),
            $"the rewritten command lost or split '{option} {value}': {string.Join(' ', result.Arguments)}");
    }

    [Fact]
    public void ACodecCopyTheProfileCannotOwnIsLeftAlone()
    {
        // The plain 2D version inserts no map and no filter, so nothing of the command's
        // video pipeline belongs to it and nothing entitles it to refuse the server's choice.
        var arguments = CommandCopyingVideo("-codec:v:0", "copy", Marker(ProfileIds.TwoDBase));

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal(ProfileIds.TwoDBase, result.ProfileId);
        Assert.Equal(SourcePath, result.Arguments[4]);
        Assert.Contains("copy", result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void ACodecCopyWrittenBeforeTheInputIsNotTheOutputsDecision()
    {
        // Options before an input belong to that input. The profile's output segment starts
        // after the last input, and a copy choice made for a different file does not refuse
        // this job.
        var arguments = new List<string>
        {
            "-c", "copy",
            "-i", Marker(ProfileIds.SideBySideFull, videoStreamIndex: 0),
            "-map", "0:0", "-c:v", "libx264", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal("copy", result.Arguments[1]);
    }

    // ----- the server's numbered video map -----------------------------------------------

    [Fact]
    public void TheServersNumberedVideoMapMakesWayForTheGraphProfilesOwn()
    {
        // "-map 0:0" is Jellyfin naming the video stream by position, and the one profile that
        // maps a graph output cannot share the output with that source picture: the input
        // stream beside the graph's label would be an output FFmpeg either refuses or fills
        // with two videos. The marker said which numbered stream is this source's video, so the
        // audio map beside it - numbered too - stays.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.CustomGrayscale, videoStreamIndex: 0),
            "-map", "0:0", "-map", "0:1",
            "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", CustomGraphStartingAt("[0:0]"),
                "-map", "[anaglyfin_custom]",
                "-map", "0:1",
                "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void ANumberedVideoMapTheGraphProfileWroteAsOptionalIsRemovedLikeAnyOther()
    {
        // The trailing '?' means "if this file has one", not "this is a different stream". The
        // graph profile still has to remove that source video so its label is the only mapped
        // picture.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.CustomGrayscale, videoStreamIndex: 3),
            "-map", "0:3?", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", CustomGraphStartingAt("[0:3]"),
                "-map", "[anaglyfin_custom]",
                "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void ANumberedMapIsOnlyRemovedWhenItIsTheStreamTheGraphProfileNamed()
    {
        // A numbered map of another stream is somebody else's stream: audio, subtitles, or a
        // second video that is not this version's business. Guessing from the number alone
        // would drop the audio out of the playlist.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.CustomGrayscale, videoStreamIndex: 2),
            "-map", "0:0", "-map", "0:2", "-map", "0:10", "-map", "1:2", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", CustomGraphStartingAt("[0:2]"),
                "-map", "[anaglyfin_custom]",
                "-map", "0:0", "-map", "0:10", "-map", "1:2", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void ACommandWithNumberedMapsAndNoVideoToNameKeepsThemAll()
    {
        // A marker that names no video stream (an older provider, or a source with no video
        // stream to name) leaves the server's stream selection exactly as written. The copy
        // guard below is still what stops an inert profile pipeline from running; this rule is
        // only about which maps this rewriter is allowed to recognize.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "0:0", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "0:0", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void DroppingTheServersNumberedVideoMapForAGraphOutputDoesNotInventAnAudioMap()
    {
        // A command that mapped only video had already chosen a video-only output. Replacing
        // that map with the graph label is not a return to FFmpeg's automatic selection, so the
        // optional audio map this rewriter writes only for a command that never named a stream
        // still does not belong here.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.CustomGrayscale, videoStreamIndex: 0),
            "-map", "0:0", "-c:v", "libx264", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", CustomGraphStartingAt("[0:0]"),
                "-map", "[anaglyfin_custom]",
                "-c:v", "libx264", "playlist.m3u8"
            },
            result.Arguments);
        Assert.DoesNotContain("0:a?", result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void ANumberedMapAndItsLinearProfileRewriteSurviveTogetherInOnePass()
    {
        // The realistic Jellyfin shape after the provider reports an un-copyable codec: a real
        // encoder stack, numeric maps for both streams, and the profile's linear filter added
        // without disturbing the streams the server already named. The composed request sits in
        // front of the input, not beside the server's map.
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",
            "-i", Marker(ProfileIds.SideBySideHalf, videoStreamIndex: 0),
            "-map", "0:0", "-map", "0:1",
            "-codec:v:0", "libx264", "-preset:v", "medium", "-b:v", "8000k",
            "-codec:a:0", "copy",
            "-f", "hls", "-hls_time", "6", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p",
                "-map", "0:0", "-map", "0:1",
                "-codec:v:0", "libx264", "-preset:v", "medium", "-b:v", "8000k",
                "-codec:a:0", "copy",
                "-f", "hls", "-hls_time", "6", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void TheSharedInstanceIsWiredToTheBuiltInCatalogAndTheSharedBuilder()
    {
        var result = WrapperArgumentRewriter.Shared.Rewrite(JellyfinLikeCommand(Marker(ProfileIds.SideBySideHalf)));

        // The out-of-process executable has no container to ask, so Shared has to be a
        // complete rewriter on its own.
        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal(ProfileIds.SideBySideHalf, result.ProfileId);
    }

    // ----- the server's own filter graph ------------------------------------------------

    [Fact]
    public void ARedCyanConversionLandsAtTheHeadOfTheServersSubtitleGraph()
    {
        // The shape Jellyfin writes when it burns an image subtitle in itself: the subtitle
        // stream scaled into [sub], the source video through its own colour and scale chain
        // into [main], and the two overlaid. A profile that converts the picture joins this
        // graph as one more chain in front of [main] - it does not refuse the command, and it
        // does not touch the subtitle chain, the overlay, or the label the output maps.
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois, videoStreamIndex: 0),
            "-filter_complex", ServersSubtitleBurnGraph,
            "-map", "[out]", "-map", "0:1", "-map", "-0:s",
            "-c:v", "libx264", "-f", "hls", "-hls_time", "6", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal(
            "[0:0]stereo3d=sbsl:arcd,format=yuv420p[anaglyfin_profile];"
            + "[0:10]scale=1920:1080:flags=area[sub];"
            + "[anaglyfin_profile]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
            + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]",
            ValueAfter(result.Arguments, WrapperArgumentRewriter.FilterComplexArgument));

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-view_ids", "-1",
                "-i", SourcePath,

                // One -filter_complex, in the position the server wrote it, carrying the
                // profile's chain ahead of the server's. No -vf beside it, and no -sn: the
                // server renders these subtitles itself and the profile converts only the
                // picture they ride on.
                "-filter_complex",
                "[0:0]stereo3d=sbsl:arcd,format=yuv420p[anaglyfin_profile];"
                + "[0:10]scale=1920:1080:flags=area[sub];"
                + "[anaglyfin_profile]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
                + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[main];"
                + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]",
                "-map", "[out]", "-map", "0:1", "-map", "-0:s",
                "-c:v", "libx264", "-f", "hls", "-hls_time", "6", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AHalfSideBySideScaleRunsBeforeTheServersScaleInsideItsGraph()
    {
        // The reason the profile's chain goes at the head of the graph and not behind it: the
        // server's scale is sized against the frame the version reports, which is the converted
        // one. Behind, it would shrink the un-converted all-view frame into the box of a
        // picture that has not been made yet.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideHalf, videoStreamIndex: 0),
            "-filter_complex", ServersSubtitleBurnGraph,
            "-map", "[out]", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        var graph = ValueAfter(result.Arguments, WrapperArgumentRewriter.FilterComplexArgument);

        Assert.StartsWith(
            "[0:0]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p[anaglyfin_profile];",
            graph,
            StringComparison.Ordinal);
        Assert.Equal(
            "[0:0]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p[anaglyfin_profile];"
            + "[0:10]scale=1920:1080:flags=area[sub];"
            + "[anaglyfin_profile]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
            + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]",
            graph);

        // Half SBS maps nothing of its own: the converted frames arrive on the stream the
        // server's map already names.
        Assert.DoesNotContain("[anaglyfin_profile]", result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void AFullSideBySideRewriteLeavesTheServersGraphByteForByteAlone()
    {
        // Full SBS converts nothing after the decode composes the eyes, so it has no chain to
        // write into the server's graph: the composed frames simply arrive on the stream that
        // graph already reads. The rewrite is the input request, the marker's replacement, and
        // nothing else.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull, videoStreamIndex: 0),
            "-filter_complex", ServersSubtitleBurnGraph,
            "-map", "[out]", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", ServersSubtitleBurnGraph,
                "-map", "[out]", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void ASelectedSubtitleMapAndItsExclusionSurviveAMergedConversion()
    {
        // A server that carries a subtitle stream of its own said so twice - with the map that
        // selects it and the exclusion that shapes what that selects - and a picture conversion
        // has nothing to say to either. Both are on the command after the merge, and no -sn
        // travels with a command that converts but renders no text.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois, videoStreamIndex: 0),
            "-filter_complex", ServersSubtitleBurnGraph,
            "-map", "[out]", "-map", "0:1", "-map", "0:s:0", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", MergedWith(
                    "[0:0]stereo3d=sbsl:arcd,format=yuv420p[anaglyfin_profile]"),
                "-map", "[out]", "-map", "0:1", "-map", "0:s:0", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void TheCustomGrayscaleGraphIsSplicedInFrontOfTheServersGraphAndMapsNothingNew()
    {
        // The one profile that carries a graph of its own merges too: its graph is already a
        // chain ending in its own label, so the server's chains follow it and read that label.
        // The label is then the picture the server's chains feed the encoder, and mapping it
        // beside the output the server mapped would be a second video in the way of the first.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.CustomGrayscale, videoStreamIndex: 0),
            "-filter_complex", ServersSubtitleBurnGraph,
            "-map", "[out]", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex",
                CustomGraphStartingAt("[0:0]")
                + ";"
                + ServersSubtitleBurnGraph.Replace("[0:0]", "[anaglyfin_custom]", StringComparison.Ordinal),
                "-map", "[out]", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);

        // The profile's label is consumed by the graph, never mapped as an argument of its own,
        // and no -vf stands behind it either: the burn-in this profile would put there is the
        // one it was not asked for.
        Assert.DoesNotContain("[anaglyfin_custom]", result.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(WrapperArgumentRewriter.VideoFilterArgument, result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void AGraphThatNeverReadsThisInputsVideoRefusesTheConversion()
    {
        // A graph that reads only audio has no place for the profile's picture, and the profile
        // has nothing to feed: writing its chain into that text would leave a converted frame
        // unreferenced and the server's own picture untouched.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois, videoStreamIndex: 0),
            "-filter_complex", "[0:1]volume=2.0[a]",
            "-map", "0:0", "-map", "[a]", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.IncompatibleFilterGraph, result.Status);
    }

    [Fact]
    public void AGraphThatAlreadyCarriesTheProfilesLabelRefusesTheMerge()
    {
        // Two producers on one pad is a graph FFmpeg reports as unparseable and a log line that
        // says nothing about the wrapper. Refusing is what turns that into an explanation.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideHalf, videoStreamIndex: 0),
            "-filter_complex", "[0:0]scale=1920:1080[anaglyfin_profile];[anaglyfin_profile]format=yuv420p[out]",
            "-map", "[out]", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.IncompatibleFilterGraph, result.Status);
    }

    [Fact]
    public void TwoGraphsRefuseTheMergeBecauseTheOutputPictureIsAmbiguous()
    {
        // The vector cannot say which of two graphs feeds the output, and merging into only the
        // last one would leave the first reading the un-converted stream.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideHalf, videoStreamIndex: 0),
            "-filter_complex", "[0:0]scale=960:540[a]",
            "-filter_complex", "[0:0]scale=1920:1080[out]",
            "-map", "[out]", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(WrapperRewriteStatus.IncompatibleFilterGraph, result.Status);
    }

    [Fact]
    public void AProfileThatBurnsInLeavesSubtitleHandlingToAServerThatAlreadyRendersIt()
    {
        // The burn-in and the server's own renderer are two answers to one question, and the
        // answer that is not taken twice is the one the maps are not taken away for. The
        // profile still converts the picture; it just does not mute a server that is already
        // drawing the text.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois, subtitleOrdinal: 2),
            "-vf", "subtitles=filename='/movies/Movie (2010)/Movie.en.srt'",
            "-map", "0:v", "-map", "0:a", "-map", "0:s:0", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.DoesNotContain(WrapperArgumentRewriter.DisableSubtitlesArgument, result.Arguments, StringComparer.Ordinal);
        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf",
                "stereo3d=sbsl:arcd,format=yuv420p," + BurnInTwo
                + ",subtitles=filename='/movies/Movie (2010)/Movie.en.srt'",
                "-map", "0:v", "-map", "0:a", "-map", "0:s:0", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AProfileThatBurnsInLeavesSubtitleHandlingToAServerGraphThatAlreadyRendersThem()
    {
        // A graph reading [0:s] of the marker's input is the same claim as a subtitles filter
        // written on a chain: whoever wrote it has decided what the output's subtitles look
        // like, and it was not this rewrite.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideHalf, subtitleOrdinal: 0, videoStreamIndex: 0),
            "-filter_complex", "[0:s:0]ass[s];[0:0]scale=1920:1080[m];[m][s]overlay[out]",
            "-map", "[out]", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.DoesNotContain(WrapperArgumentRewriter.DisableSubtitlesArgument, result.Arguments, StringComparer.Ordinal);

        var graph = ValueAfter(result.Arguments, WrapperArgumentRewriter.FilterComplexArgument);
        Assert.Equal(
            "[0:0]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p,"
            + "subtitles=filename='/movies/Movie (2010)/Movie.2010.3D.mkv':si=0[anaglyfin_profile];"
            + "[0:s:0]ass[s];[anaglyfin_profile]scale=1920:1080[m];[m][s]overlay[out]",
            graph);
    }

    [Fact]
    public void AMarkerQuotedIntoTheServersSubtitleFilterBecomesTheSourcePath()
    {
        // The server builds its burn-in from the path of the file it is transcoding, and for an
        // Anaglyfin version that file is the marker. A marker reaching FFmpeg in a filter would
        // be opened as a subtitle file - so the sweep that keeps marker text out of the command
        // has to reach inside a filter value, not only the -i value.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois),
            "-vf", "subtitles=filename='" + Marker(ProfileIds.AnaglyphRedCyanDubois) + "'",
            "-map", "0:v", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-vf",
                "stereo3d=sbsl:arcd,format=yuv420p,subtitles=filename='/movies/Movie (2010)/Movie.2010.3D.mkv'",
                "-map", "0:v", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Theory]
    // The value as the graph pass would hand it to the option pass: the marker's own colons
    // escaped once.
    [InlineData(1)]

    // The value meant to survive both decode passes: those escapes escaped again.
    [InlineData(2)]
    public void AMarkerEscapedForAFilterValueIsReplacedByThePathEscapedTheSameWay(int depth)
    {
        var marker = Marker(ProfileIds.AnaglyphRedCyanDubois);
        var arguments = new List<string>
        {
            "-i", marker,
            "-vf", "scale=1920:1080,subtitles=filename=" + EscapedForFilter(marker, depth),
            "-map", "0:v", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        var chain = ValueAfter(result.Arguments, WrapperArgumentRewriter.VideoFilterArgument);

        // The source path needs no escaping of its own, so it lands plain - and the marker's
        // scheme colon, which is what the escaping was for, is gone with it.
        Assert.Equal(
            "stereo3d=sbsl:arcd,format=yuv420p,scale=1920:1080,subtitles=filename="
            + EscapedForFilter(SourcePath, depth),
            chain);
        Assert.DoesNotContain("127.0.0.1", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void AMarkerThatTravelledThroughAUrlSlotIsReplacedWhole()
    {
        // A value written out of a URL slot carries the whole marker percent-encoded, colons and
        // slashes included, and that encoding is what the wrapper has to match - not the marker
        // text the server never wrote.
        var marker = Marker(ProfileIds.SideBySideFull);
        var arguments = new List<string>
        {
            "-i", marker,
            "-metadata", "comment=" + Uri.EscapeDataString(marker),
            "-map", "0:v", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            "comment=" + Uri.EscapeDataString(SourcePath),
            result.Arguments[result.Arguments.ToList().IndexOf("-metadata") + 1]);
        Assert.DoesNotContain("127.0.0.1", string.Join(' ', result.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void NoMarkerTextSurvivesAnywhereInTheRewrittenCommand()
    {
        // The one property every other rule here serves: after a rewrite, no argument handed to
        // FFmpeg still says what the marker said - not in the -i, not in a filter, not in a
        // value this rewriter never looked at.
        var marker = Marker(ProfileIds.AnaglyphRedCyanDubois, videoStreamIndex: 0);
        var arguments = new List<string>
        {
            "-i", marker,
            "-filter_complex",
            ServersSubtitleBurnGraph + ",subtitles=filename=" + EscapedForFilter(marker, 2),
            "-metadata", "title=" + marker,
            "-map", "[out]", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        var joined = string.Join(' ', result.Arguments);
        Assert.DoesNotContain("127.0.0.1", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("anaglyfin/profile", joined, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(marker), joined, StringComparison.Ordinal);
        Assert.Contains(SourcePath, joined, StringComparison.Ordinal);
    }

    // ----- subtitle depth on the command ---------------------------------------------------

    [Fact]
    public void ARealImageSubtitleCommandCarriesDepthBetweenCompositionAndConversion()
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",
            "-i", Marker(ProfileIds.SideBySideHalf, videoStreamIndex: 0),
            "-filter_complex", ServersSubtitleBurnGraph,
            "-map", "[out]", "-map", "0:1", "-map", "-0:s",
            "-c:v", "libx264", "-f", "hls", "-hls_time", "6", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(
            arguments,
            new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0));

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Empty(result.Warnings);
        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "warning",

                // The composed decode, the marker's replacement, and the server's option stay in
                // their established places. Only the graph text inside the option changes.
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex",
                "[0:0]format=rgba[anaglyfin_composed];"
                + "[0:10]format=rgba[anaglyfin_subtitle];"
                + "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=auto:eof_action=pass[anaglyfin_depth];"
                + "[anaglyfin_depth]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p[anaglyfin_profile];"
                + "[anaglyfin_profile]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
                + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[out]",
                "-map", "[out]", "-map", "0:1", "-map", "-0:s",
                "-c:v", "libx264", "-f", "hls", "-hls_time", "6", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void ADepthSettingThatIsSwitchedOffWritesNoDepthArgumentAndNoWarning()
    {
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideHalf, videoStreamIndex: 0),
            "-filter_complex", ServersSubtitleBurnGraph,
            "-map", "[out]", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(
            arguments,
            new SubtitleDepthSettings(false, SubtitleDepthMode.Plane, 0, 17));

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", MergedWith(
                    "[0:0]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p[anaglyfin_profile]"),
                "-map", "[out]", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);

        Assert.DoesNotContain("mvcsubdepth", ValueAfter(result.Arguments, WrapperArgumentRewriter.FilterComplexArgument)!, StringComparison.Ordinal);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AProfileThatConvertsNothingKeepsTheServersGraphAndSaysTheDepthHadNothingToUse()
    {
        // The server's graph remains the server's graph: there is no conversion chain here to put
        // a depth stage in front of. The warning is not a failure - the film still plays, with the
        // flat subtitles the server already wrote - but without it an administrator would have no
        // way to distinguish "off" from "asked for, and not applicable".
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.TwoDBase),
            "-filter_complex", ServersSubtitleBurnGraph,
            "-map", "[out]", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(
            arguments,
            new SubtitleDepthSettings(true, SubtitleDepthMode.ConstantShift, -8, 0));

        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-filter_complex", ServersSubtitleBurnGraph,
                "-map", "[out]", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("converts no picture", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandWithNoMarkerIsNeitherRewrittenNorWarnedAbout()
    {
        var arguments = JellyfinLikeCommand("/library/movie.mkv");

        var result = _rewriter.Rewrite(
            arguments,
            new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0));

        Assert.Equal(WrapperRewriteStatus.PassedThrough, result.Status);
        Assert.Equal(arguments.ToArray(), result.Arguments);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AProfileWhoseOnlyConversionIsTheComposedDecodeKeepsTheServersGraphAndWarns()
    {
        // Full SBS converts by decoding the views into one frame and writing nothing after that
        // frame. There is no conversion chain for the depth stage to precede here, so the server's
        // subtitle chain and overlay stay as written; a partial insertion into the middle of that
        // graph would be worse than no depth at all.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull, videoStreamIndex: 0),
            "-filter_complex", ServersSubtitleBurnGraph,
            "-map", "[out]", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(
            arguments,
            new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0));

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", ServersSubtitleBurnGraph,
                "-map", "[out]", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("composed decode itself", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerGraphRenderingTextItselfKeepsItsFlatTextAndSaysTheRenderer()
    {
        var serverGraph =
            "[0:0]subtitles=filename='/movies/eng.srt'[txt];"
            + "[0:10]scale=1920:1080:flags=area[sub];"
            + "[txt]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]";

        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideHalf, videoStreamIndex: 0),
            "-filter_complex", serverGraph,
            "-map", "[out]", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(
            arguments,
            new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 3));

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex",
                "[0:0]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p[anaglyfin_profile];"
                + "[anaglyfin_profile]subtitles=filename='/movies/eng.srt'[txt];"
                + "[0:10]scale=1920:1080:flags=area[sub];"
                + "[txt]scale=1920:1080:flags=area,format=yuv420p[main];"
                + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]",
                "-map", "[out]", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("text filter", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionWithItsOwnBurnInKeepsTheServerSubtitleAndRefusesTheDepth()
    {
        // The one decline that belongs to the profile rather than the server: the version carries a
        // subtitle of its own, rendered flat inside its own chain, and placing depth over a graph
        // whose subtitle is now drawn twice would render it twice. The version's own promise wins,
        // and the reason is said rather than discovered as doubled captions on screen.
        const string serverGraph = "[0:s:0]ass[s];[0:0]scale=1920:1080[m];[m][s]overlay[out]";
        const string mergedGraph =
            "[0:0]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p,"
            + "subtitles=filename='/movies/Movie (2010)/Movie.2010.3D.mkv':si=0[anaglyfin_profile];"
            + "[0:s:0]ass[s];[anaglyfin_profile]scale=1920:1080[m];[m][s]overlay[out]";

        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideHalf, subtitleOrdinal: 0, videoStreamIndex: 0),
            "-filter_complex", serverGraph,
            "-map", "[out]", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(
            arguments,
            new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0));

        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", mergedGraph,
                "-map", "[out]", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);

        Assert.DoesNotContain(WrapperArgumentRewriter.DisableSubtitlesArgument, result.Arguments, StringComparer.Ordinal);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("burn-in", warning, StringComparison.Ordinal);
    }

    // ----- fixture ------------------------------------------------------------------------

    /// <summary>
    /// The subtitle burn-in graph Jellyfin writes for an image subtitle: the subtitle stream
    /// scaled into <c>[sub]</c>, the source video through its colour and scale chain into
    /// <c>[main]</c>, the two overlaid into the label the output maps. Stream 10 is the burned
    /// subtitle, stream 0 the video.
    /// </summary>
    private const string ServersSubtitleBurnGraph =
        "[0:10]scale=1920:1080:flags=area[sub];"
        + "[0:0]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
        + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[main];"
        + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]";

    /// <summary>
    /// The burn-in graph above with its video source references retargeted onto the profile
    /// label, behind the given profile chain - the merge the rewriter is expected to write.
    /// </summary>
    private static string MergedWith(string profileSegment)
        => profileSegment + ";" + ServersSubtitleBurnGraph.Replace("[0:0]", "[anaglyfin_profile]", StringComparison.Ordinal);

    /// <summary>
    /// Escapes a value the way a filter option value is written <paramref name="depth"/> FFmpeg
    /// decode passes deep: one backslash per colon and backslash per pass.
    /// </summary>
    private static string EscapedForFilter(string value, int depth)
    {
        var escaped = value;

        for (var pass = 0; pass < depth; pass++)
        {
            escaped = escaped.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal);
        }

        return escaped;
    }

    /// <summary>
    /// The command Jellyfin builds for an HLS transcode, with the given input token.
    /// </summary>
    private static List<string> JellyfinLikeCommand(string input)
        => new()
        {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-i",
            input,
            "-map",
            "0:v",
            "-map",
            "0:a",
            "-c:v",
            "libx264",
            "-c:a",
            "copy",
            "-f",
            "hls",
            "-hls_time",
            "6",
            "playlist.m3u8"
        };

    private static string Marker(
        string profileId,
        int? subtitleOrdinal = null,
        int? videoStreamIndex = null)
        => ProfileMarker.Create(profileId, SourcePath, subtitleOrdinal, videoStreamIndex).ToString();

    /// <summary>
    /// A Jellyfin HLS transcode whose output segment carries one codec option and no encoder
    /// stack - the shape a stream-copied video arrives in, because the server writes its
    /// bitrate, preset and keyframe arguments only in the encode branch.
    /// </summary>
    private static List<string> CommandCopyingVideo(string option, string value, string input)
        => new()
        {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-i",
            input,
            "-map",
            "0:v",
            "-map",
            "0:a",
            option,
            value,
            "-f",
            "hls",
            "-hls_time",
            "6",
            "playlist.m3u8"
        };

    /// <summary>
    /// The same command with its encoder stack in place: here a <c>copy</c> value can only be
    /// somebody else's stream, because the video is provably being encoded.
    /// </summary>
    private static List<string> CommandWithCodecOption(string option, string value, string input)
        => new()
        {
            "-i",
            input,
            "-map",
            "0:v",
            "-map",
            "0:a",
            "-c:v",
            "libx264",
            option,
            value,
            "-f",
            "hls",
            "playlist.m3u8"
        };

    /// <summary>
    /// Whether an argument vector carries an option and its value next to each other.
    /// </summary>
    private static bool CarriesOptionAndValue(IReadOnlyList<string> arguments, string option, string value)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal)
                && string.Equals(arguments[index + 1], value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The Jellyfin HLS transcode shape that carries no <c>-map</c> at all. Nothing names a
    /// stream here, so FFmpeg's automatic selection is what puts both the picture and the
    /// audio into the playlist - the behaviour a profile video map has to leave intact.
    /// </summary>
    private static List<string> MapLessTranscode(string input)
        => new()
        {
            "-i",
            input,
            "-c:v",
            "libx264",
            "-c:a",
            "copy",
            "-f",
            "hls",
            "playlist.m3u8"
        };

    /// <summary>
    /// The custom grayscale graph verbatim from the profile command builder's contract, written
    /// in front of the stream label it reads.
    /// </summary>
    private static string CustomGraphStartingAt(string sourceLabel)
        => $"{sourceLabel}split=2[anaglyfin_cg_left_in][anaglyfin_cg_right_in];"
           + "[anaglyfin_cg_left_in]crop=iw/2:ih:0:0,format=gray,format=rgb24,colorchannelmixer=rr=1:gg=0:bb=0[anaglyfin_cg_left];"
           + "[anaglyfin_cg_right_in]crop=iw/2:ih:iw/2:0,format=gray,format=rgb24,colorchannelmixer=rr=0:gg=1:bb=1[anaglyfin_cg_right];"
           + "[anaglyfin_cg_left][anaglyfin_cg_right]blend=all_mode=screen,format=yuv420p[anaglyfin_custom]";

    private static string InputOf(IReadOnlyList<string> arguments)
        => arguments[arguments.ToList().IndexOf(WrapperArgumentRewriter.InputFileArgument) + 1];

    /// <summary>
    /// The value an option carries in a rewritten vector, or <c>null</c> when the vector does not
    /// carry that option at all.
    /// </summary>
    private static string? ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
