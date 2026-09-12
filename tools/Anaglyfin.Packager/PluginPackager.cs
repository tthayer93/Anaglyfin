using System;
using System.Collections.Generic;
using System.IO;

namespace Anaglyfin.Packager;

/// <summary>What one packaging run is asked to produce.</summary>
public sealed class PackageRequest
{
    /// <summary>Gets the built plugin assembly to package.</summary>
    public required string PluginDllPath { get; init; }

    /// <summary>Gets the hand-maintained identity manifest to package it under.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>Gets the directory the artifacts are written to.</summary>
    public required string ArtifactDirectory { get; init; }

    /// <summary>
    /// Gets the published wrapper executable to stage beside the plugin, or
    /// <see langword="null"/> when the run packages the plugin alone.
    /// </summary>
    public string? WrapperInputPath { get; init; }

    /// <summary>Gets the file name the wrapper is staged under.</summary>
    public string WrapperFileName { get; init; } = WrapperArtifact.DefaultFileName;

    /// <summary>Gets the runtime the staged wrapper was published for.</summary>
    public string WrapperRuntime { get; init; } = WrapperArtifact.DefaultRuntime;
}

/// <summary>What one verification run is asked to check.</summary>
public sealed class VerificationRequest
{
    /// <summary>Gets the manifest the artifacts are measured against.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>Gets the directory that holds the artifacts.</summary>
    public required string ArtifactDirectory { get; init; }

    /// <summary>
    /// Gets the wrapper file the package is required to carry, or <see langword="null"/> to
    /// check only the wrapper the metadata itself records.
    /// </summary>
    public string? WrapperFileName { get; init; }
}

/// <summary>The artifacts one packaging run left in the artifact directory.</summary>
/// <param name="ArtifactDirectory">The directory they were written to.</param>
/// <param name="ArchivePath">The plugin archive.</param>
/// <param name="MetadataPath">The install record written beside it.</param>
/// <param name="WrapperPath">The staged wrapper, when the run staged one.</param>
public sealed record PackagedPackage(
    string ArtifactDirectory,
    string ArchivePath,
    string MetadataPath,
    string? WrapperPath);

/// <summary>
/// The packaging job: build output in, installable artifacts out, refusal out otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Both operations here exist because a manual install is unrepeatable evidence. A user
/// extracts an archive on a server somewhere, and the only thing that survives the attempt is
/// what the artifacts claim about themselves, so the job states those claims in one place
/// (<c>meta.json</c>) and then checks them the way a consumer would.
/// </para>
/// <para>
/// <see cref="Pack"/> therefore re-reads what it wrote, and <see cref="Verify"/> needs nothing
/// from the build beyond the manifest: it opens the archive, hashes the files, and compares
/// them with the record. Running <see cref="Verify"/> on its own is the CI gate, which is why
/// it never takes the DLL it is checking an archive of.
/// </para>
/// </remarks>
public static class PluginPackager
{
    /// <summary>How the manifest inside the assembly is referred to when it disagrees.</summary>
    public const string EmbeddedManifestSubject = "the manifest embedded in the plugin assembly";

    /// <summary>How the generated document is referred to when it disagrees.</summary>
    public const string MetadataSubject = "the generated meta.json";

    /// <summary>
    /// Builds the artifacts.
    /// </summary>
    /// <param name="request">What to package, and where.</param>
    /// <returns>The paths written.</returns>
    /// <exception cref="PackagingError">
    /// The assembly is missing or is not the plugin the manifest describes, the archive would
    /// not install, or the artifacts would not survive a check.
    /// </exception>
    public static PackagedPackage Pack(PackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var manifest = PluginManifest.Load(request.ManifestPath);
        var probe = PluginAssemblyProbe.Load(request.PluginDllPath);

        Require(
            string.Equals(probe.SimpleName, manifest.Name, StringComparison.Ordinal),
            $"the built assembly calls itself '{probe.SimpleName}' while the manifest declares <name>{manifest.Name}."
            + " A plugin is installed by assembly name, so there is nothing to package until they agree.");

        Require(
            string.Equals(FileNameOf(request.PluginDllPath), manifest.AssemblyFileName, StringComparison.Ordinal),
            $"the plugin assembly is filed as '{FileNameOf(request.PluginDllPath)}', but the server loads it as"
            + $" '{manifest.AssemblyFileName}'.");

        RequireVersionMatchesManifest(manifest, probe);
        RequireEmbeddedManifestMatches(manifest, probe);

        Directory.CreateDirectory(request.ArtifactDirectory);

        var archivePath = Path.Combine(request.ArtifactDirectory, manifest.ZipFileName);

        PluginArchive.Create(archivePath, request.PluginDllPath, manifest.AssemblyFileName);
        RequireArchiveCarriesThePackedAssembly(archivePath, request.PluginDllPath, manifest);

        WrapperArtifact? wrapper = null;

        if (!string.IsNullOrWhiteSpace(request.WrapperInputPath))
        {
            wrapper = WrapperArtifact.Stage(
                request.WrapperInputPath!,
                request.ArtifactDirectory,
                request.WrapperFileName,
                request.WrapperRuntime);
        }

        var metadata = new PackageMetadata(
            manifest,
            PluginArtifactRecord.Measure(request.ArtifactDirectory, manifest),
            wrapper is null ? null : WrapperArtifactRecord.Of(wrapper));

        var metadataPath = metadata.Write(request.ArtifactDirectory);

        // What the job remembers writing is not what a server will find. Read the artifacts back
        // off the disk before calling the run a success.
        Verify(new VerificationRequest
        {
            ManifestPath = request.ManifestPath,
            ArtifactDirectory = request.ArtifactDirectory,
            WrapperFileName = wrapper?.FileName,
        });

        return new PackagedPackage(
            request.ArtifactDirectory,
            archivePath,
            metadataPath,
            wrapper is null ? null : Path.Combine(request.ArtifactDirectory, wrapper.FileName));
    }

