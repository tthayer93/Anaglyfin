using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
using Anaglyfin.VersionItems;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// Covers the background service: what it listens to, what it asks for, and what it refuses to do
/// on the caller's thread.
/// </summary>
/// <remarks>
/// <para>
/// The service owns the one loop that writes to the library, so the properties that matter are the
/// ones every other component of this feature depends on: a library event costs a request and not a
/// database write, a burst of events costs one pass, a failed pass does not take the listener down,
/// and shutdown takes the listener with it.
/// </para>
/// <para>
/// The second group of properties is what an event is refused for. A library refresh announces every
/// item it touched, so a listener that asked about all of them would spend a library's worth of
/// database reads on movies that have no 3D question - which is why the cheap eligibility question is
/// answered here, on the scanner's thread, from the item's own fields, before a request exists. What
/// survives is one item id; what does not is counted, once per burst, and never written anywhere.
/// </para>
/// </remarks>
public class ProfileVersionItemServiceTests
{
    private static readonly TimeSpan Never = TimeSpan.FromMinutes(5);

    /// <summary>An item whose path is one of Anaglyfin's own version markers.</summary>
    private static readonly string MarkerPath = ProfileMarker.MarkerPrefix + "sbs_full?source=%2Fmovies%2Fx.mkv";

    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        var events = new FakeLibraryEventSource();
        var detector = new MvcSourceDetector();
        var reconciler = new RecordingReconciler();
        var queue = new ProfileVersionReconcileQueue();
        var logger = NullLogger<ProfileVersionItemService>.Instance;

