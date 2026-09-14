using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.VersionItems;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// Covers what a profile version-item pass does to the library, and what it refuses to do to it.
/// </summary>
/// <remarks>
/// <para>
/// Every test here is a statement about a difference: what a pass creates when a version is missing,
/// what it leaves alone when one is already right, what it removes when one is no longer wanted, and
/// what it will not touch because it is not Anaglyfin's. The library is the in-memory fake, so the
/// whole surface a reconcile can reach is visible - items, links and persisted streams - and the
/// recorded writes distinguish a pass that skipped an item from one that rewrote it to be what it
/// already was. That distinction is the property the feature lives on: the worker runs this over the
/// whole library on every start-up and every settings save.
/// </para>
/// <para>
/// The playback consequences of a version (its codec, its frame, its marker) are the provider's
/// tests; what is checked here is that the item carries them into the library, and that the item that
/// carries them is the one the user's choice names.
/// </para>
/// </remarks>
public class ProfileVersionItemManagerTests
{
    private const string SideBySideFull = ProfileIds.SideBySideFull;

    private const string SideBySideHalf = ProfileIds.SideBySideHalf;

    private const string TwoDBase = ProfileIds.TwoDBase;

    private static readonly Guid OtherMovieId = Guid.Parse("9d8c7b6a-5e4f-4d3c-b2a1-0f1e2d3c4b5a");

    private const string OtherMvcPath = "/movies/Other (2010)/Other (2010) - 3D mvc.mkv";

    // --- what a pass creates ----------------------------------------------------

