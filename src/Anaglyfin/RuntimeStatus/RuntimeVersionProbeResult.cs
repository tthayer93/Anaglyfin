using System;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// The outcome of one version probe: a short question asked of a binary that was expected to
/// answer, with a bounded amount of its standard output kept and the rest discarded.
/// </summary>
/// <remarks>
/// <para>
/// Only three answers are possible and they are spelled out rather than left to a caller to
/// infer from a mix of fields. The binary ran and returned a code (<see cref="Started"/> is
/// <c>true</c>), it did not finish in the time it was given (<see cref="TimedOut"/>), or it could
/// not be made to run at all (<see cref="Failure"/> names why). A caller that asked the wrapper
/// for its identity reads <see cref="StandardOutputLine"/>; a caller that asked FFmpeg for its
/// banner reads the same field.
/// </para>
/// <para>
/// <see cref="Failure"/> carries an exception type name and nothing else — never a path, never a
/// command vector — so a broken deployment cannot steer text of its own onto the status page.
/// </para>
/// </remarks>
public sealed record RuntimeVersionProbeResult
{
    /// <summary>Whether the process was started and ran to an exit code.</summary>
    public bool Started { get; init; }

    /// <summary>
    /// The process's exit code when <see cref="Started"/> is <c>true</c>; <c>null</c> otherwise.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// The first line of standard output, truncated to the probe's cap, or <c>null</c> when the
    /// process wrote none.
    /// </summary>
    public string? StandardOutputLine { get; init; }

    /// <summary>Whether the process was killed for outliving the probe's timeout.</summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// The name of the exception that stopped the process from running, or <c>null</c> when it ran
    /// or timed out.
    /// </summary>
    public string? Failure { get; init; }

    /// <summary>
    /// A completed run with an exit code and its first output line.
    /// </summary>
    /// <param name="exitCode">The exit code the process returned.</param>
    /// <param name="standardOutputLine">Its first output line, or <c>null</c>.</param>
    /// <returns>The result a caller that ran the process receives.</returns>
    public static RuntimeVersionProbeResult Ran(int exitCode, string? standardOutputLine)
        => new()
        {
            Started = true,
            ExitCode = exitCode,
            StandardOutputLine = standardOutputLine
        };

    /// <summary>The result of a process that was killed for taking too long.</summary>
    /// <returns>A timed-out result.</returns>
    public static RuntimeVersionProbeResult TimedOutResult()
        => new() { Started = false, TimedOut = true };

    /// <summary>The result of a process that could not be started.</summary>
    /// <param name="failure">The exception type name, and nothing more.</param>
    /// <returns>A failed result.</returns>
    public static RuntimeVersionProbeResult Failed(string failure)
        => new() { Started = false, Failure = failure };
}
