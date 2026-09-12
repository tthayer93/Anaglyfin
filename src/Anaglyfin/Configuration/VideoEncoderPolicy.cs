namespace Anaglyfin.Configuration;

/// <summary>
/// How Anaglyfin chooses the video encoder for a profile transcode.
/// </summary>
/// <remarks>
/// <para>
/// MVC decoding is always software work, so hardware only ever applies to the encode
/// stage of an Anaglyfin job. Ordinary Jellyfin playback keeps whatever decode path
/// the server picked; this policy is only about the encoder Anaglyfin asks for.
/// </para>
/// </remarks>
public enum VideoEncoderPolicy
{
    /// <summary>
    /// Use a hardware encoder when the configured FFmpeg offers one and fall back to
    /// software encoding when it does not.
    /// </summary>
    Automatic = 0,

    /// <summary>
    /// Use a hardware encoder only. A profile job that finds no usable hardware encoder
    /// fails instead of quietly falling back.
    /// </summary>
    HardwareOnly = 1,

    /// <summary>
    /// Always encode in software. The one choice that behaves the same on every server.
    /// </summary>
    SoftwareOnly = 2
}
