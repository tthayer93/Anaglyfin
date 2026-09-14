using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Anaglyfin.Profiles;

namespace Anaglyfin.Ffmpeg;

/// <summary>
/// Produces the FFmpeg arguments for every allowlisted Anaglyfin profile.
/// </summary>
/// <remarks>
/// <para>
/// The builder is stateless and deterministic: the same profile and burn-in request
/// always produce the same argument tokens, which is what lets the tests assert
/// commands verbatim. It only ever emits the arguments a profile <em>adds</em> - the
/// input, the encoder, the muxer and the output stay with the caller assembling the
/// command (the FFmpeg wrapper in production, a fake collector in tests).
/// </para>
/// <para>
/// Command semantics, one decode per playback. Every stereo profile is served by the same
/// composed decode: the caller asks the input for it with
/// <see cref="ComposedViewInputOption"/> <see cref="ComposedViewInputValue"/> immediately
/// before <c>-i</c>, which is the one place the all-view picture can be requested from -
/// the option is in the decoder's context before decoding starts, while a view specifier is
/// only resolved after the decoder has reported its view list. In exchange, no argument the
/// builder emits names a view, because a decode configured through that option refuses a
/// view specifier on the same input.
/// </para>
/// <list type="bullet">
/// <item>
/// 2D base inserts nothing and asks for nothing: FFmpeg-mvc defaults to the base view, so
/// the profile is the untouched default command.
/// </item>
/// <item>
/// Full SBS is the composed output itself, so it needs no filter and no map: the server's
/// own video map already names the stream the composed frames arrive on.
/// </item>
/// <item>
/// Half SBS derives from that same composed frame by scaling it to half width
/// (<c>scale=iw/2:ih</c>) and then declaring the result square-pixel
/// (<c>setsar=sar=1</c>), not by decoding eyes separately and not by a stereo3d
/// re-tag, which only changes the pixel aspect ratio.
/// </item>
/// <item>
/// A built-in anaglyph preset is the composed frame through
/// <c>stereo3d=sbsl:&lt;code&gt;</c> for an official output code.
/// </item>
/// <item>
/// The custom grayscale anaglyph is one filter graph over the composed stream, read by its
/// stream index: split, grayscale each eye, tint each eye via <c>colorchannelmixer</c> with
/// the validated colour's channel coefficients, and screen-combine the two eyes. Its result
/// is a new stream, so this profile - and only this profile - maps a label of its own.
/// </item>
/// </list>
/// <para>
/// Subtitle burn-in is always the last filter stage on whatever stream carries the
/// profile output, and generated arguments never contain a hardware decode request:
/// MVC decode is software-only and hardware decoding stays with the caller.
/// </para>
/// </remarks>
public sealed class FfmpegProfileArgumentBuilder : IFfmpegProfileArgumentBuilder
{
    /// <summary>The <c>-map</c> option token.</summary>
    public const string MapArgument = "-map";

    /// <summary>The <c>-vf</c> option token.</summary>
    public const string VideoFilterArgument = "-vf";

    /// <summary>The <c>-filter_complex</c> option token.</summary>
    public const string FilterComplexArgument = "-filter_complex";

    /// <summary>
    /// The FFmpeg-mvc decoder option that asks an input for every view it carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a per-input option, so it belongs immediately before the <c>-i</c> of the file
    /// being opened, and it is the composed route this product documents as the reliable
    /// one: the value is in the decoder's context before decoding starts, whereas a view
    /// specifier names its view only after the decoder has exported its view list.
    /// </para>
    /// <para>
    /// Asking this way is also exclusive: the same decoder refuses a view specifier
    /// (<c>0:v:view:&#42;</c> in a map, <c>[0:v:view:&#42;]</c> in a filtergraph) once
    /// <c>-view_ids</c> has configured it, which is why nothing this builder emits carries
    /// one and why the composed picture is requested here rather than selected downstream.
    /// </para>
    /// </remarks>
    public const string ComposedViewInputOption = "-view_ids";

    /// <summary>
    /// The single value of <see cref="ComposedViewInputOption"/> that means "every view,
    /// composed into one frame": the whole view list, which FFmpeg-mvc assembles into the
    /// native side-by-side picture every Anaglyfin stereo profile starts from.
    /// </summary>
    public const string ComposedViewInputValue = "-1";

