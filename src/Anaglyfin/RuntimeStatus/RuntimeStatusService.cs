using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.FFmpegWrapper;

namespace Anaglyfin.RuntimeStatus;

/// <summary>
/// Builds the runtime status snapshot: it resolves the binaries and variables from the deployment's
/// environment, asks each binary a short fixed question through <see cref="IRuntimeVersionProbe"/>,
/// and checks the directories the service account has to be able to write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything about a real machine is behind a seam.</b> The environment is read through a
/// delegate, the binaries through <see cref="IRuntimeVersionProbe"/>, and the clock through a
/// function, so the whole surface is testable against fakes and a throwaway directory tree without
/// a real FFmpeg, a mutated process environment, or a wall clock a test has to race. In production
/// the composition root hands it the real probe, <see cref="Environment.GetEnvironmentVariable"/>,
/// the server's plugin path and <see cref="DateTimeOffset.UtcNow"/>.
/// </para>
/// <para>
/// <b>Read-only, and never a settings read.</b> The service reads the wrapper settings file only the
/// way the wrapper does - to know where it lives and whether it is readable - and never parses it
/// into the plugin's persisted settings. It writes nothing. The only writes it makes are the
/// create/rename/delete of a throwaway probe file, which is how a directory's writability is proven
/// for the account that will actually have to write in it.
/// </para>
/// <para>
/// <b>A probe is a fixed, short question.</b> The wrapper is asked only
/// <c>--anaglyfin-version</c> and never a second, ordinary <c>-version</c>; a binary is asked only
/// <c>-version</c>. No media path, no marker argument, and no vector a caller composed ever reaches
/// the probe, and a binary the checks already found missing or unrunnable is never started.
/// </para>
/// <para>
/// <b>One pass, cached and single-flight.</b> The complete snapshot is held for
/// <see cref="DefaultCacheSeconds"/> and refreshed at most one pass at a time, so a page refreshing
/// does not fork a binary per request. A caller's cancellation cancels only that caller's wait; the
/// pass still runs, and its answer still becomes the cache, so an aborted request cannot poison
/// the snapshot or cancel a probe other administrators are waiting on.
/// </para>
/// <para>
/// <b>Stuck passes are bounded and cancelled.</b> Each pass owns a service cancellation token, never a
/// caller token. The watchdog cancels that token when the whole-pass budget is spent, releases the
/// single-flight slot, and prevents the abandoned pass from caching itself later. The token flows to
/// the probe so a well-behaved probe terminates instead of running forever in the background.
/// </para>
/// </remarks>
public sealed class RuntimeStatusService : IRuntimeStatusService
{
    /// <summary>
    /// The server-owned variable that points the server at its FFmpeg, which in an Anaglyfin
    /// deployment is the wrapper. The wrapper never reads it; the status reads it because it is the
    /// address of the wrapper and there is no other way to name that binary.
    /// </summary>
    public const string JellyfinFFmpegEnvironmentVariable = "JELLYFIN_FFMPEG";

    /// <summary>The stable key for the wrapper binary entry.</summary>
    public const string WrapperKey = "wrapper";

    /// <summary>The stable key for the FFmpeg-mvc binary entry.</summary>
    public const string FfmpegMvcKey = "ffmpeg-mvc";

    /// <summary>The stable key for the official/server FFmpeg entry.</summary>
    public const string OfficialFfmpegKey = "official-ffmpeg";

    /// <summary>The stable key for the derived <c>ffprobe</c> sidecar entry.</summary>
    public const string FfprobeKey = "ffprobe";

    /// <summary>The identity line prefix the wrapper answers <c>--anaglyfin-version</c> with.</summary>
    /// <remarks>
    /// This mirrors the wrapper's own <c>WrapperApplication.VersionReportPrefix</c>. It is spelled
    /// here rather than referenced because the plugin assembly must not depend on the wrapper
    /// executable assembly, and the two are released together from one tag.
    /// </remarks>
    public const string WrapperIdentityPrefix = "anaglyfin-wrapper ";

    /// <summary>How long a completed snapshot is reused, in seconds.</summary>
    public const int DefaultCacheSeconds = 30;

    /// <summary>How long a single binary is given to answer a probe, in seconds.</summary>
    public const int DefaultProbeTimeoutSeconds = 2;

    /// <summary>How many binaries a normal status pass may probe in sequence.</summary>
    public const int MaxProbedBinaries = 4;

    /// <summary>
    /// Extra time added to the computed per-binary process budget. This covers environment reads,
    /// directory checks, and scheduling slack around the four bounded process probes.
    /// </summary>
    public const int DefaultWatchdogSetupMarginSeconds = 5;

    // Duplicated wrapper environment names. Source of truth: Anaglyfin.FFmpegWrapper.FFmpegWrapperOptions
    // (RealFFmpegEnvironmentVariable, RealFFmpegAlternateEnvironmentVariable,
    // ServerFFmpegEnvironmentVariable, ServerFFmpegAlternateEnvironmentVariable, FFmpegExecutableName,
    // and PathEnvironmentVariable). The plugin assembly must not depend on the wrapper executable
    // assembly, so these strings are restated as the contract and must stay synchronized with that
    // source of truth.
    private const string RealFFmpegVariable = "ANAGLYFIN_REAL_FFMPEG"; // FFmpegWrapperOptions.RealFFmpegEnvironmentVariable
    private const string RealFFmpegAliasVariable = "FFMPEG_MVC_PATH"; // FFmpegWrapperOptions.RealFFmpegAlternateEnvironmentVariable
    private const string ServerFFmpegVariable = "ANAGLYFIN_SERVER_FFMPEG"; // FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable
    private const string ServerFFmpegAliasVariable = "ANAGLYFIN_OFFICIAL_FFMPEG"; // FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable
    private const string FFmpegExecutableName = "ffmpeg"; // FFmpegWrapperOptions.FFmpegExecutableName
    private const string PathEnvironmentVariable = "PATH"; // FFmpegWrapperOptions.PathEnvironmentVariable

    /// <summary>Label used when a binary came from a <c>PATH</c> lookup rather than a variable.</summary>
    public const string PathLookupSource = "PATH lookup";

