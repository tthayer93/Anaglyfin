using System;
using System.Collections.Generic;
using System.Linq;
using Anaglyfin.Configuration;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.Profiles;

/// <summary>
/// Verifies the profile allowlist, the metadata carried with it and how settings choose
/// a default profile from it.
/// </summary>
public class ProfileCatalogTests
{
    /// <summary>
    /// Profiles a test can enable by name when it needs more than one profile in play.
    /// They are catalog members and nothing more: only red/cyan Dubois ships enabled, so a
    /// test that wants any of these has to ask for it in its settings.
    /// </summary>
    private static readonly string[] ConversionFormatIds =
    [
        ProfileIds.TwoDBase,
        ProfileIds.SideBySideFull,
        ProfileIds.SideBySideHalf
    ];

    private readonly ProfileCatalog _catalog = new();

    [Fact]
    public void CatalogCoversTheProfileIdAllowlistExactlyOnceAndInDisplayOrder()
    {
        Assert.Equal(ProfileIds.AllProfileIds, _catalog.AllProfileIds);
        Assert.Equal(ProfileIds.AllProfileIds, _catalog.Profiles.Select(profile => profile.Id).ToArray());
        Assert.Equal(_catalog.Profiles.Count, _catalog.Profiles.Select(profile => profile.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(_catalog.Profiles.Select(profile => profile.DisplayOrder).SequenceEqual(_catalog.Profiles.Select(profile => profile.DisplayOrder).OrderBy(order => order)));
    }

    [Fact]
    public void AllowlistIsTheCatalogOrderVerbatim()
    {
        // Pinned element by element: AllProfileIds order IS display order, and the id
        // feeds the version-item sort rank, so a profile quietly moving in this list
        // re-ranks the version picker without anyone deciding it.
        Assert.Equal(
            new[]
            {
                ProfileIds.SideBySideFull,
                ProfileIds.TopAndBottomFull,
                ProfileIds.AnaglyphRedCyanDubois,
                ProfileIds.AnaglyphRedCyanColor,
                ProfileIds.AnaglyphRedCyanHalfColor,
                ProfileIds.AnaglyphRedCyanGray,
                ProfileIds.AnaglyphGreenMagentaDubois,
                ProfileIds.AnaglyphGreenMagentaColor,
                ProfileIds.AnaglyphGreenMagentaHalfColor,
                ProfileIds.AnaglyphGreenMagentaGray,
                ProfileIds.AnaglyphYellowBlueDubois,
                ProfileIds.AnaglyphYellowBlueColor,
                ProfileIds.AnaglyphYellowBlueHalfColor,
                ProfileIds.AnaglyphYellowBlueGray,
                ProfileIds.AnaglyphRedBlueGray,
                ProfileIds.AnaglyphRedGreenGray,
                ProfileIds.CustomGrayscale,
                ProfileIds.TopAndBottomHalf,
                ProfileIds.SideBySideHalf,
                ProfileIds.TwoDBase
            },
            ProfileIds.AllProfileIds);
        Assert.Equal(20, ProfileIds.AllProfileIds.Count);
    }

    [Fact]
    public void CatalogCarriesTheTopAndBottomPairBesideItsSideBySideSiblings()
    {
        var full = _catalog.GetProfile(ProfileIds.TopAndBottomFull);
        var half = _catalog.GetProfile(ProfileIds.TopAndBottomHalf);

        Assert.Equal("3D Full Top-and-Bottom", full.DisplayName);
        Assert.Equal(ProfileKind.TopAndBottomFull, full.Kind);
        Assert.Equal("3D Half Top-and-Bottom", half.DisplayName);
        Assert.Equal(ProfileKind.TopAndBottomHalf, half.Kind);

        // Full TB seats directly behind full SBS (10 -> 15), half TB directly ahead of
        // half SBS (45 -> 50): each over-under profile sits beside the side-by-side
        // profile of the same size class.
        Assert.Equal(15, full.DisplayOrder);
        Assert.Equal(45, half.DisplayOrder);
        var ids = _catalog.Profiles.Select(profile => profile.Id).ToArray();
        Assert.Equal(Array.IndexOf(ids, ProfileIds.SideBySideFull) + 1, Array.IndexOf(ids, ProfileIds.TopAndBottomFull));
        Assert.Equal(Array.IndexOf(ids, ProfileIds.SideBySideHalf) - 1, Array.IndexOf(ids, ProfileIds.TopAndBottomHalf));

        // Over-under is a layout conversion, not an anaglyph mix and not an output code:
        // the anaglyph allowlist stays closed and the profiles carry no code of any kind.
        Assert.False(full.IsAnaglyph);
        Assert.False(half.IsAnaglyph);
        Assert.Null(full.Stereo3DOutputCode);
        Assert.Null(half.Stereo3DOutputCode);
        Assert.True(full.RequiresAllViews);
        Assert.True(half.RequiresAllViews);
    }

    [Fact]
    public void CatalogCarriesTheTwoDAndSideBySideFormatsBesidesTheDefaultAnaglyph()
    {
        // Catalog membership only. Which of them a fresh installation offers is the
        // enabled-set test below, and the answer there is one profile.
        foreach (var id in ConversionFormatIds.Append(ProfileIds.AnaglyphRedCyanDubois))
        {
            Assert.True(_catalog.TryGetProfile(id, out _), $"profile {id} is missing from the catalog");
        }

        Assert.Equal(ProfileKind.TwoDimensional, _catalog.GetProfile(ProfileIds.TwoDBase).Kind);
        Assert.Equal(ProfileKind.SideBySideFull, _catalog.GetProfile(ProfileIds.SideBySideFull).Kind);
        Assert.Equal(ProfileKind.SideBySideHalf, _catalog.GetProfile(ProfileIds.SideBySideHalf).Kind);
        Assert.Equal(ProfileKind.Stereo3DAnaglyph, _catalog.GetProfile(ProfileIds.AnaglyphRedCyanDubois).Kind);
    }

    [Fact]
    public void CatalogExposesEveryBuiltInStereo3DAnaglyphOutputCode()
    {
        var anaglyphProfiles = _catalog.Profiles.Where(profile => profile.Kind == ProfileKind.Stereo3DAnaglyph).ToArray();

        Assert.Equal(ProfileIds.BuiltInAnaglyphOutputCodes.Count, anaglyphProfiles.Length);
        Assert.Equal(
            ProfileIds.BuiltInAnaglyphOutputCodes,
            anaglyphProfiles.Select(profile => profile.Stereo3DOutputCode ?? string.Empty).ToArray());

        foreach (var profile in anaglyphProfiles)
        {
            Assert.Equal(ProfileIds.AnaglyphProfileIdPrefix + profile.Stereo3DOutputCode, profile.Id);
            Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName));
            Assert.Null(profile.LeftEyeColor);
            Assert.Null(profile.RightEyeColor);
        }
    }

