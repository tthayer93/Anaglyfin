using System;
using System.Collections.Generic;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.Markers;
using Anaglyfin.Profiles;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Contract tests for the rewrite result the wrapper launcher consumes: which outcomes
/// may be executed, what a refusal hands back, and what a result is allowed to say out
/// loud when it is logged.
/// </summary>
public class WrapperRewriteResultTests
{
    private const string SourcePath = "/movies/Movie (2010)/Movie.2010.3D.mkv";

    private static readonly string[] Command = { "-i", SourcePath, "playlist.m3u8" };

    [Theory]
    [InlineData(WrapperRewriteStatus.PassedThrough, true)]
    [InlineData(WrapperRewriteStatus.Rewritten, true)]
    [InlineData(WrapperRewriteStatus.RejectedMarker, false)]
    [InlineData(WrapperRewriteStatus.UnknownProfile, false)]
    [InlineData(WrapperRewriteStatus.IncompatibleFilterGraph, false)]
    [InlineData(WrapperRewriteStatus.ServerChoseVideoCopy, false)]
    [InlineData(WrapperRewriteStatus.UnsupportedCommandShape, false)]
    public void OnlyPassThroughAndRewriteMayBeExecuted(WrapperRewriteStatus status, bool executable)
    {
        var result = executable
            ? status == WrapperRewriteStatus.PassedThrough
                ? WrapperRewriteResult.PassedThrough(Command)
                : WrapperRewriteResult.Rewritten(Command, ProfileIds.SideBySideFull)
            : WrapperRewriteResult.Failure(status, "fixed refusal text");

        Assert.Equal(status, result.Status);
        Assert.Equal(executable, result.IsSuccess);
    }

    [Fact]
    public void ARefusalCarriesNothingThatCouldBeExecuted()
    {
        var result = WrapperRewriteResult.Failure(
            WrapperRewriteStatus.RejectedMarker,
            "The marker carries no source parameter.",
            MarkerParseStatus.MissingSource);

        Assert.Empty(result.Arguments);
        Assert.Null(result.ProfileId);
        Assert.Equal(MarkerParseStatus.MissingSource, result.MarkerStatus);
    }

    [Theory]
    [InlineData(WrapperRewriteStatus.PassedThrough)]
    [InlineData(WrapperRewriteStatus.Rewritten)]
    public void ASuccessStatusCannotBeBuiltAsAFailure(WrapperRewriteStatus status)
    {
        Assert.Throws<ArgumentException>(() => WrapperRewriteResult.Failure(status, "not a refusal"));
    }

    [Fact]
    public void ARefusalHasToSayWhy()
    {
        Assert.Throws<ArgumentException>(() => WrapperRewriteResult.Failure(WrapperRewriteStatus.UnknownProfile, "   "));
    }

    [Fact]
    public void ARewriteHasToSayWhichProfileItApplied()
    {
        Assert.Throws<ArgumentException>(() => WrapperRewriteResult.Rewritten(Command, string.Empty));
    }

    [Fact]
    public void ThePassThroughSnapshotSurvivesTheCallerEmptyingItsOwnList()
    {
        var arguments = new List<string>(Command);

        var result = WrapperRewriteResult.PassedThrough(arguments);
        arguments.Clear();

        Assert.Equal(Command, result.Arguments);
    }

    [Fact]
    public void LoggingAResultDoesNotPutTheCommandLineInTheLog()
    {
        var rewritten = WrapperRewriteResult.Rewritten(Command, ProfileIds.SideBySideFull);
        var refusal = WrapperRewriteResult.Failure(
            WrapperRewriteStatus.RejectedMarker,
            "The marker carries no source parameter.",
            MarkerParseStatus.MissingSource);

        // A wrapper log line is written by the launcher straight from this object, and a
        // library path is not log material; a refusal has to be readable on its own.
        Assert.DoesNotContain(SourcePath, rewritten.ToString(), StringComparison.Ordinal);
        Assert.Contains(ProfileIds.SideBySideFull, rewritten.ToString(), StringComparison.Ordinal);
        Assert.Contains("RejectedMarker", refusal.ToString(), StringComparison.Ordinal);
        Assert.Contains("no source parameter", refusal.ToString(), StringComparison.Ordinal);
    }
}
