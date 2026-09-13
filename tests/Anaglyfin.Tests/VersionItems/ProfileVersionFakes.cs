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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// The library a profile version-item pass runs over, held in memory.
/// </summary>
/// <remarks>
/// <para>
/// The manager's contract is eleven reads and writes over <see cref="IProfileVersionItemStore"/>,
/// so a fake of that interface is a whole library for the purposes of a reconciliation test: items,
/// the links between them, and the streams they report. Nothing here mimics server behaviour beyond
/// those eleven calls - it is the state a reconcile reads and what it leaves behind, which is
/// exactly what these tests need to see, including the state a real server can be in and a scan
/// would not produce (an item that exists but is no longer linked).
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
        => _items.TryGetValue(itemId, out var item) ? item : null;

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
        // What the server does with it: the row, its streams and every link naming it.
        Deleted.Add(item);
        _items.Remove(item.Id);
        _streams.Remove(item.Id);
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
            MvcEligibleSourceScanner.SourceIdentityKey(source.Id, source.Path));
}
