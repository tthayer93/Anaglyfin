using System;
using System.Collections.Generic;
using System.Linq;
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
    public void AnExistingFilterGraphRefusesAProfileThatOwnsTheVideoPipeline()
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-i", Marker(ProfileIds.CustomGrayscale),
            "-filter_complex", ForeignGraph, "-map", "[v]", "-map", "0:a",
            "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        // Grafting Anaglyfin's graph onto somebody else's would need a filter parser and a
        // policy for foreign text; the wrapper has neither on purpose.
        Assert.Equal(WrapperRewriteStatus.IncompatibleFilterGraph, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Arguments);
    }

    [Theory]
    [InlineData(ProfileIds.SideBySideFull)]
    [InlineData(ProfileIds.SideBySideHalf)]
    [InlineData(ProfileIds.AnaglyphRedCyanDubois)]
    public void TheSameGraphRefusesTheLinearProfilesToo(string profileId)
    {
        // A linear profile looks like the easy case, but it still owns the video pipeline and
        // cannot be merged into a graph it did not write; leaving that graph unreferenced is a
        // command FFmpeg rejects anyway.
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

                // Full SBS is the composed frame itself: subtitle streams switch off, but no
                // profile-specific video map or filter competes with what the server mapped.
                "-sn",

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
                "-sn",
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
                "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p", "-sn",
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
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p", "-sn",
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
                "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p", "-sn",
                "-map", "0:7?", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);
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
                "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p", "-sn",
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
                "-sn",

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
                "-sn",
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
    public void SubtitleStreamsAreSuppressedEvenWhenNothingIsBurnedIn()
    {
        // A converted picture carries no subtitle of its own; leaving Jellyfin's mapped
        // text track behind is double-rendered text wherever it is not dropped instead.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "0:v", "-map", "0:a", "-map", "0:s", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Contains("-sn", result.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("0:s", result.Arguments, StringComparer.Ordinal);
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
            "-i", Marker(ProfileIds.SideBySideFull),
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
                "-sn",
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
                "-sn",
                "-map", "0:a", "-c:v", "libx264", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AnSnAlreadyOnTheCommandIsNotDuplicated()
    {
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull), "-sn", "-map", "0:v", "-map", "0:a", "playlist.m3u8"
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
        // writes no stream name at all and does not disturb the automatic video+audio pick.
        Assert.Equal(
            new[]
            {
                "-view_ids", "-1",
                "-i", SourcePath,

                // The only output-side argument a linear Full SBS rewrite needs on this shape
                // is subtitle suppression.
                "-sn",

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
        // stream and no subtitle stream behind the burn-in.
        Assert.Equal(2, mapOptionIndexes.Length);
        Assert.Equal("[anaglyfin_custom]", result.Arguments[mapOptionIndexes[0] + 1]);
        Assert.Equal("0:a?", result.Arguments[mapOptionIndexes[1] + 1]);
        Assert.Contains("-sn", result.Arguments, StringComparer.Ordinal);
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
        Assert.Contains("-sn", result.Arguments, StringComparer.Ordinal);
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
                "-map", "[anaglyfin_custom]", "-map", "0:a?", "-sn",
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
                "-sn",
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
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p", "-sn",
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
                "-sn",
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
                "-sn",
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
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p", "-sn",
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
        // composed view and suppresses subtitles.
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
                "-sn",
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
                "-map", "[anaglyfin_custom]", "-sn",
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
                "-map", "[anaglyfin_custom]", "-sn",
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
                "-map", "[anaglyfin_custom]", "-sn",
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
                "-sn",
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
                "-map", "[anaglyfin_custom]", "-sn",
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
                "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p", "-sn",
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

    // ----- fixture ------------------------------------------------------------------------

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
