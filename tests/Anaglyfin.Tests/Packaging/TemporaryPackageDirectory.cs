using System;
using System.IO;

namespace Anaglyfin.Tests.Packaging;

/// <summary>
/// A throwaway directory a packaging run can be pointed at, so the tests write archives and
/// metadata to storage they own and delete afterwards.
/// </summary>
/// <remarks>
/// The packager's whole contract is about files on disk - what is in the archive, what the
/// record claims about its size and checksum - so the tests read those files back rather than
/// asking the packager what it remembers. Every path handed to the job under test comes from
/// here, including the fake plugin assemblies and manifests the refusal tests need.
/// </remarks>
public sealed class TemporaryPackageDirectory : IDisposable
{
    public TemporaryPackageDirectory(string purpose)
    {
        Location = Path.Combine(
            Path.GetTempPath(),
            "anaglyfin-packager-tests",
            purpose + "-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Location);
    }

    /// <summary>Gets the directory itself.</summary>
    public string Location { get; }

    /// <summary>
    /// Puts a file name inside this directory.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <returns>The full path.</returns>
    public string LocationOf(string name) => Path.Combine(Location, name);

    /// <summary>
    /// Writes a file into the directory.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="content">The text to write.</param>
    /// <returns>The full path written.</returns>
    public string WriteText(string name, string content)
    {
        var path = LocationOf(name);

        File.WriteAllText(path, content);

        return path;
    }

    /// <summary>
    /// Copies a file into the directory under a name of the caller's choosing, which is how a
    /// test stands in for a build that left the wrong file name behind.
    /// </summary>
    /// <param name="sourcePath">The file to copy.</param>
    /// <param name="name">The name to file it under.</param>
    /// <returns>The full path written.</returns>
    public string CopyIn(string sourcePath, string name)
    {
        var path = LocationOf(name);

        File.Copy(sourcePath, path, overwrite: true);

        return path;
    }

    /// <summary>
    /// Reads a file from the directory.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <returns>Its text.</returns>
    public string ReadText(string name) => File.ReadAllText(LocationOf(name));

    /// <summary>
    /// Lists the file names in the directory, ordered so a test can name them.
    /// </summary>
    /// <returns>The file names, without directories.</returns>
    public string[] FileNames()
    {
        var names = Directory.GetFiles(Location, "*", SearchOption.AllDirectories);

        for (var index = 0; index < names.Length; index++)
        {
            names[index] = Path.GetRelativePath(Location, names[index]);
        }

        Array.Sort(names, StringComparer.Ordinal);

        return names;
    }

    /// <summary>
    /// Removes one file, which is how a test arranges for an artifact to be missing.
    /// </summary>
    /// <param name="name">The file name.</param>
    public void Delete(string name) => File.Delete(LocationOf(name));

    /// <summary>Removes the directory and everything in it.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Location))
            {
                Directory.Delete(Location, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file a crashed test still holds open is not a failure to report.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
