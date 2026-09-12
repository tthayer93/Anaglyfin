using Anaglyfin.Configuration;
using Xunit;

namespace Anaglyfin.Tests.Configuration;

/// <summary>
/// Verifies how a device/client override entry decides whether it applies to a request.
/// </summary>
public class DeviceProfileDefaultTests
{
    [Fact]
    public void AnEntryPinningNothingMatchesNothing()
    {
        var entry = new DeviceProfileDefault { ProfileId = "two_d_base" };

        Assert.False(entry.Matches("any-device", "Web"));
        Assert.Equal(0, entry.MatchStrength("any-device", "Web"));
        Assert.False(entry.Matches(null, null));
    }

    [Fact]
    public void ADevicePinnedEntryMatchesThatDeviceOnly()
    {
        var entry = new DeviceProfileDefault { DeviceId = "living-room-tv", ProfileId = "sbs_half" };

        Assert.True(entry.Matches("living-room-tv", "Web"));
        Assert.Equal(1, entry.MatchStrength("living-room-tv", "Web"));
        Assert.False(entry.Matches("bedroom-tv", "Web"));
        Assert.False(entry.Matches(null, "Web"));
    }

    [Fact]
    public void AClientPinnedEntryMatchesThatClientOnEveryDevice()
    {
        var entry = new DeviceProfileDefault { ClientName = "AndroidTV", ProfileId = "sbs_half" };

        Assert.True(entry.Matches("any-device", "AndroidTV"));
        Assert.Equal(1, entry.MatchStrength("any-device", "AndroidTV"));
        Assert.False(entry.Matches("any-device", "Web"));
    }

    [Fact]
    public void AnEntryPinningBothFieldsNeedsBothToMatch()
    {
        var entry = new DeviceProfileDefault { DeviceId = "living-room-tv", ClientName = "AndroidTV", ProfileId = "sbs_half" };

        Assert.Equal(2, entry.MatchStrength("living-room-tv", "AndroidTV"));
        Assert.True(entry.Matches("living-room-tv", "AndroidTV"));
        Assert.Equal(0, entry.MatchStrength("living-room-tv", "Web"));
        Assert.Equal(0, entry.MatchStrength("bedroom-tv", "AndroidTV"));
    }

    [Fact]
    public void MatchingIgnoresCaseAndSurroundingWhitespace()
    {
        var entry = new DeviceProfileDefault { DeviceId = " Living-Room-TV ", ClientName = " AndroidTV ", ProfileId = "sbs_half" };

        Assert.True(entry.Matches("living-room-tv", "androidtv"));
        Assert.True(entry.Matches("LIVING-ROOM-TV", "ANDROIDTV"));
    }

    [Fact]
    public void ARequestWithoutDeviceOrClientInformationMatchesNothing()
    {
        var deviceEntry = new DeviceProfileDefault { DeviceId = "living-room-tv", ProfileId = "sbs_half" };
        var clientEntry = new DeviceProfileDefault { ClientName = "AndroidTV", ProfileId = "sbs_half" };

        Assert.False(deviceEntry.Matches(null, null));
        Assert.False(clientEntry.Matches(null, null));
        Assert.False(clientEntry.Matches("  ", "  "));
    }
}
