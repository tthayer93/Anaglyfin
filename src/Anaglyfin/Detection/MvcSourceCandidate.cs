using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Anaglyfin.Detection;

/// <summary>
/// The signals Anaglyfin knows how to read when it asks whether an item is 3D MVC.
/// </summary>
/// <remarks>
/// <para>
/// This is intentionally a plain data bag rather than a <see cref="BaseItem"/>: the
/// detector stays a pure function of strings and enums, which keeps it usable from a
/// <c>IMediaSourceProvider</c> (see <see cref="FromItem"/>), from a scheduled task over
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
}
