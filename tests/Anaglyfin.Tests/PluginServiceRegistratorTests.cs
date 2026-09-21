using System;
using System.Collections.Generic;
using System.Linq;
using Anaglyfin.Detection;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.Tests.Stubs;
using Anaglyfin.VersionItems;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        // hosted service that drains it), and the hosted service that publishes the wrapper settings
        // document at startup. A new service must be added here on purpose - this count is the review
        // gate against silent registrations.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        Assert.Equal(10, services.Count);
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
    public void RegisterServicesNeverShadowsAServerOwnedService()
    {
        // The provider reads the request's device claim through IHttpContextAccessor, which
        // the server itself registers (Startup adds it to the root container) and through
        // which the server's own helpers read the same claims. A plugin-side registration
        // would not merely be dead weight - it would risk replacing the server's accessor
        // with one the server's request pipeline does not feed. The provider is activated
        // with ActivatorUtilities, which resolves the server's registration directly, so
        // there is nothing for Anaglyfin to register.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

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
}
