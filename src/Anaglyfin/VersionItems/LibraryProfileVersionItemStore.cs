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
/// per pair, deleting an item takes its streams and its links with it, and a scan never removes a
/// child whose path is not a file.
/// </para>
/// <para>
/// There is deliberately no direct database access: nothing here reaches past
/// <see cref="ILibraryManager"/> into repositories beyond the stream repository the item model
/// itself uses, so a server upgrade that moves the schema under these interfaces moves Anaglyfin
/// with it rather than leaving it behind.
/// </para>
/// </remarks>
public sealed class LibraryProfileVersionItemStore : IProfileVersionItemStore
{
    private readonly ILibraryManager _libraryManager;

    private readonly IMediaStreamRepository _mediaStreamRepository;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryProfileVersionItemStore"/> class.
    /// </summary>
    /// <param name="libraryManager">The server's library manager.</param>
    /// <param name="mediaStreamRepository">The repository a item's reported streams live in.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public LibraryProfileVersionItemStore(ILibraryManager libraryManager, IMediaStreamRepository mediaStreamRepository)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _mediaStreamRepository = mediaStreamRepository ?? throw new ArgumentNullException(nameof(mediaStreamRepository));
    }

    /// <inheritdoc />
    public BaseItem? FindItem(Guid itemId)
        => itemId == Guid.Empty ? null : _libraryManager.GetItemById(itemId);

    /// <inheritdoc />
    public IReadOnlyList<Video> GetVersionRootCandidates()
    {
        // One query, one pass: the server's general queries already drop anything that names a
        // primary version, so what comes back is the set of items that could own versions -
        // never one of Anaglyfin's own version items, and never a stack part a client cannot open.
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
}
