using System;

namespace Anaglyfin.Configuration;

/// <summary>
/// Read side helpers for <see cref="PluginConfiguration"/>.
/// </summary>
/// <remarks>
/// The settings object stores what the administrator saved, including values that make
/// no sense. Normalising them here keeps the storage shape stable (so old settings files
/// keep loading) while every consumer reads a value it can act on.
/// </remarks>
public static class PluginConfigurationExtensions
{
    /// <summary>
    /// Reads the concurrency limit Anaglyfin must enforce.
    /// </summary>
    /// <param name="configuration">The persisted settings.</param>
    /// <returns>
    /// <see cref="PluginConfiguration.MaxConcurrentTranscodes"/> when it is at least one,
    /// otherwise <see cref="PluginConfiguration.DefaultMaxConcurrentTranscodes"/>.
    /// </returns>
    /// <remarks>
    /// A zero or negative limit is treated as the default rather than as "unlimited" or
    /// "block everything": a corrupted settings file must not be able to stop playback or
    /// to launch an unbounded number of software MVC decodes.
    /// </remarks>
    public static int GetEffectiveMaxConcurrentTranscodes(this PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.MaxConcurrentTranscodes < 1
            ? PluginConfiguration.DefaultMaxConcurrentTranscodes
            : configuration.MaxConcurrentTranscodes;
    }
}
