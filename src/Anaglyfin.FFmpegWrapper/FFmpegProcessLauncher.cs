using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Starts the real FFmpeg binary as a child process of the wrapper.
/// </summary>
/// <remarks>
/// <para>
/// <b>No shell, ever.</b> The command is built through
/// <see cref="ProcessStartInfo.ArgumentList"/>, one entry per argument, so nothing is
/// re-parsed out of a string. That is what lets the wrapper hand over a rewritten vector
/// containing filter text, paths with spaces and brackets, and a subtitle filename with
/// quotes in it, without either the wrapper or a shell giving that text a second meaning.
/// The vector arrives from a playback request, so this is the boundary where injection
/// would otherwise happen.
/// </para>
/// <para>
/// <b>Nothing is captured.</b> Standard input, output and error stay inherited, which is
/// the same relationship FFmpeg has to the server when the wrapper is not installed:
/// Jellyfin's stdin control (<c>q</c> to stop, the HLS progress it reads from stderr)
/// reaches the encoder, and the encoder's answers reach Jellyfin. A wrapper that buffered
/// those streams would leave the server blind while a transcode ran.
/// </para>
/// <para>
/// <b>The working directory is inherited as well</b>, deliberately: Jellyfin starts
/// FFmpeg with its transcode temp as the working directory and writes relative HLS
/// outputs against it. The wrapper neither changes it nor needs to know it - relative
/// outputs land exactly where the server put them.
/// </para>
/// <para>
/// <b>A stop signal is forwarded, never anticipated.</b> While the child runs, this type
/// keeps a <see cref="IChildSignalForwarder"/> attached to it, so a
/// <c>SIGTERM</c>/<c>SIGINT</c>/<c>SIGHUP</c> that reaches the wrapper reaches the encoder
/// under the same number (see <see cref="PosixSignalForwarder"/>), and the wait below is
/// then what ends the launch - not a kill the wrapper decided on. If the child ignores the
/// signal, the wrapper waits on it, as it always has; a bounded stop would be a timeout
/// the wrapper invented, and inventing one is a decision for whoever can measure what a
/// stopped encoder still owes the playlist.
/// </para>
/// </remarks>
public sealed class FFmpegProcessLauncher : IFFmpegProcessLauncher
{
    private readonly Func<IChildSignalForwarder> _signalForwarderFactory;

    /// <summary>
    /// Creates a launcher that hands the stop signals to the platform's forwarder - the
    /// listening one on POSIX, the silent one wherever the signals cannot arrive.
    /// </summary>
    public FFmpegProcessLauncher()
        : this(PosixSignalForwarder.Create)
    {
    }

    /// <summary>
    /// Creates a launcher over an explicit source of stop-signal forwarders.
    /// </summary>
    /// <param name="signalForwarderFactory">
    /// Called once per launch, before the child is started, for the forwarder that will
    /// speak for that child.
    /// </param>
    /// <remarks>
    /// The seam is what lets the tests watch the launcher's signal etiquette - a child
    /// attached while it runs, released the moment it exits, everything unregistered on
    /// the way out - without any test sending a real signal at the test runner, which
    /// would be a test that can kill the suite.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="signalForwarderFactory"/> is null.</exception>
    public FFmpegProcessLauncher(Func<IChildSignalForwarder> signalForwarderFactory)
    {
        ArgumentNullException.ThrowIfNull(signalForwarderFactory);

        _signalForwarderFactory = signalForwarderFactory;
    }

    /// <inheritdoc />
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// The binary could not be executed - it does not exist, is not executable, or is not
    /// an image this platform can run. The exception is deliberately allowed to travel:
    /// <see cref="WrapperApplication"/> turns it into a refusal, and the operating
    /// system's own reason is the one the administrator needs.
    /// </exception>
    public int Launch(string realFFmpegPath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realFFmpegPath);
        ArgumentNullException.ThrowIfNull(arguments);

        using var process = new Process
        {
            StartInfo = BuildStartInfo(realFFmpegPath, arguments)
        };

        // Listening begins before the start, not after it: the window in which a signal
        // would find the wrapper alive and the child not yet named is exactly the window
        // in which the old wrapper used to die silently, so the forwarder is standing by
        // (with no child to speak for) from here on, and the rest of the method only ever
        // narrows that window to the moments of the exec itself.
        using IChildSignalForwarder signalForwarder = _signalForwarderFactory();

        process.Start();
        signalForwarder.AttachChild(process.Id);

        try
        {
            process.WaitForExit();
        }
        finally
        {
            // The instant the child is gone, its id belongs to the operating system
            // again - it may name anything by the next signal - so the forwarder stops
            // knowing it here, ahead of the disposal that stops it listening.
            signalForwarder.DetachChild();
        }

        // The real binary's verdict is the wrapper's verdict: a failed encode reaches
        // Jellyfin as a failed encode, exactly as it would without the wrapper in place -
        // including after a forwarded stop, where the verdict is FFmpeg's own considered
        // exit and not the wrapper's impatience.
        return process.ExitCode;
    }

    /// <summary>
    /// Builds the start information for one real FFmpeg invocation.
    /// </summary>
    /// <param name="realFFmpegPath">The binary to execute.</param>
    /// <param name="arguments">Its arguments, without the executable token.</param>
    /// <returns>A start info that executes rather than interprets the vector.</returns>
    private static ProcessStartInfo BuildStartInfo(string realFFmpegPath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = realFFmpegPath,

            // The whole security property of this type: the entry point is an
            // executable and an argv, never a command line handed to a command
            // interpreter.
            UseShellExecute = false,

            // Inherited, not piped: see the type remarks.
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,

            // Only meaningful on Windows, where it keeps a console window off the
            // server's desktop; ignored on the Linux deployments this runs on.
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            if (argument is null)
            {
                throw new ArgumentException(
                    "An entry of the FFmpeg argument vector is null; a vector is built from text, not from holes.",
                    nameof(arguments));
            }

            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
