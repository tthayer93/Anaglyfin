using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
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
    public void SourceIdIsPrefixItemAndProfileAndIsAllLowercase()
    {
        var id = AnaglyfinMediaSourceProvider.BuildMediaSourceId(ItemId, MvcMoviePath, ProfileIds.CustomGrayscale);

        Assert.Equal($"anaglyfin:{ItemKey}:custom_grayscale", id);
        Assert.Equal(id, id.ToLowerInvariant());
    }

    [Fact]
    public void SourceIdFallsBackToAPathDigestWhenTheItemHasNoId()
    {
        var withPath = AnaglyfinMediaSourceProvider.BuildMediaSourceId(Guid.Empty, MvcMoviePath, ProfileIds.TwoDBase);
        var otherPath = AnaglyfinMediaSourceProvider.BuildMediaSourceId(Guid.Empty, "/movies/Other.3D.MVC.mkv", ProfileIds.TwoDBase);

        Assert.StartsWith(AnaglyfinMediaSourceProvider.MediaSourceIdPrefix, withPath, StringComparison.Ordinal);
        Assert.EndsWith(":two_d_base", withPath, StringComparison.Ordinal);
        Assert.Equal(withPath, withPath.ToLowerInvariant());
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
        Assert.Equal(
            new[]
            {
                $"{AnaglyfinMediaSourceProvider.MediaSourceIdPrefix}{ItemKey}:anaglyph_arcd",
                $"{AnaglyfinMediaSourceProvider.MediaSourceIdPrefix}{ItemKey}:sbs_full",
                $"{AnaglyfinMediaSourceProvider.MediaSourceIdPrefix}{ItemKey}:sbs_half",
                $"{AnaglyfinMediaSourceProvider.MediaSourceIdPrefix}{ItemKey}:two_d_base"
            },
            sources.Select(source => source.Id));
        Assert.Equal(
            new[] { "3D Anaglyph Red/Cyan (Dubois)", "3D Full Side-by-Side", "3D Half Side-by-Side", "2D Base" },
            sources.Select(source => source.Name));

        // The fallback profile is the safe last resort, so it sorts last.
        Assert.EndsWith(":two_d_base", sources[^1].Id, StringComparison.Ordinal);

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
        Assert.EndsWith(":sbs_full", sources[0].Id, StringComparison.Ordinal);
        Assert.EndsWith(":anaglyph_arcd", sources[1].Id, StringComparison.Ordinal);
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
        Assert.EndsWith(":sbs_full", sources[0].Id, StringComparison.Ordinal);
        Assert.EndsWith(":custom_grayscale", sources[1].Id, StringComparison.Ordinal);
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

        Assert.Equal($"anaglyfin:{ItemKey}:anaglyph_arcd", source.Id);
        Assert.Equal("3D Anaglyph Red/Cyan (Dubois)", source.Name);
        Assert.Equal(
            "http://127.0.0.1/anaglyfin/profile/anaglyph_arcd?source=%2Fmovies%2FAvatar%203D%20MVC.mkv",
            source.Path);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.False(source.SupportsDirectPlay);
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
            Assert.Equal(original.MediaStreams, source.MediaStreams);
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
            Assert.EndsWith($":{parsed.ProfileId}", source.Id, StringComparison.Ordinal);
            Assert.StartsWith(ProfileMarker.MarkerPrefix, source.Path, StringComparison.Ordinal);
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
        Assert.EndsWith(":anaglyph_arcd", sources[0].Id, StringComparison.Ordinal);
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
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc" },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Language = "eng" },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Language = "eng" },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 3, Language = "spa" }
            }
        };

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
