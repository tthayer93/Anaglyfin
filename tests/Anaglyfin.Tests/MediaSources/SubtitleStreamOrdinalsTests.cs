using System;
using System.Collections.Generic;
using Anaglyfin.MediaSources;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Anaglyfin.Tests.MediaSources;

/// <summary>
/// Covers the mapping from a client-facing subtitle stream index to the burn-in
/// ordinal carried by profile markers: an ordinal among subtitle streams, never the
/// global stream index.
/// </summary>
public class SubtitleStreamOrdinalsTests
{
    // A typical layout: video and audio streams interleave with the subtitles, so the
    // global indices (0, 1, 2, 5, 9) and the subtitle ordinals (0, 1, 2) disagree.
    private static readonly IReadOnlyList<MediaStream> TypicalStreams = new[]
    {
        new MediaStream { Type = MediaStreamType.Video, Index = 0 },
        new MediaStream { Type = MediaStreamType.Audio, Index = 1 },
        new MediaStream { Type = MediaStreamType.Subtitle, Index = 2 },
        new MediaStream { Type = MediaStreamType.Subtitle, Index = 5 },
        new MediaStream { Type = MediaStreamType.Audio, Index = 7 },
        new MediaStream { Type = MediaStreamType.Subtitle, Index = 9 }
    };

    [Theory]
    [InlineData(2, 0)] // first subtitle track: the ordinal coincides with nothing else
    [InlineData(5, 1)] // global index 5 is the second subtitle stream
    [InlineData(9, 2)] // global index 9 is the third subtitle stream
    public void MapsGlobalSubtitleIndicesToOrdinalsAmongSubtitleStreams(int globalIndex, int expectedOrdinal)
    {
        Assert.True(SubtitleStreamOrdinals.TryGetBurnInOrdinal(TypicalStreams, globalIndex, out var ordinal));
        Assert.Equal(expectedOrdinal, ordinal);
    }

    [Fact]
    public void ListOrderDoesNotMatterOnlyStreamIndicesDo()
    {
        var shuffled = new List<MediaStream>(TypicalStreams);
        shuffled.Reverse();

        Assert.True(SubtitleStreamOrdinals.TryGetBurnInOrdinal(shuffled, 5, out var ordinal));
        Assert.Equal(1, ordinal);
    }

    [Fact]
    public void StreamsWithUnknownIndicesDoNotCount()
    {
        var streams = new[]
        {
            new MediaStream { Type = MediaStreamType.Subtitle, Index = -1 },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 3 },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 8 }
        };

        Assert.True(SubtitleStreamOrdinals.TryGetBurnInOrdinal(streams, 8, out var ordinal));
        Assert.Equal(1, ordinal);
    }

    [Theory]
    [InlineData(0)] // the video stream is not a subtitle
    [InlineData(1)] // the audio stream is not a subtitle
    [InlineData(4)] // no stream here at all
    [InlineData(-1)] // "unknown index" never selects a track
    [InlineData(-7)]
    public void AnythingThatIsNotAnEmbeddedSubtitleStreamDoesNotMap(int globalIndex)
    {
        Assert.False(SubtitleStreamOrdinals.TryGetBurnInOrdinal(TypicalStreams, globalIndex, out var ordinal));
        Assert.Equal(0, ordinal);
    }

    [Fact]
    public void MissingStreamListsDoNotMap()
    {
        Assert.False(SubtitleStreamOrdinals.TryGetBurnInOrdinal(null, 2, out _));
        Assert.False(SubtitleStreamOrdinals.TryGetBurnInOrdinal(Array.Empty<MediaStream>(), 2, out _));
    }

    [Fact]
    public void NullEntriesAreSkippedRatherThanFatal()
    {
        var streams = new List<MediaStream>
        {
            null!, // hostile list content must not take the mapping down
            new() { Type = MediaStreamType.Subtitle, Index = 4 },
            new() { Type = MediaStreamType.Subtitle, Index = 6 }
        };

        Assert.True(SubtitleStreamOrdinals.TryGetBurnInOrdinal(streams, 6, out var ordinal));
        Assert.Equal(1, ordinal);
    }
}
