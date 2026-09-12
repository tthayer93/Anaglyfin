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
/// rewriter inserts therefore lands immediately after the last <c>-i</c> value, and
/// everything it examines or removes for a conflict lives in that same output segment: an
/// option written before an input belongs to that input and is left alone.
/// </para>
/// <para>
/// <b>What a rewrite does.</b> The marker token is replaced by the real source path the
/// provider put into it. Then, through <see cref="IFfmpegProfileArgumentBuilder"/>, the
/// profile's own view selection and filter chain are inserted, video maps that would
/// compete with them are removed, subtitle stream maps give way to <c>-sn</c> when the
/// profile burns subtitles in, and a linear profile filter is appended to an existing
/// <c>-vf</c> chain rather than replacing it. An audio map that is already on the command,
/// the encoder, the muxer and the HLS arguments are never touched: they are Jellyfin's
/// business, and the product requirement is that Anaglyfin playback differs from stock
/// playback in picture and not in delivery. The one audio argument this rewriter does
/// write is the optional audio map beside its own video map, and only for a command that
/// carried no map at all - see <see cref="OptionalAudioMapValue"/>.
/// </para>
/// <para>
/// <b>Which video maps are the profile's competition.</b> Two spellings, because the
/// server has two reasons to write one. A specifier that names the video type -
/// <c>0:v</c>, <c>0:v:0</c>, <c>0:v:view:all</c>, a filtergraph label - names video
/// whatever else it says, and so does the exclusion of that type, which would take the
/// profile's own picture back out of the output. A plain <c>-map 0:2</c> names nothing of
/// the kind: it is a stream number, and FFmpeg's own grammar gives a number no type. That
/// one is the shape Jellyfin's HLS commands actually carry, and the rewriter only knows
/// which numbered stream is video because the marker says so - <c>video=&lt;index&gt;</c>
/// is the provider naming the video stream of its own source by its stream index, the same
/// number the server's <c>-map 0:&lt;index&gt;</c> spends on it. A numbered map for any
/// other stream, and every audio, subtitle, whole-file and exclusion map that does not
/// name video, stays exactly where the server put it.
/// </para>
/// <para>
/// <b>Where it refuses.</b> A profile that owns the output's video pipeline cannot share
/// that pipeline with a filtergraph someone else wrote: this wrapper merges and inserts, it
/// does not parse or graft foreign filter text (which is also a security requirement -
/// nothing in a received command line is ever treated as filter syntax), so an existing
/// <c>-filter_complex</c> in the output segment refuses the job. So does a marker that is
/// not the first input, because every argument the builder emits addresses input <c>0</c>,
/// and so does a second valid marker anywhere in the vector: a rewrite resolves exactly
/// one marker, and the one left behind would reach FFmpeg as a file to open. And so does a
/// command that copies its video - <c>-c copy</c>, <c>-c:v copy</c>, <c>-codec:v:0 copy</c>,
/// <c>-vcodec copy</c> - because copying a stream is the server deciding that no
/// conversion happens on this output, which would leave the profile's arguments present but
/// inert (see <see cref="WrapperRewriteStatus.ServerChoseVideoCopy"/>). Every one of
/// these refusals says which rule the command broke, and every one returns no vector at
/// all.
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
    /// The prefix a <c>-map</c> value carries when it numbers a stream of the first input -
    /// the input a marker is allowed to be, and therefore the only input whose numbered
    /// maps the marker's <c>video=&lt;index&gt;</c> can identify.
    /// </summary>
    private const string FirstInputMapPrefix = "0:";

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
        // refusal of this rewrite.
        ProfileRewrite rewrite;
        try
        {
            rewrite = _builder.Build(profile, ToSubtitleBurnIn(marker));
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
    /// Splices a built rewrite into the output segment of the received vector.
    /// </summary>
    private static WrapperRewriteResult ApplyRewrite(
        IReadOnlyList<string> arguments,
        ProfileMarker marker,
        int markerIndex,
        int lastInputIndex,
        ProfileRewrite rewrite)
    {
        // Only a profile with a map of its own may take other video maps away: the plain
        // 2D profile inserts nothing, so removing the command's video map would leave the
        // output without video at all.
        var ownsVideoPipeline = rewrite.VideoMap is not null;

        // Whether the received output segment named any stream of its own. A command that
        // named none - the map-less HLS shape this rewriter has to survive as well as the one
        // carrying the server's own maps - is asking FFmpeg to pick the output's streams, and
        // that pick delivers a video *and* an audio. The moment this rewriter inserts the
        // profile's own video map, the naming is explicit and the pick is gone. A command
        // that did carry maps had already decided its own streams, audio included, and that
        // decision is Jellyfin's to keep.
        var mapsReceived = false;

        // The received vector is never mutated: removals and replacements are recorded by
        // index and applied while the new vector is built, so one pass over the output
        // segment is enough and no index can shift under another decision.
        var removals = new HashSet<int>();
        var replacements = new Dictionary<int, string> { [markerIndex] = marker.SourcePath };

        var existingGraphIndex = -1;
        var existingFilterValueIndex = -1;
        var subtitlesAlreadyDisabled = false;

        // Whether the server told FFmpeg to copy the video instead of encoding one. The
        // profile's own view selection and filters can only act on a picture they produce,
        // so on such a command the whole rewrite would be decoration; the check and the
        // refusal live together below.
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
                if ((ownsVideoPipeline && (IsConflictingVideoMap(mapValue) || IsNumberedVideoMap(mapValue, marker.VideoStreamIndex)))
                    || (rewrite.ShouldSuppressSubtitleStreams && IsSubtitleMap(mapValue)))
                {
                    removals.Add(index);
                    removals.Add(index + 1);
                }
            }
            else if (IsVideoFilterOption(token) && index + 1 < arguments.Count)
            {
                // FFmpeg keeps the last value of a repeated option, so the chain the
                // profile extends is the last one on the command.
                existingFilterValueIndex = index + 1;
            }
            else if (IsFilterGraphOption(token))
            {
                existingGraphIndex = index;
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
                "The command copies its video stream, so the view selection and filters this profile asks for would have no encoded output to reach; the version was asked to convert a picture the server decided to pass through.");
        }

        if (ownsVideoPipeline && existingGraphIndex >= 0)
        {
            return WrapperRewriteResult.Failure(
                WrapperRewriteStatus.IncompatibleFilterGraph,
                "The command already carries a filter graph, and this profile has to own the video pipeline of the output it is rewritten into.");
        }

        var insertions = new List<string>();

        if (rewrite.FilterComplex is { } filterComplex)
        {
            insertions.Add(FilterComplexArgument);
            insertions.Add(filterComplex);
        }

        if (rewrite.VideoMap is { } videoMap)
        {
            insertions.Add(MapArgument);
            insertions.Add(videoMap);

            // Naming the profile's video is what turns an un-named output into a named one,
            // and a named output exports exactly what is named. On a command that carried no
            // map of its own, the audio the automatic selection used to deliver therefore
            // needs a name of its own here - otherwise the 3D version of a film plays silent
            // and no layer reports it. On a command that did carry maps, whatever audio it
            // chose (including none) is already in the segment and stays untouched.
            if (!mapsReceived)
            {
                insertions.Add(MapArgument);
                insertions.Add(OptionalAudioMapValue);
            }
        }

        // The builder already merged the profile conversion and the subtitle burn-in into
        // one chain where they belong together, so the chain to splice is the one in
        // InsertArguments rather than either half on its own.
        if (ValueAfter(rewrite.InsertArguments, VideoFilterArgument) is { } profileFilter)
        {
            if (existingFilterValueIndex >= 0)
            {
                replacements[existingFilterValueIndex] = AppendToFilterChain(arguments[existingFilterValueIndex], profileFilter);
            }
            else
            {
                insertions.Add(VideoFilterArgument);
                insertions.Add(profileFilter);
            }
        }

        // Subtitles reach a converted picture through the burn-in filter alone; a mapped
        // or codec-level subtitle stream on top of it would render the text twice.
        if (rewrite.ShouldSuppressSubtitleStreams && !subtitlesAlreadyDisabled)
        {
            insertions.Add(DisableSubtitlesArgument);
        }

        return WrapperRewriteResult.Rewritten(
            Rebuild(arguments, lastInputIndex, insertions, removals, replacements),
            rewrite.ProfileId);
    }

    /// <summary>
    /// Turns the subtitle ordinal of a marker into the burn-in the profile asks for.
    /// </summary>
    /// <remarks>
    /// The filter re-opens the film by name to render its text, so the burn-in carries the
    /// marker's real source path and not the marker itself. A marker without an ordinal is
    /// the subtitle-free version, which still suppresses subtitle streams.
    /// </remarks>
    private static SubtitleBurnIn? ToSubtitleBurnIn(ProfileMarker marker)
        => marker.SubtitleOrdinal is int ordinal ? new SubtitleBurnIn(marker.SourcePath, ordinal) : null;

    /// <summary>
    /// Builds the rewritten vector: replacements in place, removals dropped, insertions
    /// after the last input.
    /// </summary>
    private static IReadOnlyList<string> Rebuild(
        IReadOnlyList<string> arguments,
        int insertAfterIndex,
        IReadOnlyList<string> insertions,
        HashSet<int> removals,
        IReadOnlyDictionary<int, string> replacements)
    {
        var rewritten = new List<string>(arguments.Count + insertions.Count);

        for (var index = 0; index < arguments.Count; index++)
        {
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
    /// Appends the profile's chain to an existing <c>-vf</c> chain.
    /// </summary>
    /// <remarks>
    /// Comma-chaining keeps whatever the command already asked for and puts the profile
    /// conversion behind it, which is also where a burn-in belongs; replacing the chain
    /// would silently drop server-side filtering. An empty existing chain is not chained,
    /// because FFmpeg reads a leading comma as an empty filter name.
    /// </remarks>
    private static string AppendToFilterChain(string existingChain, string profileFilter)
        => string.IsNullOrEmpty(existingChain) ? profileFilter : existingChain + "," + profileFilter;

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
    /// Whether a <c>-map</c> value competes with the profile's own video selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes do. A stream specifier of the video type - <c>0:v</c>, <c>0:v:0</c>,
    /// <c>0:v:view:all</c>, with or without the exact-index spelling - would map a second
    /// video into an output that the profile already maps, which on a MVC file means the
    /// plain base view next to the profile's all-view pivot. A filtergraph output label is
    /// video too as far as this rewriter is concerned: graphs are the profile's own
    /// business, and the only label this command may map after a rewrite is the one the
    /// profile inserted.
    /// </para>
    /// <para>
    /// Anything else is somebody else's stream and stays: audio and subtitle specifiers,
    /// data streams, and whole-file maps, which name no type at all and would drop audio
    /// as easily as video if this removed them.
    /// </para>
    /// <para>
    /// A negative value of the video type - <c>-map -0:v</c> - counts as competition too,
    /// which is the opposite of the rule
    /// <see cref="IsSubtitleMap"/> follows for the subtitle type, and the difference is where
    /// the arguments land: what this rewriter inserts goes immediately after the last input,
    /// ahead of every map the server wrote, so a standing <c>-map -0:v</c> would subtract the
    /// views the profile has just mapped and produce an output with no picture in it. A
    /// subtitle exclusion cannot do that to a profile - its own suppression is the same
    /// direction of travel - so those are left alone, and so is every exclusion the server
    /// writes of a stream number, which this rewriter's numbered rule has no part in: that
    /// rule names the stream the server chose to map, not the ones it chose to drop.
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
    /// exclusion of audio is. (A negative <em>video</em> map is not treated this way, and the
    /// reason is order rather than type: this rewriter inserts its own video map immediately
    /// after the last input, which is ahead of every map the server wrote, so a standing
    /// <c>-map -0:v</c> would subtract the streams the profile had just mapped. See
    /// <see cref="IsConflictingVideoMap"/>.)
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
    /// <c>0:v:0</c>, <c>0:v:view:all</c>, <c>0:a:0?</c> - or the bare type letter, with the
    /// uppercase spelling asking for the exact index. Lowercasing keeps both spellings, and
    /// only the type position is read: the whole file (<c>0</c>) and its negation
    /// (<c>-0</c>) name no type and answer <c>\0</c>.
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
