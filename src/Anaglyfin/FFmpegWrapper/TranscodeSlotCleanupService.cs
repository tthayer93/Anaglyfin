using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Clears the transcode slots a previous run of this server left behind, when the server starts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the plugin, and why at startup.</b> A slot is normally nobody's problem: it is held open by
/// the wrapper that claimed it and the operating system removes the file when that handle closes,
/// including for a wrapper that was killed. What it is not is the kernel's problem when the file
/// lives on storage that outlived the container it was written from - which is exactly where a
/// deployment puts its slot directory, because that is the only way several servers can share one
/// limit. Such a leftover refuses every 3D playback until someone notices it, and the wrapper that
/// would have taken it over only ever looks when a job asks for a slot. The one moment this process
/// knows that no Anaglyfin encode of its own can be running is the moment it starts, so that is when
/// the directory is swept, and the sweep is the plugin's job because the plugin is the half that
/// starts.
/// </para>
/// <para>
/// <b>What it removes, and only that.</b> A file is deleted when it states its owner and that owner
/// cannot be running: nobody holds the file open, and the process id its mark recorded is either
/// gone or a number from another boot or container. Everything else stays - a slot a wrapper is
/// holding, a slot whose owner this machine can still see, and above all a mark that cannot be read,
/// which says nothing and therefore proves nothing. The wrapper's own abandon check is allowed to
/// take an unreadable file over once it has aged; this pass has no age to offer and takes none, so
/// the worst outcome available to it is that a leftover it could not read survives until the first
/// 3D playback asks for the slot and the guard answers it. That is deliberate: this runs at startup
/// on a directory shared with whoever else the deployment pointed at the same path, and deleting a
/// live job's slot is the one thing no cleanup is worth.
/// </para>
/// <para>
/// <b>Why nothing here can stop the server.</b> The pass reads a directory on somebody's storage, and
/// a directory can be absent, unreadable, or in the middle of a mount. Every one of those is answered
/// by clearing what could be cleared and reporting it in the one line this service writes, because a
/// server that cannot start over a cleanup is a worse deployment than one that starts with a stale
/// slot still in it. The plugin loses nothing it owns: the slots belong to the wrapper, and the
/// wrapper still applies its own takeover rules to them on every job.
/// </para>
/// <para>
/// The lock directory is resolved through <see cref="TranscodeSlotStore"/>, which is the same
/// resolution the wrapper performs on its own environment, for the same reason the settings document
/// is written by the class the wrapper reads it with: what two processes have to agree on is
/// computed once.
/// </para>
/// </remarks>
public sealed class TranscodeSlotCleanupService : IHostedService
{
    private readonly ILogger<TranscodeSlotCleanupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeSlotCleanupService"/> class.
    /// </summary>
    /// <param name="logger">Where the one summary line of each pass goes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <c>null</c>.</exception>
    public TranscodeSlotCleanupService(ILogger<TranscodeSlotCleanupService> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Clears the provably-dead slots of this deployment's lock directory once.
    /// </summary>
    /// <param name="cancellationToken">Stops the start if the host calls it off.</param>
    /// <returns>A completed task; the pass never blocks startup on a failure.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Clean(TranscodeSlotStore.ResolveLockDirectory(Environment.GetEnvironmentVariable));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the service, which holds nothing to stop.
    /// </summary>
    /// <param name="cancellationToken">Stops the shutdown wait; there is none.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Removes the slot files of one directory that provably belong to owners that are gone, and says
    /// so once.
    /// </summary>
    /// <param name="lockDirectory">
    /// The directory to sweep. It does not have to exist, and a directory that cannot be read comes
    /// back as a pass that removed nothing rather than as a failure of the server.
    /// </param>
    /// <returns>
    /// What the pass saw, what it removed, and what stopped it if it was stopped.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Nothing here is repeated from the guard: the file-by-file question is
    /// <see cref="TranscodeSlotStore.ReadEvidence(string)"/>, and this answers only it. There is no age
    /// decision, no retry and no wait - the pass has as long as it needs, because it is not standing
    /// between a viewer and a playback.
    /// </para>
    /// <para>
    /// The one line it writes is the whole of its reporting. What a pass did to a directory the
    /// deployment - not the plugin - owns has to be readable in the server's log afterwards, and one
    /// line naming the directory and both counts is that: enough to tell a cleared leftover from a
    /// directory that was empty, and enough to tell either from a pass that could not read the
    /// directory at all.
    /// </para>
    /// </remarks>
    public TranscodeSlotCleanupReport Clean(string lockDirectory)
    {
        var seen = 0;
        var removed = 0;
        Exception? failure = null;

        try
        {
            foreach (var file in TranscodeSlotStore.EnumerateSlotFiles(lockDirectory))
            {
                seen++;

                DateTime observed;
                try
                {
                    observed = File.GetLastWriteTimeUtc(file);
                }
                catch (Exception exception) when (TranscodeSlotStore.IsFileFailure(exception))
                {
                    continue;
                }

                if (TranscodeSlotStore.ReadEvidence(file) != TranscodeSlotEvidence.DeadOwner)
                {
                    continue;
                }

                // The timestamp the decision was taken against, read again: a wrapper that claimed
                // this slot number while the evidence was being gathered wrote a new file at this
                // path, and deleting that would take a running encode's slot away from it. A file
                // that arrived since is not the file this pass judged, whatever its name says.
                if (File.GetLastWriteTimeUtc(file) != observed)
                {
                    continue;
                }

                File.Delete(file);
                removed++;
            }
        }
        catch (Exception exception) when (TranscodeSlotStore.IsFileFailure(exception))
        {
            // What was already removed stays removed; what this failure hid goes on being looked at by
            // the guard on the next job. A startup is not the place to decide what to do about a
            // directory the deployment cannot serve, so the answer is the counts and the reason.
            failure = exception;
        }

        var report = new TranscodeSlotCleanupReport(seen, removed, lockDirectory, failure);
        Report(report);

        return report;
    }

    /// <summary>
    /// Writes the one line a pass is allowed to write.
    /// </summary>
    /// <param name="report">The pass to describe.</param>
    /// <remarks>
    /// A pass that removed nothing says so at debug level, because the ordinary startup of an ordinary
    /// server clears an empty directory and an information line about it every boot is how a log stops
    /// being read.
    /// </remarks>
    private void Report(TranscodeSlotCleanupReport report)
    {
        if (report.Failure is not null)
        {
            _logger.LogWarning(
                report.Failure,
                "Anaglyfin cleared {Removed} of {Seen} stale FFmpeg transcode slot(s) in {LockDirectory} and stopped on a directory it could not read.",
                report.Removed,
                report.Seen,
                report.LockDirectory);

            return;
        }

        if (report.Removed > 0)
        {
            _logger.LogInformation(
                "Anaglyfin cleared {Removed} stale FFmpeg transcode slot(s) of {Seen} slot file(s) in {LockDirectory}; every one was left by a wrapper that is gone.",
                report.Removed,
                report.Seen,
                report.LockDirectory);

            return;
        }

        _logger.LogDebug(
            "Anaglyfin found no stale FFmpeg transcode slot to clear in {LockDirectory} ({Seen} slot file(s) seen).",
            report.LockDirectory,
            report.Seen);
    }
}
