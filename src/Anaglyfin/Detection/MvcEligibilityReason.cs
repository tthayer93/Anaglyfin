namespace Anaglyfin.Detection;

/// <summary>
/// Why an item was (or was not) considered an eligible 3D MVC source.
/// </summary>
/// <remarks>
/// The reason is part of the decision so that callers can log or surface the same
/// explanation operators need when a movie unexpectedly does (or does not) offer 3D
/// versions. Reasons describe the strongest signal that decided the outcome; the
/// matching marker text is carried separately in
/// <see cref="MvcSourceEligibility.Detail"/>.
/// </remarks>
public enum MvcEligibilityReason
{
    /// <summary>
    /// Nothing in the path, name, tags or metadata mentions 3D or MVC at all.
    /// </summary>
    NoMvcSignal = 0,

    /// <summary>
    /// Jellyfin metadata declares <c>Video3DFormat.MVC</c>, which is authoritative.
    /// </summary>
    ItemMetadataDeclaresMvc,

    /// <summary>
    /// The file or folder name carries an explicit MVC marker.
    /// </summary>
    FileNameDeclaresMvc,

    /// <summary>
    /// The Jellyfin item name carries an explicit MVC marker.
    /// </summary>
    ItemNameDeclaresMvc,

    /// <summary>
    /// A Jellyfin item tag carries an explicit MVC marker.
    /// </summary>
    ItemTagDeclaresMvc,

    /// <summary>
    /// An explicit MVC marker appears in the containing folder path but not on the item
    /// itself, for example a shared <c>3D MVC</c> library folder.
    /// </summary>
    DirectoryPathDeclaresMvc,

    /// <summary>
    /// An explicit MVC marker is present but a non-MVC 3D format is declared as well, so
    /// the item is offered with reduced confidence.
    /// </summary>
    MvcMarkerContradictsNonMvcFormat,

    /// <summary>
    /// <c>mvc</c> only appears embedded inside a longer word, which is not a marker.
    /// </summary>
    EmbeddedMvcTextOnly,

    /// <summary>
    /// The item is flagged as 3D side-by-side or top-and-bottom without any MVC marker.
    /// The MVP does not convert those inputs, so they stay ineligible.
    /// </summary>
    SideBySideOrTopAndBottomWithoutMvc,

    /// <summary>
    /// The item is flagged as 3D but carries no format marker at all.
    /// </summary>
    ThreeDWithoutMvcMarker,

    /// <summary>
    /// Jellyfin metadata declares a non-MVC 3D format and nothing suggests MVC.
    /// </summary>
    MetadataDeclaresNonMvcFormat
}
