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
                ProfileVersionFixtures.CreateImage("/movies/Ready Player One (2018)/stranger.jpg", ImageType.Thumb)
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

    [Fact]
    public void TheSortNamePairIsOutOfTheComparisonAndUntouchedByTheCopy()
    {
        // cp13 root cause, stated as a comparison: a version's Name is its profile label, and the item
        // model answers an item's sort names by deriving them from that Name. The source the version
        // copies wears the movie's title-derived pair, so the pair a version reads back is never the pair
        // that was written - and comparing it is a rewrite that can never settle. It is out of the
        // contract for exactly the reason the Name is.
        var source = new Video
        {
            Id = Guid.NewGuid(),
            Name = "Ready Player One",
            OriginalTitle = "Ready Player One",
            Overview = "In 2045, the answer can be found in the OASIS.",
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        source.ForcedSortName = "Ready Player One";
        source.SortName = "ready player one";

        var metadata = VersionItemMetadata.FromSource(source);

        // The version, answering its sort pair the way the server derives it from its label. Every other
        // field agrees with the source; only the sort pair differs, and it is not compared.
        var current = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            OriginalTitle = "Ready Player One",
            Overview = "In 2045, the answer can be found in the OASIS."
        };
        current.ForcedSortName = "3d full side-by-side";
        current.SortName = "3d full side by side";

        Assert.True(metadata.FieldsMatch(current));
        Assert.Null(metadata.FieldDrift(current));

        // And the copy does not overwrite them: writing the metadata leaves the item's own sort pair
        // exactly as it found it, so a row that already holds sort values keeps them untouched.
        var target = new Video { Id = Guid.NewGuid(), Name = "3D Full Side-by-Side" };
        target.ForcedSortName = "keep me";
        target.SortName = "keep me";

        metadata.ApplyTo(target);

        Assert.Equal("keep me", target.SortName);
        Assert.Equal("keep me", target.ForcedSortName);
    }

    [Fact]
    public void TheSetLikeMetadataFieldsAreComparedAsSetsNotAsPositions()
    {
        // Genres, tags, studios and filming locations are rows keyed by the item, the same shape as the
        // image rows and the credits, so a query hands them back in its own order. Compared by position
        // that is every value having moved, and a rewrite on every pass forever.
        var source = new Video
        {
            Id = Guid.NewGuid(),
            Name = "Ready Player One",
            OriginalTitle = "Ready Player One",
            Genres = new[] { "Science Fiction", "Adventure" },
            Tags = new[] { "3D", "Virtual reality" },
            Studios = new[] { "Warner Bros.", "Legendary" },
            ProductionLocations = new[] { "London, England, USA", "Atlanta, Georgia, USA" },
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        var metadata = VersionItemMetadata.FromSource(source);

        var reordered = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            OriginalTitle = "Ready Player One",
            Genres = new[] { "Adventure", "Science Fiction" },
            Tags = new[] { "Virtual reality", "3D" },
            Studios = new[] { "Legendary", "Warner Bros." },
            ProductionLocations = new[] { "Atlanta, Georgia, USA", "London, England, USA" }
        };

        // The same values in another read order say the same thing.
        Assert.True(metadata.FieldsMatch(reordered));

        // ...and a value that genuinely moved is still a difference, named on the field it moved on.
        var oneMoreGenre = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            OriginalTitle = "Ready Player One",
            Genres = new[] { "Adventure", "Science Fiction", "Action" },
            Tags = new[] { "3D", "Virtual reality" },
            Studios = new[] { "Warner Bros.", "Legendary" },
            ProductionLocations = new[] { "London, England, USA", "Atlanta, Georgia, USA" }
        };
        Assert.False(metadata.FieldsMatch(oneMoreGenre));
        Assert.Equal(nameof(Video.Genres), metadata.FieldDrift(oneMoreGenre));

        // Order-indifferent is not indifferent: one studio gone is a difference, not a reorder.
        var oneStudioShort = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            OriginalTitle = "Ready Player One",
            Genres = new[] { "Adventure", "Science Fiction" },
            Tags = new[] { "Virtual reality", "3D" },
            Studios = new[] { "Warner Bros." },
            ProductionLocations = new[] { "Atlanta, Georgia, USA", "London, England, USA" }
        };
        Assert.False(metadata.FieldsMatch(oneStudioShort));
        Assert.Equal(nameof(Video.Studios), metadata.FieldDrift(oneStudioShort));
    }

    [Fact]
    public void AFieldDriftNamesTheOneFieldAReconcileWouldRewriteOn()
    {
        // The reason a settle that will not settle was a forensic pass instead of one debug boot: "metadata"
        // named nothing. The drift is reported by field, and by the first one, so the next hunt starts
        // at the right row.
        var source = new Video
        {
            Id = Guid.NewGuid(),
            Name = "Ready Player One",
            OriginalTitle = "Ready Player One",
            Overview = "In 2045.",
            OfficialRating = "PG-13",
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        var metadata = VersionItemMetadata.FromSource(source);

        // Nothing drifted: no field to name.
        var settled = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            OriginalTitle = "Ready Player One",
            Overview = "In 2045.",
            OfficialRating = "PG-13"
        };
        Assert.Null(metadata.FieldDrift(settled));

        // One field off: it is named.
        settled.Overview = "a synopsis the source no longer carries";
        Assert.Equal(nameof(Video.Overview), metadata.FieldDrift(settled));

        // Two fields off: the first one in the comparison's order is the one named, and it is still a
        // no-match.
        settled.OfficialRating = "R";
        Assert.Equal(nameof(Video.Overview), metadata.FieldDrift(settled));
        Assert.False(metadata.FieldsMatch(settled));
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
    public async Task AVersionWhoseSortNameTheServerReDerivesFromItsLabelSettlesAnyway()
    {
        // The cp13 signature: four version items rewritten on every boot, reason "metadata", and
        // nothing observable ever different through the API. A version's Name is its profile label; the
        // source it copies wears the movie's title-derived SortName/ForcedSortName. The item model answers
        // an item's sort name by deriving it from the item's own Name on every read, so the pair the
        // plugin wrote never round-trips - and a comparison that read it back found a drift no write
        // could settle. This is the fake being exactly that server for the version item.
        var store = StackedStore(out var movie, out var mvc);
        var manager = CreateManager(store);

        // The source really does carry the title-derived pair that used to be copied and compared.
        Assert.Equal("Ready Player One", mvc.ForcedSortName);
        Assert.Equal("ready player one", mvc.SortName);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);
        var version = (Video)store.FindItem(versionId)!;

        // A settled version is named for its profile label, and that label - not the movie title - is
        // what the server will derive its sort names from.
        Assert.Equal(new ProfileCatalog().GetProfile(SideBySideFull).DisplayName, version.Name);

        // The database under this item now re-derives its sort name from its Name on every read, and
        // hands back no forced sort name, exactly as the load path does. Before the fix this is the state
        // that rewrote the item forever: the plugin had asked it to sort under "ready player one", and
        // every read answered a label-derived sort name.
        store.ReDeriveSortNameFromNameFor.Add(versionId);

        var created = store.Created.Count;

        var second = await manager.ReconcileLibraryAsync(CancellationToken.None);

        // Read, and stop. The sort name comes back different from anything the copy wrote, and the pass
        // notices nothing - because it no longer reads that pair at all.
        Assert.False(second.Changed);
        Assert.Equal(0, second.Created + second.Updated + second.Deleted + second.Skipped);
        Assert.Equal(created, store.Created.Count);
        Assert.Empty(store.Updated);
        Assert.Empty(store.Deleted);

        // And it stays settled boot after boot: the sort name still comes back label-derived, and the
        // pass still has no reason to write, because the pair is out of the contract rather than merely
        // matched by luck.
        var third = await manager.ReconcileLibraryAsync(CancellationToken.None);
        Assert.False(third.Changed);
        Assert.Empty(store.Updated);
    }

    [Fact]
    public async Task EveryOtherCopiedMetadataFieldStillConvergesAndThenSettles()
    {
        // Dropping the sort-name pair from the copy must cost nothing else. If a field had gone
        // write-only - written every pass but never compared, so its drift never noticed - or been lost
        // outright with the pair, this pass would leave it wrong. Every remaining copied field is knocked
        // off the source's answer, repaired by one pass, and shown back on the item.
        var store = StackedStore(out var movie, out var mvc);
        var manager = CreateManager(store);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);
        var version = store.FindItem(versionId)!;

        version.Overview = "an old synopsis";
        version.OriginalTitle = "an old title";
        version.Tagline = "an old tagline";
        version.OfficialRating = "R";
        version.CustomRating = "old";
        version.HomePageUrl = "https://example.invalid/old";
        version.CommunityRating = 1f;
        version.CriticRating = 2f;
        version.ProductionYear = 1999;
        version.PremiereDate = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        version.EndDate = new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        version.Genres = new[] { "Western" };
        version.Tags = new[] { "old" };
        version.Studios = new[] { "Old Studio" };
        version.ProductionLocations = new[] { "Old Place" };
        version.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt0000000" };

        var repaired = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, repaired.Updated);

        // Every field is back on the source's answer - which, for the stacked store, could only have come
        // from the scraped MVC item and never from the 1080p root.
        Assert.Equal(mvc.Overview, version.Overview);
        Assert.Equal("Ready Player One", version.OriginalTitle);
        Assert.Equal(mvc.Tagline, version.Tagline);
        Assert.Equal(mvc.OfficialRating, version.OfficialRating);
        Assert.Equal(mvc.CustomRating, version.CustomRating);
        Assert.Equal(mvc.HomePageUrl, version.HomePageUrl);
        Assert.Equal(mvc.CommunityRating, version.CommunityRating);
        Assert.Equal(mvc.CriticRating, version.CriticRating);
        Assert.Equal(mvc.ProductionYear, version.ProductionYear);
        Assert.Equal(mvc.PremiereDate, version.PremiereDate);
        Assert.Equal(mvc.EndDate, version.EndDate);
        Assert.Equal(mvc.Genres, version.Genres);
        Assert.Equal(mvc.Tags, version.Tags);
        Assert.Equal(mvc.Studios, version.Studios);
        Assert.Equal(mvc.ProductionLocations, version.ProductionLocations);
        Assert.Equal(mvc.ProviderIds["Imdb"], version.ProviderIds["Imdb"]);
        Assert.Equal(mvc.ProviderIds["Tmdb"], version.ProviderIds["Tmdb"]);
        Assert.Equal(2, version.ProviderIds.Count);

        // And it settles straight afterwards: each field's write is exactly what the comparison compares.
        var settled = await manager.ReconcileLibraryAsync(CancellationToken.None);
        Assert.False(settled.Changed);
        Assert.Empty(store.Deleted);
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
        Assert.Single(store.Updated);
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
            .Append(ProfileVersionFixtures.CreateImage("/movies/Ready Player One (2018)/stranger.jpg", ImageType.Thumb))
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
