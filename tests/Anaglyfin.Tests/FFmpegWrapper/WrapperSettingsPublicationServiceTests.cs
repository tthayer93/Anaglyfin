using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.Tests.Stubs;
using Anaglyfin.Tests.VersionItems;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Tests for the startup hand-off from the plugin's live settings to the wrapper settings document.
/// </summary>
/// <remarks>
/// The document has to survive a server restart in which nobody opens the admin page, but the
/// hand-off cannot happen inside plugin construction because the server loads settings lazily. This
/// service is the moment at which the live settings are safe to read without asking the plugin to
/// create its own default settings file first.
/// </remarks>
public sealed class WrapperSettingsPublicationServiceTests : IDisposable
{
    private readonly string _root;

    public WrapperSettingsPublicationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "anaglyfin-wrapper-publication-tests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task ARestartPublishesTheSettingsTheServerIsRunningWith()
    {
        var paths = new FakeApplicationPaths(_root);
        var service = new WrapperSettingsPublicationService(
            new StubConfigurationSource
            {
                Configuration = new PluginConfiguration
                {
                    SubtitleDepthMode = SubtitleDepthMode.ConstantShift,
                    SubtitleDepthShift = -18,
                    SubtitleDepthPlane = 7
                }
            },
            paths);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var target = Path.Combine(paths.PluginConfigurationsPath, WrapperSettingsFile.DefaultFileName);

        Assert.True(File.Exists(target), $"The startup publication wrote no settings document at {target}.");
        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.ConstantShift, -18, 0),
            WrapperSettingsFile.Read(target));
    }

    [Fact]
    public async Task ARestartOfAnUnconfiguredServerStillStatesTheDisabledRequest()
    {
        var paths = new FakeApplicationPaths(_root);
        var service = new WrapperSettingsPublicationService(new StubConfigurationSource(), paths);

        await service.StartAsync(CancellationToken.None);

        var target = Path.Combine(paths.PluginConfigurationsPath, WrapperSettingsFile.DefaultFileName);

        // "Off" is a decision the wrapper can read, not the absence of a file it has to interpret.
        Assert.True(File.Exists(target), $"The startup publication wrote no settings document at {target}.");
        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Read(target));
    }

    [Fact]
    public async Task AStartupWriteThatCannotBeMadeDoesNotStopTheHost()
    {
        var paths = new FakeApplicationPaths(_root);
        var target = Path.Combine(paths.PluginConfigurationsPath, WrapperSettingsFile.DefaultFileName);

        // A directory standing in the document's place is a deployment misconfiguration, not a
        // reason the server cannot start. The document is the one thing this service can lose.
        Directory.CreateDirectory(target);

        var service = new WrapperSettingsPublicationService(new StubConfigurationSource(), paths);
        var failure = await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));

        Assert.Null(failure);
        Assert.True(Directory.Exists(target));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
