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
/// also the only path where the wrapper gets to rewrite the command. Those flags are
/// this provider's intention rather than the server's decision - the server replaces them
/// with the user's permissions and its transcode path never consults them - so the
/// reported video codec is what actually keeps a version on the encoding path, through
/// <see cref="ForceTranscodeVideoStreams"/>.
/// Probing is switched off and the streams are instead copied from the item's original
/// source, so clients see the same audio and subtitle tracks they know from the
/// original, with the same indices and duration. Only the video stream is changed - a
/// clone reporting the <c>mvc</c> codec - and the item's own source keeps the objects it
/// was probed with.
/// </para>
/// </remarks>
public sealed class AnaglyfinMediaSourceProvider : IMediaSourceProvider
{
    private const string StrmExtension = ".strm";

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
    /// A lower-case "N"-format GUID string - the MD5 digest of
    /// <c>&lt;item-id-n-or-path-digest&gt;:&lt;profile-id&gt;</c> read as a GUID, which
    /// is exactly what <c>"&lt;seed&gt;".GetMD5().ToString("N")</c> yields in Jellyfin.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why a GUID.</b> The id is not an Anaglyfin-flavoured label, it is a GUID, and
    /// that is a hard requirement of the playback path rather than a taste for
    /// consistency. Jellyfin 12's DynamicHLS endpoints run the request's
    /// <c>MediaSourceId</c> through <c>Guid.Parse</c> on paths a client cannot avoid:
    /// the master playlist parses it whenever trickplay is on, which it is by default
    /// because the web client never sends the flag, and the main playlist parses it
    /// unconditionally. A non-GUID id therefore dies with a <see cref="FormatException"/>
    /// before a single FFmpeg process starts - no version of any kind plays. Lower-case
    /// "N" format is also the only shape that satisfies both string comparisons the
    /// server performs on it: the ordinal (case sensitive) match that resolves a
    /// streaming request and the case-insensitive match PlaybackInfo uses.
    /// </para>
    /// <para>
    /// <b>Why derived.</b> Clients pin resume position and version choice on this string
    /// and the server re-resolves the source by it at stream time, so it must be stable
    /// across requests, restarts and re-scans: it is a pure function of the item
    /// identity and the profile id - never random, never persisted, never cached. A real
    /// item id survives renames and library re-scans, so it is the identity of choice;
    /// only items without one (none of the library items this provider answers for) fall
    /// back to a digest of the path. Deriving instead of returning <c>item.Id</c> is what
    /// keeps the item's own version addressable: the server sorts the static source
    /// keyed by the item id first, so an Anaglyfin source wearing that id would shadow
    /// the original.
    /// </para>
    /// <para>
    /// <b>Why this particular derivation.</b> Folding a key string into a GUID with MD5
    /// is what the server does for its own generated source ids and library keys, so
    /// Anaglyfin's ids are unremarkable to the endpoints that parse them. Two
    /// consequences of an id that names no real item are benign and accepted: the
    /// trickplay lookup finds no tiles for it (an Anaglyfin version simply has no
    /// preview bar), and keyframe extraction ignores it because the extractors work from
    /// the file path, which for these sources is the marker's.
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

            // Lower-cased like every other seed part: the digest itself is case-insensitive
            // hex, but the seed is one string and gets exactly one textual form.
            itemKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath))).ToLowerInvariant();
        }

        // One textual form for one source. The seed is folded to lower case before it is
        // hashed, so the id of a version is the same whether the caller spelled the
        // profile id as "SBS_Full" or as "sbs_full" - the server compares the resulting
        // ids byte for byte at stream time, and two spellings of one version must not
        // resolve to two sources.
        var seed = string.Concat(itemKey, ":", profileId.Trim().ToLowerInvariant());

        // "N": digits only, lower case, no braces and no dashes - the shape Guid.Parse
        // accepts and the shape both of the server's id comparisons expect.
        return SourceIdGuid(seed).ToString("N", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Folds an id seed into a GUID the way the server's own <c>string.GetMD5()</c> does.
    /// </summary>
    /// <param name="seed">The already-normalised id seed.</param>
    /// <returns>The GUID whose bytes are the MD5 digest of <paramref name="seed"/>.</returns>
    /// <remarks>
    /// MD5 is deliberate and is not a security decision: this digest names a playback
    /// version, it authenticates nothing, and a collision - which nobody finds by
    /// accident - would at worst make two version entries indistinguishable. It is the
    /// same choice the server makes for its own synthetic source ids and for library
    /// keys, and matching it is the entire point, because those ids are what the
    /// DynamicHLS endpoints hand to <c>Guid.Parse</c>.
    /// </remarks>
    private static Guid SourceIdGuid(string seed)
    {
#pragma warning disable CA5351 // Broken algorithm: intentional, an identity hash is not an integrity digest.
        // Encoding.Unicode (UTF-16 little endian) rather than UTF-8: the point is to land
        // on the same GUID the server's own helper produces for the same seed.
        return new Guid(MD5.HashData(Encoding.Unicode.GetBytes(seed)));
#pragma warning restore CA5351
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
        // The version's video has to arrive as an encode, and the codec it reports is the
        // only thing in its report the server's copy decision actually reads, so the
        // stream list is built through ForceTranscodeVideoStreams: the video stream cloned
        // and re-labelled, the item's own report left as it was. See that type.
        var reportedStreams = ForceTranscodeVideoStreams.WithForcedVideoCodec(
            original?.MediaStreams,
            out int videoStreamIndex);

        // MVP: markers carry no subtitle ordinal, so every version plays without
        // burned-in subtitles (the stock pipeline's subtitle selection is not applied
        // to marker sources either). The marker contract - and ProfileMarker.Create -
        // already carry the optional field; wiring a per-playback subtitle choice to
        // it, mapped through SubtitleStreamOrdinals, is the follow-up task's job.
        //
        // The video index travels with it because the server maps the video stream by
        // number (-map 0:<index>), and a number only the provider can name: the wrapper
        // has no way to tell a numeric video map from a numeric audio map out of argv.
        var marker = ProfileMarker.Create(
            profile.Id,
            sourcePath,
            subtitleOrdinal: null,
            videoStreamIndex: videoStreamIndex < 0 ? null : videoStreamIndex);

        var mediaSource = new MediaSourceInfo
        {
            // Request plumbing and nothing else: the DynamicHLS routes parse this one as a
            // GUID (see BuildMediaSourceId), while which conversion a version asks for
            // travels in the marker below - the wrapper reads that, never this.
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

            // Never null: ForceTranscodeVideoStreams answers a missing or empty report
            // with an empty list, and an explicit null here would break consumers that
            // enumerate it. This is the version's own list - the video stream in it is a
            // clone, so the item's original source keeps the objects it was probed with.
            Formats = original?.Formats ?? Array.Empty<string>(),
            MediaStreams = reportedStreams,
        };

        return mediaSource;
    }
}
