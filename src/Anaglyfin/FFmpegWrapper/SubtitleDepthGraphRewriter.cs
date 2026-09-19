using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Anaglyfin.Configuration;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Puts the <c>mvcsubdepth</c> stage of a subtitle-depth request into a filter graph the server
/// wrote, or says why this graph cannot take it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this class exists for.</b> A subtitle depth is authored in the MVC stream and has to be
/// applied to the subtitle <em>picture</em> after the eyes have been composed into one frame and
/// before the profile turns that frame into the version's output. A server that burns an image
/// subtitle in has already built a graph that renders that subtitle - a sub2video chain reading the
/// subtitle stream, and an <c>overlay</c> laying the result on the picture - and the depth has to
/// land between two stages of <em>somebody else's</em> graph. That is one edit more than
/// <see cref="AnaglyfinFilterGraphComposer"/> is willing to make to text it did not write, and it is
/// the whole of this class: recognise one shape, rewrite that shape, and refuse everything else
/// without touching it.
/// </para>
/// <para>
/// <b>The one shape, spelled out.</b> These are the chains Jellyfin writes when it renders an image
/// subtitle itself, after the composer has retargeted the video labels onto the profile's label:
/// </para>
/// <code>
/// [0:10]scale=1920:1080:flags=area[sub];
/// [anaglyfin_profile]scale=1920:1080,format=yuv420p[main];
/// [main][sub]overlay=eof_action=pass:repeatlast=0[out]
/// </code>
/// <para>
/// and, with the profile's own chain in front of the server's, this is what it becomes:
/// </para>
/// <code>
/// [0:0]format=rgba[anaglyfin_composed];
/// [0:10]format=rgba[anaglyfin_subtitle];
/// [anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=auto:eof_action=pass[anaglyfin_depth];
/// [anaglyfin_depth]scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p[anaglyfin_profile];
/// [anaglyfin_profile]scale=1920:1080,format=yuv420p[out]
/// </code>
/// <para>
/// The subtitle's chain and the overlay are gone: depth renders the subtitle now, and the same text
/// still overlaid on top of it would be one sentence twice on one frame, once with depth and once
/// flat. Every other chain of the server's travels into the result byte for byte, and the label the
/// output maps is the label the overlay used to write, now written by the chain that used to feed
/// the overlay. That last move is what keeps the server's <c>-map [out]</c> valid after the
/// overlay it named is removed, and it is the reason a graph whose overlay feeds another chain is
/// refused instead: there is no label left to hand over.
/// </para>
/// <para>
/// <b>Why the order is the order.</b> The depth an authored disc carries lives in the dependent
/// view's offset metadata, so it exists only on the frames the composed decode produces, and it is a
/// horizontal eye displacement - which means nothing on one eye alone and is undone by any anaglyph
/// or half-frame conversion that moves the eyes afterwards. Composed picture, then depth, then the
/// profile's conversion, then whatever scaling the server asked of the converted picture. The
/// <c>format=rgba</c> on both inputs is the colour format the filter negotiates - a subtitle picture
/// is only a subtitle while its background is transparent - and <c>eof_action=pass</c> is what keeps
/// captions that end before their film from ending the transcode.
/// </para>
/// <para>
/// <b>What is refused.</b> Everything that is not that shape: a graph rendering text through
/// <c>subtitles</c> or <c>ass</c> (a libass burn-in draws inside the decode of a text track, where
/// there is no subtitle picture to displace), a graph with two subtitle chains or two overlays, a
/// graph whose subtitle pad is read by more than the one overlay, a graph whose main picture is read
/// by more than the one overlay, a graph carrying a label this product writes, and any graph this
/// class cannot split into chains without guessing - an unterminated bracket, a quote that never
/// closes, an empty chain, a chain with no filter in it, a label standing between one chain's
/// filters. A refusal never edits: it returns the reason and the caller leaves the server's graph
/// alone, which is the flat-subtitle playback the server would have produced anyway.
/// </para>
/// <para>
/// <b>How the text is read.</b> As FFmpeg's own parser reads it, and only for structural questions:
/// a backslash spends itself on the character behind it, a <c>'...'</c> run contributes its contents
/// literally, a label is one self-delimiting <c>[</c> name <c>]</c>, a <c>;</c> ends a chain and a
/// <c>,</c> continues one. Nothing is normalised, re-quoted or repaired, and no filter's option
/// string is ever interpreted - which is also a security property: a received command line is data,
/// and the only text this class writes into a result besides the server's own chains is its own.
/// </para>
/// </remarks>
public static class SubtitleDepthGraphRewriter
{
    /// <summary>
    /// The label the composed picture is carried to the depth filter under.
    /// </summary>
    public const string ComposedLabel = "[anaglyfin_composed]";

    /// <summary>
    /// The label the subtitle picture is carried to the depth filter under.
    /// </summary>
    public const string SubtitleLabel = "[anaglyfin_subtitle]";

