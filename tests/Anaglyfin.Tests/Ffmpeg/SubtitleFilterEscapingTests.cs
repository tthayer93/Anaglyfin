using System.Linq;
using Anaglyfin.Ffmpeg;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.Ffmpeg;

/// <summary>
/// Round-trip tests for the subtitle burn-in path escaping.
/// </summary>
/// <remarks>
/// <para>
/// The verbatim expectations in <see cref="FfmpegProfileArgumentBuilderTests"/> pin what
/// the builder writes; these tests pin what FFmpeg will read. Every filter the builder
/// emits is decoded back through <see cref="FilterGraphTokenizer"/> - the parser of the
/// pinned FFmpeg-mvc tree, run once for the filtergraph and once for the filter options -
/// and must return exactly the path that went in, with the track ordinal left standing as
/// its own option.
/// </para>
/// <para>
/// This is the regression guard for the two failures one layer of escaping produces: a
/// path containing <c>:</c> that arrives truncated at its first directory, and a path
/// containing <c>'</c> whose leftover quote swallows the trailing <c>:si=</c>, handing
/// libass a filename with an ordinal glued onto it.
/// </para>
/// </remarks>
public class SubtitleFilterEscapingTests
{
    private readonly FfmpegProfileArgumentBuilder _builder = new();

    /// <summary>
    /// Paths whose characters the escaping has to carry across both parser levels: a
    /// rooted path per <see cref="SubtitleBurnIn"/>, and never a control character.
    /// </summary>
    public static TheoryData<string> PathsNoEscapingMayLose => new()
    {
        "/movies/film.mkv",
        "/movies/Movie (2010)/Movie.2010.3D.mkv",
        "/data/dirs:with colon/film.mkv",
        "/movies/It's Here (2010)/film.mkv",
        "/movies/It's a \"test: file\" [final]/film.mkv",
        "/data/dirs:with[brackets];and,commas/film.mkv",
        "/data/equal=sign/film.mkv",
        "/movies/back\\slash/film.mkv",
        "/movies/film.mkv ",
        "/movies/$dollar & %percent%/film.mkv",
        "/movies/ünïcödé/film.mkv",
        "/movies/'quoted'/film.mkv",
        "/movies/a,b/c;d/film.mkv",
        "/movies/../dotsegments/film.mkv"
    };

    [Theory]
    [MemberData(nameof(PathsNoEscapingMayLose))]
    public void BurnInFileNameDecodesBackToTheExactPathUnderBothParsers(string path)
    {
        var rewrite = _builder.BuildSideBySideFull(new SubtitleBurnIn(path, 3));

        // Full SBS adds no conversion filter, so the whole -vf value is the burn-in.
        var burnIn = Assert.Single(FilterGraphTokenizer.DecodeGraph(rewrite.InsertArguments[^1]));

        Assert.Equal("subtitles", burnIn.Name);

        var options = FilterGraphTokenizer.DecodeOptions(burnIn.Options!);
        Assert.Equal(path, options["filename"]);
        Assert.Equal("3", options["si"]);
    }

    [Theory]
    [MemberData(nameof(PathsNoEscapingMayLose))]
    public void BurnInInsideAProfileChainStaysOneFilterAndTheOrdinalStaysItsOwnOption(string path)
    {
        var rewrite = _builder.BuildSideBySideHalf(new SubtitleBurnIn(path, 3));

        // The -vf value here is the whole chain: a comma or semicolon that escaped too
        // weakly for the graph level would cut the chain into more filters, and a colon
        // that escaped too weakly for the option level would truncate the filename and
        // orphan the ordinal.
        var filters = FilterGraphTokenizer.DecodeGraph(rewrite.InsertArguments[^1]);

        Assert.Equal(
            new[] { "scale", "format", "subtitles" },
            filters.Select(filter => filter.Name));

        var options = FilterGraphTokenizer.DecodeOptions(filters[^1].Options!);
        Assert.Equal(2, options.Count);
        Assert.Equal(path, options["filename"]);
        Assert.Equal("3", options["si"]);
    }

    [Theory]
    [MemberData(nameof(PathsNoEscapingMayLose))]
    public void BurnInBehindTheMappedCustomGrayscaleGraphRoundTrips(string path)
    {
        var rewrite = _builder.BuildCustomGrayscaleAnaglyph(
            new RgbColor(255, 0, 0),
            new RgbColor(0, 255, 255),
            new SubtitleBurnIn(path, 3));

        // This profile keeps its conversion in -filter_complex and the burn-in in its own
        // -vf behind the mapped graph output, so the burn-in is the last argument again.
        var burnIn = Assert.Single(FilterGraphTokenizer.DecodeGraph(rewrite.InsertArguments[^1]));

        Assert.Equal(
            path,
            FilterGraphTokenizer.DecodeOptions(burnIn.Options!)["filename"]);
    }

    [Theory]
    [InlineData(
        "/movies/film.mkv",
        @"subtitles=filename='/movies/film.mkv':si=1",
        @"filename=/movies/film.mkv:si=1")]
    [InlineData(
        "/data/dirs:with colon/film.mkv",
        @"subtitles=filename='/data/dirs'\:'with colon/film.mkv':si=1",
        @"filename=/data/dirs\:with colon/film.mkv:si=1")]
    [InlineData(
        "/movies/It's Here/film.mkv",
        @"subtitles=filename='/movies/It'\\\''s Here/film.mkv':si=1",
        @"filename=/movies/It\'s Here/film.mkv:si=1")]
    public void GeneratedFilterCarriesExactlyTheTwoEscapingLayersTheTwoParsersSpend(
        string path,
        string generatedFilter,
        string afterFiltergraphPass)
    {
        var rewrite = _builder.BuildSideBySideFull(new SubtitleBurnIn(path, 1));

        Assert.Equal(generatedFilter, rewrite.InsertArguments[^1]);

        // Pass one: the filtergraph parser eats one layer and hands the option string on
        // - still escaped, which is the whole point of escaping twice.
        var parsed = Assert.Single(FilterGraphTokenizer.DecodeGraph(rewrite.InsertArguments[^1]));

        Assert.Equal("subtitles", parsed.Name);
        Assert.Equal(afterFiltergraphPass, parsed.Options);

        // Pass two: the filter option parser spends the rest and names the ordinal.
        var options = FilterGraphTokenizer.DecodeOptions(parsed.Options!);

        Assert.Equal(path, options["filename"]);
        Assert.Equal("1", options["si"]);
    }
}
