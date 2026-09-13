using System;
using Anaglyfin.Detection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Anaglyfin.Tests.Detection;

/// <summary>
/// Covers how the signals an <see cref="IMvcSourceDetector"/> consumes are read off a
/// Jellyfin item and off one of its media sources, which are the two things the media
/// source provider hands it.
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

    [Fact]
    public void FromMediaSourceCarriesTheFormatThatSourceDeclares()
    {
        // A stacked movie declares its 3D on the version's own media source; reading the item
        // instead would ask about the wrong file and answer for the wrong file.
        var source = new MediaSourceInfo
        {
            Id = "b05781ed-3d3a-4bf0-a3bd-7f7a1e56b41f",
            Name = "3D mvc",
            Path = "/movies/Ready Player One (2018)/Ready Player One (2018) - 3D mvc.mkv",
            Video3DFormat = Video3DFormat.MVC
        };

        var decision = _detector.Detect(MvcSourceCandidate.FromMediaSource(source));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.ItemMetadataDeclaresMvc, decision.Reason);
    }

    [Fact]
    public void FromMediaSourceReadsTheLabelTheServerGaveThatFile()
    {
        // A local alternate version is named for what distinguishes it, which is exactly where
        // the 3D marker of a version lives.
        var source = new MediaSourceInfo
        {
            Id = "b05781ed-3d3a-4bf0-a3bd-7f7a1e56b41f",
            Name = "3D mvc",
            Path = "/movies/Ready Player One (2018)/Ready Player One (2018).mkv"
        };

        var decision = _detector.Detect(MvcSourceCandidate.FromMediaSource(source));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.ItemNameDeclaresMvc, decision.Reason);
    }

    [Fact]
    public void FromMediaSourceDoesNotInheritTheTagsItWasNotGiven()
    {
        // A media source carries no tags of its own and the item's tags describe the item's own
        // file, so a caller that wanted a sibling version judged must say so by giving no tags:
        // the alternative is one file's metadata making another file 3D.
        var source = new MediaSourceInfo
        {
            Name = "1080p",
            Path = "/movies/Ready Player One (2018)/Ready Player One (2018) - 1080p.mkv"
        };

        var decision = _detector.Detect(MvcSourceCandidate.FromMediaSource(source));

        Assert.False(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.NoMvcSignal, decision.Reason);
    }

    [Fact]
    public void FromMediaSourceUsesTheTagsOfTheItemThatOwnsIt()
    {
        // The other half of that call: asked about the source that IS the item, the caller hands
        // over the item's tags, and they count exactly as they do through FromItem.
        var source = new MediaSourceInfo
        {
            Name = "Avatar",
            Path = "/movies/Avatar (2009)/Avatar (2009).mkv"
        };

        var decision = _detector.Detect(MvcSourceCandidate.FromMediaSource(source, new[] { "3D MVC" }));

        Assert.True(decision.IsEligible);
        Assert.Equal(MvcEligibilityReason.ItemTagDeclaresMvc, decision.Reason);
    }

    [Fact]
    public void FromMediaSourceRejectsAMissingSource()
    {
        Assert.Throws<ArgumentNullException>(() => MvcSourceCandidate.FromMediaSource(null!));
    }
}
