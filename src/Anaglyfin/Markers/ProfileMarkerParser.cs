using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Anaglyfin.Profiles;

namespace Anaglyfin.Markers;

/// <summary>
/// Reads <see cref="ProfileMarker"/> text back out of a string, without ever throwing.
/// </summary>
/// <remarks>
/// <para>
/// This is the detection entry point for the FFmpeg wrapper: it is handed every input
/// token a transcode command carries, and for each one it answers exactly one of the
/// <see cref="MarkerParseStatus"/> outcomes. The wrapper's dispatch rule - and this
/// parser's contract - is: <see cref="MarkerParseStatus.NotMarker"/> means the token
/// is ordinary FFmpeg input and passes through untouched; <see cref="MarkerParseStatus.Success"/>
/// means the command is rewritten for the parsed profile; every other status means the
/// token wanted to be a marker and failed, so the job must be refused rather than run
/// with a fake input path.
/// </para>
/// <para>
/// The parser is pure and deterministic: it works on the string alone with fixed rules,
/// never on <c>System.Uri</c> normalization, and it never opens the marker "URL" - the
/// loopback address is a namespace, not an endpoint, so marker validity does not care
/// whether anything is listening or whether the referenced file currently exists.
/// Validation of the decoded values reuses the same allowlist and path guards the
/// builder enforces, so a marker only parses when it could have been built.
/// </para>
/// <para>
/// Accepted text is canonical-form-tolerant but forgery-hostile: prefix casing and
/// query-parameter order and casing are tolerated because they cannot change meaning,
/// while a second <c>source</c> parameter, a parameter Anaglyfin does not define, a
/// raw control character, a percent-decoded path that is not rooted or carries control
/// characters, a non-allowlisted profile id, or a subtitle ordinal that is not plain
/// ASCII digits of a non-negative int are all refused. In canonical marker text every
/// path character outside <c>A-Za-z0-9-._~</c> arrives percent-encoded, so a filename
/// can never forge an extra query parameter, and the decoded path is only ever treated
/// as data: it is validated to contain no control characters and is passed to
/// argument-vector consumers, never to a shell.
/// </para>
/// </remarks>
public static class ProfileMarkerParser
{
    private const string MarkerAddressPrefix = ProfileMarker.MarkerScheme + "://" + ProfileMarker.MarkerHost;

    private const string ReservedPathStem = "/anaglyfin/profile";

