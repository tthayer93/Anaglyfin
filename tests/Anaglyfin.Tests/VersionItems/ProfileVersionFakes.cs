using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.Detection;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.VersionItems;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// The library a profile version-item pass runs over, held in memory.
/// </summary>
/// <remarks>
/// <para>
/// The manager's contract is thirteen reads and writes over <see cref="IProfileVersionItemStore"/>,
/// so a fake of that interface is a whole library for the purposes of a reconciliation test: items,
/// the links between them, and the streams, images and credits they report. Nothing here mimics
/// server behaviour beyond those calls - it is the state a reconcile reads and what it leaves behind,
/// which is exactly what these tests need to see, including the state a real server can be in and a
/// scan would not produce (an item that exists but is no longer linked).
/// </para>
/// <para>
/// It records the writes it received rather than only its own state, because "the second pass
/// changed nothing" is the property most of these tests are about, and a state diff alone cannot
/// tell a pass that skipped an item from one that rewrote it identically.
/// </para>
/// </remarks>
public sealed class FakeProfileVersionItemStore : IProfileVersionItemStore
{
    private readonly Dictionary<Guid, BaseItem> _items = new();

    private readonly Dictionary<Guid, List<Guid>> _linkedVersions = new();

    private readonly Dictionary<Guid, List<MediaStream>> _streams = new();

    private readonly Dictionary<Guid, List<PersonInfo>> _people = new();

    private readonly List<Video> _versionRoots = new();

    private readonly ManualResetEventSlim _creationGate = new(initialState: true);

    /// <summary>Gets the items a full pass is told to consider.</summary>
    public IList<Video> VersionRoots => _versionRoots;

    /// <summary>Gets every item created, with the folder it was filed under.</summary>
    public List<(BaseItem Item, Folder? Parent)> Created { get; } = new();

    /// <summary>Gets every item written back.</summary>
    public List<BaseItem> Updated { get; } = new();

    /// <summary>Gets every item removed.</summary>
    public List<BaseItem> Deleted { get; } = new();

    /// <summary>Gets every link asked for, in call order, duplicates included.</summary>
    public List<(Guid PrimaryId, Guid VersionId)> Linked { get; } = new();

    /// <summary>Gets every stream save, in call order.</summary>
    public List<Guid> StreamsSaved { get; } = new();

    /// <summary>Gets every credit save, in call order.</summary>
    public List<Guid> PeopleSaved { get; } = new();

    /// <summary>
    /// Gets every image save: the item written and the files its rows name, in call order.
    /// </summary>
    public List<(Guid ItemId, IReadOnlyList<string> Paths)> ImagesSaved { get; } = new();

    /// <summary>Gets how many times the full-library list was read.</summary>
    public int RootsReadCount { get; private set; }

    /// <summary>Makes the full-library list throw, once per read.</summary>
    public bool ThrowOnListRoots { get; set; }

    /// <summary>Makes the creation of a matching item throw.</summary>
    public Func<BaseItem, bool>? ThrowOnCreate { get; set; }

    /// <summary>Makes the write-back of a matching item throw.</summary>
    public Func<BaseItem, bool>? ThrowOnUpdate { get; set; }

    /// <summary>Makes the linked-version read of a matching primary throw.</summary>
    public Func<Video, bool>? ThrowOnLinkedVersions { get; set; }

    /// <summary>
    /// Gets the ids whose child rows and list-valued fields (the image list, the credits, and the
    /// genre/tag/studio/filming-location lists) are answered in a rotated order on every read. A
    /// database hands rows keyed by an item back in whatever order its query happens to produce and
    /// owes nobody the order they were written in; a comparison keyed on read order mistakes one
    /// rotation for every row having moved. A test that wants to be that database puts the item's id
    /// in this list.
    /// </summary>
    public HashSet<Guid> RotateChildrenFor { get; } = new();

    /// <summary>
    /// Gets the ids whose sort name the server re-derives from the item's own Name on every read, the
    /// way the item model's lazy sort-name getter does once anything has dropped its cached value.
    /// Naming an item or setting its forced sort name drops that cache, and reading a stored row back
    /// names the item; a row the update path carried no forced sort name on answers none. So a real
    /// read hands back a sort name derived from the item's Name - never the <c>SortName</c> a caller
    /// wrote last pass - which for a version (whose Name is its profile label, not the movie title) is
    /// a value that can never equal the one copied off the source. A test that wants to be that server
    /// for one item puts its id in this list.
    /// </summary>
    public HashSet<Guid> ReDeriveSortNameFromNameFor { get; } = new();

