using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using Anaglyfin.Tests.VersionItems;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anaglyfin.Tests.MediaSources;

/// <summary>
/// Covers the read filter behind the "Offer original 3D MVC version" setting: what it takes out of
/// the two lists a user picks a version from, everything it leaves where the server put it, and the
/// forwarding that has to stay exact for the rest of the server to keep working through it.
/// </summary>
/// <remarks>
/// <para>
/// The library under test is the one this feature was reproduced on, built on
/// <see cref="ProfileVersionFixtures">the shared fixtures</see>: a movie whose own 1080p file is its
/// first source, an ordinary second version, and the 3D MVC file the scanner filed beside them. The
/// decorator runs against the real detector and the real profile catalog wherever the assertion is
/// about the product answer, so the tests meet the rules a playback request will meet rather than a
/// scripted stand-in for them.
/// </para>
/// <para>
/// What no test here can prove is the half above the decorator: that the server resolves
/// <see cref="IMediaSourceManager"/> to it everywhere a source list is built, and that a client's
/// version picker is therefore the thing that changes. <see cref="PluginServiceRegistratorTests"/>
/// pins the registration half; the client half needs a real server.
/// </para>
/// </remarks>
public class MediaSourceManagerSuppressionDecoratorTests
{
    /// <summary>The members the decorator answers itself. Everything else must reach the core.</summary>
    private static readonly string[] FilteredMembers =
    [
        nameof(IMediaSourceManager.GetStaticMediaSources),
        nameof(IMediaSourceManager.GetPlaybackMediaSources)
    ];

    /// <summary>The members whose whole job is to hand the call to the core manager untouched.</summary>
    private static readonly string[] ForwardedMembers =
    [
        nameof(IMediaSourceManager.AddParts),
        nameof(IMediaSourceManager.GetMediaStreams),
        nameof(IMediaSourceManager.GetMediaAttachments),
        nameof(IMediaSourceManager.GetMediaSource),
        nameof(IMediaSourceManager.OpenLiveStream),
        nameof(IMediaSourceManager.OpenLiveStreamInternal),
        nameof(IMediaSourceManager.GetLiveStream),
        nameof(IMediaSourceManager.GetLiveStreamWithDirectStreamProvider),
        nameof(IMediaSourceManager.GetLiveStreamInfo),
        nameof(IMediaSourceManager.GetLiveStreamInfoByUniqueId),
        nameof(IMediaSourceManager.GetRecordingStreamMediaSources),
        nameof(IMediaSourceManager.CloseLiveStream),
        nameof(IMediaSourceManager.GetLiveStreamMediaInfo),
        nameof(IMediaSourceManager.SupportsDirectStream),
        nameof(IMediaSourceManager.GetPathProtocol),
        nameof(IMediaSourceManager.SetDefaultAudioAndSubtitleStreamIndices),
        nameof(IMediaSourceManager.AddMediaInfoWithProbe)
    ];

    // ---- what the filter does to a version list -------------------------------------

    [Fact]
    public void ACheckedSettingAnswersWithTheServersOwnListUntouched()
    {
        // The shipped position, and the answer for every installation that never configured anything:
        // the very list object the core manager built, in its own order, with nothing examined and
        // nothing copied. The details page of every ordinary movie in the library is answered here,
        // and a filter that rebuilt lists nobody had asked it to touch would be a per-request cost
        // paid for a decision that was never made.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var detector = new CountingDetector();

        var decorator = CreateDecorator(core, detector, new ProfileCatalog(), offerOriginal: true);

        var sources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);

