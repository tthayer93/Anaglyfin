using System;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// The status of one of the environment variables the deployment is expected to set for Anaglyfin.
/// Only the variables this feature is about are ever reported; nothing unrelated is read or
/// surfaced.
/// </summary>
/// <param name="Name">
/// The variable this entry is about, in the spelling the administrator sets it in.
/// </param>
/// <param name="Required">
/// Whether the deployment has to set it. A required variable that is not set, or that names
/// something unusable, is an <see cref="RuntimeStatusLevel.Error"/>; a recommended one is a
/// <see cref="RuntimeStatusLevel.Warning"/>.
/// </param>
/// <param name="IsSet">
/// Whether a usable value was found, counting an alias as the variable it stands for. A variable
/// exported empty reads as not set.
/// </param>
/// <param name="Value">
/// The value that was found, verbatim, or <c>null</c> when none was. For these variables a value
/// is a path; no secret ever arrives here because none of them carries one.
/// </param>
/// <param name="ResolvedPath">
/// The concrete path the plugin probed for this variable — the value when it was an absolute
/// path, the value resolved through the plugin's own rules otherwise, or <c>null</c> when there
/// was nothing to resolve.
/// </param>
/// <param name="SourceVariable">
/// The name of the variable that actually supplied the value when it was an alias of
/// <paramref name="Name"/>, or <c>null</c> when <paramref name="Name"/> itself did. A caller
/// reading only <paramref name="Name"/> would otherwise not know an alias is what the deployment set.
/// </param>
/// <param name="State">What the checks turned out to be; see <see cref="RuntimeItemState"/>.</param>
/// <param name="Detail">
/// A short, authored sentence about why the item is in <paramref name="State"/>. It names the
/// variable to change and never echoes a media path or a command vector.
/// </param>
public sealed record RuntimeEnvironmentStatus(
    string Name,
    bool Required,
    bool IsSet,
    string? Value,
    string? ResolvedPath,
    string? SourceVariable,
    RuntimeItemState State,
    string? Detail);
