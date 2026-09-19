using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// The whole behaviour of one wrapper invocation: decide, allow, start, report.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper sits where Jellyfin would otherwise start FFmpeg, so it is on the path of
/// every transcode on the server and must be indistinguishable from FFmpeg in three ways:
/// the arguments a normal playback gets are the arguments it receives, the streams the
/// child sees are the streams the server gave the wrapper, and the exit code the server
/// reads is FFmpeg's. Everything else here is about the one thing that is different - an
/// Anaglyfin job - and about never letting that difference reach an ordinary playback.
/// </para>
/// <para>
/// <b>Three outcomes, and only one of them starts a process.</b> A command the rewriter
/// passed through is started unchanged and takes no concurrency slot: the server's own
/// scheduling already governs ordinary playback, and the wrapper taking a slot for it
/// would let normal transcodes starve the 3D versions the limit exists for. A command the
/// rewriter rewrote is an Anaglyfin job, so it takes a slot first and refuses itself
/// before FFmpeg is reached if the limit is already spent. A command the rewriter refused
/// - a marker that parsed to nothing, an id off the allowlist, a shape the rewrite cannot
/// place - is not started at all: running the received vector would put a marker URL in
/// front of FFmpeg as a media file, which is the one outcome the marker contract exists to
/// prevent, and it would do it while reporting success.
/// </para>
/// <para>
/// <b>What reaches the log.</b> A refusal says which rule the command broke and what the
/// administrator can change, and nothing else. Argument vectors are never echoed - they
/// arrive from a playback request, and the rewriter's own contract is that a refusal names
/// a class of problem rather than a piece of the request. Marker text, profile ids and
/// media paths do not appear in wrapper output; configured variable names do, because those
/// are the strings an administrator can act on.
/// </para>
/// </remarks>
public sealed class WrapperApplication
{
    /// <summary>FFmpeg's own success code, passed through.</summary>
    public const int ExitCodeSuccess = 0;

    /// <summary>
    /// Exit code for a command the rewriter refused. Chosen as <c>EX_DATAERR</c>: the
    /// request itself was not usable, and nothing about the server needs repairing.
    /// </summary>
    public const int ExitCodeMarkerRejected = 65;

    /// <summary>
    /// Exit code for an Anaglyfin job refused by the concurrency limit. Chosen as
    /// <c>EX_TEMPFAIL</c>: the same request may succeed once the running job finishes, and
    /// that difference is what makes retrying it the server's or the user's decision
    /// rather than the wrapper's.
    /// </summary>
    public const int ExitCodeConcurrencyLimitReached = 75;

    /// <summary>
    /// Exit code when the configured FFmpeg binary could not be executed - absent, not
    /// executable, or the wrapper pointing at itself. <c>127</c> because it is the code a
    /// shell gives "the command you configured is not there".
    /// </summary>
    public const int ExitCodeRealFFmpegNotStarted = 127;

    /// <summary>
    /// Exit code when the wrapper could not reach a decision at all. <c>EX_SOFTWARE</c>,
    /// and always a refusal to start anything: a wrapper that cannot tell a marker from an
    /// ordinary path does not get to guess one into FFmpeg.
    /// </summary>
    public const int ExitCodeInternalError = 70;

    /// <summary>Prefix of every line the wrapper writes, so it is attributable in FFmpeg's own log stream.</summary>
    public const string DiagnosticPrefix = "anaglyfin-wrapper: ";

    private readonly FFmpegWrapperOptions _options;
    private readonly IFFmpegProcessLauncher _launcher;
    private readonly WrapperConcurrencyGuard _guard;
    private readonly IWrapperArgumentRewriter _rewriter;
    private readonly TextWriter _diagnostics;

