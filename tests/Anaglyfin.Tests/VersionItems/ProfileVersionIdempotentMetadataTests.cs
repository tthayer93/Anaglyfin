using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.VersionItems;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// Covers that a library which agrees with its settings stays written: the comparisons a
/// reconciliation makes have to answer from what a row <em>says</em>, not from the order the
/// database happened to hand the rows back in, or every start-up finds a difference and rewrites
/// every version forever.
/// </summary>
/// <remarks>
/// <para>
/// The shape this was filed on: a settled cp12 library that logged "0 created, 4 updated, 0
/// removed" on every boot with nothing observable ever different through the API. The library,
/// the images and the credits were all in order; the reads were. Image rows and credit rows are
/// keyed by what they name, not by a list position, and no repository owes a caller the order it
/// wrote in - so a comparison that walked two lists index by index called the same set different
/// on every read that came back in a new order, and the pass dutifully rewrote the item to what it
/// already was. The tests below read the settled version's children back in a rotated order -
/// which is what a real query does to a real library - and require the pass to notice nothing.
/// </para>
/// <para>
/// The other half is that none of this dulls the comparisons: a row that is genuinely missing,
/// added, renamed or resized is still a difference, still repaired, and the repair still settles
/// (the repaired item answers the next read with no further write).
/// </para>
/// </remarks>
public class ProfileVersionIdempotentMetadataTests
{
    private const string SideBySideFull = ProfileIds.SideBySideFull;

    private const string SideBySideHalf = ProfileIds.SideBySideHalf;

    // --- the comparisons themselves ----------------------------------------------

    [Fact]
    public void TheSameImagesInAnotherReadOrderAreTheSameImages()
    {
        var source = ImageSource(
            "/movies/Ready Player One (2018)/poster.jpg",
            "/movies/Ready Player One (2018)/backdrop.jpg");
        var metadata = VersionItemMetadata.FromSource(source);

        // The identical set, handed back backdrop-first. The rows name what they named; only the
        // query's mood about their order changed.
        var current = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            ImageInfos = new[] { source.ImageInfos[1], source.ImageInfos[0] }
        };