    /// <summary>
    /// Gets or sets the callback a test runs the moment a creation is attempted, before the creation
    /// itself is held or recorded. Together with <see cref="HoldCreations"/> this is how a test holds
    /// a reconciliation inside the library it is about to write.
    /// </summary>
    public Action<BaseItem>? OnCreateAttempt { get; set; }

    /// <summary>Gets how many creations have been attempted, held or completed.</summary>
    public int CreateAttempts { get; private set; }

    /// <summary>Stops every creation where it stands, holding its caller with it.</summary>
    public void HoldCreations() => _creationGate.Reset();

    /// <summary>Frees every held creation.</summary>
    public void ReleaseCreations() => _creationGate.Set();

    /// <inheritdoc />
    public BaseItem? FindItem(Guid itemId)
    {
        if (!_items.TryGetValue(itemId, out var item))
        {
            return null;
        }

        // Rows and list values keyed by an item come back in the order the query produced, not the
        // order they were written; the sort name comes back derived from the item's Name, not the
        // value a caller wrote. A manager that reads a difference out of either is rewriting an item
        // to what it already is, on every pass, forever - which is the whole of what these two
        // switches let a test be a real server about.
        if (RotateChildrenFor.Contains(itemId))
        {
            // Read order is the repository's business, not the writer's: hand the image rows, and the
            // genre/tag/studio/filming-location lists, back rotated by one from however they stand, so
            // a manager that compares any of them index by index against the order it wrote them sees
            // every value having moved - and one that compares them by what they name or hold sees the
            // same set it wrote. (The credits are rotated the same way in <see cref="GetPeople"/>.)
            var images = item.ImageInfos;
            if (images is { Length: > 1 })
            {
                item.ImageInfos = RotatedByOne(images);
            }

            if (item.Genres is { Length: > 1 })
            {
                item.Genres = RotatedByOne(item.Genres);
            }

            if (item.Tags is { Length: > 1 })
            {
                item.Tags = RotatedByOne(item.Tags);
            }

            if (item.Studios is { Length: > 1 })
            {
                item.Studios = RotatedByOne(item.Studios);
            }

            if (item.ProductionLocations is { Length: > 1 })
            {
                item.ProductionLocations = RotatedByOne(item.ProductionLocations);
            }
        }

        if (ReDeriveSortNameFromNameFor.Contains(itemId))
        {
            // The item model answers SortName lazily and drops the cached value the instant the item is
            // named or its forced sort name set; reading a stored row back names the item, and the row
            // carries no forced sort name the update path did not persist. So the sort name the next read
            // answers with is derived from this item's Name - never the SortName written last pass, and
            // for a version (named for its profile label) never the source's title-derived pair either.
            // Dropping the cache the way the load does is the two assignments below.
            item.ForcedSortName = null;
            var name = item.Name;
            item.Name = name;
        }

        return item;
    }

    private static T[] RotatedByOne<T>(T[] values)
    {
        var rotated = new T[values.Length];
        Array.Copy(values, 1, rotated, 0, values.Length - 1);
        rotated[values.Length - 1] = values[0];

        return rotated;
    }

    /// <inheritdoc />
    public IReadOnlyList<Video> GetVersionRootCandidates()
    {
        RootsReadCount++;

        if (ThrowOnListRoots)
        {
            throw new InvalidOperationException("the library could not be listed (failure under test)");
        }

        return _versionRoots;
    }

    /// <inheritdoc />
    public IReadOnlyList<Video> GetLinkedAlternateVersions(Video primary)
    {
        if (ThrowOnLinkedVersions?.Invoke(primary) == true)
        {
            throw new InvalidOperationException("the linked versions could not be read (failure under test)");
        }

        return LinksOf(primary.Id).Select(FindItem).OfType<Video>().ToList();
    }

    /// <inheritdoc />
    public Folder? GetFolder(BaseItem item)
        => item.ParentId == Guid.Empty ? null : FindItem(item.ParentId) as Folder;

    /// <inheritdoc />
    public void CreateItem(BaseItem item, Folder? parent)
    {
        CreateAttempts++;
        OnCreateAttempt?.Invoke(item);

        // Bounded, because a fake that can block forever turns a broken test into a hung job.
        _creationGate.Wait(TimeSpan.FromSeconds(20), CancellationToken.None);

        if (ThrowOnCreate?.Invoke(item) == true)
        {
            throw new InvalidOperationException("the item could not be created (failure under test)");
        }

        Created.Add((item, parent));
        _items[item.Id] = item;
    }