    /// <summary>
    /// Wires one wrapper invocation.
    /// </summary>
    /// <param name="options">The configuration this invocation runs under.</param>
    /// <param name="launcher">How the real FFmpeg is started.</param>
    /// <param name="guard">The concurrency limit, over the directory of <paramref name="options"/>.</param>
    /// <param name="rewriter">
    /// The command rewriter; null uses <see cref="WrapperArgumentRewriter.Shared"/>, which
    /// is what the executable runs, since out of process there is no container to ask.
    /// </param>
    /// <param name="diagnostics">
    /// Where refusals are written; null uses <see cref="Console.Error"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">Any of the first three arguments is null.</exception>
    public WrapperApplication(
        FFmpegWrapperOptions options,
        IFFmpegProcessLauncher launcher,
        WrapperConcurrencyGuard guard,
        IWrapperArgumentRewriter? rewriter = null,
        TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(guard);

        _options = options;
        _launcher = launcher;
        _guard = guard;
        _rewriter = rewriter ?? WrapperArgumentRewriter.Shared;
        _diagnostics = diagnostics ?? Console.Error;
    }

    /// <summary>
    /// Runs one wrapper invocation and returns the code the server should see.
    /// </summary>
    /// <param name="rawArguments">
    /// The arguments as the wrapper received them - see
    /// <see cref="NormalizeIncomingArguments"/> for what counts as an argument.
    /// </param>
    /// <returns>
    /// The real FFmpeg's exit code when it ran, and one of the refusal codes when it did
    /// not. This method does not throw: a wrapper that dies with a stack trace is a
    /// transcode failure with a worse error message.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="rawArguments"/> is null.</exception>
    public int Run(IReadOnlyList<string> rawArguments)
    {
        ArgumentNullException.ThrowIfNull(rawArguments);

        try
        {
            return Execute(rawArguments);
        }
        catch (IOException exception)
        {
            return RefuseInternally(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return RefuseInternally(exception);
        }
    }

    /// <summary>
    /// Decides and executes one invocation.
    /// </summary>
    /// <param name="rawArguments">The received arguments.</param>
    /// <returns>The exit code to report.</returns>
    private int Execute(IReadOnlyList<string> rawArguments)
    {
        var rewrite = _rewriter.Rewrite(NormalizeIncomingArguments(rawArguments), _options.SubtitleDepth);

        if (!rewrite.IsSuccess)
        {
            return RefuseRewrite(rewrite);
        }

        // What the rewrite declined, and why. A rewrite that carried on without something it was
        // asked for has already decided that the request is not worth a playback - a subtitle depth
        // the command's filter graph has no shape for, say - and the only thing left to decide is
        // whether anybody finds out afterwards. That is decided here, because this is the only place
        // the wrapper has a log to write to.
        foreach (var warning in rewrite.Warnings)
        {
            Report($"warning: {warning}");
        }

        if (!TryRealFFmpegPath(out var realFFmpegPath, out var reason))
        {
            Report($"refused: {reason}");
            return ExitCodeRealFFmpegNotStarted;
        }

        // Only an Anaglyfin job is counted. A passed-through command is ordinary playback,
        // and the limit is a budget for MVC encoding, not a gate on the server's traffic.
        var takesSlot = rewrite.Status == WrapperRewriteStatus.Rewritten;
        if (takesSlot && !_guard.TryAcquire())
        {
            Report(ConcurrencyRefusal());
            return ExitCodeConcurrencyLimitReached;
        }

        try
        {
            return StartRealFFmpeg(realFFmpegPath, rewrite);
        }
        finally
        {
            // The slot has to outlive the encoder, so it is given back here and nowhere
            // earlier; and it is given back even when the encoder could not be started,
            // because a slot that leaks outlives this invocation.
            if (takesSlot)
            {
                _guard.Release();
            }
        }
    }

    /// <summary>
    /// Starts the real binary and turns a failed start into a refusal.
    /// </summary>
    /// <param name="realFFmpegPath">The configured binary.</param>
    /// <param name="rewrite">The decision whose vector to run.</param>
    /// <returns>The exit code of the process that ran.</returns>
    private int StartRealFFmpeg(string realFFmpegPath, WrapperRewriteResult rewrite)
    {
        try
        {
            return _launcher.Launch(realFFmpegPath, rewrite.Arguments);
        }
        catch (Win32Exception exception)
        {
            return RefuseStart(exception);
        }
        catch (IOException exception)
        {
            return RefuseStart(exception);
        }
        catch (InvalidOperationException exception)
        {
            return RefuseStart(exception);
        }
        catch (NotSupportedException exception)
        {
            return RefuseStart(exception);
        }
    }

    /// <summary>
    /// Checks that the configured binary is a file this process could execute.
    /// </summary>
    /// <param name="realFFmpegPath">The binary to start, when the check passes.</param>
    /// <param name="reason">Why it may not be started, when the check fails.</param>
    /// <returns><c>true</c> when the configured binary is usable.</returns>
    /// <remarks>
    /// <para>
    /// A bare name is left to the operating system, because only the operating system
    /// knows how it resolves one; a path is checked here, so that the misconfiguration
    /// surfaces as one attributable line in the transcode log rather than as a
    /// <see cref="Win32Exception"/> from a child process nobody started.
    /// </para>
    /// <para>
    /// The self-check matters more than it looks: the wrapper is deployed under the name
    /// the server expects for FFmpeg, so a variable pointing back at that name would make
    /// every playback fork another wrapper, forever.
    /// </para>
    /// </remarks>
    private bool TryRealFFmpegPath(out string realFFmpegPath, out string reason)
    {
        realFFmpegPath = _options.RealFFmpegPath;

        if (realFFmpegPath.Length == 0)
        {
            reason = $"no FFmpeg binary is configured; set {FFmpegWrapperOptions.RealFFmpegEnvironmentVariable}.";
            return false;
        }

        var isSelf = IsWrapperItself(realFFmpegPath);
        var isMissing = Path.IsPathRooted(realFFmpegPath) && !File.Exists(realFFmpegPath);

        if (isSelf)
        {
            reason =
                $"the FFmpeg binary named by {_options.RealFFmpegPathSource} is the wrapper itself; "
                + $"point {FFmpegWrapperOptions.RealFFmpegEnvironmentVariable} at the real FFmpeg-mvc build.";
            return false;
        }

        if (isMissing)
        {
            reason =
                $"the FFmpeg binary named by {_options.RealFFmpegPathSource} ('{Path.GetFileName(realFFmpegPath)}') "
                + "is not an existing file.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Whether a configured binary path denotes this running executable.
    /// </summary>
    /// <param name="realFFmpegPath">The configured path or name.</param>
    /// <returns><c>true</c> when starting it would start another wrapper.</returns>
    private static bool IsWrapperItself(string realFFmpegPath)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
        {
            return false;
        }

        var configured = FullPathOrNull(realFFmpegPath);
        var own = FullPathOrNull(self);

        if (configured is null || own is null)
        {
            return false;
        }

        return string.Equals(configured, own, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private static string? FullPathOrNull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the refusal the rewriter decided on.
    /// </summary>
    /// <param name="rewrite">The failed decision.</param>
    /// <returns><see cref="ExitCodeMarkerRejected"/>.</returns>
    private int RefuseRewrite(WrapperRewriteResult rewrite)
    {
        // The rewriter's status and error are authored text, not command text: the status
        // says which contract broke, its marker status says which marker rule, and the
        // error says what to do about it. That is the whole message, and the argument
        // vector that caused it stays unprinted.
        var classification = rewrite.MarkerStatus is { } markerStatus
            ? $"{rewrite.Status}/{markerStatus}"
            : rewrite.Status.ToString();

        var detail = string.IsNullOrEmpty(rewrite.Error) ? "The rewriter reported no reason." : rewrite.Error;

        Report($"refused: the command was not started ({classification}). {detail}");

        return ExitCodeMarkerRejected;
    }

    /// <summary>The refusal line for a job the concurrency limit turned away.</summary>
    private string ConcurrencyRefusal()
        => $"refused: {_guard.MaxConcurrentTranscodes} Anaglyfin transcode(s) are already running, which is the configured maximum, so FFmpeg was not started."
           + $" Raise {FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable} to allow more,"
           + $" or remove slot files left in the directory {FFmpegWrapperOptions.LockDirectoryEnvironmentVariable} names if a wrapper was killed without exiting.";

    /// <summary>
    /// Turns a start failure into a refusal.
    /// </summary>
    /// <param name="exception">What the process layer reported.</param>
    /// <returns><see cref="ExitCodeRealFFmpegNotStarted"/>.</returns>
    /// <remarks>
    /// The exception's message is not quoted: <see cref="Win32Exception"/> names the binary
    /// and the working directory, which are the server's filesystem rather than this job's
    /// business. The type is the part that says what went wrong.
    /// </remarks>
    private int RefuseStart(Exception exception)
    {
        Report($"the configured FFmpeg binary could not be started ({exception.GetType().Name}).");

        return ExitCodeRealFFmpegNotStarted;
    }

    /// <summary>
    /// Turns an undecided failure into a refusal.
    /// </summary>
    /// <param name="exception">What went wrong while deciding.</param>
    /// <returns><see cref="ExitCodeInternalError"/>.</returns>
    private int RefuseInternally(Exception exception)
    {
        Report($"refused: the wrapper could not reach a decision about this command ({exception.GetType().Name}).");

        return ExitCodeInternalError;
    }

    private void Report(string message)
    {
        _diagnostics.WriteLine(DiagnosticPrefix + message);
        _diagnostics.Flush();
    }

    /// <summary>
    /// Turns the arguments the wrapper was started with into an FFmpeg argument vector.
    /// </summary>
    /// <param name="rawArguments">The received arguments, in order.</param>
    /// <returns>The vector to hand to the rewriter.</returns>
    /// <remarks>
    /// <para>
    /// Jellyfin starts the wrapper as an executable with FFmpeg's arguments after it, so
    /// what arrives is already a vector without the executable token and goes through
    /// unchanged. The exception is a caller that forwards its own command line whole - a
    /// wrapper script or a shell function that passes <c>$0</c> along with <c>$@</c> - where
    /// the leading token is the program, not an argument. Dropping it is only safe when the
    /// token could not be anything else, so two conditions have to hold: it is not an option,
    /// and naming an executable whose name begins with <c>ffmpeg</c>
    /// (<c>ffmpeg</c>, <c>ffmpeg-mvc</c>, <c>/usr/lib/jellyfin-ffmpeg/ffmpeg</c>,
    /// <c>ffmpeg.exe</c>) is all that reading can mean.
    /// </para>
    /// <para>
    /// Everything else stays, including a bare output name: a wrapper that stripped a
    /// token FFmpeg would have used has broken a playback it was never asked to touch, so
    /// the conservative reading is to pass it along and let FFmpeg judge its own command
    /// line as it always has.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="rawArguments"/> is null.</exception>
    public static IReadOnlyList<string> NormalizeIncomingArguments(IReadOnlyList<string> rawArguments)
    {
        ArgumentNullException.ThrowIfNull(rawArguments);

        if (rawArguments.Count == 0 || !IsExecutableToken(rawArguments[0]))
        {
            return rawArguments;
        }

        return rawArguments.Skip(1).ToArray();
    }

    /// <summary>
    /// Whether a leading token is the executable rather than one of FFmpeg's arguments.
    /// </summary>
    /// <param name="token">The token to read.</param>
    /// <returns><c>true</c> when it names an FFmpeg executable.</returns>
    private static bool IsExecutableToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token[0] == '-')
        {
            return false;
        }

        // An extension is dropped so "ffmpeg.exe" reads like "ffmpeg"; a token whose name
        // is "out", "playlist" or "-hide_banner" is not a program whatever it is a path to.
        return Path.GetFileNameWithoutExtension(token)
            .StartsWith(FFmpegWrapperOptions.FFmpegExecutableName, StringComparison.OrdinalIgnoreCase);
    }
}
