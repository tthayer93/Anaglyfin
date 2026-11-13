using System;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// How the whole runtime looks once every check has been made: the one answer an administrator
/// reads first, before any per-item detail.
/// </summary>
/// <remarks>
/// The three levels are ordered by severity, and the overall status is the most severe a single
/// item produced. Nothing sits between them: a deployment is either usable, usable with a
/// caveat the administrator should know about, or broken in a way that costs playback.
/// </remarks>
public enum RuntimeStatusLevel
{
    /// <summary>Every required binary, environment variable and directory answered healthy.</summary>
    Ok = 0,

    /// <summary>
    /// Nothing required is broken, but something the administrator should see is not as it
    /// should be: a recommended binary is absent, a settings document has not been written,
    /// a version drifted, or a probe could not be completed.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// A required binary is missing, unreadable, not executable or not runnable, a required
    /// directory cannot be written, or the derived sidecar is invalid. Any of these costs
    /// Anaglyfin playback.
    /// </summary>
    Error = 2
}
