using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Anaglyfin.VersionItems;

/// <summary>
/// Keeps the library's profile version items equal to what the library and the settings say they
/// should be.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model.</b> For every file of an item that a profile could convert (as
/// <see cref="MvcEligibleSourceScanner"/> answers), and for every profile an administrator has
/// enabled, there is exactly one linked alternate-version <see cref="Video"/> item under the item
/// the user browses to. Those items are what put Anaglyfin's versions in the stock details-page
/// selector: the selector reads the item DTO's <c>MediaSources</c>, which the server fills only
/// from <em>static</em> sources, and a linked alternate version is a static source. The dynamic
/// provider remains in place as the fallback for anything whose item cannot be created, and
/// suppresses a profile this manager has already materialised, so a version reaches a client
/// exactly once either way.
/// </para>
/// <para>
/// <b>Desired state, not events.</b> Reconciliation is a diff, never a reaction: it computes the
/// set of versions the item should have, reads the set it has, and applies the difference. That is
/// what makes the same routine correct for a first start-up, for a file replaced on disk, for an
/// administrator who disabled a profile, and for a request that arrived twice - and what makes
/// running it twice cheap and running it a third time a no-op. An event only ever says "ask again";
/// nothing decides anything from which event it was.
/// </para>
/// <para>
/// <b>Stability.</b> A version item's id is derived from the identity of the media source it
/// converts and its profile id (<see cref="AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource"/>),
/// never generated, so the item a library builds today is the item the same library builds after a
/// restart, and a user's resume position, chosen version and played state - all pinned on that id -
/// survive every reconcile. An id that is already worn by something that is not Anaglyfin's is left
/// alone: a version that cannot be created is a version the dynamic provider still offers.
/// </para>
/// <para>
/// <b>Never standalone.</b> A profile version item is a version of something. One that has lost its
/// primary - deleted under it, or un-merged by hand - would appear in browse as a movie with a
/// marker URL for a file, so it is deleted on sight rather than inherited.
/// </para>
/// <para>
/// <b>One at a time.</b> A single gate serialises every pass: the reconciliations read the library
/// they are about to write, and two of them running at once can each see a version item as missing
/// and create it twice - the one outcome that cannot be repaired afterwards.
/// </para>
/// </remarks>
public sealed class ProfileVersionItemManager : IProfileVersionReconciler
{
    private readonly IProfileVersionItemStore _store;

    private readonly IMvcSourceDetector _detector;

    private readonly IProfileCatalog _profileCatalog;

    private readonly IAnaglyfinConfigurationSource _configurationSource;