        Assert.Same(core.StaticSources, sources);
        Assert.Equal(0, detector.AskedCount);
        Assert.Equal(1, core.StaticCalls);
    }

    [Fact]
    public async Task ACheckedSettingAnswersThePlaybackListUntouchedToo()
    {
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: true);

        var sources = await decorator.GetPlaybackMediaSources(movie, null, allowMediaProbe: false, enablePathSubstitution: false, CancellationToken.None);

        Assert.Same(core.PlaybackSources, sources);
        Assert.Equal(1, core.PlaybackCalls);
    }

    [Fact]
    public async Task AnUncheckedSettingDropsTheRawMvcVersionFromBothLists()
    {
        // The two surfaces a user picks from, filtered by one decision. They are tested together on
        // purpose: a details page that still offers the raw file while PlaybackInfo hides it - or the
        // other way round - is one bug wearing two faces, and the two lists converge on this pair of
        // functions.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        var staticSources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);
        var playbackSources = await decorator.GetPlaybackMediaSources(movie, null, allowMediaProbe: false, enablePathSubstitution: false, CancellationToken.None);

        Assert.DoesNotContain(core.MvcSource, staticSources, ReferenceSame);
        Assert.DoesNotContain(core.MvcSource, playbackSources, ReferenceSame);

        // Path substitution is a client's view of the same file, not a different version of it: the
        // answer is the same either way.
        Assert.DoesNotContain(core.MvcSource, decorator.GetStaticMediaSources(movie, enablePathSubstitution: true), ReferenceSame);
    }

    [Fact]
    public void TheSurvivingSourcesKeepTheirOwnOrderAndIdentity()
    {
        // Entries are taken out of the server's list; nothing is added, renumbered, rewritten or
        // replaced by an equal-looking copy. Order is the server's business - preferred version first,
        // then direct play and stream capability, protocol, bitrate fit, and the resume reordering it
        // performs per user - and a filter that re-sorted what survived it would be an opinion about
        // version order that nobody asked for.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        var sources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);

        var expected = new List<MediaSourceInfo> { core.OwnSource, core.OtherAlternateSource };
        expected.AddRange(core.VersionSources);

        AssertReferenceSequenceEqual(expected, sources);
    }

    [Fact]
    public void TheConvertedVersionsSurviveTheirOwnOriginalBeingHidden()
    {
        // The whole of the trade: the raw file goes and the conversions of it stay. Each one is safe
        // by construction rather than by a special case - a version's path is a marker URL behind the
        // HTTP protocol, so the one gate that refuses a remote source refuses it, and Anaglyfin's own
        // scanner refuses it for that very same reason.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        var sources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);

        foreach (var version in core.VersionSources)
        {
            Assert.Contains(version, sources, ReferenceSame);
        }

        Assert.All(core.VersionSources, version => Assert.StartsWith(ProfileMarker.MarkerPrefix, version.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void AVersionThatExistsAsALibraryItemSurvivesAsWell()
    {
        // A materialised profile version reaches the picker as a static source rather than as one a
        // provider added, reported from its item's own fields: the marker URL as its path, and the
        // server's own verdict that a URL path is remote. Both gates apply to it, and it survives on
        // either.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var materialised = core.VersionSources[0];
        materialised.IsRemote = true;

        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        var sources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);

        Assert.Contains(materialised, sources, ReferenceSame);
        Assert.DoesNotContain(core.MvcSource, sources, ReferenceSame);
    }

    [Fact]
    public void AnOrdinaryAlternateVersionIsNotTheOriginalMvcVersion()
    {
        // Hiding the MVC file is a decision about one file. The other versions of the same movie are
        // not part of it, however much they resemble the entry being hidden - and this one is filed
        // in the same folder, under the same item, beside it.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        var sources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);

        Assert.Contains(core.OwnSource, sources, ReferenceSame);
        Assert.Contains(core.OtherAlternateSource, sources, ReferenceSame);
    }

    [Fact]
    public void TheItemAskedAboutAlwaysKeepsItsOwnSource()
    {
        // The single-file MVC movie: the item, its only file and the raw MVC entry are one and the
        // same thing, so the rule that protects it is not a courtesy, it is the difference between a
        // library item that plays and one with nothing left to play. Its source is keyed by the item's
        // own id, and that is the only test the filter makes.
        var movie = ProfileVersionFixtures.CreateSingleFileMvcMovie();
        var ownSource = Assert.Single(movie.StaticSources);

        var core = new FakeMediaSourceManager(movie);
        core.Script(new[] { ownSource });

        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        Assert.Same(core.StaticSources, decorator.GetStaticMediaSources(movie, enablePathSubstitution: false));
    }

    [Fact]
    public async Task AVersionOfTheItemKeepsItsSourceWhenTheItemItselfIsAskedAbout()
    {
        // The hidden MVC alternate version is not invisible to the server: asked about as itself - a
        // resume pointing at it, a client that was given its id - it is an item over its own file, and
        // this setting is about the picker of some other item. Same file, same MVC name, same
        // detection, and this time nothing is hidden, because the source is keyed by the item being
        // asked about.
        var mvcItem = ProfileVersionFixtures.CreateMvcAlternateItem(scraped: false);
        var ownSource = Assert.Single(mvcItem.StaticSources);

        var core = new FakeMediaSourceManager(mvcItem);
        core.Script(new[] { ownSource });

        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        Assert.Same(core.StaticSources, decorator.GetStaticMediaSources(mvcItem, enablePathSubstitution: false));
        Assert.Same(
            core.PlaybackSources,
            await decorator.GetPlaybackMediaSources(mvcItem, null, allowMediaProbe: false, enablePathSubstitution: false, CancellationToken.None));
    }

    [Fact]
    public void NothingButALibraryFileIsEverACandidate()
    {
        // The gate is the scanner's own: a source served over anything but a file, a source the server
        // has no path for yet, a pointer file and a disc image are all refused as conversion inputs,
        // so none of them is a raw MVC version this setting could hide - whatever their names say, and
        // every one of them named "3D mvc" here.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);

        var remote = ProfileVersionFixtures.CreateSource(
            Guid.NewGuid(),
            "3D mvc",
            "http://server/media/Ready Player One (2018) - 3D mvc.mkv");
        remote.Protocol = MediaProtocol.Http;
        remote.IsRemote = true;

        var placeholder = ProfileVersionFixtures.CreateSource(Guid.NewGuid(), "3D mvc", ProfileVersionFixtures.MvcPath);
        placeholder.Type = MediaSourceType.Placeholder;
        placeholder.Path = string.Empty;

        var pointer = ProfileVersionFixtures.CreateSource(Guid.NewGuid(), "3D mvc", ProfileVersionFixtures.FolderPath + "/3D mvc.strm");

        var disc = ProfileVersionFixtures.CreateSource(Guid.NewGuid(), "3D mvc", ProfileVersionFixtures.FolderPath + "/3D mvc/BDMV");
        disc.VideoType = VideoType.BluRay;

        core.AddExtraSources(remote, placeholder, pointer, disc);

        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        var sources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);

        Assert.DoesNotContain(core.MvcSource, sources, ReferenceSame);
        Assert.Contains(remote, sources, ReferenceSame);
        Assert.Contains(placeholder, sources, ReferenceSame);
        Assert.Contains(pointer, sources, ReferenceSame);
        Assert.Contains(disc, sources, ReferenceSame);
    }

    [Fact]
    public void ASourceTheServerDoesNotKeyByAnItemIsKept()
    {
        // A static source is keyed by the id of the item its file belongs to, and that key is what
        // makes it a version of this item. Something else - a hand built report, an older server - is
        // not a version this feature was ever about, and there is no converted version of it to offer
        // in its place. An id that will not parse is answered the way a detector that cannot answer is
        // answered: the entry stays, even when it names the very file whose keyed entry is hidden.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);

        var unkeyed = ProfileVersionFixtures.CreateSource(Guid.NewGuid(), "3D mvc", ProfileVersionFixtures.MvcPath);
        unkeyed.Id = "anaglyfin-not-an-item-id";
        core.AddExtraSources(unkeyed);

        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        var sources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);

        Assert.Contains(unkeyed, sources, ReferenceSame);
        Assert.DoesNotContain(core.MvcSource, sources, ReferenceSame);
    }

    // ---- what the filter refuses to do ----------------------------------------------

    [Fact]
    public void TheRawVersionSurvivesWhenNoProfileIsOffered()
    {
        // The first fail-safe. An administrator who disabled every profile has no converted version of
        // anything to offer, so hiding the raw file would take a way of watching the film away and add
        // nothing in its place: the setting is a trade, and with nothing on the other side there is no
        // trade to make.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: false);

        var decorator = CreateDecorator(core, new MvcSourceDetector(), new NoEnabledProfilesCatalog(), offerOriginal: false);

        var sources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);

        Assert.Same(core.StaticSources, sources);
        Assert.Contains(core.MvcSource, sources, ReferenceSame);
    }

    [Fact]
    public void TheRawVersionSurvivesWhenNoVersionIsOfferedForThatFile()
    {
        // The second fail-safe, and the one that has to be asked per file: profiles may be enabled and
        // this item may still have nothing to convert - its sources are mid-refresh, or the file that
        // reads as MVC is not one the offer would build a version from. The scanner is the authority on
        // that question, so the filter asks it rather than trusting its own cheaper look at the entry,
        // and keeps the entry when the answer is nothing.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);

        // The list the server answered with still carries the MVC entry, while the item itself can no
        // longer name that file - an item between refreshes, which is the case the scanner degrades to
        // the item alone.
        movie.StaticSources = new[] { core.OwnSource };

        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        var sources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);

        Assert.Same(core.StaticSources, sources);
        Assert.Contains(core.MvcSource, sources, ReferenceSame);
    }

    [Fact]
    public void ADetectorThatCannotAnswerKeepsEverySource()
    {
        // Detection runs on a path a user is waiting for, and an exception here would be swallowed by
        // the server a layer up and cost the item its whole version list. An answer that never arrived
        // is therefore treated as no evidence to hide anything with - after the detector was asked, so
        // that this is a refusal rather than a case that was never reached.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var detector = new CountingDetector { Throws = true };

        var decorator = CreateDecorator(core, detector, new ProfileCatalog(), offerOriginal: false);

        var sources = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);

        Assert.Same(core.StaticSources, sources);
        Assert.True(detector.AskedCount > 0, "the filter hid or kept the raw MVC entry without asking the detector about it");
    }

    [Fact]
    public void ASettingsSourceThatFailsKeepsEverySource()
    {
        // The settings seam is documented never to fail, and is treated like everything else on this
        // path: a source that cannot be judged is a source that stays.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);

        var decorator = new MediaSourceManagerSuppressionDecorator(
            core,
            ownsCore: true,
            new MvcSourceDetector(),
            new ProfileCatalog(),
            new ThrowingConfigurationSource(),
            NullLogger<MediaSourceManagerSuppressionDecorator>.Instance);

        Assert.Same(core.StaticSources, decorator.GetStaticMediaSources(movie, enablePathSubstitution: false));
    }

    [Fact]
    public async Task AFailureOfTheCoreManagerIsNotSwallowed()
    {
        // Failing safe is about this decorator's own decisions. The server failing to answer at all is
        // the server's to see, and turning that into an empty list would replace a reported error with
        // a library that quietly has no versions.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true) { Throws = true };
        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        Assert.Throws<InvalidOperationException>(() => decorator.GetStaticMediaSources(movie, enablePathSubstitution: false));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await decorator.GetPlaybackMediaSources(movie, null, false, false, CancellationToken.None));
    }

    [Fact]
    public async Task AnEmptyAnswerIsAnsweredWithNoSources()
    {
        // The core is allowed to answer with nothing, and the filter answers that with the same nothing
        // rather than by going looking for a raw MVC file in a list that is not there.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie);
        core.Script(Array.Empty<MediaSourceInfo>());

        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        Assert.Empty(decorator.GetStaticMediaSources(movie, enablePathSubstitution: false));
        Assert.Empty(await decorator.GetPlaybackMediaSources(movie, null, false, false, CancellationToken.None));
    }

    // ---- the setting, live -----------------------------------------------------------

    [Fact]
    public void TickingTheSettingBackRestoresTheEntryOnTheNextRequest()
    {
        // The reversibility is the product promise, and it is a property of where the filter sits:
        // nothing was deleted, unlinked or hidden in the library, so a save is the whole of the change.
        // No scan, no restart, no cache to invalidate - and the settings seam hands back the live
        // object, which is what lets the very next request see the new answer.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var settings = new StubConfigurationSource();

        var decorator = new MediaSourceManagerSuppressionDecorator(
            core,
            ownsCore: true,
            new MvcSourceDetector(),
            new ProfileCatalog(),
            settings,
            NullLogger<MediaSourceManagerSuppressionDecorator>.Instance);

        Assert.Contains(core.MvcSource, decorator.GetStaticMediaSources(movie, enablePathSubstitution: false), ReferenceSame);

        settings.Configuration = new PluginConfiguration { OfferOriginalMVCVersion = false };

        var hidden = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);
        Assert.DoesNotContain(core.MvcSource, hidden, ReferenceSame);
        Assert.Equal(core.StaticSources.Count - 1, hidden.Count);

        settings.Configuration = new PluginConfiguration { OfferOriginalMVCVersion = true };

        Assert.Same(core.StaticSources, decorator.GetStaticMediaSources(movie, enablePathSubstitution: false));
    }

    [Fact]
    public void TheAnswerIsTheSameForEveryClient()
    {
        // The provider answers the same for every client and reads nothing about the request around the
        // call. A picker that hid the raw file for one client and offered it to another would make the
        // version list depend on who asked, so the filter follows the same rule: the user travels to
        // the core untouched, and is never consulted by the filter - which is also what makes a
        // background, DLNA or API-key request get the same answer as a signed-in one.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);
        var user = new User("tester", "anaglyfin-test", "anaglyfin-test");

        var anonymous = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);
        var signedIn = decorator.GetStaticMediaSources(movie, enablePathSubstitution: true, user);

        Assert.DoesNotContain(core.MvcSource, anonymous, ReferenceSame);
        Assert.DoesNotContain(core.MvcSource, signedIn, ReferenceSame);
        Assert.Equal(2, core.StaticCalls);
        Assert.True(core.LastStaticPathSubstitutionRequested);
        Assert.Same(user, core.LastStaticUser);
    }

    // ---- the detection seam stays where the versions need it -------------------------

    [Fact]
    public void TheScannerStillSeesTheMvcFileThatThePickerNoLongerShows()
    {
        // The invariant the whole design rests on. The filter sits on the manager, and Anaglyfin's own
        // detection asks the item for its sources below it. Were it otherwise - were the raw file
        // hidden, unlinked, ignored or deleted instead of merely left out of the answer - the offer
        // would disappear with it, and the setting would have taken the converted versions away along
        // with the original. This is the test that says the filter cannot do that by construction.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var detector = new MvcSourceDetector();

        var decorator = CreateDecorator(core, detector, new ProfileCatalog(), offerOriginal: false);

        var offered = decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);
        Assert.DoesNotContain(core.MvcSource, offered, ReferenceSame);

        var scan = MvcEligibleSourceScanner.Scan(movie, detector, NullLogger.Instance);

        var candidate = Assert.Single(scan.Candidates);
        Assert.Equal(ProfileVersionFixtures.MvcPath, candidate.SourcePath);
        Assert.Equal(ProfileVersionFixtures.MvcVersionItemId, candidate.MetadataSourceItemId);
        Assert.Contains(candidate.IdentityKey, scan.Candidates.Select(source => source.IdentityKey));

        // Including in the ids the provider suppresses materialised versions by: as far as Anaglyfin is
        // concerned the raw file is still one of this item's own versions, which is exactly why hiding
        // it from a client is safe.
        Assert.Contains(core.MvcSource.Id, scan.ReportedSourceIds);
    }

    // ---- forwarding ------------------------------------------------------------------

    [Fact]
    public void EveryOtherMemberIsAnsweredByTheCoreManager()
    {
        // Nineteen members forwarded, each to the object the server registered, each handing back what
        // that object answered. Streams, attachments, live streams and recordings are the server's to
        // answer; a version filter that quietly changed how any of them is read would be a much larger
        // feature than the one asked for. AddParts is on the list rather than exempt from it: the
        // server hands the media source providers - Anaglyfin's among them - to whatever it resolves
        // for this service, so the offer itself depends on that call reaching the core.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        var providers = new IMediaSourceProvider[] { new FakeMediaSourceProvider() };
        decorator.AddParts(providers);
        Assert.Same(providers, Assert.Single(core.Parts));

        Assert.Same(core.MediaStreamsForItem, decorator.GetMediaStreams(Guid.NewGuid()));
        Assert.Same(core.MediaStreamsForQuery, decorator.GetMediaStreams(new MediaStreamQuery()));
        Assert.Same(core.AttachmentsForItem, decorator.GetMediaAttachments(Guid.NewGuid()));
        Assert.Same(core.AttachmentsForQuery, decorator.GetMediaAttachments(new MediaAttachmentQuery()));
        Assert.Same(core.ResolvedSource, Run(decorator.GetMediaSource(movie, "id", "live", false, CancellationToken.None)));
        Assert.Same(core.LiveStreamAnswer, Run(decorator.OpenLiveStream(null!, CancellationToken.None)));
        Assert.Same(core.OpenLiveStreamInternalAnswer, Run(decorator.OpenLiveStreamInternal(null!, CancellationToken.None)));
        Assert.Same(core.LiveStream, Run(decorator.GetLiveStream("id", CancellationToken.None)));
        Assert.Same(core.LiveStreamWithProvider, Run(decorator.GetLiveStreamWithDirectStreamProvider("id", CancellationToken.None)));
        Assert.Same(core.LiveStreamInfo, decorator.GetLiveStreamInfo("id"));
        Assert.Same(core.LiveStreamInfoByUniqueId, decorator.GetLiveStreamInfoByUniqueId("unique"));
        Assert.Same(core.RecordingSources, Run(decorator.GetRecordingStreamMediaSources(null!, CancellationToken.None)));
        Run(decorator.CloseLiveStream("id"));
        Assert.Same(core.LiveStreamMediaInfo, Run(decorator.GetLiveStreamMediaInfo("id", CancellationToken.None)));

        Assert.True(decorator.SupportsDirectStream(ProfileVersionFixtures.MvcPath, MediaProtocol.File));
        Assert.Equal(MediaProtocol.Rtsp, decorator.GetPathProtocol(ProfileVersionFixtures.MvcPath));

        decorator.SetDefaultAudioAndSubtitleStreamIndices(movie, core.OwnSource, null);
        Run(decorator.AddMediaInfoWithProbe(core.OwnSource, isAudio: false, cacheKey: null, addProbeDelay: false, isLiveStream: false, CancellationToken.None));

        // Every one of them reached the core, and nothing that was not asked of it did.
        Assert.Equal(
            ForwardedMembers.OrderBy(name => name, StringComparer.Ordinal),
            core.Calls.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryMemberOfTheInterfaceIsEitherForwardedOrFiltered()
    {
        // The forwarding list above is hand written, which makes it the one thing in this feature that
        // can go stale against a newer server. This says so out loud: a member that appears on
        // IMediaSourceManager and lands in neither list is a member that would silently stop being
        // answered, and the failure names it.
        var members = typeof(IMediaSourceManager)
            .GetMethods()
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var accounted = FilteredMembers.Concat(ForwardedMembers).Distinct(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            members.OrderBy(name => name, StringComparer.Ordinal),
            accounted.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheFilteredListAnswersAreBuiltOnWhatTheCoreAnswered()
    {
        // Neither filtered read answers for itself: the core is asked once per request, and the filter
        // only ever takes entries out of what came back. The server's per-user visibility pass, its
        // resume reordering and its merge of the providers have to have happened first for "hide this
        // version" to mean anything at all.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var core = new FakeMediaSourceManager(movie, offerConvertedVersions: true);
        var decorator = CreateDecorator(core, new MvcSourceDetector(), new ProfileCatalog(), offerOriginal: false);

        decorator.GetStaticMediaSources(movie, enablePathSubstitution: false);
        await decorator.GetPlaybackMediaSources(movie, null, allowMediaProbe: false, enablePathSubstitution: false, CancellationToken.None);

        Assert.Equal(1, core.StaticCalls);
        Assert.Equal(1, core.PlaybackCalls);
    }

    [Fact]
    public void TheDecoratorDisposesOnlyTheManagerItBuilt()
    {
        // Registering the decorator after the server's registration means the container stops building
        // the core manager - and so stops disposing it, which is the half the swap has to take over:
        // the server's manager is what closes open live streams on shutdown. A manager handed over as
        // an object somebody else already had belongs to that somebody else and is left alone.
        var movie = ProfileVersionFixtures.CreateStackedMvcMovie();
        var built = new FakeMediaSourceManager(movie);
        var handedOver = new FakeMediaSourceManager(movie);

        new MediaSourceManagerSuppressionDecorator(
            built,
            ownsCore: true,
            new MvcSourceDetector(),
            new ProfileCatalog(),
            new StubConfigurationSource(),
            NullLogger<MediaSourceManagerSuppressionDecorator>.Instance).Dispose();

        new MediaSourceManagerSuppressionDecorator(
            handedOver,
            ownsCore: false,
            new MvcSourceDetector(),
            new ProfileCatalog(),
            new StubConfigurationSource(),
            NullLogger<MediaSourceManagerSuppressionDecorator>.Instance).Dispose();

        Assert.True(built.Disposed);
        Assert.False(handedOver.Disposed);
    }

    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        var core = new FakeMediaSourceManager(ProfileVersionFixtures.CreateStackedMvcMovie());
        var detector = new MvcSourceDetector();
        var catalog = new ProfileCatalog();
        var settings = new StubConfigurationSource();
        var logger = NullLogger<MediaSourceManagerSuppressionDecorator>.Instance;

        Assert.Throws<ArgumentNullException>(() => new MediaSourceManagerSuppressionDecorator(null!, true, detector, catalog, settings, logger));
        Assert.Throws<ArgumentNullException>(() => new MediaSourceManagerSuppressionDecorator(core, true, null!, catalog, settings, logger));
        Assert.Throws<ArgumentNullException>(() => new MediaSourceManagerSuppressionDecorator(core, true, detector, null!, settings, logger));
        Assert.Throws<ArgumentNullException>(() => new MediaSourceManagerSuppressionDecorator(core, true, detector, catalog, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new MediaSourceManagerSuppressionDecorator(core, true, detector, catalog, settings, null!));
    }

    // ---- helpers ---------------------------------------------------------------------

    private static MediaSourceManagerSuppressionDecorator CreateDecorator(
        FakeMediaSourceManager core,
        IMvcSourceDetector detector,
        IProfileCatalog catalog,
        bool offerOriginal)
        => new(
            core,
            ownsCore: true,
            detector,
            catalog,
            new StubConfigurationSource { Configuration = new PluginConfiguration { OfferOriginalMVCVersion = offerOriginal } },
            NullLogger<MediaSourceManagerSuppressionDecorator>.Instance);

    /// <summary>
    /// Compares two source references rather than two source reports, because the question every one of
    /// these assertions asks is whether the entry the server built is still the entry the client is
    /// being handed - not whether an equal-looking copy of it is.
    /// </summary>
    private static readonly ReferenceSourceComparer ReferenceSame = new();

    /// <summary>
    /// Compares two sequences by requiring every survivor to be the exact object the core answered with.
    /// </summary>
    private static void AssertReferenceSequenceEqual(
        IReadOnlyList<MediaSourceInfo> expected,
        IReadOnlyList<MediaSourceInfo> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Same(expected[index], actual[index]);
        }
    }

    /// <summary>
    /// Takes one of the core's already-finished tasks to its result. The fakes answer synchronously
    /// because a unit test has no live stream to open and nothing to wait for on a thread.
    /// </summary>
    private static T Run<T>(Task<T> task)
        => task.GetAwaiter().GetResult();

    private static void Run(Task task)
        => task.GetAwaiter().GetResult();

    private sealed class ReferenceSourceComparer : IEqualityComparer<MediaSourceInfo?>
    {
        public bool Equals(MediaSourceInfo? left, MediaSourceInfo? right)
            => ReferenceEquals(left, right);

        public int GetHashCode(MediaSourceInfo? source)
            => source is null ? 0 : RuntimeHelpers.GetHashCode(source);
    }

    /// <summary>
    /// A settings source that fails, which the seam promises not to do and the filter has to assume can
    /// happen anyway.
    /// </summary>
    private sealed class ThrowingConfigurationSource : IAnaglyfinConfigurationSource
    {
        public PluginConfiguration GetConfiguration()
            => throw new InvalidOperationException("settings source failure under test");
    }

    /// <summary>
    /// A detector that counts how often it was consulted and can be made to throw, so that "nothing was
    /// examined" and "an answer never arrived" are both observable rather than inferred.
    /// </summary>
    private sealed class CountingDetector : IMvcSourceDetector
    {
        public bool Throws { get; init; }

        public int AskedCount { get; private set; }

        public MvcSourceEligibility Detect(MvcSourceCandidate candidate)
        {
            AskedCount++;

            if (Throws)
            {
                throw new InvalidOperationException("detector failure under test");
            }

            return candidate.Path is not null && candidate.Path.Contains("3D mvc", StringComparison.OrdinalIgnoreCase)
                ? MvcSourceEligibility.Eligible(MvcEligibilityReason.ItemMetadataDeclaresMvc, MvcDetectionConfidence.High)
                : MvcSourceEligibility.NotEligible(MvcEligibilityReason.NoMvcSignal);
        }
    }

    /// <summary>
    /// A media source provider with nothing to offer, for the sole purpose of watching
    /// <see cref="IMediaSourceManager.AddParts"/> reach the core manager.
    /// </summary>
    private sealed class FakeMediaSourceProvider : IMediaSourceProvider
    {
        public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());

        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
            => Task.FromException<ILiveStream>(new NotSupportedException());
    }

    /// <summary>
    /// The media source manager the server registered: a scripted version list, one canned answer per
    /// forwarded member, and a record of everything it was asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It files the stacked library's versions the way the server reports them - the item's own file
    /// first, its other versions beside it, and Anaglyfin's conversions of the MVC file as sources of
    /// their own, which is what a materialised version item looks like in a static list. The converted
    /// sources are built by <see cref="ProfileVersionSource"/>, the one builder the provider and the
    /// version-item manager share, so the entries that have to survive are the shape the feature
    /// really ships rather than a guess at it.
    /// </para>
    /// <para>
    /// The playback answer is a separate list object over the same entries, because the server builds
    /// that one itself (static sources plus the providers, then a sort), and an assertion that the
    /// filter returned "the list the core answered" is only worth making when the two lists are not one
    /// object.
    /// </para>
    /// </remarks>
    private sealed class FakeMediaSourceManager : IMediaSourceManager, IDisposable
    {
        private readonly ScriptedVideo _item;

        private List<MediaSourceInfo> _sources = new();

        public FakeMediaSourceManager(ScriptedVideo item, bool offerConvertedVersions = false)
        {
            _item = item;

            OwnSource = item.StaticSources.Count > 0 ? item.StaticSources[0] : new MediaSourceInfo();
            MvcSource = item.StaticSources.FirstOrDefault(source => source.Path == ProfileVersionFixtures.MvcPath)
                ?? new MediaSourceInfo();

            // A third file of the same movie, filed beside the other two and named for nothing but its
            // resolution: the version that has to survive for the reasons any ordinary version does.
            OtherAlternateSource = ProfileVersionFixtures.CreateSource(
                Guid.NewGuid(),
                "720p",
                ProfileVersionFixtures.FolderPath + "/Ready Player One (2018) - 720p.mkv");

            var sources = new List<MediaSourceInfo>(item.StaticSources) { OtherAlternateSource };
            sources.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));

            if (offerConvertedVersions)
            {
                VersionSources = new ProfileCatalog()
                    .GetOfferedProfiles(new PluginConfiguration())
                    .Select(profile => ProfileVersionSource.Build(
                        item,
                        ProfileVersionFixtures.CreateEligibleSource(item, MvcSource),
                        profile,
                        labelSource: false))
                    .ToArray();

                sources.AddRange(VersionSources);
            }

            _sources = sources;

            // What the item reports for itself and what the manager answers with are the same files -
            // the manager asks the item, and this is the one library where both halves have to agree
            // for the scanner's answer to mean anything. The same mutable list is handed to both, so a
            // refusal case that appends an extra source after construction is visible on both sides.
            item.StaticSources = sources;
        }

        /// <summary>Gets the list the manager answers a static-source request with.</summary>
        public IReadOnlyList<MediaSourceInfo> StaticSources => _sources;

        /// <summary>Gets the list the manager answers a playback-source request with.</summary>
        public IReadOnlyList<MediaSourceInfo> PlaybackSources => _playback ??= new List<MediaSourceInfo>(_sources);

        /// <summary>Gets the item's own file, as its source report.</summary>
        public MediaSourceInfo OwnSource { get; }

        /// <summary>Gets the MVC file the scanner filed beside the item.</summary>
        public MediaSourceInfo MvcSource { get; }

        /// <summary>Gets another alternate version of the same movie, with no 3D anywhere on it.</summary>
        public MediaSourceInfo OtherAlternateSource { get; }

        /// <summary>Gets Anaglyfin's converted versions of the MVC file.</summary>
        public IReadOnlyList<MediaSourceInfo> VersionSources { get; } = Array.Empty<MediaSourceInfo>();

        /// <summary>Gets or sets whether the manager answers by throwing instead of by listing.</summary>
        public bool Throws { get; init; }

        /// <summary>Gets how many static-source requests were answered.</summary>
        public int StaticCalls { get; private set; }

        /// <summary>Gets how many playback-source requests were answered.</summary>
        public int PlaybackCalls { get; private set; }

        /// <summary>Gets whether path substitution was asked for on the last static request.</summary>
        public bool LastStaticPathSubstitutionRequested { get; private set; }

        /// <summary>Gets the user the last static request carried.</summary>
        public User? LastStaticUser { get; private set; }

        /// <summary>Gets every member the manager was asked to answer.</summary>
        public List<string> Calls { get; } = new();

        /// <summary>Gets what <see cref="AddParts"/> was handed.</summary>
        public List<IEnumerable<IMediaSourceProvider>> Parts { get; } = new();

        /// <summary>Gets whether the manager was disposed.</summary>
        public bool Disposed { get; private set; }

        // ---- canned answers, one per forwarded member --------------------------------

        public IReadOnlyList<MediaStream> MediaStreamsForItem { get; } = new[] { new MediaStream { Type = MediaStreamType.Audio, Index = 1 } };

        public IReadOnlyList<MediaStream> MediaStreamsForQuery { get; } = new[] { new MediaStream { Type = MediaStreamType.Subtitle, Index = 2 } };

        public IReadOnlyList<MediaAttachment> AttachmentsForItem { get; } = new[] { new MediaAttachment { Index = 0 } };

        public IReadOnlyList<MediaAttachment> AttachmentsForQuery { get; } = new[] { new MediaAttachment { Index = 1 } };

        public MediaSourceInfo ResolvedSource { get; } = new() { Id = "resolved" };

        public MediaSourceInfo LiveStream { get; } = new() { Id = "live-stream" };

        public MediaSourceInfo LiveStreamMediaInfo { get; } = new() { Id = "live-media-info" };

        public LiveStreamResponse LiveStreamAnswer { get; }
            = new(new MediaSourceInfo { Id = "live-stream-answer" });

        public Tuple<LiveStreamResponse, IDirectStreamProvider> OpenLiveStreamInternalAnswer { get; }
            = new(
                new LiveStreamResponse(new MediaSourceInfo { Id = "open-live-stream-internal" }),
                new FakeDirectStreamProvider());

        public Tuple<MediaSourceInfo, IDirectStreamProvider> LiveStreamWithProvider { get; }
            = new(new MediaSourceInfo { Id = "live-with-provider" }, new FakeDirectStreamProvider());

        public ILiveStream LiveStreamInfo { get; } = new FakeLiveStream("info");

        public ILiveStream LiveStreamInfoByUniqueId { get; } = new FakeLiveStream("unique");

        public IReadOnlyList<MediaSourceInfo> RecordingSources { get; } = new[] { new MediaSourceInfo { Id = "recording-sources" } };

        /// <summary>
        /// Replaces the scripted answers with a given list, for the cases the built library cannot
        /// describe: an item that reports nothing, or one whose files changed under the server.
        /// </summary>
        public void Script(IReadOnlyList<MediaSourceInfo> staticSources)
        {
            _sources = new List<MediaSourceInfo>(staticSources);
            _playback = new List<MediaSourceInfo>(staticSources);
            _item.StaticSources = staticSources;
        }

        /// <summary>
        /// Appends entries to the scripted library after construction, for the cases that need sources
        /// the standard fixtures cannot describe.
        /// </summary>
        public void AddExtraSources(params MediaSourceInfo[] extraSources)
        {
            _sources.AddRange(extraSources);

            if (_playback is List<MediaSourceInfo> playback)
            {
                playback.AddRange(extraSources);
            }
        }

        // ---- the two filtered reads ---------------------------------------------------

        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
        {
            Record(nameof(GetStaticMediaSources));
            StaticCalls++;
            ThrowIfConfigured();

            LastStaticPathSubstitutionRequested = enablePathSubstitution;
            LastStaticUser = user;

            return StaticSources;
        }

        public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
            BaseItem item,
            User? user,
            bool allowMediaProbe,
            bool enablePathSubstitution,
            CancellationToken cancellationToken)
        {
            Record(nameof(GetPlaybackMediaSources));
            PlaybackCalls++;
            ThrowIfConfigured();

            return Task.FromResult(PlaybackSources);
        }

        // ---- everything else ------------------------------------------------------------

        public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        {
            Record(nameof(AddParts));
            Parts.Add(providers);
        }

        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        {
            Record(nameof(GetMediaStreams));
            return MediaStreamsForItem;
        }

        public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
        {
            Record(nameof(GetMediaStreams));
            return MediaStreamsForQuery;
        }

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
        {
            Record(nameof(GetMediaAttachments));
            return AttachmentsForItem;
        }

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
        {
            Record(nameof(GetMediaAttachments));
            return AttachmentsForQuery;
        }

        public Task<MediaSourceInfo> GetMediaSource(
            BaseItem item,
            string mediaSourceId,
            string liveStreamId,
            bool enablePathSubstitution,
            CancellationToken cancellationToken)
        {
            Record(nameof(GetMediaSource));
            return Task.FromResult(ResolvedSource);
        }

        public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        {
            Record(nameof(OpenLiveStream));
            return Task.FromResult(LiveStreamAnswer);
        }

        public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
            LiveStreamRequest request,
            CancellationToken cancellationToken)
        {
            Record(nameof(OpenLiveStreamInternal));
            return Task.FromResult(OpenLiveStreamInternalAnswer);
        }

        public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        {
            Record(nameof(GetLiveStream));
            return Task.FromResult(LiveStream);
        }

        public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(
            string id,
            CancellationToken cancellationToken)
        {
            Record(nameof(GetLiveStreamWithDirectStreamProvider));
            return Task.FromResult(LiveStreamWithProvider);
        }

        public ILiveStream GetLiveStreamInfo(string id)
        {
            Record(nameof(GetLiveStreamInfo));
            return LiveStreamInfo;
        }

        public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId)
        {
            Record(nameof(GetLiveStreamInfoByUniqueId));
            return LiveStreamInfoByUniqueId;
        }

        public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
            ActiveRecordingInfo info,
            CancellationToken cancellationToken)
        {
            Record(nameof(GetRecordingStreamMediaSources));
            return Task.FromResult(RecordingSources);
        }

        public Task CloseLiveStream(string id)
        {
            Record(nameof(CloseLiveStream));
            return Task.CompletedTask;
        }

        public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        {
            Record(nameof(GetLiveStreamMediaInfo));
            return Task.FromResult(LiveStreamMediaInfo);
        }

        public bool SupportsDirectStream(string path, MediaProtocol protocol)
        {
            Record(nameof(SupportsDirectStream));
            return true;
        }

        public MediaProtocol GetPathProtocol(string path)
        {
            Record(nameof(GetPathProtocol));
            return MediaProtocol.Rtsp;
        }

        public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User? user)
            => Record(nameof(SetDefaultAudioAndSubtitleStreamIndices));

        public Task AddMediaInfoWithProbe(
            MediaSourceInfo mediaSource,
            bool isAudio,
            string? cacheKey,
            bool addProbeDelay,
            bool isLiveStream,
            CancellationToken cancellationToken)
        {
            Record(nameof(AddMediaInfoWithProbe));
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;

        private IReadOnlyList<MediaSourceInfo>? _playback;

        private void Record(string member) => Calls.Add(member);

        private void ThrowIfConfigured()
        {
            if (Throws)
            {
                throw new InvalidOperationException("media source manager failure under test");
            }
        }

        private sealed class FakeDirectStreamProvider : IDirectStreamProvider
        {
            public System.IO.Stream GetStream() => System.IO.Stream.Null;
        }

        private sealed class FakeLiveStream : ILiveStream
        {
            public FakeLiveStream(string id)
            {
                UniqueId = id;
                MediaSource = new MediaSourceInfo { Id = id };
            }

            public int ConsumerCount { get; set; }

            public string OriginalStreamId { get; set; } = string.Empty;

            public string TunerHostId => "anaglyfin-test";

            public bool EnableStreamSharing => false;

            public MediaSourceInfo MediaSource { get; set; }

            public string UniqueId { get; }

            public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

            public Task Close() => Task.CompletedTask;

            public System.IO.Stream GetStream() => System.IO.Stream.Null;

            public void Dispose()
            {
            }
        }
    }
}
