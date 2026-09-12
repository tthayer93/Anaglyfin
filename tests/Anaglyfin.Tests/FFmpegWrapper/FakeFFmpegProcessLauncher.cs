using System;
using System.Collections.Generic;
using Anaglyfin.FFmpegWrapper;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Stands in for the real FFmpeg process, recording what the wrapper asked it to start.
/// </summary>
/// <remarks>
/// <para>
/// The recorder is the reason the wrapper's behaviour can be asserted at all: what a
/// wrapper invocation owes its caller is a decision (which vector, which binary), an exit
/// code, and nothing in between, so the fake captures the decision and hands back an exit
/// code the test chose.
/// </para>
/// <para>
/// <see cref="Observe"/> is the part the concurrency tests need. A slot is only interesting
/// while FFmpeg is running - that is when a second job must be turned away - and the fake
/// is the only place in a test that can stand inside that window and look at the slot
/// directory. Whatever it records is therefore a fact about the state of the world at the
/// moment the encoder would have been running, not an inference from after the fact.
/// </para>
/// </remarks>
public sealed class FakeFFmpegProcessLauncher : IFFmpegProcessLauncher
{
    private readonly List<Launch> _launches = new();

    /// <summary>
    /// Gets or sets the code the started "process" exits with; 0, as a successful encode does.
    /// </summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// Gets or sets the exception the start throws instead of running, to stand in for a
    /// binary that could not be executed.
    /// </summary>
    public Exception? FailWith { get; set; }

    /// <summary>
    /// Gets or sets the look taken while the "process" runs.
    /// </summary>
    public Action<Launch>? Observe { get; set; }

    /// <summary>Gets every start the wrapper asked for, in order.</summary>
    public IReadOnlyList<Launch> Launches => _launches;

    /// <summary>Gets the one start the wrapper asked for; it fails when there was not exactly one.</summary>
    public Launch OnlyLaunch
    {
        get
        {
            Xunit.Assert.Single(_launches);

            return _launches[0];
        }
    }

    /// <inheritdoc />
    public int Launch(string realFFmpegPath, IReadOnlyList<string> arguments)
    {
        var launch = new Launch(realFFmpegPath, new List<string>(arguments));
        _launches.Add(launch);

        Observe?.Invoke(launch);

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
    public sealed record Launch(string ExecutablePath, IReadOnlyList<string> Arguments)
    {
        /// <summary>
        /// Gets the vector as one line, for assertions about text that must not appear.
        /// </summary>
        public string CommandLine => string.Join(" ", Arguments);
    }
}
