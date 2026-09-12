using System;

namespace Anaglyfin.Profiles;

/// <summary>
/// An 8 bit per channel RGB colour, used by the custom grayscale anaglyph profile.
/// </summary>
/// <remarks>
/// <para>
/// This type is the only representation of an admin chosen colour that leaves the
/// settings layer. Settings persist colours as text, and the profile catalog is the
/// only consumer that turns that text back into a colour, so anything that reaches a
/// profile is guaranteed to be three values in <c>0..255</c>. Arbitrary colour
/// strings never reach the command builder.
/// </para>
/// <para>
/// The accepted text form is a six digit hexadecimal triplet with an optional
/// leading <c>#</c> (<c>#RRGGBB</c> or <c>RRGGBB</c>). Anything else - including
/// colour names, <c>rgb()</c> syntax, three digit shorthands and filter syntax - is
/// rejected by <see cref="TryParse"/>.
/// </para>
/// </remarks>
public readonly struct RgbColor : IEquatable<RgbColor>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RgbColor"/> struct.
    /// </summary>
    /// <param name="red">The red channel value.</param>
    /// <param name="green">The green channel value.</param>
    /// <param name="blue">The blue channel value.</param>
    public RgbColor(byte red, byte green, byte blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    /// <summary>
    /// Gets the red channel value.
    /// </summary>
    public byte Red { get; }

    /// <summary>
    /// Gets the green channel value.
    /// </summary>
    public byte Green { get; }

    /// <summary>
    /// Gets the blue channel value.
    /// </summary>
    public byte Blue { get; }

    /// <summary>
    /// Parses a <c>#RRGGBB</c> or <c>RRGGBB</c> colour.
    /// </summary>
    /// <param name="value">The text to parse. May be null or empty.</param>
    /// <param name="color">Receives the parsed colour on success.</param>
    /// <returns><c>true</c> when the text is a valid colour; otherwise <c>false</c>.</returns>
    public static bool TryParse(string? value, out RgbColor color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text[0] == '#')
        {
            text = text[1..];
        }

        if (text.Length != 6)
        {
            return false;
        }

        byte[] channels;
        try
        {
            channels = Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            return false;
        }

        color = new RgbColor(channels[0], channels[1], channels[2]);
        return true;
    }

    /// <summary>
    /// Parses a <c>#RRGGBB</c> or <c>RRGGBB</c> colour.
    /// </summary>
    /// <param name="value">The text to parse.</param>
    /// <returns>The parsed colour.</returns>
    /// <exception cref="FormatException">The text is not a valid colour.</exception>
    public static RgbColor Parse(string? value)
        => TryParse(value, out var color)
            ? color
            : throw new FormatException($"'{value}' is not a #RRGGBB colour.");

    /// <summary>
    /// Formats the colour as a canonical <c>#RRGGBB</c> string.
    /// </summary>
    /// <returns>The canonical colour text, uppercase and always in range.</returns>
    /// <remarks>
    /// The result only ever contains <c>#</c> and uppercase hex digits, which is what
    /// makes it safe to embed in a generated FFmpeg argument later.
    /// </remarks>
    public string ToHexString()
        => "#" + Convert.ToHexString(stackalloc byte[] { Red, Green, Blue });

    /// <inheritdoc />
    public bool Equals(RgbColor other)
        => Red == other.Red && Green == other.Green && Blue == other.Blue;

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is RgbColor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => (Red << 16) | (Green << 8) | Blue;

    /// <inheritdoc />
    public override string ToString()
        => ToHexString();

    /// <summary>
    /// Equality operator.
    /// </summary>
    /// <param name="left">Left hand value.</param>
    /// <param name="right">Right hand value.</param>
    /// <returns><c>true</c> when both colours have the same channels.</returns>
    public static bool operator ==(RgbColor left, RgbColor right)
        => left.Equals(right);

    /// <summary>
    /// Inequality operator.
    /// </summary>
    /// <param name="left">Left hand value.</param>
    /// <param name="right">Right hand value.</param>
    /// <returns><c>true</c> when the colours differ in any channel.</returns>
    public static bool operator !=(RgbColor left, RgbColor right)
        => !left.Equals(right);
}