    [Fact]
    public async Task APassGivesEveryEnabledProfileItsOwnItemOfEveryEligibleFile()
    {
        var store = NewStore(out var movie);
        var profiles = new ProfileCatalog();

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull, SideBySideHalf))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(2, result.Created);
        Assert.Equal(2, store.Created.Count);
        Assert.Empty(store.Updated);
        Assert.Empty(store.Deleted);

        var source = movie.StaticSources[0];
        var wanted = new Dictionary<Guid, StereoProfile>
        {
            [VersionIdOf(source, SideBySideFull)] = profiles.GetProfile(SideBySideFull),
            [VersionIdOf(source, SideBySideHalf)] = profiles.GetProfile(SideBySideHalf)
        };

        Assert.Equal(wanted.Keys.OrderBy(id => id), store.Created.Select(created => created.Item.Id).OrderBy(id => id));

        foreach (var (item, parent) in store.Created)
        {
            var profile = wanted[item.Id];

            // A version is a picture with a profile applied to it: a plain video, filed where the
            // movie it converts is filed so it is visible to exactly the same users, and claimed by
            // that movie - which is also what keeps it out of browse and search.
            var version = Assert.IsType<Video>(item);
            Assert.Null(version.Video3DFormat);
            Assert.Equal(movie.Id, version.PrimaryVersionId);
            Assert.Equal(ProfileVersionFixtures.FolderId, version.ParentId);
            Assert.Equal(ProfileVersionFixtures.FolderId, parent?.Id);
            Assert.Equal(profile.DisplayName, item.Name);
            Assert.Equal(movie.Container, item.Container);
            Assert.Equal(movie.RunTimeTicks, item.RunTimeTicks);
            Assert.Equal(movie.Size, item.Size);
            Assert.Equal(movie.TotalBitrate, item.TotalBitrate);

            // The marker, so the wrapper converts instead of copying.
            Assert.StartsWith(ProfileMarker.MarkerPrefix, item.Path, StringComparison.Ordinal);

            // The streams are what a static source reports, so a version without them would be a
            // picture with no tracks in it.
            var streams = store.GetMediaStreams(item.Id);
            Assert.Equal(3, streams.Count);

            var video = Assert.Single(streams, stream => stream.Type == MediaStreamType.Video);
            Assert.Equal(ForceTranscodeVideoStreams.VideoCodec, video.Codec);
            Assert.NotNull(video.Width);
            Assert.Equal(video.Width, item.Width);
            Assert.Equal(video.Height, item.Height);

            Assert.True(store.IsLinked(movie.Id, item.Id));
        }

        Assert.Equal(2, store.StreamsSaved.Count);
        Assert.Equal(2, store.Linked.Count);
    }

    [Fact]
    public async Task TheHiddenFileOfAStackGetsItsVersionsUnderTheItemTheUserReaches()
    {
        var store = new FakeProfileVersionItemStore();
        var movie = store.AddVersionRoot(ProfileVersionFixtures.CreateStackedMvcMovie());
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var detector = new ScriptedMvcDetector(ProfileVersionFixtures.MvcPath);

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull), detector)
            .ReconcileLibraryAsync(CancellationToken.None);

        var (item, parent) = Assert.Single(store.Created);

        Assert.Equal(1, result.Created);

        // Both files were asked, and the versions came from the one that is 3D - filed under the item
        // a client can open, because the MVC file behind them is not listable.
        Assert.Equal(2, detector.Candidates.Count);
        Assert.Equal(VersionIdOf(movie.StaticSources[1], SideBySideFull), item.Id);
        Assert.Equal(movie.Id, ((Video)item).PrimaryVersionId);
        Assert.Equal(ProfileVersionFixtures.FolderId, item.ParentId);
        Assert.Equal(ProfileVersionFixtures.FolderId, parent?.Id);

        // Only one of the two files converts, so the label is the profile's own - the same label the
        // provider gives for the same version, which is the point of building both from one source.
        Assert.Equal(new ProfileCatalog().GetProfile(SideBySideFull).DisplayName, item.Name);
    }

    [Fact]
    public async Task AFileThatIsNotMVCGetsWithNothing()
    {
        var store = NewStore(out _);

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull), new ScriptedMvcDetector())
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Empty(store.Created);
        Assert.Empty(store.Linked);
        Assert.Empty(store.StreamsSaved);
    }

    // --- what a second pass does not do -----------------------------------------

    [Fact]
    public async Task ASecondPassOverASettledLibraryChangesNothing()
    {
        var store = NewStore(out _);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull, SideBySideHalf));

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var created = store.Created.Count;
        var linked = store.Linked.Count;
        var savedStreams = store.StreamsSaved.Count;

        var second = await manager.ReconcileLibraryAsync(CancellationToken.None);

        // This is the pass that runs on every start-up and every settings save: on a library that
        // already agrees with the settings it must read, and stop.
        Assert.False(second.Changed);
        Assert.Equal(0, second.Created + second.Updated + second.Deleted + second.Skipped);
        Assert.Equal(created, store.Created.Count);
        Assert.Equal(linked, store.Linked.Count);
        Assert.Equal(savedStreams, store.StreamsSaved.Count);
        Assert.Empty(store.Updated);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task AVersionOfAVersionIsNeverPlanned()
    {
        var store = NewStore(out var movie);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull));
        var versionId = VersionIdOf(movie.StaticSources[0], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        // The item the worker is asked about is one of Anaglyfin's own: the answer is the primary's
        // reconciliation, never a fresh set of versions hanging off a marker.
        var result = await manager.ReconcileItemAsync(versionId, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Single(store.Created);
        Assert.All(store.Created, created => Assert.NotEqual(versionId, ((Video)created.Item).PrimaryVersionId));
    }

    // --- what a pass removes, and what it must not ------------------------------

    [Fact]
    public async Task AProfileSwitchedOffLosesItsItem()
    {
        var store = NewStore(out var movie);
        var configuration = ConfigurationWith(SideBySideFull, SideBySideHalf);
        var manager = CreateManager(store, configuration);
        var halfId = VersionIdOf(movie.StaticSources[0], SideBySideHalf);
        var fullId = VersionIdOf(movie.StaticSources[0], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        configuration.Configuration.EnabledProfileIds = new List<string> { SideBySideFull };

        var result = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Null(store.FindItem(halfId));
        Assert.False(store.IsLinked(movie.Id, halfId));
        Assert.NotNull(store.FindItem(fullId));
    }

    [Fact]
    public async Task AVersionOfEveryEnabledProfileGoesWhenTheCatalogEnablesNothing()
    {
        var store = NewStore(out var movie);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull, SideBySideHalf));

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var createdBefore = store.Created.Count;

        var result = await CreateManager(store, ConfigurationWith(), new NoEnabledProfilesCatalog())
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(createdBefore, store.Created.Count);
        Assert.Empty(store.LinksOf(movie.Id));
        Assert.Null(store.FindItem(VersionIdOf(movie.StaticSources[0], SideBySideFull)));
        Assert.Null(store.FindItem(VersionIdOf(movie.StaticSources[0], SideBySideHalf)));
    }

    [Fact]
    public async Task AVersionSomebodyMergedByHandStaysWhereTheyPutIt()
    {
        var store = NewStore(out var movie);
        var handMerged = new Video
        {
            Id = Guid.NewGuid(),
            Name = "Director's cut disc 2",
            Path = ProfileVersionFixtures.PlainPath,
            ParentId = ProfileVersionFixtures.FolderId
        };

        store.AddItem(handMerged);
        store.LinkAlternateVersion(movie.Id, handMerged.Id);

        var result = await CreateManager(store, ConfigurationWith(), new NoEnabledProfilesCatalog())
            .ReconcileLibraryAsync(CancellationToken.None);

        // Not one byte of it is Anaglyfin's: no marker path, no derived id, no stream report this
        // manager wrote. A reconcile that cleaned it up would be an unasked-for edit of a library.
        Assert.Equal(0, result.Deleted);
        Assert.NotNull(store.FindItem(handMerged.Id));
        Assert.True(store.IsLinked(movie.Id, handMerged.Id));
    }

    [Fact]
    public async Task AnIdWornBySomethingElseIsLeftOnIt()
    {
        var store = NewStore(out var movie);
        var takenId = VersionIdOf(movie.StaticSources[0], SideBySideFull);
        var stranger = new Video
        {
            Id = takenId,
            Name = "Somebody else's movie",
            Path = "/movies/Elsewhere/Elsewhere.mkv",
            ParentId = ProfileVersionFixtures.FolderId
        };

        store.AddItem(stranger);

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull, SideBySideHalf))
            .ReconcileLibraryAsync(CancellationToken.None);

        // Rewriting it would rename somebody's library item to a version label and repoint its file
        // at a marker. The dynamic provider still offers that profile, so nothing is lost.
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.Created);
        Assert.Empty(store.Updated);
        Assert.Empty(store.Deleted);
        Assert.Equal("Somebody else's movie", stranger.Name);
        Assert.Equal("/movies/Elsewhere/Elsewhere.mkv", stranger.Path);
        Assert.Null(stranger.PrimaryVersionId);
        Assert.False(store.IsLinked(movie.Id, takenId));
        Assert.NotNull(store.FindItem(VersionIdOf(movie.StaticSources[0], SideBySideHalf)));
    }

    [Fact]
    public async Task AVersionLeftWithoutAPrimaryIsRemoved()
    {
        var store = NewStore(out var movie);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull));
        var versionId = VersionIdOf(movie.StaticSources[0], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        store.DeleteItem(movie);

        var result = await manager.ReconcileItemAsync(versionId, CancellationToken.None);

        // A version of nothing would list as a movie whose file is a marker URL, and no pass over the
        // library would ever look at it again: the server's general queries skip items naming a
        // primary, and this one names one that is gone.
        Assert.Equal(1, result.Deleted);
        Assert.Null(store.FindItem(versionId));
    }

    [Fact]
    public async Task AMarkerItemFoundInTheLibraryIsRemovedByAPass()
    {
        var store = NewStore(out _);
        var orphan = new Video
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            Path = ProfileMarker.MarkerPrefix + "sbs_full?source=%2Fmovies%2Fx.mkv",
            ParentId = ProfileVersionFixtures.FolderId
        };

        store.AddItem(orphan);
        store.VersionRoots.Add(orphan);

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Null(store.FindItem(orphan.Id));
    }

    // --- what a pass repairs ----------------------------------------------------

    [Fact]
    public async Task AVersionThatDriftedIsWrittenBackToItsProfile()
    {
        var store = NewStore(out var movie);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull));
        var versionId = VersionIdOf(movie.StaticSources[0], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var version = (Video)store.FindItem(versionId)!;

        // A stale Anaglyfin label, not a user's rename: the manager keeps its own labels current, but
        // leaves a name somebody chose in the dashboard alone.
        version.Name = new ProfileCatalog().GetProfile(TwoDBase).DisplayName;
        store.AddStreams(versionId, new[]
        {
            new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = 1920, Height = 1080 }
        });

        var result = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal(new ProfileCatalog().GetProfile(SideBySideFull).DisplayName, version.Name);

        var video = Assert.Single(store.GetMediaStreams(versionId), stream => stream.Type == MediaStreamType.Video);
        Assert.Equal(ForceTranscodeVideoStreams.VideoCodec, video.Codec);

        // The link is not rewritten when it is already there: only what was wrong is repaired.
        Assert.Single(store.Linked);
        Assert.True(store.IsLinked(movie.Id, versionId));
    }

    [Fact]
    public async Task AVersionThatLostItsLinkIsLinkedAgain()
    {
        var store = NewStore(out var movie);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull));
        var versionId = VersionIdOf(movie.StaticSources[0], SideBySideFull);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        store.Unlink(movie.Id, versionId);

        var result = await manager.ReconcileLibraryAsync(CancellationToken.None);

        // The item is right and the streams are right; only the link the version picker reads is
        // missing. Recreating the item would throw away the resume position pinned on its id.
        Assert.Equal(0, result.Created);
        Assert.Equal(2, store.Linked.Count);
        Assert.True(store.IsLinked(movie.Id, versionId));
        Assert.Single(store.Created);
    }

    [Fact]
    public async Task AVersionBuiltOnlyFromTheItemIsFoundByItsDerivedId()
    {
        var store = new FakeProfileVersionItemStore();
        var movie = ProfileVersionFixtures.CreateSingleFileMvcMovie();

        // An item with no media sources at all: the scanner falls back to asking the item, so the
        // versions are derived from the item's own id and path, and the drift this test sets up is
        // one nothing links to.
        movie.StaticSources = Array.Empty<MediaSourceInfo>();
        store.AddVersionRoot(movie);
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var versionId = AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(
            movie.Id.ToString("N"),
            movie.Path,
            SideBySideFull);

        var manager = CreateManager(store, ConfigurationWith(SideBySideFull));

        var first = await manager.ReconcileLibraryAsync(CancellationToken.None);
        Assert.Equal(1, first.Created);

        store.Unlink(movie.Id, versionId);

        var second = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(0, second.Created);
        Assert.True(store.IsLinked(movie.Id, versionId));
    }

    // --- failure paths ----------------------------------------------------------

    [Fact]
    public async Task ALibraryThatCannotBeListedChangesNothing()
    {
        var store = NewStore(out _);
        store.ThrowOnListRoots = true;

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(store.Created);
    }

    [Fact]
    public async Task OneItemThatCannotBeReadDoesNotStopThePass()
    {
        var store = new FakeProfileVersionItemStore();
        var good = store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());
        var bad = store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie(OtherMovieId, OtherMvcPath));
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        store.ThrowOnLinkedVersions = primary => primary.Id == bad.Id;

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull), new ScriptedMvcDetector(ProfileVersionFixtures.MvcPath, OtherMvcPath))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(VersionIdOf(good.StaticSources[0], SideBySideFull), Assert.Single(store.Created).Item.Id);
    }

    [Fact]
    public async Task AVersionThatCannotBeCreatedIsCountedAndTheRestAreMade()
    {
        var store = NewStore(out var movie);
        var halfId = VersionIdOf(movie.StaticSources[0], SideBySideHalf);
        store.ThrowOnCreate = item => item.Id == halfId;

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull, SideBySideHalf))
            .ReconcileLibraryAsync(CancellationToken.None);

        // The dynamic provider is the reason a failed write is a shrug and not a rollback: the
        // profile is still offered, it simply has no item of its own yet.
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.Null(store.FindItem(halfId));
        Assert.NotNull(store.FindItem(VersionIdOf(movie.StaticSources[0], SideBySideFull)));
    }

    // --- what the manager asks about --------------------------------------------

    [Fact]
    public async Task AnItemThatIsGoneOrNotAVideoAsksForNothing()
    {
        var store = NewStore(out _);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull));

        // Removed under it: its versions went with it, and asking again is a no-op.
        Assert.False((await manager.ReconcileItemAsync(Guid.NewGuid(), CancellationToken.None)).Changed);

        // A track: nothing here concerns the audio library.
        store.AddItem(new Audio { Id = OtherMovieId, Name = "A track", Path = "/music/Audience/Track.mp3" });
        Assert.False((await manager.ReconcileItemAsync(OtherMovieId, CancellationToken.None)).Changed);

        Assert.Empty(store.Created);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task TheVersionsBuiltAreTheEnabledOnesAndNotTheOnesOneDevicePrefers()
    {
        var store = NewStore(out var movie);

        // An administrator who pins one device's default to 2D has said what that device should start
        // on, not that every client should be handed a 2D item in its library: which profile a client
        // begins with is an answer a request gives, not a fact about the file.
        var configuration = ConfigurationWith(SideBySideFull);
        configuration.Configuration.DeviceDefaultProfiles.Add(new DeviceProfileDefault
        {
            ClientName = "AndroidTV",
            ProfileId = TwoDBase
        });

        await CreateManager(store, configuration).ReconcileLibraryAsync(CancellationToken.None);

        var created = Assert.Single(store.Created);
        Assert.Equal(VersionIdOf(movie.StaticSources[0], SideBySideFull), created.Item.Id);
        Assert.Null(store.FindItem(VersionIdOf(movie.StaticSources[0], TwoDBase)));
    }

    [Fact]
    public async Task TwoPassesAreNeverInTheLibraryTogether()
    {
        var store = NewStore(out _);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull));

        var attempted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.HoldCreations();
        store.OnCreateAttempt = _ => attempted.TrySetResult(true);

        // Two passes over the same library: the one outcome that cannot be repaired afterwards is
        // both of them seeing the same version as missing and creating it twice, because from then on
        // two items answer to one id.
        var firstPass = manager.ReconcileLibraryAsync(CancellationToken.None);
        var secondPass = manager.ReconcileLibraryAsync(CancellationToken.None);

        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(100);

        Assert.Equal(1, store.CreateAttempts);

        store.ReleaseCreations();
        await Task.WhenAll(firstPass, secondPass);

        // The second pass went in after the first had written, found the item it was going to make,
        // and left it alone.
        Assert.Single(store.Created);
    }

    // --- fixtures ---------------------------------------------------------------

    private static FakeProfileVersionItemStore NewStore(out ScriptedVideo movie)
    {
        var store = new FakeProfileVersionItemStore();
        movie = store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());
        store.AddItem(ProfileVersionFixtures.CreateFolder());

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

    private static ProfileVersionItemManager CreateManager(
        FakeProfileVersionItemStore store,
        StubConfigurationSource configuration,
        IProfileCatalog? catalog = null)
        => new(
            store,
            new ScriptedMvcDetector(ProfileVersionFixtures.MvcPath, OtherMvcPath),
            catalog ?? new ProfileCatalog(),
            configuration,
            NullLogger<ProfileVersionItemManager>.Instance);

    private static ProfileVersionItemManager CreateManager(
        FakeProfileVersionItemStore store,
        StubConfigurationSource configuration,
        IMvcSourceDetector detector)
        => new(
            store,
            detector,
            new ProfileCatalog(),
            configuration,
            NullLogger<ProfileVersionItemManager>.Instance);

    private static Guid VersionIdOf(MediaBrowser.Model.Dto.MediaSourceInfo source, string profileId)
        => AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(source.Id, source.Path, profileId);
}
