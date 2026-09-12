using System;

namespace Anaglyfin.Markers;

/// <summary>
/// The decision returned by <see cref="ProfileMarkerParser.Parse(string?)"/>.
/// </summary>
/// <remarks>
/// <para>
/// It carries the parsed <see cref="Marker"/> on success and a human-readable,
/// classification-grade reason on failure, so a caller can both act on the outcome and
/// log it without inventing its own vocabulary. The <see cref="Status"/> decides how a
/// wrapper must react: pass through on <see cref="MarkerParseStatus.NotMarker"/>,
/// rewrite on <see cref="MarkerParseStatus.Success"/>, and refuse the job on every
/// other status.
/// </para>
/// <para>
/// Failure texts are fixed strings authored here, never echoes of the parsed input:
/// marker text is client-influenced, and untrusted text must not reach the log.
/// </para>
/// </remarks>
public sealed record MarkerParseResult
{
    /// <summary>
    /// Gets the outcome classification.
    /// </summary>
    public required MarkerParseStatus Status { get; init; }

    /// <summary>
    /// Gets the parsed marker, or null unless <see cref="Status"/> is
    /// <see cref="MarkerParseStatus.Success"/>.
    /// </summary>
    public ProfileMarker? Marker { get; init; }

    /// <summary>
    /// Gets the fixed description of why a parse failed, or null on success.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets a value indicating whether the text was a valid Anaglyfin marker.
    /// </summary>
    public bool IsSuccess => Status == MarkerParseStatus.Success;

    /// <summary>
    /// Gets the canonical profile id of a successful parse, otherwise null.
    /// </summary>
    public string? ProfileId => Marker?.ProfileId;

    /// <summary>
    /// Gets the decoded source path of a successful parse, otherwise null.
    /// </summary>
    public string? SourcePath => Marker?.SourcePath;

    /// <summary>
    /// Gets the subtitle ordinal of a successful parse; null also when a valid marker
    /// simply carries no subtitle.
    /// </summary>
    public int? SubtitleOrdinal => Marker?.SubtitleOrdinal;

    /// <summary>
    /// Gets the video stream index of a successful parse; null also when a valid marker
    /// simply names no video stream.
    /// </summary>
    public int? VideoStreamIndex => Marker?.VideoStreamIndex;

    /// <summary>
    /// Creates the accepting outcome for a validated marker.
    /// </summary>
    /// <param name="marker">The parsed marker.</param>
    /// <returns>The accepting outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="marker"/> is null.</exception>
    public static MarkerParseResult Success(ProfileMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        return new MarkerParseResult { Status = MarkerParseStatus.Success, Marker = marker };
    }

    /// <summary>
    /// Creates a rejecting outcome.
    /// </summary>
    /// <param name="status">The failure classification; must not be success.</param>
    /// <param name="error">The fixed description of the failure.</param>
    /// <returns>The rejecting outcome.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="error"/> is blank, or <paramref name="status"/> is
    /// <see cref="MarkerParseStatus.Success"/>.
    /// </exception>
    public static MarkerParseResult Failure(MarkerParseStatus status, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        if (status == MarkerParseStatus.Success)
        {
            throw new ArgumentException("A failure outcome cannot claim success.", nameof(status));
        }

        return new MarkerParseResult { Status = status, Error = error };
    }

    /// <inheritdoc />
    public override string ToString()
        => Marker is ProfileMarker marker
            ? marker.ToString()
            : $"{Status}: {Error}";
}
