using System;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// The forwarder for platforms that have no POSIX signals: it attaches, detaches and
/// disposes, hears nothing, and sends nothing.
/// </summary>
/// <remarks>
/// <para>
/// Windows is the deployment this exists for. There is no wrong answer to "forward the
/// <c>SIGTERM</c> you received" on a platform where the question cannot be asked, and the
/// runtime makes the asking itself fail - so the wrapper's job is to not ask: not to
/// register, not to send, not to mention signals anywhere a Windows operator would read
/// about it. Stopping a Windows FFmpeg is the server's own affair, exactly as it was
/// before this wrapper existed.
/// </para>
/// <para>
/// Doing nothing is stated rather than left absent because every other caller - the
/// launcher attaching a child, the tests walking the same sequence on every platform -
/// then runs one code path and the platform difference lives in exactly one decision: the
/// one <see cref="PosixSignalForwarder.Create"/> makes.
/// </para>
/// </remarks>
internal sealed class NoOpChildSignalForwarder : IChildSignalForwarder
{
    /// <inheritdoc />
    /// <remarks>Goes unheard; the child has no forwarder on this platform.</remarks>
    public void AttachChild(int childProcessId)
    {
        _ = childProcessId;
    }

    /// <inheritdoc />
    public void DetachChild()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
