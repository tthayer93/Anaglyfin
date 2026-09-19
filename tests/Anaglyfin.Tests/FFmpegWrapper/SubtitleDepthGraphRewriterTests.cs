using System;
using Anaglyfin.Configuration;
using Anaglyfin.FFmpegWrapper;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Contract tests for the one graph edit a subtitle depth asks for: the exact argument the filter
/// is given, the exact graph it is placed into, and the exact list of shapes it declines to touch.
/// </summary>
/// <remarks>
/// <para>
/// Every expectation here is one string in and one string out. These are the graphs a real
/// FFmpeg-mvc receives, so they are asserted verbatim rather than by inspection: a chain dropped by
/// mistake, a label left unread, or a subtitle still overlaid on top of its own depth all produce a
/// video that plays, or fails to start, in a way no test short of the whole command line would
/// catch. No FFmpeg is executed.
/// </para>
/// <para>
/// The graphs are given to the rewriter the way the argument rewriter hands them over: the server's
/// own chains, with this input's video labels already retargeted onto the profile's label. The
/// profile's text is the second argument, because it is the text the rewriter threads the depth
/// through and the only graph text in the command that Anaglyfin wrote itself.
/// </para>
/// </remarks>
public class SubtitleDepthGraphRewriterTests
{
    /// <summary>The label a linear profile conversion writes to, as the composer spells it.</summary>
    private const string ProfileLabel = "[anaglyfin_profile]";

    /// <summary>The composed stream label a linear profile conversion reads its picture from.</summary>
    private const string ProfileSource = "[0:0]";

    /// <summary>The half-SBS chain, reading the composed stream and writing the profile's label.</summary>
    private const string ProfileSegment =
        "[0:0]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p[anaglyfin_profile]";

