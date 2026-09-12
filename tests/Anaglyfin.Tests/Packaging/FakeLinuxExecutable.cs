using System;
using System.Buffers.Binary;
using System.IO;

namespace Anaglyfin.Tests.Packaging;

/// <summary>
/// The bytes of a Linux x86-64 executable, for tests that only need the packager to read the
/// header of a published wrapper.
/// </summary>
/// <remarks>
/// The job never runs the wrapper - it cannot, the server that will is somebody else's - so the
/// claim under test is exactly what an ELF header states. These are real headers over an inert
/// payload, including the wrong ones (another architecture, a relocatable object) that the job
/// has to turn away.
/// </remarks>
internal static class FakeLinuxExecutable
{
    /// <summary>The header size the packager reads.</summary>
    internal const int MinimumLength = 64;

    /// <summary>
    /// Builds the bytes of an ELF file.
    /// </summary>
    /// <param name="elfClass">The ELF class byte; 2 is 64-bit.</param>
    /// <param name="elfData">The endianness byte; 1 is little-endian.</param>
    /// <param name="fileType">The ELF object type; 2 is an executable, 3 a shared object.</param>
    /// <param name="machine">The ELF machine; 0x3E is x86-64.</param>
    /// <returns>A file's worth of bytes.</returns>
    internal static byte[] Bytes(byte elfClass = 2, byte elfData = 1, ushort fileType = 2, ushort machine = 0x3E)
    {
        var bytes = new byte[MinimumLength + 32];

        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = elfClass;
        bytes[5] = elfData;
        bytes[6] = 1;

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16, 2), fileType);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18, 2), machine);

        for (var index = MinimumLength; index < bytes.Length; index++)
        {
            // The bundle a published wrapper carries behind its apphost.
            bytes[index] = (byte)'x';
        }

        return bytes;
    }

    /// <summary>
    /// Writes a well-formed linux-x64 executable into a test directory.
    /// </summary>
    /// <param name="directory">The directory to write it into.</param>
    /// <param name="name">The file name a published wrapper leaves behind.</param>
    /// <returns>The written path.</returns>
    internal static string WriteInto(TemporaryPackageDirectory directory, string name = "Anaglyfin.FFmpegWrapper")
    {
        var path = directory.LocationOf(name);

        File.WriteAllBytes(path, Bytes());

        return path;
    }
}
