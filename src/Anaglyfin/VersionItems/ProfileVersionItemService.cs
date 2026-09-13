using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Anaglyfin.VersionItems;

/// <summary>
/// Runs the profile version-item reconciliation the rest of the plugin asks for.
/// </summary>
/// <remarks>
/// <para>
/// The plugin's only background worker, and the reason none of its callers ever writes to the
/// library: a library event arrives on the scanner's thread and a settings save on an HTTP request,
/// and neither may be blocked by database writes for versions of something else. Both hand the work
/// to <see cref="ProfileVersionReconcileQueue"/> and return; this service takes it from there.
/// </para>
/// <para>
/// <b>What it listens to.</b> The library's three item events (added, updated, removed - the whole
/// of "something about a file or its metadata changed") and a full pass on start-up, which is what
/// covers an item that changed while the server was not running. A settings save arrives through the
/// same queue (<c>Plugin.SaveConfiguration</c> asks for the full pass).
/// </para>
/// <para>
/// <b>Why it debounces.</b> A folder refresh is not one change; it is an item, then its images, then
/// its metadata, then its parent, each an event. Reconciling after each one would run the same diff
/// over the same files half a dozen times, so a burst is waited out and answered once. The wait is a
/// quiet period rather than a fixed delay, so a quiet library is served the moment it asks and a
/// noisy one costs one pass.
/// </para>
/// <para>
/// <b>What keeps it from feeding itself.</b> Every write it makes raises events of its own -
/// <c>CreateItem</c> announces the item it created. Those are filtered here (a version item is asked
/// about, and the manager refuses to build a version of a version) and they are idempotent at the
/// manager, which reconciles to a state and stops. One pass of a settled library performs no writes,
/// therefore raises no events, therefore schedules no further pass.
/// </para>
/// </remarks>
public sealed class ProfileVersionItemService : IHostedService, IDisposable
{
    /// <summary>
    /// How long the queue must hold still before a burst is reconciled.
    /// </summary>
    public static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromSeconds(2);

    private readonly ILibraryEventSource _libraryEvents;

    private readonly IProfileVersionReconciler _reconciler;

    private readonly ProfileVersionReconcileQueue _queue;

    private readonly ILogger<ProfileVersionItemService> _logger;

    private readonly TimeSpan _debounceWindow;

    private readonly object _sync = new();

    private CancellationTokenSource? _stopping;

    private Task? _worker;

