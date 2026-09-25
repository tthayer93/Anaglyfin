using System;
using System.Collections.Generic;
using System.Linq;
using Anaglyfin.FFmpegWrapper;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Token-level tests for the marker-route audio compatibility map: which vectors it touches,
/// what exactly the touched tokens become, and which vectors it must not touch at all.
/// </summary>
/// <remarks>
/// <para>
/// The map is a fixed table over a fixed set of spellings, so every case here states the whole
/// received vector and the whole expected one. A sanitizer that rewrote a neighbouring token
/// would break an audio command the server was entitled to run, and only a complete-vector
/// expectation notices that class of mistake - a fragment assertion would not.
/// </para>
/// <para>
/// Vectors are written as the token strings FFmpeg would see, in the position they occupy on a
/// Jellyfin HLS command: the audio selection after the server's video encoder, the output file
/// last. Nothing here runs FFmpeg or asks a binary anything - the whole point of the component
/// is that its answer is a table, so the table is what is asserted.
/// </para>
/// </remarks>
public static class MarkerAudioCompatibilityTests
{
    // ----- the trigger and the mapping ------------------------------------------------

    [Fact]
    public static void TheReportedFdkVectorBecomesNativeAacWithTheMappedBitrate()
    {
        // The exact shape behind the v0.2.0 3D failures: Jellyfin's builder, an official build
        // that answered the fdk probe, and a stereo command. The vbr pair is gone, the codec is
        // native, and the bitrate the command never had now stands where the level did.
        var arguments = Tokens("-codec:a:0 libfdk_aac -ac 2 -vbr:a 4");

        var result = MarkerAudioCompatibility.Sanitize(arguments);

        Assert.True(result.Changed);
        Assert.Equal(Tokens("-codec:a:0 aac -ac 2 -b:a 128000"), result.Arguments);
        Assert.NotNull(result.Notice);
        Assert.Contains("-b:a 128000", result.Notice!, StringComparison.Ordinal);
    }

    [Theory]
    // Jellyfin 12's own spelling and its stream-indexed vbr.
    [InlineData("-c:a:0 libfdk_aac -vbr:a:0 5", "-c:a:0 aac -b:a 192000")]
    // The audio-codec alias: audio by definition, no specifier anywhere.
    [InlineData("-acodec libfdk_aac -vbr:a 1", "-acodec aac -b:a 48000")]
    // The short codec option with a plain audio specifier.
    [InlineData("-c:a libfdk_aac -vbr:a 2", "-c:a aac -b:a 64000")]
    // Bare -codec / -c carrying the audio-only codec: the value scopes what the option means.
    [InlineData("-codec libfdk_aac -vbr:a 3", "-codec aac -b:a 96000")]
    [InlineData("-c libfdk_aac -vbr:a 4", "-c aac -b:a 128000")]
    // A second indexed audio specifier spelling, to pin that indexes are read and not guessed.
    [InlineData("-codec:a:1 libfdk_aac -vbr:a:1 4", "-codec:a:1 aac -b:a 128000")]
    public static void EveryAudioScopedSpellingOfTheSelectionIsSanitized(string received, string expected)
    {
        var result = MarkerAudioCompatibility.Sanitize(Tokens(received));

        Assert.True(result.Changed);
        Assert.Equal(Tokens(expected), result.Arguments);
    }

    [Theory]
    // The documented per-channel floors of fdk's VBR modes, times the channel count: Jellyfin's
    // own thresholds, floor side, so the synthesis can never out-authorise the server.
    [InlineData(1, "48000", "144000")]
    [InlineData(2, "64000", "192000")]
    [InlineData(3, "96000", "288000")]
    [InlineData(4, "128000", "384000")]
    [InlineData(5, "192000", "576000")]
    public static void EachVbrLevelMapsToItsFloorForTheCommandChannelCount(int level, string stereoBits, string surroundBits)
    {
        var stereo = MarkerAudioCompatibility.Sanitize(
            Tokens($"-codec:a:0 libfdk_aac -ac 2 -vbr:a {level} -c:v libx264 out.m3u8"));

        Assert.True(stereo.Changed);
        Assert.Equal(stereoBits, BitrateAfter(stereo.Arguments));

        var surround = MarkerAudioCompatibility.Sanitize(
            Tokens($"-codec:a:0 libfdk_aac -ac 6 -vbr:a {level} -c:v libx264 out.m3u8"));

        Assert.True(surround.Changed);
        Assert.Equal(surroundBits, BitrateAfter(surround.Arguments));
    }

