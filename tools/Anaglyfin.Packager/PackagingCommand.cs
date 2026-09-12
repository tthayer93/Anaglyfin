using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Anaglyfin.Packager;

/// <summary>
/// The command line of the packaging job, which is what makes it a CI step.
/// </summary>
/// <remarks>
/// <para>
/// Two sub-commands, because the gate wants both halves: <c>pack</c> writes the artifacts from
/// a build, and <c>verify</c> judges a directory of artifacts knowing only the manifest. A
/// packaging step that only ever ran its own happy path would prove that files were written and
/// not that they install, so CI is expected to run <c>pack</c> and then <c>verify</c> - the
/// second call cannot see anything the first one remembered.
/// </para>
/// <para>
/// Every failure leaves as a non-zero exit code with one line of reason. There is no flag that
/// turns a refusal into a warning.
/// </para>
/// </remarks>
public static class PackagingCommand
{
    /// <summary>The exit code of an accepted run.</summary>
    public const int ExitOk = 0;

    /// <summary>The exit code of a refusal.</summary>
    public const int ExitRefused = 1;

    /// <summary>The exit code of a command line the job could not act on.</summary>
    public const int ExitUsage = 2;

    /// <summary>The name the job calls itself by.</summary>
    public const string ProgramName = "anaglyfin-packager";

    private static readonly string[] PackOptionNames =
    {
        "--plugin-dll",
        "--manifest",
        "--output",
        "--wrapper-input",
        "--wrapper-name",
        "--wrapper-runtime",
    };

    private static readonly string[] VerifyOptionNames =
    {
        "--manifest",
        "--artifacts",
        "--wrapper-file",
    };

    private static readonly string Usage = string.Join(
        Environment.NewLine,
        "usage:",
        $"  {ProgramName} pack --plugin-dll <path> --manifest <path> --output <dir>",
        $"              [--wrapper-input <path>] [--wrapper-name {WrapperArtifact.DefaultFileName}]",
        $"              [--wrapper-runtime {WrapperArtifact.DefaultRuntime}]",
        $"  {ProgramName} verify --manifest <path> --artifacts <dir> [--wrapper-file <name>]",
        string.Empty,
        "pack    writes <name>_<version>.zip with <name>.dll at the archive root, plus meta.json,",
        "        stages the published wrapper beside them, and verifies what it wrote.",
        "verify  re-reads the artifacts and refuses anything a server would not install.");

    /// <summary>
    /// Runs the job.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="standardOutput">Where the list of written artifacts goes.</param>
    /// <param name="standardError">Where refusals go.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args, TextWriter standardOutput, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (args.Length == 0)
        {
            standardError.WriteLine(Usage);

            return ExitUsage;
        }

        if (args[0] is "help" or "--help" or "-h")
        {
            standardOutput.WriteLine(Usage);

            return ExitOk;
        }

        try
        {
            standardOutput.WriteLine(args[0] switch
            {
                "pack" => Pack(args),
                "verify" => Verify(args),
                _ => throw new UsageException($"unknown command '{args[0]}'"),
            });

            return ExitOk;
        }
        catch (UsageException usage)
        {
            standardError.WriteLine($"{ProgramName}: {usage.Message}.{Environment.NewLine}{Usage}");

            return ExitUsage;
        }
        catch (PackagingError refusal)
        {
            standardError.WriteLine($"{ProgramName}: refused: {refusal.Message}");

            return ExitRefused;
        }

        // Last line of a build step. Anything reaching here is a bug in the job itself, and the
        // gate only needs to know that the job is the thing that failed.
#pragma warning disable CA1031 // Deliberate: an unexpected exception is still a refusal.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            standardError.WriteLine(
                $"{ProgramName}: refused: {failure.GetType().Name}: {failure.Message}");

            return ExitRefused;
        }
    }

    private static string Pack(string[] args)
    {
        var options = ReadOptions(args, PackOptionNames, "pack");

        var request = new PackageRequest
        {
            PluginDllPath = Required(options, "--plugin-dll", "pack"),
            ManifestPath = Required(options, "--manifest", "pack"),
            ArtifactDirectory = Required(options, "--output", "pack"),
            WrapperInputPath = Optional(options, "--wrapper-input"),
            WrapperFileName = Optional(options, "--wrapper-name") ?? WrapperArtifact.DefaultFileName,
            WrapperRuntime = Optional(options, "--wrapper-runtime") ?? WrapperArtifact.DefaultRuntime,
        };

        var packaged = PluginPackager.Pack(request);

        var written = new List<string> { packaged.ArchivePath, packaged.MetadataPath };

        if (packaged.WrapperPath is not null)
        {
            written.Add(packaged.WrapperPath);
        }

        return string.Join(" + ", written) + $" (verified against {request.ManifestPath})";
    }

    private static string Verify(string[] args)
    {
        var options = ReadOptions(args, VerifyOptionNames, "verify");

        var request = new VerificationRequest
        {
            ManifestPath = Required(options, "--manifest", "verify"),
            ArtifactDirectory = Required(options, "--artifacts", "verify"),
            WrapperFileName = Optional(options, "--wrapper-file"),
        };

        PluginPackager.Verify(request);

        return $"verified the artifacts in {request.ArtifactDirectory} against {request.ManifestPath}";
    }

    private static Dictionary<string, string> ReadOptions(string[] args, string[] allowedNames, string command)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];

            if (!name.StartsWith("--", StringComparison.Ordinal) || !allowedNames.Contains(name, StringComparer.Ordinal))
            {
                throw new UsageException($"'{name}' is not an option of the '{command}' command");
            }

            if (options.ContainsKey(name))
            {
                throw new UsageException($"the option '{name}' was given twice");
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"the option '{name}' was given without a value");
            }

            options[name] = args[++index];
        }

        return options;
    }

    private static string Required(Dictionary<string, string> options, string name, string command) =>
        Optional(options, name) ?? throw new UsageException($"the '{command}' command needs {name}");

    private static string? Optional(Dictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// A command line the job could not act on, which is a different outcome from a refusal: no
    /// artifact was ever at stake.
    /// </summary>
    private sealed class UsageException : Exception
    {
        public UsageException(string message)
            : base(message)
        {
        }
    }
}
