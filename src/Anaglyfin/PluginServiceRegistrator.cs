using System;
using Anaglyfin.Detection;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.VersionItems;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        // ---- the version picker's view of the original MVC file ------------------------------
        //
        // The raw MVC file reaches a client as a static media source of the item the scanner filed
        // it beside, and both surfaces a user picks a version from - the details page and
        // PlaybackInfo - read those sources through IMediaSourceManager. Anaglyfin's answer to
        // "hide it" is therefore a filter on that one service and nothing else: no library item,
        // link, path or item enumeration is touched, which is what lets the checkbox be ticked back
        // and the entry reappear on the next request without a scan. See the decorator for the
        // whole argument, including what it refuses to hide.
        RegisterOriginalMvcVersionFilter(serviceCollection);

        // Media source providers are discovered by type scan and must not be registered
        // manually; an IFfmpegProfileArgumentBuilder registration would be dead weight
        // until the wrapper needs one in-process, so there is none.
    }

    /// <summary>
    /// Appends the read filter that answers the <c>Offer original 3D MVC version</c> setting, on
    /// top of the media source manager the server already registered.
    /// </summary>
    /// <param name="serviceCollection">The collection the server is building its container from.</param>
    /// <remarks>
    /// <para>
    /// <b>Why appending works.</b> The server registers its own manager before it asks any plugin
    /// registrator to add anything, and a service type resolves to its last registration, so an
    /// entry appended here is the entry the container answers with - including for
    /// <c>BaseItem.MediaSourceManager</c> and for <c>AddParts</c>, both of which the host performs
    /// later, against the resolved instance. Nothing is replaced and nothing is removed: the
    /// server's descriptor stays exactly where it was and stays the definition of the object being
    /// wrapped.
    /// </para>
    /// <para>
    /// <b>Why it is conditional.</b> The decorator has nothing to be a decorator without: the core
    /// it forwards to is built out of that earlier descriptor, and with no manager registered there
    /// is no list to filter and no object to wrap. Registering anyway would be a container that
    /// answers a server-owned service with a plugin object that cannot answer for it. The same
    /// reasoning keeps a second call from decorating the decorator.
    /// </para>
    /// <para>
    /// <b>Why the core is built here.</b> Because the container will not build it: only the last
    /// registration for a service type is ever activated, so the server's own registration would
    /// go unused and its instance never created. Building it from that descriptor - through its
    /// factory, its instance or its type, whichever the server used - is what keeps the wrapped
    /// object the one the server asked for, and it is also why the decorator, not the container,
    /// now owns its disposal.
    /// </para>
    /// </remarks>
    private static void RegisterOriginalMvcVersionFilter(IServiceCollection serviceCollection)
    {
        ServiceDescriptor? coreDescriptor = null;

        foreach (var descriptor in serviceCollection)
        {
            // The last one wins, in this loop as it does in the container: decorating an entry the
            // container would never have answered would decorate nothing.
            if (descriptor.ServiceType == typeof(IMediaSourceManager))
            {
                coreDescriptor = descriptor;
            }
        }

        if (coreDescriptor is null
            || coreDescriptor.ImplementationType == typeof(MediaSourceManagerSuppressionDecorator)
            || (coreDescriptor.ImplementationType is null
                && coreDescriptor.ImplementationFactory is null
                && coreDescriptor.ImplementationInstance is null))
        {
            return;
        }

        serviceCollection.Add(new ServiceDescriptor(
            typeof(IMediaSourceManager),
            provider =>
            {
                var (core, owned) = BuildCoreMediaSourceManager(provider, coreDescriptor);

                return new MediaSourceManagerSuppressionDecorator(
                    core,
                    owned,
                    provider.GetRequiredService<IMvcSourceDetector>(),
                    provider.GetRequiredService<IProfileCatalog>(),
                    provider.GetRequiredService<IAnaglyfinConfigurationSource>(),
                    provider.GetRequiredService<ILogger<MediaSourceManagerSuppressionDecorator>>());
            },
            ServiceLifetime.Singleton));
    }

    /// <summary>
    /// Builds the media source manager the server registered, from the descriptor that registered
    /// it.
    /// </summary>
    /// <param name="provider">The container resolving the manager.</param>
    /// <param name="coreDescriptor">The server's own registration.</param>
    /// <returns>
    /// The manager, and whether it was built here rather than handed over - which decides whose job
    /// it is to dispose it.
    /// </returns>
    /// <remarks>
    /// The three shapes a registration can carry are answered in the order the container answers
    /// them, and nothing else is invented: an instance the server already has is used as it stands,
    /// a factory is called with this container, and a type is activated the same way the container
    /// would activate it - its own constructor dependencies resolved from the same container. A
    /// descriptor that answers with something that is not a media source manager is not quietly
    /// worked around: it is a server Anaglyfin does not recognise, and the container would have
    /// said so on its own had it been the one building the object.
    /// </remarks>
    private static (IMediaSourceManager Manager, bool Owned) BuildCoreMediaSourceManager(
        IServiceProvider provider,
        ServiceDescriptor coreDescriptor)
    {
        object? core;
        var owned = true;

        if (coreDescriptor.ImplementationInstance is not null)
        {
            core = coreDescriptor.ImplementationInstance;
            owned = false;
        }
        else if (coreDescriptor.ImplementationFactory is not null)
        {
            core = coreDescriptor.ImplementationFactory(provider);
        }
        else
        {
            core = ActivatorUtilities.CreateInstance(provider, coreDescriptor.ImplementationType!);
        }

        if (core is not IMediaSourceManager manager)
        {
            throw new InvalidOperationException(
                $"The registered {nameof(IMediaSourceManager)} is answered by "
                + (core?.GetType().Name ?? "nothing")
                + ", which Anaglyfin's version filter cannot wrap.");
        }

        return (manager, owned);
    }
}
