using System;

namespace Anaglyfin.Profiles;

/// <summary>
/// A single selectable conversion: identity plus metadata, never command lines.
/// </summary>
/// <remarks>
/// <para>
/// A profile describes <em>what</em> the output is so that the media source provider
/// can name it, the admin UI can list it and the command builder can decide how to
/// build it. It deliberately carries no filtergraph, no argument array and no free
/// text that could be passed to FFmpeg: the only conversion detail it stores is the
/// official <c>stereo3d</c> output code of a built-in anaglyph preset and the two
/// validated eye colours of the custom profile, both of which are validated
/// allowlisted values rather than strings from a caller.
/// </para>
/// <para>
/// Every profile is produced by one decode of the source: the all-view native
/// side-by-side pivot. Full SBS is that pivot unchanged, while half SBS and the
/// anaglyph profiles are derived from the same output, so choosing a profile never
/// means decoding the file twice.
/// </para>
/// </remarks>
public sealed record StereoProfile
{
    /// <summary>
    /// Gets the allowlisted profile id.
    /// </summary>
    /// <remarks>
    /// This is the value that travels through media source markers and settings. It
    /// must be one of <see cref="ProfileIds.AllProfileIds"/>.
    /// </remarks>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the human readable name shown to clients as the version label.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the conversion family this profile belongs to.
    /// </summary>
    public required ProfileKind Kind { get; init; }

    /// <summary>
    /// Gets the official <c>stereo3d</c> output code, for built-in anaglyph presets.
    /// </summary>
    /// <remarks>
    /// Null for every profile that is not an anaglyph preset. The value always comes
    /// from <see cref="ProfileIds.BuiltInAnaglyphOutputCodes"/>; it is an official
    /// FFmpeg code name, never a caller supplied string.
    /// </remarks>
    public string? Stereo3DOutputCode { get; init; }

    /// <summary>
    /// Gets the colour applied to the left eye.
    /// </summary>
    /// <remarks>
    /// Only the custom grayscale anaglyph profile carries eye colours; they are
    /// validated <see cref="RgbColor"/> values, never raw text.
    /// </remarks>
    public RgbColor? LeftEyeColor { get; init; }

    /// <summary>
    /// Gets the colour applied to the right eye.
    /// </summary>
    /// <remarks>
    /// Only the custom grayscale anaglyph profile carries eye colours; they are
    /// validated <see cref="RgbColor"/> values, never raw text.
    /// </remarks>
    public RgbColor? RightEyeColor { get; init; }

    /// <summary>
    /// Gets the position of this profile in a version list that has no default to
    /// promote.
    /// </summary>
    /// <remarks>
    /// Lower values are listed first. The value is catalog policy, not user input.
    /// </remarks>
    public int DisplayOrder { get; init; }

    /// <summary>
    /// Gets a value indicating whether subtitles can be burned into this output.
    /// </summary>
    /// <remarks>
    /// Burn-in happens after the profile conversion, never before the eyes are
    /// separated, so the label stays true for every stereo output.
    /// </remarks>
    public bool SupportsSubtitleBurnIn { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the profile needs the all-view pivot.
    /// </summary>
    /// <remarks>
    /// Only the plain 2D profile is served by the decoder's default base view;
    /// everything else is derived from the all-view native side-by-side output.
    /// </remarks>
    public bool RequiresAllViews => Kind != ProfileKind.TwoDimensional;

    /// <summary>
    /// Gets a value indicating whether this profile produces an anaglyph image.
    /// </summary>
    public bool IsAnaglyph => Kind is ProfileKind.Stereo3DAnaglyph or ProfileKind.CustomGrayscaleAnaglyph;

    /// <summary>
    /// Returns a copy of this profile with the given eye colours applied.
    /// </summary>
    /// <param name="leftEyeColor">The validated left eye tint.</param>
    /// <param name="rightEyeColor">The validated right eye tint.</param>
    /// <returns>A profile carrying both colours.</returns>
    /// <remarks>
    /// This is how the catalog hands the admin chosen colours to a profile: the
    /// settings text is parsed once into <see cref="RgbColor"/> values and the
    /// resulting profile only ever exposes those.
    /// </remarks>
    public StereoProfile WithEyeColors(RgbColor leftEyeColor, RgbColor rightEyeColor)
        => this with { LeftEyeColor = leftEyeColor, RightEyeColor = rightEyeColor };

    /// <inheritdoc />
    public override string ToString()
        => $"{Id} ({DisplayName})";

    /// <summary>
    /// Validates the structural rules a profile must obey.
    /// </summary>
    /// <exception cref="InvalidOperationException">The profile is malformed.</exception>
    /// <remarks>
    /// Called while the catalog is built, so a catalog that drifts from the allowlist
    /// fails loudly at start-up instead of leaking a broken profile to a client.
    /// </remarks>
    internal void Validate()
    {
        if (!ProfileIds.IsAllowed(Id))
        {
            throw new InvalidOperationException($"Profile id '{Id}' is not on the profile id allowlist.");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new InvalidOperationException($"Profile '{Id}' has no display name.");
        }

        if (Stereo3DOutputCode is not null)
        {
            if (Kind != ProfileKind.Stereo3DAnaglyph)
            {
                throw new InvalidOperationException($"Profile '{Id}' of kind {Kind} must not carry a stereo3d output code.");
            }

            if (!IsAllowedCode(Stereo3DOutputCode))
            {
                throw new InvalidOperationException($"Anaglyph profile '{Id}' carries the unknown stereo3d output code '{Stereo3DOutputCode}'.");
            }
        }
        else if (Kind == ProfileKind.Stereo3DAnaglyph)
        {
            throw new InvalidOperationException($"Anaglyph profile '{Id}' must carry an official stereo3d output code.");
        }

        var hasEyeColors = LeftEyeColor is not null || RightEyeColor is not null;
        if (hasEyeColors && Kind != ProfileKind.CustomGrayscaleAnaglyph)
        {
            throw new InvalidOperationException($"Profile '{Id}' of kind {Kind} must not carry eye colours.");
        }

        if (!hasEyeColors && Kind == ProfileKind.CustomGrayscaleAnaglyph)
        {
            throw new InvalidOperationException($"Custom anaglyph profile '{Id}' must carry validated eye colours.");
        }
    }

    private static bool IsAllowedCode(string code)
    {
        foreach (var allowed in ProfileIds.BuiltInAnaglyphOutputCodes)
        {
            if (string.Equals(allowed, code, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
