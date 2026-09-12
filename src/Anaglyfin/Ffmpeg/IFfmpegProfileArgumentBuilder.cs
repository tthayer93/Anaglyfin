using Anaglyfin.Profiles;

namespace Anaglyfin.Ffmpeg;

/// <summary>
/// Turns an allowlisted profile into the FFmpeg arguments that realise it.
/// </summary>
/// <remarks>
/// <para>
/// The builder is the only place in Anaglyfin where profile semantics become FFmpeg
/// syntax. Its inputs are a catalog profile (or the fixed inputs of the explicit
/// methods), never FFmpeg text: callers cannot pass a filter, a map value or a colour
/// string through, and an unrecognised profile id or output code is rejected rather
/// than interpreted. Everything it emits is deterministic - the same profile in, the
/// same argument tokens out - so commands can be asserted verbatim in tests.
/// </para>
/// <para>
/// Every stereo profile is expressed over the single all-view native side-by-side
/// pivot (one decode per playback), and none of the generated arguments ever asks for
/// hardware decoding: MVC decode is software-only, and the encoder choice belongs to
/// the caller assembling the rest of the command.
/// </para>
/// </remarks>
public interface IFfmpegProfileArgumentBuilder
{
    /// <summary>
    /// Builds the rewrite for a catalog profile.
    /// </summary>
    /// <param name="profile">The catalog profile; its kind selects the conversion.</param>
    /// <param name="subtitleBurnIn">
    /// An optional subtitle burn-in, applied after the profile conversion. Ignored for
    /// the plain 2D profile (the stock pipeline owns its subtitles) and for profiles
    /// that do not support burn-in.
    /// </param>
    /// <returns>The arguments to insert into the transcode command.</returns>
    /// <exception cref="System.ArgumentException">
    /// The profile id is not on the profile id allowlist, or the profile declares a
    /// stereo3d output code that is not on the allowlist.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The profile is malformed: an anaglyph without an output code, or a custom
    /// grayscale profile without eye colours.
    /// </exception>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// The profile carries a kind this build does not know.
    /// </exception>
    ProfileRewrite Build(StereoProfile profile, SubtitleBurnIn? subtitleBurnIn = null);

    /// <summary>
    /// Builds the plain 2D profile: the decoder's default base view.
    /// </summary>
    /// <returns>
    /// A rewrite that inserts nothing, because an unmodified FFmpeg-mvc invocation
    /// already decodes exactly the base view this profile is.
    /// </returns>
    ProfileRewrite BuildTwoDimensionalBase();

    /// <summary>
    /// Builds the full side-by-side profile: the native all-view output unchanged.
    /// </summary>
    /// <param name="subtitleBurnIn">An optional subtitle burn-in.</param>
    /// <returns>
    /// A rewrite whose only conversion argument is the all-view map; the assembled
    /// full-width SBS frames are the profile output, so no filter is added.
    /// </returns>
    ProfileRewrite BuildSideBySideFull(SubtitleBurnIn? subtitleBurnIn = null);

    /// <summary>
    /// Builds the half side-by-side profile from the full side-by-side output.
    /// </summary>
    /// <param name="subtitleBurnIn">An optional subtitle burn-in.</param>
    /// <returns>
    /// A rewrite mapping the all-view output and scaling the combined frame to half
    /// width in place. Real scaling is required; a stereo3d re-tag keeps the frame
    /// size and only changes the pixel aspect ratio, so it is never used here.
    /// </returns>
    ProfileRewrite BuildSideBySideHalf(SubtitleBurnIn? subtitleBurnIn = null);

    /// <summary>
    /// Builds a built-in <c>stereo3d</c> anaglyph profile.
    /// </summary>
    /// <param name="stereo3DOutputCode">
    /// The official output code, which must be on
    /// <see cref="ProfileIds.BuiltInAnaglyphOutputCodes"/>.
    /// </param>
    /// <param name="subtitleBurnIn">An optional subtitle burn-in.</param>
    /// <returns>
    /// A rewrite mapping the all-view output and converting it with
    /// <c>stereo3d=sbsl:&lt;code&gt;</c>.
    /// </returns>
    /// <exception cref="System.ArgumentException">The code is not an allowlisted code.</exception>
    ProfileRewrite BuildStereo3DAnaglyph(string stereo3DOutputCode, SubtitleBurnIn? subtitleBurnIn = null);

    /// <summary>
    /// Builds the custom grayscale anaglyph profile for a pair of validated colours.
    /// </summary>
    /// <param name="leftEyeColor">The validated left eye tint.</param>
    /// <param name="rightEyeColor">The validated right eye tint.</param>
    /// <param name="subtitleBurnIn">An optional subtitle burn-in.</param>
    /// <returns>
    /// A rewrite carrying one filter graph over the all-view output: split the native
    /// side-by-side frame, grayscale each eye, tint each eye with the colour's channel
    /// coefficients, screen-combine, and map the labelled result. Deliberately not
    /// colour balanced.
    /// </returns>
    ProfileRewrite BuildCustomGrayscaleAnaglyph(RgbColor leftEyeColor, RgbColor rightEyeColor, SubtitleBurnIn? subtitleBurnIn = null);
}