    /// <summary>
    /// The label the depth filter writes the depth-placed picture to, and the label the profile's
    /// conversion then reads.
    /// </summary>
    public const string DepthLabel = "[anaglyfin_depth]";

    /// <summary>The prefix every label this product writes carries.</summary>
    private const string OwnedLabelPrefix = "anaglyfin";

    /// <summary>
    /// The colour format both depth inputs are put through: the filter negotiates RGBA, because a
    /// subtitle picture is only a subtitle while its background is transparent.
    /// </summary>
    private const string RgbaFormatFilter = "format=rgba";

    /// <summary>The depth filter's name.</summary>
    private const string DepthFilterName = "mvcsubdepth";

    /// <summary>
    /// The framesync option that keeps a subtitle ending before its film from ending the encode:
    /// the picture keeps flowing and the captions simply stop.
    /// </summary>
    private const string EndOfStreamOption = "eof_action=pass";

    /// <summary>The filter that lays a subtitle picture onto the main picture.</summary>
    private const string OverlayFilterName = "overlay";

    /// <summary>
    /// The filter names that render a text track onto a picture, and which no depth can be applied
    /// to: they draw inside the decode of a text track, where there is no subtitle picture for the
    /// depth filter to place.
    /// </summary>
    private static readonly string[] TextRendererNames = ["subtitles", "ass"];

    /// <summary>
    /// The detail parts that make a specifier name a view rather than a stream, as FFmpeg-mvc spells
    /// them. A view of the composed input is refused by an input the wrapper configured through
    /// <c>-view_ids</c>, so a graph holding one is never a graph this rewrite can feed.
    /// </summary>
    private static readonly string[] ViewSpecifierParts = ["view", "vidx", "vpos"];

    /// <summary>
    /// The <c>depth</c> option a request spells for the filter, or null when the request asks for
    /// nothing the filter can be given.
    /// </summary>
    /// <param name="settings">The subtitle depth request, as it reached the wrapper.</param>
    /// <returns>
    /// <c>depth=auto</c>, <c>depth=shift=&lt;pixels&gt;</c> or <c>depth=plane=&lt;index&gt;</c>; null
    /// for a request that is off, and for one whose number the filter would silently widen or refuse.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The three spellings are the three modes this product names, in the filter's own grammar -
    /// which is the consolidated one, where the separate <c>plane=</c> and <c>shift=</c> options of
    /// the first depth builds were removed. <c>depth=flat</c> is deliberately unreachable: it is what
    /// Anaglyfin produces by leaving the feature off, and one knob with one off position is one knob.
    /// </para>
    /// <para>
    /// Nothing is clamped here. The filter travels 64 pixels of transparent slack per side and its
    /// plane index addresses a table of at most 32 sequences, so a number outside those bounds is a
    /// request nobody can recover the meaning of - and the caller's alternative to a number it was
    /// not given is the flat subtitle the settings did not ask to replace.
    /// </para>
    /// </remarks>
    public static string? BuildDepthOption(SubtitleDepthSettings? settings)
    {
        if (settings is null || !settings.Enabled)
        {
            return null;
        }

        return settings.Mode switch
        {
            SubtitleDepthMode.Automatic => "depth=auto",

            SubtitleDepthMode.ConstantShift when SubtitleDepthSettings.IsShiftInRange(settings.ShiftPixels)
                => "depth=shift=" + settings.ShiftPixels.ToString(CultureInfo.InvariantCulture),

            SubtitleDepthMode.Plane when SubtitleDepthSettings.IsPlaneInRange(settings.Plane)
                => "depth=plane=" + settings.Plane.ToString(CultureInfo.InvariantCulture),

            _ => null
        };
    }

    /// <summary>
    /// The whole depth filter stage, option and all.
    /// </summary>
    /// <param name="depthOption">The <c>depth=</c> option from <see cref="BuildDepthOption"/>.</param>
    /// <returns><c>mvcsubdepth=&lt;depth option&gt;:eof_action=pass</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="depthOption"/> is blank.</exception>
    public static string BuildDepthFilter(string depthOption)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(depthOption);

