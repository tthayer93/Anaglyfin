namespace Anaglyfin.Detection;

/// <summary>
/// How directly a piece of text states that an item is MVC.
/// </summary>
internal enum MvcMarkerStrength
{
    /// <summary>
    /// The text does not mention MVC in any form.
    /// </summary>
    None = 0,

    /// <summary>
    /// <c>mvc</c> appears, but swallowed inside a longer flag rather than as a marker.
    /// </summary>
    Inferred,

    /// <summary>
    /// A whole flag says MVC, for example <c>MVC</c>, <c>3DMVC</c> or <c>3D_MVC</c>.
    /// </summary>
    Explicit
}
