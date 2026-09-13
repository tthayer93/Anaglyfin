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
/// <em>media source</em> this provider answers with one source per offered profile - the
/// configured default promoted to the front, the remaining enabled profiles behind it in
/// catalog display order - each carrying a <see cref="ProfileMarker"/> instead of a bare
/// path, so the FFmpeg wrapper can tell which conversion the chosen version asks for. The
/// item's own original sources stay in the list (the server keeps them first) and stay
/// untouched.
/// </para>
/// <para>
/// <b>What is asked about is not always one file.</b> A movie assembled as a stack, or
/// carrying linked alternate versions, answers a playback-info request with one static
/// media source per version while remaining a single item whose own <c>Path</c>, <c>Name</c>
/// and <c>Video3DFormat</c> describe only the primary file. Asking the detector about the
/// item alone therefore judges every version of that movie by the primary: a folder holding
/// a plain 1080p file beside an MVC one would answer "no 3D here" and lose the MVC versions
/// nobody else offers, because the MVC child is not a listable item a client can open on its
/// own. So the provider asks about <em>each</em> file-backed static source of the item and
/// builds the versions of every eligible one from that source's own path, streams and
/// duration. The item itself is asked only when it is not already one of the sources named -
/// a source that is the item is the item, and asking twice would double the offer.
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
/// for every item. Everything it does is in-memory: one read of the item's static media
/// sources, metadata signals for the detector, one settings read, catalog lookups, and the
/// sources' already-persisted media streams. No ffprobe, no FFmpeg, no filesystem or network
/// access, and no caching - the answer is cheap enough to recompute per request, and
/// recomputing keeps it honest when an administrator changes the enabled profiles or the
/// default.
/// </para>
/// <para>
/// <b>Fail closed.</b> A provider exception is invisible to users (the server swallows
/// it) and costs all of Anaglyfin's sources for the item, so the decision to offer
/// nothing is made here, explicitly: non-video items, sources that could not be a real
/// FFmpeg input (blank, relative, <c>.strm</c> pointers, disc images, placeholder or
/// non-file sources), and sources the detector does not accept all answer with an empty
/// list. Any unexpected failure is logged and answered the same way - the user always keeps
/// the original source.
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
/// Probing is switched off and the streams are instead copied from the source the version
/// is built from, so clients see the same audio and subtitle tracks they know from the
/// original, with the same indices and duration. Only the video stream is changed - a
/// clone reporting the <c>mvc</c> codec through <see cref="ForceTranscodeVideoStreams"/> and
/// the frame size its profile encodes through <see cref="ProfileVideoGeometry"/> - and the
/// original source keeps the objects it was probed with. What a version does <em>not</em>
/// change is the source's stereo declaration: <see cref="MediaSourceInfo.Video3DFormat"/> is
/// left unset, because the server reads that field as an instruction to convert a
/// side-by-side source to 2D itself, which is the opposite of what a version that has already
/// converted the picture needs.
/// </para>
/// </remarks>
public sealed class AnaglyfinMediaSourceProvider : IMediaSourceProvider
{
    private const string StrmExtension = ".strm";

    /// <summary>
    /// The separator between a version's own file label and the profile it converts it with,
    /// used only when an item has more than one file to choose between.
    /// </summary>
    private const string SourceNameSeparator = " / ";

    private static readonly MediaSourceInfo[] NoSources = Array.Empty<MediaSourceInfo>();

    private readonly IMvcSourceDetector _detector;

    private readonly IProfileCatalog _profileCatalog;

    private readonly IAnaglyfinConfigurationSource _configurationSource;

