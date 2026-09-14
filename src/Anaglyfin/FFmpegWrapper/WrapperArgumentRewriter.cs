using System;
using System.Collections.Generic;
using System.Globalization;
using Anaglyfin.Ffmpeg;
using Anaglyfin.Markers;
using Anaglyfin.Profiles;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Rewrites a received FFmpeg argument vector into the one a profile asks for.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole decision the Anaglyfin FFmpeg wrapper makes, and it is deliberately
/// free of process handling: given the argv Jellyfin built, it returns the argv the real
/// FFmpeg-mvc binary should run. The launcher (T5b) owns starting the process, concurrency
/// and exit codes; keeping this part pure is what lets every command below be asserted
/// without executing anything.
/// </para>
/// <para>
/// <b>Dispatch.</b> Every value introduced by <c>-i</c> is put through
/// <see cref="ProfileMarkerParser.Parse"/>, which leaves exactly three readings: no input
/// is a marker, so the vector is returned unchanged and the server's own command - with
/// its hardware decode choices - runs as written (see
/// <see cref="WrapperRewriteStatus.PassedThrough"/>); exactly one input is a valid marker,
/// so the command is rewritten; or an input presents itself as a marker and fails to parse,
/// which refuses the job outright - a token that wanted to be a marker is not an ordinary
/// media path and must never reach FFmpeg (see <see cref="WrapperRewriteStatus.RejectedMarker"/>).
/// Two valid markers are neither of these and are refused too, for the same reason as a
/// broken one: only one of them can be the input being rewritten.
/// </para>
/// <para>
/// <b>Where arguments go.</b> FFmpeg binds a per-file output option to the file it opens
/// next, so output options are only unambiguous after the last input. Everything this
/// rewriter inserts for the output therefore lands immediately after the last <c>-i</c>
/// value, and everything it examines or removes for a conflict lives in that same output
/// segment: an option written before an input belongs to that input and is left alone. The
/// one thing it writes on the other side of an input is the composed view request - a
/// decoder option is read when the input is opened, so
/// <see cref="ComposedViewInputArgument"/> goes immediately before the <c>-i</c> of the
/// marker's own input and nowhere else.
/// </para>
/// <para>
/// <b>What a rewrite does.</b> The marker token is replaced by the real source path the
/// provider put into it - in the <c>-i</c> value that carried it, and everywhere else the
/// server quoted that same value into its own arguments, which is what makes a server-side
/// <c>subtitles</c> filter read the film rather than the marker
/// (<see cref="AnaglyfinFilterGraphComposer.ReplaceMarkerUrl"/>). Then, through
/// <see cref="IFfmpegProfileArgumentBuilder"/>, the profile's rewrite is applied: a profile
/// that converts a stereo picture asks the input for the composed all-view frames, puts its
/// filter chain <em>in front of</em> whatever the server wrote for the picture rather than
/// behind it or in place of it (see <see cref="PrependToFilterChain"/> and the paragraph
/// below on a server filter graph), and the one profile that converts through a filtergraph
/// brings that graph and - unless the server's own graph consumes its output - the map of its
/// label. Subtitle streams are taken out of the output only where this rewrite renders the
/// subtitles itself: a conversion has nothing to do with the text on the screen, and the
/// server's own subtitle selection - its maps, and the subtitle streams its graph reads -
/// survives a profile rewrite untouched. An audio map that is already on the
/// command, the encoder, the muxer and the HLS arguments are never touched: they are
/// Jellyfin's business, and the product requirement is that Anaglyfin playback differs from
/// stock playback in picture and not in delivery. The one audio argument this rewriter does
/// write is the optional audio map beside its own video map, and only for a command that
/// carried no map at all - see <see cref="OptionalAudioMapValue"/>.
/// </para>
/// <para>
/// <b>The composed view, not a view map.</b> Every converting profile needs the eyes
/// composed into one frame, and this rewriter asks for that at the only moment it can be
/// asked: <c>-view_ids -1</c> immediately before the marker input's <c>-i</c>, so the value
/// is in the decoder before decoding starts. A view <em>specifier</em> - <c>-map 0:v:view:*</c>
/// or a <c>[0:v:view:*]</c> filter label - cannot do this job: it names its view only once
/// the decoder has reported its view list, and FFmpeg-mvc refuses to combine the two ways of
/// asking on one input. So this rewriter never writes a view specifier, and it makes sure one
/// already on the command for the marker input cannot survive either, because leaving it
/// beside the option would be a command FFmpeg rejects.
/// </para>
/// <para>
/// <b>Which video maps are the profile's competition.</b> A profile that maps nothing of its
/// own - every linear conversion, because the composed frames arrive on the very stream the
/// server named - competes with nothing: the server's ordinary video map is the profile's
/// video map, and it stays exactly where and as it was written. Only a profile that brings a
/// graph and therefore a map of its own has a picture to defend, and there two spellings
/// count as competition. A specifier that names the video type - <c>0:v</c>, <c>0:v:0</c>, a
/// filtergraph label - names video whatever else it says, and so does the exclusion of that
/// type, which would take the profile's own picture back out of the output. A plain
/// <c>-map 0:2</c> names nothing of the kind: it is a stream number, and FFmpeg's own grammar
/// gives a number no type. That one is the shape Jellyfin's HLS commands actually carry, and
/// the rewriter only knows which numbered stream is video because the marker says so -
/// <c>video=&lt;index&gt;</c> is the provider naming the video stream of its own source by
/// its stream index, the same number the server's <c>-map 0:&lt;index&gt;</c> spends on it. A
/// numbered map for any other stream, and every audio, subtitle, whole-file and exclusion map
/// that does not name video, stays exactly where the server put it. One profile has no map to
/// defend at all even though it brings a graph: the one whose label the server's own graph
/// consumes, which is a picture the server is already feeding to its encoder (see the paragraph
/// below).
/// </para>
/// <para>
/// <b>Where the server put the picture.</b> A server that filters the video itself does it in one
/// of two spellings, and a profile that converts a picture has to land in front of both. A
/// <c>-vf</c> chain is a single chain of stages, so the profile's own chain goes in front of the
/// server's (see <see cref="PrependToFilterChain"/>). A <c>-filter_complex</c> graph is not a
/// chain somebody else can join, but it does not need to be: a graph links its chains by label,
/// so the profile arrives as one more chain of its own - the composed source stream through the
/// profile's conversion, into a label of its own - and the server's chains are retargeted from the
/// source video label onto that label
/// (<see cref="AnaglyfinFilterGraphComposer.EditVideoSourceReferences"/>). The graph then filters
/// what the profile produced, at the position the server wrote its filters for, which is what
/// keeps its scale sized against the converted frame instead of against the frame the profile has
/// not made yet. Nothing else in the text is touched: its filters, its own labels, the subtitle
/// streams it reads and the pads it feeds the encoder all stay as the server spelled them. A
/// profile whose label a server graph consumes then maps nothing of its own, because the graph
/// consuming that label is the thing feeding the output picture, and mapping it as well would put
/// a second video in the way of the one the server mapped.
/// </para>
/// <para>
/// <b>Where it refuses.</b> A profile that owns the output's video pipeline cannot share that
/// pipeline with a filtergraph whose contents it cannot see, and cannot be joined to a graph that
/// never asks for the picture it converts: this wrapper inserts a chain and retargets a label, it
/// does not parse or graft foreign filter text (which is also a security requirement - nothing in
/// a received command line is ever treated as filter syntax). So a graph read from a
/// <c>-filter_complex_script</c> file refuses the job, because nothing in the vector says what it
/// reads; so does a graph that never references this input's video stream, because the conversion
/// would be left with nothing to feed; so does one that reads the source through a view
/// specifier, which a composed decode refuses to name; so does one that already carries the label
/// this profile writes. So does a marker that is not the first input, because every argument the
/// builder emits addresses input <c>0</c>, and so does a second valid marker anywhere in the
/// vector: a rewrite resolves exactly one marker, and the one left behind would reach FFmpeg as a
/// file to open. And so does a command that copies its video - <c>-c copy</c>, <c>-c:v copy</c>,
/// <c>-codec:v:0 copy</c>, <c>-vcodec copy</c> - because copying a stream is the server deciding
/// that no conversion happens on this output, which would leave the profile's arguments present
/// but inert (see <see cref="WrapperRewriteStatus.ServerChoseVideoCopy"/>). Every one of these
/// refusals says which rule the command broke, and every one returns no vector at all.
/// </para>
/// </remarks>
public sealed class WrapperArgumentRewriter : IWrapperArgumentRewriter
{
    /// <summary>The FFmpeg option that introduces an input file; a marker rides on its value.</summary>
    public const string InputFileArgument = "-i";

