using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Anaglyfin.Packager;
using Xunit;

namespace Anaglyfin.Tests.Packaging;

/// <summary>
/// What the packaging job writes, and everything it refuses to write.
/// </summary>
/// <remarks>
/// <para>
/// The three things a manual install depends on are all asserted here: the archive holds the
/// plugin assembly where Jellyfin will look for it, <c>meta.json</c> says where that archive
/// came from and what it measures, and a package that would not install is refused with a non-
/// zero exit rather than shipped. The inputs are a real plugin build (see
/// <see cref="PluginBuildStandin"/>) because every refusal this job exists for is a
/// disagreement between a manifest and a build.
/// </para>
/// <para>
/// <see cref="PluginPackager.Verify"/> is exercised separately from <see cref="PluginPackager.Pack"/>
/// because that is how the CI gate runs it: with nothing but the manifest and the artifact
/// directory, so nothing the packaging run remembered can vouch for itself.
/// </para>
/// </remarks>
public sealed class PluginPackagerTests : IDisposable
{
    private readonly TemporaryPackageDirectory _build = new("build");
    private readonly TemporaryPackageDirectory _artifacts = new("artifacts");
    private readonly string _manifestPath;
    private readonly string _assemblyPath;

    public PluginPackagerTests()
    {
        _manifestPath = _build.WriteText("Plugin.manifest.xml", PluginBuildStandin.ManifestText());
        _assemblyPath = PluginBuildStandin.CopyAssemblyInto(_build);
    }

    [Fact]
    public void TheArchiveIsNamedFromTheManifestAndCarriesTheAssemblyAtItsRoot()
    {
        var packaged = PluginPackager.Pack(Request());

        Assert.Equal("Anaglyfin_0.1.0.zip", Path.GetFileName(packaged.ArchivePath));
        Assert.Equal(PackageMetadata.FileName, Path.GetFileName(packaged.MetadataPath));
        Assert.Null(packaged.WrapperPath);

        using var archive = ZipFile.OpenRead(packaged.ArchivePath);

        var entry = Assert.Single(archive.Entries);

        // A build folder inside the archive is where a plugin goes unfound by the server.
        Assert.Equal("Anaglyfin.dll", entry.FullName);

        using var entryStream = entry.Open();
        using var packed = new MemoryStream();

        entryStream.CopyTo(packed);

        Assert.Equal(
            Sha256(File.ReadAllBytes(_assemblyPath)),
            Sha256(packed.ToArray()));
    }

    [Fact]
    public void TheInstallRecordCarriesEveryManifestFieldAndTheMeasuredArchive()
    {
        var packaged = PluginPackager.Pack(Request());

        var manifest = PluginManifest.Load(_manifestPath);
        var recorded = PackageMetadata.Read(_artifacts.Location);

        Assert.Empty(recorded.Manifest.IdentityDifferences(manifest, "the record under test"));
        Assert.Equal(manifest.Version, recorded.Manifest.Version);
        Assert.Equal("Anaglyfin", recorded.Manifest.Name);
        Assert.Equal("12.0.0", recorded.Manifest.TargetAbi);
        Assert.Equal("net10.0", recorded.Manifest.Framework);
        Assert.Null(recorded.Wrapper);

        var zipBytes = File.ReadAllBytes(packaged.ArchivePath);

        Assert.Equal("Anaglyfin_0.1.0.zip", recorded.Plugin.ZipFileName);
        Assert.Equal("Anaglyfin.dll", recorded.Plugin.AssemblyFileName);
        Assert.Equal(Digest.Sha256Algorithm, recorded.Plugin.ChecksumType);
        Assert.Equal(Sha256(zipBytes), recorded.Plugin.Checksum);
        Assert.Equal(zipBytes.LongLength, recorded.Plugin.Size);

        using var document = JsonDocument.Parse(File.ReadAllText(packaged.MetadataPath));
        var package = document.RootElement.GetProperty("packages")[0];

        // The plugin repository shape asks for the same archive twice; both copies are written.
        Assert.Equal("Anaglyfin.dll", package.GetProperty("assemblyFileName").GetString());
        Assert.Equal(Sha256(zipBytes), package.GetProperty("checksum").GetString());
        Assert.Equal(zipBytes.LongLength, package.GetProperty("size").GetInt64());
        Assert.Equal(manifest.Version, package.GetProperty("version").GetString());
    }

    [Fact]
    public void TwoRunsOverTheSameBuildWriteTheSameBytes()
    {
        using var elsewhere = new TemporaryPackageDirectory("artifacts-again");

        var first = PluginPackager.Pack(Request());
        var second = PluginPackager.Pack(Request(artifactDirectory: elsewhere.Location));

        Assert.Equal(
            File.ReadAllBytes(first.ArchivePath),
            File.ReadAllBytes(second.ArchivePath));

        Assert.Equal(
            File.ReadAllText(first.MetadataPath),
            File.ReadAllText(second.MetadataPath));
    }

