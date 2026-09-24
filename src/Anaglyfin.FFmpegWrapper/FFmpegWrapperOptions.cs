using System;
using System.Globalization;
using System.IO;
using Anaglyfin.Configuration;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// The configuration of one wrapper invocation, read from the process environment and from
/// the settings document the plugin leaves for it.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper is a separate process started by Jellyfin once per playback, so it has
/// no access to the plugin's configuration, its container, or its logging. The
/// environment is the only channel that reaches it directly, and these are the things it
/// needs: which binary to hand this command to, how many Anaglyfin transcodes may run at
/// once, and where those jobs announce themselves.
/// </para>
/// <para>
/// <b>Two binaries, and one variable for each.</b> A command that carries an Anaglyfin
/// marker is written in features only the FFmpeg-mvc build has, so it goes to
/// <see cref="RealFFmpegPath"/>. A command without one is the server's own, composed around
/// the capabilities of the FFmpeg the server would have run, so it goes to
/// <see cref="ServerFFmpegPath"/> - and when the deployment named no second binary, it goes
/// to the first one, which is what every deployment did before that variable existed. One
/// variable per binary, both optional in the sense that the wrapper invents nothing when
/// either is missing.
/// </para>
/// <para>
/// <b>Nothing here is a path the code invents.</b> Either binary is always a value an
/// administrator set, or the plain name <c>ffmpeg</c> resolved through <c>PATH</c> by the
/// operating system; no project directory, install directory or host path is ever assumed,
/// and no variable that belongs to somebody else's configuration is read as if it had been
/// set for this wrapper. That is also what makes the wrapper testable: every value is a
/// parameter, and <see cref="FromEnvironment(Func{string,string?})"/> takes the environment
/// itself as a delegate, so a test can describe a machine without mutating the one it runs
/// on.
/// </para>
/// <para>
/// <b>Bad configuration degrades to the safe default rather than to a crash.</b> The
/// wrapper is on the path of every transcode on the server, including the ordinary
/// ones it must not touch; a typo in <see cref="MaxConcurrentTranscodesEnvironmentVariable"/>
/// may fail the concurrency knob, but it must not stop playback. An unusable
/// <see cref="RealFFmpegEnvironmentVariable"/> or <see cref="ServerFFmpegEnvironmentVariable"/>
/// is the exception: neither is a tuning value but a binary, so whichever one a command was
/// sent to is reported by <see cref="WrapperApplication"/> instead of being quietly
/// replaced.
/// </para>
/// <para>
/// <b>Two channels, one order of precedence.</b> The environment is the deployment's channel: it
/// names the binaries and the slot directory, and - when it names one at all - the concurrency limit.
/// <see cref="WrapperSettingsFile"/> is the plugin's channel: it carries what the admin page owns,
/// the subtitle depth request and the concurrency limit, and the wrapper never writes it. The two
/// meet on the limit and nowhere else, and the environment wins there: a number written into the
/// environment of the machine that starts the transcodes says something specific about that
/// machine, and it is not silently overridden by a document in a data directory. Everywhere the
/// deployment has no opinion, the document is how the admin page's setting reaches this process at
/// all, which is the reason it is in the document. What neither channel states arrives as the
/// shipped default.
/// </para>
/// </remarks>
public sealed record FFmpegWrapperOptions
{
    /// <summary>
    /// Name of the variable holding the maximum number of concurrent Anaglyfin
    /// transcodes. Overrides the admin setting of the same name for this server
    /// (decision 10: default 1).
    /// </summary>
    public const string MaxConcurrentTranscodesEnvironmentVariable = "ANAGLYFIN_MAX_CONCURRENT_TRANSCODES";

    /// <summary>
    /// Name of the variable holding the directory the concurrency slots are created in.
    /// </summary>
    /// <remarks>
    /// The definition is the shared one, because this variable is not a wrapper setting so much as
    /// the address two processes have to agree on: the plugin's startup cleanup pass resolves the
    /// same directory out of the same name, and a directory each process read its own copy of would
    /// be two limits that add up to none.
    /// </remarks>
    public const string LockDirectoryEnvironmentVariable = TranscodeSlotStore.LockDirectoryEnvironmentVariable;

