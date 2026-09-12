using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Anaglyfin.Profiles;

namespace Anaglyfin.Markers;

/// <summary>
/// The profile marker an Anaglyfin alternate media source carries instead of a bare file path.
/// </summary>
/// <remarks>
/// <para>
/// A marker is the only channel through which the media-source provider (which knows
/// the profile the user picked) can tell the FFmpeg wrapper (which is the only place
/// allowed to add view and filter arguments) what to do. It is deliberately a plain
/// URL on a reserved local address:
/// </para>
/// <c>http://127.0.0.1/anaglyfin/profile/&lt;profileId&gt;?source=&lt;encoded source path&gt;&amp;subtitle=&lt;optional ordinal&gt;</c>
/// <para>
/// A URL-shaped marker survives every transport between provider and wrapper intact:
/// it is one whitespace-free token, so <c>MediaSourceInfo.Path</c>, the encoder command
/// line and argv tokenization all keep it whole, and the reserved host makes it cheap
/// for the wrapper to recognise. The address is a namespace, not an endpoint -
/// nothing is ever fetched from it, and neither this type nor
/// <see cref="ProfileMarkerParser"/> touches the network or the filesystem.
/// </para>
/// <para>
/// Security invariants, enforced at construction: the profile id must be on the
/// <see cref="ProfileIds"/> allowlist, the source path must be rooted and free of
/// control characters, and the subtitle ordinal must be non-negative when present. The
/// source path travels percent-encoded, so the marker's own <c>?</c> and <c>&amp;</c>
/// structure cannot be forged from inside a filename, and no marker can carry FFmpeg
/// filter syntax: a profile id is not filter syntax (see <see cref="ProfileIds"/>).
/// </para>
/// <para>
/// The type is inert data, usable from the plugin process and from an out-of-process
/// wrapper alike; it needs no services and no dependency injection.
/// </para>
/// </remarks>
public sealed record ProfileMarker
{
    /// <summary>The scheme every marker uses. Never <c>https</c>: the address is a namespace, not a service.</summary>
    public const string MarkerScheme = "http";

    /// <summary>The reserved loopback host that identifies a marker as Anaglyfin's.</summary>
    public const string MarkerHost = "127.0.0.1";

    /// <summary>The URL path prefix, ending at the segment boundary before the profile id.</summary>
    public const string MarkerPathPrefix = "/anaglyfin/profile/";

    /// <summary>
    /// The exact text every canonical marker starts with: <c>http://127.0.0.1/anaglyfin/profile/</c>.
    /// </summary>
    public const string MarkerPrefix = MarkerScheme + "://" + MarkerHost + MarkerPathPrefix;

    /// <summary>Name of the query parameter carrying the percent-encoded real source path.</summary>
    public const string SourceQueryParameter = "source";

