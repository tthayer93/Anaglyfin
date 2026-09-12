namespace Anaglyfin.Detection;

/// <summary>
/// Strength of the evidence behind an MVC/3D eligibility decision.
/// </summary>
/// <remarks>
/// The tiers exist so that consumers (the alternate media source provider, and later
/// the ffprobe based detection step) can apply their own threshold instead of
/// re-implementing the signal vocabulary. <see cref="Low"/> is deliberately recorded
/// for evidence the MVP does not act on, so that a later probe-based stage can still
/// use it as a hint.
/// </remarks>
public enum MvcDetectionConfidence
{
    /// <summary>
    /// No MVC evidence was found.
    /// </summary>
    None = 0,

    /// <summary>
    /// Fuzzy evidence only, for example text that merely contains <c>mvc</c> inside a
    /// longer word. Never enough to offer 3D sources in the MVP.
    /// </summary>
    Low = 1,

    /// <summary>
    /// A clear MVC marker that is either indirect (only present in the containing folder
    /// path) or contradicted by a non-MVC 3D format signal.
    /// </summary>
    Medium = 2,

    /// <summary>
    /// Decisive evidence: Jellyfin declares <see cref="MediaBrowser.Model.Entities.Video3DFormat.MVC"/>,
    /// or the file name, folder name, item name or an item tag carries an explicit MVC marker.
    /// </summary>
    High = 3
}
