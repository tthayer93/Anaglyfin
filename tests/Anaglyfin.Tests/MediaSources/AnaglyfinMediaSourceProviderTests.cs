using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.Configuration;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
using Anaglyfin.MediaSources;
using Anaglyfin.Profiles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anaglyfin.Tests.MediaSources;

/// <summary>
/// Covers the alternate media source provider: what it offers, what it refuses, and
/// the exact shape of every <see cref="MediaSourceInfo"/> it puts in front of a client.
/// </summary>
public class AnaglyfinMediaSourceProviderTests
{
    private const string MvcMoviePath = "/movies/Avatar 3D MVC.mkv";

    private const string MvcMovieName = "Avatar 3D MVC";

    private const long RunTimeTicks = 7_200_000_000L;

    private const long FileSize = 8_000_000_000L;

    private static readonly Guid ItemId = Guid.Parse("1c4c3d0a-9b0c-4a1b-8c7d-2e5f6a7b8c9d");

    private static readonly string ItemKey = ItemId.ToString("N", CultureInfo.InvariantCulture);

    // --- identity and interface -------------------------------------------------

    [Fact]
    public void ProviderIsAnMediaSourceProviderForTheServerScan()
    {
        Assert.IsAssignableFrom<IMediaSourceProvider>(CreateProvider());
    }

    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        var detector = new ScriptedDetector();
        var catalog = new ProfileCatalog();
        var configuration = new StubConfigurationSource();
        var logger = NullLogger<AnaglyfinMediaSourceProvider>.Instance;

        Assert.Throws<ArgumentNullException>(() => new AnaglyfinMediaSourceProvider(null!, catalog, configuration, logger));
        Assert.Throws<ArgumentNullException>(() => new AnaglyfinMediaSourceProvider(detector, null!, configuration, logger));
        Assert.Throws<ArgumentNullException>(() => new AnaglyfinMediaSourceProvider(detector, catalog, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new AnaglyfinMediaSourceProvider(detector, catalog, configuration, null!));
    }

    [Fact]
    public void SourceIdIsAGuidDerivedFromTheItemAndTheProfile()
    {
        var id = AnaglyfinMediaSourceProvider.BuildMediaSourceId(ItemId, MvcMoviePath, ProfileIds.CustomGrayscale);

        // The documented derivation, restated here independently of the provider: the
        // server's own key-to-guid fold over "<item id, N>:<profile id>", as a lower-case
        // "N" GUID. The pin matters - clients store this string, so a provider that
        // quietly changes it silently orphans every saved version choice.
        Assert.Equal(DeriveGuid(ItemKey + ":" + ProfileIds.CustomGrayscale), id);
        Assert.Equal(id, id.ToLowerInvariant());
    }

    [Fact]
    public void SourceIdIsAGuidBecauseDynamicHlsParsesItAsOne()
    {
        // The regression this exists for: Jellyfin 12 hands the request's MediaSourceId to
        // Guid.Parse on the master playlist (trickplay, on by default) and on the main
        // playlist (unconditionally). A descriptive id - "anaglyfin:<item>:<profile>" -
        // survived PlaybackInfo and then threw a FormatException before FFmpeg started,
        // which is a version list that plays nothing.
        var id = AnaglyfinMediaSourceProvider.BuildMediaSourceId(ItemId, MvcMoviePath, ProfileIds.SideBySideFull);

        AssertIsLowerCaseGuid(id);
        Assert.DoesNotContain(":", id, StringComparison.Ordinal);
        Assert.DoesNotContain("anaglyfin", id, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceIdsSeparateEveryProfileOfAnItemAndEveryItemOfAProfile()
    {
        var ids = ProfileIds.AllProfileIds
            .Select(profileId => AnaglyfinMediaSourceProvider.BuildMediaSourceId(ItemId, MvcMoviePath, profileId))
            .ToList();

        // One item, every shipped profile: no two versions may share an id, or the second
        // one is unreachable once the server resolves the source by id.
        Assert.Equal(ProfileIds.AllProfileIds.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => AssertIsLowerCaseGuid(id));

        // And never the item's own id: the item's static source is keyed by it and sorted
        // first, so an Anaglyfin version wearing it would shadow the original source.
        Assert.DoesNotContain(ItemKey, ids, StringComparer.Ordinal);
        Assert.DoesNotContain(ItemId.ToString("D", CultureInfo.InvariantCulture), ids, StringComparer.Ordinal);

        // The same profile on another item is another id, so a version choice saved on one
        // title cannot be applied to a different one.
        var otherItem = Guid.Parse("9f8e7d6c-5b4a-4938-a7b6-c5d4e3f2a1b0");
        Assert.NotEqual(
            AnaglyfinMediaSourceProvider.BuildMediaSourceId(ItemId, MvcMoviePath, ProfileIds.AnaglyphRedCyanDubois),
            AnaglyfinMediaSourceProvider.BuildMediaSourceId(otherItem, MvcMoviePath, ProfileIds.AnaglyphRedCyanDubois));
    }

    [Fact]
    public void SourceIdFallsBackToAPathDigestWhenTheItemHasNoId()
    {
        var withPath = AnaglyfinMediaSourceProvider.BuildMediaSourceId(Guid.Empty, MvcMoviePath, ProfileIds.TwoDBase);
        var otherPath = AnaglyfinMediaSourceProvider.BuildMediaSourceId(Guid.Empty, "/movies/Other.3D.MVC.mkv", ProfileIds.TwoDBase);

        // A different identity under the id, not a different shape of it: the fallback is
        // still a GUID, because the routes that parse it do not know or care which
        // identity the provider had to fall back to.
        AssertIsLowerCaseGuid(withPath);
        AssertIsLowerCaseGuid(otherPath);
        Assert.NotEqual(withPath, otherPath);
    }

    [Fact]
    public void SourceIdNeedsSomethingToIdentify()
    {
        // ThrowIfNullOrWhiteSpace reports null as ArgumentNullException and blank as
        // ArgumentException; both are argument failures on the same contract.
        Assert.ThrowsAny<ArgumentException>(() => AnaglyfinMediaSourceProvider.BuildMediaSourceId(Guid.Empty, null!, ProfileIds.TwoDBase));
        Assert.ThrowsAny<ArgumentException>(() => AnaglyfinMediaSourceProvider.BuildMediaSourceId(ItemId, MvcMoviePath, " "));
    }

    // --- eligibility gate ---------------------------------------------------------

    [Fact]
    public async Task GetMediaSourcesIgnoresAMissingItem()
    {
        var detector = new ScriptedDetector();
        var configuration = new StubConfigurationSource();
        var provider = CreateProvider(detector: detector, configuration: configuration);

        Assert.Empty(await provider.GetMediaSources(null!, CancellationToken.None));

        Assert.Equal(0, detector.CallCount);
        Assert.Equal(0, configuration.CallCount);
    }

    [Fact]
    public async Task GetMediaSourcesIgnoresNonVideoItemsWithoutDetectingOrLoadingSettings()
    {
        var detector = new ScriptedDetector();
        var configuration = new StubConfigurationSource();
        var provider = CreateProvider(detector: detector, configuration: configuration);

        var song = new Audio
        {
            Name = "Song 3D MVC",
            Path = MvcMoviePath
        };

        Assert.Empty(await provider.GetMediaSources(song, CancellationToken.None));
        Assert.Equal(0, detector.CallCount);
        Assert.Equal(0, configuration.CallCount);
    }

    [Fact]
    public async Task GetMediaSourcesOffersNothingForIneligibleItemsAndDoesNotReadSettings()
    {
        var detector = new ScriptedDetector { Eligible = false };
        var configuration = new StubConfigurationSource();
        var provider = CreateProvider(detector: detector, configuration: configuration);

        Assert.Empty(await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None));

        Assert.Equal(1, detector.CallCount);
        Assert.Equal(0, configuration.CallCount);
    }

    [Theory]
    [InlineData("movies/Avatar 3D MVC.mkv")] // relative: would resolve against the transcode working directory
    [InlineData("/movies/Avatar 3D MVC.strm")] // a strm is a text pointer, not a decodable input
    public async Task GetMediaSourcesRefusesBeforeDetectingWhenThePathCannotBeARealInput(string path)
    {
        var detector = new ScriptedDetector();
        var provider = CreateProvider(detector: detector);
        var item = CreateMvcItem(path: path);

        var sources = await provider.GetMediaSources(item, CancellationToken.None);

        Assert.Empty(sources);
        Assert.Equal(0, detector.CallCount);
    }

    [Theory]
    [InlineData(VideoType.Iso)]
    [InlineData(VideoType.Dvd)]
    [InlineData(VideoType.BluRay)]
    public async Task GetMediaSourcesRefusesDiscImagesRegardlessOfTheirNames(VideoType videoType)
    {
        // Disc layouts reach FFmpeg through mounts or bluray-specific arguments the
        // marker contract deliberately does not cover.
        var detector = new ScriptedDetector();
        var provider = CreateProvider(detector: detector);
        var item = CreateMvcItem(videoType: videoType);

        Assert.Empty(await provider.GetMediaSources(item, CancellationToken.None));
        Assert.Equal(0, detector.CallCount);
    }

    [Fact]
    public async Task GetMediaSourcesNeverLetsADetectorFailureEscape()
    {
        var detector = new ScriptedDetector { Throws = true };
        var provider = CreateProvider(detector: detector);

        var sources = await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None);

        Assert.Empty(sources);
    }