    private readonly ILogger<AnaglyfinMediaSourceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnaglyfinMediaSourceProvider"/> class.
    /// </summary>
    /// <param name="detector">Decides which sources may be offered 3D versions.</param>
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
    /// The convenience form for the identity a caller already holds as an item id. Both
    /// this overload and <see cref="BuildMediaSourceIdFromSource"/> answer for the same
    /// question - "which version of which media source?" - and both fold a source
    /// identity into the profile id, because a version belongs to a file, not to the item
    /// the client happened to route the request under.
    /// </para>
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
    /// across requests, restarts and re-scans: it is a pure function of the source
    /// identity and the profile id - never random, never persisted, never cached. A real
    /// id survives renames and library re-scans, so it is the identity of choice; only
    /// sources without one (none of the library sources this provider answers for) fall
    /// back to a digest of the path. Deriving instead of returning the id the source
    /// already wears is what keeps that source addressable: the server sorts the static
    /// source keyed by an item's own id first, so an Anaglyfin version wearing it would
    /// shadow the original.
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
            itemKey = PathDigestKey(sourcePath);
        }

        return FoldVersionId(itemKey, profileId);
    }

    /// <summary>
    /// Builds the id of one profile version of one <em>media source</em>.
    /// </summary>
    /// <param name="mediaSourceId">
    /// The id the source is addressed by. A static source is keyed by the id of the item that
    /// file belongs to - the item's own id for its own source, a hidden alternate version's id
    /// for a stacked one - so a parseable GUID here is the strongest identity a version can be
    /// given. Anything else (blank, or an id from a source implementation that names no GUID)
    /// falls back to the path.
    /// </param>
    /// <param name="sourcePath">The source's media path, the fallback identity.</param>
    /// <param name="profileId">The profile id of the version.</param>
    /// <returns>A lower-case "N"-format GUID string; see <see cref="BuildMediaSourceId"/>.</returns>
    /// <remarks>
    /// <para>
    /// Why the source and not the root item carries the identity: an item assembled from a
    /// stack has one id and several files, so a version id folded only from the item's id
    /// would give the full-SBS version of its 1080p file and the full-SBS version of its MVC
    /// file the same id. Two sources under one id is one source the server can resolve, and
    /// which one it resolves to is decided by list order rather than by the user's choice -
    /// the one failure mode an id is not allowed to have. Folding the <em>source's</em> id
    /// instead makes every version of every file of the item separately addressable.
    /// </para>
    /// <para>
    /// The GUID that a static source is keyed by is a real library item id, so the same
    /// warning as for <see cref="BuildMediaSourceId"/> applies and is satisfied the same way:
    /// the folded id is not that id, and cannot collide with the static source it was derived
    /// from - which is what keeps the original file of an alternate version playable.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="profileId"/> is empty, or <paramref name="mediaSourceId"/> is not a
    /// GUID and <paramref name="sourcePath"/> cannot identify the source either.
    /// </exception>
    public static string BuildMediaSourceIdFromSource(string? mediaSourceId, string? sourcePath, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var sourceKey = TryParseSourceKey(mediaSourceId, out var parsed)
            ? parsed
            : PathDigestKey(sourcePath);

        return FoldVersionId(sourceKey, profileId);
    }

    /// <summary>
    /// Reads the id seed a source contributes, when its id is one.
    /// </summary>
    /// <param name="mediaSourceId">The id the source is addressed by.</param>
    /// <param name="sourceKey">The lower-case "N"-format key, when the id parsed.</param>
    /// <returns>Whether the source id can serve as the identity of a version.</returns>
    private static bool TryParseSourceKey(string? mediaSourceId, out string sourceKey)
    {
        if (Guid.TryParse(mediaSourceId, CultureInfo.InvariantCulture, out var parsed) && parsed != Guid.Empty)
        {
            sourceKey = parsed.ToString("N", CultureInfo.InvariantCulture);
            return true;
        }

        sourceKey = string.Empty;
        return false;
    }

    /// <summary>
    /// The identity of last resort: a digest of the path the version converts.
    /// </summary>
    /// <remarks>
    /// Lower-cased like every other seed part: the digest itself is case-insensitive
    /// hex, but the seed is one string and gets exactly one textual form.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> is empty.</exception>
    private static string PathDigestKey(string? sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath))).ToLowerInvariant();
    }

    /// <summary>
    /// Folds one source identity and one profile id into the version's id.
    /// </summary>
    private static string FoldVersionId(string sourceKey, string profileId)
    {
        // One textual form for one source. The seed is folded to lower case before it is
        // hashed, so the id of a version is the same whether the caller spelled the
        // profile id as "SBS_Full" or as "sbs_full" - the server compares the resulting
        // ids byte for byte at stream time, and two spellings of one version must not
        // resolve to two sources.
        var seed = string.Concat(sourceKey, ":", profileId.Trim().ToLowerInvariant());

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

        // Every file the item can be played from, asked about as itself, and accepted or not
        // on its own signals. Nothing is read from settings until one of them qualifies: an
        // item with no 3D in it - the overwhelming majority of a library - must not even pay
        // for a settings read.
        var candidates = CollectMvcSources(item);
        if (candidates.Count == 0)
        {
            _logger.LogDebug("Anaglyfin offers no versions for {ItemName}: no media source of it is 3D MVC.", item.Name);
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

        // More than one eligible file means the user is choosing a file as well as a
        // conversion, so each version says which file it converts. With one file there is
        // nothing to disambiguate and the profile's own name is the whole label.
        var labelSources = candidates.Count > 1;

        var sources = new List<MediaSourceInfo>(candidates.Count * offered.Count);
        var takenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            foreach (var profile in offered)
            {
                var version = CreateVersion(candidate, profile, labelSources);

                // Two sources of one item naming one version is one version the server can
                // resolve and one the client cannot reach, so the id decides: the second
                // answer is dropped rather than added beside the first.
                if (takenIds.Add(version.Id))
                {
                    sources.Add(version);
                }
                else
                {
                    _logger.LogDebug(
                        "Anaglyfin skipped a duplicate {Profile} version of {ItemName}.",
                        profile.Id,
                        item.Name);
                }
            }
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
    /// Asks the item and each of its own media sources whether it is 3D MVC, and keeps the
    /// ones that answer yes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The item is asked only when none of its sources is the item's own file. A stacked
    /// movie is one item over several files and the request is addressed to that item, so
    /// both the item and its sources describe the same playability - but not the same file.
    /// Asking both whenever a list happens to be available would answer a plain single-file
    /// MVC movie twice with the same four versions, which the server would present as eight
    /// versions of one file; asking it never would strand a source implementation that names
    /// no static sources at all. Asking it exactly when its own file is absent from the list
    /// is what covers both without ever answering twice.
    /// </para>
    /// <para>
    /// <b>The item is the witness for its own file, and only for that one.</b> A source is a
    /// report about a file, so the item's tags and display name - the signals a user or a
    /// metadata provider gave the file, which no media source repeats - travel with the source
    /// that is the item and with no other. A sibling version is judged by the file it names, the
    /// label the server read off that file, and the stereo format recorded for it: nothing that
    /// belongs to a neighbour.
    /// </para>
    /// <para>
    /// Sources are read through the item's own media-source API with path substitution off,
    /// because the wrapper needs server-side paths, and are kept in the order the server
    /// gave them (its own source first). The item's linked alternate versions arrive through
    /// that same API, which is why the provider never has to enumerate a folder or open a
    /// file to see them - and why the hidden MVC child of a stack, which no client can list
    /// as an item, is still asked.
    /// </para>
    /// </remarks>
    private List<VersionCandidate> CollectMvcSources(BaseItem item)
    {
        var staticSources = ReadStaticMediaSources(item);

        var candidates = new List<VersionCandidate>();
        var namesTheItem = false;

        foreach (var source in staticSources)
        {
            if (!IsTranscodableSource(source))
            {
                continue;
            }

            // A source keyed by the item's own id (or path) is the item's own file, and the item
            // is the richer witness for it: it carries the tags and the display name a user or
            // metadata manager gave that file, which no media source repeats. Any other source is
            // a different file and may not be credited with those signals - which is the whole
            // reason the enumeration exists.
            var namesTheItemSelf = NamesTheItem(item, source);
            namesTheItem |= namesTheItemSelf;

            var candidate = namesTheItemSelf
                ? MvcSourceCandidate.FromItem(item)
                : MvcSourceCandidate.FromMediaSource(source);

            var decision = _detector.Detect(candidate);
            if (decision.IsEligible)
            {
                candidates.Add(new VersionCandidate(item, source));
            }
            else
            {
                _logger.LogDebug(
                    "Anaglyfin offers no versions for the media source {SourceName} of {ItemName}: {Decision}.",
                    source.Name,
                    item.Name,
                    decision);
            }
        }

        if (!namesTheItem)
        {
            // The item's own file was not among the sources, so the item itself is the only
            // witness it has. This is also the whole answer for an item that reports no static
            // sources at all: versions are still built, from the item's own duration and
            // without stream lists - fidelity drops, nothing else changes.
            if (_detector.Detect(MvcSourceCandidate.FromItem(item)).IsEligible)
            {
                candidates.Add(new VersionCandidate(item, original: null));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Reads the item's static media sources, or nothing when they cannot be enumerated.
    /// </summary>
    /// <remarks>
    /// Never throws: an item that cannot answer for its own sources (a source implementation
    /// that fails, an item mid-refresh) is answered by the item alone rather than costing the
    /// item its versions - the same degradation an empty list produces.
    /// </remarks>
    private IReadOnlyList<MediaSourceInfo> ReadStaticMediaSources(BaseItem item)
    {
        try
        {
            return ((IHasMediaSources)item).GetMediaSources(enablePathSubstitution: false)
                   ?? Array.Empty<MediaSourceInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Anaglyfin could not enumerate the static media sources of {ItemName}; asking the item itself.", item.Name);
            return Array.Empty<MediaSourceInfo>();
        }
    }

    /// <summary>
    /// Checks that a media source is a file the marker could name and FFmpeg could open.
    /// </summary>
    /// <remarks>
    /// The same refusals as <see cref="IsOfferableVideo"/>, applied per file: a placeholder
    /// (a source the server lists but has no path for yet), a source served over a protocol
    /// the marker contract does not cover, a relative path, a <c>.strm</c> pointer, or a disc
    /// image. Each would produce a version that reaches the transcode pipeline and fails
    /// there.
    /// <para>
    /// A <see cref="MediaSourceType.Grouping"/> source passes, deliberately: grouping says the
    /// version was merged onto this item by hand rather than discovered beside it, and a
    /// hand-merged MVC file is exactly as convertible as a discovered one. A
    /// <see cref="MediaSourceType.Placeholder"/> source is the opposite case - the server knows
    /// the version exists and has nothing playable about it yet - so that one is refused.
    /// </para>
    /// </remarks>
    private static bool IsTranscodableSource(MediaSourceInfo? source)
    {
        if (source is null || source.Type == MediaSourceType.Placeholder)
        {
            return false;
        }

        if (source.Protocol != MediaProtocol.File || source.IsRemote)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(source.Path)
            || !Path.IsPathRooted(source.Path)
            || source.Path.EndsWith(StrmExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A source that names no video type is a hand-built or older report rather than a
        // disc: only a declared non-file video type is a refusal this provider can act on.
        if (source.VideoType is not null && source.VideoType != VideoType.VideoFile)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Decides whether a source is the item's own file rather than one of its other versions.
    /// </summary>
    /// <remarks>
    /// Id first, because that is how the server keys and sorts a static source (its own
    /// source is the one carrying the item's id in "N" format). The path is the fallback for a
    /// source implementation that keys itself otherwise, because a file is what the question
    /// "is this the same file?" is actually about. The comparison against the item's path is
    /// ordinal-ignore-case to match the server's own case-insensitive id comparison: paths on
    /// the server's own filesystems are the case they were stored in.
    /// </remarks>
    private static bool NamesTheItem(BaseItem item, MediaSourceInfo source)
    {
        if (!string.IsNullOrEmpty(source.Id)
            && string.Equals(source.Id, item.Id.ToString("N", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(source.Path, item.Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds one profile version of one media source.
    /// </summary>
    private static MediaSourceInfo CreateVersion(VersionCandidate candidate, StereoProfile profile, bool labelSource)
    {
        var item = candidate.Item;
        var original = candidate.Original;
        var sourcePath = candidate.SourcePath;

        // The version's video has to arrive as an encode, and the codec it reports is the
        // only thing in its report the server's copy decision actually reads, so the
        // stream list is built through ForceTranscodeVideoStreams: the video stream cloned
        // and re-labelled, the source's own report left as it was. See that type.
        var reportedStreams = ForceTranscodeVideoStreams.WithForcedVideoCodec(
            original?.MediaStreams,
            out int videoStreamIndex);

        // A version also converts the picture, so the frame it encodes is its own and not the
        // file's. The size the clone reports is what that frame is, because the server sizes
        // its own scale from the numbers on this stream and the scale it writes is clamped
        // against the frame that reaches it at run time - report the source's 1920x1080 for a
        // full-SBS version whose encoder produces 3840x1080 and the server shrinks the
        // converted picture into the source's box on the way out. See ProfileVideoGeometry.
        reportedStreams = ProfileVideoGeometry.WithEncodedFrameSize(reportedStreams, profile);

        // MVP: markers carry no subtitle ordinal, so every version plays without
        // burned-in subtitles (the stock pipeline's subtitle selection is not applied
        // to marker sources either). The marker contract - and ProfileMarker.Create -
        // already carry the optional field; wiring a per-playback subtitle choice to
        // it, mapped through SubtitleStreamOrdinals, is the follow-up task's job.
        //
        // The video index travels with it because the server maps the video stream by number
        // (-map 0:<index>, the stream's own MediaStream.Index) and that number is something
        // only the provider can name: the wrapper has no way to tell a numeric video map from
        // a numeric audio map out of argv, and no way to know that the list it was handed
        // skips a stream the file has.
        var marker = ProfileMarker.Create(
            profile.Id,
            sourcePath,
            subtitleOrdinal: null,
            videoStreamIndex: videoStreamIndex < 0 ? null : videoStreamIndex);

        var mediaSource = new MediaSourceInfo
        {
            // Request plumbing and nothing else: the DynamicHLS routes parse this one as a
            // GUID (see BuildMediaSourceId), while which conversion a version asks for
            // travels in the marker below - the wrapper reads that, never this. Folded from
            // the source's own identity, so two files of one item never share a version id.
            Id = BuildMediaSourceIdFromSource(candidate.SourceId, sourcePath, profile.Id),

            // The version label clients show verbatim in their version pickers.
            Name = VersionName(candidate, profile, labelSource),

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
            // source this version is built from is authoritative.
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

            // Duration and container fidelity to the source this version converts: the same
            // film frame for frame, only re-rendered, so resume, seeking and the details page
            // behave across versions exactly as within one version.
            RunTimeTicks = original?.RunTimeTicks ?? item.RunTimeTicks,
            Container = original?.Container,
            Size = original?.Size,
            Bitrate = original?.Bitrate,

            // Written as unset rather than simply left out, because this one is a decision and
            // not an oversight. The server reads a source's stereo format as an instruction to
            // convert that format to 2D itself: a request carrying fixed dimensions gets a
            // crop-and-setsar chain for HalfSideBySide/FullSideBySide/TopAndBottom straight
            // from GetFixedSwScaleFilter. A version has already produced the picture it is
            // selling, so wearing the source's own MVC marker (or worse, SideBySide) would have
            // the server undo part of the conversion the profile just paid for. Fidelity to the
            // source stops at the fields that describe the media, and this one describes a
            // conversion.
            Video3DFormat = null,

            // Never null: ForceTranscodeVideoStreams answers a missing or empty report
            // with an empty list, and an explicit null here would break consumers that
            // enumerate it. This is the version's own list - the video stream in it is a
            // clone, so the source this version converts keeps the objects it was probed
            // with.
            Formats = original?.Formats ?? Array.Empty<string>(),
            MediaStreams = reportedStreams,
        };

        return mediaSource;
    }

    /// <summary>
    /// Labels one version for a client's version picker.
    /// </summary>
    /// <remarks>
    /// With one eligible file the profile's display name is the whole label, exactly as it
    /// has always been: "3D Full Side-by-Side" says everything there is to say when there is
    /// one thing to convert. With several, the label names the file first ("3D mvc / 3D Full
    /// Side-by-Side"), because two identical labels over two different originals are a
    /// version picker the user cannot choose from - and the answer they choose is what gets
    /// encoded.
    /// </remarks>
    private static string VersionName(VersionCandidate candidate, StereoProfile profile, bool labelSource)
    {
        if (!labelSource)
        {
            return profile.DisplayName;
        }

        var sourceName = candidate.SourceName;
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            // A source with no name of its own still has a file, and the file name is the
            // label a user recognises from their own library.
            sourceName = Path.GetFileNameWithoutExtension(candidate.SourcePath);
        }

        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return profile.DisplayName;
        }

        return string.Concat(sourceName.Trim(), SourceNameSeparator, profile.DisplayName);
    }

    /// <summary>
    /// One accepted media source of one item, with what a version built from it needs.
    /// </summary>
    /// <remarks>
    /// A private carrier, not a public contract: it exists so the eligibility decision, the
    /// fidelity template and the identity of one file travel together to
    /// <see cref="CreateVersion"/> without any of them being re-derived - and so a version
    /// cannot be built from the path of one source and the streams of another, which is the
    /// mistake a stacked movie makes easy.
    /// </remarks>
    private sealed class VersionCandidate
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="VersionCandidate"/> class.
        /// </summary>
        /// <param name="item">The item the request was addressed to.</param>
        /// <param name="original">
        /// The source this version is built from, or <c>null</c> when only the item could be
        /// asked about it and there is no source report to copy.
        /// </param>
        public VersionCandidate(BaseItem item, MediaSourceInfo? original)
        {
            Item = item;
            Original = original;

            // Path and identity come from the source when there is one and from the item when
            // there is not; never from both at once.
            SourcePath = original?.Path ?? item.Path;
            SourceId = original?.Id ?? item.Id.ToString("N", CultureInfo.InvariantCulture);
            SourceName = original?.Name ?? item.Name;
        }

        /// <summary>Gets the item the playback request was addressed to.</summary>
        public BaseItem Item { get; }

        /// <summary>Gets the source this version converts, or <c>null</c> for the item alone.</summary>
        public MediaSourceInfo? Original { get; }

        /// <summary>Gets the file the version's marker will name.</summary>
        public string SourcePath { get; }

        /// <summary>Gets the id the source is addressed by, the seed of the version's id.</summary>
        public string SourceId { get; }

        /// <summary>Gets the label the source (or the item, alone) carries.</summary>
        public string? SourceName { get; }
    }
}
