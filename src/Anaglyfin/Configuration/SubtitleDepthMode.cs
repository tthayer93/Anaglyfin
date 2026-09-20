namespace Anaglyfin.Configuration;

/// <summary>
/// How Anaglyfin asks FFmpeg-mvc to place subtitles in depth.
/// </summary>
/// <remarks>
/// <para>
/// The four names are the readings an administrator can pick on the settings page. Three of
/// them are the readings FFmpeg-mvc's <c>mvcsubdepth</c> filter answers to, narrowed to the
/// ones this product is willing to promise: <c>depth=auto</c>, <c>depth=shift=&lt;pixels&gt;</c>
/// and <c>depth=plane=&lt;0..31&gt;</c>. The fourth, <see cref="Flat"/>, is the reading that
/// asks for nothing: it is the one dropdown position that leaves the filter out of the graph
/// entirely, and the position an installation lands on when it has never asked for depth.
/// </para>
/// <para>
/// Flat is named here rather than spelled <c>depth=flat</c> on the FFmpeg command line because
/// Anaglyfin has never produced that spelling and will not start now: the request is turned off
/// before it reaches the filter, so the graph simply has no <c>mvcsubdepth</c> stage - exactly
/// the graph a build without the feature would have run.
/// </para>
/// </remarks>
public enum SubtitleDepthMode
{
    /// <summary>
    /// Take the depth the disc authored for the subtitle's depth sequence
    /// (<c>depth=auto</c>). The reading that needs no number, and the shipped default.
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
    Plane = 2,

    /// <summary>
    /// Ask for no depth at all: subtitles stay on the screen plane, and no <c>mvcsubdepth</c>
    /// stage is placed in the graph. The one position that is switched off rather than narrowed.
    /// </summary>
    Flat = 3
}