    /// <summary>Name of the optional query parameter carrying the subtitle burn-in ordinal.</summary>
    public const string SubtitleQueryParameter = "subtitle";

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileMarker"/> record, validating every
    /// security invariant of the marker contract.
    /// </summary>
    /// <param name="profileId">
    /// An allowlisted Anaglyfin profile id. Surrounding whitespace and casing are
    /// canonicalized; anything not on the <see cref="ProfileIds"/> allowlist is refused.
    /// </param>
    /// <param name="sourcePath">
    /// The real, rooted filesystem path of the media file the marker stands in for. The
    /// provider owns this value; it must never be copied from a client- or query-supplied
    /// string, and the marker only ever transports it, percent-encoded.
    /// </param>
    /// <param name="subtitleOrdinal">
    /// The zero-based subtitle track ordinal to burn in, or null for a subtitle-free version.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="profileId"/> is empty or not an allowlisted profile id, or
    /// <paramref name="sourcePath"/> is blank, contains a control character, or is not a
    /// rooted path. A relative path would resolve against the transcode working
    /// directory, which the plugin does not control.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="subtitleOrdinal"/> is negative.
    /// </exception>
    public ProfileMarker(string profileId, string sourcePath, int? subtitleOrdinal)
    {
        var normalizedProfileId = ProfileIds.Normalize(profileId);
        if (normalizedProfileId.Length == 0 || !ProfileIds.IsAllowed(normalizedProfileId))
        {
            throw new ArgumentException(
                "A profile marker must name one allowlisted Anaglyfin profile id.",
                nameof(profileId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        // Control characters cannot survive transport inside a URL token and have no
        // business in a real media path, so a value carrying one is refused outright
        // rather than percent-encoded around.
        if (sourcePath.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A profile marker source path must not contain control characters.",
                nameof(sourcePath));
        }

        if (!Path.IsPathRooted(sourcePath))
        {
            throw new ArgumentException(
                "A profile marker source path must be rooted, because the wrapper resolves it against the real filesystem, not a working directory.",
                nameof(sourcePath));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(subtitleOrdinal.GetValueOrDefault());

        // Every allowlisted id is lowercase ASCII, so canonicalizing to lowercase is
        // lossless and gives a marker exactly one textual form.
        ProfileId = normalizedProfileId.ToLowerInvariant();
        SourcePath = sourcePath;
        SubtitleOrdinal = subtitleOrdinal;
    }

    /// <summary>
    /// Gets the allowlisted profile id, canonicalized to lowercase.
    /// </summary>
    public string ProfileId { get; init; }

    /// <summary>
    /// Gets the real, rooted filesystem path the marker stands in for.
    /// </summary>
    public string SourcePath { get; init; }

    /// <summary>
    /// Gets the subtitle track ordinal to burn in, or null when the version is subtitle-free.
    /// </summary>
    /// <remarks>
    /// Zero is a value, not an absence: it means the first subtitle track. It is
    /// therefore always transported in the marker text, unlike null.
    /// </remarks>
    public int? SubtitleOrdinal { get; init; }

    /// <summary>
    /// Builds a marker for one profile version of one file.
    /// </summary>
    /// <param name="profileId">An allowlisted Anaglyfin profile id.</param>
    /// <param name="sourcePath">The rooted path of the media file the version is made from.</param>
    /// <param name="subtitleOrdinal">The subtitle ordinal to burn in, or null for no subtitles.</param>
    /// <returns>The validated marker.</returns>
    /// <exception cref="ArgumentException">
    /// The profile id or source path fails a marker security invariant; see the constructor.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="subtitleOrdinal"/> is negative.
    /// </exception>
    public static ProfileMarker Create(string profileId, string sourcePath, int? subtitleOrdinal = null)
        => new(profileId, sourcePath, subtitleOrdinal);

    /// <summary>
    /// Gets the marker text to put into <c>MediaSourceInfo.Path</c> of the alternate
    /// version this marker describes.
    /// </summary>
    /// <returns>The canonical marker URL text.</returns>
    /// <remarks>
    /// The provider (not this type) owns the rest of the <c>MediaSourceInfo</c>: the
    /// stable source id, the name, the runtime and the stream list. The path is the
    /// only field the FFmpeg wrapper is allowed to read, which is why the marker - and
    /// not a filter string, never - is what travels in it.
    /// </remarks>
    public string ToMediaSourcePath()
        => ToString();

    /// <inheritdoc />
    /// <remarks>
    /// The canonical form: <see cref="MarkerPrefix"/>, the lowercase profile id, and a
    /// query whose parameters appear in builder order (<c>source</c>, then
    /// <c>subtitle</c>) with the source path percent-encoded. Parsing this text returns
    /// an equal marker.
    /// </remarks>
    public override string ToString()
    {
        var builder = new StringBuilder(MarkerPrefix)
            .Append(ProfileId)
            .Append('?')
            .Append(SourceQueryParameter)
            .Append('=')
            .Append(Uri.EscapeDataString(SourcePath));

        if (SubtitleOrdinal is int ordinal)
        {
            builder
                .Append('&')
                .Append(SubtitleQueryParameter)
                .Append('=')
                .Append(ordinal.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
