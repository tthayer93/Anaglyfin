using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
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
/// Covers what a profile version item says about the movie it converts: the metadata, the artwork
/// and the credits it takes from the item that owns the file, and what it does when any of that
/// stops being true.
/// </summary>
/// <remarks>
/// <para>
/// A version is the same movie under a different picture, and the details panel is where a user finds
/// that out. cp11 gave the version items their id, their streams and their marker; everything a
/// scrape had put on the library was left behind, so the panel of a version was empty while the panel
/// of the MVC file beside it was full. Every test here is a statement about that copy: where the
/// values come from (the item that owns the file, which for a stack is not the item the versions are
/// offered under), that they are copies rather than shares, that they are repaired when the original
/// moves, and that copying them costs no file.
/// </para>
/// <para>
/// The library is the same in-memory fake the rest of the version-item tests use. That is the point:
/// the whole of what a metadata pass can reach is <see cref="IProfileVersionItemStore"/>, and nothing
/// in it is a file, a metadata saver or a provider. A test that needs no disk is a test that proves
/// no disk is touched.
/// </para>
/// </remarks>
public class ProfileVersionItemMetadataTests
{
    private const string SideBySideFull = ProfileIds.SideBySideFull;

    private const string SideBySideHalf = ProfileIds.SideBySideHalf;

    private const string TwoDBase = ProfileIds.TwoDBase;

    // --- what a version is wearing when it is born ------------------------------------

    [Fact]
    public async Task AVersionWearsTheMetadataOfTheItemThatOwnsTheFileItConverts()
    {
        var store = StackedStore(out var movie, out var mvc);

        var result = await CreateManager(store).ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Created);

        var version = Assert.IsType<Video>(store.FindItem(VersionIdOf(movie.StaticSources[1], SideBySideFull)));

        // Everything the movie's panel shows, off the MVC item - which is the item somebody scraped,
        // and not the 1080p root the versions happen to be offered under.
        Assert.Equal(mvc.Overview, version.Overview);
        Assert.Equal(mvc.Tagline, version.Tagline);
        Assert.Equal(mvc.Genres, version.Genres);
        Assert.Equal(mvc.Tags, version.Tags);
        Assert.Equal(mvc.Studios, version.Studios);
        Assert.Equal(mvc.ProductionLocations, version.ProductionLocations);
        Assert.Equal(mvc.OfficialRating, version.OfficialRating);
        Assert.Equal(mvc.CustomRating, version.CustomRating);
        Assert.Equal(mvc.CommunityRating, version.CommunityRating);
        Assert.Equal(mvc.CriticRating, version.CriticRating);
        Assert.Equal(mvc.ProductionYear, version.ProductionYear);
        Assert.Equal(mvc.PremiereDate, version.PremiereDate);
        Assert.Equal(mvc.EndDate, version.EndDate);
        Assert.Equal(mvc.HomePageUrl, version.HomePageUrl);

        // The same answer, and demonstrably not the root's: the two items were given different values
        // for every field, so a version that had copied the primary would fail on the first of them.
        Assert.NotEqual(movie.Overview, version.Overview);
        Assert.NotEqual(movie.Genres, version.Genres);
        Assert.NotEqual(movie.ProductionYear, version.ProductionYear);
        Assert.Equal(new[] { "Documentary" }, movie.Genres);

        // The title travels as the original title, because the version's own name is the label the
        // stock version picker shows - and the panel still has to be able to say which movie this is.
        Assert.Equal(new ProfileCatalog().GetProfile(SideBySideFull).DisplayName, version.Name);
        Assert.Equal("Ready Player One", version.OriginalTitle);
        Assert.Equal("Ready Player One (2018) [1080p]", movie.OriginalTitle);