    /// <inheritdoc />
    public Task UpdateItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (ThrowOnUpdate?.Invoke(item) == true)
        {
            throw new InvalidOperationException("the item could not be written back (failure under test)");
        }

        Updated.Add(item);
        _items[item.Id] = item;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void DeleteItem(BaseItem item)
    {
        // What the server does with it: the row, its streams, its credits, its images and every link
        // naming it.
        Deleted.Add(item);
        _items.Remove(item.Id);
        _streams.Remove(item.Id);
        _people.Remove(item.Id);
        _linkedVersions.Remove(item.Id);

        foreach (var links in _linkedVersions.Values)
        {
            links.Remove(item.Id);
        }
    }

    /// <inheritdoc />
    public void LinkAlternateVersion(Guid primaryId, Guid versionItemId)
    {
        Linked.Add((primaryId, versionItemId));

        if (!_linkedVersions.TryGetValue(primaryId, out var links))
        {
            links = new List<Guid>();
            _linkedVersions[primaryId] = links;
        }

        if (!links.Contains(versionItemId))
        {
            links.Add(versionItemId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        => _streams.TryGetValue(itemId, out var streams) ? streams : Array.Empty<MediaStream>();

    /// <inheritdoc />
    public void SaveMediaStreams(Guid itemId, IReadOnlyList<MediaStream> streams, CancellationToken cancellationToken)
    {
        StreamsSaved.Add(itemId);
        _streams[itemId] = streams.ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<PersonInfo> GetPeople(Guid itemId)
    {
        if (!_people.TryGetValue(itemId, out var people))
        {
            return Array.Empty<PersonInfo>();
        }

        if (RotateChildrenFor.Contains(itemId) && people.Count > 1)
        {
            // The same answer as the image rows above: the rows of one item are sorted by the
            // query, not by the writer, and the copy handed back is rotated to say so.
            var rotated = new List<PersonInfo>(people.Count);
            rotated.AddRange(people.Skip(1));
            rotated.Add(people[0]);

            return rotated;
        }

        return people;
    }

    /// <inheritdoc />
    public void SavePeople(Guid itemId, IReadOnlyList<PersonInfo> people)
    {
        PeopleSaved.Add(itemId);
        _people[itemId] = people.ToList();
    }

    /// <inheritdoc />
    public Task SaveImagesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        // What the rows would name: the item they are keyed by and the files they point at. A test
        // about artwork is a test about which files a version's image rows name, so that is what this
        // keeps - and the fact that nothing had to exist on disk for it to be written.
        ImagesSaved.Add((item.Id, item.ImageInfos.Select(image => image.Path).ToArray()));

        return Task.CompletedTask;
    }

    /// <summary>Puts an item in the library without linking or creating it.</summary>
    /// <param name="item">The item to file.</param>
    public void AddItem(BaseItem item)
        => _items[item.Id] = item;

    /// <summary>Puts an item in the library and lists it as a version root.</summary>
    /// <typeparam name="T">The kind of item, so a test can keep the type it built.</typeparam>
    /// <param name="video">The item to file and list.</param>
    /// <returns>The same item it was handed.</returns>
    public T AddVersionRoot<T>(T video)
        where T : Video
    {
        AddItem(video);
        _versionRoots.Add(video);

        return video;
    }

    /// <summary>Writes the streams an item reports, as a prior pass or a probe would have.</summary>
    /// <param name="itemId">The item the streams belong to.</param>
    /// <param name="streams">The streams to store.</param>
    public void AddStreams(Guid itemId, IEnumerable<MediaStream> streams)
        => _streams[itemId] = streams.ToList();

    /// <summary>Writes the credits an item carries, as a scrape or a merge would have left them.</summary>
    /// <param name="itemId">The item the credits belong to.</param>
    /// <param name="people">The credits to store.</param>
    public void AddPeople(Guid itemId, IEnumerable<PersonInfo> people)
        => _people[itemId] = people.ToList();

    /// <summary>Drops a link without removing the item it named.</summary>
    /// <param name="primaryId">The primary the version is linked to.</param>
    /// <param name="versionItemId">The version to unlink.</param>
    public void Unlink(Guid primaryId, Guid versionItemId)
    {
        if (_linkedVersions.TryGetValue(primaryId, out var links))
        {
            links.Remove(versionItemId);
        }
    }

    /// <summary>Removes an item from the library, leaving any link naming it behind.</summary>
    /// <param name="itemId">The item to remove.</param>
    public void RemoveItem(Guid itemId)
    {
        _items.Remove(itemId);
        _streams.Remove(itemId);
        _people.Remove(itemId);
    }

    /// <summary>Gets the version ids linked to one primary.</summary>
    /// <param name="primaryId">The item to ask about.</param>
    /// <returns>The linked version ids.</returns>
    public IReadOnlyList<Guid> LinksOf(Guid primaryId)
        => _linkedVersions.TryGetValue(primaryId, out var links) ? links : Array.Empty<Guid>();

    /// <summary>Whether anything links one primary to one version.</summary>
    /// <param name="primaryId">The primary to ask about.</param>
    /// <param name="versionItemId">The version to look for.</param>
    /// <returns><c>true</c> when the pair is linked.</returns>
    public bool IsLinked(Guid primaryId, Guid versionItemId)
        => LinksOf(primaryId).Contains(versionItemId);
}

/// <summary>
/// A <see cref="Video"/> whose static media sources are scripted, because a unit test has no
/// database for the real item to read them from.
/// </summary>
public sealed class ScriptedVideo : Video
{
    /// <summary>Gets or sets the sources the item reports for its own files.</summary>
    public IReadOnlyList<MediaSourceInfo> StaticSources { get; set; } = Array.Empty<MediaSourceInfo>();

    /// <inheritdoc />
    public override IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution)
        => StaticSources;
}

/// <summary>
/// An <see cref="IMvcSourceDetector"/> that answers by the file it was asked about, so a test can
/// give one item an MVC file and a plain one and watch which of them the versions are built from.
/// </summary>
public sealed class ScriptedMvcDetector : IMvcSourceDetector
{
    private readonly HashSet<string> _mvcPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initialises the detector with the files it should accept.</summary>
    /// <param name="mvcPaths">The paths that are 3D MVC; every other file is not.</param>
    public ScriptedMvcDetector(params string[] mvcPaths)
    {
        foreach (var path in mvcPaths)
        {
            _mvcPaths.Add(path);
        }
    }

    /// <summary>Gets every candidate the detector was asked about, in order.</summary>
    public List<MvcSourceCandidate> Candidates { get; } = new();

    /// <inheritdoc />
    public MvcSourceEligibility Detect(MvcSourceCandidate candidate)
    {
        Candidates.Add(candidate);

        return candidate.Path is not null && _mvcPaths.Contains(candidate.Path)
            ? MvcSourceEligibility.Eligible(MvcEligibilityReason.ItemMetadataDeclaresMvc, MvcDetectionConfidence.High)
            : MvcSourceEligibility.NotEligible(MvcEligibilityReason.NoMvcSignal);
    }
}

/// <summary>
/// A catalog with the real profiles to look up and none to enable - the state a test needs when it
/// wants to watch a manager remove versions rather than add them, and one the settings cannot
/// express (an empty <c>EnabledProfileIds</c> means "the defaults", not "none").
/// </summary>
public sealed class NoEnabledProfilesCatalog : IProfileCatalog
{
    private readonly ProfileCatalog _inner = new();

    /// <inheritdoc />
    public IReadOnlyList<StereoProfile> Profiles => _inner.Profiles;

    /// <inheritdoc />
    public IReadOnlyList<string> AllProfileIds => _inner.AllProfileIds;

    /// <inheritdoc />
    public bool IsKnownProfileId(string? profileId) => _inner.IsKnownProfileId(profileId);

    /// <inheritdoc />
    public bool TryGetProfile(string? profileId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StereoProfile? profile)
        => _inner.TryGetProfile(profileId, out profile);

    /// <inheritdoc />
    public StereoProfile GetProfile(string? profileId) => _inner.GetProfile(profileId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetEnabledProfileIds(PluginConfiguration configuration) => Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<StereoProfile> GetEnabledProfiles(PluginConfiguration configuration) => Array.Empty<StereoProfile>();

    /// <inheritdoc />
    public string ResolveDefaultProfileId(PluginConfiguration configuration, string? deviceId = null, string? clientName = null)
        => _inner.ResolveDefaultProfileId(configuration, deviceId, clientName);

    /// <inheritdoc />
    public StereoProfile GetDefaultProfile(PluginConfiguration configuration, string? deviceId = null, string? clientName = null)
        => _inner.GetDefaultProfile(configuration, deviceId, clientName);

    /// <inheritdoc />
    public string ResolveFallbackProfileId(PluginConfiguration configuration) => _inner.ResolveFallbackProfileId(configuration);

    /// <inheritdoc />
    public StereoProfile GetFallbackProfile(PluginConfiguration configuration) => _inner.GetFallbackProfile(configuration);

    /// <inheritdoc />
    public IReadOnlyList<StereoProfile> GetOfferedProfiles(PluginConfiguration configuration, string? deviceId = null, string? clientName = null)
        => Array.Empty<StereoProfile>();
}

/// <summary>
/// A settings source over one in-memory settings object.
/// </summary>
public sealed class StubConfigurationSource : IAnaglyfinConfigurationSource
{
    /// <summary>Gets or sets the settings every read answers with.</summary>
    public PluginConfiguration Configuration { get; set; } = new();

    /// <inheritdoc />
    public PluginConfiguration GetConfiguration() => Configuration;
}

/// <summary>
/// The three library events, with the raise methods a test needs and nothing else.
/// </summary>
public sealed class FakeLibraryEventSource : ILibraryEventSource
{
    /// <inheritdoc />
    public event EventHandler<ItemChangeEventArgs>? ItemAdded;

    /// <inheritdoc />
    public event EventHandler<ItemChangeEventArgs>? ItemUpdated;

    /// <inheritdoc />
    public event EventHandler<ItemChangeEventArgs>? ItemRemoved;

    /// <summary>How many listeners the service currently holds, across all three events.</summary>
    public int SubscriberCount
        => (ItemAdded?.GetInvocationList().Length ?? 0)
           + (ItemUpdated?.GetInvocationList().Length ?? 0)
           + (ItemRemoved?.GetInvocationList().Length ?? 0);

    /// <summary>Raises an item-added event.</summary>
    /// <param name="item">The item that changed.</param>
    public void RaiseAdded(BaseItem item)
        => ItemAdded?.Invoke(this, new ItemChangeEventArgs { Item = item });

    /// <summary>Raises an item-updated event.</summary>
    /// <param name="item">The item that changed.</param>
    public void RaiseUpdated(BaseItem item)
        => ItemUpdated?.Invoke(this, new ItemChangeEventArgs { Item = item });

    /// <summary>Raises an item-removed event.</summary>
    /// <param name="item">The item that left.</param>
    public void RaiseRemoved(BaseItem item)
        => ItemRemoved?.Invoke(this, new ItemChangeEventArgs { Item = item });

    /// <summary>Raises an item-updated event carrying no item at all.</summary>
    public void RaiseUpdatedWithNoItem()
        => ItemUpdated?.Invoke(this, null!);
}

/// <summary>
/// The reconciliation work, reduced to what a caller can assert: who was asked, for which item, and
/// whether the answer arrived.
/// </summary>
public sealed class RecordingReconciler : IProfileVersionReconciler
{
    private readonly TaskCompletionSource<bool> _firstLibraryPass = NewSignal();

    private readonly List<TaskCompletionSource<bool>> _gates = new();

    private readonly List<Guid> _itemReconciliations = new();

    private readonly object _sync = new();

    private int _libraryPasses;

    private Exception? _throwOnReconcile;

    /// <summary>Gets the item ids reconciled on request, in call order.</summary>
    public IReadOnlyList<Guid> ItemReconciliations
    {
        get
        {
            lock (_sync)
            {
                return _itemReconciliations.ToArray();
            }
        }
    }

    /// <summary>Gets how many full passes started.</summary>
    public int LibraryPasses => Volatile.Read(ref _libraryPasses);

    /// <summary>
    /// Gets or sets the exception the next reconciliation throws - once, so that a test can watch
    /// the worker survive one failure and take the next request.
    /// </summary>
    public Exception? ThrowOnReconcile
    {
        get => _throwOnReconcile;
        set
        {
            lock (_sync)
            {
                _throwOnReconcile = value;
            }
        }
    }

    /// <summary>
    /// Gets a task completed the first time a full pass starts, so a test can wait for the worker
    /// rather than for a wall clock.
    /// </summary>
    public Task FirstLibraryPass => _firstLibraryPass.Task;

    /// <summary>
    /// Makes the next <paramref name="count"/> full passes block until <see cref="ReleasePasses"/>
    /// is called, which is how a test holds a worker inside a pass.
    /// </summary>
    /// <param name="count">How many passes to hold.</param>
    public void HoldPasses(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _gates.Add(NewSignal());
        }
    }

    /// <summary>Frees every held pass.</summary>
    public void ReleasePasses()
    {
        foreach (var gate in _gates)
        {
            gate.TrySetResult(true);
        }
    }

    /// <inheritdoc />
    public async Task<ProfileVersionReconcileResult> ReconcileLibraryAsync(CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _libraryPasses) - 1;

        _firstLibraryPass.TrySetResult(true);

        if (index < _gates.Count)
        {
            await _gates[index].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        FailIfAsked();

        return new ProfileVersionReconcileResult();
    }

    /// <inheritdoc />
    public Task<ProfileVersionReconcileResult> ReconcileItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _itemReconciliations.Add(itemId);
        }

        FailIfAsked();

        return Task.FromResult(new ProfileVersionReconcileResult());
    }

