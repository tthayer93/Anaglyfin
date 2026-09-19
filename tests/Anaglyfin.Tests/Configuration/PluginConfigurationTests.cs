using System;
using System.IO;
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
        Assert.Equal(ProfileIds.TwoDBase, configuration.FallbackProfileId);
        Assert.Equal(PluginConfiguration.DefaultMaxConcurrentTranscodes, configuration.MaxConcurrentTranscodes);
        Assert.Equal(1, configuration.MaxConcurrentTranscodes);
        Assert.Equal(VideoEncoderPolicy.Automatic, configuration.EncoderPolicy);
        Assert.Empty(configuration.EnabledProfileIds);
        Assert.Empty(configuration.DeviceDefaultProfiles);
        Assert.Equal("#FF0000", configuration.CustomLeftEyeColor);
        Assert.Equal("#00FFFF", configuration.CustomRightEyeColor);
        Assert.False(configuration.SubtitleDepthEnabled);
        Assert.Equal(SubtitleDepthMode.Automatic, configuration.SubtitleDepthMode);
        Assert.Equal(0, configuration.SubtitleDepthShift);
        Assert.Equal(0, configuration.SubtitleDepthPlane);
    }

    [Fact]
    public void FreshSettingsAskForNoSubtitleDepth()
    {
        // The stored defaults and the read side have to say the same thing here: a fresh
        // installation and a server reading its settings for the first time must both produce
        // "flat subtitles", and not one of them by way of a fallback.
        var configuration = new PluginConfiguration();

        Assert.False(configuration.SubtitleDepthEnabled);
        Assert.Equal(SubtitleDepthSettings.Disabled, configuration.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void SettingsOnlyReferenceAllowlistedProfileIds()
    {
        var configuration = new PluginConfiguration();
        configuration.DeviceDefaultProfiles.Add(new DeviceProfileDefault { DeviceId = "tv", ProfileId = ProfileIds.SideBySideHalf });

        Assert.True(ProfileIds.IsAllowed(configuration.DefaultProfileId));
        Assert.True(ProfileIds.IsAllowed(configuration.FallbackProfileId));
        Assert.All(configuration.DeviceDefaultProfiles, entry => Assert.True(ProfileIds.IsAllowed(entry.ProfileId)));
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
    public void EncoderPolicyStillSeparatesTheThreeChoices()
    {
        Assert.NotEqual(VideoEncoderPolicy.Automatic, VideoEncoderPolicy.HardwareOnly);
        Assert.NotEqual(VideoEncoderPolicy.HardwareOnly, VideoEncoderPolicy.SoftwareOnly);
        Assert.Equal(0, (int)VideoEncoderPolicy.Automatic);
    }

    [Fact]
    public void SettingsSurviveTheServerXmlRoundTrip()
    {
        var configuration = new PluginConfiguration
        {
            DefaultProfileId = ProfileIds.SideBySideHalf,
            FallbackProfileId = ProfileIds.SideBySideFull,
            MaxConcurrentTranscodes = 3,
            EncoderPolicy = VideoEncoderPolicy.SoftwareOnly,
            CustomLeftEyeColor = "#00FF00",
            CustomRightEyeColor = "#0000FF"
        };
        configuration.EnabledProfileIds.AddRange(new[] { ProfileIds.SideBySideFull, ProfileIds.SideBySideHalf, ProfileIds.CustomGrayscale });
        configuration.DeviceDefaultProfiles.Add(new DeviceProfileDefault
        {
            DeviceId = "living-room-tv",
            ClientName = "AndroidTV",
            ProfileId = ProfileIds.CustomGrayscale
        });
        configuration.DeviceDefaultProfiles.Add(new DeviceProfileDefault
        {
            ClientName = "Web",
            ProfileId = ProfileIds.SideBySideFull
        });

        var reloaded = RoundTripXml(configuration);

        Assert.Equal(ProfileIds.SideBySideHalf, reloaded.DefaultProfileId);
        Assert.Equal(ProfileIds.SideBySideFull, reloaded.FallbackProfileId);
        Assert.Equal(3, reloaded.MaxConcurrentTranscodes);
        Assert.Equal(VideoEncoderPolicy.SoftwareOnly, reloaded.EncoderPolicy);
        Assert.Equal("#00FF00", reloaded.CustomLeftEyeColor);
        Assert.Equal("#0000FF", reloaded.CustomRightEyeColor);
        Assert.Equal(
            new[] { ProfileIds.SideBySideFull, ProfileIds.SideBySideHalf, ProfileIds.CustomGrayscale },
            reloaded.EnabledProfileIds);

        var deviceEntry = Assert.Single(reloaded.DeviceDefaultProfiles, entry => !string.IsNullOrEmpty(entry.DeviceId));
        Assert.Equal("living-room-tv", deviceEntry.DeviceId);
        Assert.Equal("AndroidTV", deviceEntry.ClientName);
        Assert.Equal(ProfileIds.CustomGrayscale, deviceEntry.ProfileId);
        Assert.Equal(2, reloaded.DeviceDefaultProfiles.Count);
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
        Assert.Equal(ProfileIds.TwoDBase, loaded.FallbackProfileId);
        Assert.Equal(1, loaded.MaxConcurrentTranscodes);
        Assert.Equal(VideoEncoderPolicy.Automatic, loaded.EncoderPolicy);
        Assert.Empty(loaded.EnabledProfileIds);
        Assert.Empty(loaded.DeviceDefaultProfiles);

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
            EncoderPolicy = VideoEncoderPolicy.HardwareOnly,
            CustomLeftEyeColor = "#112233",
            CustomRightEyeColor = "#445566"
        };
        configuration.EnabledProfileIds.Add(ProfileIds.CustomGrayscale);
        configuration.DeviceDefaultProfiles.Add(new DeviceProfileDefault { ClientName = "Web", ProfileId = ProfileIds.SideBySideFull });

        var payload = JsonSerializer.Serialize(configuration, options);
        var reloaded = JsonSerializer.Deserialize<PluginConfiguration>(payload, options);

        Assert.NotNull(reloaded);
        Assert.Equal(ProfileIds.CustomGrayscale, reloaded!.DefaultProfileId);
        Assert.Equal(4, reloaded.MaxConcurrentTranscodes);
        Assert.Equal(VideoEncoderPolicy.HardwareOnly, reloaded.EncoderPolicy);
        Assert.Equal("#112233", reloaded.CustomLeftEyeColor);

        // The settings endpoint hands these collections back on every save, so they have
        // to travel in both directions, not just out.
        Assert.Contains("\"EnabledProfileIds\":[\"custom_grayscale\"]", payload);
        Assert.Contains("\"DeviceDefaultProfiles\":[", payload);
        Assert.Contains("\"HardwareOnly\"", payload);

        Assert.Equal(ProfileIds.CustomGrayscale, Assert.Single(reloaded.EnabledProfileIds));
        Assert.Equal("Web", Assert.Single(reloaded.DeviceDefaultProfiles).ClientName);
        Assert.Equal(ProfileIds.SideBySideFull, Assert.Single(reloaded.DeviceDefaultProfiles).ProfileId);
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
            SubtitleDepthEnabled = true,
            SubtitleDepthMode = mode,
            SubtitleDepthShift = shift,
            SubtitleDepthPlane = plane
        };

        var reloaded = RoundTripXml(configuration);

        Assert.True(reloaded.SubtitleDepthEnabled);
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
            SubtitleDepthEnabled = true,
            SubtitleDepthMode = SubtitleDepthMode.Plane,
            SubtitleDepthPlane = 9
        };

        var payload = JsonSerializer.Serialize(configuration, options);
        var reloaded = JsonSerializer.Deserialize<PluginConfiguration>(payload, options);

        Assert.Contains("\"SubtitleDepthEnabled\":true", payload, StringComparison.Ordinal);
        Assert.Contains("\"SubtitleDepthMode\":\"Plane\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"SubtitleDepthPlane\":9", payload, StringComparison.Ordinal);

        var saved = Assert.IsAssignableFrom<PluginConfiguration>(reloaded);
        Assert.True(saved.SubtitleDepthEnabled);
        Assert.Equal(SubtitleDepthMode.Plane, saved.SubtitleDepthMode);
        Assert.Equal(9, saved.SubtitleDepthPlane);
        Assert.Equal(new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 9), saved.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void ASettingsFileWrittenBeforeSubtitleDepthExistedAsksForNone()
    {
        // What an upgrading server has on disk: a settings file that names profiles, a limit
        // and colours, and has never heard of depth. It has to load as the installation that
        // was not asked for anything, because the alternative is an upgrade that starts moving
        // captions without anybody asking it to.
        const string PreDepthXml = """
            <PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <DefaultProfileId>sbs_half</DefaultProfileId>
              <FallbackProfileId>two_d_base</FallbackProfileId>
              <MaxConcurrentTranscodes>2</MaxConcurrentTranscodes>
              <EncoderPolicy>SoftwareOnly</EncoderPolicy>
              <CustomLeftEyeColor>#00FF00</CustomLeftEyeColor>
              <CustomRightEyeColor>#0000FF</CustomRightEyeColor>
            </PluginConfiguration>
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(PreDepthXml);

        var loaded = (PluginConfiguration?)serializer.Deserialize(reader)
            ?? throw new InvalidOperationException("The pre-depth settings XML did not load a configuration.");

        Assert.Equal(2, loaded.MaxConcurrentTranscodes);
        Assert.False(loaded.SubtitleDepthEnabled);
        Assert.Equal(SubtitleDepthMode.Automatic, loaded.SubtitleDepthMode);
        Assert.Equal(0, loaded.SubtitleDepthShift);
        Assert.Equal(0, loaded.SubtitleDepthPlane);
        Assert.Equal(SubtitleDepthSettings.Disabled, loaded.GetEffectiveSubtitleDepth());
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
            SubtitleDepthEnabled = true,
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
            SubtitleDepthEnabled = true,
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
            SubtitleDepthEnabled = true,
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
            SubtitleDepthEnabled = true,
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
            SubtitleDepthEnabled = true,
            SubtitleDepthMode = SubtitleDepthMode.ConstantShift,
            SubtitleDepthShift = 12,
            SubtitleDepthPlane = 99
        };

        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.ConstantShift, 12, 0),
            shiftWithAPlaneStowed.GetEffectiveSubtitleDepth());

        var planeWithAShiftStowed = new PluginConfiguration
        {
            SubtitleDepthEnabled = true,
            SubtitleDepthMode = SubtitleDepthMode.Plane,
            SubtitleDepthShift = 999,
            SubtitleDepthPlane = 4
        };

        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 4),
            planeWithAShiftStowed.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void TheNumbersAreInertUntilTheSwitchIsOn()
    {
        // "Mode is meaningful only when enabled" is the rule that lets the page keep a mode and
        // two numbers stored while the feature is off, and it is also what makes a settings file
        // full of left-over numbers a request for nothing.
        var unticked = new PluginConfiguration
        {
            SubtitleDepthEnabled = false,
            SubtitleDepthMode = SubtitleDepthMode.Plane,
            SubtitleDepthShift = 40,
            SubtitleDepthPlane = 11
        };

        Assert.Equal(SubtitleDepthSettings.Disabled, unticked.GetEffectiveSubtitleDepth());
    }

    [Fact]
    public void AutomaticModeCarriesNoNumberAtAll()
    {
        // Depth from the disc needs neither number, and the read side states neither - so what
        // travels towards a filter graph cannot be a number that was left in the box.
        var configuration = new PluginConfiguration
        {
            SubtitleDepthEnabled = true,
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
            SubtitleDepthEnabled = true,
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
}
