using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Anaglyfin.Configuration;

namespace Anaglyfin.Profiles;

/// <summary>
/// The built-in profile catalog: the profile allowlist together with its metadata.
/// </summary>
/// <remarks>
/// <para>
/// The catalog is fixed at build time. Profiles are not data an administrator can
/// invent: the admin can enable, disable and default them, and can pick two
/// validated colours for the custom grayscale profile, but nothing in settings
/// turns free text into a conversion. That is what makes it safe for a media source
/// marker to carry a profile id all the way to the FFmpeg wrapper.
/// </para>
/// <para>
/// Registered as a singleton by <see cref="PluginServiceRegistrator"/>; it holds no
/// per-request state and reads no settings, so it is thread safe. Methods that
/// resolve a profile for a client take the current settings as an argument.
/// </para>
/// </remarks>
public sealed class ProfileCatalog : IProfileCatalog
{
    /// <summary>
    /// Colour the left eye is tinted with when the custom profile has no usable
    /// configured colour.
    /// </summary>
    public static RgbColor DefaultCustomLeftEyeColor { get; } = new(255, 0, 0);

    /// <summary>
    /// Colour the right eye is tinted with when the custom profile has no usable
    /// configured colour.
    /// </summary>
    public static RgbColor DefaultCustomRightEyeColor { get; } = new(0, 255, 255);

    /// <summary>
    /// The official <c>stereo3d</c> anaglyph output codes and the label each one is
    /// offered under. Display names follow the wording FFmpeg uses for the codes.
    /// </summary>
    private static readonly (string Code, string DisplayName)[] Stereo3DAnaglyphPresets =
    [
        ("arcd", "3D Anaglyph Red/Cyan (Dubois)"),
        ("arcc", "3D Anaglyph Red/Cyan"),
        ("arch", "3D Anaglyph Red/Cyan (Half Colour)"),
        ("arcg", "3D Anaglyph Red/Cyan (Gray)"),
        ("agmd", "3D Anaglyph Green/Magenta (Dubois)"),
        ("agmc", "3D Anaglyph Green/Magenta"),
        ("agmh", "3D Anaglyph Green/Magenta (Half Colour)"),
        ("agmg", "3D Anaglyph Green/Magenta (Gray)"),
        ("aybd", "3D Anaglyph Yellow/Blue (Dubois)"),
        ("aybc", "3D Anaglyph Yellow/Blue"),
        ("aybh", "3D Anaglyph Yellow/Blue (Half Colour)"),
        ("aybg", "3D Anaglyph Yellow/Blue (Gray)"),
        ("arbg", "3D Anaglyph Red/Blue (Gray)"),
        ("argg", "3D Anaglyph Red/Green (Gray)")
    ];

    private static readonly StereoProfile[] BuiltInProfiles = CreateProfiles();

    private static readonly Dictionary<string, StereoProfile> BuiltInProfilesById = CreateIndex(BuiltInProfiles);

    private static readonly string[] BuiltInProfileIdList = CreateIdList(BuiltInProfiles);

    /// <inheritdoc />
    public IReadOnlyList<StereoProfile> Profiles => BuiltInProfiles;

    /// <inheritdoc />
    public IReadOnlyList<string> AllProfileIds => BuiltInProfileIdList;

    /// <inheritdoc />
    public bool IsKnownProfileId(string? profileId)
        => Find(profileId) is not null;

    /// <inheritdoc />
    public bool TryGetProfile(string? profileId, [NotNullWhen(true)] out StereoProfile? profile)
    {
        profile = Find(profileId);
        return profile is not null;
    }

    /// <inheritdoc />
    public StereoProfile GetProfile(string? profileId)
        => Find(profileId)
            ?? throw new KeyNotFoundException(
                $"'{profileId}' is not an Anaglyfin profile id. Known ids: {string.Join(", ", BuiltInProfileIdList)}.");

    /// <inheritdoc />
    public IReadOnlyList<string> GetEnabledProfileIds(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = FilterKnown(configuration.EnabledProfileIds);

        // An admin who disabled every profile, or a settings file written against a
        // catalog this build does not know, must not leave a client with no version to
        // pick. Fall back to the shipped enabled set.
        IReadOnlyList<string> candidates = configured.Count > 0 ? configured : ProfileIds.DefaultEnabledProfileIds;

        var enabled = new List<string>(candidates.Count);
        foreach (var id in BuiltInProfileIdList)
        {
            if (candidates.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                enabled.Add(id);
            }
        }

        return enabled.Count > 0
            ? enabled
            : (IReadOnlyList<string>)ProfileIds.DefaultEnabledProfileIds;
    }

