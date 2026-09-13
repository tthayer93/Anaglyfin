using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Anaglyfin.Detection;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Anaglyfin.MediaSources;

/// <summary>
/// Answers the question every Anaglyfin feature starts from: <em>which files of this item could a
/// profile convert?</em>
/// </summary>
/// <remarks>
/// <para>
/// This is the single copy of that question. The media source provider asks it on every
/// playback-info request, and the profile version-item manager asks it when it decides which
/// alternate-version items a library should hold, and the two must never disagree: a source the
/// provider offers and the manager does not know about is a version with no item behind it, and
/// the reverse is an item that offers nothing. Both therefore call here, and neither keeps its
/// own copy of the rules.
/// </para>
/// <para>
/// <b>The item is asked only when its sources cannot answer.</b> A movie assembled as a stack, or
/// carrying linked alternate versions, is one item over several files, and its own <c>Path</c>,
/// <c>Name</c> and <c>Video3DFormat</c> describe only the primary file. Asking the item alongside
/// its sources would judge every version of that movie by the primary - losing the MVC versions
/// nobody else offers - and would answer a plain single-file MVC movie twice with the same
/// versions. So each file-backed static source is asked about itself and accepted or refused on
/// its own signals; the item is the witness for its own file (and only for that one, because a
/// media source owns no tags), and the last resort when no source could be asked at all.
/// </para>
/// <para>
/// <b>Fail closed.</b> Anything that could not name a playable input for the FFmpeg wrapper is
/// refused here rather than turned into a version that fails later: non-video items, blank or
/// relative paths, <c>.strm</c> pointers, disc images, sources served over a protocol the marker
/// contract does not cover, placeholder sources, and sources the detector does not accept.
/// </para>
/// </remarks>
public static class MvcEligibleSourceScanner
{
    private const string StrmExtension = ".strm";

    /// <summary>
    /// Checks the preconditions a real alternate version needs, before any work.
    /// </summary>
    /// <param name="item">The item a version would be built for.</param>
    /// <returns>Whether this item can carry Anaglyfin versions at all.</returns>
    /// <remarks>
    /// Each refusal is a case in which a marker could not name a playable input for the wrapper,
    /// so the item keeps its original source alone. The checks are static property reads on
    /// purpose: they run for every item of every library, including the overwhelming majority
    /// that are not 3D at all.
    /// </remarks>
    public static bool IsOfferableVideo(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

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
        // An Anaglyfin marker URL is refused by the same rooted-path rule, which is what
        // stops a version of a version: the marker is a namespace, never an input.
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
    /// Asks the item and each of its own media sources whether it is 3D MVC.
    /// </summary>
    /// <param name="item">The item to ask.</param>
    /// <param name="detector">The rules that decide whether one candidate is MVC.</param>
    /// <param name="logger">Logger for the refusals, or <c>null</c> for a caller that has none.</param>
    /// <returns>
    /// The accepted sources, in the order the server reported them, together with the ids of every
    /// static source the item reported - which is what a caller needs to know whether a version of
    /// one of those files already exists as something the server can see.
    /// </returns>
    /// <remarks>
    /// Sources are read through the item's own media-source API with path substitution off, because
    /// the wrapper needs server-side paths. The item's linked alternate versions arrive through
    /// that same API, which is why neither caller has to enumerate a folder or open a file to see
    /// them - and why the hidden MVC child of a stack, which no client can list as an item, is
    /// still asked.
    /// <para>
    /// Never throws for an item that cannot enumerate its own sources: a source implementation that
    /// fails, or an item mid-refresh, is answered by the item alone - the same degradation an
    /// empty list produces.
    /// </para>
    /// </remarks>
    public static MvcSourceScan Scan(BaseItem item, IMvcSourceDetector detector, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(detector);

        var reportedSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var staticSources = ReadStaticMediaSources(item, logger);

        var candidates = new List<MvcEligibleSource>();
        var askedFiles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in staticSources)
        {
            if (!string.IsNullOrEmpty(source.Id))
            {
                reportedSourceIds.Add(source.Id);
            }

            if (!IsTranscodableSource(source))
            {
                continue;
            }

            var identityKey = SourceIdentityKey(source.Id, source.Path);

            // The same file listed twice - which a server is entitled to do, e.g. a version that is
            // both a local alternate version and a linked one - is one file. Asked twice it would
            // be answered twice, and the offer would carry the same conversion of the same file
            // under the same identity.
            if (!askedFiles.Add(identityKey))
            {
                continue;
            }

            // A source keyed by the item's own id (or path) is the item's own file, and the item
            // is the richer witness for it: it carries the tags and the display name a user or a
            // metadata manager gave that file, which no media source repeats. Any other source is
            // a different file and may not be credited with those signals - which is the whole
            // reason the enumeration exists.
            var candidate = NamesTheItem(item, source)
                ? MvcSourceCandidate.FromItem(item)
                : MvcSourceCandidate.FromMediaSource(source);

            var decision = detector.Detect(candidate);
            if (decision.IsEligible)
            {
                candidates.Add(new MvcEligibleSource(item, source, source.Path, source.Id, source.Name, identityKey));
            }
            else
            {
                logger?.LogDebug(
                    "Anaglyfin offers no versions for the media source {SourceName} of {ItemName}: {Decision}.",
                    source.Name,
                    item.Name,
                    decision);
            }
        }

        if (askedFiles.Count == 0)
        {
            // Not one source of this item could be asked about - no list, an empty list, or a list
            // of nothing the server has a file for yet. The item is then the only witness it has,
            // and the same one a single-file movie has always had: versions are still built, from
            // the item's own duration and without stream lists. Fidelity drops, nothing else
            // changes.
            if (detector.Detect(MvcSourceCandidate.FromItem(item)).IsEligible)
            {
                candidates.Add(new MvcEligibleSource(
                    item,
                    original: null,
                    sourcePath: item.Path,
                    sourceId: item.Id.ToString("N", CultureInfo.InvariantCulture),
                    sourceName: item.Name,
                    identityKey: SourceIdentityKey(item.Id.ToString("N", CultureInfo.InvariantCulture), item.Path)));
            }
        }

        return new MvcSourceScan(candidates, reportedSourceIds);
    }

