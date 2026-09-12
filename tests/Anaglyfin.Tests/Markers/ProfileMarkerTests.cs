using System;
using System.Linq;
using Anaglyfin.Markers;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.Markers;

/// <summary>
/// Verifies the builder side of the profile marker contract: the canonical marker
/// text and the security gates a marker must pass before it may exist.
/// </summary>
public class ProfileMarkerTests
{
    /// <summary>The documented marker example from the architecture.</summary>
    private const string CanonicalExample =
        "http://127.0.0.1/anaglyfin/profile/anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&subtitle=0";

    [Fact]
    public void TheCanonicalFormIsExactlyTheDocumentedMarkerShape()
    {
        var marker = ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 0);

        Assert.Equal(CanonicalExample, marker.ToString());
    }

    [Fact]
    public void ASubtitleFreeVersionCarriesNoSubtitleParameter()
    {
        var marker = ProfileMarker.Create(ProfileIds.TwoDBase, "/movies/Film.mkv");

        Assert.Equal(
            "http://127.0.0.1/anaglyfin/profile/two_d_base?source=%2Fmovies%2FFilm.mkv",
            marker.ToString());
        Assert.Null(marker.SubtitleOrdinal);
    }

    [Fact]
    public void SubtitleZeroIsAValueAndTravelsInText()
    {
        var marker = ProfileMarker.Create(ProfileIds.TwoDBase, "/movies/Film.mkv", 0);

        Assert.EndsWith("&subtitle=0", marker.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, marker.SubtitleOrdinal);
        Assert.NotEqual(
            marker.ToString(),
            ProfileMarker.Create(ProfileIds.TwoDBase, "/movies/Film.mkv").ToString());
    }

    [Fact]
    public void AVersionThatNamesNoVideoStreamCarriesNoVideoParameter()
    {
        var marker = ProfileMarker.Create(ProfileIds.SideBySideFull, "/movies/Film.mkv");

        Assert.Equal(
            "http://127.0.0.1/anaglyfin/profile/sbs_full?source=%2Fmovies%2FFilm.mkv",
            marker.ToString());
        Assert.Null(marker.VideoStreamIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void TheVideoStreamIndexTravelsBetweenTheSourceAndTheSubtitle(int videoStreamIndex)
    {
        // The position the server's own "-map 0:<index>" will spend on this video stream,
        // which is the number the wrapper needs to recognize that map and cannot read off
        // the command line - a bare number names no stream type.
        var marker = ProfileMarker.Create(ProfileIds.SideBySideFull, "/movies/Film.mkv", videoStreamIndex: videoStreamIndex);

        Assert.Equal(
            $"http://127.0.0.1/anaglyfin/profile/sbs_full?source=%2Fmovies%2FFilm.mkv&video={videoStreamIndex}",
            marker.ToString());
        Assert.Equal(videoStreamIndex, marker.VideoStreamIndex);
    }

    [Fact]
    public void VideoStreamIndexZeroIsAValueAndNotAnAbsence()
    {
        var named = ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", videoStreamIndex: 0);
        var unnamed = ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv");

        Assert.Contains("&video=0", named.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(named.ToString(), unnamed.ToString());
        Assert.NotEqual(named, unnamed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NegativeVideoStreamIndexesAreRefused(int videoStreamIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProfileMarker.Create(ProfileIds.TwoDBase, "/movies/Film.mkv", videoStreamIndex: videoStreamIndex));
    }

    [Fact]
    public void AMarkerCarryingEveryParameterIsStillOneUnforgeableToken()
    {
        var marker = ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Movie (2010)/Movie.2010.3D.mkv", 2, 1);
        var text = marker.ToString();

        // The video parameter joins the query without joining its structure: one '?', the
        // separators of exactly the three known parameters, and no whitespace for a shell or
        // an argv splitter to trip over.
        Assert.Equal(1, text.Count(c => c == '?'));
        Assert.Equal(2, text.Count(c => c == '&'));
        Assert.DoesNotContain(" ", text);
        Assert.Equal(
            "http://127.0.0.1/anaglyfin/profile/anaglyph_arcd"
            + "?source=%2Fmovies%2FMovie%20%282010%29%2FMovie.2010.3D.mkv&video=1&subtitle=2",
            text);
    }

    [Fact]
    public void ProfileIdsAreCanonicalizedToTrimmedLowercase()
    {
        var marker = ProfileMarker.Create("  ANAGLYPH_ARCD  ", "/movies/Film.mkv", 0);

        Assert.Equal("anaglyph_arcd", marker.ProfileId);
        Assert.StartsWith("http://127.0.0.1/anaglyfin/profile/anaglyph_arcd?", marker.ToString());
    }

    [Fact]
    public void EveryAllowlistedProfileIdBuildsAMarkerThatCarriesExactlyThatId()
    {
        Assert.All(
            ProfileIds.AllProfileIds,
            profileId =>
            {
                var marker = ProfileMarker.Create(profileId, "/movies/Film.mkv");

                Assert.Equal(profileId, marker.ProfileId);
                Assert.StartsWith(
                    ProfileMarker.MarkerPrefix + profileId + "?source=",
                    marker.ToString(),
                    StringComparison.Ordinal);
            });
    }

    [Theory]
    [InlineData("/movies/$dollar & %percent%/film.mkv")]
    [InlineData("/movies/Movie (2010)/Movie.2010.3D [4K].mkv")]
    [InlineData("/movies/It's Here (2010)/film.mkv")]
    [InlineData("/movies/a,b/c;d/e?question#.mkv")]
    [InlineData("/movies/ünïcödé/film.mkv")]
    [InlineData("/movies/film.mkv ")]
    [InlineData("/movies/back\\slash/film.mkv")]
    [InlineData("/movies/equal=sign/film.mkv")]
    public void TheSourcePathIsEncodedSoTheMarkerStaysASingleUnforgeableToken(string sourcePath)
    {
        var marker = ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, sourcePath, 1);

        // Query structure cannot be forged from inside the filename: exactly one '?'
        // and the exactly two '&' separators for the two known parameters.
        Assert.Equal(1, marker.ToString().Count(c => c == '?'));
        Assert.Equal(1, marker.ToString().Count(c => c == '&'));
        Assert.DoesNotContain(" ", marker.ToString());
    }

    [Fact]
    public void TheEncodingOfTheExamplePathIsThePercentFormUsedByTheContract()
    {
        var marker = ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 0);

        Assert.Contains("source=%2Fmovies%2FFilm.mkv", marker.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("anaglyph_notarealprofile")]
    [InlineData("arcd")]
    [InlineData("-vf fade")]
    [InlineData("subtitles=x")]
    [InlineData("two_d_base extra")]
    [InlineData("/movies/Film.mkv")]
    [InlineData("stereo3d=sbsl:arcd")]
    public void EmptyOrUnknownProfileIdsAreRefused(string? profileId)
    {
        Assert.Throws<ArgumentException>(
            () => ProfileMarker.Create(profileId!, "/movies/Film.mkv", 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("movies/Film.mkv")]
    [InlineData("./movies/Film.mkv")]
    [InlineData("../Film.mkv")]
    [InlineData("~/Film.mkv")]
    [InlineData("/movies/film.mkv\n-t 10")]
    [InlineData("/movies/film\0.mkv")]
    [InlineData("/movies/film\t.mkv")]
    [InlineData("/movies/fil\u001bm.mkv")]
    public void NonRootedOrControlCharacterSourcePathsAreRefused(string? sourcePath)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => ProfileMarker.Create(ProfileIds.TwoDBase, sourcePath!, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NegativeSubtitleOrdinalsAreRefused(int ordinal)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProfileMarker.Create(ProfileIds.TwoDBase, "/movies/Film.mkv", ordinal));
    }

    [Fact]
    public void IntMaxSubtitleOrdinalIsAccepted()
    {
        var marker = ProfileMarker.Create(ProfileIds.TwoDBase, "/movies/Film.mkv", int.MaxValue);

        Assert.Equal(int.MaxValue, marker.SubtitleOrdinal);
    }

    [Fact]
    public void NoFileIsTouchedOrResolvedWhileBuildingAMarker()
    {
        // Nothing under this root exists, and building must not care: a marker is text,
        // so provider and wrapper can construct and validate it independently of the
        // filesystem the path refers to.
        var marker = ProfileMarker.Create(
            ProfileIds.SideBySideFull,
            "/anaglyfin-does-not-exist-zqxff/ghost.mkv");

        Assert.Equal("/anaglyfin-does-not-exist-zqxff/ghost.mkv", marker.SourcePath);
    }

    [Fact]
    public void MarkersCompareByTheirValues()
    {
        var marker = ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 0);

        Assert.Equal(marker, ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 0));
        Assert.NotEqual(marker, ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 1));
        Assert.NotEqual(marker, ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv"));

        // A named video stream is a different version instruction, not a decoration: two
        // markers that differ only there address different streams of the same file.
        Assert.NotEqual(marker, ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 0, 0));
        Assert.NotEqual(
            ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", videoStreamIndex: 0),
            ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", videoStreamIndex: 1));
    }

    [Fact]
    public void TheMediaSourcePathHelperProducesTheCanonicalMarkerText()
    {
        var marker = ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 0);

        Assert.Equal(CanonicalExample, marker.ToMediaSourcePath());
        Assert.Equal(marker.ToString(), marker.ToMediaSourcePath());
    }
}
