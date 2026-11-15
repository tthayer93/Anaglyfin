using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.RuntimeStatus;
using Xunit;

namespace Anaglyfin.Tests.RuntimeStatus;

/// <summary>
/// The runtime-status checks, exercised against a throwaway directory tree and a fake probe: a
/// healthy deployment and every way one of its required parts can be missing, unrunnable, writable
/// or lying about what it is. No test here runs a real FFmpeg.
/// </summary>
public sealed class RuntimeStatusServiceTests
{
    private const string PluginVersion = "1.2.3";

    [Fact]
    public void AFullHealthyDeploymentIsOk()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Ok, status.OverallStatus);
        Assert.Equal(PluginVersion, status.PluginVersion);
        Assert.NotEqual(default, status.CheckedAtUtc);

        // Four binaries, in the fixed order the page depends on.
        Assert.Equal(
            new[]
            {
                RuntimeStatusService.WrapperKey,
                RuntimeStatusService.FfmpegMvcKey,
                RuntimeStatusService.OfficialFfmpegKey,
                RuntimeStatusService.FfprobeKey
            },
            status.Binaries.Select(binary => binary.Key).ToArray());

        // Five environment entries: the wrapper variable, the two FFmpeg variables, the lock and the
        // settings variable, and nothing else.
        Assert.Equal(
            new[]
            {
                RuntimeStatusService.JellyfinFFmpegEnvironmentVariable,
                FFmpegWrapperOptions.RealFFmpegEnvironmentVariable,
                FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable,
                TranscodeSlotStore.LockDirectoryEnvironmentVariable,
                WrapperSettingsFile.EnvironmentVariable
            },
            status.Environment.Select(entry => entry.Name).ToArray());

        var wrapper = Bin(status, RuntimeStatusService.WrapperKey);
        Assert.Equal(RuntimeItemState.Ok, wrapper.State);
        Assert.Equal(PluginVersion, wrapper.Version);
        Assert.Equal(paths.Wrapper, wrapper.ResolvedPath);
        Assert.True(wrapper.Required);

        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.FfmpegMvcKey).State);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.OfficialFfmpegKey).State);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.FfprobeKey).State);

        // The FFmpeg rows carry the whole version token of the banner their binary answered with.
        Assert.Equal("6.1.1-mvc11-jf2", Bin(status, RuntimeStatusService.FfmpegMvcKey).Version);
        Assert.Equal("6.1.1-mvc11-jf2", Bin(status, RuntimeStatusService.OfficialFfmpegKey).Version);
        Assert.Equal("6.1.1-mvc11-jf2", Bin(status, RuntimeStatusService.FfprobeKey).Version);

        // The sidecar is the wrapper's sibling, named ffprobe.
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(paths.Wrapper)!, "ffprobe"),
            Bin(status, RuntimeStatusService.FfprobeKey).ResolvedPath);

        Assert.All(status.Binaries, binary => Assert.Null(binary.Detail));
    }

    [Fact]
    public void TheWrapperIsAskedOnlyItsIdentityAndNeverAMediaProbe()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        Status(Create(harness, probe));

        var wrapperCalls = probe.Calls.Where(call => call.ExecutablePath == paths.Wrapper).ToArray();

        // One call, the identity flag, and nothing that looks like a media path or an FFmpeg banner.
        var call = Assert.Single(wrapperCalls);
        Assert.Equal(new[] { "--anaglyfin-version" }, call.Arguments);
        Assert.DoesNotContain("-version", call.Arguments);
        Assert.DoesNotContain("anaglyfin-marker", call.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrapperThatIsNotConfiguredIsAnErrorAndIsNeverProbed()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);
        harness.Env[RuntimeStatusService.JellyfinFFmpegEnvironmentVariable] = null;

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        Assert.Equal(RuntimeItemState.Missing, Bin(status, RuntimeStatusService.WrapperKey).State);
        Assert.False(Env(status, RuntimeStatusService.JellyfinFFmpegEnvironmentVariable).IsSet);
        Assert.DoesNotContain(probe.Calls, call => call.ExecutablePath!.EndsWith("ffmpeg", StringComparison.Ordinal));
    }

    [Fact]
    public void AWrapperThatIsNotExecutableIsAnErrorAndIsNeverStarted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // Same path, but without the execute bit the service account needs.
        harness.WriteNonExecutableFile("bin", "ffmpeg");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        Assert.Equal(RuntimeItemState.NotExecutable, Bin(status, RuntimeStatusService.WrapperKey).State);
        Assert.DoesNotContain(probe.Calls, call => call.ExecutablePath == paths.Wrapper);
    }

    [Fact]
    public void AWrapperThatCannotBeReadIsAnErrorAndIsNeverStarted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // Same path, but the service account cannot read the file to start it.
        harness.WriteUnreadableFile("bin", "ffmpeg");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        Assert.Equal(RuntimeItemState.NotReadable, Bin(status, RuntimeStatusService.WrapperKey).State);
        Assert.DoesNotContain(probe.Calls, call => call.ExecutablePath == paths.Wrapper);
    }

    [Fact]
    public void AWrapperPathThatIsTheFfmpegMvcBuildNamesThatFfmpeg()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // JELLYFIN_FFMPEG aimed at the very FFmpeg-mvc file. The same path is then asked two questions:
        // the identity flag, which an FFmpeg refuses, and its own -version, which it answers.
        harness.Env[RuntimeStatusService.JellyfinFFmpegEnvironmentVariable] = paths.FfmpegMvc;
        probe.Responder = (executable, arguments) =>
            arguments[0] == "--anaglyfin-version"
                ? RuntimeVersionProbeResult.Ran(1, null)
                : RuntimeVersionProbeResult.Ran(0, "ffmpeg version 6.1.1-mvc11-jf2 Copyright (c) the FFmpeg developers");

        var status = Status(Create(harness, probe));

        // A wrapper path that IS an FFmpeg is a misconfiguration, not a mystery.
        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        var wrapper = Bin(status, RuntimeStatusService.WrapperKey);
        Assert.Equal(RuntimeItemState.ProbeFailed, wrapper.State);
        Assert.Null(wrapper.Version);
        Assert.Contains("is the same file as the FFmpeg-mvc build", wrapper.Detail, StringComparison.Ordinal);
        Assert.Contains("points at FFmpeg, not at the Anaglyfin wrapper", wrapper.Detail, StringComparison.Ordinal);

        // It is not also told it is out of date, and the variable row carries the same advice.
        Assert.DoesNotContain("outdated wrapper", wrapper.Detail, StringComparison.Ordinal);
        Assert.Equal(wrapper.Detail, Env(status, RuntimeStatusService.JellyfinFFmpegEnvironmentVariable).Detail);
    }

    [Fact]
    public void AWrapperPathThatIsTheOfficialFfmpegNamesThatFfmpeg()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // The server's own FFmpeg where the wrapper belongs: still FFmpeg, still the wrong file.
        harness.Env[RuntimeStatusService.JellyfinFFmpegEnvironmentVariable] = paths.Official;
        probe.Responder = (executable, arguments) =>
            arguments[0] == "--anaglyfin-version"
                ? RuntimeVersionProbeResult.Ran(1, null)
                : RuntimeVersionProbeResult.Ran(0, "ffmpeg version 6.1.1 Copyright (c) the FFmpeg developers");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        var wrapper = Bin(status, RuntimeStatusService.WrapperKey);
        Assert.Equal(RuntimeItemState.ProbeFailed, wrapper.State);
        Assert.Null(wrapper.Version);
        Assert.Contains("is the same file as the official/server FFmpeg", wrapper.Detail, StringComparison.Ordinal);
        Assert.Contains("points at FFmpeg, not at the Anaglyfin wrapper", wrapper.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("outdated wrapper", wrapper.Detail, StringComparison.Ordinal);

        // And the other three rows are untouched: the error is the wrapper row's alone.
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.FfmpegMvcKey).State);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.OfficialFfmpegKey).State);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.FfprobeKey).State);
    }

    [Fact]
    public void ABinaryThatAnswersTheIdentityLineIsTheWrapperEvenWhereAnFfmpegPathNamesItToo()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // The identity line is the proof. A deployment that also names this file as its FFmpeg-mvc
        // build must not talk the answered identity out of being the wrapper.
        harness.Env[RuntimeStatusService.JellyfinFFmpegEnvironmentVariable] = paths.FfmpegMvc;
        probe.ByPath[paths.FfmpegMvc] = RuntimeVersionProbeResult.Ran(0, "anaglyfin-wrapper " + PluginVersion);

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Ok, status.OverallStatus);
        var wrapper = Bin(status, RuntimeStatusService.WrapperKey);
        Assert.Equal(RuntimeItemState.Ok, wrapper.State);
        Assert.Equal(PluginVersion, wrapper.Version);
        Assert.Null(wrapper.Detail);
    }

    [Fact]
    public void AWrapperThatAnswersTheIdentityFlagWithAFfmpegBannerPointsAtAnOutdatedWrapper()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // An ordinary FFmpeg answers the identity flag with its own banner, not the wrapper line. The
        // wrapper path is neither FFmpeg this pass resolved, so the honest answer is that the binary
        // never identified itself - and the cause an admin can act on is a wrapper left behind.
        probe.ByPath[paths.Wrapper] = RuntimeVersionProbeResult.Ran(0, "ffmpeg version 6.1.1 Copyright");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Warning, status.OverallStatus);
        var wrapper = Bin(status, RuntimeStatusService.WrapperKey);
        Assert.Equal(RuntimeItemState.ProbeFailed, wrapper.State);
        Assert.Null(wrapper.Version);
        Assert.Contains("did not answer --anaglyfin-version", wrapper.Detail, StringComparison.Ordinal);
        Assert.Contains("outdated wrapper", wrapper.Detail, StringComparison.Ordinal);
        Assert.Contains("update the anaglyfin-ffmpeg wrapper to the release matching the plugin", wrapper.Detail, StringComparison.Ordinal);
        Assert.Contains("(" + PluginVersion + ")", wrapper.Detail, StringComparison.Ordinal);
        Assert.Contains("names the wrapper rather than a plain FFmpeg", wrapper.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("it is not a real wrapper", wrapper.Detail, StringComparison.Ordinal);
        Assert.Equal(wrapper.Detail, Env(status, RuntimeStatusService.JellyfinFFmpegEnvironmentVariable).Detail);

        // The warning is this row's alone; nothing else in the deployment went wrong.
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.FfmpegMvcKey).State);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.OfficialFfmpegKey).State);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.FfprobeKey).State);
    }

    [Fact]
    public void AWrapperThatRefusesTheIdentityFlagPointsAtAnOutdatedWrapper()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // A binary that exits without ever writing the identity line is the same unanswered question as
        // a banner: the pre-0.2.3 wrapper has no such flag to answer. The second FFmpeg is not even
        // configured here, so a path that resolved to nothing must not become a second answer.
        harness.Env[FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable] = null;
        probe.ByPath[paths.Wrapper] = RuntimeVersionProbeResult.Ran(1, null);

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Warning, status.OverallStatus);
        var wrapper = Bin(status, RuntimeStatusService.WrapperKey);
        Assert.Equal(RuntimeItemState.ProbeFailed, wrapper.State);
        Assert.Contains("did not answer --anaglyfin-version", wrapper.Detail, StringComparison.Ordinal);
        Assert.Contains("outdated wrapper", wrapper.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("it is not a real wrapper", wrapper.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrapperOnADifferentVersionIsAWarningNotAnError()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        probe.ByPath[paths.Wrapper] = RuntimeVersionProbeResult.Ran(0, "anaglyfin-wrapper 9.9.9");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Warning, status.OverallStatus);
        var wrapper = Bin(status, RuntimeStatusService.WrapperKey);
        Assert.Equal(RuntimeItemState.VersionMismatch, wrapper.State);
        Assert.Equal("9.9.9", wrapper.Version);
    }

    [Fact]
    public void AVersionThatIsNotThreeNumbersIsNotAValidIdentity()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // A line that wears the prefix but is not a version is an identity that was never answered, so
        // it is the unanswered question - not the answered-but-wrong one the version mismatch reports.
        probe.ByPath[paths.Wrapper] = RuntimeVersionProbeResult.Ran(0, "anaglyfin-wrapper 1.2");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Warning, status.OverallStatus);
        var wrapper = Bin(status, RuntimeStatusService.WrapperKey);
        Assert.Equal(RuntimeItemState.ProbeFailed, wrapper.State);
        Assert.Null(wrapper.Version);
        Assert.Contains("did not answer --anaglyfin-version", wrapper.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ffmpeg version n8.1.3-mvc11-jf2 Copyright (c) 2000-2026 the FFmpeg developers", "n8.1.3-mvc11-jf2")]
    [InlineData("ffmpeg version 8.1.3-2 Copyright (c) 2000-2026 the FFmpeg developers", "8.1.3-2")]
    [InlineData("ffmpeg version 8.1.3 Copyright (c) 2000-2026 the FFmpeg developers", "8.1.3")]
    [InlineData("ffprobe version 8.1.3-2 Copyright (c) 2007-2026 the FFmpeg developers", "8.1.3-2")]
    [InlineData("ffmpeg version n8.1.3-mvc11-jf2", "n8.1.3-mvc11-jf2")]
    [InlineData("   ffmpeg   version   n8.1.3-mvc11-jf2   Copyright", "n8.1.3-mvc11-jf2")]
    [InlineData("FFmpeg Version N-115422-g8ba1b3ba1b Copyright", "N-115422-g8ba1b3ba1b")]
    [InlineData("ffmpeg version unknown Copyright (c) the FFmpeg developers", null)]
    [InlineData("ffmpeg version development (based on 8.1.3) Copyright", "8.1.3")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AFfmpegRowShowsTheWholeVersionTokenItsOwnBannerCarries(string? banner, string? expected)
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        // The FFmpeg rows are where an administrator reads which build is actually installed, so the
        // whitespace-delimited token after "version" is shown as the binary wrote it: the leading "n"
        // and the "-mvc11-jf2" build suffix included, not the numeric prefix of it.
        probe.Default = RuntimeVersionProbeResult.Ran(0, banner);

        var status = Status(Create(harness, probe));

        var mvc = Bin(status, RuntimeStatusService.FfmpegMvcKey);
        Assert.Equal(RuntimeItemState.Ok, mvc.State);
        Assert.Equal(expected, mvc.Version);
    }

    [Fact]
    public void TheOfficialFfmpegRowKeepsItsDistributorBuildSuffix()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // The server's own FFmpeg answers with the build tag its distributor gave it, and that tag is the
        // compatibility pointer the deployment is checked against.
        probe.ByPath[paths.Official] =
            RuntimeVersionProbeResult.Ran(0, "ffmpeg version 8.1.3-2 Copyright (c) 2000-2026 the FFmpeg developers");

        var status = Status(Create(harness, probe));

        var official = Bin(status, RuntimeStatusService.OfficialFfmpegKey);
        Assert.Equal(RuntimeItemState.Ok, official.State);
        Assert.Equal("8.1.3-2", official.Version);
    }

    [Fact]
    public void TheFfprobeSidecarRowShowsTheVersionTokenOfItsOwnBanner()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // The sidecar is a separate binary and answers with its own banner, which opens with ffprobe.
        probe.ByPath[paths.Ffprobe] =
            RuntimeVersionProbeResult.Ran(0, "ffprobe version n8.1.3-mvc11-jf2 Copyright (c) 2007-2026 the FFmpeg developers");

        var status = Status(Create(harness, probe));

        var sidecar = Bin(status, RuntimeStatusService.FfprobeKey);
        Assert.Equal(RuntimeItemState.Ok, sidecar.State);
        Assert.Equal("n8.1.3-mvc11-jf2", sidecar.Version);
    }

    [Fact]
    public void TheWrapperIdentityStillRefusesABannerStyleVersionToken()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // The whole-token rule belongs to the FFmpeg banner probe alone. --anaglyfin-version is answered
        // by the exact "anaglyfin-wrapper x.y.z" line or not at all, so an identity dressed in a build
        // tag is still the unanswered question, not a version the page may show.
        probe.ByPath[paths.Wrapper] = RuntimeVersionProbeResult.Ran(0, "anaglyfin-wrapper n8.1.3-mvc11-jf2");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Warning, status.OverallStatus);
        var wrapper = Bin(status, RuntimeStatusService.WrapperKey);
        Assert.Equal(RuntimeItemState.ProbeFailed, wrapper.State);
        Assert.Null(wrapper.Version);
        Assert.Contains("did not answer --anaglyfin-version", wrapper.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFfmpegMvcIsAnErrorAndIsNeverProbed()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        var missing = harness.PathOf("bin", "not-here");
        harness.Env[FFmpegWrapperOptions.RealFFmpegEnvironmentVariable] = missing;

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        Assert.Equal(RuntimeItemState.Missing, Bin(status, RuntimeStatusService.FfmpegMvcKey).State);
        Assert.DoesNotContain(probe.Calls, call => call.ExecutablePath == missing);
    }

    [Fact]
    public void ARequiredBinaryNamedByABareNameThatWillNotStartIsMissing()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        // A bare name cannot be pre-checked; when the operating system cannot start it, that is the
        // answer, and for a required binary it is a missing one.
        harness.Env[FFmpegWrapperOptions.RealFFmpegEnvironmentVariable] = "ffmpeg-mvc-missing";
        probe.ByPath["ffmpeg-mvc-missing"] = RuntimeVersionProbeResult.Failed("Win32Exception");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        Assert.Equal(RuntimeItemState.Missing, Bin(status, RuntimeStatusService.FfmpegMvcKey).State);
    }

    [Fact]
    public void TheOfficialFfmpegIsRecommendedAndItsAbsenceIsSingleBinaryMode()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);
        harness.Env[FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable] = null;

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Warning, status.OverallStatus);
        var official = Bin(status, RuntimeStatusService.OfficialFfmpegKey);
        Assert.Equal(RuntimeItemState.Missing, official.State);
        Assert.False(official.Required);
        Assert.Contains("single-binary", official.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnOfficialFfmpegThatIsTheFfmpegMvcFileIsAWarning()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // The deployment pointed the server's own FFmpeg at the very FFmpeg-mvc build.
        harness.Env[FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable] = paths.FfmpegMvc;

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Warning, status.OverallStatus);
        Assert.Equal(RuntimeItemState.Warning, Bin(status, RuntimeStatusService.OfficialFfmpegKey).State);
        Assert.Contains("capability", Bin(status, RuntimeStatusService.OfficialFfmpegKey).Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSidecarIsDerivedFromTheWrapperDirectory()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        // The wrapper may be called anything; the sidecar is always the sibling named ffprobe.
        var wrapper = harness.WriteExecutable("bin", "ffmpeg-mvc-custom");
        harness.Env[RuntimeStatusService.JellyfinFFmpegEnvironmentVariable] = wrapper;
        harness.WriteExecutable("bin", "ffprobe");
        probe.ByPath[wrapper] = RuntimeVersionProbeResult.Ran(0, "anaglyfin-wrapper 1.2.3");

        var status = Status(Create(harness, probe));

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(wrapper)!, "ffprobe"),
            Bin(status, RuntimeStatusService.FfprobeKey).ResolvedPath);
    }

    [Fact]
    public void TheSidecarIsNeverSearchedForOnPath()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        // A real ffprobe on PATH, and a wrapper in a directory that has none.
        var onPath = harness.WriteExecutable(Path.Combine(harness.EmptyPath, "ffprobe"));
        var wrapper = harness.WriteExecutable("elsewhere", "ffmpeg");
        harness.Env[RuntimeStatusService.JellyfinFFmpegEnvironmentVariable] = wrapper;
        probe.ByPath[wrapper] = RuntimeVersionProbeResult.Ran(0, "anaglyfin-wrapper 1.2.3");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        Assert.Equal(RuntimeItemState.Missing, Bin(status, RuntimeStatusService.FfprobeKey).State);
        Assert.DoesNotContain(probe.Calls, call => call.ExecutablePath == onPath);
    }

    [Theory]
    [InlineData("/opt/jellyfin/ffmpeg", false, "/opt/jellyfin/ffprobe")]
    [InlineData("/opt/jellyfin/ffmpeg.exe", false, "/opt/jellyfin/ffprobe")]
    [InlineData("/opt/jellyfin/ffmpeg.exe", true, "/opt/jellyfin/ffprobe.exe")]
    [InlineData("/opt/jellyfin/ffmpeg", true, "/opt/jellyfin/ffprobe")]
    [InlineData(null, false, null)]
    [InlineData("   ", true, null)]
    [InlineData("ffmpeg", true, null)]
    public void TheSidecarDerivationPreservesTheWrapperExtensionOnlyWhereItMeansSomething(
        string? wrapperPath,
        bool preserveWrapperExtension,
        string? expected)
    {
        var derived = RuntimeStatusService.DeriveFFprobeSidecarPath(wrapperPath, preserveWrapperExtension);

        if (expected is null)
        {
            Assert.Null(derived);
        }
        else
        {
            Assert.Equal(expected, derived);
        }
    }

    [Fact]
    public void ASidecarThatIsTheFfmpegMvcBinaryIsAnError()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);

        // A deployment that put FFmpeg-mvc in the wrapper's own directory under the name ffprobe.
        harness.WriteExecutable("bin", "ffprobe");
        harness.Env[FFmpegWrapperOptions.RealFFmpegEnvironmentVariable] = paths.Ffprobe;

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        var sidecar = Bin(status, RuntimeStatusService.FfprobeKey);
        Assert.Equal(RuntimeItemState.ProbeFailed, sidecar.State);
        Assert.True(sidecar.Required);
    }

    [Fact]
    public void AMissingLockDirectoryIsAnError()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);
        harness.Env[TranscodeSlotStore.LockDirectoryEnvironmentVariable] = harness.PathOf("no-locks");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        Assert.Equal(
            RuntimeItemState.Missing,
            Env(status, TranscodeSlotStore.LockDirectoryEnvironmentVariable).State);
    }

    [Fact]
    public void ALockDirectoryTheServiceCannotWriteIsAnError()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);
        harness.Env[TranscodeSlotStore.LockDirectoryEnvironmentVariable] = harness.MakeReadOnlyDirectory("read-only-locks");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        Assert.Equal(
            RuntimeItemState.NotWritable,
            Env(status, TranscodeSlotStore.LockDirectoryEnvironmentVariable).State);
    }

    [Fact]
    public void AMissingSettingsDocumentIsAWarningAboutFlatDepth()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        // No variable, and no document at the default location either.
        harness.Env[WrapperSettingsFile.EnvironmentVariable] = null;

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Warning, status.OverallStatus);
        var settings = Env(status, WrapperSettingsFile.EnvironmentVariable);
        Assert.Equal(RuntimeItemState.Warning, settings.State);
        Assert.False(settings.IsSet);
    }

    [Fact]
    public void ASettingsDirectoryTheServiceCannotWriteIsAnError()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        var readOnly = harness.MakeReadOnlyDirectory("locked-config");
        harness.Env[WrapperSettingsFile.EnvironmentVariable] = Path.Combine(readOnly, "wrapper-settings.json");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Error, status.OverallStatus);
        Assert.Equal(
            RuntimeItemState.NotWritable,
            Env(status, WrapperSettingsFile.EnvironmentVariable).State);
    }

    [Fact]
    public void TheAliasForTheRealFfmpegNamesItsSourceVariable()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        var mvcAlias = harness.WriteExecutable("bin", "ffmpeg-mvc-alias");
        harness.Env[FFmpegWrapperOptions.RealFFmpegEnvironmentVariable] = null;
        harness.Env[FFmpegWrapperOptions.RealFFmpegAlternateEnvironmentVariable] = mvcAlias;

        var status = Status(Create(harness, probe));

        var real = Env(status, FFmpegWrapperOptions.RealFFmpegEnvironmentVariable);
        Assert.True(real.IsSet);
        Assert.Equal(mvcAlias, real.Value);
        Assert.Equal(FFmpegWrapperOptions.RealFFmpegAlternateEnvironmentVariable, real.SourceVariable);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.FfmpegMvcKey).State);
    }

    [Fact]
    public void TheAliasForTheOfficialFfmpegNamesItsSourceVariable()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        var officialAlias = harness.WriteExecutable("bin", "ffmpeg-official-alias");
        harness.Env[FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable] = null;
        harness.Env[FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable] = officialAlias;

        var status = Status(Create(harness, probe));

        var server = Env(status, FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable);
        Assert.True(server.IsSet);
        Assert.Equal(FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable, server.SourceVariable);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.OfficialFfmpegKey).State);
    }

    [Fact]
    public void TheFfmpegMvcPathSourceNamesPathAsItsSource()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        var pathFfmpeg = harness.WriteExecutable(Path.Combine(harness.EmptyPath, "ffmpeg"));
        harness.Env[FFmpegWrapperOptions.RealFFmpegEnvironmentVariable] = null;
        harness.Env[FFmpegWrapperOptions.RealFFmpegAlternateEnvironmentVariable] = null;

        var status = Status(Create(harness, probe));

        var real = Env(status, FFmpegWrapperOptions.RealFFmpegEnvironmentVariable);
        Assert.False(real.IsSet);
        Assert.Null(real.Value);
        Assert.Equal(pathFfmpeg, real.ResolvedPath);
        Assert.Equal("PATH", real.SourceVariable);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.FfmpegMvcKey).State);
    }

    [Fact]
    public void TheOfficialFfmpegBareConfiguredValueKeepsItsVariableAndMentionsPath()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        harness.Env[FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable] = "ffmpeg";
        harness.Env[FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable] = null;

        var status = Status(Create(harness, probe));

        var server = Env(status, FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable);
        Assert.True(server.IsSet);
        Assert.Equal("ffmpeg", server.Value);
        Assert.Equal("ffmpeg", server.ResolvedPath);
        Assert.Null(server.SourceVariable);
        Assert.Contains("PATH", server.Detail, StringComparison.Ordinal);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.OfficialFfmpegKey).State);
    }

    [Fact]
    public void TheFfmpegMvcBareConfiguredValueKeepsItsVariableAndMentionsPath()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        harness.Env[FFmpegWrapperOptions.RealFFmpegEnvironmentVariable] = "ffmpeg";
        harness.Env[FFmpegWrapperOptions.RealFFmpegAlternateEnvironmentVariable] = null;

        var status = Status(Create(harness, probe));

        var real = Env(status, FFmpegWrapperOptions.RealFFmpegEnvironmentVariable);
        Assert.True(real.IsSet);
        Assert.Equal("ffmpeg", real.Value);
        Assert.Equal("ffmpeg", real.ResolvedPath);
        Assert.Null(real.SourceVariable);
        Assert.Contains("PATH", real.Detail, StringComparison.Ordinal);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.FfmpegMvcKey).State);
    }

    [Fact]
    public void TheOfficialFfmpegBareAliasKeepsItsAliasAndMentionsPath()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        harness.Env[FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable] = null;
        harness.Env[FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable] = "ffmpeg";

        var status = Status(Create(harness, probe));

        var server = Env(status, FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable);
        Assert.True(server.IsSet);
        Assert.Equal("ffmpeg", server.Value);
        Assert.Equal(FFmpegWrapperOptions.ServerFFmpegAlternateEnvironmentVariable, server.SourceVariable);
        Assert.Contains("PATH", server.Detail, StringComparison.Ordinal);
        Assert.Equal(RuntimeItemState.Ok, Bin(status, RuntimeStatusService.OfficialFfmpegKey).State);
    }

    [Fact]
    public void AWrapperThatTimesOutIsAWarningNotAnError()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        var paths = SetupHealthy(harness, probe);
        probe.ByPath[paths.Wrapper] = RuntimeVersionProbeResult.TimedOutResult();

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Warning, status.OverallStatus);
        Assert.Equal(RuntimeItemState.ProbeFailed, Bin(status, RuntimeStatusService.WrapperKey).State);
    }

    [Fact]
    public void ASidecarProbeThatCannotStartIsAWarningNotAnError()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        // The derived sibling exists and is executable, but the probe itself could not run it.
        var ffprobe = Path.Combine(Path.GetDirectoryName(EnvPath(harness, RuntimeStatusService.JellyfinFFmpegEnvironmentVariable))!, "ffprobe");
        probe.ByPath[ffprobe] = RuntimeVersionProbeResult.Failed("Win32Exception");

        var status = Status(Create(harness, probe));

        Assert.Equal(RuntimeStatusLevel.Warning, status.OverallStatus);
        Assert.Equal(RuntimeItemState.ProbeFailed, Bin(status, RuntimeStatusService.FfprobeKey).State);
    }

    [Fact]
    public void AProbeThatHangsIsBoundedByTheProbeTimeout()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        var status = Status(Create(harness, probe));

        // Every probe the service runs is given the service's bounded timeout, never an open-ended one.
        Assert.All(probe.Calls, call => Assert.Equal(TimeSpan.FromSeconds(RuntimeStatusService.DefaultProbeTimeoutSeconds), call.Timeout));
        Assert.NotEmpty(status.Binaries);
    }

    [Fact]
    public void AProbeVectorNeverCarriesAMediaPathOrAMarker()
    {
        var harness = new RuntimeStatusHarness();
        var probe = new FakeRuntimeVersionProbe();
        SetupHealthy(harness, probe);

        Status(Create(harness, probe));

        Assert.All(
            probe.Calls,
            call =>
            {
                Assert.All(call.Arguments, argument => Assert.DoesNotContain("http", argument, StringComparison.OrdinalIgnoreCase));
                Assert.All(call.Arguments, argument => Assert.DoesNotContain(".mkv", argument, StringComparison.OrdinalIgnoreCase));
                Assert.All(
                    call.Arguments,
                    argument => Assert.Contains(argument, new[] { "--anaglyfin-version", "-version" }, StringComparer.Ordinal));
            });
    }

    private static RuntimeStatusService Create(RuntimeStatusHarness harness, FakeRuntimeVersionProbe probe)
        => new(probe, harness.ReadVariable, harness.Root, PluginVersion);

    private static RuntimeStatusResponse Status(RuntimeStatusService service)
        => service.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();

    private static RuntimeBinaryStatus Bin(RuntimeStatusResponse status, string key)
        => status.Binaries.Single(binary => binary.Key == key);

    private static RuntimeEnvironmentStatus Env(RuntimeStatusResponse status, string name)
        => status.Environment.Single(entry => entry.Name == name);

    private static string EnvPath(RuntimeStatusHarness harness, string name)
        => harness.Env[name]!;

    private sealed record HealthyPaths(
        string Wrapper,
        string FfmpegMvc,
        string Official,
        string Ffprobe,
        string Locks,
        string Settings);

    private static HealthyPaths SetupHealthy(RuntimeStatusHarness harness, FakeRuntimeVersionProbe probe)
    {
        var wrapper = harness.WriteExecutable("bin", "ffmpeg");
        var ffmpegMvc = harness.WriteExecutable("bin", "ffmpeg-mvc");
        var official = harness.WriteExecutable("bin", "ffmpeg-official");
        var ffprobe = harness.WriteExecutable("bin", "ffprobe");
        var locks = harness.EnsureDirectory("locks");
        var settings = harness.WriteFile("settings", "anaglyfin-wrapper-settings.json");

        harness.Env[RuntimeStatusService.JellyfinFFmpegEnvironmentVariable] = wrapper;
        harness.Env[FFmpegWrapperOptions.RealFFmpegEnvironmentVariable] = ffmpegMvc;
        harness.Env[FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable] = official;
        harness.Env[TranscodeSlotStore.LockDirectoryEnvironmentVariable] = locks;
        harness.Env[WrapperSettingsFile.EnvironmentVariable] = settings;

        probe.Default = RuntimeVersionProbeResult.Ran(0, "ffmpeg version 6.1.1-mvc11-jf2 Copyright (c) the FFmpeg developers");
        probe.ByPath[wrapper] = RuntimeVersionProbeResult.Ran(0, "anaglyfin-wrapper " + PluginVersion);

        return new HealthyPaths(wrapper, ffmpegMvc, official, ffprobe, locks, settings);
    }
}
