using System;

namespace Anaglyfin.Detection;

/// <summary>
/// The lightweight decision returned by <see cref="IMvcSourceDetector"/>.
/// </summary>
/// <remarks>
/// It carries just enough for a caller to act and to explain itself: whether the item
/// may be offered 3D MVC sources, why, how strongly the evidence supports that, and the
/// marker text that was matched. Nothing in here is cached or item scoped, so callers are
/// free to log it and throw it away.
/// </remarks>
public sealed class MvcSourceEligibility
{
    private readonly string? _detail;

    private MvcSourceEligibility(
        bool isEligible,
        MvcEligibilityReason reason,
        MvcDetectionConfidence confidence,
        string? detail)
    {
        IsEligible = isEligible;
        Reason = reason;
        Confidence = confidence;
        _detail = detail;
    }

    /// <summary>
    /// Gets a value indicating whether Anaglyfin may offer 3D sources for this item.
    /// </summary>
    public bool IsEligible { get; }

    /// <summary>
    /// Gets the signal that decided the outcome.
    /// </summary>
    public MvcEligibilityReason Reason { get; }

    /// <summary>
    /// Gets the strength of the matched evidence.
    /// </summary>
    /// <remarks>
    /// Confidence always describes the strength of the <em>MVC</em> evidence found, also
    /// when the outcome is negative: a rejection therefore reports <see cref="None"/> or
    /// <see cref="MvcDetectionConfidence.Low"/>.
    /// </remarks>
    public MvcDetectionConfidence Confidence { get; }

    /// <summary>
    /// Gets the marker text that was matched, when a marker decided the outcome.
    /// </summary>
    public string? Detail => _detail;

    /// <summary>
    /// Creates an accepting decision.
    /// </summary>
    /// <param name="reason">The deciding signal.</param>
    /// <param name="confidence">The strength of the matched MVC evidence.</param>
    /// <param name="detail">Optional marker text that was matched.</param>
    /// <returns>The accepting decision.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="confidence"/> is below <see cref="MvcDetectionConfidence.Medium"/>.
    /// </exception>
    public static MvcSourceEligibility Eligible(
        MvcEligibilityReason reason,
        MvcDetectionConfidence confidence,
        string? detail = null)
    {
        if (confidence < MvcDetectionConfidence.Medium)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence),
                confidence,
                "An eligible decision requires at least medium confidence.");
        }

        return new MvcSourceEligibility(true, reason, confidence, detail);
    }

    /// <summary>
    /// Creates a rejecting decision.
    /// </summary>
    /// <param name="reason">The deciding signal.</param>
    /// <param name="confidence">The strength of any MVC evidence that was found.</param>
    /// <param name="detail">Optional marker text that was matched.</param>
    /// <returns>The rejecting decision.</returns>
    public static MvcSourceEligibility NotEligible(
        MvcEligibilityReason reason,
        MvcDetectionConfidence confidence = MvcDetectionConfidence.None,
        string? detail = null)
        => new(false, reason, confidence, detail);

    /// <inheritdoc />
    public override string ToString()
    {
        var outcome = IsEligible ? "eligible" : "not eligible";
        return string.IsNullOrEmpty(_detail)
            ? $"{outcome}: {Reason} ({Confidence})"
            : $"{outcome}: {Reason} ({Confidence}) [{_detail}]";
    }
}
