using System;

namespace Anaglyfin.VersionItems;

/// <summary>
/// What one reconciliation pass actually changed.
/// </summary>
/// <remarks>
/// Mutable on purpose: a pass is a loop over items and each of them adds what it did. A result is
/// never shared between passes, so there is nothing to synchronise.
/// </remarks>
public sealed class ProfileVersionReconcileResult
{
    /// <summary>Gets or sets how many version items were created.</summary>
    public int Created { get; set; }

    /// <summary>Gets or sets how many version items were rewritten because they had drifted.</summary>
    public int Updated { get; set; }

    /// <summary>Gets or sets how many version items were removed as no longer wanted.</summary>
    public int Deleted { get; set; }

    /// <summary>
    /// Gets or sets how many version items were left alone for a reason worth counting: an id that
    /// belongs to something that is not Anaglyfin's, or a write that failed.
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Gets whether the pass changed the library at all.
    /// </summary>
    public bool Changed => Created > 0 || Updated > 0 || Deleted > 0;

    /// <summary>
    /// Folds another pass's counts into these.
    /// </summary>
    /// <param name="other">The result to absorb; may be <c>null</c>, which changes nothing.</param>
    public void Add(ProfileVersionReconcileResult? other)
    {
        if (other is null)
        {
            return;
        }

        Created += other.Created;
        Updated += other.Updated;
        Deleted += other.Deleted;
        Skipped += other.Skipped;
    }
}
