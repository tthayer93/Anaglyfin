using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.Detection;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.Tests.Stubs;
using Anaglyfin.Tests.VersionItems;
using Anaglyfin.VersionItems;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anaglyfin.Tests;

/// <summary>
/// Verifies the service registrator honours the constraints the server places on it.
/// </summary>
public class PluginServiceRegistratorTests
{
    [Fact]
    public void RegistratorIsDiscoveredAsAPluginServiceRegistrator()
    {
        Assert.IsAssignableFrom<IPluginServiceRegistrator>(new PluginServiceRegistrator());
    }

    [Fact]
    public void RegistratorKeepsAPublicParameterlessConstructor()
    {
        // The server creates registrators itself and requires a parameterless constructor,
        // so constructor dependencies would break plugin start-up.
        var constructor = typeof(PluginServiceRegistrator).GetConstructor(Type.EmptyTypes);

        Assert.NotNull(constructor);
        Assert.True(constructor!.IsPublic);
    }

    [Fact]
    public void RegisterServicesRejectsAMissingServiceCollection()
    {
        var registrator = new PluginServiceRegistrator();

        Assert.Throws<ArgumentNullException>(() => registrator.RegisterServices(null!, null!));
    }

    [Fact]
    public void RegisterServicesRegistersThePluginOwnedSingletons()
    {
        // The deliberate set of registrations: the catalog, the detector the media source provider
        // runs on, the settings seam, the seven members of the version-item subsystem (its store,
        // its event source, its reconciler, the queue, the narrow request interface over it, and the
        // hosted service that drains it), the hosted service that publishes the wrapper settings
        // document at startup, and the hosted service that sweeps the transcode slots the previous
        // run left behind. A new service must be added here on purpose - this count is the review
        // gate against silent registrations.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        Assert.Equal(11, services.Count);
    }

