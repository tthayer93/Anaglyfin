using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
using Anaglyfin.Profiles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
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
/// <b>What is asked about is not always one file, and what is offered is not always this
/// provider's to offer.</b> Those two rules belong to
/// <see cref="MvcEligibleSourceScanner"/> and are stated there in full: this type asks the scanner
/// which of an item's files a profile could convert - so that a stacked movie whose primary file is
/// plain 1080p still offers the versions of its hidden MVC file - and it drops any version the item
/// already reports as a static source of its own, which is the case the moment a profile exists as
/// a linked alternate-version item (see <c>Anaglyfin.VersionItems.ProfileVersionItemManager</c>).
/// The server does not deduplicate static against dynamic sources, so an offer that repeats one is
/// the same version in the list twice; and the provider stays in place as the fallback for the
/// sources and items whose version items cannot be materialised.
/// </para>
/// <para>
/// <b>Discovery.</b> The server finds this type by assembly scan and activates it
/// through <c>ActivatorUtilities</c>, so its constructor dependencies - the detector,
/// the catalog, the settings source and the ambient request accessor it reads the
/// playback device from - must be resolvable by that activation: the first three are
/// registered in <see cref="PluginServiceRegistrator"/> and the accessor is the
/// server's own root-container registration. The provider itself must <em>not</em>
/// be registered (a container entry would hide it from the scan), and neither must
/// anything else that the server already owns - a plugin-side
/// <c>IHttpContextAccessor</c> entry would replace the accessor the server's request
/// pipeline feeds, which is precisely the one this type depends on.
/// </para>
/// <para>
/// <b>Which device is asking.</b> <see cref="IMediaSourceProvider"/> carries no
/// device parameter, but the server settles one onto every authenticated request:
/// the claim <c>Jellyfin-DeviceId</c> arrives inside the request's
/// <see cref="HttpContext"/>, which flows to this call through the ambient
/// <see cref="IHttpContextAccessor"/> the server registered. The provider reads that
/// one claim and hands the id to the profile catalog, so an administrator's default
/// for one exact device goes first for that device alone. Everything else falls back
/// to the global default without being treated as an error: an API-key request
/// (<c>Jellyfin-IsApiKey</c> true - its device id is the server's own system id, not
/// a client's), a request without a device claim, and the background and DLNA
/// compositions that run with no HTTP context at all. A default is a UX preference
/// about ordering; it authorises no playback, and a failure to read the context
/// costs ordering and nothing else - the resolution never throws. And because the
/// answer is per-request, it triggers nothing: the materialised version items are
/// built from the enabled profiles, not this order, so a context-derived default
/// starts no reconcile pass.
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
/// nothing is made here, explicitly: an item the scanner will not accept (see
/// <see cref="MvcEligibleSourceScanner.IsOfferableVideo"/>), a catalog that offers no profile, and
/// sources the detector does not accept all answer with an empty list. Any unexpected failure is
/// logged and answered the same way - the user always keeps the original source.
/// </para>
/// <para>
/// <b>Transcoding only.</b> A version is a conversion, so it is declared transcode-only and
/// reports its streams through <see cref="ForceTranscodeVideoStreams"/> and
/// <see cref="ProfileVideoGeometry"/>; the whole reasoning for those flags, for the copied stream
/// list and for the deliberately unset <see cref="MediaSourceInfo.Video3DFormat"/> belongs to
/// <see cref="ProfileVersionSource"/>, which builds a version for this provider and for the
/// version-item manager alike.
/// </para>
/// </remarks>
public sealed class AnaglyfinMediaSourceProvider : IMediaSourceProvider
{
    /// <summary>
    /// The claim the server settles the authenticated request's device id onto. The
    /// literal is deliberate: Jellyfin 12 keeps these claim names in its
    /// <c>Jellyfin.Api</c> assembly, which a plugin does not reference, and they have
    /// been stable across the versions this plugin targets.
    /// </summary>
    private const string DeviceIdClaimType = "Jellyfin-DeviceId";

    /// <summary>
    /// The claim that says the request authenticated with an API key rather than a
    /// client's user token.
    /// </summary>
    private const string IsApiKeyClaimType = "Jellyfin-IsApiKey";

