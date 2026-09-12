using System;
using System.Collections.Generic;
using System.Linq;
using Anaglyfin.Markers;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.Markers;

/// <summary>
/// Verifies the parser side of the profile marker contract: what text counts as a
/// marker, what a parsed marker must contain, and - the load-bearing part for the
/// FFmpeg wrapper - the strict split between "ordinary FFmpeg input, pass through"
/// (<see cref="MarkerParseStatus.NotMarker"/>) and "marker attempted and failed,
/// refuse" (every other failure status).
/// </summary>
public class ProfileMarkerParserTests
{
    private const string Prefix = "http://127.0.0.1/anaglyfin/profile/";

    private const string CanonicalExample = Prefix + "anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&subtitle=0";

    /// <summary>Paths a real library can hold; the parser must hand them back unmangled.</summary>
    public static IEnumerable<object?[]> RoundTripCases => new[]
    {
        // profile id, source path, subtitle ordinal
        new object?[] { "anaglyph_arcd", "/movies/Film.mkv", 0 },
        new object?[] { "two_d_base", "/movies/Movie (2010)/Movie.2010.3D.mkv", null },
        new object?[] { "sbs_full", "/data/dirs:with colon/film.mkv", 12 },
        new object?[] { "sbs_half", "/movies/It's Here (2010)/film.mkv", 1 },
        new object?[] { "anaglyph_arcd", "/movies/$dollar & %percent%/film.mkv", 0 },
        new object?[] { "anaglyph_arcd", "/movies/ünïcödé/film.mkv", null },
        new object?[] { "anaglyph_arcd", "/movies/back\\slash/film.mkv", 3 },
        new object?[] { "two_d_base", "/movies/film.mkv ", null },
        new object?[] { "sbs_full", "/movies/equal=sign/x,;#?.mkv", 2 },
        new object?[] { "custom_grayscale", "/movies/../dotsegments/film.mkv", null },
        new object?[] { "anaglyph_arcd", "/nowhere/a-file-that-does-not-exist.mkv", 7 }
    };

    [Fact]
    public void TheDocumentedExampleParsesToItsThreeValues()
    {
        var result = ProfileMarkerParser.Parse(CanonicalExample);

        Assert.True(result.IsSuccess);
        Assert.Equal(MarkerParseStatus.Success, result.Status);
        Assert.Equal("anaglyph_arcd", result.ProfileId);
        Assert.Equal("/movies/Film.mkv", result.SourcePath);
        Assert.Equal(0, result.SubtitleOrdinal);
        Assert.Equal(ProfileMarker.Create("anaglyph_arcd", "/movies/Film.mkv", 0), result.Marker);
    }

    [Fact]
    public void ACanonicalMarkerWithoutSubtitleParsesWithNullOrdinal()
    {
        var result = ProfileMarkerParser.Parse(Prefix + "two_d_base?source=%2Fmovies%2FFilm.mkv");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Marker!.SubtitleOrdinal);
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void EveryBuiltMarkerReparsesToAnEqualMarker(string profileId, string sourcePath, int? subtitleOrdinal)
    {
        var marker = ProfileMarker.Create(profileId, sourcePath, subtitleOrdinal);

        var result = ProfileMarkerParser.Parse(marker.ToString());

        Assert.True(result.IsSuccess);
        Assert.Equal(marker, result.Marker);
        Assert.Equal(sourcePath, result.SourcePath);
        Assert.Equal(subtitleOrdinal, result.SubtitleOrdinal);
    }

    [Fact]
    public void ParsingAcceptsCasingVariationInTheTransportWithoutChangingTheMarker()
    {
        // URL casing tolerance: scheme, host, path and parameter keys are case
        // insensitive, the profile id resolves through the case-insensitive allowlist.
        var result = ProfileMarkerParser.Parse(
            "HTTP://127.0.0.1/ANAGLYFIN/PROFILE/ANAGLYPH_ARCD?SOURCE=%2Fmovies%2FFilm.mkv&SubTitle=0");

        Assert.True(result.IsSuccess);
        Assert.Equal("anaglyph_arcd", result.ProfileId);
        Assert.Equal(CanonicalExample, result.Marker!.ToString());
    }

    [Fact]
    public void QueryParametersMayArriveInAnyOrderAndStillCanonicallyRoundTrip()
    {
        var result = ProfileMarkerParser.Parse(Prefix + "two_d_base?subtitle=2&source=%2Fmovies%2FFilm.mkv");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.SubtitleOrdinal);

