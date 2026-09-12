using System;
using System.Collections.Generic;
using Anaglyfin.FFmpegWrapper;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Stands in for the real FFmpeg process, recording what the wrapper asked it to start.
/// </summary>
/// <remarks>
/// <para>
/// The recorder is the reason a wrapper invocation can be tested at all: what it owes its
/// caller is a decision (which binary, which vector), an exit code, and nothing in between,
/// so the fake captures the decision and hands back the exit code the test chose.
/// </para>
/// <para>
/// <see cref="Observe"/> is the part the concurrency tests need. A slot is only interesting
/// while FFmpeg is running - that is when a second job has to be turned away - and the fake
/// is the only place in a test that can stand inside that window and look at the slot
/// directory. What it records is therefore a fact about the state of the world at the moment
/// the encoder would have been running, rather than an inference made afterwards.
/// </para>
/// </remarks>
public sealed class FakeFFmpegProcessLauncher : IFFmpegProcessLauncher
{
    private readonly List<LaunchCall> _launches = new();

    /// <summary>
    /// Gets or sets the code the started "process" exits with; 0, as a successful encode does.
    /// </summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// Gets or sets the exception the start throws instead of running, to stand in for a
    /// binary that exists but could not be executed.
    /// </summary>
    public Exception? FailWith { get; set; }

    /// <summary>
    /// Gets or sets the look taken while the "process" is running.
    /// </summary>
    public Action<LaunchCall>? Observe { get; set; }

    /// <summary>Gets every start the wrapper asked for, in order.</summary>
    public IReadOnlyList<LaunchCall> Launches => _launches;

    /// <inheritdoc />
    public int Launch(string realFFmpegPath, IReadOnlyList<string> arguments)
    {
        var call = new LaunchCall(realFFmpegPath, new List<string>(arguments));
        _launches.Add(call);

        Observe?.Invoke(call);

        if (FailWith is { } failure)
        {
            throw failure;
        }

        return ExitCode;
    }

    /// <summary>
    /// One start the wrapper asked for: the binary and the exact vector it was given.
    /// </summary>
    /// <param name="ExecutablePath">The binary the wrapper resolved.</param>
    /// <param name="Arguments">The vector, one entry per argument, without the executable token.</param>
    public sealed record LaunchCall(string ExecutablePath, IReadOnlyList<string> Arguments)
    {
        /// <summary>
        /// Gets the vector as one line, for assertions about text that must not appear.
        /// </summary>
        public string CommandLine => string.Join(" ", Arguments);
    }
}
