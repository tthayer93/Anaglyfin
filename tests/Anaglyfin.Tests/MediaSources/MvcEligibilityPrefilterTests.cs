using System;
using System.Linq;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
using Anaglyfin.MediaSources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Anaglyfin.Tests.MediaSources;

/// <summary>
/// Covers the cheap eligibility question on its own: what it refuses, what it passes, and what it
/// refuses to answer.
/// </summary>
/// <remarks>
/// <para>
/// The property these tests hold is a one-way one. Every refusal has to be a case the expensive scan
/// would have refused too, because a wrong refusal costs a movie its versions and nothing downstream
/// ever notices - so each accepted case here is checked against the same
/// <see cref="MvcSourceDetector"/> the scan consults, rather than against an expectation of what the
/// rules feel like. The passes, on the other hand, are allowed to be wrong in the expensive direction:
/// an item that cannot be refused cheaply is asked at the price of one media-source read, which is the
/// cost this class exists to move rather than to eliminate.
/// </para>
/// <para>
/// Every item here names a rooted file and declares the video type a scanned file carries, so that a
/// refusal is about the 3D signals and not about the item having no playable file at all.
/// </para>
/// </remarks>
public class MvcEligibilityPrefilterTests
{
    private const string Folder = "/movies/Ready Player One (2018)";

    private const string PlainPath = Folder + "/Ready Player One (2018) - 1080p.mkv";

    private const string MvcPath = Folder + "/Ready Player One (2018) - 3D mvc.mkv";

    private static readonly IMvcSourceDetector Detector = new MvcSourceDetector();

    [Fact]
    public void ACallerWithoutRulesIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => MvcEligibilityPrefilter.IsCheapCandidate(Video(""), null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnItemWithoutAFileIsRefused(string? path)
    {
        // Nothing to name and nothing to describe: the scan would refuse this file as soon as it was
        // asked, so the cheap answer simply declines to ask.
        Assert.False(MvcEligibilityPrefilter.IsCheapCandidate(Video(path, video3DFormat: Video3DFormat.MVC), Detector));
    }

    [Fact]
    public void ThingsThatAreNotVideosAreRefused()
    {
        // A track whose file says MVC in its name still has no 3D question: the format this reads is a
        // claim only a video item can make, and no media type string turns one into the other.
        Assert.False(MvcEligibilityPrefilter.IsCheapCandidate(new Audio { Id = Guid.NewGuid(), Name = "Ready Player One 3D mvc", Path = "/music/A/3D mvc.mp3" }, Detector));
        Assert.False(MvcEligibilityPrefilter.IsCheapCandidate(new Folder { Id = Guid.NewGuid(), Name = "Movies", Path = "/movies" }, Detector));
        Assert.False(MvcEligibilityPrefilter.IsCheapCandidate(null, Detector));
    }

    [Theory]
    [InlineData("/movies/Elsewhere/Elsewhere.strm")]
    [InlineData("relative/3D mvc.mkv")]
    [InlineData("3D mvc.mkv")]
    public void AFileTheMarkerCouldNeverNameIsRefused(string path)
    {
        // The structural refusals belong to the scanner and are repeated here deliberately: a pointer
        // file and a relative path are refused whether or not the item next to them shouts MVC, and
        // the earlier half has no business inventing its own list of what counts as a file.
        Assert.False(MvcEligibilityPrefilter.IsCheapCandidate(Video(path, video3DFormat: Video3DFormat.MVC), Detector));
    }

    [Theory]
    [InlineData(VideoType.Iso)]
    [InlineData(VideoType.Dvd)]
    [InlineData(VideoType.BluRay)]
    public void ADiscIsRefused(VideoType videoType)
    {
        // A disc reaches FFmpeg through arguments the marker contract does not carry, and the stereo
        // format of its main feature is somebody else's question.
        Assert.False(MvcEligibilityPrefilter.IsCheapCandidate(Video(PlainPath, videoType: videoType, video3DFormat: Video3DFormat.MVC), Detector));
    }

    [Fact]
    public void AnItemOfAnaglyfinsOwnIsAskedAboutForTheJanitor()
    {
        // A version is never the owner of versions, but an item whose path is one of our markers is
        // still not ignorable: it has to reach the janitor so a healthy primary can be reconciled or
        // an orphan can be removed. The manager's janitor branch decides what happens next; this answer
        // only refuses to discard the item.
        var version = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            Path = ProfileMarker.MarkerPrefix + "sbs_full?source=%2Fmovies%2FReady%20Player%20One%20-%203D%20mvc.mkv",
            VideoType = VideoType.VideoFile,
            Video3DFormat = Video3DFormat.MVC
        };

        Assert.True(MvcEligibilityPrefilter.IsCheapCandidate(version, Detector));
    }

    [Fact]
    public void AnItemThatDeclaresMvcIsAccepted()
    {
        var item = Video(MvcPath, video3DFormat: Video3DFormat.MVC);

        // Strip every naming signal off this item and the declaration alone still decides: this is the
        // case a library carries no words about, and it is the one the server's 3D query exists for.
        item.Name = "Movie";
        item.Path = "/movies/Movie/Movie.mkv";

        Assert.True(MvcEligibilityPrefilter.IsCheapCandidate(item, Detector));
    }

