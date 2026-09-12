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

        Assert.Contains("-map 0:v:view:all", rendered);
        Assert.Contains(" -sn ", rendered);

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

        Assert.Contains("-vf stereo3d=sbsl:arcd,format=yuv420p,subtitles='" + Input + "':si=1", rendered);

        // The profile filter chain ends with the burn-in, so text is rendered onto
        // the finished anaglyph - never onto the separated eyes.
        var videoFilter = collector.Arguments[collector.IndexOf("-vf") + 1];
        Assert.EndsWith("subtitles='" + Input + "':si=1", videoFilter);
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

        Assert.True(collector.IndexOf("-filter_complex") >= 0);
        Assert.True(collector.IndexOf("[anaglyfin_custom]") < collector.IndexOf("-vf"));
        Assert.Equal("subtitles='" + Input + "':si=0", collector.Arguments[collector.IndexOf("-vf") + 1]);
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