    // --- the offer itself ---------------------------------------------------------

    [Fact]
    public async Task GetMediaSourcesOffersTheEnabledProfilesWithTheDefaultFirst()
    {
        var detectorScript = new ScriptedDetector();
        var provider = CreateProvider(detector: detectorScript);

        var sources = (await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None)).ToList();

        // Shipped settings: red/cyan Dubois default, 2D base fallback, MVP profile set.
        // The ids say nothing about which profile they are (they are GUIDs), so the order
        // is asserted against the id each of those profiles is bound to.
        Assert.Equal(
            new[]
            {
                IdOf(ProfileIds.AnaglyphRedCyanDubois),
                IdOf(ProfileIds.SideBySideFull),
                IdOf(ProfileIds.SideBySideHalf),
                IdOf(ProfileIds.TwoDBase)
            },
            sources.Select(source => source.Id));
        Assert.Equal(
            new[] { "3D Anaglyph Red/Cyan (Dubois)", "3D Full Side-by-Side", "3D Half Side-by-Side", "2D Base" },
            sources.Select(source => source.Name));

        // The fallback profile is the safe last resort, so it sorts last.
        Assert.Equal(IdOf(ProfileIds.TwoDBase), sources[^1].Id);

        // The detector saw the item's own signals (path, name), not a fabricated copy.
        Assert.Equal(MvcMoviePath, detectorScript!.LastCandidate!.Path);
        Assert.Equal(MvcMovieName, detectorScript.LastCandidate.Name);
    }

    [Fact]
    public async Task GetMediaSourcesPromotesTheConfiguredDefaultProfileToTheFront()
    {
        var configuration = new StubConfigurationSource
        {
            Configuration = new PluginConfiguration { DefaultProfileId = ProfileIds.SideBySideFull }
        };
        var provider = CreateProvider(configuration: configuration);

        var sources = (await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None)).ToList();

        Assert.Equal("3D Full Side-by-Side", sources[0].Name);
        Assert.Equal(IdOf(ProfileIds.SideBySideFull), sources[0].Id);
        Assert.Equal(IdOf(ProfileIds.AnaglyphRedCyanDubois), sources[1].Id);
    }

    [Fact]
    public async Task GetMediaSourcesOffersOnlyTheEnabledProfiles()
    {
        var configuration = new StubConfigurationSource
        {
            Configuration = new PluginConfiguration
            {
                EnabledProfileIds = new List<string> { ProfileIds.CustomGrayscale, ProfileIds.SideBySideFull }
            }
        };
        var provider = CreateProvider(configuration: configuration);

        var sources = (await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None)).ToList();

        // The custom grayscale preset is only offered when the administrator enables it.
        Assert.Equal(2, sources.Count);
        Assert.Equal(IdOf(ProfileIds.SideBySideFull), sources[0].Id);
        Assert.Equal(IdOf(ProfileIds.CustomGrayscale), sources[1].Id);
        Assert.Equal("3D Anaglyph Custom Colours", sources[1].Name);
    }

    [Fact]
    public async Task GetMediaSourcesOffersNothingWhenTheCatalogOffersNoProfiles()
    {
        var provider = CreateProvider(catalog: new EmptyProfileCatalog());

        Assert.Empty(await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None));
    }

    [Fact]
    public async Task GetMediaSourcesReadsTheSettingsExactlyOncePerRequest()
    {
        var configuration = new StubConfigurationSource();
        var provider = CreateProvider(configuration: configuration);

        await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None);

        Assert.Equal(1, configuration.CallCount);
    }

    // --- shape of every offered source --------------------------------------------

    [Fact]
    public async Task TheRedCyanVersionCarriesExactlyTheContractedFields()
    {
        // One enabled profile so the shape assertion holds on a single source; the
        // settings only gate which profiles appear, never what a source looks like.
        var configuration = new StubConfigurationSource
        {
            Configuration = new PluginConfiguration
            {
                EnabledProfileIds = new List<string> { ProfileIds.AnaglyphRedCyanDubois }
            }
        };
        var provider = CreateProvider(configuration: configuration);
        var item = CreateMvcItem();

        var source = Assert.Single(await provider.GetMediaSources(item, CancellationToken.None));

        // The id is the server's own fold over "1c4c...:anaglyph_arcd", written out in
        // full: an Anaglyfin version must be addressable by a GUID the HLS routes accept.
        Assert.Equal(DeriveGuid(ItemKey + ":" + ProfileIds.AnaglyphRedCyanDubois), source.Id);
        Assert.Equal("3D Anaglyph Red/Cyan (Dubois)", source.Name);

        // The marker names the video stream as well as the file: the server maps the stream it
        // chose by its index inside the file, and the wrapper can only take that map out of the
        // way of the profile's own if the provider told it which stream that is. This file's
        // video is stream 0, so the marker says 0.
        Assert.Equal(
            "http://127.0.0.1/anaglyfin/profile/anaglyph_arcd?source=%2Fmovies%2FAvatar%203D%20MVC.mkv&video=0",
            source.Path);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.False(source.SupportsDirectPlay);

        // The provider's own intent, not the server's decision: the server overwrites both
        // flags with the user's permissions during PlaybackInfo, and the transcode path that
        // decides video copy never reads them. What keeps this version encoding is the codec
        // it reports for its video stream - see
        // TheVersionsReportTheirVideoAsACodecNoTranscodeProfileCanCopy.
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.False(source.SupportsProbing);
        Assert.False(source.IsRemote);
        Assert.False(source.RequiresOpening);
        Assert.False(source.RequiresClosing);
        Assert.False(source.IsInfiniteStream);
        Assert.Null(source.OpenToken);
        Assert.Null(source.LiveStreamId);
        Assert.Equal(MediaSourceType.Default, source.Type);
    }

    [Fact]
    public async Task VersionsCarryTheDurationContainerAndStreamsOfTheOriginalSource()
    {
        var provider = CreateProvider();
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        item.StaticSources = new[] { original };

        var sources = await provider.GetMediaSources(item, CancellationToken.None);

        foreach (var source in sources)
        {
            Assert.Equal(RunTimeTicks, source.RunTimeTicks);
            Assert.Equal("mkv", source.Container);
            Assert.Equal(FileSize, source.Size);
            Assert.Equal(original.Bitrate, source.Bitrate);
            Assert.Equal(original.Formats, source.Formats);

            // The stream report travels at full length - same count, same order, same
            // languages, same indices - with the one exception the versions exist for: the
            // video stream is the version's own copy of it, because a version must be able
            // to report that video differently without rewriting what the original source
            // tells its own clients. See the tests below for that one stream.
            Assert.Equal(original.MediaStreams.Count, source.MediaStreams.Count);
            for (var index = 0; index < original.MediaStreams.Count; index++)
            {
                var reported = source.MediaStreams[index];
                var expected = original.MediaStreams[index];

                Assert.Equal(expected.Type, reported.Type);
                Assert.Equal(expected.Index, reported.Index);
                Assert.Equal(expected.Language, reported.Language);

                if (expected.Type == MediaStreamType.Video)
                {
                    Assert.NotSame(expected, reported);
                }
                else
                {
                    // Audio and subtitles are the tracks the user already knows, handed over
                    // as the same objects: nothing about a stereo version changes them.
                    Assert.Same(expected, reported);
                }
            }
        }
    }

    [Fact]
    public async Task TheVersionsReportTheirVideoAsACodecNoTranscodeProfileCanCopy()
    {
        // Why this assertion is load-bearing: Jellyfin decides video copy from the codec a
        // source reports for its video stream, not from any flag a plugin can set. Report the
        // file's real "hevc" and every client whose HLS profile copies hevc gets a command
        // that stream-copies the picture, which leaves the profile's view selection and
        // filters in the command with nothing to convert - a version that plays the original
        // file while looking like a converted one. A codec name no client's direct-play list
        // and no client's transcode profile contains cannot be matched by that decision, and
        // so cannot be copied by any client under any permission. The name is the whole of the
        // trick; it is not a report of what the track is encoded with.
        var provider = CreateProvider();
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        item.StaticSources = new[] { original };

        var sources = await provider.GetMediaSources(item, CancellationToken.None);

        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            var video = Assert.Single(source.MediaStreams, stream => stream.Type == MediaStreamType.Video);

            Assert.Equal(ForceTranscodeVideoStreams.VideoCodec, video.Codec);

            // Everything a player or the server's transcode conditions read stays as the
            // file's own probe reported it: the version re-labels the codec, it does not
            // re-describe the picture. Size is the one field a version answers differently,
            // because a version really does encode a different frame - see
            // EveryVersionReportsTheFrameItsOwnProfileEncodes.
            var probed = Assert.Single(original.MediaStreams, stream => stream.Type == MediaStreamType.Video);
            Assert.Equal(probed.BitDepth, video.BitDepth);
            Assert.Equal(probed.RealFrameRate, video.RealFrameRate);
            Assert.Equal(probed.Profile, video.Profile);
            Assert.Equal(probed.Level, video.Level);
            Assert.Equal(probed.Index, video.Index);
        }
    }

    // --- reported geometry ---------------------------------------------------------

    [Fact]
    public async Task EveryVersionReportsTheFrameItsOwnProfileEncodes()
    {
        // Why the numbers below are load-bearing and not decoration: the server sizes the
        // transcode it builds from the video stream this source reports. When the client asks
        // for no resolution and brings at least the stream's bitrate, its MaxWidth/MaxHeight
        // default to exactly these numbers, and the software scale it then writes into -vf is
        // evaluated at run time against the frame that actually reaches the filter. So a version
        // that reports the source's frame while encoding another one gets its own converted
        // picture scaled into the source's box on the way out - the full-SBS version of this
        // 1920x1080 source, encoding 3840x1080 while claiming 1920x1080, came out of a real
        // server at 1920x540.
        var allProfiles = new PluginConfiguration
        {
            EnabledProfileIds = ProfileIds.AllProfileIds.ToList()
        };
        var configuration = new StubConfigurationSource { Configuration = allProfiles };
        var provider = CreateProvider(configuration: configuration);
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        item.StaticSources = new[] { original };

        var sources = (await provider.GetMediaSources(item, CancellationToken.None)).ToList();

        // One entry per version so a wrong answer names which profile it belongs to.
        var reported = sources.ToDictionary(
            source => ProfileMarkerParser.Parse(source.Path).ProfileId!,
            source =>
            {
                var video = Assert.Single(source.MediaStreams, stream => stream.Type == MediaStreamType.Video);
                return (video.Width, video.Height);
            });

        Assert.Equal(
            new Dictionary<string, (int? Width, int? Height)>
            {
                // The native all-view frame: both eyes, side by side, at full height.
                [ProfileIds.SideBySideFull] = (3840, 1080),

                // Halved back to the source's own size on the way to the encoder, so the source's
                // size is the size the encoder is handed.
                [ProfileIds.SideBySideHalf] = (1920, 1080),

                // The base view, and both anaglyph families, are inside the source's frame.
                [ProfileIds.TwoDBase] = (1920, 1080),
                [ProfileIds.AnaglyphRedCyanDubois] = (1920, 1080),
                [ProfileIds.CustomGrayscale] = (1920, 1080)
            },
            reported);

        // Every one of those versions is still the file's own stream in every other respect: a
        // version restates the size of its picture, not the rest of its paperwork. (The codec
        // every version answers differently, and why, is
        // TheVersionsReportTheirVideoAsACodecNoTranscodeProfileCanCopy.)
        var probed = Assert.Single(original.MediaStreams, stream => stream.Type == MediaStreamType.Video);
        foreach (var source in sources)
        {
            var video = Assert.Single(source.MediaStreams, stream => stream.Type == MediaStreamType.Video);
            Assert.Equal(probed.Index, video.Index);
            Assert.Equal(probed.BitDepth, video.BitDepth);
            Assert.Equal(probed.RealFrameRate, video.RealFrameRate);
            Assert.Equal(probed.Profile, video.Profile);
            Assert.Equal(probed.Level, video.Level);

            // The two fields a doubled width makes dishonest if they were left behind, and which
            // the server consults for neither of its scaling decisions: the display ratio is a
            // client badge, and the sample aspect ratio travels with the frame, not with the
            // row in the database.
            Assert.Equal(probed.AspectRatio, video.AspectRatio);
            Assert.Equal(probed.IsAnamorphic, video.IsAnamorphic);
        }
    }

    [Fact]
    public async Task ReportingAConvertedFrameLeavesWhatTheItemReportsAboutItsOwnAlone()
    {
        // The item's original source hands out the very objects its own clients are served, so
        // the doubled width of a full-SBS version has to be a copy's width. Written onto the
        // probed stream it would tell every client, and every later transcode of the original
        // file, that a 1920x1080 film is 3840 wide.
        var configuration = new StubConfigurationSource
        {
            Configuration = new PluginConfiguration { EnabledProfileIds = new List<string> { ProfileIds.SideBySideFull } }
        };
        var provider = CreateProvider(configuration: configuration);
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        item.StaticSources = new[] { original };

        var source = Assert.Single(await provider.GetMediaSources(item, CancellationToken.None));

        var version = Assert.Single(source.MediaStreams, stream => stream.Type == MediaStreamType.Video);
        Assert.Equal(3840, version.Width);
        Assert.Equal(1080, version.Height);

        var probed = Assert.Single(original.MediaStreams, stream => stream.Type == MediaStreamType.Video);
        Assert.Equal(1920, probed.Width);
        Assert.Equal(1080, probed.Height);
        Assert.NotSame(probed, version);

        // And the answer is stable across requests: the size is derived from the profile and the
        // source, never stored, so a second playback-info request cannot see the first one's
        // clone.
        var again = Assert.Single(await provider.GetMediaSources(item, CancellationToken.None));
        Assert.Equal(
            1920,
            Assert.Single(original.MediaStreams, stream => stream.Type == MediaStreamType.Video).Width);
        Assert.Equal(3840, Assert.Single(again.MediaStreams, stream => stream.Type == MediaStreamType.Video).Width);
    }

    [Theory]
    [InlineData(null, null)] // never probed for a size
    [InlineData(0, null)] // a report that carries 0 rather than nothing
    public async Task AVersionDoesNotInventAWidthTheSourceNeverReported(int? sourceWidth, int? sourceHeight)
    {
        // Doubling nothing produces a number, and a number out of nothing is exactly what the
        // server would then scale a real picture against. With no width to work from the version
        // reports what the file reports, which is the answer the original source gives too.
        var configuration = new StubConfigurationSource
        {
            Configuration = new PluginConfiguration { EnabledProfileIds = new List<string> { ProfileIds.SideBySideFull } }
        };
        var provider = CreateProvider(configuration: configuration);
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        original.MediaStreams = new[]
        {
            new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = sourceWidth, Height = sourceHeight }
        };
        item.StaticSources = new[] { original };

        var source = Assert.Single(await provider.GetMediaSources(item, CancellationToken.None));

        var video = Assert.Single(source.MediaStreams, stream => stream.Type == MediaStreamType.Video);
        Assert.Equal(sourceWidth, video.Width);
        Assert.Equal(sourceHeight, video.Height);

        // The forced codec is a separate lever and is still reported: keeping the version on the
        // encode path does not depend on knowing its size.
        Assert.Equal(ForceTranscodeVideoStreams.VideoCodec, video.Codec);
    }

    [Fact]
    public async Task TheHeightASourceReportedIsKeptEvenWhenTheWidthCouldNotBeDoubled()
    {
        // Only the width is derived here. Dropping or halving a height the file did report,
        // because the width beside it was missing, would lose information the server sizes
        // against for nothing.
        var configuration = new StubConfigurationSource
        {
            Configuration = new PluginConfiguration { EnabledProfileIds = new List<string> { ProfileIds.SideBySideFull } }
        };
        var provider = CreateProvider(configuration: configuration);
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        original.MediaStreams = new[]
        {
            new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = null, Height = 1080 }
        };
        item.StaticSources = new[] { original };

        var source = Assert.Single(await provider.GetMediaSources(item, CancellationToken.None));

        var video = Assert.Single(source.MediaStreams, stream => stream.Type == MediaStreamType.Video);
        Assert.Null(video.Width);
        Assert.Equal(1080, video.Height);
    }

    [Fact]
    public async Task AnaglyfinVersionsDeclareNoStereoFormatForTheServerToUndo()
    {
        // A version's stereo claim lives in its picture, not in its metadata, and leaving the
        // field alone is not enough to say that: the item's own source carries a real
        // Video3DFormat, and a version that inherited it in the name of fidelity would tell the
        // server to run its own stereo conversion over the frame the profile has just produced.
        // Jellyfin 12 answers a SideBySide/HalfSideBySide source and fixed request dimensions
        // with a crop-and-setsar chain straight out of GetFixedSwScaleFilter - the server taking
        // the file apart again while a version is trying to deliver it.
        var provider = CreateProvider();
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        original.Video3DFormat = Video3DFormat.MVC;
        item.StaticSources = new[] { original };

        var sources = await provider.GetMediaSources(item, CancellationToken.None);

        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            Assert.Null(source.Video3DFormat);
        }

        // The item's own source keeps its own declaration.
        Assert.Equal(Video3DFormat.MVC, original.Video3DFormat);
    }

    [Fact]
    public async Task ReLabellingTheVersionsLeavesTheItemsOwnReportAlone()
    {
        // The item's original version reports the very same stream objects this provider was
        // handed. Mutating them in place would leave the one version that must always keep
        // working - the file as it sits on disk - claiming its video is something it is not,
        // which is a broken original library source for every client on the server.
        var provider = CreateProvider();
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        item.StaticSources = new[] { original };

        await provider.GetMediaSources(item, CancellationToken.None);

        var probed = Assert.Single(original.MediaStreams, stream => stream.Type == MediaStreamType.Video);
        Assert.Equal("hevc", probed.Codec);
        Assert.Equal(4, original.MediaStreams.Count);
    }

    [Fact]
    public async Task TheMarkerNamesTheVideoStreamByIdItsOwnReportCarries()
    {
        // The number the server spends on "-map 0:<index>" is the stream's index inside the
        // file, and for a report the probe wrote in file order that is also the position the
        // stream sits at in the list. Naming it matters: a marker saying stream 0 while the
        // server maps 0:2 leaves both maps on the command and converts nothing.
        var provider = CreateProvider();
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        original.MediaStreams = new[]
        {
            new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = "aac", Language = "eng" },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 1, Language = "eng" },
            new MediaStream { Type = MediaStreamType.Video, Index = 2, Codec = "hevc", Width = 1920, Height = 1080 },
            new MediaStream { Type = MediaStreamType.Audio, Index = 3, Codec = "ac3", Language = "spa" }
        };

        item.StaticSources = new[] { original };

        var sources = (await provider.GetMediaSources(item, CancellationToken.None)).ToList();

        foreach (var source in sources)
        {
            var parsed = ProfileMarkerParser.Parse(source.Path);

            Assert.True(parsed.IsSuccess);
            Assert.Equal(2, parsed.VideoStreamIndex);
            Assert.Contains("&video=2", source.Path, StringComparison.Ordinal);

            // The named stream is the version's re-labelled video, and it is still the only
            // video stream in the report.
            Assert.Equal(MediaStreamType.Video, source.MediaStreams[2].Type);
            Assert.Equal(ForceTranscodeVideoStreams.VideoCodec, source.MediaStreams[2].Codec);
            Assert.Same(original.MediaStreams[0], source.MediaStreams[0]);
            Assert.Same(original.MediaStreams[1], source.MediaStreams[1]);
            Assert.Same(original.MediaStreams[3], source.MediaStreams[3]);
        }
    }

    [Fact]
    public async Task TheMarkerNamesTheStreamIndexAndNotThePlaceTheStreamSitsInTheList()
    {
        // The two numbers are only ever the same while the report is in file order, and a
        // report is out of it as soon as the file carries a stream the server does not report
        // (a data stream, an attached picture) or appends one it does. Here the video is the
        // file's stream 1 and the first entry of the list. A marker that named the position
        // would say 0, the server would map 0:1, and the wrapper would take the audio out of
        // the playlist while leaving the base view beside the profile's own.
        var provider = CreateProvider();
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        original.MediaStreams = new[]
        {
            new MediaStream { Type = MediaStreamType.Video, Index = 1, Codec = "hevc", Width = 1920, Height = 1080 },
            new MediaStream { Type = MediaStreamType.Audio, Index = 2, Codec = "aac", Language = "eng" },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 4, Language = "eng" }
        };

        item.StaticSources = new[] { original };

        foreach (var source in await provider.GetMediaSources(item, CancellationToken.None))
        {
            var parsed = ProfileMarkerParser.Parse(source.Path);

            Assert.True(parsed.IsSuccess);
            Assert.Equal(1, parsed.VideoStreamIndex);
            Assert.Contains("&video=1", source.Path, StringComparison.Ordinal);

            // The re-labelled video is the stream this list holds first, and it kept the index
            // the file gave it: the version's report and the server's map count the same file.
            var video = source.MediaStreams[0];
            Assert.Equal(MediaStreamType.Video, video.Type);
            Assert.Equal(ForceTranscodeVideoStreams.VideoCodec, video.Codec);
            Assert.Equal(1, video.Index);
            Assert.NotSame(original.MediaStreams[0], video);
            Assert.Same(original.MediaStreams[1], source.MediaStreams[1]);
            Assert.Same(original.MediaStreams[2], source.MediaStreams[2]);
        }
    }

    [Fact]
    public async Task AVideoStreamThatNamesNoIndexIsStillForcedToEncodeButIsNotNamed()
    {
        // A stream carried over from a source that was never probed for an index answers -1.
        // Guessing a position for it would put a number the server's map does not use into the
        // marker, and the wrapper would then remove whichever stream did use it. The codec
        // re-label is a separate question and still happens, because that is what keeps the
        // version off the copy path.
        var provider = CreateProvider();
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        original.MediaStreams = new[]
        {
            new MediaStream { Type = MediaStreamType.Video, Index = -1, Codec = "hevc", Width = 1920, Height = 1080 },
            new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = "aac", Language = "eng" }
        };

        item.StaticSources = new[] { original };

        foreach (var source in await provider.GetMediaSources(item, CancellationToken.None))
        {
            Assert.DoesNotContain("&video=", source.Path, StringComparison.Ordinal);
            Assert.Null(ProfileMarkerParser.Parse(source.Path).VideoStreamIndex);

            var video = source.MediaStreams[0];
            Assert.Equal(ForceTranscodeVideoStreams.VideoCodec, video.Codec);
            Assert.NotSame(original.MediaStreams[0], video);
            Assert.Equal(-1, video.Index);
            Assert.Same(original.MediaStreams[1], source.MediaStreams[1]);
        }
    }

    [Fact]
    public async Task AVersionWithNoVideoStreamToNameCarriesNoVideoParameter()
    {
        // An item the server has no video stream for is not this provider's problem to
        // invent one for: the version keeps its streams as reported and its marker keeps
        // quiet about a position that does not exist.
        var provider = CreateProvider();
        var item = CreateMvcItem();
        var original = CreateOriginalSource();
        original.MediaStreams = new[]
        {
            new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = "aac", Language = "eng" }
        };

        item.StaticSources = new[] { original };

        foreach (var source in await provider.GetMediaSources(item, CancellationToken.None))
        {
            Assert.DoesNotContain("&video=", source.Path, StringComparison.Ordinal);
            Assert.Null(ProfileMarkerParser.Parse(source.Path).VideoStreamIndex);
            Assert.Same(original.MediaStreams[0], Assert.Single(source.MediaStreams));
        }
    }

    [Fact]
    public async Task VersionsSelectTheSourceThatIsTheItemWhenThereAreSeveral()
    {
        var item = CreateMvcItem();
        var unrelated = new MediaSourceInfo { Id = "someone-elses-source", RunTimeTicks = 1, Container = "avi" };
        item.StaticSources = new[] { unrelated, CreateOriginalSource() };
        var provider = CreateProvider();

        var sources = await provider.GetMediaSources(item, CancellationToken.None);

        // Fidelity came from the item's own source, not from whatever else the item
        // exposes (linked alternate versions would add more).
        Assert.All(sources, source =>
        {
            Assert.Equal(RunTimeTicks, source.RunTimeTicks);
            Assert.Equal("mkv", source.Container);
        });
    }

    [Fact]
    public async Task VersionsFallBackToTheItemItselfWhenStaticSourcesAreUnavailable()
    {
        var provider = CreateProvider();
        var item = CreateMvcItem();
        item.StaticSources = Array.Empty<MediaSourceInfo>();

        var sources = (await provider.GetMediaSources(item, CancellationToken.None)).ToList();

        foreach (var source in sources)
        {
            // Duration still comes from the item; streams stay empty because probing is
            // (correctly) off and there was nothing to copy - fidelity drops, lies do not.
            Assert.Equal(RunTimeTicks, source.RunTimeTicks);
            Assert.Empty(source.MediaStreams);
            Assert.Null(source.Container);
            Assert.Empty(source.Formats);
        }
    }

    [Fact]
    public async Task VersionsAskForServerSidePathsNotClientSubstitutions()
    {
        var item = CreateMvcItem();
        item.StaticSources = new[] { CreateOriginalSource() };
        var provider = CreateProvider();

        await provider.GetMediaSources(item, CancellationToken.None);

        Assert.False(item.StaticPathSubstitutionRequested);
    }

    // --- marker transport -----------------------------------------------------------

    [Fact]
    public async Task VersionPathsAreMarkersNamingTheItemsOwnMediaPathAndNothingElse()
    {
        // The provider owns marker paths: the only filesystem value a version may carry
        // is the item's own library path, percent-encoded inside the marker query. A
        // raw path, a folder, or any foreign string in Path would put caller-shaped
        // input on the FFmpeg command line.
        var provider = CreateProvider();
        var item = CreateMvcItem();

        var sources = (await provider.GetMediaSources(item, CancellationToken.None)).ToList();

        foreach (var source in sources)
        {
            var parsed = ProfileMarkerParser.Parse(source.Path);

            Assert.True(parsed.IsSuccess, $"expected a valid marker, got {parsed.Status}");
            Assert.Equal(MvcMoviePath, parsed.SourcePath);
            Assert.StartsWith(ProfileMarker.MarkerPrefix, source.Path, StringComparison.Ordinal);

            // Which conversion a version is stays readable from the marker and nowhere
            // else: the id is an opaque GUID - by design, because the HLS routes parse it
            // - so the wrapper must keep getting the profile from this path.
            Assert.Equal(IdOf(parsed.ProfileId!), source.Id);
            Assert.DoesNotContain(parsed.ProfileId!, source.Id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task VersionMarkersCarryNoSubtitleOrdinalInTheMvp()
    {
        var provider = CreateProvider();

        var sources = await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None);

        foreach (var source in sources)
        {
            Assert.DoesNotContain("&subtitle=", source.Path, StringComparison.Ordinal);
            Assert.Null(ProfileMarkerParser.Parse(source.Path).SubtitleOrdinal);
        }
    }

    // --- stability, statelessness, refusal to open ----------------------------------

    [Fact]
    public async Task RepeatedRequestsGiveEqualSourceIdsWithoutReusingObjects()
    {
        var provider = CreateProvider();
        var item = CreateMvcItem();

        var first = (await provider.GetMediaSources(item, CancellationToken.None)).ToList();
        var second = (await provider.GetMediaSources(item, CancellationToken.None)).ToList();

        Assert.Equal(first.Select(source => source.Id), second.Select(source => source.Id));

        // Stable identities, fresh objects: the same answer computed twice, not a
        // cached one handed back.
        Assert.NotSame(first[0], second[0]);
    }

    [Fact]
    public async Task SourceIdsAreStableAcrossInstancesBecauseTheyAreDerivedAndNotAssigned()
    {
        var item = CreateMvcItem();

        // A fresh provider is what a server restart looks like to this code: nothing is
        // generated at construction and nothing is stored, so the ids a client saved
        // still name the same versions afterwards. An id minted per request or per
        // instance would pass every other test here and still break resume.
        var before = (await CreateProvider().GetMediaSources(item, CancellationToken.None)).ToList();
        var after = (await CreateProvider().GetMediaSources(item, CancellationToken.None)).ToList();

        Assert.Equal(before.Select(source => source.Id), after.Select(source => source.Id));
    }

    [Fact]
    public async Task EveryOfferedSourceIdIsAGuidTheDynamicHlsRoutesAccept()
    {
        var provider = CreateProvider();

        var sources = (await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None)).ToList();

        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            // Guid.TryParse is the call the master and main playlists make on this value
            // (via Guid.Parse); a source that survives PlaybackInfo and fails here is the
            // bug this file exists for, not a limitation of the test.
            AssertIsLowerCaseGuid(source.Id);

            // The item's own source is keyed by the item id and put first, so wearing it
            // would make the original version unreachable.
            Assert.NotEqual(ItemKey, source.Id, StringComparer.Ordinal);
            Assert.NotEqual(ItemId.ToString("D", CultureInfo.InvariantCulture), source.Id, StringComparer.Ordinal);
        }

        // One id per version: two versions with one id is one playable version.
        Assert.Equal(sources.Count, sources.Select(source => source.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task TheProviderIsConstructedForRealDependencyResolutions()
    {
        // The server activates providers with ActivatorUtilities: the production wiring
        // is detector + catalog + settings source + logger, nothing else.
        var provider = new AnaglyfinMediaSourceProvider(
            new MvcSourceDetector(),
            new ProfileCatalog(),
            new StubConfigurationSource(),
            NullLogger<AnaglyfinMediaSourceProvider>.Instance);

        var sources = (await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None)).ToList();

        Assert.Equal(4, sources.Count);
        Assert.Equal(IdOf(ProfileIds.AnaglyphRedCyanDubois), sources[0].Id);
    }

    [Fact]
    public async Task OpenMediaSourceRefusesBecauseNoSourceAsksToBeOpened()
    {
        var provider = CreateProvider();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.OpenMediaSource("any-token", new List<ILiveStream>(), CancellationToken.None));
    }

    // --- detector integration ---------------------------------------------------------

    [Fact]
    public async Task TheRealDetectorKeepsPlainMoviesOut()
    {
        var provider = new AnaglyfinMediaSourceProvider(
            new MvcSourceDetector(),
            new ProfileCatalog(),
            new StubConfigurationSource(),
            NullLogger<AnaglyfinMediaSourceProvider>.Instance);

        var plain = CreateMvcItem(path: "/movies/Avatar (2009)/Avatar (2009).mkv", name: "Avatar (2009)");

        Assert.Empty(await provider.GetMediaSources(plain, CancellationToken.None));
    }

    [Fact]
    public async Task TheRealDetectorLetsMvcMarkedItemsThrough()
    {
        var provider = new AnaglyfinMediaSourceProvider(
            new MvcSourceDetector(),
            new ProfileCatalog(),
            new StubConfigurationSource(),
            NullLogger<AnaglyfinMediaSourceProvider>.Instance);

        var sources = await provider.GetMediaSources(CreateMvcItem(), CancellationToken.None);

        Assert.NotEmpty(sources);
    }

    // --- construction helpers -----------------------------------------------------------

    private static AnaglyfinMediaSourceProvider CreateProvider(
        IMvcSourceDetector? detector = null,
        IProfileCatalog? catalog = null,
        StubConfigurationSource? configuration = null)
        => new(
            detector ?? new ScriptedDetector(),
            catalog ?? new ProfileCatalog(),
            configuration ?? new StubConfigurationSource(),
            NullLogger<AnaglyfinMediaSourceProvider>.Instance);

    private static FakeVideo CreateMvcItem(
        string path = MvcMoviePath,
        string name = MvcMovieName,
        VideoType videoType = VideoType.VideoFile)
        => new()
        {
            Id = ItemId,
            Name = name,
            Path = path,
            RunTimeTicks = RunTimeTicks,
            VideoType = videoType,
            StaticSources = new[] { CreateOriginalSource() }
        };

    private static MediaSourceInfo CreateOriginalSource()
        => new()
        {
            Id = ItemId.ToString("N", CultureInfo.InvariantCulture),
            Name = MvcMovieName,
            Path = MvcMoviePath,
            Protocol = MediaProtocol.File,
            Container = "mkv",
            Size = FileSize,
            Bitrate = 40_000_000,
            RunTimeTicks = RunTimeTicks,
            Formats = new[] { "matroska" },
            MediaStreams = new[]
            {
                // A probed 1080p MVC track: the per-eye frame ffprobe reports for the sample
                // this feature was debugged on, which is the number the geometry assertions
                // below double or keep.
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = 1920, Height = 1080 },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Language = "eng" },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Language = "eng" },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 3, Language = "spa" }
            }
        };

    // --- id helpers ------------------------------------------------------------------------

    /// <summary>
    /// The id the provider is expected to give one profile of the shared test item, so the
    /// order assertions can name the profile they mean instead of a hex string.
    /// </summary>
    private static string IdOf(string profileId)
        => AnaglyfinMediaSourceProvider.BuildMediaSourceId(ItemId, MvcMoviePath, profileId);

    /// <summary>
    /// The provider's documented derivation, computed here rather than called: MD5 over the
    /// UTF-16 bytes of <c>"&lt;item id, N&gt;:&lt;profile id&gt;"</c>, read as a GUID and
    /// formatted lower-case "N" - which is what Jellyfin's own
    /// <c>"&lt;seed&gt;".GetMD5().ToString("N")</c> answers. Restating it is the point: a
    /// test that asks the provider for its own expectation proves nothing about the shape
    /// the server is going to parse.
    /// </summary>
    private static string DeriveGuid(string seed)
    {
#pragma warning disable CA5351 // Deliberately the provider's id fold, weaknesses and all: an id hash, not a digest.
        return new Guid(MD5.HashData(Encoding.Unicode.GetBytes(seed))).ToString("N", CultureInfo.InvariantCulture);
#pragma warning restore CA5351
    }

    /// <summary>
    /// Fails unless an id is a GUID in the single shape the playback routes agree on: 32
    /// lower-case hex digits, no dashes, no braces, no decoration.
    /// </summary>
    private static void AssertIsLowerCaseGuid(string id)
    {
        Assert.True(Guid.TryParse(id, out var parsed), $"not a GUID: {id}");

        // The round trip is what rejects the decoration: "N" is the canonical, lower-case,
        // separator-free form, so an id that parses but is not that form is an id some
        // comparison somewhere will eventually miss.
        Assert.Equal(parsed.ToString("N", CultureInfo.InvariantCulture), id);
        Assert.NotEqual(Guid.Empty, parsed);
    }

    // --- fakes --------------------------------------------------------------------------

    /// <summary>
    /// A <see cref="Video"/> whose static media sources are scripted, because a unit
    /// test has no database for the real item to read them from.
    /// </summary>
    private sealed class FakeVideo : Video
    {
        public IReadOnlyList<MediaSourceInfo> StaticSources { get; set; } = Array.Empty<MediaSourceInfo>();

        /// <summary>
        /// Records whether the provider ever asked for client-substituted paths; it must not.
        /// </summary>
        public bool StaticPathSubstitutionRequested { get; private set; }

        public override IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution)
        {
            StaticPathSubstitutionRequested |= enablePathSubstitution;
            return StaticSources;
        }
    }

    /// <summary>
    /// An <see cref="IMvcSourceDetector"/> with a fixed script, so tests can separate
    /// eligibility plumbing from the detection rules themselves.
    /// </summary>
    private sealed class ScriptedDetector : IMvcSourceDetector
    {
        public bool Eligible { get; init; } = true;

        public bool Throws { get; init; }

        public int CallCount { get; private set; }

        public MvcSourceCandidate? LastCandidate { get; private set; }

        public MvcSourceEligibility Detect(MvcSourceCandidate candidate)
        {
            CallCount++;
            LastCandidate = candidate;

            if (Throws)
            {
                throw new InvalidOperationException("detector failure under test");
            }

            return Eligible
                ? MvcSourceEligibility.Eligible(MvcEligibilityReason.ItemMetadataDeclaresMvc, MvcDetectionConfidence.High)
                : MvcSourceEligibility.NotEligible(MvcEligibilityReason.NoMvcSignal);
        }
    }

    /// <summary>
    /// An <see cref="IAnaglyfinConfigurationSource"/> over a given settings object that
    /// also counts reads, for asserting the hot path reads it once.
    /// </summary>
    private sealed class StubConfigurationSource : IAnaglyfinConfigurationSource
    {
        public PluginConfiguration Configuration { get; set; } = new();

        public int CallCount { get; private set; }

        public PluginConfiguration GetConfiguration()
        {
            CallCount++;
            return Configuration;
        }
    }

    /// <summary>
    /// A catalog that has profiles to look up and none to offer: the shape an
    /// administrator who disabled everything leaves behind.
    /// </summary>
    private sealed class EmptyProfileCatalog : IProfileCatalog
    {
        private readonly ProfileCatalog _inner = new();

        public IReadOnlyList<StereoProfile> Profiles => _inner.Profiles;

        public IReadOnlyList<string> AllProfileIds => _inner.AllProfileIds;

        public bool IsKnownProfileId(string? profileId) => _inner.IsKnownProfileId(profileId);

        public bool TryGetProfile(string? profileId, [NotNullWhen(true)] out StereoProfile? profile) => _inner.TryGetProfile(profileId, out profile);

        public StereoProfile GetProfile(string? profileId) => _inner.GetProfile(profileId);

        public IReadOnlyList<string> GetEnabledProfileIds(PluginConfiguration configuration) => _inner.GetEnabledProfileIds(configuration);

        public IReadOnlyList<StereoProfile> GetEnabledProfiles(PluginConfiguration configuration) => _inner.GetEnabledProfiles(configuration);

        public string ResolveDefaultProfileId(PluginConfiguration configuration, string? deviceId = null, string? clientName = null)
            => _inner.ResolveDefaultProfileId(configuration, deviceId, clientName);

        public StereoProfile GetDefaultProfile(PluginConfiguration configuration, string? deviceId = null, string? clientName = null)
            => _inner.GetDefaultProfile(configuration, deviceId, clientName);

        public string ResolveFallbackProfileId(PluginConfiguration configuration) => _inner.ResolveFallbackProfileId(configuration);

        public StereoProfile GetFallbackProfile(PluginConfiguration configuration) => _inner.GetFallbackProfile(configuration);

        public IReadOnlyList<StereoProfile> GetOfferedProfiles(PluginConfiguration configuration, string? deviceId = null, string? clientName = null)
            => Array.Empty<StereoProfile>();
    }
}
