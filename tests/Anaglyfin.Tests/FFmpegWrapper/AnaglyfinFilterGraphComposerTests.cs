using System;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.Markers;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Contract tests for the targeted edits this wrapper makes to filter text it did not write:
/// which labels count as this source's video, what a scan recognises without parsing a graph,
/// and how a marker quoted into a value comes back out as the real path.
/// </summary>
/// <remarks>
/// Every expectation is one string in, one string out - the composer is pure text work and
/// nothing here needs FFmpeg, a file, or a process. The shapes are the ones Jellyfin writes:
/// an image-subtitle graph reading the subtitle stream by number, a scale chain with escaped
/// commas in its option values, and a burn-in quoting a filename between single quotes.
/// </remarks>
public class AnaglyfinFilterGraphComposerTests
{
    private const string SourcePath = "/movies/Movie (2010)/Movie.2010.3D.mkv";

    private const string ProfileLabel = "[anaglyfin_profile]";

    private const string CustomLabel = "[anaglyfin_custom]";

    // ----- which labels name this source's video -----------------------------------------

    [Theory]
    // The stream the marker named, by its number inside the file: the spelling Jellyfin's
    // subtitle graphs carry.
    [InlineData("[0:0]scale=2:2[v]", 0)]

    // The video type of the input, with and without the index behind it.
    [InlineData("[0:v]scale=2:2[v]", null)]
    [InlineData("[0:v]scale=2:2[v]", 0)]
    [InlineData("[0:v:0]scale=2:2[v]", 7)]
    [InlineData("[0:v:7]scale=2:2[v]", 7)]

    // The uppercase spelling asks for the exact index of the same type, which is the same
    // picture.
    [InlineData("[0:V]scale=2:2[v]", null)]
    public void EverySpellingOfTheSourceVideoIsRetargeted(string graph, int? videoStreamIndex)
    {
        var edit = AnaglyfinFilterGraphComposer.EditVideoSourceReferences(graph, videoStreamIndex, ProfileLabel);

        Assert.Equal("[anaglyfin_profile]scale=2:2[v]", edit.Graph);
        Assert.Equal(1, edit.Replacements);
        Assert.False(edit.CarriesViewSpecifier);
        Assert.False(edit.UsesAnaglyfinLabel);
    }

    [Theory]
    // A numbered stream that is not the one the marker named - audio, a subtitle, a second
    // video - is somebody else's stream, and a scan cannot type a number.
    [InlineData("[0:1]scale=2:2[v]", 0)]
    [InlineData("[0:10]scale=2:2[sub]", 0)]
    [InlineData("[0:0]scale=2:2[v]", 3)]

    // An index behind the video type is an index within that type, not the file position the
    // marker named: only the first stream of the type counts without the marker's own number.
    [InlineData("[0:v:3]scale=2:2[v]", 0)]

    // Another input file, and every label the graph produced for itself.
    [InlineData("[1:v]scale=2:2[v]", 0)]
    [InlineData("[main][sub]overlay=0[v]", 0)]
    [InlineData("[out]scale=2:2", 0)]

    // Audio and subtitles of this very input.
    [InlineData("[0:a]anull[a]", 0)]
    [InlineData("[0:s]ass[s]", 0)]
    [InlineData("[0:s:1]ass[s]", 0)]
    public void EveryOtherLabelIsCopiedUnchanged(string graph, int? videoStreamIndex)
    {
        var edit = AnaglyfinFilterGraphComposer.EditVideoSourceReferences(graph, videoStreamIndex, ProfileLabel);

        Assert.Equal(graph, edit.Graph);
        Assert.Equal(0, edit.Replacements);
        Assert.False(edit.CarriesViewSpecifier);
    }

    [Theory]
    // A view of the source video cannot be retargeted - the composed decode refuses the request
    // itself - so it is counted rather than rewritten.
    [InlineData("[0:v:view:all]scale=2:2[v]")]
    [InlineData("[0:v:vidx:1]scale=2:2[v]")]
    [InlineData("[0:v:vpos:left]scale=2:2[v]")]
    [InlineData("[0:v:view:0]crop=iw/2:ih:0:0[l],[0:v:view:1]crop=iw/2:ih:0:0[r],[l][r]hstack[v]")]
    public void AViewSpecifierIsCountedAndLeftAsWritten(string graph)
    {
        var edit = AnaglyfinFilterGraphComposer.EditVideoSourceReferences(graph, 0, ProfileLabel);

        Assert.Equal(graph, edit.Graph);
        Assert.Equal(0, edit.Replacements);
        Assert.True(edit.CarriesViewSpecifier);
    }

