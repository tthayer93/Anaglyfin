using System;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Detection;
using Anaglyfin.MediaSources;
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
/// same queue (<c>Plugin.SaveConfiguration</c> asks for the full pass). Playback is not among them:
/// the server reports watched state, resume position and played-state on its own user-data event,
/// which this service never subscribes to, and an item written back for no reason at all
/// (<see cref="ItemUpdateType.None"/>) says less than the events below do.
/// </para>
/// <para>
/// <b>What it refuses.</b> An item event is a request about one item, and the cheap half of the
/// eligibility question (<see cref="MvcEligibilityPrefilter.IsCheapCandidate"/>) answers whether that
/// item could carry a version at all before anything is queued: a track, a folder, one of Anaglyfin's
/// own marker items, and the ordinary movie whose name, tags and declared format say nothing about 3D
/// are refused on the spot. The refusal is the whole point of listening to a library at all - a
/// refresh announces every item it touched, most of them several times, and a queue that kept all of
/// them would be reconciling a ten-thousand-item library one movie at a time to change nothing in any
/// of them.
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

    private readonly IMvcSourceDetector _detector;

    private readonly IProfileVersionReconciler _reconciler;

    private readonly ProfileVersionReconcileQueue _queue;

    private readonly ILogger<ProfileVersionItemService> _logger;

    private readonly TimeSpan _debounceWindow;

    private readonly object _sync = new();

    private CancellationTokenSource? _stopping;

    private Task? _worker;

    private bool _listening;

    private int _ignoredEvents;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileVersionItemService"/> class.
    /// </summary>
    /// <param name="libraryEvents">The library events to listen to.</param>
    /// <param name="detector">The rules that decide whether an item could carry versions at all.</param>
    /// <param name="reconciler">The work to run.</param>
    /// <param name="queue">The requests, already made and not yet acted on.</param>
    /// <param name="logger">Logger for the passes and their failures.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public ProfileVersionItemService(
        ILibraryEventSource libraryEvents,
        IMvcSourceDetector detector,
        IProfileVersionReconciler reconciler,
        ProfileVersionReconcileQueue queue,
        ILogger<ProfileVersionItemService> logger)
        : this(libraryEvents, detector, reconciler, queue, logger, DefaultDebounceWindow)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileVersionItemService"/> class with a chosen
    /// debounce window.
    /// </summary>
    /// <param name="libraryEvents">The library events to listen to.</param>
    /// <param name="detector">The rules that decide whether an item could carry versions at all.</param>
    /// <param name="reconciler">The work to run.</param>
    /// <param name="queue">The requests, already made and not yet acted on.</param>
    /// <param name="logger">Logger for the passes and their failures.</param>
    /// <param name="debounceWindow">How long a burst is waited out before it is reconciled.</param>
    /// <exception cref="ArgumentNullException">
    /// Any reference argument is <c>null</c>, or <paramref name="debounceWindow"/> is negative.
    /// </exception>
    public ProfileVersionItemService(
        ILibraryEventSource libraryEvents,
        IMvcSourceDetector detector,
        IProfileVersionReconciler reconciler,
        ProfileVersionReconcileQueue queue,
        ILogger<ProfileVersionItemService> logger,
        TimeSpan debounceWindow)
    {
        _libraryEvents = libraryEvents ?? throw new ArgumentNullException(nameof(libraryEvents));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
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
    /// The library noticed an item, and it is only worth asking about if it could carry a version.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="args">The item that changed.</param>
    /// <remarks>
    /// <para>
    /// Cheap by contract: runs on the scanner's thread for every item of every refresh, so it reads
    /// nothing the item does not already carry and writes nothing anywhere. What it decides is the
    /// cheap half of the eligibility question - the same half a full pass asks, from the same
    /// detector, so an item cannot be refused by one and reconciled by the other - and everything it
    /// cannot decide cheaply is asked about: an item that names other versions of itself may hold its
    /// MVC file in one of them, and that is the case the whole feature exists for.
    /// </para>
    /// <para>
    /// <b>What arrives here at all.</b> Only the three item events this service subscribes to, which
    /// is why no playback state is ever reconciled: watched flags, resume positions and played-state
    /// travel on the server's user-data event, and a write-back that carries
    /// <see cref="ItemUpdateType.None"/> carries no answer about an item's metadata either - the two
    /// shapes that could otherwise turn every pause in the house into a database pass.
    /// </para>
    /// <para>
    /// What it is asked, if it is asked at all, is one item's id: the walk from an item to the item
    /// that owns its versions is the reconciler's job, and a burst is coalesced and debounced there.
    /// </para>
    /// </remarks>
    public void OnItemChanged(object? sender, ItemChangeEventArgs? args)
    {
        // An addition carries no update reason at all (the server leaves the flag unset), so only the
        // bare "nothing changed" answer is refused: the shapes that cannot put an MVC file anywhere -
        // import, image, scrape, hand edit - are the ones this listener was built for, and an addition
        // is the loudest of them.
        if (args is { UpdateReason: ItemUpdateType.None })
        {
            Ignored();
            return;
        }

        if (args?.Item is not Video video)
        {
            // Nothing that is not a video has a 3D question, and an event with no item in it is not a
            // report of anything.
            Ignored();
            return;
        }

        if (!MvcEligibilityPrefilter.IsCheapCandidate(video, _detector))
        {
            Ignored();
            return;
        }

        _queue.RequestItem(video.Id);
    }

    /// <summary>
    /// Counts an event that will not be reconciled, so that a burst that changed nothing is at least
    /// visible once per burst instead of once per item.
    /// </summary>
    private void Ignored() => Interlocked.Increment(ref _ignoredEvents);

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
        // One line per burst, whatever the library raised: the count is what a listener on a large
        // library has to say about itself, and saying it per item would be the noise this listener
        // exists to avoid. No item is named - the number is the whole story, and it is the number that
        // tells a reader whether the prefilter is doing its job.
        var ignored = Interlocked.Exchange(ref _ignoredEvents, 0);
        if (ignored > 0)
        {
            _logger.LogDebug(
                "Anaglyfin ignored {Count} library item events that could not carry a version item.",
                ignored);
        }

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
