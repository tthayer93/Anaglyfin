namespace Anaglyfin.Configuration;

/// <summary>
/// How Anaglyfin asks FFmpeg-mvc to place subtitles in depth.
/// </summary>
/// <remarks>
/// <para>
/// The three names are the three readings FFmpeg-mvc's <c>mvcsubdepth</c> filter answers
/// to, narrowed to the ones this product is willing to promise an administrator:
/// <c>depth=auto</c>, <c>depth=shift=&lt;pixels&gt;</c> and <c>depth=plane=&lt;0..31&gt;</c>.
/// The filter's fourth spelling - <c>depth=flat</c> - is what Anaglyfin produces by leaving
/// the feature switched off, so it is not a mode a settings file can name: one knob with
/// one off position is one knob.
/// </para>
/// <para>
/// A mode is only meaningful while <see cref="PluginConfiguration.SubtitleDepthEnabled"/>
/// is on, and only meaningful on a build whose FFmpeg carries the filter: the settings
/// record what an administrator asked for, and nothing here claims the binary honours it.
/// </para>
/// </remarks>
public enum SubtitleDepthMode
{
    /// <summary>
    /// Take the depth the disc authored for the subtitle's depth sequence
    /// (<c>depth=auto</c>). The reading that needs no number.
    /// </summary>
    Automatic = 0,

    /// <summary>
    /// Move every caption by one constant horizontal eye shift
    /// (<c>depth=shift=&lt;pixels&gt;</c>), held in
    /// <see cref="PluginConfiguration.SubtitleDepthShift"/>.
    /// </summary>
    ConstantShift = 1,

    /// <summary>
    /// Read one of the authored depth sequences directly
    /// (<c>depth=plane=&lt;0..31&gt;</c>), the sequence index held in
    /// <see cref="PluginConfiguration.SubtitleDepthPlane"/>.
    /// </summary>
    Plane = 2
}
