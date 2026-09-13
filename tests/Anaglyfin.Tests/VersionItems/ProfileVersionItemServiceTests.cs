using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.VersionItems;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// Covers the background service: what it listens to, what it asks for, and what it refuses to do
/// on the caller's thread.
/// </summary>
/// <remarks>
/// The service owns the one loop that writes to the library, so the properties that matter are the
/// ones every other component of this feature depends on: a library event costs a request and not a
/// database write, a burst of events costs one pass, a failed pass does not take the listener down,
/// and shutdown takes the listener with it.
/// </remarks>
public class ProfileVersionItemServiceTests
{
    private static readonly TimeSpan Never = TimeSpan.FromMinutes(5);

    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        var events = new FakeLibraryEventSource();
        var reconciler = new RecordingReconciler();
        var queue = new ProfileVersionReconcileQueue();
        var logger = NullLogger<ProfileVersionItemService>.Instance;

        Assert.Throws<ArgumentNullException>(() => new ProfileVersionItemService(null!, reconciler, queue, logger));
        Assert.Throws<ArgumentNullException>(() => new ProfileVersionItemService(events, null!, queue, logger));
        Assert.Throws<ArgumentNullException>(() => new ProfileVersionItemService(events, reconciler, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new ProfileVersionItemService(events, reconciler, queue, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProfileVersionItemService(events, reconciler, queue, logger, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task StartAsksForAPassOverTheLibrary()
    {
        // A pass over everything is the only honest answer to "the server was not running when that
        // file changed"; it is asked for, not run, because the host has a first request to serve.
        var (service, _, queue) = Create(out _, debounce: Never);

        await service.StartAsync(CancellationToken.None);

        Assert.True(queue.FullPassPending);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ASecondStartIsNotASecondWorker()
    {
        var (service, events, queue) = Create(out _, debounce: Never);

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        // Two workers over one queue would double every pass and halve nothing.
        Assert.Equal(1, queue.RequestCount);
        Assert.Equal(3, events.SubscriberCount);
    }

    [Fact]
    public async Task AVideosChangeIsARequestAndNotAWrite()
    {
        var (service, events, queue) = Create(out var movie, debounce: Never);
        await service.StartAsync(CancellationToken.None);

        // The start-up pass will reconcile this item too, so the request is counted and not queued -
        // until the pass has actually been taken, when a change is news again.
        queue.TryTakeFullPass();

        var before = queue.RequestCount;
        events.RaiseUpdated(movie);

        Assert.Equal(before + 1, queue.RequestCount);
        Assert.Equal(1, queue.PendingItemCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnlyVideosAreAskedAbout()
    {
        var (service, events, queue) = Create(out _, debounce: Never);
        await service.StartAsync(CancellationToken.None);

        var requests = queue.RequestCount;

        events.RaiseAdded(new Audio { Id = Guid.NewGuid(), Name = "A track", Path = "/music/A/Track.mp3" });
        events.RaiseUpdated(new Folder { Id = Guid.NewGuid(), Name = "Movies", Path = "/movies" });
        events.RaiseUpdatedWithNoItem();
        events.RaiseRemoved(new Audio { Id = Guid.NewGuid(), Name = "Another track", Path = "/music/B/Track.mp3" });

        Assert.Equal(requests, queue.RequestCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopStopsListening()
    {
        var (service, events, queue) = Create(out var movie, debounce: Never);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        var requests = queue.RequestCount;
        events.RaiseUpdated(movie);

        Assert.Equal(0, events.SubscriberCount);
        Assert.Equal(requests, queue.RequestCount);
    }

    [Fact]
    public async Task ABurstIsAnsweredOncePerItem()
    {
        var (service, events, _) = Create(out var movie, out var reconciler, debounce: TimeSpan.FromMilliseconds(25));
        var other = new Video { Id = Guid.NewGuid(), Name = "Second", Path = "/movies/B/B.mkv" };

        await service.StartAsync(CancellationToken.None);
        await reconciler.FirstLibraryPass.WaitAsync(TimeSpan.FromSeconds(5));

        // A refresh is not one change: the same items are announced over and over as their images,
        // metadata and parents are written too. One burst, one reconciliation of each.
        events.RaiseUpdated(movie);
        events.RaiseUpdated(movie);
        events.RaiseAdded(other);
        events.RaiseUpdated(other);
        events.RaiseUpdated(movie);

        await WaitUntil(() => reconciler.ItemReconciliations.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.Equal(
            new[] { movie.Id, other.Id }.OrderBy(id => id),
            reconciler.ItemReconciliations.Distinct().OrderBy(id => id));
        Assert.Equal(1, reconciler.LibraryPasses);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFailingPassDoesNotStopTheListener()
    {
        var (service, events, queue) = Create(out var movie, out var reconciler, debounce: TimeSpan.FromMilliseconds(25));
        reconciler.ThrowOnReconcile = new InvalidOperationException("the pass failed (failure under test)");

        await service.StartAsync(CancellationToken.None);
        await reconciler.FirstLibraryPass.WaitAsync(TimeSpan.FromSeconds(5));

        // The next request arrives on its own, constantly, and deserves the same attempt: a listener
        // that gave up after one database error would leave this library with stale versions until a
        // restart.
        await WaitUntil(
            () =>
            {
                events.RaiseUpdated(movie);

                return queue.RequestCount > 1;
            },
            TimeSpan.FromSeconds(5));

        Assert.True(queue.PendingItemCount >= 1);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopReleasesAWorkInTheMiddle()
    {
        var (service, _, _) = Create(out _, out var reconciler, debounce: TimeSpan.FromMilliseconds(25));
        reconciler.HoldPasses(1);

        await service.StartAsync(CancellationToken.None);
        await reconciler.FirstLibraryPass.WaitAsync(TimeSpan.FromSeconds(5));

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // The pass in progress is stopped rather than waited out: a half-created item is a state the
        // next start-up pass reads and repairs, and a server that cannot shut down is worse.
        await service.StopAsync(stopping.Token);

        reconciler.ReleasePasses();
    }

    [Fact]
    public async Task DisposeBeforeStartIsHarmless()
    {
        var (service, events, _) = Create(out _, debounce: Never);

        service.Dispose();

        Assert.Equal(0, events.SubscriberCount);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Builds a service over a fresh queue, with the movie the events will be raised for.
    /// </summary>
    private static (ProfileVersionItemService Service, FakeLibraryEventSource Events, ProfileVersionReconcileQueue Queue) Create(
        out Video movie,
        TimeSpan debounce)
        => Create(out movie, out _, debounce);

    private static (ProfileVersionItemService Service, FakeLibraryEventSource Events, ProfileVersionReconcileQueue Queue) Create(
        out Video movie,
        out RecordingReconciler reconciler,
        TimeSpan debounce)
    {
        movie = new Video { Id = ProfileVersionFixtures.MovieId, Name = "Ready Player One (2018)", Path = ProfileVersionFixtures.MvcPath };
        reconciler = new RecordingReconciler();

        var events = new FakeLibraryEventSource();
        var queue = new ProfileVersionReconcileQueue();
        var service = new ProfileVersionItemService(
            events,
            reconciler,
            queue,
            NullLogger<ProfileVersionItemService>.Instance,
            debounce);

        return (service, events, queue);
    }

    /// <summary>Waits for a condition the worker is responsible for making true.</summary>
    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("the version-item worker did not get there in time");
    }
}
