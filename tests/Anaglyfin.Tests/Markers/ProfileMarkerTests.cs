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
    public void MarkersCompareByTheirThreeValues()
    {
        var marker = ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 0);

        Assert.Equal(marker, ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 0));
        Assert.NotEqual(marker, ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 1));
        Assert.NotEqual(marker, ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv"));
    }

    [Fact]
    public void TheMediaSourcePathHelperProducesTheCanonicalMarkerText()
    {
        var marker = ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, "/movies/Film.mkv", 0);

        Assert.Equal(CanonicalExample, marker.ToMediaSourcePath());
        Assert.Equal(marker.ToString(), marker.ToMediaSourcePath());
    }
}
