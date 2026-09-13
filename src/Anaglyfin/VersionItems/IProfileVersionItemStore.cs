using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Anaglyfin.VersionItems;

/// <summary>
/// The library facts a profile version item needs, and the writes it is allowed to make.
/// </summary>
/// <remarks>
/// <para>
/// This is the version-item manager's whole view of the library, and it is deliberately small.
/// The server's own surface (<c>ILibraryManager</c>, <c>IItemRepository</c>,
/// <c>IMediaStreamRepository</c>) spans a hundred members, most of them about scanning,
/// security and virtual folders; reconciling versions needs seven reads and five writes. Naming
/// those twelve is what makes the reconciliation logic testable without a server, and it is also
/// what keeps the manager from reaching for a library operation that would surprise the server -
/// a reconcile that can only read, create, link, update and delete its own items cannot corrupt
/// anything else.
/// </para>
/// <para>
/// The implementation over the real server (<see cref="LibraryProfileVersionItemStore"/>) adds no
/// policy: it translates to the public Jellyfin APIs and nothing else. Every decision - what a
/// version is, when one is missing, when one is stale - lives in
/// <see cref="ProfileVersionItemManager"/>.
/// </para>
/// </remarks>
public interface IProfileVersionItemStore
{
    /// <summary>
    /// Finds one item by id, whatever it is.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The item, or <c>null</c> when nothing carries that id.</returns>
    /// <remarks>
    /// Unfiltered on purpose: the manager has to be able to tell "no such item" (create it) from
    /// "an item that is not ours" (leave it alone), and a user-scoped lookup answers both with
    /// <c>null</c>.
    /// </remarks>
    BaseItem? FindItem(Guid itemId);

    /// <summary>
    /// Gets the items a full reconciliation pass has to consider.
    /// </summary>
    /// <returns>Every library video that could carry versions of its own.</returns>
    /// <remarks>
    /// Alternate versions are not in here: the server's general queries exclude items that name a
    /// primary version, which is both why a profile version item never shows up in browse or search
    /// and why a pass over this list never mistakes one of its own items for a library movie.
    /// </remarks>
    IReadOnlyList<Video> GetVersionRootCandidates();

    /// <summary>
    /// Gets the items linked to a primary as its alternate versions.
    /// </summary>
    /// <param name="primary">The item whose versions to read.</param>
    /// <returns>The linked items, including anything linked for reasons other than Anaglyfin.</returns>
    IReadOnlyList<Video> GetLinkedAlternateVersions(Video primary);

    /// <summary>
    /// Gets the folder a version item belongs under.
    /// </summary>
    /// <param name="item">The item to take a folder from.</param>
    /// <returns>The item's folder, or <c>null</c> when it has none.</returns>
    /// <remarks>
    /// A version lives beside the movie it converts, which is what makes it visible to exactly the
    /// audience that may see that movie: access is inherited from the folder, not granted here.
    /// </remarks>
    Folder? GetFolder(BaseItem item);

    /// <summary>
    /// Puts a new item into the library.
    /// </summary>
    /// <param name="item">The item, already carrying its id and every field it should be born with.</param>
    /// <param name="parent">The folder to file it under, when one is known.</param>
    void CreateItem(BaseItem item, Folder? parent);

    /// <summary>
    /// Writes an existing item back after a change.
    /// </summary>
    /// <param name="item">The item, already modified.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the item is stored.</returns>
    Task UpdateItemAsync(BaseItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Removes an item from the library without touching any file.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    /// <remarks>
    /// The server removes the item's own media streams and its linked-child rows with it, so an
    /// unlinked, deleted version leaves nothing behind.
    /// </remarks>
    void DeleteItem(BaseItem item);

    /// <summary>
    /// Links an item to a primary as one of its alternate versions, without creating a duplicate.
    /// </summary>
    /// <param name="primaryId">The primary item's id.</param>
    /// <param name="versionItemId">The id of the item that is a version of it.</param>
    void LinkAlternateVersion(Guid primaryId, Guid versionItemId);

    /// <summary>
    /// Reads the media streams persisted for one item.
    /// </summary>
    /// <param name="itemId">The item to read.</param>
    /// <returns>The persisted streams, in persisted order.</returns>
    IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId);

    /// <summary>
    /// Writes the media streams one item reports.
    /// </summary>
    /// <param name="itemId">The item to write.</param>
    /// <param name="streams">The streams to persist, in report order.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// A static media source reports the streams persisted under the item's id, so a version item
    /// without persisted streams would advertise a picture with no tracks in it - no audio, no
    /// subtitles, and nothing for the server to build a transcode from.
    /// </remarks>
    void SaveMediaStreams(Guid itemId, IReadOnlyList<MediaStream> streams, CancellationToken cancellationToken);
}
