using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.Markers;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anaglyfin.Tests.VersionItems;

/// <summary>
/// Covers the one identity a profile version has, and the label it wears, from the side of the
/// library item that is created from it.
/// </summary>
/// <remarks>
/// The provider's own tests already pin the shape of a version as a media source. What matters to
/// the version-item manager is three facts about that same object: that its id is derivable a second
/// time from the file and profile alone (so the manager can name an item it has never created), that
/// the id is not the id of the source it converts (so the original stays playable beside it), and
/// that both roads - the dynamic offer and the materialised item - reach a client with the same
/// answer. A version whose item and whose offer disagreed about either would be one version a client
/// could resolve twice, differently.
/// </remarks>
public class ProfileVersionIdentityTests
{
    private const string SideBySideFull = ProfileIds.SideBySideFull;

    private const string SideBySideHalf = ProfileIds.SideBySideHalf;

    [Fact]
    public void TheItemIdIsTheIdTheVersionIsOfferedBy()
    {
        var item = ProfileVersionFixtures.CreateSingleFileMvcMovie();
        var source = item.StaticSources[0];
        var profile = new ProfileCatalog().GetProfile(SideBySideFull);

        var version = ProfileVersionSource.Build(
            item,
            ProfileVersionFixtures.CreateEligibleSource(item, source),
            profile,
            labelSource: false);

        // The static source of a linked item is keyed by that item's id, so these two spellings are
        // one identity - which is what lets the provider notice the version it would otherwise offer
        // again, and what lets the manager predict an item's id before it exists.
        var itemId = ProfileVersionSource.GetVersionItemId(version.Id);

        Assert.Equal(
            AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(source.Id, source.Path, profile.Id),
            itemId);
        Assert.Equal(
            Guid.Parse(AnaglyfinMediaSourceProvider.BuildMediaSourceId(item.Id, source.Path, profile.Id)),
            itemId);
        Assert.Equal(version.Id, itemId.ToString("N", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AVersionNeverWearsTheIdOfTheFileItConverts()
    {
        var item = ProfileVersionFixtures.CreateStackedMvcMovie();
        var mvcSource = item.StaticSources[1];
        var profile = new ProfileCatalog().GetProfile(SideBySideFull);

        var version = ProfileVersionSource.Build(
            item,
            ProfileVersionFixtures.CreateEligibleSource(item, mvcSource),
            profile,
            labelSource: true);

        // Wearing the converted file's own id would shadow the original in the version list and make
        // the file unplayable, which is why the id is folded from it rather than taken from it.
        Assert.NotEqual(mvcSource.Id, version.Id);
        Assert.NotEqual(item.Id.ToString("N", CultureInfo.InvariantCulture), version.Id);
    }

    [Fact]
    public void OneVersionIsTheSameItemEveryTimeItIsAskedFor()
    {
        var item = ProfileVersionFixtures.CreateSingleFileMvcMovie();
        var source = item.StaticSources[0];
        var profile = new ProfileCatalog().GetProfile(SideBySideFull);
        var eligible = ProfileVersionFixtures.CreateEligibleSource(item, source);

        var first = ProfileVersionSource.Build(item, eligible, profile, labelSource: false);
        var second = ProfileVersionSource.Build(item, eligible, profile, labelSource: false);

        // Resume position and played state ride on this id, and a second pass must find the item it
        // created rather than plan a new one.
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(
            ProfileVersionSource.GetVersionItemId(first.Id),
            AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(source.Id, source.Path, profile.Id));
    }

    [Fact]
    public void TheIdSurvivesADifferentSpellingOfTheSameProfile()
    {
        var item = ProfileVersionFixtures.CreateSingleFileMvcMovie();
        var source = item.StaticSources[0];
        var catalog = new ProfileCatalog();

        var lower = ProfileVersionSource.Build(
            item,
            ProfileVersionFixtures.CreateEligibleSource(item, source),
            catalog.GetProfile(SideBySideFull),
            labelSource: false);
        var upper = AnaglyfinMediaSourceProvider.BuildMediaSourceIdFromSource(source.Id, source.Path, "SBS_FULL");

        // The server compares source ids byte for byte at stream time, so two spellings of one
        // profile must fold to one id - and one item, not two.
        Assert.Equal(upper, lower.Id);
    }

    [Fact]
    public void EveryProfileOfOneFileIsItsOwnItem()
    {
        var item = ProfileVersionFixtures.CreateSingleFileMvcMovie();
        var source = item.StaticSources[0];
        var catalog = new ProfileCatalog();
        var eligible = ProfileVersionFixtures.CreateEligibleSource(item, source);

        var full = ProfileVersionSource.GetVersionItemId(
            ProfileVersionSource.Build(item, eligible, catalog.GetProfile(SideBySideFull), false).Id);
        var half = ProfileVersionSource.GetVersionItemId(
            ProfileVersionSource.Build(item, eligible, catalog.GetProfile(SideBySideHalf), false).Id);

        Assert.NotEqual(full, half);
        Assert.NotEqual(Guid.Empty, full);
        Assert.NotEqual(Guid.Empty, half);
    }

    [Fact]
    public void TwoFilesOfOneItemNeverShareAnItemIdentity()
    {
        var item = ProfileVersionFixtures.CreateStackedMvcMovie();
        var plain = item.StaticSources[0];
        var mvc = item.StaticSources[1];
        var profile = new ProfileCatalog().GetProfile(SideBySideFull);

        var fromPlain = ProfileVersionSource.GetVersionItemId(
            ProfileVersionSource.Build(item, ProfileVersionFixtures.CreateEligibleSource(item, plain), profile, true).Id);
        var fromMvc = ProfileVersionSource.GetVersionItemId(
            ProfileVersionSource.Build(item, ProfileVersionFixtures.CreateEligibleSource(item, mvc), profile, true).Id);

        // Two items under one id is one item the server resolves, and which one it resolves to is
        // list order rather than the user's choice.
        Assert.NotEqual(fromPlain, fromMvc);
    }

    [Fact]
    public void AVersionWithNoSourceIdIsStillOneDeterministicItem()
    {
        var item = ProfileVersionFixtures.CreateSingleFileMvcMovie();
        var profile = new ProfileCatalog().GetProfile(SideBySideFull);

        // A source implementation that keys itself with something other than a GUID: the path is the
        // identity of last resort, and it has to be as stable as a real id is.
        var source = new MvcEligibleSource(
            item,
            original: null,
            sourcePath: ProfileVersionFixtures.MvcPath,
            sourceId: string.Empty,
            sourceName: null,
            identityKey: MvcEligibleSourceScanner.PathDigestKey(ProfileVersionFixtures.MvcPath));

        var version = ProfileVersionSource.Build(item, source, profile, labelSource: false);
        var itemId = ProfileVersionSource.GetVersionItemId(version.Id);

        Assert.Equal(
            AnaglyfinMediaSourceProvider.BuildVersionItemIdFromSource(null, ProfileVersionFixtures.MvcPath, profile.Id),
            itemId);
        Assert.NotEqual(item.Id, itemId);
    }

    [Fact]
    public void OneFileNeedsNothingInTheLabelButItsProfile()
    {
        var item = ProfileVersionFixtures.CreateSingleFileMvcMovie();
        var source = item.StaticSources[0];
        var profile = new ProfileCatalog().GetProfile(SideBySideFull);
        var eligible = ProfileVersionFixtures.CreateEligibleSource(item, source);

        var version = ProfileVersionSource.Build(item, eligible, profile, labelSource: false);

        // With one file to convert, naming it in the label is noise in the picker the user reads.
        Assert.Equal(profile.DisplayName, version.Name);
        Assert.Equal("3D Full Side-by-Side", version.Name);
        Assert.Equal(profile.DisplayName, ProfileVersionSource.VersionName(eligible, profile, labelSource: false));
    }

    [Fact]
    public void SeveralFilesSayWhichOneTheyConvert()
    {
        var item = ProfileVersionFixtures.CreateStackedMvcMovie();
        var mvc = item.StaticSources[1];
        var profile = new ProfileCatalog().GetProfile(SideBySideFull);
        var eligible = ProfileVersionFixtures.CreateEligibleSource(item, mvc);

        var version = ProfileVersionSource.Build(item, eligible, profile, labelSource: true);

        // Two versions labelled "3D Full Side-by-Side" over two different originals is a picker the
        // user cannot choose from - and the choice they make is what gets encoded.
        Assert.Equal("3D mvc / 3D Full Side-by-Side", version.Name);
        Assert.Equal(
            "3D mvc / 3D Full Side-by-Side",
            ProfileVersionSource.VersionName(eligible, profile, labelSource: true));
    }

    [Fact]
    public void AFileWithNoLabelOfItsOwnIsNamedFromItsFile()
    {
        var item = ProfileVersionFixtures.CreateSingleFileMvcMovie();
        var profile = new ProfileCatalog().GetProfile(SideBySideFull);
        var unnamed = new MvcEligibleSource(
            item,
            original: item.StaticSources[0],
            sourcePath: ProfileVersionFixtures.MvcPath,
            sourceId: item.StaticSources[0].Id,
            sourceName: "   ",
            identityKey: MvcEligibleSourceScanner.SourceIdentityKey(item.StaticSources[0].Id, ProfileVersionFixtures.MvcPath));

        var label = ProfileVersionSource.VersionName(unnamed, profile, labelSource: true);

        // The file name is what the user recognises from their own library, so it is a better label
        // than the profile alone once there is more than one file to tell apart.
        Assert.Equal("Ready Player One (2018) - 3D mvc / 3D Full Side-by-Side", label);
    }

    [Fact]
    public async Task BothRoadsHandTheClientTheSameVersion()
    {
        var item = ProfileVersionFixtures.CreateStackedMvcMovie();
        var mvcSource = item.StaticSources[1];
        var profile = new ProfileCatalog().GetProfile(SideBySideFull);

        var provider = new AnaglyfinMediaSourceProvider(
            new ScriptedMvcDetector(ProfileVersionFixtures.MvcPath),
            new ProfileCatalog(),
            new StubConfigurationSource
            {
                Configuration = new PluginConfiguration
                {
                    DefaultProfileId = SideBySideFull,
                    EnabledProfileIds = new List<string> { SideBySideFull }
                }
            },
            NullLogger<AnaglyfinMediaSourceProvider>.Instance);

        var offered = (await provider.GetMediaSources(item, CancellationToken.None)).ToList();

        var offeredVersion = Assert.Single(offered);
        var materialised = ProfileVersionSource.Build(
            item,
            ProfileVersionFixtures.CreateEligibleSource(item, mvcSource),
            profile,
            labelSource: false);

        // The version a user picks and the version that version exists as in the library must be one
        // answer. Every field a client or the transcode pipeline reads is compared, because the item
        // is created from the materialised one and offered by the other.
        Assert.Equal(materialised.Id, offeredVersion.Id);
        Assert.Equal(materialised.Name, offeredVersion.Name);
        Assert.Equal(materialised.Path, offeredVersion.Path);
        Assert.Equal(materialised.Protocol, offeredVersion.Protocol);
        Assert.Equal(materialised.Container, offeredVersion.Container);
        Assert.Equal(materialised.RunTimeTicks, offeredVersion.RunTimeTicks);
        Assert.Equal(materialised.Size, offeredVersion.Size);
        Assert.Equal(materialised.SupportsDirectPlay, offeredVersion.SupportsDirectPlay);
        Assert.Equal(materialised.SupportsDirectStream, offeredVersion.SupportsDirectStream);
        Assert.Equal(materialised.SupportsTranscoding, offeredVersion.SupportsTranscoding);
        Assert.Equal(materialised.Video3DFormat, offeredVersion.Video3DFormat);

        // What a version says about its own media has to be the same answer whichever road it arrived
        // by. Before this field was written on both roads the two differed silently - the dynamic
        // source named no video type, the item named the type its type gives an unwritten field - and
        // nothing downstream could see the difference until something read it.
        Assert.Equal(ProfileVersionSource.VersionVideoType, offeredVersion.VideoType);
        Assert.Equal(materialised.VideoType, offeredVersion.VideoType);
        Assert.Equal(
            materialised.MediaStreams.Select(stream => (stream.Type, stream.Index, stream.Codec, stream.Language, stream.Width, stream.Height)),
            offeredVersion.MediaStreams.Select(stream => (stream.Type, stream.Index, stream.Codec, stream.Language, stream.Width, stream.Height)));
    }

    [Fact]
    public void AVersionItemIsBuiltFromAMarkerAndNothingElse()
    {
        var item = ProfileVersionFixtures.CreateSingleFileMvcMovie();
        var source = item.StaticSources[0];
        var profile = new ProfileCatalog().GetProfile(SideBySideFull);

        var version = ProfileVersionSource.Build(
            item,
            ProfileVersionFixtures.CreateEligibleSource(item, source),
            profile,
            labelSource: false);

        // The item created from a version carries this path, so it is the marker that keeps the
        // version on the encoding path: the wrapper recognises it, and a path that were a real file
        // would make the item a copy of the original rather than a conversion of it.
        Assert.StartsWith(ProfileMarker.MarkerPrefix, version.Path, StringComparison.Ordinal);
        Assert.Contains(ProfileMarker.SourceQueryParameter, version.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(ProfileVersionFixtures.MvcPath, version.Path, StringComparison.Ordinal);
        Assert.False(ProfileMarkerParser.IsMarkerCandidate(source.Path));
    }
}
