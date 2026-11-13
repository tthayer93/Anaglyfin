using System;
using System.Threading;
using System.Threading.Tasks;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// Produces the live runtime status of this Anaglyfin installation, for the administrator's
/// read-only view.
/// </summary>
/// <remarks>
/// The answer is a cached snapshot, not a fresh probe on every call: the binaries it reports on
/// are the ones a page refresh would otherwise re-execute, and a status surface that forks a
/// process per request is a status surface that must not be refreshed. The refresh is single-flight
/// so that even a burst of requests runs one probe pass.
/// </remarks>
public interface IRuntimeStatusService
{
    /// <summary>
    /// Gets the runtime status, taking the cached snapshot when it is still fresh and otherwise
    /// starting — or joining — a probe pass.
    /// </summary>
    /// <param name="cancellationToken">Stops the wait for a probe pass that has not started yet.</param>
    /// <returns>The snapshot, current to within the service's cache lifetime.</returns>
    Task<RuntimeStatusResponse> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forces a fresh probe pass, ignoring the cached snapshot. Joins any refresh already running
    /// rather than starting a second one.
    /// </summary>
    /// <param name="cancellationToken">Stops the wait for a probe pass that has not started yet.</param>
    /// <returns>The snapshot this pass produced.</returns>
    Task<RuntimeStatusResponse> RefreshAsync(CancellationToken cancellationToken);
}
