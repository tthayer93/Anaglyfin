using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Detection;
using Anaglyfin.Profiles;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Anaglyfin.MediaSources;

/// <summary>
/// Wraps the server's media source manager and drops the raw 3D MVC file from the two lists a
/// user picks a version from, while the <c>Offer original 3D MVC version</c> setting is off.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this boundary.</b> A 3D MVC file the scanner filed beside a movie reaches a client twice
/// over: as the hidden alternate-version item of that movie, and - because the server turns every
/// such item into a static media source of it - as an entry in the version list. Both surfaces a
/// user chooses from converge on two functions of one service: the details page asks the media
/// source manager for an item's static sources, and PlaybackInfo asks it for the playback sources,
/// which are those same static sources plus whatever the providers add. Filtering there is the
/// whole feature, because nothing below it is touched: the library item, its links, its paths, its
/// resume state and the item's own enumeration of its sources stay exactly as the scanner left
/// them. That is what makes the setting reversible - tick it back and the entry is back on the next
/// request, with no scan, no re-link and nothing re-scraped - and what keeps Anaglyfin's own
/// detection working, since the detection asks the item for its sources below this decorator
/// (see <see cref="MvcEligibleSourceScanner.Scan"/>).
/// </para>
/// <para>
/// <b>What is hidden.</b> One kind of entry, and only while the setting is off: a source that is a
/// file this plugin could convert (<see cref="MvcEligibleSourceScanner.IsTranscodableSource"/>),
/// that the detector reads as 3D MVC, that is keyed by a library item other than the one being
/// asked about, and whose file has a converted version offered for it. Everything else is returned
/// as the server produced it - same objects, same order.
/// </para>
/// <para>
/// <b>What is never hidden.</b>
/// <list type="bullet">
///   <item><description>
///     Anything that is not a file on the server's disk. That is where an Anaglyfin version is
///     safe by construction rather than by special case: a version's path is a marker URL behind
///     the HTTP protocol, so the one gate that refuses a remote source refuses it - and the
///     scanner refuses it for the very same reason, which is what stops a version of a version.
///     A hand merged version, which the scanner accepts as an input, is accepted here too: a merged
///     MVC file is exactly as raw as a discovered one.
///   </description></item>
///   <item><description>
///     The requested item's own source. A single-file MVC movie is asked about as itself and its
///     own source is keyed by its own id; hiding that would leave an item with no playable entry at
///     all. The id decides, never the path: a source keyed by another item is a sibling version even
///     when the two name one file, and a source keyed by this item is the item itself.
///   </description></item>
///   <item><description>
///     The raw file of a movie that has no converted version to offer instead - no enabled profile,
///     or a file no enabled profile would convert. Hiding it would take away a way of watching the
///     film and add none.
///   </description></item>
///   <item><description>
///     Anything this decorator cannot answer for: a source with no parsable id, a detector that
///     throws, a settings read that fails. Every failure path ends at the list the server built.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Cost.</b> Statelessness is the point. One settings read decides the whole feature, and the
/// checked answer - what every installation ships with - returns the server's list by reference
/// without looking at a single source. What happens past that gate is bounded by the setting being
/// off and the item actually carrying a convertible file, and it is the same in-memory enumeration
/// and detection the media source provider already performs per playback request. Nothing here
/// probes a file, starts a process, reads the database or caches a decision, which is what makes a
/// saved setting visible to the next request rather than to the next restart.
/// </para>
/// <para>
/// <b>Ownership.</b> Registering this decorator after the server's own registration is what makes
/// the container hand this one out instead (see <c>Anaglyfin.PluginServiceRegistrator</c>), and the
/// swap has one consequence: the core manager is no longer an instance the container creates, so it
/// is no longer one the container disposes either. Anaglyfin builds it from the server's own
/// descriptor and disposes it here, which is the least the swap owes the object that closes open
/// live streams on shutdown.
/// </para>
/// <para>
/// <b>Out of scope.</b> <see cref="GetMediaSource"/> resolves one source by the id a request named,
/// and is forwarded unfiltered like every other member: a client that asks for a hidden source by
/// id explicitly still reaches it. That is a source named by identity, not a version offered in a
/// list, and the two surfaces a user chooses from are the two that are filtered. The same reasoning
/// covers everything below this boundary - including Anaglyfin's own detection, which is what the
/// converted versions depend on.
/// </para>
/// </remarks>
public sealed class MediaSourceManagerSuppressionDecorator : IMediaSourceManager, IDisposable
{
    private readonly IMediaSourceManager _core;