    [Fact]
    public void APublishedWrapperIsStagedBesideThePluginAndRecorded()
    {
        var wrapper = FakeLinuxExecutable.WriteInto(_build);

        var packaged = PluginPackager.Pack(Request(wrapperInputPath: wrapper));

        Assert.Equal("anaglyfin-ffmpeg", Path.GetFileName(packaged.WrapperPath));
        Assert.True(File.Exists(packaged.WrapperPath));
        Assert.Equal(File.ReadAllBytes(wrapper), File.ReadAllBytes(packaged.WrapperPath!));

        var recorded = PackageMetadata.Read(_artifacts.Location);

        Assert.NotNull(recorded.Wrapper);
        Assert.Equal("anaglyfin-ffmpeg", recorded.Wrapper!.FileName);
        Assert.Equal(WrapperArtifact.DefaultRuntime, recorded.Wrapper.Runtime);
        Assert.Equal(Digest.Sha256Algorithm, recorded.Wrapper.ChecksumType);
        Assert.Equal(new FileInfo(wrapper).Length, recorded.Wrapper.Size);

        // The gate asks for the wrapper by name; a package without one is a refusal.
        PluginPackager.Verify(VerifyRequest(wrapperFileName: WrapperArtifact.DefaultFileName));
    }

    [Fact]
    public void AWrapperThatIsNotALinuxExecutableIsRefused()
    {
        // The framework-dependent build output is the thing that tempts people: a managed DLL
        // with a launcher next it is not what the server starts.
        var notAnExecutable = _build.WriteText("Anaglyfin.FFmpegWrapper", "#!/bin/sh\nexit 0\n");

        var refusal = Assert.Throws<PackagingError>(
            () => PluginPackager.Pack(Request(wrapperInputPath: notAnExecutable)));

        Assert.Contains("ELF", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyRefusesAWrapperRecordNamedOutsideTheArtifactDirectory()
    {
        var wrapper = FakeLinuxExecutable.WriteInto(_build);

        PluginPackager.Pack(Request(wrapperInputPath: wrapper));

        // The record is input by the time a gate or an administrator hands it back, so a name
        // that walks out of the artifact directory is refused before it is ever combined.
        RewriteInstallRecord(
            "\"fileName\": \"anaglyfin-ffmpeg\"",
            "\"fileName\": \"../../../../etc/anaglyfin-ffmpeg\"");

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Verify(VerifyRequest()));

        Assert.Contains("contains a path", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyAcceptsTheArtifactsTheJobWrote()
    {
        PluginPackager.Pack(Request());

        PluginPackager.Verify(VerifyRequest());
    }

    [Fact]
    public void VerifyRefusesAMissingInstallRecord()
    {
        PluginPackager.Pack(Request());
        _artifacts.Delete(PackageMetadata.FileName);

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Verify(VerifyRequest()));

        Assert.Contains("meta.json", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyRefusesAMissingArchive()
    {
        PluginPackager.Pack(Request());
        _artifacts.Delete("Anaglyfin_0.1.0.zip");

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Verify(VerifyRequest()));

        Assert.Contains("missing", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyRefusesAnArchiveWhoseAssemblyIsNotAtTheRoot()
    {
        PluginPackager.Pack(Request());

        // What `dotnet publish -p` style output would leave behind: the assembly in a folder.
        File.Delete(_artifacts.LocationOf("Anaglyfin_0.1.0.zip"));

        using (var archive = ZipFile.Open(_artifacts.LocationOf("Anaglyfin_0.1.0.zip"), ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(_assemblyPath, "net10.0/Anaglyfin.dll");
        }

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Verify(VerifyRequest()));

        Assert.Contains("archive root", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyRefusesAnArchiveThatDoesNotMatchTheRecordedChecksum()
    {
        PluginPackager.Pack(Request());

        var recorded = PackageMetadata.Read(_artifacts.Location);

        RewriteInstallRecord(recorded.Plugin.Checksum, new string('0', recorded.Plugin.Checksum.Length));

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Verify(VerifyRequest()));

        Assert.Contains("checksum", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyRefusesAnArchiveThatDoesNotMatchTheRecordedSize()
    {
        PluginPackager.Pack(Request());

        var recorded = PackageMetadata.Read(_artifacts.Location);

        RewriteInstallRecord(
            "\"size\": " + recorded.Plugin.Size.ToString(CultureInfo.InvariantCulture),
            "\"size\": " + (recorded.Plugin.Size + 1).ToString(CultureInfo.InvariantCulture));

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Verify(VerifyRequest()));

        Assert.Contains("bytes", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyRefusesAnInstallRecordThatContradictsTheManifest()
    {
        PluginPackager.Pack(Request());

        RewriteInstallRecord("\"owner\": \"Anaglyfin\"", "\"owner\": \"Someone Else\"");

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Verify(VerifyRequest()));

        Assert.Contains("owner", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("manifest", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyRefusesAWrapperThePackageDoesNotCarry()
    {
        PluginPackager.Pack(Request());

        var refusal = Assert.Throws<PackagingError>(
            () => PluginPackager.Verify(VerifyRequest(wrapperFileName: WrapperArtifact.DefaultFileName)));

        Assert.Contains("wrapper", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingPluginAssemblyIsRefused()
    {
        var refusal = Assert.Throws<PackagingError>(
            () => PluginPackager.Pack(Request(assemblyPath: _build.LocationOf("Anaglyfin.dll.nowhere"))));

        Assert.Contains("was not found", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APluginAssemblyThatIsNotManagedCodeIsRefused()
    {
        var notAnAssembly = _build.CopyIn(_manifestPath, "Anaglyfin.dll");

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Pack(Request(assemblyPath: notAnAssembly)));

        Assert.Contains("managed", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestNamingAnotherAssemblyIsRefused()
    {
        var manifest = PluginBuildStandin.WriteManifestWith(_build, "name", "AnaglyfinOther");

        // The copy under test keeps the real assembly name, which is the disagreement.
        var assembly = PluginBuildStandin.CopyAssemblyInto(_build, "AnaglyfinOther.dll");

        var refusal = Assert.Throws<PackagingError>(
            () => PluginPackager.Pack(Request(manifestPath: manifest, assemblyPath: assembly)));

        Assert.Contains("AnaglyfinOther", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APluginAssemblyFiledUnderAnotherNameIsRefused()
    {
        var renamed = PluginBuildStandin.CopyAssemblyInto(_build, "anaglyfin-plugin.dll");

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Pack(Request(assemblyPath: renamed)));

        Assert.Contains("filed as", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildWhoseEmbeddedManifestDisagreesIsRefused()
    {
        // The file was edited and the build was not: the DLL on disk still describes the old
        // overview, and packaging it would publish an identity the assembly does not carry.
        var manifest = PluginBuildStandin.WriteManifestWith(_build, "overview", "Something the build never said.");

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Pack(Request(manifestPath: manifest)));

        Assert.Contains("embedded", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("overview", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestDeclaringAnOlderJellyfinIsRefused()
    {
        var manifest = PluginBuildStandin.WriteManifestWith(_build, "targetAbi", "10.11.0");

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Pack(Request(manifestPath: manifest)));

        Assert.Contains("older servers", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestDeclaringAnOlderFrameworkIsRefused()
    {
        var manifest = PluginBuildStandin.WriteManifestWith(_build, "framework", "net8.0");

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Pack(Request(manifestPath: manifest)));

        Assert.Contains("net8.0", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestWithoutAnIdentityIsRefused()
    {
        var manifest = PluginBuildStandin.WriteManifestWith(_build, "guid", string.Empty);

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Pack(Request(manifestPath: manifest)));

        Assert.Contains("<guid>", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestWhoseVersionCannotBeAFileNameIsRefused()
    {
        var manifest = PluginBuildStandin.WriteManifestWith(_build, "version", "0.1");

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Pack(Request(manifestPath: manifest)));

        Assert.Contains("dot-separated", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestWhoseNameCannotBeAFileNameIsRefused()
    {
        var manifest = PluginBuildStandin.WriteManifestWith(_build, "name", "Media/Anaglyfin");

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Pack(Request(manifestPath: manifest)));

        Assert.Contains("Anaglyfin", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestWithAnEmptyDescriptionIsRefused()
    {
        var manifest = PluginBuildStandin.WriteManifestWith(_build, "description", string.Empty);

        var refusal = Assert.Throws<PackagingError>(() => PluginPackager.Pack(Request(manifestPath: manifest)));

        Assert.Contains("description", refusal.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _build.Dispose();
        _artifacts.Dispose();
    }

    private PackageRequest Request(
        string? manifestPath = null,
        string? assemblyPath = null,
        string? artifactDirectory = null,
        string? wrapperInputPath = null) =>
        new()
        {
            PluginDllPath = assemblyPath ?? _assemblyPath,
            ManifestPath = manifestPath ?? _manifestPath,
            ArtifactDirectory = artifactDirectory ?? _artifacts.Location,
            WrapperInputPath = wrapperInputPath,
        };

    private VerificationRequest VerifyRequest(string? wrapperFileName = null) =>
        new()
        {
            ManifestPath = _manifestPath,
            ArtifactDirectory = _artifacts.Location,
            WrapperFileName = wrapperFileName,
        };

    /// <summary>
    /// Rewrites the install record as an administrator might edit it, or as a corrupted transfer
    /// would leave it. Both copies of a value are rewritten so the only thing wrong with the
    /// document is the value under test.
    /// </summary>
    private void RewriteInstallRecord(string oldValue, string newValue)
    {
        var path = _artifacts.LocationOf(PackageMetadata.FileName);
        var text = File.ReadAllText(path);

        Assert.Contains(oldValue, text, StringComparison.Ordinal);

        File.WriteAllText(path, text.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
