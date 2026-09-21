using System.Collections.Generic;
using Anaglyfin.Profiles;
using MediaBrowser.Model.Plugins;

namespace Anaglyfin.Configuration;

/// <summary>
/// Persisted Anaglyfin settings.
/// </summary>
/// <remarks>
/// <para>
/// The settings type is deliberately made of ids, numbers, enum values and short
/// strings - no filtergraphs, no paths and no free form conversion description. The
/// profile catalog interprets the ids on every read, so a settings file written by an
/// older plugin, edited by hand or written against a profile that no longer exists can
/// never reach FFmpeg with anything the catalog does not recognise.
/// </para>
/// <para>
/// Persisted as XML by the server and exchanged with the admin UI as JSON. Both
/// serialisers are happy with the shapes used here: read only collections with a
/// public add method, enums by name and primitives. Every property keeps a default
/// that describes an installation that has never been configured, which is what lets a
/// settings file from an earlier build load unchanged.
/// </para>
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Default value of <see cref="MaxConcurrentTranscodes"/>: one Anaglyfin job at a
    /// time, because MVC decoding is software work on every supported build.
    /// </summary>
    public const int DefaultMaxConcurrentTranscodes = 1;

    /// <summary>
    /// Gets or sets the profile offered first to every client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to the red/cyan Dubois anaglyph, the general purpose 3D glasses choice.
    /// The value is a profile id and is resolved through the profile catalog. This is the
    /// only default profile setting: the same default orders the offer for every client,
    /// device and context. Exact-device default overrides existed on a pre-release
    /// development branch and were removed before release; a settings file that still
    /// carries their element loads unchanged because the serialiser ignores what it
    /// cannot place, the entries decide nothing, and the next save drops the element.
    /// </para>
    /// <para>
    /// There is no separate fallback setting: when the default cannot be served - it is
    /// unknown, or the administrator disabled it - the profile catalog offers the first
    /// enabled profile in display order. Fewer knobs means fewer ways for a settings file
    /// to describe a version nobody enabled.
    /// </para>
    /// </remarks>
    public string DefaultProfileId { get; set; } = ProfileIds.AnaglyphRedCyanDubois;

    /// <summary>
    /// Gets or sets the profile ids the administrator wants offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty means "no explicit selection", which the profile catalog answers with the
    /// shipped default set (red/cyan Dubois, full SBS, half SBS, 2D base). Keeping the
    /// persisted default empty is also what makes a settings file load cleanly: the XML
    /// serialiser adds stored ids to the collection the constructor created, so a
    /// prefilled default would come back duplicated after every restart.
    /// </para>
    /// <para>
    /// Unknown ids are ignored rather than rejected, so a settings file that mentions a
    /// profile from a newer build does not break playback.
    /// </para>
    /// </remarks>
    public List<string> EnabledProfileIds { get; set; } = new();

    /// <summary>
    /// Gets or sets how many Anaglyfin transcodes may run at the same time across the
    /// server.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="PluginConfigurationExtensions.GetEffectiveMaxConcurrentTranscodes"/>,
    /// which clamps nonsensical values to the default of one. Enforced by the FFmpeg
    /// wrapper, not by the settings layer.
    /// </remarks>
    public int MaxConcurrentTranscodes { get; set; } = DefaultMaxConcurrentTranscodes;

    /// <summary>
    /// Gets or sets the colour the custom grayscale anaglyph tints the left eye with,
    /// as <c>#RRGGBB</c> text.
    /// </summary>
    /// <remarks>
    /// Text is only the storage format: the profile catalog parses it into a validated
    /// <see cref="RgbColor"/> and uses the shipped default when it does not parse, so
    /// nothing but three bytes in <c>0..255</c> can travel towards an encoder.
    /// </remarks>
    public string CustomLeftEyeColor { get; set; } = ProfileCatalog.DefaultCustomLeftEyeColor.ToHexString();

    /// <summary>
    /// Gets or sets the colour the custom grayscale anaglyph tints the right eye with,
    /// as <c>#RRGGBB</c> text.
    /// </summary>
    /// <remarks>
    /// Text is only the storage format: the profile catalog parses it into a validated
    /// <see cref="RgbColor"/> and uses the shipped default when it does not parse, so
    /// nothing but three bytes in <c>0..255</c> can travel towards an encoder.
    /// </remarks>
    public string CustomRightEyeColor { get; set; } = ProfileCatalog.DefaultCustomRightEyeColor.ToHexString();

    /// <summary>
    /// Gets or sets the subtitle depth asked for: <see cref="SubtitleDepthMode.Automatic"/>,
    /// <see cref="SubtitleDepthMode.ConstantShift"/>, <see cref="SubtitleDepthMode.Plane"/> or
    /// <see cref="SubtitleDepthMode.Flat"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One dropdown replaced the old enable switch and mode picker: Flat is now the position
    /// that switches the feature off, so there is a single knob with a single off position.
    /// <see cref="SubtitleDepthMode.Flat"/> asks for nothing and no <c>mvcsubdepth</c> stage is
    /// placed in the graph.
    /// </para>
    /// <para>
    /// The shipped default is <see cref="SubtitleDepthMode.Automatic"/>. A settings file that
    /// predates the feature - or that had the old switch unticked - is migrated to
    /// <see cref="SubtitleDepthMode.Flat"/> on load by <see cref="Plugin"/> rather than left on
    /// this default, so an upgrade never starts moving captions nobody asked it to.
    /// </para>
    /// <para>
    /// Nothing here asserts that the configured FFmpeg offers the filter: the setting records the
    /// request, and the deployment is what names the binary that has to honour it. A server whose
    /// FFmpeg has no depth support plays the captions flat whatever this says.
    /// </para>
    /// </remarks>
    public SubtitleDepthMode SubtitleDepthMode { get; set; } = SubtitleDepthMode.Automatic;

    /// <summary>
    /// Gets or sets the constant eye shift asked for in
    /// <see cref="Anaglyfin.Configuration.SubtitleDepthMode.ConstantShift"/> mode, in native picture
    /// pixels, positive toward the viewer.
    /// </summary>
    /// <remarks>
    /// Stored as typed, and only honoured inside
    /// <see cref="SubtitleDepthSettings.MinShiftPixels"/>..<see cref="SubtitleDepthSettings.MaxShiftPixels"/>
    /// - the distance FFmpeg-mvc travels without clamping - which
    /// <see cref="PluginConfigurationExtensions.GetEffectiveSubtitleDepth"/> enforces. The
    /// admin page restricts entry to the same range; the check is not on the page because
    /// the page is not the only way a settings file gets written.
    /// </remarks>
    public int SubtitleDepthShift { get; set; }

    /// <summary>
    /// Gets or sets the authored depth sequence index read directly in
    /// <see cref="Configuration.SubtitleDepthMode.Plane"/> mode.
    /// </summary>
    /// <remarks>
    /// Only honoured inside <see cref="SubtitleDepthSettings.MinPlane"/>..
    /// <see cref="SubtitleDepthSettings.MaxPlane"/>, the range an offset-metadata table can
    /// hold, and ignored in every other mode.
    /// </remarks>
    public int SubtitleDepthPlane { get; set; }
}
