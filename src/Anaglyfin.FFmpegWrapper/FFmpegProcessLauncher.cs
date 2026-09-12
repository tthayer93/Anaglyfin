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
/// </remarks>
public sealed class FFmpegProcessLauncher : IFFmpegProcessLauncher
{
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

        process.Start();
        process.WaitForExit();

        // The real binary's verdict is the wrapper's verdict: a failed encode reaches
        // Jellyfin as a failed encode, exactly as it would without the wrapper in place.
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
