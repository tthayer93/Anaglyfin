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

    /// <summary>The custom grayscale graph, verbatim from the profile command builder's contract.</summary>
    private const string CustomGraph =
        "[0:v:view:all]split=2[anaglyfin_cg_left_in][anaglyfin_cg_right_in];"
        + "[anaglyfin_cg_left_in]crop=iw/2:ih:0:0,format=gray,format=rgb24,colorchannelmixer=rr=1:gg=0:bb=0[anaglyfin_cg_left];"
        + "[anaglyfin_cg_right_in]crop=iw/2:ih:iw/2:0,format=gray,format=rgb24,colorchannelmixer=rr=0:gg=1:bb=1[anaglyfin_cg_right];"
        + "[anaglyfin_cg_left][anaglyfin_cg_right]blend=all_mode=screen,format=yuv420p[anaglyfin_custom]";

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
        // A linear profile looks like the easy case, but its all-view map still competes
        // with the graph's labelled output, and leaving that graph unreferenced is a
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
    public void FullSideBySideReplacesTheMarkerAndTheConflictingVideoMap()
    {
        var result = _rewriter.Rewrite(JellyfinLikeCommand(Marker(ProfileIds.SideBySideFull)));

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal(ProfileIds.SideBySideFull, result.ProfileId);
        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-i", SourcePath,

                // The profile's own all-view map, with subtitle streams switched off for
                // the whole output; inserted after the last input, where FFmpeg reads
                // output options.
                "-map", "0:v:view:all", "-sn",

                // The command's own "-map 0:v" is gone, and everything that was not video
                // is exactly where the server put it.
                "-map", "0:a", "-c:v", "libx264", "-c:a", "copy",
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

    // ----- linear profile filters ----------------------------------------------------

    [Fact]
    public void HalfSideBySideInsertsItsScaleFilterWhenTheCommandHasNoVideoFilter()
    {
        var result = _rewriter.Rewrite(JellyfinLikeCommand(Marker(ProfileIds.SideBySideHalf)));

        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-i", SourcePath,
                "-map", "0:v:view:all",
                "-vf", "scale=iw/2:ih:flags=bicubic,format=yuv420p", "-sn",
                "-map", "0:a", "-c:v", "libx264", "-c:a", "copy",
                "-f", "hls", "-hls_time", "6", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void RedCyanAppendsItsFilterToTheVideoFilterChainTheServerAlreadyAskedFor()
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
                "-i", SourcePath,
                "-map", "0:v:view:all", "-sn",

                // One -vf, comma-chained, server filter first and the profile conversion
                // behind it: replacing the chain would silently drop server-side filtering.
                "-vf", "scale=1920:1080,stereo3d=sbsl:arcd,format=yuv420p",
                "-map", "0:a", "-c:v", "libx264",
                "-f", "hls", "-hls_time", "6", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void TheLastVideoFilterChainOnTheCommandIsTheOneTheProfileExtends()
    {
        // FFmpeg keeps the last value of a repeated option, so appending to the first one
        // would be appending to a chain that never runs.
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
                "-i", SourcePath,
                "-map", "0:v:view:all", "-sn",
                "-vf", "scale=1920:1080",
                "-vf", "format=nv12,stereo3d=sbsl:arcd,format=yuv420p",
                "playlist.m3u8"
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
                "-i", SourcePath,
                "-map", "0:v:view:all",
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p," + BurnInTwo, "-sn",
                "-map", "0:a", "-c:v", "libx264", "-c:s", "copy",
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
                "-i", SourcePath,
                "-map", "0:v:view:all", "-sn",
                "-map", "0:a",
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
                "-i", SourcePath,
                "-map", "0:v:view:all", "-sn",
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
                "-i", SourcePath,
                "-filter_complex", CustomGraph,
                "-map", "[anaglyfin_custom]",

                // The graph output is its own mapped stream, so the burn-in is a plain
                // -vf behind the map instead of another stage inside the graph.
                "-vf", BurnInZero, "-sn",
                "-map", "0:a", "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
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
    public void AMapLessTranscodeGainsTheProfileVideoMapAndAnAudioMapBesideIt()
    {
        var result = _rewriter.Rewrite(MapLessTranscode(Marker(ProfileIds.SideBySideFull)));

        // A -map is how FFmpeg is told which streams an output carries. With none on the
        // command, FFmpeg picks a video *and* an audio by itself; the moment this rewriter
        // names the profile's own video, that automatic pick is replaced by the single
        // stream named. The audio has to be named too here, or the 3D version of a film
        // plays silent and nothing reports it.
        Assert.Equal(
            new[]
            {
                "-i", SourcePath,

                // The profile's view map and the optional audio map: optional so that a
                // source with no audio track at all is still a source this profile converts.
                "-map", "0:v:view:all", "-map", "0:a?", "-sn",

                // The server's encoder, muxer and output choices, untouched.
                "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Theory]
    [InlineData(ProfileIds.SideBySideFull)]
    [InlineData(ProfileIds.SideBySideHalf)]
    [InlineData(ProfileIds.AnaglyphRedCyanDubois)]
    [InlineData(ProfileIds.CustomGrayscale)]
    public void EveryProfileThatOwnsTheVideoPipelineAlsoNamesTheAudioItWouldLeaveBehind(string profileId)
    {
        var result = _rewriter.Rewrite(MapLessTranscode(Marker(profileId)));

        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);

        var mapOptionIndexes = Enumerable.Range(0, result.Arguments.Count)
            .Where(index => result.Arguments[index] == "-map")
            .ToArray();

        // Exactly two maps: the profile's own stream and the audio. The rewrite invents no
        // third stream and no subtitle stream behind the burn-in.
        Assert.Equal(2, mapOptionIndexes.Length);
        Assert.Equal("0:a?", result.Arguments[mapOptionIndexes[1] + 1]);
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

        // Maps on the command are the server's own stream selection. The video map is
        // replaced because the profile competes with it; the audio map stays exactly where
        // and as it was written, and no optional audio map is invented next to it - the
        // command already said what audio it wants.
        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-map", "0:v:view:all", "-sn",
                "-map", "0:a",
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
        // profile map still replaces the competing one; undoing that choice is not this
        // rewriter's call, so inventing "-map 0:a?" here would change what the server asked
        // to mux.
        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-map", "0:v:view:all",
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p", "-sn",
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
                "-i", SourcePath,
                "-f", "srt", "-i", "/movies/Movie (2010)/Movie.en.srt",
                "-map", "0:v:view:all", "-sn",
                "-map", "0:a", "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
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

        // Only video specifiers and labelled outputs are the profile's competition. A
        // whole-file map ("0") and its exclusion ("-0") name no stream type at all, so
        // removing them would drop audio as readily as video.
        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-map", "0:v:view:all", "-sn",
                "-map", "0:a:0", "-map", "0", "-map", "-0",
                "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void AViewSpecifierVideoMapIsConflictEnoughForTheProfileOwnMap()
    {
        // A command already carrying a view selection is somebody's 3D attempt; the
        // profile replaces it rather than mapping a second video next to it.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois),
            "-map", "0:v:view:all", "-map", "0:a", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[] { "-i", SourcePath, "-map", "0:v:view:all", "-vf", "stereo3d=sbsl:arcd,format=yuv420p", "-sn", "-map", "0:a", "playlist.m3u8" },
            result.Arguments);
    }

    [Fact]
    public void ALabelledVideoMapIsRemovedWhereTheProfileHasNoGraphToMerge()
    {
        // A labelled map only means anything together with a graph, and a command that
        // carries both is refused outright (see the filter-graph refusals above). The rule
        // still has to hold on its own: as far as this rewriter goes a label is video, and
        // after a rewrite the only label the output may map is the profile's own.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "[v]", "-map", "0:a", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-map", "0:v:view:all", "-sn",
                "-map", "0:a", "playlist.m3u8"
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
        // command anyway would put "-map 0:v:view:all" and a stereo3d chain onto a command
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
    public void TheServersNumberedVideoMapMakesWayForTheProfilesOwn()
    {
        // "-map 0:0" is Jellyfin naming the video stream by position, and a profile that maps
        // its own all-view video cannot share the output with a second picture: the base view
        // next to the converted one would be an output FFmpeg either refuses or fills with
        // two videos. The marker said which numbered stream is this source's video, so the
        // audio map beside it - numbered too - stays.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideFull, videoStreamIndex: 0),
            "-map", "0:0", "-map", "0:1",
            "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-map", "0:v:view:all", "-sn",
                "-map", "0:1",
                "-c:v", "libx264", "-c:a", "copy", "-f", "hls", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void ANumberedVideoMapTheServerWroteAsOptionalIsRemovedLikeAnyOther()
    {
        // The trailing '?' means "if this file has one", not "this is a different stream".
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois, videoStreamIndex: 3),
            "-map", "0:3?", "-map", "0:1", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-map", "0:v:view:all",
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p", "-sn",
                "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void ANumberedMapIsOnlyRemovedWhenItIsTheStreamTheMarkerNamed()
    {
        // A numbered map of another stream is somebody else's stream: audio, subtitles, or a
        // second video that is not this version's business. Guessing from the number alone
        // would drop the audio out of the playlist.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.SideBySideHalf, videoStreamIndex: 2),
            "-map", "0:0", "-map", "0:2", "-map", "0:10", "-map", "1:2", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-map", "0:v:view:all",
                "-vf", "scale=iw/2:ih:flags=bicubic,format=yuv420p", "-sn",
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
                "-i", SourcePath,
                "-map", "0:v:view:all", "-sn",
                "-map", "0:0", "-map", "0:1", "playlist.m3u8"
            },
            result.Arguments);
    }

    [Fact]
    public void DroppingTheServersNumberedVideoMapDoesNotInventAnAudioMap()
    {
        // A command that mapped only video had already chosen a video-only output. Removing
        // that map is a replacement of one video choice by another, not a return to FFmpeg's
        // automatic selection, so the optional audio map this rewriter writes only for a
        // command that never named a stream still does not belong here.
        var arguments = new List<string>
        {
            "-i", Marker(ProfileIds.AnaglyphRedCyanDubois, videoStreamIndex: 0),
            "-map", "0:0", "-c:v", "libx264", "playlist.m3u8"
        };

        var result = _rewriter.Rewrite(arguments);

        Assert.Equal(
            new[]
            {
                "-i", SourcePath,
                "-map", "0:v:view:all",
                "-vf", "stereo3d=sbsl:arcd,format=yuv420p", "-sn",
                "-c:v", "libx264", "playlist.m3u8"
            },
            result.Arguments);
        Assert.DoesNotContain("0:a?", result.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void ANumberedMapAndItsProfileRewriteSurviveTogetherInOnePass()
    {
        // The realistic Jellyfin shape after the provider reports an un-copyable codec: a real
        // encoder stack, numeric maps for both streams, and the profile's conversion slotted in
        // where the server's video map used to be.
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
                "-i", SourcePath,
                "-map", "0:v:view:all",
                "-vf", "scale=iw/2:ih:flags=bicubic,format=yuv420p", "-sn",
                "-map", "0:1",
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

    private static string InputOf(IReadOnlyList<string> arguments)
        => arguments[arguments.ToList().IndexOf(WrapperArgumentRewriter.InputFileArgument) + 1];
}
