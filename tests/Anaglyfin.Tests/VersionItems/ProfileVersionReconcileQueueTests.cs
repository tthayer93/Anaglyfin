using System;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.VersionItems;
using Xunit;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// Covers the reconcile queue: what it coalesces, what it refuses to duplicate, and how it wakes
/// whoever is waiting to do the work.
/// </summary>
/// <remarks>
/// A request here is a statement that a state may have changed, not a unit of work, so the property
/// that matters is that a hundred events about one movie folder cost one reconciliation. Every test
/// is therefore about a count: how many times the queue handed the same question out.
/// </remarks>
public class ProfileVersionReconcileQueueTests
{
    private static readonly Guid MovieId = ProfileVersionFixtures.MovieId;

    private static readonly Guid OtherId = Guid.Parse("3b7c9d21-5a4e-4c8f-9e6d-1a2b3c4d5e6f");

    [Fact]
    public void AnItemAskedAboutTwiceIsOneRequest()
    {
        var queue = new ProfileVersionReconcileQueue();

        queue.RequestItem(MovieId);
        queue.RequestItem(MovieId);
        queue.RequestItem(MovieId);

        Assert.Equal(1, queue.PendingItemCount);
        Assert.True(queue.HasWork);

        // Every request still counts as having arrived: that is what a debounce waits out, and a
        // dropped duplicate is not a request nobody made.
        Assert.Equal(3, queue.RequestCount);

        Assert.True(queue.TryTakeItem(out var taken));
        Assert.Equal(MovieId, taken);
        Assert.False(queue.TryTakeItem(out _));
        Assert.False(queue.HasWork);
    }

    [Fact]
    public void AnItemCanBeAskedAboutAgainOnceItHasBeenTaken()
    {
        var queue = new ProfileVersionReconcileQueue();
        queue.RequestItem(MovieId);
        queue.TryTakeItem(out _);

        // An event that arrives while an item is being reconciled is news about that item, and must
        // be run again rather than swallowed by a record of the run already under way.
        queue.RequestItem(MovieId);

        Assert.Equal(1, queue.PendingItemCount);
    }

    [Fact]
    public void AnItemThatNamesNothingIsNotARequest()
    {
        var queue = new ProfileVersionReconcileQueue();

        queue.RequestItem(Guid.Empty);

        Assert.False(queue.HasWork);
        Assert.Equal(0, queue.RequestCount);
    }

    [Fact]
    public void EveryFullPassRequestedIsOnePassToRun()
    {
        var queue = new ProfileVersionReconcileQueue();

        queue.RequestFullPass();
        queue.RequestFullPass();
        queue.RequestFullPass();

        Assert.True(queue.FullPassPending);
        Assert.True(queue.TryTakeFullPass());
        Assert.False(queue.TryTakeFullPass());
        Assert.False(queue.FullPassPending);
    }

    [Fact]
    public void AFullPassTakesTheItemRequestsItMakesRedundant()
    {
        var queue = new ProfileVersionReconcileQueue();
        queue.RequestItem(MovieId);
        queue.RequestItem(OtherId);

        queue.RequestFullPass();

        // A pass over the library reconciles those two items too. Leaving them queued would run the
        // same reconciliation again and report its no-ops as changes.
        Assert.Equal(0, queue.PendingItemCount);
        Assert.True(queue.TryTakeFullPass());
        Assert.False(queue.TryTakeItem(out _));
    }

    [Fact]
    public void AnItemAskedForWhileAPassIsPendingIsNotAskedForTwice()
    {
        var queue = new ProfileVersionReconcileQueue();
        queue.RequestFullPass();

        queue.RequestItem(MovieId);

        Assert.Equal(0, queue.PendingItemCount);
        Assert.Equal(2, queue.RequestCount);

        Assert.True(queue.TryTakeFullPass());

        // After the pass, a fresh request is news again.
        queue.RequestItem(MovieId);

        Assert.Equal(1, queue.PendingItemCount);
    }

    [Fact]
    public async Task APendingRequestWakesTheWaiterAtOnce()
    {
        var queue = new ProfileVersionReconcileQueue();
        queue.RequestItem(MovieId);

        await queue.WaitForWorkAsync(CancellationToken.None);

        Assert.True(queue.WaitForWorkAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task ARequestWakesTheWaiter()
    {
        var queue = new ProfileVersionReconcileQueue();
        var waiting = queue.WaitForWorkAsync(CancellationToken.None);

        Assert.False(waiting.IsCompleted);

        queue.RequestFullPass();

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AWakenedWaiterIsReplacedForTheNextOne()
    {
        var queue = new ProfileVersionReconcileQueue();

        var first = queue.WaitForWorkAsync(CancellationToken.None);
        queue.RequestItem(MovieId);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        queue.TryTakeItem(out _);

        // One signal handed to two waiters would leave the second waiting forever, and the worker
        // with it. The queue that is now empty must be able to wake someone again.
        var second = queue.WaitForWorkAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);

        queue.RequestItem(OtherId);

        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AQuietWindowIsWhatEndsTheDebounce()
    {
        var queue = new ProfileVersionReconcileQueue();
        var window = TimeSpan.FromMilliseconds(200);

        var quiet = queue.WaitForQuietAsync(window, CancellationToken.None);
        Assert.False(quiet.IsCompleted);

        // A refresh that dribbles events in keeps pushing the wait out: one burst, one pass, however
        // the burst was shaped.
        var bursts = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                queue.RequestItem(i % 2 == 0 ? MovieId : OtherId);
                await Task.Delay(20);
            }
        });

        await Task.Delay(300);
        Assert.False(quiet.IsCompleted);

        await bursts;
        await quiet.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(quiet.IsCompleted);
    }

    [Fact]
    public async Task AWaitForQuietEndsWithTheWorker()
    {
        var queue = new ProfileVersionReconcileQueue();
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), stopping.Token));
    }

    [Fact]
    public async Task AWaitForWorkEndsWithTheWorker()
    {
        var queue = new ProfileVersionReconcileQueue();
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.WaitForWorkAsync(stopping.Token));
    }
}
