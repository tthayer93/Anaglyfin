using System;
using Anaglyfin.Detection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Anaglyfin.Tests.Detection;

/// <summary>
/// Covers how the signals an <see cref="IMvcSourceDetector"/> consumes are read off a
/// Jellyfin item, which is what the media source provider will hand it.
/// </summary>
public class MvcSourceCandidateTests
{
    private readonly MvcSourceDetector _detector = new();

    [Fact]
    public void FromItemCarriesTheFormatDeclaredByVideoMetadata()
    {
        var item = new Video
        {
            Path = "/movies/Avatar (2009)/Avatar (2009).mkv",
            Name = "Avatar",
            Tags = new[] { "3D" },
            Video3DFormat = Video3DFormat.MVC
        };

        var decision = _detector.Detect(MvcSourceCandidate.FromItem(item));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.ItemMetadataDeclaresMvc, decision.Reason);
    }

    [Fact]
    public void FromItemFallsBackToFileNameSignalsWhenNoFormatIsDeclared()
    {
        var item = new Movie
        {
            Path = "/movies/Avatar.3D.2009.1080p.BluRay.MVC.mkv",
            Name = "Avatar"
        };

        var decision = _detector.Detect(MvcSourceCandidate.FromItem(item));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.FileNameDeclaresMvc, decision.Reason);
    }

    [Fact]
    public void FromItemReadsTagsFromAnyLibraryItem()
    {
        // Tags exist on BaseItem, so an item Jellyfin models as a folder is still detectable
        // by the signals it does have.
        var item = new Folder
        {
            Path = "/movies/Avatar (2009)",
            Name = "Avatar",
            Tags = new[] { "3D MVC" }
        };

        var decision = _detector.Detect(MvcSourceCandidate.FromItem(item));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.ItemTagDeclaresMvc, decision.Reason);
    }

    [Fact]
    public void FromItemRejectsAMissingItem()
    {
        Assert.Throws<ArgumentNullException>(() => MvcSourceCandidate.FromItem(null!));
    }
}
