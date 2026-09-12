using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Command semantics, one decode per playback:
/// </para>
/// <list type="bullet">
/// <item>
/// 2D base inserts nothing: FFmpeg-mvc defaults to the base view, so the profile is
/// the untouched default command.
/// </item>
/// <item>
/// Full SBS is the native all-view output: <c>-map 0:v:view:all</c> and no filter.
/// </item>
/// <item>
/// Half SBS derives from that same all-view output by scaling the combined frame
/// (<c>scale=iw/2:ih</c>), not by decoding eyes separately and not by a stereo3d
/// re-tag, which only changes the pixel aspect ratio.
/// </item>
/// <item>
/// A built-in anaglyph preset is the all-view output through
/// <c>stereo3d=sbsl:&lt;code&gt;</c> for an official output code.
/// </item>
/// <item>
/// The custom grayscale anaglyph is one filter graph over the all-view output: split,
/// grayscale each eye, tint each eye via <c>colorchannelmixer</c> with the validated
/// colour's channel coefficients, and screen-combine the two eyes.
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

    /// <summary>The FFmpeg-mvc stream specifier selecting all views as native SBS.</summary>
    public const string AllViewsMapValue = "0:v:view:all";

    /// <summary>The filter graph input label for the all-view native SBS stream.</summary>
    public const string AllViewsFilterInput = "[0:v:view:all]";

    /// <summary>Pix_fmt normalisation appended to every profile conversion chain.</summary>
    public const string FormatYuv420PFilter = "format=yuv420p";

    /// <summary>
    /// Half SBS conversion: scale the combined native SBS frame to half width. Real
    /// scaling, so the output is genuinely half-side-by-side.
    /// </summary>
    public const string HalfSideBySideScaleFilter = "scale=iw/2:ih:flags=bicubic,format=yuv420p";

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
    public ProfileRewrite Build(StereoProfile profile, SubtitleBurnIn? subtitleBurnIn = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var profileId = ProfileIds.Normalize(profile.Id);
        var burnIn = EligibleSubtitleBurnIn(profile, subtitleBurnIn);

        switch (profile.Kind)
        {
            case ProfileKind.TwoDimensional:
                // The plain 2D version is what the FFmpeg-mvc binary already produces
                // for the command as written, and its subtitle handling is the stock
                // pipeline's, so a burn-in request cannot make this rewrite busier.
                return BuildTwoDimensional(profileId);

            case ProfileKind.SideBySideFull:
                return BuildAllViewFiltered(profileId, ProfileKind.SideBySideFull, profileFilter: null, burnIn);

            case ProfileKind.SideBySideHalf:
                return BuildAllViewFiltered(profileId, ProfileKind.SideBySideHalf, HalfSideBySideScaleFilter, burnIn);

            case ProfileKind.Stereo3DAnaglyph:
                {
                    var code = RequireOutputCode(profile);
                    return BuildAllViewFiltered(profileId, ProfileKind.Stereo3DAnaglyph, Stereo3DAnaglyphFilter(code), burnIn);
                }

            case ProfileKind.CustomGrayscaleAnaglyph:
                {
                    var (left, right) = RequireEyeColors(profile);
                    return BuildCustomGrayscaleAnaglyphCore(profileId, left, right, burnIn);
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
        => BuildAllViewFiltered(ProfileIds.SideBySideFull, ProfileKind.SideBySideFull, profileFilter: null, subtitleBurnIn);

    /// <inheritdoc />
    public ProfileRewrite BuildSideBySideHalf(SubtitleBurnIn? subtitleBurnIn = null)
        => BuildAllViewFiltered(ProfileIds.SideBySideHalf, ProfileKind.SideBySideHalf, HalfSideBySideScaleFilter, subtitleBurnIn);

    /// <inheritdoc />
    public ProfileRewrite BuildStereo3DAnaglyph(string stereo3DOutputCode, SubtitleBurnIn? subtitleBurnIn = null)
    {
        var code = RequireAllowedOutputCode(stereo3DOutputCode, nameof(stereo3DOutputCode));

        return BuildAllViewFiltered(
            ProfileIds.BuildAnaglyphProfileId(code),
            ProfileKind.Stereo3DAnaglyph,
            Stereo3DAnaglyphFilter(code),
            subtitleBurnIn);
    }

    /// <inheritdoc />
    public ProfileRewrite BuildCustomGrayscaleAnaglyph(RgbColor leftEyeColor, RgbColor rightEyeColor, SubtitleBurnIn? subtitleBurnIn = null)
        => BuildCustomGrayscaleAnaglyphCore(ProfileIds.CustomGrayscale, leftEyeColor, rightEyeColor, subtitleBurnIn);

    /// <summary>
    /// The plain 2D rewrite: no insertion, no map override, subtitle handling kept by
    /// the caller.
    /// </summary>
    private static ProfileRewrite BuildTwoDimensional(string profileId)
        => new()
        {
            ProfileId = profileId,
            Kind = ProfileKind.TwoDimensional,
            InsertArguments = Array.Empty<string>()
        };

    /// <summary>
    /// Builds a rewrite over the all-view native SBS output, with an optional linear
    /// conversion filter and an optional subtitle burn-in appended after it.
    /// </summary>
    /// <remarks>
    /// Shared by full SBS (no conversion filter at all), half SBS (the full-frame
    /// scale) and the stereo3d presets: they differ only in the chain they put behind
    /// the all-view map, so the view selection - the one decode pivot - is written once.
    /// </remarks>
    private static ProfileRewrite BuildAllViewFiltered(
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

        var args = new List<string>(finalFilter is null ? 2 : 4)
        {
            MapArgument,
            AllViewsMapValue
        };

        if (finalFilter is not null)
        {
            args.Add(VideoFilterArgument);
            args.Add(finalFilter);
        }

        return new ProfileRewrite
        {
            ProfileId = profileId,
            Kind = kind,
            InsertArguments = args,
            VideoMap = AllViewsMapValue,
            VideoFilter = profileFilter,
            FilterComplex = null,
            SubtitleFilter = subtitleFilter,
            ShouldSuppressSubtitleStreams = true,
            ShouldAppendSubtitlesToProfileFilter = profileFilter is not null && subtitleFilter is not null
        };
    }

    /// <summary>
    /// Builds the custom grayscale rewrite: one filter graph over the all-view output
    /// plus the map of its labelled result.
    /// </summary>
    private static ProfileRewrite BuildCustomGrayscaleAnaglyphCore(
        string profileId,
        RgbColor leftEyeColor,
        RgbColor rightEyeColor,
        SubtitleBurnIn? subtitleBurnIn)
    {
        var filterComplex = BuildCustomGrayscaleFilterGraph(leftEyeColor, rightEyeColor);
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
            VideoMap = CustomAnaglyphOutputLabel,
            VideoFilter = null,
            FilterComplex = filterComplex,
            SubtitleFilter = subtitleFilter,
            ShouldSuppressSubtitleStreams = true

            // ShouldAppendSubtitlesToProfileFilter stays false: the burn-in for this
            // profile is its own stage behind the mapped graph output, not a merge
            // into a profile chain.
        };
    }

    /// <summary>
    /// Assembles the custom grayscale filter graph.
    /// </summary>
    /// <remarks>
    /// The chain reads, left to right: take the all-view native SBS frames (one
    /// decode), split each combined frame, crop out each eye, reduce it to grayscale
    /// and expand it back to RGB (which replicates the luma into all three channels),
    /// multiply each channel by the eye colour's coefficient, and screen-combine the
    /// eyes into the anaglyph. Colours only ever enter as coefficients derived from
    /// <see cref="RgbColor"/> byte values, never as text.
    /// </remarks>
    private static string BuildCustomGrayscaleFilterGraph(RgbColor leftEyeColor, RgbColor rightEyeColor)
        => string.Join(
            ";",
            AllViewsFilterInput + "split=2" + CustomLeftInput + CustomRightInput,
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
    private static string BuildSubtitleFilter(SubtitleBurnIn subtitleBurnIn)
        => string.Concat(
            "subtitles=",
            EscapeFilterPath(subtitleBurnIn.SourcePath),
            ":si=",
            subtitleBurnIn.SubtitleStreamOrdinal.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Quotes a path for use as a filtergraph argument value.
    /// </summary>
    /// <remarks>
    /// FFmpeg's documented filter-quoting form: wrap the value in single quotes, so
    /// colons, brackets, semicolons and spaces lose their filtergraph meaning (this
    /// also keeps Windows drive letters intact), and spell any literal single quote
    /// with the <c>'\''</c> idiom. Control characters are rejected by
    /// <see cref="SubtitleBurnIn"/>, so nothing can break out of the value.
    /// </remarks>
    private static string EscapeFilterPath(string path)
        => "'" + path.Replace("'", @"'\''", StringComparison.Ordinal) + "'";

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
