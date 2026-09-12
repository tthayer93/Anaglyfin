using System;
using System.Buffers.Binary;
using System.IO;

namespace Anaglyfin.Packager;

/// <summary>
/// The FFmpeg wrapper as an installable artifact: one executable the server is pointed at in
/// place of FFmpeg, described by its size and checksum.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper reaches the server differently on the two deployments this repository packages
/// for - copied onto a bare-metal host, or mounted into the official container - but both want
/// the same thing from the build: a file that runs on the server's own architecture with
/// nothing else installed. Inside <c>jellyfin/jellyfin</c> nothing can be installed, which is
/// what makes the self-contained published binary the artifact and not the framework-dependent
/// build output.
/// </para>
/// <para>
/// The one property the job can prove without running the binary is that it is a Linux x86-64
/// executable, so that is what it checks by reading the ELF header. A wrapper that is really a
/// managed DLL, a script, or a binary for another architecture is refused here rather than
/// becoming an exit code 127 in a Jellyfin transcode log.
/// </para>
/// </remarks>
public sealed class WrapperArtifact
{
    /// <summary>The file name the wrapper is installed under.</summary>
    public const string DefaultFileName = "anaglyfin-ffmpeg";

    /// <summary>The first wrapper target this repository packages for.</summary>
    public const string DefaultRuntime = "linux-x64";

    private const int HeaderLength = 20;

    private WrapperArtifact(string fileName, string runtime, long size, string checksum)
    {
        FileName = fileName;
        Runtime = runtime;
        Size = size;
        Checksum = checksum;
    }

    /// <summary>Gets the file name the artifact is staged under.</summary>
    public string FileName { get; }

    /// <summary>Gets the runtime the artifact was published for.</summary>
    public string Runtime { get; }

    /// <summary>Gets the artifact size in bytes.</summary>
    public long Size { get; }

    /// <summary>Gets the lowercase hexadecimal SHA-256 of the artifact.</summary>
    public string Checksum { get; }

    /// <summary>
    /// Copies a published wrapper into the artifact directory and describes it.
    /// </summary>
    /// <param name="publishedPath">The published wrapper executable.</param>
    /// <param name="artifactDirectory">The directory the artifacts are installed from.</param>
    /// <param name="fileName">The file name to stage it under.</param>
    /// <param name="runtime">The runtime it was published for.</param>
    /// <returns>The staged artifact.</returns>
    /// <exception cref="PackagingError">
    /// The name carries a path, the published binary is missing, or it is not a Linux x86-64
    /// executable.
    /// </exception>
    public static WrapperArtifact Stage(string publishedPath, string artifactDirectory, string fileName, string runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);

        RequireFileName(fileName);

        RequireLinuxX64Executable(publishedPath, "the published wrapper");

        var stagedPath = Path.Combine(artifactDirectory, fileName);

        Directory.CreateDirectory(artifactDirectory);
        File.Copy(publishedPath, stagedPath, overwrite: true);

        // A copy inherits the published binary's permissions, which are correct on Linux but
        // are not something the job should hope for: the server starts this file directly.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                stagedPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return Measure(artifactDirectory, fileName, runtime);
    }

    /// <summary>
    /// Checks the file name an artifact is recorded or staged under: a bare name, because the
    /// artifact sits at the root of the artifact directory.
    /// </summary>
    /// <param name="fileName">The name to check.</param>
    /// <exception cref="PackagingError">It carries a path.</exception>
    /// <remarks>
    /// The name is read back out of <c>meta.json</c> before it is ever combined with a directory,
    /// so a record naming <c>../../etc/something</c> as its wrapper is a refusal rather than a
    /// hint about where on the host to look for an ELF binary.
    /// </remarks>
    public static void RequireFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
        {
            throw new PackagingError(
                $"The wrapper artifact name '{fileName}' contains a path. It is staged at the root of the artifact directory.");
        }
    }

    /// <summary>
    /// Describes an artifact that already sits in the artifact directory.
    /// </summary>
    /// <param name="artifactDirectory">The directory the artifacts are installed from.</param>
    /// <param name="fileName">The file name the metadata says to look for.</param>
    /// <param name="runtime">The runtime it claims to be published for.</param>
    /// <returns>The artifact as it actually is on disk.</returns>
    /// <exception cref="PackagingError">The name carries a path, the file is missing, or it is not a Linux x86-64 executable.</exception>
    public static WrapperArtifact Measure(string artifactDirectory, string fileName, string runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);

        RequireFileName(fileName);

        var path = Path.Combine(artifactDirectory, fileName);

        if (!File.Exists(path))
        {
            throw new PackagingError($"The wrapper artifact '{path}' is missing.");
        }

        RequireLinuxX64Executable(path, "the staged wrapper artifact");

        return new WrapperArtifact(fileName, runtime, new FileInfo(path).Length, Digest.Sha256OfFile(path));
    }

    /// <summary>
    /// Checks the ELF header of a file: 64-bit, little-endian, x86-64, and executable rather
    /// than a shared object or an object file.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="subject">What to call it in the refusal.</param>
    /// <exception cref="PackagingError">It is not a Linux x86-64 executable.</exception>
    public static void RequireLinuxX64Executable(string path, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        if (!File.Exists(path))
        {
            throw new PackagingError($"The file called '{path}' - {subject} - is missing.");
        }

        Span<byte> header = new byte[HeaderLength];
        int read;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false);
        }

        if (read < 4)
        {
            throw new PackagingError(
                $"The file called '{path}' ({subject}) is too short to be a Linux executable.");
        }

        if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
        {
            throw new PackagingError(
                $"The file called '{path}' ({subject}) is not an ELF executable."
                + " Publish the wrapper for linux-x64; a managed DLL or a script is not what the server starts.");
        }

        if (read < HeaderLength)
        {
            throw new PackagingError(
                $"The file called '{path}' ({subject}) starts as an ELF binary but ends before its header does.");
        }

        const byte elfClass64 = 2;
        const byte elfDataLittleEndian = 1;
        const ushort typeExecutable = 2;
        const ushort typeSharedObject = 3;
        const ushort machineXAmd64 = 0x3E;

        var elfClass = header[4];
        var elfData = header[5];
        var fileType = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);

        if (elfClass != elfClass64 || elfData != elfDataLittleEndian)
        {
            throw new PackagingError(
                $"The file called '{path}' ({subject}) is not a 64-bit little-endian ELF binary"
                + $" (ELF class {elfClass}, data {elfData}); {DefaultRuntime} binaries are.");
        }

        if (fileType != typeExecutable && fileType != typeSharedObject)
        {
            throw new PackagingError(
                $"The file called '{path}' ({subject}) has ELF type {fileType}, which is not an executable."
                + " A relocatable object or a core dump does not start.");
        }

        if (machine != machineXAmd64)
        {
            throw new PackagingError(
                $"The file called '{path}' ({subject}) targets ELF machine 0x{machine:X4},"
                + $" not x86-64. The {DefaultRuntime} build is the one this repository packages.");
        }
    }
}
