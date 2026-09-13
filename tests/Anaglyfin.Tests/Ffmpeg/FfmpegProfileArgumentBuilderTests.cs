using System;
using System.Collections.Generic;
using System.Linq;
using Anaglyfin.Ffmpeg;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.Ffmpeg;

/// <summary>
/// Exact-output tests for the FFmpeg profile argument builder.
/// </summary>
/// <remarks>
/// Every expectation is written verbatim on purpose: these strings are the commands
/// FFmpeg-mvc will run, so a change to a generated argument must be an explicit,
/// reviewed edit here rather than a silent drift. No FFmpeg is executed.
/// </remarks>
public class FfmpegProfileArgumentBuilderTests
{
    private const string MoviePath = "/movies/Movie (2010)/Movie.2010.3D.mkv";

    private static readonly ProfileCatalog Catalog = new();
    private static readonly SubtitleBurnIn BurnIn = new(MoviePath, 0);

    private readonly FfmpegProfileArgumentBuilder _builder = new();

    public static TheoryData<string> BuiltInAnaglyphCodes => new(ProfileIds.BuiltInAnaglyphOutputCodes.ToArray());

    // ----- 2D base ----------------------------------------------------------------

    [Fact]
    public void TwoDimensionalBaseInsertsNothingAndKeepsDefaultBaseView()
    {
        var rewrite = _builder.BuildTwoDimensionalBase();

        Assert.Equal(ProfileIds.TwoDBase, rewrite.ProfileId);
        Assert.Equal(ProfileKind.TwoDimensional, rewrite.Kind);
        Assert.Empty(rewrite.InsertArguments);
        Assert.Null(rewrite.VideoMap);
        Assert.Null(rewrite.VideoFilter);
        Assert.Null(rewrite.FilterComplex);
        Assert.Null(rewrite.SubtitleFilter);
        Assert.False(rewrite.ShouldSuppressSubtitleStreams);
        Assert.False(rewrite.ShouldAppendSubtitlesToProfileFilter);
    }

    [Fact]
    public void TwoDimensionalBuildIgnoresSubtitleBurnInForTheStockPipeline()
    {
        var profile = Catalog.GetProfile(ProfileIds.TwoDBase);

        var rewrite = _builder.Build(profile, new SubtitleBurnIn(MoviePath, 2));

        Assert.Empty(rewrite.InsertArguments);
        Assert.Null(rewrite.SubtitleFilter);
        Assert.False(rewrite.ShouldSuppressSubtitleStreams);
    }

    // ----- Full SBS ----------------------------------------------------------------

    [Fact]
    public void FullSideBySideMapsAllViewsAndAddsNoFilter()
    {
        var rewrite = _builder.BuildSideBySideFull();

        Assert.Equal(ProfileIds.SideBySideFull, rewrite.ProfileId);
        Assert.Equal(
            new[] { "-map", "0:v:view:all" },
            rewrite.InsertArguments);
        Assert.Equal("0:v:view:all", rewrite.VideoMap);
        Assert.Null(rewrite.VideoFilter);
        Assert.Null(rewrite.FilterComplex);
        Assert.True(rewrite.ShouldSuppressSubtitleStreams);

        // The native all-view frame IS full SBS: neither a re-tag nor a resize.
        Assert.DoesNotContain("stereo3d", string.Join(" ", rewrite.InsertArguments));
        Assert.DoesNotContain("scale", string.Join(" ", rewrite.InsertArguments));
    }

    // ----- Half SBS ----------------------------------------------------------------

    [Fact]
    public void HalfSideBySideScalesTheCombinedNativeSbsFrame()
    {
        var rewrite = _builder.BuildSideBySideHalf();

        Assert.Equal(ProfileIds.SideBySideHalf, rewrite.ProfileId);
        Assert.Equal(
            new[] { "-map", "0:v:view:all", "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p" },
            rewrite.InsertArguments);
        Assert.Equal("0:v:view:all", rewrite.VideoMap);
        Assert.Equal("scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p", rewrite.VideoFilter);
        Assert.Null(rewrite.FilterComplex);
    }

