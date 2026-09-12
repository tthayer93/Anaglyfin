using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Anaglyfin.Packager;

/// <summary>
/// The plugin archive: one assembly, at the root, packed the same way every time.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin's assembly scanner looks in the plugin directory it unpacks an archive into, so
/// the shape of the archive is part of the install contract: <c>Anaglyfin.dll</c> has to be
/// there, not under <c>net10.0/</c> or a build folder the packer happened to walk. Everything
/// the plugin needs beyond the assembly - the identity manifest and the admin page - is
/// embedded in it, which is why a single entry is the whole package.
/// </para>
/// <para>
/// The archive is also meant to be comparable between builds, so nothing that varies between
/// runs goes into it: entry names and order are fixed by this code, every entry carries the
/// same timestamp, and the compression level is stated rather than defaulted.
/// </para>
/// </remarks>
public static class PluginArchive
{
    /// <summary>
    /// The timestamp every entry is stamped with. Local kind, so the stored DOS time is the
    /// same wall-clock value whatever timezone the agent runs in.
    /// </summary>
    private static readonly DateTimeOffset EntryTimestamp = new(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local));

    /// <summary>
    /// Writes the plugin archive.
    /// </summary>
    /// <param name="archivePath">Where to write it.</param>
    /// <param name="assemblyPath">The plugin assembly to pack.</param>
    /// <param name="entryName">The name it is packed under.</param>
    /// <returns>The path written.</returns>
    public static string Create(string archivePath, string assemblyPath, string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        if (!File.Exists(assemblyPath))
        {
            throw new PackagingError($"Cannot pack '{archivePath}': the plugin assembly '{assemblyPath}' is missing.");
        }

        using (var archiveStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = EntryTimestamp;

            using (var entryStream = entry.Open())
            using (var assemblyStream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                assemblyStream.CopyTo(entryStream);
            }
        }

        return archivePath;
    }

    /// <summary>
    /// Checks an archive the way a server will meet it: the assembly at the root, and nothing
    /// packed into a subdirectory.
    /// </summary>
    /// <param name="archivePath">The archive to open.</param>
    /// <param name="expectedAssemblyFileName">The assembly the archive has to carry.</param>
    /// <exception cref="PackagingError">The assembly is absent, duplicated, or nested.</exception>
    public static void RequirePluginLayout(string archivePath, string expectedAssemblyFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAssemblyFileName);

        if (!File.Exists(archivePath))
        {
            throw new PackagingError($"The plugin archive '{archivePath}' is missing.");
        }

        var entryNames = new List<string>();

        using (var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                entryNames.Add(entry.FullName);
            }
        }

        foreach (var entryName in entryNames)
        {
            if (entryName.IndexOf('/') >= 0 || entryName.IndexOf('\\') >= 0)
            {
                throw new PackagingError(
                    $"The plugin archive '{archivePath}' packs '{entryName}' inside a directory. Jellyfin loads plugins"
                    + $" from the directory it unpacks into, so '{expectedAssemblyFileName}' has to sit at the archive root.");
            }
        }

        if (!entryNames.Contains(expectedAssemblyFileName, StringComparer.Ordinal))
        {
            throw new PackagingError(
                $"The plugin archive '{archivePath}' does not carry '{expectedAssemblyFileName}' at its root."
                + $" It carries: {string.Join(", ", entryNames)}.");
        }
    }

    /// <summary>
    /// Reads one entry out of an archive.
    /// </summary>
    /// <param name="archivePath">The archive to open.</param>
    /// <param name="entryName">The entry to read.</param>
    /// <returns>The packed bytes.</returns>
    /// <exception cref="PackagingError">The archive or the entry is missing.</exception>
    public static byte[] ReadEntry(string archivePath, string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        if (!File.Exists(archivePath))
        {
            throw new PackagingError($"The plugin archive '{archivePath}' is missing.");
        }

        using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

        var entry = archive.GetEntry(entryName)
            ?? throw new PackagingError($"The plugin archive '{archivePath}' does not carry '{entryName}'.");

        using var entryStream = entry.Open();

        return ReadAll(entryStream);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();

        stream.CopyTo(buffer);

        return buffer.ToArray();
    }
}
