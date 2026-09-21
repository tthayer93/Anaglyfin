using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Anaglyfin.Configuration;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.Configuration;

/// <summary>
/// Verifies the settings model: the shipped defaults, and that the shapes used here
/// survive the two serialisers the server actually uses on them.
/// </summary>
public class PluginConfigurationTests
{
    [Fact]
    public void FreshSettingsCarryTheAgreedDefaults()
    {
        var configuration = new PluginConfiguration();

        Assert.Equal(ProfileIds.AnaglyphRedCyanDubois, configuration.DefaultProfileId);
        Assert.Equal(PluginConfiguration.DefaultMaxConcurrentTranscodes, configuration.MaxConcurrentTranscodes);
        Assert.Equal(1, configuration.MaxConcurrentTranscodes);
        Assert.Empty(configuration.EnabledProfileIds);
        Assert.Equal("#FF0000", configuration.CustomLeftEyeColor);
        Assert.Equal("#00FFFF", configuration.CustomRightEyeColor);
        Assert.Equal(SubtitleDepthMode.Automatic, configuration.SubtitleDepthMode);
        Assert.Equal(0, configuration.SubtitleDepthShift);
        Assert.Equal(0, configuration.SubtitleDepthPlane);
    }

    [Fact]
    public void FreshSettingsAskForAutomaticSubtitleDepth()
    {
        // The shipped default of the one dropdown is Automatic, and the read side agrees: a
        // fresh installation asks for the depth the disc carries, so the stored default and the
        // effective request say the same thing rather than one of them hiding behind a switch.
        var configuration = new PluginConfiguration();

        Assert.Equal(SubtitleDepthMode.Automatic, configuration.SubtitleDepthMode);
        Assert.Equal(new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0), configuration.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void SettingsOnlyReferenceAllowlistedProfileIds()
    {
        var configuration = new PluginConfiguration();
        configuration.DefaultProfileId = ProfileIds.SideBySideHalf;

        Assert.True(ProfileIds.IsAllowed(configuration.DefaultProfileId));
        Assert.All(configuration.EnabledProfileIds, id => Assert.True(ProfileIds.IsAllowed(id)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void ANonsensicalConcurrencyLimitIsReadAsTheDefault(int stored)
    {
        var configuration = new PluginConfiguration { MaxConcurrentTranscodes = stored };

        Assert.Equal(PluginConfiguration.DefaultMaxConcurrentTranscodes, configuration.GetEffectiveMaxConcurrentTranscodes());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    public void AUsableConcurrencyLimitIsHonoured(int stored)
    {
        var configuration = new PluginConfiguration { MaxConcurrentTranscodes = stored };

        Assert.Equal(stored, configuration.GetEffectiveMaxConcurrentTranscodes());
    }

    [Fact]
    public void ConcurrencyHelperRejectsAMissingConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => ((PluginConfiguration)null!).GetEffectiveMaxConcurrentTranscodes());
    }

    [Fact]
    public void SettingsSurviveTheServerXmlRoundTrip()
    {
        var configuration = new PluginConfiguration
        {
            DefaultProfileId = ProfileIds.SideBySideHalf,
            MaxConcurrentTranscodes = 3,
            CustomLeftEyeColor = "#00FF00",
            CustomRightEyeColor = "#0000FF",
            SubtitleDepthMode = SubtitleDepthMode.Plane,
            SubtitleDepthPlane = 5
        };
        configuration.EnabledProfileIds.AddRange(new[] { ProfileIds.SideBySideFull, ProfileIds.SideBySideHalf, ProfileIds.CustomGrayscale });

        var reloaded = RoundTripXml(configuration);

        Assert.Equal(ProfileIds.SideBySideHalf, reloaded.DefaultProfileId);
        Assert.Equal(3, reloaded.MaxConcurrentTranscodes);
        Assert.Equal("#00FF00", reloaded.CustomLeftEyeColor);
        Assert.Equal("#0000FF", reloaded.CustomRightEyeColor);
        Assert.Equal(SubtitleDepthMode.Plane, reloaded.SubtitleDepthMode);
        Assert.Equal(5, reloaded.SubtitleDepthPlane);
        Assert.Equal(
            new[] { ProfileIds.SideBySideFull, ProfileIds.SideBySideHalf, ProfileIds.CustomGrayscale },
            reloaded.EnabledProfileIds);
    }

    [Fact]
    public void ReloadingSettingsFromXmlDoesNotDuplicateStoredValues()
    {
        // The server loads a settings file into a fresh instance whose collection
        // properties already hold their defaults. A prefilled default would therefore be
        // added to instead of replaced, so the persisted default of both collections has
        // to stay empty.
        var configuration = new PluginConfiguration();
        configuration.EnabledProfileIds.Add(ProfileIds.TwoDBase);

        var reloaded = RoundTripXml(RoundTripXml(configuration));

        Assert.Equal(new[] { ProfileIds.TwoDBase }, reloaded.EnabledProfileIds);
    }

    [Fact]
    public void ASettingsFileFromTheScaffoldEraLoadsAgainstTheNewModel()
    {
        // What the bootstrap build wrote: the settings type with no settings at all.
        // Loading that file against this model must produce a usable configuration, not
        // a load failure and not an empty profile set.
        const string ScaffoldEraXml = """
            <PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" />
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(ScaffoldEraXml);

        var loaded = (PluginConfiguration?)serializer.Deserialize(reader)
            ?? throw new InvalidOperationException("The scaffold era settings XML did not load a configuration.");

        Assert.Equal(ProfileIds.AnaglyphRedCyanDubois, loaded.DefaultProfileId);
        Assert.Equal(1, loaded.MaxConcurrentTranscodes);
        Assert.Empty(loaded.EnabledProfileIds);

        // Raw deserialisation leaves the shipped default here: Automatic. This is the pre-depth
        // file that the migration turns into Flat - see PluginConfigurationMigrationTests and the
        // load path in PluginTests - and the reason a bare settings load is not enough on its own.
        Assert.Equal(SubtitleDepthMode.Automatic, loaded.SubtitleDepthMode);

        // And the empty enabled list still offers the MVP formats.
        var catalog = new ProfileCatalog();
        Assert.Contains(ProfileIds.AnaglyphRedCyanDubois, catalog.GetEnabledProfileIds(loaded));
    }

    [Fact]
    public void SettingsSurviveTheAdminUiJsonRoundTrip()
    {
        // The plugin settings endpoint reads the admin UI payload with string enums, so
        // the model has to round-trip that shape as well.
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var configuration = new PluginConfiguration
        {
            DefaultProfileId = ProfileIds.CustomGrayscale,
            MaxConcurrentTranscodes = 4,
            SubtitleDepthMode = SubtitleDepthMode.ConstantShift,
            CustomLeftEyeColor = "#112233",
            CustomRightEyeColor = "#445566"
        };
        configuration.EnabledProfileIds.Add(ProfileIds.CustomGrayscale);

        var payload = JsonSerializer.Serialize(configuration, options);
        var reloaded = JsonSerializer.Deserialize<PluginConfiguration>(payload, options);

        Assert.NotNull(reloaded);
        Assert.Equal(ProfileIds.CustomGrayscale, reloaded!.DefaultProfileId);
        Assert.Equal(4, reloaded.MaxConcurrentTranscodes);
        Assert.Equal(SubtitleDepthMode.ConstantShift, reloaded.SubtitleDepthMode);
        Assert.Equal("#112233", reloaded.CustomLeftEyeColor);

        // The settings endpoint hands these collections back on every save, so they have
        // to travel in both directions, not just out.
        Assert.Contains("\"EnabledProfileIds\":[\"custom_grayscale\"]", payload);
        Assert.Contains("\"ConstantShift\"", payload);

        // The exact-device default feature never shipped and has no property on this
        // model: a JSON save round-trips the settings and nothing device-shaped.
        Assert.DoesNotContain("DeviceDefaultProfiles", payload, StringComparison.Ordinal);

        Assert.Equal(ProfileIds.CustomGrayscale, Assert.Single(reloaded.EnabledProfileIds));
    }

    [Fact]
    public void AStoredDeviceDefaultEntryLoadsIgnoredAndIsGoneAfterTheNextSave()
    {
        // What an upgrading server can have on disk: a settings file written by a build
        // that carried the exact-device defaults feature. That feature never shipped and
        // this model has no property for it, so the XML serialiser's job is to ignore what
        // it cannot place. Three promises, in the order a loader meets them:
        //
        //   1. the file loads without throwing - a stale element never fails the plugin,
        //   2. the stale entries decide nothing - neither the default nor the offered
        //      order moves because of a hidden device row,
        //   3. the next save drops them - what this build writes back is this build's
        //      model, so the entries are not silently preserved for the next upgrade.
        //
        // Nothing is migrated on disk and nothing is honoured from the dark.
        const string DeviceEraXml = """
            <PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <DefaultProfileId>two_d_base</DefaultProfileId>
              <DeviceDefaultProfiles>
                <DeviceProfileDefault>
                  <DeviceId>old-device</DeviceId>
                  <ProfileId>sbs_half</ProfileId>
                </DeviceProfileDefault>
              </DeviceDefaultProfiles>
            </PluginConfiguration>
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        PluginConfiguration loaded;
        using (var reader = new StringReader(DeviceEraXml))
        {
            loaded = (PluginConfiguration?)serializer.Deserialize(reader)
                ?? throw new InvalidOperationException("The device-era settings XML did not load a configuration.");
        }

        // 1. It loaded, and the settings this build understands are intact.
        Assert.Equal(ProfileIds.TwoDBase, loaded.DefaultProfileId);

        // 2. The stale entry decides nothing. Were it still applied, "old-device" would
        // start on sbs_half; there is no longer any reader that could ask that question,
        // so the global default and the catalog's global order stand.
        var catalog = new ProfileCatalog();
        Assert.Equal(ProfileIds.TwoDBase, catalog.ResolveDefaultProfileId(loaded));
        Assert.Equal(
            new[] { ProfileIds.TwoDBase, ProfileIds.SideBySideFull, ProfileIds.AnaglyphRedCyanDubois, ProfileIds.SideBySideHalf },
            catalog.GetOfferedProfiles(loaded).Select(profile => profile.Id).ToArray());

        // 3. The next save - what the settings path writes from this model - carries the
        // surviving settings and drops the device element with its entry.
        var saved = SerializeXml(loaded);

        Assert.Contains("<DefaultProfileId>two_d_base</DefaultProfileId>", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceDefaultProfiles", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceProfileDefault", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("old-device", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("sbs_half", saved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SubtitleDepthMode.Automatic, 0, 0)]
    [InlineData(SubtitleDepthMode.ConstantShift, -8, 0)]
    [InlineData(SubtitleDepthMode.ConstantShift, 64, 17)]
    [InlineData(SubtitleDepthMode.ConstantShift, -64, 0)]
    [InlineData(SubtitleDepthMode.Plane, 3, 7)]
    [InlineData(SubtitleDepthMode.Plane, 0, 31)]
    public void SubtitleDepthSettingsSurviveTheServerXmlRoundTrip(SubtitleDepthMode mode, int shift, int plane)
    {
        var configuration = new PluginConfiguration
        {
            SubtitleDepthMode = mode,
            SubtitleDepthShift = shift,
            SubtitleDepthPlane = plane
        };

        var reloaded = RoundTripXml(configuration);

        Assert.Equal(mode, reloaded.SubtitleDepthMode);
        Assert.Equal(shift, reloaded.SubtitleDepthShift);
        Assert.Equal(plane, reloaded.SubtitleDepthPlane);

        // Stored is one thing and acted on is another: the request the settings describe has to
        // arrive intact, including the sign of a shift, which is the difference between a
        // caption in front of the screen and one behind it.
        var effective = reloaded.GetEffectiveSubtitleDepth();

        Assert.True(effective.Enabled);
        Assert.Equal(mode, effective.Mode);
        Assert.Equal(mode == SubtitleDepthMode.ConstantShift ? shift : 0, effective.ShiftPixels);
        Assert.Equal(mode == SubtitleDepthMode.Plane ? plane : 0, effective.Plane);
    }

    [Fact]
    public void SubtitleDepthSettingsSurviveTheAdminUiJsonRoundTrip()
    {
        // The settings endpoint binds an enum by its name, so the depth mode has to travel as
        // text in both directions - and a payload the page posts that the model cannot read
        // would come back as "flat subtitles" with a successful save.
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var configuration = new PluginConfiguration
        {
            SubtitleDepthMode = SubtitleDepthMode.Plane,
            SubtitleDepthPlane = 9
        };

        var payload = JsonSerializer.Serialize(configuration, options);
        var reloaded = JsonSerializer.Deserialize<PluginConfiguration>(payload, options);

        Assert.Contains("\"SubtitleDepthMode\":\"Plane\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"SubtitleDepthPlane\":9", payload, StringComparison.Ordinal);

        var saved = Assert.IsAssignableFrom<PluginConfiguration>(reloaded);
        Assert.Equal(SubtitleDepthMode.Plane, saved.SubtitleDepthMode);
        Assert.Equal(9, saved.SubtitleDepthPlane);
        Assert.Equal(new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 9), saved.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void ARetiredSettingInAStaleSettingsFileStillLoadsAgainstTheModel()
    {
        // What an upgrading server has on disk: a settings file from a build that still had the
        // fallback profile, the encoder policy and the depth switch. None of those is a property
        // of this model any more, and the serialiser's job is to ignore what it cannot place - so
        // the file must load with its surviving settings intact rather than fail on the extra
        // elements. That an unrecognised element is dropped is exactly why the retired depth
        // switch needs its own migration step (see PluginConfigurationMigrationTests and the load
        // path in PluginTests): a bare settings load cannot tell a switch that was off from a
        // fresh default, and lands on Automatic.
        const string StaleXml = """
            <PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <DefaultProfileId>sbs_half</DefaultProfileId>
              <FallbackProfileId>two_d_base</FallbackProfileId>
              <MaxConcurrentTranscodes>2</MaxConcurrentTranscodes>
              <EncoderPolicy>SoftwareOnly</EncoderPolicy>
              <CustomLeftEyeColor>#00FF00</CustomLeftEyeColor>
              <CustomRightEyeColor>#0000FF</CustomRightEyeColor>
              <SubtitleDepthEnabled>false</SubtitleDepthEnabled>
            </PluginConfiguration>
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(StaleXml);

        var loaded = (PluginConfiguration?)serializer.Deserialize(reader)
            ?? throw new InvalidOperationException("The stale settings XML did not load a configuration.");

        // Surviving settings are intact; the three retired elements left no trace on the model.
        Assert.Equal(ProfileIds.SideBySideHalf, loaded.DefaultProfileId);
        Assert.Equal(2, loaded.MaxConcurrentTranscodes);
        Assert.Equal("#00FF00", loaded.CustomLeftEyeColor);
        Assert.Equal("#0000FF", loaded.CustomRightEyeColor);

        // The retired switch was dropped, so the mode reads as the shipped default. This is the
        // silent-ON hazard the migration exists to close, not something this load fixes.
        Assert.Equal(SubtitleDepthMode.Automatic, loaded.SubtitleDepthMode);
    }

    [Theory]
    // The ends of the range are honoured as given: they are the travel the filter has, not an
    // approximation of it.
    [InlineData(-64)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    public void AShiftInsideTheTravelledRangeIsHonoured(int shift)
    {
        var configuration = new PluginConfiguration
        {
            SubtitleDepthMode = SubtitleDepthMode.ConstantShift,
            SubtitleDepthShift = shift
        };

        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.ConstantShift, shift, 0),
            configuration.GetEffectiveSubtitleDepth());
    }

    [Theory]
    // Past the clamp distance the filter either pulls the caption back towards the screen or
    // refuses the value, and neither is a request Anaglyfin sends: the depth is off, and the
    // stored number is left where it is for whoever narrows it.
    [InlineData(-65)]
    [InlineData(-1000)]
    [InlineData(65)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void AShiftOutsideTheTravelledRangeIsReadAsNoDepth(int shift)
    {
        var configuration = new PluginConfiguration
        {
            SubtitleDepthMode = SubtitleDepthMode.ConstantShift,
            SubtitleDepthShift = shift
        };

        Assert.Equal(SubtitleDepthSettings.Disabled, configuration.GetEffectiveSubtitleDepth());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(31)]
    public void APlaneIndexAnAuthoredDiscCanCarryIsHonoured(int plane)
    {
        var configuration = new PluginConfiguration
        {
            SubtitleDepthMode = SubtitleDepthMode.Plane,
            SubtitleDepthPlane = plane
        };

        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, plane),
            configuration.GetEffectiveSubtitleDepth());
    }

    [Theory]
    // There are at most 32 sequences to read, so a bigger index has no sequence behind it.
    [InlineData(-1)]
    [InlineData(32)]
    [InlineData(4096)]
    public void APlaneIndexOffTheAuthoredTableIsReadAsNoDepth(int plane)
    {
        var configuration = new PluginConfiguration
        {
            SubtitleDepthMode = SubtitleDepthMode.Plane,
            SubtitleDepthPlane = plane
        };

        Assert.Equal(SubtitleDepthSettings.Disabled, configuration.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void AStoredModeDecidesWhichNumberIsRead()
    {
        // The number the picked mode does not use is neither checked nor sent: it is what the
        // page keeps for the other mode, and an administrator who set a plane index and then
        // picked a constant shift is asking for the shift, not for a decision between the two.
        var shiftWithAPlaneStowed = new PluginConfiguration
        {
            SubtitleDepthMode = SubtitleDepthMode.ConstantShift,
            SubtitleDepthShift = 12,
            SubtitleDepthPlane = 99
        };

        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.ConstantShift, 12, 0),
            shiftWithAPlaneStowed.GetEffectiveSubtitleDepth());

        var planeWithAShiftStowed = new PluginConfiguration
        {
            SubtitleDepthMode = SubtitleDepthMode.Plane,
            SubtitleDepthShift = 999,
            SubtitleDepthPlane = 4
        };

        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 4),
            planeWithAShiftStowed.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void FlatModeAsksForNothingEvenWhenNumbersAreStored()
    {
        // Flat is the one dropdown position that switches the feature off, and it does so whatever
        // numbers the other modes left behind: a settings file that names a plane index and a shift
        // but picks Flat is a request for nothing, which is what lets the page keep the numbers
        // stored for the next time a mode that carries them is picked.
        var flatWithLeftoverNumbers = new PluginConfiguration
        {
            SubtitleDepthMode = SubtitleDepthMode.Flat,
            SubtitleDepthShift = 40,
            SubtitleDepthPlane = 11
        };

        Assert.Equal(SubtitleDepthSettings.Disabled, flatWithLeftoverNumbers.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void AutomaticModeCarriesNoNumberAtAll()
    {
        // Depth from the disc needs neither number, and the read side states neither - so what
        // travels towards a filter graph cannot be a number that was left in the box.
        var configuration = new PluginConfiguration
        {
            SubtitleDepthMode = SubtitleDepthMode.Automatic,
            SubtitleDepthShift = -30,
            SubtitleDepthPlane = 12
        };

        Assert.Equal(new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0), configuration.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void AModeNoNameDeclaresIsReadAsNoDepth()
    {
        // Both settings serialisers can hand back an ordinal nobody declared: the XML one puts a
        // number into an enum without asking whether anybody named it, so a settings file from a
        // future build carries a mode this build cannot honour. Flat is the answer that cannot be
        // wrong.
        var configuration = new PluginConfiguration
        {
            SubtitleDepthMode = (SubtitleDepthMode)77,
            SubtitleDepthShift = 8
        };

        Assert.False(SubtitleDepthSettings.IsDeclaredMode(configuration.SubtitleDepthMode));
        Assert.Equal(SubtitleDepthSettings.Disabled, configuration.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void SubtitleDepthHelperRejectsAMissingConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => ((PluginConfiguration)null!).GetEffectiveSubtitleDepth());
    }

    [Theory]
    [InlineData(-65, false)]
    [InlineData(-64, true)]
    [InlineData(0, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void TheShiftRangeIsTheTravelTheFilterMakes(int candidate, bool expected)
    {
        Assert.Equal(expected, SubtitleDepthSettings.IsShiftInRange(candidate));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(31, true)]
    [InlineData(32, false)]
    public void ThePlaneRangeIsTheTableAnAuthoredDiscCarries(int candidate, bool expected)
    {
        Assert.Equal(expected, SubtitleDepthSettings.IsPlaneInRange(candidate));
    }

    private static PluginConfiguration RoundTripXml(PluginConfiguration configuration)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var written = new MemoryStream();
        serializer.Serialize(written, configuration);

        written.Position = 0;
        using var read = new MemoryStream(written.ToArray());

        var reloaded = (PluginConfiguration?)serializer.Deserialize(read);

        return reloaded ?? throw new InvalidOperationException("The settings XML did not contain a configuration.");
    }

    /// <summary>
    /// Writes the settings the way the server's settings save path does and hands back the
    /// text: what a stale settings file must have shrunk to after the next save is visible
    /// in this text and nowhere else.
    /// </summary>
    private static string SerializeXml(PluginConfiguration configuration)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var written = new MemoryStream();
        serializer.Serialize(written, configuration);

        return Encoding.UTF8.GetString(written.ToArray());
    }
}
