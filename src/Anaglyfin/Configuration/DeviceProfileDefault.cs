using System;

namespace Anaglyfin.Configuration;

/// <summary>
/// An administrator's default profile choice for one device or one client.
/// </summary>
/// <remarks>
/// <para>
/// Entries are stored in the plugin settings so that a TV client can get the anaglyph
/// version by default while a web browser gets side-by-side. Both keys are optional,
/// but an entry that pins neither is inert: it matches nothing.
/// </para>
/// <para>
/// Only the profile id is persisted, and it is checked against the profile allowlist
/// whenever settings are read, so an entry pointing at a profile this build does not
/// know is ignored instead of being interpreted.
/// </para>
/// </remarks>
public class DeviceProfileDefault
{
    /// <summary>
    /// Gets or sets the Jellyfin device id this entry applies to.
    /// </summary>
    /// <remarks>
    /// Empty means the entry does not pin a device.
    /// </remarks>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client name this entry applies to, for example <c>Web</c> or
    /// <c>AndroidTV</c>.
    /// </summary>
    /// <remarks>
    /// Empty means the entry does not pin a client.
    /// </remarks>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the profile id to offer this device or client by default.
    /// </summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Scores how well this entry matches a playback request.
    /// </summary>
    /// <param name="deviceId">The device id of the request, if known.</param>
    /// <param name="clientName">The client name of the request, if known.</param>
    /// <returns>
    /// <c>0</c> when the entry does not apply, <c>1</c> when it pins one field and that
    /// field matches, <c>2</c> when it pins both and both match. A higher score wins,
    /// which lets a device specific entry beat a client wide one.
    /// </returns>
    /// <remarks>
    /// Comparison ignores surrounding whitespace and letter case. An entry that pins
    /// neither device nor client can never match, so an accidentally empty row in the
    /// settings file cannot hijack every request.
    /// </remarks>
    public int MatchStrength(string? deviceId, string? clientName)
    {
        var pinsDevice = !string.IsNullOrWhiteSpace(DeviceId);
        var pinsClient = !string.IsNullOrWhiteSpace(ClientName);

        if (!pinsDevice && !pinsClient)
        {
            return 0;
        }

        var strength = 0;

        if (pinsDevice)
        {
            if (!ValueMatches(DeviceId, deviceId))
            {
                return 0;
            }

            strength++;
        }

        if (pinsClient)
        {
            if (!ValueMatches(ClientName, clientName))
            {
                return 0;
            }

            strength++;
        }

        return strength;
    }

    /// <summary>
    /// Checks whether this entry applies to a playback request.
    /// </summary>
    /// <param name="deviceId">The device id of the request, if known.</param>
    /// <param name="clientName">The client name of the request, if known.</param>
    /// <returns><c>true</c> when the entry applies.</returns>
    public bool Matches(string? deviceId, string? clientName)
        => MatchStrength(deviceId, clientName) > 0;

    private static bool ValueMatches(string pinned, string? candidate)
        => !string.IsNullOrWhiteSpace(candidate)
            && string.Equals(pinned.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase);
}
