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
/// <remarks>
/// <para>
/// What the builder emits is the output side of a profile - filters, and for one profile a
/// graph plus the map of its label. The composed all-view picture the stereo profiles need is
/// an input-time request the caller writes in front of <c>-i</c>
/// (<see cref="FfmpegProfileArgumentBuilder.ComposedViewInputOption"/>), which is why it is
/// asserted here as a flag on the rewrite and never as an argument in the list, and why
/// nothing in these lists names a view at all.
/// </para>
/// </remarks>
public class FfmpegProfileArgumentBuilderTests
{
    private const string MoviePath = "/movies/Movie (2010)/Movie.2010.3D.mkv";

    /// <summary>The video stream index a caller names when it has one, used by the graph profile.</summary>
    private const int VideoStreamIndex = 0;

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

        // The base view IS this profile, so it must not be handed the composed picture -
        // that request would replace the 2D output with a double-width SBS frame.
        Assert.False(rewrite.RequiresComposedViewInput);

        // Nothing of the output belongs to it either, which is what keeps a server-side
        // filter graph or stream copy legal for this one profile.
        Assert.False(rewrite.OwnsVideoPipeline);
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
    public void FullSideBySideNeedsTheComposedInputAndCarriesNoArgumentOfItsOwn()
    {
        var rewrite = _builder.BuildSideBySideFull();

        Assert.Equal(ProfileIds.SideBySideFull, rewrite.ProfileId);

        // The composed frame IS full SBS: neither a re-tag nor a resize, and no map either -
        // the composed frames arrive on the stream the caller's own video map already names,
        // so this profile has nothing to add to the output segment.
        Assert.Empty(rewrite.InsertArguments);
        Assert.Null(rewrite.VideoMap);
        Assert.Null(rewrite.VideoFilter);
        Assert.Null(rewrite.FilterComplex);
        Assert.True(rewrite.ShouldSuppressSubtitleStreams);

        // What it does need is asked at the input, which the flags say rather than leaving a
        // caller to infer it from an argument list that is empty here.
        Assert.True(rewrite.RequiresComposedViewInput);
        Assert.True(rewrite.OwnsVideoPipeline);
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
            new[] { "-vf", "scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p" },
            rewrite.InsertArguments);

        // No map: the scale runs on the composed frames wherever the command maps them, and a
        // second video map beside the server's own would export two pictures.
        Assert.Null(rewrite.VideoMap);
        Assert.Equal("scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p", rewrite.VideoFilter);
        Assert.Null(rewrite.FilterComplex);
        Assert.True(rewrite.RequiresComposedViewInput);
        Assert.True(rewrite.OwnsVideoPipeline);
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
    public void HalfSideBySideIsDerivedFromTheComposedOutputNotSeparateEyeTranscodes()
    {
        var rewrite = _builder.BuildSideBySideHalf();

        // One composed decode (the flag, not a map), no filter_complex, no second input, and
        // real scaling - a stereo3d re-tag would keep the frame size and only halve the PAR.
        Assert.True(rewrite.RequiresComposedViewInput);
        Assert.Null(rewrite.FilterComplex);
        Assert.Contains("scale=iw/2:ih", rewrite.VideoFilter);
        Assert.DoesNotContain("stereo3d", string.Join(" ", rewrite.InsertArguments));

        // Two pins feeding an hstack is the other way to express this profile, and the one the
        // composed route replaced: no graph here means no second decode and no second pin.
        Assert.DoesNotContain("hstack", string.Join(" ", rewrite.InsertArguments));
    }

    // ----- Stereo3D anaglyph presets -----------------------------------------------

    [Theory]
    [MemberData(nameof(BuiltInAnaglyphCodes))]
    public void EachBuiltInAnaglyphPresetConvertsTheComposedFrameWithItsOfficialCode(string outputCode)
    {
        var rewrite = _builder.BuildStereo3DAnaglyph(outputCode);

        Assert.Equal(ProfileIds.BuildAnaglyphProfileId(outputCode), rewrite.ProfileId);
        Assert.Equal(
            new[] { "-vf", $"stereo3d=sbsl:{outputCode},format=yuv420p" },
            rewrite.InsertArguments);
        Assert.Null(rewrite.VideoMap);
        Assert.Equal($"stereo3d=sbsl:{outputCode},format=yuv420p", rewrite.VideoFilter);
        Assert.True(rewrite.ShouldSuppressSubtitleStreams);
        Assert.True(rewrite.RequiresComposedViewInput);
        Assert.True(rewrite.OwnsVideoPipeline);
    }

