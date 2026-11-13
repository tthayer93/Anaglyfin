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
/// is a refusal and not a crash. The one decision it does make for itself is to let
/// <see cref="WrapperApplication.VersionCommand"/> out before any of that wiring runs,
/// so the identity answer does not depend on a configuration being readable.
/// </para>
/// <para>
/// The wiring is the whole deployment contract: the server's FFmpeg path points at this
/// executable, the binary an Anaglyfin command is handed to comes from the environment
/// (<see cref="FFmpegWrapperOptions.RealFFmpegEnvironmentVariable"/>) and the one an ordinary
/// command is handed to from
/// (<see cref="FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable"/>) - or from the same
/// place as the first when the deployment named no second build - and one process is one
/// playback, which is why <see cref="WrapperApplication.Run"/> waits for FFmpeg and leaves its
/// exit code behind.
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
        try
        {
            // The identity question is answered here, ahead of everything else this executable
            // does - ahead of the environment read below, so that a deployment with no FFmpeg
            // path, no slot directory and no settings document still gets an answer. It is the one
            // invocation that asks who the wrapper is rather than asking it to run something, and
            // the caller asking it is looking at a deployment nobody trusts yet. It sits inside
            // this try for the same reason as everything after it: the one way writing the line
            // can fail is a stdout nobody will take, and that is a refusal with an exit code the
            // server reads, not a stack trace.
            if (WrapperApplication.TryReportVersion(args, Console.Out))
            {
                return WrapperApplication.ExitCodeSuccess;
            }

            var options = FFmpegWrapperOptions.FromEnvironment();

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