    [Fact]
    public void AnaglyphCodesAreTheOfficialStereo3DOutputNames()
    {
        // The out-unit of libavfilter/vf_stereo3d.c names exactly these anaglyph modes.
        // Anything else - a made up name, a geometry code, the mono outputs - is drift.
        var officialCodes = new[]
        {
            "arcg", "arch", "arcc", "arcd",
            "agmg", "agmh", "agmc", "agmd",
            "aybg", "aybh", "aybc", "aybd",
            "arbg", "argg"
        };

        Assert.Equal(14, officialCodes.Length);
        Assert.Equal(officialCodes.OrderBy(code => code, StringComparer.Ordinal), ProfileIds.BuiltInAnaglyphOutputCodes.OrderBy(code => code, StringComparer.Ordinal));

        Assert.DoesNotContain("ml", ProfileIds.BuiltInAnaglyphOutputCodes);
        Assert.DoesNotContain("mr", ProfileIds.BuiltInAnaglyphOutputCodes);
        Assert.DoesNotContain("sbsl", ProfileIds.BuiltInAnaglyphOutputCodes);
        Assert.DoesNotContain("sbs2l", ProfileIds.BuiltInAnaglyphOutputCodes);
    }

    [Fact]
    public void OnlyThePlainTwoDProfileSkipsTheAllViewPivot()
    {
        foreach (var profile in _catalog.Profiles)
        {
            var expected = profile.Kind != ProfileKind.TwoDimensional;

            Assert.Equal(expected, profile.RequiresAllViews);
            Assert.True(profile.SupportsSubtitleBurnIn, $"profile {profile.Id} must stay burn-in capable");

            if (profile.Kind == ProfileKind.SideBySideFull)
            {
                // Full SBS is the native all-view output: no stereo filter is attached.
                Assert.Null(profile.Stereo3DOutputCode);
            }
        }

        Assert.False(_catalog.GetProfile(ProfileIds.TwoDBase).RequiresAllViews);
    }

