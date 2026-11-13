using System;
using System.Collections.Generic;
using System.Threading;
using Anaglyfin.RuntimeStatus;

namespace Anaglyfin.Tests.RuntimeStatus;

/// <summary>
/// Stands in for the real version probe, recording the exact vector it was asked to run and
/// returning whatever answer a test chose. No test of the runtime status runs a real FFmpeg; this
/// is the seam that makes that true, exactly as <c>FakeFFmpegProcessLauncher</c> does for the
/// wrapper.
/// </summary>
public sealed class FakeRuntimeVersionProbe : IRuntimeVersionProbe
{
    private readonly List<ProbeCall> _calls = new();
    private readonly object _sync = new();

    /// <summary>
    /// Gets or sets the answer for one executable path. The last match by exact path wins; when no
    /// path matches, <see cref="Default"/> is returned.
    /// </summary>
    public Dictionary<string, RuntimeVersionProbeResult> ByPath { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the answer when no path was registered; a successful FFmpeg banner by default.
    /// </summary>
    public RuntimeVersionProbeResult Default { get; set; } =
        RuntimeVersionProbeResult.Ran(0, "ffmpeg version 6.1.1-mvc11-jf2 Copyright (c) the FFmpeg developers");

    /// <summary>
    /// Gets or sets the function that answers, when a test wants to branch on the arguments rather
    /// than the path. When set it wins over <see cref="ByPath"/> and <see cref="Default"/>.
    /// </summary>
    public Func<string, IReadOnlyList<string>, RuntimeVersionProbeResult>? Responder { get; set; }

    /// <summary>
    /// Gets or sets an event signalled the instant a probe begins, for a test that has to observe the
    /// single-flight window without racing a clock.
    /// </summary>
    public ManualResetEventSlim? Entered { get; set; }

    /// <summary>
    /// Gets or sets an event a blocked probe waits on before answering, so a test can hold a pass
    /// open and assert that a second request joins it rather than forking another.
    /// </summary>
    public ManualResetEventSlim? Release { get; set; }

    /// <summary>
    /// Gets a value indicating whether a blocked probe observed its cancellation token. This lets a
    /// watchdog test prove the service actually handed its own pass token to the probe seam.
    /// </summary>
    public bool CancellationSeen { get; private set; }

    /// <summary>Gets every vector the service asked the probe to run, in order.</summary>
    public IReadOnlyList<ProbeCall> Calls
    {
        get
        {
            lock (_sync)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>How many times a probe was run.</summary>
    public int CallCount => Calls.Count;

    /// <inheritdoc />
    public RuntimeVersionProbeResult Probe(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var call = new ProbeCall(executablePath, new List<string>(arguments), timeout);
        lock (_sync)
        {
            _calls.Add(call);
        }

        Entered?.Set();

        try
        {
            Release?.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CancellationSeen = true;
            throw;
        }

        if (Responder is { } responder)
        {
            return responder(executablePath, arguments);
        }

        lock (_sync)
        {
            return ByPath.TryGetValue(executablePath, out var configured) ? configured : Default;
        }
    }

    /// <summary>
    /// One vector the service asked to run: the binary, its fixed arguments, and the timeout it was
    /// given.
    /// </summary>
    /// <param name="ExecutablePath">The binary the service resolved.</param>
    /// <param name="Arguments">The argument vector, one entry per argument.</param>
    /// <param name="Timeout">How long the service allowed the process.</param>
    public sealed record ProbeCall(string ExecutablePath, IReadOnlyList<string> Arguments, TimeSpan Timeout)
    {
        /// <summary>Gets the vector as one line, for assertions about text that must never appear.</summary>
        public string CommandLine => string.Join(" ", Arguments);
    }
}
