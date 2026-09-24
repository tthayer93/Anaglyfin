using System;
using System.Collections.Generic;
using System.Globalization;
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
/// where hosts look. The second binary - the one ordinary commands are handed to - is read
/// from <c>ANAGLYFIN_SERVER_FFMPEG</c> or its alias and from nowhere else, and an environment
/// that names neither leaves the wrapper with the single binary it had before. Nothing else is
/// a source of a path, which is why no test here has to know where it is running.
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

    // ----- how long a refusal waits ------------------------------------------------------

    [Fact]
    public void ARefusalWaitsForTheShippedMomentWhenTheDeploymentSaidNothing()
    {
        var options = OptionsFrom();

        // Short enough that nobody reads it as a queue and long enough to outlast the handover it is
        // waiting for. Pinned as a number because a wait nobody remembers is a wait somebody will
        // "optimise" to zero without noticing what it was for.
        Assert.Equal(TimeSpan.FromMilliseconds(FFmpegWrapperOptions.DefaultSlotWaitMilliseconds), options.SlotWait);
        Assert.Equal(FFmpegWrapperOptions.DefaultSlotWait, options.SlotWait);
    }

    [Fact]
    public void ADeploymentNamesThePatienceOfARefusalInTheVariableForIt()
    {
        var options = OptionsFrom((FFmpegWrapperOptions.SlotWaitEnvironmentVariable, "750"));

        Assert.Equal(TimeSpan.FromMilliseconds(750), options.SlotWait);

        // Zero is a request and not a mistake: a deployment that would rather hear "no" at once gets it.
        Assert.Equal(
            TimeSpan.Zero,
            OptionsFrom((FFmpegWrapperOptions.SlotWaitEnvironmentVariable, "0")).SlotWait);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("1.5")]
    public void AnUnusableWaitIsReadAsTheShippedOne(string? value)
    {
        // The same rule every other knob on this type follows: a mistyped number on a wrapper that is
        // on the path of every transcode costs the knob, not the playback.
        Assert.Equal(
            FFmpegWrapperOptions.DefaultSlotWait,
            OptionsFrom((FFmpegWrapperOptions.SlotWaitEnvironmentVariable, value)).SlotWait);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100000)]
    public void ANegativeWaitIsReadAsWaitingNotAtAll(int value)
    {
        // A negative wait is not a duration anybody means; the nearest answer the wrapper will honour
        // is the refusal that asks for none of it, which is also the answer of a wrapper that was
        // configured before there was a wait at all.
        Assert.Equal(
            TimeSpan.Zero,
            OptionsFrom((FFmpegWrapperOptions.SlotWaitEnvironmentVariable, value.ToString(CultureInfo.InvariantCulture))).SlotWait);
    }

    [Fact]
    public void AWaitLongerThanAWrapperIsWillingToHoldIsClampedAndNotDiscarded()
    {
        // A wrapper that waits on a typo is a playback that never starts and never says why, so the
        // longest hold this one accepts is the answer. Clamped rather than replaced, because the
        // someone who typed an hour plainly did not mean the shipped two seconds.
        var options = OptionsFrom((FFmpegWrapperOptions.SlotWaitEnvironmentVariable, "3600000"));

        Assert.Equal(FFmpegWrapperOptions.MaximumSlotWait, options.SlotWait);
        Assert.Equal(TimeSpan.FromMilliseconds(FFmpegWrapperOptions.MaximumSlotWaitMilliseconds), options.SlotWait);
    }

    [Fact]
    public void TheWaitOfAnInvocationIsReadFromTheEnvironmentLikeTheRestOfIt()
    {
        var options = FFmpegWrapperOptions.FromEnvironment();

        // The entry point the executable calls, on whatever machine this runs on: a wait it produced
        // out of nothing is still a wait, and never a negative one.
        Assert.True(options.SlotWait >= TimeSpan.Zero);
        Assert.True(options.SlotWait <= FFmpegWrapperOptions.MaximumSlotWait);
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

    // ----- the second binary: the FFmpeg the server would have run -----------------------

    [Fact]
    public void TheServerVariableNamesTheOrdinaryBinarySeparatelyFromTheMvcOne()
    {
        // Two variables, two binaries, and neither one read out of the other: the deployment
        // that has a stock FFmpeg beside the FFmpeg-mvc build is the deployment this exists for.
        var options = OptionsFrom(
            (FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, "/config/anaglyfin/ffmpeg/ffmpeg-mvc"),
            (FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, "/usr/lib/jellyfin-ffmpeg/ffmpeg"));

        Assert.Equal("/config/anaglyfin/ffmpeg/ffmpeg-mvc", options.RealFFmpegPath);
        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffmpeg", options.ServerFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, options.ServerFFmpegPathSource);
        Assert.True(options.HasServerFFmpegPath);
        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffmpeg", options.OrdinaryFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, options.OrdinaryFFmpegPathSource);
    }

    [Fact]
    public void TheAliasNamesTheOrdinaryBinaryAndSaysWhichNameItUsed()
    {
        // The alias exists so an administrator who wrote the other spelling is not wrong, and the
        // source exists so a refusal can name the spelling this server actually wrote.
        var options = OptionsFrom(
            (FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable, "/usr/bin/ffmpeg"));

        Assert.Equal("/usr/bin/ffmpeg", options.ServerFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable, options.ServerFFmpegPathSource);
        Assert.Equal("/usr/bin/ffmpeg", options.OrdinaryFFmpegPath);
        Assert.Equal(
            FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable,
            options.OrdinaryFFmpegPathSource);
    }

    [Fact]
    public void ThePrimaryServerVariableDecidesWhenTheAliasIsSetToo()
    {
        var options = OptionsFrom(
            (FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, "/usr/lib/jellyfin-ffmpeg/ffmpeg"),
            (FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable, "/elsewhere/ffmpeg"));

        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffmpeg", options.OrdinaryFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, options.OrdinaryFFmpegPathSource);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetOrBlankServerVariableKeepsOrdinaryCommandsOnTheMvcBinary(string? serverValue)
    {
        // The old deployment, the leftover empty export, and the half-written systemd line: all
        // three keep behaving exactly as they did before the second variable existed, wording
        // included, which is why the fallback states the first binary's source as well.
        var options = OptionsFrom(
            (FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, "/config/anaglyfin/ffmpeg/ffmpeg-mvc"),
            (FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, serverValue));

        Assert.Null(options.ServerFFmpegPath);
        Assert.Null(options.ServerFFmpegPathSource);
        Assert.False(options.HasServerFFmpegPath);
        Assert.Equal("/config/anaglyfin/ffmpeg/ffmpeg-mvc", options.OrdinaryFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, options.OrdinaryFFmpegPathSource);
    }

    [Fact]
    public void NothingConfiguredLeavesBothRoutesOnTheSameHostResolvedName()
    {
        // With no variables at all, the ordinary route is not given a lookup of its own: one
        // answer, one search, one binary.
        var options = OptionsFrom();

        Assert.Null(options.ServerFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.FFmpegExecutableName, options.RealFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.FFmpegExecutableName, options.OrdinaryFFmpegPath);
        Assert.Equal(FFmpegWrapperOptions.PathLookupSource, options.OrdinaryFFmpegPathSource);
    }

    [Fact]
    public void TheSearchPathNamesOneBinaryAndIsNotAskedTwice()
    {
        // The PATH search exists because a host may have installed its one binary in a standard
        // place, and it is asked once, for the binary that has nothing else to name it. A second
        // binary a search invented would be a routing decision nobody made: an ordinary command
        // quietly sent somewhere the marker command is not.
        var directory = TemporaryDirectory();

        try
        {
            var binary = Path.Combine(directory, FFmpegWrapperOptions.FFmpegExecutableName);
            File.WriteAllText(binary, "#!/bin/sh\nexit 0\n");

            var options = OptionsFrom((FFmpegWrapperOptions.PathEnvironmentVariable, directory));

            Assert.True(File.Exists(binary), "The stand-in FFmpeg the search is meant to find.");
            Assert.Equal(binary, options.RealFFmpegPath);
            Assert.Equal(FFmpegWrapperOptions.PathLookupSource, options.RealFFmpegPathSource);
            Assert.Null(options.ServerFFmpegPath);
            Assert.False(options.HasServerFFmpegPath);
            Assert.Equal(binary, options.OrdinaryFFmpegPath);
            Assert.Equal(FFmpegWrapperOptions.PathLookupSource, options.OrdinaryFFmpegPathSource);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Theory]
    [InlineData("JELLYFIN_FFMPEG")]
    [InlineData("FFMPEG_PATH")]
    public void AVariableThatDoesNotBelongToTheWrapperNeverNamesTheSecondBinary(string variable)
    {
        // JELLYFIN_FFMPEG is the wrapper's own path in every documented deployment, so reading it
        // would point the ordinary route back at this executable; FFMPEG_PATH belongs to
        // whatever else runs on this host. Neither is a value this wrapper was given.
        var options = OptionsFrom(
            (FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, "/config/anaglyfin/ffmpeg/ffmpeg-mvc"),
            (variable, "/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg"));

        Assert.Null(options.ServerFFmpegPath);
        Assert.Equal("/config/anaglyfin/ffmpeg/ffmpeg-mvc", options.OrdinaryFFmpegPath);
    }

    [Fact]
    public void ReadingANullEnvironmentIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => FFmpegWrapperOptions.FromEnvironment(null!));
        Assert.Throws<ArgumentNullException>(() => FFmpegWrapperOptions.ResolveRealFFmpegPath(null!));
        Assert.Throws<ArgumentNullException>(() => FFmpegWrapperOptions.ResolveServerFFmpegPath(null!));
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
                SubtitleDepthMode = SubtitleDepthMode.Plane,
                SubtitleDepthShift = 12,
                SubtitleDepthPlane = 6,
                MaxConcurrentTranscodes = 2
            };

            var request = configuration.GetEffectiveSubtitleDepth();
            var limit = configuration.GetEffectiveMaxConcurrentTranscodes();
            var path = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);

            Assert.True(WrapperSettingsFile.TryWrite(path, request, limit, out var failure));
            Assert.Null(failure);

            var options = OptionsFrom((WrapperSettingsFile.EnvironmentVariable, path));

            Assert.Equal(request, options.SubtitleDepth);
            Assert.Equal(2, options.MaxConcurrentTranscodes);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void TheLimitTheDocumentStatesDecidesWhenTheDeploymentSaysNothing()
    {
        // The whole point of publishing the limit: an administrator set it on the page, no variable
        // names it, and the wrapper counts the number the page was left on rather than the shipped
        // one. This is the shape of an ordinary deployment.
        Assert.Equal(5, LimitFromDocument("""{ "schemaVersion": 2, "transcoding": { "maxConcurrentTranscodes": 5 } }"""));
    }

    [Fact]
    public void TheEnvironmentOverridesTheLimitTheDocumentStates()
    {
        // Two authorities stated a number, and the deployment's wins: the variable was written about
        // this machine by whoever runs it, and the document is the product-wide answer the same
        // person overrode.
        var directory = TemporaryDirectory();

        try
        {
            var path = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);
            File.WriteAllText(path, """{ "schemaVersion": 2, "transcoding": { "maxConcurrentTranscodes": 9 } }""");

            var options = OptionsFrom(
                (WrapperSettingsFile.EnvironmentVariable, path),
                (FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable, "2"));

            Assert.Equal(2, options.MaxConcurrentTranscodes);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void AnUnusableEnvironmentLimitLetsTheDocumentDecide()
    {
        // A knob the deployment set wrongly is not usable, and the next authority is the answer - the
        // same way a typo lands on the shipped default when no document states anything either.
        foreach (var unusable in new[] { "  ", "0", "-3", "many", "2.5" })
        {
            var directory = TemporaryDirectory();

            try
            {
                var path = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);
                File.WriteAllText(path, """{ "schemaVersion": 2, "transcoding": { "maxConcurrentTranscodes": 6 } }""");

                var options = OptionsFrom(
                    (WrapperSettingsFile.EnvironmentVariable, path),
                    (FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable, unusable));

                Assert.Equal(6, options.MaxConcurrentTranscodes);
            }
            finally
            {
                TryDelete(directory);
            }
        }
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "automatic" } }""")]
    [InlineData("""{ "schemaVersion": 2, "subtitleDepth": { "enabled": true, "mode": "automatic" } }""")]
    [InlineData("""{ "schemaVersion": 2, "transcoding": { "maxConcurrentTranscodes": 0 } }""")]
    [InlineData("""{ "schemaVersion": 2, "transcoding": { "maxConcurrentTranscodes": "plenty" } }""")]
    [InlineData("""{ "schemaVersion": 2, "transcoding": [] }""")]
    [InlineData("not a document")]
    public void ADocumentThatStatesNoUsableLimitLeavesTheLimitWhereItWas(string documentText)
    {
        // A version 1 file, a section written wrongly, and no file at all are the same answer for the
        // limit: nobody stated one, so the shipped one stands. A wrapper does not invent a process
        // count out of a file it could not read.
        Assert.Equal(FFmpegWrapperOptions.DefaultMaxConcurrentTranscodes, LimitFromDocument(documentText));
    }

    [Fact]
    public void AVersionOneDocumentStillCarriesItsDepth()
    {
        // The file a server that has not restarted since the upgrade still has on disk. Its depth is
        // honoured exactly as it was, and the limit it never stated stays nobody's opinion: an
        // upgrade must not cost a running server the one setting it had already published.
        var directory = TemporaryDirectory();

        try
        {
            var path = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);
            File.WriteAllText(
                path,
                """{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "plane", "shiftPixels": 0, "plane": 7 } }""");

            var options = OptionsFrom((WrapperSettingsFile.EnvironmentVariable, path));

            Assert.Equal(new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 7), options.SubtitleDepth);
            Assert.Equal(FFmpegWrapperOptions.DefaultMaxConcurrentTranscodes, options.MaxConcurrentTranscodes);
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

            File.WriteAllText(absent, """{ "schemaVersion": 2, "subtitleDepth": { "enabled": true, "mo""");

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
    public void ASettingsDocumentDoesNotGetToNameTheBinaryTheSlotDirectoryOrTheWait()
    {
        // The document is the plugin's channel and the environment is the deployment's. The one
        // setting they both may state is the limit, and the environment wins even there; the binary
        // the commands go to, the directory running jobs are counted in and the patience of a refusal
        // stay the deployment's alone, because a file a plugin writes can know none of them: not where
        // a server chose to put its slots, and not how long its players take to retry.
        var directory = TemporaryDirectory();

        try
        {
            var path = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);

            File.WriteAllText(
                path,
                """
                {
                  "schemaVersion": 2,
                  "subtitleDepth": { "enabled": true, "mode": "automatic", "shiftPixels": 0, "plane": 0 },
                  "lockDirectory": "/somewhere/else",
                  "slotWaitMs": 900000,
                  "realFFmpeg": "/bin/false",
                  "serverFFmpeg": "/bin/echo"
                }
                """);

            var configured = OptionsFrom(
                (WrapperSettingsFile.EnvironmentVariable, path),
                (FFmpegWrapperOptions.LockDirectoryEnvironmentVariable, "/var/lib/anaglyfin/slots"));

            Assert.Equal(new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0), configured.SubtitleDepth);
            Assert.Equal("/var/lib/anaglyfin/slots", configured.LockDirectory);
            Assert.Null(configured.ServerFFmpegPath);

            // And the document does not get to set them by being the only source either: with no
            // variables, the shipped defaults stand whatever it claims.
            var unconfigured = OptionsFrom((WrapperSettingsFile.EnvironmentVariable, path));

            Assert.Equal(FFmpegWrapperOptions.DefaultLockDirectory, unconfigured.LockDirectory);
            Assert.Equal(FFmpegWrapperOptions.FFmpegExecutableName, unconfigured.RealFFmpegPath);
            Assert.Null(unconfigured.ServerFFmpegPath);
            Assert.Equal(FFmpegWrapperOptions.FFmpegExecutableName, unconfigured.OrdinaryFFmpegPath);
            Assert.Equal(FFmpegWrapperOptions.DefaultMaxConcurrentTranscodes, unconfigured.MaxConcurrentTranscodes);

            // The wait a refusal is made of is the newest of these and the one most tempting to move:
            // it is a duration a player's retry decides, and the plugin has never met this server's
            // players. It stays where the rest of the machine's numbers are.
            Assert.Equal(FFmpegWrapperOptions.DefaultSlotWait, unconfigured.SlotWait);

            Assert.True(unconfigured.SubtitleDepth.Enabled);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>
    /// Reads the concurrency limit an invocation sees when the settings document holds the given
    /// text and no variable states a limit of its own.
    /// </summary>
    private static int LimitFromDocument(string documentText)
    {
        var directory = TemporaryDirectory();

        try
        {
            var path = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);
            File.WriteAllText(path, documentText);

            return OptionsFrom((WrapperSettingsFile.EnvironmentVariable, path)).MaxConcurrentTranscodes;
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
