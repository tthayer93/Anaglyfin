using System;

namespace Anaglyfin.Packager;

/// <summary>
/// The refusal of the packaging job: a reason why no artifact may be handed to a server.
/// </summary>
/// <remarks>
/// <para>
/// Everything the job emits is meant to be installed on somebody's media server and
/// restarted against, so the useful failure is the early one. Every check in this project
/// ends in this exception rather than in a warning on a console, and the command turns it
/// into a non-zero exit code, which is what makes the CI packaging step a gate and not a
/// report.
/// </para>
/// </remarks>
public sealed class PackagingError : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PackagingError"/> class.</summary>
    public PackagingError()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PackagingError"/> class.
    /// </summary>
    /// <param name="message">What was refused, and why.</param>
    public PackagingError(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PackagingError"/> class.
    /// </summary>
    /// <param name="message">What was refused, and why.</param>
    /// <param name="innerException">The failure that made packaging impossible.</param>
    public PackagingError(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