    /// <summary>The filename every official sidecar carries, whatever the wrapper is called.</summary>
    private const string SidecarFileName = "ffprobe";

    /// <summary>
    /// The filename the wrapper is published under in a release, named in the one message that tells an
    /// administrator to update it. It is display text for that message, kept in step with the release
    /// assets and <c>docs/install.md</c>; nothing reads it back.
    /// </summary>
    private const string WrapperReleaseAssetName = "anaglyfin-ffmpeg";

    private static readonly Regex WrapperVersionPattern =
        new(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FfmpegVersionTokenPattern =
        new(@"[0-9]+\.[0-9]+(?:\.[0-9]+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] WrapperProbeArguments = { "--anaglyfin-version" };
    private static readonly string[] VersionProbeArguments = { "-version" };

    private readonly IRuntimeVersionProbe _probe;
    private readonly Func<string, string?> _readEnvironmentVariable;
    private readonly string _pluginDataPath;
    private readonly string _pluginVersion;
    private readonly TimeSpan _cacheLifetime;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _passWatchdogTimeout;
    private readonly Func<DateTimeOffset> _clock;

    private readonly object _gate = new();
    private Task<RuntimeStatusResponse>? _inFlight;
    private RuntimeStatusResponse? _cached;
    private DateTimeOffset _cachedAt;

    /// <summary>
    /// Wires the service over its seams.
    /// </summary>
    /// <param name="probe">How a binary is asked for its version.</param>
    /// <param name="readEnvironmentVariable">
    /// Reads one environment variable; <c>null</c> when it is not set.
    /// </param>
    /// <param name="pluginDataPath">
    /// The directory the plugin's own settings live in, used as the default location of the wrapper
    /// settings document when no variable names one — the same default the plugin writes to.
    /// </param>
    /// <param name="pluginVersion">
    /// The plugin's own version, in the <c>X.Y.Z</c> form; the wrapper's identity is compared to it.
    /// </param>
    /// <param name="cacheLifetime">How long a completed snapshot is reused; defaults to 30 seconds.</param>
    /// <param name="probeTimeout">How long one binary is given to answer; defaults to 2 seconds.</param>
    /// <param name="clock">The clock the cache reads; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="passWatchdogTimeout">
    /// How long a whole probe pass may occupy the single-flight slot before the watchdog cancels it and
    /// releases the slot. Defaults to <see cref="MaxProbedBinaries"/> process-probe budgets plus
    /// <see cref="DefaultWatchdogSetupMarginSeconds"/>. Must be positive.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="probe"/> or <paramref name="readEnvironmentVariable"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="probeTimeout"/> or <paramref name="passWatchdogTimeout"/> is not positive.
    /// </exception>
    public RuntimeStatusService(
        IRuntimeVersionProbe probe,
        Func<string, string?> readEnvironmentVariable,
        string pluginDataPath,
        string pluginVersion,
        TimeSpan? cacheLifetime = null,
        TimeSpan? probeTimeout = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? passWatchdogTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        if (probeTimeout is { } positiveProbeTimeout && positiveProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout), probeTimeout, "The probe timeout must be positive.");
        }

        if (passWatchdogTimeout is { } positiveWatchdog && positiveWatchdog <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(passWatchdogTimeout), passWatchdogTimeout, "The pass watchdog timeout must be positive.");
        }

