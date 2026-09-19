using System;
using System.Collections.Generic;
using System.IO;
using Anaglyfin.Configuration;
using Anaglyfin.FFmpegWrapper;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Tests for how the wrapper reads its configuration out of the environment.
/// </summary>
/// <remarks>
/// <para>
/// The environment is the only channel that reaches a process Jellyfin starts, so the
/// resolution order in these tests is the deployment contract: <c>ANAGLYFIN_REAL_FFMPEG</c>
/// for a wrapper-specific target, <c>FFMPEG_MVC_PATH</c> for an installation that already
/// names its FFmpeg-mvc build, and the host's own <c>PATH</c> search for a binary installed
/// where hosts look. Nothing else is a source of a path, which is why no test here has to
/// know where it is running.
/// </para>
/// <para>
/// <see cref="FFmpegWrapperOptions.FromEnvironment(Func{string,string?})"/> takes the
/// environment as a delegate, so these tests describe machines - including ones with a
/// <c>PATH</c> that has, or has not, an FFmpeg in it - without changing the one the tests
/// run on.
/// </para>
/// </remarks>
public sealed class FFmpegWrapperOptionsTests
{
    [Fact]
    public void TheAnaglyfinVariableNamesTheBinaryBeforeAnyOther()
    {
        var options = OptionsFrom(
            (FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, "/opt/anaglyfin/ffmpeg-mvc"),
            (FFmpegWrapperOptions.RealFFmpegAlternateEnvironmentVariable, "/usr/bin/ffmpeg"),
            (FFmpegWrapperOptions.PathEnvironmentVariable, "/usr/bin"));

        Assert.Equal("/opt/anaglyfin/ffmpeg-mvc", options.RealFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, options.RealFFmpegPathSource);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheDeploymentVariableNamesTheBinaryWhenTheAnaglyfinOneIsUnset(string? anaglyfinValue)
    {
        var options = OptionsFrom(
            (FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, anaglyfinValue),
            (FFmpegWrapperOptions.RealFFmpegAlternateEnvironmentVariable, "/usr/lib/jellyfin-ffmpeg/ffmpeg-mvc"));

        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffmpeg-mvc", options.RealFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.RealFFmpegAlternateEnvironmentVariable, options.RealFFmpegPathSource);
    }

    [Fact]
    public void NothingConfiguredLeavesTheBareNameForTheHostToResolve()
    {
        var options = OptionsFrom();

        // No default install directory is invented: the name is what the operating system
        // searches for, so the answer is the host's, not a guess baked into the assembly.
        Assert.Equal(FFmpegWrapperOptions.FFmpegExecutableName, options.RealFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.PathLookupSource, options.RealFFmpegPathSource);
    }

    [Fact]
    public void TheBinaryIsFoundInADirectoryOnTheSearchPath()
    {
        var directory = TemporaryDirectory();

        try
        {
            var binary = Path.Combine(directory, FFmpegWrapperOptions.FFmpegExecutableName);
            File.WriteAllText(binary, "#!/bin/sh\nexit 0\n");

            var options = OptionsFrom((FFmpegWrapperOptions.PathEnvironmentVariable, directory));

            Assert.Equal(binary, options.RealFFmpegPath);
            Assert.Equal(FFmpegWrapperOptions.PathLookupSource, options.RealFFmpegPathSource);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void ADirectoryOnTheSearchPathWithoutAnFFmpegIsNotAMatch()
    {
        var directory = TemporaryDirectory();

        try
        {
            File.WriteAllText(Path.Combine(directory, "ffprobe"), "not the encoder\n");

            var options = OptionsFrom((FFmpegWrapperOptions.PathEnvironmentVariable, directory));

            Assert.Equal(FFmpegWrapperOptions.FFmpegExecutableName, options.RealFFmpegPath);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void TheSearchPathIsWalkedInOrderAndAnEmptyEntryIsNotTheWorkingDirectory()
    {
        var first = TemporaryDirectory();
        var second = TemporaryDirectory();

        try
        {
            var firstBinary = Path.Combine(first, FFmpegWrapperOptions.FFmpegExecutableName);
            var secondBinary = Path.Combine(second, FFmpegWrapperOptions.FFmpegExecutableName);
            File.WriteAllText(firstBinary, "first\n");
            File.WriteAllText(secondBinary, "second\n");

            // The leading empty entry means the working directory in POSIX. The wrapper is
            // started in a directory it does not choose, so it does not look there for an
            // executable.
            var options = OptionsFrom(
                (FFmpegWrapperOptions.PathEnvironmentVariable,
                    string.Empty + Path.PathSeparator + first + Path.PathSeparator + second));

            Assert.Equal(firstBinary, options.RealFFmpegPath);
        }
        finally
        {
            TryDelete(first);
            TryDelete(second);
        }
    }

    [Fact]
    public void TheConcurrencyLimitIsOneUntilSomebodyRaisesIt()
    {
        Assert.Equal(FFmpegWrapperOptions.DefaultMaxConcurrentTranscodes, OptionsFrom().MaxConcurrentTranscodes);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    [InlineData(" 4 ", 4)]
    [InlineData("007", 7)]
    public void AConfiguredConcurrencyLimitIsHonoured(string value, int expected)
    {
        var options = OptionsFrom((FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable, value));

        Assert.Equal(expected, options.MaxConcurrentTranscodes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("one")]
    [InlineData("1.5")]
    public void AnUnusableConcurrencyLimitIsReadAsTheShippedDefault(string? value)
    {
        // Every one of these is a knob somebody set wrongly, and the wrapper is on the path
        // of every transcode on the server: the knob fails to the default, playback does not.
        var options = OptionsFrom((FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable, value));

        Assert.Equal(FFmpegWrapperOptions.DefaultMaxConcurrentTranscodes, options.MaxConcurrentTranscodes);
    }

    [Fact]
    public void TheSlotDirectoryDefaultsUnderTheHostsTempLocation()
    {
        var options = OptionsFrom();

        Assert.Equal(FFmpegWrapperOptions.DefaultLockDirectory, options.LockDirectory);
        Assert.StartsWith(Path.GetTempPath(), FFmpegWrapperOptions.DefaultLockDirectory);
        Assert.True(Path.IsPathRooted(FFmpegWrapperOptions.DefaultLockDirectory));
    }

    [Fact]
    public void AConfiguredSlotDirectoryIsUsedAsGiven()
    {
        // This is the override a deployment with several containers sharing one limit needs,
        // and the one the tests use to keep off the host's real slot directory.
        var options = OptionsFrom((FFmpegWrapperOptions.LockDirectoryEnvironmentVariable, "/var/lib/anaglyfin/slots"));

        Assert.Equal("/var/lib/anaglyfin/slots", options.LockDirectory);
    }

    [Fact]
    public void ARelativeSlotDirectoryLandsUnderTheDefaultRatherThanUnderTheWorkingDirectory()
    {
        var options = OptionsFrom((FFmpegWrapperOptions.LockDirectoryEnvironmentVariable, "per-worker"));

        // A relative limit would live wherever the transcode happened to start, and a limit
        // nobody can find is a limit nobody can explain.
        Assert.Equal(
            Path.Combine(FFmpegWrapperOptions.DefaultLockDirectory, "per-worker"),
            options.LockDirectory);
    }

    [Fact]
    public void TheEnvironmentOfThisProcessProducesUsableOptions()
    {
        var options = FFmpegWrapperOptions.FromEnvironment();

        // The delegate overload is the interesting behaviour, but the no-argument entry
        // point is the one the executable calls, so it is at least proven to produce
        // something the wrapper can act on whatever machine it runs on.
        Assert.False(string.IsNullOrWhiteSpace(options.RealFFmpegPath));
        Assert.False(string.IsNullOrWhiteSpace(options.LockDirectory));
        Assert.True(Path.IsPathRooted(options.LockDirectory));
        Assert.True(options.MaxConcurrentTranscodes >= 1);

        // Including on a machine that happens to have a settings file pointed at it: the
        // depth is always a value the wrapper can read, never a hole it has to check.
        Assert.NotNull(options.SubtitleDepth);
    }

    [Fact]
    public void ReadingANullEnvironmentIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => FFmpegWrapperOptions.FromEnvironment(null!));
        Assert.Throws<ArgumentNullException>(() => FFmpegWrapperOptions.ResolveRealFFmpegPath(null!));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "automatic", "shiftPixels": 0, "plane": 0 } }""", true, SubtitleDepthMode.Automatic, 0, 0)]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "constantShift", "shiftPixels": 40, "plane": 0 } }""", true, SubtitleDepthMode.ConstantShift, 40, 0)]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "constantShift", "shiftPixels": -40, "plane": 0 } }""", true, SubtitleDepthMode.ConstantShift, -40, 0)]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "plane", "shiftPixels": 0, "plane": 9 } }""", true, SubtitleDepthMode.Plane, 0, 9)]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": false, "mode": "plane", "shiftPixels": 0, "plane": 9 } }""", false, SubtitleDepthMode.Automatic, 0, 0)]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "flat", "shiftPixels": 0, "plane": 9 } }""", false, SubtitleDepthMode.Automatic, 0, 0)]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "plane", "shiftPixels": 0, "plane": 32 } }""", false, SubtitleDepthMode.Automatic, 0, 0)]
    [InlineData("""{ "schemaVersion": 4, "subtitleDepth": { "enabled": true, "mode": "plane", "shiftPixels": 0, "plane": 9 } }""", false, SubtitleDepthMode.Automatic, 0, 0)]
    [InlineData("""this is not a document""", false, SubtitleDepthMode.Automatic, 0, 0)]
    public void TheDepthTheDocumentStatesReachesTheInvocation(
        string written,
        bool enabled,
        SubtitleDepthMode mode,
        int shiftPixels,
        int plane)
    {
        // Written here as text rather than as what the plugin's writer produces, so that what
        // is being tested is the wrapper's reading of the published shape and not its agreement
        // with itself. A deployment can edit this file, and so can a newer plugin.
        var directory = TemporaryDirectory();

        try
        {
            var path = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);
            File.WriteAllText(path, written);

            var options = OptionsFrom((WrapperSettingsFile.EnvironmentVariable, path));

            Assert.Equal(new SubtitleDepthSettings(enabled, mode, shiftPixels, plane), options.SubtitleDepth);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void TheFileThePluginWritesIsTheFileTheWrapperReads()
    {
        // The two halves of the bridge, in one test: the settings model says what it wants, the
        // plugin's writer states it, and this process reads it back as the same request. A
        // spelling that drifted on either side is invisible everywhere else until playback.
        var directory = TemporaryDirectory();

        try
        {
            var configuration = new PluginConfiguration
            {
                SubtitleDepthEnabled = true,
                SubtitleDepthMode = SubtitleDepthMode.Plane,
                SubtitleDepthShift = 12,
                SubtitleDepthPlane = 6
            };

            var request = configuration.GetEffectiveSubtitleDepth();
            var path = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);

            Assert.True(WrapperSettingsFile.TryWrite(path, request, out var failure));
            Assert.Null(failure);

            Assert.Equal(request, OptionsFrom((WrapperSettingsFile.EnvironmentVariable, path)).SubtitleDepth);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void AWrapperWithNothingToReadOffersNoDepth()
    {
        // Every one of these is a real deployment: the variable unset, the file not yet written,
        // a plugin that died mid-save, a data directory the wrapper cannot open. None of them is
        // a transcode failure, and none of them is answered by a guess.
        var directory = TemporaryDirectory();

        try
        {
            var absent = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);
            var unreadable = Path.Combine(directory, "unreadable");

            Assert.Equal(SubtitleDepthSettings.Disabled, OptionsFrom((WrapperSettingsFile.EnvironmentVariable, absent)).SubtitleDepth);
            Assert.Equal(SubtitleDepthSettings.Disabled, OptionsFrom((WrapperSettingsFile.EnvironmentVariable, null)).SubtitleDepth);
            Assert.Equal(SubtitleDepthSettings.Disabled, OptionsFrom((WrapperSettingsFile.EnvironmentVariable, "  ")).SubtitleDepth);
            Assert.Equal(SubtitleDepthSettings.Disabled, OptionsFrom().SubtitleDepth);

            File.WriteAllText(absent, """{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mo""");

            Assert.Equal(SubtitleDepthSettings.Disabled, OptionsFrom((WrapperSettingsFile.EnvironmentVariable, absent)).SubtitleDepth);

            Directory.CreateDirectory(unreadable);

            Assert.Equal(SubtitleDepthSettings.Disabled, OptionsFrom((WrapperSettingsFile.EnvironmentVariable, unreadable)).SubtitleDepth);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void ASettingsDocumentLeavesTheProcessLimitsWhereTheDeploymentPutThem()
    {
        // The document is the plugin's channel and the environment is the deployment's, and the
        // two are not allowed to dispute a setting: a file in a data directory does not decide how
        // many FFmpeg processes this server starts, and it does not move where the running jobs
        // are counted - a limit nobody can find is a limit nobody can explain.
        var directory = TemporaryDirectory();

        try
        {
            var path = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);

            File.WriteAllText(
                path,
                """
                {
                  "schemaVersion": 1,
                  "subtitleDepth": { "enabled": true, "mode": "automatic", "shiftPixels": 0, "plane": 0 },
                  "maxConcurrentTranscodes": 99,
                  "lockDirectory": "/somewhere/else",
                  "realFFmpeg": "/bin/false"
                }
                """);

            var configured = OptionsFrom(
                (WrapperSettingsFile.EnvironmentVariable, path),
                (FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable, "3"),
                (FFmpegWrapperOptions.LockDirectoryEnvironmentVariable, "/var/lib/anaglyfin/slots"));

            Assert.Equal(new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0), configured.SubtitleDepth);
            Assert.Equal(3, configured.MaxConcurrentTranscodes);
            Assert.Equal("/var/lib/anaglyfin/slots", configured.LockDirectory);

            // And the document does not get to set them by being the only source either: with no
            // variables, the shipped defaults stand whatever it claims.
            var unconfigured = OptionsFrom((WrapperSettingsFile.EnvironmentVariable, path));

            Assert.Equal(FFmpegWrapperOptions.DefaultMaxConcurrentTranscodes, unconfigured.MaxConcurrentTranscodes);
            Assert.Equal(FFmpegWrapperOptions.DefaultLockDirectory, unconfigured.LockDirectory);
            Assert.Equal(FFmpegWrapperOptions.FFmpegExecutableName, unconfigured.RealFFmpegPath);
            Assert.True(unconfigured.SubtitleDepth.Enabled);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>
    /// Reads options from an environment that contains only the listed variables.
    /// </summary>
    private static FFmpegWrapperOptions OptionsFrom(params (string Name, string? Value)[] variables)
    {
        var view = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in variables)
        {
            view[name] = value;
        }

        return FFmpegWrapperOptions.FromEnvironment(name => view.TryGetValue(name, out var value) ? value : null);
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "anaglyfin-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