    [Fact]
    public static void ACommandWithoutAcDefaultsToStereoWhenTheBitrateIsSynthesized()
    {
        var result = MarkerAudioCompatibility.Sanitize(Tokens("-c:a libfdk_aac -vbr:a 4"));

        Assert.True(result.Changed);
        Assert.Equal(Tokens("-c:a aac -b:a 128000"), result.Arguments);
    }

    [Fact]
    public static void TheSynthesizedBitrateIsInsertedWhereTheVbrLevelStoodNotAfterTheOutput()
    {
        // FFmpeg binds an output option to the file it opens next; a bitrate appended behind
        // "out.m3u8" would be a bitrate for a file that does not exist. The removed level's own
        // slot is the only position that keeps the server's option-to-file binding.
        var result = MarkerAudioCompatibility.Sanitize(
            Tokens("-c:v libx264 -c:a libfdk_aac -vbr:a 4 -t 60 -f hls out.m3u8"));

        Assert.True(result.Changed);
        Assert.Equal(Tokens("-c:v libx264 -c:a aac -b:a 128000 -t 60 -f hls out.m3u8"), result.Arguments);
    }

    [Fact]
    public static void EveryVbrPairIsRemovedAndOnlyOneBitrateIsSynthesizedAtTheFirstSlot()
    {
        var result = MarkerAudioCompatibility.Sanitize(
            Tokens("-c:a libfdk_aac -vbr:a 4 -map 0:1 -vbr:a:0 5 out.m3u8"));

        Assert.True(result.Changed);
        Assert.DoesNotContain("vbr", string.Join(' ', result.Arguments), StringComparison.Ordinal);
        Assert.Equal(Tokens("-c:a aac -b:a 128000 -map 0:1 out.m3u8"), result.Arguments);
    }

    [Fact]
    public static void EveryFdkSelectionOnTheCommandBecomesNativeAac()
    {
        var result = MarkerAudioCompatibility.Sanitize(
            Tokens("-c:a libfdk_aac -t 60 -acodec libfdk_aac -vbr:a 4 out.m3u8"));

        Assert.True(result.Changed);
        Assert.Equal(Tokens("-c:a aac -t 60 -acodec aac -b:a 128000 out.m3u8"), result.Arguments);
    }

    // ----- an existing audio bitrate is never out-authorised -----------------------------

    [Theory]
    // All three spellings of "the server already stated an audio bitrate": the audio-scoped
    // generic option, the CLI alias that sets exactly b:a, and the bare generic bitrate - each
    // of which native aac accepts unmodified.
    [InlineData("-c:a libfdk_aac -b:a 128k -vbr:a 4", "-c:a aac -b:a 128k")]
    [InlineData("-c:a libfdk_aac -ab 192000 -vbr:a 4", "-c:a aac -ab 192000")]
    [InlineData("-c:a libfdk_aac -b 160000 -vbr:a 4", "-c:a aac -b 160000")]
    [InlineData("-codec:a:0 libfdk_aac -b:a:0 96k -vbr:a:0 3", "-codec:a:0 aac -b:a:0 96k")]
    public static void AnExistingAudioBitrateSuppressesTheSynthesizedOne(string received, string expected)
    {
        // The codec is still rewritten and the option the build cannot parse is still removed -
        // suppressing the synthesis is not suppressing the whole fix.
        var result = MarkerAudioCompatibility.Sanitize(Tokens(received));

        Assert.True(result.Changed);
        Assert.Equal(Tokens(expected), result.Arguments);
        Assert.Contains("keeping the audio bitrate", result.Notice!, StringComparison.Ordinal);
    }

