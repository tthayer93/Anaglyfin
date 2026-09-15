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
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// Covers what a full reconciliation pass considers, and what it refuses to read expensively.
/// </summary>
/// <remarks>
/// <para>
/// The other version-item tests ask what a pass changes. These ask what it costs, because that is the
/// property a library of thousands experiences: a pass runs on every start-up and every settings save.
/// The population it walks is deliberately the whole library - the detector's vocabulary is wider than
/// a 3D or tag query - but the expensive thing a pass can do is read the media sources of an item. The
/// answers below are therefore about reads: which items the fake full walk returns naturally, and which
/// of those items ever had their sources looked at. The fake counts reads for exactly this question,
/// and an item whose sources were never read is the outcome several of these tests are about.
/// </para>
/// <para>
/// The fake full walk is not a list of query results. A video in the fake library is seen by the pass
/// because it is in the library, which is what lets tests prove the detector's filename vocabulary
/// works without naming it as a query answer first.
/// </para>
/// </remarks>
public class ProfileVersionReconcileCheapPassTests
{
    private const string SideBySideFull = ProfileIds.SideBySideFull;

    private const string SideBySideHalf = ProfileIds.SideBySideHalf;

    private static readonly Guid OtherMovieId = Guid.Parse("2b1f0e9d-7c6b-4a59-8e3d-4c5b6a7988f7");

    // --- what a full walk reaches -------------------------------------------------

    [Fact]
    public async Task AFileNamedForMVCReachesTheDetectorFromTheFullWalk()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        store.AddOrdinaryLibraryVideo();