    /// <summary>
    /// Checks that a media source is a file the marker could name and FFmpeg could open.
    /// </summary>
    /// <param name="source">The source to judge. May be <c>null</c>.</param>
    /// <returns>Whether a version could be built from this source.</returns>
    /// <remarks>
    /// The same refusals as <see cref="IsOfferableVideo"/>, applied per file: a placeholder
    /// (a source the server lists but has no path for yet), a source served over a protocol the
    /// marker contract does not cover - including an Anaglyfin marker source, which is how a
    /// profile version of a version is refused - a relative path, a <c>.strm</c> pointer, or a disc
    /// image. Each would produce a version that reaches the transcode pipeline and fails there.
    /// <para>
    /// A <see cref="MediaSourceType.Grouping"/> source passes, deliberately: grouping says the
    /// version was merged onto this item by hand rather than discovered beside it, and a
    /// hand-merged MVC file is exactly as convertible as a discovered one. A
    /// <see cref="MediaSourceType.Placeholder"/> source is the opposite case - the server knows the
    /// version exists and has nothing playable about it yet - so that one is refused.
    /// </para>
    /// </remarks>
    public static bool IsTranscodableSource(MediaSourceInfo? source)
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
        // disc: only a declared non-file video type is a refusal this scanner can act on.
        if (source.VideoType is not null && source.VideoType != VideoType.VideoFile)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The identity one media source is being treated as: its own id when that id is a GUID, and a
    /// digest of its path when it is not.
    /// </summary>
    /// <remarks>
    /// This is both the seed of a version's id and the key a caller deduplicates sources by, and it
    /// is deliberately one function for both questions: the answer to "is this the same file again?"
    /// has to be the answer to "would this be the same version id?", or the two listings this
    /// catches would be dropped as duplicates or shipped as distinct by whichever of the two
    /// happened to look first.
    /// </remarks>
    /// <param name="mediaSourceId">The id the source is addressed by.</param>
    /// <param name="sourcePath">The source's path, the fallback identity.</param>
    /// <returns>The identity key of this source, as normalised text.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="mediaSourceId"/> is not a GUID and <paramref name="sourcePath"/> is empty.
    /// </exception>
    public static string SourceIdentityKey(string? mediaSourceId, string? sourcePath)
        => TryParseSourceKey(mediaSourceId, out var parsed) ? parsed : PathDigestKey(sourcePath);

