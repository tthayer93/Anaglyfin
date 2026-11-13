using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
/// <b>A hard timeout that kills, and a bounded stdout answer.</b> A probe that has not answered within
/// the time it was given is killed — the process tree, not just its root — and reported as timed out.
/// After the direct process exits, the probe waits only for stdout evidence — a first line or stdout
/// EOF — with a bounded grace. If stdout has answered and only stderr remains open because a
/// descendant inherited that handle, the valid stdout answer is kept rather than discarded. A
/// best-effort kill is then attempted for any remaining process tree; when the direct process has
/// already exited, that kill may fail and is ignored because the answer is already bounded. An
/// administrator's page refresh must never be held open by a hung binary; the caller caches the answer
/// so a refresh is not a fresh fork either.
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

    /// <summary>
    /// How long the probe is allowed to wait after the process has already exited for stdout evidence:
    /// either a first line, or end-of-stream. The wait exists so a normal FFmpeg banner is not lost when
    /// its last line races the process exit; it is bounded so a surviving grandchild holding a handle
    /// open cannot pin the status pass. Stderr is drained to keep the child from blocking, but delayed
    /// stderr EOF alone must not turn a valid stdout answer into a timeout.
    /// </summary>
    public const int DefaultFinalWaitGraceMilliseconds = 2000;

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

        if (cancellationToken.IsCancellationRequested)
        {
            return RuntimeVersionProbeResult.TimedOutResult();
        }

        using var process = new Process
        {
            StartInfo = BuildStartInfo(executablePath, arguments)
        };

        var firstLine = (string?)null;

        // Stdout, not stderr, carries the version answer. After the direct process exits, the probe
        // needs stdout evidence within the bounded grace: either the first line arrived, or stdout
        // reached EOF. Stderr is still drained to keep the child from blocking, but delayed stderr
        // EOF must not discard a stdout answer.
        var stdoutEof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutLineCaptured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                stdoutEof.TrySetResult(true);
                return;
            }

            // The first line only. Once it is captured, later lines are read and dropped so the
            // pipe keeps draining without growing the result.
            if (firstLine is null)
            {
                firstLine = eventArgs.Data.Length > MaxCapturedLineCharacters
                    ? eventArgs.Data.Substring(0, MaxCapturedLineCharacters)
                    : eventArgs.Data;
                stdoutLineCaptured.TrySetResult(true);
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

        var cancelled = false;
        using var cancellation = cancellationToken.Register(
            () =>
            {
                cancelled = true;
                TryKill(process);
            });

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

        if (cancellationToken.IsCancellationRequested || cancelled)
        {
            TryKill(process);
            return RuntimeVersionProbeResult.TimedOutResult();
        }

        var finalWait = (int)Math.Clamp(DefaultFinalWaitGraceMilliseconds, 1, int.MaxValue);
        var stdoutEvidence = Task.WhenAny(stdoutLineCaptured.Task, stdoutEof.Task);
        var sawStdoutEvidence = WaitCompleted(stdoutEvidence, finalWait);

        if (cancellationToken.IsCancellationRequested || cancelled)
        {
            TryKill(process);
            return RuntimeVersionProbeResult.TimedOutResult();
        }

        // Best effort. If the direct process already exited, Kill may fail even with the whole-tree
        // option; a surviving grandchild that still holds a stderr handle is allowed to run out its
        // own life because the caller's answer is already bounded.
        TryKill(process);

        return sawStdoutEvidence
            ? RuntimeVersionProbeResult.Ran(process.ExitCode, firstLine)
            : RuntimeVersionProbeResult.TimedOutResult();
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
    /// Best-effort kill of the process tree. When the direct process has already exited, the whole-tree
    /// kill can still fail because there is no live root to enumerate; the probe's answer is already
    /// bounded at that point, so this is allowed to do nothing.
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

    private static bool WaitCompleted(Task task, int milliseconds)
    {
        try
        {
            return task.Wait(milliseconds, CancellationToken.None);
        }
        catch (Exception exception) when (IsDrainFailure(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Whether waiting on an EOF/first-line signal can be answered by "the signal did not arrive"
    /// instead of letting a status probe throw at the caller.
    /// </summary>
    private static bool IsDrainFailure(Exception exception)
        => exception is AggregateException
            or InvalidOperationException
            or NotSupportedException
            or IOException
            or ObjectDisposedException;

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