    /// <summary>
    /// Name of the variable holding how long a wrapper keeps trying to take a slot before it refuses
    /// the playback with <see cref="WrapperApplication.ExitCodeConcurrencyLimitReached"/>.
    /// </summary>
    /// <remarks>
    /// The wait is not a queue and not a second limit: it is the length of the moment a wrapper is
    /// willing to spend finding out whether "all slots taken" was a capacity answer or the half-second
    /// between one job releasing its slot and the next one asking for it. A deployment that would
    /// rather refuse immediately states <c>0</c> here; one whose slots are handed over slowly states
    /// more, up to <see cref="MaximumSlotWaitMilliseconds"/>.
    /// </remarks>
    public const string SlotWaitEnvironmentVariable = "ANAGLYFIN_SLOT_WAIT_MS";

    /// <summary>
    /// Name of the variable naming the real FFmpeg-mvc binary the Anaglyfin commands are
    /// handed to.
    /// </summary>
    /// <remarks>
    /// A deployment that installed only the FFmpeg-mvc build - no separate stock FFmpeg - is
    /// the deployment where this one variable answers for every command, because an unset
    /// <see cref="ServerFFmpegEnvironmentVariable"/> leaves ordinary commands on this binary.
    /// </remarks>
    public const string RealFFmpegEnvironmentVariable = "ANAGLYFIN_REAL_FFMPEG";

    /// <summary>
    /// Name of the variable naming the real binary when
    /// <see cref="RealFFmpegEnvironmentVariable"/> is unset. The wrapper is installed as
    /// the server's FFmpeg, so the same variable that points the deployment at the
    /// FFmpeg-mvc build is the one that already names it.
    /// </summary>
    public const string RealFFmpegAlternateEnvironmentVariable = "FFMPEG_MVC_PATH";

    /// <summary>
    /// Name of the variable naming the FFmpeg the server would have run, for the commands
    /// Anaglyfin has no part in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the second binary of a two-binary deployment. A command carrying an Anaglyfin
    /// marker is written in features only the FFmpeg-mvc build has, so it needs
    /// <see cref="RealFFmpegEnvironmentVariable"/>; a command without one is the server's own,
    /// composed around the decoders, encoders and filters the server believes it is talking to,
    /// so it needs the build those beliefs were read out of.
    /// </para>
    /// <para>
    /// The name says whose binary it is rather than calling it <i>real</i>:
    /// <see cref="RealFFmpegEnvironmentVariable"/> already owns that word for the FFmpeg-mvc
    /// build, and two variables each claiming to be the real one is how a deployment ends up
    /// handing MVC jobs to the stock build. Nor is it the server's own <c>JELLYFIN_FFMPEG</c> -
    /// in every documented deployment that names the wrapper, so reading it here would point
    /// the wrapper at itself.
    /// </para>
    /// </remarks>
    public const string ServerFFmpegEnvironmentVariable = "ANAGLYFIN_SERVER_FFMPEG";

    /// <summary>
    /// Name accepted for the same binary as <see cref="ServerFFmpegEnvironmentVariable"/>,
    /// for a deployment that wrote the other spelling.
    /// </summary>
    /// <remarks>
    /// An alias, not a second opinion: when both are set
    /// <see cref="ServerFFmpegEnvironmentVariable"/> decides, so the alias can only ever name
    /// the binary, never override it.
    /// </remarks>
    public const string ServerFFmpegAlternateEnvironmentVariable = "ANAGLYFIN_OFFICIAL_FFMPEG";

    /// <summary>The bare name looked up on <c>PATH</c> when no variable names the binary.</summary>
    public const string FFmpegExecutableName = "ffmpeg";

    /// <summary>The name of the <c>PATH</c> variable itself.</summary>
    public const string PathEnvironmentVariable = "PATH";

    /// <summary>How many Anaglyfin transcodes may run at once when nothing is configured.</summary>
    public const int DefaultMaxConcurrentTranscodes = 1;

    /// <summary>
    /// How long a wrapper keeps trying to take a slot when nothing is configured, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Short because it is a handover and not a queue: it has to outlast the moment between one
    /// encoder releasing a slot and the next request asking for it - which in a browser that retried
    /// a refused playback is about a second - without becoming a wait a viewer would notice as a
    /// stalled start.
    /// </remarks>
    public const int DefaultSlotWaitMilliseconds = 2000;

    /// <summary>
    /// The shortest wait a wrapper accepts: <c>0</c> refuses as soon as every slot looks taken.
    /// </summary>
    public const int MinimumSlotWaitMilliseconds = 0;

    /// <summary>
    /// The longest wait a wrapper accepts, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Bounded because the value arrives from a variable somebody typed, and a wrapper that waits on
    /// a typo is a playback that never starts and never reports why. A longer hold than this is the
    /// server's business, not the wrapper's.
    /// </remarks>
    public const int MaximumSlotWaitMilliseconds = 30000;