    [Fact]
    public void HalfSideBySideDeclaresSquarePixelsAfterHalvingTheWidth()
    {
        // The scale is what halves the frame, and it is also what hands on a 2:1 sample aspect
        // ratio while it does: scale keeps the display aspect by adjusting the pixel shape
        // (out SAR = in SAR x in size / out size), so a square-pixel 3840x1080 becomes
        // 1920x1080 of 2:1 pixels. The encoded pixels are exactly the half-SBS frame this
        // profile sells, so nothing is wrong with the picture - but everything downstream that
        // trusts the shape travelling with it stretches it sideways, including the server's own
        // scale once a client reports a ceiling. Declaring 1:1 immediately behind the scale,
        // and before the pixel-format normalisation, is part of the conversion: it is what makes
        // the 1920x1080 this profile reports to clients mean 1920x1080 of square pixels.
        var stages = _builder.BuildSideBySideHalf().VideoFilter!.Split(',');

        Assert.Equal(
            new[] { "scale=iw/2:ih:flags=bicubic", "setsar=sar=1", "format=yuv420p" },
            stages);
    }

    [Fact]
    public void OnlyHalfSideBySideDeclaresItsSampleAspectRatio()
    {
        // The other chains change no pixel shape: full SBS is the decoder's own frame, an
        // anaglyph mixes inside it, and 2D inserts nothing. A setsar in one of those would be a
        // claim about a scale that never happened.
        Assert.DoesNotContain(
            "setsar",
            string.Join(' ', _builder.BuildSideBySideFull().InsertArguments));
        Assert.DoesNotContain(
            "setsar",
            string.Join(' ', _builder.BuildStereo3DAnaglyph("arcd").InsertArguments));
        Assert.DoesNotContain(
            "setsar",
            string.Join(' ', _builder.BuildCustomGrayscaleAnaglyph(new RgbColor(255, 0, 0), new RgbColor(0, 255, 255)).InsertArguments));
        Assert.DoesNotContain("setsar", string.Join(' ', _builder.BuildTwoDimensionalBase().InsertArguments));
    }

    [Fact]
    public void HalfSideBySideIsDerivedFromTheAllViewOutputNotSeparateEyeTranscodes()
    {
        var rewrite = _builder.BuildSideBySideHalf();

        // One all-view map, no filter_complex, no second input, and real scaling -
        // a stereo3d re-tag would keep the frame size and only halve the PAR.
        Assert.Equal("0:v:view:all", rewrite.VideoMap);
        Assert.Null(rewrite.FilterComplex);
        Assert.Contains("scale=iw/2:ih", rewrite.VideoFilter);
        Assert.DoesNotContain("stereo3d", string.Join(" ", rewrite.InsertArguments));
    }

    // ----- Stereo3D anaglyph presets -----------------------------------------------

    [Theory]
    [MemberData(nameof(BuiltInAnaglyphCodes))]
    public void EachBuiltInAnaglyphPresetMapsAllViewsAndAppliesItsOfficialCode(string outputCode)
    {
        var rewrite = _builder.BuildStereo3DAnaglyph(outputCode);

        Assert.Equal(ProfileIds.BuildAnaglyphProfileId(outputCode), rewrite.ProfileId);
        Assert.Equal(
            new[] { "-map", "0:v:view:all", "-vf", $"stereo3d=sbsl:{outputCode},format=yuv420p" },
            rewrite.InsertArguments);
        Assert.Equal("0:v:view:all", rewrite.VideoMap);
        Assert.Equal($"stereo3d=sbsl:{outputCode},format=yuv420p", rewrite.VideoFilter);
        Assert.True(rewrite.ShouldSuppressSubtitleStreams);
    }

