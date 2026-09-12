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
        Assert.Single(reloaded.EnabledProfileIds);
        Assert.Equal(ProfileIds.CustomGrayscale, Assert.Single(reloaded.EnabledProfileIds));
        Assert.Equal(ProfileIds.SideBySideFull, Assert.Single(reloaded.DeviceDefaultProfiles).ProfileId);
        Assert.Contains("\"HardwareOnly\"", payload);
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
