using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.Detection;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.VersionItems;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// Covers what a full reconciliation pass asks the library, and what it refuses to ask.
/// </summary>
/// <remarks>
/// <para>
/// The other version-item tests ask what a pass changes. These ask what it costs, because that is the
/// property a library of thousands experiences: a pass runs on every start-up and every settings save,
/// and the expensive thing a pass can do is read the media sources of an item - the persisted streams,
/// attachments and segment state of every file grouped with it. The answers below are therefore about
/// reads: which queries ran, which items they named, and which of those items ever had their sources
/// looked at. The fake counts reads for exactly this question, and an item whose sources were never
/// read is the outcome several of these tests are about.
/// </para>
/// <para>
/// The narrow queries are asserted as answers rather than as query objects: what a pass may ask the
/// server is bounded by what this seam exposes, and a pass that went hunting for every video in the
/// library would have to ask for something the fake has no answer for.
/// </para>
/// </remarks>
public class ProfileVersionReconcileCheapPassTests
{
    private const string SideBySideFull = ProfileIds.SideBySideFull;

    private const string SideBySideHalf = ProfileIds.SideBySideHalf;

    private static readonly Guid OtherMovieId = Guid.Parse("2b1f0e9d-7c6b-4a59-8e3d-4c5b6a7988f7");

    // --- what a pass asks -------------------------------------------------------

