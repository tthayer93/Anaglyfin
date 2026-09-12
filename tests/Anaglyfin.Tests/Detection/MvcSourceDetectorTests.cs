using System;
using Anaglyfin.Detection;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Anaglyfin.Tests.Detection;

/// <summary>
/// Covers the conservative MVC eligibility rules the MVP offers 3D sources on.
/// </summary>
public class MvcSourceDetectorTests
{
    private const string PlainMovie = "/movies/Avatar (2009)/Avatar (2009).mkv";

    private readonly MvcSourceDetector _detector = new();

    [Fact]
    public void DetectorIsUsableThroughItsInterface()
    {
        Assert.IsAssignableFrom<IMvcSourceDetector>(_detector);
    }

    [Fact]
    public void DetectRejectsAMissingCandidate()
    {
        Assert.Throws<ArgumentNullException>(() => _detector.Detect(null!));
    }

    [Theory]
    [InlineData("/movies/Avatar.3D.2009.1080p.BluRay.MVC.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.BluRay.mvc.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.BluRay.3DMVC.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.BluRay.MVC3D.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.BluRay.3D-MVC.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.BluRay.3d_mvc.mkv")]
    [InlineData("/movies/Avatar (2009) [3DMVC].mkv")]
    [InlineData("/movies/Avatar 3D MVC (2009).mkv")]
    [InlineData("/movies/Avatar.2009.3D.BluRay.REMUX.AVC.MVC.mkv")]
    [InlineData("D:\\Movies\\Avatar.3D.MVC.mkv")]
    [InlineData("Avatar.3D.MVC.mkv")]
    public void DetectAcceptsExplicitMvcMarkersInFileNames(string path)
    {
        var decision = _detector.Detect(MvcSourceCandidate.ForPath(path));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.FileNameDeclaresMvc, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.High, decision.Confidence);
        Assert.False(string.IsNullOrEmpty(decision.Detail));
    }

    [Fact]
    public void DetectReportsTheMatchedMvcMarker()
    {
        var decision = _detector.Detect(MvcSourceCandidate.ForPath("/movies/Avatar.3D.2009.3DMVC.mkv"));

        Assert.Equal("3DMVC", decision.Detail);
    }

    [Theory]
    [InlineData("/movies/Avatar (2009) 3D MVC")]
    [InlineData("/movies/Avatar (2009) 3D MVC/")]
    public void DetectAcceptsMvcMarkersOnAFolderRipName(string path)
    {
        // Folder based rips (Blu-ray structures, ISO mounts) are addressed by their folder,
        // so the marker sits on the folder segment rather than on a file name.
        var decision = _detector.Detect(MvcSourceCandidate.ForPath(path));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.FileNameDeclaresMvc, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.High, decision.Confidence);
    }

    [Fact]
    public void DetectAcceptsMetadataThatDeclaresTheMvcFormat()
    {
        var decision = _detector.Detect(new MvcSourceCandidate
        {
            Path = PlainMovie,
            Name = "Avatar",
            Video3DFormat = Video3DFormat.MVC
        });

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.ItemMetadataDeclaresMvc, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.High, decision.Confidence);
        Assert.Equal("Video3DFormat=MVC", decision.Detail);
    }

    [Fact]
    public void DetectAcceptsAMvcMarkerCarriedByTheItemName()
    {
        var decision = _detector.Detect(new MvcSourceCandidate
        {
            Path = PlainMovie,
            Name = "Avatar (3D MVC)"
        });

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.ItemNameDeclaresMvc, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.High, decision.Confidence);
    }

    [Theory]
    [InlineData("MVC")]
    [InlineData("mvc")]
    [InlineData("3D MVC")]
    [InlineData("3DMVC")]
    [InlineData("3D-MVC")]
    [InlineData("3D_MVC")]
    public void DetectAcceptsAMvcMarkerCarriedByAnItemTag(string tag)
    {
        var decision = _detector.Detect(new MvcSourceCandidate
        {
            Path = PlainMovie,
            Name = "Avatar",
            Tags = new[] { "3D", tag }
        });

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.ItemTagDeclaresMvc, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.High, decision.Confidence);
    }

    [Fact]
    public void DetectAcceptsAnMvcMarkerFoundOnlyInTheContainingFolders()
    {
        // Shared "3D MVC" library folders are a real layout. The marker is indirect evidence
        // there, so the item is accepted with reduced confidence.
        var decision = _detector.Detect(MvcSourceCandidate.ForPath("/movies/3D MVC/Avatar (2009).mkv"));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.DirectoryPathDeclaresMvc, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.Medium, decision.Confidence);
    }

    [Theory]
    [InlineData("/movies/Avatar.3D.2009.1080p.HSBS.MVC.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.MVC.HSBS.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.3DSBS.MVC.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.3DTAB.3DMVC.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.HalfSideBySide.MVC.mkv")]
    public void DetectAcceptsMvcMarkersThatConflictWithAStereoScopicFlag(string path)
    {
        var decision = _detector.Detect(MvcSourceCandidate.ForPath(path));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.MvcMarkerContradictsNonMvcFormat, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.Medium, decision.Confidence);
    }

    [Fact]
    public void DetectAcceptsMvcMarkersThatConflictWithDeclaredMetadata()
    {
        var decision = _detector.Detect(new MvcSourceCandidate
        {
            Path = "/movies/Avatar.3D.MVC.mkv",
            Name = "Avatar",
            Video3DFormat = Video3DFormat.HalfSideBySide
        });

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.MvcMarkerContradictsNonMvcFormat, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.Medium, decision.Confidence);
    }

    [Theory]
    [InlineData("/movies/Avatar.3D.2009.1080p.3DSBS.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.3DTAB.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.HSBS.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.FSBS.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.HTAB.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.FTAB.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.SBS.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.TAB.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.SideBySide.mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.Anaglyph.mkv")]
    public void DetectRejectsSideBySideAndTopAndBottomItemsWithoutAnMvcMarker(string path)
    {
        var decision = _detector.Detect(MvcSourceCandidate.ForPath(path));

        Assert.False(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.SideBySideOrTopAndBottomWithoutMvc, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.None, decision.Confidence);
    }

    [Theory]
    [InlineData(Video3DFormat.HalfSideBySide)]
    [InlineData(Video3DFormat.FullSideBySide)]
    [InlineData(Video3DFormat.HalfTopAndBottom)]
    [InlineData(Video3DFormat.FullTopAndBottom)]
    public void DetectRejectsMetadataDeclaringANonMvcFormat(Video3DFormat format)
    {
        var decision = _detector.Detect(new MvcSourceCandidate
        {
            Path = PlainMovie,
            Name = "Avatar",
            Video3DFormat = format
        });

        Assert.False(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.MetadataDeclaresNonMvcFormat, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.None, decision.Confidence);
    }

    [Fact]
    public void DetectRejectsMetadataDeclaringANonMvcFormatEvenWithAPlainThreeDName()
    {
        var decision = _detector.Detect(new MvcSourceCandidate
        {
            Path = "/movies/Avatar (2009) 3D.mkv",
            Name = "Avatar 3D",
            Video3DFormat = Video3DFormat.FullTopAndBottom
        });

        Assert.False(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.MetadataDeclaresNonMvcFormat, decision.Reason);
        Assert.Equal("Video3DFormat=FullTopAndBottom", decision.Detail);
    }

    [Theory]
    [InlineData("/movies/Avatar.3D.2009.1080p.BluRay.mkv")]
    [InlineData("/movies/Avatar.3DTV.1080p.mkv")]
    [InlineData("/movies/3D/Avatar (2009).mkv")]
    [InlineData("/movies/Avatar (2009) 3D/INDEX.BDM")]
    public void DetectRejectsThreeDItemsThatNeverNameAMvcFormat(string path)
    {
        var decision = _detector.Detect(MvcSourceCandidate.ForPath(path));

        Assert.False(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.ThreeDWithoutMvcMarker, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.None, decision.Confidence);
    }

    [Theory]
    [InlineData("/movies/Avatar.2009.1080p.BluRay.x264.mkv")]
    [InlineData("/movies/Avatar.2009.2D.Imax.mkv")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DetectRejectsItemsWithNoThreeDSignal(string? path)
    {
        var decision = _detector.Detect(new MvcSourceCandidate { Path = path });

        Assert.False(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.NoMvcSignal, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.None, decision.Confidence);
    }

    [Theory]
    [InlineData("/movies/Avatar.2009.1080p.x264-MVCHD.mkv")]
    [InlineData("/movies/MVCFlix/Avatar (2009).mkv")]
    [InlineData("/movies/Avatar.3D.2009.1080p.SBS.MVCless.mkv")]
    public void DetectRecordsEmbeddedMvcTextButDoesNotAcceptIt(string path)
    {
        var decision = _detector.Detect(MvcSourceCandidate.ForPath(path));

        Assert.False(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.EmbeddedMvcTextOnly, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.Low, decision.Confidence);
        Assert.False(string.IsNullOrEmpty(decision.Detail));
    }

    [Fact]
    public void DetectPrefersAnExplicitMarkerInAWeakerPositionOverFuzzyText()
    {
        // "MVCFlix" in the file name is not a marker, so the explicit marker one folder up
        // still decides the outcome.
        var decision = _detector.Detect(MvcSourceCandidate.ForPath("/movies/3D MVC/Avatar.MVCFlix.mkv"));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.DirectoryPathDeclaresMvc, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.Medium, decision.Confidence);
    }

    [Fact]
    public void DetectPrefersAMarkerOnTheItemOverOneInTheContainingFolders()
    {
        var decision = _detector.Detect(MvcSourceCandidate.ForPath("/movies/3D MVC/Avatar.3D.MVC.mkv"));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.FileNameDeclaresMvc, decision.Reason);
        Assert.Equal(MvcDetectionConfidence.High, decision.Confidence);
    }

    [Fact]
    public void DetectIgnoresTagsThatCarryNoThreeDSignal()
    {
        var decision = _detector.Detect(new MvcSourceCandidate
        {
            Path = PlainMovie,
            Name = "Avatar",
            Tags = new[] { "Fantasy", "Watched" }
        });

        Assert.False(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.NoMvcSignal, decision.Reason);
    }

    [Fact]
    public void DetectReturnsTheSameDecisionForRepeatedCallsAndSeparateInstances()
    {
        var candidate = new MvcSourceCandidate
        {
            Path = "/movies/Avatar.3D.2009.HSBS.MVC.mkv",
            Name = "Avatar",
            Tags = new[] { "3D" }
        };

        var first = _detector.Detect(candidate);
        var second = _detector.Detect(candidate);
        var otherInstance = new MvcSourceDetector().Detect(candidate);

        Assert.Equal(first.IsEligible, second.IsEligible);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Detail, second.Detail);

        Assert.Equal(first.IsEligible, otherInstance.IsEligible);
        Assert.Equal(first.Reason, otherInstance.Reason);
        Assert.Equal(first.Confidence, otherInstance.Confidence);
        Assert.Equal(first.Detail, otherInstance.Detail);
    }

    [Fact]
    public void DetectDescribesItsDecisionForLogs()
    {
        var decision = _detector.Detect(MvcSourceCandidate.ForPath("/movies/Avatar.3D.MVC.mkv"));

        Assert.Contains("eligible", decision.ToString(), StringComparison.Ordinal);
        Assert.Contains("FileNameDeclaresMvc", decision.ToString(), StringComparison.Ordinal);
        Assert.Contains("MVC", decision.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DetectAcceptsCandidatesThatOnlyKnowTheirPath()
    {
        var decision = _detector.Detect(MvcSourceCandidate.ForPath("/movies/Avatar.3D.MVC.mkv"));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.FileNameDeclaresMvc, decision.Reason);
    }

    [Fact]
    public void DetectRejectsAnEmptyCandidate()
    {
        var decision = _detector.Detect(new MvcSourceCandidate());

        Assert.False(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.NoMvcSignal, decision.Reason);
        Assert.Null(decision.Detail);
    }

    [Fact]
    public void EligibleDecisionsRequireAtLeastMediumConfidence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MvcSourceEligibility.Eligible(
            MvcEligibilityReason.FileNameDeclaresMvc,
            MvcDetectionConfidence.Low,
            "MVC"));
    }
}