    private bool _listening;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileVersionItemService"/> class.
    /// </summary>
    /// <param name="libraryEvents">The library events to listen to.</param>
    /// <param name="reconciler">The work to run.</param>
    /// <param name="queue">The requests, already made and not yet acted on.</param>
    /// <param name="logger">Logger for the passes and their failures.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public ProfileVersionItemService(
        ILibraryEventSource libraryEvents,
        IProfileVersionReconciler reconciler,
        ProfileVersionReconcileQueue queue,
        ILogger<ProfileVersionItemService> logger)
        : this(libraryEvents, reconciler, queue, logger, DefaultDebounceWindow)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileVersionItemService"/> class with a chosen
    /// debounce window.
    /// </summary>
    /// <param name="libraryEvents">The library events to listen to.</param>
    /// <param name="reconciler">The work to run.</param>
    /// <param name="queue">The requests, already made and not yet acted on.</param>
    /// <param name="logger">Logger for the passes and their failures.</param>
    /// <param name="debounceWindow">How long a burst is waited out before it is reconciled.</param>
    /// <exception cref="ArgumentNullException">
    /// Any reference argument is <c>null</c>, or <paramref name="debounceWindow"/> is negative.
    /// </exception>
    public ProfileVersionItemService(
        ILibraryEventSource libraryEvents,
        IProfileVersionReconciler reconciler,
        ProfileVersionReconcileQueue queue,
        ILogger<ProfileVersionItemService> logger,
        TimeSpan debounceWindow)
    {
        _libraryEvents = libraryEvents ?? throw new ArgumentNullException(nameof(libraryEvents));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (debounceWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceWindow), "A debounce window cannot be in the past.");
        }

        _debounceWindow = debounceWindow;
    }

    /// <summary>
    /// Starts listening, and asks for the first pass over the library.
    /// </summary>
    /// <param name="cancellationToken">Stops the start.</param>
    /// <returns>A task completed once the service is listening.</returns>
    /// <remarks>
    /// The first request is made before the listener is attached, so an event that arrives during
    /// start-up can only add to the pass that is already queued, never be missed by it. The pass
    /// itself runs on the worker, not here: the host has better things to do between starting plugins
    /// and serving the first request than to walk a library.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_worker is not null)
            {
                return Task.CompletedTask;
            }

            _stopping = new CancellationTokenSource();

            _libraryEvents.ItemAdded += OnItemChanged;
            _libraryEvents.ItemUpdated += OnItemChanged;
            _libraryEvents.ItemRemoved += OnItemRemoved;
            _listening = true;

            var token = _stopping.Token;
            _queue.RequestFullPass();

            _worker = Task.Run(() => ProcessAsync(token), token);

            _logger.LogDebug(
                "Anaglyfin is reconciling profile version items, debouncing library changes for {Window}.",
                _debounceWindow);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops listening and lets the pass in progress finish.
    /// </summary>
    /// <param name="cancellationToken">Stops the wait, not the work.</param>
    /// <returns>A task completed once the worker has stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? stopping;
        Task? worker;

        lock (_sync)
        {
            Unsubscribe();

            stopping = _stopping;
            worker = _worker;

            _stopping = null;
            _worker = null;
        }

        stopping?.Cancel();

        if (worker is null)
        {
            return;
        }

        try
        {
            // A pass interrupted mid-write leaves at most one item half-created, and the next
            // start-up pass is a diff over everything: it will notice. Waiting past the host's own
            // patience would hold the shutdown up for work that is already repaired.
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Anaglyfin's version-item pass did not finish before shutdown; the next start reconciles it.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anaglyfin's version-item worker ended abnormally during shutdown.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            Unsubscribe();
        }

        _stopping?.Dispose();
    }

    /// <summary>
    /// The library noticed an item. Whether it is one that could carry versions is not decided here.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="args">The item that changed.</param>
    /// <remarks>
    /// Cheap by contract: runs on the scanner's thread for every item of every refresh, so it looks at
    /// nothing but the item's kind and identity and hands the question on. Folders, audio and people
    /// are filtered because they can never be a video with a 3D question; everything else is asked
    /// about, including the items whose versions belong to another item - that walk is the
    /// reconciler's job.
    /// </remarks>
    public void OnItemChanged(object? sender, ItemChangeEventArgs? args)
    {
        if (args?.Item is null or not Video)
        {
            return;
        }

        _queue.RequestItem(args.Item.Id);
    }

    /// <summary>
    /// The library removed an item.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="args">The item that was removed.</param>
    /// <remarks>
    /// A removed item asks for nothing: it is gone, and the versions of a file that no longer exists
    /// are removed by the pass over the item they belonged to (which the server also runs, since the
    /// removal of a primary takes its alternate versions with it). An Anaglyfin version removing one
    /// of its own items is the same event, and answering it would be the loop this whole listener is
    /// built to avoid.
    /// </remarks>
    public void OnItemRemoved(object? sender, ItemChangeEventArgs? args)
    {
        if (args?.Item is null)
        {
            return;
        }

        _logger.LogDebug("Anaglyfin noticed {ItemName} leaving the library; its versions go with it.", args.Item.Name);
    }

    /// <summary>
    /// Takes requests as they arrive and answers each burst with one pass.
    /// </summary>
    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Anaglyfin's version-item worker is running.");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _queue.WaitForWorkAsync(cancellationToken).ConfigureAwait(false);

                if (_debounceWindow > TimeSpan.Zero)
                {
                    await _queue.WaitForQuietAsync(_debounceWindow, cancellationToken).ConfigureAwait(false);
                }

                await RunQueuedWorkAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never a reason to stop listening: a pass that failed had one reason, and the next
                // request - which arrives on its own, constantly - deserves the same attempt. The
                // delay is only so a persistent failure does not become a hot loop.
                _logger.LogError(ex, "Anaglyfin's version-item pass failed; trying again at the next request.");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogDebug("Anaglyfin's version-item worker has stopped.");
    }

    /// <summary>
    /// Runs whatever the burst asked for: one pass over the library, or the items named.
    /// </summary>
    private async Task RunQueuedWorkAsync(CancellationToken cancellationToken)
    {
        if (_queue.TryTakeFullPass())
        {
            await _reconciler.ReconcileLibraryAsync(cancellationToken).ConfigureAwait(false);
        }

        while (_queue.TryTakeItem(out var itemId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _reconciler.ReconcileItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops listening. Caller holds the lock.
    /// </summary>
    private void Unsubscribe()
    {
        if (!_listening)
        {
            return;
        }

        _libraryEvents.ItemAdded -= OnItemChanged;
        _libraryEvents.ItemUpdated -= OnItemChanged;
        _libraryEvents.ItemRemoved -= OnItemRemoved;
        _listening = false;
    }
}