    [Fact]
    public void ProfilesCarryNoFreeFormConversionText()
    {
        foreach (var profile in _catalog.Profiles)
        {
            Assert.DoesNotContain(" ", profile.Id);
            Assert.DoesNotContain("=", profile.Id);
            Assert.DoesNotContain(":", profile.Id);

            if (profile.Stereo3DOutputCode is not null)
            {
                Assert.DoesNotContain("=", profile.Stereo3DOutputCode);
                Assert.DoesNotContain(":", profile.Stereo3DOutputCode);
                Assert.DoesNotContain("stereo3d", profile.Stereo3DOutputCode);
            }
        }
    }

    [Fact]
    public void DisplayNamesAreUniqueAndNonEmpty()
    {
        var names = _catalog.Profiles.Select(profile => profile.DisplayName).ToArray();

        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(ProfileIds.TwoDBase)]
    [InlineData(ProfileIds.SideBySideFull)]
    [InlineData(ProfileIds.SideBySideHalf)]
    [InlineData(ProfileIds.CustomGrayscale)]
    [InlineData(ProfileIds.AnaglyphRedCyanDubois)]
    [InlineData(ProfileIds.AnaglyphGreenMagentaGray)]
    [InlineData("  sbs_full  ")]
    [InlineData("SBS_FULL")]
    public void KnownProfileIdsAreAccepted(string profileId)
    {
        Assert.True(_catalog.IsKnownProfileId(profileId));
        Assert.True(_catalog.TryGetProfile(profileId, out var profile));
        Assert.NotNull(profile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("arcd")]
    [InlineData("stereo3d=sbsl:arcd")]
    [InlineData("-vf scale=iw/2:ih")]
    [InlineData("anaglyfin://profile/two_d_base")]
    [InlineData("../etc/passwd")]
    [InlineData("two_d_base; rm -rf /")]
    [InlineData("scale")]
    [InlineData("custom_grayscale;format=gbrp")]
    [InlineData("mergeplanes=0x101102:gbrp")]
    public void UnknownProfileIdsAreRejected(string? profileId)
    {
        Assert.False(_catalog.IsKnownProfileId(profileId));
        Assert.False(_catalog.TryGetProfile(profileId, out var profile));
        Assert.Null(profile);
        Assert.Throws<KeyNotFoundException>(() => _catalog.GetProfile(profileId));
    }

    [Fact]
    public void FreshSettingsDefaultToRedCyanAnaglyph()
    {
        var configuration = new PluginConfiguration();

        Assert.Equal(ProfileIds.AnaglyphRedCyanDubois, _catalog.ResolveDefaultProfileId(configuration));
        Assert.Equal(ProfileIds.AnaglyphRedCyanDubois, _catalog.GetDefaultProfile(configuration).Id);
    }

    [Fact]
    public void FreshSettingsEnableRedCyanDuboisAlone()
    {
        var enabled = _catalog.GetEnabledProfileIds(new PluginConfiguration());

        // One version for a server nobody has configured: the profile the shipped default
        // names, offered first because it is also the only one offered. Assert.Single is the
        // count assertion, and the equality below is the identity assertion: exactly one
        // entry, and it is red/cyan Dubois - in the resolved offer and as declared.
        Assert.Equal(new[] { ProfileIds.AnaglyphRedCyanDubois }, enabled);
        Assert.Equal(ProfileIds.AnaglyphRedCyanDubois, Assert.Single(enabled));
        Assert.Equal(ProfileIds.AnaglyphRedCyanDubois, new PluginConfiguration().DefaultProfileId);
        Assert.Equal(ProfileIds.AnaglyphRedCyanDubois, Assert.Single(ProfileIds.DefaultEnabledProfileIds));
    }

    [Fact]
    public void EveryOtherCatalogProfileNeedsAnExplicitEnable()
    {
        var enabled = _catalog.GetEnabledProfileIds(new PluginConfiguration());

        // The named entries an administrator has to switch on. The over-under pair is here
        // because adding a profile is a code change while enabling it stays the
        // administrator's decision; the side-by-side pair, the base view and the custom
        // colours are here because a fresh install no longer offers them either.
        Assert.DoesNotContain(ProfileIds.SideBySideFull, enabled);
        Assert.DoesNotContain(ProfileIds.SideBySideHalf, enabled);
        Assert.DoesNotContain(ProfileIds.TwoDBase, enabled);
        Assert.DoesNotContain(ProfileIds.TopAndBottomFull, enabled);
        Assert.DoesNotContain(ProfileIds.TopAndBottomHalf, enabled);
        Assert.DoesNotContain(ProfileIds.CustomGrayscale, enabled);
        Assert.DoesNotContain(ProfileIds.AnaglyphRedCyanGray, enabled);

        // The sweep, so no catalog entry slips through the named list above: red/cyan Dubois
        // is the only shipped-enabled id, in the resolved offer and in the declared set.
        Assert.All(
            _catalog.AllProfileIds.Where(id => !ProfileIds.EqualsId(id, ProfileIds.AnaglyphRedCyanDubois)),
            id =>
            {
                Assert.DoesNotContain(id, enabled);
                Assert.DoesNotContain(id, ProfileIds.DefaultEnabledProfileIds);
            });
    }

    [Fact]
    public void ADefaultThatIsNotShippedEnabledIsNotOfferedByAFreshInstall()
    {
        // Configuring a default is not enabling: on settings that store no enabled list the
        // offer is the shipped set, so a default naming a profile nobody switched on
        // degrades to red/cyan instead of promoting itself. Full SBS was offered by the
        // shipped defaults of earlier builds, which is exactly the drift this pins.
        var configuration = new PluginConfiguration { DefaultProfileId = ProfileIds.SideBySideFull };

        Assert.Equal(ProfileIds.AnaglyphRedCyanDubois, _catalog.ResolveDefaultProfileId(configuration));
        Assert.Equal(
            new[] { ProfileIds.AnaglyphRedCyanDubois },
            _catalog.GetOfferedProfiles(configuration).Select(profile => profile.Id));
    }

    [Fact]
    public void EnabledProfilesKeepDisplayOrderAndIgnoreUnknownIds()
    {
        var configuration = new PluginConfiguration();
        configuration.EnabledProfileIds.AddRange(new[]
        {
            "no_such_profile",
            ProfileIds.TwoDBase,
            ProfileIds.CustomGrayscale,
            ProfileIds.SideBySideHalf,
            ProfileIds.TwoDBase
        });

        Assert.Equal(
            new[] { ProfileIds.CustomGrayscale, ProfileIds.SideBySideHalf, ProfileIds.TwoDBase },
            _catalog.GetEnabledProfileIds(configuration));
    }

    [Fact]
    public void EnabledProfilesFallBackToTheDefaultSetWhenNothingIsLeft()
    {
        var configuration = new PluginConfiguration();
        configuration.EnabledProfileIds.AddRange(new[] { "gone_one", "gone_two" });

        // Nothing usable was stored, so the shipped set answers - which is one profile now,
        // and still never an empty offer.
        Assert.Equal(
            new[] { ProfileIds.AnaglyphRedCyanDubois },
            _catalog.GetEnabledProfileIds(configuration));
    }

    [Fact]
    public void AnaglyphPresetsCanBeEnabledAlongsideTheOtherProfiles()
    {
        var configuration = new PluginConfiguration();
        configuration.EnabledProfileIds.AddRange(new[]
        {
            ProfileIds.TwoDBase,
            ProfileIds.AnaglyphRedCyanDubois,
            ProfileIds.AnaglyphYellowBlueDubois
        });

        var enabled = _catalog.GetEnabledProfiles(configuration);

        Assert.Contains(ProfileIds.AnaglyphYellowBlueDubois, enabled.Select(profile => profile.Id));
        Assert.Equal("aybd", _catalog.GetProfile(ProfileIds.AnaglyphYellowBlueDubois).Stereo3DOutputCode);
    }

    [Fact]
    public void TheGlobalDefaultIsTheOnlyDefaultTierTheResolutionOffers()
    {
        // The global default is the only default there is: the resolution call takes the
        // settings and nothing else - no device, client or user argument exists to pass,
        // which is the compile-time pin as much as the runtime one. The same settings
        // answer the same default for every caller.
        var configuration = new PluginConfiguration
        {
            DefaultProfileId = ProfileIds.SideBySideFull
        };
        configuration.EnabledProfileIds.AddRange(new[]
        {
            ProfileIds.SideBySideFull,
            ProfileIds.SideBySideHalf,
            ProfileIds.AnaglyphRedCyanDubois
        });

        Assert.Equal(ProfileIds.SideBySideFull, _catalog.ResolveDefaultProfileId(configuration));
        Assert.Equal(ProfileIds.SideBySideFull, _catalog.GetDefaultProfile(configuration).Id);
    }

    [Fact]
    public void ADefaultThatIsNotEnabledDegradesInsteadOfFailing()
    {
        var configuration = new PluginConfiguration
        {
            // Enabled, but the administrator switched it off.
            DefaultProfileId = ProfileIds.TwoDBase
        };
        configuration.EnabledProfileIds.Add(ProfileIds.SideBySideHalf);

        Assert.Equal(ProfileIds.SideBySideHalf, _catalog.ResolveDefaultProfileId(configuration));

        // An id this build does not know at all.
        configuration.DefaultProfileId = "profile_from_the_future";

        Assert.Equal(ProfileIds.SideBySideHalf, _catalog.ResolveDefaultProfileId(configuration));
    }

    [Fact]
    public void WhenTheDefaultIsUnusableTheFirstEnabledProfileInDisplayOrderWins()
    {
        // There is no separate fallback setting any more: when the default cannot be served - it is
        // unknown, or the administrator disabled it - the catalog offers the first enabled profile
        // in display order. That order is fixed by the catalog, so the degrade is predictable
        // without a second knob to agree with it.
        var configuration = new PluginConfiguration
        {
            DefaultProfileId = "also_from_the_future"
        };
        configuration.EnabledProfileIds.AddRange(new[]
        {
            ProfileIds.TwoDBase,
            ProfileIds.SideBySideHalf,
            ProfileIds.AnaglyphRedCyanDubois,
            ProfileIds.SideBySideFull
        });

        // The shipped display order puts full side-by-side first of these four.
        Assert.Equal(ProfileIds.SideBySideFull, _catalog.ResolveDefaultProfileId(configuration));
    }

    [Fact]
    public void OfferedProfilesPutTheGlobalDefaultFirstWithoutDuplicates()
    {
        // The offered list is the global default promoted to the front and the remaining
        // enabled profiles behind it in display order - for every client alike.
        var configuration = new PluginConfiguration();
        configuration.EnabledProfileIds.AddRange(new[]
        {
            ProfileIds.SideBySideFull,
            ProfileIds.SideBySideHalf,
            ProfileIds.AnaglyphRedCyanDubois,
            ProfileIds.CustomGrayscale
        });

        var offered = _catalog.GetOfferedProfiles(configuration).Select(profile => profile.Id).ToArray();

        Assert.Equal(
            new[] { ProfileIds.AnaglyphRedCyanDubois, ProfileIds.SideBySideFull, ProfileIds.CustomGrayscale, ProfileIds.SideBySideHalf },
            offered);
        Assert.Equal(offered.Length, offered.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void OfferedProfilesNeverReturnAnUnknownIdEvenWithHostileSettings()
    {
        var configuration = new PluginConfiguration
        {
            DefaultProfileId = "stereo3d=sbsl:arcd"
        };
        configuration.EnabledProfileIds.Add("an arbitrary string");

        var offered = _catalog.GetOfferedProfiles(configuration);

        Assert.NotEmpty(offered);
        Assert.All(offered, profile => Assert.True(_catalog.IsKnownProfileId(profile.Id)));
    }

    [Fact]
    public void CustomProfileCarriesTheConfiguredColoursAsValidatedValues()
    {
        var configuration = new PluginConfiguration
        {
            CustomLeftEyeColor = "#ff3300",
            CustomRightEyeColor = "#0033ff"
        };
        configuration.EnabledProfileIds.Add(ProfileIds.CustomGrayscale);

        var custom = _catalog.GetEnabledProfiles(configuration).Single(profile => profile.Id == ProfileIds.CustomGrayscale);

        Assert.Equal(new RgbColor(255, 51, 0), custom.LeftEyeColor);
        Assert.Equal(new RgbColor(0, 51, 255), custom.RightEyeColor);
        Assert.True(custom.IsAnaglyph);
        Assert.Null(custom.Stereo3DOutputCode);
        Assert.Equal(ProfileKind.CustomGrayscaleAnaglyph, custom.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hotpink")]
    [InlineData("#GGGGGG")]
    [InlineData("#FF0000 -vf hstack")]
    [InlineData("rgb(255,0,0)")]
    public void UnusableColoursFallBackToTheShippedOnes(string storedColour)
    {
        var configuration = new PluginConfiguration
        {
            DefaultProfileId = ProfileIds.CustomGrayscale,
            CustomLeftEyeColor = storedColour,
            CustomRightEyeColor = storedColour
        };

        var custom = _catalog.GetDefaultProfile(WithCustomEnabled(configuration));

        Assert.Equal(ProfileIds.CustomGrayscale, custom.Id);
        Assert.Equal(ProfileCatalog.DefaultCustomLeftEyeColor, custom.LeftEyeColor);
        Assert.Equal(ProfileCatalog.DefaultCustomRightEyeColor, custom.RightEyeColor);

        // The unusable text is gone: what the catalog hands on is canonical at worst.
        Assert.Equal("#FF0000", custom.LeftEyeColor?.ToHexString());
        Assert.Equal("#00FFFF", custom.RightEyeColor?.ToHexString());
    }

    [Fact]
    public void CustomColoursReachTheCatalogOnlyAsCanonicalText()
    {
        var configuration = new PluginConfiguration
        {
            DefaultProfileId = ProfileIds.CustomGrayscale,
            CustomLeftEyeColor = "  #0a1b2c ",
            CustomRightEyeColor = "#FF0000"
        };

        var custom = _catalog.GetDefaultProfile(WithCustomEnabled(configuration));

        Assert.Equal(ProfileIds.CustomGrayscale, custom.Id);
        Assert.Equal("#0A1B2C", custom.LeftEyeColor?.ToHexString());
        Assert.Equal("#FF0000", custom.RightEyeColor?.ToHexString());
    }

    [Fact]
    public void CatalogIsAStatelessSingletonShape()
    {
        var first = new ProfileCatalog();
        var second = new ProfileCatalog();

        Assert.Equal(first.AllProfileIds, second.AllProfileIds);
        Assert.Equal(first.GetProfile(ProfileIds.SideBySideFull), second.GetProfile(ProfileIds.SideBySideFull));

        // A resolved profile must not be able to mutate catalog state.
        var configuration = new PluginConfiguration
        {
            DefaultProfileId = ProfileIds.CustomGrayscale,
            CustomLeftEyeColor = "#010203"
        };

        var configured = _catalog.GetDefaultProfile(WithCustomEnabled(configuration));

        Assert.Equal(new RgbColor(1, 2, 3), configured.LeftEyeColor);
        Assert.Equal(ProfileCatalog.DefaultCustomLeftEyeColor, _catalog.GetProfile(ProfileIds.CustomGrayscale).LeftEyeColor);
        Assert.Equal(second.GetProfile(ProfileIds.CustomGrayscale).LeftEyeColor, first.GetProfile(ProfileIds.CustomGrayscale).LeftEyeColor);
    }

    private static PluginConfiguration WithCustomEnabled(PluginConfiguration configuration)
    {
        if (!configuration.EnabledProfileIds.Contains(ProfileIds.CustomGrayscale))
        {
            configuration.EnabledProfileIds.Add(ProfileIds.CustomGrayscale);
        }

        return configuration;
    }
}