    /// <summary>
    /// The image-subtitle graph Jellyfin writes, as it stands after the composer retargeted its
    /// video labels: the subtitle stream 10 scaled into <c>[sub]</c>, the profile's picture through
    /// the server's colour and scale chain into <c>[main]</c>, the two overlaid into the label the
    /// output maps.
    /// </summary>
    private const string Sub2VideoGraph =
        "[0:10]scale=1920:1080:flags=area[sub];"
        + "[anaglyfin_profile]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
        + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[main];"
        + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]";

    /// <summary>The depth graph <see cref="Sub2VideoGraph"/> is expected to become.</summary>
    private const string Sub2VideoGraphWithDepth =
        "[0:0]format=rgba[anaglyfin_composed];"
        + "[0:10]format=rgba[anaglyfin_subtitle];"
        + "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=auto:eof_action=pass[anaglyfin_depth];"
        + "[anaglyfin_depth]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p[anaglyfin_profile];"
        + "[anaglyfin_profile]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
        + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[out]";

    // ----- the argument the filter is given ----------------------------------------------

    [Theory]
    // The three modes this product names, in the filter's own consolidated grammar.
    [InlineData(true, SubtitleDepthMode.Automatic, 0, 0, "depth=auto")]
    [InlineData(true, SubtitleDepthMode.ConstantShift, -8, 0, "depth=shift=-8")]
    [InlineData(true, SubtitleDepthMode.ConstantShift, 0, 0, "depth=shift=0")]
    [InlineData(true, SubtitleDepthMode.ConstantShift, 64, 0, "depth=shift=64")]
    [InlineData(true, SubtitleDepthMode.ConstantShift, -64, 0, "depth=shift=-64")]
    [InlineData(true, SubtitleDepthMode.Plane, 0, 0, "depth=plane=0")]
    [InlineData(true, SubtitleDepthMode.Plane, 0, 3, "depth=plane=3")]
    [InlineData(true, SubtitleDepthMode.Plane, 0, 31, "depth=plane=31")]

    // Off is off, and so is any number the filter would clamp or refuse: one knob with one off
    // position, and nothing is quietly narrowed into range on the way past.
    [InlineData(false, SubtitleDepthMode.Automatic, 0, 0, null)]
    [InlineData(false, SubtitleDepthMode.ConstantShift, -8, 0, null)]
    [InlineData(false, SubtitleDepthMode.Plane, 0, 3, null)]
    [InlineData(true, SubtitleDepthMode.ConstantShift, 65, 0, null)]
    [InlineData(true, SubtitleDepthMode.ConstantShift, -65, 0, null)]
    [InlineData(true, SubtitleDepthMode.Plane, 0, 32, null)]
    [InlineData(true, SubtitleDepthMode.Plane, 0, -1, null)]
    public void TheDepthRequestSpellsOneFilterArgument(
        bool enabled,
        SubtitleDepthMode mode,
        int shift,
        int plane,
        string? expected)
    {
        Assert.Equal(expected, SubtitleDepthGraphRewriter.BuildDepthOption(new SubtitleDepthSettings(enabled, mode, shift, plane)));
    }

    [Fact]
    public void NoSettingsMeansNoArgument()
    {
        Assert.Null(SubtitleDepthGraphRewriter.BuildDepthOption(null));
        Assert.Null(SubtitleDepthGraphRewriter.BuildDepthOption(SubtitleDepthSettings.Disabled));
    }

    [Theory]
    [InlineData("depth=auto", "mvcsubdepth=depth=auto:eof_action=pass")]
    [InlineData("depth=shift=-8", "mvcsubdepth=depth=shift=-8:eof_action=pass")]
    [InlineData("depth=plane=3", "mvcsubdepth=depth=plane=3:eof_action=pass")]
    public void TheFilterCarriesTheDepthAndPassesOnEndOfStream(string depthOption, string expected)
    {
        // eof_action=pass is the framesync option that keeps captions ending before their film from
        // ending the encode: the picture keeps flowing and the subtitles simply stop.
        Assert.Equal(expected, SubtitleDepthGraphRewriter.BuildDepthFilter(depthOption));
    }

    // ----- the graph it is placed into -----------------------------------------------------

    [Fact]
    public void AutomaticDepthIsPlacedBetweenTheComposedPictureAndTheProfileConversion()
    {
        var rewrite = Rewrite(Sub2VideoGraph, "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.Equal(Sub2VideoGraphWithDepth, rewrite.Graph);
    }

    [Theory]
    [InlineData("depth=auto")]
    [InlineData("depth=shift=-8")]
    [InlineData("depth=shift=0")]
    [InlineData("depth=plane=3")]
    public void EveryModeIsWrittenIntoTheOneFilterStage(string depthOption)
    {
        var rewrite = Rewrite(Sub2VideoGraph, depthOption);

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.Contains(
            "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=" + depthOption + ":eof_action=pass[anaglyfin_depth];",
            rewrite.Graph,
            StringComparison.Ordinal);

        // One depth stage however many modes are on offer, and the option is not spelled twice.
        Assert.Equal(1, CountOccurrences(rewrite.Graph, "mvcsubdepth"));
        Assert.Equal(1, CountOccurrences(rewrite.Graph, depthOption));
    }

    [Fact]
    public void TheSubtitleChainAndItsOverlayAreGoneAndNothingElseIs()
    {
        // The property the whole rewrite exists for: the subtitle is rendered once, by the depth
        // filter, and the film's own picture still travels through every filter the server sized for
        // it. An overlay left standing would draw the same captions flat on top of their depth.
        var rewrite = Rewrite(Sub2VideoGraph, "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.DoesNotContain("overlay", rewrite.Graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[sub]", rewrite.Graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[main]", rewrite.Graph, StringComparison.Ordinal);
        Assert.DoesNotContain(";[0:10]scale", rewrite.Graph, StringComparison.Ordinal);

        // The server's colour chain and its scale, byte for byte, ahead of the label the output maps.
        Assert.Contains(
            "[anaglyfin_profile]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
            + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[out]",
            rewrite.Graph,
            StringComparison.Ordinal);

        // The escaped-comma chain Jellyfin writes for its own sub2video input would have been a
        // second subtitle; it is gone, and no un-escaped copy of it survived elsewhere.
        Assert.Equal(1, CountOccurrences(rewrite.Graph, "[0:10]"));
    }

    [Fact]
    public void TheServerFinalOutputLabelSurvivesTheOverlayThatUsedToWriteIt()
    {
        // The command maps [out]. The chain producing [out] is gone, so the label has to be written
        // by the chain that used to feed it - or the rewritten command maps a pad nothing produces,
        // which FFmpeg reports as a graph it cannot resolve.
        var rewrite = Rewrite(Sub2VideoGraph, "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.EndsWith("format=yuv420p[out]", rewrite.Graph, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(rewrite.Graph, "[out]"));
    }

    [Fact]
    public void AGraphWithoutAnOutputLabelEndsOnTheAnonymousPadTheOutputBinds()
    {
        // The exact graph Jellyfin writes for an image-subtitle burn-in: the last chain is the
        // subtitle overlay and it labels NOTHING - the server binds the encoder to the graph's one
        // anonymous output pad (a -map 0:0 that names the consumed video stream), with no
        // -map [label] anywhere. Removing that overlay makes the picture chain the last chain, and
        // if it kept its [main] label the graph would end on a labelled pad no reader and no map
        // claims - the "Filter ... has output 0 (main) unconnected" FFmpeg refuses. So the terminal
        // label is dropped: the rewritten graph hands back the same single anonymous pad the server's
        // own -map already binds, so the command needs no new map and carries no dangling label.
        var graph =
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[anaglyfin_profile]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass";

        var rewrite = Rewrite(graph, "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.Equal(
            "[0:0]format=rgba[anaglyfin_composed];"
            + "[0:10]format=rgba[anaglyfin_subtitle];"
            + "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=auto:eof_action=pass[anaglyfin_depth];"
            + "[anaglyfin_depth]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p[anaglyfin_profile];"
            + "[anaglyfin_profile]scale=1920:1080:flags=area,format=yuv420p",
            rewrite.Graph);

        // No dangling pad and no second labelled output: [main] is gone entirely, and the graph does
        // not end in a bracket - its last character is the terminal pad of the picture chain, exactly
        // where the server's removed overlay used to sit.
        Assert.DoesNotContain("[main]", rewrite.Graph, StringComparison.Ordinal);
        Assert.False(rewrite.Graph.EndsWith("]", StringComparison.Ordinal), rewrite.Graph);
        Assert.EndsWith("format=yuv420p", rewrite.Graph, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitlyLabelledOverlayStillHandsThatLabelToTheOutput()
    {
        // The other server spelling - the overlay writes [out] and the command maps [out]. Here the
        // label is the output's, the server maps it, and the chain feeding the overlay takes it over
        // unchanged: the map still names a pad something produces, so nothing is added or removed.
        var rewrite = Rewrite(Sub2VideoGraph, "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.EndsWith("[out]", rewrite.Graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[main]", rewrite.Graph, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComposedDecodeEndsOnTheAnonymousPadWhenTheServersOverlayLabelsNothing()
    {
        // Full SBS runs the same terminal-pad rule through the composed-picture rewrite: the graph
        // ends where the server's unlabeled overlay used to end, so its own video map binds the one
        // anonymous output pad instead of a dangling [main].
        var rewrite = SubtitleDepthGraphRewriter.TryRewriteComposedPicture(
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[0:0]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass",
            videoStreamIndex: 0,
            depthOption: "depth=shift=24");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.Equal(
            "[0:0]format=rgba[anaglyfin_composed];"
            + "[0:10]format=rgba[anaglyfin_subtitle];"
            + "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=shift=24:eof_action=pass[anaglyfin_depth];"
            + "[anaglyfin_depth]scale=1920:1080:flags=area,format=yuv420p",
            rewrite.Graph);
        Assert.DoesNotContain("[main]", rewrite.Graph, StringComparison.Ordinal);
        Assert.EndsWith("format=yuv420p", rewrite.Graph, StringComparison.Ordinal);
    }

    [Theory]
    // The subtitle stream by type, by type and index, and by its number inside the file: the three
    // spellings a Jellyfin subtitle graph carries, none of which the composer retargeted because
    // none of them is this input's video.
    [InlineData("[0:s]")]
    [InlineData("[0:s:0]")]
    [InlineData("[0:s:1]")]
    [InlineData("[0:S]")]
    [InlineData("[0:10]")]
    [InlineData("[0:2]")]
    public void EverySpellingOfTheSubtitleSourceIsCarriedIntoTheDepthStage(string subtitleSourceLabel)
    {
        var graph = subtitleSourceLabel
            + "scale=1920:1080:flags=area[sub];"
            + "[anaglyfin_profile]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass[out]";

        var rewrite = Rewrite(graph, "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.StartsWith(
            "[0:0]format=rgba[anaglyfin_composed];" + subtitleSourceLabel + "format=rgba[anaglyfin_subtitle];",
            rewrite.Graph,
            StringComparison.Ordinal);

        // Re-spelled with the server's own label, retargeted into nothing else: stream 10 stays
        // stream 10 because the depth filter reads the very track the server picked.
        Assert.Equal(1, CountOccurrences(rewrite.Graph, subtitleSourceLabel));
    }

    [Theory]
    // An audio stream, a view of the composed picture, and a label of somebody else's graph: none of
    // them is this input's subtitle, so none of them is a graph this rewrite feeds depth through.
    [InlineData("[0:a]")]
    [InlineData("[0:a:0]")]
    [InlineData("[0:v:view:all]")]
    [InlineData("[0:v:vidx:1]")]
    [InlineData("[subpad]")]
    public void AChainThatIsNotTheSubtitleSourceIsNotMistakenForOne(string inputLabel)
    {
        var graph = inputLabel
            + "scale=1920:1080:flags=area[sub];"
            + "[anaglyfin_profile]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass[out]";

        var rewrite = Rewrite(graph, "depth=auto");

        Assert.False(rewrite.IsApplied);
        Assert.Empty(rewrite.Graph);
        Assert.False(string.IsNullOrEmpty(rewrite.Reason));
    }

    [Fact]
    public void TheDepthComesBeforeTheProfileConversionAndTheServerScale()
    {
        // The ordering invariant, in the one assertion that means something: the depth filter's
        // output feeds the conversion chain, the conversion feeds the server's scale, and the server
        // is scaled against the frame the version reports. A depth placed after stereo3d or after a
        // half-frame scale would displace eyes that conversion has already moved.
        var graph =
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[anaglyfin_profile]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass[out]";

        var rewrite = Rewrite(graph, "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);

        var composed = rewrite.Graph.IndexOf("[0:0]format=rgba[anaglyfin_composed]", StringComparison.Ordinal);
        var depth = rewrite.Graph.IndexOf("mvcsubdepth", StringComparison.Ordinal);
        var conversion = rewrite.Graph.IndexOf(
            "[anaglyfin_depth]scale=iw/2:ih:flags=bicubic", StringComparison.Ordinal);
        var serverScale = rewrite.Graph.IndexOf(
            "[anaglyfin_profile]scale=1920:1080:flags=area", StringComparison.Ordinal);

        Assert.True(composed < depth, "the composed picture has to reach the depth filter");
        Assert.True(depth < conversion, "the depth has to be applied before the profile converts");
        Assert.True(conversion < serverScale, "the server scales the converted picture");
    }

    [Fact]
    public void ASecondChainOfTheServersTravelsIntoTheResultUnTouched()
    {
        // The server is free to write more of the pipeline than the three chains of the shape it
        // usually writes. Everything except the subtitle chain and its overlay survives in order,
        // which is the "losslessly" half of the contract.
        var graph =
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[anaglyfin_profile]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709[graded];"
            + "[graded]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]";

        var rewrite = Rewrite(graph, "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.Equal(
            "[0:0]format=rgba[anaglyfin_composed];"
            + "[0:10]format=rgba[anaglyfin_subtitle];"
            + "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=auto:eof_action=pass[anaglyfin_depth];"
            + "[anaglyfin_depth]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p[anaglyfin_profile];"
            + "[anaglyfin_profile]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709[graded];"
            + "[graded]scale=1920:1080:flags=area,format=yuv420p[out]",
            rewrite.Graph);
    }

    // ----- what it refuses ------------------------------------------------------------------

    [Theory]
    // Two subtitles: two chains reading subtitle streams, and the two overlays that lay them on.
    // One depth stage renders one subtitle, and saying which of the two it got would be a guess.
    [InlineData(
        "[0:10]scale=1920:1080[sub0];[0:11]scale=1920:1080[sub1];"
        + "[anaglyfin_profile]scale=1920:1080[main];[main][sub0]overlay[m1];[m1][sub1]overlay[out]")]

    // A subtitle picture read twice, by the overlay and by a later chain besides. Dropping the
    // overlay would leave that later chain reaching for a picture that no longer exists.
    [InlineData(
        "[0:10]scale=1920:1080[sub];[anaglyfin_profile]scale=1920:1080[main];"
        + "[main][sub]overlay[out];[sub]format=gray[other]")]

    // The main picture read twice: it cannot simply be renamed to the label the output maps, because
    // the other reader would then name a pad nothing writes.
    [InlineData(
        "[0:10]scale=1920:1080[sub];[anaglyfin_profile]scale=1920:1080[main];"
        + "[main][sub]overlay[out];[main]showinfo[seen]")]

    // The overlay's own output feeds a later chain, so [out] is somebody's intermediate rather than
    // the label the output is drawn from - and there is no label left to hand to the main chain.
    [InlineData(
        "[0:10]scale=1920:1080[sub];[anaglyfin_profile]scale=1920:1080[main];"
        + "[main][sub]overlay[merged];[merged]scale=1280:720[out]")]

    // The overlay reading the profile's picture directly: it is that picture's only reader, and
    // removing it would leave the converted frames with nothing downstream, which FFmpeg refuses.
    [InlineData(
        "[0:10]scale=1920:1080[sub];[anaglyfin_profile][sub]overlay[out]")]

    // The chain feeding the overlay does not read the profile's picture at all, so these subtitles
    // sit outside the pipeline the profile owns.
    [InlineData(
        "[0:10]scale=1920:1080[sub];[0:1]volume=2.0[a];[a]scale=1920:1080[main];[main][sub]overlay[out]")]

    // No overlay anywhere: a graph that lays nothing onto the film has nothing to place depth in
    // front of.
    [InlineData("[0:10]scale=1920:1080[sub];[anaglyfin_profile]scale=1920:1080[main]")]

    // No subtitle stream of the marker's input at all.
    [InlineData(
        "[subpad]scale=1920:1080[sub];[anaglyfin_profile]scale=1920:1080[main];[main][sub]overlay[out]")]

    // A graph whose main picture has two producers is one FFmpeg refuses to parse, and this rewrite
    // cannot tell which of them the output was drawn from.
    [InlineData(
        "[0:10]scale=1920:1080[sub];[anaglyfin_profile]scale=1920:1080[main];"
        + "[0:1]scale=1920:1080[main];[main][sub]overlay[out]")]
    public void AGraphThatIsNotTheOneShapeIsLeftAlone(string graph)
    {
        var rewrite = Rewrite(graph, "depth=auto");

        // Not applied, nothing half-written, and one attributable reason: the caller keeps the
        // server's graph and the viewer keeps the flat subtitles the server would have played
        // anyway.
        Assert.False(rewrite.IsApplied);
        Assert.Empty(rewrite.Graph);
        Assert.False(string.IsNullOrEmpty(rewrite.Reason));
    }

    [Theory]
    // A text burn-in, alone with an overlay: the renderer is the server answering what the output's
    // subtitles look like, and it answers it flat.
    [InlineData(
        "[anaglyfin_profile]subtitles=filename='/movies/Movie (2010)/Movie.en.srt':si=0,format=yuv420p[txt];"
        + "[0:10]scale=1920:1080[sub];[txt]scale=1920:1080[main];[main][sub]overlay[out]")]

    // The same renderer on a chain of its own, with the sub2video shape around it.
    [InlineData(
        "[0:10]scale=1920:1080[sub];[0:v]ass=filename='/movies/Movie (2010)/Movie.ass'[txt];"
        + "[txt][sub]overlay[over];[anaglyfin_profile]scale=1920:1080[main];[main][over]overlay[out]")]
    public void AGraphRenderingTextItselfIsLeftToTheServerThatWroteIt(string graph)
    {
        var rewrite = Rewrite(graph, "depth=auto");

        Assert.False(rewrite.IsApplied);
        Assert.Contains("text filter", rewrite.Reason, StringComparison.Ordinal);
    }

    [Theory]
    // A label whose closing bracket never arrives: everything after it is somebody's guess.
    [InlineData("[0:10]scale=1920:1080[sub];[anaglyfin_profile]scale=2:2[main];[main][sub]overlay[out")]

    // A quoted run that never closes, so the ';' behind it may or may not be a chain boundary.
    [InlineData("[0:10]scale=1920:1080[sub];[anaglyfin_profile]drawtext=text='hi[main];[main][sub]overlay[out]")]

    // An empty chain, which is not a chain.
    [InlineData("[0:10]scale=1920:1080[sub];;[anaglyfin_profile]scale=2:2[main];[main][sub]overlay[out]")]

    // A label standing between one chain's filters, which no reading of a graph resolves the same way.
    [InlineData("[0:10]scale=1920:1080[sub];[anaglyfin_profile]scale=2:2[middle]format=yuv420p[main];[main][sub]overlay[out]")]

    // A graph that is one chain: no overlay to place anything in front of.
    [InlineData("[0:10]scale=1920:1080[sub]")]

    // A trailing backslash with nothing left to escape.
    [InlineData("[0:10]scale=1920:1080[sub];[anaglyfin_profile]scale=2:2[main];[main][sub]overlay[out]\\")]
    public void ATornGraphIsRefusedRatherThanGuessedAt(string graph)
    {
        var rewrite = Rewrite(graph, "depth=auto");

        Assert.False(rewrite.IsApplied);
        Assert.Empty(rewrite.Graph);

        // The reason names the unreadable piece; the wording differs between an unclosed quote, a
        // lone chain and a dangling escape, but every one of them says the same thing: this graph
        // will not be guessed at.
        Assert.False(string.IsNullOrEmpty(rewrite.Reason));
    }

    [Fact]
    public void AChainReadingTheLabelThisProductWritesIsRefused()
    {
        // The depth stage writes three labels of its own, all carrying the prefix. A graph already
        // holding one of those names would end up with two producers for one pad - the parse error
        // FFmpeg reports without naming the wrapper that caused it. The profile's own label is the
        // single exception, because the caller put that one there.
        var graph =
            "[0:10]scale=1920:1080[sub];[anaglyfin_depth]scale=1920:1080[main];[main][sub]overlay[out]";

        var rewrite = Rewrite(graph, "depth=auto");

        Assert.False(rewrite.IsApplied);
        Assert.Contains("label this product writes", rewrite.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileChainThatDoesNotStartAtTheComposedPictureIsNotThreaded()
    {
        // The depth goes in front of the profile's conversion, which is only meaningful if the
        // conversion starts where the composed picture enters. A segment spelled any other way would
        // have the depth run after the conversion it is supposed to precede.
        var rewrite = SubtitleDepthGraphRewriter.TryRewrite(
            Sub2VideoGraph,
            ProfileLabel,
            "[0:v:view:all]scale=iw/2:ih[anaglyfin_profile]",
            "[0:v:view:all]",
            "depth=auto");

        Assert.False(rewrite.IsApplied);
        Assert.Contains("composed picture", rewrite.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalNeverCarriesAHalfRewrittenGraph()
    {
        // The property every caller rests on: there is no outcome that is applied-but-partial, so a
        // caller that only checks IsApplied can never write a graph this class stopped halfway
        // through.
        var rewrite = Rewrite("[0:10]scale=1920:1080[sub];[anaglyfin_profile]scale=2:2[main]", "depth=auto");

        Assert.False(rewrite.IsApplied);
        Assert.Equal(string.Empty, rewrite.Graph);
        Assert.NotEqual(string.Empty, rewrite.Reason);
    }

    // ----- the profile's own graph -----------------------------------------------------------

    [Fact]
    public void AProfileThatConvertsThroughAGraphHasThatGraphThreadedAfterTheDepth()
    {
        // The custom grayscale anaglyph converts through a graph of its own and writes its own label.
        // Threading it means re-hanging its head on the depth output - and nothing else about it,
        // because that graph is text this product wrote and this rewrite does not parse its own work.
        const string customGraph =
            "[0:0]split=2[anaglyfin_cg_left_in][anaglyfin_cg_right_in];"
            + "[anaglyfin_cg_left_in]crop=iw/2:ih:0:0,format=gray,format=rgb24,colorchannelmixer=rr=1:gg=0:bb=0[anaglyfin_cg_left];"
            + "[anaglyfin_cg_right_in]crop=iw/2:ih:iw/2:0,format=gray,format=rgb24,colorchannelmixer=rr=0:gg=1:bb=1[anaglyfin_cg_right];"
            + "[anaglyfin_cg_left][anaglyfin_cg_right]blend=all_mode=screen,format=yuv420p[anaglyfin_custom]";

        var rewrite = SubtitleDepthGraphRewriter.TryRewrite(
            Sub2VideoGraph.Replace("[anaglyfin_profile]", "[anaglyfin_custom]", StringComparison.Ordinal),
            "[anaglyfin_custom]",
            customGraph,
            "[0:0]",
            "depth=plane=3");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.Equal(
            "[0:0]format=rgba[anaglyfin_composed];"
            + "[0:10]format=rgba[anaglyfin_subtitle];"
            + "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=plane=3:eof_action=pass[anaglyfin_depth];"
            + "[anaglyfin_depth]split=2[anaglyfin_cg_left_in][anaglyfin_cg_right_in];"
            + "[anaglyfin_cg_left_in]crop=iw/2:ih:0:0,format=gray,format=rgb24,colorchannelmixer=rr=1:gg=0:bb=0[anaglyfin_cg_left];"
            + "[anaglyfin_cg_right_in]crop=iw/2:ih:iw/2:0,format=gray,format=rgb24,colorchannelmixer=rr=0:gg=1:bb=1[anaglyfin_cg_right];"
            + "[anaglyfin_cg_left][anaglyfin_cg_right]blend=all_mode=screen,format=yuv420p[anaglyfin_custom];"
            + "[anaglyfin_custom]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
            + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[out]",
            rewrite.Graph);
    }

    // ----- the profile that is only the composed decode ------------------------------------

    [Fact]
    public void TheComposedDecodePlacesDepthBeforeTheServersNonSubtitleChain()
    {
        var rewrite = SubtitleDepthGraphRewriter.TryRewriteComposedPicture(
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[0:0]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
            + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]",
            videoStreamIndex: 0,
            depthOption: "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.Equal(
            "[0:0]format=rgba[anaglyfin_composed];"
            + "[0:10]format=rgba[anaglyfin_subtitle];"
            + "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=auto:eof_action=pass[anaglyfin_depth];"
            + "[anaglyfin_depth]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
            + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[out]",
            rewrite.Graph);
    }

    [Theory]
    [InlineData("depth=auto")]
    [InlineData("depth=shift=-8")]
    [InlineData("depth=plane=3")]
    public void TheComposedDecodeCarriesEveryModeIntoTheOneFilterStage(string depthOption)
    {
        var rewrite = SubtitleDepthGraphRewriter.TryRewriteComposedPicture(
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[0:0]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass[out]",
            videoStreamIndex: 0,
            depthOption);

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.Contains(
            "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=" + depthOption + ":eof_action=pass[anaglyfin_depth];",
            rewrite.Graph,
            StringComparison.Ordinal);
        Assert.DoesNotContain("overlay", rewrite.Graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[main]", rewrite.Graph, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(rewrite.Graph, "mvcsubdepth"));
    }

    [Fact]
    public void TheComposedDecodeFollowsTheMarkersSpellingOfItsSourceStream()
    {
        var rewrite = SubtitleDepthGraphRewriter.TryRewriteComposedPicture(
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[0:v]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass[out]",
            videoStreamIndex: null,
            depthOption: "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.Equal(
            "[0:v]format=rgba[anaglyfin_composed];"
            + "[0:10]format=rgba[anaglyfin_subtitle];"
            + "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=auto:eof_action=pass[anaglyfin_depth];"
            + "[anaglyfin_depth]scale=1920:1080:flags=area,format=yuv420p[out]",
            rewrite.Graph);
    }

    [Fact]
    public void TheComposedDecodeThreadsDepthThroughAChainedServerPicturePipeline()
    {
        var rewrite = SubtitleDepthGraphRewriter.TryRewriteComposedPicture(
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[0:0]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709[graded];"
            + "[graded]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass[out]",
            videoStreamIndex: 0,
            depthOption: "depth=plane=3");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.Equal(
            "[0:0]format=rgba[anaglyfin_composed];"
            + "[0:10]format=rgba[anaglyfin_subtitle];"
            + "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=plane=3:eof_action=pass[anaglyfin_depth];"
            + "[anaglyfin_depth]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709[graded];"
            + "[graded]scale=1920:1080:flags=area,format=yuv420p[out]",
            rewrite.Graph);
    }

    [Fact]
    public void TheComposedDecodeRefusesATextRendererInsteadOfDrawingItTwice()
    {
        var rewrite = SubtitleDepthGraphRewriter.TryRewriteComposedPicture(
            "[0:10]scale=1920:1080[sub];"
            + "[0:0]subtitles=filename='/movies/eng.srt'[txt];"
            + "[txt]scale=1920:1080[main];[main][sub]overlay[out]",
            videoStreamIndex: 0,
            depthOption: "depth=auto");

        Assert.False(rewrite.IsApplied);
        Assert.Empty(rewrite.Graph);
        Assert.Contains("text filter", rewrite.Reason, StringComparison.Ordinal);
    }

    [Theory]
    // A backslash and a quoted run are both punctuation this rewrite has to spend before reading a
    // comma as a filter separator. An overlay option carrying either is still one overlay.
    [InlineData("[main][sub]overlay=eof_action=pass:x=a\\,b[out]")]
    [InlineData("[main][sub]overlay=x='a,b':eof_action=pass[out]")]
    public void AnEscapedOrQuotedCommaInsideOneOverlayDoesNotMakeItTwoFilters(string overlayChain)
    {
        var graph =
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[anaglyfin_profile]scale=1920:1080:flags=area,format=yuv420p[main];"
            + overlayChain;

        var rewrite = Rewrite(graph, "depth=auto");

        Assert.True(rewrite.IsApplied, rewrite.Reason);
        Assert.DoesNotContain("overlay", rewrite.Graph, StringComparison.Ordinal);
    }

    private static SubtitleDepthGraphRewrite Rewrite(string serverGraph, string depthOption)
        => SubtitleDepthGraphRewriter.TryRewrite(serverGraph, ProfileLabel, ProfileSegment, ProfileSource, depthOption);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