        Assert.Throws<ArgumentNullException>(() => new ProfileVersionItemService(null!, detector, reconciler, queue, logger));
        Assert.Throws<ArgumentNullException>(() => new ProfileVersionItemService(events, null!, reconciler, queue, logger));
        Assert.Throws<ArgumentNullException>(() => new ProfileVersionItemService(events, detector, null!, queue, logger));
        Assert.Throws<ArgumentNullException>(() => new ProfileVersionItemService(events, detector, reconciler, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new ProfileVersionItemService(events, detector, reconciler, queue, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProfileVersionItemService(events, detector, reconciler, queue, logger, TimeSpan.FromSeconds(-1)));
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
        var other = new Video
        {
            Id = Guid.NewGuid(),
            Name = "Second",
            Path = "/movies/B/B (2011) - 3D mvc.mkv",
            VideoType = VideoType.VideoFile
        };

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

    // --- what an event is refused for -------------------------------------------

    [Fact]
    public async Task AVideoWithNo3DSignalIsNotARequest()
    {
        var (service, events, queue) = Create(out _, debounce: Never);
        await service.StartAsync(CancellationToken.None);
        queue.TryTakeFullPass();

        // The movie a library is made of: a file, a name and a folder that say nothing about 3D, and
        // nothing grouped with them that might. Its refresh event is answered with no request at all,
        // because the reconciliation it would ask for can only read its sources and decide nothing.
        var ordinary = new Video
        {
            Id = Guid.NewGuid(),
            Name = "A Ordinary Movie (2015)",
            Path = "/movies/A Ordinary Movie (2015)/A Ordinary Movie (2015).mkv",
            VideoType = VideoType.VideoFile
        };

        var requests = queue.RequestCount;
        events.RaiseAdded(ordinary);
        events.RaiseUpdated(ordinary);

        Assert.Equal(requests, queue.RequestCount);
        Assert.Equal(0, queue.PendingItemCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AVersionItemOfAnaglyfinsOwnIsNotARequest()
    {
        var (service, events, queue) = Create(out _, debounce: Never);
        await service.StartAsync(CancellationToken.None);
        queue.TryTakeFullPass();

        // The write that created a version announces that version, and answering the announcement is
        // the loop this listener exists to break. Its path is a marker, which is the whole answer.
        var version = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            Path = MarkerPath
        };

        var requests = queue.RequestCount;
        events.RaiseAdded(version);
        events.RaiseUpdated(version);

        Assert.Equal(requests, queue.RequestCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(ItemUpdateType.MetadataEdit)]
    [InlineData(ItemUpdateType.MetadataDownload)]
    [InlineData(ItemUpdateType.MetadataImport)]
    [InlineData(ItemUpdateType.ImageUpdate)]
    public async Task EveryMetadataShapeOnAMovieThatCouldCarryVersionsIsARequest(ItemUpdateType reason)
    {
        var (service, events, queue) = Create(out var movie, debounce: Never);
        await service.StartAsync(CancellationToken.None);
        queue.TryTakeFullPass();

        // A version item copies the metadata and the artwork of the file it converts, so every shape
        // of a metadata write is a reason to look again - and a movie whose own fields say MVC is
        // asked about whatever else the write carried. Deciding which of them actually moved a field
        // is the reconciliation's job, not the listener's guess.
        var requests = queue.RequestCount;
        events.RaiseUpdated(movie, reason);

        Assert.Equal(requests + 1, queue.RequestCount);
        Assert.Equal(1, queue.PendingItemCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnAdditionOfAMovieThatCouldCarryVersionsIsARequest()
    {
        var (service, events, queue) = Create(out var movie, debounce: Never);
        await service.StartAsync(CancellationToken.None);
        queue.TryTakeFullPass();

        // A file that arrived is the case that matters most: the MVC rip dropped into a folder while
        // the server was up. An addition carries no update reason at all, and it is asked about.
        var requests = queue.RequestCount;
        events.RaiseAdded(movie);

        Assert.Equal(requests + 1, queue.RequestCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AMovieKnownOnlyByItsTagIsARequest()
    {
        var (service, events, queue) = Create(out _, debounce: Never);
        await service.StartAsync(CancellationToken.None);
        queue.TryTakeFullPass();

        // Nothing on this item's name or path says MVC; a tag does, written by an NFO file or by a
        // person in the dashboard. The same rules read it here as read it in a pass.
        var tagged = ProfileVersionFixtures.CreateTaggedMvcMovie(Guid.NewGuid());

        var requests = queue.RequestCount;
        events.RaiseUpdated(tagged);

        Assert.Equal(requests + 1, queue.RequestCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheStackRootsEventIsARequestEvenThoughItsOwnFileIsPlain()
    {
        var (service, events, queue) = Create(out _, debounce: Never);
        await service.StartAsync(CancellationToken.None);
        queue.TryTakeFullPass();

        // The case the feature was built on: a movie browsed to over its 1080p file, with the MVC rip
        // filed as one of its versions. Every field of this item describes the plain file, and the
        // versions belong to them anyway - so the listener that refused it for what its own fields
        // said would lose the versions of its MVC sibling.
        var stacked = ProfileVersionFixtures.CreateStackedMvcMovie();

        var requests = queue.RequestCount;
        events.RaiseUpdated(stacked);

        Assert.Equal(requests + 1, queue.RequestCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AWriteBackThatCarriesNoAnswerIsNotARequest()
    {
        var (service, events, queue) = Create(out var movie, debounce: Never);
        await service.StartAsync(CancellationToken.None);
        queue.TryTakeFullPass();

        // The server writes an item back with nothing to report when a refresh found no metadata to
        // change, and watched state and resume position travel on the user-data event this service
        // never subscribes to. A pause in the house is not a reason to read a library.
        var requests = queue.RequestCount;
        events.RaiseUpdated(movie, ItemUpdateType.None);

        Assert.Equal(requests, queue.RequestCount);
        Assert.Equal(0, queue.PendingItemCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARefusedEventLeavesNothingQueuedForTheWorkerToRun()
    {
        var (service, events, _) = Create(out _, out var reconciler, debounce: TimeSpan.FromMilliseconds(25));

        await service.StartAsync(CancellationToken.None);
        await reconciler.FirstLibraryPass.WaitAsync(TimeSpan.FromSeconds(5));

        var ordinary = new Video { Id = Guid.NewGuid(), Name = "A Ordinary Movie", Path = "/movies/A/A.mkv", VideoType = VideoType.VideoFile };

        // Not enqueued is the property: an event that was dropped on the scanner's thread cannot be
        // reconciled later either, so the worker never hears about it.
        events.RaiseUpdated(ordinary);
        events.RaiseUpdated(ordinary);
        events.RaiseAdded(new Video { Id = Guid.NewGuid(), Name = "A track", Path = "/music/A/Track.mp3" });

        Assert.Empty(reconciler.ItemReconciliations);

        await Task.Delay(150);
        Assert.Empty(reconciler.ItemReconciliations);
        Assert.Equal(1, reconciler.LibraryPasses);

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Builds a service over a fresh queue, with the movie the events will be raised for.
    /// </summary>
    /// <remarks>
    /// The movie the events name is the MVC one, and the service is given the real detector: which
    /// items are worth a pass is the property under test in half of these cases, and a scripted answer
    /// would only prove the script.
    /// </remarks>
    private static (ProfileVersionItemService Service, FakeLibraryEventSource Events, ProfileVersionReconcileQueue Queue) Create(
        out Video movie,
        TimeSpan debounce)
        => Create(out movie, out _, debounce);

    private static (ProfileVersionItemService Service, FakeLibraryEventSource Events, ProfileVersionReconcileQueue Queue) Create(
        out Video movie,
        out RecordingReconciler reconciler,
        TimeSpan debounce)
    {
        movie = new Video
        {
            Id = ProfileVersionFixtures.MovieId,
            Name = "Ready Player One (2018)",
            Path = ProfileVersionFixtures.MvcPath,

            // A library item over a plain file: the type a resolver gives such a file, and the one
            // answer that lets the cheap question be about 3D rather than about whether the item has
            // a file at all.
            VideoType = VideoType.VideoFile
        };

        reconciler = new RecordingReconciler();

        var events = new FakeLibraryEventSource();
        var queue = new ProfileVersionReconcileQueue();
        var service = new ProfileVersionItemService(
            events,
            new MvcSourceDetector(),
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
