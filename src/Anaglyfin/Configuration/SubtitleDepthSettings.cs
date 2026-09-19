using System;

namespace Anaglyfin.Configuration;

/// <summary>
/// The subtitle depth Anaglyfin will act on: one settings object read through the rules,
/// with room for exactly the numbers the FFmpeg-mvc filter accepts.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PluginConfiguration"/> stores what an administrator typed, and typing
/// includes ranges nothing downstream can use. This record is the read side of that: the
/// settings helpers hand every consumer - the wrapper settings file today, a filter graph
/// tomorrow - a value that is either switchably off or inside the accepted ranges, so no
/// consumer has to repeat the checking and none can be the one that forgot.
/// </para>
/// <para>
/// The ranges are not a product preference. <c>mvcsubdepth</c> keeps 64 pixels of
/// transparent slack per side and clamps anything wider, and its plane index addresses at
/// most 32 authored depth sequences, so a number outside these bounds is a value the
/// encoder would silently widen or refuse - and a value Anaglyfin never sends.
/// </para>
/// </remarks>
/// <param name="Enabled">Whether subtitle depth is asked for at all.</param>
/// <param name="Mode">Which reading of the depth to use; only meaningful when <paramref name="Enabled"/> is on.</param>
/// <param name="ShiftPixels">The constant eye shift, in native picture pixels, positive toward the viewer.</param>
/// <param name="Plane">The authored depth sequence index to read directly.</param>
public sealed record SubtitleDepthSettings(
    bool Enabled,
    SubtitleDepthMode Mode,
    int ShiftPixels,
    int Plane)
{
    /// <summary>
    /// Smallest constant eye shift the filter travels: its own clamp distance, in pixels
    /// behind the screen plane.
    /// </summary>
    public const int MinShiftPixels = -64;

    /// <summary>
    /// Largest constant eye shift the filter travels: its own clamp distance, in pixels in
    /// front of the screen plane.
    /// </summary>
    public const int MaxShiftPixels = 64;

    /// <summary>First depth sequence index an authored disc can carry.</summary>
    public const int MinPlane = 0;

    /// <summary>
    /// Last depth sequence index an authored disc can carry: the offset-metadata table
    /// holds at most 32 sequences.
    /// </summary>
    public const int MaxPlane = 31;

    /// <summary>
    /// Gets the settings that ask for nothing: the depth Anaglyfin produces by not asking,
    /// which is also the answer every unusable stored or received value falls back to.
    /// </summary>
    public static SubtitleDepthSettings Disabled { get; } = new(false, SubtitleDepthMode.Automatic, 0, 0);

    /// <summary>
    /// Whether a constant eye shift is one the filter honours as given.
    /// </summary>
    /// <param name="shiftPixels">The shift in native picture pixels.</param>
    /// <returns><c>true</c> inside <see cref="MinShiftPixels"/>..<see cref="MaxShiftPixels"/>.</returns>
    public static bool IsShiftInRange(int shiftPixels)
        => shiftPixels is >= MinShiftPixels and <= MaxShiftPixels;

    /// <summary>
    /// Whether a depth sequence index is one an authored disc can carry.
    /// </summary>
    /// <param name="plane">The sequence index.</param>
    /// <returns><c>true</c> inside <see cref="MinPlane"/>..<see cref="MaxPlane"/>.</returns>
    public static bool IsPlaneInRange(int plane)
        => plane is >= MinPlane and <= MaxPlane;

    /// <summary>
    /// Whether a stored or received mode is one of the modes Anaglyfin names.
    /// </summary>
    /// <param name="mode">The value to check, which may be an ordinal nothing declares.</param>
    /// <returns><c>true</c> for a declared <see cref="SubtitleDepthMode"/>.</returns>
    /// <remarks>
    /// Both settings serialisers can hand back an ordinal no name declares: the XML one
    /// parses a number into the enum without asking whether anybody named it, so a settings
    /// file from a future build carries a mode this build has never heard of.
    /// </remarks>
    public static bool IsDeclaredMode(SubtitleDepthMode mode)
        => Enum.IsDefined(mode);
}
