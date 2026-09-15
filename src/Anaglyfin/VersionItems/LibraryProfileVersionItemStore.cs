using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace Anaglyfin.VersionItems;

/// <summary>
/// The version-item store, backed by the server's own public library and persistence APIs.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is a translation, and none of it is a decision. The server exposes the
/// alternate-version model publicly (<c>CreateItem</c>, <c>UpdateItemAsync</c>,
/// <c>UpsertLinkedChild</c>, <c>GetLinkedAlternateVersions</c> on <see cref="ILibraryManager"/>,
/// <c>SaveMediaStreams</c> on <see cref="IMediaStreamRepository"/>) - the same calls the stock
/// "Merge versions" REST endpoint performs in-process - so Anaglyfin writes versions the way the
/// server writes them and gets the server's guarantees with them: linked children are stored once
/// per pair, deleting an item takes its streams, its credits and its images with it, and a scan
/// never removes a child whose path is not a file.
/// </para>
/// <para>
/// There is deliberately no direct database access: nothing here reaches past
/// <see cref="ILibraryManager"/> into repositories beyond the ones the item model itself uses -
/// the stream repository, the people repository and the item persistence service - so a server
/// upgrade that moves the schema under these interfaces moves Anaglyfin with it rather than
/// leaving it behind.
/// </para>
/// <para>
/// The three metadata-bearing writes go to the tables that own the metadata rather than through a
/// refresh: <see cref="IMediaStreamRepository"/> for the streams a version reports,
/// <see cref="IPeopleRepository"/> for its credits and <see cref="IItemPersistenceService"/> for
/// its image rows. That is what keeps a version's metadata a database fact. Nothing here opens a
/// metadata saver, and none of these APIs can write a file: a version has no file to describe.
/// </para>
/// </remarks>
public sealed class LibraryProfileVersionItemStore : IProfileVersionItemStore
{
    private readonly ILibraryManager _libraryManager;

    private readonly IMediaStreamRepository _mediaStreamRepository;

    private readonly IPeopleRepository _peopleRepository;

    private readonly IItemPersistenceService _itemPersistenceService;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryProfileVersionItemStore"/> class.
    /// </summary>
    /// <param name="libraryManager">The server's library manager.</param>
    /// <param name="mediaStreamRepository">The repository a item's reported streams live in.</param>
    /// <param name="peopleRepository">The repository an item's credits live in.</param>
    /// <param name="itemPersistenceService">The service an item's image rows live behind.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public LibraryProfileVersionItemStore(
        ILibraryManager libraryManager,
        IMediaStreamRepository mediaStreamRepository,
        IPeopleRepository peopleRepository,
        IItemPersistenceService itemPersistenceService)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _mediaStreamRepository = mediaStreamRepository ?? throw new ArgumentNullException(nameof(mediaStreamRepository));
        _peopleRepository = peopleRepository ?? throw new ArgumentNullException(nameof(peopleRepository));
        _itemPersistenceService = itemPersistenceService ?? throw new ArgumentNullException(nameof(itemPersistenceService));
    }

    /// <inheritdoc />
    public BaseItem? FindItem(Guid itemId)
        => itemId == Guid.Empty ? null : _libraryManager.GetItemById(itemId);

    /// <inheritdoc />
    public IReadOnlyList<Video> GetVersionRootCandidates()
    {
        // One query for one pass, and the authoritative one: a filename, folder or tag the detector
        // understands is not necessarily visible to a 3D or tag query, and a root that lost its own
        // MVC signal can still own Anaglyfin versions that have to be removed. The cost is kept down
        // by MvcEligibilityPrefilter before the scan, not by narrowing the population below what the
        // detector can actually say anything about.
        //
        // What makes this list the right list is the general-query rule the server applies to it: an
        // item that names a primary version is not in a general query at all, which is both why a
        // profile version never shows up in browse or search and why a pass over this list never
        // mistakes one of Anaglyfin's own items for a library movie. SourceTypes states the intent the
        // server's own query builder does not implement (it ignores that field), so the eligibility
        // scan remains the real gate on what a pass attempts; it is declared here because a background
        // pass has no business editing channel or remote content, and a server that honours the field
        // will find Anaglyfin already asking correctly.
        var query = new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            SourceTypes = new[] { SourceType.Library },
            Recursive = true,
            IsVirtualItem = false,
            IsPlaceHolder = false
        };

        return _libraryManager.GetItemList(query).OfType<Video>().ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<Video> GetLinkedAlternateVersions(Video primary)
        => _libraryManager.GetLinkedAlternateVersions(primary).ToList();

    /// <inheritdoc />
    public Folder? GetFolder(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // The parent id is what the database carries; GetParent() would fall back to the root
        // folder, which would quietly file a version somewhere its audience cannot reach.
        return item.ParentId == Guid.Empty ? null : _libraryManager.GetItemById(item.ParentId) as Folder;
    }

    /// <inheritdoc />
    public void CreateItem(BaseItem item, Folder? parent)
    {
        ArgumentNullException.ThrowIfNull(item);

        _libraryManager.CreateItem(item, parent);
    }

    /// <inheritdoc />
    public Task UpdateItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        // The server asks for the parent alongside the item so it can invalidate cached children;
        // the parent id is the item's own record of where it was filed, and the root folder is the
        // answer the server itself gives when there is no folder.
        var parent = GetFolder(item) ?? _libraryManager.RootFolder;

        return _libraryManager.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, cancellationToken);
    }

    /// <inheritdoc />
    public void DeleteItem(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Never a file: a profile version has no file, and the flag is the server's only
        // instruction to delete one.
        _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, notifyParentItem: false);
    }

    /// <inheritdoc />
    public void LinkAlternateVersion(Guid primaryId, Guid versionItemId)
        => _libraryManager.UpsertLinkedChild(primaryId, versionItemId, LinkedChildType.LinkedAlternateVersion);

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        => _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = itemId });

    /// <inheritdoc />
    public void SaveMediaStreams(Guid itemId, IReadOnlyList<MediaStream> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);

        _mediaStreamRepository.SaveMediaStreams(itemId, streams, cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyList<PersonInfo> GetPeople(Guid itemId)
    {
        if (itemId == Guid.Empty)
        {
            return Array.Empty<PersonInfo>();
        }

        // The credits of one item, in the order the item lists them (the repository sorts by the
        // mapping's list order when the query names an item). The record count is not asked for:
        // nobody pages through a movie's cast from here, and counting is the expensive half of the
        // query when a library is behind an access filter.
        return _peopleRepository.GetPeople(new InternalPeopleQuery { ItemId = itemId, EnableTotalRecordCount = false }).Items;
    }

    /// <inheritdoc />
    public void SavePeople(Guid itemId, IReadOnlyList<PersonInfo> people)
    {
        ArgumentNullException.ThrowIfNull(people);

        // The server's own credit write, keyed by item id: it reuses the person rows that already
        // exist by name and kind, so crediting a version does not start a second Halle Berry, and
        // it replaces the item's credit list wholesale, so nothing of the previous answer survives.
        _peopleRepository.UpdatePeople(itemId, people);
    }

    /// <inheritdoc />
    public Task SaveImagesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Writes the item's image rows as they stand on the item: the same files, under the version's
        // own id. The server's own refresh path would size and hash each file again and fetch any
        // remote one, which is work on files a version does not own; this writes the rows and stops.
        return _itemPersistenceService.SaveImagesAsync(item, cancellationToken);
    }
}
