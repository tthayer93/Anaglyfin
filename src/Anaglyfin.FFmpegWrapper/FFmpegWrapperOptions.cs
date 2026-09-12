using System;
using System.Globalization;
using System.IO;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// The configuration of one wrapper invocation, read from the process environment.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper is a separate process started by Jellyfin once per playback, so it has
/// no access to the plugin's configuration, its container, or its logging. The
/// environment is the only channel that reaches it, and these are the three things it
/// needs: which real binary to hand the command to, how many Anaglyfin transcodes may
/// run at once, and where those jobs announce themselves.
/// </para>
/// <para>
/// <b>Nothing here is a path the code invents.</b> The real FFmpeg binary is always a
/// value an administrator set, or the plain name <c>ffmpeg</c> resolved through
/// <c>PATH</c> by the operating system; no project directory, install directory or
/// host path is ever assumed. That is also what makes the wrapper testable: every
/// value is a parameter, and <see cref="FromEnvironment(Func{string,string?})"/> takes
/// the environment itself as a delegate, so a test can describe a machine without
/// mutating the one it runs on.
/// </para>
/// <para>
/// <b>Bad configuration degrades to the safe default rather than to a crash.</b> The
/// wrapper is on the path of every transcode on the server, including the ordinary
/// ones it must not touch; a typo in <see cref="MaxConcurrentTranscodesEnvironmentVariable"/>
/// may fail the concurrency knob, but it must not stop playback. An unusable
/// <see cref="RealFFmpegEnvironmentVariable"/> is the exception: it is not a tuning
/// value but the binary itself, so it is reported by
/// <see cref="WrapperApplication"/> instead of being quietly replaced.
/// </para>
/// </remarks>
public sealed record FFmpegWrapperOptions
{
    /// <summary>
    /// Name of the variable holding the maximum number of concurrent Anaglyfin
    /// transcodes. Mirrors the admin setting of the same name (decision 10: default 1).
    /// </summary>
    public const string MaxConcurrentTranscodesEnvironmentVariable = "ANAGLYFIN_MAX_CONCURRENT_TRANSCODES";

    /// <summary>
    /// Name of the variable holding the directory the concurrency slots are created in.
    /// </summary>
    public const string LockDirectoryEnvironmentVariable = "ANAGLYFIN_LOCK_DIR";

    /// <summary>
    /// Name of the variable naming the real FFmpeg-mvc binary the wrapper hands commands to.
    /// </summary>
    public const string RealFFmpegEnvironmentVariable = "ANAGLYFIN_REAL_FFMPEG";

    /// <summary>
    /// Name of the variable naming the real binary when
    /// <see cref="RealFFmpegEnvironmentVariable"/> is unset. The wrapper is installed as
    /// the server's FFmpeg, so the same variable that points the deployment at the
    /// FFmpeg-mvc build is the one that already names it.
    /// </summary>
    public const string RealFFmpegAlternateEnvironmentVariable = "FFMPEG_MVC_PATH";

    /// <summary>The bare name looked up on <c>PATH</c> when no variable names the binary.</summary>
    public const string FFmpegExecutableName = "ffmpeg";

    /// <summary>The name of the <c>PATH</c> variable itself.</summary>
    public const string PathEnvironmentVariable = "PATH";

    /// <summary>How many Anaglyfin transcodes may run at once when nothing is configured.</summary>
    public const int DefaultMaxConcurrentTranscodes = 1;

    /// <summary>Label used when the binary came from a <c>PATH</c> lookup rather than a variable.</summary>
    public const string PathLookupSource = "PATH lookup";

    /// <summary>
    /// Gets the maximum number of Anaglyfin transcodes allowed at the same time.
    /// </summary>
    /// <remarks>
    /// Always at least one: a zero or negative limit would refuse every Anaglyfin job,
    /// which is a configuration mistake rather than a policy anyone configures on purpose.
    /// </remarks>
    public int MaxConcurrentTranscodes { get; init; } = DefaultMaxConcurrentTranscodes;

    /// <summary>
    /// Gets the directory the concurrency slot files are created in.
    /// </summary>
    public string LockDirectory { get; init; } = DefaultLockDirectory;

    /// <summary>
    /// Gets the real FFmpeg binary every command is handed to.
    /// </summary>
    /// <remarks>
    /// Either an absolute path an administrator configured, or the bare name
    /// <see cref="FFmpegExecutableName"/> left for the operating system to resolve.
    /// Never the wrapper itself: the wrapper is what is running.
    /// </remarks>
    public string RealFFmpegPath { get; init; } = FFmpegExecutableName;

    /// <summary>
    /// Gets where <see cref="RealFFmpegPath"/> came from, for the wording of a refusal.
    /// </summary>
    /// <remarks>
    /// The variable name and nothing else: a wrapper log line is not the place to
    /// reprint the server's filesystem layout, while the variable name is exactly what
    /// an administrator has to go and fix.
    /// </remarks>
    public string RealFFmpegPathSource { get; init; } = PathLookupSource;

