using System;
using System.Collections.Generic;
using Anaglyfin.Ffmpeg;

namespace Anaglyfin.Tests.Ffmpeg;

/// <summary>
/// A fake FFmpeg command assembler used to prove how a <see cref="ProfileRewrite"/>
/// composes with the rest of a transcode command.
/// </summary>
/// <remarks>
/// <para>
/// The production caller of a rewrite is the T5 FFmpeg wrapper, which is out of scope
/// here. This collector plays that role in tests: it holds a Jellyfin-shaped base
/// command, splices a rewrite's <see cref="ProfileRewrite.InsertArguments"/> in front
/// of the output argument, and honours the rewrite's flags - dropping the mapped
/// subtitle streams and adding <c>-sn</c> when
/// <see cref="ProfileRewrite.ShouldSuppressSubtitleStreams"/> is set. That lets the
/// tests assert the composed command, including the invariant that a profile
/// playback stays one process with one decode input.
/// </para>
/// <para>
/// Nothing here runs FFmpeg; the collector only builds argument lists.
/// </para>
/// </remarks>
public sealed class FakeFfmpegCommandCollector
{
    private readonly List<string> _arguments = new();

    /// <summary>
    /// Starts from a Jellyfin-shaped VOD/HLS command that still carries a mapped
    /// subtitle stream, the situation a profile rewrite must defuse.
    /// </summary>
    /// <param name="inputPath">The transcode input file.</param>
    /// <param name="outputPath">The HLS playlist output, kept as the last argument.</param>
    /// <returns>This collector, reset to the base command.</returns>
    public FakeFfmpegCommandCollector WithJellyfinStyleBase(string inputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        _arguments.Clear();
        _arguments.AddRange(new[]
        {
            "ffmpeg",
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i",
            inputPath,
            "-map",
            "0:a:0",
            "-map",
            "0:s:0",
            "-c:v",
            "libx264",
            "-preset",
            "medium",
            "-crf",
            "21",
            "-c:a",
            "aac",
            "-f",
            "hls",
            "-hls_time",
            "6",
            outputPath
        });

        return this;
    }

    /// <summary>
    /// Applies a profile rewrite: removes subtitle stream handling when the profile
    /// takes subtitles over, then inserts the profile arguments before the output.
    /// </summary>
    /// <param name="rewrite">The rewrite produced by the argument builder.</param>
    /// <returns>This collector.</returns>
    /// <exception cref="InvalidOperationException">No base command was started.</exception>
    public FakeFfmpegCommandCollector Apply(ProfileRewrite rewrite)
    {
        ArgumentNullException.ThrowIfNull(rewrite);

        if (_arguments.Count == 0)
        {
            throw new InvalidOperationException("Start from a base command before applying a rewrite.");
        }

        if (rewrite.ShouldSuppressSubtitleStreams)
        {
            for (var i = _arguments.Count - 2; i >= 0; i--)
            {
                if (string.Equals(_arguments[i], "-map", StringComparison.Ordinal)
                    && _arguments[i + 1].StartsWith("0:s:", StringComparison.Ordinal))
                {
                    _arguments.RemoveRange(i, 2);
                }
            }

            _arguments.Insert(_arguments.Count - 1, "-sn");
        }

        _arguments.InsertRange(_arguments.Count - 1, rewrite.InsertArguments);

        return this;
    }

    /// <summary>
    /// Gets the arguments assembled so far.
    /// </summary>
    public IReadOnlyList<string> Arguments => _arguments;

    /// <summary>
    /// Renders the command the way a process listing would show it.
    /// </summary>
    public string Render()
        => string.Join(" ", _arguments);

    /// <summary>
    /// Finds the index of a token, or -1 when absent.
    /// </summary>
    public int IndexOf(string token)
        => _arguments.IndexOf(token);
}
