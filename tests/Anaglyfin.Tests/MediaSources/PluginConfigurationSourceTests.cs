using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.MediaSources;
using Anaglyfin.Tests.Stubs;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Anaglyfin.Tests.MediaSources;

/// <summary>
/// Covers the settings seam the media source provider reads through: it must hand out
/// the plugin instance's live configuration and degrade to shipped defaults when that
/// instance is not there.
/// </summary>
public class PluginConfigurationSourceTests
{
    [Fact]
    public void ConstructorRejectsAMissingPluginManager()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginConfigurationSource(null!));
    }

    [Fact]
    public void ReturnsTheLiveConfigurationOfThePluginInstance()
    {
        var plugin = new FakeAnaglyfinPlugin();
        var source = new PluginConfigurationSource(new StubPluginManager(plugin));

        var read = source.GetConfiguration();

        // Same object, not a copy: an administrator's saved change must be visible to
        // the next read without any reload.
        Assert.Same(plugin.Configuration, read);

        plugin.Configuration.DefaultProfileId = "changed-by-the-admin";
        Assert.Equal("changed-by-the-admin", source.GetConfiguration().DefaultProfileId);
    }

    [Fact]
    public void ReturnsShippedDefaultsWhenThePluginIsNotInstalledYet()
    {
        var source = new PluginConfigurationSource(new StubPluginManager(plugin: null));

        var configuration = source.GetConfiguration();

        Assert.NotNull(configuration);
        Assert.Equal(new PluginConfiguration().DefaultProfileId, configuration.DefaultProfileId);
    }

    [Fact]
    public void ReturnsShippedDefaultsForAPluginWithoutOurSettingsShape()
    {
        // A plugin instance whose Configuration is null (settings file not loaded) or
        // of a foreign type must not poison readers with nulls or foreign objects.
        var source = new PluginConfigurationSource(new StubPluginManager(new FakeAnaglyfinPlugin { Configuration = null! }));

        Assert.NotNull(source.GetConfiguration());
    }

    /// <summary>
    /// Carries the settings object the way <c>BasePlugin</c> does: a public property
    /// exposed through <see cref="IHasPluginConfiguration"/>.
    /// </summary>
    private sealed class FakeAnaglyfinPlugin : IPlugin, IHasPluginConfiguration
    {
        public PluginConfiguration Configuration { get; set; } = new();

        public string Name => "Anaglyfin";

        public string Description => "test";

        public Guid Id => Plugin.PluginId;

        public Version Version => typeof(Plugin).Assembly.GetName().Version!;

        public string AssemblyFilePath => "Anaglyfin.dll";

        public bool CanUninstall => true;

        public string DataFolderPath => new FakeApplicationPaths().PluginsPath;

        public Type ConfigurationType => typeof(PluginConfiguration);

        BasePluginConfiguration IHasPluginConfiguration.Configuration => Configuration;

        public void UpdateConfiguration(BasePluginConfiguration configuration)
            => Configuration = (PluginConfiguration)configuration;

        public PluginInfo GetPluginInfo()
            => new(Name, Version, Description, Id, CanUninstall);

        public void OnUninstalling()
        {
        }
    }

    /// <summary>
    /// An <see cref="IPluginManager"/> that knows exactly one plugin instance, the one
    /// the test is about. Everything else the interface offers is out of scope here and
    /// fails fast.
    /// </summary>
    private sealed class StubPluginManager : IPluginManager
    {
        private readonly FakeAnaglyfinPlugin? _plugin;

        public StubPluginManager(FakeAnaglyfinPlugin? plugin)
            => _plugin = plugin;

        public IReadOnlyList<LocalPlugin> Plugins => Array.Empty<LocalPlugin>();

        public LocalPlugin? GetPlugin(Guid id, Version? version = null)
        {
            if (_plugin is null || id != Plugin.PluginId)
            {
                return null;
            }

            return new LocalPlugin("/plugins/anaglyfin", isSupported: true, new PluginManifest
            {
                Id = Plugin.PluginId,
                Name = "Anaglyfin",
                Version = "0.1.0",
                Description = "test",
                Status = PluginStatus.Active
            })
            {
                Instance = _plugin
            };
        }

        public void CreatePlugins()
            => throw new NotSupportedException();

        public IEnumerable<Assembly> LoadAssemblies()
            => throw new NotSupportedException();

        public void RegisterServices(IServiceCollection serviceCollection)
            => throw new NotSupportedException();

        public Task<bool> PopulateManifest(PackageInfo packageInfo, Version version, string path, PluginStatus status)
            => throw new NotSupportedException();

        public bool SaveManifest(PluginManifest manifest, string path)
            => throw new NotSupportedException();

        public void ImportPluginFrom(string folder)
            => throw new NotSupportedException();

        public void FailPlugin(Assembly assembly)
            => throw new NotSupportedException();

        public void DisablePlugin(LocalPlugin plugin)
            => throw new NotSupportedException();

        public void EnablePlugin(LocalPlugin plugin)
            => throw new NotSupportedException();

        public bool RemovePlugin(LocalPlugin plugin)
            => throw new NotSupportedException();
    }
}
