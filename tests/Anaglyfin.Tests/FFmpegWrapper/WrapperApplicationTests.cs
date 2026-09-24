using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Anaglyfin.Configuration;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.Markers;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Behaviour tests for the wrapper itself: what it starts, what it refuses, what it counts
/// as a job, and what it leaves behind in each case.
/// </summary>
/// <remarks>
/// <para>
/// The rewrite itself is T5a's subject and is exercised token by token over there; these
/// tests use the real rewriter so that the two halves are tested as they will run, but what
/// they assert is the launcher's and the slot's behaviour. Every expectation is a complete
/// verbatim vector, because a wrapper that started a subtly wrong command would produce a
/// subtly wrong video and nothing downstream would notice.
/// </para>
/// <para>
/// A slot is asserted from the slot directory rather than from the guard's own opinion, and
/// the observation is taken from inside the fake launcher: the interesting moment is while
/// FFmpeg would be running, which no test can reach from outside the launch.
/// </para>
/// <para>
/// Which binary a command is handed to is part of the decision, so it is asserted the same way
/// - from the launcher - and over two stand-in binaries, because "the marker went to the
/// FFmpeg-mvc build and the ordinary playback went to the server's" is a statement about a
/// pair. The rewriter's status is the only discriminator, so a test of a marker command states
/// the slot as well: dispatch must not have quietly moved the limit.
/// </para>
/// </remarks>
public sealed class WrapperApplicationTests : IDisposable
{
    /// <summary>The real file a marker stands in for.</summary>
    private const string SourcePath = "/movies/Movie (2010)/Movie.2010.3D.mkv";

    private readonly TemporarySlotDirectory _slots = new();
    private readonly StringWriter _diagnostics = new();

    /// <summary>The directory holding this test's stand-ins for the real FFmpeg binaries.</summary>
    private readonly string _binaryDirectory;

    /// <summary>The binary an Anaglyfin command is configured to be handed to.</summary>
    private readonly string _realFFmpeg;

    /// <summary>The binary an ordinary command is configured to be handed to.</summary>
    private readonly string _serverFFmpeg;

    /// <summary>
    /// Lays out the two things on the filesystem a wrapper invocation needs: a slot
    /// directory, and existing files it can be told are the real FFmpeg binaries.
    /// </summary>
    /// <remarks>
    /// The stand-in binaries are never executed - the launcher records instead of starting -
    /// but they do have to exist: the wrapper refuses a configured absolute path that is
    /// not a file, and that check is the wrapper's own behaviour rather than the launcher's.
    /// There are two of them because a wrapper with one binary and a wrapper with two are two
    /// deployments, and the interesting assertions are about which command goes where.
    /// </remarks>
    public WrapperApplicationTests()
    {
        _binaryDirectory = Path.Combine(Path.GetTempPath(), "anaglyfin-wrapper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_binaryDirectory);
        _realFFmpeg = Path.Combine(_binaryDirectory, "ffmpeg-mvc");
        _serverFFmpeg = Path.Combine(_binaryDirectory, "ffmpeg-server");

        File.WriteAllText(_realFFmpeg, "#!/bin/sh\nexit 0\n");
        File.WriteAllText(_serverFFmpeg, "#!/bin/sh\nexit 0\n");
    }

    /// <summary>Removes the slot directory and the stand-in binary this test used.</summary>
    public void Dispose()
    {
        _slots.Dispose();
        TryDelete(_binaryDirectory);
    }

    // ----- pass-through: ordinary playback ------------------------------------------

    [Fact]
    public void AnOrdinaryCommandStartsTheRealBinaryUnchangedAndTakesNoSlot()
    {
        var arguments = JellyfinLikeCommand("/library/movie.mkv");
        var (application, launcher) = CreateApplication();

        var slotFilesWhileRunning = Array.Empty<string>();
        launcher.Observe = _ => slotFilesWhileRunning = _slots.SlotFiles();

        var exitCode = application.Run(arguments);

        Assert.Equal(WrapperApplication.ExitCodeSuccess, exitCode);

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(_realFFmpeg, launch.ExecutablePath);
        Assert.Equal(arguments.ToArray(), launch.Arguments);

        // The limit governs MVC encoding, not the server's traffic: an ordinary transcode
        // neither takes a slot nor waits for one.
        Assert.Empty(slotFilesWhileRunning);
        Assert.Empty(_slots.SlotFiles());
    }

    [Fact]
    public void TheExitCodeOfTheRealBinaryIsTheExitCodeOfTheWrapper()
    {
        var (application, launcher) = CreateApplication();
        launcher.ExitCode = 8;

        var exitCode = application.Run(JellyfinLikeCommand("/library/movie.mkv"));

        // An encode that failed stays a failed encode, with the server's own retry and
        // reporting behaviour attached to it.
        Assert.Equal(8, exitCode);
    }

    [Fact]
    public void AnOrdinaryCommandRunsAlongsideABusyAnaglyfinSlot()
    {
        var running = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);
        Assert.True(running.TryAcquire());

        var (application, launcher) = CreateApplication();

        try
        {
            Assert.Equal(WrapperApplication.ExitCodeSuccess, application.Run(JellyfinLikeCommand("/library/movie.mkv")));
            Assert.Single(launcher.Launches);
        }
        finally
        {
            running.Release();
        }
    }

