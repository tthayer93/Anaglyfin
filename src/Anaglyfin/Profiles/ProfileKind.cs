namespace Anaglyfin.Profiles;

/// <summary>
/// The conversion family a profile belongs to.
/// </summary>
/// <remarks>
/// This is a discriminator for the command builder, not a request: it tells the
/// builder which family of view selection and filtering a profile needs, while the
/// concrete arguments stay inside the builder. Every Anaglyfin profile derives from
/// a single decode of the source (the all-view native side-by-side pivot), except
/// the plain 2D profile which takes the decoder's default base view.
/// </remarks>
public enum ProfileKind
{
    /// <summary>
    /// Plain 2D output: the decoder's default base view, no view selection and no
    /// stereo conversion.
    /// </summary>
    TwoDimensional = 0,

    /// <summary>
    /// Full width side-by-side output. This is the native all-view output of the
    /// FFmpeg-mvc decoder, so it needs no scaling and no stereo filter.
    /// </summary>
    SideBySideFull = 1,

    /// <summary>
    /// Half width side-by-side output: the native all-view frame scaled down to half
    /// its width. Re-tagging with a stereo filter is not sufficient, real scaling is
    /// required.
    /// </summary>
    SideBySideHalf = 2,

    /// <summary>
    /// Anaglyph output produced by an allowlisted <c>stereo3d</c> output code.
    /// </summary>
    Stereo3DAnaglyph = 3,

    /// <summary>
    /// Anaglyph output produced from user chosen eye colours: each eye is reduced to
    /// grayscale, tinted with the chosen colour and both eyes are combined again.
    /// Deliberately not colour balanced (no Dubois projection).
    /// </summary>
    CustomGrayscaleAnaglyph = 4,

    /// <summary>
    /// Full height top-and-bottom (over-under) output: the composed side-by-side frame
    /// restacked with the left eye above the right, each eye keeping the source's own
    /// frame. Unlike full SBS this is not a layout the decoder emits natively, so it
    /// needs a real stereo conversion, and it is the one family whose encoded frame
    /// doubles the source's <em>height</em>.
    /// </summary>
    TopAndBottomFull = 5,

    /// <summary>
    /// Half height top-and-bottom (over-under) output: the restacked full frame scaled
    /// back to the source's own size, each eye occupying half the height. The vertical
    /// sibling of half SBS - a re-tag is not enough, real scaling is required.
    /// </summary>
    TopAndBottomHalf = 6
}
