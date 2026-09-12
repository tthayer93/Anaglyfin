using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using Anaglyfin.Tests.Stubs;
using MediaBrowser.Common.Plugins;
using Xunit;

namespace Anaglyfin.Tests;

/// <summary>
/// Verifies the plugin scaffold exposes what the Jellyfin server needs from it.
/// </summary>
public class PluginTests
{
    [Fact]
    public void PluginIsAJellyfinPluginWithAFixedIdentity()
    {
        var plugin = CreatePlugin();

        Assert.IsAssignableFrom<IPlugin>(plugin);
        Assert.NotEqual(Guid.Empty, Plugin.PluginId);
        Assert.Equal(Plugin.PluginId, plugin.Id);
        Assert.Equal("Anaglyfin", plugin.Name);
    }

    [Fact]
    public void PluginIdentityIsStableAcrossInstances()
    {
        Assert.Equal(CreatePlugin().Id, CreatePlugin().Id);
    }

    [Fact]
    public void PluginInfoIsPopulatedForServerConsumers()
    {
        var plugin = CreatePlugin();

        var info = plugin.GetPluginInfo();

        Assert.Equal("Anaglyfin", info.Name);
        Assert.Equal(Plugin.PluginId, info.Id);
        Assert.Equal(plugin.Description, info.Description);
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version, info.Version);

        // The server derives the settings file name from the plugin assembly name.
        var expectedConfigurationFile = Path.ChangeExtension($"{typeof(Plugin).Assembly.GetName().Name}.dll", ".xml");
        Assert.Equal(expectedConfigurationFile, info.ConfigurationFileName);
    }

    [Fact]
    public void PluginAssemblyTargetsTheNet10ServerLine()
    {
        var target = typeof(Plugin).Assembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.NotNull(target);
        Assert.Equal(".NETCoreApp,Version=v10.0", target!.FrameworkName);
    }

    [Fact]
    public void PluginAssemblyReferencesTheDeclaredJellyfinAbi()
    {
        var controller = typeof(Plugin).Assembly.GetReferencedAssemblies()
            .SingleOrDefault(assembly => string.Equals(assembly.Name, "MediaBrowser.Controller", StringComparison.Ordinal));

        Assert.NotNull(controller);
        Assert.Equal(12, controller!.Version?.Major ?? 0);
    }

    private static Plugin CreatePlugin()
        => new(new FakeApplicationPaths(), new FakeXmlSerializer());
}
