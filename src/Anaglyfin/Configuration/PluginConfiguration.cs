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
    /// Gets or sets the profile offered to clients that have no device or client
    /// override.
    /// </summary>
    /// <remarks>
    /// Defaults to the red/cyan Dubois anaglyph, the general purpose 3D glasses choice.
    /// The value is a profile id and is resolved through the profile catalog.
    /// </remarks>
    public string DefaultProfileId { get; set; } = ProfileIds.AnaglyphRedCyanDubois;

    /// <summary>
    /// Gets or sets the profile to fall back to when the default cannot be served.
    /// </summary>
    /// <remarks>
    /// Defaults to the plain 2D base view, the version that plays everywhere.
    /// </remarks>
    public string FallbackProfileId { get; set; } = ProfileIds.TwoDBase;

    /// <summary>
    /// Gets or sets the per device and per client default profile overrides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty by default: a fresh installation has one global default. Entries are
    /// evaluated by <see cref="DeviceProfileDefault.MatchStrength"/> and their profile
    /// ids are only honoured when enabled in <see cref="EnabledProfileIds"/>.
    /// </para>
    /// <para>
    /// The setter exists because both settings serialisers the server uses replace the
    /// collection when they load a value: the XML serialiser will only assign a property
    /// it can set, and the admin UI endpoint deserialises JSON into a fresh instance.
    /// </para>
    /// </remarks>
    public List<DeviceProfileDefault> DeviceDefaultProfiles { get; set; } = new();

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
    /// Gets or sets the encoder selection policy for Anaglyfin profile jobs.
    /// </summary>
    public VideoEncoderPolicy EncoderPolicy { get; set; } = VideoEncoderPolicy.Automatic;

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
}