    /// <summary>
    /// Gets the directory used when <see cref="LockDirectoryEnvironmentVariable"/> is unset.
    /// </summary>
    /// <remarks>
    /// Under the temp path, because a slot is a runtime artefact and not state worth
    /// keeping: the operating system removes it with the process that created it, so a
    /// default location on a rebooted or cleaned machine can only ever be empty.
    /// An absolute, shared location is what a multi-container deployment configures.
    /// </remarks>
    public static string DefaultLockDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "anaglyfin", "ffmpeg-wrapper");

    /// <summary>
    /// Reads the options of the current process environment.
    /// </summary>
    /// <returns>The options to run one wrapper invocation with.</returns>
    public static FFmpegWrapperOptions FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Reads the options from an arbitrary environment view.
    /// </summary>
    /// <param name="readVariable">
    /// Reads one environment variable; null when it is not set. Supplied by callers so
    /// that the resolution order below is testable without changing the process.
    /// </param>
    /// <returns>The options those variables describe.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="readVariable"/> is null.</exception>
    public static FFmpegWrapperOptions FromEnvironment(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        var (path, source) = ResolveRealFFmpeg(readVariable);

        return new FFmpegWrapperOptions
        {
            MaxConcurrentTranscodes = ReadMaximum(readVariable(MaxConcurrentTranscodesEnvironmentVariable)),
            LockDirectory = ReadLockDirectory(readVariable(LockDirectoryEnvironmentVariable)),
            RealFFmpegPath = path,
            RealFFmpegPathSource = source
        };
    }

    /// <summary>
    /// Resolves the real FFmpeg binary from an environment view.
    /// </summary>
    /// <param name="readVariable">Reads one environment variable; null when it is not set.</param>
    /// <returns>The path to start.</returns>
    /// <remarks>
    /// The order is <see cref="RealFFmpegEnvironmentVariable"/>, then
    /// <see cref="RealFFmpegAlternateEnvironmentVariable"/>, then
    /// <see cref="FFmpegExecutableName"/> on <c>PATH</c>. The Anaglyfin-specific variable
    /// comes first because it is the one that exists to name the wrapper's target; the
    /// deployment variable is honoured second so a normal FFmpeg-mvc installation needs
    /// no second setting; the <c>PATH</c> lookup is the last resort of a host that
    /// installed the binary in a standard location.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="readVariable"/> is null.</exception>
    public static string ResolveRealFFmpegPath(Func<string, string?> readVariable)
        => ResolveRealFFmpeg(readVariable).Path;

    /// <summary>
    /// Resolves the real FFmpeg binary together with the name of its source.
    /// </summary>
    private static (string Path, string Source) ResolveRealFFmpeg(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        var configured = ReadNonBlank(readVariable(RealFFmpegEnvironmentVariable));
        if (configured is not null)
        {
            return (configured, RealFFmpegEnvironmentVariable);
        }

        var deployed = ReadNonBlank(readVariable(RealFFmpegAlternateEnvironmentVariable));
        if (deployed is not null)
        {
            return (deployed, RealFFmpegAlternateEnvironmentVariable);
        }

        // Nothing named the binary, so the host's own executable search names it. The
        // name is returned unchanged when the search finds nothing, which keeps the
        // failure the operating system's - "no such file or directory" - rather than a
        // guess about where FFmpeg might have been installed.
        return (FindOnPath(readVariable) ?? FFmpegExecutableName, PathLookupSource);
    }

    /// <summary>
    /// Looks the FFmpeg executable up in the <c>PATH</c> of the same environment view.
    /// </summary>
    /// <param name="readVariable">Reads one environment variable; null when it is not set.</param>
    /// <returns>The first matching file, or null when the search has no result.</returns>
    /// <remarks>
    /// Only existing files in the search entries are considered, in order. This is the
    /// wrapper's own read of the same information the <c>exec</c> layer would use; it
    /// exists so that "which binary am I about to start" is answerable from the
    /// configuration alone instead of as a side effect of starting a process.
    /// </remarks>
    private static string? FindOnPath(Func<string, string?> readVariable)
    {
        var searchPath = ReadNonBlank(readVariable(PathEnvironmentVariable));
        if (searchPath is null)
        {
            return null;
        }

        foreach (var entry in searchPath.Split(Path.PathSeparator))
        {
            // A PATH entry is a directory, and an empty entry means the working
            // directory in POSIX - a directory the wrapper does not control and will
            // not search.
            var directory = entry.Trim();
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var candidate in ExecutableCandidates(directory))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The file names one PATH directory could provide the executable under.
    /// </summary>
    private static string[] ExecutableCandidates(string directory)
    {
        var plain = Path.Combine(directory, FFmpegExecutableName);

        // The extension is what Windows's search adds; on Unix a ".exe" sibling would be
        // an unrelated file, so it is only offered where it means something.
        return OperatingSystem.IsWindows()
            ? new[] { plain, plain + ".exe" }
            : new[] { plain };
    }

    /// <summary>
    /// Reads the concurrency limit, falling back to the shipped default.
    /// </summary>
    /// <param name="value">The raw variable value, if any.</param>
    /// <returns>At least <see cref="DefaultMaxConcurrentTranscodes"/>.</returns>
    /// <remarks>
    /// Unset, blank, unparseable and non-positive all land on the default, and the
    /// difference between them is not visible to the wrapper: every one of them is a
    /// knob an administrator set wrong, and the shipped default is the safe reading.
    /// </remarks>
    private static int ReadMaximum(string? value)
    {
        var text = ReadNonBlank(value);
        if (text is null
            || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1)
        {
            return DefaultMaxConcurrentTranscodes;
        }

        return parsed;
    }

    /// <summary>
    /// Reads the slot directory, falling back to the temp location.
    /// </summary>
    /// <param name="value">The raw variable value, if any.</param>
    /// <returns>An absolute directory path.</returns>
    /// <remarks>
    /// A relative value is anchored at the temp location instead of being used as
    /// written: the wrapper inherits its working directory from whatever started it, and
    /// Jellyfin starts transcode helpers in its transcode temp. A concurrency limit that
    /// silently lives in a directory nobody chose is a limit nobody can find.
    /// </remarks>
    private static string ReadLockDirectory(string? value)
    {
        var text = ReadNonBlank(value);

        if (text is null)
        {
            return DefaultLockDirectory;
        }

        return Path.IsPathRooted(text) ? text : Path.Combine(DefaultLockDirectory, text);
    }

    private static string? ReadNonBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