    /// <summary>Throws the armed exception once, so a failure is a one-shot and not a state.</summary>
    private void FailIfAsked()
    {
        Exception? failure;

        lock (_sync)
        {
            failure = _throwOnReconcile;
            _throwOnReconcile = null;
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The reconcile requests a caller made, as a list.
/// </summary>
public sealed class FakeReconcileTrigger : IProfileVersionReconcileTrigger
{
    /// <summary>Gets the items asked about, in call order.</summary>
    public List<Guid> ItemRequests { get; } = new();

    /// <summary>Gets how many full passes were asked for.</summary>
    public int FullPassRequests { get; private set; }

    /// <inheritdoc />
    public void RequestItem(Guid itemId) => ItemRequests.Add(itemId);

    /// <inheritdoc />
    public void RequestFullPass() => FullPassRequests++;
}

/// <summary>
/// The two files and one folder the version-item tests are built on.
/// </summary>
/// <remarks>
/// The shapes are the ones the feature was reproduced on: a movie whose own file is plain 1080p
/// with the MVC file beside it, and the same movie alone. Both are stated as the server states
/// them - a static source keyed by the id of the item the file belongs to.
/// </remarks>
public static class ProfileVersionFixtures
{
    static ProfileVersionFixtures()
    {
        // Some tests change an item's name after it is built, and the server's sort-name getter re-derives
        // the sort name through this static in that case. A unit test has no server, but it still has to
        // answer that getter the way the server would.
        BaseItem.ConfigurationManager ??= new StubServerConfigurationManager();
    }

    /// <summary>The folder both movies are filed under.</summary>
    public const string FolderPath = "/movies/Ready Player One (2018)";

    /// <summary>The plain 1080p file of the stacked movie.</summary>
    public const string PlainPath = FolderPath + "/Ready Player One (2018) - 1080p.mkv";

    /// <summary>The 3D MVC file.</summary>
    public const string MvcPath = FolderPath + "/Ready Player One (2018) - 3D mvc.mkv";

    /// <summary>The folder's id.</summary>
    public static readonly Guid FolderId = Guid.Parse("6f1a2b3c-4d5e-4f60-8172-8394a5b6c7d8");

    /// <summary>The id of the item a user browses to.</summary>
    public static readonly Guid MovieId = Guid.Parse("1c4c3d0a-9b0c-4a1b-8c7d-2e5f6a7b8c9d");

    /// <summary>The id of the hidden MVC item, which no client can list.</summary>
    public static readonly Guid MvcVersionItemId = Guid.Parse("7a5d0f2c-1b3e-4c6a-9d84-0f1e2d3c4b5a");

    /// <summary>The duration every fixture file reports.</summary>
    public const long RunTimeTicks = 7_200_000_000L;

    /// <summary>The size every fixture file reports.</summary>
    public const long FileSize = 8_000_000_000L;

    /// <summary>The bitrate every fixture file reports.</summary>
    public const int Bitrate = 40_000_000;

    /// <summary>Builds the folder a movie is filed under.</summary>
    /// <returns>A folder carrying <see cref="FolderId"/>.</returns>
    public static Folder CreateFolder() => new() { Id = FolderId, Name = "Ready Player One (2018)", Path = FolderPath };

    /// <summary>
    /// Builds the item a user browses to, reporting the given files as its media sources.
    /// </summary>
    /// <param name="sources">The static sources the item reports, in server order.</param>
    /// <param name="path">The item's own file.</param>
    /// <param name="id">The item's id.</param>
    /// <returns>The scripted item.</returns>
    public static ScriptedVideo CreateMovie(
        IReadOnlyList<MediaSourceInfo> sources,
        string path = PlainPath,
        Guid? id = null)
        => new()
        {
            Id = id ?? MovieId,
            Name = "Ready Player One (2018)",

            // What a library item always carries: the server derives a sort name when it files one
            // and stores it with the row, so an item read back from the library has one. A test item
            // without it would be an item the real server cannot produce.
            SortName = "ready player one (2018)",
            Path = path,
            ParentId = FolderId,
            RunTimeTicks = RunTimeTicks,
            Container = "mkv",
            Size = FileSize,
            TotalBitrate = Bitrate,
            VideoType = VideoType.VideoFile,
            StaticSources = sources
        };

    /// <summary>
    /// Builds the item a user browses to over its MVC file alone: the single-file case.
    /// </summary>
    /// <param name="id">The item's id.</param>
    /// <param name="path">The item's own file.</param>
    /// <returns>The scripted item, reporting one source: its own MVC file.</returns>
    public static ScriptedVideo CreateSingleFileMvcMovie(Guid? id = null, string path = MvcPath)
    {
        var itemId = id ?? MovieId;

        return CreateMovie(new[] { CreateSource(itemId, "3D mvc", path) }, path, itemId);
    }

    /// <summary>
    /// Builds the stacked case: one item, its plain file as its own source and the MVC file as a
    /// version of it that only the media-source API can name.
    /// </summary>
    /// <returns>The scripted item.</returns>
    public static ScriptedVideo CreateStackedMvcMovie()
        => CreateMovie(new[]
        {
            CreateSource(MovieId, "1080p", PlainPath),
            CreateSource(MvcVersionItemId, "3D mvc", MvcPath)
        });

    /// <summary>
    /// Builds one static media source as the item's own media-source API reports it.
    /// </summary>
    /// <param name="id">The id the source is keyed by: the item the file belongs to.</param>
    /// <param name="name">The label the source carries.</param>
    /// <param name="path">The file the source is.</param>
    /// <param name="video3DFormat">The stereo declaration the file carries, when it has one.</param>
    /// <returns>The source report.</returns>
    public static MediaSourceInfo CreateSource(
        Guid id,
        string name,
        string path,
        Video3DFormat? video3DFormat = null)
        => new()
        {
            Id = id.ToString("N", CultureInfo.InvariantCulture),
            Name = name,
            Path = path,
            Protocol = MediaProtocol.File,
            Container = "mkv",
            Size = FileSize,
            Bitrate = Bitrate,
            RunTimeTicks = RunTimeTicks,
            Formats = new[] { "matroska" },
            Video3DFormat = video3DFormat,
            MediaStreams = new[]
            {
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = 1920, Height = 1080 },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "truehd", Language = "eng" },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Codec = "ass", Language = "eng" }
            }
        };