    /// <summary>The <c>-map</c> option token, shared with the command builder.</summary>
    public const string MapArgument = FfmpegProfileArgumentBuilder.MapArgument;

    /// <summary>The <c>-vf</c> option token, shared with the command builder.</summary>
    public const string VideoFilterArgument = FfmpegProfileArgumentBuilder.VideoFilterArgument;

    /// <summary>The <c>-filter_complex</c> option token, shared with the command builder.</summary>
    public const string FilterComplexArgument = FfmpegProfileArgumentBuilder.FilterComplexArgument;

    /// <summary>
    /// The <c>-filter_complex_script</c> option token. It carries a filtergraph just like
    /// <see cref="FilterComplexArgument"/> does, only from a file, and is recognised for
    /// the same reason.
    /// </summary>
    public const string FilterComplexScriptArgument = "-filter_complex_script";

    /// <summary>The <c>-sn</c> option token: the output carries no subtitle stream.</summary>
    public const string DisableSubtitlesArgument = "-sn";

    /// <summary>
    /// The <c>copy</c> codec value: what a codec option carries when it asks FFmpeg to move
    /// a stream through the output untouched instead of encoding it.
    /// </summary>
    public const string StreamCopyValue = "copy";

    /// <summary>The long spelling of the option that sets a stream's codec.</summary>
    public const string CodecOptionName = "codec";

    /// <summary>The short spelling of the option that sets a stream's codec.</summary>
    public const string ShortCodecOptionName = "c";

    /// <summary>The option that sets the video codec without naming a stream specifier.</summary>
    public const string VideoCodecOptionName = "vcodec";

    /// <summary>
    /// The <c>-map</c> value that adds an input's audio to an output this rewriter has
    /// already given a video map of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A command carrying no <c>-map</c> at all asks FFmpeg to choose the output's streams,
    /// and that choice is one video <em>and</em> one audio. Naming a video stream - which is
    /// exactly what a profile map does - replaces that choice with the one stream named, so
    /// without this the rewritten command would export a silent picture and nothing would
    /// report it.
    /// </para>
    /// <para>
    /// The trailing <c>?</c> is what keeps the map optional: FFmpeg then exports the audio
    /// that is there and stays silent about a file that has none, instead of refusing a
    /// source that simply carries no audio track.
    /// </para>
    /// </remarks>
    public const string OptionalAudioMapValue = "0:a?";

    /// <summary>
    /// The <c>-view_ids</c> option token, shared with the command builder: the per-input
    /// decoder option that asks an input for its composed all-view picture.
    /// </summary>
    public const string ComposedViewInputArgument = FfmpegProfileArgumentBuilder.ComposedViewInputOption;

    /// <summary>
    /// The value of <see cref="ComposedViewInputArgument"/> that means "every view, composed
    /// into one frame", shared with the command builder.
    /// </summary>
    public const string ComposedViewInputValue = FfmpegProfileArgumentBuilder.ComposedViewInputValue;

    /// <summary>
    /// The prefix a <c>-map</c> value carries when it numbers a stream of the first input -
    /// the input a marker is allowed to be, and therefore the only input whose numbered
    /// maps the marker's <c>video=&lt;index&gt;</c> can identify.
    /// </summary>
    private const string FirstInputMapPrefix = "0:";

    /// <summary>
    /// The <c>-map</c> value naming the video stream of the first input when no stream index is
    /// known for it. Every video stream of that input would then be meant, which is why an
    /// index is preferred wherever the marker carries one.
    /// </summary>
    private const string FirstInputVideoTypeMap = "0:v";

    /// <summary>
    /// The prefixes a <c>-map</c> or filter-label detail carries when it selects a view of a
    /// stream rather than the stream itself: <c>view:</c> by id or <c>all</c>, <c>vidx:</c>
    /// by view index, <c>vpos:</c> by view position.
    /// </summary>
    /// <remarks>
    /// These are the specifiers FFmpeg-mvc parses after the stream specifier of a map value
    /// (<c>0:v:view:all</c>, <c>0:v:vidx:1</c>, <c>0:v:vpos:left</c>), and the reason they
    /// are listed here as a set is that the composed input this rewriter configures makes
    /// every one of them an error on the input it set the option for - so none of them may
    /// survive a rewrite, whichever of the three the command happened to carry.
    /// </remarks>
    private static readonly string[] ViewSpecifierPrefixes = ["view:", "vidx:", "vpos:"];

    private readonly IProfileCatalog _catalog;
    private readonly IFfmpegProfileArgumentBuilder _builder;

    /// <summary>
    /// Initializes a rewriter over a profile catalog and a command builder.
    /// </summary>
    /// <param name="catalog">
    /// The profile allowlist a marker id is resolved against. Out of process this is
    /// <see cref="Profiles.ProfileCatalog"/>, the same built-in catalog the media source
    /// provider offered versions from.
    /// </param>
    /// <param name="builder">
    /// The command builder a parsed profile becomes arguments with; production uses
    /// <see cref="FfmpegProfileArgumentBuilder.Shared"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public WrapperArgumentRewriter(IProfileCatalog catalog, IFfmpegProfileArgumentBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(builder);