    [Fact]
    public void RedCyanDuboisPresetProducesTheDocumentedCommand()
    {
        var profile = Catalog.GetProfile(ProfileIds.AnaglyphRedCyanDubois);

        var rewrite = _builder.Build(profile);

        Assert.Equal(
            new[] { "-map", "0:v:view:all", "-vf", "stereo3d=sbsl:arcd,format=yuv420p" },
            rewrite.InsertArguments);
    }

    [Theory]
    [InlineData("arcd;scale=2:2")]
    [InlineData("sbs2l")]
    [InlineData("arcg,format=rgb24")]
    [InlineData("ARCD")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnknownStereo3DOutputCodeIsRejectedNotInterpreted(string outputCode)
    {
        Assert.Throws<ArgumentException>(() => _builder.BuildStereo3DAnaglyph(outputCode));
    }

    [Fact]
    public void MissingStereo3DOutputCodeIsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => _builder.BuildStereo3DAnaglyph(null!));
    }

    [Fact]
    public void AnaglyphProfileWithADisallowedCodeFailsTheGenericBuild()
    {
        // A hand-built or settings-corrupted profile is not trusted even though the
        // catalog validates its own instances: the builder is the last seam.
        var tampered = new StereoProfile
        {
            Id = ProfileIds.AnaglyphRedCyanDubois,
            DisplayName = "Tampered",
            Kind = ProfileKind.Stereo3DAnaglyph,
            Stereo3DOutputCode = "arcd,scale=7680:ih"
        };

        Assert.Throws<ArgumentException>(() => _builder.Build(tampered));
    }

    [Fact]
    public void AnaglyphProfileWithoutAnOutputCodeFailsTheGenericBuild()
    {
        var tampered = new StereoProfile
        {
            Id = ProfileIds.AnaglyphRedCyanDubois,
            DisplayName = "Tampered",
            Kind = ProfileKind.Stereo3DAnaglyph
        };

        Assert.Throws<InvalidOperationException>(() => _builder.Build(tampered));
    }

    [Fact]
    public void GenericBuildMatchesTheExplicitPresetBuildForEveryCatalogPreset()
    {
        foreach (var outputCode in ProfileIds.BuiltInAnaglyphOutputCodes)
        {
            var profile = Catalog.GetProfile(ProfileIds.BuildAnaglyphProfileId(outputCode));

            var generic = _builder.Build(profile);
            var explicitBuild = _builder.BuildStereo3DAnaglyph(outputCode);

            Assert.Equal(explicitBuild.ProfileId, generic.ProfileId);
            Assert.Equal(explicitBuild.InsertArguments, generic.InsertArguments);
        }
    }

    // ----- Custom grayscale anaglyph ------------------------------------------------

    [Fact]
    public void CustomGrayscaleAnaglyphProducesExactlyTheContractedFilterGraph()
    {
        var rewrite = _builder.BuildCustomGrayscaleAnaglyph(new RgbColor(255, 0, 0), new RgbColor(0, 255, 255));

        const string ExpectedGraph =
            "[0:v:view:all]split=2[anaglyfin_cg_left_in][anaglyfin_cg_right_in];"
            + "[anaglyfin_cg_left_in]crop=iw/2:ih:0:0,format=gray,format=rgb24,colorchannelmixer=rr=1:gg=0:bb=0[anaglyfin_cg_left];"
            + "[anaglyfin_cg_right_in]crop=iw/2:ih:iw/2:0,format=gray,format=rgb24,colorchannelmixer=rr=0:gg=1:bb=1[anaglyfin_cg_right];"
            + "[anaglyfin_cg_left][anaglyfin_cg_right]blend=all_mode=screen,format=yuv420p[anaglyfin_custom]";

        Assert.Equal(ProfileIds.CustomGrayscale, rewrite.ProfileId);
        Assert.Equal(
            new[] { "-filter_complex", ExpectedGraph, "-map", "[anaglyfin_custom]" },
            rewrite.InsertArguments);
        Assert.Equal("[anaglyfin_custom]", rewrite.VideoMap);
        Assert.Null(rewrite.VideoFilter);
        Assert.Equal(ExpectedGraph, rewrite.FilterComplex);
        Assert.True(rewrite.ShouldSuppressSubtitleStreams);
        Assert.False(rewrite.ShouldAppendSubtitlesToProfileFilter);
    }

    [Fact]
    public void CustomGrayscaleGraphStartsFromASingleAllViewInput()
    {
        var rewrite = _builder.BuildCustomGrayscaleAnaglyph(new RgbColor(255, 0, 0), new RgbColor(0, 255, 255));

        // One all-view input label means one decode: the eyes are cropped out of the
        // shared native SBS frames, not decoded separately.
        Assert.NotNull(rewrite.FilterComplex);
        var graph = rewrite.FilterComplex!;
        var allViewInputs = graph
            .Split("0:v:view:all", StringSplitOptions.None)
            .Length - 1;

        Assert.Equal(1, allViewInputs);
        Assert.StartsWith("[0:v:view:all]", rewrite.FilterComplex);
    }

    [Fact]
    public void CustomGrayscaleGraphGrayscalesEachEyeAndScreenCombinesThem()
    {
        var rewrite = _builder.BuildCustomGrayscaleAnaglyph(new RgbColor(17, 34, 51), new RgbColor(128, 64, 255));

        Assert.NotNull(rewrite.FilterComplex);
        var graph = rewrite.FilterComplex!;
        var grayscaleStages = graph.Split("format=gray,", StringSplitOptions.None).Length - 1;
        var eyeCrops = graph
            .Split("crop=iw/2:ih:", StringSplitOptions.None)
            .Length - 1;

        Assert.Equal(2, grayscaleStages);
        Assert.Equal(2, eyeCrops);
        Assert.Contains("blend=all_mode=screen", rewrite.FilterComplex);
        Assert.EndsWith(",format=yuv420p[anaglyfin_custom]", rewrite.FilterComplex);
    }

    [Fact]
    public void CustomGrayscaleTintCoefficientsAreNormalisedColourComponents()
    {
        var rewrite = _builder.BuildCustomGrayscaleAnaglyph(new RgbColor(17, 34, 51), new RgbColor(128, 64, 255));

        // 17/255, 34/255, 51/255 and 128/255, 64/255, 255/255, invariant and rounded
        // to six decimals - never a colour string from settings or a client.
        Assert.Contains("colorchannelmixer=rr=0.066667:gg=0.133333:bb=0.2", rewrite.FilterComplex);
        Assert.Contains("colorchannelmixer=rr=0.501961:gg=0.25098:bb=1", rewrite.FilterComplex);
    }

    [Fact]
    public void CustomGrayscaleProfileWithoutEyeColorsFailsTheGenericBuild()
    {
        var tampered = new StereoProfile
        {
            Id = ProfileIds.CustomGrayscale,
            DisplayName = "Tampered",
            Kind = ProfileKind.CustomGrayscaleAnaglyph
        };

        Assert.Throws<InvalidOperationException>(() => _builder.Build(tampered));
    }

    [Fact]
    public void CustomGrayscaleBuildViaCatalogMatchesTheExplicitBuild()
    {
        var profile = Catalog.GetProfile(ProfileIds.CustomGrayscale);

        var generic = _builder.Build(profile);
        var explicitBuild = _builder.BuildCustomGrayscaleAnaglyph(
            ProfileCatalog.DefaultCustomLeftEyeColor,
            ProfileCatalog.DefaultCustomRightEyeColor);

        Assert.Equal(explicitBuild.InsertArguments, generic.InsertArguments);
    }

    // ----- Subtitle burn-in ----------------------------------------------------------

    [Fact]
    public void BurnInIsAppendedAfterTheProfileFilterChain()
    {
        var rewrite = _builder.BuildSideBySideHalf(new SubtitleBurnIn(MoviePath, 1));

        Assert.Equal(
            new[]
            {
                "-map",
                "0:v:view:all",
                "-vf",
                "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p,subtitles=filename='" + MoviePath + "':si=1"
            },
            rewrite.InsertArguments);

        // VideoFilter stays the conversion itself; the merge is visible in the args.
        Assert.Equal("scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p", rewrite.VideoFilter);
        Assert.Equal("subtitles=filename='" + MoviePath + "':si=1", rewrite.SubtitleFilter);
        Assert.True(rewrite.ShouldAppendSubtitlesToProfileFilter);
        Assert.True(rewrite.ShouldSuppressSubtitleStreams);
    }

    [Fact]
    public void BurnInBecomesTheWholeFilterChainWhenTheProfileFiltersNothing()
    {
        var rewrite = _builder.BuildSideBySideFull(BurnIn);

        Assert.Equal(
            new[] { "-map", "0:v:view:all", "-vf", "subtitles=filename='" + MoviePath + "':si=0" },
            rewrite.InsertArguments);
        Assert.Null(rewrite.VideoFilter);
        Assert.False(rewrite.ShouldAppendSubtitlesToProfileFilter);
    }

    [Fact]
    public void BurnInFollowsTheMappedGraphForCustomGrayscale()
    {
        var rewrite = _builder.BuildCustomGrayscaleAnaglyph(
            new RgbColor(255, 0, 0),
            new RgbColor(0, 255, 255),
            new SubtitleBurnIn(MoviePath, 3));

        Assert.Equal("subtitles=filename='" + MoviePath + "':si=3", rewrite.SubtitleFilter);
        Assert.False(rewrite.ShouldAppendSubtitlesToProfileFilter);
        Assert.DoesNotContain("subtitles", rewrite.FilterComplex);

        // Argument order: graph, map of its label, then the burn-in as its own stage.
        var arguments = rewrite.InsertArguments.ToList();
        var mappedOutput = arguments.IndexOf("-map");
        var burnInStage = arguments.IndexOf("-vf");
        Assert.True(mappedOutput >= 0 && mappedOutput < burnInStage);
    }

    [Theory]
    [InlineData("/movies/Movie (2010)/Movie.2010.3D.mkv", @"subtitles=filename='/movies/Movie (2010)/Movie.2010.3D.mkv':si=0")]
    [InlineData("/movies/a=b/film.mkv", @"subtitles=filename='/movies/a=b/film.mkv':si=0")]
    [InlineData("/data/dirs:with colon/film.mkv", @"subtitles=filename='/data/dirs'\\:'with colon/film.mkv':si=0")]
    [InlineData("/movies/It's Here (2010)/film.mkv", @"subtitles=filename='/movies/It'\\\''s Here (2010)/film.mkv':si=0")]
    [InlineData("/data/dirs:with[brackets];and,commas/film.mkv", @"subtitles=filename='/data/dirs'\\:'with'\['brackets'\]\;'and'\,'commas/film.mkv':si=0")]
    public void BurnInEscapesThePluginProvidedPathWithoutChangingIt(string path, string expectedFilter)
    {
        var rewrite = _builder.BuildSideBySideFull(new SubtitleBurnIn(path, 0));

        // A colon carries two backslashes where a bracket carries one: both parser passes
        // consume a colon, so it is escaped once per pass, while only the filtergraph
        // pass looks at a bracket. Dropping either backslash changes what FFmpeg opens.
        //
        // What the emitted value means - that FFmpeg reads every one of these paths back
        // unchanged, ordinal and all - is asserted by decoding them in
        // SubtitleFilterEscapingTests against the parser of the pinned FFmpeg-mvc tree.
        Assert.Equal(expectedFilter, rewrite.SubtitleFilter);
    }

    [Theory]
    [InlineData("relative/Movie.mkv")]
    [InlineData("Movie.mkv")]
    [InlineData("")]
    [InlineData("   ")]
    public void BurnInRejectsPathsThatCannotNameThePluginProvidedFile(string path)
    {
        Assert.Throws<ArgumentException>(() => new SubtitleBurnIn(path, 0));
    }

    [Fact]
    public void BurnInRejectsControlCharactersInPaths()
    {
        Assert.Throws<ArgumentException>(() => new SubtitleBurnIn("/movies/Movie.mkv\n-map 0:v:0", 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-42)]
    public void BurnInRejectsNegativeSubtitleOrdinals(int ordinal)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubtitleBurnIn(MoviePath, ordinal));
    }

    [Fact]
    public void BurnInIsSkippedForProfilesThatDoNotSupportIt()
    {
        var profile = new StereoProfile
        {
            Id = ProfileIds.CustomGrayscale,
            DisplayName = "No burn-in",
            Kind = ProfileKind.CustomGrayscaleAnaglyph,
            LeftEyeColor = new RgbColor(255, 0, 0),
            RightEyeColor = new RgbColor(0, 255, 255),
            SupportsSubtitleBurnIn = false
        };

        var rewrite = _builder.Build(profile, BurnIn);

        Assert.Null(rewrite.SubtitleFilter);
        Assert.DoesNotContain("subtitles", string.Join(" ", rewrite.InsertArguments));
    }

    // ----- Profile id allowlist ----------------------------------------------------------

    [Theory]
    [InlineData("not_a_profile")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sbs_full;scale=7680:ih")]
    [InlineData("subtitles=/movies/film.mkv:si=0")]
    [InlineData("anaglyph_zzzz")]
    [InlineData("../../etc/passwd")]
    public void UnknownProfileIdIsRejectedBeforeAnyArgumentIsBuilt(string profileId)
    {
        // A StereoProfile is a public shape: settings that survived a bad edit, or a
        // hand-built profile, can carry any string as the id while still naming a kind
        // this builder knows. The kind alone must never be enough to reach FFmpeg.
        var stranger = new StereoProfile
        {
            Id = profileId,
            DisplayName = "Handed in by a caller",
            Kind = ProfileKind.SideBySideFull
        };

        var exception = Assert.Throws<ArgumentException>(() => _builder.Build(stranger));

        Assert.Equal("profile", exception.ParamName);
        Assert.Contains("allowlist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownProfileIdIsRejectedForEveryProfileKindIncludingOneFromTheFuture()
    {
        var kinds = new[]
        {
            ProfileKind.TwoDimensional,
            ProfileKind.SideBySideFull,
            ProfileKind.SideBySideHalf,
            ProfileKind.Stereo3DAnaglyph,
            ProfileKind.CustomGrayscaleAnaglyph,
            (ProfileKind)99
        };

        foreach (var kind in kinds)
        {
            var nearMiss = new StereoProfile
            {
                Id = ProfileIds.CustomGrayscale + "_typo",
                DisplayName = "Near miss",
                Kind = kind,
                Stereo3DOutputCode = kind == ProfileKind.Stereo3DAnaglyph ? "arcd" : null,
                LeftEyeColor = kind == ProfileKind.CustomGrayscaleAnaglyph ? new RgbColor(255, 0, 0) : null,
                RightEyeColor = kind == ProfileKind.CustomGrayscaleAnaglyph ? new RgbColor(0, 255, 255) : null
            };

            // The id is checked before the kind is dispatched on, so even a kind this
            // build does not know is reported as the unknown id that it is.
            Assert.Throws<ArgumentException>(() => _builder.Build(nearMiss));
        }
    }

    [Fact]
    public void KnownProfileIdIsCheckedTheWayTheAllowlistReadsIt()
    {
        var padded = new StereoProfile
        {
            Id = "  " + ProfileIds.SideBySideFull + "  ",
            DisplayName = "Padded",
            Kind = ProfileKind.SideBySideFull
        };

        Assert.Equal(ProfileIds.SideBySideFull, _builder.Build(padded).ProfileId);

        // The allowlist ignores case, so a caller that mangled the casing still gets a
        // command; the id comes back as handed over, only trimmed.
        var recased = new StereoProfile
        {
            Id = "SBS_Full",
            DisplayName = "Recased",
            Kind = ProfileKind.SideBySideFull
        };

        var rewrite = _builder.Build(recased);

        Assert.Equal("SBS_Full", rewrite.ProfileId);
        Assert.Equal(new[] { "-map", "0:v:view:all" }, rewrite.InsertArguments);
    }

    [Fact]
    public void EveryRewriteTheBuilderHandsOutCarriesAnAllowlistedProfileId()
    {
        var builtByHand = new[]
        {
            _builder.BuildTwoDimensionalBase(),
            _builder.BuildSideBySideFull(),
            _builder.BuildSideBySideHalf(),
            _builder.BuildStereo3DAnaglyph(ProfileIds.BuiltInAnaglyphOutputCodes[0]),
            _builder.BuildCustomGrayscaleAnaglyph(
                ProfileCatalog.DefaultCustomLeftEyeColor,
                ProfileCatalog.DefaultCustomRightEyeColor)
        };

        var fromCatalog = Catalog.Profiles.Select(profile => _builder.Build(profile, BurnIn));

        Assert.All(builtByHand.Concat(fromCatalog).ToList(), rewrite =>
            Assert.True(ProfileIds.IsAllowed(rewrite.ProfileId), rewrite.ProfileId));
    }

    // ----- Whole-catalog invariants ----------------------------------------------------

    [Fact]
    public void EveryCatalogProfileBuildsAndMatchesTheAllViewRequirement()
    {
        foreach (var profile in Catalog.Profiles)
        {
            var rewrite = _builder.Build(profile, BurnIn);

            // Subtitle suppression follows the all-view pivot: everything that
            // converts owns its subtitles; 2D keeps the stock pipeline.
            Assert.Equal(profile.RequiresAllViews, rewrite.ShouldSuppressSubtitleStreams);
            Assert.Equal(profile.Id, rewrite.ProfileId);
            Assert.Equal(profile.Kind, rewrite.Kind);

            if (profile.RequiresAllViews)
            {
                Assert.Contains(rewrite.InsertArguments, argument => argument == "0:v:view:all" || argument == "[anaglyfin_custom]");
            }
        }
    }

    [Fact]
    public void GeneratedArgumentsNeverAskForHardwareDecodingOrLegacyViewSelection()
    {
        foreach (var profile in Catalog.Profiles)
        {
            var rewrite = _builder.Build(profile, BurnIn);

            foreach (var argument in rewrite.InsertArguments)
            {
                Assert.DoesNotContain("hwaccel", argument);
                Assert.DoesNotContain("view_ids", argument);
            }
        }
    }

    [Fact]
    public void BuildingIsDeterministic()
    {
        foreach (var profile in Catalog.Profiles)
        {
            var first = _builder.Build(profile, BurnIn);
            var second = _builder.Build(profile, BurnIn);

            // Compared piece by piece: the argument lists are sequences, and the
            // record's own equality would compare them by reference.
            Assert.Equal(first.ProfileId, second.ProfileId);
            Assert.Equal(first.Kind, second.Kind);
            Assert.Equal(first.InsertArguments, second.InsertArguments);
            Assert.Equal(first.VideoMap, second.VideoMap);
            Assert.Equal(first.VideoFilter, second.VideoFilter);
            Assert.Equal(first.FilterComplex, second.FilterComplex);
            Assert.Equal(first.SubtitleFilter, second.SubtitleFilter);
            Assert.Equal(first.ShouldSuppressSubtitleStreams, second.ShouldSuppressSubtitleStreams);
            Assert.Equal(first.ShouldAppendSubtitlesToProfileFilter, second.ShouldAppendSubtitlesToProfileFilter);
        }
    }

    [Fact]
    public void BuildRejectsAMissingProfile()
    {
        Assert.Throws<ArgumentNullException>(() => _builder.Build(null!));
    }

    [Fact]
    public void UnknownProfileKindIsRejected()
    {
        var future = new StereoProfile
        {
            Id = ProfileIds.SideBySideFull,
            DisplayName = "From the future",
            Kind = (ProfileKind)99
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => _builder.Build(future));
    }
}