    /// <summary>
    /// Builds one eligible source of one item, as the scanner would have accepted it.
    /// </summary>
    /// <param name="item">The item the file belongs to.</param>
    /// <param name="source">The source report the file arrived as.</param>
    /// <returns>The eligible source, carrying the identity the scanner gave it.</returns>
    public static MvcEligibleSource CreateEligibleSource(BaseItem item, MediaSourceInfo source)
        => new(
            item,
            source,
            source.Path,
            source.Id,
            source.Name,
            MvcEligibleSourceScanner.SourceIdentityKey(source.Id, source.Path),
            MetadataSourceItemIdOf(source));

    /// <summary>
    /// The item a source report is keyed by, which is the item whose metadata describes that file.
    /// </summary>
    /// <param name="source">The source report.</param>
    /// <returns>The owning item's id, or <c>null</c> when the source is keyed by something else.</returns>
    public static Guid? MetadataSourceItemIdOf(MediaSourceInfo source)
        => Guid.TryParse(source.Id, CultureInfo.InvariantCulture, out var itemId) && itemId != Guid.Empty
            ? itemId
            : null;

    /// <summary>
    /// Builds the hidden MVC item of the stacked movie: the file beside the primary, the one the
    /// versions are built from and the one no client can list.
    /// </summary>
    /// <param name="scraped">
    /// Whether to wear the metadata a scrape left on it - the movie's own title, synopsis, artwork and
    /// cast, deliberately nothing like the 1080p root's. A version has to be wearing these, and not
    /// the root's, for the details panel of a version to say anything.
    /// </param>
    /// <returns>The alternate-version item, filed where the movie is filed and claimed by it.</returns>
    public static Video CreateMvcAlternateItem(bool scraped = true)
    {
        var item = new Video
        {
            Id = MvcVersionItemId,
            Name = "Ready Player One (2018)",
            SortName = "ready player one (2018)",
            Path = MvcPath,
            ParentId = FolderId,
            RunTimeTicks = RunTimeTicks,
            Container = "mkv",
            Size = FileSize,
            TotalBitrate = Bitrate,
            VideoType = VideoType.VideoFile
        };

        item.SetPrimaryVersionId(MovieId);

        if (scraped)
        {
            StampScrapedMetadata(item, title: "Ready Player One");
        }

        return item;
    }