    [Theory]
    [InlineData(Video3DFormat.HalfSideBySide)]
    [InlineData(Video3DFormat.FullSideBySide)]
    [InlineData(Video3DFormat.HalfTopAndBottom)]
    [InlineData(Video3DFormat.FullTopAndBottom)]
    public void AFormatTheScannerDoesNotConvertIsRefused(Video3DFormat format)
    {
        // The 3D query answers "a stereo format is recorded", which is a superset: a shelf of half-SBS
        // rips comes back from it, and this is the half that reads which format it actually is. The
        // MVP converts MVC alone, so the rest cost a pass nothing.
        Assert.False(MvcEligibilityPrefilter.IsCheapCandidate(Video(PlainPath, video3DFormat: format), Detector));
    }

    [Fact]
    public void AFormatTheScannerDoesNotConvertIsAcceptedWhenTheItemAlsoSaysMvc()
    {
        // The contradiction a person creates by hand - a file tagged MVC wearing a Side-by-Side
        // declaration - is accepted with reduced confidence by the scanner, so the cheap answer accepts
        // it too. Whatever the scanner would ask expensively, the prefilter may not refuse first.
        var item = Video(PlainPath, video3DFormat: Video3DFormat.HalfSideBySide);
        item.Tags = new[] { "3D MVC" };

        Assert.True(MvcEligibilityPrefilter.IsCheapCandidate(item, Detector));
    }

    [Theory]
    [InlineData("Ready Player One (2018) - 3D mvc.mkv")]
    [InlineData("Ready Player One (2018) - 3DMVC.mkv")]
    [InlineData("Ready Player One (2018) - MVC.mkv")]
    public void AFileNamedForMvcIsAccepted(string fileName)
    {
        Assert.True(MvcEligibilityPrefilter.IsCheapCandidate(Video($"/movies/Ready Player One (2018)/{fileName}"), Detector));
    }

    [Theory]
    [InlineData("3D MVC")]
    [InlineData("mvc")]
    [InlineData("3DMVC")]
    [InlineData("3d-mvc")]
    public void ATagThatMeansMvcIsAccepted(string tag)
    {
        Assert.True(MvcEligibilityPrefilter.IsCheapCandidate(Video(PlainPath, tags: new[] { tag }), Detector));
    }

    [Theory]
    [InlineData("3D")]
    [InlineData("3D, Virtual reality")]
    [InlineData("Side by side")]
    [InlineData("Anaglyph")]
    [InlineData("Remastered")]
    public void AWordThatDoesNotNameMvcIsRefused(string tags)
    {
        // The ordinary movie a scraped library hands over: a "3D" tag from whoever tagged the shelf, a
        // format word for something the MVP does not convert, or nothing at all. None of it is a
        // reason to read a media source, and a plain "3D" in particular says nothing about MVC - the
        // rule that keeps a library of 3D-labelled rips from being scanned on every start-up.
        Assert.False(MvcEligibilityPrefilter.IsCheapCandidate(Video(PlainPath, tags: tags.Split(", ", StringSplitOptions.RemoveEmptyEntries)), Detector));
    }

    [Fact]
    public void TextThatMerelyContainsMvcIsRefused()
    {
        // A word with mvc inside it is the detector's low-confidence case, refused there on purpose, and
        // the cheap answer inherits the refusal rather than rounding up.
        Assert.False(MvcEligibilityPrefilter.IsCheapCandidate(Video("/movies/Movie/Movie rmvc.mkv"), Detector));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnItemNamingOtherVersionsCannotBeRefusedFromItsOwnFields(bool namesLocal, bool namesLinked)
    {
        // The stacked case, which is the whole product: a movie browsed to over its plain file, with the
        // MVC rip filed as one of its versions - either the scanned shape, where the primary records the
        // sibling paths, or the hand-merged shape, where it names the version item. Both files of that
        // movie carry their signals themselves, and a refusal here would lose the versions of a file this
        // item has never been asked about.
        var movie = Video(PlainPath);

        if (namesLocal)
        {
            movie.LocalAlternateVersions = new[] { MvcPath };
        }

        if (namesLinked)
        {
            movie.LinkedAlternateVersions = new[]
            {
                new LinkedChild { ItemId = Guid.NewGuid(), Type = LinkedChildType.LinkedAlternateVersion }
            };
        }

        Assert.True(MvcEligibilityPrefilter.IsCheapCandidate(movie, Detector));
    }

    [Fact]
    public void AnOrdinaryMovieAloneIsRefused()
    {
        // The same item with the versions taken away: nothing of its own says MVC and nothing else is
        // claimed to exist, which is the state a refusal is actually owed.
        Assert.False(MvcEligibilityPrefilter.IsCheapCandidate(Video(PlainPath), Detector));
    }

    private static Video Video(
        string? path,
        VideoType videoType = VideoType.VideoFile,
        Video3DFormat? video3DFormat = null,
        string[]? tags = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Ready Player One (2018)",
            Path = path ?? string.Empty,
            VideoType = videoType,
            Video3DFormat = video3DFormat,
            Tags = tags ?? Array.Empty<string>()
        };
}
