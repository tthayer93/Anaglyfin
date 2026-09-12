using System;

namespace Anaglyfin.Packager;

/// <summary>
/// What <c>meta.json</c> records about the plugin archive.
/// </summary>
/// <param name="ZipFileName">The archive name, derived from the identity.</param>
/// <param name="AssemblyFileName">The assembly the archive carries at its root.</param>
/// <param name="ChecksumType">The checksum algorithm the checksum was produced with.</param>
/// <param name="Checksum">The lowercase hexadecimal checksum of the archive file.</param>
/// <param name="Size">The archive size in bytes.</param>
/// <remarks>
/// These are the values a server or a plugin repository re-checks before it unpacks anything,
/// so they are measured from the finished file in the artifact directory. The entry the
/// repository format expects (<c>packages[0]</c>) and the flat fields of the document are the
/// same measurement, and are cross-checked against each other when the document is read.
/// </remarks>
public sealed record PluginArtifactRecord(
    string ZipFileName,
    string AssemblyFileName,
    string ChecksumType,
    string Checksum,
    long Size)
{
    /// <summary>
    /// Measures the archive the identity says to ship.
    /// </summary>
    /// <param name="artifactDirectory">The directory the artifacts are installed from.</param>
    /// <param name="manifest">The identity that names the archive.</param>
    /// <returns>The record for that archive.</returns>
    /// <exception cref="PackagingError">The archive is missing.</exception>
    public static PluginArtifactRecord Measure(string artifactDirectory, PluginManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArgumentNullException.ThrowIfNull(manifest);

        var zipPath = System.IO.Path.Combine(artifactDirectory, manifest.ZipFileName);

        return new PluginArtifactRecord(
            manifest.ZipFileName,
            manifest.AssemblyFileName,
            Digest.Sha256Algorithm,
            Digest.Sha256OfFile(zipPath),
            Digest.SizeOfFile(zipPath));
    }
}

/// <summary>
/// What <c>meta.json</c> records about the FFmpeg wrapper artifact.
/// </summary>
/// <param name="FileName">The file name the wrapper is installed under.</param>
/// <param name="Runtime">The runtime it was published for.</param>
/// <param name="ChecksumType">The checksum algorithm the checksum was produced with.</param>
/// <param name="Checksum">The lowercase hexadecimal checksum of the wrapper file.</param>
/// <param name="Size">The wrapper size in bytes.</param>
public sealed record WrapperArtifactRecord(
    string FileName,
    string Runtime,
    string ChecksumType,
    string Checksum,
    long Size)
{
    /// <summary>
    /// Records a wrapper that was just staged.
    /// </summary>
    /// <param name="artifact">The staged wrapper.</param>
    /// <returns>The record for it.</returns>
    public static WrapperArtifactRecord Of(WrapperArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        return new WrapperArtifactRecord(
            artifact.FileName,
            artifact.Runtime,
            Digest.Sha256Algorithm,
            artifact.Checksum,
            artifact.Size);
    }
}