        // Nothing here says this item was returned by a narrow query. It is only a video in the fake
        // library whose file name carries the detector's vocabulary, and the full pass has to ask the
        // detector about it anyway.
        var mvc = AddNamedVideo(
            store,
            "/movies/Ready Player One (2018)/Ready Player One (2018) 3DMVC.mkv",
            Video3DFormat.MVC);

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, mvc.MediaSourceReads);

        var created = Assert.Single(store.Created);
        Assert.Equal(mvc.Id, Assert.IsType<Video>(created.Item).PrimaryVersionId);
    }

    [Fact]
    public async Task APassNeverReadsTheSourcesOfOrdinaryMoviesInTheWalk()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var ordinary = store.AddOrdinaryLibraryVideo();
        var mvc = store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        // The MVC movie was read as media, once, and the ordinary one not at all. The ordinary movie is
        // still in the walk: the cheap answer is what keeps the library's size off the media-source
        // API, not a query that pretends ordinary items do not exist.
        Assert.Equal(1, mvc.MediaSourceReads);
        Assert.Equal(0, ordinary.MediaSourceReads);
        Assert.Equal(1, result.Created);
    }

    [Fact]
    public async Task AWholeShelfOfKnownFormatsTheScannerRefusesCostsNoSourceRead()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        // The case a 3D query would have named and this half can refuse before asking the scanner:
        // a library of half-SBS rips. The full walk still returns them, but reading their streams to
        // find that out is the cost the cheap question takes away.
        var sideBySide = store.AddVersionRoot(WithFormat(ProfileVersionFixtures.CreateOrdinaryMovie(OtherMovieId), Video3DFormat.HalfSideBySide));

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Empty(store.Created);
        Assert.Equal(0, sideBySide.MediaSourceReads);
    }

    [Fact]
    public async Task APassFindsTheMovieOnlyItsTagMentions()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        // An MVC movie with no stereo format on it and no MVC in a name - what a tagged NFO leaves
        // behind. It is in the full walk because it is a library video, and only the detector can tell
        // its tag from the plain "3D" a scraper hangs on an ordinary movie.
        var tagged = store.AddVersionRoot(ProfileVersionFixtures.CreateTaggedMvcMovie(OtherMovieId));

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, tagged.MediaSourceReads);

        var created = Assert.Single(store.Created);
        Assert.Equal(tagged.Id, Assert.IsType<Video>(created.Item).PrimaryVersionId);
    }

    [Fact]
    public async Task ATagThatMeansNothingToTheScannerIsAskedAndLeftAlone()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        // The full walk does not know what a tag means, so a movie wearing a 3D word is walked and
        // then judged by the same rules everything else is. A plain "3D" is asked about and refused.
        var tagged = store.AddVersionRoot(ProfileVersionFixtures.CreateOrdinaryMovie(OtherMovieId));
        tagged.Tags = new[] { "3D" };

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(0, tagged.MediaSourceReads);
    }

    // --- what still has to reach the scan even with no MVC signal -----------------

    [Fact]
    public async Task TheStackRootStillGetsItsVersionsFromTheFileThatIsThreeD()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var root = store.AddStackedRootToFullWalk();

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        // The root says nothing about 3D in its own name, path or tags, and it is still reconciled -
        // because it names a version its own fields do not speak for, which is the one thing the cheap
        // question is not allowed to guess about.
        Assert.Equal(1, result.Created);
        Assert.Equal(1, root.MediaSourceReads);
        Assert.True(store.IsLinked(root.Id, Assert.Single(store.Created).Item.Id));
    }

    [Fact]
    public async Task AHiddenMvcChildIsReconciledUnderTheRootThatTheWalkReaches()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var root = store.AddStackedRootToFullWalk();
        var hidden = store.AddItemAndReturn(ProfileVersionFixtures.CreateMvcAlternateItem(video3DFormat: Video3DFormat.MVC));

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        // The hidden alternate names a primary, so the general walk leaves it where a client cannot
        // list it. The versions have to land on the root, and the root's source list - not the hidden
        // item's own media-source API - is what the scan asks.
        Assert.Equal(1, result.Created);

        var created = Assert.IsType<Video>(Assert.Single(store.Created).Item);
        Assert.Equal(root.Id, created.PrimaryVersionId);
        Assert.Equal(root.ParentId, created.ParentId);
        Assert.True(store.IsLinked(root.Id, created.Id));
        Assert.Empty(store.LinksOf(hidden.Id));
        Assert.Equal(1, root.MediaSourceReads);
        Assert.Equal(0, hidden.MediaSourceReads);
    }

    [Fact]
    public async Task AVersionOfAnaglyfinsOwnFoundByTheWalkIsLeftToTheJanitor()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var orphan = store.AddItemAndReturn(new ScriptedVideo
        {
            Id = Guid.NewGuid(),
            Name = "3D Full Side-by-Side",
            Path = ProfileMarker.MarkerPrefix + "sbs_full?source=%2Fmovies%2FMissing%203DMVC.mkv",
            ParentId = ProfileVersionFixtures.FolderId,
            VideoType = VideoType.VideoFile
        });

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, orphan.MediaSourceReads);
        Assert.Empty(store.Created);
        Assert.Null(store.FindItem(orphan.Id));
    }

    [Fact]
    public async Task ANamedAlternateWhosePrimaryIsGoneIsLeftToThePassThatRemovesIt()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        // The MVC item of a stack whose primary has been deleted under it: still in the database, still
        // names something, and reachable by no client. Versions hung off it would be versions of nothing,
        // and the removal of the file already owns the job of removing what was filed under it.
        var orphan = store.AddVersionRoot(ProfileVersionFixtures.CreateMvcAlternateItem(video3DFormat: Video3DFormat.MVC));
        store.RemoveItem(ProfileVersionFixtures.MovieId);

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Empty(store.Created);
        Assert.Equal(0, orphan.MediaSourceReads);
    }

    // --- what a pass still gets right -------------------------------------------

    [Fact]
    public async Task ThePassASettingsSaveAsksForStillPutsEveryEnabledProfileOnTheMovie()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        store.AddOrdinaryLibraryVideo();
        store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());

        var configuration = ConfigurationWith(SideBySideFull, SideBySideHalf);
        var manager = CreateManager(store, configuration);

        var first = await manager.ReconcileLibraryAsync(CancellationToken.None);
        var second = await manager.ReconcileLibraryAsync(CancellationToken.None);

        // Narrowing what a pass reads may not narrow what it does: the administrator who enabled two
        // profiles expects both in the picker, on the movie they named, and a pass over a library that
        // agrees with the settings still writes nothing the second time.
        Assert.Equal(2, first.Created);
        Assert.Equal(2, store.Created.Count);
        Assert.False(second.Changed);
        Assert.Empty(store.Updated);
        Assert.Empty(store.Deleted);
        Assert.Equal(2, store.LinksOf(ProfileVersionFixtures.MovieId).Count);
    }

    // --- what a pass says about itself ------------------------------------------

    [Fact]
    public async Task APassSaysWhatItConsideredWhatItRefusedAndWhatItScanned()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        store.AddOrdinaryLibraryVideo();
        store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());
        store.AddVersionRoot(WithFormat(ProfileVersionFixtures.CreateOrdinaryMovie(Guid.NewGuid()), Video3DFormat.HalfSideBySide));

        var logger = new ListLogger<ProfileVersionItemManager>();

        await CreateManager(store, ConfigurationWith(SideBySideFull), logger)
            .ReconcileLibraryAsync(CancellationToken.None);

        // Three ordinary library videos are considered, two of them by the cheap question alone, and
        // only one reaches the scanner. The counts are the whole diagnostic - they are what a reader
        // looks for when a library that should be cheap is not - and the titles are not, because a pass
        // over a library has no reason to write the names of other people's movies into this line.
        var considered = logger.Message(LogLevel.Debug, "reconciling profile version items");
        Assert.Contains("3 library videos", considered, StringComparison.Ordinal);

        var summary = logger.Message(LogLevel.Debug, "without reading a media source");
        Assert.Contains("refused 2", summary, StringComparison.Ordinal);
        Assert.Contains("scanned 1", summary, StringComparison.Ordinal);

        Assert.DoesNotContain(considered, "Ready Player One", StringComparison.Ordinal);
        Assert.DoesNotContain(summary, "Ready Player One", StringComparison.Ordinal);
    }

    // --- fixtures ---------------------------------------------------------------

    private static ScriptedVideo AddNamedVideo(
        FakeProfileVersionItemStore store,
        string path,
        Video3DFormat? video3DFormat = null)
    {
        var id = Guid.NewGuid();
        var video = new ScriptedVideo
        {
            Id = id,
            Name = "Movie",
            Path = path,
            ParentId = ProfileVersionFixtures.FolderId,
            RunTimeTicks = ProfileVersionFixtures.RunTimeTicks,
            Container = "mkv",
            Size = ProfileVersionFixtures.FileSize,
            TotalBitrate = ProfileVersionFixtures.Bitrate,
            VideoType = VideoType.VideoFile,
            Video3DFormat = video3DFormat,
            StaticSources = new[] { ProfileVersionFixtures.CreateSource(id, "3D mvc", path, video3DFormat) }
        };

        store.AddItem(video);

        return video;
    }

    private static ScriptedVideo WithFormat(ScriptedVideo video, Video3DFormat format)
    {
        video.Video3DFormat = format;

        return video;
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
        ILogger<ProfileVersionItemManager>? logger = null)
        => new(
            store,
            new MvcSourceDetector(),
            new ProfileCatalog(),
            configuration,
            logger ?? new ListLogger<ProfileVersionItemManager>());

    /// <summary>
    /// The logger a test can read: every message it was handed, with the level it was handed at.
    /// </summary>
    /// <remarks>
    /// Formats through the same formatter the server would, because the assertion is about the line a
    /// reader sees - the numbers in it and the names that are not.
    /// </remarks>
    private sealed class ListLogger<T> : ILogger<T>
    {
        /// <summary>Gets every message written, in call order.</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => new NullScope();

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        /// <summary>Gets the single message at a level that mentions <paramref name="needle"/>.</summary>
        /// <param name="level">The level the line was written at.</param>
        /// <param name="needle">Text the line carries.</param>
        /// <returns>The formatted message.</returns>
        /// <exception cref="InvalidOperationException">No single message matches.</exception>
        public string Message(LogLevel level, string needle)
            => Entries.Single(entry => entry.Level == level && entry.Message.Contains(needle, StringComparison.Ordinal)).Message;

        private sealed class NullScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
