namespace Anaglyfin.Detection;

/// <summary>
/// The 3D related flags found inside one string, as produced by <see cref="MvcDetectionRules"/>.
/// </summary>
internal sealed class TokenSignals
{
    internal TokenSignals(
        MvcMarkerStrength mvcStrength,
        string? mvcMarker,
        string? threeDimensionalMarker,
        string? nonMvcFormatMarker)
    {
        MvcStrength = mvcStrength;
        MvcMarker = mvcMarker;
        ThreeDimensionalMarker = threeDimensionalMarker;
        NonMvcFormatMarker = nonMvcFormatMarker;
    }

    /// <summary>
    /// A reused result for inputs without any signal.
    /// </summary>
    internal static TokenSignals Empty { get; } = new(MvcMarkerStrength.None, null, null, null);

    /// <summary>
    /// Gets how directly the text states MVC.
    /// </summary>
    internal MvcMarkerStrength MvcStrength { get; }

    /// <summary>
    /// Gets the flag that carried the MVC statement, if any.
    /// </summary>
    internal string? MvcMarker { get; }

    /// <summary>
    /// Gets the flag that stated plain 3D without naming a format, if any.
    /// </summary>
    internal string? ThreeDimensionalMarker { get; }

    /// <summary>
    /// Gets the flag that stated a stereoscopic format Anaglyfin does not handle yet, if any.
    /// </summary>
    internal string? NonMvcFormatMarker { get; }
}
