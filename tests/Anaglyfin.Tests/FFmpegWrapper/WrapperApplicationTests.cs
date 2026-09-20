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
/// </remarks>
public sealed class WrapperApplicationTests : IDisposable
{
    /// <summary>The real file a marker stands in for.</summary>
    private const string SourcePath = "/movies/Movie (2010)/Movie.2010.3D.mkv";

    private readonly TemporarySlotDirectory _slots = new();
    private readonly StringWriter _diagnostics = new();

    /// <summary>The directory holding this test's stand-in for the real FFmpeg binary.</summary>
    private readonly string _binaryDirectory;

    /// <summary>The binary the wrapper is configured to hand commands to.</summary>
    private readonly string _realFFmpeg;

    /// <summary>
    /// Lays out the two things on the filesystem a wrapper invocation needs: a slot
    /// directory, and an existing file it can be told is the real FFmpeg.
    /// </summary>
    /// <remarks>
    /// The stand-in binary is never executed - the launcher records instead of starting -
    /// but it does have to exist: the wrapper refuses a configured absolute path that is
    /// not a file, and that check is the wrapper's own behaviour rather than the launcher's.
    /// </remarks>
    public WrapperApplicationTests()
    {
        _binaryDirectory = Path.Combine(Path.GetTempPath(), "anaglyfin-wrapper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_binaryDirectory);
        _realFFmpeg = Path.Combine(_binaryDirectory, "ffmpeg-mvc");

        File.WriteAllText(_realFFmpeg, "#!/bin/sh\nexit 0\n");
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
    /// Builds one wrapper invocation over this test's slot directory, with a launcher that
    /// records instead of executing.
    /// </summary>
    /// <param name="maxConcurrentTranscodes">The Anaglyfin limit this invocation enforces.</param>
    /// <param name="realFFmpegPath">
    /// A configured binary other than this test's stand-in, when the test is about a
    /// configuration that is wrong.
    /// </param>
    private (WrapperApplication Application, FakeFFmpegProcessLauncher Launcher) CreateApplication(
        int maxConcurrentTranscodes = FFmpegWrapperOptions.DefaultMaxConcurrentTranscodes,
        string? realFFmpegPath = null)
    {
        var options = new FFmpegWrapperOptions
        {
            MaxConcurrentTranscodes = maxConcurrentTranscodes,
            LockDirectory = _slots.Location,
            RealFFmpegPath = realFFmpegPath ?? _realFFmpeg,
            RealFFmpegPathSource = FFmpegWrapperOptions.RealFFmpegEnvironmentVariable
        };

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