    /// <summary>
    /// The filtergraph input label used when the source video stream is not named by index.
    /// </summary>
    /// <remarks>
    /// The video type of the first input, which is what the composed decode delivers when
    /// the caller has no stream index to hand over. Still not a view specifier: under
    /// <see cref="ComposedViewInputOption"/> a view specifier would be refused outright.
    /// </remarks>
    public const string UnnamedVideoStreamFilterInput = "[0:v]";

    /// <summary>Pix_fmt normalisation appended to every profile conversion chain.</summary>
    public const string FormatYuv420PFilter = "format=yuv420p";

    /// <summary>
    /// Declares the frame it follows square-pixel, which is what every Anaglyfin profile's
    /// output is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter carries no pixels; it carries the pixel <em>shape</em>, and the one place
    /// that shape is wrong by construction is the half-SBS scale below. Spelled
    /// <c>setsar=sar=1</c> rather than the shorter <c>setsar=1</c> for the same reason the
    /// server spells it that way in its own stereo chains: the option name in the text says
    /// what the number means, and both spellings reach the same ratio (the shorthand lands on
    /// the filter's first option, <c>sar</c>, in FFmpeg's own option table).
    /// </para>
    /// <para>
    /// The alternative that would need no extra filter - <c>scale</c>'s own
    /// <c>reset_sar</c> option - exists only in recent FFmpeg builds, while
    /// <c>setsar</c> has been in every FFmpeg anyone deploys.
    /// </para>
    /// </remarks>
    public const string SquareSampleAspectRatioFilter = "setsar=sar=1";

    /// <summary>
    /// Half SBS conversion: scale the combined native SBS frame to half width. Real
    /// scaling, so the output is genuinely half-side-by-side.
    /// </summary>
    /// <remarks>
    /// Scaling half the width of a frame whose eyes are already laid side by side is the whole
    /// conversion, and it is why no <c>stereo3d</c> re-tag appears here: a re-tag changes the
    /// pixel aspect and leaves two full-size eyes in one frame.
    /// <para>
    /// What scaling does on the way out is the reason the chain does not stop at the scale:
    /// <c>scale</c> keeps the picture's display aspect by adjusting the sample aspect ratio it
    /// hands on, so halving the width of a square-pixel frame hands on a 2:1 one
    /// (<c>vf_scale.c</c>: out SAR = in SAR × in size / out size). Nothing in the encoded
    /// file's pixels is wrong - 1920x1080 of half-SBS is exactly the frame this profile sells -
    /// but every consumer that trusts the metadata the frame travels with now stretches it
    /// sideways, and so does anything downstream that scales from it. Declaring 1:1 behind the
    /// scale is therefore part of the conversion rather than a cosmetic touch: it is what makes
    /// the reported 1920x1080 of this profile mean 1920x1080 of square pixels all the way to
    /// the player.
    /// </para>
    /// </remarks>
    public const string HalfSideBySideScaleFilter =
        "scale=iw/2:ih:flags=bicubic," + SquareSampleAspectRatioFilter + "," + FormatYuv420PFilter;

    /// <summary>Filter graph output label of the custom grayscale anaglyph.</summary>
    public const string CustomAnaglyphOutputLabel = "[anaglyfin_custom]";

    private const string CustomLeftInput = "[anaglyfin_cg_left_in]";

    private const string CustomRightInput = "[anaglyfin_cg_right_in]";

    private const string CustomLeftEye = "[anaglyfin_cg_left]";

    private const string CustomRightEye = "[anaglyfin_cg_right]";

    private const string LeftEyeCrop = "crop=iw/2:ih:0:0";

    private const string RightEyeCrop = "crop=iw/2:ih:iw/2:0";

    private const string ToGrayscaleFilter = "format=gray";

    private const string ToRgbFilter = "format=rgb24";

    private const string ScreenCombineFilter = "blend=all_mode=screen";

    private const string ColorChannelMixerPrefix = "colorchannelmixer=rr=";

    /// <summary>
    /// Gets the shared stateless instance, convenient for out-of-process callers such
    /// as the FFmpeg wrapper that cannot use the plugin DI container.
    /// </summary>
    public static FfmpegProfileArgumentBuilder Shared { get; } = new FfmpegProfileArgumentBuilder();

