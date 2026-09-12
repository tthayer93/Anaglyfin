using System;
using MediaBrowser.Controller.Plugins;
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
}