    /// <summary>Gets <see cref="DefaultSlotWaitMilliseconds"/> as a duration.</summary>
    public static readonly TimeSpan DefaultSlotWait = TimeSpan.FromMilliseconds(DefaultSlotWaitMilliseconds);

    /// <summary>Gets <see cref="MaximumSlotWaitMilliseconds"/> as a duration.</summary>
    public static readonly TimeSpan MaximumSlotWait = TimeSpan.FromMilliseconds(MaximumSlotWaitMilliseconds);

    /// <summary>Label used when the binary came from a <c>PATH</c> lookup rather than a variable.</summary>
    public const string PathLookupSource = "PATH lookup";

    /// <summary>
    /// Gets the maximum number of Anaglyfin transcodes allowed at the same time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always at least one: a zero or negative limit would refuse every Anaglyfin job,
    /// which is a configuration mistake rather than a policy anyone configures on purpose.
    /// </para>
    /// <para>
    /// The deployment's variable first, the settings document's number second, the shipped default
    /// third - in that order, and each one only when it states something usable. See the remarks on
    /// this type for why the environment is the one that wins.
    /// </para>
    /// </remarks>
    public int MaxConcurrentTranscodes { get; init; } = DefaultMaxConcurrentTranscodes;

    /// <summary>
    /// Gets how long a wrapper keeps asking for a slot before it refuses the playback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wait belongs beside the limit rather than somewhere else in the options because it is the
    /// other half of the same answer: the limit says how many Anaglyfin encodes this machine allows,
    /// and the wait says how patient a refusal over that number is allowed to be. It changes neither
    /// the count nor the accounting - a slot is still one encode and an ordinary command still takes
    /// none.
    /// </para>
    /// <para>
    /// One channel only, and it is the deployment's. The settings document carries what the admin
    /// page owns, and a page that set the patience of a refusal would be asking an administrator a
    /// question whose answer belongs to the machine and to the way its players retry.
    /// </para>
    /// </remarks>
    public TimeSpan SlotWait { get; init; } = DefaultSlotWait;

    /// <summary>
    /// Gets the directory the concurrency slot files are created in.
    /// </summary>
    public string LockDirectory { get; init; } = DefaultLockDirectory;

    /// <summary>
    /// Gets the real FFmpeg binary an Anaglyfin command is handed to.
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
    /// Gets the binary a command without an Anaglyfin marker is handed to, or <c>null</c>
    /// when the deployment named no second binary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unset is the compatible answer and the ordinary one: with no second binary the
    /// wrapper does not choose one, and an ordinary command goes to
    /// <see cref="RealFFmpegPath"/> exactly as it did before this variable existed. Use
    /// <see cref="OrdinaryFFmpegPath"/> rather than this property, because that is where the
    /// fallback is stated.
    /// </para>
    /// <para>
    /// Read only from <see cref="ServerFFmpegEnvironmentVariable"/> and its alias - never from
    /// the server's own <c>JELLYFIN_FFMPEG</c>, which names the wrapper, and never from another
    /// project's <c>FFMPEG_PATH</c>. A path read out of a variable nobody set for this wrapper
    /// is a path nobody configured.
    /// </para>
    /// </remarks>
    public string? ServerFFmpegPath { get; init; }

    /// <summary>
    /// Gets where <see cref="ServerFFmpegPath"/> came from, for the wording of a refusal, or
    /// <c>null</c> when it named nothing.
    /// </summary>
    public string? ServerFFmpegPathSource { get; init; }

    /// <summary>
    /// Gets whether this invocation was given a second binary at all.
    /// </summary>
    /// <remarks>
    /// Blank counts as unset: a variable exported empty is a variable that names nothing, and
    /// it would be a poor upgrade if a deployment's leftover blank setting cost it the
    /// ordinary playbacks that worked before the variable existed.
    /// </remarks>
    public bool HasServerFFmpegPath => !string.IsNullOrWhiteSpace(ServerFFmpegPath);

    /// <summary>
    /// Gets the binary an ordinary command - one the rewriter passed through - is handed to.
    /// </summary>
    /// <remarks>
    /// The second binary when the deployment named one, and the first one otherwise. The
    /// fallback is deliberate: an ordinary playback of a server that installed nothing but the
    /// FFmpeg-mvc build must not lose its playback because of a variable nobody set.
    /// </remarks>
    public string OrdinaryFFmpegPath => HasServerFFmpegPath ? ServerFFmpegPath! : RealFFmpegPath;

