using System;

namespace Anaglyfin.Configuration;

/// <summary>
/// An administrator's default profile choice for one exact registered device.
/// </summary>
/// <remarks>
/// <para>
/// Entries are stored in the plugin settings so that one device - the TV behind the
/// sofa, say - can start on the anaglyph version while every other client still
/// starts on the global default. An entry that names no device is inert: it matches
/// nothing.
/// </para>
/// <para>
/// Only the profile id is persisted, and it is checked against the profile allowlist
/// whenever settings are read, so an entry pointing at a profile this build does not
/// know is ignored instead of being interpreted.
/// </para>
/// <para>
/// Matching is by device id alone, and device ids are treated as opaque strings:
/// they are self-reported by the client, so this entry decides which versions a
/// device is offered first and authorises nothing. Client names are deliberately not
/// a key - they are free text that changes between releases of the same app, and an
/// approximate match on them would promise a reliability the platform does not have.
/// Approximate device categories (TV, headset, projector) are deferred future work;
/// nothing here infers one.
/// </para>
/// <para>
/// The class and its stored element spellings are kept exactly as the build that
/// also stored a <c>&lt;ClientName&gt;</c> element wrote them: the XML serialiser
/// drops the element it cannot place, so an already-saved settings file still loads.
/// No disk migration is needed or performed.
/// </para>
/// </remarks>
public class DeviceProfileDefault
{
    /// <summary>
    /// Gets or sets the Jellyfin device id this entry applies to.
    /// </summary>
    /// <remarks>
    /// Empty means the entry does not pin a device, and an entry that pins no device
    /// matches no request.
    /// </remarks>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the profile id to offer this device by default.
    /// </summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Scores how well this entry matches a playback request.
    /// </summary>
    /// <param name="deviceId">The device id of the request, if known.</param>
    /// <returns>
    /// <c>0</c> when the entry does not apply and <c>1</c> when it pins a device and
    /// the request comes from that exact device. One device entry cannot outrank
    /// another, so among entries pinned to one device the settings order decides and
    /// the first one wins (see <c>Anaglyfin.Profiles.ProfileCatalog</c>).
    /// </returns>
    /// <remarks>
    /// Comparison ignores surrounding whitespace and letter case. An entry that pins
    /// no device can never match, so an accidentally empty row in the settings file
    /// cannot hijack every request.
    /// </remarks>
    public int MatchStrength(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(DeviceId))
        {
            return 0;
        }

        return ValueMatches(DeviceId, deviceId) ? 1 : 0;
    }

    /// <summary>
    /// Checks whether this entry applies to a playback request.
    /// </summary>
    /// <param name="deviceId">The device id of the request, if known.</param>
    /// <returns><c>true</c> when the entry applies.</returns>
    public bool Matches(string? deviceId)
        => MatchStrength(deviceId) > 0;

    private static bool ValueMatches(string pinned, string? candidate)
        => !string.IsNullOrWhiteSpace(candidate)
            && string.Equals(pinned.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase);
}
