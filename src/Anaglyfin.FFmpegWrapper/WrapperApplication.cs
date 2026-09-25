using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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
/// <b>Two binaries, and the rewrite already says which one.</b> A rewritten command is written
/// in features only the FFmpeg-mvc build has - view selection, the depth filter - so it is
/// handed to the build the deployment named as the real one. A passed-through command is the
/// server's own, composed around the decoders, encoders and filters the server believes it is
/// talking to, so it is handed to the FFmpeg the server would have run. The discriminator is
/// the same one the slot uses, and a deployment that named no second binary keeps the single
/// answer it always had: both routes are the same path. Whichever one is chosen gets the same
/// check before it is started, and a refusal about it names the variable that chose it. The
/// split has one consequence this class answers by rule: the server reads its capability
/// probes off the official binary, so a rewritten marker command can arrive carrying the
/// official build's preferred audio encoder, and the FFmpeg-mvc build it is handed to cannot
/// run that selection or its private quality option. The one known case - <c>libfdk_aac</c>
/// and its <c>vbr</c> option - is mapped to the native encoder on this route only, by
/// <see cref="MarkerAudioCompatibility"/>, and only when the deployment named the second
/// binary that made the split; everything else about a command stays as the server wrote it.
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
    /// Exit code when the FFmpeg binary this command was to be handed to could not be
    /// executed - absent, not executable, or a variable pointing at the wrapper. <c>127</c>
    /// because it is the code a shell gives "the command you configured is not there".
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

        // The one capability gap this dispatch closes by rule instead of by parity. Naming a
        // second binary lets the server read its audio encoders off the official build, and
        // Jellyfin answers that probe with libfdk_aac plus its private -vbr option on every
        // transcode it composes - including this marker job, which now runs on the minimal
        // FFmpeg-mvc build that carries neither. Rewritten status and a named server binary are
        // exactly the proof that this command is the marker route of a two-binary deployment, so
        // the known selection is mapped to the native encoder here, from a fixed table, with no
        // probing. A passed-through command and a single-binary deployment never reach this call
        // and stay byte-for-byte what the server wrote.
        if (rewrite.Status == WrapperRewriteStatus.Rewritten && _options.HasServerFFmpegPath)
        {
            rewrite = MarkerAudioCompatibility.ApplyTo(rewrite);
        }

        // What the rewrite declined, and why. A rewrite that carried on without something it was
        // asked for has already decided that the request is not worth a playback - a subtitle depth
        // the command's filter graph has no shape for, say - and the only thing left to decide is
        // whether anybody finds out afterwards. That is decided here, because this is the only place
        // the wrapper has a log to write to. The audio-compatibility notice travels on the same
        // channel, ahead of the declines, because it names a change the command did undergo.
        foreach (var warning in rewrite.Warnings)
        {
            Report($"warning: {warning}");
        }

        // Only an Anaglyfin job is counted. A passed-through command is ordinary playback,
        // and the limit is a budget for MVC encoding, not a gate on the server's traffic.
        // The same answer decides the binary: an Anaglyfin job needs the build its command
        // was written for, and an ordinary command needs the one the server wrote its command
        // around.
        var takesSlot = rewrite.Status == WrapperRewriteStatus.Rewritten;

        if (!TrySelectedFFmpegPath(takesSlot, out var ffmpegPath, out var reason))
        {
            Report($"refused: {reason}");
            return ExitCodeRealFFmpegNotStarted;
        }

        if (takesSlot && !_guard.TryAcquire())
        {
            Report(ConcurrencyRefusal());
            return ExitCodeConcurrencyLimitReached;
        }

        try
        {
            return StartFFmpeg(ffmpegPath, rewrite);
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
    /// Starts the selected binary and turns a failed start into a refusal.
    /// </summary>
    /// <param name="ffmpegPath">The binary this command was sent to.</param>
    /// <param name="rewrite">The decision whose vector to run.</param>
    /// <returns>The exit code of the process that ran.</returns>
    private int StartFFmpeg(string ffmpegPath, WrapperRewriteResult rewrite)
    {
        try
        {
            return _launcher.Launch(ffmpegPath, rewrite.Arguments);
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
    /// Checks that the binary this command is sent to is a file this process could execute.
    /// </summary>
    /// <param name="isAnaglyfinJob">
    /// Whether the decided command is an Anaglyfin job, which is what picks the binary.
    /// </param>
    /// <param name="ffmpegPath">The binary to start, when the check passes.</param>
    /// <param name="reason">Why it may not be started, when the check fails.</param>
    /// <returns><c>true</c> when the selected binary is usable.</returns>
    /// <remarks>
    /// <para>
    /// The same three questions are put to whichever binary was selected, because a second
    /// binary buys nothing if one of them can now be got wrong in a new way: a bare name is
    /// left to the operating system, because only the operating system knows how it resolves
    /// one, and a path is checked here, so that the misconfiguration surfaces as one
    /// attributable line in the transcode log rather than as a
    /// <see cref="Win32Exception"/> from a child process nobody started.
    /// </para>
    /// <para>
    /// The self-check matters more than it looks, and matters twice: the wrapper is deployed
    /// under the name the server expects for FFmpeg, so a variable pointing back at that name
    /// would make every playback fork another wrapper, forever - including an ordinary
    /// playback, which is the playback the second variable was added to protect.
    /// </para>
    /// </remarks>
    private bool TrySelectedFFmpegPath(bool isAnaglyfinJob, out string ffmpegPath, out string reason)
    {
        var route = RouteFor(isAnaglyfinJob);

        ffmpegPath = route.Path;

        if (ffmpegPath.Length == 0)
        {
            reason = $"no FFmpeg binary is configured; set {route.Variable}.";
            return false;
        }

        var isSelf = IsWrapperItself(ffmpegPath);
        var isMissing = Path.IsPathRooted(ffmpegPath) && !File.Exists(ffmpegPath);

        if (isSelf)
        {
            reason =
                $"the FFmpeg binary named by {route.Source} is the wrapper itself; "
                + $"point {route.Variable} at {route.Target}.";
            return false;
        }

        if (isMissing)
        {
            reason =
                $"the FFmpeg binary named by {route.Source} ('{Path.GetFileName(ffmpegPath)}') "
                + "is not an existing file.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// The binary one decided command goes to, and the names to use if it turns out not to be
    /// a binary.
    /// </summary>
    /// <param name="isAnaglyfinJob">Whether the command carries an Anaglyfin profile.</param>
    /// <returns>The binary to start, with the wording a refusal about it needs.</returns>
    /// <remarks>
    /// <para>
    /// An Anaglyfin command is a command written in the FFmpeg-mvc build's own features, so it
    /// goes to the build <see cref="FFmpegWrapperOptions.RealFFmpegEnvironmentVariable"/> names
    /// whatever else is installed. An ordinary command is the server's own, composed around the
    /// capabilities the server read off its FFmpeg, so it goes to the build the server would
    /// have run - when the deployment named one.
    /// </para>
    /// <para>
    /// A deployment that named no second binary gets this decision as the single binary it
    /// already had: path, source and wording all the first binary's, so an upgrade costs it
    /// neither a playback nor a log line it has to learn to read.
    /// </para>
    /// </remarks>
    private FFmpegRoute RouteFor(bool isAnaglyfinJob)
    {
        if (!isAnaglyfinJob && _options.HasServerFFmpegPath)
        {
            // The source names the variable this server actually set, alias included, and a
            // refusal has to name that one.
            var source = _options.OrdinaryFFmpegPathSource;

            return new FFmpegRoute(_options.OrdinaryFFmpegPath, source, source, "the FFmpeg the server would otherwise run");
        }

        // An Anaglyfin job, and an ordinary command of a deployment that named no second
        // binary: the same binary, the same variable, and the same sentence about it.
        return new FFmpegRoute(
            _options.RealFFmpegPath,
            _options.RealFFmpegPathSource,
            FFmpegWrapperOptions.RealFFmpegEnvironmentVariable,
            "the real FFmpeg-mvc build");
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
    /// <remarks>
    /// <para>
    /// Both places the limit can be raised are named, because the wrapper cannot tell which of them
    /// stated the number it is refusing over: the admin page is the setting and
    /// <see cref="FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable"/> is the override
    /// that beat it, and an administrator reading this line has to be able to find the one that
    /// applies to this server.
    /// </para>
    /// <para>
    /// What it no longer says is "remove the slot files". That sentence used to be the only answer a
    /// wrapper could offer for a leftover, and it was advice to delete the files of jobs that were
    /// running; the guard takes a slot over on its own as soon as the process that claimed it is
    /// provably gone, so nothing here asks anybody to read a directory or guess at a file in it. The
    /// wait the wrapper just spent is stated in milliseconds, because the two readings an
    /// administrator has to tell apart are "the machine is full" and "the slot was busy for slightly
    /// longer than this wrapper was willing to look".
    /// </para>
    /// </remarks>
    private string ConcurrencyRefusal()
        => $"refused: {_guard.MaxConcurrentTranscodes} Anaglyfin transcode(s) are already running, which is the configured maximum, so FFmpeg was not started."
           + $" The slot stayed taken for the whole {SlotWaitMilliseconds()} ms the wrapper waited for it to come free;"
           + " a slot left behind by a wrapper that died is taken over on its own as soon as the process that claimed it is gone, so slot files do not have to be removed by hand."
           + " Raise the maximum concurrent Anaglyfin transcodes on the Anaglyfin settings page,"
           + $" or {FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable} to override it from the deployment.";

    /// <summary>
    /// Gets the wait this invocation refused after, in the unit the environment variable is stated
    /// in, so the number in the line is the one an administrator can set.
    /// </summary>
    private string SlotWaitMilliseconds()
        => _guard.SlotWait.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);

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

    /// <summary>
    /// The binary one command is handed to, described the way a refusal about it has to
    /// describe it.
    /// </summary>
    /// <param name="Path">The configured path or bare name.</param>
    /// <param name="Source">
    /// Where that value came from - a variable name, or the fact that the host's search
    /// named it - which is what the refusal says named the binary.
    /// </param>
    /// <param name="Variable">
    /// The variable an administrator has to go and set, which is the source when a variable
    /// was the source.
    /// </param>
    /// <param name="Target">
    /// What that variable is supposed to name, so the refusal is an instruction and not only a
    /// complaint.
    /// </param>
    private readonly record struct FFmpegRoute(string Path, string Source, string Variable, string Target);
}