        // The sort-name pair is deliberately NOT copied. A version's Name is its profile label and the
        // server derives SortName/ForcedSortName from that name, so the source's title-derived pair
        // never round-trips back through the item - copying and comparing it is the rewrite-on-every-
        // boot this excludes. Left alone, the version sorts under its label, so what it answers for its
        // sort name is not the source's, and its forced sort name is whatever the server set (nothing).
        Assert.NotEqual(mvc.SortName, version.SortName);
        Assert.NotEqual(mvc.ForcedSortName, version.ForcedSortName);
    }

    [Fact]
    public async Task AVersionCarriesTheSourceProvidersIdsAndItsArtworkAndItsCast()
    {
        var store = StackedStore(out var movie, out var mvc);

        await CreateManager(store).ReconcileLibraryAsync(CancellationToken.None);

        var version = Assert.IsType<Video>(store.FindItem(VersionIdOf(movie.StaticSources[1], SideBySideFull)));

        // The provider ids are how a client links the version to the same record the movie is linked
        // to; without them a version is an island.
        Assert.Equal(mvc.ProviderIds["Imdb"], version.ProviderIds["Imdb"]);
        Assert.Equal(mvc.ProviderIds["Tmdb"], version.ProviderIds["Tmdb"]);
        Assert.DoesNotContain(movie.ProviderIds["Imdb"], version.ProviderIds.Values, StringComparer.Ordinal);

        // Artwork: the version's image rows name the files the source's rows name. Nothing was
        // downloaded and nothing was copied next to the movie.
        Assert.Equal(mvc.ImageInfos.Select(image => image.Path), version.ImageInfos.Select(image => image.Path));
        Assert.Equal(mvc.ImageInfos.Select(image => image.Type), version.ImageInfos.Select(image => image.Type));

        var savedImages = Assert.Single(store.ImagesSaved);
        Assert.Equal(version.Id, savedImages.ItemId);
        Assert.Equal(mvc.ImageInfos.Select(image => image.Path), savedImages.Paths);

        // And the cast, credited under the version's own id: the people are the source's people, so
        // the credits do not start a second copy of anybody.
        var credits = store.GetPeople(version.Id);
        Assert.Equal(2, credits.Count);
        Assert.Equal(new[] { "Tye Sheridan", "Steven Spielberg" }, credits.Select(credit => credit.Name));
        Assert.Equal("Parzival", credits[0].Role);
        Assert.Equal(PersonKind.Director, credits[1].Type);
        Assert.All(credits, credit => Assert.Equal(version.Id, credit.ItemId));

        var savedCredits = Assert.Single(store.PeopleSaved);
        Assert.Equal(version.Id, savedCredits);
    }

    [Fact]
    public async Task ACopiedListIsTheVersionsOwnAndNotASecondNameForTheSources()
    {
        var store = StackedStore(out var movie, out var mvc);

        await CreateManager(store, ConfigurationWith(SideBySideFull, SideBySideHalf))
            .ReconcileLibraryAsync(CancellationToken.None);

        var version = store.FindItem(VersionIdOf(movie.StaticSources[1], SideBySideFull))!;
        var other = store.FindItem(VersionIdOf(movie.StaticSources[1], SideBySideHalf))!;

        // One list over two items is one edit away from two items disagreeing - and of a refresh of
        // the movie editing what its versions say, with nothing rewritten.
        Assert.NotSame(mvc.Genres, version.Genres);
        Assert.NotSame(mvc.Tags, version.Tags);
        Assert.NotSame(mvc.Studios, version.Studios);
        Assert.NotSame(mvc.ProviderIds, version.ProviderIds);
        Assert.NotSame(mvc.ImageInfos, version.ImageInfos);

        mvc.Genres[0] = "Changed on the source afterwards";
        mvc.ImageInfos[0].Path = "/movies/Replaced/poster.jpg";
        mvc.ProviderIds["Wikipedia"] = "added after the pass";

        Assert.Equal("Science Fiction", version.Genres[0]);
        Assert.Equal("Science Fiction", other.Genres[0]);
        Assert.EndsWith("poster-mvc.jpg", version.ImageInfos[0].Path, StringComparison.Ordinal);
        Assert.False(version.ProviderIds.ContainsKey("Wikipedia"));

        // Two versions of one file convert one cast list, and still do not hold hands over it.
        Assert.NotSame(store.GetPeople(version.Id), store.GetPeople(other.Id));

        store.GetPeople(version.Id)[0].Name = "Edited on the version";
        Assert.Equal("Tye Sheridan", store.GetPeople(other.Id)[0].Name);
    }

    [Fact]
    public async Task AVersionIsLockedSoTheServerLeavesItsMarkerAlone()
    {
        var store = StackedStore(out var movie, out _);

        await CreateManager(store).ReconcileLibraryAsync(CancellationToken.None);

        var version = store.FindItem(VersionIdOf(movie.StaticSources[1], SideBySideFull))!;

        // Its path is a marker URL, and an unlocked item with a path is an item the server scrapes,
        // probes and offers to write metadata next to. Locking it is what says there is nothing there
        // to describe.
        Assert.True(version.IsLocked);
        Assert.StartsWith(ProfileMarker.MarkerPrefix, version.Path, StringComparison.Ordinal);
    }

    // --- what a pass repairs --------------------------------------------------------

    [Fact]
    public async Task AVersionFollowsItsSourceWhenTheSourceIsScrapedAgain()
    {
        var store = StackedStore(out var movie, out var mvc);
        var manager = CreateManager(store);
        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        // The library moved: a better synopsis, a genre that was missing, a provider id that turned
        // out to be wrong, new artwork and one more credit. A version is a copy of this, so it has to
        // move too - this is the pass that makes a version item and not a photograph of one.
        mvc.Overview = "In 2045, the answer was always going to be a game.";
        mvc.Genres = new[] { "Science Fiction", "Adventure", "Action" };
        mvc.ProviderIds["Tmdb"] = "000000";
        mvc.ProviderIds.Remove("Imdb");
        mvc.ImageInfos = new[] { ProfileVersionFixtures.CreateImage("/movies/Ready Player One (2018)/poster-v2.jpg", ImageType.Primary) };
        store.AddPeople(mvc.Id, new[]
        {
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor),
            ProfileVersionFixtures.CreatePerson("Olivia Cooke", "Art3mis", PersonKind.Actor),
            ProfileVersionFixtures.CreatePerson("Steven Spielberg", null, PersonKind.Director)
        });

        var result = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Updated);

        var version = store.FindItem(versionId)!;
        Assert.Equal("In 2045, the answer was always going to be a game.", version.Overview);
        Assert.Equal(new[] { "Science Fiction", "Adventure", "Action" }, version.Genres);
        Assert.Equal("000000", version.ProviderIds["Tmdb"]);
        Assert.False(version.ProviderIds.ContainsKey("Imdb"));

        var savedImages = Assert.Single(store.ImagesSaved.Skip(1));
        Assert.Equal(new[] { "/movies/Ready Player One (2018)/poster-v2.jpg" }, savedImages.Paths);
        Assert.Equal(new[] { "/movies/Ready Player One (2018)/poster-v2.jpg" }, version.ImageInfos.Select(image => image.Path));

        Assert.Equal(
            new[] { "Tye Sheridan", "Olivia Cooke", "Steven Spielberg" },
            store.GetPeople(version.Id).Select(credit => credit.Name));

        // The primary is not touched by any of this, and neither is the version's own label.
        Assert.Equal(new ProfileCatalog().GetProfile(SideBySideFull).DisplayName, version.Name);
        Assert.Equal(new[] { "Documentary" }, movie.Genres);
    }

    [Fact]
    public async Task AVersionBornBeforeMetadataParityIsGivenItOnTheNextPass()
    {
        var store = StackedStore(out var movie, out var mvc);
        var manager = CreateManager(store);
        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var version = store.FindItem(versionId)!;

        // The state every existing version item is in the moment this plugin is upgraded: an item
        // with its id, its streams and its marker, and nothing else - the metadata, artwork, credits
        // and lock it never had. It is not re-created (that would throw away whatever is pinned on its
        // id), it is filled in.
        version.Overview = null;
        version.OriginalTitle = null;
        version.Genres = Array.Empty<string>();
        version.Tags = Array.Empty<string>();
        version.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        version.ImageInfos = Array.Empty<ItemImageInfo>();
        version.IsLocked = false;
        store.AddPeople(versionId, Array.Empty<PersonInfo>());

        var result = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Deleted);
        Assert.Single(store.Created);

        Assert.Equal(mvc.Overview, version.Overview);
        Assert.Equal("Ready Player One", version.OriginalTitle);
        Assert.Equal(mvc.Genres, version.Genres);
        Assert.Equal(mvc.ProviderIds["Imdb"], version.ProviderIds["Imdb"]);
        Assert.Equal(mvc.ImageInfos.Select(image => image.Path), version.ImageInfos.Select(image => image.Path));
        Assert.Equal(2, store.GetPeople(version.Id).Count);
        Assert.True(version.IsLocked);

        // And it stayed what it is: the same item, still a version of the same movie.
        Assert.Equal(movie.Id, ((Video)version).PrimaryVersionId);
        Assert.True(store.IsLinked(movie.Id, versionId));
    }

    [Fact]
    public async Task AVersionSomebodyRenamedKeepsTheNameTheyGaveIt()
    {
        var store = StackedStore(out var movie, out _);
        var manager = CreateManager(store);
        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var version = store.FindItem(versionId)!;
        version.Name = "The one with the good glasses";

        // Unlocked as well, so the pass has a reason to visit the item at all: what it repairs is not
        // what it leaves alone.
        version.IsLocked = false;

        var result = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal("The one with the good glasses", version.Name);
        Assert.True(version.IsLocked);

        // The name is the version picker's label, so a renamed version says nothing about its profile
        // there - but the panel still knows which movie it is, because that is OriginalTitle's job.
        Assert.Equal("Ready Player One", version.OriginalTitle);
    }

    [Theory]
    [InlineData(TwoDBase)]
    [InlineData("3D mvc / 2D Base")]
    [InlineData("")]
    public async Task ANameAnaglyfinWroteIsAnaglyfinsToCorrect(string staleName)
    {
        var store = StackedStore(out var movie, out _);
        var manager = CreateManager(store);
        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var version = store.FindItem(versionId)!;
        var profiles = new ProfileCatalog();
        version.Name = staleName == TwoDBase ? profiles.GetProfile(TwoDBase).DisplayName : staleName;

        // Text this builder writes - another profile's label, a label with a file in front of it, or
        // no name at all - is a label that went stale, not a name somebody chose.
        await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(profiles.GetProfile(SideBySideFull).DisplayName, version.Name);
    }

    [Fact]
    public async Task AVersionWhoseSourceHasMovedOnLosesTheCreditsAndImagesItWasLeftWith()
    {
        var store = StackedStore(out var movie, out var mvc);
        var manager = CreateManager(store);
        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        // The library cleaned up: the artwork was deleted off the source's list and the cast was
        // trimmed to the director. A version that kept the previous answer would be showing a cast
        // and a poster the movie itself no longer claims.
        mvc.ImageInfos = Array.Empty<ItemImageInfo>();
        store.AddPeople(mvc.Id, new[] { ProfileVersionFixtures.CreatePerson("Steven Spielberg", null, PersonKind.Director) });

        var result = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Updated);

        var version = store.FindItem(versionId)!;
        Assert.Empty(version.ImageInfos);
        Assert.Equal(new[] { "Steven Spielberg" }, store.GetPeople(version.Id).Select(credit => credit.Name));
    }

    // --- what a pass does not need --------------------------------------------------

    [Fact]
    public async Task ASecondPassWritesTheMetadataOfASettledLibraryAgainToItself()
    {
        var store = StackedStore(out var movie, out _);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull, SideBySideHalf));

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var created = store.Created.Count;
        var updated = store.Updated.Count;
        var imagesSaved = store.ImagesSaved.Count;
        var peopleSaved = store.PeopleSaved.Count;
        var streamsSaved = store.StreamsSaved.Count;
        var linked = store.Linked.Count;

        var second = await manager.ReconcileLibraryAsync(CancellationToken.None);

        // Copied metadata is metadata a pass has to re-read and re-compare every run, and the copy is
        // only worth having if the comparison says "no" once: two writes per version per start-up, on
        // a library of them, is a pass that never finishes doing nothing.
        Assert.False(second.Changed);
        Assert.Equal(0, second.Created + second.Updated + second.Deleted + second.Skipped);
        Assert.Equal(created, store.Created.Count);
        Assert.Equal(updated, store.Updated.Count);
        Assert.Equal(imagesSaved, store.ImagesSaved.Count);
        Assert.Equal(peopleSaved, store.PeopleSaved.Count);
        Assert.Equal(streamsSaved, store.StreamsSaved.Count);
        Assert.Equal(linked, store.Linked.Count);
    }

    [Fact]
    public async Task AVersionCopiesMetadataWithoutNamingAFileOfItsOwn()
    {
        var store = StackedStore(out var movie, out var mvc);

        await CreateManager(store).ReconcileLibraryAsync(CancellationToken.None);

        var version = store.FindItem(VersionIdOf(movie.StaticSources[1], SideBySideFull))!;

        // The item's own path is still the marker: a version converts a file, it is not one, and every
        // image it names is a file the source already named. This is the whole of the "no metadata
        // files" requirement as a unit test can state it - the library under test has no filesystem to
        // write to, and a pass that tried to open, save or fetch one would have nowhere to be quiet
        // about it.
        Assert.StartsWith(ProfileMarker.MarkerPrefix, version.Path, StringComparison.Ordinal);
        Assert.All(version.ImageInfos, image => Assert.Contains(image.Path, mvc.ImageInfos.Select(source => source.Path)));
        Assert.DoesNotContain(version.ImageInfos, image => image.Path.StartsWith(movie.Path, StringComparison.Ordinal));
    }

    // --- what happens when the source cannot be found -------------------------------

    [Fact]
    public async Task AVersionWhoseOwnerIsGoneStillGetsMadeAndWearsItsPrimariesMetadata()
    {
        // The stacked case with the hidden MVC item missing from the library: the file still arrives
        // as a source the item reports, but the item that owns it - and holds its metadata - is gone.
        var store = new FakeProfileVersionItemStore();
        var movie = store.AddVersionRoot(ProfileVersionFixtures.CreateStackedMvcMovie());
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        StampRootMetadata(movie);

        var result = await CreateManager(store).ReconcileLibraryAsync(CancellationToken.None);

        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);

        // Nothing here fails, and nothing is withheld: an empty panel beats no version, and the
        // primary's own metadata is the best answer left.
        Assert.Equal(1, result.Created);
        Assert.Empty(store.Deleted);

        var version = store.FindItem(versionId)!;
        Assert.Equal(movie.Overview, version.Overview);
        Assert.Equal(movie.OriginalTitle, version.OriginalTitle);
        Assert.Equal(movie.Genres, version.Genres);
        Assert.Equal(movie.ProviderIds["Imdb"], version.ProviderIds["Imdb"]);
        Assert.Equal(movie.ImageInfos.Select(image => image.Path), version.ImageInfos.Select(image => image.Path));
        Assert.Equal(new ProfileCatalog().GetProfile(SideBySideFull).DisplayName, version.Name);
        Assert.True(version.IsLocked);
        Assert.True(store.IsLinked(movie.Id, versionId));
        Assert.NotEmpty(store.GetMediaStreams(versionId));
    }

    [Fact]
    public async Task AMarkerItemIsStillNoOnesSourceForVersionsOfAnything()
    {
        var store = StackedStore(out var movie, out _);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull));
        var versionId = VersionIdOf(movie.StaticSources[1], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var version = store.FindItem(versionId)!;
        var created = store.Created.Count;

        // A version of a version needs a second eligible file, and there is only one: the materialised
        // versions of a movie are sources the primary now reports, so a scan that could not tell a
        // marker from a media file would find work forever. Their metadata does not qualify them.
        var second = await manager.ReconcileItemAsync(versionId, CancellationToken.None);

        Assert.False(second.Changed);
        Assert.Equal(created, store.Created.Count);
        Assert.DoesNotContain(store.Created, entry => ((Video)entry.Item).PrimaryVersionId == versionId);
    }

    // --- fixtures ------------------------------------------------------------------

    /// <summary>
    /// A stacked movie: the 1080p item the user reaches, the hidden MVC item its versions are built
    /// from, and deliberately different metadata on each of the two - so that every assertion about
    /// "whose metadata" has an answer that could only have come from one of them.
    /// </summary>
    private static FakeProfileVersionItemStore StackedStore(out ScriptedVideo movie, out Video mvc)
    {
        var store = new FakeProfileVersionItemStore();

        movie = store.AddVersionRoot(ProfileVersionFixtures.CreateStackedMvcMovie());
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        mvc = ProfileVersionFixtures.CreateMvcAlternateItem();
        store.AddItem(mvc);

        StampRootMetadata(movie);

        store.AddPeople(mvc.Id, new[]
        {
            ProfileVersionFixtures.CreatePerson("Tye Sheridan", "Parzival", PersonKind.Actor, 0),
            ProfileVersionFixtures.CreatePerson("Steven Spielberg", null, PersonKind.Director, 1)
        });

        return store;
    }

    /// <summary>
    /// Puts the 1080p root's own - and different - metadata on an item: the values a version must not
    /// end up wearing when it converts a file belonging to somebody else.
    /// </summary>
    private static void StampRootMetadata(ScriptedVideo movie)
    {
        movie.OriginalTitle = "Ready Player One (2018) [1080p]";
        movie.Overview = "The plain high definition cut.";
        movie.Tagline = "Different tagline.";
        movie.SortName = "ready player one (2018) [1080p]";
        movie.Genres = new[] { "Documentary" };
        movie.Tags = new[] { "Flat" };
        movie.Studios = new[] { "Different Studio" };
        movie.ProductionLocations = Array.Empty<string>();
        movie.OfficialRating = "PG";
        movie.CustomRating = string.Empty;
        movie.CommunityRating = 1.1f;
        movie.CriticRating = 10f;
        movie.ProductionYear = 2001;
        movie.PremiereDate = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        movie.EndDate = null;
        movie.HomePageUrl = "https://example.invalid/different";
        movie.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt0000000" };
        movie.ImageInfos = new[]
        {
            ProfileVersionFixtures.CreateImage(ProfileVersionFixtures.FolderPath + "/poster-1080p.jpg", ImageType.Primary)
        };
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

    private static ProfileVersionItemManager CreateManager(
        FakeProfileVersionItemStore store,
        StubConfigurationSource? configuration = null)
        => new(
            store,
            new ScriptedMvcDetector(ProfileVersionFixtures.MvcPath),
            new ProfileCatalog(),
            configuration ?? ConfigurationWith(SideBySideFull),
            NullLogger<ProfileVersionItemManager>.Instance);

    private static Guid VersionIdOf(MediaSourceInfo source, string profileId)
        => AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(source.Id, source.Path, profileId);
}
