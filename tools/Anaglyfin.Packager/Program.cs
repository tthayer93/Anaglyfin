using System;

namespace Anaglyfin.Packager;

/// <summary>
/// The entry point of the packaging job.
/// </summary>
/// <remarks>
/// Kept as thin as the wrapper's is, for the same reason: <see cref="PackagingCommand.Run"/> is
/// the behaviour, and a thin entry point lets the test project drive the command through the
/// assembly's public surface instead of through a process. That is also why <c>Main</c> is
/// private and <c>Program</c> internal - this assembly is referenced as a library by the tests,
/// exactly as the wrapper executable is, and a second visible entry point would not compile.
/// </remarks>
internal static class Program
{
    /// <summary>
    /// Packages or verifies a directory of artifacts.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <returns>The exit code: 0 accepted, 1 refused, 2 unusable command line.</returns>
    private static int Main(string[] args) => PackagingCommand.Run(args, Console.Out, Console.Error);
}