        _probe = probe;
        _readEnvironmentVariable = readEnvironmentVariable;
        _pluginDataPath = pluginDataPath ?? string.Empty;
        _pluginVersion = string.IsNullOrWhiteSpace(pluginVersion) ? "0.0.0" : pluginVersion.Trim();
        _cacheLifetime = cacheLifetime ?? TimeSpan.FromSeconds(DefaultCacheSeconds);
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(DefaultProbeTimeoutSeconds);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _passWatchdogTimeout = passWatchdogTimeout ?? DefaultPassWatchdogTimeout(_probeTimeout);
    }

    private static TimeSpan DefaultPassWatchdogTimeout(TimeSpan probeTimeout)
    {
        var perBinaryBudget = probeTimeout
            + TimeSpan.FromMilliseconds(2L * ProcessRuntimeVersionProbe.DefaultFinalWaitGraceMilliseconds);

        return TimeSpan.FromTicks(MaxProbedBinaries * perBinaryBudget.Ticks)
            + TimeSpan.FromSeconds(DefaultWatchdogSetupMarginSeconds);
    }

    /// <inheritdoc />
    public Task<RuntimeStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = _clock();
            if (_cached is not null && now - _cachedAt < _cacheLifetime)
            {
                return Task.FromResult(_cached);
            }

            if (_inFlight is { } running)
            {
                return WaitForCallerAsync(running, cancellationToken);
            }

            var pass = StartPassLocked();
            return WaitForCallerAsync(pass, cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task<RuntimeStatusResponse> RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_inFlight is { } running)
            {
                return WaitForCallerAsync(running, cancellationToken);
            }

            var pass = StartPassLocked();
            return WaitForCallerAsync(pass, cancellationToken);
        }
    }

    /// <summary>
    /// Starts a probe pass off the request thread and remembers the caller-facing task as the single
    /// in-flight pass, so every caller until it finishes joins the same one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pass runs on the thread pool, never on the request thread: a probe is exactly the kind of
    /// bounded, possibly-blocking work an HTTP request must not be pinned to. When it finishes, the
    /// snapshot becomes the cache and the in-flight slot is released under the same lock that handed
    /// it out, so there is no window in which a finished pass is both cached and still running.
    /// </para>
    /// <para>
    /// The request cancellation token is deliberately absent from this pass. A disconnected admin
    /// request may stop waiting for the snapshot, but it must not cancel the probe pass other
    /// administrators are waiting on, and it must not be able to cache a cancelled answer.
    /// </para>
    /// <para>
    /// The watchdog owns the service-side cancellation for the pass. It cancels the pass token, releases
    /// the slot, and marks the pass abandoned so it cannot cache itself after cancellation. The watchdog
    /// timer is cancelled when the pass completes, so a normal pass does not leave a timer parked.
    /// </para>
    /// <para>
    /// <b>One owner releases the cancellations.</b> Neither the pass continuation nor the timer
    /// continuation disposes anything; a single continuation of the <c>Task.WhenAll</c> of the two of
    /// them disposes both sources once each, after both have settled. That is what makes the lifetime
    /// race-free rather than merely caught: a pass that finishes in the same instant the watchdog fires
    /// cannot have a source pulled out from under the continuation still holding it, and the abandoned
    /// path - which returns before any result is cached - releases its sources exactly like every other
    /// path.
    /// </para>
    /// </remarks>
    private Task<RuntimeStatusResponse> StartPassLocked()
    {
        var passCancellation = new CancellationTokenSource();
        var watchdogCancellation = new CancellationTokenSource();
        var callerPass = new TaskCompletionSource<RuntimeStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        var abandoned = false;
        var inFlightPublished = false;
        var watchdogFiredBeforePublish = false;

        var pass = Task.Run(
            () => Build(passCancellation.Token),
            passCancellation.Token);

        var passFinished = pass.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_inFlight, callerPass.Task))
                    {
                        _inFlight = null;
                    }

                    if (abandoned)
                    {
                        return;
                    }

                    if (completed.Status == TaskStatus.RanToCompletion && completed.Result is { } snapshot)
                    {
                        _cached = snapshot;
                        _cachedAt = _clock();
                        callerPass.TrySetResult(snapshot);
                    }
                    else if (completed.IsCanceled)
                    {
                        callerPass.TrySetResult(WatchdogTimeoutSnapshot());
                    }
                    else
                    {
                        var cause = completed.Exception?.GetBaseException();
                        callerPass.TrySetResult(PassFailureSnapshot(cause));
                    }
                }

                // A pass that finished owns the timer it was waiting under: cancelling the source here
                // is what stops the delay from sitting on the timer queue for the rest of the budget.
                // Disposal is not this continuation's business - the single owner below does it.
                CancelQuietly(watchdogCancellation);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var watchdogElapsed = Task.Delay(_passWatchdogTimeout, watchdogCancellation.Token).ContinueWith(
            timer =>
            {
                if (timer.IsCanceled || timer.IsFaulted)
                {
                    return;
                }

                lock (_gate)
                {
                    if (abandoned)
                    {
                        return;
                    }

                    if (ReferenceEquals(_inFlight, callerPass.Task))
                    {
                        abandoned = true;
                        _inFlight = null;
                        CancelQuietly(passCancellation);
                        callerPass.TrySetResult(WatchdogTimeoutSnapshot());
                    }
                    else if (!inFlightPublished && !callerPass.Task.IsCompleted)
                    {
                        watchdogFiredBeforePublish = true;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        lock (_gate)
        {
            inFlightPublished = true;
            _inFlight = callerPass.Task;

            if (watchdogFiredBeforePublish && !abandoned && !callerPass.Task.IsCompleted)
            {
                abandoned = true;
                _inFlight = null;
                CancelQuietly(passCancellation);
                callerPass.TrySetResult(WatchdogTimeoutSnapshot());
            }
            else if (callerPass.Task.IsCompleted && ReferenceEquals(_inFlight, callerPass.Task))
            {
                _inFlight = null;
            }
        }

        // The single owner of both cancellation sources. Neither continuation above disposes anything,
        // so the pass that completes in the same instant the watchdog fires - and the abandoned pass,
        // whose continuation returns before any result is cached - are both released here, once, after
        // the last hand still holding a source has let go of it.
        Task.WhenAll(passFinished, watchdogElapsed).ContinueWith(
            settled =>
            {
                // Each continuation settles its own callers; reading the aggregate here only keeps one
                // of them from resurfacing later as an unobserved task exception.
                _ = settled.Exception;

                DisposeQuietly(watchdogCancellation);
                DisposeQuietly(passCancellation);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        return callerPass.Task;
    }

    /// <summary>
    /// Cancels one of a pass's cancellation sources, tolerating the one case that is not a failure: the
    /// single owner in <see cref="StartPassLocked"/> may already have released it.
    /// </summary>
    private static void CancelQuietly(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Disposes one of a pass's cancellation sources. The single owner disposes each source once, and
    /// the guard keeps any future lifetime slip from throwing out of a continuation.
    /// </summary>
    private static void DisposeQuietly(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Gives the caller a task that stops waiting when its own token does, without ever handing that
    /// token to the shared probe pass.
    /// </summary>
    private static Task<RuntimeStatusResponse> WaitForCallerAsync(
        Task<RuntimeStatusResponse> pass,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || pass.IsCompleted)
        {
            return pass;
        }

        return WaitWithCallerCancellationAsync(pass, cancellationToken);
    }

    private static async Task<RuntimeStatusResponse> WaitWithCallerCancellationAsync(
        Task<RuntimeStatusResponse> pass,
        CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<RuntimeStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(
            state => ((TaskCompletionSource<RuntimeStatusResponse>)state!).TrySetCanceled(),
            waiter,
            useSynchronizationContext: false);

        var completed = await Task.WhenAny(pass, waiter.Task).ConfigureAwait(false);

        if (completed == pass)
        {
            return await pass.ConfigureAwait(false);
        }

        return await waiter.Task.ConfigureAwait(false);
    }

    private RuntimeStatusResponse WatchdogTimeoutSnapshot()
        => new(
            RuntimeStatusLevel.Warning,
            _pluginVersion,
            _clock(),
            Array.Empty<RuntimeBinaryStatus>(),
            new[]
            {
                new RuntimeEnvironmentStatus(
                    "runtime-status",
                    true,
                    false,
                    null,
                    null,
                    null,
                    RuntimeItemState.ProbeFailed,
                    $"The runtime status pass did not finish within {(int)_passWatchdogTimeout.TotalMilliseconds} ms; a new request will start another pass.")
            });

    private RuntimeStatusResponse PassFailureSnapshot(Exception? exception)
    {
        var detail = exception is null
            ? "The runtime status pass did not complete."
            : $"The status pass could not be completed ({exception.GetType().Name}).";

        return new(
            RuntimeStatusLevel.Error,
            _pluginVersion,
            _clock(),
            Array.Empty<RuntimeBinaryStatus>(),
            new[]
            {
                new RuntimeEnvironmentStatus(
                    "runtime-status",
                    true,
                    false,
                    null,
                    null,
                    null,
                    RuntimeItemState.ProbeFailed,
                    detail)
            });
    }

    /// <summary>
    /// Runs one full probe pass and assembles the snapshot. Never throws: a check that cannot be made
    /// is a warning or an error in the snapshot, never an exception the request would carry.
    /// </summary>
    private RuntimeStatusResponse Build(CancellationToken cancellationToken)
    {
        try
        {
            return BuildCore(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WatchdogTimeoutSnapshot();
        }
        catch (Exception exception) when (IsProbePathFailure(exception))
        {
            // Nothing here is worth failing the request over: a snapshot that says it could not
            // complete, naming the failure type, is the honest answer and the one the page can show.
            return new RuntimeStatusResponse(
                RuntimeStatusLevel.Error,
                _pluginVersion,
                _clock(),
                Array.Empty<RuntimeBinaryStatus>(),
                new[]
                {
                    new RuntimeEnvironmentStatus(
                        "runtime-status",
                        true,
                        false,
                        null,
                        null,
                        null,
                        RuntimeItemState.ProbeFailed,
                        $"The status pass could not be completed ({exception.GetType().Name}).")
                });
        }
    }

    private RuntimeStatusResponse BuildCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (realPath, realSource) = ResolveRealFFmpeg(_readEnvironmentVariable);
        var (serverPath, serverSource) = ResolveServerFFmpeg(_readEnvironmentVariable);
        var hasServerFFmpeg = !string.IsNullOrWhiteSpace(serverPath);

        var binaries = new List<RuntimeBinaryStatus>();
        var environment = new List<RuntimeEnvironmentStatus>();
        var worst = RuntimeStatusLevel.Ok;

        void Raise(RuntimeStatusLevel level)
        {
            if ((int)level > (int)worst)
            {
                worst = level;
            }
        }

        var wrapperConfigured = ReadNonBlank(_readEnvironmentVariable(JellyfinFFmpegEnvironmentVariable));
        var (wrapperBinary, wrapperLevel) = BuildWrapper(wrapperConfigured, realPath, serverPath, cancellationToken);
        binaries.Add(wrapperBinary);
        Raise(wrapperLevel);
        environment.Add(new RuntimeEnvironmentStatus(
            JellyfinFFmpegEnvironmentVariable,
            Required: true,
            IsSet: wrapperConfigured is not null,
            Value: wrapperConfigured,
            ResolvedPath: wrapperBinary.ResolvedPath,
            SourceVariable: null,
            State: wrapperBinary.State,
            Detail: wrapperBinary.Detail));

        var (ffmpegMvcBinary, ffmpegMvcLevel) = BuildFfmpegMvc(realPath, cancellationToken);
        binaries.Add(ffmpegMvcBinary);
        Raise(ffmpegMvcLevel);
        environment.Add(new RuntimeEnvironmentStatus(
            RealFFmpegVariable,
            Required: true,
            IsSet: IsResolvedByVariable(realSource),
            Value: FirstNonBlank(
                ReadNonBlank(_readEnvironmentVariable(RealFFmpegVariable)),
                ReadNonBlank(_readEnvironmentVariable(RealFFmpegAliasVariable))),
            ResolvedPath: ffmpegMvcBinary.ResolvedPath,
            SourceVariable: SourceVariableForBinary(realSource, RealFFmpegAliasVariable),
            State: ffmpegMvcBinary.State,
            Detail: ffmpegMvcBinary.Detail));

        var (officialBinary, officialLevel) = BuildOfficial(serverPath, hasServerFFmpeg, ffmpegMvcBinary, cancellationToken);
        binaries.Add(officialBinary);
        Raise(officialLevel);
        environment.Add(new RuntimeEnvironmentStatus(
            ServerFFmpegVariable,
            Required: false,
            IsSet: hasServerFFmpeg,
            Value: serverPath,
            ResolvedPath: officialBinary.ResolvedPath,
            SourceVariable: SourceVariableForBinary(serverSource, ServerFFmpegAliasVariable),
            State: officialBinary.State,
            Detail: officialBinary.Detail));

        var (sidecarBinary, sidecarLevel) = BuildSidecar(wrapperConfigured, ffmpegMvcBinary, cancellationToken);
        binaries.Add(sidecarBinary);
        Raise(sidecarLevel);

        var (lockStatus, lockLevel) = BuildLockDirectory();
        environment.Add(lockStatus);
        Raise(lockLevel);

        var (settingsStatus, settingsLevel) = BuildWrapperSettings();
        environment.Add(settingsStatus);
        Raise(settingsLevel);

        return new RuntimeStatusResponse(
            worst,
            _pluginVersion,
            _clock(),
            binaries,
            environment);
    }

    /// <summary>
    /// The wrapper binary: asked only <c>--anaglyfin-version</c>, and only accepted as the wrapper if
    /// that one line comes back exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A binary that starts and does not answer is two different failures, and they are told apart
    /// by the paths this pass already resolved.</b> When the configured path is the very file one of the
    /// deployment's own FFmpeg builds, the server was pointed at an FFmpeg instead of at the wrapper:
    /// that is positively a misconfiguration, and it is an error naming which FFmpeg the file turned out
    /// to be. Anything else that starts and stays silent about its identity is
    /// most likely a wrapper from before <c>--anaglyfin-version</c> existed — the same wrong-release
    /// condition <see cref="RuntimeItemState.VersionMismatch"/> already answers with a warning — so it
    /// warns, names the release to update to, and points at the variable in case the binary is not the
    /// wrapper at all. Nothing here is re-resolved or re-probed: the two FFmpeg paths are the ones
    /// <see cref="BuildCore"/> settled before any binary was started.
    /// </para>
    /// </remarks>
    /// <param name="configuredPath">
    /// The path <c>JELLYFIN_FFMPEG</c> named, or <c>null</c> when it named nothing.
    /// </param>
    /// <param name="realPath">
    /// The FFmpeg-mvc path this pass resolved, used only to recognise a wrapper path that is that very
    /// file. Never re-resolved, and never a reason to start anything.
    /// </param>
    /// <param name="serverPath">
    /// The official/server FFmpeg path this pass resolved, or <c>null</c> when none is configured, used
    /// the same way.
    /// </param>
    /// <param name="cancellationToken">The pass's own cancellation token.</param>
    private (RuntimeBinaryStatus, RuntimeStatusLevel) BuildWrapper(
        string? configuredPath,
        string realPath,
        string? serverPath,
        CancellationToken cancellationToken)
    {
        const string Label = "Anaglyfin FFmpeg wrapper";

        if (configuredPath is null)
        {
            return (Missing(WrapperKey, Label, null, null, true,
                $"{JellyfinFFmpegEnvironmentVariable} is not set, so the wrapper binary is unknown."),
                RuntimeStatusLevel.Error);
        }

        var decision = DecideProbe(configuredPath, out var blocked, out var bare);
        if (decision == ProbeDecision.Skip)
        {
            return (FromPresence(WrapperKey, Label, configuredPath, configuredPath, true, blocked),
                LevelForPresence(blocked, true));
        }

        var result = _probe.Probe(configuredPath, WrapperProbeArguments, _probeTimeout, cancellationToken);

        if (result.TimedOut)
        {
            return (new RuntimeBinaryStatus(WrapperKey, Label, configuredPath, configuredPath, null, true,
                RuntimeItemState.ProbeFailed, $"The wrapper did not answer {WrapperProbeArguments[0]} in time."),
                RuntimeStatusLevel.Warning);
        }

        if (!result.Started)
        {
            var state = bare ? RuntimeItemState.Missing : RuntimeItemState.ProbeFailed;
            return (new RuntimeBinaryStatus(WrapperKey, Label, configuredPath, configuredPath, null, true,
                state, $"The wrapper at {JellyfinFFmpegEnvironmentVariable} could not be started."),
                state == RuntimeItemState.Missing ? RuntimeStatusLevel.Error : RuntimeStatusLevel.Warning);
        }

        // exit 0 and the exact identity line, or it is not accepted as the wrapper - which includes an
        // ordinary FFmpeg that answers --anaglyfin-version with a banner or an error.
        if (result.ExitCode == 0 && TryParseWrapperIdentity(result.StandardOutputLine, out var version))
        {
            if (string.Equals(version, _pluginVersion, StringComparison.Ordinal))
            {
                return (new RuntimeBinaryStatus(WrapperKey, Label, configuredPath, configuredPath, version, true,
                    RuntimeItemState.Ok, null),
                    RuntimeStatusLevel.Ok);
            }

            return (new RuntimeBinaryStatus(WrapperKey, Label, configuredPath, configuredPath, version, true,
                RuntimeItemState.VersionMismatch,
                $"The wrapper reports {version} but the plugin is {_pluginVersion}."),
                RuntimeStatusLevel.Warning);
        }

        // Started, and the identity line never came back. The first question is whether this binary is
        // one of the two FFmpeg builds the deployment already named: a wrapper path that is one of them
        // is not an unidentified wrapper, it is FFmpeg where the wrapper belongs. The comparison is
        // against the paths this pass settled before any binary was started, so it costs no second
        // resolution and no extra process. Which FFmpeg it turned out to be is said in the words the
        // page uses for that row rather than as a variable name, because either path can have come from
        // a variable, from its alias, or - for FFmpeg-mvc - from PATH.
        var ffmpegThePathTurnedOutToBe =
            AreSameFile(configuredPath, realPath) ? "the FFmpeg-mvc build the Anaglyfin commands go to"
                : AreSameFile(configuredPath, serverPath) ? "the official/server FFmpeg"
                : null;

        if (ffmpegThePathTurnedOutToBe is { } ffmpeg)
        {
            return (new RuntimeBinaryStatus(WrapperKey, Label, configuredPath, configuredPath, null, true,
                RuntimeItemState.ProbeFailed,
                $"The binary at {JellyfinFFmpegEnvironmentVariable} is the same file as {ffmpeg}; "
                + $"{JellyfinFFmpegEnvironmentVariable} points at FFmpeg, not at the Anaglyfin wrapper."),
                RuntimeStatusLevel.Error);
        }

        // The remaining answer is the one an administrator can act on: the wrapper gained
        // --anaglyfin-version in the release the plugin ships with, so a real wrapper left behind by an
        // older deployment cannot answer it at all. That is the same wrong-release condition a wrapper
        // which does answer carries as a warning, so it warns with the release to update to, and names
        // the variable to check in case the binary is not the wrapper either.
        return (new RuntimeBinaryStatus(WrapperKey, Label, configuredPath, configuredPath, null, true,
            RuntimeItemState.ProbeFailed,
            $"The binary at {JellyfinFFmpegEnvironmentVariable} did not answer {WrapperProbeArguments[0]} as the "
            + $"Anaglyfin wrapper. The most likely cause is an outdated wrapper: update the {WrapperReleaseAssetName} "
            + $"wrapper to the release matching the plugin ({_pluginVersion}). If it is already current, check that "
            + $"{JellyfinFFmpegEnvironmentVariable} names the wrapper rather than a plain FFmpeg."),
            RuntimeStatusLevel.Warning);
    }

    /// <summary>
    /// The FFmpeg-mvc binary the Anaglyfin commands go to, resolved through the wrapper's own
    /// canonical order (real variable, then its alias, then <c>PATH</c>).
    /// </summary>
    private (RuntimeBinaryStatus, RuntimeStatusLevel) BuildFfmpegMvc(string realPath, CancellationToken cancellationToken)
    {
        const string Label = "FFmpeg-mvc (Anaglyfin)";

        return BuildVersionedBinary(
            FfmpegMvcKey,
            Label,
            realPath,
            realPath,
            true,
            cancellationToken);
    }

    /// <summary>
    /// The FFmpeg the server would run for commands Anaglyfin has no part in: recommended, and its
    /// absence is a plain single-binary deployment rather than a fault.
    /// </summary>
    private (RuntimeBinaryStatus, RuntimeStatusLevel) BuildOfficial(
        string? serverPath,
        bool hasServer,
        RuntimeBinaryStatus ffmpegMvc,
        CancellationToken cancellationToken)
    {
        const string Label = "Official/server FFmpeg";

        if (!hasServer)
        {
            return (new RuntimeBinaryStatus(OfficialFfmpegKey, Label, null, null, null, false,
                RuntimeItemState.Missing,
                "No second FFmpeg is configured; ordinary playback runs on the FFmpeg-mvc build (single-binary mode)."),
                RuntimeStatusLevel.Warning);
        }

        // The capability inversion: if the server's own FFmpeg is the very same file as the
        // FFmpeg-mvc build, the server reads its capability probes off a build that cannot serve the
        // commands it then composes for them. It runs, but it is the deployment telling itself a lie.
        if (AreSameFile(serverPath, ffmpegMvc.ResolvedPath))
        {
            return (new RuntimeBinaryStatus(OfficialFfmpegKey, Label, serverPath, serverPath, null, false,
                RuntimeItemState.Warning,
                $"The official FFmpeg is the same file as the FFmpeg-mvc build (a capability inversion); {ServerFFmpegVariable} "
                + "should name a different FFmpeg so the server probes the build it is really talking to."),
                RuntimeStatusLevel.Warning);
        }

        return BuildVersionedBinary(
            OfficialFfmpegKey,
            Label,
            serverPath,
            serverPath,
            false,
            cancellationToken);
    }

    /// <summary>
    /// The official sidecar <c>ffprobe</c>, derived from the wrapper's own directory rather than read
    /// from any variable, and required.
    /// </summary>
    /// <remarks>
    /// Jellyfin replaces the wrapper's filename with <c>ffprobe</c> in the same directory and never
    /// looks on <c>PATH</c>, so the status derives exactly that sibling and checks it - and refuses it
    /// if it turned out to be the FFmpeg-mvc binary itself.
    /// </remarks>
    private (RuntimeBinaryStatus, RuntimeStatusLevel) BuildSidecar(
        string? wrapperConfiguredPath,
        RuntimeBinaryStatus ffmpegMvc,
        CancellationToken cancellationToken)
    {
        const string Label = "Derived ffprobe sidecar";

        var derived = DeriveSidecarPath(wrapperConfiguredPath);
        if (derived is null)
        {
            return (new RuntimeBinaryStatus(FfprobeKey, Label, wrapperConfiguredPath, null, null, true,
                RuntimeItemState.Missing,
                $"'{SidecarFileName}' could not be derived because {JellyfinFFmpegEnvironmentVariable} names no directory."),
                RuntimeStatusLevel.Error);
        }

        if (AreSameFile(derived, ffmpegMvc.ResolvedPath))
        {
            return (new RuntimeBinaryStatus(FfprobeKey, Label, wrapperConfiguredPath, derived, null, true,
                RuntimeItemState.ProbeFailed,
                $"The derived '{SidecarFileName}' is the FFmpeg-mvc binary itself; the two must be different files."),
                RuntimeStatusLevel.Error);
        }

        return BuildVersionedBinary(
            FfprobeKey,
            Label,
            wrapperConfiguredPath,
            derived,
            true,
            cancellationToken);
    }

    /// <summary>
    /// A binary answered by FFmpeg's own <c>-version</c> banner: existence and executability first,
    /// a probe only for a file that can be run, and a version read from the first line when there is
    /// one.
    /// </summary>
    private (RuntimeBinaryStatus, RuntimeStatusLevel) BuildVersionedBinary(
        string key,
        string label,
        string? configuredPath,
        string? resolvedPath,
        bool required,
        CancellationToken cancellationToken)
    {
        if (resolvedPath is null)
        {
            return (Missing(key, label, configuredPath, null, required,
                $"{label} is not configured."),
                required ? RuntimeStatusLevel.Error : RuntimeStatusLevel.Warning);
        }

        var decision = DecideProbe(resolvedPath, out var blocked, out var bare);
        if (decision == ProbeDecision.Skip)
        {
            return (FromPresence(key, label, configuredPath, resolvedPath, required, blocked),
                LevelForPresence(blocked, required));
        }

        var result = _probe.Probe(resolvedPath, VersionProbeArguments, _probeTimeout, cancellationToken);

        if (result.TimedOut)
        {
            return (new RuntimeBinaryStatus(key, label, configuredPath, resolvedPath, null, required,
                RuntimeItemState.ProbeFailed, $"{label} did not answer {VersionProbeArguments[0]} in time."),
                RuntimeStatusLevel.Warning);
        }

        if (!result.Started)
        {
            var state = bare ? RuntimeItemState.Missing : RuntimeItemState.ProbeFailed;
            return (new RuntimeBinaryStatus(key, label, configuredPath, resolvedPath, null, required,
                state, $"{label} could not be started."),
                (state == RuntimeItemState.Missing && required) ? RuntimeStatusLevel.Error : RuntimeStatusLevel.Warning);
        }

        if (result.ExitCode == 0)
        {
            var detail = DetailForBarePath(null, resolvedPath);

            return (new RuntimeBinaryStatus(key, label, configuredPath, resolvedPath, ParseFfmpegVersion(result.StandardOutputLine), required,
                RuntimeItemState.Ok, detail),
                RuntimeStatusLevel.Ok);
        }

        return (new RuntimeBinaryStatus(key, label, configuredPath, resolvedPath, null, required,
            RuntimeItemState.ProbeFailed, $"{label} answered {VersionProbeArguments[0]} with exit {result.ExitCode}."),
            RuntimeStatusLevel.Warning);
    }

    /// <summary>
    /// The slot directory: it has to exist as a directory and be writable by the service account,
    /// proven the only way that means anything - by writing, renaming and deleting a throwaway file
    /// as this very process.
    /// </summary>
    private (RuntimeEnvironmentStatus, RuntimeStatusLevel) BuildLockDirectory()
    {
        var configured = ReadNonBlank(_readEnvironmentVariable(TranscodeSlotStore.LockDirectoryEnvironmentVariable));
        var directory = TranscodeSlotStore.ResolveLockDirectory(configured);

        if (!Directory.Exists(directory))
        {
            return (new RuntimeEnvironmentStatus(
                TranscodeSlotStore.LockDirectoryEnvironmentVariable, true, configured is not null, configured,
                directory, null, RuntimeItemState.Missing,
                $"The slot directory '{directory}' does not exist as a directory."),
                RuntimeStatusLevel.Error);
        }

        if (!DirectoryWritable(directory))
        {
            return (new RuntimeEnvironmentStatus(
                TranscodeSlotStore.LockDirectoryEnvironmentVariable, true, configured is not null, configured,
                directory, null, RuntimeItemState.NotWritable,
                $"The service account cannot write in the slot directory '{directory}'."),
                RuntimeStatusLevel.Error);
        }

        return (new RuntimeEnvironmentStatus(
            TranscodeSlotStore.LockDirectoryEnvironmentVariable, true, configured is not null, configured,
            directory, null, RuntimeItemState.Ok, null),
            RuntimeStatusLevel.Ok);
    }

    /// <summary>
    /// The wrapper settings document: its directory has to be writable, an existing file readable,
    /// and its absence a warning that the depth request may stay flat - never a settings parse.
    /// </summary>
    private (RuntimeEnvironmentStatus, RuntimeStatusLevel) BuildWrapperSettings()
    {
        var name = WrapperSettingsFile.EnvironmentVariable;
        var configured = ReadNonBlank(_readEnvironmentVariable(name));
        var path = WrapperSettingsFile.ResolveWritePath(_readEnvironmentVariable, _pluginDataPath);
        var parent = Path.GetDirectoryName(path);

        RuntimeEnvironmentStatus Status(RuntimeItemState state, string? detail)
            => new(name, true, configured is not null, configured, path, null, state, detail);

        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return (Status(RuntimeItemState.Missing, $"The settings directory '{parent}' does not exist."),
                RuntimeStatusLevel.Error);
        }

        if (!DirectoryWritable(parent))
        {
            return (Status(RuntimeItemState.NotWritable, $"The service account cannot write in the settings directory '{parent}'."),
                RuntimeStatusLevel.Error);
        }

        if (!File.Exists(path))
        {
            return (Status(RuntimeItemState.Warning,
                "The wrapper settings document has not been written, so the subtitle depth request may remain flat."),
                RuntimeStatusLevel.Warning);
        }

        if (!CanRead(path))
        {
            return (Status(RuntimeItemState.NotReadable,
                "The wrapper settings document exists but the service account cannot read it, so the depth request may remain flat."),
                RuntimeStatusLevel.Warning);
        }

        return (Status(RuntimeItemState.Ok, null), RuntimeStatusLevel.Ok);
    }

    /// <summary>
    /// Whether a binary at <paramref name="path"/> should be probed, or is already known to be
    /// unusable and must not be started.
    /// </summary>
    private static ProbeDecision DecideProbe(string path, out RuntimeItemState blocked, out bool bare)
    {
        bare = false;
        blocked = RuntimeItemState.Ok;

        if (!Path.IsPathRooted(path))
        {
            // A bare name is left to the operating system, which is the only thing that knows how it
            // resolves one; nothing here can pre-check it, so it is not started-blind either - it is
            // run once and a failure to start is read back as its answer.
            bare = true;
            return ProbeDecision.Probe;
        }

        if (!File.Exists(path))
        {
            blocked = RuntimeItemState.Missing;
            return ProbeDecision.Skip;
        }

        if (!CanRead(path))
        {
            blocked = RuntimeItemState.NotReadable;
            return ProbeDecision.Skip;
        }

        if (!IsExecutable(path))
        {
            blocked = RuntimeItemState.NotExecutable;
            return ProbeDecision.Skip;
        }

        return ProbeDecision.Probe;
    }

    private static RuntimeBinaryStatus FromPresence(
        string key,
        string label,
        string? configuredPath,
        string? resolvedPath,
        bool required,
        RuntimeItemState state)
        => new(key, label, configuredPath, resolvedPath, null, required, state, DetailForPresence(state, required));

    /// <summary>
    /// The severity a presence failure carries: a required binary that is not there or not runnable
    /// is an error, a recommended one only a note.
    /// </summary>
    private static RuntimeStatusLevel LevelForPresence(RuntimeItemState state, bool required)
        => state switch
        {
            RuntimeItemState.Missing or RuntimeItemState.NotReadable or RuntimeItemState.NotExecutable
                => required ? RuntimeStatusLevel.Error : RuntimeStatusLevel.Warning,
            _ => RuntimeStatusLevel.Ok
        };

    private static string? DetailForPresence(RuntimeItemState state, bool required)
        => state switch
        {
            RuntimeItemState.Missing => "The file is not there.",
            RuntimeItemState.NotReadable => "The service account cannot read the file.",
            RuntimeItemState.NotExecutable => "The file is not marked executable.",
            _ => null
        };

    private static RuntimeBinaryStatus Missing(
        string key,
        string label,
        string? configuredPath,
        string? resolvedPath,
        bool required,
        string detail)
        => new(key, label, configuredPath, resolvedPath, null, required, RuntimeItemState.Missing, detail);

    private static bool TryParseWrapperIdentity(string? line, out string version)
    {
        version = string.Empty;

        if (line is null)
        {
            return false;
        }

        var trimmed = line.Trim();
        if (!trimmed.StartsWith(WrapperIdentityPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = trimmed.Substring(WrapperIdentityPrefix.Length);
        if (!WrapperVersionPattern.IsMatch(rest))
        {
            return false;
        }

        version = rest;
        return true;
    }

    private static string? ParseFfmpegVersion(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var match = FfmpegVersionTokenPattern.Match(line);
        return match.Success ? match.Value : null;
    }

    private static string? DeriveSidecarPath(string? wrapperPath)
        => DeriveFFprobeSidecarPath(wrapperPath, preserveWrapperExtension: OperatingSystem.IsWindows());

    /// <summary>
    /// Derives the official sidecar path Jellyfin uses for <c>ffprobe</c>: the same directory as the
    /// wrapper, that filename always, and the wrapper's extension only on a platform where the
    /// extension means something.
    /// </summary>
    /// <param name="wrapperPath">
    /// The wrapper path <c>JELLYFIN_FFMPEG</c> named, or <c>null</c>/blank when it named nothing.
    /// </param>
    /// <param name="preserveWrapperExtension">
    /// Whether to keep the wrapper's file extension on the sidecar. The real caller passes
    /// <see cref="OperatingSystem.IsWindows()"/>; the parameter is exposed so both the Linux answer
    /// (always plain <c>ffprobe</c>) and the Windows answer (<c>ffprobe.exe</c> for a
    /// <c>.exe</c> wrapper) can be stated from one platform.
    /// </param>
    /// <returns>
    /// The sibling path, or <c>null</c> when no directory could be derived — which the checks read as
    /// a missing sidecar, and never as a licence to search <c>PATH</c>.
    /// </returns>
    public static string? DeriveFFprobeSidecarPath(string? wrapperPath, bool preserveWrapperExtension)
    {
        if (string.IsNullOrWhiteSpace(wrapperPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(wrapperPath);
        if (string.IsNullOrEmpty(directory))
        {
            // No directory to derive the sibling from: the sidecar is never looked for on PATH.
            return null;
        }

        var extension = preserveWrapperExtension ? Path.GetExtension(wrapperPath) : string.Empty;
        return Path.Combine(directory, SidecarFileName + extension);
    }

    private static bool AreSameFile(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            var first = Path.GetFullPath(left);
            var second = Path.GetFullPath(right);

            return string.Equals(
                first,
                second,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (IsProbePathFailure(exception))
        {
            return false;
        }
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (Exception exception) when (IsProbePathFailure(exception))
        {
            return false;
        }
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows has no execute bit; existence is the whole check there.
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception exception) when (IsProbePathFailure(exception))
        {
            return false;
        }
    }

    private static bool DirectoryWritable(string directory)
    {
        var probe = Path.Combine(directory, ".anaglyfin-runtime-status-" + Guid.NewGuid().ToString("N"));

        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Move(probe, probe + ".r", overwrite: true);
            File.Delete(probe + ".r");
            return true;
        }
        catch (Exception exception) when (IsProbePathFailure(exception))
        {
            return false;
        }
        finally
        {
            TryDelete(probe);
            TryDelete(probe + ".r");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsProbePathFailure(exception))
        {
            // A leftover probe file is untidy and nothing else.
        }
    }

    private static string? ReadNonBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonBlank(string? first, string? second)
        => first ?? second;

    private static bool IsResolvedByVariable(string? source)
        => source is not null && source != PathLookupSource;

    private static string? SourceVariableForBinary(string? source, string aliasName)
    {
        if (string.Equals(source, PathLookupSource, StringComparison.Ordinal))
        {
            return PathEnvironmentVariable;
        }

        return string.Equals(source, aliasName, StringComparison.Ordinal) ? aliasName : null;
    }

    private static string? DetailForBarePath(string? detail, string? resolvedPath)
        => string.IsNullOrEmpty(detail) && IsBarePath(resolvedPath)
            ? "The configured value is a bare name, so it was resolved through PATH."
            : detail;

    private static bool IsBarePath(string? resolvedPath)
        => !string.IsNullOrWhiteSpace(resolvedPath) && !Path.IsPathRooted(resolvedPath);

    /// <summary>
    /// Resolves the FFmpeg-mvc binary the Anaglyfin commands go to, from the deployment's variables
    /// in the wrapper's own canonical order: the real variable, then its alias, then a <c>PATH</c>
    /// lookup of the bare FFmpeg name.
    /// </summary>
    /// <remarks>
    /// This mirrors <c>FFmpegWrapperOptions.ResolveRealFFmpeg</c> because the status must report the
    /// same binary the wrapper would actually run; the plugin cannot reference the wrapper assembly
    /// (the wrapper is built from the plugin), so the small resolution is restated here against the
    /// same variable names.
    /// </remarks>
    private static (string Path, string Source) ResolveRealFFmpeg(Func<string, string?> readVariable)
    {
        var configured = ReadNonBlank(readVariable(RealFFmpegVariable));
        if (configured is not null)
        {
            return (configured, RealFFmpegVariable);
        }

        var alias = ReadNonBlank(readVariable(RealFFmpegAliasVariable));
        if (alias is not null)
        {
            return (alias, RealFFmpegAliasVariable);
        }

        return (FindOnPath(readVariable) ?? FFmpegExecutableName, PathLookupSource);
    }

    /// <summary>
    /// Resolves the second, official FFmpeg binary from its variable then its alias, and nothing
    /// after: an environment that names no second binary is answered with nothing, never a lookup.
    /// </summary>
    private static (string? Path, string? Source) ResolveServerFFmpeg(Func<string, string?> readVariable)
    {
        var configured = ReadNonBlank(readVariable(ServerFFmpegVariable));
        if (configured is not null)
        {
            return (configured, ServerFFmpegVariable);
        }

        var alias = ReadNonBlank(readVariable(ServerFFmpegAliasVariable));
        if (alias is not null)
        {
            return (alias, ServerFFmpegAliasVariable);
        }

        return (null, null);
    }

    /// <summary>
    /// Looks the FFmpeg executable up in the <c>PATH</c> of the same environment view, returning the
    /// first existing candidate file. Only ever used for the FFmpeg name, never for the sidecar.
    /// </summary>
    private static string? FindOnPath(Func<string, string?> readVariable)
    {
        var searchPath = ReadNonBlank(readVariable(PathEnvironmentVariable));
        if (searchPath is null)
        {
            return null;
        }

        foreach (var entry in searchPath.Split(Path.PathSeparator))
        {
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

    private static string[] ExecutableCandidates(string directory)
    {
        var plain = Path.Combine(directory, FFmpegExecutableName);

        // The extension is what Windows's search adds; on Unix a ".exe" sibling means nothing.
        return OperatingSystem.IsWindows()
            ? new[] { plain, plain + ".exe" }
            : new[] { plain };
    }

    private static bool IsProbePathFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException;

    private enum ProbeDecision
    {
        /// <summary>The file has to be run to be answered.</summary>
        Probe,

        /// <summary>The file is already known unusable; do not start a process for it.</summary>
        Skip
    }
}