    [Fact]
    public async Task APassAsksTheNarrowQueriesAndNeverTheWholeLibrary()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        // Two movies with no 3D anywhere on them and nothing filed beside them, and the one item a
        // query could name: a pass over a library like this one is the common case on a real server,
        // and the only things it is allowed to learn are the two answers below.
        store.AddItem(OtherMovie(OtherMovieId, "/movies/Second (2015)/Second (2015).mkv"));
        store.AddItem(OtherMovie(Guid.NewGuid(), "/movies/Third (2016)/Third (2016).mkv"));
        store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());

        await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        // One 3D query and one tag query, and the tag query asked for the vocabulary the detector reads
        // rather than for a word invented on the spot. There is no third question: nothing here offers a
        // pass a way to list every video, which is the ask these two queries exist to replace.
        Assert.Equal(1, store.ThreeDQueryCount);
        Assert.Equal(1, store.TagQueryCount);
        Assert.Equal(MvcEligibilityPrefilter.TagQueryValues, Assert.Single(store.TagQueries));
        Assert.Single(store.QueriedCandidates);
    }

    [Fact]
    public async Task APassNeverReadsTheSourcesOfAnOrdinaryMovie()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var ordinary = store.AddItemReturnedByNoQuery();
        var mvc = store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        // The MVC movie was read as media, once, and the ordinary one not at all: a named item is still
        // judged from its own fields, and an item no query named is never reached at all. The version is
        // the proof that the pass did its job while costing one read.
        Assert.Equal(1, mvc.MediaSourceReads);
        Assert.Equal(0, ordinary.MediaSourceReads);
        Assert.Equal(1, result.Created);
    }

    [Fact]
    public async Task TheShelfTheThreeDQueryNamesAndTheScannerRefusesCostsNoSourceRead()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        // The case the server's filter cannot answer and this half can: a library of half-SBS rips every
        // one of which comes back from a 3D query, and not one of which the MVP converts. Reading their
        // streams to find that out is the cost the cheap question takes away.
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
        // behind. Only the tag query can see it, and only the detector can tell its tag from the plain
        // "3D" a scraper hangs on an ordinary movie, so the versions have to arrive through both.
        var tagged = store.AddTaggedVersionRoot(ProfileVersionFixtures.CreateTaggedMvcMovie(OtherMovieId));

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

        // The tag query asks for names, not for meanings, so a movie wearing a tag from the ask list is
        // walked and then judged by the same rules everything else is. This one is in the list because a
        // person could type it; the answer it gets is the one the file deserves.
        var tagged = store.AddTaggedVersionRoot(ProfileVersionFixtures.CreateTaggedMvcMovie(OtherMovieId, tag: "mvc"));

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, tagged.MediaSourceReads);
    }

    // --- which item the versions belong to --------------------------------------

    [Fact]
    public async Task TheHiddenMvcChildAQueryNamesIsMaterialisedUnderTheItemTheUserReaches()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var root = store.AddItemRootOfNothing();
        var hidden = store.AddVersionRoot(ProfileVersionFixtures.CreateMvcAlternateItem(video3DFormat: Video3DFormat.MVC));

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        // The signals a query can read live on the hidden alternate - that is the whole reason the file
        // is invisible - and the versions cannot be filed under it: an item that names a primary is in
        // no browse list, so a version hung off it would be a version nobody can select. The walk from
        // the named item to the reachable one is what this pass owes the library.
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
    public async Task ARootIsReconciledOnceWhenItAndItsVersionAreBothNamed()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var root = store.AddVersionRoot(ProfileVersionFixtures.CreateStackedMvcMovie());
        var hidden = store.AddVersionRoot(ProfileVersionFixtures.CreateMvcAlternateItem(video3DFormat: Video3DFormat.MVC));

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        // Two named items, one movie, one answer. Running the same diff twice over one item would
        // report the second one as a change it made, and a media-source read for a stack is a read of
        // every file in it, so the second one is not free.
        Assert.Equal(1, result.Created);
        Assert.Equal(1, root.MediaSourceReads);
        Assert.Equal(0, hidden.MediaSourceReads);
        Assert.Single(store.Created);
        Assert.Single(store.LinksOf(root.Id));
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
        store.AddItem(OtherMovie(OtherMovieId, "/movies/Second (2015)/Second (2015).mkv"));
        store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());

        var configuration = ConfigurationWith(SideBySideFull, SideBySideHalf);
        var manager = CreateManager(store, configuration);

        var first = await manager.ReconcileLibraryAsync(CancellationToken.None);
        var second = await manager.ReconcileLibraryAsync(CancellationToken.None);

        // Narrowing what a pass looks at may not narrow what it does: the administrator who enabled two
        // profiles expects both in the picker, on the movie they named, and a pass over a library that
        // agrees with the settings still writes nothing the second time.
        Assert.Equal(2, first.Created);
        Assert.Equal(2, store.Created.Count);
        Assert.False(second.Changed);
        Assert.Empty(store.Updated);
        Assert.Empty(store.Deleted);
        Assert.Equal(2, store.LinksOf(ProfileVersionFixtures.MovieId).Count);
    }

    [Fact]
    public async Task TheStackedMovieStillGetsItsVersionsFromTheFileThatIsThreeD()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        var root = store.AddVersionRoot(ProfileVersionFixtures.CreateStackedMvcMovie());

        var result = await CreateManager(store, ConfigurationWith(SideBySideFull))
            .ReconcileLibraryAsync(CancellationToken.None);

        // The root says nothing about 3D in its own name, path or tags, and it is still reconciled -
        // because it names a version its own fields do not speak for, which is the one thing the cheap
        // question is not allowed to guess about.
        Assert.Equal(1, result.Created);
        Assert.Equal(1, root.MediaSourceReads);
        Assert.True(store.IsLinked(root.Id, Assert.Single(store.Created).Item.Id));
    }

    // --- what a pass says about itself ------------------------------------------

    [Fact]
    public async Task APassSaysWhatItConsideredWhatItRefusedAndWhatItScanned()
    {
        var store = new FakeProfileVersionItemStore();
        store.AddItem(ProfileVersionFixtures.CreateFolder());
        store.AddItem(OtherMovie(OtherMovieId, "/movies/Second (2015)/Second (2015).mkv"));
        store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());
        store.AddVersionRoot(WithFormat(ProfileVersionFixtures.CreateOrdinaryMovie(Guid.NewGuid()), Video3DFormat.HalfSideBySide));

        var logger = new ListLogger<ProfileVersionItemManager>();

        await CreateManager(store, ConfigurationWith(SideBySideFull), logger)
            .ReconcileLibraryAsync(CancellationToken.None);

        // Two named items: one of them a movie the cheap question refused outright, the other scanned.
        // The counts are the whole diagnostic - they are what a reader looks for when a library that
        // should be cheap is not - and the titles are not, because a pass over a library has no reason
        // to write the names of other people's movies into a log line it emits for itself.
        var considered = logger.Message(LogLevel.Debug, "reconciling profile version items");
        Assert.Contains("2 cheap candidates", considered, StringComparison.Ordinal);

        var summary = logger.Message(LogLevel.Debug, "without reading a media source");
        Assert.Contains("refused 1", summary, StringComparison.Ordinal);
        Assert.Contains("scanned 1", summary, StringComparison.Ordinal);

        Assert.DoesNotContain(considered, "Ready Player One", StringComparison.Ordinal);
        Assert.DoesNotContain(summary, "Ready Player One", StringComparison.Ordinal);
    }

    // --- fixtures ---------------------------------------------------------------

    private static ScriptedVideo OtherMovie(Guid id, string path)
        => ProfileVersionFixtures.CreateOrdinaryMovie(id, path);

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
