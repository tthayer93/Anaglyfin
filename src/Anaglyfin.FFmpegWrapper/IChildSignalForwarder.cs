using System;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Stands by while one FFmpeg child runs, so a stop signal aimed at the wrapper can be
/// handed on to that child.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this answers.</b> The wrapper sits where the server used to start
/// FFmpeg itself, which also moves the server's stop path onto the wrapper: a
/// <c>SIGTERM</c> from systemd, a <c>SIGINT</c> from a terminal, a <c>SIGHUP</c> from a
/// dying session - these now arrive at the wrapper's process id and very often at nobody
/// else's. Without a forwarder the wrapper dies exactly as any process dies from an
/// unhandled signal, and the FFmpeg it started keeps encoding for a client that has
/// already gone: burning a decoder, a concurrency slot's worth of expectation, and disk
/// on HLS segments nobody will serve again. Forwarding is the one thing that makes the
/// wrapper transparent on the way out as well as on the way in - the stop signal reaches
/// the encoder, as it would with no wrapper at all.
/// </para>
/// <para>
/// <b>Forward, then wait; never kill.</b> Handing over the signal is the whole
/// intervention. What FFmpeg does with it - <c>SIGTERM</c> finalises the current segment
/// and the playlist, <c>SIGINT</c> interrupts, <c>SIGHUP</c> ends - is FFmpeg's and the
/// server's business, not the wrapper's; it exits with its own verdict and the wrapper
/// relays that verdict as its exit code. A wrapper that killed its child would be
/// replacing FFmpeg's considered stop with <c>SIGKILL</c>'s silence.
/// </para>
/// <para>
/// <b>Only while a child exists.</b> Outside the window between a child's start and its
/// exit there is nobody to speak for, and the wrapper has no business changing what a
/// signal does to an idle process: it stops exactly as FFmpeg would stop. An
/// implementation must therefore treat "no child" as "do nothing", and must never hand a
/// signal to a process id it learned from a child that has already exited - ids are
/// reused, and a stale one may by then name somebody else's process.
/// </para>
/// </remarks>
public interface IChildSignalForwarder : IDisposable
{
    /// <summary>
    /// Names the running child that stop signals are to be handed to from now on.
    /// </summary>
    /// <param name="childProcessId">
    /// The process id of the child, as the operating system knows it.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="childProcessId"/> is not a positive number.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A child is already attached: one wrapper starts one FFmpeg, so a second child on
    /// one forwarder is a caller bug, not a second encoder.
    /// </exception>
    void AttachChild(int childProcessId);

    /// <summary>
    /// Forgets the child, because it has exited - from now on there is nobody to signal,
    /// and the id must never be used again even if it is still numerically valid.
    /// </summary>
    /// <remarks>Idempotent: detaching what is already detached is a no-op.</remarks>
    void DetachChild();
}