    /// <inheritdoc />
    public ProfileRewrite Build(StereoProfile profile, SubtitleBurnIn? subtitleBurnIn = null, int? videoStreamIndex = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var profileId = RequireAllowedProfileId(profile);
        var burnIn = EligibleSubtitleBurnIn(profile, subtitleBurnIn);

        switch (profile.Kind)
        {
            case ProfileKind.TwoDimensional:
                // The plain 2D version is what the FFmpeg-mvc binary already produces
                // for the command as written, and its subtitle handling is the stock
                // pipeline's, so a burn-in request cannot make this rewrite busier. Nor is
                // the composed picture asked for: the base view is this profile.
                return BuildTwoDimensional(profileId);

            case ProfileKind.SideBySideFull:
                return BuildComposedFiltered(profileId, ProfileKind.SideBySideFull, profileFilter: null, burnIn);

            case ProfileKind.SideBySideHalf:
                return BuildComposedFiltered(profileId, ProfileKind.SideBySideHalf, HalfSideBySideScaleFilter, burnIn);

            case ProfileKind.Stereo3DAnaglyph:
                {
                    var code = RequireOutputCode(profile);
                    return BuildComposedFiltered(profileId, ProfileKind.Stereo3DAnaglyph, Stereo3DAnaglyphFilter(code), burnIn);
                }

            case ProfileKind.CustomGrayscaleAnaglyph:
                {
                    var (left, right) = RequireEyeColors(profile);
                    return BuildCustomGrayscaleAnaglyphCore(profileId, left, right, burnIn, videoStreamIndex);
                }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(profile),
                    profile.Kind,
                    $"Profile '{profileId}' carries the profile kind {(int)profile.Kind}, which this build cannot build a command for.");
        }
    }

    /// <inheritdoc />
    public ProfileRewrite BuildTwoDimensionalBase()
        => BuildTwoDimensional(ProfileIds.TwoDBase);

    /// <inheritdoc />
    public ProfileRewrite BuildSideBySideFull(SubtitleBurnIn? subtitleBurnIn = null)
        => BuildComposedFiltered(ProfileIds.SideBySideFull, ProfileKind.SideBySideFull, profileFilter: null, subtitleBurnIn);

    /// <inheritdoc />
    public ProfileRewrite BuildSideBySideHalf(SubtitleBurnIn? subtitleBurnIn = null)
        => BuildComposedFiltered(ProfileIds.SideBySideHalf, ProfileKind.SideBySideHalf, HalfSideBySideScaleFilter, subtitleBurnIn);

    /// <inheritdoc />
    public ProfileRewrite BuildStereo3DAnaglyph(string stereo3DOutputCode, SubtitleBurnIn? subtitleBurnIn = null)
    {
        var code = RequireAllowedOutputCode(stereo3DOutputCode, nameof(stereo3DOutputCode));

        return BuildComposedFiltered(
            ProfileIds.BuildAnaglyphProfileId(code),
            ProfileKind.Stereo3DAnaglyph,
            Stereo3DAnaglyphFilter(code),
            subtitleBurnIn);
    }

    /// <inheritdoc />
    public ProfileRewrite BuildCustomGrayscaleAnaglyph(
        RgbColor leftEyeColor,
        RgbColor rightEyeColor,
        SubtitleBurnIn? subtitleBurnIn = null,
        int? videoStreamIndex = null)
        => BuildCustomGrayscaleAnaglyphCore(ProfileIds.CustomGrayscale, leftEyeColor, rightEyeColor, subtitleBurnIn, videoStreamIndex);

    /// <summary>
    /// The plain 2D rewrite: nothing inserted, nothing asked of the decode, subtitle
    /// handling kept by the caller.
    /// </summary>
    private static ProfileRewrite BuildTwoDimensional(string profileId)
        => new()
        {
            ProfileId = profileId,
            Kind = ProfileKind.TwoDimensional,
            InsertArguments = Array.Empty<string>()

            // OwnsVideoPipeline and RequiresComposedViewInput both stay false: the decoder's
            // own base-view default is the profile, and with nothing of ours in the pipeline
            // there is nothing to defend on the output.
        };

    /// <summary>
    /// Builds a rewrite over the composed all-view picture, with an optional linear
    /// conversion filter and an optional subtitle burn-in appended after it.
    /// </summary>
    /// <remarks>
    /// Shared by full SBS (no conversion filter at all), half SBS (the full-frame scale) and
    /// the stereo3d presets: they differ only in the chain they put behind the composed
    /// frame, while the composed decode - the one pivot - and the stream that carries it are
    /// the same question for all three. That shared answer is an empty insertion for full SBS:
    /// the composed frame needs neither a filter nor a map of its own, only the input the
    /// caller composed.
    /// </remarks>
    private static ProfileRewrite BuildComposedFiltered(
        string profileId,
        ProfileKind kind,
        string? profileFilter,
        SubtitleBurnIn? subtitleBurnIn)
    {
        var subtitleFilter = subtitleBurnIn is null ? null : BuildSubtitleFilter(subtitleBurnIn);

        // Burn-in lands last in the chain: text is rendered onto the finished profile
        // picture, never onto the separate eyes.
        var finalFilter = (profileFilter, subtitleFilter) switch
        {
            (null, null) => null,
            (null, not null) => subtitleFilter,
            (not null, null) => profileFilter,
            (not null, not null) => profileFilter + "," + subtitleFilter
        };

        IReadOnlyList<string> args = finalFilter is null
            ? Array.Empty<string>()
            : new[] { VideoFilterArgument, finalFilter };

        return new ProfileRewrite
        {
            ProfileId = profileId,
            Kind = kind,
            InsertArguments = args,
            OwnsVideoPipeline = true,
            RequiresComposedViewInput = true,

            // VideoMap stays null: the composed frames arrive on the stream the server already
            // named, so this profile has no map to insert and no server map to displace.
            VideoMap = null,
            VideoFilter = profileFilter,
            FilterComplex = null,
            FilterComplexInput = null,
            SubtitleFilter = subtitleFilter,

            // Suppression is the burn-in's consequence and not the conversion's: a profile that
            // only converts the picture leaves the server's subtitle choice standing, with its
            // maps and its filter graphs intact.
            ShouldSuppressSubtitleStreams = subtitleFilter is not null,
            ShouldAppendSubtitlesToProfileFilter = profileFilter is not null && subtitleFilter is not null
        };
    }

    /// <summary>
    /// Builds the custom grayscale rewrite: one filter graph over the composed stream plus
    /// the map of its labelled result.
    /// </summary>
    private static ProfileRewrite BuildCustomGrayscaleAnaglyphCore(
        string profileId,
        RgbColor leftEyeColor,
        RgbColor rightEyeColor,
        SubtitleBurnIn? subtitleBurnIn,
        int? videoStreamIndex)
    {
        var filterInput = ComposedStreamInput(videoStreamIndex);
        var filterComplex = BuildCustomGrayscaleFilterGraph(filterInput, leftEyeColor, rightEyeColor);
        var subtitleFilter = subtitleBurnIn is null ? null : BuildSubtitleFilter(subtitleBurnIn);

        var args = new List<string>(subtitleFilter is null ? 4 : 6)
        {
            FilterComplexArgument,
            filterComplex,
            MapArgument,
            CustomAnaglyphOutputLabel
        };

        // The graph output is already its own mapped stream, so the burn-in goes into
        // a plain -vf behind the map rather than into the graph itself.
        if (subtitleFilter is not null)
        {
            args.Add(VideoFilterArgument);
            args.Add(subtitleFilter);
        }

        return new ProfileRewrite
        {
            ProfileId = profileId,
            Kind = ProfileKind.CustomGrayscaleAnaglyph,
            InsertArguments = args,
            OwnsVideoPipeline = true,
            RequiresComposedViewInput = true,

            // This profile is the one that does own the output's video map: a graph output is
            // a new stream, unnamed until it is mapped, and the composed stream it was made
            // from must not travel to the output beside it.
            VideoMap = CustomAnaglyphOutputLabel,
            VideoFilter = null,
            FilterComplex = filterComplex,
            FilterComplexInput = filterInput,
            SubtitleFilter = subtitleFilter,

            // Same rule as the linear profiles: only the burn-in asks for the server's subtitle
            // streams to go, never the conversion the graph performs.
            ShouldSuppressSubtitleStreams = subtitleFilter is not null

            // ShouldAppendSubtitlesToProfileFilter stays false: the burn-in for this
            // profile is its own stage behind the mapped graph output, not a merge
            // into a profile chain.
        };
    }

    /// <summary>
    /// The filtergraph label of the composed video stream: the marker input's video stream
    /// named by index, or by its stream type when no index was named.
    /// </summary>
    /// <remarks>
    /// Named by index wherever the index is known, because that is the one spelling that
    /// addresses exactly the stream the caller composed: a bare <c>0:v</c> means "every video
    /// stream of this input", which is a second picture in the graph's way as soon as a file
    /// carries one (an attached cover track is enough). Neither spelling selects a view, so
    /// both survive the <see cref="ComposedViewInputOption"/> the caller set on this input.
    /// </remarks>
    private static string ComposedStreamInput(int? videoStreamIndex)
        => videoStreamIndex is int index
            ? string.Concat("[0:", index.ToString(CultureInfo.InvariantCulture), "]")
            : UnnamedVideoStreamFilterInput;

    /// <summary>
    /// Assembles the custom grayscale filter graph.
    /// </summary>
    /// <remarks>
    /// The chain reads, left to right: take the composed native SBS frames off the one
    /// decoded stream (one decode), split each combined frame, crop out each eye, reduce it
    /// to grayscale and expand it back to RGB (which replicates the luma into all three
    /// channels), multiply each channel by the eye colour's coefficient, and screen-combine
    /// the eyes into the anaglyph. Colours only ever enter as coefficients derived from
    /// <see cref="RgbColor"/> byte values, never as text.
    /// </remarks>
    private static string BuildCustomGrayscaleFilterGraph(
        string filterInput,
        RgbColor leftEyeColor,
        RgbColor rightEyeColor)
        => string.Join(
            ";",
            filterInput + "split=2" + CustomLeftInput + CustomRightInput,
            CustomLeftInput + LeftEyeCrop + "," + GrayscaleTintFilter(leftEyeColor) + CustomLeftEye,
            CustomRightInput + RightEyeCrop + "," + GrayscaleTintFilter(rightEyeColor) + CustomRightEye,
            CustomLeftEye + CustomRightEye + ScreenCombineFilter + "," + FormatYuv420PFilter + CustomAnaglyphOutputLabel);

    /// <summary>
    /// One eye: grayscale, expand to RGB, multiply the channels by the eye colour.
    /// </summary>
    private static string GrayscaleTintFilter(RgbColor color)
        => string.Concat(
            ToGrayscaleFilter,
            ",",
            ToRgbFilter,
            ",",
            ColorChannelMixerPrefix,
            FormatTintCoefficient(color.Red),
            ":gg=",
            FormatTintCoefficient(color.Green),
            ":bb=",
            FormatTintCoefficient(color.Blue));

    /// <summary>
    /// Formats one colour channel as the multiplicative coefficient FFmpeg expects.
    /// </summary>
    /// <remarks>
    /// The value space is <c>0..255</c>, so the output is always a plain decimal in
    /// <c>0..1</c>; culture invariant and fixed precision to keep commands stable
    /// across machines.
    /// </remarks>
    private static string FormatTintCoefficient(byte channelValue)
        => (channelValue / 255d).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the <c>subtitles</c> filter for a validated burn-in request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path goes in as an explicit <c>filename=</c> option rather than through the
    /// filter's shorthand: the option parser reads a leading shorthand value by first
    /// scanning for a key, and its key characters include <c>/</c> and <c>.</c>
    /// (<c>libavutil/opt.c:get_key</c>), so a path carrying an <c>=</c> would be read as
    /// a key/value pair instead of as the filename.
    /// </para>
    /// <para>
    /// The ordinal then follows as a plain <c>:si=</c> pair. That is only unambiguous
    /// because <see cref="EscapeFilterPath"/> leaves no unescaped <c>:</c> and no
    /// unbalanced quote anywhere in the filename value - otherwise the option parser
    /// either stops the filename at a directory containing a colon or swallows the
    /// ordinal into the path.
    /// </para>
    /// </remarks>
    private static string BuildSubtitleFilter(SubtitleBurnIn subtitleBurnIn)
        => string.Concat(
            "subtitles=filename=",
            EscapeFilterPath(subtitleBurnIn.SourcePath),
            ":si=",
            subtitleBurnIn.SubtitleStreamOrdinal.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Renders a path as a filter option value that decodes back to exactly that path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A filter option value is decoded <em>twice</em> before FFmpeg uses it: once by
    /// the filtergraph parser, which reads a filter's option string with
    /// <c>av_get_token(filter, "[],;")</c> (<c>libavfilter/graphparser.c:filter_parse</c>),
    /// and once by the filter's own option parser, which reads each option value with
    /// <c>':'</c> as the only separator (<c>libavfilter/avfilter.c:ff_filter_opt_parse</c>
    /// through <c>libavutil/opt.c:av_opt_get_key_value</c>). Both passes run the same
    /// routine (<c>libavutil/avstring.c:av_get_token</c>): a backslash consumes the
    /// character after it, a <c>'...'</c> pair contributes its contents literally, and
    /// leading plus trailing whitespace are dropped.
    /// </para>
    /// <para>
    /// Escaping once is therefore not enough. <c>\:</c> alone is spent by the graph pass,
    /// which hands the option pass a bare <c>:</c> - and <c>:</c> is exactly what the
    /// option pass splits a value on, so <c>/data/dirs:with colon/film.mkv</c> would
    /// arrive at libass as <c>/data/dirs</c>. Every character that either parser
    /// consumes is consequently escaped twice: once for the option level, then again as
    /// a whole for the graph level. That is also what makes the trailing <c>:si=</c>
    /// survive - every path colon reaches the option parser still escaped, so the first
    /// unescaped colon left in the value is the one naming the ordinal.
    /// </para>
    /// <para>
    /// Runs of characters that need no escaping at either level are wrapped in
    /// single quotes instead, the documented filter-quoting form, which is what keeps an
    /// ordinary path readable in a command log. Quoting and escaping are deliberately
    /// never mixed inside one segment: the quotes are consumed by the graph pass alone,
    /// so anything between them has to be inert for the option pass as well. An
    /// apostrophe can never sit between them at all - FFmpeg cannot quote its own quote
    /// character - so it is escaped on both levels, which is also why the
    /// <c>'\''</c> idiom is not used here: it spends the graph pass leaving a lone quote
    /// in front of <c>:si=</c>, and that quote then eats the ordinal.
    /// </para>
    /// <para>
    /// Worked example: the path <c>/movies/It's Here/film.mkv</c> is emitted as the
    /// value <c>'/movies/It'\\\''s Here/film.mkv'</c>; the graph pass decodes that to
    /// <c>/movies/It\'s Here/film.mkv</c> and the option pass decodes it to the path.
    /// </para>
    /// <para>
    /// Neither parser gives <c>"</c> any meaning, so a double quote is quoted as literal
    /// text and needs no escape; keeping a command line intact for a shell, if the caller
    /// assembles one, is that caller's own layer of quoting.
    /// </para>
    /// </remarks>
    private static string EscapeFilterPath(string path)
    {
        var escaped = new StringBuilder(path.Length + 16);
        var runStart = 0;

        for (var index = 0; index <= path.Length; index++)
        {
            var atEnd = index == path.Length;

            if (!atEnd && IsFilterLiteral(path[index], index == 0 || index == path.Length - 1))
            {
                continue;
            }

            // Close the literal run collected so far, then spell the character that
            // ended it in its doubly escaped form.
            if (index > runStart)
            {
                escaped.Append('\'').Append(path, runStart, index - runStart).Append('\'');
            }

            if (!atEnd)
            {
                AppendFilterEscaped(escaped, path[index]);
            }

            runStart = index + 1;
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Whether a character may be written as literal text between graph-level quotes.
    /// </summary>
    /// <remarks>
    /// Whitespace is literal only between other characters: both parsers trim a value,
    /// so whitespace at either edge of the path has to go through the escaping route.
    /// </remarks>
    private static bool IsFilterLiteral(char value, bool atEdge)
        => !IsOptionSpecial(value)
           && !IsGraphSpecial(value)
           && !(atEdge && char.IsWhiteSpace(value));

    /// <summary>
    /// The characters the filter-option parser consumes inside a value: its pair
    /// separator plus its two quoting and escaping characters.
    /// </summary>
    private static bool IsOptionSpecial(char value) => value is ':' or '\\' or '\'';

    /// <summary>
    /// The characters the filtergraph parser consumes: the quoting and escaping
    /// characters, plus the ones that would cut this filter's option string short - a
    /// link label or the next filter in the chain.
    /// </summary>
    private static bool IsGraphSpecial(char value) => value is '\'' or '\\' or '[' or ']' or ',' or ';';

    /// <summary>
    /// Appends one character in the form that survives both decode passes: escaped for
    /// the option level first, then that escape escaped for the graph level.
    /// </summary>
    /// <remarks>
    /// Whitespace counts as option-level here because trimming it is the option pass's
    /// doing, and it is graph-escaped for the same reason - the backslash that protects
    /// it must itself survive the graph pass to be there when the option pass runs.
    /// </remarks>
    private static void AppendFilterEscaped(StringBuilder escaped, char value)
    {
        if (IsOptionSpecial(value) || char.IsWhiteSpace(value))
        {
            escaped.Append("\\\\");
        }

        if (IsGraphSpecial(value) || char.IsWhiteSpace(value))
        {
            escaped.Append('\\');
        }

        escaped.Append(value);
    }

    /// <summary>
    /// The stereo3d anaglyph conversion chain for an official output code.
    /// </summary>
    private static string Stereo3DAnaglyphFilter(string outputCode)
        => "stereo3d=sbsl:" + outputCode + "," + FormatYuv420PFilter;

    /// <summary>
    /// A burn-in only survives the dispatch when the profile supports it.
    /// </summary>
    private static SubtitleBurnIn? EligibleSubtitleBurnIn(StereoProfile profile, SubtitleBurnIn? subtitleBurnIn)
        => subtitleBurnIn is not null && profile.SupportsSubtitleBurnIn ? subtitleBurnIn : null;

    /// <summary>
    /// Reads the profile id off a profile, insisting on the allowlist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catalog validates its own ids while it is built, but a
    /// <see cref="StereoProfile"/> is a public shape: settings that survived a bad edit
    /// or a hand-built profile can carry any string in <c>Id</c>, and nothing about the
    /// id is used to choose the command - the kind alone decides that, so an unknown id
    /// would otherwise reach the assembled command line carrying a conversion it was
    /// never defined as.
    /// </para>
    /// <para>
    /// This is the last seam before FFmpeg, so the id is rejected rather than
    /// interpreted here, the same way the stereo3d output code below is, and before any
    /// argument is produced. The interface already promised this rejection; this is where
    /// it is kept.
    /// </para>
    /// </remarks>
    private static string RequireAllowedProfileId(StereoProfile profile)
    {
        var profileId = ProfileIds.Normalize(profile.Id);
        if (ProfileIds.IsAllowed(profileId))
        {
            return profileId;
        }

        throw new ArgumentException(
            $"'{profile.Id}' is not an allowlisted Anaglyfin profile id, so no command can be built for it. Allowed ids: {string.Join(", ", ProfileIds.AllProfileIds)}.",
            nameof(profile));
    }

    /// <summary>
    /// Reads the output code off an anaglyph profile, insisting on the allowlist.
    /// </summary>
    /// <remarks>
    /// The catalog validates its profiles on construction; the check is repeated here
    /// because a <see cref="StereoProfile"/> is a public shape a caller could hand-build
    /// and because the builder is the last seam before FFmpeg.
    /// </remarks>
    private static string RequireOutputCode(StereoProfile profile)
    {
        if (profile.Stereo3DOutputCode is null)
        {
            throw new InvalidOperationException($"Anaglyph profile '{profile.Id}' must carry an official stereo3d output code.");
        }

        return RequireAllowedOutputCode(profile.Stereo3DOutputCode, nameof(profile.Stereo3DOutputCode));
    }

    /// <summary>
    /// Checks an output code against the built-in anaglyph allowlist.
    /// </summary>
    private static string RequireAllowedOutputCode(string outputCode, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputCode);

        var code = outputCode.Trim();
        foreach (var allowed in ProfileIds.BuiltInAnaglyphOutputCodes)
        {
            if (string.Equals(allowed, code, StringComparison.Ordinal))
            {
                return code;
            }
        }

        throw new ArgumentException(
            $"'{outputCode}' is not an allowlisted stereo3d anaglyph output code. Allowed codes: {string.Join(", ", ProfileIds.BuiltInAnaglyphOutputCodes)}.",
            paramName);
    }

    /// <summary>
    /// Reads the eye colours off a custom grayscale profile.
    /// </summary>
    private static (RgbColor Left, RgbColor Right) RequireEyeColors(StereoProfile profile)
    {
        if (profile.LeftEyeColor is not { } left || profile.RightEyeColor is not { } right)
        {
            throw new InvalidOperationException($"Custom anaglyph profile '{profile.Id}' must carry validated eye colours.");
        }

        return (left, right);
    }
}