        _catalog = catalog;
        _builder = builder;
    }

    /// <summary>
    /// Gets the shared instance wired to the built-in catalog and the shared builder.
    /// </summary>
    /// <remarks>
    /// The wrapper is an out-of-process executable and has no access to the plugin's
    /// container, so this is the entry point it uses. Both collaborators are stateless,
    /// which makes the instance safe to share across requests.
    /// </remarks>
    public static WrapperArgumentRewriter Shared { get; } =
        new(new ProfileCatalog(), FfmpegProfileArgumentBuilder.Shared);

    /// <inheritdoc />
    public WrapperRewriteResult Rewrite(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var firstInputIndex = -1;
        var lastInputIndex = -1;
        var markerIndex = -1;
        var markerCount = 0;
        MarkerParseResult? marker = null;
        MarkerParseResult? rejected = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            // A trailing "-i" without a value is not Anaglyfin's to judge: there is no
            // token to classify, and FFmpeg's own argument check is the right place for it.
            if (!IsInputOption(arguments[index]) || index + 1 >= arguments.Count)
            {
                continue;
            }

            var valueIndex = index + 1;
            if (firstInputIndex < 0)
            {
                firstInputIndex = valueIndex;
            }

            lastInputIndex = valueIndex;

            // The value is data, never an option: step over it so a path that happens to
            // read like an option token cannot be mistaken for one.
            index = valueIndex;

            var parsed = ProfileMarkerParser.Parse(arguments[valueIndex]);
            if (parsed.IsSuccess)
            {
                // Only the first marker is kept as the one to resolve; the count is what
                // notices a second one, which is a command shape rather than a profile
                // question and is refused below.
                markerCount++;

                if (marker is null)
                {
                    marker = parsed;
                    markerIndex = valueIndex;
                }
            }
            else if (rejected is null && parsed.Status != MarkerParseStatus.NotMarker)
            {
                rejected = parsed;
            }
        }

        // A marker-shaped token that failed to parse outranks everything else in the
        // command, including a valid marker elsewhere: a vector carrying both was not
        // assembled by the media source provider, and guessing which half to believe is
        // how a marker URL ends up being opened as a media file.
        if (rejected is not null)
        {
            return WrapperRewriteResult.Failure(
                WrapperRewriteStatus.RejectedMarker,
                rejected.Error ?? "An input of the command is not a valid Anaglyfin marker.",
                rejected.Status);
        }

        if (marker is null)
        {
            return WrapperRewriteResult.PassedThrough(arguments);
        }

        // A second valid marker is not a second job and not part of the first one either.
        // Exactly one input can be the file this rewrite is about, so the others would keep
        // their marker text and FFmpeg would open that marker as a media file - the one
        // outcome the whole marker contract exists to prevent. A vector carrying two of them
        // was not assembled by the media source provider, and is refused rather than guessed
        // at.
        if (markerCount > 1)
        {
            return WrapperRewriteResult.Failure(
                WrapperRewriteStatus.UnsupportedCommandShape,
                "More than one input of the command carries an Anaglyfin marker, and a rewrite resolves exactly one of them; the marker left behind would reach FFmpeg as an input file.");
        }

        return RewriteMarker(arguments, marker.Marker!, markerIndex, firstInputIndex, lastInputIndex);
    }

    /// <summary>
    /// Applies the profile of one validated marker to the command that carried it.
    /// </summary>
    /// <param name="arguments">The received vector.</param>
    /// <param name="marker">The parsed marker.</param>
    /// <param name="markerIndex">Index of the token the marker arrived in.</param>
    /// <param name="firstInputIndex">Index of the first input value on the command.</param>
    /// <param name="lastInputIndex">Index of the last input value on the command.</param>
    /// <returns>The rewrite outcome.</returns>
    private WrapperRewriteResult RewriteMarker(
        IReadOnlyList<string> arguments,
        ProfileMarker marker,
        int markerIndex,
        int firstInputIndex,
        int lastInputIndex)
    {
        // Every map value and every filter input label the builder emits addresses input
        // 0, which is the marker's own file only while the marker is the first input.
        // Rewriting any other shape would apply the profile to somebody else's stream.
        if (markerIndex != firstInputIndex)
        {
            return WrapperRewriteResult.Failure(
                WrapperRewriteStatus.UnsupportedCommandShape,
                "An Anaglyfin marker has to be the first input of the command, because every profile argument addresses input 0.");
        }

        if (!_catalog.TryGetProfile(marker.ProfileId, out var profile))
        {
            return WrapperRewriteResult.Failure(
                WrapperRewriteStatus.UnknownProfile,
                $"'{marker.ProfileId}' parsed from the marker is not a profile of this build's catalog, so no command can be built for it.");
        }

        // Both collaborators refuse instead of improvising: the builder rejects an id, an
        // output code or a colour set it was not built to serve, the burn-in rejects a path
        // that cannot denote a file. Neither refusal is a reason to run the received command
        // as written, which is what a degraded pass-through would do, so both become a
        // refusal of this rewrite. The marker's video stream index travels with the profile
        // because the one profile that converts through a graph has to name the stream it
        // reads, and the marker is what names it.
        ProfileRewrite rewrite;
        try
        {
            rewrite = _builder.Build(profile, ToSubtitleBurnIn(marker), marker.VideoStreamIndex);
        }
        catch (ArgumentException exception)
        {
            return WrapperRewriteResult.Failure(WrapperRewriteStatus.UnknownProfile, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return WrapperRewriteResult.Failure(WrapperRewriteStatus.UnknownProfile, exception.Message);
        }

        return ApplyRewrite(arguments, marker, markerIndex, lastInputIndex, rewrite);
    }

    /// <summary>
    /// Splices a built rewrite into the command that carried the marker: the composed view
    /// request in front of the marker's <c>-i</c>, everything else in its output segment, and
    /// the conversion in front of whatever the server wrote for the picture - a <c>-vf</c>
    /// chain or the chains of a <c>-filter_complex</c> graph.
    /// </summary>
    /// <remarks>
    /// The order below is the order the questions have to be asked in. What the server wrote for
    /// the picture decides where this profile's conversion can go; where the conversion goes
    /// decides whether the profile has a picture of its own for another video map to compete
    /// with; and a command whose picture is written somewhere the conversion cannot reach is
    /// refused before anything is spliced, so that no refusal depends on a decision taken after
    /// it and no half-decision reaches the vector.
    /// </remarks>
    private static WrapperRewriteResult ApplyRewrite(
        IReadOnlyList<string> arguments,
        ProfileMarker marker,
        int markerIndex,
        int lastInputIndex,
        ProfileRewrite rewrite)
    {
        // What this profile may insist on, read off the rewrite instead of off the argument
        // list it carries: only a profile that converts the picture has a video pipeline to
        // defend, and only a profile that converts stereo needs the composed decode. The
        // two are not the same question - full SBS converts the composed frame and converts
        // nothing after it - and neither one is answered by asking whether a map was
        // generated.
        var ownsVideoPipeline = rewrite.OwnsVideoPipeline;
        var needsComposedView = rewrite.RequiresComposedViewInput;

        // The chain this profile puts in front of the server's filters: its conversion, with
        // the burn-in last where one was asked for. Null for the profiles that convert nothing
        // (2D, and full SBS with no text to burn), and for the one profile that converts through
        // a graph, whose burn-in travels behind its mapped label instead.
        var profileFilter = ValueAfter(rewrite.InsertArguments, VideoFilterArgument);

        // What the server wrote for the picture, and where it wrote it: FFmpeg keeps the last
        // value of a repeated option, so both the chain and the graph are read where FFmpeg
        // would read them.
        var server = ReadServerFilterText(arguments, lastInputIndex);

        // A server graph can carry this profile's conversion, and can carry nothing else: the
        // profile's text goes in as one more chain of that graph, and the server's references to
        // the source picture are retargeted onto what that chain produces. A profile with
        // nothing to convert has nothing to put there, and leaves the server's graph exactly as
        // the server wrote it.
        var mergesIntoServerGraph = server.Graph is not null
                                    && (rewrite.FilterComplex is not null || profileFilter is not null);

        // The scan runs wherever a graph is present, whether or not anything is merged into it:
        // the facts it reports - whether the graph reads the composed stream at all, whether it
        // names a view of it, whether it renders subtitles itself - are what several of the
        // decisions below rest on, and one of them, the view specifier, refuses even a profile
        // that writes nothing into the graph.
        var graphEdit = server.Graph is null
            ? null
            : AnaglyfinFilterGraphComposer.EditVideoSourceReferences(
                server.Graph,
                marker.VideoStreamIndex,
                rewrite.FilterComplex is not null
                    ? AnaglyfinFilterGraphComposer.CustomOutputLabel
                    : AnaglyfinFilterGraphComposer.ProfileOutputLabel);

        // Whether the server's graph is the thing reading this profile's converted picture, and
        // therefore whether the profile's own label needs a map in front of it. Consumed by a
        // graph, the label is already feeding the encoder through the server's own chains, and
        // mapping it as well would put a second video in the way of the picture the server
        // mapped.
        var graphConsumesProfileLabel = mergesIntoServerGraph && rewrite.FilterComplex is not null;

        // The same fact one step further: the linear chain that went into the graph has to be
        // kept out of the output segment as well, or the same filters would run twice.
        var linearChainMergedIntoGraph = mergesIntoServerGraph && rewrite.FilterComplex is null;

        // Whether this profile has a picture of its own to defend in the output. A linear
        // conversion runs on the very stream the server already named, so that map is the
        // profile's own video map and removing it would leave the converted picture with nothing
        // to travel on; a graph whose label the server's graph consumes has handed its picture to
        // those same chains. Neither has a map to insert or a competing map to remove: only a
        // graph standing on its own does.
        var mapsItsOwnVideo = rewrite.VideoMap is not null && !graphConsumesProfileLabel;

        // What to name the marker input's video stream with wherever the profile needs to
        // write that name itself: the stream the marker named, since that is the one stream
        // the composed frames arrive on.
        var markerVideoMap = MarkerVideoStreamMap(marker);

        // Whether the received output segment named any stream of its own. A command that
        // named none - the map-less HLS shape this rewriter has to survive as well as the one
        // carrying the server's own maps - is asking FFmpeg to pick the output's streams, and
        // that pick delivers a video *and* an audio. The moment this rewriter inserts a map
        // of its own, the naming is explicit and the pick is gone. A command that did carry
        // maps had already decided its own streams, audio included, and that decision is
        // Jellyfin's to keep.
        var mapsReceived = false;

        // The received vector is never mutated: removals and replacements are recorded by
        // index and applied while the new vector is built, so one pass over the output
        // segment is enough and no index can shift under another decision.
        var removals = new HashSet<int>();
        var replacements = new Dictionary<int, string> { [markerIndex] = marker.SourcePath };

        // Subtitle suppression is the burn-in's own consequence and never the conversion's. A
        // profile that converts the picture says nothing about the text on it: the server picked
        // those subtitles, the server's maps and the server's graph reference them, and a wrapper
        // that answered that question a second time - <c>-sn</c> with the maps taken out - is how
        // a subtitle disappears from a film. Only where this rewrite renders the subtitles itself
        // do the streams the server selected become a second copy of the same text, and not even
        // then where the server's own filter text is already rendering them.
        var serverHandlesSubtitles = (graphEdit?.HandlesSubtitles ?? false)
                                     || AnaglyfinFilterGraphComposer.HandlesSubtitles(server.Chain);
        var suppressSubtitleStreams = rewrite.ShouldSuppressSubtitleStreams && !serverHandlesSubtitles;

        var subtitlesAlreadyDisabled = false;

        // Whether the server told FFmpeg to copy the video instead of encoding one. The
        // profile's composed decode and filters can only act on a picture they produce, so
        // on such a command the whole rewrite would be decoration; the check and the refusal
        // live together below.
        var serverChoseVideoCopy = false;

        for (var index = lastInputIndex + 1; index < arguments.Count; index++)
        {
            var token = arguments[index];

            if (IsMapOption(token) && index + 1 < arguments.Count)
            {
                // Recorded before the conflict test below: a map this rewriter removes is
                // still proof that the command chose its own streams.
                mapsReceived = true;

                var mapValue = arguments[index + 1];
                if (mapsItsOwnVideo
                    && (IsConflictingVideoMap(mapValue) || IsNumberedVideoMap(mapValue, marker.VideoStreamIndex)))
                {
                    removals.Add(index);
                    removals.Add(index + 1);
                }
                else if (suppressSubtitleStreams && IsSubtitleMap(mapValue))
                {
                    removals.Add(index);
                    removals.Add(index + 1);
                }
                else if (needsComposedView && !mapsItsOwnVideo && NamesFirstInput(mapValue))
                {
                    // A composed input is configured by option, and FFmpeg-mvc refuses a view
                    // specifier on an input configured that way. So the two shapes of the
                    // marker input's video selection that survive every other rule have to be
                    // settled here, or the command this rewriter hands FFmpeg would be one
                    // FFmpeg refuses to run at all.
                    if (IsViewSpecifierMap(mapValue))
                    {
                        if (IsStreamExclusion(mapValue))
                        {
                            // Nobody's ordinary video map: it was taking a view-selected
                            // picture out of the output, and under the composed decode there
                            // is no separate view selection to take out.
                            removals.Add(index);
                            removals.Add(index + 1);
                        }
                        else
                        {
                            // The same stream, named without a view: what the composed decode
                            // delivers on exactly the map the server already wrote.
                            replacements[index + 1] = WithoutViewSelection(mapValue, markerVideoMap);
                        }
                    }
                    else if (IsStreamExclusion(mapValue) && StreamSpecifierType(mapValue) == 'v')
                    {
                        // The one exclusion that still reaches a profile that maps nothing:
                        // written after the server's own video map it deletes the very stream
                        // the profile is about to convert, which is an Anaglyfin output with
                        // no picture in it - and, since FFmpeg refuses an exclusion that
                        // matches nothing left to exclude, usually not even that.
                        removals.Add(index);
                        removals.Add(index + 1);
                    }
                }
            }
            else if (IsSubtitleDisableOption(token))
            {
                subtitlesAlreadyDisabled = true;
            }
            else if (index + 1 < arguments.Count && IsVideoCodecCopyOption(token, arguments[index + 1]))
            {
                serverChoseVideoCopy = true;
            }
        }

        // Checked before the filter graph, because it is the earlier question: a command
        // that copies its video has decided not to produce a picture at all, whatever else
        // is on the command line.
        if (ownsVideoPipeline && serverChoseVideoCopy)
        {
            return WrapperRewriteResult.Failure(
                WrapperRewriteStatus.ServerChoseVideoCopy,
                "The command copies its video stream, so the composed view and the filters this profile asks for would have no encoded output to reach; the version was asked to convert a picture the server decided to pass through.");
        }

        // A graph this wrapper cannot read - because it lives in a script file, or because the
        // option carrying it has no graph text beside it - is not a graph anything can be put in
        // front of. Running it as written instead would put the profile's filters and the
        // server's picture into one command with neither seeing the other, and the profile would
        // report a converted picture it had not produced.
        if (ownsVideoPipeline && (server.GraphReadFromScript || (server.GraphOptionCount > 0 && server.Graph is null)))
        {
            return WrapperRewriteResult.Failure(
                WrapperRewriteStatus.IncompatibleFilterGraph,
                "The command's filter graph is not written in the command: it is read from a file the argument vector names, or the option carrying it has no graph text beside it, so nothing here says which streams that graph reads or what it feeds the encoder, and this profile's conversion cannot be put in front of text the wrapper cannot see.");
        }

        if (graphEdit is not null && mergesIntoServerGraph)
        {
            // Several graphs are several pictures, and the vector does not say which of them the
            // output is drawn from; merging into the last one while an earlier one still reads
            // the unconverted stream would be a guess at which picture the user asked for.
            if (server.GraphOptionCount > 1)
            {
                return WrapperRewriteResult.Failure(
                    WrapperRewriteStatus.IncompatibleFilterGraph,
                    "The command carries more than one filter graph, and this profile's conversion can only be put in front of the graph the output's picture is drawn from, which no command with several of them says.");
            }

            if (graphEdit.Replacements == 0)
            {
                return WrapperRewriteResult.Failure(
                    WrapperRewriteStatus.IncompatibleFilterGraph,
                    "The command's filter graph never reads the video stream this profile converts, so the conversion could only be grafted into somebody else's filter text - which this wrapper does not parse - or be left with nothing to feed, which FFmpeg refuses to run.");
            }

            if (graphEdit.UsesAnaglyfinLabel)
            {
                return WrapperRewriteResult.Failure(
                    WrapperRewriteStatus.IncompatibleFilterGraph,
                    "The command's filter graph already carries a label this profile writes, so merging the conversion into that graph would leave one output pad with two producers, which FFmpeg reports as a graph it cannot parse.");
            }
        }

        // Asked of the graph whether or not anything was merged into it: a view specifier is a
        // request for one eye of a stream, and a decode that was asked for its composed all-view
        // picture refuses that request outright.
        if (needsComposedView && graphEdit?.CarriesViewSpecifier == true)
        {
            return WrapperRewriteResult.Failure(
                WrapperRewriteStatus.IncompatibleFilterGraph,
                "The command's filter graph names a view of the video stream this profile composes at the input, and a decode configured through the composed view request refuses a view specifier, so this graph cannot be given the picture the profile asks for.");
        }

        // The composed view, asked of the input itself: a decoder option is read when its
        // input is opened, so it belongs immediately before the "-i" the marker arrived on -
        // never in the output segment, where it would be an option for the encoder and would
        // compose nothing at all. A profile served by the decoder's own base view asks for
        // nothing, and nothing is written.
        IReadOnlyList<string> inputInsertions = Array.Empty<string>();

        if (needsComposedView)
        {
            var unsettled = SettleComposedViewInput(arguments, markerIndex - 1, replacements, out inputInsertions);

            if (unsettled is not null)
            {
                return WrapperRewriteResult.Failure(WrapperRewriteStatus.UnsupportedCommandShape, unsettled);
            }
        }

        var insertions = new List<string>();

        if (mergesIntoServerGraph)
        {
            // The profile's own text goes in as the graph's first chain - the composed stream
            // through the conversion into the profile's label, or the whole of the graph profile's
            // graph, which already ends in its own label - and the server's chains follow behind
            // it, unchanged but for the labels now naming what the profile produces. The server's
            // option keeps the position the server wrote it at: a graph is read as a whole, and
            // the whole of it belongs to the output that maps a pad out of it.
            var profileSegment = rewrite.FilterComplex
                ?? string.Concat(
                    AnaglyfinFilterGraphComposer.ComposedVideoStreamLabel(marker.VideoStreamIndex),
                    profileFilter!,
                    AnaglyfinFilterGraphComposer.ProfileOutputLabel);

            replacements[server.GraphValueIndex] = profileSegment + ";" + graphEdit!.Graph;
        }
        else if (rewrite.FilterComplex is { } profileGraph)
        {
            insertions.Add(FilterComplexArgument);
            insertions.Add(profileGraph);
        }

        if (mapsItsOwnVideo && rewrite.VideoMap is { } videoMap)
        {
            insertions.Add(MapArgument);
            insertions.Add(videoMap);

            // Naming the profile's video is what turns an un-named output into a named one,
            // and a named output exports exactly what is named. On a command that carried no
            // map of its own, the audio the automatic selection used to deliver therefore
            // needs a name of its own here - otherwise the 3D version of a film plays silent
            // and no layer reports it. On a command that did carry maps, whatever audio it
            // chose (including none) is already in the segment and stays untouched.
            //
            // This is the only branch that can write the argument, and the only one that has
            // to: a profile keeping the server's video map changes nothing about which
            // streams the output names, so the audio the command already carried - or already
            // left to FFmpeg's pick - is still carried.
            if (!mapsReceived)
            {
                insertions.Add(MapArgument);
                insertions.Add(OptionalAudioMapValue);
            }
        }

        // The builder already merged the profile conversion and the subtitle burn-in into
        // one chain where they belong together, so the chain to splice is the one in
        // InsertArguments rather than either half on its own - unless that chain is already
        // standing at the head of the server's graph, which is where a chain in front of a
        // graph belongs and where writing it a second time would run it twice.
        if (profileFilter is not null && !linearChainMergedIntoGraph)
        {
            if (server.ChainValueIndex >= 0)
            {
                replacements[server.ChainValueIndex] = PrependToFilterChain(profileFilter, server.Chain!);
            }
            else
            {
                insertions.Add(VideoFilterArgument);
                insertions.Add(profileFilter);
            }
        }

        // Subtitles reach a converted picture through the burn-in filter alone; a mapped
        // or codec-level subtitle stream on top of it would render the text twice. Where the
        // server renders them itself, its filter text is the answer to that question already.
        if (suppressSubtitleStreams && !subtitlesAlreadyDisabled)
        {
            insertions.Add(DisableSubtitlesArgument);
        }

        return WrapperRewriteResult.Rewritten(
            ReplaceMarkerUrls(
                Rebuild(
                    arguments,
                    markerIndex - 1,
                    inputInsertions,
                    lastInputIndex,
                    insertions,
                    removals,
                    replacements),
                marker),
            rewrite.ProfileId);
    }

    /// <summary>
    /// Reads the filter text the server wrote for the picture in the output segment.
    /// </summary>
    /// <param name="arguments">The received vector.</param>
    /// <param name="lastInputIndex">The index of the last input value on the command.</param>
    /// <returns>
    /// The graph and the chain it found, where it found them, and whether the output segment
    /// carries a second graph or a graph this wrapper cannot read at all.
    /// </returns>
    /// <remarks>
    /// One pass, and no reading of the text itself: what a graph says is
    /// <see cref="AnaglyfinFilterGraphComposer"/>'s business, and what the two spellings mean to
    /// this rewrite is only that the server, not this wrapper, wrote the picture's pipeline. Both
    /// the chain and the graph are read at their last occurrence, because FFmpeg keeps the last
    /// value of a repeated option and a rewrite that extended an ignored one would be writing into
    /// a chain that never runs.
    /// </remarks>
    private static ServerFilterText ReadServerFilterText(IReadOnlyList<string> arguments, int lastInputIndex)
    {
        string? graph = null;
        var graphValueIndex = -1;
        var graphOptionCount = 0;
        var graphReadFromScript = false;
        string? chain = null;
        var chainValueIndex = -1;

        for (var index = lastInputIndex + 1; index < arguments.Count; index++)
        {
            var token = arguments[index];

            if (IsVideoFilterOption(token) && index + 1 < arguments.Count)
            {
                chain = arguments[index + 1];
                chainValueIndex = index + 1;
            }
            else if (string.Equals(token, FilterComplexScriptArgument, StringComparison.Ordinal))
            {
                // A graph in a file is exactly as much the server's pipeline as one written
                // inline, and exactly as unread here: the vector carries a filename, not a graph.
                graphReadFromScript = true;
            }
            else if (IsFilterGraphOption(token))
            {
                graphOptionCount++;

                // A graph option whose value is missing or empty is not a graph anything can be
                // put in front of, so it is recorded as an option seen and not as text read: the
                // refusal that belongs to it is the same one a script carries, and it lives with
                // that refusal rather than being a second rule here.
                if (index + 1 < arguments.Count && !string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    graph = arguments[index + 1];
                    graphValueIndex = index + 1;
                }
            }
        }

        return new ServerFilterText(graph, graphValueIndex, graphOptionCount, graphReadFromScript, chain, chainValueIndex);
    }

    /// <summary>
    /// Replaces the marker URL wherever a rewritten argument quotes it, so that no argument of
    /// the command handed to FFmpeg still holds marker text.
    /// </summary>
    /// <param name="rewritten">The vector this rewriter built, which it owns and may edit.</param>
    /// <param name="marker">The marker the command's first input arrived as.</param>
    /// <returns>The same vector, with the marker text replaced where it was quoted.</returns>
    /// <remarks>
    /// The <c>-i</c> value is already the real path, but a server that burns subtitles in writes
    /// the path of the file it is transcoding into its own filter arguments as well, so a marker
    /// can survive a rewrite in a <c>subtitles=</c> filter while never appearing as an input.
    /// Every argument is asked - which costs nothing on a command this size, and is the only way
    /// to be sure the marker text that must never reach FFmpeg does not reach it in some slot this
    /// rewriter did not look at.
    /// </remarks>
    private static List<string> ReplaceMarkerUrls(List<string> rewritten, ProfileMarker marker)
    {
        for (var index = 0; index < rewritten.Count; index++)
        {
            var value = AnaglyfinFilterGraphComposer.ReplaceMarkerUrl(rewritten[index], marker);

            if (!ReferenceEquals(value, rewritten[index]))
            {
                rewritten[index] = value;
            }
        }

        return rewritten;
    }

    /// <summary>
    /// Puts the composed all-view request on the input the marker arrived on: written in front
    /// of its <c>-i</c> when the command asked for nothing, repaired where the command asked
    /// for something else, and left exactly as written where the command already asked for
    /// this.
    /// </summary>
    /// <param name="arguments">The received vector.</param>
    /// <param name="inputOptionIndex">Index of the <c>-i</c> token that carried the marker.</param>
    /// <param name="replacements">The pending replacements, one per repaired value index.</param>
    /// <param name="inputInsertions">
    /// The arguments to write in front of <paramref name="inputOptionIndex"/>, empty whenever
    /// the command already carried a request.
    /// </param>
    /// <returns>
    /// The reason this command's view state cannot be settled, or null when it can. Exactly one
    /// shape cannot: a view option whose value the command does not carry with it, which would
    /// leave the picture this profile needs to a guess across an option boundary - and the
    /// guess it would need is the very thing FFmpeg would have to guess too.
    /// </returns>
    private static string? SettleComposedViewInput(
        IReadOnlyList<string> arguments,
        int inputOptionIndex,
        IDictionary<int, string> replacements,
        out IReadOnlyList<string> inputInsertions)
    {
        var viewSelections = FindViewSelections(arguments, inputOptionIndex);

        if (viewSelections.Count == 0)
        {
            inputInsertions = new[] { ComposedViewInputArgument, ComposedViewInputValue };
            return null;
        }

        // Whatever the command already said stands, so nothing is written in front of it.
        inputInsertions = Array.Empty<string>();

        foreach (var selection in viewSelections)
        {
            if (!selection.HasValue)
            {
                return "The input this profile converts carries a view selection with no value carried beside it, so the composed all-view picture this profile needs could only be settled by guessing what the command meant to ask for.";
            }

            // Already every view composed: left as its author spelled it, because a second
            // request on one input is at best a duplicate. Anything else - one eye, one view,
            // an empty list - is the wrong picture for this profile, and repairing the value
            // where it stands beats stacking a contradicting request in front of the input.
            if (!string.Equals(selection.Value, ComposedViewInputValue, StringComparison.Ordinal))
            {
                replacements[selection.ValueIndex] = selection.ValueInsideOption
                    ? ComposedViewInputArgument + "=" + ComposedViewInputValue
                    : ComposedViewInputValue;
            }
        }

        return null;
    }

    /// <summary>
    /// Turns the subtitle ordinal of a marker into the burn-in the profile asks for.
    /// </summary>
    /// <remarks>
    /// The filter re-opens the film by name to render its text, so the burn-in carries the
    /// marker's real source path and not the marker itself. A marker without an ordinal is the
    /// subtitle-free version: the profile converts the picture and leaves whatever subtitles the
    /// server picked exactly as the server picked them.
    /// </remarks>
    private static SubtitleBurnIn? ToSubtitleBurnIn(ProfileMarker marker)
        => marker.SubtitleOrdinal is int ordinal ? new SubtitleBurnIn(marker.SourcePath, ordinal) : null;

    /// <summary>
    /// Builds the rewritten vector: replacements in place, removals dropped, and the two
    /// insertion points honoured - input-side arguments immediately before the option that
    /// opens the marker's input, output-side ones after the last input value.
    /// </summary>
    /// <param name="arguments">The received vector, which is never mutated.</param>
    /// <param name="insertBeforeOptionIndex">
    /// The index of the option token the input-side arguments go in front of, or -1 when
    /// there are none. An option naming the file to open next is only read when that file is
    /// opened, so this is the position a per-input option has to sit in.
    /// </param>
    /// <param name="inputInsertions">The input-side arguments, in order.</param>
    /// <param name="insertAfterIndex">
    /// The index of the last input value; the output-side arguments follow it.
    /// </param>
    /// <param name="insertions">The output-side arguments, in order.</param>
    /// <param name="removals">Indexes dropped from the result.</param>
    /// <param name="replacements">Indexes whose value is written as something else.</param>
    /// <returns>
    /// The vector to hand to FFmpeg, as a list the caller may still edit in place - which is
    /// exactly what the marker sweep after this method does.
    /// </returns>
    private static List<string> Rebuild(
        IReadOnlyList<string> arguments,
        int insertBeforeOptionIndex,
        IReadOnlyList<string> inputInsertions,
        int insertAfterIndex,
        IReadOnlyList<string> insertions,
        HashSet<int> removals,
        IReadOnlyDictionary<int, string> replacements)
    {
        var rewritten = new List<string>(arguments.Count + insertions.Count + inputInsertions.Count);

        for (var index = 0; index < arguments.Count; index++)
        {
            if (index == insertBeforeOptionIndex)
            {
                rewritten.AddRange(inputInsertions);
            }

            if (!removals.Contains(index))
            {
                rewritten.Add(replacements.TryGetValue(index, out var replacement) ? replacement : arguments[index]);
            }

            if (index == insertAfterIndex)
            {
                rewritten.AddRange(insertions);
            }
        }

        return rewritten;
    }

    /// <summary>
    /// Reads the value that follows an option token in a generated argument list.
    /// </summary>
    private static string? ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Puts the profile's chain in front of an existing <c>-vf</c> chain.
    /// </summary>
    /// <param name="profileFilter">The profile's own chain, conversion first, burn-in last.</param>
    /// <param name="existingChain">The chain already on the command, written by the server.</param>
    /// <returns>
    /// One comma-chained <c>-vf</c> value with the profile's filters running first. An empty
    /// existing chain is not chained, because FFmpeg reads a leading comma as an empty filter
    /// name.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why in front.</b> A <c>-vf</c> chain runs left to right on the frames the previous
    /// filter produced, and the server's chain is sized for the picture this source says it
    /// carries: its <c>scale</c> clamps against the <c>MaxWidth</c>/<c>MaxHeight</c> the request
    /// settled on, and those default to the width and height the version reports - which is the
    /// frame <em>after</em> the profile conversion (<see cref="MediaSources.ProfileVideoGeometry"/>).
    /// Landing the conversion behind that chain therefore runs the numbers in the wrong order:
    /// the server scales the un-converted all-view frame to the box of the converted one, and on
    /// a full-SBS version of a 1920x1080 source that turns 3840x1080 into 1920x540 before the
    /// profile's own work even starts. In front, the sequence reads the way the pipeline is
    /// built - convert, then whatever the server asks of the converted picture - and at native
    /// quality the server's scale lands on an identity.
    /// </para>
    /// <para>
    /// <b>What is kept, and why the server's scale is not simply dropped.</b> Nothing of the
    /// server's chain is removed here, because the chain is not decoration: it carries the
    /// client's resolution ceiling and the bitrate ladder's rung, which are the server's answer
    /// to the requesting device rather than a mistake to correct. A wrapper that deleted filters
    /// it did not write would also have to parse them, and nothing on a received command line is
    /// ever treated as filter syntax (see the class remarks). Chaining is the same text-safety
    /// class as before: one string is written in front of another and no foreign text is read.
    /// </para>
    /// <para>
    /// <b>What the server's chain sees at the profile's output.</b> Every converting profile
    /// ends its own chain at <c>format=yuv420p</c> and, for half SBS, at an explicit square
    /// sample aspect ratio, so the frames arriving at the server's filters are software frames
    /// of the geometry the version reports. A following <c>format=yuv420p</c> is then a no-op,
    /// and a following <c>format=nv12</c> - the QSV upload form - is the server doing what it
    /// always does with a software frame on its way to an hardware encoder. No hardware <em>decode</em>
    /// filter can be waiting behind the profile's position either: the codec this provider
    /// reports is never one the server can request a hardware decoder for, so these sources are
    /// software-decoded by construction and their chain starts in software too.
    /// </para>
    /// <para>
    /// <b>Subtitle burn-in.</b> A profile's burn-in is already the last stage of its own chain,
    /// so this ordering renders text onto the finished profile picture and ahead of the server's
    /// scale, which is where a burn-in belongs: at the size the picture was converted to, and
    /// once. Behind the server's chain it would be scaled with everything else instead.
    /// </para>
    /// <para>
    /// <b>The one profile this does not apply to.</b> The custom grayscale anaglyph converts
    /// through a <c>-filter_complex</c> graph, and a graph is not a stage in somebody else's
    /// linear chain: it is its own graph, it is inserted as its own option, and nothing here
    /// reorders it. FFmpeg then refuses a simple <c>-vf</c> on a stream fed from a complex graph
    /// outright ("Simple and complex filtering cannot be used together for the same stream"), so
    /// for that profile the merge below is not a question that arises - its burn-in travels as
    /// its own stage behind the mapped label, exactly as before this ordering existed.
    /// </para>
    /// </remarks>
    private static string PrependToFilterChain(string profileFilter, string existingChain)
        => string.IsNullOrEmpty(existingChain) ? profileFilter : profileFilter + "," + existingChain;

    private static bool IsInputOption(string? token)
        => string.Equals(token, InputFileArgument, StringComparison.Ordinal);

    private static bool IsMapOption(string? token)
        => string.Equals(token, MapArgument, StringComparison.Ordinal);

    private static bool IsVideoFilterOption(string? token)
        => string.Equals(token, VideoFilterArgument, StringComparison.Ordinal);

    private static bool IsSubtitleDisableOption(string? token)
        => string.Equals(token, DisableSubtitlesArgument, StringComparison.Ordinal);

    /// <summary>
    /// Whether a token carries a filtergraph this wrapper did not write.
    /// </summary>
    /// <remarks>
    /// Both spellings count: a graph read from a script file is exactly as foreign to this
    /// rewriter as one written inline, and the compatibility question is the same.
    /// </remarks>
    private static bool IsFilterGraphOption(string? token)
        => string.Equals(token, FilterComplexArgument, StringComparison.Ordinal)
           || string.Equals(token, FilterComplexScriptArgument, StringComparison.Ordinal);

    /// <summary>
    /// Whether a <c>-map</c> value competes with a video map the profile mapped for itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes do, and only for a profile that brings its own map - the graph profile. A
    /// specifier of the video type - <c>0:v</c>, <c>0:v:0</c>, with or without the exact-index
    /// spelling - would map a second video into an output whose picture the profile already
    /// maps through its graph, and a filtergraph output label is video too as far as this
    /// rewriter is concerned: graphs are the profile's own business, and the only label this
    /// command may map after a rewrite is the one the profile inserted.
    /// </para>
    /// <para>
    /// Anything else is somebody else's stream and stays: audio and subtitle specifiers, data
    /// streams, and whole-file maps, which name no type at all and would drop audio as easily
    /// as video if this removed them.
    /// </para>
    /// <para>
    /// A negative value of the video type - <c>-map -0:v</c> - counts as competition too,
    /// which is the opposite of the rule <see cref="IsSubtitleMap"/> follows for the subtitle
    /// type, and the difference is where the arguments land: what this rewriter inserts goes
    /// immediately after the last input, ahead of every map the server wrote, so a standing
    /// <c>-map -0:v</c> would subtract the streams the profile has just mapped and produce an
    /// output with no picture in it. A subtitle exclusion cannot do that to a profile - its own
    /// suppression is the same direction of travel - so those are left alone, and so is every
    /// exclusion the server writes of a stream number, which this rewriter's numbered rule has
    /// no part in: that rule names the stream the server chose to map, not the ones it chose to
    /// drop. (A profile that maps no video of its own removes that exclusion on its own merits
    /// - see <see cref="ApplyRewrite"/> - because there it deletes the stream the profile is
    /// about to convert rather than a map the profile inserted.)
    /// </para>
    /// </remarks>
    private static bool IsConflictingVideoMap(string? mapValue)
    {
        if (string.IsNullOrEmpty(mapValue))
        {
            return false;
        }

        return IsFilterGraphLabel(mapValue) || StreamSpecifierType(mapValue) == 'v';
    }

    /// <summary>
    /// Whether a <c>-map</c> value selects a subtitle stream of an input file.
    /// </summary>
    /// <remarks>
    /// An exclusion is not a selection. <c>-map -0:s</c> takes subtitle streams <em>away</em>
    /// from an output, which is the direction subtitle suppression is already going in: the
    /// profile asks for with <c>-sn</c>. Removing that map would add subtitles back, so an
    /// exclusion of the subtitle type is left where the server wrote it, exactly as an
    /// exclusion of audio is. (A negative <em>video</em> map is treated the other way for both
    /// profiles, each for its own reason: a profile that maps its own video would have that map
    /// subtracted, and a profile that converts the server's own video stream would have that
    /// stream taken away.)
    /// </remarks>
    private static bool IsSubtitleMap(string? mapValue)
        => !string.IsNullOrEmpty(mapValue)
           && !IsStreamExclusion(mapValue)
           && StreamSpecifierType(mapValue) == 's';

    /// <summary>
    /// Whether a <c>-map</c> value is an exclusion - the <c>-map -0:s</c> spelling, which
    /// removes streams already selected rather than naming one to add.
    /// </summary>
    private static bool IsStreamExclusion(string mapValue)
        => mapValue.Length > 0 && mapValue[0] == '-';

    /// <summary>
    /// The lowercased stream type a map value names, or <c>\0</c> for a value that names
    /// none this rewriter reasons about.
    /// </summary>
    /// <remarks>
    /// FFmpeg's specifier grammar is <c>fileIndex:streamType[:detail]</c> - <c>0:v</c>,
    /// <c>0:v:0</c>, <c>0:a:0?</c>, and with FFmpeg-mvc a view detail behind the type - or the
    /// bare type letter, with the uppercase spelling asking for the exact index. Lowercasing
    /// keeps both spellings, and only the type position is read: the whole file (<c>0</c>) and
    /// its negation (<c>-0</c>) name no type and answer <c>\0</c>. Which view the detail asks
    /// for is not this function's question - see <see cref="IsViewSpecifierMap"/>.
    /// </remarks>
    private static char StreamSpecifierType(string mapValue)
    {
        var separator = mapValue.IndexOf(':');

        if (separator < 0)
        {
            return mapValue.Length == 1 && char.IsAsciiLetter(mapValue[0])
                ? char.ToLowerInvariant(mapValue[0])
                : '\0';
        }

        return separator + 1 < mapValue.Length
            ? char.ToLowerInvariant(mapValue[separator + 1])
            : '\0';
    }

    /// <summary>
    /// Whether a <c>-map</c> value numbers the video stream the marker named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server's own HLS commands name the stream they chose by number -
    /// <c>-map 0:&lt;index&gt;</c>, or <c>-map 0:&lt;index&gt;?</c> where it wants the map to
    /// survive a file that turns out not to carry it - and a bare number carries no stream
    /// type for <see cref="StreamSpecifierType"/> to read. This is therefore the one map
    /// question this rewriter cannot answer out of the argv alone, and the one the marker
    /// contract answers: the provider names its video stream by its own stream index
    /// (<c>MediaStream.Index</c>), which is the number of that stream inside the file and so
    /// the number a <c>-map 0:&lt;number&gt;</c> of this input addresses. The position the
    /// stream happens to hold in a reported stream list is not that number as soon as the
    /// list leaves file order - a data stream the server does not report, an externally
    /// sourced track - which is why the marker carries the index and not the position.
    /// </para>
    /// <para>
    /// Both the input and the number have to match. A map of another input file is not this
    /// source's video, and a map of another stream of this one is somebody else's stream -
    /// removing it would drop audio or subtitles, which is the same mistake this rewriter
    /// refuses to make with a whole-file map.
    /// </para>
    /// </remarks>
    private static bool IsNumberedVideoMap(string? mapValue, int? videoStreamIndex)
    {
        if (videoStreamIndex is not int named || string.IsNullOrEmpty(mapValue))
        {
            return false;
        }

        if (!mapValue.StartsWith(FirstInputMapPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var number = mapValue.AsSpan(FirstInputMapPrefix.Length);
        if (number.EndsWith('?'))
        {
            // The optional-map suffix, not part of the number: FFmpeg tolerates a missing
            // stream behind it, which is why the server writes it at all.
            number = number[..^1];
        }

        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
               && index == named;
    }

    /// <summary>
    /// Whether a <c>-map</c> value addresses the marker's input, which is the only input this
    /// rewriter composes views for and therefore the only one whose maps a composed rewrite
    /// has to look at.
    /// </summary>
    /// <remarks>
    /// An exclusion addresses the input it would take streams out of, so the leading <c>-</c>
    /// is not part of this answer; whether the value adds or removes streams is
    /// <see cref="IsStreamExclusion"/>'s question.
    /// </remarks>
    private static bool NamesFirstInput(string? mapValue)
    {
        if (string.IsNullOrEmpty(mapValue))
        {
            return false;
        }

        var value = IsStreamExclusion(mapValue) ? mapValue[1..] : mapValue;

        return value.StartsWith(FirstInputMapPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a <c>-map</c> value asks for one view or every view of a stream rather than for
    /// the stream itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FFmpeg-mvc spells a view selection behind the stream type - <c>0:v:view:all</c>,
    /// <c>0:v:view:1</c>, <c>0:v:vidx:1</c>, <c>0:v:vpos:left</c>, each allowed to end in the
    /// optional-map <c>?</c> - and the uppercase type spelling is the same selection with the
    /// exact index. All of them are the same problem for a composed rewrite: a decoder that was
    /// configured through <c>-view_ids</c> refuses a view specifier outright, so none of them
    /// may survive onto the input the option was written for, whichever of the spellings the
    /// command happened to carry.
    /// </para>
    /// <para>
    /// Which view is named is deliberately not read: every view specifier is refused beside the
    /// composed request, including a request for the base view alone, which would compose
    /// nothing and is a different profile's claim anyway.
    /// </para>
    /// </remarks>
    private static bool IsViewSpecifierMap(string? mapValue)
    {
        if (string.IsNullOrEmpty(mapValue))
        {
            return false;
        }

        var value = IsStreamExclusion(mapValue) ? mapValue[1..] : mapValue;

        // The stream type is the field after the input index, and the view detail whatever
        // follows the separator behind that type.
        var typeSeparator = value.IndexOf(':');
        if (typeSeparator < 0 || typeSeparator + 1 >= value.Length
            || char.ToLowerInvariant(value[typeSeparator + 1]) != 'v')
        {
            return false;
        }

        var afterType = value[(typeSeparator + 2)..];
        var detail = afterType.StartsWith(":", StringComparison.Ordinal)
            ? afterType[1..]
            : afterType;

        foreach (var prefix in ViewSpecifierPrefixes)
        {
            if (detail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The map that names the same stream as a view selection does, without naming a view: the
    /// marker's video stream, keeping the command's own optional-map suffix so a rewrite does
    /// not turn a map that tolerated a missing stream into one that insists on it.
    /// </summary>
    private static string WithoutViewSelection(string mapValue, string markerVideoMap)
        => mapValue.EndsWith("?", StringComparison.Ordinal) ? markerVideoMap + "?" : markerVideoMap;

    /// <summary>
    /// The <c>-map</c> value naming the converted source's own video stream: the stream the
    /// marker identified by index where it could, and the video type of the marker's input
    /// where it could not.
    /// </summary>
    /// <remarks>
    /// By index wherever the index is known, because that is the one spelling that names a
    /// single stream: <c>0:v</c> means every video stream of the input, which is a second
    /// picture in the output's way as soon as a file carries one - an attached cover track is
    /// enough to make that file.
    /// </remarks>
    private static string MarkerVideoStreamMap(ProfileMarker marker)
        => marker.VideoStreamIndex is int index
            ? string.Concat(FirstInputMapPrefix, index.ToString(CultureInfo.InvariantCulture))
            : FirstInputVideoTypeMap;

    /// <summary>
    /// The view selections the command already asks of the input the marker arrived on.
    /// </summary>
    /// <param name="arguments">The received vector.</param>
    /// <param name="inputOptionIndex">
    /// The index of the <c>-i</c> token that carried the marker. Everything before it is the
    /// scope this input is opened with, and the only place an option of this input can stand.
    /// </param>
    /// <returns>
    /// Every <c>-view_ids</c> option in that scope, in the order the command wrote them. An
    /// empty list is the ordinary case: nobody asked this input for its views yet.
    /// </returns>
    private static IReadOnlyList<ViewSelection> FindViewSelections(
        IReadOnlyList<string> arguments,
        int inputOptionIndex)
    {
        var found = new List<ViewSelection>();

        for (var index = 0; index < inputOptionIndex; index++)
        {
            var token = arguments[index];
            if (!IsViewSelectionOption(token))
            {
                continue;
            }

            var valueSeparator = token.IndexOf('=');

            if (valueSeparator >= 0)
            {
                // "-view_ids=0": one token carrying both halves.
                found.Add(new ViewSelection(index, token[(valueSeparator + 1)..], ValueInsideOption: true));
                continue;
            }

            var valueIndex = index + 1;
            if (valueIndex >= inputOptionIndex)
            {
                // The option is the last thing the command says before opening the file, so
                // its argument is missing rather than wrong.
                found.Add(new ViewSelection(index, Value: null, ValueInsideOption: false));
                continue;
            }

            var value = arguments[valueIndex];

            // The composed value begins with a dash and is still an argument; any other token
            // beginning with one is the following option, which means this option never got a
            // value to be wrong or right about.
            var isValue = !value.StartsWith("-", StringComparison.Ordinal)
                          || string.Equals(value, ComposedViewInputValue, StringComparison.Ordinal);

            found.Add(new ViewSelection(index, isValue ? value : null, ValueInsideOption: false));

            if (isValue)
            {
                // Step over the value: an argument is data, never an option, whatever it reads
                // like.
                index = valueIndex;
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a token is the option that selects views of the input it is written for, in any
    /// of the spellings FFmpeg routes it under: bare, with a stream specifier behind its name,
    /// or with its value glued to it.
    /// </summary>
    private static bool IsViewSelectionOption(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(ComposedViewInputArgument, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = token[ComposedViewInputArgument.Length..];

        return rest.Length == 0 || rest[0] is ':' or '=';
    }

    /// <summary>
    /// One <c>-view_ids</c> option of the input being converted, together with the value the
    /// command carried beside it.
    /// </summary>
    /// <param name="OptionIndex">The index of the option token itself.</param>
    /// <param name="Value">
    /// The value as the command spelled it, or null when the command carried none.
    /// </param>
    /// <param name="ValueInsideOption">
    /// Whether the value came glued to the option token rather than as the token after it,
    /// which decides which index a repair has to rewrite.
    /// </param>
    private readonly record struct ViewSelection(int OptionIndex, string? Value, bool ValueInsideOption)
    {
        /// <summary>Whether this option came with a value that can be judged.</summary>
        public bool HasValue => Value is not null;

        /// <summary>The index of the token that carries this value, glued to the option or not.</summary>
        public int ValueIndex => ValueInsideOption ? OptionIndex : OptionIndex + 1;
    }

    /// <summary>
    /// The filter text the server wrote for the picture, and where in the output segment it
    /// stands.
    /// </summary>
    /// <param name="Graph">
    /// The value of the last inline <c>-filter_complex</c> on the command, or null when the
    /// command carries none.
    /// </param>
    /// <param name="GraphValueIndex">
    /// The index of that value, or -1. This is the slot a merged graph is written back into, so
    /// that the server's option keeps the position the server wrote it at.
    /// </param>
    /// <param name="GraphOptionCount">
    /// How many inline graphs the command carries. More than one is a refusal for a merge - the
    /// vector does not say which of them feeds the output - but several are also how a
    /// graph-less rewrite learns the command's shape is unusual.
    /// </param>
    /// <param name="GraphReadFromScript">
    /// Whether a <c>-filter_complex_script</c> stands on the command: the server's picture is
    /// then written in a file this wrapper cannot see, which is the one graph shape it can never
    /// merge into.
    /// </param>
    /// <param name="Chain">The value of the last <c>-vf</c> on the command, or null when there is none.</param>
    /// <param name="ChainValueIndex">The index of that value, or -1 when there is none.</param>
    private readonly record struct ServerFilterText(
        string? Graph,
        int GraphValueIndex,
        int GraphOptionCount,
        bool GraphReadFromScript,
        string? Chain,
        int ChainValueIndex);

    /// <summary>
    /// Whether a token and the value after it ask FFmpeg to copy a video stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FFmpeg spells one option several ways: <c>-c</c>, <c>-codec</c> and <c>-vcodec</c>,
    /// each carrying an optional <c>:stream-specifier</c> - and <c>-codec:v:0 copy</c> is
    /// the exact spelling a Jellyfin HLS copy command writes. Whether a spelling can reach
    /// the output's video is answered conservatively:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>-vcodec</c> is video by name.</description></item>
    /// <item><description>
    /// <c>-c</c> or <c>-codec</c> with no specifier is every stream of the output, the video
    /// among them.
    /// </description></item>
    /// <item><description>
    /// A specifier of a known non-video type - audio, subtitle, data, attachment - is
    /// provably not this output's video, which is what keeps the ordinary
    /// <c>-c:a copy</c> of a normal transcode from refusing an Anaglyfin job.
    /// </description></item>
    /// <item><description>
    /// A video specifier is video, and anything this rewriter cannot classify - a bare
    /// stream number, a type letter it does not know, an empty <c>-c:</c> - cannot be shown
    /// not to be. Guessing the wrong way here means running a profile pipeline that converts
    /// nothing while reporting success, which is the outcome this guard exists to refuse.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static bool IsVideoCodecCopyOption(string? token, string? value)
    {
        if (!string.Equals(value, StreamCopyValue, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrEmpty(token) || token[0] != '-' || token.Length < 2)
        {
            return false;
        }

        var separator = token.IndexOf(':');
        var name = separator < 0 ? token[1..] : token[1..separator];

        if (string.Equals(name, VideoCodecOptionName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var setsEveryStream = string.Equals(name, CodecOptionName, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(name, ShortCodecOptionName, StringComparison.OrdinalIgnoreCase);

        if (!setsEveryStream)
        {
            return false;
        }

        return separator < 0 || StreamSpecifierMayBeVideo(token[(separator + 1)..]);
    }

    /// <summary>
    /// Whether a codec option's stream specifier can address a video stream.
    /// </summary>
    /// <remarks>
    /// Only the specifier's type position is read, exactly as for a map value, and the
    /// uppercase spelling counts as video too: in FFmpeg's grammar it asks for the exact
    /// index within that type rather than naming another one.
    /// </remarks>
    private static bool StreamSpecifierMayBeVideo(string specifier)
    {
        if (specifier.Length == 0)
        {
            // "-c:" with nothing behind it is a broken option, and a broken option is not
            // evidence that nothing is being copied.
            return true;
        }

        return char.ToLowerInvariant(specifier[0]) switch
        {
            'a' or 's' or 'd' or 't' => false,
            _ => true
        };
    }

    /// <summary>
    /// Whether a value is a single filtergraph output label, <c>[name]</c>.
    /// </summary>
    /// <remarks>
    /// A <c>-map</c> value holds one label, so a second closing bracket inside the value
    /// means the text is not a label at all and is treated as an ordinary map value.
    /// </remarks>
    private static bool IsFilterGraphLabel(string value)
        => value.Length >= 3
           && value[0] == '['
           && value[^1] == ']'
           && value.IndexOf(']', 1) == value.Length - 1;
}