    [Fact]
    public void RedCyanDuboisPresetProducesTheDocumentedCommand()
    {
        var profile = Catalog.GetProfile(ProfileIds.AnaglyphRedCyanDubois);

        var rewrite = _builder.Build(profile);

        Assert.Equal(
            new[] { "-vf", "stereo3d=sbsl:arcd,format=yuv420p" },
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
            "[0:v]split=2[anaglyfin_cg_left_in][anaglyfin_cg_right_in];"
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
        Assert.Equal("[0:v]", rewrite.FilterComplexInput);
        Assert.True(rewrite.ShouldSuppressSubtitleStreams);
        Assert.False(rewrite.ShouldAppendSubtitlesToProfileFilter);
        Assert.True(rewrite.RequiresComposedViewInput);

        // The one profile with a map of its own, because its graph output is a new stream: the
        // label has to be named, and the stream it was made from has to be left out.
        Assert.True(rewrite.OwnsVideoPipeline);
    }

    [Fact]
    public void CustomGrayscaleGraphReadsTheVideoStreamTheCallerNamed()
    {
        var rewrite = _builder.BuildCustomGrayscaleAnaglyph(
            new RgbColor(255, 0, 0),
            new RgbColor(0, 255, 255),
            videoStreamIndex: 2);

        // The composed picture arrives on the source's own video stream, which the caller names
        // by index. Naming it is what stops the graph from reading every video stream the file
        // carries - an attached cover track would otherwise be a second, wrong input to the
        // split - and it is not a view selection, which is what the composed request forbids.
        Assert.StartsWith("[0:2]split=2", rewrite.FilterComplex, StringComparison.Ordinal);
        Assert.Equal("[0:2]", rewrite.FilterComplexInput);
        Assert.DoesNotContain("view", rewrite.FilterComplex, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomGrayscaleGraphFallsBackToTheVideoTypeWhenNoStreamWasNamed()
    {
        var rewrite = _builder.BuildCustomGrayscaleAnaglyph(
            new RgbColor(255, 0, 0),
            new RgbColor(0, 255, 255),
            videoStreamIndex: null);

        // Still no view specifier: the fallback addresses the stream type of the first input,
        // which is what the composed decode delivers.
        Assert.StartsWith("[0:v]split=2", rewrite.FilterComplex, StringComparison.Ordinal);
        Assert.DoesNotContain("view:", rewrite.FilterComplex, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomGrayscaleGraphStartsFromOneComposedInput()
    {
        var rewrite = _builder.BuildCustomGrayscaleAnaglyph(
            new RgbColor(255, 0, 0),
            new RgbColor(0, 255, 255),
            videoStreamIndex: 0);

        // One input label means one decode: the eyes are cropped out of the shared composed
        // frames, not decoded separately, and no second pin is opened by a view specifier.
        Assert.NotNull(rewrite.FilterComplex);
        var graph = rewrite.FilterComplex!;
        var sourceLabels = graph
            .Split("[0:0]", StringSplitOptions.None)
            .Length - 1;

        Assert.Equal(1, sourceLabels);
        Assert.StartsWith("[0:0]", rewrite.FilterComplex);
        Assert.DoesNotContain("hstack", graph, StringComparison.Ordinal);
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

        // Full SBS still converts nothing: the burn-in is the only -vf this profile writes,
        // and it is still the whole argument list, because the composed picture it draws on is
        // requested at the input and not here.
        Assert.Equal(
            new[] { "-vf", "subtitles=filename='" + MoviePath + "':si=0" },
            rewrite.InsertArguments);
        Assert.Null(rewrite.VideoFilter);
        Assert.Null(rewrite.VideoMap);
        Assert.False(rewrite.ShouldAppendSubtitlesToProfileFilter);
        Assert.True(rewrite.RequiresComposedViewInput);
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
        Assert.Empty(rewrite.InsertArguments);
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
    public void EveryCatalogProfileBuildsAndMatchesTheComposedViewRequirement()
    {
        foreach (var profile in Catalog.Profiles)
        {
            var rewrite = _builder.Build(profile, BurnIn);

            // Subtitle suppression, pipeline ownership and the composed input all follow the
            // same line: everything that converts owns its output and its decode; 2D keeps the
            // stock pipeline and the decoder's own base view.
            Assert.Equal(profile.RequiresAllViews, rewrite.ShouldSuppressSubtitleStreams);
            Assert.Equal(profile.RequiresAllViews, rewrite.RequiresComposedViewInput);
            Assert.Equal(profile.RequiresAllViews, rewrite.OwnsVideoPipeline);
            Assert.Equal(profile.Id, rewrite.ProfileId);
            Assert.Equal(profile.Kind, rewrite.Kind);

            // Only the graph profile maps anything: a graph output is a new stream that has to
            // be named. The linear conversions ride the caller's own video map.
            Assert.Equal(profile.Kind == ProfileKind.CustomGrayscaleAnaglyph, rewrite.VideoMap is not null);

            if (profile.RequiresAllViews)
            {
                // The composed picture is asked for in front of the input, which no argument in
                // this list can be.
                Assert.DoesNotContain(FfmpegProfileArgumentBuilder.ComposedViewInputOption, rewrite.InsertArguments);
            }
        }
    }

    [Fact]
    public void GeneratedArgumentsNeverAskForHardwareDecodingOrSelectAViewBySpecifier()
    {
        foreach (var profile in Catalog.Profiles)
        {
            var rewrite = _builder.Build(profile, BurnIn, videoStreamIndex: 0);
            var rendered = string.Join(' ', rewrite.InsertArguments);

            // MVC decode is software-only, and the encoder stays the caller's choice.
            Assert.DoesNotContain("hwaccel", rendered, StringComparison.Ordinal);

            // The composed request is an input option the caller writes before -i; nothing the
            // builder produces carries it, because everything it produces is output-side.
            Assert.DoesNotContain("view_ids", rendered, StringComparison.Ordinal);

            // The guard that matters for the composed decode: FFmpeg-mvc refuses a view
            // specifier on an input configured through -view_ids, so no generated argument may
            // contain one. This is asserted on the whole argument list rather than only on the
            // maps, because a filtergraph input label is spelled the same way.
            Assert.DoesNotContain("0:v:view", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("v:view:", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("vidx:", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("vpos:", rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheComposedViewRequestIsTheOneSpellingTheDecoderAccepts()
    {
        // The option and its value are stated by the builder so that the wrapper writes exactly
        // what the pinned FFmpeg-mvc documents as the composed route: view_ids set to the single
        // value -1, which is the whole view list assembled into one native SBS frame. Anything
        // else on the command line - any view specifier - is the other, refused way of asking.
        Assert.Equal("-view_ids", FfmpegProfileArgumentBuilder.ComposedViewInputOption);
        Assert.Equal("-1", FfmpegProfileArgumentBuilder.ComposedViewInputValue);
    }

    [Fact]
    public void BuildingIsDeterministic()
    {
        foreach (var profile in Catalog.Profiles)
        {
            var first = _builder.Build(profile, BurnIn, videoStreamIndex: 0);
            var second = _builder.Build(profile, BurnIn, videoStreamIndex: 0);

            // Compared piece by piece: the argument lists are sequences, and the
            // record's own equality would compare them by reference.
            Assert.Equal(first.ProfileId, second.ProfileId);
            Assert.Equal(first.Kind, second.Kind);
            Assert.Equal(first.InsertArguments, second.InsertArguments);
            Assert.Equal(first.VideoMap, second.VideoMap);
            Assert.Equal(first.VideoFilter, second.VideoFilter);
            Assert.Equal(first.FilterComplex, second.FilterComplex);
            Assert.Equal(first.FilterComplexInput, second.FilterComplexInput);
            Assert.Equal(first.SubtitleFilter, second.SubtitleFilter);
            Assert.Equal(first.OwnsVideoPipeline, second.OwnsVideoPipeline);
            Assert.Equal(first.RequiresComposedViewInput, second.RequiresComposedViewInput);
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
