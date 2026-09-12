using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
using Anaglyfin.Profiles;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Anaglyfin.MediaSources;

/// <summary>
/// Exposes the enabled Anaglyfin profiles as alternate playback versions of a 3D MVC video.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam through which a user picks a version: the server merges what a
/// provider returns into the item's <c>MediaSources</c>, which clients render as their
/// version list (the same seam Jellyfin's own Live TV runs on). For one eligible MVC
/// item this provider answers with one source per offered profile - the configured
/// default promoted to the front, the remaining enabled profiles behind it in catalog
/// display order - each carrying a <see cref="ProfileMarker"/> instead of a bare path,
/// so the FFmpeg wrapper can tell which conversion the chosen version asks for. The
/// item's own original source stays in the list (the server keeps it first) and stays
/// untouched.
/// </para>
/// <para>
/// <b>Discovery.</b> The server finds this type by assembly scan and activates it
/// through <c>ActivatorUtilities</c>, so its constructor dependencies - the detector,
/// the catalog and the settings source - must be registered in
/// <see cref="PluginServiceRegistrator"/>, while the provider itself must <em>not</em>
/// be registered (a container entry would hide it from the scan).
/// </para>
/// <para>
/// <b>Hot path.</b> <see cref="GetMediaSources"/> runs on every playback-info request
/// for every item. Everything it does is in-memory: metadata signals for the detector,
/// one settings read, catalog lookups, and the item's already-persisted media streams.
/// No ffprobe, no FFmpeg, no filesystem or network access, and no caching - the answer
/// is cheap enough to recompute per request, and recomputing keeps it honest when an
/// administrator changes the enabled profiles or the default.
/// </para>
/// <para>
/// <b>Fail closed.</b> A provider exception is invisible to users (the server swallows
/// it) and costs all of Anaglyfin's sources for the item, so the decision to offer
/// nothing is made here, explicitly: non-video items, items whose path could not be a
/// real FFmpeg input (blank, relative, <c>.strm</c> pointers, disc images), and items
/// the detector does not accept all answer with an empty list. Any unexpected failure
/// is logged and answered the same way - the user always keeps the original source.
/// </para>
/// <para>
/// <b>Transcoding only.</b> The marker is an Anaglyfin namespace, not playable media as
/// far as the server is concerned, so the versions are declared transcode-only
/// (<see cref="MediaSourceInfo.SupportsDirectPlay"/> and
/// <see cref="MediaSourceInfo.SupportsDirectStream"/> off, transcoding on), which is
/// also the only path where the wrapper gets to rewrite the command.
/// Probing is switched off and the streams are instead copied from the item's original
/// source, so clients see the same audio and subtitle tracks they know from the
/// original, with the same indices and duration.
/// </para>
/// </remarks>
public sealed class AnaglyfinMediaSourceProvider : IMediaSourceProvider
{
    /// <summary>
    /// Prefix of every Anaglyfin media source id, so one string comparison tells a
    /// plugin version apart from a library source.
    /// </summary>
    public const string MediaSourceIdPrefix = "anaglyfin:";

    private const string StrmExtension = ".strm";

    private static readonly IReadOnlyList<MediaStream> NoStreams = Array.Empty<MediaStream>();

    private static readonly MediaSourceInfo[] NoSources = Array.Empty<MediaSourceInfo>();

    private readonly IMvcSourceDetector _detector;

    private readonly IProfileCatalog _profileCatalog;

    private readonly IAnaglyfinConfigurationSource _configurationSource;