    private static readonly MediaSourceInfo[] NoSources = Array.Empty<MediaSourceInfo>();

    private readonly IMvcSourceDetector _detector;

    private readonly IProfileCatalog _profileCatalog;

    private readonly IAnaglyfinConfigurationSource _configurationSource;

    private readonly IHttpContextAccessor _httpContextAccessor;

    private readonly ILogger<AnaglyfinMediaSourceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnaglyfinMediaSourceProvider"/> class.
    /// </summary>
    /// <param name="detector">Decides which sources may be offered 3D versions.</param>
    /// <param name="profileCatalog">The profiles to offer and their order.</param>
    /// <param name="configurationSource">The live plugin settings.</param>
    /// <param name="httpContextAccessor">
    /// The server's ambient request accessor, the seam the playback device's exact id is
    /// read from. It is registered by the server itself (<c>AddHttpContextAccessor</c>) and
    /// resolved by the <c>ActivatorUtilities</c> activation of this provider; the plugin must
    /// not register it.
    /// </param>
    /// <param name="logger">Logger for decisions and swallowed failures.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public AnaglyfinMediaSourceProvider(
        IMvcSourceDetector detector,
        IProfileCatalog profileCatalog,
        IAnaglyfinConfigurationSource configurationSource,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AnaglyfinMediaSourceProvider> logger)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _profileCatalog = profileCatalog ?? throw new ArgumentNullException(nameof(profileCatalog));
        _configurationSource = configurationSource ?? throw new ArgumentNullException(nameof(configurationSource));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
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
            itemKey = MvcEligibleSourceScanner.PathDigestKey(sourcePath);
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
    /// <paramref name="mediaSourceId"/> is not a GUID and <paramref name="sourcePath"/> cannot identify the source either.
    /// </exception>
    public static string BuildMediaSourceIdFromSource(string? mediaSourceId, string? sourcePath, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        return FoldVersionId(MvcEligibleSourceScanner.SourceIdentityKey(mediaSourceId, sourcePath), profileId);
    }

    /// <summary>
    /// Builds the library-item id of one profile version of one <em>media source</em>.
    /// </summary>
    /// <param name="mediaSourceId">The id the source is addressed by; see <see cref="BuildMediaSourceIdFromSource"/>.</param>
    /// <param name="sourcePath">The source's media path, the fallback identity.</param>
    /// <param name="profileId">The profile id of the version.</param>
    /// <returns>The GUID a profile version of this source is addressed by, in both spellings.</returns>
    /// <remarks>
    /// A static media source is keyed by the id of the item behind it, so a version that exists as
    /// a linked alternate-version item is addressed by exactly this GUID - which is the whole
    /// reason the item manager derives its keys here instead of choosing its own: the version a
    /// client picks and the item that version exists as must be one identity, or the same profile
    /// reaches a user twice under two different answers.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="profileId"/> is empty, or <paramref name="mediaSourceId"/> is not a GUID and
    /// <paramref name="sourcePath"/> cannot identify the source either.
    /// </exception>
    public static Guid BuildVersionItemIdFromSource(string? mediaSourceId, string? sourcePath, string profileId)
        => Guid.Parse(BuildMediaSourceIdFromSource(mediaSourceId, sourcePath, profileId));

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

        if (item is null || !MvcEligibleSourceScanner.IsOfferableVideo(item))
        {
            return NoSources;
        }

        // Every file the item can be played from, asked about as itself, and accepted or not
        // on its own signals - and, alongside them, the ids the item already reports as its
        // own versions. Nothing is read from settings until one of those files qualifies: an
        // item with no 3D in it - the overwhelming majority of a library - must not even pay
        // for a settings read.
        var scan = MvcEligibleSourceScanner.Scan(item, _detector, _logger);
        if (scan.Candidates.Count == 0)
        {
            _logger.LogDebug("Anaglyfin offers no versions for {ItemName}: no media source of it is 3D MVC.", item.Name);
            return NoSources;
        }

        var configuration = _configurationSource.GetConfiguration();

        // Which device is asking is a fact about the request, not about the item, and the
        // provider interface cannot carry it - so it is read from the request itself below,
        // and only now, once a source has actually qualified for a version. The exact-device
        // default goes first for that one device; every other answer - API-key auth, a
        // request with no device claim, a composition running with no HTTP context at all -
        // is null, and null leaves the global default to decide the order. Catalog order is
        // already default-first either way.
        var deviceId = ResolveRequestDeviceId();
        if (deviceId is not null)
        {
            _logger.LogDebug("Anaglyfin is ordering versions for the exact device {DeviceId}.", deviceId);
        }

        IReadOnlyList<StereoProfile> offered = _profileCatalog.GetOfferedProfiles(configuration, deviceId);
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
        var labelSources = scan.Candidates.Count > 1;

        var sources = new List<MediaSourceInfo>(scan.Candidates.Count * offered.Count);
        var takenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in scan.Candidates)
        {
            foreach (var profile in offered)
            {
                var version = ProfileVersionSource.Build(item, candidate, profile, labelSources);

                // A profile the library already carries as an item of its own is a source the
                // server publishes by itself, on this very request: the item manager materialises
                // versions under this same id, and the server does not deduplicate static against
                // dynamic sources. Offering it again would put one version in the picker twice,
                // with two entries the user cannot tell apart and only one answer the server can
                // resolve. Staying quiet here is what keeps the provider a fallback rather than a
                // duplicate - and it stays correct without a cache or a database read, because the
                // item's own source list is already in hand.
                if (scan.ReportedSourceIds.Contains(version.Id))
                {
                    _logger.LogDebug(
                        "Anaglyfin withheld the {Profile} version of {ItemName}: it already exists as a version item.",
                        profile.Id,
                        item.Name);
                    continue;
                }

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
    /// Reads the exact device id of the request this call runs inside, if it has one.
    /// </summary>
    /// <returns>
    /// The request's device claim, or <c>null</c> when the request does not identify one
    /// exact device: no ambient request at all, no authenticated user on it, no device
    /// claim (or an empty one) on that user, or an API-key request, whose device claim
    /// carries the server's own system id rather than any client's device.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Exactly one claim is read - <c>Jellyfin-DeviceId</c> - and nothing else about the
    /// request is consulted: not the client name, not the user, not the device name, and
    /// no device category (Jellyfin 12 has no such field server-side to consult). The
    /// value is the client's own self-reported id, taken verbatim, which makes it a key
    /// for ordering versions and nothing more: an entry for it decides which version that
    /// device starts on, never what it may play.
    /// </para>
    /// <para>
    /// Nothing here may throw. <c>GetMediaSources</c> answers on request threads and on
    /// background ones alike, and the whole value of the context seam is a better default
    /// order - a cost of losing it is one profile back in the list, so a context that
    /// cannot be read is answered the way a request with no device already is. The
    /// accessor itself is the only unexpected shape here, and even it is caught.
    /// </para>
    /// </remarks>
    private string? ResolveRequestDeviceId()
    {
        try
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user is null)
            {
                // No ambient request (background composition, DLNA listener, startup work),
                // or one that never reached authentication: the global default decides.
                return null;
            }

            if (IsApiKeyRequest(user))
            {
                return null;
            }

            var deviceId = user.FindFirst(DeviceIdClaimType)?.Value;
            return string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        }
        catch (Exception ex)
        {
            // Reading a claim is never worth losing the versions over; this is the same
            // degradation an empty claim already answers with, logged so a broken
            // deployment can be diagnosed from the log instead of by the missing default.
            _logger.LogDebug(ex, "Anaglyfin could not read the request device id; falling back to the global default profile.");
            return null;
        }
    }

    /// <summary>
    /// Checks whether the request authenticated with an API key.
    /// </summary>
    /// <param name="user">The authenticated user of the request.</param>
    /// <returns><c>true</c> when the server marked this request as API-key auth.</returns>
    /// <remarks>
    /// An API-key request carries a device claim - the server's own system id - and that
    /// id is no client's device: treating it as one would let a script pin the default of
    /// a device nobody is holding. The API key names no device, so the global default
    /// decides, and the claim's own spelling ("True"/"False", the invariant rendering of
    /// the server's flag) is parsed rather than string-compared against one spelling.
    /// </remarks>
    private static bool IsApiKeyRequest(ClaimsPrincipal user)
        => bool.TryParse(user.FindFirst(IsApiKeyClaimType)?.Value, out var isApiKey) && isApiKey;
}
