using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Anaglyfin.Detection;

/// <summary>
/// The signals Anaglyfin knows how to read when it asks whether an item is 3D MVC.
/// </summary>
/// <remarks>
/// <para>
/// This is intentionally a plain data bag rather than a <see cref="BaseItem"/>: the
/// detector stays a pure function of strings and enums, which keeps it usable from a
/// <c>IMediaSourceProvider</c> (see <see cref="FromItem"/> and
/// <see cref="FromMediaSource(MediaSourceInfo)"/>), from a scheduled task over
/// database rows, and from unit tests without a server.
/// </para>
/// <para>
/// Every member is optional. Missing signals simply do not contribute evidence, so a
/// caller that only has a path can call <see cref="ForPath"/> and still get a
/// conservative answer.
/// </para>
/// </remarks>
public sealed class MvcSourceCandidate
{
    /// <summary>
    /// Gets the item path (file or folder), if known.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets the Jellyfin display name, if known.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the 3D format recorded in Jellyfin metadata, if any.
    /// </summary>
    public Video3DFormat? Video3DFormat { get; init; }

    /// <summary>
    /// Gets the Jellyfin item tags, if known.
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Builds a candidate from a path alone, for callers that have no metadata at hand.
    /// </summary>
    /// <param name="path">The file or folder path.</param>
    /// <returns>A candidate carrying only the path signal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <c>null</c>.</exception>
    public static MvcSourceCandidate ForPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return new MvcSourceCandidate { Path = path };
    }

    /// <summary>
    /// Reads the detection signals off a Jellyfin library item.
    /// </summary>
    /// <param name="item">The item handed to the caller, typically by the media source pipeline.</param>
    /// <returns>A candidate carrying path, name, tags and the declared 3D format.</returns>
    /// <remarks>
    /// <see cref="Video3DFormat"/> only exists on <see cref="Video"/>, so non-video items
    /// contribute their path, name and tags and no format claim.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <c>null</c>.</exception>
    public static MvcSourceCandidate FromItem(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new MvcSourceCandidate
        {
            Path = item.Path,
            Name = item.Name,
            Video3DFormat = item is Video video ? video.Video3DFormat : null,
            Tags = item.Tags
        };
    }

    /// <summary>
    /// Reads the detection signals off one media source of an item.
    /// </summary>
    /// <param name="source">
    /// One static media source - one alternate version of the item the server is being asked
    /// about - as the item's own media-source API reports it.
    /// </param>
    /// <param name="itemTags">
    /// Tags of the item the source belongs to, or <c>null</c>. A media source carries no tags
    /// of its own, so a caller that has the owning item's tags can hand them over; see the
    /// remarks for when that is and is not a fair claim to make.
    /// </param>
    /// <returns>A candidate carrying the source's path, its own version label and its declared 3D format.</returns>
    /// <remarks>
    /// <para>
    /// This is the source-shaped twin of <see cref="FromItem"/>, and it exists because an item
    /// is not always one file. A movie assembled from a stack or from linked alternate versions
    /// answers a playback-info request with one static media source per version, and only one of
    /// those files may be the MVC one: read the item's own fields and every version of
    /// <c>Ready Player One</c> inherits the primary file's answer, which is the opposite of a
    /// detection. The three signals here are the ones a source genuinely owns - the file it names,
    /// the label the server put on that file, and the stereo format declared for it.
    /// </para>
    /// <para>
    /// <see cref="Video3DFormat"/> is the item's own value copied onto its own source by the
    /// server, so a source that is not the item reports the format of its own file rather than
    /// the primary's.
    /// </para>
    /// <para>
    /// <paramref name="itemTags"/> is a caller's judgement call, not a free inheritance: an item's
    /// tags describe the item, which is the item's own file and nothing else's, so a caller asking
    /// about a <em>sibling</em> version would be crediting one file with evidence another file
    /// never gave. The provider therefore passes tags only for the source that is the item.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <c>null</c>.</exception>
    public static MvcSourceCandidate FromMediaSource(MediaSourceInfo source, IReadOnlyList<string>? itemTags = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new MvcSourceCandidate
        {
            // A substituted or mapped path would be the client's view of the file, while the
            // marker contract and the wrapper both work in server-side paths, so whatever the
            // source reports is taken as reported.
            Path = source.Path,
            Name = source.Name,
            Video3DFormat = source.Video3DFormat,
            Tags = itemTags
        };
    }
}
