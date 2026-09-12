using Anaglyfin.Configuration;

namespace Anaglyfin.MediaSources;

/// <summary>
/// Reads the live Anaglyfin settings at the moment a caller needs them.
/// </summary>
/// <remarks>
/// <para>
/// The settings object is owned by the plugin instance, which the server - not the
/// DI container - constructs, so consumers that live in the container (the media
/// source provider is one) cannot be handed <c>Plugin.Instance.Configuration</c>
/// directly. This seam names exactly what they need - the current settings - and
/// nothing else, which keeps those consumers unit testable without a server and
/// keeps every reader on the live object so an administrator's change is visible
/// to the next playback request without a restart.
/// </para>
/// <para>
/// Implementations must be cheap: <see cref="GetConfiguration"/> is called on the
/// playback hot path, once per media-source enumeration.
/// </para>
/// </remarks>
public interface IAnaglyfinConfigurationSource
{
    /// <summary>
    /// Gets the settings the plugin is running with right now.
    /// </summary>
    /// <returns>
    /// Never <c>null</c>: an implementation that cannot find the plugin instance
    /// (during start-up, or after a settings-model change) returns the shipped
    /// defaults rather than failing the caller's playback path.
    /// </returns>
    PluginConfiguration GetConfiguration();
}
