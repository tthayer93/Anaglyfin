using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Anaglyfin.Configuration;

namespace Anaglyfin.Profiles;

/// <summary>
/// The authority on which conversions Anaglyfin offers and which one a client gets by
/// default.
/// </summary>
/// <remarks>
/// <para>
/// The catalog is the allowlist in service form: nothing outside it is a valid
/// profile, and every lookup takes untrusted text (a marker, a query string, a
/// settings file) and returns either a known profile or nothing.
/// </para>
/// <para>
/// Resolution methods never throw on bad configuration. A settings file that names a
/// removed, disabled or mistyped profile degrades to another enabled profile rather
/// than failing a playback request, because the caller is the playback path.
/// </para>
/// </remarks>
public interface IProfileCatalog
{
    /// <summary>
    /// Gets every known profile in display order, with the shipped custom colour
    /// defaults on the custom profile.
    /// </summary>
    IReadOnlyList<StereoProfile> Profiles { get; }

    /// <summary>
    /// Gets every known profile id in display order.
    /// </summary>
    /// <remarks>
    /// Named <c>AllProfileIds</c> rather than <c>ProfileIds</c> to stay readable next
    /// to the <c>ProfileIds</c> allowlist type.
    /// </remarks>
    IReadOnlyList<string> AllProfileIds { get; }

    /// <summary>
    /// Checks untrusted text against the profile allowlist.
    /// </summary>
    /// <param name="profileId">The candidate id. May be null.</param>
    /// <returns><c>true</c> when the value is a known profile id.</returns>
    bool IsKnownProfileId(string? profileId);

    /// <summary>
    /// Looks a profile up by id.
    /// </summary>
    /// <param name="profileId">The candidate id. May be null.</param>
    /// <param name="profile">The profile when found.</param>
    /// <returns><c>true</c> when the id is known.</returns>
    bool TryGetProfile(string? profileId, [NotNullWhen(true)] out StereoProfile? profile);

    /// <summary>
    /// Looks a profile up by id.
    /// </summary>
    /// <param name="profileId">The profile id.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="KeyNotFoundException">The id is not on the allowlist.</exception>
    StereoProfile GetProfile(string? profileId);

    /// <summary>
    /// Resolves the enabled profile ids for a configuration, in display order.
    /// </summary>
    /// <param name="configuration">The persisted settings.</param>
    /// <returns>At least one profile id.</returns>
    IReadOnlyList<string> GetEnabledProfileIds(PluginConfiguration configuration);

    /// <summary>
    /// Resolves the enabled profiles for a configuration, in display order.
    /// </summary>
    /// <param name="configuration">The persisted settings.</param>
    /// <returns>At least one profile; the custom profile carries the configured colours.</returns>
    IReadOnlyList<StereoProfile> GetEnabledProfiles(PluginConfiguration configuration);

    /// <summary>
    /// Resolves the profile clients should get by default.
    /// </summary>
    /// <param name="configuration">The persisted settings.</param>
    /// <returns>
    /// A profile id that is always known and always enabled: the configured global
    /// default when it is enabled, otherwise the first enabled profile in display
    /// order. The global default is the only default tier - there is no device, client
    /// or user matching, and the resolution is the same answer for every request.
    /// </returns>
    string ResolveDefaultProfileId(PluginConfiguration configuration);

    /// <summary>
    /// Resolves the profile clients should get by default.
    /// </summary>
    /// <param name="configuration">The persisted settings.</param>
    /// <returns>The default profile, with configured colours applied when custom.</returns>
    StereoProfile GetDefaultProfile(PluginConfiguration configuration);

    /// <summary>
    /// Builds the version list: the enabled profiles with the resolved global default
    /// promoted to the front.
    /// </summary>
    /// <param name="configuration">The persisted settings.</param>
    /// <returns>The profiles to offer, default first, then the rest in display order.</returns>
    IReadOnlyList<StereoProfile> GetOfferedProfiles(PluginConfiguration configuration);
}
