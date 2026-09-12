using System;
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
    public void RegisterServicesRegistersTheProfileCatalogAsASingleton()
    {
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var descriptor = Assert.Single(services);
        Assert.Equal(typeof(IProfileCatalog), descriptor.ServiceType);
        Assert.Equal(typeof(ProfileCatalog), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void RegisterServicesDoesNotRegisterAMediaSourceProviderYet()
    {
        // Media source providers are discovered by type scan; registering one here would
        // make it invisible to that scan. Profiles and settings are all T1 may register.
        var services = new FakeServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.Name.Contains("MediaSource", StringComparison.Ordinal));
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
