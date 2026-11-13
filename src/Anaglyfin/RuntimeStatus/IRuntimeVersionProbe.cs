using System;
using System.Collections.Generic;
using System.Threading;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// Asks one binary a short, fixed question — its version — and reports what it answered, without
/// the caller having to know anything about processes.
/// </summary>
/// <remarks>
/// <para>
/// The probe exists as a seam so the whole runtime-status surface can be tested against a fake
/// that records the vector it was handed and returns a canned answer, exactly as the wrapper tests
/// stand in for FFmpeg with <c>FakeFFmpegProcessLauncher</c>. No test of a status ever runs a real
/// FFmpeg; the only implementation that starts a process is <see cref="ProcessRuntimeVersionProbe"/>,
/// and it is reached in production.
/// </para>
/// <para>
/// A caller must not hand this anything but a short, fixed vector: the arguments it is given are
/// the whole point of the call, and it runs them as data with no shell. The caller that owns the
/// decision of what to ask — and the promise never to pass a media path or a marker — is
/// <see cref="RuntimeStatusService"/>, not an implementation of this interface.
/// </para>
/// </remarks>
public interface IRuntimeVersionProbe
{
    /// <summary>
    /// Runs one binary with a fixed argument vector and reports what it answered.
    /// </summary>
    /// <param name="executablePath">
    /// The binary to run. The caller has already decided it exists and is executable; an
    /// implementation must not invent a lookup when it is a bare name.
    /// </param>
    /// <param name="arguments">
    /// The fixed argument vector, one entry per argument, never a command string.
    /// </param>
    /// <param name="timeout">
    /// How long to wait before the process is killed. A hung probe must not be able to hold the
    /// request open.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the wait; an implementation kills the process when it is raised.
    /// </param>
    /// <returns>What the process answered; see <see cref="RuntimeVersionProbeResult"/>.</returns>
    RuntimeVersionProbeResult Probe(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