        return DepthFilterName + "=" + depthOption + ":" + EndOfStreamOption;
    }

    /// <summary>
    /// Rewrites the server's chains into the depth graph, around the profile chain the caller has
    /// already built.
    /// </summary>
    /// <param name="serverGraph">
    /// The server's filter graph as the command wrote it, with this input's video labels already
    /// retargeted onto <paramref name="profileLabel"/>.
    /// </param>
    /// <param name="profileLabel">
    /// The label the server's chains now read the profile's picture from -
    /// <see cref="AnaglyfinFilterGraphComposer.ProfileOutputLabel"/> or
    /// <see cref="AnaglyfinFilterGraphComposer.CustomOutputLabel"/>.
    /// </param>
    /// <param name="profileSegment">
    /// The profile's own text: one chain for a linear conversion, a whole graph for the profile that
    /// converts through one. It is the caller's to reorder and this class's to thread the depth
    /// through: it has to begin with <paramref name="profileSourceLabel"/> and end with
    /// <paramref name="profileLabel"/>.
    /// </param>
    /// <param name="profileSourceLabel">
    /// The label <paramref name="profileSegment"/> reads the composed picture from -
    /// <c>[0:&lt;index&gt;]</c>, or <c>[0:v]</c> where the marker named no index.
    /// </param>
    /// <param name="depthOption">The <c>depth=</c> option from <see cref="BuildDepthOption"/>.</param>
    /// <returns>
    /// The merged graph with depth in it, or a refusal carrying the one reason this graph is not the
    /// shape a depth stage can be placed in. A refusal never holds a half-edited graph.
    /// </returns>
    /// <remarks>
    /// The questions below are asked in the order they have to be asked: whether the profile's text
    /// can be threaded at all, then whether the server's text can be read, then whether it is a graph
    /// a depth filter has any business in, and only then whether the two chains being taken out can
    /// be taken out without leaving a pad unread or a label the output maps unproduced.
    /// </remarks>
    /// <exception cref="ArgumentException">Any text argument is blank.</exception>
    public static SubtitleDepthGraphRewrite TryRewrite(
        string serverGraph,
        string profileLabel,
        string profileSegment,
        string profileSourceLabel,
        string depthOption)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverGraph);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileSegment);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileSourceLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(depthOption);

        if (NamesViewSpecifier(profileSourceLabel))
        {
            // A view specifier names one eye. The depth filter works on the composed frame that the
            // decoder puts both eyes on, so a profile reading its conversion from one eye cannot be
            // threaded through a stage that belongs to the composed picture.
            return SubtitleDepthGraphRewrite.NotSupported(
                "the profile's conversion reads a view of its source instead of the composed picture this rewrite places depth on.");
        }

        if (!profileSegment.StartsWith(profileSourceLabel, StringComparison.Ordinal)
            || !profileSegment.EndsWith(profileLabel, StringComparison.Ordinal)
            || profileSegment.Length <= profileSourceLabel.Length + profileLabel.Length)
        {
            // The profile's text is the one piece of this graph the wrapper wrote itself, and the
            // depth stage threads through it - in front of the conversion, not beside it. A segment
            // that does not start where the composed picture enters cannot be threaded, and a
            // conversion that ran before the depth would move the eyes the depth had just placed.
            return SubtitleDepthGraphRewrite.NotSupported(
                "the profile's conversion does not read its composed picture through the label this rewrite names, so a depth stage could not be placed in front of it.");
        }

        if (!TrySplitChains(serverGraph, out var chainTexts, out var splitReason))
        {
            return SubtitleDepthGraphRewrite.NotSupported(splitReason!);
        }

        var chains = new List<Chain>(chainTexts.Count);

        for (var index = 0; index < chainTexts.Count; index++)
        {
            if (!Chain.TryParse(chainTexts[index], index, out var chain, out var chainReason))
            {
                return SubtitleDepthGraphRewrite.NotSupported(
                    $"chain {index + 1} of the command's filter graph is not a chain this wrapper can read ({chainReason}).");
            }

            chains.Add(chain!);
        }

        var refusal = RefuseForTextRendering(chains);

        if (refusal is not null)
        {
            return refusal;
        }

        // Every label the graph reads and every label it writes, counted. The counts are what make
        // the two removals below provably lossless: a pad read twice cannot be taken out by removing
        // one of its readers, and a label written twice is a graph FFmpeg refuses to parse.
        var producers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var readers = new Dictionary<string, int>(StringComparer.Ordinal);
        var profileLabelName = LabelName(profileLabel);

        foreach (var chain in chains)
        {
            foreach (var label in chain.AllLabels)
            {
                // The labels this rewrite writes are prefixed like every other label this product
                // writes, and a graph already carrying one of the names would gain a second producer
                // for a pad the depth stage writes. The profile's own label is the one name with a
                // right to be here: the caller put it there when it retargeted the server's video
                // references onto the profile's conversion.
                var name = LabelName(label);

                if (name.StartsWith(OwnedLabelPrefix, StringComparison.Ordinal)
                    && !string.Equals(name, profileLabelName, StringComparison.Ordinal))
                {
                    return SubtitleDepthGraphRewrite.NotSupported(
                        "the command's filter graph already carries a label this product writes, so the depth stage would have somewhere to write that somebody else already took.");
                }
            }

            foreach (var input in chain.Inputs)
            {
                readers[input] = readers.TryGetValue(input, out var reads) ? reads + 1 : 1;
            }

            foreach (var output in chain.Outputs)
            {
                if (!producers.TryGetValue(output, out var written))
                {
                    written = new List<int>(1);
                    producers[output] = written;
                }

                written.Add(chain.Index);
            }
        }

        var overlays = chains.Where(chain => chain.IsSingleOverlay).ToList();

        if (overlays.Count != 1)
        {
            return SubtitleDepthGraphRewrite.NotSupported(overlays.Count == 0
                ? "the command's filter graph lays no subtitle picture onto the film, so there is nothing here for a depth stage to place."
                : $"the command's filter graph lays {overlays.Count} subtitle pictures onto the film, and one depth stage can only be placed in front of one of them.");
        }

        var subtitleChains = chains
            .Where(chain => chain.Inputs.Count == 1 && NamesSubtitleSourceOfFirstInput(chain.Inputs[0]))
            .ToList();

        if (subtitleChains.Count != 1)
        {
            return SubtitleDepthGraphRewrite.NotSupported(subtitleChains.Count == 0
                ? "the command's filter graph reads no subtitle stream of the marker's input, so there is no subtitle picture here to give depth."
                : $"the command's filter graph reads {subtitleChains.Count} subtitle streams of the marker's input, and one depth stage renders one of them.");
        }

        var overlay = overlays[0];
        var subtitleChain = subtitleChains[0];

        if (overlay.Outputs.Count > 1)
        {
            return SubtitleDepthGraphRewrite.NotSupported(
                "the command's subtitle overlay writes more than one picture, so which of them the output is drawn from is written nowhere in the command.");
        }

        if (subtitleChain.Outputs.Count != 1)
        {
            return SubtitleDepthGraphRewrite.NotSupported(
                "the command's subtitle chain does not write exactly one picture, so the sub2video chain this rewrite replaces is not the one the overlay reads.");
        }

        var mainLabel = overlay.Inputs.Count == 2 ? overlay.Inputs[0] : null;
        var subtitleLabel = overlay.Inputs.Count == 2 ? overlay.Inputs[1] : null;

        if (mainLabel is null || subtitleLabel is null)
        {
            return SubtitleDepthGraphRewrite.NotSupported(
                $"the command's subtitle overlay reads {overlay.Inputs.Count} pictures, and a depth stage is placed between one film picture and the one subtitle picture laid on it.");
        }

        if (!string.Equals(subtitleLabel, subtitleChain.Outputs[0], StringComparison.Ordinal)
            || producers[subtitleLabel].Count != 1)
        {
            return SubtitleDepthGraphRewrite.NotSupported(
                "the command's subtitle chain does not write the picture its overlay reads, so the sub2video chain and its consumer are not the pair this rewrite replaces.");
        }

        if (readers[subtitleLabel] != 1)
        {
            return SubtitleDepthGraphRewrite.NotSupported(
                "the command's subtitle picture is read by more than the one overlay this rewrite replaces, so the other reader would be left reaching for a picture that no longer exists.");
        }

        if (string.Equals(mainLabel, profileLabel, StringComparison.Ordinal))
        {
            // The overlay is the profile's only reader. Taking it out would leave the converted
            // picture with nothing downstream - which FFmpeg refuses - and there is no chain of the
            // server's to hand the output's label over to.
            return SubtitleDepthGraphRewrite.NotSupported(
                "the command's subtitle overlay reads the profile's picture directly, so removing it would leave the converted picture with nothing to feed.");
        }

        if (!producers.TryGetValue(mainLabel, out var mainWriters) || mainWriters.Count != 1)
        {
            return SubtitleDepthGraphRewrite.NotSupported(
                "the picture the command's subtitle overlay lays its captions onto is not written by exactly one chain of the graph, so there is nothing single to place a depth stage in front of.");
        }

        var mainChain = chains[mainWriters[0]];

        if (mainChain.Index == subtitleChain.Index || mainChain.Outputs.Count != 1)
        {
            return SubtitleDepthGraphRewrite.NotSupported(
                "the command's subtitle overlay draws its captions onto a picture that is not written once by one chain, so there is no label to carry the output in its place.");
        }

        if (!TryTraceProfileOwnedPicturePath(
                chains,
                producers,
                profileLabel,
                mainChain,
                subtitleChain,
                overlay,
                out var pathReason))
        {
            return SubtitleDepthGraphRewrite.NotSupported(pathReason);
        }

        if (readers[mainLabel] != 1)
        {
            return SubtitleDepthGraphRewrite.NotSupported(
                "the picture the command's subtitle overlay draws onto is read by more than that overlay, so it cannot simply be renamed to the label the output maps.");
        }

        // The overlay's own output label, where it wrote one, is the label the output maps - and from
        // now on the label the chain that fed the overlay writes. Read by a later chain, it is
        // somebody's intermediate rather than the output's, and there is no label left to hand over.
        var finalLabel = overlay.Outputs.Count == 1 ? overlay.Outputs[0] : null;

        if (finalLabel is not null
            && (readers.TryGetValue(finalLabel, out var finalReads) && finalReads > 0
                || producers[finalLabel].Count != 1))
        {
            return SubtitleDepthGraphRewrite.NotSupported(
                "the picture the command's subtitle overlay writes is itself filtered by a later chain, so the label the output maps is not the overlay's to hand over.");
        }

        var rewritten = new StringBuilder(serverGraph.Length + 160);

        // The depth stage, with both of its inputs put through the colour format it negotiates.
        rewritten
            .Append(profileSourceLabel).Append(RgbaFormatFilter).Append(ComposedLabel).Append(';')
            .Append(subtitleChain.Inputs[0]).Append(RgbaFormatFilter).Append(SubtitleLabel).Append(';')
            .Append(ComposedLabel).Append(SubtitleLabel)
            .Append(BuildDepthFilter(depthOption)).Append(DepthLabel).Append(';');

        // The profile's own text, reading the depth output where it used to read the composed stream.
        rewritten
            .Append(DepthLabel)
            .Append(
                profileSegment,
                profileSourceLabel.Length,
                profileSegment.Length - profileSourceLabel.Length - profileLabel.Length)
            .Append(profileLabel);

        // What is left of the server's graph: every chain this rewrite does not remove, in the order
        // the server wrote them, with the chain that fed the overlay writing the label the output
        // maps. The subtitle's chain and the overlay are the two that are gone - the first because
        // the depth stage reads that stream itself, the second because a subtitle laid on twice would
        // be one subtitle rendered flat on top of its own depth.
        foreach (var chain in chains)
        {
            if (chain.Index == subtitleChain.Index || chain.Index == overlay.Index)
            {
                continue;
            }

            rewritten.Append(';');

            if (chain.Index == mainChain.Index && finalLabel is not null)
            {
                rewritten.Append(chain.Text, 0, chain.OutputStart).Append(finalLabel);
            }
            else
            {
                rewritten.Append(chain.Text);
            }
        }

        return SubtitleDepthGraphRewrite.Applied(rewritten.ToString());
    }

    /// <summary>
    /// Whether the picture the overlay draws onto is fed, through one chain of the server's own
    /// labels, from the profile's converted picture.
    /// </summary>
    /// <remarks>
    /// The server is allowed to split its picture pipeline into more than one chain, and the depth
    /// stage still belongs behind all of those chains rather than beside one of them. Walking
    /// backwards from the overlay's main picture proves where that pipeline starts without parsing
    /// any more of the server's filter text than labels and positions. A path that branches into a
    /// source this rewrite cannot see - another stream, another overlay, or a cycle - is refused,
    /// because the output would no longer be provably the profile's picture with depth in it.
    /// </remarks>
    private static bool TryTraceProfileOwnedPicturePath(
        IReadOnlyList<Chain> chains,
        Dictionary<string, List<int>> producers,
        string profileLabel,
        Chain mainChain,
        Chain subtitleChain,
        Chain overlay,
        out string reason)
    {
        var currentChain = mainChain;
        var visited = new HashSet<int>();

        while (true)
        {
            if (!visited.Add(currentChain.Index))
            {
                reason = "the chain the command's subtitle overlay draws onto is reached twice by the labels that feed it, so its picture cannot be traced back to one converted source.";

                return false;
            }

            if (currentChain.Index == subtitleChain.Index || currentChain.Index == overlay.Index)
            {
                reason = "the chain the command's subtitle overlay draws onto is one of the chains this rewrite would remove, so the film picture and the subtitle picture are not separate here.";

                return false;
            }

            if (currentChain.Inputs.Count == 0)
            {
                reason = "the chain the command's subtitle overlay draws onto is not fed by a label this rewrite can trace back to the profile's converted picture.";

                return false;
            }

            if (currentChain.Inputs.Contains(profileLabel, StringComparer.Ordinal))
            {
                if (currentChain.Inputs.Count != 1)
                {
                    reason = "the chain that carries the profile's picture to the command's subtitle overlay also reads another picture, so the overlay is not drawing onto the profile's picture alone.";

                    return false;
                }

                reason = string.Empty;

                return true;
            }

            if (currentChain.Inputs.Count != 1)
            {
                reason = "the chain the command's subtitle overlay draws onto is joined from more pictures than this rewrite can prove came from the profile's converted frame.";

                return false;
            }

            var sourceLabel = currentChain.Inputs[0];

            if (!producers.TryGetValue(sourceLabel, out var sourceWriters) || sourceWriters.Count != 1)
            {
                reason = "the picture feeding the command's subtitle overlay is not written by exactly one earlier chain, so it cannot be traced back to one converted source.";

                return false;
            }

            if (sourceWriters[0] == currentChain.Index)
            {
                reason = "the chain the command's subtitle overlay draws onto writes the picture it reads, so its labels form a cycle instead of a path to the profile.";

                return false;
            }

            currentChain = chains[sourceWriters[0]];
        }
    }

    /// <summary>
    /// The refusal owed to a graph that renders text itself, or null when it renders none.
    /// </summary>
    /// <param name="chains">The graph's chains, each already read.</param>
    /// <remarks>
    /// A libass burn-in is the server answering what the output's subtitles look like, and it answers
    /// it flat: the <c>subtitles</c> and <c>ass</c> filters draw inside the decode of a text track,
    /// where there is no subtitle picture for depth to displace. Depth placed around such a graph
    /// would render the picture it is handed while the text still landed flat on top of it - one
    /// question answered twice, twice over - so the graph is left to the server that wrote it.
    /// </remarks>
    private static SubtitleDepthGraphRewrite? RefuseForTextRendering(IReadOnlyList<Chain> chains)
    {
        foreach (var chain in chains)
        {
            foreach (var name in chain.FilterNames)
            {
                foreach (var renderer in TextRendererNames)
                {
                    if (string.Equals(name, renderer, StringComparison.Ordinal))
                    {
                        return SubtitleDepthGraphRewrite.NotSupported(
                            "the command's filter graph renders its subtitles through the '" + renderer
                            + "' text filter, which draws them flat inside the picture and has no subtitle picture for a depth stage to place.");
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Splits filter text into its chains at every top-level <c>;</c>.
    /// </summary>
    /// <param name="graph">The graph text.</param>
    /// <param name="chains">The chain texts, in order - even when this returns <c>false</c>, so a
    /// caller can report on what it managed to read.</param>
    /// <param name="reason">Why the text is not readable as chains, when this returns <c>false</c>.</param>
    /// <returns><c>true</c> when the text split without guessing.</returns>
    /// <remarks>
    /// A separator inside a quoted run or behind a backslash is somebody's punctuation and not a
    /// chain boundary, and a quote that never closes leaves everything behind it unreadable - which is
    /// reported rather than guessed at, because the guess would be a graph FFmpeg parses differently
    /// from the one its author wrote.
    /// </remarks>
    private static bool TrySplitChains(string graph, out List<string> chains, out string? reason)
    {
        var found = new List<string>();
        var current = new StringBuilder();
        var index = 0;

        while (index < graph.Length)
        {
            var character = graph[index];

            if (character == '\\')
            {
                if (index + 1 >= graph.Length)
                {
                    chains = found;
                    reason = "the graph ends with a backslash that has nothing to escape";

                    return false;
                }

                current.Append(character).Append(graph[index + 1]);
                index += 2;

                continue;
            }

            if (character == '\'')
            {
                var closing = graph.IndexOf('\'', index + 1);

                if (closing < 0)
                {
                    chains = found;
                    reason = "the graph carries a quoted run that never closes";

                    return false;
                }

                current.Append(graph, index, closing - index + 1);
                index = closing + 1;

                continue;
            }

            if (character == ';')
            {
                found.Add(current.ToString());
                current.Clear();
                index++;

                continue;
            }

            current.Append(character);
            index++;
        }

        found.Add(current.ToString());

        if (found.Count < 2)
        {
            chains = found;
            reason = "the graph is a single chain, which carries no subtitle overlay a depth stage could be placed in front of";

            return false;
        }

        chains = found;
        reason = null;

        return true;
    }

    /// <summary>
    /// Whether the label behind a stream specifier names a subtitle stream of the marker's input - or
    /// names a stream of it whose kind no label spells, which is what a numbered subtitle is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The typed spellings answer for themselves: <c>[0:s]</c>, <c>[0:s:0]</c> and their uppercase
    /// spelling are a subtitle stream of the first input, and the first input is the marker's own.
    /// </para>
    /// <para>
    /// A number answers nothing. <c>[0:10]</c> is stream 10 of the file, and the file is not in the
    /// argument vector to say what kind of stream that is; it is accepted here because the caller has
    /// already retargeted this input's video labels onto the profile and because the marker named
    /// which number of this file is its picture - so a numbered label still standing names a stream
    /// that is not this profile's video. That is not yet a subtitle. It becomes one upstairs, where
    /// the picture this chain produces turns out to be the one an overlay lays onto the film, which
    /// is a subtitle's job and not audio's.
    /// </para>
    /// <para>
    /// A view of anything is refused outright: a decode configured through the composed view request
    /// will not name one eye of its output, so a graph reading a view cannot be handed this
    /// profile's picture at all.
    /// </para>
    /// </remarks>
    private static bool NamesSubtitleSourceOfFirstInput(string label)
    {
        var name = label.Substring(1, label.Length - 2);

        if (name.Length < 3 || name[0] != '0' || name[1] != ':')
        {
            return false;
        }

        var specifier = name.Substring(2);
        var separator = specifier.IndexOf(':');
        var type = separator < 0 ? specifier : specifier.Substring(0, separator);
        var detail = separator < 0 ? null : specifier.Substring(separator + 1);

        if (detail is not null && CarriesViewSpecifier(detail))
        {
            return false;
        }

        if (type.Length == 1 && char.IsAsciiLetter(type[0]))
        {
            // The uppercase spelling asks for the exact index within a type instead of its default
            // one, and leaves the type - which is the question here - alone.
            return char.ToLowerInvariant(type[0]) == 's';
        }

        return int.TryParse(type, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Whether a filter-label source names a view of the marker's first input.</summary>
    private static bool NamesViewSpecifier(string label)
    {
        if (label.Length < 5 || label[0] != '[' || label[^1] != ']'
            || label[1] != '0' || label[2] != ':')
        {
            return false;
        }

        var specifier = label.Substring(3, label.Length - 4);
        var separator = specifier.IndexOf(':');

        return separator >= 0 && CarriesViewSpecifier(specifier.Substring(separator + 1));
    }

    /// <summary>Whether a specifier detail asks for a view rather than for a stream.</summary>
    private static bool CarriesViewSpecifier(string detail)
    {
        foreach (var part in detail.Split(':'))
        {
            foreach (var specifier in ViewSpecifierParts)
            {
                if (string.Equals(part, specifier, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The pad name inside a bracketed filter label.</summary>
    private static string LabelName(string label)
        => label.Length >= 2 && label[0] == '[' && label[^1] == ']'
            ? label.Substring(1, label.Length - 2)
            : label;

    private static bool IsNameStart(char value)
        => value is >= 'a' and <= 'z'
           || value is >= 'A' and <= 'Z'
           || value == '_';

    private static bool IsNameChar(char value)
        => IsNameStart(value) || value is >= '0' and <= '9';

    /// <summary>
    /// One <c>;</c>-separated chain of a filter graph, read as far as its labels and its filter names
    /// and not one step further.
    /// </summary>
    /// <remarks>
    /// A chain is <c>[in][in] filters separated by commas [out][out]</c>, so everything this rewrite
    /// needs is where the leading labels stop, where the trailing ones start, and which names stand
    /// at the positions a filter name can stand. A label in between those two runs, a chain with no
    /// filter in it, or a quote or bracket left open makes the chain unreadable: the graph this
    /// rewrite would build out of a chain it cannot read is a graph whose meaning its author never
    /// wrote.
    /// </remarks>
    private sealed class Chain
    {
        private Chain(string text, int index)
        {
            Text = text;
            Index = index;
        }

        /// <summary>The chain's text exactly as the command wrote it.</summary>
        public string Text { get; }

        /// <summary>Which chain of the graph this is, so the rewrite can splice by position.</summary>
        public int Index { get; }

        /// <summary>The labels at the head of the chain, brackets included.</summary>
        public List<string> Inputs { get; } = [];

        /// <summary>The labels at the end of the chain, brackets included.</summary>
        public List<string> Outputs { get; } = [];

        /// <summary>Every label of the chain, in the order they stand in it.</summary>
        public List<string> AllLabels { get; } = [];

        /// <summary>The filter names the chain carries, in the positions a filter name can stand.</summary>
        public List<string> FilterNames { get; } = [];

        /// <summary>Where the chain's trailing labels begin.</summary>
        public int OutputStart { get; private set; }

        /// <summary>
        /// Whether the chain is one <c>overlay</c> filter and nothing else - the filter that lays a
        /// subtitle picture onto the film.
        /// </summary>
        public bool IsSingleOverlay { get; private set; }

        /// <summary>Reads one chain, or says what about it cannot be read.</summary>
        /// <param name="text">The chain's text.</param>
        /// <param name="index">The chain's position in the graph.</param>
        /// <param name="chain">The chain, when this returns <c>true</c>.</param>
        /// <param name="reason">What made it unreadable, when this returns <c>false</c>.</param>
        /// <returns><c>true</c> when the chain was read without guessing.</returns>
        public static bool TryParse(string text, int index, out Chain? chain, out string? reason)
        {
            if (text.Length == 0)
            {
                reason = "the graph carries an empty chain";
                chain = null;

                return false;
            }

            var labels = new List<LabelToken>();

            if (!TryScan(text, labels, out reason))
            {
                chain = null;

                return false;
            }

            var parsed = new Chain(text, index);

            foreach (var label in labels)
            {
                parsed.AllLabels.Add(label.WithBrackets);
            }

            // The head run: labels touching the start of the chain, each one beginning where the
            // last one stopped.
            var headEnd = 0;

            foreach (var label in labels)
            {
                if (label.Start != headEnd)
                {
                    break;
                }

                parsed.Inputs.Add(label.WithBrackets);
                headEnd = label.End;
            }

            // The tail run, from the other end, under the same rule.
            var tailStart = text.Length;

            for (var position = labels.Count - 1; position >= 0; position--)
            {
                if (labels[position].End != tailStart)
                {
                    break;
                }

                parsed.Outputs.Insert(0, labels[position].WithBrackets);
                tailStart = labels[position].Start;
            }

            if (tailStart <= headEnd)
            {
                reason = "the chain is nothing but labels";
                chain = null;

                return false;
            }

            foreach (var label in labels)
            {
                if (label.End > headEnd && label.Start < tailStart)
                {
                    // A label between the two runs is a pad name standing in the middle of a filter's
                    // arguments. FFmpeg reads a link there and nothing else can be concluded about
                    // what the chain does, which is exactly the sort of guessing this rewrite refuses.
                    reason = $"the chain carries a label {label.WithBrackets} between its filters";
                    chain = null;

                    return false;
                }
            }

            // The body is what stands between the two runs, and the check above is what makes it
            // non-empty: a chain that is labels and nothing else has already been refused.
            var body = text.Substring(headEnd, tailStart - headEnd);

            parsed.IsSingleOverlay = TryReadSingleOverlay(body, out var names);
            parsed.OutputStart = tailStart;
            parsed.FilterNames.AddRange(names);
            chain = parsed;
            reason = null;

            return true;
        }

        /// <summary>
        /// Collects every label of a chain, refusing anything the scan cannot finish reading.
        /// </summary>
        private static bool TryScan(string text, List<LabelToken> labels, out string? reason)
        {
            var index = 0;

            while (index < text.Length)
            {
                var character = text[index];

                if (character == '\\')
                {
                    if (index + 1 >= text.Length)
                    {
                        reason = "a chain ends with a backslash that has nothing to escape";

                        return false;
                    }

                    index += 2;

                    continue;
                }

                if (character == '\'')
                {
                    var closing = text.IndexOf('\'', index + 1);

                    if (closing < 0)
                    {
                        reason = "a chain carries a quoted run that never closes";

                        return false;
                    }

                    index = closing + 1;

                    continue;
                }

                if (character == '[')
                {
                    var closing = text.IndexOf(']', index + 1);

                    if (closing < 0)
                    {
                        reason = "a chain carries a label whose closing bracket never arrives";

                        return false;
                    }

                    labels.Add(new LabelToken(index, closing + 1, text.Substring(index, closing - index + 1)));
                    index = closing + 1;

                    continue;
                }

                index++;
            }

            reason = null;

            return true;
        }

        /// <summary>
        /// Whether a chain's filters are one <c>overlay</c>, collecting the filter names found on the
        /// way.
        /// </summary>
        /// <remarks>
        /// The names matter to the caller - they are how a text renderer is spotted - and finding them
        /// is the same walk that answers the question here: a filter name stands at the head of a
        /// chain's body and after every top-level comma, and nowhere else, which is what keeps a
        /// caption or a path reading "ass" from being read as the renderer of that name.
        /// </remarks>
        private static bool TryReadSingleOverlay(string body, out List<string> names)
        {
            var found = new List<string>();
            var single = false;
            var atFilterName = true;
            var index = 0;

            while (index < body.Length)
            {
                var character = body[index];

                if (character == '\\')
                {
                    index += 2;
                    atFilterName = false;

                    continue;
                }

                if (character == '\'')
                {
                    var closing = body.IndexOf('\'', index + 1);
                    index = closing < 0 ? body.Length : closing + 1;
                    atFilterName = false;

                    continue;
                }

                if (character == '[')
                {
                    // A label inside a body means the chain is not the single filter this rewrite
                    // places depth in front of. The caller rejects such chains on their own merits;
                    // this is the same answer, given where it was seen.
                    var closing = body.IndexOf(']', index + 1);
                    index = closing < 0 ? body.Length : closing + 1;
                    single = false;
                    atFilterName = false;

                    continue;
                }

                if (character == ',')
                {
                    single = false;
                    index++;
                    atFilterName = true;

                    continue;
                }

                if (atFilterName && IsNameStart(character))
                {
                    var start = index;

                    while (index < body.Length && IsNameChar(body[index]))
                    {
                        index++;
                    }

                    var name = body.Substring(start, index - start);
                    found.Add(name);

                    single = found.Count == 1
                             && string.Equals(name, OverlayFilterName, StringComparison.Ordinal)
                             && !AnotherFilterFollows(body, index);
                    atFilterName = false;

                    continue;
                }

                if (!char.IsWhiteSpace(character))
                {
                    atFilterName = false;
                }

                index++;
            }

            names = found;

            return single;
        }

        /// <summary>
        /// Whether another filter follows the name that just ended, which is what a top-level comma
        /// means here: an <c>overlay</c> chained with anything else is no longer the single stage this
        /// rewrite can drop out of the graph.
        /// </summary>
        private static bool AnotherFilterFollows(string body, int offset)
            => body.IndexOf(',', offset, body.Length - offset) >= 0;

        /// <summary>One <c>[name]</c> pad label, with the offsets that put it back in its chain.</summary>
        private readonly record struct LabelToken(int Start, int End, string WithBrackets);
    }
}
