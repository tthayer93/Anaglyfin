using System;
using Anaglyfin.Detection;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.VersionItems;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

        // ---- profile version items -------------------------------------------------
        //
        // A version item is a library item, so it is created through the server's own
        // public library and persistence APIs, behind one seam small enough to test.

        serviceCollection.Add(new ServiceDescriptor(typeof(IProfileVersionItemStore), typeof(LibraryProfileVersionItemStore), ServiceLifetime.Singleton));

        // The library's item events, narrowed to the three Anaglyfin listens to.
        serviceCollection.Add(new ServiceDescriptor(typeof(ILibraryEventSource), typeof(LibraryEventSource), ServiceLifetime.Singleton));

        // The reconciliation itself. One instance for the process: it holds the gate that
        // keeps two passes from each seeing the same version item as missing.
        serviceCollection.Add(new ServiceDescriptor(typeof(IProfileVersionReconciler), typeof(ProfileVersionItemManager), ServiceLifetime.Singleton));

        // The requests, and the coalescing state behind them. One instance, handed to its
        // two kinds of caller as itself (the worker, which drains it) and as the narrow
        // request interface (the plugin, which may only ask).
        serviceCollection.Add(new ServiceDescriptor(typeof(ProfileVersionReconcileQueue), typeof(ProfileVersionReconcileQueue), ServiceLifetime.Singleton));
        serviceCollection.Add(new ServiceDescriptor(
            typeof(IProfileVersionReconcileTrigger),
            provider => provider.GetRequiredService<ProfileVersionReconcileQueue>(),
            ServiceLifetime.Singleton));

        // The plugin's background worker. The host starts it after the library is up and
        // stops it on shutdown, which is the only honest lifetime for something that
        // writes to the library on the library's own events.
        serviceCollection.Add(new ServiceDescriptor(typeof(IHostedService), typeof(ProfileVersionItemService), ServiceLifetime.Singleton));

        // The wrapper settings bridge. The host starts it after plugin construction, which is the
        // first moment the settings source can be read without making the plugin constructor write
        // a default settings file. It publishes once and then leaves the live save path to refresh
        // the document.
        serviceCollection.Add(new ServiceDescriptor(typeof(IHostedService), typeof(WrapperSettingsPublicationService), ServiceLifetime.Singleton));

        // Media source providers are discovered by type scan and must not be registered
        // manually; an IFfmpegProfileArgumentBuilder registration would be dead weight
        // until the wrapper needs one in-process, so there is none.
    }
}