    private readonly ILogger<ProfileVersionItemManager> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileVersionItemManager"/> class.
    /// </summary>
    /// <param name="store">The library, as far as a version item needs it.</param>
    /// <param name="detector">Decides which files may carry versions.</param>
    /// <param name="profileCatalog">The profiles, which of them are enabled, and their order.</param>
    /// <param name="configurationSource">The live plugin settings.</param>
    /// <param name="logger">Logger for every decision and every failure.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public ProfileVersionItemManager(
        IProfileVersionItemStore store,
        IMvcSourceDetector detector,
        IProfileCatalog profileCatalog,
        IAnaglyfinConfigurationSource configurationSource,
        ILogger<ProfileVersionItemManager> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _profileCatalog = profileCatalog ?? throw new ArgumentNullException(nameof(profileCatalog));
        _configurationSource = configurationSource ?? throw new ArgumentNullException(nameof(configurationSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reconciles every video in the library.
    /// </summary>
    /// <param name="cancellationToken">Stops the pass between items.</param>
    /// <returns>What the pass changed.</returns>
    /// <remarks>
    /// Called on start-up and whenever the settings are saved. One item failing (a database read, an
    /// item mid-refresh) is logged and skipped: the pass has no reason to abandon the rest of the
    /// library because one folder had a bad moment, and the next request for that item reconciles it
    /// again.
    /// </remarks>
    public async Task<ProfileVersionReconcileResult> ReconcileLibraryAsync(CancellationToken cancellationToken)
    {
        var total = new ProfileVersionReconcileResult();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<Video> roots;
            try
            {
                roots = _store.GetVersionRootCandidates();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anaglyfin could not list the library for a version-item pass; nothing will change this time.");
                return total;
            }

            _logger.LogDebug("Anaglyfin is reconciling profile version items across {Count} items.", roots.Count);

            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    total.Add(await ReconcileRootAsync(root, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Anaglyfin could not reconcile the profile version items of {ItemName}; leaving it as it is.",
                        root?.Name);
                    total.Skipped++;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (total.Changed || total.Skipped > 0)
        {
            _logger.LogInformation(
                "Anaglyfin version items: {Created} created, {Updated} updated, {Deleted} removed, {Skipped} skipped.",
                total.Created,
                total.Updated,
                total.Deleted,
                total.Skipped);
        }

        return total;
    }

    /// <summary>
    /// Reconciles the versions of one item, whichever item the change was reported for.
    /// </summary>
    /// <param name="itemId">The item a change was noticed on.</param>
    /// <param name="cancellationToken">Stops the work between writes.</param>
    /// <returns>What the reconciliation changed.</returns>
    /// <remarks>
    /// The item asked about is not always the item that owns the versions: the hidden MVC file of a
    /// stack is an alternate version itself, and its versions belong to the item the user can
    /// actually reach. And an item that is itself an Anaglyfin version is never offered versions of
    /// its own - that is the recursion a naive listener would never stop.
    /// </remarks>
    public async Task<ProfileVersionReconcileResult> ReconcileItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store.FindItem(itemId) is not Video video)
            {
                // Not there (removed: its own versions go with it, or already did), or not a video
                // (a folder, an album: nothing here concerns them).
                return new ProfileVersionReconcileResult();
            }

            if (IsAnaglyfinVersion(video))
            {
                return await ReconcileOrphanVersionAsync(video, cancellationToken).ConfigureAwait(false);
            }

            var root = ResolveRoot(video);
            if (root is null)
            {
                // An alternate version whose primary is gone: its own versions, if it had any, are
                // handled by the pass that removes it, not by this one.
                return new ProfileVersionReconcileResult();
            }

            return await ReconcileRootAsync(root, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings one item's versions in line with the settings, creating, rewriting and removing as
    /// the difference demands.
    /// </summary>
    private async Task<ProfileVersionReconcileResult> ReconcileRootAsync(Video root, CancellationToken cancellationToken)
    {
        var result = new ProfileVersionReconcileResult();

        ArgumentNullException.ThrowIfNull(root);

        if (IsAnaglyfinVersion(root))
        {
            // A version of a version is never wanted. Reaching here at all means the item was
            // offered as a library item in its own right, which only happens once it has stopped
            // being anybody's version - so the same rule as the event path applies to it.
            return await ReconcileOrphanVersionAsync(root, cancellationToken).ConfigureAwait(false);
        }

        // The enabled profiles, not the offered ones: an offered list is a per-request answer, with a
        // device's default promoted to the front and - for a device override naming a profile nobody
        // enabled - a profile that is not enabled at all. A version item is not per-request. It is in
        // the library, visible to every client, so it is materialised for exactly the profiles an
        // administrator switched on. Which of them a given client should start on stays the dynamic
        // provider's answer, because that is the one question an item cannot ask.
        var configuration = _configurationSource.GetConfiguration();
        var enabled = _profileCatalog.GetEnabledProfiles(configuration) ?? new List<StereoProfile>();

        var scan = MvcEligibleSourceScanner.IsOfferableVideo(root)
            ? MvcEligibleSourceScanner.Scan(root, _detector, _logger)
            : new MvcSourceScan(new List<MvcEligibleSource>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var desired = BuildDesiredState(root, scan, enabled);

        var existing = CollectExisting(root, scan, out var linkedIds);

        foreach (var wanted in desired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existing.TryGetValue(wanted.Key, out var current))
            {
                if (!IsAnaglyfinVersion(current))
                {
                    // The id is taken by something that is not ours. Overwriting it would rewrite a
                    // library item somebody chose; withholding is cheap, because the dynamic
                    // provider still offers this version.
                    _logger.LogWarning(
                        "Anaglyfin left {ItemName} ({ItemId}) alone: a profile version of {SourceName} needs that id.",
                        current.Name,
                        current.Id,
                        wanted.Value.Path);
                    result.Skipped++;
                    continue;
                }

                if (await EnsureCurrentAsync(current, wanted.Value, root, linkedIds, cancellationToken).ConfigureAwait(false))
                {
                    result.Updated++;
                }

                continue;
            }

            if (await CreateVersionItemAsync(root, wanted.Key, wanted.Value, cancellationToken).ConfigureAwait(false))
            {
                result.Created++;
            }
            else
            {
                result.Skipped++;
            }
        }

        foreach (var current in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (desired.ContainsKey(current.Key))
            {
                continue;
            }

            if (!IsAnaglyfinVersion(current.Value))
            {
                // Linked to this item for somebody else's reasons - a version an administrator
                // merged by hand is the common one. It is not ours to remove.
                continue;
            }

            // Wanted no longer: the profile was disabled, the file stopped being MVC, or the file
            // is gone. Removing it also removes its link and its persisted streams, and it cannot
            // come back: the same diff that deleted it will not want it again.
            _store.DeleteItem(current.Value);
            result.Deleted++;

            _logger.LogInformation(
                "Anaglyfin removed the version {VersionName} of {ItemName}: it is no longer a version this library asks for.",
                current.Value.Name,
                root.Name);
        }

        return result;
    }

    /// <summary>
    /// Handles a change reported for one of Anaglyfin's own version items.
    /// </summary>
    private async Task<ProfileVersionReconcileResult> ReconcileOrphanVersionAsync(Video version, CancellationToken cancellationToken)
    {
        var result = new ProfileVersionReconcileResult();

        var owner = version.PrimaryVersionId.HasValue && version.PrimaryVersionId.Value != Guid.Empty
            ? _store.FindItem(version.PrimaryVersionId.Value)
            : null;

        if (owner is Video ownerVideo && !IsAnaglyfinVersion(ownerVideo))
        {
            // A healthy version of a healthy primary: the interesting work is at the primary, and
            // asking about the version is the same question as asking about the movie.
            return await ReconcileRootAsync(ownerVideo, cancellationToken).ConfigureAwait(false);
        }

        // An Anaglyfin item with nothing it is a version of. It would list as a movie whose file is
        // a marker URL, and no pass over the library would ever look at it again, because the
        // server's general queries do not return items that name a primary. Deleting is also what
        // the server itself does to an alternate version whose file has gone.
        _store.DeleteItem(version);
        result.Deleted++;

        _logger.LogInformation(
            "Anaglyfin removed the version {VersionName}: it is not a version of anything any more.",
            version.Name);

        return result;
    }

    /// <summary>
    /// The versions one item should hold right now, keyed by the id each one must carry.
    /// </summary>
    /// <param name="root">The item whose versions are being planned.</param>
    /// <param name="scan">The files of that item a profile could convert.</param>
    /// <param name="enabled">The profiles an administrator has switched on.</param>
    /// <returns>The wanted versions, in source-then-profile order.</returns>
    private Dictionary<Guid, MediaSourceInfo> BuildDesiredState(
        Video root,
        MvcSourceScan scan,
        IReadOnlyList<StereoProfile> enabled)
    {
        var desired = new Dictionary<Guid, MediaSourceInfo>();

        if (enabled.Count == 0 || scan.Candidates.Count == 0)
        {
            return desired;
        }

        var labelSources = scan.Candidates.Count > 1;

        foreach (var source in scan.Candidates)
        {
            foreach (var profile in enabled)
            {
                var version = ProfileVersionSource.Build(root, source, profile, labelSources);
                var id = ProfileVersionSource.GetVersionItemId(version.Id);

                if (!desired.TryAdd(id, version))
                {
                    // Two files of one item folding to one version id is the identity collision the
                    // derivation exists to avoid; the first answer wins, exactly as the provider's
                    // dedup keeps the first offer.
                    _logger.LogDebug(
                        "Anaglyfin already has a {Profile} version planned for {ItemName}; the second one is not a separate version.",
                        profile.Id,
                        root.Name);
                }
            }
        }

        return desired;
    }

    /// <summary>
    /// The version items an item already has, by id.
    /// </summary>
    /// <remarks>
    /// Read two ways, because each catches what the other cannot. The links are the authoritative
    /// list of a primary's versions - and the only way to find an item whose file stopped being MVC
    /// and therefore has no id anyone can derive. The derived ids catch the opposite drift: an item
    /// that exists and is correct in every way except that nothing links to it any more, which
    /// would otherwise leave a version permanently invisible.
    /// </remarks>
    private Dictionary<Guid, BaseItem> CollectExisting(Video root, MvcSourceScan scan, out HashSet<Guid> linkedIds)
    {
        var existing = new Dictionary<Guid, BaseItem>();

        linkedIds = new HashSet<Guid>();
        foreach (var linked in _store.GetLinkedAlternateVersions(root))
        {
            if (linked is null)
            {
                continue;
            }

            existing[linked.Id] = linked;
            linkedIds.Add(linked.Id);
        }

        foreach (var source in scan.Candidates)
        {
            // Every profile the build knows, not only the enabled ones: a version item of a profile
            // that was just disabled has to be found before it can be removed.
            foreach (var profileId in _profileCatalog.AllProfileIds)
            {
                Guid id;
                try
                {
                    id = AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(source.SourceId, source.SourcePath, profileId);
                }
                catch (ArgumentException)
                {
                    // A source with no id and no path has no derivable versions at all; the links
                    // above still catch anything left over from when it did.
                    continue;
                }

                if (existing.ContainsKey(id))
                {
                    continue;
                }

                var item = _store.FindItem(id);
                if (item is not null)
                {
                    existing[id] = item;
                }
            }
        }

        return existing;
    }

    /// <summary>
    /// Creates one version item, its streams and its link.
    /// </summary>
    private async Task<bool> CreateVersionItemAsync(Video root, Guid versionId, MediaSourceInfo version, CancellationToken cancellationToken)
    {
        try
        {
            var item = BuildVersionItem(root, versionId, version);

            _store.CreateItem(item, _store.GetFolder(root));

            // Streams before the link: the link is what makes the item visible as a source, and a
            // source that appears before its tracks exist is a version a client can select and
            // cannot play.
            _store.SaveMediaStreams(versionId, version.MediaStreams, cancellationToken);

            _store.LinkAlternateVersion(root.Id, versionId);

            _logger.LogInformation(
                "Anaglyfin added the version {VersionName} to {ItemName}.",
                version.Name,
                root.Name);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Anaglyfin could not add the version {VersionName} to {ItemName}; the dynamic provider will keep offering it.",
                version.Name,
                root.Name);
            return false;
        }
    }

    /// <summary>
    /// Rewrites a version item that has stopped matching what its profile says it should be.
    /// </summary>
    /// <returns><c>true</c> when the item itself was written back.</returns>
    /// <remarks>
    /// Three independent facts are checked - the item's own fields, the streams it reports, and
    /// whether anything still links it to this primary - and only the ones that are wrong are
    /// repaired. An item that is right in all three is read and left alone, which is the common
    /// case and the reason a pass can afford to run over the whole library.
    /// </remarks>
    private async Task<bool> EnsureCurrentAsync(
        BaseItem current,
        MediaSourceInfo version,
        Video root,
        HashSet<Guid> linkedIds,
        CancellationToken cancellationToken)
    {
        var needsStreams = !ReportsSameStreams(_store.GetMediaStreams(current.Id), version.MediaStreams);
        var needsLink = !linkedIds.Contains(current.Id);

        var needsItem = !string.Equals(current.Name, version.Name, StringComparison.Ordinal)
                        || !string.Equals(current.Path, version.Path, StringComparison.Ordinal)
                        || current.RunTimeTicks != version.RunTimeTicks
                        || current.Size != version.Size
                        || current.TotalBitrate != version.Bitrate
                        || !string.Equals(current.Container ?? string.Empty, version.Container ?? string.Empty, StringComparison.Ordinal)
                        || current.ParentId != root.ParentId
                        || (current is Video currentVideo && currentVideo.PrimaryVersionId != root.Id);

        if (!needsItem && !needsStreams && !needsLink)
        {
            return false;
        }

        if (needsItem)
        {
            current.Name = version.Name;
            current.Path = version.Path;
            current.RunTimeTicks = version.RunTimeTicks;
            current.Size = version.Size;
            current.TotalBitrate = version.Bitrate;
            current.Container = version.Container ?? string.Empty;
            current.ParentId = root.ParentId;

            if (current is Video reOwnedVideo && reOwnedVideo.PrimaryVersionId != root.Id)
            {
                // It is ours and it converts this item's file, whatever it was told to belong to
                // before. Leaving the claim on another primary would leave it invisible here.
                reOwnedVideo.SetPrimaryVersionId(root.Id);
            }

            await _store.UpdateItemAsync(current, cancellationToken).ConfigureAwait(false);
        }

        if (needsStreams)
        {
            _store.SaveMediaStreams(current.Id, version.MediaStreams, cancellationToken);
        }

        if (needsLink)
        {
            _store.LinkAlternateVersion(root.Id, current.Id);
        }

        return needsItem;
    }

    /// <summary>
    /// Builds the library item one version becomes.
    /// </summary>
    /// <remarks>
    /// Filed under the primary's folder, so it is visible to exactly the users the primary is visible
    /// to and to no others; typed <see cref="Video"/> rather than a movie, so it is a picture with a
    /// profile applied to it and nothing more; and carrying the marker URL as its path, which is the
    /// same text the dynamic provider puts in <see cref="MediaSourceInfo.Path"/> and therefore the
    /// same text the FFmpeg wrapper recognises. The primary-version id is what keeps it out of browse
    /// and search - the server excludes items that name a primary from its general queries.
    /// </remarks>
    private Video BuildVersionItem(Video root, Guid versionId, MediaSourceInfo version)
    {
        var item = new Video
        {
            Id = versionId,
            Name = version.Name,
            Path = version.Path,
            ParentId = root.ParentId,
            RunTimeTicks = version.RunTimeTicks,
            Container = version.Container ?? string.Empty,
            Size = version.Size,
            TotalBitrate = version.Bitrate,
            DateCreated = root.DateCreated == default ? DateTime.UtcNow : root.DateCreated,

            // Deliberately unset, and inherited from the type: no 3D format (the server would read
            // it as an instruction to convert the picture itself), no owner (this is a linked
            // version, not a file belonging to another item), no extra type, and no local alternate
            // versions of its own.
            Video3DFormat = null
        };

        // The frame the profile encodes, on the item as well as on its stream report, so the item
        // describes the picture it can actually deliver rather than the one it was derived from.
        foreach (var stream in version.MediaStreams)
        {
            if (stream.Type == MediaStreamType.Video)
            {
                item.Width = stream.Width ?? 0;
                item.Height = stream.Height ?? 0;
                break;
            }
        }

        item.SetPrimaryVersionId(root.Id);

        return item;
    }

    /// <summary>
    /// The item that owns an item's versions.
    /// </summary>
    /// <remarks>
    /// Itself, unless it is an alternate version of something else - the MVC file beside a stack's
    /// primary, for instance. Those files are not listable, and their versions have to be attached
    /// to the item a user can reach, which is the item whose details page holds the selector.
    /// </remarks>
    private Video? ResolveRoot(Video video)
    {
        if (!video.PrimaryVersionId.HasValue || video.PrimaryVersionId.Value == Guid.Empty)
        {
            return video;
        }

        return _store.FindItem(video.PrimaryVersionId.Value) as Video;
    }

    /// <summary>
    /// Whether an item is one of Anaglyfin's version items.
    /// </summary>
    /// <remarks>
    /// Decided by the path, which is the deterministic marker stem every one of them carries and the
    /// only field a version is identified by that it cannot lose. It is also the recursion guard: an
    /// item whose path is a marker is a version, so it gets no versions of its own - the item it
    /// converts already owns them.
    /// </remarks>
    private static bool IsAnaglyfinVersion(BaseItem item)
        => item is not null && ProfileMarkerParser.IsMarkerCandidate(item.Path);

    /// <summary>
    /// Whether the streams an item already persists would report the same version as the ones it
    /// would be given.
    /// </summary>
    /// <remarks>
    /// A comparison of what a client and the transcode pipeline read out of a stream - kind, index,
    /// codec, language, and the size for the video stream - rather than of the whole object: the
    /// server fills in fields a version never sets, and comparing them would rewrite every item on
    /// every pass for differences that change nothing. The one field pair that must not drift is the
    /// codec and frame size, because they are what keeps the version on the encoding path and
    /// inside the right box.
    /// </remarks>
    private static bool ReportsSameStreams(IReadOnlyList<MediaStream> persisted, IReadOnlyList<MediaStream> expected)
    {
        if (persisted.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var wanted = expected[index];
            var have = persisted[index];

            if (have.Type != wanted.Type
                || have.Index != wanted.Index
                || !string.Equals(have.Codec ?? string.Empty, wanted.Codec ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(have.Language ?? string.Empty, wanted.Language ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }

            if (wanted.Type == MediaStreamType.Video
                && (have.Width != wanted.Width || have.Height != wanted.Height))
            {
                return false;
            }
        }

        return true;
    }
}