        // The canonical text re-parses to the very same outcome.
        var again = ProfileMarkerParser.Parse(result.Marker!.ToString());
        Assert.Equal(result, again);
    }

    [Fact]
    public void APercentEncodedPercentDecodesExactlyOnce()
    {
        // "%252F" must arrive as the literal text "%2F" inside the path, not as a
        // second decoding round: values are decoded once and then treated as data.
        var result = ProfileMarkerParser.Parse(Prefix + "two_d_base?source=%2Fmovies%2F%252ff.mkv");

        Assert.True(result.IsSuccess);
        Assert.Equal("/movies/%2ff.mkv", result.SourcePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/movies/Film.mkv")]
    [InlineData("ffmpeg -i /movies/Film.mkv out.m3u8")]
    [InlineData("https://127.0.0.1/anaglyfin/profile/anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&subtitle=0")]
    [InlineData("http://example.com/anaglyfin/profile/anaglyph_arcd?source=%2Fmovies%2FFilm.mkv")]
    [InlineData("anaglyfin://profile/anaglyph_arcd?source=%2Fmovies%2FFilm.mkv")]
    [InlineData("http://127.0.0.1:8096/anaglyfin/profile/anaglyph_arcd?source=%2Fmovies%2FFilm.mkv")]
    [InlineData("http://127.0.0.1/anaglyfin")]
    [InlineData("http://127.0.0.1/other/profile/anaglyph_arcd")]
    public void AnythingBelowTheMarkerPrefixIsOrdinaryInput(string? candidate)
    {
        var result = ProfileMarkerParser.Parse(candidate);

        Assert.Equal(MarkerParseStatus.NotMarker, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Marker);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(Prefix)]
    [InlineData("http://127.0.0.1/anaglyfin/profile")]
    [InlineData("http://127.0.0.1/anaglyfin/profiles/anaglyph_arcd?source=%2Fmovies%2FFilm.mkv")]
    [InlineData(Prefix + "anaglyph_arcd")]
    [InlineData(Prefix + "anaglyph_arcd/extra?source=%2Fmovies%2FFilm.mkv")]
    [InlineData(Prefix + "anaglyph_arcd&&source=%2Fmovies%2FFilm.mkv")]
    [InlineData(Prefix + "anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&")]
    [InlineData(Prefix + "anaglyph_arcd?source")]
    [InlineData(Prefix + "anaglyph_arcd?=x&source=%2Fmovies%2FFilm.mkv")]
    [InlineData(Prefix + "anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&source=%2Fother.mkv")]
    [InlineData(Prefix + "anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&subtitle=0&subtitle=1")]
    [InlineData(Prefix + "anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&vf=fade=in:0:30")]
    [InlineData(Prefix + "anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&-filter_complex=evil")]
    [InlineData(Prefix + "anaglyph_arcd?\x20source=%2Fmovies%2FFilm.mkv")]
    [InlineData(Prefix + "anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&subtitle=0\n-map 0:v:view:all")]
    [InlineData(Prefix + "anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&subtitle=0\t-x")]
    public void MarkerShapedJunkIsRefusedAsMalformed(string markerShapedText)
    {
        var result = ProfileMarkerParser.Parse(markerShapedText);

        Assert.Equal(MarkerParseStatus.MalformedMarker, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Marker);
    }

    [Theory]
    [InlineData("http://127.0.0.1/anaglyfin/profile")]
    [InlineData("http://127.0.0.1/anaglyfin/profiles/anaglyph_arcd?source=%2Fmovies%2FFilm.mkv")]
    public void NearPrefixTextIsNeverClassifiedAsPassThrough(string markerShapedText)
    {
        // The wrapper's pass-through branch must never swallow marker-shaped text:
        // anything claiming the reserved path namespace is refused, not forwarded.
        var result = ProfileMarkerParser.Parse(markerShapedText);

        Assert.NotEqual(MarkerParseStatus.NotMarker, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData(Prefix + "no_such_profile?source=%2Fmovies%2FFilm.mkv")]
    [InlineData(Prefix + "arcd?source=%2Fmovies%2FFilm.mkv")]
    [InlineData(Prefix + "subtitles%3Dx?source=%2Fmovies%2FFilm.mkv")]
    [InlineData(Prefix + "anaglyfin%2Fprofile?source=%2Fmovies%2FFilm.mkv")]
    [InlineData(Prefix + "-vf%20fade?source=%2Fmovies%2FFilm.mkv")]
    [InlineData(Prefix + "%2520?source=%2Fmovies%2FFilm.mkv")]
    public void UnknownProfileIdsAreRefused(string candidate)
    {
        var result = ProfileMarkerParser.Parse(candidate);

        Assert.Equal(MarkerParseStatus.UnknownProfile, result.Status);
        Assert.Null(result.Marker);
    }

    [Theory]
    [InlineData(Prefix + "anaglyph_arcd?")]
    [InlineData(Prefix + "anaglyph_arcd?subtitle=0")]
    [InlineData(Prefix + "anaglyph_arcd?source=")]
    [InlineData(Prefix + "anaglyph_arcd?source=%20%20")]
    public void AMarkerWithoutAUsableSourceIsRefused(string candidate)
    {
        var result = ProfileMarkerParser.Parse(candidate);

        Assert.Equal(MarkerParseStatus.MissingSource, result.Status);
        Assert.Null(result.Marker);
    }

    [Theory]
    [InlineData(Prefix + "anaglyph_arcd?source=movies%2FFilm.mkv")]
    [InlineData(Prefix + "anaglyph_arcd?source=.%2F.%2Fetc%2Fpasswd")]
    [InlineData(Prefix + "anaglyph_arcd?source=%7E%2FFilm.mkv")]
    [InlineData(Prefix + "anaglyph_arcd?source=%2Fmovies%2Ffilm%00.mkv")]
    [InlineData(Prefix + "anaglyph_arcd?source=%2Fmovies%2Ffilm%0A.mkv")]
    [InlineData(Prefix + "anaglyph_arcd?source=%2Fmovies%2Ffilm%0D%0Aexecute.mkv")]
    public void NonRootedOrControlCharacterSourcesAreRefused(string candidate)
    {
        var result = ProfileMarkerParser.Parse(candidate);

        Assert.Equal(MarkerParseStatus.InvalidSource, result.Status);
        Assert.Null(result.Marker);
    }

    [Theory]
    [InlineData("subtitle=")]
    [InlineData("subtitle=-1")]
    [InlineData("subtitle=-0")]
    [InlineData("subtitle=+1")]
    [InlineData("subtitle=1.5")]
    [InlineData("subtitle=abc")]
    [InlineData("subtitle=%30")]
    [InlineData("subtitle=%200")]
    [InlineData("subtitle=0%20")]
    [InlineData("subtitle=0x1")]
    [InlineData("subtitle=99999999999999999999999")]
    [InlineData("subtitle=٣")]
    public void ASubtitleOrdinalMustBePlainNonNegativeDigits(string subtitleParameter)
    {
        var result = ProfileMarkerParser.Parse(
            Prefix + "anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&" + subtitleParameter);

        Assert.Equal(MarkerParseStatus.InvalidSubtitleOrdinal, result.Status);
        Assert.Null(result.Marker);
    }

    [Fact]
    public void LeadingZeroSubtitlesParseToTheirValueAndCanonicallyReEncode()
    {
        var result = ProfileMarkerParser.Parse(
            Prefix + "anaglyph_arcd?source=%2Fmovies%2FFilm.mkv&subtitle=007");

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.SubtitleOrdinal);
        Assert.EndsWith("&subtitle=7", result.Marker!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParsingIsIndependentOfReachabilityAndFilesystemPresence()
    {
        // The marker "URL" is a namespace, not an endpoint, and the referenced file
        // need not exist: parsing is pure string classification for the wrapper.
        var result = ProfileMarkerParser.Parse(
            Prefix + "sbs_full?source=%2Fvolume%2Fnot%2Fmounted%2Fghost.mkv&subtitle=3");

        Assert.True(result.IsSuccess);
        Assert.Equal("/volume/not/mounted/ghost.mkv", result.SourcePath);
    }

    [Fact]
    public void ParsingIsDeterministicForIdenticalInput()
    {
        var first = ProfileMarkerParser.Parse(CanonicalExample);
        var second = ProfileMarkerParser.Parse(CanonicalExample);
        var broken = ProfileMarkerParser.Parse(Prefix + "anaglyph_arcd?source=movies");
        var brokenAgain = ProfileMarkerParser.Parse(Prefix + "anaglyph_arcd?source=movies");

        Assert.Equal(first, second);
        Assert.Equal(broken, brokenAgain);
    }

    [Theory]
    [InlineData(CanonicalExample, true)]
    [InlineData("HTTP://127.0.0.1/anaglyfin/Profile/anaglyph_arcd?source=%2Ff.mkv", true)]
    [InlineData(Prefix + "anaglyph_arcd", true)]
    [InlineData("http://127.0.0.1/anaglyfin/profile", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("/movies/Film.mkv", false)]
    public void IsMarkerCandidateIsTheCheapPrefixFilter(string? candidate, bool expected)
    {
        // The filter judges the prefix alone: a prefixed token without a query is a
        // candidate that Parse will go on to refuse as malformed, while near-prefix
        // text is not a candidate and Parse classifies it separately.
        Assert.Equal(expected, ProfileMarkerParser.IsMarkerCandidate(candidate));
    }

    [Fact]
    public void FailureOutcomesCarryNoMarkerAndSuccessOutcomesCarryNoError()
    {
        var failure = ProfileMarkerParser.Parse("/movies/Film.mkv");
        var success = ProfileMarkerParser.Parse(CanonicalExample);

        Assert.Null(failure.Marker);
        Assert.Null(failure.ProfileId);
        Assert.Null(failure.SourcePath);
        Assert.Null(failure.SubtitleOrdinal);
        Assert.Null(success.Error);
        Assert.Equal(CanonicalExample, success.ToString());
        Assert.StartsWith("NotMarker:", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResultsGuardTheirOwnInvariants()
    {
        Assert.Throws<ArgumentNullException>(
            () => MarkerParseResult.Success(null!));
        Assert.Throws<ArgumentException>(
            () => MarkerParseResult.Failure(MarkerParseStatus.Success, "smuggled"));
        Assert.ThrowsAny<ArgumentException>(
            () => MarkerParseResult.Failure(MarkerParseStatus.MalformedMarker, "  "));
    }
}