    /// <summary>
    /// Reads the id seed a source contributes, when its id is one.
    /// </summary>
    /// <param name="mediaSourceId">The id the source is addressed by.</param>
    /// <param name="sourceKey">The lower-case "N"-format key, when the id parsed.</param>
    /// <returns>Whether the source id can serve as the identity of a version.</returns>
    public static bool TryParseSourceKey(string? mediaSourceId, out string sourceKey)
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
    /// Lower-cased like every other seed part: the digest itself is case-insensitive hex, but the
    /// seed is one string and gets exactly one textual form.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> is empty.</exception>
    public static string PathDigestKey(string? sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath))).ToLowerInvariant();
    }

    /// <summary>
    /// Reads the item's static media sources, or nothing when they cannot be enumerated.
    /// </summary>
    private static IReadOnlyList<MediaSourceInfo> ReadStaticMediaSources(BaseItem item, ILogger? logger)
    {
        try
        {
            return ((IHasMediaSources)item).GetMediaSources(enablePathSubstitution: false)
                   ?? Array.Empty<MediaSourceInfo>();
        }
        catch (Exception ex)
        {
            // Never throws: an item that cannot answer for its own sources (a source
            // implementation that fails, an item mid-refresh) is answered by the item alone
            // rather than costing the item its versions - the same degradation an empty list
            // produces.
            logger?.LogDebug(
                ex,
                "Anaglyfin could not enumerate the static media sources of {ItemName}; asking the item itself.",
                item.Name);
            return Array.Empty<MediaSourceInfo>();
        }
    }

    /// <summary>
    /// Decides whether a source is the item's own file rather than one of its other versions.
    /// </summary>
    /// <remarks>
    /// Id first, because that is how the server keys and sorts a static source (its own source is
    /// the one carrying the item's id in "N" format). The path is the fallback for a source
    /// implementation that keys itself otherwise, because a file is what the question "is this the
    /// same file?" is actually about. The comparison against the item's path is ordinal-ignore-case
    /// to match the server's own case-insensitive id comparison: paths on the server's own
    /// filesystems are the case they were stored in.
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
}

/// <summary>
/// One file of one item that Anaglyfin accepted as a source of profile versions.
/// </summary>
/// <remarks>
/// A carrier, not a contract worth inventing: it exists so the eligibility decision, the fidelity
/// template and the identity of one file travel together to whoever builds the version, and so a
/// version cannot be built from the path of one source and the streams of another - which is the
/// mistake a stacked movie makes easy.
/// </remarks>
public sealed class MvcEligibleSource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MvcEligibleSource"/> class.
    /// </summary>
    /// <param name="item">The item the file belongs to.</param>
    /// <param name="original">
    /// The source report this file arrived as, or <c>null</c> when only the item could be asked
    /// about it and there is no source report to copy.
    /// </param>
    /// <param name="sourcePath">The file the version's marker will name.</param>
    /// <param name="sourceId">The id the source is addressed by, the seed of the version's id.</param>
    /// <param name="sourceName">The label the source (or the item, alone) carries.</param>
    /// <param name="identityKey">The identity this file is treated as; see <see cref="MvcEligibleSourceScanner.SourceIdentityKey"/>.</param>
    public MvcEligibleSource(BaseItem item, MediaSourceInfo? original, string sourcePath, string sourceId, string? sourceName, string identityKey)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        SourcePath = sourcePath;
        SourceId = sourceId;
        SourceName = sourceName;
        IdentityKey = identityKey;
        Original = original;
    }

    /// <summary>Gets the item the file was reached through.</summary>
    public BaseItem Item { get; }

    /// <summary>Gets the source report this file arrived as, or <c>null</c> for the item alone.</summary>
    public MediaSourceInfo? Original { get; }

    /// <summary>Gets the file the version's marker will name.</summary>
    public string SourcePath { get; }

    /// <summary>Gets the id the source is addressed by, the seed of the version's id.</summary>
    public string SourceId { get; }

    /// <summary>Gets the label the source (or the item, alone) carries.</summary>
    public string? SourceName { get; }

    /// <summary>Gets the identity this file is treated as for id derivation and deduplication.</summary>
    public string IdentityKey { get; }
}

/// <summary>
/// The outcome of asking one item about its sources: the files a profile could convert, and what
/// the item already reports as its own versions.
/// </summary>
public sealed class MvcSourceScan
{
    private static readonly IReadOnlySet<string> NoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="MvcSourceScan"/> class.
    /// </summary>
    /// <param name="candidates">The accepted sources, in the order the item reported them.</param>
    /// <param name="reportedSourceIds">Every static source id the item reported, whatever it accepted.</param>
    public MvcSourceScan(IReadOnlyList<MvcEligibleSource> candidates, IReadOnlySet<string> reportedSourceIds)
    {
        Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        ReportedSourceIds = reportedSourceIds ?? NoIds;
    }

    /// <summary>Gets the files a profile can convert, in the order the item reported them.</summary>
    public IReadOnlyList<MvcEligibleSource> Candidates { get; }

    /// <summary>
    /// Gets the ids of every static source the item reported - accepted or not.
    /// </summary>
    /// <remarks>
    /// A profile version that exists as a library item is one of these ids, because a static source
    /// is keyed by the id of the item behind it. Whoever offers versions therefore reads this set
    /// to learn which of its versions the server can already see on its own, and keeps quiet about
    /// those: the server does not deduplicate static against dynamic sources, so an offer that
    /// repeats one is a version list with the same version in it twice.
    /// </remarks>
    public IReadOnlySet<string> ReportedSourceIds { get; }
}
