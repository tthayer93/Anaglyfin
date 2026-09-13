using System;

namespace Anaglyfin.VersionItems;

/// <summary>
/// Asks for Anaglyfin's version items to be brought in line with the library and the settings.
/// </summary>
/// <remarks>
/// <para>
/// This is the only way in to the reconciliation work, and it is a request rather than a command:
/// whoever notices a change (a library event, an administrator saving settings, the server starting
/// up) is never the one allowed to act on it, because acting inline would put database writes on
/// the scanner's thread and on the settings-save request. The requester says "this may be out of
/// date now" and moves on.
/// </para>
/// <para>
/// Implementations coalesce: the same item asked about a hundred times during a folder refresh is
/// one reconciliation, and a hundred full-pass requests are one pass.
/// </para>
/// </remarks>
public interface IProfileVersionReconcileTrigger
{
    /// <summary>
    /// Asks for one item's versions to be reconciled.
    /// </summary>
    /// <param name="itemId">The item that may need different versions now.</param>
    /// <remarks>
    /// Asking about an item that turns out to be one of Anaglyfin's own version items is allowed
    /// and cheap: the reconciliation walks to the primary it belongs to, and refuses to build a
    /// version of a version.
    /// </remarks>
    void RequestItem(Guid itemId);

    /// <summary>
    /// Asks for every video in the library to be reconciled.
    /// </summary>
    /// <remarks>
    /// The blunt instrument, and the right one when the whole premise may have moved: the profiles
    /// an administrator has enabled changed, or the library itself was rebuilt. Requests coalesce,
    /// so a settings page saved five times costs one pass.
    /// </remarks>
    void RequestFullPass();
}
