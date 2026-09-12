using System;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// The entry point of the Anaglyfin FFmpeg wrapper executable.
/// </summary>
/// <remarks>
/// <para>
/// This type is deliberately almost empty: reading the environment, taking the slot and
/// starting FFmpeg are all behaviour of the types it wires together, so the executable's
/// build has exactly one behaviour of its own left to test - that an unexpected failure
/// is a refusal and not a crash.
/// </para>
/// <para>
/// The wiring is the whole deployment contract: the server's FFmpeg path points at this
/// executable, the real binary comes from the environment
/// (<see cref="FFmpegWrapperOptions.RealFFmpegEnvironmentVariable"/>), and one process is
/// one playback - which is why <see cref="WrapperApplication.Run"/> waits for FFmpeg and
/// leaves its exit code behind.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// Wraps one FFmpeg invocation.
    /// </summary>
    /// <param name="args">The arguments the server started the wrapper with.</param>
    /// <returns>The exit code the server should read.</returns>
    private static int Main(string[] args)
    {
        var options = FFmpegWrapperOptions.FromEnvironment();

        try
        {
            using var guard = new WrapperConcurrencyGuard(options);
            var wrapper = new WrapperApplication(options, new FFmpegProcessLauncher(), guard);

            return wrapper.Run(args);
        }

#pragma warning disable CA1031 // Deliberate: the last line of a process that must not crash.
        catch (Exception exception)
        {
            // An unhandled exception here would be a stack trace in the middle of a
            // transcode log and an exit code the server has no meaning for. The wrapper
            // has not started anything it did not already account for, so the useful
            // behaviour is the one the rest of this executable uses: say what happened in
            // one line, and refuse.
            Console.Error.WriteLine(
                $"{WrapperApplication.DiagnosticPrefix}refused: the wrapper stopped unexpectedly ({exception.GetType().Name}).");

            return WrapperApplication.ExitCodeInternalError;
        }
#pragma warning restore CA1031
    }
}