        Assert.True(metadata.ImagesMatch(current));
    }

    [Fact]
    public void ASetOfImagesIsComparedAsACopyNotAsAReferenceToTheSameRows()
    {
        var source = ImageSource(
            "/movies/Ready Player One (2018)/poster.jpg",
            "/movies/Ready Player One (2018)/backdrop.jpg");
        var metadata = VersionItemMetadata.FromSource(source);

        // Same what, different objects: the rows come back from a database as their own copies,
        // and a comparison that only matched the objects the snapshot itself held would match
        // nothing real.
        var current = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            ImageInfos = new[]
            {
                ProfileVersionFixtures.CreateImage("/movies/Ready Player One (2018)/backdrop.jpg", ImageType.Backdrop),
                ProfileVersionFixtures.CreateImage("/movies/Ready Player One (2018)/poster.jpg", ImageType.Primary)
            }
        };

        Assert.True(metadata.ImagesMatch(current));
    }

    [Fact]
    public void TheNumbersTheServerDerivesFromAnImageFileAreNotWhatItsRowIsComparedOn()
    {
        var source = ImageSource("/movies/Ready Player One (2018)/poster.jpg");
        var metadata = VersionItemMetadata.FromSource(source);

        // The same image after the server recomputed its file-derived halves - a resized poster, a
        // fresh blurhash, a touched file. The row still names the same file; the numbers belong to
        // the file and no copy of them the item holds can be kept level with them.
        var current = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            ImageInfos = new[]
            {
                new ItemImageInfo
                {
                    Path = "/movies/Ready Player One (2018)/poster.jpg",
                    Type = ImageType.Primary,
                    Width = 800,
                    Height = 1200,
                    BlurHash = "recalculated",
                    DateModified = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc)
                }
            }
        };

        Assert.True(metadata.ImagesMatch(current));
    }

    [Fact]
    public void DuplicateRowsNamingOneImageAreTheSameSetInAnyOrder()
    {
        // Two backdrop rows pointing at one file is a shape the image table can hold and the item
        // model hands back. Identity is kind plus file, so the duplicates are interchangeable -
        // which is exactly what makes the answer independent of the order they came back in.
        var source = new Video
        {
            Id = Guid.NewGuid(),
            Name = "Ready Player One",
            ImageInfos = new[]
            {
                ProfileVersionFixtures.CreateImage("/movies/a/backdrop-1.jpg", ImageType.Backdrop),
                ProfileVersionFixtures.CreateImage("/movies/a/backdrop-2.jpg", ImageType.Backdrop),
                ProfileVersionFixtures.CreateImage("/movies/a/backdrop-1.jpg", ImageType.Backdrop)
            }
        };
        var metadata = VersionItemMetadata.FromSource(source);

        var rotated = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            ImageInfos = new[]
            {
                ProfileVersionFixtures.CreateImage("/movies/a/backdrop-1.jpg", ImageType.Backdrop),
                ProfileVersionFixtures.CreateImage("/movies/a/backdrop-1.jpg", ImageType.Backdrop),
                ProfileVersionFixtures.CreateImage("/movies/a/backdrop-2.jpg", ImageType.Backdrop)
            }
        };

        Assert.True(metadata.ImagesMatch(rotated));

        // Multiset, not set: two rows naming one file are not the same answer as one row naming it,
        // and one row more than the snapshot names is still a difference.
        var wrongMultiplicity = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            ImageInfos = new[]
            {
                ProfileVersionFixtures.CreateImage("/movies/a/backdrop-1.jpg", ImageType.Backdrop),
                ProfileVersionFixtures.CreateImage("/movies/a/backdrop-2.jpg", ImageType.Backdrop),
                ProfileVersionFixtures.CreateImage("/movies/a/backdrop-2.jpg", ImageType.Backdrop)
            }
        };

        Assert.False(metadata.ImagesMatch(wrongMultiplicity));
    }

    [Theory]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("renamed")]
    [InlineData("retyped")]
    public void AChangedSetOfImagesIsStillFound(string drift)
    {
        var source = ImageSource(
            "/movies/Ready Player One (2018)/poster.jpg",
            "/movies/Ready Player One (2018)/backdrop.jpg");
        var metadata = VersionItemMetadata.FromSource(source);

        ItemImageInfo[] current = drift switch
        {
            // A row the version has and the source does not name.
            "added" => new[]
            {
                source.ImageInfos[0],
                source.ImageInfos[1],
                ProfileVersionFixtures.CreateImage("/movies/Ready Player One (2018)/stranger.jpg", ImageType.Still)
            },

            // A row the version lost.
            "removed" => new[] { source.ImageInfos[0] },

            // A row naming another file.
            "renamed" => new[]
            {
                ProfileVersionFixtures.CreateImage("/movies/Ready Player One (2018)/poster-v2.jpg", ImageType.Primary),
                source.ImageInfos[1]
            },

            // A row naming the same file as what the other kind of image.
            _ => new[]
            {
                ProfileVersionFixtures.CreateImage("/movies/Ready Player One (2018)/poster.jpg", ImageType.Backdrop),
                source.ImageInfos[1]
            }
        };

        Assert.False(metadata.ImagesMatch(new Video { Id = Guid.NewGuid(), Name = "Version", ImageInfos = current }));
    }

    [Fact]
    public void TheSameCreditsInAnotherReadOrderAreTheSameCredits()
    {
        var wanted = new[]
        {
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Olivia Cooke", "Art3mis", PersonKind.Actor, 1),
            ProfileVersionFixtures.CreatePerson("Steven Spielberg", null, PersonKind.Director, 2)
        };

        // Read back last-first, each row carrying its own stored SortOrder. Where a credit sits in
        // the item's list is a value inside the row; the order the repository hands the rows over
        // in carries nothing.
        var current = new[] { wanted[2], wanted[1], wanted[0] };

        Assert.True(VersionItemCredits.Matches(current, wanted));
    }

    [Fact]
    public void DuplicateCreditsAreComparedDeterministically()
    {
        var wanted = new[]
        {
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Steven Spielberg", null, PersonKind.Director, 1)
        };

        // Two rows with the same four stored values are interchangeable, so their multiplicities
        // are compared deterministically in any read order...
        var rotated = new[] { wanted[2], wanted[0], wanted[1] };
        Assert.True(VersionItemCredits.Matches(rotated, wanted));

        // ...and a multiplicity that changed is still found: the version holding one copy of a
        // credit the source credits twice is a credit short, however the rows were ordered.
        var oneShort = new[]
        {
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Steven Spielberg", null, PersonKind.Director, 1)
        };
        Assert.False(VersionItemCredits.Matches(oneShort, wanted));

        var oneTooMany = new[]
        {
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Steven Spielberg", null, PersonKind.Director, 1)
        };
        Assert.False(VersionItemCredits.Matches(oneTooMany, wanted));

        // And it is found even when the row count never changed: three copies of one credit are
        // not two copies of it and a director, however both lists are put together.
        var movedMultiplicity = new[]
        {
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0)
        };
        Assert.False(VersionItemCredits.Matches(movedMultiplicity, wanted));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("role")]
    [InlineData("kind")]
    [InlineData("order")]
    public void AChangedCreditIsStillFound(string drift)
    {
        var wanted = new[]
        {
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Steven Spielberg", null, PersonKind.Director, 1)
        };

        var drifted = drift switch
        {
            "name" => ProfileVersionFixtures.CreatePerson("Tye Sherridan", "Parzival", PersonKind.Actor, 0),
            "role" => ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival (voice)", PersonKind.Actor, 0),
            "kind" => ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Director, 0),

            // The position travels inside the row: an item whose list order actually moved is a
            // difference, and order-insensitive comparison is not the same as order-indifferent.
            _ => ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 3)
        };

        var current = new[] { drifted, wanted[1] };

        Assert.False(VersionItemCredits.Matches(current, wanted));
    }

    [Fact]
    public void NothingToCreditIsTheSameAnswerUnderEverySpellingOfNothing()
    {
        Assert.True(VersionItemCredits.Matches(null, null));
        Assert.True(VersionItemCredits.Matches(Array.Empty<PersonInfo>(), null));
        Assert.True(VersionItemCredits.Matches(null, Array.Empty<PersonInfo>()));
        Assert.False(VersionItemCredits.Matches(
            new[] { ProfileVersionFixtures.CreatePerson("Tye Sheridan", null, PersonKind.Actor, null) },
            Array.Empty<PersonInfo>()));
    }

    [Fact]
    public void TheCreditCopyKeepsTheSourceOrderTrimsItsTextAndRefusesBlankNames()
    {
        var targetItemId = Guid.NewGuid();
        var source = new List<PersonInfo>
        {
            new() { ItemId = Guid.NewGuid(), Name = "  Tye Sheridan  ", Role = " Parzival ", Type = PersonKind.Actor, SortOrder = 0 },
            new() { ItemId = Guid.NewGuid(), Name = "   ", Type = PersonKind.Actor, SortOrder = 1 },
            new() { ItemId = Guid.NewGuid(), Name = "Steven Spielberg", Type = PersonKind.Director, SortOrder = 2 }
        };

        var copy = VersionItemCredits.Clone(source, targetItemId);

        // The copy is written in the source's own order, trimmed the way the credit write trims (a
        // copy that kept the padding could never match what comes back), and without the blank
        // name there is no person to credit.
        Assert.Equal(2, copy.Count);
        Assert.Equal("Tye Sheridan", copy[0].Name);
        Assert.Equal("Parzival", copy[0].Role);
        Assert.Equal("Steven Spielberg", copy[1].Name);
        Assert.All(copy, credit => Assert.Equal(targetItemId, credit.ItemId));

        // And a row still carrying the padding is distinguishable from the trimmed row the write
        // stores - drift repaired, not tolerated.
        Assert.False(VersionItemCredits.Matches(
            new[] { new PersonInfo { Name = "  Tye Sheridan ", Role = " Parzival ", Type = PersonKind.Actor, SortOrder = 0 } },
            new[] { new PersonInfo { Name = "Tye Sheridan", Role = "Parzival", Type = PersonKind.Actor, SortOrder = 0 } }));
    }

    // --- what a pass over a settled library does not do ---------------------------

    [Fact]
    public async Task ASettledVersionStaysUnwrittenWhenTheRepositoryReadsItsChildrenInAnotherOrder()
    {
        var store = StackedStore(out var movie, out _);
        var manager = CreateManager(store, SideBySideFull, SideBySideHalf);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);

        // The library has settled - and the database underneath it answers reads of this item's
        // image rows and credit rows in an order of its own choosing, which is its right and has
        // always been its right. Every start-up since this shape appeared has rewritten the item
        // for it.
        store.RotateChildrenFor.Add(versionId);

        var created = store.Created.Count;
        var imagesSaved = store.ImagesSaved.Count;
        var peopleSaved = store.PeopleSaved.Count;
        var streamsSaved = store.StreamsSaved.Count;
        var linked = store.Linked.Count;

        var second = await manager.ReconcileLibraryAsync(CancellationToken.None);

        // Read, and stop. Not one row rewritten: what the rows say never moved, and saying it in
        // another order is not saying something else.
        Assert.False(second.Changed);
        Assert.Equal(0, second.Created + second.Updated + second.Deleted + second.Skipped);
        Assert.Equal(created, store.Created.Count);
        Assert.Equal(imagesSaved, store.ImagesSaved.Count);
        Assert.Equal(peopleSaved, store.PeopleSaved.Count);
        Assert.Equal(streamsSaved, store.StreamsSaved.Count);
        Assert.Equal(linked, store.Linked.Count);
        Assert.Empty(store.Updated);
        Assert.Empty(store.Deleted);

        // And it stays that way for the next start too - one rotation settled nothing that a
        // second read in yet another order would unsettle.
        var third = await manager.ReconcileLibraryAsync(CancellationToken.None);
        Assert.False(third.Changed);
        Assert.Equal(imagesSaved, store.ImagesSaved.Count);
        Assert.Equal(peopleSaved, store.PeopleSaved.Count);
    }

    [Fact]
    public async Task AVersionWhoseFrameTheItemStoppedDescribingIsRepairedAndThenSettles()
    {
        var store = new FakeProfileVersionItemStore();
        var movie = store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var manager = CreateManager(store);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var versionId = VersionIdOf(movie.StaticSources[0], SideBySideFull);
        var version = (Video)store.FindItem(versionId)!;
        var video = Assert.Single(store.GetMediaStreams(versionId), stream => stream.Type == MediaStreamType.Video);

        Assert.NotNull(video.Width);
        Assert.Equal(video.Width, version.Width);
        Assert.Equal(video.Height, version.Height);

        // The item describes a different picture than its own stream reports - the frame the
        // profile encodes is on the stream and not on the item, which is what the item's details
        // and the version picker read.
        version.Width = 1080;
        version.Height = 1920;

        var repaired = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, repaired.Updated);
        Assert.Equal(video.Width, version.Width);
        Assert.Equal(video.Height, version.Height);

        // Repaired, and quiet about it afterwards: the repair writes exactly the numbers the
        // comparison compares, so the pass after it finds nothing - a frame check that rewrote
        // the item every pass would be the bug this one is fixing, only smaller.
        var settled = await manager.ReconcileLibraryAsync(CancellationToken.None);
        Assert.False(settled.Changed);
        Assert.Equal(1, store.Updated.Count);
    }

    [Fact]
    public async Task AMatchingFrameIsNoReasonToWriteAnItem()
    {
        var store = new FakeProfileVersionItemStore();
        var movie = store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        await CreateManager(store).ReconcileLibraryAsync(CancellationToken.None);

        var versionId = VersionIdOf(movie.StaticSources[0], SideBySideFull);
        var version = (Video)store.FindItem(versionId)!;
        var video = Assert.Single(store.GetMediaStreams(versionId), stream => stream.Type == MediaStreamType.Video);
        Assert.Equal(video.Width, version.Width);

        var second = await CreateManager(store).ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(0, second.Updated);
        Assert.Empty(store.Updated);
    }

    // --- what a pass still repairs -------------------------------------------------

    [Fact]
    public async Task AVersionThatLostAnImageRowGetsItBackAndOneThatGainedOneLosesIt()
    {
        var store = StackedStore(out var movie, out _);
        var manager = CreateManager(store);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);
        var version = store.FindItem(versionId)!;
        var imagesSaved = store.ImagesSaved.Count;

        // Lost: the row the source names is gone off the item.
        version.ImageInfos = new[] { version.ImageInfos[0] };

        var lost = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, lost.Updated);
        Assert.Equal(2, version.ImageInfos.Length);
        Assert.Equal(imagesSaved + 1, store.ImagesSaved.Count);

        // Gained: a row naming a file the source does not name. The mirror of the loss - the same
        // comparison answers it the same way, and the repair is the same write.
        version.ImageInfos = version.ImageInfos
            .Append(ProfileVersionFixtures.CreateImage("/movies/Ready Player One (2018)/stranger.jpg", ImageType.Still))
            .ToArray();

        var gained = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, gained.Updated);
        Assert.Equal(2, version.ImageInfos.Length);
        Assert.Equal(imagesSaved + 2, store.ImagesSaved.Count);

        // And the repaired item is settled at once: it is exactly what the source names.
        var settled = await manager.ReconcileLibraryAsync(CancellationToken.None);
        Assert.False(settled.Changed);
        Assert.Equal(imagesSaved + 2, store.ImagesSaved.Count);
    }

    [Fact]
    public async Task ASourceThatMovedOnStillMovesItsVersion()
    {
        var store = StackedStore(out var movie, out var mvc);
        var manager = CreateManager(store);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);
        var version = store.FindItem(versionId)!;

        // The library moved: a re-scraped synopsis, replaced artwork and a cast trimmed to the
        // director. Copied metadata is a copy of something with an original, and the original
        // moving is exactly when the copy has to follow.
        mvc.Overview = "In 2045, the answer was always going to be a game.";
        mvc.ImageInfos = new[] { ProfileVersionFixtures.CreateImage("/movies/Ready Player One (2018)/poster-v2.jpg", ImageType.Primary) };
        store.AddPeople(mvc.Id, new[] { ProfileVersionFixtures.CreatePerson("Steven Spielberg", null, PersonKind.Director, 0) });

        var result = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal("In 2045, the answer was always going to be a game.", version.Overview);
        Assert.Equal(new[] { "/movies/Ready Player One (2018)/poster-v2.jpg" }, version.ImageInfos.Select(image => image.Path));
        Assert.Equal(new[] { "Steven Spielberg" }, store.GetPeople(version.Id).Select(credit => credit.Name));

        // The primary keeps its own different answer throughout.
        Assert.Equal("The plain high definition cut.", movie.Overview);
    }

    // --- fixtures -----------------------------------------------------------------

    /// <summary>
    /// An item wearing exactly the given poster-and-backdrop set, as a metadata snapshot's source.
    /// </summary>
    private static Video ImageSource(params string[] paths)
    {
        var imageTypes = new[] { ImageType.Primary, ImageType.Backdrop };

        return new Video
        {
            Id = Guid.NewGuid(),
            Name = "Ready Player One",

            // What a library item always carries: the sort name the server filed it with, so the
            // snapshot read never has to re-derive one.
            SortName = "ready player one",
            ImageInfos = paths
                .Select((path, index) => ProfileVersionFixtures.CreateImage(path, imageTypes[Math.Min(index, imageTypes.Length - 1)]))
                .ToArray()
        };
    }

    /// <summary>
    /// The stacked movie with scraped metadata, artwork and cast on the hidden MVC item - the
    /// shape whose settled version reproduces the boot-log finding: the item is complete, its
    /// children are readable, and the only thing a repository can vary is the order it reads them
    /// in.
    /// </summary>
    private static FakeProfileVersionItemStore StackedStore(out ScriptedVideo movie, out Video mvc)
    {
        var store = new FakeProfileVersionItemStore();

        movie = store.AddVersionRoot(ProfileVersionFixtures.CreateStackedMvcMovie());
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        mvc = ProfileVersionFixtures.CreateMvcAlternateItem();
        store.AddItem(mvc);

        // Deliberately different from everything the scraped MVC item says, so "whose metadata"
        // always has one answer that could only have come from one of them.
        movie.Overview = "The plain high definition cut.";

        store.AddPeople(mvc.Id, new[]
        {
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Steven Spielberg", null, PersonKind.Director, 1)
        });

        return store;
    }

    private static StubConfigurationSource ConfigurationWith(params string[] enabledProfileIds)
        => new()
        {
            Configuration = new PluginConfiguration
            {
                DefaultProfileId = SideBySideFull,
                EnabledProfileIds = enabledProfileIds.ToList()
            }
        };

    /// <summary>
    /// A manager over the real catalog and settings - one profile enabled unless the test asks for
    /// more, so an <c>Updated</c> count means exactly the one version the test touched. The settled
    /// read is the one test that wants the whole enabled set in play: on a settled library, every
    /// version of every profile has to stay unwritten, not just the one being poked.
    /// </summary>
    private static ProfileVersionItemManager CreateManager(
        FakeProfileVersionItemStore store,
        params string[] enabledProfileIds)
        => new(
            store,
            new ScriptedMvcDetector(ProfileVersionFixtures.MvcPath),
            new ProfileCatalog(),
            ConfigurationWith(enabledProfileIds.Length == 0 ? new[] { SideBySideFull } : enabledProfileIds),
            NullLogger<ProfileVersionItemManager>.Instance);

    private static Guid VersionIdOf(MediaSourceInfo source, string profileId)
        => AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(source.Id, source.Path, profileId);
}
