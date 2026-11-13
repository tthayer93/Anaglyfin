using System;
using System.Collections.Generic;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// The live runtime status of an Anaglyfin installation: which binaries are in place and run,
/// which environment variables the deployment set, and whether the directories they name are
/// usable by the service account.
/// </summary>
/// <remarks>
/// <para>
/// This is a snapshot, taken when <see cref="CheckedAtUtc"/> says it was, and held for a short
/// while so that a page refreshing does not re-probe the binaries on every request. It is
/// deliberately read-only: nothing here is a setting, and nothing here is written back.
/// </para>
/// <para>
/// Every string on it is authored by the checks rather than lifted from the environment or from a
/// process's output, so a misconfigured deployment cannot put its own text — a media path, a
/// command vector — onto the page. The only environment values carried are the binary and
/// directory paths the deployment itself set, which is what the administrator needs to see.
/// </para>
/// </remarks>
/// <param name="OverallStatus">The most severe any single item produced.</param>
/// <param name="PluginVersion">
/// The version this plugin assembly was built as, in the <c>X.Y.Z</c> form a release states. The
/// wrapper's own identity is compared against it.
/// </param>
/// <param name="CheckedAtUtc">When this snapshot was taken, in UTC.</param>
/// <param name="Binaries">
/// The binaries the plugin can name and probe, in a fixed order: the wrapper, FFmpeg-mvc, the
/// official FFmpeg, and the derived <c>ffprobe</c> sidecar.
/// </param>
/// <param name="Environment">
/// The environment variables this feature is about, in a fixed order. Nothing unrelated is ever
/// added.
/// </param>
public sealed record RuntimeStatusResponse(
    RuntimeStatusLevel OverallStatus,
    string PluginVersion,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<RuntimeBinaryStatus> Binaries,
    IReadOnlyList<RuntimeEnvironmentStatus> Environment);
