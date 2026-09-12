using System.Collections.Generic;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Starts the real FFmpeg binary on behalf of the wrapper and waits for it to finish.
/// </summary>
/// <remarks>
/// <para>
/// This is the one seam between the wrapper's decision and the outside world, and the
/// only type in the wrapper that starts a process. Everything the wrapper decides -
/// which vector to run, whether it is allowed to run at all, which exit code comes back
/// - is expressed against this interface, so the whole of
/// <see cref="WrapperApplication"/> is testable without an FFmpeg binary or a slot file
/// on disk.
/// </para>
/// <para>
/// A launch is synchronous by contract: the call returns the process's exit code after
/// it has exited. That is what the wrapper's two guarantees are built on - the
/// concurrency slot has to be held for as long as the encoder runs, and the code the
/// server sees has to be FFmpeg's own and not "started successfully".
/// </para>
/// <para>
/// An implementation is required to start the binary directly with an argument list
/// (never through a shell, and never by quoting a command line back together), to let
/// the child inherit the wrapper's standard streams rather than capture them, and to
/// throw rather than invent an exit code when the process could not be started at all.
/// Inheriting the streams is not a detail: Jellyfin reads FFmpeg's stderr to follow a
/// transcode, so a launcher that swallows it leaves the server watching a job that looks
/// stuck.
/// </para>
/// </remarks>
public interface IFFmpegProcessLauncher
{
    /// <summary>
    /// Starts one real FFmpeg process and waits for it to exit.
    /// </summary>
    /// <param name="realFFmpegPath">
    /// The configured FFmpeg binary, as resolved by
    /// <see cref="FFmpegWrapperOptions.RealFFmpegPath"/>.
    /// </param>
    /// <param name="arguments">
    /// The argument vector to start it with, without the executable token - the vector
    /// the rewriter returned, either unchanged or rewritten.
    /// </param>
    /// <returns>The exit code of the process that ran.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="realFFmpegPath"/> is blank, or a vector entry is null.
    /// </exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    /// <exception cref="System.InvalidOperationException">The process could not be started.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// The binary is not executable. Reported, never turned into a exit code of its own:
    /// only a process that actually ran gets to decide the wrapper's exit code.
    /// </exception>
    int Launch(string realFFmpegPath, IReadOnlyList<string> arguments);
}
