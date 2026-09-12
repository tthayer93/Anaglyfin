using System;
using Anaglyfin.Detection;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.Tests.Stubs;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
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
        // The deliberate set of registrations: the catalog, the detector the media
        // source provider runs on, and the settings seam. A new service must be added
        // here on purpose - this count is the review gate against silent registrations.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        Assert.Equal(3, services.Count);
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
    public void TheRegisteredCatalogCanBuildItselfWithoutDependencies()
    {
        // The container activates the implementation type directly, so a constructor the
        // container cannot satisfy would fail plugin start-up rather than a single call.
        var catalog = (IProfileCatalog)Activator.CreateInstance(typeof(ProfileCatalog))!;

        Assert.NotEmpty(catalog.Profiles);
        Assert.True(catalog.IsKnownProfileId(ProfileIds.TwoDBase));
    }
}
