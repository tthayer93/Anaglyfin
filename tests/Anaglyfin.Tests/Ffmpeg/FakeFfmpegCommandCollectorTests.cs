using System;
using Anaglyfin.Ffmpeg;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.Ffmpeg;

/// <summary>
/// Shows what a profile rewrite does when it is spliced into a Jellyfin-shaped
/// transcode command, using the fake collector in place of the T5 wrapper.
/// </summary>
public class FakeFfmpegCommandCollectorTests
{
    private const string Input = "/movies/Library/Movie.2010.3D.mkv";
    private const string Output = "/tmp/anaglyfin-test/playlist.m3u8";

    private readonly FfmpegProfileArgumentBuilder _builder = new();

    [Fact]
    public void FullSideBySideRewriteComposesOneCommandWithOneDecodeInput()
    {
        var collector = new FakeFfmpegCommandCollector()
            .WithJellyfinStyleBase(Input, Output)
            .Apply(_builder.BuildSideBySideFull());

        var rendered = collector.Render();

        Assert.Contains("-view_ids -1", rendered);
        Assert.Contains(" -sn ", rendered);

        // The composed request is an input option, not a map in the output segment.
        var viewOption = collector.IndexOf("-view_ids");
        Assert.True(viewOption >= 0 && viewOption < collector.IndexOf("-i"), rendered);
        Assert.DoesNotContain("0:v:view", rendered);

        // The linear profile rides the server's ordinary video map instead of naming another.
        Assert.Contains("-map 0:v", rendered);

        // The rewrite took subtitles off the stream level...
        Assert.DoesNotContain("0:s:0", rendered);

        // ...and nothing else grew a second input: still one -i, one output.
        Assert.Single(collector.Arguments, argument => argument == "-i");
        Assert.EndsWith(Output, rendered);
    }

    [Fact]
    public void PresetWithBurnInComposesOneFilterChainAppliedAfterTheConversion()
    {
        var rewrite = _builder.Build(CatalogProfile(ProfileIds.AnaglyphRedCyanDubois), new SubtitleBurnIn(Input, 1));

        var collector = new FakeFfmpegCommandCollector()
            .WithJellyfinStyleBase(Input, Output)
            .Apply(rewrite);

        var rendered = collector.Render();

        Assert.Contains("-view_ids -1", rendered);
        Assert.DoesNotContain("0:v:view", rendered);
        Assert.Contains("-vf stereo3d=sbsl:arcd,format=yuv420p,subtitles=filename='" + Input + "':si=1", rendered);

        // The profile filter chain ends with the burn-in, so text is rendered onto
        // the finished anaglyph - never onto the separated eyes.
        var videoFilter = collector.Arguments[collector.IndexOf("-vf") + 1];
        Assert.EndsWith("subtitles=filename='" + Input + "':si=1", videoFilter);
        Assert.StartsWith("stereo3d=sbsl:arcd,", videoFilter);
    }

    [Fact]
    public void CustomGrayscaleWithBurnInAppliesTheSubtitlesAfterTheMappedGraphOutput()
    {
        var rewrite = _builder.BuildCustomGrayscaleAnaglyph(
            new RgbColor(255, 0, 0),
            new RgbColor(0, 255, 255),
            new SubtitleBurnIn(Input, 0));

        var collector = new FakeFfmpegCommandCollector()
            .WithJellyfinStyleBase(Input, Output)
            .Apply(rewrite);

        var rendered = collector.Render();

        Assert.Contains("-view_ids -1", rendered);
        Assert.DoesNotContain("0:v:view", rendered);
        Assert.DoesNotContain("-map 0:v ", rendered);
        Assert.True(collector.IndexOf("-filter_complex") >= 0);
        Assert.True(collector.IndexOf("[anaglyfin_custom]") < collector.IndexOf("-vf"));
        Assert.Equal("subtitles=filename='" + Input + "':si=0", collector.Arguments[collector.IndexOf("-vf") + 1]);
    }

    [Theory]
    [InlineData(
        @"/data/dirs:with colon/Movie.2010.3D.mkv",
        @"stereo3d=sbsl:arcd,format=yuv420p,subtitles=filename='/data/dirs'\\:'with colon/Movie.2010.3D.mkv':si=2")]
    [InlineData(
        "/movies/It's Here (2010)/Movie.2010.3D.mkv",
        @"stereo3d=sbsl:arcd,format=yuv420p,subtitles=filename='/movies/It'\\\''s Here (2010)/Movie.2010.3D.mkv':si=2")]
    public void BurnInPathCarryingFilterSyntaxReachesTheCommandLineAsOneArgument(string sourcePath, string expectedFilterChain)
    {
        var rewrite = _builder.Build(CatalogProfile(ProfileIds.AnaglyphRedCyanDubois), new SubtitleBurnIn(sourcePath, 2));

        var collector = new FakeFfmpegCommandCollector()
            .WithJellyfinStyleBase(sourcePath, Output)
            .Apply(rewrite);

        Assert.Contains("-vf " + expectedFilterChain, collector.Render());

        // The path stays inside the single -vf argument it was assembled as: nothing in
        // it re-splits the command, and nothing is left for a shell to undo.
        Assert.Equal(expectedFilterChain, collector.Arguments[collector.IndexOf("-vf") + 1]);
    }

    [Fact]
    public void TwoDimensionalRewriteLeavesTheStockCommandUntouched()
    {
        var baseCollector = new FakeFfmpegCommandCollector().WithJellyfinStyleBase(Input, Output);
        var expected = string.Join(" ", baseCollector.Arguments);

        var rendered = baseCollector.Apply(_builder.BuildTwoDimensionalBase()).Render();

        // 2D is the drop-in binary's default behaviour: same arguments, stock subtitle
        // stream handling, no suppression, no filters.
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void ApplyingWithoutABaseCommandFails()
    {
        var collector = new FakeFfmpegCommandCollector();

        Assert.Throws<InvalidOperationException>(() => collector.Apply(_builder.BuildSideBySideFull()));
    }

    private static StereoProfile CatalogProfile(string profileId)
        => new ProfileCatalog().GetProfile(profileId);
}
