using System;
using System.Collections.Generic;
using System.IO;

namespace Anaglyfin.Tests.RuntimeStatus;

/// <summary>
/// A throwaway directory tree and an environment view for one runtime-status test. Everything the
/// service reads — binaries, the slot directory, the settings document, and every variable including
/// <c>PATH</c> — comes from here, so a status test observes a machine it built rather than the one it
/// happens to run on.
/// </summary>
/// <remarks>
/// The environment is a dictionary, never the real process environment, so no test mutates shared
/// state and none can leak a variable into another. <c>PATH</c> is pointed at an empty directory by
/// default, which is what makes "the sidecar is never looked for on <c>PATH</c>" a fact a test can
/// prove rather than assume.
/// </remarks>
internal sealed class RuntimeStatusHarness : IDisposable
{
    private static readonly byte[] ExecutableBytes =
    {
        0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01, 0x00
    };

    public RuntimeStatusHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "anaglyfin-runtime-status-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        // An empty directory so a PATH lookup, if one is ever reached, finds nothing real.
        EmptyPath = Path.Combine(Root, "empty-path");
        Directory.CreateDirectory(EmptyPath);
        Env["PATH"] = EmptyPath;
    }

    /// <summary>Gets the root directory the test builds inside.</summary>
    public string Root { get; }

    /// <summary>Gets the empty directory <c>PATH</c> points at, so no real FFmpeg can be found.</summary>
    public string EmptyPath { get; }

    /// <summary>Gets the environment the service will read.</summary>
    public Dictionary<string, string?> Env { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the environment reader handed to the service.</summary>
    public Func<string, string?> ReadVariable
        => name => Env.TryGetValue(name, out var value) ? value : null;

    /// <summary>Places a path under the root.</summary>
    public string PathOf(params string[] parts)
        => Path.Combine(Root, Path.Combine(parts));

    /// <summary>Creates a directory under the root and returns its full path.</summary>
    public string EnsureDirectory(params string[] parts)
    {
        var path = PathOf(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes an executable file under the root and returns its path.</summary>
    public string WriteExecutable(params string[] parts)
    {
        var path = PathOf(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ExecutableBytes);
        ApplyMode(path, executable: true);
        return path;
    }

    /// <summary>Writes a readable, non-executable file under the root and returns its path.</summary>
    public string WriteFile(params string[] parts)
    {
        var path = PathOf(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"schemaVersion\":2}");
        ApplyMode(path, executable: false);
        return path;
    }

    /// <summary>Writes a file the service account cannot read and returns its path.</summary>
    public string WriteUnreadableFile(params string[] parts)
    {
        var path = PathOf(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "unreadable");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
        }

        return path;
    }

    /// <summary>Writes a file that exists but has no execute bit and returns its path.</summary>
    public string WriteNonExecutableFile(params string[] parts)
    {
        var path = PathOf(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ExecutableBytes);
        ApplyMode(path, executable: false);
        return path;
    }

    /// <summary>Creates a directory the service account cannot write in and returns its path.</summary>
    public string MakeReadOnlyDirectory(params string[] parts)
    {
        var path = PathOf(parts);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return path;
    }

    private static void ApplyMode(string path, bool executable)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                   | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

        if (executable)
        {
            mode |= UnixFileMode.UserExecute;
        }

        File.SetUnixFileMode(path, mode);
    }

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // A read-only directory has to be made writable again before it can be removed.
                foreach (var directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))
                {
                    TryMakeWritable(directory);
                }

                TryMakeWritable(Root);
            }

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover test tree is not a failure to report.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryMakeWritable(string directory)
    {
        try
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
