using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.Detection;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.VersionItems;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// Covers the order materialised version items sort in: every version is written a rank in its
/// <c>ForcedSortName</c> so the stock version picker lists the configured default first and the rest
/// of the enabled profiles behind it in offered order, deterministically and without any device
/// being consulted.
/// </summary>
/// <remarks>
/// <para>
/// A version item is a library item, and the server orders one item's alternate versions by their
/// sort name (<c>LibraryManager.GetLinkedAlternateVersions</c>). Left to their labels the versions
/// sort alphabetically - "2D Base" before "3D Anaglyph..." before "3D Half..." - which is nobody's
/// choice of what to start on. Writing a fixed-width rank into <c>ForcedSortName</c> (and refreshing
/// the derived <c>SortName</c> from it) is what makes that order say "the default first". It is a
/// global answer, asked with no device - and the dynamic provider path reads that same global
/// default, because the exact-device matching a pre-release build carried was removed before
/// release (the history is kept in <c>.shared/discovery/cp27-device-ordering_20260921.md</c>).
/// </para>
/// <para>
/// The other half of what these tests pin is the settle discipline the whole manager is built on: a
/// rank is compared against <c>ForcedSortName</c> - the persisted column that round-trips - and never
/// against the derived <c>SortName</c>, so writing it is a one-time event and a settled library is
/// left written. The <see cref="FakeProfileVersionItemStore"/> is the same in-memory library the rest
/// of the version-item tests use.
/// </para>
/// </remarks>
public class ProfileVersionItemOrderTests
{
    private const string SideBySideFull = ProfileIds.SideBySideFull;

    private const string SideBySideHalf = ProfileIds.SideBySideHalf;

    private const string TwoDBase = ProfileIds.TwoDBase;

    /// <summary>A second MVC file, so a pass has two sources to group versions by.</summary>
    private static readonly string SecondMvcPath = ProfileVersionFixtures.FolderPath + "/Ready Player One (2018) - 3D mvc2.mkv";

    private static readonly Guid SecondMvcItemId = Guid.Parse("3b2a1c0d-9e8f-4765-b432-1fedcba98765");

    // --- what a version is born wearing -------------------------------------------

    [Fact]
    public async Task ANewVersionIsBornRankedWithTheDefaultProfileFirst()
    {
        var store = NewStore(out var movie);

        // The default is not the first profile in display order: full side-by-side comes first in the
        // catalog, but the admin set half side-by-side as what the page offers first. The rank has to
        // follow the offered order, which promotes the default over the display order.
        var result = await CreateManager(store, ConfigurationWith(SideBySideHalf, SideBySideFull, SideBySideHalf))
            .ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(2, result.Created);

        var source = movie.StaticSources[0];

        var half = (Video)store.FindItem(VersionIdOf(source, SideBySideHalf))!;
        var full = (Video)store.FindItem(VersionIdOf(source, SideBySideFull))!;

        // The default wears the first rank; the other enabled profile follows. The rank is fixed-width
        // and the item's Name is untouched - still the human label the picker shows, not the number.
        Assert.Equal("001 - 3D Half Side-by-Side", half.ForcedSortName);
        Assert.Equal("002 - 3D Full Side-by-Side", full.ForcedSortName);

        // The label a client reads stays the profile's own, with no rank in front of it.
        Assert.Equal(new ProfileCatalog().GetProfile(SideBySideHalf).DisplayName, half.Name);
        Assert.Equal(new ProfileCatalog().GetProfile(SideBySideFull).DisplayName, full.Name);

        // SortName is the column the server actually orders the version list by, and the item model
        // normalises it (numbers padded, text folded) rather than echoing ForcedSortName - which is
        // exactly why the manager compares the forced half and not this derived one. What has to hold
        // is the order: the rank is the leading number, so the normalised sort name still puts the
        // default ahead of the rest.
        Assert.NotEqual(half.ForcedSortName, half.SortName);
        Assert.True(
            string.CompareOrdinal(half.SortName, full.SortName) < 0,
            "the default profile's version must sort first by the persisted sort column");
    }

    // --- what a pass repairs, and once --------------------------------------------

