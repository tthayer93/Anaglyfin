namespace Anaglyfin.Markers;

/// <summary>
/// The outcome classification returned by <see cref="ProfileMarkerParser.Parse(string?)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The distinction between <see cref="NotMarker"/> and every other failure is the
/// contract the FFmpeg wrapper builds on: a non-marker argument is ordinary FFmpeg
/// input and must pass through untouched, while a failed marker - a broken, forged or
/// unknown-profile marker - is never ordinary input and must fail the job closed
/// instead of being handed to FFmpeg.
/// </para>
/// <para>
/// The classifications are disjoint by construction: the parser reports at most one
/// reason, choosing the first one it encounters in marker order (structure, profile,
/// source, subtitle).
/// </para>
/// </remarks>
public enum MarkerParseStatus
{
    /// <summary>
    /// The text is a well-formed marker and every field passed validation.
    /// </summary>
    Success,

    /// <summary>
    /// The text does not carry the marker prefix and is not an Anaglyfin marker; a
    /// wrapper must treat the surrounding input as ordinary FFmpeg traffic.
    /// </summary>
    NotMarker,

    /// <summary>
    /// The text carries the marker prefix but is not a well-formed marker: broken
    /// query structure, a missing profile-id segment, a raw control character, or a
    /// query parameter Anaglyfin does not define.
    /// </summary>
    MalformedMarker,

    /// <summary>
    /// The marker names a profile id that is not on the <see cref="Profiles.ProfileIds"/>
    /// allowlist.
    /// </summary>
    UnknownProfile,

    /// <summary>
    /// The marker carries no <c>source</c> parameter, or its value is empty or blank.
    /// </summary>
    MissingSource,

    /// <summary>
    /// The decoded source path is not a rooted filesystem path or contains a control
    /// character.
    /// </summary>
    InvalidSource,

    /// <summary>
    /// The <c>subtitle</c> parameter is not a non-negative integer.
    /// </summary>
    InvalidSubtitleOrdinal
}
