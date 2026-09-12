using System;
using Anaglyfin.Detection;
using Anaglyfin.MediaSources;
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

        // The MVC detector is stateless rules over metadata signals; one instance for
        // the process. The media source provider receives it through its constructor,
        // which the server activates with ActivatorUtilities.
        serviceCollection.Add(new ServiceDescriptor(typeof(IMvcSourceDetector), typeof(MvcSourceDetector), ServiceLifetime.Singleton));

        // The settings seam resolves the plugin instance through the server's plugin
        // manager, so it can serve the live configuration to container-constructed
        // consumers without a plugin static.
        serviceCollection.Add(new ServiceDescriptor(typeof(IAnaglyfinConfigurationSource), typeof(PluginConfigurationSource), ServiceLifetime.Singleton));

        // Media source providers are discovered by type scan and must not be registered
        // manually; an IFfmpegProfileArgumentBuilder registration would be dead weight
        // until the wrapper needs one in-process, so there is none.
    }
}
