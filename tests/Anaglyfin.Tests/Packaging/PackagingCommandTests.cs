using System;
using System.IO;
using Anaglyfin.Packager;
using Xunit;

namespace Anaglyfin.Tests.Packaging;

/// <summary>
/// The command line, which is the shape the CI gate actually calls.
/// </summary>
/// <remarks>
/// The exit code is the interface here: a packaging step in a build that treats a refusal and an
/// accepted run as the same outcome is a step that cannot fail. The tests drive
/// <see cref="PackagingCommand.Run"/> in-process - the same call <c>Main</c> makes - so the
/// assertions are about the contract, and the real process is what the CI job runs.
/// </remarks>
public sealed class PackagingCommandTests : IDisposable
{
    private readonly TemporaryPackageDirectory _build = new("command-build");
    private readonly TemporaryPackageDirectory _artifacts = new("command-artifacts");
    private readonly string _manifestPath;
    private readonly string _assemblyPath;

    public PackagingCommandTests()
    {
        _manifestPath = _build.WriteText("Plugin.manifest.xml", PluginBuildStandin.ManifestText());
        _assemblyPath = PluginBuildStandin.CopyAssemblyInto(_build);
    }

    [Fact]
    public void WithoutACommandTheJobExplainsItselfAndFails()
    {
        var run = Run();

        Assert.Equal(PackagingCommand.ExitUsage, run.ExitCode);
        Assert.Contains("usage:", run.Errors, StringComparison.Ordinal);
        Assert.Equal(string.Empty, run.Output.Trim());
    }

    [Fact]
    public void AskingForHelpIsNotAFailure()
    {
        var run = Run("--help");

        Assert.Equal(PackagingCommand.ExitOk, run.ExitCode);
        Assert.Contains("pack", run.Output, StringComparison.Ordinal);
        Assert.Contains("verify", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void PackThenVerifyAreBothGreenAndSayWhatTheyWrote()
    {
        var packed = Run(
            "pack",
            "--plugin-dll",
            _assemblyPath,
            "--manifest",
            _manifestPath,
            "--output",
            _artifacts.Location);

        Assert.Equal(PackagingCommand.ExitOk, packed.ExitCode);
        Assert.Contains("Anaglyfin_0.1.0.zip", packed.Output, StringComparison.Ordinal);
        Assert.Contains("meta.json", packed.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, packed.Errors.Trim());

        var verified = Run(
            "verify",
            "--manifest",
            _manifestPath,
            "--artifacts",
            _artifacts.Location);

        Assert.Equal(PackagingCommand.ExitOk, verified.ExitCode);
        Assert.Contains("verified", verified.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCommandIsACommandLineError()
    {
        var run = Run("publish");

        Assert.Equal(PackagingCommand.ExitUsage, run.ExitCode);
        Assert.Contains("unknown command 'publish'", run.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionTheCommandDoesNotTakeIsACommandLineError()
    {
        var run = Run(
            "verify",
            "--manifest",
            _manifestPath,
            "--artifacts",
            _artifacts.Location,
            "--output",
            _artifacts.Location);

        Assert.Equal(PackagingCommand.ExitUsage, run.ExitCode);
        Assert.Contains("'--output' is not an option of the 'verify' command", run.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionWithoutAValueIsACommandLineError()
    {
        var run = Run("pack", "--plugin-dll", _assemblyPath, "--manifest");

        Assert.Equal(PackagingCommand.ExitUsage, run.ExitCode);
        Assert.Contains("without a value", run.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandMissingItsManifestIsACommandLineError()
    {
        var run = Run("pack", "--plugin-dll", _assemblyPath, "--output", _artifacts.Location);

        Assert.Equal(PackagingCommand.ExitUsage, run.ExitCode);
        Assert.Contains("--manifest", run.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalIsOneLineAndExitCodeOne()
    {
        var manifest = PluginBuildStandin.WriteManifestWith(_build, "targetAbi", "10.11.0");

        var run = Run(
            "pack",
            "--plugin-dll",
            _assemblyPath,
            "--manifest",
            manifest,
            "--output",
            _artifacts.Location);

        Assert.Equal(PackagingCommand.ExitRefused, run.ExitCode);
        Assert.Contains("refused", run.Errors, StringComparison.Ordinal);
        Assert.Contains("older servers", run.Errors, StringComparison.Ordinal);
        Assert.Equal(string.Empty, run.Output.Trim());
    }

    public void Dispose()
    {
        _build.Dispose();
        _artifacts.Dispose();
    }

    private static CommandRun Run(params string[] arguments)
    {
        var output = new StringWriter();
        var errors = new StringWriter();

        var exitCode = PackagingCommand.Run(arguments, output, errors);

        return new CommandRun(exitCode, output.ToString(), errors.ToString());
    }

    private sealed record CommandRun(int ExitCode, string Output, string Errors);
}
