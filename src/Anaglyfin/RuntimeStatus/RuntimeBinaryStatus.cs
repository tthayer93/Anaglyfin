using System;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// The status of one runtime binary the plugin can name: the wrapper, FFmpeg-mvc, the official
/// FFmpeg the server would run, and the derived <c>ffprobe</c> sidecar.
/// </summary>
/// <param name="Key">
/// A stable, machine-readable identifier for the binary, so a reader can find the same entry
/// across builds regardless of the label's wording.
/// </param>
/// <param name="Label">A human-readable name for the binary, for the page.</param>
/// <param name="ConfiguredPath">
/// The path an environment variable named for this binary, or <c>null</c> when no variable named
/// one. This is what the administrator set, verbatim.
/// </param>
/// <param name="ResolvedPath">
/// The path the plugin actually probed, or <c>null</c> when there was nothing to probe. For a
/// binary resolved off <c>PATH</c> or derived from another path, this is where that left it.
/// </param>
/// <param name="Version">
/// The version read out of a successful probe, or <c>null</c> when the probe yielded no version.
/// </param>
/// <param name="Required">
/// Whether Anaglyfin playback needs this binary. A required item that fails is an
/// <see cref="RuntimeStatusLevel.Error"/>; a recommended one is a
/// <see cref="RuntimeStatusLevel.Warning"/>.
/// </param>
/// <param name="State">What the checks turned out to be; see <see cref="RuntimeItemState"/>.</param>
/// <param name="Detail">
/// A short, authored sentence about why the item is in <paramref name="State"/>, naming the
/// variable an administrator can change. It never carries a media path or a command vector.
/// </param>
public sealed record RuntimeBinaryStatus(
    string Key,
    string Label,
    string? ConfiguredPath,
    string? ResolvedPath,
    string? Version,
    bool Required,
    RuntimeItemState State,
    string? Detail);