    [Fact]
    public async Task AVersionThatCarriesNoRankIsGivenOneOnceAndThenLeftAlone()
    {
        var store = NewStore(out var movie);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull, SideBySideFull));

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var versionId = VersionIdOf(movie.StaticSources[0], SideBySideFull);
        var version = (Video)store.FindItem(versionId)!;
        Assert.Equal("001 - 3D Full Side-by-Side", version.ForcedSortName);

        // The state every version item is in the moment this feature is installed over a library that
        // predates it: an item with its id and its marker and no rank of its own. It is filled in -
        // once.
        version.ForcedSortName = null;
        version.SortName = null;

        var repaired = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(1, repaired.Updated);
        Assert.Equal("001 - 3D Full Side-by-Side", version.ForcedSortName);

        // SortName is the server's normalised form of that forced rank, not a verbatim echo - which is
        // why the settle comparison reads ForcedSortName. Its stability is proven by the settle pass
        // below finding nothing to rewrite.
        Assert.False(string.IsNullOrEmpty(version.SortName));

        // And it settles straight afterwards: the comparison reads the same column the write put there,
        // so the rank the pass had to repair is a rank it will never have to repair again.
        var settled = await manager.ReconcileLibraryAsync(CancellationToken.None);
        Assert.False(settled.Changed);
        Assert.Single(store.Updated);
    }

    [Fact]
    public async Task ASettledRankIsNoReasonToRewriteAnItem()
    {
        var store = NewStore(out _);
        var manager = CreateManager(store, ConfigurationWith(SideBySideFull, SideBySideFull, SideBySideHalf, TwoDBase));

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var updated = store.Updated.Count;

        // A pass that found every version already wearing the rank it would have given it: read, and
        // stop. This is the pass that runs on every start-up and every settings save.
        var second = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.False(second.Changed);
        Assert.Equal(0, second.Created + second.Updated + second.Deleted + second.Skipped);
        Assert.Equal(updated, store.Updated.Count);
        Assert.Empty(store.Updated);
    }

    // --- what the settings change -------------------------------------------------

    [Fact]
    public async Task ChangingTheDefaultReRanksEveryVersionOnTheNextPass()
    {
        var store = NewStore(out var movie);
        var configuration = ConfigurationWith(SideBySideFull, SideBySideFull, SideBySideHalf, TwoDBase);
        var manager = CreateManager(store, configuration);

        await manager.ReconcileLibraryAsync(CancellationToken.None);

        var source = movie.StaticSources[0];
        var halfId = VersionIdOf(source, SideBySideHalf);
        var fullId = VersionIdOf(source, SideBySideFull);
        var twoDId = VersionIdOf(source, TwoDBase);

        Assert.Equal("001 - 3D Full Side-by-Side", ((Video)store.FindItem(fullId)!).ForcedSortName);
        Assert.Equal("002 - 3D Half Side-by-Side", ((Video)store.FindItem(halfId)!).ForcedSortName);
        Assert.Equal("003 - 2D Base", ((Video)store.FindItem(twoDId)!).ForcedSortName);

        // The page now offers 2D first. Same profiles, same items, same ids - only the order, and so
        // only the rank, moves. The items are not re-created; a user's resume position and chosen
        // version ride on the id, and none of that is disturbed by a re-rank.
        configuration.Configuration.DefaultProfileId = TwoDBase;

        var reRanked = await manager.ReconcileLibraryAsync(CancellationToken.None);

        Assert.Equal(3, reRanked.Updated);
        Assert.Equal(0, reRanked.Created + reRanked.Deleted);

        Assert.Equal("001 - 2D Base", ((Video)store.FindItem(twoDId)!).ForcedSortName);
        Assert.Equal("002 - 3D Full Side-by-Side", ((Video)store.FindItem(fullId)!).ForcedSortName);
        Assert.Equal("003 - 3D Half Side-by-Side", ((Video)store.FindItem(halfId)!).ForcedSortName);

        // And the re-ranked library settles at once: the new ranks are written exactly as they are
        // compared.
        var settled = await manager.ReconcileLibraryAsync(CancellationToken.None);
        Assert.False(settled.Changed);
    }

    // --- what more than one file, and more than nine versions, do -----------------

    [Fact]
    public async Task RanksGroupBySourceAndStayNumericPastNine()
    {
        // Ten profiles on a default that is not first in display order, over two MVC files: a plan of
        // twenty versions. It crosses the nine boundary (rank ten has to sort after rank nine, which an
        // unpadded number would not) and it exercises the source grouping the plan promises.
        var store = new FakeProfileVersionItemStore();
        var firstId = Guid.NewGuid();
        var firstSource = ProfileVersionFixtures.CreateSource(
            firstId,
            "3D mvc",
            ProfileVersionFixtures.MvcPath);
        var secondSource = ProfileVersionFixtures.CreateSource(
            SecondMvcItemId,
            "3D mvc2",
            SecondMvcPath);

        var movie = store.AddVersionRoot(ProfileVersionFixtures.CreateMovie(new[] { firstSource, secondSource }));
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        var enabled = new List<string>
        {
            SideBySideFull,
            ProfileIds.AnaglyphRedCyanDubois,
            ProfileIds.AnaglyphRedCyanColor,
            ProfileIds.AnaglyphRedCyanHalfColor,
            ProfileIds.AnaglyphRedCyanGray,
            ProfileIds.AnaglyphGreenMagentaDubois,
            ProfileIds.AnaglyphGreenMagentaColor,
            ProfileIds.AnaglyphGreenMagentaHalfColor,
            ProfileIds.AnaglyphGreenMagentaGray,
            SideBySideHalf
        };

        var configuration = ConfigurationWith(SideBySideHalf, enabled.ToArray());
        var manager = CreateManager(store, configuration, new ScriptedMvcDetector(ProfileVersionFixtures.MvcPath, SecondMvcPath));

        // Asked about directly, the way a library event would ask about the item a user is on: the two
        // MVC files both arrive as sources of this one item and both convert, which is the multi-source
        // shape the whole plan has to keep grouped.
        var result = await manager.ReconcileItemAsync(movie.Id, CancellationToken.None);

        Assert.Equal(20, result.Created);

        var offered = new ProfileCatalog().GetOfferedProfiles(configuration.Configuration);
        Assert.Equal(SideBySideHalf, offered[0].Id); // default promoted to the front.

        // The order the picker should show: every version of the first file (default first), then every
        // version of the second - source grouping, and default promotion inside each group. That order,
        // read back through the items' own sort names, has to be the order the ranks put them in.
        var sources = new[] { firstSource, secondSource };
        var expectedOrder = new List<Guid>();
        foreach (var source in sources)
        {
            foreach (var profile in offered)
            {
                expectedOrder.Add(AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(source.Id, source.Path, profile.Id));
            }
        }

        var actualOrder = store.Created
            .Select(created => (Id: created.Item.Id, SortName: created.Item.ForcedSortName ?? string.Empty))
            .OrderBy(entry => entry.SortName, StringComparer.Ordinal)
            .Select(entry => entry.Id)
            .ToList();

        Assert.Equal(expectedOrder, actualOrder);

        // Every rank is the fixed-width shape, and the sequence is exactly 1..20 with no gaps: rank ten
        // spells "010", so it sorts after "009" as a number does, and rank one is padded to the same
        // width as rank twenty.
        var ranks = new List<int>();
        foreach (var created in store.Created)
        {
            Assert.True(
                TryReadFixedRank(created.Item.ForcedSortName, out var rank),
                $"rank {created.Item.ForcedSortName} is not the fixed-width 'NNN - label' shape");
            ranks.Add(rank);
        }

        Assert.Equal(Enumerable.Range(1, 20), ranks.OrderBy(rank => rank));

        // The default wears the first rank of its own file, and the first rank of the second file is
        // what comes after the whole of the first - which is the grouping, made visible.
        var firstFileDefault = (Video)store.FindItem(
            AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(firstSource.Id, firstSource.Path, SideBySideHalf))!;
        var secondFileDefault = (Video)store.FindItem(
            AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(secondSource.Id, secondSource.Path, SideBySideHalf))!;

        Assert.StartsWith("001 - ", firstFileDefault.ForcedSortName, StringComparison.Ordinal);
        Assert.StartsWith("011 - ", secondFileDefault.ForcedSortName, StringComparison.Ordinal);
    }

    // --- fixtures -----------------------------------------------------------------

    private static FakeProfileVersionItemStore NewStore(out ScriptedVideo movie)
    {
        var store = new FakeProfileVersionItemStore();
        movie = store.AddVersionRoot(ProfileVersionFixtures.CreateSingleFileMvcMovie());
        store.AddItem(ProfileVersionFixtures.CreateFolder());

        return store;
    }

    private static StubConfigurationSource ConfigurationWith(string defaultProfileId, params string[] enabledProfileIds)
        => new()
        {
            Configuration = new PluginConfiguration
            {
                DefaultProfileId = defaultProfileId,
                EnabledProfileIds = enabledProfileIds.ToList()
            }
        };

    private static ProfileVersionItemManager CreateManager(
        FakeProfileVersionItemStore store,
        StubConfigurationSource configuration)
        => CreateManager(store, configuration, new ScriptedMvcDetector(ProfileVersionFixtures.MvcPath));

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

    private static Guid VersionIdOf(MediaSourceInfo source, string profileId)
        => AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(source.Id, source.Path, profileId);

    /// <summary>
    /// Reads the fixed-width rank out of a sort name: three ASCII digits, the separator, then a label.
    /// </summary>
    private static bool TryReadFixedRank(string? sortName, out int rank)
    {
        rank = -1;

        var prefixLength = "000 - ".Length;

        if (string.IsNullOrEmpty(sortName) || sortName.Length < prefixLength)
        {
            return false;
        }

        var digits = sortName.Substring(0, 3);
        if (!digits.All(char.IsAsciiDigit) || !sortName.Substring(3, 3).Equals(" - ", StringComparison.Ordinal))
        {
            return false;
        }

        rank = int.Parse(digits, CultureInfo.InvariantCulture);
        return true;
    }
}
