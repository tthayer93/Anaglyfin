using System;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.MediaSources;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Hands the FFmpeg wrapper the settings the plugin is running with when the host starts.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper is a separate process, started by the server rather than by this plugin, and the
/// settings live behind the plugin instance. The host calls this service after plugin construction
/// and after the container can resolve the live settings source, so the publication never has to
/// force a settings load from inside the plugin constructor.
/// </para>
/// <para>
/// A restart without an administrator visiting the settings page still refreshes the document,
/// while a settings save continues to refresh it through <see cref="Plugin.SaveConfiguration"/>.
/// Those are the two moments the wrapper's view can otherwise fall behind the server's.
/// </para>
/// <para>
/// A failed write is deliberately swallowed. The deployment loses the settings the document carries -
/// the depth request and the concurrency limit - and keeps ordinary playback; it does not lose a
/// server that can start. What the wrapper reads when the document is absent or stale is the answer
/// an unconfigured server gives: flat subtitles and whichever limit the deployment names.
/// </para>
/// </remarks>
public sealed class WrapperSettingsPublicationService : IHostedService
{
    private readonly IAnaglyfinConfigurationSource _configurationSource;
    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="WrapperSettingsPublicationService"/> class.
    /// </summary>
    /// <param name="configurationSource">The live settings the wrapper is allowed to receive.</param>
    /// <param name="applicationPaths">The server paths used to resolve the default document location.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="configurationSource"/> or <paramref name="applicationPaths"/> is <c>null</c>.
    /// </exception>
    public WrapperSettingsPublicationService(
        IAnaglyfinConfigurationSource configurationSource,
        IApplicationPaths applicationPaths)
    {
        _configurationSource = configurationSource ?? throw new ArgumentNullException(nameof(configurationSource));
        _applicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
    }

    /// <summary>
    /// Publishes the current settings once.
    /// </summary>
    /// <param name="cancellationToken">Stops the start if the host calls it off.</param>
    /// <returns>A completed task; the write is best effort and never blocks startup on a failure.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configuration = _configurationSource.GetConfiguration();

        var path = WrapperSettingsFile.ResolveWritePath(
            Environment.GetEnvironmentVariable,
            _applicationPaths.PluginConfigurationsPath);

        WrapperSettingsFile.TryWrite(
            path,
            configuration.GetEffectiveSubtitleDepth(),
            configuration.GetEffectiveMaxConcurrentTranscodes(),
            out _);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the service, which holds nothing to stop.
    /// </summary>
    /// <param name="cancellationToken">Stops the shutdown wait; there is none.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