    private readonly ILogger<AnaglyfinMediaSourceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnaglyfinMediaSourceProvider"/> class.
    /// </summary>
    /// <param name="detector">Decides which items may be offered 3D versions.</param>
    /// <param name="profileCatalog">The profiles to offer and their order.</param>
    /// <param name="configurationSource">The live plugin settings.</param>
    /// <param name="logger">Logger for decisions and swallowed failures.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public AnaglyfinMediaSourceProvider(
        IMvcSourceDetector detector,
        IProfileCatalog profileCatalog,
        IAnaglyfinConfigurationSource configurationSource,
        ILogger<AnaglyfinMediaSourceProvider> logger)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _profileCatalog = profileCatalog ?? throw new ArgumentNullException(nameof(profileCatalog));
        _configurationSource = configurationSource ?? throw new ArgumentNullException(nameof(configurationSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The work is synchronous by design (nothing may block on I/O here); the task is
    /// only what the interface asks for. It never faults: every failure path ends at
    /// the empty list, because "no alternate versions" degrades to normal playback
    /// while an exception would cost the same anyway, silently.
    /// </remarks>
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            IEnumerable<MediaSourceInfo> sources = BuildMediaSources(item, cancellationToken);
            return Task.FromResult(sources);
        }
        catch (Exception ex)
        {
            // The server would swallow this exception, log it anonymously and empty
            // only this provider's contribution anyway; catching it here keeps the
            // log line attributed to Anaglyfin and the degradation explicit.
            _logger.LogError(ex, "Anaglyfin could not enumerate alternate media sources for item {ItemType}; offering no alternate versions.", item?.GetType().Name);
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(NoSources);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never called in practice and deliberately refused: none of this provider's
    /// sources sets <see cref="MediaSourceInfo.RequiresOpening"/> or carries an open
    /// token, so there is nothing to open - the server hands the marker in
    /// <see cref="MediaSourceInfo.Path"/> straight to the transcode pipeline, where
    /// the FFmpeg wrapper resolves it. Returning a faulted task rather than throwing
    /// synchronously keeps the method honest as a task-returning member.
    /// </remarks>
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        => Task.FromException<ILiveStream>(new NotSupportedException(
            "Anaglyfin alternate media sources are not opened through the provider; they are transcoded from their profile marker."));

    /// <summary>
    /// Builds the id of one profile version of one item.
    /// </summary>
    /// <param name="itemId">The item's id; its path digest is used when empty.</param>
    /// <param name="sourcePath">The item's media path, the fallback identity.</param>
    /// <param name="profileId">The profile id of the version.</param>
    /// <returns>
    /// <c>anaglyfin:&lt;item-id-or-path-hash&gt;:&lt;profile-id&gt;</c>, lower-cased.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Clients pin resume position and version choice on this string and the server
    /// re-resources the source by it at stream time, so it must be stable across
    /// requests for the same item and profile, and distinct across profiles. A real
    /// item id survives renames and library re-scans, so it is the identity of choice;
    /// only items without one (none of the library ones the provider answers for)
    /// fall back to a digest of the path.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="profileId"/> is empty, or <paramref name="itemId"/> is empty and
    /// <paramref name="sourcePath"/> cannot identify the item either.
    /// </exception>
    public static string BuildMediaSourceId(Guid itemId, string? sourcePath, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        string itemKey;
        if (itemId != Guid.Empty)
        {
            itemKey = itemId.ToString("N", CultureInfo.InvariantCulture);
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

            // Lower-cased like every other id part: the digest itself is case-insensitive
            // hex, but the id is one string and gets exactly one textual form.
            itemKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath))).ToLowerInvariant();
        }

        // One textual form for one source: ids are compared with case-insensitive
        // equality by the server, so producing lowercase removes the ambiguity rather
        // than relying on the comparison.
        return string.Concat(MediaSourceIdPrefix, itemKey, ":", profileId.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Gets the sources to offer, or the empty list when nothing qualifies.
    /// </summary>
    private IEnumerable<MediaSourceInfo> BuildMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (item is null || !IsOfferableVideo(item))
        {
            return NoSources;
        }

        // The item travels to the detector as a candidate, never as a BaseItem: the
        // eligibility rules stay a pure function of metadata signals.
        var decision = _detector.Detect(MvcSourceCandidate.FromItem(item));
        if (!decision.IsEligible)
        {
            _logger.LogDebug("Anaglyfin offers no versions for {ItemName}: {Decision}.", item.Name, decision);
            return NoSources;
        }

        var configuration = _configurationSource.GetConfiguration();

        // The provider interface carries no device or client context, so the global
        // default decides the order; per-device promotion rides on PlaybackInfo-level
        // work, not here. Catalog order is already default-first.
        IReadOnlyList<StereoProfile> offered = _profileCatalog.GetOfferedProfiles(configuration);
        if (offered is null || offered.Count == 0)
        {
            // A catalog that offers nothing (an administrator disabled everything the
            // build does not fall back from) must yield no versions, not a list of
            // invented ones.
            _logger.LogDebug("Anaglyfin offers no versions for {ItemName}: the profile catalog offered none.", item.Name);
            return NoSources;
        }

        // The item's own source supplies duration, container and streams. Its path is
        // also the only source path a marker may carry: this provider owns markers,
        // and the path they name is the library item's file, never anything a client
        // supplied.
        var original = FindOriginalMediaSource(item);
        var sourcePath = item.Path;

        var sources = new List<MediaSourceInfo>(offered.Count);
        foreach (var profile in offered)
        {
            sources.Add(CreateVersion(item, original, sourcePath, profile));
        }

        _logger.LogDebug("Anaglyfin offers {Count} versions for {ItemName}.", sources.Count, item.Name);
        return sources;
    }

    /// <summary>
    /// Checks the preconditions a real alternate version needs, before any work.
    /// </summary>
    /// <remarks>
    /// Each rejection is a case in which a marker could not name a playable input for
    /// the wrapper, so the item keeps its original source alone. The checks are static
    /// property reads on purpose: this runs for every item in every playback-info
    /// request, including the overwhelming majority that are not 3D at all.
    /// </remarks>
    private static bool IsOfferableVideo(BaseItem item)
    {
        // Only video has a 3D question to answer; the check runs before detection so a
        // music library never pays for it.
        if (item.MediaType != MediaType.Video)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return false;
        }

        // A relative path would resolve against whichever working directory FFmpeg
        // starts in; a .strm is a text pointer, not a decodable file; and disc images
        // (ISO/DVD/BluRay) reach FFmpeg through mount or bluray arguments the marker
        // contract does not cover. All would produce a version that cannot transcode.
        if (!Path.IsPathRooted(item.Path)
            || item.Path.EndsWith(StrmExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (item is Video video && video.VideoType != VideoType.VideoFile)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Finds the item's own source, the metadata template for the new versions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads the static sources through the item's own media-source API with path
    /// substitution off, because the wrapper needs server-side paths, and picks the
    /// source that is the item (items with linked alternate versions expose more than
    /// one). A null answer - an item whose media sources cannot be enumerated right
    /// now - only costs fidelity: versions are still built, from the item's own
    /// duration and without stream lists, rather than silently offering nothing.
    /// </para>
    /// <para>
    /// The streams are carried by reference, the same way the server's own static
    /// sources carry the item's persisted streams: nothing in the playback path mutates
    /// them, and consumers receive a JSON clone long before anything could.
    /// </para>
    /// </remarks>
    private static MediaSourceInfo? FindOriginalMediaSource(BaseItem item)
    {
        var sources = ((IHasMediaSources)item).GetMediaSources(enablePathSubstitution: false);
        if (sources is null || sources.Count == 0)
        {
            return null;
        }

        var itemId = item.Id.ToString("N", CultureInfo.InvariantCulture);
        MediaSourceInfo? first = null;
        foreach (var source in sources)
        {
            if (source is null)
            {
                continue;
            }

            first ??= source;

            if (string.Equals(source.Id, itemId, StringComparison.OrdinalIgnoreCase)
                && source.Type != MediaSourceType.Placeholder)
            {
                return source;
            }
        }

        return first;
    }

    /// <summary>
    /// Builds one profile version of one item.
    /// </summary>
    private static MediaSourceInfo CreateVersion(
        BaseItem item,
        MediaSourceInfo? original,
        string sourcePath,
        StereoProfile profile)
    {
        // MVP: markers carry no subtitle ordinal, so every version plays without
        // burned-in subtitles (the stock pipeline's subtitle selection is not applied
        // to marker sources either). The marker contract - and ProfileMarker.Create -
        // already carry the optional field; wiring a per-playback subtitle choice to
        // it, mapped through SubtitleStreamOrdinals, is the follow-up task's job.
        var marker = ProfileMarker.Create(profile.Id, sourcePath, subtitleOrdinal: null);

        var mediaSource = new MediaSourceInfo
        {
            Id = BuildMediaSourceId(item.Id, sourcePath, profile.Id),

            // The version label clients show verbatim in their version pickers.
            Name = profile.DisplayName,

            // The marker stands in for the path: it is what FFmpeg-mvc (via the
            // wrapper) is handed as input, and the only field of this object the
            // wrapper is allowed to read.
            Path = marker.ToMediaSourcePath(),

            // The marker is URL-shaped, and every consumer between provider and
            // wrapper treats URL-shaped inputs as HTTP; declaring anything else would
            // make the server second-guess the path's protocol.
            Protocol = MediaProtocol.Http,

            // Transcoding is where the wrapper can rewrite the command, so it is the
            // only transport these versions support. The server may still revoke the
            // transcoding flag by user permission afterwards; that is permissions
            // working, not this provider lying.
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,

            // ffprobe cannot read a marker URL, so the stream list copied from the
            // original source below is authoritative.
            SupportsProbing = false,

            // The media sits on the server's own filesystem; only its marker looks
            // like a URL, and nothing is ever fetched from it.
            IsRemote = false,

            // No open token, no live stream: the source is inert until the transcode
            // pipeline reads its marker.
            RequiresOpening = false,
            RequiresClosing = false,
            LiveStreamId = null,
            OpenToken = null,
            IsInfiniteStream = false,
            Type = MediaSourceType.Default,

            // Duration and container fidelity to the original: the same film frame
            // for frame, only re-rendered, so resume, seeking and the details page
            // behave across versions exactly as within one version.
            RunTimeTicks = original?.RunTimeTicks ?? item.RunTimeTicks,
            Container = original?.Container,
            Size = original?.Size,
            Bitrate = original?.Bitrate,

            // Never null: the property's own constructor default is an empty array,
            // and an explicit null here would break consumers that enumerate it.
            Formats = original?.Formats ?? Array.Empty<string>(),
            MediaStreams = original?.MediaStreams ?? NoStreams,
        };

        return mediaSource;
    }
}
