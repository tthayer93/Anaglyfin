using System.Collections.Generic;
using Anaglyfin.FFmpegWrapper;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Stands in for a stop-signal forwarder, recording the launcher's etiquette around it.
/// </summary>
/// <remarks>
/// <para>
/// A launcher cannot be tested on its signal handling by receiving signals - a unit test
/// suite that can be sent <c>SIGTERM</c> is a unit test suite that dies halfway - so it is
/// tested on the only signal behaviour it owns: which child it puts under protection,
/// when, and for how long. This recorder turns that into two lists: the ordered sequence
/// of things it was asked to do, and the process ids it was told about. A launch that
/// starts a child and ends it must read attach-detach-dispose with one positive id
/// attached, and a launch that never got a child must never have named one at all.
/// </para>
/// <para>
/// The recorder also proves the lifetime by existing: the launcher created it through its
/// factory, so seeing it once in the factory's output means the launcher asked for a
/// forwarder per launch, and seeing <c>IsDisposed</c> means it gave the listener back
/// instead of leaving handlers installed past the playback they belonged to.
/// </para>
/// </remarks>
public sealed class FakeChildSignalForwarder : IChildSignalForwarder
{
    private readonly List<string> _events = new();
    private readonly List<int> _attachedProcessIds = new();

    /// <summary>Gets everything this forwarder was asked to do, in order.</summary>
    /// <remarks>Entries are <c>attach</c>, <c>detach</c> and <c>dispose</c>.</remarks>
    public IReadOnlyList<string> Events => _events;

    /// <summary>Gets every process id this forwarder was told to protect, in order.</summary>
    public IReadOnlyList<int> AttachedProcessIds => _attachedProcessIds;

    /// <summary>Gets the child currently under protection, or null when none is.</summary>
    public int? AttachedChildProcessId { get; private set; }

    /// <summary>Gets whether the launcher gave the forwarder back.</summary>
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public void AttachChild(int childProcessId)
    {
        _events.Add("attach");
        _attachedProcessIds.Add(childProcessId);
        AttachedChildProcessId = childProcessId;
    }

    /// <inheritdoc />
    public void DetachChild()
    {
        _events.Add("detach");
        AttachedChildProcessId = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _events.Add("dispose");
        IsDisposed = true;
    }
}
