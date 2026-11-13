using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// The read-only runtime status endpoint the Anaglyfin administration page reads: a snapshot of
/// which binaries are in place and run, which environment variables the deployment set, and whether
/// the directories they name are usable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Admin-only, by the server's own policy.</b> The whole surface is <c>RequiresElevation</c>, the
/// server's admin policy - not merely any signed-in user. It resolves a role claim of
/// <c>Administrator</c> against the server's authentication, so a non-administrative user is refused
/// before the action runs, and the environment variables and filesystem layout this reveals stay
/// where only an administrator can see them.
/// </para>
/// <para>
/// <b>Read-only and discovered.</b> The action only asks the injected <see cref="IRuntimeStatusService"/>
/// for its snapshot; it changes nothing. Like every controller in a plugin assembly it is discovered
/// by the server as an application part and is deliberately not registered into the DI container -
/// the controller itself is not a registered service, only the service it is handed is.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Anaglyfin")]
public sealed class RuntimeStatusController : ControllerBase
{
    private readonly IRuntimeStatusService _runtimeStatusService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeStatusController"/> class.
    /// </summary>
    /// <param name="runtimeStatusService">The service that owns the runtime snapshot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtimeStatusService"/> is <c>null</c>.</exception>
    public RuntimeStatusController(IRuntimeStatusService runtimeStatusService)
    {
        _runtimeStatusService = runtimeStatusService ?? throw new ArgumentNullException(nameof(runtimeStatusService));
    }

    /// <summary>
    /// Gets the current runtime status, optionally asking the service for a fresh probe pass.
    /// </summary>
    /// <param name="refresh">
    /// When <c>true</c>, ignores the cache and calls <see cref="IRuntimeStatusService.RefreshAsync"/>;
    /// when <c>false</c> or absent, calls <see cref="IRuntimeStatusService.GetStatusAsync"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops this request from waiting for the snapshot. It does not cancel the service's shared
    /// probe pass; that pass completes on its own and may still become the cache.
    /// </param>
    /// <returns>A <see cref="RuntimeStatusResponse"/> describing the installation.</returns>
    /// <response code="200">The runtime status snapshot.</response>
    [HttpGet("RuntimeStatus")]
    [ProducesResponseType(typeof(RuntimeStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RuntimeStatusResponse>> GetRuntimeStatus(
        [FromQuery] bool? refresh = null,
        CancellationToken cancellationToken = default)
    {
        var status = refresh == true
            ? await _runtimeStatusService.RefreshAsync(cancellationToken).ConfigureAwait(false)
            : await _runtimeStatusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        return Ok(status);
    }
}
