using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// Runs a version probe as a real child process of the server, the only implementation in this
/// assembly that starts one.
/// </summary>
/// <remarks>
/// <para>
/// <b>No shell, ever, and a fixed vector.</b> The binary and its arguments are handed over through
/// <see cref="ProcessStartInfo.ArgumentList"/> with <see cref="ProcessStartInfo.UseShellExecute"/>
/// off, the same guarantee the wrapper's own launcher makes, so nothing is re-parsed out of a
/// command string. The vector a status probe runs is always short and fixed by the caller — a single
/// identity or version flag — and never a media path.
/// </para>
/// <para>
/// <b>Bounded output, on purpose.</b> Only the first line of standard output is kept, and only up to
/// <see cref="MaxCapturedLineCharacters"/> of it; everything after the first line is dropped. Standard
/// error is drained and discarded concurrently, because a process that fills an unread error pipe
/// blocks and would otherwise hang the request that is waiting on it.
/// </para>
/// <para>
/// <b>A hard timeout that kills.</b> A probe that has not answered within the time it was given is
/// killed — the process tree, not just its root — and reported as timed out. An administrator's page
/// refresh must never be held open by a hung binary; the caller caches the answer so a refresh is not
/// a fresh fork either.
/// </para>
/// <para>
/// <b>It runs as the service account.</b> This is the server's own process asking, so the probe
/// exercises exactly the rights the transcode path has — including the ones a manual
/// <c>ffmpeg -version</c> from a shell would not.
/// </para>
/// </remarks>
public sealed class ProcessRuntimeVersionProbe : IRuntimeVersionProbe
{
    /// <summary>
    /// The most characters of a process's first output line that are kept. A version answer is a
    /// single short line; anything longer is a process talking too much, and the rest is not what
    /// the probe asked for.
    /// </summary>
    public const int MaxCapturedLineCharacters = 4096;

    /// <inheritdoc />
    /// <remarks>
    /// A bare name is run as given; no path lookup is performed here, because deciding which
    /// binary a name means is the caller's job and is done before a process is ever started.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="executablePath"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is <c>null</c>.</exception>
    public RuntimeVersionProbeResult Probe(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        using var process = new Process
        {
            StartInfo = BuildStartInfo(executablePath, arguments)
        };

        var firstLine = (string?)null;

        process.OutputDataReceived += (_, eventArgs) =>
        {
            // The first line only. Once it is captured, later lines are read and dropped so the
            // pipe keeps draining without growing the result.
            if (firstLine is null && eventArgs.Data is not null)
            {
                firstLine = eventArgs.Data.Length > MaxCapturedLineCharacters
                    ? eventArgs.Data.Substring(0, MaxCapturedLineCharacters)
                    : eventArgs.Data;
            }
        };

        // Stderr is drained into nothing. If it were left unread, a chatty binary would fill the
        // pipe and block in its own write, and the wait below would then only end on the timeout.
        process.ErrorDataReceived += (_, _) =>
        {
        };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (IsStartFailure(exception))
        {
            return RuntimeVersionProbeResult.Failed(exception.GetType().Name);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cancellation = cancellationToken.Register(() => TryKill(process));

        var waited = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);

        bool exited;
        try
        {
            exited = process.WaitForExit(waited);
        }
        catch (Exception exception) when (IsStartFailure(exception))
        {
            TryKill(process);
            return RuntimeVersionProbeResult.Failed(exception.GetType().Name);
        }

        if (!exited)
        {
            TryKill(process);
            return RuntimeVersionProbeResult.TimedOutResult();
        }

        // Blocks until the asynchronous readers have hit end-of-stream, so the first line they
        // captured is visible here and the exit code is the final one.
        process.WaitForExit();

        return RuntimeVersionProbeResult.Ran(process.ExitCode, firstLine);
    }

    /// <summary>
    /// Builds the start info: an executable and a fixed argument list, redirected so the answer can
    /// be read, with no shell and no window.
    /// </summary>
    private static ProcessStartInfo BuildStartInfo(string executablePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            if (argument is null)
            {
                throw new ArgumentException(
                    "A probe argument vector is built from fixed text, never from holes.",
                    nameof(arguments));
            }

            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>
    /// Kills the process tree, ignoring the case where it already exited between the wait and here.
    /// </summary>
    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (Win32Exception)
        {
            // Already gone, or not ours to kill.
        }
    }

    /// <summary>
    /// Whether an exception from the process layer is one that means "it never ran", as opposed to
    /// a programming error the caller has to hear about.
    /// </summary>
    private static bool IsStartFailure(Exception exception)
        => exception is Win32Exception
            or InvalidOperationException
            or NotSupportedException
            or IOException
            or ObjectDisposedException;
}