    private readonly bool _ownsCore;

    private readonly IMvcSourceDetector _detector;

    private readonly IProfileCatalog _profileCatalog;

    private readonly IAnaglyfinConfigurationSource _configurationSource;

    private readonly ILogger<MediaSourceManagerSuppressionDecorator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSourceManagerSuppressionDecorator"/> class.
    /// </summary>
    /// <param name="core">
    /// The media source manager being wrapped - the server's own, built from the server's own
    /// registration. Every member that is not filtered is answered by this instance.
    /// </param>
    /// <param name="ownsCore">
    /// Whether this instance built <paramref name="core"/> and so has to dispose it. A manager
    /// handed over as an object somebody else created belongs to whoever created it and is left
    /// alone.
    /// </param>
    /// <param name="detector">Decides which of an item's files is 3D MVC.</param>
    /// <param name="profileCatalog">The profiles that would be offered for those files.</param>
    /// <param name="configurationSource">The live plugin settings.</param>
    /// <param name="logger">Logger for the refusals and the swallowed failures.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public MediaSourceManagerSuppressionDecorator(
        IMediaSourceManager core,
        bool ownsCore,
        IMvcSourceDetector detector,
        IProfileCatalog profileCatalog,
        IAnaglyfinConfigurationSource configurationSource,
        ILogger<MediaSourceManagerSuppressionDecorator> logger)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _ownsCore = ownsCore;
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _profileCatalog = profileCatalog ?? throw new ArgumentNullException(nameof(profileCatalog));
        _configurationSource = configurationSource ?? throw new ArgumentNullException(nameof(configurationSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ---- the two filtered reads ----------------------------------------------------------
    //
    // Everything a client can pick a version from is answered by one of these two, which is why
    // they are the only two that consult the setting. Both ask the core first and filter what it
    // answered: the core's own work - its per-user visibility pass, its resume reordering, its
    // ordering of the result, and the providers it merges into the playback answer - happens exactly
    // as it would have, and this decorator only ever takes entries out of its result.

    /// <inheritdoc />
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var sources = _core.GetStaticMediaSources(item, enablePathSubstitution, user);

        return WithoutOriginalMvcVersion(item, sources);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User? user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var sources = await _core
            .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken)
            .ConfigureAwait(false);

        return WithoutOriginalMvcVersion(item, sources);
    }

    // ---- everything else, forwarded ------------------------------------------------------
    //
    // One line per member, and no behaviour added: streams, attachments, live streams, recordings,
    // protocol and codec questions are the server's, and this decorator has no opinion to add to
    // any of them. AddParts is included rather than exempt: the server hands the media source
    // providers - Anaglyfin's among them - to whatever it resolves for this service, so the
    // providers have to reach the core manager through the decorator for the offer to exist at all.

    /// <inheritdoc />
    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        => _core.AddParts(providers);

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        => _core.GetMediaStreams(itemId);

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
        => _core.GetMediaStreams(query);

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
        => _core.GetMediaAttachments(itemId);

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
        => _core.GetMediaAttachments(query);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
        => _core.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);

    /// <inheritdoc />
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        => _core.OpenLiveStream(request, cancellationToken);

    /// <inheritdoc />
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
        LiveStreamRequest request,
        CancellationToken cancellationToken)
        => _core.OpenLiveStreamInternal(request, cancellationToken);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        => _core.GetLiveStream(id, cancellationToken);

    /// <inheritdoc />
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(
        string id,
        CancellationToken cancellationToken)
        => _core.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    /// <inheritdoc />
    public ILiveStream GetLiveStreamInfo(string id)
        => _core.GetLiveStreamInfo(id);

    /// <inheritdoc />
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId)
        => _core.GetLiveStreamInfoByUniqueId(uniqueId);

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
        ActiveRecordingInfo info,
        CancellationToken cancellationToken)
        => _core.GetRecordingStreamMediaSources(info, cancellationToken);

    /// <inheritdoc />
    public Task CloseLiveStream(string id)
        => _core.CloseLiveStream(id);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        => _core.GetLiveStreamMediaInfo(id, cancellationToken);

    /// <inheritdoc />
    public bool SupportsDirectStream(string path, MediaProtocol protocol)
        => _core.SupportsDirectStream(path, protocol);

    /// <inheritdoc />
    public MediaProtocol GetPathProtocol(string path)
        => _core.GetPathProtocol(path);

    /// <inheritdoc />
    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User? user)
        => _core.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    /// <inheritdoc />
    public Task AddMediaInfoWithProbe(
        MediaSourceInfo mediaSource,
        bool isAudio,
        string? cacheKey,
        bool addProbeDelay,
        bool isLiveStream,
        CancellationToken cancellationToken)
        => _core.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);

    /// <summary>
    /// Disposes the wrapped manager when this instance built it.
    /// </summary>
    /// <remarks>
    /// See the type remarks: the container no longer creates the core manager, so it no longer
    /// disposes it either, and the manager that closes open live streams on shutdown is precisely
    /// the thing that would quietly stop being cleaned up.
    /// </remarks>
    public void Dispose()
    {
        if (_ownsCore && _core is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Returns the server's list without the raw MVC version, or the very same list when there is
    /// nothing to drop.
    /// </summary>
    /// <param name="item">The item the sources were asked for.</param>
    /// <param name="sources">What the core manager answered.</param>
    /// <returns>
    /// <paramref name="sources"/> itself - same object, same order, same entries - unless an entry
    /// really is hidden, in which case a new list holding the survivors in the order the server put
    /// them.
    /// </returns>
    /// <remarks>
    /// Never throws. Every failure - settings that cannot be read, a detector that throws, an item
    /// that cannot enumerate its own sources - is answered the way an unchecked setting is answered:
    /// with the list the server built. A version picker that lost an entry because of a plugin bug
    /// is a worse answer than a picker that kept one.
    /// </remarks>
    private IReadOnlyList<MediaSourceInfo> WithoutOriginalMvcVersion(BaseItem item, IReadOnlyList<MediaSourceInfo>? sources)
    {
        if (sources is null || sources.Count == 0)
        {
            return sources ?? Array.Empty<MediaSourceInfo>();
        }

        try
        {
            var configuration = _configurationSource.GetConfiguration();

            // The shipped position of the setting, and the answer for every installation that has
            // never been configured: the server's list, untouched and by reference. One property
            // read is the whole cost of that case, and this is the path most requests never leave -
            // the details page of every ordinary movie in the library is asked about here too.
            if (configuration is null || configuration.OfferOriginalMVCVersion)
            {
                return sources;
            }

            // Past the gate, only items carrying a raw MVC version of some file are worth any more
            // work, and the question is answered from the list already in hand.
            var candidates = FindRawMvcCandidates(item, sources);
            if (candidates is null)
            {
                return sources;
            }

            // A profile nobody enabled converts nothing, so hiding the file would leave the user
            // with no version at all. The catalog is asked because the catalog decides the offer,
            // and the offer is exactly what this setting trades away.
            var offered = _profileCatalog.GetOfferedProfiles(configuration);
            if (offered is null || offered.Count == 0)
            {
                _logger.LogDebug(
                    "Anaglyfin kept the original MVC version of {ItemName}: no profile is offered.",
                    item.Name);
                return sources;
            }

            // And the offer has to be an offer for this file. Asking the scanner keeps one copy of
            // that question: it is the answer the provider and the version-item manager act on,
            // read from the item's own sources below this decorator.
            var convertibleFiles = ConvertibleFileKeys(item);
            if (convertibleFiles.Count == 0)
            {
                _logger.LogDebug(
                    "Anaglyfin kept the original MVC version of {ItemName}: no version is offered for any of its files.",
                    item.Name);
                return sources;
            }

            return HideRawMvcSources(sources, candidates, convertibleFiles);
        }
        catch (Exception ex)
        {
            // Both callers sit on a path a user is waiting on, and the server's alternative to an
            // exception here is the answer produced below anyway. Keeping the source is the
            // degradation that cannot be wrong.
            _logger.LogWarning(
                ex,
                "Anaglyfin could not decide whether to hide the original MVC version of {ItemName}; offering it.",
                item.Name);
            return sources;
        }
    }

    /// <summary>
    /// Collects the positions in the list of the sources that are candidate raw MVC alternate
    /// versions, or <c>null</c> when the list carries none.
    /// </summary>
    private List<int>? FindRawMvcCandidates(BaseItem item, IReadOnlyList<MediaSourceInfo> sources)
    {
        // The spelling a static source is keyed by an item with, worked out once per call. The
        // server compares these ids case insensitively and so does the scanner.
        var itemKey = item.Id.ToString("N", CultureInfo.InvariantCulture);

        List<int>? candidates = null;

        for (var index = 0; index < sources.Count; index++)
        {
            if (IsRawMvcAlternateVersion(itemKey, sources[index]))
            {
                candidates ??= new List<int>(1);
                candidates.Add(index);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Decides whether one source is a raw 3D MVC file that is a version of the item rather than
    /// the item itself.
    /// </summary>
    /// <param name="itemKey">The item's id, in the spelling a static source is keyed by.</param>
    /// <param name="source">One entry of the item's list.</param>
    /// <returns>
    /// <c>true</c> when this source is a convertible file, the detector reads it as MVC, and it is
    /// keyed by another item. Nothing is hidden on this answer alone: the caller still has to know
    /// that a converted version of this file is offered.
    /// </returns>
    private bool IsRawMvcAlternateVersion(string itemKey, MediaSourceInfo source)
    {
        // The scanner's own gate, reused rather than restated: a placeholder, a remote source, a
        // .strm pointer, a disc image, and - the case that matters here - an Anaglyfin marker
        // source are all refused as inputs, so none of them is a raw file this feature could hide.
        if (!MvcEligibleSourceScanner.IsTranscodableSource(source))
        {
            return false;
        }

        // A source the server does not key by a library item is not a version of this item that
        // this feature was ever about, and no converted version of it exists to offer instead.
        if (!MvcEligibleSourceScanner.TryParseSourceKey(source.Id, out var sourceKey))
        {
            return false;
        }

        // The item being asked about keeps its own source whoever is asking, and this is the only
        // test for it: keyed by another item is a sibling version even when both name one file, and
        // keyed by this item is the item itself even when the paths do not match.
        if (string.Equals(sourceKey, itemKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DetectsAsMvc(source);
    }

    /// <summary>
    /// Asks the detector about one source, and treats an answer that never arrived as "not MVC".
    /// </summary>
    private bool DetectsAsMvc(MediaSourceInfo source)
    {
        try
        {
            // The source is asked about as itself and never through the item it is listed under: a
            // stack root's name and tags describe its primary file, and crediting a sibling with
            // them is how the wrong file gets hidden.
            return _detector.Detect(MvcSourceCandidate.FromMediaSource(source)).IsEligible;
        }
        catch (Exception ex)
        {
            // A detector that cannot answer about one file is not evidence to hide it with. That
            // source stays, and the rest of the list is still decided.
            _logger.LogDebug(
                ex,
                "Anaglyfin could not detect the media source {SourceName}; keeping it.",
                source.Name);
            return false;
        }
    }

    /// <summary>
    /// The files of this item that an enabled profile would convert, in the identities the scanner
    /// answers under.
    /// </summary>
    private HashSet<string> ConvertibleFileKeys(BaseItem item)
    {
        var scan = MvcEligibleSourceScanner.Scan(item, _detector, _logger);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in scan.Candidates)
        {
            keys.Add(candidate.IdentityKey);
        }

        return keys;
    }

    /// <summary>
    /// Builds the list the caller gets: the server's entries, less the raw MVC files a converted
    /// version of is offered.
    /// </summary>
    /// <param name="sources">What the core manager answered.</param>
    /// <param name="candidates">The positions of the candidate raw MVC entries in it.</param>
    /// <param name="convertibleFiles">The files that have a converted version offered.</param>
    /// <returns>
    /// <paramref name="sources"/> itself when no candidate turns out to be hidden after all, and
    /// otherwise the survivors in the server's own order - entries taken out, nothing added,
    /// reordered, copied or rewritten.
    /// </returns>
    private IReadOnlyList<MediaSourceInfo> HideRawMvcSources(
        IReadOnlyList<MediaSourceInfo> sources,
        IReadOnlyList<int> candidates,
        HashSet<string> convertibleFiles)
    {
        List<int>? hidden = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var source = sources[candidates[i]];

            // Matched by the identity the scanner answered under, so "a version of this file is
            // offered" means this file - not another file of the same item.
            if (convertibleFiles.Contains(MvcEligibleSourceScanner.SourceIdentityKey(source.Id, source.Path)))
            {
                hidden ??= new List<int>(1);
                hidden.Add(candidates[i]);
            }
        }

        if (hidden is null)
        {
            return sources;
        }

        // Positions rather than objects: an entry of this list is dropped because of where the
        // server put it, so a source that names the same file twice is dropped once and the second
        // report of it survives. Identity is preserved by copying the references themselves.
        var dropped = new HashSet<int>(hidden);
        var kept = new List<MediaSourceInfo>(sources.Count - hidden.Count);

        for (var index = 0; index < sources.Count; index++)
        {
            if (!dropped.Contains(index))
            {
                kept.Add(sources[index]);
            }
        }

        _logger.LogDebug("Anaglyfin hid {Count} original MVC version(s) from the offered list.", hidden.Count);

        return kept;
    }
}