    /// <summary>
    /// Cheaply checks whether a value could be an Anaglyfin marker.
    /// </summary>
    /// <param name="value">A candidate token. May be null.</param>
    /// <returns>
    /// <c>true</c> when the value starts with <see cref="ProfileMarker.MarkerPrefix"/>
    /// (ignoring casing); everything else certainly is not a canonical marker.
    /// </returns>
    /// <remarks>
    /// A convenience for callers that want a quick filter. Detection that must fail
    /// closed on marker-shaped junk should use <see cref="Parse"/> instead, which also
    /// classifies near-prefix texts such as <c>…/anaglyfin/profile</c> as malformed
    /// rather than as pass-through.
    /// </remarks>
    public static bool IsMarkerCandidate(string? value)
        => value is not null
           && value.StartsWith(ProfileMarker.MarkerPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a string as an Anaglyfin profile marker.
    /// </summary>
    /// <param name="candidate">The text to classify, for example one argv token. May be null.</param>
    /// <returns>
    /// The outcome; on success it carries the validated <see cref="MarkerParseResult.Marker"/>,
    /// otherwise the <see cref="MarkerParseStatus"/> and a fixed failure description.
    /// </returns>
    /// <remarks>
    /// This method never throws: any input, however broken, is data to classify, so
    /// that a wrapper can call it over hostile command lines safely.
    /// </remarks>
    public static MarkerParseResult Parse(string? candidate)
    {
        if (candidate is null)
        {
            return MarkerParseResult.Failure(
                MarkerParseStatus.NotMarker,
                "The value does not carry the Anaglyfin marker prefix.");
        }

        if (!candidate.StartsWith(ProfileMarker.MarkerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Text that only nearly carries the prefix (…/profile, …/profiles/x) is
            // marker-shaped and belongs to us: classify it malformed so a wrapper fails
            // the job instead of handing a nonsense input URL to FFmpeg. Anything else
            // is not Anaglyfin traffic at all.
            return candidate.StartsWith(MarkerAddressPrefix + ReservedPathStem, StringComparison.OrdinalIgnoreCase)
                ? MarkerParseResult.Failure(
                    MarkerParseStatus.MalformedMarker,
                    "The marker path does not carry a profile-id segment.")
                : MarkerParseResult.Failure(
                    MarkerParseStatus.NotMarker,
                    "The value does not carry the Anaglyfin marker prefix.");
        }

        var body = candidate[ProfileMarker.MarkerPrefix.Length..];

        if (body.Any(char.IsControl))
        {
            return MarkerParseResult.Failure(
                MarkerParseStatus.MalformedMarker,
                "The marker contains a raw control character.");
        }

        var queryStart = body.IndexOf('?');
        if (queryStart < 0)
        {
            return MarkerParseResult.Failure(
                MarkerParseStatus.MalformedMarker,
                "The marker carries no query parameters.");
        }

        var profileSegment = body[..queryStart];
        if (profileSegment.Length == 0 || profileSegment.Contains('/'))
        {
            return MarkerParseResult.Failure(
                MarkerParseStatus.MalformedMarker,
                "The marker path does not carry exactly one profile-id segment.");
        }

        var profileId = ProfileIds.Normalize(Uri.UnescapeDataString(profileSegment)).ToLowerInvariant();
        if (!ProfileIds.IsAllowed(profileId))
        {
            return MarkerParseResult.Failure(
                MarkerParseStatus.UnknownProfile,
                "The marker names a profile id that is not on the Anaglyfin allowlist.");
        }

        var query = body[(queryStart + 1)..];
        if (query.Length == 0)
        {
            return MarkerParseResult.Failure(
                MarkerParseStatus.MissingSource,
                "The marker carries no source parameter.");
        }

        if (!TryReadParameters(query, out var sourceValue, out var subtitleValue, out var structureError))
        {
            return MarkerParseResult.Failure(MarkerParseStatus.MalformedMarker, structureError);
        }

        if (sourceValue is null)
        {
            return MarkerParseResult.Failure(
                MarkerParseStatus.MissingSource,
                "The marker carries no source parameter.");
        }

        var sourcePath = Uri.UnescapeDataString(sourceValue);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return MarkerParseResult.Failure(
                MarkerParseStatus.MissingSource,
                "The marker source parameter is empty.");
        }

        if (sourcePath.Any(char.IsControl))
        {
            return MarkerParseResult.Failure(
                MarkerParseStatus.InvalidSource,
                "The decoded marker source path contains a control character.");
        }

        if (!Path.IsPathRooted(sourcePath))
        {
            return MarkerParseResult.Failure(
                MarkerParseStatus.InvalidSource,
                "The decoded marker source path is not a rooted filesystem path.");
        }

        int? subtitleOrdinal = null;
        if (subtitleValue is not null)
        {
            if (!TryParseOrdinal(subtitleValue, out var parsedOrdinal))
            {
                return MarkerParseResult.Failure(
                    MarkerParseStatus.InvalidSubtitleOrdinal,
                    "The marker subtitle ordinal is not a non-negative integer.");
            }

            subtitleOrdinal = parsedOrdinal;
        }

        // profileId is allowlisted and sourcePath validated, so construction cannot throw here.
        return MarkerParseResult.Success(new ProfileMarker(profileId, sourcePath, subtitleOrdinal));
    }

    /// <summary>
    /// Splits the query into the two allowlisted parameters, refusing anything else.
    /// </summary>
    private static bool TryReadParameters(
        string query,
        out string? sourceValue,
        out string? subtitleValue,
        [NotNullWhen(false)] out string? error)
    {
        sourceValue = null;
        subtitleValue = null;
        error = null;

        foreach (var segment in query.Split('&'))
        {
            if (segment.Length == 0)
            {
                error = "The marker query contains an empty parameter.";
                return false;
            }

            var separator = segment.IndexOf('=');
            if (separator < 0)
            {
                error = "The marker query carries a parameter without a value.";
                return false;
            }

            // Only the two known parameters are read at all; a raw '=' inside a value
            // is content of that value, and unknown or repeated keys cannot smuggle a
            // second opinion past the allowlist.
            var key = Uri.UnescapeDataString(segment[..separator]);
            var value = segment[(separator + 1)..];

            if (key.Equals(ProfileMarker.SourceQueryParameter, StringComparison.OrdinalIgnoreCase))
            {
                if (sourceValue is not null)
                {
                    error = "The marker query repeats the source parameter.";
                    return false;
                }

                sourceValue = value;
            }
            else if (key.Equals(ProfileMarker.SubtitleQueryParameter, StringComparison.OrdinalIgnoreCase))
            {
                if (subtitleValue is not null)
                {
                    error = "The marker query repeats the subtitle parameter.";
                    return false;
                }

                subtitleValue = value;
            }
            else
            {
                error = "The marker query carries a parameter Anaglyfin does not define.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Strictly reads a non-negative int: plain ASCII digits only, no signs, spaces or
    /// escapes, so only an unambiguous ordinal becomes a subtitle track choice.
    /// </summary>
    private static bool TryParseOrdinal(string value, out int ordinal)
    {
        ordinal = 0;

        if (value.Length == 0 || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
               && ordinal >= 0;
    }
}
