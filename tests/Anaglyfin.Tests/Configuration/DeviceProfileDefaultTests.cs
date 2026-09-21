using Anaglyfin.Configuration;
using Xunit;

namespace Anaglyfin.Tests.Configuration;

/// <summary>
/// Verifies how an exact-device default entry decides whether it applies to a request.
/// </summary>
/// <remarks>
/// Matching is pinned to the device id alone: this release has no client-name, user or
/// device-category matching, so every case that used to be answered by a client pin is
/// now asserted to match nothing instead.
/// </remarks>
public class DeviceProfileDefaultTests
{
    [Fact]
    public void AnEntryPinningNothingMatchesNothing()
    {
        var entry = new DeviceProfileDefault { ProfileId = "two_d_base" };

        Assert.False(entry.Matches("any-device"));
        Assert.Equal(0, entry.MatchStrength("any-device"));
        Assert.False(entry.Matches(null));
    }

    [Fact]
    public void AnEmptyDeviceIdNeverMatchesEvenAgainstARealRequest()
    {
        // The whitespace spellings of "empty" are empty too: a row an administrator added
        // and left blank must not answer for every device on the server.
        var blank = new DeviceProfileDefault { DeviceId = string.Empty, ProfileId = "sbs_half" };
        var whitespace = new DeviceProfileDefault { DeviceId = "   ", ProfileId = "sbs_half" };

        Assert.False(blank.Matches("living-room-tv"));
        Assert.Equal(0, blank.MatchStrength("living-room-tv"));
        Assert.False(whitespace.Matches("living-room-tv"));
    }

    [Fact]
    public void ADevicePinnedEntryMatchesThatDeviceOnly()
    {
        var entry = new DeviceProfileDefault { DeviceId = "living-room-tv", ProfileId = "sbs_half" };

        Assert.True(entry.Matches("living-room-tv"));
        Assert.Equal(1, entry.MatchStrength("living-room-tv"));
        Assert.False(entry.Matches("bedroom-tv"));
        Assert.False(entry.Matches(null));
        Assert.False(entry.Matches("  "));
    }

    [Fact]
    public void ADeviceEntryScoresOneAndCannotBeOutranked()
    {
        // There is no second tier above an exact device any more - no client-wide entry
        // that a device pin has to beat - so the score is the whole answer.
        var entry = new DeviceProfileDefault { DeviceId = "living-room-tv", ProfileId = "sbs_half" };

        Assert.Equal(1, entry.MatchStrength("living-room-tv"));
        Assert.Equal(0, entry.MatchStrength("someone-elses-device"));
    }

    [Fact]
    public void MatchingIgnoresCaseAndSurroundingWhitespace()
    {
        // Device ids travel from a settings file to an auth claim; both ends may pick up
        // decoration nobody meant, and the comparison is the place that forgives it.
        var entry = new DeviceProfileDefault { DeviceId = " Living-Room-TV ", ProfileId = "sbs_half" };

        Assert.True(entry.Matches("living-room-tv"));
        Assert.True(entry.Matches("LIVING-ROOM-TV"));
        Assert.True(entry.Matches(" living-room-tv "));
    }

    [Fact]
    public void ARequestWithoutADeviceIdMatchesNothing()
    {
        // Anonymous, API-key and background requests carry no usable device claim, and an
        // exact-device entry must not answer any of them: those requests fall through to
        // the global default.
        var entry = new DeviceProfileDefault { DeviceId = "living-room-tv", ProfileId = "sbs_half" };

        Assert.False(entry.Matches(null));
        Assert.False(entry.Matches(string.Empty));
        Assert.False(entry.Matches("   "));
    }
}
