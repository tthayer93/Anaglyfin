using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>
    /// The text between a version's rank and its human label in the sort name the item is written with.
    /// A hyphen with spaces around it, so the number and the words stay legible apart and no rank prefix
    /// can run into a label to spell another rank. See <see cref="FormatSortRank"/>.
    /// </summary>
    private const string SortRankSeparator = " - ";

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
    /// Reconciles every library video the full version-item walk can list.
    /// </summary>
    /// <param name="cancellationToken">Stops the pass between items.</param>
    /// <returns>What the pass changed.</returns>
    /// <remarks>
    /// <para>
    /// Called on start-up and whenever the settings are saved. One item failing (a database read, an
    /// item mid-refresh) is logged and skipped: the pass has no reason to abandon the rest of the
    /// library because one folder had a bad moment, and the next request for that item reconciles it
    /// again.
    /// </para>
    /// <para>
    /// <b>The pass is a walk, made cheap item by item.</b> The source population is every library
    /// video the server's general query can list, because that is the only population guaranteed to
    /// contain both the detector's vocabulary and any stale version a root still owns. The cost is
    /// kept down after that: <see cref="MvcEligibilityPrefilter.IsCheapCandidate"/> is answered from
    /// the fields of each item, and only what survives it reaches
    /// <see cref="MvcEligibleSourceScanner.Scan"/>, which is the one call in this file that reads
    /// media sources. On a shelf of ordinary movies with no links, the walk reads items and reads no
    /// media sources.
    /// </para>
    /// <para>
    /// <b>What still reaches the scan.</b> Anything the cheap question cannot refuse: an item the
    /// detector accepts from its own fields, an item that names local alternate versions, and an item
    /// whose linked versions may be Anaglyfin's own. The last one is the correctness case the prefilter
    /// must fail open on - a root that stopped looking MVC still has to be reconciled so its stale
    /// version items can disappear.
    /// </para>
    /// <para>
    /// <b>The janitor does not need an invitation.</b> One of Anaglyfin's own version items found in a
    /// candidate list is not something to scan but something to look after - it is either a healthy
    /// version of a healthy primary or an orphan that has to go - and neither answer needs a media
    /// source. It is passed through the prefilter rather than refused by it, because "ignore this item"
    /// is the only answer the prefilter is allowed to give on its own.
    /// </para>
    /// </remarks>
    public async Task<ProfileVersionReconcileResult> ReconcileLibraryAsync(CancellationToken cancellationToken)
    {
        var total = new ProfileVersionReconcileResult();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<Video> candidates;
            try
            {
                candidates = CollectPassCandidates();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anaglyfin could not list the library for a version-item pass; nothing will change this time.");
                return total;
            }

            _logger.LogDebug(
                "Anaglyfin is reconciling profile version items across {CandidateCount} library videos.",
                candidates.Count);

            var scanned = 0;
            var scansSkipped = 0;
            var reconciled = new HashSet<Guid>();

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (IsAnaglyfinVersion(candidate))
                    {
                        // One of ours in a candidate list: nothing to scan and nothing to plan, but
                        // something to look after, and that answer is an item read and a decision -
                        // never a media source.
                        total.Add(await ReconcileOrphanVersionAsync(candidate, cancellationToken).ConfigureAwait(false));
                        continue;
                    }

                    if (!MvcEligibilityPrefilter.IsCheapCandidate(candidate, _detector))
                    {
                        scansSkipped++;
                        continue;
                    }

                    // The item the walk reached is not always the item the versions belong to: an
                    // alternate version of something else - the MVC file beside a stack's primary -
                    // carries the signals the detector can read, and its versions have to be filed
                    // where a client can reach them. Nothing to reach is nothing to reconcile: an
                    // alternate whose primary is gone is left to the pass over the file that was
                    // removed, which takes its versions with it.
                    var root = ResolveRoot(candidate);
                    if (root is null || !reconciled.Add(root.Id))
                    {
                        scansSkipped++;
                        continue;
                    }

                    scanned++;
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
                        candidate?.Name);
                    total.Skipped++;
                }
            }

            // The two numbers that say what a pass cost, in one line per pass and with no title in
            // either of them: the walk reached these items, the cheap question refused these of them
            // outright, and only the rest were read as media. A library that stopped costing scans is
            // visible here; a library that did not is, too.
            _logger.LogDebug(
                "Anaglyfin's version-item pass refused {Skipped} of its candidates without reading a media source and scanned {Scanned} of them.",
                scansSkipped,
                scanned);
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
    /// The items a pass has to look at, from the full library walk.
    /// </summary>
    /// <returns>The candidates in store order, each item named once.</returns>
    /// <remarks>
    /// One authoritative query, not a guess at which items the detector would understand. The server's
    /// general query excludes items that name a primary version, which keeps Anaglyfin's own items out
    /// of browse and out of this list under normal circumstances, but it does not narrow away ordinary
    /// movies, filenames that merely contain the MVC markers, tags, or roots that still own stale
    /// versions. Those are filtered cheaply per item in the loop that consumes this list.
    /// </remarks>
    private IReadOnlyList<Video> CollectPassCandidates()
    {
        var candidates = new List<Video>();
        var seen = new HashSet<Guid>();

        AddCandidates(_store.GetVersionRootCandidates());

        return candidates;

        void AddCandidates(IReadOnlyList<Video> named)
        {
            foreach (var video in named)
            {
                if (video is not null && seen.Add(video.Id))
                {
                    candidates.Add(video);
                }
            }
        }
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

        // The offered order, not the enabled order. A materialised version sorts in its picker under
        // a rank this pass computes (see <see cref="BuildDesiredState"/>), and the rank has to put the
        // page's first offer - the configured default - in front, which is exactly what the catalog's
        // offered list is: the enabled profiles with the default promoted to the front. It is asked for
        // with no device, so the default promoted is the configured global one and never a single
        // client's - a version item is in the library for every client, and which of them a given
        // device should start on stays the dynamic provider's per-request answer (that is the one
        // question an item cannot ask). The offered list is the enabled set in a different order and
        // never a wider or a narrower one - the catalog resolves the default to a profile that is
        // enabled, so a device pin naming a profile nobody switched on cannot reach it here - so what
        // gets materialised is still exactly the profiles an administrator switched on, whatever any
        // one device prefers to begin with.
        var configuration = _configurationSource.GetConfiguration();
        var offered = _profileCatalog.GetOfferedProfiles(configuration) ?? new List<StereoProfile>();

        var scan = MvcEligibleSourceScanner.IsOfferableVideo(root)
            ? MvcEligibleSourceScanner.Scan(root, _detector, _logger)
            : new MvcSourceScan(new List<MvcEligibleSource>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var desired = BuildDesiredState(root, scan, offered);

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
                        wanted.Value.Source.Path);
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
    /// <param name="offered">
    /// The profiles an administrator has switched on, in offered order: the configured default first,
    /// then the rest in display order. The set is the enabled set; only its order carries information,
    /// and that order is what the rank each version is born wearing is read from.
    /// </param>
    /// <returns>The wanted versions, in source-then-offered-profile order, each carrying its rank.</returns>
    /// <remarks>
    /// A planned version is the source the client picks <em>and</em> the metadata the item behind it
    /// has to carry, because those two come from different places: the source is a fact about the
    /// file and the profile, and the metadata is a fact about the item the file belongs to - which
    /// for the hidden MVC file of a stack is not the item the versions are offered under. Reading the
    /// metadata once per file rather than once per version is deliberate: it is the same answer for
    /// every profile that file converts, and a pass over a large library has no reason to ask for it
    /// five times per movie.
    /// <para>
    /// <b>The rank.</b> Every planned version also carries the label its item is sorted under: a
    /// fixed-width running count over the whole plan, taken in source-then-offered-profile order. One
    /// count does two jobs. It groups - every version of one file sorts beside its own file, ahead of
    /// the next file's - and it promotes, because within a file the profile the page offers first (the
    /// configured default) wears the lowest rank and therefore comes out first in the server's
    /// order-by-sort-name of the version list. The width is fixed so the eleventh version still sorts
    /// numerically after the tenth, which a bare number would not; it is deliberately not a persisted
    /// setting, because the order a version sorts under is a consequence of the settings (which
    /// profile is default, which are enabled) and nothing the library has to remember on its own.
    /// A rank is spent only on a version that is actually planned, so the identity de-duplication
    /// below cannot leave a hole in the sequence.
    /// </para>
    /// </remarks>
    private Dictionary<Guid, PlannedVersion> BuildDesiredState(
        Video root,
        MvcSourceScan scan,
        IReadOnlyList<StereoProfile> offered)
    {
        var desired = new Dictionary<Guid, PlannedVersion>();

        if (offered.Count == 0 || scan.Candidates.Count == 0)
        {
            return desired;
        }

        var labelSources = scan.Candidates.Count > 1;

        // The one running rank, spent on each version as it is accepted into the plan. See the remarks.
        var rank = 0;

        foreach (var source in scan.Candidates)
        {
            // The metadata and the credits of the file, read once and shared by every profile this
            // file converts: they are answers about the file, not about the conversion, and a pass
            // over a library with five enabled profiles has no reason to ask five times.
            var metadataSource = ResolveMetadataSource(root, source);
            var metadata = VersionItemMetadata.FromSource(metadataSource);
            var credits = _store.GetPeople(metadataSource.Id);

            foreach (var profile in offered)
            {
                var version = ProfileVersionSource.Build(root, source, profile, labelSources);
                var id = ProfileVersionSource.GetVersionItemId(version.Id);

                // The rank the version wears is the next one in line, but the line only advances when
                // the version is actually taken - a fold to an id already planned spends nothing, so
                // the sequence the library settles on is contiguous whatever the catalog or the scan
                // throws at it.
                var sortName = FormatSortRank(rank + 1, version.Name);
                if (desired.TryAdd(id, new PlannedVersion(version, profile.Id, metadata, credits, sortName)))
                {
                    rank++;
                    continue;
                }

                // Two files of one item folding to one version id is the identity collision the
                // derivation exists to avoid; the first answer wins, exactly as the provider's
                // dedup keeps the first offer.
                _logger.LogDebug(
                    "Anaglyfin already has a {Profile} version planned for {ItemName}; the second one is not a separate version.",
                    profile.Id,
                    root.Name);
            }
        }

        return desired;
    }

    /// <summary>
    /// The item whose metadata a version of one file carries.
    /// </summary>
    /// <param name="root">The item the versions are offered under.</param>
    /// <param name="source">The file they convert.</param>
    /// <returns>
    /// The item that owns the file, when the library still has it; the root, when it does not.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The owner, not the root, because the owner is the item somebody scraped: on a stacked movie
    /// the eligible file is the hidden MVC item beside the 1080p one, and it is that item's poster,
    /// synopsis and cast that describe what the user is about to watch. Asking the root instead
    /// would give a version the metadata of a different file of the same movie - which is the same
    /// complaint the version items were filed for, only subtler.
    /// </para>
    /// <para>
    /// The lookup goes through the store even when the answer is the root itself, because the item a
    /// pass was handed is not always the whole of an item: the library list a full pass walks is a
    /// list item, and the images are read with the item behind it. Nothing is failed over here
    /// though: an owner that cannot be read (deleted between the scan and this moment, mid-refresh)
    /// leaves the root as the only item Anaglyfin knows anything about, and a version wearing its
    /// primary's metadata beats a version that was never created.
    /// </para>
    /// </remarks>
    private BaseItem ResolveMetadataSource(Video root, MvcEligibleSource source)
        => (source.MetadataSourceItemId is Guid itemId ? _store.FindItem(itemId) : null) ?? root;

    /// <summary>
    /// One version a pass wants: the source a client picks, and what the item behind it must say.
    /// </summary>
    private sealed class PlannedVersion
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlannedVersion"/> class.
        /// </summary>
        /// <param name="source">The version as a media source.</param>
        /// <param name="profileId">
        /// The profile this version offers. Carried beside the source because the item a plan
        /// materialises into does not name its profile anywhere of its own once it exists, and a
        /// diagnostic that says which version was rewritten wants to say which profile it is for.
        /// </param>
        /// <param name="metadata">The metadata of the item whose file this version converts.</param>
        /// <param name="sourceCredits">
        /// The credits of that same item, read once for every profile the file converts. Credits are
        /// the one part of an item's metadata that is not on the item, so they travel beside the
        /// snapshot rather than inside it; each version gets its own copy of them, because a credit
        /// names the item it is credited on.
        /// </param>
        /// <param name="sortName">
        /// The label the item this plan materialises into is sorted under: a fixed-width rank that
        /// puts the page's first offer ahead of the rest of its file's versions (see
        /// <see cref="BuildDesiredState"/>). It is carried beside the source rather than derived from
        /// it because the rank is a fact about the whole plan - where this version sits among its
        /// siblings - and not about this one version, and because the plan computes it once so the
        /// create and repair paths write one and the same value.
        /// </param>
        public PlannedVersion(MediaSourceInfo source, string profileId, VersionItemMetadata metadata, IReadOnlyList<PersonInfo> sourceCredits, string sortName)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            ProfileId = profileId ?? throw new ArgumentNullException(nameof(profileId));
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            SourceCredits = sourceCredits ?? throw new ArgumentNullException(nameof(sourceCredits));
            SortName = sortName ?? throw new ArgumentNullException(nameof(sortName));
        }

        /// <summary>Gets the version as the media source a client picks.</summary>
        public MediaSourceInfo Source { get; }

        /// <summary>Gets the profile this version offers.</summary>
        public string ProfileId { get; }

        /// <summary>Gets the metadata the version item has to carry.</summary>
        public VersionItemMetadata Metadata { get; }

        /// <summary>Gets the credits the version carries, as the source item carries them.</summary>
        public IReadOnlyList<PersonInfo> SourceCredits { get; }

        /// <summary>
        /// Gets the rank label the version item is written into <c>ForcedSortName</c> and, through it,
        /// into <c>SortName</c>: the fixed-width form of the position this version holds in the plan.
        /// </summary>
        public string SortName { get; }
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
    /// Creates one version item, its metadata, its streams and its link.
    /// </summary>
    private async Task<bool> CreateVersionItemAsync(Video root, Guid versionId, PlannedVersion planned, CancellationToken cancellationToken)
    {
        var version = planned.Source;

        try
        {
            var item = BuildVersionItem(root, versionId, planned);

            _store.CreateItem(item, _store.GetFolder(root));

            // Streams before the link: the link is what makes the item visible as a source, and a
            // source that appears before its tracks exist is a version a client can select and
            // cannot play.
            _store.SaveMediaStreams(versionId, version.MediaStreams, cancellationToken);

            // Images and credits after the item: both are rows keyed by the item's id, and a row
            // naming an item that does not exist yet is a write the server refuses. Both name the
            // source item's own files and people rather than making new ones, and a brand-new item
            // has nothing of either to clear, so an empty list is not written at all.
            if (item.ImageInfos.Length > 0)
            {
                await _store.SaveImagesAsync(item, cancellationToken).ConfigureAwait(false);
            }

            var credits = VersionItemCredits.Clone(planned.SourceCredits, versionId);
            if (credits.Count > 0)
            {
                _store.SavePeople(versionId, credits);
            }

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
    /// Rewrites a version item that has stopped matching what its profile and its source say it
    /// should be.
    /// </summary>
    /// <returns><c>true</c> when anything about the version was written.</returns>
    /// <remarks>
    /// <para>
    /// Every fact about the item is checked - the streams it reports, the link that puts it in the
    /// picker, its own fields (label, marker, duration, size, bitrate, container, parent, the frame
    /// it describes, the video type it declares), the metadata it is wearing, the images it names,
    /// the credits it carries, whether it is still locked, and whether anything still links it to
    /// this primary - and only the ones that are wrong are repaired. An item that is right in all of
    /// them is read and left alone, which is the common case and the reason a pass can afford to run
    /// over the whole library.
    /// </para>
    /// <para>
    /// <b>Every comparison must answer the same way the database answers it.</b> A check that reads
    /// a difference the storage did not write - a list handed back in another order, a number the
    /// server recomputed from a file - is a rewrite on every start-up, forever, and on a library of
    /// versions that is a pass that never settles. That is why the image and credit comparisons work
    /// on sorted identities rather than on list positions (see
    /// <see cref="VersionItemMetadata.ImagesMatch"/> and <see cref="VersionItemCredits.Matches"/>),
    /// why the file-derived halves of an image row stay out of the comparison, and why every field
    /// this method compares is also a field it writes: compared-but-not-written is drift nobody
    /// repairs, and written-but-not-compared drifts unnoticed - the frame the profile encodes is
    /// compared here and written here precisely because the create path has always written it.
    /// </para>
    /// <para>
    /// <b>Why the metadata is checked at all.</b> A version's library metadata is copied, which
    /// makes it a second copy of something that has an original: the original gets refreshed by a
    /// scrape, a hand edit in the dashboard, a subtitle or artwork change, and until this comparison
    /// runs the version is wearing the previous answer. Reconciling the fields a version was born
    /// with and never noticing they stopped being the source's is the difference between a version
    /// item and a screenshot of one.
    /// </para>
    /// <para>
    /// <b>What is not repaired.</b> A name somebody changed. The version label is what the stock
    /// version picker shows, so Anaglyfin keeps its own labels current - a label left behind by a
    /// profile rename or by a file that started or stopped needing the file's name in the label is
    /// rewritten - but a name that is not one of ours came from a user, and the version's identity
    /// for that user is the label they typed, not the one the catalog would have written.
    /// </para>
    /// </remarks>
    private async Task<bool> EnsureCurrentAsync(
        BaseItem current,
        PlannedVersion planned,
        Video root,
        HashSet<Guid> linkedIds,
        CancellationToken cancellationToken)
    {
        var version = planned.Source;

        var needsStreams = !ReportsSameStreams(_store.GetMediaStreams(current.Id), version.MediaStreams);
        var needsLink = !linkedIds.Contains(current.Id);
        var needsName = NeedsName(current, version.Name);

        // The rank the version's item must be sorted under. It is compared on ForcedSortName - the
        // column this method writes and the item model persists and reads back verbatim - and never on
        // SortName. SortName is the half the server derives (and may re-derive from the label on a
        // read), which is exactly the never-settling pair cp13 was filed for; ForcedSortName round-
        // trips, so the comparison and the write below read and put the same value and the item settles
        // the pass after it is written. An item born before this rank existed answers no forced sort
        // name at all, so its first pass after the upgrade is the pass that gives it one.
        var needsSortRank = !string.Equals(current.ForcedSortName, planned.SortName, StringComparison.Ordinal);

        // The metadata is asked once, and for the field it is wrong on rather than the bare fact that
        // it is wrong somewhere: the same walk answers "does anything differ" and "which one", and the
        // reason below can name the field a hunt would otherwise have to find by hand.
        var metadataDrift = planned.Metadata.FieldDrift(current);
        var needsMetadata = metadataDrift is not null;
        var needsImages = !planned.Metadata.ImagesMatch(current);

        // The lock is part of what the item is, not of what it copied: an unlocked item whose path
        // is a marker URL is an item the server will keep trying to scrape and probe.
        var needsLock = !current.IsLocked;

        // The frame the profile encodes is written onto the item when it is created, so it has to be
        // compared here too: an item describing a picture its stream report does not report - or
        // the other way round - is a version that promises one frame and delivers another, and a
        // drift no pass ever repairs is a drift that outlives every repair around it.
        var currentVideo = current as Video;
        var frame = PlannedFrame(version);
        var needsFrame = frame.HasValue
                         && currentVideo is not null
                         && (currentVideo.Width != frame.Value.Width
                             || currentVideo.Height != frame.Value.Height);

        // The video type is part of what the item is: it is the answer the item gives about the media
        // behind its marker, and code on both sides reads it - the server lists a version as the type
        // it claims and orders its sources by it, and this plugin's own scanner refuses an item that
        // names a disc type. A version item that drifted onto Iso, Dvd or BluRay is a version that
        // lies about itself in the picker and loses its encoder at the gate, and drift is exactly what
        // a reconciliation pass is for: a resolver that guessed from a path, a hand edit, an item
        // merged in from another library. The field round-trips through item persistence as the plain
        // enum value it is, so a repaired item is read back repaired and the next pass leaves it alone.
        var needsVideoType = currentVideo is not null
                             && currentVideo.VideoType != ProfileVersionSource.VersionVideoType;

        // The credits are compared against the source's, read once for this file, and written as
        // their own copy: a credit names the item it is credited on, so the version cannot share the
        // primary's rows, and neither can two versions of one file share one list with each other.
        var wantedCredits = VersionItemCredits.Clone(planned.SourceCredits, current.Id);
        var needsCredits = !VersionItemCredits.Matches(_store.GetPeople(current.Id), wantedCredits);

        var needsPath = !string.Equals(current.Path, version.Path, StringComparison.Ordinal);
        var needsDuration = current.RunTimeTicks != version.RunTimeTicks;
        var needsSize = current.Size != version.Size;
        var needsBitrate = current.TotalBitrate != version.Bitrate;
        var needsContainer = !string.Equals(current.Container ?? string.Empty, version.Container ?? string.Empty, StringComparison.Ordinal);
        var needsParent = current.ParentId != root.ParentId;
        var needsPrimary = currentVideo is not null && currentVideo.PrimaryVersionId != root.Id;

        var needsItem = needsName
                        || needsSortRank
                        || needsPath
                        || needsDuration
                        || needsSize
                        || needsBitrate
                        || needsContainer
                        || needsParent
                        || needsMetadata
                        || needsImages
                        || needsFrame
                        || needsVideoType
                        || needsLock
                        || needsPrimary;

        if (!needsItem && !needsStreams && !needsLink && !needsCredits)
        {
            return false;
        }

        // The counts in the pass summary say a version was rewritten; on a library that logs one on
        // every start-up they are also the whole story, which is not enough to find out why. The
        // reasons name the field, not the values: an item id, a profile id and a list of short
        // field names carry nothing a user would call private, and they are enough to point the
        // next look straight at the row that drifted.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var reasons = new List<string>();

            void AddIf(string reason, bool needed)
            {
                if (needed)
                {
                    reasons.Add(reason);
                }
            }

            AddIf("streams", needsStreams);
            AddIf("link", needsLink);
            AddIf("credits", needsCredits);
            AddIf("name", needsName);
            AddIf("sortrank", needsSortRank);
            AddIf("path", needsPath);
            AddIf("duration", needsDuration);
            AddIf("size", needsSize);
            AddIf("bitrate", needsBitrate);
            AddIf("container", needsContainer);
            AddIf("parent", needsParent);

            // Metadata is a dozen fields under one reason, so it is the one reason worth naming the
            // field on: "metadata" says a version was rewritten for something on its panel, and the
            // field says which row to go look at. A field name carries no value, so nothing a user
            // would call private reaches the log - and a settle that will not settle stops being a
            // library-wide hunt for the one field that will not round-trip.
            if (needsMetadata)
            {
                reasons.Add("metadata(" + metadataDrift + ")");
            }

            AddIf("images", needsImages);
            AddIf("frame", needsFrame);
            AddIf("videotype", needsVideoType);
            AddIf("lock", needsLock);
            AddIf("primary", needsPrimary);

            _logger.LogDebug(
                "Anaglyfin is rewriting the version item {VersionItemId} of profile {ProfileId}: {Reasons}.",
                current.Id,
                planned.ProfileId,
                string.Join(", ", reasons));
        }

        if (needsItem)
        {
            if (needsName)
            {
                current.Name = version.Name;
            }

            current.Path = version.Path;
            current.RunTimeTicks = version.RunTimeTicks;
            current.Size = version.Size;
            current.TotalBitrate = version.Bitrate;
            current.Container = version.Container ?? string.Empty;
            current.ParentId = root.ParentId;
            current.IsLocked = true;

            planned.Metadata.ApplyTo(current);

            if (needsSortRank)
            {
                // Written after the metadata copy on purpose: that copy deliberately leaves the item's
                // own sort names alone, and writing the rank here keeps that separation honest whatever
                // the copy grows to touch. The value written is exactly the one the comparison above
                // read, so a rank this pass had to repair is a rank it will not have to repair again -
                // the compare-what-you-write rule, applied to the sort column.
                ApplySortRank(current, planned.SortName);
            }

            if (needsFrame && currentVideo is not null && frame.HasValue)
            {
                // The same numbers the create path writes on the day the item is born: what the
                // version's video stream reports is what the item describes - the comparison and
                // this write read one PlannedFrame, or a settled item would never settle.
                currentVideo.Width = frame.Value.Width;
                currentVideo.Height = frame.Value.Height;
            }

            if (needsVideoType && currentVideo is not null)
            {
                // The same value the create path writes, and the same one the comparison above
                // compares: an item answering anything else is repaired once and then left alone,
                // which is the only way a claim this widely read settles.
                currentVideo.VideoType = ProfileVersionSource.VersionVideoType;
            }

            if (needsPrimary && currentVideo is not null)
            {
                // It is ours and it converts this item's file, whatever it was told to belong to
                // before. Leaving the claim on another primary would leave it invisible here.
                currentVideo.SetPrimaryVersionId(root.Id);
            }

            await _store.UpdateItemAsync(current, cancellationToken).ConfigureAwait(false);
        }

        if (needsStreams)
        {
            _store.SaveMediaStreams(current.Id, version.MediaStreams, cancellationToken);
        }

        if (needsImages)
        {
            await _store.SaveImagesAsync(current, cancellationToken).ConfigureAwait(false);
        }

        if (needsCredits)
        {
            // Written whole, not patched: the credit write replaces an item's credits, which is what
            // lets a source that lost a credit lose it here too.
            _store.SavePeople(current.Id, wantedCredits);
        }

        if (needsLink)
        {
            _store.LinkAlternateVersion(root.Id, current.Id);
        }

        return needsItem || needsStreams || needsLink || needsCredits;
    }

    /// <summary>
    /// The frame the profile encodes, in the form the version's item describes it with.
    /// </summary>
    /// <param name="version">The version as a media source.</param>
    /// <returns>
    /// The size the version's video stream reports, or <c>null</c> when the version has no video
    /// stream to name one.
    /// </returns>
    /// <remarks>
    /// The one definition of the item's geometry, read off the one place that decides it: the
    /// version's own video stream, sized by the profile that converts the picture. A stream that
    /// reports no side counts as zero, which is the number the create path has always written.
    /// </remarks>
    private static (int Width, int Height)? PlannedFrame(MediaSourceInfo version)
    {
        foreach (var stream in version.MediaStreams)
        {
            if (stream.Type == MediaStreamType.Video)
            {
                return (stream.Width ?? 0, stream.Height ?? 0);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an item's name is one Anaglyfin may write.
    /// </summary>
    /// <param name="current">The item as it stands.</param>
    /// <param name="label">The label its version is built with.</param>
    /// <returns><c>true</c> when the label should be put on the item.</returns>
    /// <remarks>
    /// A name that already is the label needs no writing whatever its provenance. Beyond that the
    /// question is whose name this is: text the label builder could have produced (this profile's
    /// label, another profile's, a label with a file name in front of it) is Anaglyfin's to keep
    /// current, and an empty name is nothing to preserve. Anything else is a name a user gave this
    /// version, and no version label outranks what somebody typed - the details panel reads
    /// <c>OriginalTitle</c> for the movie, so a renamed version still says which movie it is.
    /// </remarks>
    private bool NeedsName(BaseItem current, string label)
        => !string.Equals(current.Name, label, StringComparison.Ordinal)
           && (string.IsNullOrWhiteSpace(current.Name)
               || ProfileVersionSource.IsVersionLabel(current.Name, _profileCatalog.Profiles));

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
    /// <para>
    /// It is also born wearing its source's metadata and locked. The metadata is what the details
    /// panel of a version would otherwise be missing; the lock is what tells the server that this
    /// item has no file to scrape, probe or write metadata next to - its path is a marker URL, and
    /// an item the size of a library that a pass has to keep re-locking would be an item the server
    /// keeps trying to refresh.
    /// </para>
    /// <para>
    /// And it is born declaring the video type its version declares, so that the one claim a version
    /// makes about its own media is true of the item as much as of the source: the server builds the
    /// static version list, the item listing and the source ordering off this field, and the encoder
    /// gate downstream refuses anything that names a disc. See
    /// <see cref="ProfileVersionSource.VersionVideoType"/> for why the value is stated rather than
    /// inherited from the type's default.
    /// </para>
    /// </remarks>
    private Video BuildVersionItem(Video root, Guid versionId, PlannedVersion planned)
    {
        var version = planned.Source;

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

            // Nobody scrapes a marker. See the remarks: this is the flag that keeps the server from
            // treating the item's path as a media file it may probe and describe.
            IsLocked = true,

            // The video type the version declares for itself, written onto the item so that the
            // static media source the server builds from this item carries the same claim the dynamic
            // source makes. A materialised version is listed, ordered and gated on the item's answer
            // and not on the provider's, so the claim has to be on both.
            // See ProfileVersionSource.VersionVideoType.
            VideoType = ProfileVersionSource.VersionVideoType,

            // Deliberately unset, and inherited from the type: no 3D format (the server would read
            // it as an instruction to convert the picture itself), no owner (this is a linked
            // version, not a file belonging to another item), no extra type, and no local alternate
            // versions of its own.
            Video3DFormat = null
        };

        // The movie the version is a version of: title, synopsis, artwork and the rest, off the item
        // that owns the file this version converts. The item's own Name stays the profile label,
        // because that name is also the label the stock version picker shows.
        planned.Metadata.ApplyTo(item);

        // The frame the profile encodes, on the item as well as on its stream report, so the item
        // describes the picture it can actually deliver rather than the one it was derived from.
        // The same read <see cref="EnsureCurrentAsync"/> compares against: one definition of the
        // geometry, written and checked by the same two lines.
        var frame = PlannedFrame(version);
        if (frame.HasValue)
        {
            item.Width = frame.Value.Width;
            item.Height = frame.Value.Height;
        }

        // The rank this version was planned to sort under, written the same way the repair path writes
        // it and compared the same way it is compared (see <see cref="EnsureCurrentAsync"/>). A version
        // is born wearing its place in the picker: the server orders an item's versions by their sort
        // name, so putting the page's first offer first is a matter of the item carrying the right rank
        // from the day it exists - and not of a later pass noticing it was missing.
        ApplySortRank(item, planned.SortName);

        item.SetPrimaryVersionId(root.Id);

        return item;
    }

    /// <summary>
    /// Puts a version's rank on its item and refreshes the derived sort column from it.
    /// </summary>
    /// <param name="item">The version item to rank.</param>
    /// <param name="sortName">The rank label from <see cref="PlannedVersion.SortName"/>.</param>
    /// <remarks>
    /// <para>
    /// The rank is forced onto <c>ForcedSortName</c>, which the item model persists as its own column
    /// and reads back verbatim - the one half of the sort-name pair that round-trips, and therefore the
    /// half the settle comparison reads. <c>SortName</c>, the column the server actually orders the
    /// version list by, is then refreshed from it: the item model derives <c>SortName</c> from a forced
    /// sort name, so reading it straight back is the value the server itself would persist, and writing
    /// exactly that settles the two columns on the same text rather than leaving the ordered one to be
    /// re-derived behind this plugin's back. Neither write touches the item's <c>Name</c>, which stays
    /// the human label the version picker shows.
    /// </para>
    /// <para>
    /// This is what the cp13 lesson permits rather than forbids: the never-settling rewrite there was a
    /// copy of the <em>source item's</em> derived pair, which the version could never read back. A rank
    /// this plugin forces onto its own item is its own persisted value, read back exactly as written -
    /// compared and written by the same two lines, which is the rule, not the exception to it.
    /// </para>
    /// </remarks>
    private static void ApplySortRank(BaseItem item, string sortName)
    {
        item.ForcedSortName = sortName;

        var derivedSortName = item.SortName;
        item.SortName = derivedSortName;
    }

    /// <summary>
    /// The sort label one rank in the plan wears.
    /// </summary>
    /// <param name="rank">The 1-based position in the plan, in source-then-offered-profile order.</param>
    /// <param name="label">
    /// The version's human label (<see cref="MediaSourceInfo.Name"/>), carried behind the number so the
    /// sort column names what it ranks for anyone reading it and so a rank the human label never quite
    /// separates still breaks in a stable order.
    /// </param>
    /// <returns>The rank prefix and the label, joined.</returns>
    /// <remarks>
    /// The number is the whole of the ordering and the label is decoration behind it, so the number has
    /// to sort as a number: fixed width, zero padded, and formatted in the invariant culture so the
    /// digits are the ASCII digits an ordinal sort compares, whatever the server's locale. The width is
    /// fixed at three, which bounds one item's plan at a thousand versions - comfortably more than the
    /// profiles the catalog knows multiplied by the files a single item reports, and enough never to
    /// carry a rank into a wider spelling that would reorder it against a smaller one.
    /// </remarks>
    private static string FormatSortRank(int rank, string label)
        => string.Concat(
            rank.ToString("D3", CultureInfo.InvariantCulture),
            SortRankSeparator,
            label);

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
