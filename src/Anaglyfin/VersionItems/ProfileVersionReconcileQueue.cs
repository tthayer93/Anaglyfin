using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Anaglyfin.VersionItems;

/// <summary>
/// The reconcile requests Anaglyfin has been asked for and has not yet acted on.
/// </summary>
/// <remarks>
/// <para>
/// A coalescing queue rather than a work queue, because that is what the requests actually mean.
/// "This item may need different versions" is a statement about a <em>state</em>, not a unit of
/// work: a library refresh raises hundreds of events for one movie folder, and processing them one
/// by one would run the same reconciliation hundreds of times over the same dozen files. Duplicates
/// are therefore dropped by item id, and every full-pass request collapses into the single pending
/// full pass - which subsumes the item requests it arrived beside, because a pass over the library
/// reconciles those items too.
/// </para>
/// <para>
/// <b>Why it never blocks a caller.</b> Both entry points are non-blocking and never throw for a
/// request the queue cannot hold: the callers are library events and the settings-save path, and a
/// version that is late is a version nobody noticed, while a version that blocks a scanner is a
/// stalled library.
/// </para>
/// <para>
/// Thread safe: every state change and every read of the state happens under one lock, and the
/// waiting primitives hand out a task rather than holding that lock while awaiting.
/// </para>
/// </remarks>
public sealed class ProfileVersionReconcileQueue : IProfileVersionReconcileTrigger
{
    private readonly object _sync = new();

    private readonly Queue<Guid> _pendingItems = new();

    private readonly HashSet<Guid> _queuedItems = new();

    private TaskCompletionSource<bool> _workSignal = NewSignal();

    private long _requestCount;

    private bool _fullPassPending;

    /// <summary>
    /// Gets the number of item requests waiting to be taken.
    /// </summary>
    public int PendingItemCount
    {
        get
        {
            lock (_sync)
            {
                return _pendingItems.Count;
            }
        }
    }

    /// <summary>
    /// Gets whether a full pass over the library is waiting to be taken.
    /// </summary>
    public bool FullPassPending
    {
        get
        {
            lock (_sync)
            {
                return _fullPassPending;
            }
        }
    }

    /// <summary>
    /// Gets whether anything is waiting: one pass, or at least one item.
    /// </summary>
    public bool HasWork
    {
        get
        {
            lock (_sync)
            {
                return _fullPassPending || _pendingItems.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets how many requests have been accepted, coalesced ones included.
    /// </summary>
    /// <remarks>
    /// Only ever useful to a caller that wants to know whether the queue went quiet: a dropped
    /// duplicate is still a request that arrived.
    /// </remarks>
    public long RequestCount
    {
        get
        {
            lock (_sync)
            {
                return _requestCount;
            }
        }
    }

    /// <inheritdoc />
    public void RequestItem(Guid itemId)
    {
        if (itemId == Guid.Empty)
        {
            return;
        }

        lock (_sync)
        {
            _requestCount++;

            // A pending full pass will reconcile this item too; queueing it again would run the
            // same reconciliation twice and report the second one as a change it made.
            if (!_fullPassPending && _queuedItems.Add(itemId))
            {
                _pendingItems.Enqueue(itemId);
            }

            Signal();
        }
    }

    /// <inheritdoc />
    public void RequestFullPass()
    {
        lock (_sync)
        {
            _requestCount++;

            if (!_fullPassPending)
            {
                _fullPassPending = true;

                // The pass covers every item, so the item requests it renders redundant are
                // dropped rather than re-run the moment it finishes.
                _pendingItems.Clear();
                _queuedItems.Clear();
            }

            Signal();
        }
    }

    /// <summary>
    /// Takes the pending full pass, if one is waiting, and clears the request.
    /// </summary>
    /// <returns><c>true</c> when a pass was waiting and is now the caller's to run.</returns>
    public bool TryTakeFullPass()
    {
        lock (_sync)
        {
            if (!_fullPassPending)
            {
                return false;
            }

            _fullPassPending = false;
            return true;
        }
    }

    /// <summary>
    /// Takes one pending item request.
    /// </summary>
    /// <param name="itemId">The item to reconcile, when one was waiting.</param>
    /// <returns><c>true</c> when an item was waiting.</returns>
    /// <remarks>
    /// The id stops being deduplicated the moment it is taken: an event that arrives while that
    /// item is being reconciled is a new request about it, and must be run again rather than
    /// swallowed by a record of a reconciliation already under way.
    /// </remarks>
    public bool TryTakeItem(out Guid itemId)
    {
        lock (_sync)
        {
            if (_pendingItems.Count == 0)
            {
                itemId = Guid.Empty;
                return false;
            }

            itemId = _pendingItems.Dequeue();
            _queuedItems.Remove(itemId);
            return true;
        }
    }

    /// <summary>
    /// Waits until a request has been accepted.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// A task completed when there is work, or already completed when the queue holds work right
    /// now. It never completes on its own without a request: an idle queue is an idle queue.
    /// </returns>
    public Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_fullPassPending || _pendingItems.Count > 0)
            {
                return Task.CompletedTask;
            }

            return _workSignal.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Waits until no request has been accepted for one whole window.
    /// </summary>
    /// <param name="quietWindow">How long the queue must hold still before the wait completes.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task completed once the burst that woke it is over.</returns>
    /// <remarks>
    /// This is the debounce, and it is a quiet period rather than a fixed delay: a folder refresh
    /// that raises its events in one go costs one wait, and one that dribbles them in over ten
    /// seconds costs one wait too - then one reconciliation of everything they were about. Waiting
    /// on a request counter instead of a timestamp keeps the comparison exact: nothing about the
    /// server's clock enters into it.
    /// </remarks>
    public async Task WaitForQuietAsync(TimeSpan quietWindow, CancellationToken cancellationToken)
    {
        while (true)
        {
            long observed;
            lock (_sync)
            {
                observed = _requestCount;
            }

            await Task.Delay(quietWindow, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                if (_requestCount == observed)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Wakes a waiter and hands the next one a fresh signal.
    /// </summary>
    /// <remarks>
    /// Called under the lock, and never awaiting: swapping the source before completing the old
    /// one is what keeps two waiters from both being handed a task that will never complete.
    /// </remarks>
    private void Signal()
    {
        var signal = _workSignal;
        _workSignal = NewSignal();
        signal.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
