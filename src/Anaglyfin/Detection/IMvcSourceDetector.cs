namespace Anaglyfin.Detection;

/// <summary>
/// Decides whether an item is a safe-enough 3D MVC candidate for the MVP, where the only
/// supported source format is MVC.
/// </summary>
/// <remarks>
/// Implementations must be cheap and side effect free: the alternate media source provider
/// runs this on every playback request, and the server swallows provider exceptions rather
/// than reporting them, so detection may not probe files or reach the network.
/// </remarks>
public interface IMvcSourceDetector
{
    /// <summary>
    /// Evaluates one item against the MVC eligibility rules.
    /// </summary>
    /// <param name="candidate">The signals read off the item.</param>
    /// <returns>
    /// The decision, its reason and the confidence of the matched evidence. Never <c>null</c>.
    /// </returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="candidate"/> is <c>null</c>.</exception>
    MvcSourceEligibility Detect(MvcSourceCandidate candidate);
}
