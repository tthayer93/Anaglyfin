using System;
using System.IO;
using System.Security.Cryptography;

namespace Anaglyfin.Packager;

/// <summary>
/// The checksum an install record carries for an artifact.
/// </summary>
/// <remarks>
/// Jellyfin compares this value with the download before it unpacks anything, so it is
/// computed over the finished file in the artifact directory - not over what the writer held
/// in memory - and the CI gate recomputes it from the artifact on disk. A file that grew,
/// shrank, or was rewritten between the two is caught by that and not by a log line.
/// </remarks>
public static class Digest
{
    /// <summary>The name the checksum algorithm is recorded under.</summary>
    public const string Sha256Algorithm = "sha256";

    /// <summary>
    /// Computes the lowercase hexadecimal SHA-256 of a file.
    /// </summary>
    /// <param name="path">The file to measure.</param>
    /// <returns>The checksum, lowercase and unprefixed.</returns>
    /// <exception cref="PackagingError">The file is not there.</exception>
    public static string Sha256OfFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new PackagingError($"Expected the artifact '{path}' and found nothing there.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Computes the lowercase hexadecimal SHA-256 of an in-memory artifact, which is how an
    /// archive entry is compared with the file it was packed from.
    /// </summary>
    /// <param name="bytes">The bytes to measure.</param>
    /// <returns>The checksum, lowercase and unprefixed.</returns>
    public static string Sha256Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Measures a file in bytes.
    /// </summary>
    /// <param name="path">The file to measure.</param>
    /// <returns>Its length.</returns>
    /// <exception cref="PackagingError">The file is not there.</exception>
    public static long SizeOfFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new PackagingError($"Expected the artifact '{path}' and found nothing there.");
        }

        return new FileInfo(path).Length;
    }
}