    [Fact]
    public void AnEmptyVectorIsHandedOverAsAnEmptyVector()
    {
        var (application, launcher) = CreateApplication();

        var exitCode = application.Run(Array.Empty<string>());

        // The wrapper never invents an argument. FFmpeg with no arguments is FFmpeg's own
        // usage failure, and it is exactly what the server would have got without a wrapper.
        Assert.Equal(WrapperApplication.ExitCodeSuccess, exitCode);
        Assert.Empty(Assert.Single(launcher.Launches).Arguments);
    }

    [Theory]
    [InlineData("/usr/lib/jellyfin-ffmpeg/ffmpeg")]
    [InlineData("ffmpeg")]
    [InlineData("ffmpeg-mvc")]
    [InlineData("FFMPEG.exe")]
    [InlineData("ffmpeg.sh")]
    public void ACommandThatArrivesWithItsExecutableTokenStartsWithoutIt(string executableToken)
    {
        var (application, launcher) = CreateApplication();

        application.Run(new[] { executableToken, "-hide_banner", "-i", "/library/movie.mkv", "out.m3u8" });

        Assert.Equal(
            new[] { "-hide_banner", "-i", "/library/movie.mkv", "out.m3u8" },
            Assert.Single(launcher.Launches).Arguments);
    }

    [Theory]
    [InlineData("out.m3u8")]
    [InlineData("playlist")]
    [InlineData("Movie.2010.3D.mkv")]
    [InlineData("ffprobe")]
    [InlineData("anaglyfin-ffmpeg-wrapper")]
    [InlineData("-hide_banner")]
    public void ALeadingTokenThatCouldNotBeTheExecutableStaysPartOfTheCommand(string token)
    {
        var (application, launcher) = CreateApplication();

        application.Run(new[] { token, "-i", "/library/movie.mkv", "out.m3u8" });

        // Stripping anything else would break a playback the wrapper has no business
        // touching, so the conservative reading is to pass the token along and let FFmpeg
        // judge its own command line.
        Assert.Equal(
            new[] { token, "-i", "/library/movie.mkv", "out.m3u8" },
            Assert.Single(launcher.Launches).Arguments);
    }

    // ----- a marker: rewritten command, under a slot ---------------------------------

    [Fact]
    public void AMarkerCommandRunsTheRewrittenVectorAndHoldsASlotWhileItRuns()
    {
        var (application, launcher) = CreateApplication();

        var slotFilesWhileRunning = Array.Empty<string>();
        launcher.Observe = _ => slotFilesWhileRunning = _slots.SlotFiles();

        var exitCode = application.Run(JellyfinLikeCommand(Marker(ProfileIds.SideBySideFull)));

        Assert.Equal(WrapperApplication.ExitCodeSuccess, exitCode);

        // The marker is gone, the composed-view request sits in front of its input, and
        // everything the server chose - its maps and its subtitles included - is where the
        // server put it: a version that converts and renders nothing has no output argument of
        // its own to add.
        Assert.Equal(
            new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-view_ids", "-1",
                "-i", SourcePath,
                "-map", "0:v", "-map", "0:a", "-c:v", "libx264", "-c:a", "copy",
                "-f", "hls", "-hls_time", "6", "playlist.m3u8"
            },
            Assert.Single(launcher.Launches).Arguments);

        Assert.DoesNotContain("127.0.0.1", Assert.Single(launcher.Launches).CommandLine, StringComparison.Ordinal);

