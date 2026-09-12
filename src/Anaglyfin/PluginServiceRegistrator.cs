using System;
using Anaglyfin.Profiles;
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

        // applicationHost is not consumed yet: settings live on the plugin instance and
        // are handed to the catalog per call, so no service here needs the host to start.

        // The profile catalog is stateless and built once for the process.
        serviceCollection.Add(new ServiceDescriptor(typeof(IProfileCatalog), typeof(ProfileCatalog), ServiceLifetime.Singleton));

        // Media source providers are discovered by type scan and must not be registered
        // manually.
    }
}
