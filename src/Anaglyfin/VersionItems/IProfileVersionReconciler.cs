using System;
using System.Threading;
using System.Threading.Tasks;

namespace Anaglyfin.VersionItems;

/// <summary>
/// Brings Anaglyfin's profile version items in line with the library.
/// </summary>
/// <remarks>
/// The reconciliation work behind a name the callers can be tested against: whoever notices a change
/// asks for work and never runs it, and the background service that does run it should not have to
/// know how a version item is built to know when to ask.
/// </remarks>
public interface IProfileVersionReconciler
{
    /// <summary>
    /// Reconciles every video in the library.
    /// </summary>
    /// <param name="cancellationToken">Stops the pass between items.</param>
    /// <returns>What the pass changed.</returns>
    Task<ProfileVersionReconcileResult> ReconcileLibraryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reconciles the versions of one item, whichever item the change was reported for.
    /// </summary>
    /// <param name="itemId">The item a change was noticed on.</param>
    /// <param name="cancellationToken">Stops the work between writes.</param>
    /// <returns>What the reconciliation changed.</returns>
    Task<ProfileVersionReconcileResult> ReconcileItemAsync(Guid itemId, CancellationToken cancellationToken);
}