    /// <summary>
    /// Gets the source to name in a refusal about <see cref="OrdinaryFFmpegPath"/>: the second
    /// binary's variable when there is one - the alias too, when the alias is what named it -
    /// and the first binary's source when an ordinary command is running on it.
    /// </summary>
    public string OrdinaryFFmpegPathSource => HasServerFFmpegPath
        ? ServerFFmpegPathSource ?? ServerFFmpegEnvironmentVariable
        : RealFFmpegPathSource;

    /// <summary>
    /// Gets the subtitle depth this invocation was told to offer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One of the two settings that arrive from the plugin rather than from the deployment, through
    /// <see cref="WrapperSettingsFile"/>: the admin page owns it, the environment cannot carry it,
    /// and the wrapper never writes it.
    /// </para>
    /// <para>
    /// <see cref="SubtitleDepth"/> is <see cref="SubtitleDepthSettings.Disabled"/> for every
    /// case where no request reached this process - the variable unset, the file absent, the
    /// document unreadable - which is the same answer an unconfigured server gives, and the
    /// reason a settings channel that fails costs a deployment nothing but the enhancement.
    /// </para>
    /// </remarks>
    public SubtitleDepthSettings SubtitleDepth { get; init; } = SubtitleDepthSettings.Disabled;

    /// <summary>
    /// Gets the directory used when <see cref="LockDirectoryEnvironmentVariable"/> is unset.
    /// </summary>
    /// <remarks>
    /// The shared one, so that the wrapper and the plugin's startup cleanup pass cannot resolve the
    /// slot directory of an unconfigured server differently: see
    /// <see cref="TranscodeSlotStore.DefaultLockDirectory"/> for why it sits under the temp path.
    /// </remarks>
    public static string DefaultLockDirectory => TranscodeSlotStore.DefaultLockDirectory;

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
    /// <remarks>
    /// The environment is read first and completely: the two binaries, the slot directory and the
    /// patience of a refusal are settled before anything is opened, and the concurrency limit is read
    /// from the variable before the document is opened, so the order of precedence in
    /// <see cref="ReadMaximum"/> is the order this method reads them in. The settings
    /// document is addressed only by <see cref="WrapperSettingsFile.EnvironmentVariable"/> and is
    /// given the two values the admin page owns; the slot directory and the wait are not among them,
    /// because both describe the machine the deployment started rather than anything the page offers.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="readVariable"/> is null.</exception>
    public static FFmpegWrapperOptions FromEnvironment(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        var (path, source) = ResolveRealFFmpeg(readVariable);
        var (serverPath, serverSource) = ResolveServerFFmpeg(readVariable);
        var published = WrapperSettingsFile.Read(WrapperSettingsFile.ReadConfiguredPath(readVariable));

        return new FFmpegWrapperOptions
        {
            MaxConcurrentTranscodes = ReadMaximum(
                readVariable(MaxConcurrentTranscodesEnvironmentVariable),
                published.MaxConcurrentTranscodes),
            LockDirectory = ReadLockDirectory(readVariable(LockDirectoryEnvironmentVariable)),
            SlotWait = ReadSlotWait(readVariable(SlotWaitEnvironmentVariable)),
            RealFFmpegPath = path,
            RealFFmpegPathSource = source,
            ServerFFmpegPath = serverPath,
            ServerFFmpegPathSource = serverSource,
            SubtitleDepth = published.SubtitleDepth
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
    /// Resolves the second FFmpeg binary - the one ordinary commands are handed to - from an
    /// environment view, together with the name of the variable that stated it.
    /// </summary>
    /// <param name="readVariable">Reads one environment variable; null when it is not set.</param>
    /// <returns>
    /// The configured path and its source, or <c>(null, null)</c> when no variable named a
    /// second binary.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The order is <see cref="ServerFFmpegEnvironmentVariable"/> then its alias
    /// <see cref="ServerFFmpegAlternateEnvironmentVariable"/>, and nothing after that. Unlike
    /// <see cref="ResolveRealFFmpeg"/>, an environment that names nothing is answered with
    /// nothing rather than with a lookup: there is no second binary to guess at, and the caller
    /// that has to say which variable was wrong needs the source back unchanged.
    /// </para>
    /// <para>
    /// Two variables are deliberately not consulted. The server's <c>JELLYFIN_FFMPEG</c> is the
    /// one a deployment sets to the wrapper itself, so reading it here would make every ordinary
    /// playback a wrapper starting a wrapper; another project's <c>FFMPEG_PATH</c> is a value this
    /// wrapper was never given. A deployment that wants ordinary commands on a particular binary
    /// states it in the variable that exists for that.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="readVariable"/> is null.</exception>
    public static (string? Path, string? Source) ResolveServerFFmpegPath(Func<string, string?> readVariable)
        => ResolveServerFFmpeg(readVariable);

    /// <summary>
    /// Resolves the second FFmpeg binary together with the name of its source.
    /// </summary>
    private static (string? Path, string? Source) ResolveServerFFmpeg(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        var configured = ReadNonBlank(readVariable(ServerFFmpegEnvironmentVariable));
        if (configured is not null)
        {
            return (configured, ServerFFmpegEnvironmentVariable);
        }

        var alias = ReadNonBlank(readVariable(ServerFFmpegAlternateEnvironmentVariable));
        if (alias is not null)
        {
            return (alias, ServerFFmpegAlternateEnvironmentVariable);
        }

        return (null, null);
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
    /// Reads the concurrency limit from the two channels that may state it.
    /// </summary>
    /// <param name="value">The raw variable value, if any.</param>
    /// <param name="published">
    /// The limit the settings document stated, or <c>null</c> when it stated none. Its own reader
    /// already refused anything below one, so a value that arrives can be used as it stands.
    /// </param>
    /// <returns>At least <see cref="DefaultMaxConcurrentTranscodes"/>.</returns>
    /// <remarks>
    /// <para>
    /// The variable wins while it is usable. Unset, blank, unparseable and non-positive each fail to
    /// be usable, and the difference between them is not visible to the wrapper: every one of them is
    /// a knob an administrator set wrong, and the next authority is the answer.
    /// </para>
    /// <para>
    /// The document is second rather than first for one reason. A deployment that named a limit in
    /// the environment of the server that starts these processes made that statement about the
    /// machine - its cores, its GPU, what else it hosts - and a page setting written for the product
    /// generally should not quietly outrank it. What the document is for is the server where nobody
    /// set the variable, which is the ordinary deployment, and there the administrator's number
    /// decides.
    /// </para>
    /// </remarks>
    private static int ReadMaximum(string? value, int? published)
    {
        var text = ReadNonBlank(value);

        if (text is not null
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 1)
        {
            return parsed;
        }

        return published ?? DefaultMaxConcurrentTranscodes;
    }

    /// <summary>
    /// Reads the slot directory, falling back to the temp location.
    /// </summary>
    /// <param name="value">The raw variable value, if any.</param>
    /// <returns>An absolute directory path.</returns>
    /// <remarks>
    /// Read by the shared resolver rather than spelled out again here, because the process that
    /// cleans the directory at plugin startup is not this one and has to arrive at the same answer
    /// from the same variable - including for a relative value, which neither of them may resolve
    /// against its own working directory. See
    /// <see cref="TranscodeSlotStore.ResolveLockDirectory(string)"/> for the anchoring.
    /// </remarks>
    private static string ReadLockDirectory(string? value)
        => TranscodeSlotStore.ResolveLockDirectory(value);

    /// <summary>
    /// Reads how long a wrapper keeps trying for a slot before it refuses.
    /// </summary>
    /// <param name="value">The raw variable value, if any.</param>
    /// <returns>
    /// Between <see cref="MinimumSlotWaitMilliseconds"/> and <see cref="MaximumSlotWaitMilliseconds"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Unset, blank and unparseable are the shipped wait, in line with every other knob on this
    /// type: the wrapper is on the path of every transcode on the server and a mistyped number costs
    /// it a tuning value, not a playback.
    /// </para>
    /// <para>
    /// A number that parses but stands outside the bounds is clamped rather than replaced. Zero is a
    /// statement an administrator means - "refuse at once" - and so is a very large one, so the
    /// nearest answer the wrapper is willing to honour is closer to the intent than the default
    /// would be. Out of range is still reported by the refusal line, which states the wait it waited.
    /// </para>
    /// </remarks>
    private static TimeSpan ReadSlotWait(string? value)
    {
        var text = ReadNonBlank(value);

        if (text is null
            || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return DefaultSlotWait;
        }

        if (milliseconds < MinimumSlotWaitMilliseconds)
        {
            return TimeSpan.FromMilliseconds(MinimumSlotWaitMilliseconds);
        }

        return milliseconds > MaximumSlotWaitMilliseconds
            ? MaximumSlotWait
            : TimeSpan.FromMilliseconds(milliseconds);
    }

    private static string? ReadNonBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
