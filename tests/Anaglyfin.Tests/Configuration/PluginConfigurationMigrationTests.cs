using System;
using System.IO;
using System.Xml.Serialization;
using Anaglyfin.Configuration;
using Xunit;

namespace Anaglyfin.Tests.Configuration;

/// <summary>
/// Verifies the one decision the retirement of the subtitle-depth switch turns on: what a
/// stored settings file says the new mode should be. The switch is gone from the model, so
/// the file is the only place its reading still survives, and the whole point of the probe
/// is that an upgrade which had depth switched off must not read back as the new shipped
/// default of Automatic.
/// </summary>
public class PluginConfigurationMigrationTests
{
    [Theory]
    // A legacy switch that is off - however the serialiser spelled off - is the position that
    // asks for nothing, and the reading stored beside it is dropped: the file said "not asked
    // for", and that is stronger than whatever number happened to be parked in the mode box.
    [InlineData("<SubtitleDepthEnabled>false</SubtitleDepthEnabled>", SubtitleDepthMode.Flat)]
    [InlineData("<SubtitleDepthEnabled>False</SubtitleDepthEnabled>", SubtitleDepthMode.Flat)]
    [InlineData("<SubtitleDepthEnabled>0</SubtitleDepthEnabled>", SubtitleDepthMode.Flat)]
    [InlineData("<SubtitleDepthEnabled>false</SubtitleDepthEnabled><SubtitleDepthMode>Plane</SubtitleDepthMode>", SubtitleDepthMode.Flat)]
    // A file that has neither the switch nor a mode predates the feature: it never asked for
    // depth, so it lands on the mode that asks for nothing rather than on the new default.
    [InlineData("<DefaultProfileId>sbs_half</DefaultProfileId><MaxConcurrentTranscodes>2</MaxConcurrentTranscodes>", SubtitleDepthMode.Flat)]
    // A legacy switch that is on keeps the reading stored beside it - as a name, as an ordinal,
    // and after the whitespace a hand written file may carry around both values.
    [InlineData("<SubtitleDepthEnabled>true</SubtitleDepthEnabled><SubtitleDepthMode>Automatic</SubtitleDepthMode>", SubtitleDepthMode.Automatic)]
    [InlineData("<SubtitleDepthEnabled>True</SubtitleDepthEnabled><SubtitleDepthMode>ConstantShift</SubtitleDepthMode>", SubtitleDepthMode.ConstantShift)]
    [InlineData("<SubtitleDepthEnabled>1</SubtitleDepthEnabled><SubtitleDepthMode>Plane</SubtitleDepthMode>", SubtitleDepthMode.Plane)]
    [InlineData("<SubtitleDepthEnabled>  true  </SubtitleDepthEnabled><SubtitleDepthMode>  Automatic  </SubtitleDepthMode>", SubtitleDepthMode.Automatic)]
    [InlineData("<SubtitleDepthEnabled>true</SubtitleDepthEnabled><SubtitleDepthMode>2</SubtitleDepthMode>", SubtitleDepthMode.Plane)]
    // Switched on with no reading stored, or with one no name carries, is the shipped default
    // the ordinary settings load would have given anyway - never a mode nobody asked for.
    [InlineData("<SubtitleDepthEnabled>true</SubtitleDepthEnabled>", SubtitleDepthMode.Automatic)]
    [InlineData("<SubtitleDepthEnabled>true</SubtitleDepthEnabled><SubtitleDepthMode>NowhereNearADepthMode</SubtitleDepthMode>", SubtitleDepthMode.Automatic)]
    public void ALegacyDepthSwitchDecidesTheModeTheModelShouldHold(string innerXml, SubtitleDepthMode expected)
    {
        Assert.Equal(expected, PluginConfigurationMigration.ResolveStoredSubtitleDepthMode(Settings(innerXml)));
    }

    [Fact]
    public void AFileThisBuildWroteIsLeftExactlyAsItIs()
    {
        // A settings file this build wrote states a mode and carries no legacy switch. That shape
        // is the current schema: the probe decides nothing about it, which is what keeps the load
        // step a one-time migration rather than a rewrite on every start.
        const string CurrentSchemaXml =
            "<SubtitleDepthMode>ConstantShift</SubtitleDepthMode><SubtitleDepthShift>6</SubtitleDepthShift>";

        Assert.Null(PluginConfigurationMigration.ResolveStoredSubtitleDepthMode(Settings(CurrentSchemaXml)));
    }

    [Fact]
    public void TheFileThisSerialiserActuallyWritesIsLeftExactlyAsItIs()
    {
        // The strongest form of the previous claim: run the probe against the bytes the server's
        // own serialiser writes, not against a hand-rolled guess at them. Whatever combination of
        // settings a save puts on disk, re-loading the file must ask the migration to change
        // nothing - or the migration would fire on every server's settings on the next start.
        var configuration = new PluginConfiguration
        {
            DefaultProfileId = Anaglyfin.Profiles.ProfileIds.SideBySideFull,
            SubtitleDepthMode = SubtitleDepthMode.Automatic,
            SubtitleDepthShift = 3,
            SubtitleDepthPlane = 2
        };

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var written = new MemoryStream();
        serializer.Serialize(written, configuration);
        written.Position = 0;

        using var reader = new StreamReader(written);
        var storedXml = reader.ReadToEnd();

        // Sanity: the serialiser really did record the mode, so this is a current-schema file.
        Assert.Contains("<SubtitleDepthMode>Automatic</SubtitleDepthMode>", storedXml, StringComparison.Ordinal);
        Assert.DoesNotContain("SubtitleDepthEnabled", storedXml, StringComparison.Ordinal);

        Assert.Null(PluginConfigurationMigration.ResolveStoredSubtitleDepthMode(storedXml));
    }

    [Fact]
    public void AFileWithNoDepthAtAllIsTreatedAsNeverHavingAskedForIt()
    {
        // The scaffold-era file: the settings type with nothing in it. It has neither a switch nor
        // a mode, so it is a pre-depth file and belongs on the mode that asks for nothing - not on
        // the Automatic the bare settings load would otherwise hand it.
        const string ScaffoldEraXml =
            "<PluginConfiguration xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" />";

        Assert.Equal(SubtitleDepthMode.Flat, PluginConfigurationMigration.ResolveStoredSubtitleDepthMode(ScaffoldEraXml));
    }

    [Fact]
    public void NothingThatIsNotASettingsFileThisBuildCanReadIsAnythingToMigrate()
    {
        // Every shape that is not a readable settings document answers "change nothing": the
        // ordinary settings load is the place that decides what a missing or unreadable file means,
        // and a migration that acted on guesses would rewrite files it has no business touching.
        Assert.Null(PluginConfigurationMigration.ResolveStoredSubtitleDepthMode(null));
        Assert.Null(PluginConfigurationMigration.ResolveStoredSubtitleDepthMode(string.Empty));
        Assert.Null(PluginConfigurationMigration.ResolveStoredSubtitleDepthMode("   \n  "));
        Assert.Null(PluginConfigurationMigration.ResolveStoredSubtitleDepthMode("<PluginConfiguration><SubtitleDepthMode>Plane"));
        Assert.Null(PluginConfigurationMigration.ResolveStoredSubtitleDepthMode("<NotSettings><SubtitleDepthEnabled>true</SubtitleDepthEnabled></NotSettings>"));
    }

    private static string Settings(string innerXml)
        => "<PluginConfiguration xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" + innerXml + "</PluginConfiguration>";
}
