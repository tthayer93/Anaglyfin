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

        // The slot sweep. Also a startup hosted service, and for the same reason the publication is
        // one: the moment the server starts is the moment no Anaglyfin encode of its own can be
        // running, which is the only moment a leftover slot can be told apart from a running job
        // without waiting for a playback to find out the hard way.
        serviceCollection.Add(new ServiceDescriptor(typeof(IHostedService), typeof(TranscodeSlotCleanupService), ServiceLifetime.Singleton));

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
    /// answers a server-owned service with a plugin object that cannot answer for it.
    /// </para>
    /// <para>
    /// <b>How often this runs.</b> Once per collection, on a normal server: the host registers its
    /// own services into a fresh collection and then walks every plugin registrator over that same
    /// collection exactly once, before the container is built. So the refusal to decorate a filter
    /// that is already in the collection is not what keeps one decoration in place on such a server
    /// - nothing else could have added a second one - it is what keeps this method honest when the
    /// same collection comes round twice, which is the only way the registration it would decorate
    /// can be the plugin's own. Decorating it would hide a filter behind a filter.
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

        // Three answers all mean "append nothing": there is no manager registered to wrap, the
        // manager registered is already Anaglyfin's version filter, or the registration carries no
        // object at all to hand to a decorator.
        if (coreDescriptor is null
            || IsOriginalMvcVersionFilter(coreDescriptor)
            || CarriesNoImplementation(coreDescriptor))
        {
            return;
        }

        // The registration is the object its factory is made from, which is what lets a later call
        // recognise it: see OriginalMvcVersionFilterRegistration.
        var registration = new OriginalMvcVersionFilterRegistration(coreDescriptor);

        serviceCollection.Add(new ServiceDescriptor(
            typeof(IMediaSourceManager),
            registration.Create,
            ServiceLifetime.Singleton));
    }

    /// <summary>
    /// Answers whether a media source manager registration is already Anaglyfin's version filter.
    /// </summary>
    /// <param name="descriptor">The registration the container would answer with.</param>
    /// <returns>
    /// <c>true</c> when wrapping it would put a filter behind a filter rather than a filter around
    /// the server's manager.
    /// </returns>
    /// <remarks>
    /// Every shape a registration can carry is answered, because a filter can arrive in any of
    /// them. This plugin appends a <b>factory</b>, which is the shape with no type to name: it is
    /// recognised by the object its delegate was made from rather than by anything it would return,
    /// since nothing has run it yet. A build that registered the decorator as a <b>type</b>, or a
    /// host that handed one over as an <b>instance</b>, carries that type on its face. Answering
    /// only those last two is what let a second call wrap the first.
    /// </remarks>
    private static bool IsOriginalMvcVersionFilter(ServiceDescriptor descriptor)
        => descriptor.ImplementationType == typeof(MediaSourceManagerSuppressionDecorator)
            || descriptor.ImplementationInstance is MediaSourceManagerSuppressionDecorator
            || descriptor.ImplementationFactory?.Target is OriginalMvcVersionFilterRegistration;

    /// <summary>
    /// Answers whether a registration carries nothing the container could build.
    /// </summary>
    /// <param name="descriptor">The registration to read.</param>
    /// <returns>
    /// <c>true</c> when it names no type, holds no instance and carries no factory, so there is no
    /// object to wrap and nothing to hand over as the core of a decorator.
    /// </returns>
    private static bool CarriesNoImplementation(ServiceDescriptor descriptor)
        => descriptor.ImplementationType is null
            && descriptor.ImplementationFactory is null
            && descriptor.ImplementationInstance is null;

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

    /// <summary>
    /// One appended <see cref="IMediaSourceManager"/> registration: the server's registration this
    /// plugin wraps, and the factory that wraps it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason this is an object rather than a lambda over a local is that a factory-shaped
    /// registration is otherwise unrecognisable after the fact. The version filter has to be able to
    /// say "the collection already carries me" (see
    /// <see cref="IsOriginalMvcVersionFilter(ServiceDescriptor)"/>), and a
    /// <see cref="ServiceDescriptor"/> exposes a factory only as a function - a closure over a local
    /// exposes nothing at all, while a delegate made from a method of this type exposes the object it
    /// was made from. That object is the marker: it says the factory is Anaglyfin's, and nothing else
    /// in the container can produce it.
    /// </para>
    /// <para>
    /// Holding the wrapped registration here rather than in a closure is also what turns the factory
    /// into a method with a name of its own, so the read path and the failure path of building the
    /// core manager are stated once, in a method a reader can find, instead of inside an argument
    /// list.
    /// </para>
    /// </remarks>
    private sealed class OriginalMvcVersionFilterRegistration
    {
        private readonly ServiceDescriptor _coreDescriptor;

        /// <summary>
        /// Initializes a new instance of the <see cref="OriginalMvcVersionFilterRegistration"/> class.
        /// </summary>
        /// <param name="coreDescriptor">
        /// The registration being decorated - the server's own manager, as the collection held it at
        /// the moment this registration was appended.
        /// </param>
        public OriginalMvcVersionFilterRegistration(ServiceDescriptor coreDescriptor)
        {
            _coreDescriptor = coreDescriptor;
        }

        /// <summary>
        /// Builds the decorated manager the container answers with.
        /// </summary>
        /// <param name="provider">The container resolving the manager.</param>
        /// <returns>The version filter, wrapping the manager the server registered.</returns>
        public object Create(IServiceProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);

            var (core, owned) = BuildCoreMediaSourceManager(provider, _coreDescriptor);

            return new MediaSourceManagerSuppressionDecorator(
                core,
                owned,
                provider.GetRequiredService<IMvcSourceDetector>(),
                provider.GetRequiredService<IProfileCatalog>(),
                provider.GetRequiredService<IAnaglyfinConfigurationSource>(),
                provider.GetRequiredService<ILogger<MediaSourceManagerSuppressionDecorator>>());
        }
    }
}