    /// <summary>
    /// Puts a full set of scraped movie metadata on an item - the values a metadata manager would
    /// have written, none of which a version item would invent for itself.
    /// </summary>
    /// <param name="item">The item to describe.</param>
    /// <param name="title">The original title to carry.</param>
    public static void StampScrapedMetadata(BaseItem item, string title)
    {
        item.OriginalTitle = title;
        item.Overview = "In 2045, the answer can be found in the OASIS.";
        item.Tagline = "An adventure beyond degree.";
        item.ForcedSortName = title;
        item.SortName = title.ToLowerInvariant();
        item.Genres = new[] { "Science Fiction", "Adventure" };
        item.Tags = new[] { "3D", "Virtual reality" };
        item.Studios = new[] { "Warner Bros." };
        item.ProductionLocations = new[] { "London, England, USA" };
        item.OfficialRating = "PG-13";
        item.CustomRating = "Staff pick";
        item.CommunityRating = 7.4f;
        item.CriticRating = 69f;
        item.ProductionYear = 2018;
        item.PremiereDate = new DateTime(2018, 3, 29, 0, 0, 0, DateTimeKind.Utc);
        item.EndDate = new DateTime(2018, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        item.HomePageUrl = "https://example.invalid/ready-player-one";
        item.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Imdb"] = "tt1677720",
            ["Tmdb"] = "293167"
        };

        item.ImageInfos = new[]
        {
            CreateImage(FolderPath + "/poster-mvc.jpg", ImageType.Primary),
            CreateImage(FolderPath + "/backdrop-mvc.jpg", ImageType.Backdrop)
        };
    }

