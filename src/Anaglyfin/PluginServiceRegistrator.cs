using System;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Anaglyfin;

/// <summary>
/// Registers Anaglyfin services in the server DI container.
/// </summary>
/// <remarks>
/// The server discovers this type by assembly scan and instantiates it directly,
/// so it must keep a public parameterless constructor.
/// </remarks>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(applicationHost);

        // Scaffold only. Profile catalog, settings and detection services will
        // register here as they land. Media source providers are discovered by
        // type scan and must not be registered manually.
    }
}