    /// <summary>
    /// Checks a directory of artifacts against the manifest it was packaged from.
    /// </summary>
    /// <param name="request">What to check, and against which manifest.</param>
    /// <exception cref="PackagingError">
    /// A file is missing, the archive is not laid out as a plugin, or any recorded value fails
    /// to match the manifest or the file it records.
    /// </exception>
    public static void Verify(VerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var manifest = PluginManifest.Load(request.ManifestPath);
        var metadata = PackageMetadata.Read(request.ArtifactDirectory);

        RequireAgreement(metadata.Manifest.IdentityDifferences(manifest, MetadataSubject), MetadataSubject);

        var archivePath = Path.Combine(request.ArtifactDirectory, manifest.ZipFileName);

        PluginArchive.RequirePluginLayout(archivePath, manifest.AssemblyFileName);

        // Measured here, and compared with what the record claims.
        var measuredChecksum = Digest.Sha256OfFile(archivePath);
        var measuredSize = Digest.SizeOfFile(archivePath);

        Require(
            string.Equals(metadata.Plugin.Checksum, measuredChecksum, StringComparison.Ordinal),
            $"{MetadataSubject} records '{manifest.ZipFileName}' as checksum {metadata.Plugin.Checksum},"
            + $" but the archive on disk hashes to {measuredChecksum}.");

        Require(
            metadata.Plugin.Size == measuredSize,
            $"{MetadataSubject} records '{manifest.ZipFileName}' as {metadata.Plugin.Size} bytes,"
            + $" but the archive on disk is {measuredSize} bytes.");

        RequireRecordedWrapper(
            request.ArtifactDirectory,
            metadata,
            request.WrapperFileName);
    }

    private static void RequireVersionMatchesManifest(PluginManifest manifest, PluginAssemblyProbe probe)
    {
        var built = probe.Version;

        if (built is null)
        {
            throw new PackagingError(
                $"'{probe.AssemblyPath}' declares no assembly version, while the manifest declares '{manifest.Version}'.");
        }

        var declared = Version.Parse(manifest.Version);

        Require(
            SameVersion(declared, built),
            $"the manifest declares version '{manifest.Version}' but the built assembly is version '{built}'."
            + " Rebuild before packaging, or fix the manifest.");
    }

    private static void RequireEmbeddedManifestMatches(PluginManifest manifest, PluginAssemblyProbe probe)
    {
        var embedded = probe.EmbeddedManifest
            ?? throw new PackagingError(
                $"'{probe.AssemblyPath}' carries no identity manifest. The plugin project embeds Plugin.manifest.xml,"
                + " so this assembly was not built from this source tree.");

        RequireAgreement(embedded.IdentityDifferences(manifest, EmbeddedManifestSubject), EmbeddedManifestSubject);
    }

    private static void RequireArchiveCarriesThePackedAssembly(string archivePath, string assemblyPath, PluginManifest manifest)
    {
        var packed = PluginArchive.ReadEntry(archivePath, manifest.AssemblyFileName);

        Require(
            string.Equals(
                Digest.Sha256Of(packed),
                Digest.Sha256OfFile(assemblyPath),
                StringComparison.Ordinal),
            $"the archive '{archivePath}' does not hold the assembly '{assemblyPath}' it was packed from.");
    }

    private static void RequireRecordedWrapper(string artifactDirectory, PackageMetadata metadata, string? requiredFileName)
    {
        var recorded = metadata.Wrapper;

        if (requiredFileName is not null)
        {
            Require(
                recorded is not null && string.Equals(recorded.FileName, requiredFileName, StringComparison.Ordinal),
                $"the package was expected to carry the wrapper '{requiredFileName}' and {MetadataSubject} records"
                + (recorded is null ? " no wrapper at all." : $" '{recorded.FileName}' instead."));
        }

        if (recorded is null)
        {
            return;
        }

        // The file is measured, not trusted: the record says what should be there and the ELF
        // header says what is.
        var measured = WrapperArtifact.Measure(artifactDirectory, recorded.FileName, recorded.Runtime);

        Require(
            string.Equals(recorded.Checksum, measured.Checksum, StringComparison.Ordinal) && recorded.Size == measured.Size,
            $"{MetadataSubject} records the wrapper '{recorded.FileName}' as {recorded.Size} bytes with checksum"
            + $" {recorded.Checksum}, but the file on disk is {measured.Size} bytes with checksum {measured.Checksum}.");
    }

    private static void RequireAgreement(IReadOnlyList<string> differences, string subject)
    {
        if (differences.Count == 0)
        {
            return;
        }

        var details = string.Join("," + Environment.NewLine + "  - ", differences);

        throw new PackagingError($"the artifacts disagree with the manifest:{Environment.NewLine}  - {details} ({subject})");
    }

    private static bool SameVersion(Version left, Version right) =>
        Component(left.Major) == Component(right.Major)
        && Component(left.Minor) == Component(right.Minor)
        && Component(left.Build) == Component(right.Build)
        && Component(left.Revision) == Component(right.Revision);

    private static int Component(int component) => component < 0 ? 0 : component;

    private static string FileNameOf(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new PackagingError(message);
        }
    }
}
