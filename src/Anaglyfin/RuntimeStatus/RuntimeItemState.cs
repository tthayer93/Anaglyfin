using System;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// What a single runtime item — one binary, one environment variable, one directory — turned
/// out to be. The reason for a state that is not simply good or bad lives here, so the page can
/// name the thing to fix rather than only that something is wrong.
/// </summary>
/// <remarks>
/// <para>
/// These states are about a fact the plugin observed, not about severity: whether an item is
/// tolerated is decided by the checks that know whether the item was required, and not by the
/// state name alone. <see cref="Warning"/> is the state for an item that is present but should
/// be brought to the administrator's attention; the rest name a specific failure so the wording
/// can be specific too.
/// </para>
/// <para>
/// <see cref="Unknown"/> is the honest answer for a question the plugin could not ask — a
/// platform check that has no meaning here, or a state it cannot observe — and it is never used
/// as a stand-in for a failure the plugin could have detected and chose not to.
/// </para>
/// </remarks>
public enum RuntimeItemState
{
    /// <summary>The item is present and did everything its check asked of it.</summary>
    Ok = 0,

    /// <summary>The item is not there: no path was configured, or the configured path is no file.</summary>
    Missing = 1,

    /// <summary>The item is there but could not be opened for reading by the service account.</summary>
    NotReadable = 2,

    /// <summary>The item is there and readable but could not be run (the execute bit is off on Unix).</summary>
    NotExecutable = 3,

    /// <summary>The item's directory could not be written by the service account.</summary>
    NotWritable = 4,

    /// <summary>
    /// A version probe could not be completed, or completed with something that was not the
    /// answer the item had to give. A timeout and a binary that is simply not what it was said
    /// to be both land here; the <c>Detail</c> tells them apart.
    /// </summary>
    ProbeFailed = 5,

    /// <summary>
    /// The item answered as itself, but its version is not the one it should carry — the wrapper
    /// reporting a release other than the plugin's own.
    /// </summary>
    VersionMismatch = 6,

    /// <summary>The item is present but should be brought to the administrator's attention.</summary>
    Warning = 7,

    /// <summary>A state the plugin cannot observe on this platform or from this position.</summary>
    Unknown = 8
}