    [Fact]
    public void OnlyTheSourceVideoOfAGraphIsRetargetedAndNothingElseIsTouched()
    {
        // The image-subtitle graph Jellyfin writes: the subtitle stream by number, the video
        // through a chain whose option values carry escaped commas and colons, and the overlay
        // the output maps. Only the one video label moves; the numbers, the labels, the filter
        // text and the escapes all stay byte for byte.
        var graph =
            "[0:10]scale=iw*1.500000*0.750000:ih*1.500000*0.750000,crop=iw\\,ih:0:0[sub];"
            + "[0:0]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
            + "scale=trunc(ow/a/2)*2:trunc(oh/a/2)*2,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]";

        var edit = AnaglyfinFilterGraphComposer.EditVideoSourceReferences(graph, 0, ProfileLabel);

        Assert.Equal(1, edit.Replacements);
        Assert.Contains(ProfileLabel + "setparams=", edit.Graph, StringComparison.Ordinal);
        Assert.StartsWith(
            "[0:10]scale=iw*1.500000*0.750000:ih*1.500000*0.750000,crop=iw\\,ih:0:0[sub];",
            edit.Graph,
            StringComparison.Ordinal);
        Assert.EndsWith("[main][sub]overlay=eof_action=pass:repeatlast=0[out]", edit.Graph, StringComparison.Ordinal);
        Assert.Contains("iw\\,ih", edit.Graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[0:0]", edit.Graph, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotedOrEscapedTextInsideAGraphIsNeverALabelBoundary()
    {
        // A path between single quotes is somebody's filename, not a pad name; a backslash
        // spends itself on the character behind it; and a bracket with no closing bracket is
        // not a label. None of them is rewritten, and none of them is repaired either.
        var graph = "[0:0]subtitles=filename='[0:v]/film.mkv',drawtext=text=\\[0:v\\],showinfo[0:v]";

        var edit = AnaglyfinFilterGraphComposer.EditVideoSourceReferences(graph, 0, ProfileLabel);

        Assert.Equal(
            "[anaglyfin_profile]subtitles=filename='[0:v]/film.mkv',drawtext=text=\\[0:v\\],showinfo[anaglyfin_profile]",
            edit.Graph);
        Assert.Equal(2, edit.Replacements);
    }

    [Fact]
    public void AnUnterminatedBracketIsPlainTextAndStaysThatWay()
    {
        var edit = AnaglyfinFilterGraphComposer.EditVideoSourceReferences("[0:v]scale=2:2[v", 0, ProfileLabel);

        Assert.Equal("[anaglyfin_profile]scale=2:2[v", edit.Graph);
        Assert.Equal(1, edit.Replacements);
    }

    [Fact]
    public void ALabelThisProductAlreadyWritesIsReportedAndNotRewritten()
    {
        // A merge that wrote its label into a graph already carrying that name would leave one
        // pad with two producers. The collision is reported so the caller can refuse instead of
        // writing a graph FFmpeg calls unparseable - and the label that collided is not
        // silently rewritten out of the way.
        var edit = AnaglyfinFilterGraphComposer.EditVideoSourceReferences(
            "[0:0]null[out],[anaglyfin_custom]format=yuv420p",
            0,
            ProfileLabel);

        Assert.True(edit.UsesAnaglyfinLabel);
        Assert.Contains("[anaglyfin_custom]format=yuv420p", edit.Graph, StringComparison.Ordinal);
        Assert.Equal(1, edit.Replacements);
    }

    // ----- what a scan recognises --------------------------------------------------------

    [Theory]
    // A subtitle stream of this input, by type: an image subtitle the server routes through a
    // filter is the server rendering subtitles.
    [InlineData("[0:s]ass[s]", true)]
    [InlineData("[0:s:0]subtitles[s]", true)]

    // The two renderers, in the position a filter name stands: after a label, after a comma,
    // at the head of the text.
    [InlineData("[0:v]subtitles=filename='film.srt'[v]", true)]
    [InlineData("subtitles=filename='film.srt'", true)]
    [InlineData("[0:v]scale=2:2,ass=filename='film.ass'[v]", true)]

    // A picture-only chain, a filter whose name merely contains a renderer's name, and a
    // stream that is a subtitle only by number - which no scan of text can know.
    [InlineData("[0:v]scale=2:2,format=yuv420p[v]", false)]
    [InlineData("[0:v]drawtext=text='ass.mkv'", false)]
    [InlineData("[0:10]scale=2:2[sub];[0:0]scale=2:2[main];[main][sub]overlay[out]", false)]
    [InlineData("", false)]
    public void AScanReportsWhetherTheTextRendersSubtitles(string filterText, bool expected)
    {
        Assert.Equal(expected, AnaglyfinFilterGraphComposer.HandlesSubtitles(filterText));
    }

    [Fact]
    public void NothingIsRecognisedInAbsenceAndNoArgumentIsNeededForThat()
    {
        Assert.False(AnaglyfinFilterGraphComposer.HandlesSubtitles(null));
    }

    // ----- the label a profile reads its composed picture from ----------------------------

    [Theory]
    [InlineData(0, "[0:0]")]
    [InlineData(7, "[0:7]")]
    [InlineData(null, "[0:v]")]
    public void TheComposedSourceIsNamedByIndexWhereverOneIsKnown(int? videoStreamIndex, string expected)
    {
        Assert.Equal(expected, AnaglyfinFilterGraphComposer.ComposedVideoStreamLabel(videoStreamIndex));
    }

    // ----- a marker quoted into a value ---------------------------------------------------

    [Fact]
    public void AValueThatCarriesNoMarkerIsTheValueThatCameIn()
    {
        const string value = "scale=1920:1080";

        // Not rewritten, not copied: the argument a caller passed is the argument it gets back.
        Assert.Equal(value, AnaglyfinFilterGraphComposer.ReplaceMarkerUrl(value, Marker()));
        Assert.Equal(string.Empty, AnaglyfinFilterGraphComposer.ReplaceMarkerUrl(null, Marker()));
    }

    [Fact]
    public void TheMarkerTokenItselfBecomesTheSourcePath()
    {
        var marker = Marker();

        Assert.Equal(SourcePath, AnaglyfinFilterGraphComposer.ReplaceMarkerUrl(marker.ToString(), marker));
        Assert.Equal(
            "subtitles=filename='" + SourcePath + "':si=0",
            AnaglyfinFilterGraphComposer.ReplaceMarkerUrl("subtitles=filename='" + marker + "':si=0", marker));
    }

    [Fact]
    public void AMarkerPercentEncodedAsAWholeBecomesThePathPercentEncoded()
    {
        var marker = Marker();

        Assert.Equal(
            Uri.EscapeDataString(SourcePath),
            AnaglyfinFilterGraphComposer.ReplaceMarkerUrl(Uri.EscapeDataString(marker.ToString()), marker));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AMarkerEscapedForAFilterValueBecomesThePathEscapedTheSameWay(int depth)
    {
        // The marker's scheme colon is the only character either FFmpeg decode pass would act
        // on, so the escaping is visible in the marker and - because the source path is a plain
        // rooted path - not in the replacement. The depth still has to be matched: reading the
        // singly escaped spelling out of a doubly escaped value would leave a stray backslash
        // in front of the path.
        var marker = Marker();

        Assert.Equal(
            SourcePath,
            AnaglyfinFilterGraphComposer.ReplaceMarkerUrl(EscapeForFilter(marker.ToString(), depth), marker));
    }

    [Fact]
    public void EveryMarkerSpellingInOneValueIsReplacedAtOnce()
    {
        var marker = Marker();
        var value = "a=" + marker + " b=" + Uri.EscapeDataString(marker.ToString())
            + " c=" + EscapeForFilter(marker.ToString(), 1)
            + " d=" + EscapeForFilter(marker.ToString(), 2);

        var replaced = AnaglyfinFilterGraphComposer.ReplaceMarkerUrl(value, marker);

        Assert.DoesNotContain("127.0.0.1", replaced, StringComparison.Ordinal);
        Assert.DoesNotContain("anaglyfin/profile", replaced, StringComparison.Ordinal);
        Assert.Contains("a=" + SourcePath, replaced, StringComparison.Ordinal);

        // Three plain spellings became the plain path; the percent-encoded one became the
        // percent-encoded path, which is the same file by another spelling.
        Assert.Equal(3, CountOccurrences(replaced, SourcePath));
        Assert.Contains(Uri.EscapeDataString(SourcePath), replaced, StringComparison.Ordinal);
    }

    [Fact]
    public void TextThatMerelyResemblesAMarkerIsLeftAlone()
    {
        // Matching is literal, so neither a path with the loopback in it nor a truncated marker
        // is rewritten out from under its author.
        const string value = "/library/127.0.0.1.film.mkv and http://127.0.0.1/anaglyfin/profile";

        Assert.Equal(value, AnaglyfinFilterGraphComposer.ReplaceMarkerUrl(value, Marker()));
    }

    private static ProfileMarker Marker()
        => ProfileMarker.Create(ProfileIds.AnaglyphRedCyanDubois, SourcePath, subtitleOrdinal: null, videoStreamIndex: 0);

    /// <summary>
    /// The escaping a filter option value carries <paramref name="depth"/> decode passes deep:
    /// one backslash in front of every colon and every backslash, per pass.
    /// </summary>
    private static string EscapeForFilter(string value, int depth)
    {
        var escaped = value;

        for (var pass = 0; pass < depth; pass++)
        {
            escaped = escaped.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal);
        }

        return escaped;
    }

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