    /// <summary>
    /// Builds one image row, pointing at a file the source item's library already has.
    /// </summary>
    /// <param name="path">The image file.</param>
    /// <param name="type">What the image is.</param>
    /// <returns>The image row.</returns>
    public static ItemImageInfo CreateImage(string path, ImageType type)
        => new()
        {
            Path = path,
            Type = type,
            DateModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Width = 1000,
            Height = 1500,
            BlurHash = "UA9Ga}L2-p9O%1Vts,ae00ae00ae"
        };

    /// <summary>
    /// Builds one credit, as the people repository would hand it back.
    /// </summary>
    /// <param name="name">Who is credited.</param>
    /// <param name="role">What for.</param>
    /// <param name="kind">What kind of credit it is.</param>
    /// <param name="sortOrder">The item's own order for this credit.</param>
    /// <returns>The credit.</returns>
    public static PersonInfo CreatePerson(
        string name,
        string? role,
        PersonKind kind,
        int? sortOrder = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Role = role,
            Type = kind,
            SortOrder = sortOrder,
            ImageUrl = $"Persons/{name}"
        };

    private sealed class StubServerConfigurationManager : IServerConfigurationManager
    {
        private readonly ServerConfiguration _configuration = new();

        public event EventHandler<ConfigurationUpdateEventArgs> NamedConfigurationUpdating = (_, _) => { };

        public event EventHandler<EventArgs> ConfigurationUpdated = (_, _) => { };

        public event EventHandler<ConfigurationUpdateEventArgs> NamedConfigurationUpdated = (_, _) => { };

        public IServerApplicationPaths ApplicationPaths => null!;

        public IApplicationPaths CommonApplicationPaths => null!;

        public ServerConfiguration Configuration => _configuration;

        public BaseApplicationConfiguration CommonConfiguration => _configuration;

        public void AddParts(IEnumerable<IConfigurationFactory> factories)
        {
        }

        public object GetConfiguration(string key) => _configuration;

        public ConfigurationStore[] GetConfigurationStores() => Array.Empty<ConfigurationStore>();

        public Type GetConfigurationType(string key) => typeof(ServerConfiguration);

        public void RegisterConfiguration<T>()
            where T : IConfigurationFactory
        {
        }

        public void SaveConfiguration()
        {
        }

        public void SaveConfiguration(string key, object configuration)
        {
        }

        public void ReplaceConfiguration(BaseApplicationConfiguration newConfiguration)
        {
        }
    }
}
