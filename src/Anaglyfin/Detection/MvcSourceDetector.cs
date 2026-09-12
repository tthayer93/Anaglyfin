using System;
using MediaBrowser.Model.Entities;

namespace Anaglyfin.Detection;

/// <summary>
/// Conservative MVC source detection built on the filename, tag and metadata behaviour
/// Jellyfin already has.
/// </summary>
/// <remarks>
/// <para>
/// The MVP can only turn an MVC source into the 2D/anaglyph/SBS profiles, so a false
/// positive means a movie gets 3D versions it cannot render correctly. The rules therefore
/// favour missing a candidate over guessing wrong:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Video3DFormat.MVC"/> in the item metadata decides alone.</description></item>
///   <item><description>
///     An explicit MVC flag (<c>MVC</c>, <c>3DMVC</c>, <c>3D-MVC</c>, <c>3D_MVC</c>,
///     <c>MVC3D</c>) in the file or folder name, in the item name or in an item tag makes
///     the item eligible. Plain <c>3D</c> never does: it says nothing about MVC.
///   </description></item>
///   <item><description>
///     Side-by-side and top-and-bottom flags (<c>3DSBS</c>, <c>3DTAB</c>, <c>HSBS</c>,
///     <c>HTAB</c>, …) are ineligible unless the item also carries an MVC flag, in which
///     case the item is accepted with reduced confidence.
///   </description></item>
///   <item><description>
///     A flag found only in the containing folder path is indirect evidence, so it is
///     accepted with reduced confidence as well.
///   </description></item>
///   <item><description>
///     Text that merely contains <c>mvc</c> inside a longer word is recorded with
///     <see cref="MvcDetectionConfidence.Low"/> and rejected.
///   </description></item>
/// </list>
/// <para>
/// Deeper evidence (container streams, MVC depopulated tracks) belongs to a later probing
/// stage; nothing here opens a file or starts a process.
/// </para>
/// </remarks>
public sealed class MvcSourceDetector : IMvcSourceDetector
{
    private readonly MvcDetectionRules _rules = new();

    /// <inheritdoc />
    public MvcSourceEligibility Detect(MvcSourceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Video3DFormat == Video3DFormat.MVC)
        {
            return MvcSourceEligibility.Eligible(
                MvcEligibilityReason.ItemMetadataDeclaresMvc,
                MvcDetectionConfidence.High,
                DescribeVideo3DFormat(candidate.Video3DFormat));
        }

        var fileNameSignals = _rules.EvaluateText(_rules.GetItemSegment(candidate.Path));
        var itemNameSignals = _rules.EvaluateText(candidate.Name);
        var tagSignals = _rules.EvaluateAll(candidate.Tags);
        var directorySignals = _rules.EvaluateText(_rules.GetDirectorySegment(candidate.Path));

        var threeDimensionalMarker = fileNameSignals.ThreeDimensionalMarker
            ?? itemNameSignals.ThreeDimensionalMarker
            ?? tagSignals.ThreeDimensionalMarker
            ?? directorySignals.ThreeDimensionalMarker;

        var nonMvcFormatMarker = fileNameSignals.NonMvcFormatMarker
            ?? itemNameSignals.NonMvcFormatMarker
            ?? tagSignals.NonMvcFormatMarker
            ?? directorySignals.NonMvcFormatMarker;

        // Explicit markers everywhere outrank fuzzy text anywhere, and a marker on the item
        // itself outranks one in the folder it lives in.
        var marker = SelectMarker(MvcMarkerStrength.Explicit, fileNameSignals, itemNameSignals, tagSignals, directorySignals)
            ?? SelectMarker(MvcMarkerStrength.Inferred, fileNameSignals, itemNameSignals, tagSignals, directorySignals);

        if (marker is not null)
        {
            var evidence = marker.Value;

            if (evidence.Strength != MvcMarkerStrength.Explicit)
            {
                return MvcSourceEligibility.NotEligible(
                    MvcEligibilityReason.EmbeddedMvcTextOnly,
                    MvcDetectionConfidence.Low,
                    evidence.Marker);
            }

            if (candidate.Video3DFormat is not null || nonMvcFormatMarker is not null)
            {
                return MvcSourceEligibility.Eligible(
                    MvcEligibilityReason.MvcMarkerContradictsNonMvcFormat,
                    MvcDetectionConfidence.Medium,
                    evidence.Marker);
            }

            return MvcSourceEligibility.Eligible(evidence.Reason, evidence.Confidence, evidence.Marker);
        }

        if (candidate.Video3DFormat is not null)
        {
            return MvcSourceEligibility.NotEligible(
                MvcEligibilityReason.MetadataDeclaresNonMvcFormat,
                MvcDetectionConfidence.None,
                DescribeVideo3DFormat(candidate.Video3DFormat));
        }

        if (nonMvcFormatMarker is not null)
        {
            return MvcSourceEligibility.NotEligible(
                MvcEligibilityReason.SideBySideOrTopAndBottomWithoutMvc,
                MvcDetectionConfidence.None,
                nonMvcFormatMarker);
        }

        if (threeDimensionalMarker is not null)
        {
            return MvcSourceEligibility.NotEligible(
                MvcEligibilityReason.ThreeDWithoutMvcMarker,
                MvcDetectionConfidence.None,
                threeDimensionalMarker);
        }

        return MvcSourceEligibility.NotEligible(MvcEligibilityReason.NoMvcSignal);
    }

    private static MarkerEvidence? SelectMarker(
        MvcMarkerStrength strength,
        TokenSignals fileName,
        TokenSignals itemName,
        TokenSignals tags,
        TokenSignals directoryPath)
    {
        if (fileName.MvcStrength == strength)
        {
            return new MarkerEvidence(strength, MvcEligibilityReason.FileNameDeclaresMvc, MvcDetectionConfidence.High, fileName.MvcMarker);
        }

        if (itemName.MvcStrength == strength)
        {
            return new MarkerEvidence(strength, MvcEligibilityReason.ItemNameDeclaresMvc, MvcDetectionConfidence.High, itemName.MvcMarker);
        }

        if (tags.MvcStrength == strength)
        {
            return new MarkerEvidence(strength, MvcEligibilityReason.ItemTagDeclaresMvc, MvcDetectionConfidence.High, tags.MvcMarker);
        }

        if (directoryPath.MvcStrength == strength)
        {
            return new MarkerEvidence(strength, MvcEligibilityReason.DirectoryPathDeclaresMvc, MvcDetectionConfidence.Medium, directoryPath.MvcMarker);
        }

        return null;
    }

    private static string? DescribeVideo3DFormat(Video3DFormat? video3DFormat)
        => video3DFormat is null ? null : "Video3DFormat=" + Enum.GetName(video3DFormat.Value);

    /// <summary>
    /// Where an MVC marker was found and what it is worth.
    /// </summary>
    private readonly struct MarkerEvidence
    {
        internal MarkerEvidence(
            MvcMarkerStrength strength,
            MvcEligibilityReason reason,
            MvcDetectionConfidence confidence,
            string? marker)
        {
            Strength = strength;
            Reason = reason;
            Confidence = confidence;
            Marker = marker;
        }

        internal MvcMarkerStrength Strength { get; }

        internal MvcEligibilityReason Reason { get; }

        internal MvcDetectionConfidence Confidence { get; }

        internal string? Marker { get; }
    }
}