    [Fact]
    public static void AVideoBitrateIsNotAnAudioBitrateAndDoesNotSuppressTheSynthesis()
    {
        var result = MarkerAudioCompatibility.Sanitize(
            Tokens("-c:v libx264 -b:v 4000k -c:a libfdk_aac -vbr:a 4 out.m3u8"));

        Assert.True(result.Changed);
        Assert.Equal(Tokens("-c:v libx264 -b:v 4000k -c:a aac -b:a 128000 out.m3u8"), result.Arguments);
    }

    // ----- commands this map must not touch ------------------------------------------------

    [Theory]
    // Native aac at a stated bitrate: a command that already fits the minimal build.
    [InlineData("-c:a aac -b:a 192k -ac 2")]
    // libopus carries its own vbr, which is a real option on a real build: without fdk on the
    // command there is nothing to trigger, and opus keeps its quality setting.
    [InlineData("-codec:a libopus -vbr:a 1 -b:a 128k")]
    // An audio vbr with no fdk selection anywhere: not ours to remove.
    [InlineData("-c:a copy -vbr:a 4 out.m3u8")]
    // fdk named for video: the specifier says this command is not Jellyfin's audio selection,
    // and this map never guesses across specifiers.
    [InlineData("-c:v libfdk_aac -vbr:a 4 out.m3u8")]
    // A numbered specifier names a stream, not a type; only a specifier denoting audio does.
    [InlineData("-c:1 libfdk_aac -vbr:a 4 out.m3u8")]
    // A subtitle-scoped selection, for the same reason in the other direction.
    [InlineData("-c:s libfdk_aac -vbr:a 4 out.m3u8")]
    // An audio-scoped vbr on a video-scoped selection: still no trigger.
    [InlineData("-codec libx264 -vbr:a 4 out.m3u8")]
    public static void AVectorWithoutTheFdkAudioSelectionIsReturnedExactlyAsReceived(string received)
    {
        var arguments = Tokens(received);

        var result = MarkerAudioCompatibility.Sanitize(arguments);

        Assert.False(result.Changed);
        Assert.Null(result.Notice);

        // Byte-for-byte: the very same vector, so "unchanged" cannot hide a copy that fixed
        // something nobody asked it to fix.
        Assert.Same(arguments, result.Arguments);
    }

    // ----- malformed input cannot crash what it cannot satisfy ------------------------------

    [Theory]
    // A level below the range: fdk's own encoder clamps it up to 1, and so does the map.
    [InlineData("-c:a libfdk_aac -ac 2 -vbr:a 0", "-c:a aac -ac 2 -b:a 48000")]
    // A level above the range clamps down to 5, the same clip.
    [InlineData("-c:a libfdk_aac -ac 2 -vbr:a 9", "-c:a aac -ac 2 -b:a 192000")]
    // An absurd channel count is no channel count at all; the stereo default answers.
    [InlineData("-c:a libfdk_aac -ac 4000 -vbr:a 4", "-c:a aac -ac 4000 -b:a 128000")]
    public static void OutOfRangeLevelsAndChannelsAreClampedToTheTableNotToAFailure(string received, string expected)
    {
        var result = MarkerAudioCompatibility.Sanitize(Tokens(received));

        Assert.True(result.Changed);
        Assert.Equal(Tokens(expected), result.Arguments);
    }

    [Theory]
    // A level that is not a number maps to no bitrate: the option still goes, because the
    // minimal build cannot parse it whatever it says, and the codec still becomes native.
    [InlineData("-c:a libfdk_aac -vbr:a abc out.m3u8", "-c:a aac out.m3u8")]
    // The option is the last token on the command: it carried no value, so nothing is eaten
    // along with it, and there is nothing after it to eat anyway.
    [InlineData("-c:a libfdk_aac -vbr:a", "-c:a aac")]
    // The option is followed by another option, so the command never stated a level here.
    // Dropping the next token would eat an argument that is not fdk's - the -f stays.
    [InlineData("-c:a libfdk_aac -vbr:a -f hls out.m3u8", "-c:a aac -f hls out.m3u8")]
    public static void AMalformedVbrOptionIsRemovedWithoutInventingABitrateOrThrowing(string received, string expected)
    {
        var result = MarkerAudioCompatibility.Sanitize(Tokens(received));

        Assert.True(result.Changed);
        Assert.Equal(Tokens(expected), result.Arguments);
        Assert.Contains("default bitrate", result.Notice!, StringComparison.Ordinal);
    }

