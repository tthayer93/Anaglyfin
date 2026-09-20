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

    /// <summary>
    /// Reads the subtitle depth Anaglyfin acts on.
    /// </summary>
    /// <param name="configuration">The persisted settings.</param>
    /// <returns>
    /// The stored request when it is a request the FFmpeg-mvc filter can honour, and
    /// <see cref="SubtitleDepthSettings.Disabled"/> when it is not.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Three rules, in the order a reader can check them:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// The single mode is read as it stands. <see cref="SubtitleDepthMode.Flat"/> is the off
    /// position and answers with <see cref="SubtitleDepthSettings.Disabled"/>, so the one knob
    /// has one off position and there is nothing else to consult.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// The mode decides which number matters, and only that number is read. The other one
    /// stays on the page for the next time its mode is picked, but it never travels.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// A number outside what the filter honours, or a mode no name declares, is answered
    /// with <see cref="SubtitleDepthSettings.Disabled"/> rather than with a clamp. Depth is
    /// an enhancement: a settings file that cannot state it is a settings file that does not
    /// want it, and the flat result is the one that is certain to play.
    /// </description>
    /// </item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    public static SubtitleDepthSettings GetEffectiveSubtitleDepth(this PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.SubtitleDepthMode switch
        {
            SubtitleDepthMode.Automatic
                => new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0),

            SubtitleDepthMode.ConstantShift when SubtitleDepthSettings.IsShiftInRange(configuration.SubtitleDepthShift)
                => new SubtitleDepthSettings(true, SubtitleDepthMode.ConstantShift, configuration.SubtitleDepthShift, 0),

            SubtitleDepthMode.Plane when SubtitleDepthSettings.IsPlaneInRange(configuration.SubtitleDepthPlane)
                => new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, configuration.SubtitleDepthPlane),

            // Flat is the off position, and a number the filter would clamp or refuse is a
            // request the settings cannot state: the feature is off, and the stored numbers are
            // left where they are for whoever picks a mode that can carry them.
            _ => SubtitleDepthSettings.Disabled
        };
    }
}
