using System;
using Anaglyfin.Configuration;
using MediaBrowser.Common.Plugins;

namespace Anaglyfin.MediaSources;

/// <summary>
/// Reads the live settings off the plugin instance the server's plugin manager holds.
/// </summary>
/// <remarks>
/// <para>
/// The plugin manager is the only container-visible route to the plugin instance
/// (the server creates that instance itself, and an <c>IPluginServiceRegistrator</c>
/// runs before it exists), so this source resolves it by the plugin's fixed id and
/// type-tests its configuration. No plugin static is involved, which keeps the
/// lookup honest under DI and testable without a server.
/// </para>
/// <para>
/// Every failure to find the settings - plugin not loaded yet, settings file of a
/// type this build does not know, configuration not loaded - yields a fresh default
/// configuration: the callers sit on the playback path, and the shipped defaults
/// (red/cyan first, 2D base as fallback, the MVP profile set) are a correct answer
/// for an installation nobody has configured. They are never a substitute for a
/// loaded one: the live object is returned by reference, so an administrator's
/// change is seen by the next call.
/// </para>
/// </remarks>
public sealed class PluginConfigurationSource : IAnaglyfinConfigurationSource
{
    private readonly IPluginManager _pluginManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationSource"/> class.
    /// </summary>
    /// <param name="pluginManager">The server's plugin registry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pluginManager"/> is <c>null</c>.</exception>
    public PluginConfigurationSource(IPluginManager pluginManager)
    {
        _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
    }

    /// <inheritdoc />
    public PluginConfiguration GetConfiguration()
    {
        // Instance (not the LocalPlugin record) is the live plugin object; the
        // property pattern also rejects a null Configuration, which the plugin base
        // can hand back while its settings file has not loaded.
        if (_pluginManager.GetPlugin(Plugin.PluginId)?.Instance
            is IHasPluginConfiguration { Configuration: PluginConfiguration configuration })
        {
            return configuration;
        }

        return new PluginConfiguration();
    }
}