    [Fact]
    public static void ANullVectorIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => MarkerAudioCompatibility.Sanitize(null!));
    }

    // ----- the outcome seam ---------------------------------------------------------------

    [Fact]
    public static void APassedThroughOutcomeIsHandedBackAsTheSameOutcome()
    {
        // The dispatch must never be able to stamp a pass-through, fdk tokens and all: the
        // probe vectors live here, and their answer is the server's own binary's business.
        var arguments = Tokens("-hide_banner -i probe.mkv -c:a libfdk_aac -vbr:a 4 -f null -");
        var passed = WrapperRewriteResult.PassedThrough(arguments);

        var result = MarkerAudioCompatibility.ApplyTo(passed);

        Assert.Same(passed, result);
        Assert.Equal(arguments, result.Arguments);
    }

    [Fact]
    public static void ARewriteWithoutTheTriggerIsHandedBackAsTheSameOutcome()
    {
        var arguments = Tokens("-view_ids -1 -i /media/Movie.mkv -c:a aac -b:a 192k out.m3u8");
        var rewritten = WrapperRewriteResult.Rewritten(arguments, "sbs_full");

        Assert.Same(rewritten, MarkerAudioCompatibility.ApplyTo(rewritten));
    }

    [Fact]
    public static void ARewriteWithTheTriggerComesBackSanitizedWithTheNoticeAsItsFirstWarning()
    {
        var arguments = Tokens("-view_ids -1 -i /media/Movie.mkv -c:a libfdk_aac -ac 2 -vbr:a 4 out.m3u8");
        var rewritten = WrapperRewriteResult.Rewritten(
            arguments, "sbs_full", new[] { "an existing decline" });

        var result = MarkerAudioCompatibility.ApplyTo(rewritten);

        Assert.NotSame(rewritten, result);
        Assert.Equal(WrapperRewriteStatus.Rewritten, result.Status);
        Assert.Equal("sbs_full", result.ProfileId);
        Assert.Equal(
            Tokens("-view_ids -1 -i /media/Movie.mkv -c:a aac -ac 2 -b:a 128000 out.m3u8"),
            result.Arguments);

        // The notice leads the warnings: it names a change the command did undergo, and the
        // declines that follow name what it did not.
        Assert.Equal(2, result.Warnings.Count);
        Assert.StartsWith("the FFmpeg-mvc build", result.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("native aac", result.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("128000", result.Warnings[0], StringComparison.Ordinal);
        Assert.Equal("an existing decline", result.Warnings[1]);
    }

    [Fact]
    public static void TheNoticeNeverEchoesTextFromTheCommand()
    {
        // A wrapper diagnostic reaches the server log, and a command line arrives from a
        // playback request: the notice names this component's own numbers, not the request.
        var result = MarkerAudioCompatibility.Sanitize(
            Tokens("-c:a libfdk_aac -ac 6 -vbr:a 4 /media/SomeoneElses/Secret_Title.mkv"));

        Assert.NotNull(result.Notice);
        Assert.DoesNotContain("Secret_Title", result.Notice!, StringComparison.Ordinal);
        Assert.DoesNotContain("SomeoneElses", result.Notice!, StringComparison.Ordinal);
        Assert.DoesNotContain("/media", result.Notice!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a written-out vector as the token list FFmpeg would see.
    /// </summary>
    private static IReadOnlyList<string> Tokens(string tokens)
        => tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Reads the value of the first <c>-b:a</c> on a sanitized vector, which every test that
    /// asserts a synthesized bitrate states exactly one of.
    /// </summary>
    private static string BitrateAfter(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().IndexOf(MarkerAudioCompatibility.NativeBitrateArgument);
        Assert.True(index >= 0, $"no {MarkerAudioCompatibility.NativeBitrateArgument} on: {string.Join(' ', arguments)}");
        return arguments[index + 1];
    }
}