    /// <inheritdoc />
    public IReadOnlyList<StereoProfile> GetEnabledProfiles(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var ids = GetEnabledProfileIds(configuration);
        var profiles = new List<StereoProfile>(ids.Count);
        foreach (var id in ids)
        {
            profiles.Add(Configured(GetProfile(id), configuration));
        }

        return profiles;
    }

    /// <inheritdoc />
    public string ResolveDefaultProfileId(PluginConfiguration configuration, string? deviceId = null, string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = GetEnabledProfileIds(configuration);

        // Most specific first: the device/client override, then the global default. A
        // candidate only wins when it is a known profile that the administrator has
        // actually enabled; when neither is, the first enabled profile in display order is
        // what the page offered as the version to fall back to, and GetEnabledProfileIds
        // guarantees there is one.
        var deviceOverride = FindDeviceOverrideProfileId(configuration, deviceId, clientName);
        foreach (var candidate in new[] { deviceOverride, configuration.DefaultProfileId })
        {
            var id = ProfileIds.Normalize(candidate);
            if (id.Length > 0 && enabled.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return enabled[0];
    }

    /// <inheritdoc />
    public StereoProfile GetDefaultProfile(PluginConfiguration configuration, string? deviceId = null, string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Configured(GetProfile(ResolveDefaultProfileId(configuration, deviceId, clientName)), configuration);
    }

    /// <inheritdoc />
    public IReadOnlyList<StereoProfile> GetOfferedProfiles(PluginConfiguration configuration, string? deviceId = null, string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = GetEnabledProfiles(configuration);
        var defaultProfile = GetDefaultProfile(configuration, deviceId, clientName);

        var offered = new List<StereoProfile>(enabled.Count) { defaultProfile };
        foreach (var profile in enabled)
        {
            if (!ProfileIds.EqualsId(profile.Id, defaultProfile.Id))
            {
                offered.Add(profile);
            }
        }

        return offered;
    }

    /// <summary>
    /// Applies the configured custom anaglyph colours to a profile.
    /// </summary>
    private static StereoProfile Configured(StereoProfile profile, PluginConfiguration configuration)
    {
        if (profile.Kind != ProfileKind.CustomGrayscaleAnaglyph)
        {
            return profile;
        }

        // Settings hold text; the profile only ever carries parsed colours, so a
        // hand-edited settings file cannot push an arbitrary string towards FFmpeg.
        var left = RgbColor.TryParse(configuration.CustomLeftEyeColor, out var parsedLeft) ? parsedLeft : DefaultCustomLeftEyeColor;
        var right = RgbColor.TryParse(configuration.CustomRightEyeColor, out var parsedRight) ? parsedRight : DefaultCustomRightEyeColor;

        return profile.WithEyeColors(left, right);
    }

    /// <summary>
    /// Finds the profile id of the settings entry that matches this device or client.
    /// </summary>
    /// <remarks>
    /// The entry that pins the most fields wins; equally specific entries keep their
    /// declared order. An entry whose profile is unknown or disabled is ignored by the
    /// caller's enabled check.
    /// </remarks>
    private static string? FindDeviceOverrideProfileId(PluginConfiguration configuration, string? deviceId, string? clientName)
    {
        var overrides = configuration.DeviceDefaultProfiles;
        if (overrides is null || overrides.Count == 0)
        {
            return null;
        }

        string? bestProfileId = null;
        var bestStrength = 0;

        foreach (var entry in overrides)
        {
            if (entry is null)
            {
                continue;
            }

            var strength = entry.MatchStrength(deviceId, clientName);
            if (strength > bestStrength)
            {
                bestStrength = strength;
                bestProfileId = entry.ProfileId;
            }
        }

        return bestProfileId;
    }

    private static List<string> FilterKnown(IEnumerable<string>? candidateIds)
    {
        var known = new List<string>();
        if (candidateIds is null)
        {
            return known;
        }

        foreach (var candidate in candidateIds)
        {
            var id = ProfileIds.Normalize(candidate);
            if (id.Length > 0 && TryGetKnownId(id, out var resolved) && !known.Contains(resolved, StringComparer.OrdinalIgnoreCase))
            {
                known.Add(resolved);
            }
        }

        return known;
    }

    private static bool TryGetKnownId(string candidate, [NotNullWhen(true)] out string? id)
    {
        foreach (var known in BuiltInProfileIdList)
        {
            if (string.Equals(known, candidate, StringComparison.OrdinalIgnoreCase))
            {
                id = known;
                return true;
            }
        }

        id = null;
        return false;
    }

    private static StereoProfile? Find(string? profileId)
    {
        var key = ProfileIds.Normalize(profileId);
        return key.Length == 0 ? null : BuiltInProfilesById.GetValueOrDefault(key);
    }

    private static Dictionary<string, StereoProfile> CreateIndex(StereoProfile[] profiles)
    {
        var index = new Dictionary<string, StereoProfile>(profiles.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            index.Add(profile.Id, profile);
        }

        return index;
    }

    private static string[] CreateIdList(StereoProfile[] profiles)
    {
        var ids = new string[profiles.Length];
        for (var i = 0; i < profiles.Length; i++)
        {
            ids[i] = profiles[i].Id;
        }

        return ids;
    }

    private static StereoProfile[] CreateProfiles()
    {
        var profiles = new List<StereoProfile>
        {
            new()
            {
                Id = ProfileIds.SideBySideFull,
                DisplayName = "3D Full Side-by-Side",
                Kind = ProfileKind.SideBySideFull,
                DisplayOrder = 10
            }
        };

        // Every built-in stereo3d anaglyph output mode is offered; the order of the
        // preset table is the order they are listed in.
        var anaglyphOrder = 20;
        foreach (var (code, displayName) in Stereo3DAnaglyphPresets)
        {
            profiles.Add(new StereoProfile
            {
                Id = ProfileIds.BuildAnaglyphProfileId(code),
                DisplayName = displayName,
                Kind = ProfileKind.Stereo3DAnaglyph,
                Stereo3DOutputCode = code,
                DisplayOrder = anaglyphOrder++
            });
        }

        profiles.Add(new StereoProfile
        {
            Id = ProfileIds.CustomGrayscale,
            DisplayName = "3D Anaglyph Custom Colours",
            Kind = ProfileKind.CustomGrayscaleAnaglyph,
            LeftEyeColor = DefaultCustomLeftEyeColor,
            RightEyeColor = DefaultCustomRightEyeColor,
            DisplayOrder = 40
        });

        profiles.Add(new StereoProfile
        {
            Id = ProfileIds.SideBySideHalf,
            DisplayName = "3D Half Side-by-Side",
            Kind = ProfileKind.SideBySideHalf,
            DisplayOrder = 50
        });

        // 2D base is last in the list: it is the version that always plays, and the one
        // an administrator who reaches for the plain view expects at the end.
        profiles.Add(new StereoProfile
        {
            Id = ProfileIds.TwoDBase,
            DisplayName = "2D Base",
            Kind = ProfileKind.TwoDimensional,
            DisplayOrder = 60
        });

        profiles.Sort((left, right) =>
        {
            var byOrder = left.DisplayOrder.CompareTo(right.DisplayOrder);
            return byOrder != 0 ? byOrder : string.CompareOrdinal(left.Id, right.Id);
        });

        var ordered = profiles.ToArray();
        ValidateCatalog(ordered);
        return ordered;
    }

    /// <summary>
    /// Guards the catalog against drift from the profile id allowlist.
    /// </summary>
    /// <remarks>
    /// Runs while the type is initialised, so a mistake in the preset table surfaces
    /// as a start-up failure instead of a profile that a client cannot play.
    /// </remarks>
    private static void ValidateCatalog(StereoProfile[] profiles)
    {
        if (profiles.Length != ProfileIds.AllProfileIds.Count)
        {
            throw new InvalidOperationException(
                $"The profile catalog holds {profiles.Length} profiles but the allowlist declares {ProfileIds.AllProfileIds.Count}.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (!seen.Add(profile.Id))
            {
                throw new InvalidOperationException($"Profile id '{profile.Id}' is declared twice in the catalog.");
            }

            profile.Validate();
        }

        foreach (var id in ProfileIds.AllProfileIds)
        {
            if (!seen.Contains(id))
            {
                throw new InvalidOperationException($"The allowlist declares profile id '{id}' but the catalog has no profile for it.");
            }
        }
    }
}
