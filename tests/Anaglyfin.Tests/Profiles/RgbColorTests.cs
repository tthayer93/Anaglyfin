using System;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.Profiles;

/// <summary>
/// Verifies the colour guard rail: only three validated bytes may travel towards an
/// encoder, and only a canonical token may leave the model.
/// </summary>
public class RgbColorTests
{
    [Theory]
    [InlineData("#FF0000", 255, 0, 0)]
    [InlineData("ff0000", 255, 0, 0)]
    [InlineData("#00ff7f", 0, 255, 127)]
    [InlineData("00FFFF", 0, 255, 255)]
    [InlineData("  #FFFFFF  ", 255, 255, 255)]
    [InlineData("#000000", 0, 0, 0)]
    public void AcceptedColoursDecodeToTheirChannels(string text, byte red, byte green, byte blue)
    {
        Assert.True(RgbColor.TryParse(text, out var color));

        Assert.Equal(red, color.Red);
        Assert.Equal(green, color.Green);
        Assert.Equal(blue, color.Blue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("red")]
    [InlineData("cyan")]
    [InlineData("FFF")]
    [InlineData("#FFF")]
    [InlineData("#FFFFF")]
    [InlineData("#FFFFFFF")]
    [InlineData("#GGGGGG")]
    [InlineData("rgb(255,0,0)")]
    [InlineData("0xFF0000")]
    [InlineData("#FF0000;")]
    [InlineData("#FF0000 -vf hstack")]
    [InlineData("arcd")]
    [InlineData("# FF0000")]
    public void AnythingElseIsRejected(string? text)
    {
        Assert.False(RgbColor.TryParse(text, out var color));
        Assert.Equal(default, color);
    }

    [Fact]
    public void ParseThrowsOnInvalidText()
    {
        Assert.Throws<FormatException>(() => RgbColor.Parse("hotpink"));
    }

    [Fact]
    public void EveryChannelStaysInsideTheByteRange()
    {
        var color = RgbColor.Parse("#040506");

        Assert.InRange(color.Red, (byte)0, (byte)255);
        Assert.InRange(color.Green, (byte)0, (byte)255);
        Assert.InRange(color.Blue, (byte)0, (byte)255);
    }

    [Fact]
    public void FormattingIsCanonicalUppercaseHex()
    {
        Assert.Equal("#FF0000", new RgbColor(255, 0, 0).ToHexString());
        Assert.Equal("#040506", new RgbColor(4, 5, 6).ToHexString());
    }

    [Fact]
    public void ParsedColoursRoundTripThroughTheirCanonicalText()
    {
        var color = RgbColor.Parse("#0a1b2c");

        var again = RgbColor.Parse(color.ToHexString());

        Assert.Equal(color, again);
        Assert.Equal("#0A1B2C", color.ToHexString());
    }

    [Fact]
    public void ColoursCompareByChannelValues()
    {
        var left = new RgbColor(1, 2, 3);

        Assert.Equal(left, new RgbColor(1, 2, 3));
        Assert.True(left == new RgbColor(1, 2, 3));
        Assert.True(left != new RgbColor(1, 2, 4));
        Assert.Equal(left.GetHashCode(), new RgbColor(1, 2, 3).GetHashCode());
        Assert.Equal("#010203", left.ToString());
    }
}