    [Fact]
    public void RegisterServicesRegistersTheProfileCatalogAsASingleton()
    {
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IProfileCatalog));
        Assert.Equal(typeof(ProfileCatalog), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void RegisterServicesRegistersTheMvcSourceDetectorAsASingleton()
    {
        // The media source provider is activated by the server's ActivatorUtilities,
        // so its constructor dependency must resolve from the container.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IMvcSourceDetector));
        Assert.Equal(typeof(MvcSourceDetector), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void RegisterServicesRegistersTheConfigurationSourceAsASingleton()
    {
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IAnaglyfinConfigurationSource));
        Assert.Equal(typeof(PluginConfigurationSource), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void RegisterServicesDoesNotRegisterAMediaSourceProvider()
    {
        // Media source providers are discovered by type scan; registering one here would
        // make it invisible to that scan. The provider's dependencies are registered;
        // the provider itself never is.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.Name.Contains("MediaSourceProvider", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisterServicesShadowsOnlyTheApprovedMediaSourceManagerDecoration()
    {
        // A plugin registration for a service the server already registered is not dead weight. A
        // service type resolves to its last registration, so the plugin's object becomes the one
        // every controller, helper and static in the server receives - which is exactly why the
        // plugin's one shadow of a server-owned service is spelled out here instead of assumed:
        // IMediaSourceManager, deliberately, for the original MVC version filter, and reasoned in
        // PluginServiceRegistrator. Every other server-owned type in the same collection has to
        // come back exactly as the server left it.
        var services = new FakeServiceCollection();
        var seeded = new Dictionary<Type, ServiceDescriptor>();

        foreach (var serviceType in ServerOwnedServices)
        {
            // The manager is seeded in the shape the host registers it in, with an object of its
            // own, because it is the one service here a registration is later built from. The rest
            // carry a factory that refuses, which is the honest answer to a service nobody is about
            // to resolve: they are here to be counted, not to be built.
            seeded[serviceType] = serviceType == typeof(IMediaSourceManager)
                ? new ServiceDescriptor(serviceType, new RegistrationCoreManager())
                : new ServiceDescriptor(serviceType, _ => throw new NotSupportedException(), ServiceLifetime.Singleton);

            services.Add(seeded[serviceType]);
        }

        new PluginServiceRegistrator().RegisterServices(services, null!);

        foreach (var serverOwned in seeded)
        {
            // IMediaSourceManager is the exception this test exists to keep, and it is kept by the
            // assertions below rather than by this one: the server's descriptor stays, and the one
            // entry appended after it is the version filter and nothing else.
            if (serverOwned.Key != typeof(IMediaSourceManager))
            {
                Assert.Equal(1, services.Count(descriptor => descriptor.ServiceType == serverOwned.Key));
            }

            Assert.Contains(serverOwned.Value, services);
        }

        var managerRegistrations = services.Where(descriptor => descriptor.ServiceType == typeof(IMediaSourceManager)).ToArray();

        Assert.Equal(2, managerRegistrations.Length);
        Assert.Same(seeded[typeof(IMediaSourceManager)], managerRegistrations[0]);

        var decoration = managerRegistrations[1];
        Assert.Null(decoration.ImplementationType);
        Assert.Null(decoration.ImplementationInstance);
        Assert.NotNull(decoration.ImplementationFactory);
        Assert.Equal(ServiceLifetime.Singleton, decoration.Lifetime);

        var provider = new FakeServiceProvider(
            (typeof(IMvcSourceDetector), new MvcSourceDetector()),
            (typeof(IProfileCatalog), new ProfileCatalog()),
            (typeof(IAnaglyfinConfigurationSource), new StubConfigurationSource()),
            (typeof(ILogger<MediaSourceManagerSuppressionDecorator>), NullLogger<MediaSourceManagerSuppressionDecorator>.Instance));

        using var resolved = Assert.IsType<MediaSourceManagerSuppressionDecorator>(decoration.ImplementationFactory!(provider));

        // The approval is for a filter around the server's manager, not for a manager of Anaglyfin's
        // own: what the decoration answers with is the object the server registered.
        var wrapped = typeof(MediaSourceManagerSuppressionDecorator)
            .GetField("_core", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(resolved);

        Assert.Same(seeded[typeof(IMediaSourceManager)].ImplementationInstance, wrapped);

        // One more shadow is refused for the reason the first one is granted: the server registers
        // IHttpContextAccessor on the root container and the request pipeline feeds it, so a
        // plugin-side registration would answer the server's own helpers with Anaglyfin's object.
        // Anaglyfin's settings are user and library scoped, never request scoped - the exact-device
        // feature that once read a request claim was taken out before release - so there is nothing
        // here to register, and this keeps it that way.
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.Assembly == typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor).Assembly);
    }

    [Fact]
    public void TheRegisteredCatalogCanBuildItselfWithoutDependencies()
    {
        // The container activates the implementation type directly, so a constructor the
        // container cannot satisfy would fail plugin start-up rather than a single call.
        var catalog = (IProfileCatalog)Activator.CreateInstance(typeof(ProfileCatalog))!;

        Assert.NotEmpty(catalog.Profiles);
        Assert.True(catalog.IsKnownProfileId(ProfileIds.TwoDBase));
    }

    [Theory]
    [InlineData(typeof(IProfileVersionItemStore), typeof(LibraryProfileVersionItemStore))]
    [InlineData(typeof(ILibraryEventSource), typeof(LibraryEventSource))]
    [InlineData(typeof(IProfileVersionReconciler), typeof(ProfileVersionItemManager))]
    [InlineData(typeof(ProfileVersionReconcileQueue), typeof(ProfileVersionReconcileQueue))]
    [InlineData(typeof(IHostedService), typeof(ProfileVersionItemService))]
    public void RegisterServicesRegistersTheVersionItemSubsystemAsSingletons(Type serviceType, Type implementationType)
    {
        // Every one of these holds state the library depends on: the reconciler owns the gate that
        // keeps two passes from creating the same version twice, and the queue is the requests
        // themselves. A second instance of either is a reconcile that does not see the other's work.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var descriptor = Assert.Single(
            services,
            d => d.ServiceType == serviceType && d.ImplementationType == implementationType);

        Assert.Equal(implementationType, descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void RegisterServicesRegistersTheWrapperSettingsPublicationAsAHostedService()
    {
        // The document has to be refreshed on a restart, not only when an administrator saves the
        // page. Doing it as a hosted service keeps plugin construction free of settings reads.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var descriptor = Assert.Single(
            services,
            d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WrapperSettingsPublicationService));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void RegisterServicesRegistersTheSlotCleanupAsAHostedService()
    {
        // A slot left on storage that outlived the container which wrote it refuses every Anaglyfin
        // playback until somebody clears it, and a wrapper only ever looks at the directory when a
        // job asks for a slot. Sweeping it is therefore startup work, and startup work in a plugin is
        // a hosted service: doing it in the plugin constructor would touch the filesystem - and a
        // directory the deployment, not the plugin, chose - while the server is still loading.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var descriptor = Assert.Single(
            services,
            d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TranscodeSlotCleanupService));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void RegisterServicesHandsThePluginTheQueueAsItsOwnRequestInterface()
    {
        // The plugin may ask for work and nothing else, and the worker drains the same object. Two
        // queues - one to ask, one to answer - would mean a settings save that reconciles nothing.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IProfileVersionReconcileTrigger));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Null(descriptor.ImplementationType);
        Assert.NotNull(descriptor.ImplementationFactory);

        var queue = new ProfileVersionReconcileQueue();
        var resolved = descriptor.ImplementationFactory!(new FakeServiceProvider((typeof(ProfileVersionReconcileQueue), queue)));

        Assert.Same(queue, resolved);
        Assert.IsAssignableFrom<IProfileVersionReconcileTrigger>(resolved);
    }

    [Fact]
    public void RegisterServicesDoesNotInventAMediaSourceManagerToDecorate()
    {
        // The filter wraps the server's manager and cannot answer for the server's job on its own.
        // Without the earlier registration there is no list to filter and no object to wrap, so the
        // plugin leaves the service type to the server rather than answering it with a half feature.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IMediaSourceManager));
    }

    [Fact]
    public void RegisterServicesAppendsTheOriginalMvcVersionFilterAfterTheServersManager()
    {
        var services = new FakeServiceCollection();
        var core = new RegistrationCoreManager();
        services.Add(new ServiceDescriptor(typeof(IMediaSourceManager), core));

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var managerDescriptors = services.Where(descriptor => descriptor.ServiceType == typeof(IMediaSourceManager)).ToArray();

        Assert.Equal(2, managerDescriptors.Length);
        Assert.Same(core, Assert.Single(managerDescriptors, descriptor => descriptor.ImplementationInstance is not null).ImplementationInstance);

        var filterDescriptor = Assert.Single(
            managerDescriptors,
            descriptor => descriptor.ImplementationFactory is not null);

        Assert.Equal(ServiceLifetime.Singleton, filterDescriptor.Lifetime);
        Assert.Equal(typeof(IMediaSourceManager), filterDescriptor.ServiceType);
    }

    [Fact]
    public void RegisterServicesResolvesTheOriginalMvcVersionFilterOverTheRegisteredManager()
    {
        var services = new FakeServiceCollection();
        var core = new RegistrationCoreManager();
        services.Add(new ServiceDescriptor(typeof(IMediaSourceManager), core));

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var filterDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IMediaSourceManager) && descriptor.ImplementationFactory is not null);

        var provider = new FakeServiceProvider(
            (typeof(IMvcSourceDetector), new MvcSourceDetector()),
            (typeof(IProfileCatalog), new ProfileCatalog()),
            (typeof(IAnaglyfinConfigurationSource), new StubConfigurationSource()),
            (typeof(ILogger<MediaSourceManagerSuppressionDecorator>), NullLogger<MediaSourceManagerSuppressionDecorator>.Instance));

        var resolved = Assert.IsType<MediaSourceManagerSuppressionDecorator>(filterDescriptor.ImplementationFactory!(provider));
        var wrapped = typeof(MediaSourceManagerSuppressionDecorator)
            .GetField("_core", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(resolved);

        // A handover is not a build: the container still owns the instance behind the descriptor, so
        // the decorator takes the core out of the swap but leaves its disposal to its owner.
        Assert.Same(core, wrapped);
        resolved.Dispose();
        Assert.False(core.Disposed);
    }

    [Fact]
    public void RegisterServicesBuildsTheCoreManagerWhenTheServerRegisteredAType()
    {
        var services = new FakeServiceCollection();
        services.Add(new ServiceDescriptor(typeof(IMediaSourceManager), typeof(RegistrationCoreManager), ServiceLifetime.Singleton));

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var filterDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IMediaSourceManager) && descriptor.ImplementationFactory is not null);

        var provider = new FakeServiceProvider(
            (typeof(IMvcSourceDetector), new MvcSourceDetector()),
            (typeof(IProfileCatalog), new ProfileCatalog()),
            (typeof(IAnaglyfinConfigurationSource), new StubConfigurationSource()),
            (typeof(ILogger<MediaSourceManagerSuppressionDecorator>), NullLogger<MediaSourceManagerSuppressionDecorator>.Instance));

        var resolved = Assert.IsType<MediaSourceManagerSuppressionDecorator>(filterDescriptor.ImplementationFactory!(provider));
        var wrapped = Assert.IsType<RegistrationCoreManager>(
            typeof(MediaSourceManagerSuppressionDecorator)
                .GetField("_core", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(resolved));

        // Only the last registration is ever activated, so the decorator must build the object the
        // container stopped building - and take over disposing the live-stream closer along with it.
        resolved.Dispose();
        Assert.True(wrapped.Disposed);
    }

    [Fact]
    public void RegisterServicesDoesNotDecorateTheOriginalMvcVersionFilterTwice()
    {
        // The filter is appended as a factory, which is the one registration shape with no type to
        // name, so a second pass over the same collection has to recognise it by the delegate it
        // carries. The shapes that do name a type - a build that registered the decorator itself, or
        // a host that handed one over - are recognised by that type. A shape the guard fails to
        // answer is a filter wrapped in a filter: a settings read and a drop list twice over on every
        // request, and a core manager with two owners of its disposal.
        var registrator = new PluginServiceRegistrator();
        var core = new RegistrationCoreManager();

        var appended = new FakeServiceCollection();
        appended.Add(new ServiceDescriptor(typeof(IMediaSourceManager), core));

        registrator.RegisterServices(appended, null!);
        registrator.RegisterServices(appended, null!);

        // The server's manager and one filter after it: the second pass found its own work already
        // there and added nothing.
        var appendedManagers = OriginalMvcVersionFilterRegistrations(appended);

        Assert.Equal(2, appendedManagers.Length);
        Assert.Same(core, Assert.Single(appendedManagers, d => d.ImplementationInstance is not null).ImplementationInstance);
        Assert.Single(appendedManagers, d => d.ImplementationFactory is not null);

        var typed = new FakeServiceCollection();
        typed.Add(new ServiceDescriptor(typeof(IMediaSourceManager), core));
        typed.Add(new ServiceDescriptor(
            typeof(IMediaSourceManager),
            typeof(MediaSourceManagerSuppressionDecorator),
            ServiceLifetime.Singleton));

        registrator.RegisterServices(typed, null!);

        // Nothing to decorate that is not already decorated, so nothing was appended: what the
        // container answers with is the decorator it was given, once.
        Assert.Equal(2, OriginalMvcVersionFilterRegistrations(typed).Length);

        var handedOver = new FakeServiceCollection();
        handedOver.Add(new ServiceDescriptor(typeof(IMediaSourceManager), core));
        handedOver.Add(new ServiceDescriptor(
            typeof(IMediaSourceManager),
            new MediaSourceManagerSuppressionDecorator(
                core,
                ownsCore: false,
                new MvcSourceDetector(),
                new ProfileCatalog(),
                new StubConfigurationSource(),
                NullLogger<MediaSourceManagerSuppressionDecorator>.Instance)));

        registrator.RegisterServices(handedOver, null!);

        Assert.Equal(2, OriginalMvcVersionFilterRegistrations(handedOver).Length);
    }

    /// <summary>
    /// The registrations answering <see cref="IMediaSourceManager"/>, in the order the container
    /// would walk them.
    /// </summary>
    private static ServiceDescriptor[] OriginalMvcVersionFilterRegistrations(FakeServiceCollection services)
        => services.Where(descriptor => descriptor.ServiceType == typeof(IMediaSourceManager)).ToArray();

    /// <summary>
    /// A representative set of the services the server registers for its own use, out of the one
    /// assembly the plugin is written against.
    /// </summary>
    /// <remarks>
    /// Representative, not exhaustive: the rule they are used to state is about any service the
    /// server owns. Contracts the server fills from many registrations - <see cref="IHostedService"/>,
    /// the media source providers, the registrators themselves - are not on it, because appending to
    /// one of those adds a contributor rather than shadowing an instance, and each has its own test
    /// here.
    /// </remarks>
    private static Type[] ServerOwnedServices { get; } =
    [
        typeof(IMediaSourceManager),
        typeof(ILibraryManager),
        typeof(ILibraryMonitor),
        typeof(IUserManager),
        typeof(ISessionManager),
        typeof(IDeviceManager),
        typeof(IItemRepository),
        typeof(IServerApplicationHost)
    ];

    /// <summary>
    /// A container that knows exactly the services a test hands it, so a registration's factory can
    /// be run without the container implementation the plugin deliberately does not reference.
    /// </summary>
    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services;

        public FakeServiceProvider(params (Type ServiceType, object Instance)[] services)
            => _services = services.ToDictionary(service => service.ServiceType, service => service.Instance);

        public object? GetService(Type serviceType)
            => _services.TryGetValue(serviceType, out var service) ? service : null;
    }

    /// <summary>
    /// The server's manager as the registrator sees it: an object shape that may be handed over or
    /// built from a type, plus the disposal answer the decorator has to take over when it builds it.
    /// </summary>
    private sealed class RegistrationCoreManager : IMediaSourceManager, IDisposable
    {
        public bool Disposed { get; private set; }

        public void AddParts(IEnumerable<IMediaSourceProvider> providers)
            => throw new NotSupportedException();

        public void Dispose() => Disposed = true;

        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
            => throw new NotSupportedException();

        public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
            => throw new NotSupportedException();

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
            => throw new NotSupportedException();

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
            => throw new NotSupportedException();

        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
            BaseItem item,
            User? user,
            bool allowMediaProbe,
            bool enablePathSubstitution,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MediaSourceInfo> GetMediaSource(
            BaseItem item,
            string mediaSourceId,
            string liveStreamId,
            bool enablePathSubstitution,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
            LiveStreamRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(
            string id,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ILiveStream GetLiveStreamInfo(string id)
            => throw new NotSupportedException();

        public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
            ActiveRecordingInfo info,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CloseLiveStream(string id)
            => throw new NotSupportedException();

        public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public bool SupportsDirectStream(string path, MediaProtocol protocol)
            => throw new NotSupportedException();

        public MediaProtocol GetPathProtocol(string path)
            => throw new NotSupportedException();

        public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User? user)
            => throw new NotSupportedException();

        public Task AddMediaInfoWithProbe(
            MediaSourceInfo mediaSource,
            bool isAudio,
            string? cacheKey,
            bool addProbeDelay,
            bool isLiveStream,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