        // It ran as the one Anaglyfin job there was, and it gave the slot back on the way out.
        var held = Assert.Single(slotFilesWhileRunning);
        Assert.Contains(WrapperConcurrencyGuard.SlotFileNamePrefix + "0", held, StringComparison.Ordinal);
        Assert.Empty(_slots.SlotFiles());
    }

    [Fact]
    public void TheRealBinaryDecidesTheExitCodeOfAnAnaglyfinJobToo()
    {
        var (application, launcher) = CreateApplication();
        launcher.ExitCode = 137;

        var exitCode = application.Run(JellyfinLikeCommand(Marker(ProfileIds.AnaglyphRedCyanDubois)));

        Assert.Equal(137, exitCode);
        Assert.Empty(_slots.SlotFiles());
    }

    [Fact]
    public void ASecondAnaglyfinJobIsTurnedAwayBeforeFFmpegIsReached()
    {
        var running = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 1);
        Assert.True(running.TryAcquire());

        var (application, launcher) = CreateApplication();

        try
        {
            var exitCode = application.Run(JellyfinLikeCommand(Marker(ProfileIds.AnaglyphRedCyanDubois)));

            Assert.Equal(WrapperApplication.ExitCodeConcurrencyLimitReached, exitCode);
            Assert.Empty(launcher.Launches);

            var output = _diagnostics.ToString();
            Assert.Contains("maximum", output, StringComparison.Ordinal);
            Assert.Contains(FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable, output, StringComparison.Ordinal);
        }
        finally
        {
            running.Release();
        }

        // The refusal is temporary in exactly the way its exit code says it is: the same
        // request runs once the job in progress is over.
        var (retryApplication, retryLauncher) = CreateApplication();

        Assert.Equal(WrapperApplication.ExitCodeSuccess, retryApplication.Run(JellyfinLikeCommand(Marker(ProfileIds.AnaglyphRedCyanDubois))));
        Assert.Single(retryLauncher.Launches);
    }

    [Fact]
    public void TheLimitThePluginPublishedIsTheLimitTheWrapperCounts()
    {
        // The admin page owns this number and the settings document is the only way it reaches this
        // process. One of the two slots the document states is already held, so the shipped limit of
        // one would turn this command away and the published limit of two admits it. That difference
        // is the whole of the test: it is the difference between a saved setting and a decoration.
        var running = new WrapperConcurrencyGuard(_slots.Location, maxConcurrentTranscodes: 2);
        Assert.True(running.TryAcquire());

        try
        {
            var (application, launcher) = CreateApplicationFromSettingsFile(
                SubtitleDepthSettings.Disabled, maxConcurrentTranscodes: 2);

            Assert.Equal(WrapperApplication.ExitCodeSuccess, application.Run(JellyfinLikeCommand(Marker(ProfileIds.AnaglyphRedCyanDubois))));
            Assert.Single(launcher.Launches);
            Assert.Empty(_diagnostics.ToString());
        }
        finally
        {
            running.Release();
        }

        Assert.Empty(_slots.SlotFiles());
    }

    [Fact]
    public void AJobWhoseBinaryCouldNotBeStartedGivesTheSlotBack()
    {
        var (application, launcher) = CreateApplication();
        launcher.FailWith = new Win32Exception(13, "Permission denied");

        var exitCode = application.Run(JellyfinLikeCommand(Marker(ProfileIds.SideBySideFull)));

        Assert.Equal(WrapperApplication.ExitCodeRealFFmpegNotStarted, exitCode);
        Assert.Single(launcher.Launches);
        Assert.Empty(_slots.SlotFiles());

        var (nextApplication, nextLauncher) = CreateApplication();

        Assert.Equal(
            WrapperApplication.ExitCodeSuccess,
            nextApplication.Run(JellyfinLikeCommand(Marker(ProfileIds.SideBySideFull))));
        Assert.Single(nextLauncher.Launches);
    }

    // ----- two binaries: which command reaches which FFmpeg -----------------------------

    [Fact]
    public void AnOrdinaryCommandIsHandedToTheServerBinaryAndTakesNoSlot()
    {
        // The reason the second binary exists: a command with no marker in it is the server's
        // own, composed around the capabilities of the FFmpeg the server would have run, and
        // sending it to the minimal FFmpeg-mvc build costs it hardware decode and encoders.
        var arguments = JellyfinLikeCommand("/library/movie.mkv");
        var (application, launcher) = CreateApplication(serverFFmpegPath: _serverFFmpeg);

        var slotFilesWhileRunning = Array.Empty<string>();
        launcher.Observe = _ => slotFilesWhileRunning = _slots.SlotFiles();

        Assert.Equal(WrapperApplication.ExitCodeSuccess, application.Run(arguments));

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(_serverFFmpeg, launch.ExecutablePath);
        Assert.Equal(arguments.ToArray(), launch.Arguments);
        Assert.Equal(string.Empty, _diagnostics.ToString());

        // Dispatch changes who a command is handed to, and nothing else: an ordinary playback
        // still neither takes a slot nor waits for one.
        Assert.Empty(slotFilesWhileRunning);
        Assert.Empty(_slots.SlotFiles());
    }

    [Fact]
    public void AMarkerCommandIsHandedToTheMvcBinaryWhileTheServerBinaryStandsBy()
    {
        // The other half of the pair: a rewritten command is written in the FFmpeg-mvc build's
        // own view-selection features, so naming a second binary must not move it. The slot is
        // asserted from inside the launch because the marker job is still the only thing the
        // limit counts, second binary or not.
        var (application, launcher) = CreateApplication(serverFFmpegPath: _serverFFmpeg);

        var slotFilesWhileRunning = Array.Empty<string>();
        launcher.Observe = _ => slotFilesWhileRunning = _slots.SlotFiles();

        Assert.Equal(WrapperApplication.ExitCodeSuccess, application.Run(JellyfinLikeCommand(Marker(ProfileIds.SideBySideFull))));

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(_realFFmpeg, launch.ExecutablePath);
        Assert.Contains("-view_ids", launch.CommandLine, StringComparison.Ordinal);

        var held = Assert.Single(slotFilesWhileRunning);
        Assert.Contains(WrapperConcurrencyGuard.SlotFileNamePrefix + "0", held, StringComparison.Ordinal);
        Assert.Empty(_slots.SlotFiles());
    }

    [Fact]
    public void AnOrdinaryCommandIsHandedToTheMvcBinaryWhenNoServerBinaryIsNamed()
    {
        // The upgrade must be invisible to a server that never heard of the second variable -
        // including the validation harness, whose whole point is that it points the Anaglyfin
        // variable at the image's own FFmpeg.
        var (application, launcher) = CreateApplicationFromEnvironment();

        Assert.Equal(WrapperApplication.ExitCodeSuccess, application.Run(JellyfinLikeCommand("/library/movie.mkv")));

        Assert.Equal(_realFFmpeg, Assert.Single(launcher.Launches).ExecutablePath);
        Assert.Equal(string.Empty, _diagnostics.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AServerVariableThatNamesNothingLeavesOrdinaryCommandsOnTheMvcBinary(string? serverValue)
    {
        // A variable exported empty is a variable that names nothing. Falling back is not only
        // compatible but safe: the other choice is a playback refused over a blank.
        var (application, launcher) = CreateApplicationFromEnvironment(
            (FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, serverValue));

        Assert.Equal(WrapperApplication.ExitCodeSuccess, application.Run(JellyfinLikeCommand("/library/movie.mkv")));

        Assert.Equal(_realFFmpeg, Assert.Single(launcher.Launches).ExecutablePath);
        Assert.Equal(string.Empty, _diagnostics.ToString());
    }

    [Fact]
    public void TheAliasNamesTheServerBinaryAndThePrimaryVariableOutranksIt()
    {
        // Two spellings of one setting, so an administrator who writes the other one is not
        // wrong - and one winner, so a deployment that has both cannot be routed by whichever
        // was read last.
        var aliased = CreateApplicationFromEnvironment(
            (FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable, _serverFFmpeg));
        Assert.Equal(
            WrapperApplication.ExitCodeSuccess,
            aliased.Application.Run(JellyfinLikeCommand("/library/movie.mkv")));
        Assert.Equal(_serverFFmpeg, Assert.Single(aliased.Launcher.Launches).ExecutablePath);

        var both = CreateApplicationFromEnvironment(
            (FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, _realFFmpeg),
            (FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable, _serverFFmpeg));
        Assert.Equal(
            WrapperApplication.ExitCodeSuccess,
            both.Application.Run(JellyfinLikeCommand("/library/movie.mkv")));
        Assert.Equal(_realFFmpeg, Assert.Single(both.Launcher.Launches).ExecutablePath);
    }

    [Fact]
    public void TheServersOwnHardwareArgumentsReachTheServerBinaryUnchanged()
    {
        // The regression this guards is a silent one: hardware options that survive the wrapper
        // but arrive at a build without those decoders still produce a video, at half speed and
        // on the CPU. The vector must be the server's, verbatim, at the server's binary.
        var arguments = JellyfinLikeHardwareCommand();
        var (application, launcher) = CreateApplication(serverFFmpegPath: _serverFFmpeg);

        Assert.Equal(WrapperApplication.ExitCodeSuccess, application.Run(arguments));

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(_serverFFmpeg, launch.ExecutablePath);
        Assert.Equal(arguments.ToArray(), launch.Arguments);
        Assert.Contains("-hwaccel vaapi", launch.CommandLine, StringComparison.Ordinal);
        Assert.Contains("-init_hw_device qsv=vaapi", launch.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("-view_ids", launch.CommandLine, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _diagnostics.ToString());
        Assert.Empty(_slots.SlotFiles());
    }

    [Fact]
    public void TheSecondBinaryIsNeverReadFromTheVariablesThatNameTheWrapper()
    {
        // JELLYFIN_FFMPEG is the wrapper's own path in every documented deployment, and
        // FFMPEG_PATH belongs to whatever else runs on this host. Either read here would be a
        // path nobody configured, and in the first case it would be the wrapper starting a
        // wrapper.
        var (application, launcher) = CreateApplicationFromEnvironment(
            ("JELLYFIN_FFMPEG", _serverFFmpeg),
            ("FFMPEG_PATH", _serverFFmpeg));

        Assert.Equal(WrapperApplication.ExitCodeSuccess, application.Run(JellyfinLikeCommand("/library/movie.mkv")));

        Assert.Equal(_realFFmpeg, Assert.Single(launcher.Launches).ExecutablePath);
        Assert.Equal(string.Empty, _diagnostics.ToString());
    }

    [Fact]
    public void AnOrdinaryPlaybackSurvivesAMissingMvcBinary()
    {
        // The dispatch only pays for itself if an ordinary command is never held hostage by the
        // binary it is not using: a broken FFmpeg-mvc install is a broken 3D feature, not a
        // server that cannot transcode its 1080p library.
        var (application, launcher) = CreateApplication(
            realFFmpegPath: Path.Combine(_binaryDirectory, "ffmpeg-mvc-not-built"),
            serverFFmpegPath: _serverFFmpeg);

        Assert.Equal(WrapperApplication.ExitCodeSuccess, application.Run(JellyfinLikeCommand("/library/movie.mkv")));
        Assert.Equal(_serverFFmpeg, Assert.Single(launcher.Launches).ExecutablePath);
        Assert.Equal(string.Empty, _diagnostics.ToString());
    }

    [Fact]
    public void AnAnaglyfinJobSurvivesAMissingServerBinary()
    {
        // The same guarantee, mirrored: the profile command needs the build it was written for,
        // and nothing in this invocation is waiting for the other one.
        var (application, launcher) = CreateApplication(
            realFFmpegPath: _realFFmpeg,
            serverFFmpegPath: Path.Combine(_binaryDirectory, "ffmpeg-server-not-built"));

        Assert.Equal(
            WrapperApplication.ExitCodeSuccess,
            application.Run(JellyfinLikeCommand(Marker(ProfileIds.AnaglyphRedCyanDubois))));

        Assert.Equal(_realFFmpeg, Assert.Single(launcher.Launches).ExecutablePath);
        Assert.Equal(string.Empty, _diagnostics.ToString());
        Assert.Empty(_slots.SlotFiles());
    }

    [Fact]
    public void AServerBinaryNamingTheWrapperIsRefusedForAnOrdinaryCommand()
    {
        var itself = Environment.ProcessPath;
        Assert.NotNull(itself);

        var (application, launcher) = CreateApplication(realFFmpegPath: _realFFmpeg, serverFFmpegPath: itself!);

        // The fork bomb is the same shape on either route, and the ordinary route is the one an
        // administrator is more likely to aim at the wrapper: the server's FFmpeg path already
        // names the wrapper, and it is tempting to copy it.
        Assert.Equal(
            WrapperApplication.ExitCodeRealFFmpegNotStarted,
            application.Run(JellyfinLikeCommand("/library/movie.mkv")));

        Assert.Empty(launcher.Launches);

        var output = _diagnostics.ToString();
        Assert.Contains("the wrapper itself", output, StringComparison.Ordinal);
        Assert.Contains(FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, output, StringComparison.Ordinal);
        Assert.DoesNotContain(FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalAboutTheServerBinaryNamesTheVariableThatNamedIt()
    {
        var itself = Environment.ProcessPath;
        Assert.NotNull(itself);

        // Two variables can name this binary and the administrator set one of them. The line has
        // to blame - and repair - the variable this server actually wrote, alias included.
        var (application, launcher) = CreateApplicationFromEnvironment(
            (FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable, itself!));

        Assert.Equal(
            WrapperApplication.ExitCodeRealFFmpegNotStarted,
            application.Run(JellyfinLikeCommand("/library/movie.mkv")));

        Assert.Empty(launcher.Launches);

        var output = _diagnostics.ToString();
        Assert.Contains("the wrapper itself", output, StringComparison.Ordinal);
        Assert.Contains(FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable, output, StringComparison.Ordinal);
        Assert.DoesNotContain(FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, output, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingServerBinaryIsRefusedOnTheOrdinaryRouteAndNamesItsOwnVariable()
    {
        var missing = Path.Combine(_binaryDirectory, "ffmpeg-server-not-built");
        var (application, launcher) = CreateApplication(serverFFmpegPath: missing);

        Assert.Equal(
            WrapperApplication.ExitCodeRealFFmpegNotStarted,
            application.Run(JellyfinLikeCommand("/library/movie.mkv")));

        Assert.Empty(launcher.Launches);

        var output = _diagnostics.ToString();
        Assert.Contains("not an existing file", output, StringComparison.Ordinal);
        Assert.Contains(FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, output, StringComparison.Ordinal);
        Assert.DoesNotContain(_binaryDirectory, output, StringComparison.Ordinal);

        // A refusal is not a job: nothing was counted, either.
        Assert.Empty(_slots.SlotFiles());
    }

    // ----- subtitle depth through the real settings channel --------------------------------

    [Fact]
    public void AnAutomaticDepthSettingReadFromThePluginFileStartsAConvertedGraphWithTheDepthFilter()
    {
        var serverGraph =
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[0:v]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
            + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]";

        var arguments = new List<string>
        {
            "-hide_banner",
            "-i", Marker(ProfileIds.SideBySideFull),
            "-filter_complex", serverGraph,
            "-map", "[out]", "-map", "0:a",
            "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
        };

        var (application, launcher) = CreateApplicationFromSettingsFile(
            new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0));

        var exitCode = application.Run(arguments);

        Assert.Equal(WrapperApplication.ExitCodeSuccess, exitCode);
        Assert.Equal(
            new[]
            {
                "-hide_banner",
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex",
                "[0:v]format=rgba[anaglyfin_composed];"
                + "[0:10]format=rgba[anaglyfin_subtitle];"
                + "[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=auto:eof_action=pass[anaglyfin_depth];"
                + "[anaglyfin_depth]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,"
                + "scale=1920:1080:flags=area:original_ip=1920:1080:ow=1920:oh=1080,format=yuv420p[out]",
                "-map", "[out]", "-map", "0:a",
                "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
            },
            Assert.Single(launcher.Launches).Arguments);
        Assert.Equal(string.Empty, _diagnostics.ToString());
    }

    [Fact]
    public void ADepthDeclinedByTheServerGraphStillStartsThePlaybackAndWritesADiagnostic()
    {
        var serverGraph =
            "[0:10]scale=1920:1080:flags=area[sub];"
            + "[0:v]subtitles=filename='/movies/eng.srt'[txt];"
            + "[txt]scale=1920:1080:flags=area,format=yuv420p[main];"
            + "[main][sub]overlay=eof_action=pass:repeatlast=0[out]";

        var arguments = new List<string>
        {
            "-hide_banner",
            "-i", Marker(ProfileIds.SideBySideFull),
            "-filter_complex", serverGraph,
            "-map", "[out]", "-map", "0:a",
            "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
        };

        var (application, launcher) = CreateApplicationFromSettingsFile(
            new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0));

        var exitCode = application.Run(arguments);

        Assert.Equal(WrapperApplication.ExitCodeSuccess, exitCode);
        Assert.Equal(
            new[]
            {
                "-hide_banner",
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex", serverGraph,
                "-map", "[out]", "-map", "0:a",
                "-c:v", "libx264", "-f", "hls", "playlist.m3u8"
            },
            Assert.Single(launcher.Launches).Arguments);

        var output = _diagnostics.ToString();
        Assert.StartsWith(WrapperApplication.DiagnosticPrefix, output);
        Assert.Contains("warning: subtitle depth was asked for and not applied", output, StringComparison.Ordinal);
        Assert.Contains("text filter", output, StringComparison.Ordinal);
    }

    // ----- refusals: nothing starts ---------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1/anaglyfin/profile/sbs_full?subtitle=0", "RejectedMarker/MissingSource")]
    [InlineData("http://127.0.0.1/anaglyfin/profile/side_by_side_too?source=%2Fmovies%2FFilm.mkv", "RejectedMarker/UnknownProfile")]
    [InlineData("http://127.0.0.1/anaglyfin/profile", "RejectedMarker/MalformedMarker")]
    [InlineData("http://127.0.0.1/anaglyfin/profile/sbs_full?source=%2Fmovies%2FFilm.mkv&subtitle=0;scale=2:2", "RejectedMarker/InvalidSubtitleOrdinal")]
    public void AMarkerThatCannotBeRewrittenStartsNothing(string marker, string classification)
    {
        var (application, launcher) = CreateApplication();

        var exitCode = application.Run(JellyfinLikeCommand(marker));

        // Running the received command instead would hand the marker to FFmpeg as a media
        // file, which is the outcome the marker contract exists to prevent.
        Assert.Equal(WrapperApplication.ExitCodeMarkerRejected, exitCode);
        Assert.Empty(launcher.Launches);
        Assert.Empty(_slots.SlotFiles());

        var output = _diagnostics.ToString();
        Assert.StartsWith(WrapperApplication.DiagnosticPrefix, output);
        Assert.Contains(classification, output, StringComparison.Ordinal);
    }

    [Fact]
    public void AMarkerBehindAnotherInputIsRefusedAsAnUnsupportedShape()
    {
        var arguments = new List<string>
        {
            "-i", "/library/other.mkv", "-i", Marker(ProfileIds.SideBySideFull),
            "-map", "1:v", "out.m3u8"
        };

        var (application, launcher) = CreateApplication();

        Assert.Equal(WrapperApplication.ExitCodeMarkerRejected, application.Run(arguments));
        Assert.Empty(launcher.Launches);
        Assert.Contains("UnsupportedCommandShape", _diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandAlreadyCarryingSomebodyElsesFilterGraphRunsAsTheMergedGraph()
    {
        // The shape Jellyfin writes when it filters the picture itself: the profile's conversion
        // joins that graph as its head chain and the server's reference to the source video is
        // retargeted onto what the conversion produces. The output still maps the label the
        // server mapped, and the wrapper runs the command rather than refusing it.
        var arguments = new List<string>
        {
            "-hide_banner", "-i", Marker(ProfileIds.AnaglyphRedCyanDubois),
            "-filter_complex", "[0:v]scale=iw/2:ih[v]", "-map", "[v]", "-map", "0:a",
            "-f", "hls", "playlist.m3u8"
        };

        var (application, launcher) = CreateApplication();

        Assert.Equal(WrapperApplication.ExitCodeSuccess, application.Run(arguments));

        Assert.Equal(
            new[]
            {
                "-hide_banner",
                "-view_ids", "-1",
                "-i", SourcePath,
                "-filter_complex",
                "[0:v]stereo3d=sbsl:arcd,format=yuv420p[anaglyfin_profile];"
                + "[anaglyfin_profile]scale=iw/2:ih[v]",
                "-map", "[v]", "-map", "0:a",
                "-f", "hls", "playlist.m3u8"
            },
            Assert.Single(launcher.Launches).Arguments);
    }

    [Fact]
    public void ACommandWhoseFilterGraphWasWrittenToAFileIsRefused()
    {
        // Nothing in the vector says what a graph in a file reads or feeds, and a merge needs
        // those facts: the refusal is the same exit code and the same classification it has
        // always been, only now for the one graph shape the wrapper truly cannot reach.
        var arguments = new List<string>
        {
            "-hide_banner", "-i", Marker(ProfileIds.AnaglyphRedCyanDubois),
            "-filter_complex_script", "/tmp/graph.txt", "-map", "[v]", "-map", "0:a",
            "-f", "hls", "playlist.m3u8"
        };

        var (application, launcher) = CreateApplication();

        Assert.Equal(WrapperApplication.ExitCodeMarkerRejected, application.Run(arguments));
        Assert.Empty(launcher.Launches);
        Assert.Contains("IncompatibleFilterGraph", _diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalSaysWhatsWrongWithoutRepeatingTheRequest()
    {
        var (application, launcher) = CreateApplication();

        application.Run(JellyfinLikeCommand("http://127.0.0.1/anaglyfin/profile/side_by_side_too?source=%2Fmovies%2FSecret%20Title.mkv"));

        Assert.Empty(launcher.Launches);

        // A command line arrives from a playback request, and the refusal is the line that
        // ends up in the server's log.
        var output = _diagnostics.ToString();
        Assert.Contains("RejectedMarker", output, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("source=", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("%2F", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AJobThatRunsWritesNothingToTheLog()
    {
        var (application, _) = CreateApplication();

        Assert.Equal(
            WrapperApplication.ExitCodeSuccess,
            application.Run(JellyfinLikeCommand(Marker(ProfileIds.AnaglyphRedCyanDubois))));

        // The stream belongs to FFmpeg, whose stderr Jellyfin reads to follow a transcode.
        // A wrapper that chatted on it would be a wrapper the server has to learn to ignore.
        Assert.Equal(string.Empty, _diagnostics.ToString());
    }

    // ----- the configured binary ------------------------------------------------------

    [Fact]
    public void AConfiguredBinaryThatDoesNotExistIsRefusedBeforeAnythingStarts()
    {
        var missing = Path.Combine(_slots.Location, "ffmpeg-mvc-not-built");
        var (application, launcher) = CreateApplication(realFFmpegPath: missing);

        Assert.Equal(
            WrapperApplication.ExitCodeRealFFmpegNotStarted,
            application.Run(JellyfinLikeCommand("/library/movie.mkv")));

        Assert.Empty(launcher.Launches);
        var output = _diagnostics.ToString();
        Assert.Contains("not an existing file", output, StringComparison.Ordinal);

        // The variable name is the actionable part; the server's filesystem layout is not.
        Assert.Contains(FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, output, StringComparison.Ordinal);
        Assert.DoesNotContain(_slots.Location, output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWrapperRefusesToBeItsOwnRealBinary()
    {
        var itself = Environment.ProcessPath;
        Assert.NotNull(itself);

        var (application, launcher) = CreateApplication(realFFmpegPath: itself!);

        // Pointing the target back at the wrapper would make every playback start another
        // wrapper, forever. Nothing is a clearer failure than a fork bomb.
        Assert.Equal(
            WrapperApplication.ExitCodeRealFFmpegNotStarted,
            application.Run(JellyfinLikeCommand("/library/movie.mkv")));

        Assert.Empty(launcher.Launches);
        Assert.Contains("the wrapper itself", _diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANullVectorIsAProgrammingErrorRatherThanAJob()
    {
        var (application, launcher) = CreateApplication();

        Assert.Throws<ArgumentNullException>(() => application.Run(null!));
        Assert.Empty(launcher.Launches);
    }

    // ----- fixture --------------------------------------------------------------------

    /// <summary>
    /// The command Jellyfin builds for an HLS transcode, with the given input token.
    /// </summary>
    private static List<string> JellyfinLikeCommand(string input)
        => new()
        {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-i",
            input,
            "-map",
            "0:v",
            "-map",
            "0:a",
            "-c:v",
            "libx264",
            "-c:a",
            "copy",
            "-f",
            "hls",
            "-hls_time",
            "6",
            "playlist.m3u8"
        };

    private static string Marker(string profileId)
        => ProfileMarker.Create(profileId, SourcePath, null).ToString();

    /// <summary>
    /// The command Jellyfin builds for a hardware-assisted 4K HDR transcode: the server's own
    /// decode devices, its own tone-mapping graph and its own encoder, with no Anaglyfin marker
    /// anywhere. This is the everyday command whose capabilities the server read off the binary
    /// it expected to be given.
    /// </summary>
    private static List<string> JellyfinLikeHardwareCommand()
        => new()
        {
            "-hide_banner", "-loglevel", "info", "-noaccurate_seek",
            "-hwaccel", "vaapi",
            "-hwaccel_flags", "allow_profile_mismatch",
            "-init_hw_device", "vaapi=vaapi:/dev/dri/renderD128",
            "-init_hw_device", "qsv=vaapi",
            "-filter_hw_device", "vaapi",
            "-i", "/library/4K HDR.mkv",
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-map", "0:0", "-map", "0:1",
            "-filter_complex",
            "[0:v:0]scale=1920:1080:flags=lanczos:format=yuv420p,"
            + "tonemapx=tonemap=bt2390:desat=0:format=nv12[v]",
            "-map", "[v]",
            "-c:v", "h264_qsv", "-preset", "veryfast", "-async", "1", "-c:a", "copy",
            "-f", "hls", "-hls_time", "6", "playlist.m3u8"
        };

    /// <summary>
    /// Builds one wrapper invocation over this test's slot directory, with a launcher that
    /// records instead of executing.
    /// </summary>
    /// <param name="maxConcurrentTranscodes">The Anaglyfin limit this invocation enforces.</param>
    /// <param name="realFFmpegPath">
    /// A configured Anaglyfin binary other than this test's stand-in, when the test is about a
    /// configuration that is wrong.
    /// </param>
    /// <param name="serverFFmpegPath">
    /// The second binary this deployment named, or null for the single-binary deployment every
    /// test here ran against before there was a second one.
    /// </param>
    private (WrapperApplication Application, FakeFFmpegProcessLauncher Launcher) CreateApplication(
        int maxConcurrentTranscodes = FFmpegWrapperOptions.DefaultMaxConcurrentTranscodes,
        string? realFFmpegPath = null,
        string? serverFFmpegPath = null)
    {
        var options = new FFmpegWrapperOptions
        {
            MaxConcurrentTranscodes = maxConcurrentTranscodes,
            LockDirectory = _slots.Location,
            RealFFmpegPath = realFFmpegPath ?? _realFFmpeg,
            RealFFmpegPathSource = FFmpegWrapperOptions.RealFFmpegEnvironmentVariable,
            ServerFFmpegPath = serverFFmpegPath,
            ServerFFmpegPathSource = serverFFmpegPath is null
                ? null
                : FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable
        };

        var launcher = new FakeFFmpegProcessLauncher();

        return (
            new WrapperApplication(options, launcher, new WrapperConcurrencyGuard(options), diagnostics: _diagnostics),
            launcher);
    }

    /// <summary>
    /// Builds one wrapper invocation the way the executable does - from an environment - over
    /// this test's slot directory and stand-in binaries, so that a test can be about the
    /// variables a deployment actually wrote.
    /// </summary>
    /// <param name="variables">
    /// The variables on top of the two every deployment sets here: the Anaglyfin binary and the
    /// slot directory. A null value stands in for a variable exported empty.
    /// </param>
    private (WrapperApplication Application, FakeFFmpegProcessLauncher Launcher) CreateApplicationFromEnvironment(
        params (string Name, string? Value)[] variables)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [FFmpegWrapperOptions.RealFFmpegEnvironmentVariable] = _realFFmpeg,
            [FFmpegWrapperOptions.LockDirectoryEnvironmentVariable] = _slots.Location
        };

        foreach (var (name, value) in variables)
        {
            environment[name] = value;
        }

        var options = FFmpegWrapperOptions.FromEnvironment(
            name => environment.TryGetValue(name, out var value) ? value : null);

        var launcher = new FakeFFmpegProcessLauncher();

        return (
            new WrapperApplication(options, launcher, new WrapperConcurrencyGuard(options), diagnostics: _diagnostics),
            launcher);
    }

    /// <summary>
    /// Builds one wrapper invocation from the real settings channel: the document the plugin writes,
    /// pointed at by the same environment variable a deployment has to set for the wrapper.
    /// </summary>
    /// <param name="settings">The subtitle-depth request the plugin published.</param>
    /// <param name="maxConcurrentTranscodes">
    /// The limit the published document states. The shipped one by default, so a test that is not
    /// about the limit sees the same wrapper it saw before this setting travelled in the document.
    /// </param>
    /// <returns>The application under test and its recording launcher.</returns>
    private (WrapperApplication Application, FakeFFmpegProcessLauncher Launcher) CreateApplicationFromSettingsFile(
        SubtitleDepthSettings settings,
        int maxConcurrentTranscodes = 1)
    {
        var settingsPath = Path.Combine(_binaryDirectory, "wrapper-settings.json");
        var wrote = WrapperSettingsFile.TryWrite(settingsPath, settings, maxConcurrentTranscodes, out var failure);

        Assert.True(wrote, failure?.Message ?? "The wrapper settings file could not be written.");

        var environment = new Dictionary<string, string?>
        {
            [FFmpegWrapperOptions.RealFFmpegEnvironmentVariable] = _realFFmpeg,
            [FFmpegWrapperOptions.LockDirectoryEnvironmentVariable] = _slots.Location,
            [WrapperSettingsFile.EnvironmentVariable] = settingsPath
        };

        var options = FFmpegWrapperOptions.FromEnvironment(
            name => environment.TryGetValue(name, out var value) ? value : null);

        var launcher = new FakeFFmpegProcessLauncher();

        return (
            new WrapperApplication(options, launcher, new WrapperConcurrencyGuard(options), diagnostics: _diagnostics),
            launcher);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file a test left open is not a failure to report.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
