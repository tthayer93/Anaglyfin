using System;
using System.IO;
using System.Security.Cryptography;
using Anaglyfin.Packager;
using Xunit;

namespace Anaglyfin.Tests.Packaging;

/// <summary>
/// The wrapper artifact: the one file in the package that a server starts directly, and the
/// checks that keep it to be an executable for the server's own architecture.
/// </summary>
/// <remarks>
/// The official Jellyfin container gets no package manager at runtime, so what the wrapper has
/// to be is a single file that runs on linux-x86-64 with nothing installed beside it. The job
/// cannot start it - there is no FFmpeg-mvc to hand it and no playback to serve - so what it
/// asserts is what the binary declares about itself, read from the ELF header, plus the size and
/// checksum the install record will carry.
/// </remarks>
public sealed class WrapperArtifactTests : IDisposable
{
    private readonly TemporaryPackageDirectory _published = new("published");
    private readonly TemporaryPackageDirectory _artifacts = new("wrapper-artifacts");

    [Fact]
    public void ThePublishedBinaryIsStagedUnderTheInstallNameAndMeasured()
    {
        var published = FakeLinuxExecutable.WriteInto(_published);

        var staged = WrapperArtifact.Stage(
            published,
            _artifacts.Location,
            WrapperArtifact.DefaultFileName,
            WrapperArtifact.DefaultRuntime);

        var path = _artifacts.LocationOf(WrapperArtifact.DefaultFileName);

        Assert.Equal(WrapperArtifact.DefaultFileName, staged.FileName);
        Assert.Equal(WrapperArtifact.DefaultRuntime, staged.Runtime);
        Assert.Equal(new FileInfo(published).Length, staged.Size);
        Assert.Equal(Sha256(File.ReadAllBytes(published)), staged.Checksum);
        Assert.Equal(File.ReadAllBytes(published), File.ReadAllBytes(path));
    }

    [Fact]
    public void TheStagedCopyIsOneTheServerCanStart()
    {
        var published = FakeLinuxExecutable.WriteInto(_published);

        WrapperArtifact.Stage(published, _artifacts.Location, WrapperArtifact.DefaultFileName, WrapperArtifact.DefaultRuntime);

        var path = _artifacts.LocationOf(WrapperArtifact.DefaultFileName);

        // The mode bit is the deployment fact on the platforms the wrapper runs on; the job is
        // expected to leave the binary executable for whoever the server runs as.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(path);

            Assert.True(mode.HasFlag(UnixFileMode.UserExecute), $"the staged wrapper is not owner-executable ({mode}).");
            Assert.True(mode.HasFlag(UnixFileMode.GroupExecute), $"the staged wrapper is not group-executable ({mode}).");
            Assert.True(mode.HasFlag(UnixFileMode.OtherExecute), $"the staged wrapper is not world-executable ({mode}).");
        }
        else
        {
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public void ASharedObjectIsAcceptedAndAnythingElseIsNot()
    {
        // A position-independent executable and a PIE apphost are both type 3; a relocatable
        // object (type 1) is not something the server can start.
        var sharedObject = _published.LocationOf("apphost");

        File.WriteAllBytes(sharedObject, FakeLinuxExecutable.Bytes(fileType: 3));

        WrapperArtifact.Stage(sharedObject, _artifacts.Location, "apphost", WrapperArtifact.DefaultRuntime);

        var objectFile = _published.LocationOf("wrapper.o");

        File.WriteAllBytes(objectFile, FakeLinuxExecutable.Bytes(fileType: 1));

        var refusal = Assert.Throws<PackagingError>(
            () => WrapperArtifact.Stage(objectFile, _artifacts.Location, "wrapper.o", WrapperArtifact.DefaultRuntime));

        Assert.Contains("not an executable", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABinaryForAnotherArchitectureIsRefused()
    {
        const ushort machineArm64 = 0xB7;

        var arm64 = _published.LocationOf("anaglyfin-ffmpeg");

        File.WriteAllBytes(arm64, FakeLinuxExecutable.Bytes(machine: machineArm64));

        var refusal = Assert.Throws<PackagingError>(
            () => WrapperArtifact.Stage(arm64, _artifacts.Location, WrapperArtifact.DefaultFileName, "linux-arm64"));

        Assert.Contains("x86-64", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("0x00B7", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A32BitBinaryIsRefused()
    {
        var elf32 = _published.LocationOf("anaglyfin-ffmpeg");

        File.WriteAllBytes(elf32, FakeLinuxExecutable.Bytes(elfClass: 1));

        var refusal = Assert.Throws<PackagingError>(
            () => WrapperArtifact.Stage(elf32, _artifacts.Location, WrapperArtifact.DefaultFileName, WrapperArtifact.DefaultRuntime));

        Assert.Contains("64-bit", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManagedDllIsRefusedAsTheWrapper()
    {
        // The framework-dependent build output, which is exactly what is on hand after a normal
        // build and exactly what the server cannot start.
        var managedDll = _published.CopyIn(typeof(Plugin).Assembly.Location, "anaglyfin-ffmpeg");

        var refusal = Assert.Throws<PackagingError>(
            () => WrapperArtifact.Stage(
                managedDll,
                _artifacts.Location,
                WrapperArtifact.DefaultFileName,
                WrapperArtifact.DefaultRuntime));

        Assert.Contains("not an ELF executable", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileTooShortToBeAnExecutableIsRefused()
    {
        var truncated = _published.WriteText("anaglyfin-ffmpeg", "!");

        var refusal = Assert.Throws<PackagingError>(
            () => WrapperArtifact.Stage(
                truncated,
                _artifacts.Location,
                WrapperArtifact.DefaultFileName,
                WrapperArtifact.DefaultRuntime));

        Assert.Contains("too short", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameThatCarriesAPathIsRefused()
    {
        var published = FakeLinuxExecutable.WriteInto(_published);

        var refusal = Assert.Throws<PackagingError>(
            () => WrapperArtifact.Stage(published, _artifacts.Location, "ffmpeg/anaglyfin-ffmpeg", WrapperArtifact.DefaultRuntime));

        Assert.Contains("path", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MeasuringAStagedArtifactRepeatsItsOwnCheck()
    {
        var published = FakeLinuxExecutable.WriteInto(_published);

        WrapperArtifact.Stage(published, _artifacts.Location, WrapperArtifact.DefaultFileName, WrapperArtifact.DefaultRuntime);

        var measured = WrapperArtifact.Measure(
            _artifacts.Location,
            WrapperArtifact.DefaultFileName,
            WrapperArtifact.DefaultRuntime);

        Assert.Equal(new FileInfo(published).Length, measured.Size);

        // The record pointing at a file that has gone is a refusal, not a null.
        _artifacts.Delete(WrapperArtifact.DefaultFileName);

        var refusal = Assert.Throws<PackagingError>(
            () => WrapperArtifact.Measure(
                _artifacts.Location,
                WrapperArtifact.DefaultFileName,
                WrapperArtifact.DefaultRuntime));

        Assert.Contains("missing", refusal.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _published.Dispose();
        _artifacts.Dispose();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
