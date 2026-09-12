using System;
using System.Collections.Generic;
using Anaglyfin.Markers;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// The outcome of one wrapper argument rewrite: what to execute, or why not to.
/// </summary>
/// <remarks>
/// <para>
/// The result is the whole contract between the rewriter and the process launcher that
/// consumes it. <see cref="IsSuccess"/> is the only gate that should let a launcher call
/// <c>exec</c>, and <see cref="Arguments"/> is the only vector it may execute: on every
/// failure the vector is empty, so a launcher that forgets the gate still cannot open a
/// marker URL or run a half-rewritten command.
/// </para>
/// <para>
/// <see cref="Error"/> carries a fixed description authored by the rewriter, never text
/// echoed from the command line: an FFmpeg argv is client-influenced (it arrives from a
/// playback request), and a refusal reason is exactly the string that ends up in a server
/// log. What a refusal names is the class of problem and, at most, an allowlisted profile
/// id.
/// </para>
/// </remarks>
public sealed record WrapperRewriteResult
{
    /// <summary>
    /// Gets the decision the rewriter reached.
    /// </summary>
    public required WrapperRewriteStatus Status { get; init; }

    /// <summary>
    /// Gets the argument vector to hand to the real FFmpeg binary.
    /// </summary>
    /// <remarks>
    /// A snapshot the caller may mutate freely. It equals the received vector when
    /// <see cref="Status"/> is <see cref="WrapperRewriteStatus.PassedThrough"/>, carries
    /// the rewrite when it is <see cref="WrapperRewriteStatus.Rewritten"/>, and is empty
    /// for every failure so that nothing can be executed by accident.
    /// </remarks>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>
    /// Gets the fixed description of a refusal, or null when the rewrite succeeded.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the allowlisted profile id a rewrite was applied for, otherwise null.
    /// </summary>
    public string? ProfileId { get; init; }

    /// <summary>
    /// Gets the marker classification that caused a
    /// <see cref="WrapperRewriteStatus.RejectedMarker"/> refusal, otherwise null.
    /// </summary>
    /// <remarks>
    /// This is the parser's own status (<c>MalformedMarker</c>, <c>UnknownProfile</c>,
    /// <c>MissingSource</c>, …) rather than the rewriter's, so the log says which marker
    /// rule was broken without the rewriter having to invent its own vocabulary.
    /// </remarks>
    public MarkerParseStatus? MarkerStatus { get; init; }

    /// <summary>
    /// Gets a value indicating whether the wrapper may execute <see cref="Arguments"/>.
    /// </summary>
    public bool IsSuccess => Status is WrapperRewriteStatus.PassedThrough or WrapperRewriteStatus.Rewritten;

    /// <summary>
    /// Creates the pass-through outcome for an ordinary FFmpeg command.
    /// </summary>
    /// <param name="arguments">The received vector, copied as the returned snapshot.</param>
    /// <returns>The outcome, whose vector is equal to the received one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    public static WrapperRewriteResult PassedThrough(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return new WrapperRewriteResult
        {
            Status = WrapperRewriteStatus.PassedThrough,
            Arguments = new List<string>(arguments)
        };
    }

    /// <summary>
    /// Creates the rewritten outcome.
    /// </summary>
    /// <param name="arguments">The rewritten vector to execute.</param>
    /// <param name="profileId">The allowlisted profile id the rewrite applied.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="profileId"/> is blank.</exception>
    public static WrapperRewriteResult Rewritten(IReadOnlyList<string> arguments, string profileId)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        return new WrapperRewriteResult
        {
            Status = WrapperRewriteStatus.Rewritten,
            Arguments = arguments,
            ProfileId = profileId
        };
    }

    /// <summary>
    /// Creates a refusing outcome, which carries no executable vector.
    /// </summary>
    /// <param name="status">The refusal classification.</param>
    /// <param name="error">The fixed description of the refusal.</param>
    /// <param name="markerStatus">
    /// The parser status behind a marker rejection, when there is one.
    /// </param>
    /// <returns>The outcome with an empty argument vector.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="status"/> is not a refusal, or <paramref name="error"/> is blank.
    /// </exception>
    public static WrapperRewriteResult Failure(
        WrapperRewriteStatus status,
        string error,
        MarkerParseStatus? markerStatus = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        if (status is WrapperRewriteStatus.PassedThrough or WrapperRewriteStatus.Rewritten)
        {
            throw new ArgumentException(
                $"'{status}' is not a refusal, so it cannot carry a failure outcome.",
                nameof(status));
        }

        return new WrapperRewriteResult
        {
            Status = status,
            Arguments = Array.Empty<string>(),
            Error = error,
            MarkerStatus = markerStatus
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Summarises the decision without rendering the command line or a source path: this
    /// text is what a wrapper logs, and a media library path is not log material.
    /// </remarks>
    public override string ToString()
        => Status switch
        {
            WrapperRewriteStatus.PassedThrough => "pass-through (unchanged)",
            WrapperRewriteStatus.Rewritten => $"rewritten for '{ProfileId}' ({Arguments.Count} arguments)",
            _ => $"refused: {Status} ({Error})"
        };
}
