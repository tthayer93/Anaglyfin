using System;
using System.Collections.Generic;
using System.Globalization;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Maps the one audio selection a two-binary deployment can put on a marker command and the
/// FFmpeg-mvc build cannot run - <c>libfdk_aac</c> and its private <c>vbr</c> option - onto the
/// native <c>aac</c> encoder, without probing anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>The inversion this answers.</b> A deployment that names <c>ANAGLYFIN_SERVER_FFMPEG</c>
/// hands the server's capability probes to the official FFmpeg build, and that build carries
/// <c>libfdk_aac</c>. Jellyfin 12 prefers fdk whenever its probe answers true, so it writes
/// <c>-codec:a:0 libfdk_aac</c> plus the encoder's private quality option - <c>-vbr:a &lt;n&gt;</c> -
/// into every transcode it composes, including the commands that carry an Anaglyfin marker. The
/// rewritten marker commands run on the minimal FFmpeg-mvc build, which has neither the encoder
/// nor the option: <c>vbr</c> is not a generic codec option, so FFmpeg's argument splitter dies
/// on it before any input is opened - <c>Unrecognized option 'vbr:a'</c>,
/// <c>Error splitting the argument list</c>, and no playback. Stripping only the codec name would
/// still die on the option, and stripping only the option would silently drop the audio to
/// native's default bitrate, because the fdk branch of Jellyfin's builder emits no
/// <c>-b:a</c>/<c>-ab</c> at all.
/// </para>
/// <para>
/// <b>The rule, and why it needs no probing.</b> The capability gap that matters is one fixed
/// codec with one fixed option, emitted in spellings FFmpeg itself fixes; a table answers it
/// deterministically where a probe would answer it slowly and a wrapper-probe would answer it
/// wrongly (a probe tells what a binary has, not what the server already committed to). On a
/// command this component is asked about:
/// </para>
/// <list type="number">
/// <item><description>
/// An audio-scoped codec selection (<c>-c</c>/<c>-codec</c>/<c>-acodec</c>, with an optional
/// specifier that denotes audio - <c>a</c>, <c>a:0</c>, …) whose value token is <c>libfdk_aac</c>
/// becomes <c>aac</c>. The specifier must denote audio; a numeric or other specifier, or a
/// non-audio type like <c>v</c>, is never touched. That the selection names audio at all is the
/// trigger for everything below - a command without it is returned byte-for-byte unchanged, which
/// is what keeps libopus's and NVENC's unrelated <c>vbr</c> options safe.
/// </description></item>
/// <item><description>
/// Every audio-scoped <c>vbr</c> option (<c>-vbr:a</c>, <c>-vbr:a:0</c>, …) is removed together
/// with its value token. A <c>vbr</c> that does not denote audio is left alone - this component
/// removes fdk's option, not anybody else's, and only once fdk has been proven on the command.
/// </description></item>
/// <item><description>
/// If the command already carries an audio bitrate - <c>-b:a</c>, <c>-ab</c>, or a bare
/// <c>-b</c> - nothing is added: that bitrate is the server's stated audio budget and survives.
/// Otherwise the removed VBR level is mapped through the fixed table below, times the command's
/// <c>-ac</c> channel count (defaulting to 2), and the result goes in as <c>-b:a &lt;bits&gt;</c>
/// at the position of the first removed <c>vbr</c> pair, never behind the output file.
/// </description></item>
/// </list>
/// <para>
/// <b>The table is Jellyfin's own thresholds, floor side.</b> The per-channel values are the
/// lower bounds of the bands Jellyfin 12 uses when it picks the fdk level
/// (&lt;32k→1, &lt;48k→2, &lt;64k→3, &lt;96k→4, else 5), so the synthesized bitrate can never
/// exceed the bandwidth the server had already authorised, and under-coding is the safe failure
/// direction for a bandwidth-capped HLS stream. Levels outside 1-5 are clamped the way fdk's own
/// encoder clamps them; a level that is not a number maps to nothing, and a command with no
/// bitrate to map keeps native's default. Nothing here throws: a malformed command is sanitized
/// as far as its tokens allow and FFmpeg remains the judge of its own argument list.
/// </para>
/// <para>
/// <b>What is never touched.</b> Passed-through vectors (this component is never called for
/// them), single-binary deployments (the dispatch gate does not fire), non-audio arguments,
/// audio arguments other than the fdk selection and its option, and <c>-profile:a</c> - the
/// profile option is generic and parses on every build, so an administrator-authored HE
/// expectation stays the visible misconfiguration it is, rather than something this map quietly
/// rewrites. The codec name is a value token, replaced in place; option tokens keep their exact
/// server-authored spelling.
/// </para>
/// </remarks>
public static class MarkerAudioCompatibility
{
    /// <summary>
    /// The codec token Jellyfin writes when the official build's probe answers
    /// <c>SupportsEncoder("libfdk_aac")</c>.
    /// </summary>
    public const string FdkCodecArgument = "libfdk_aac";

    /// <summary>The encoder every FFmpeg build carries, which this map selects instead.</summary>
    public const string NativeCodecArgument = "aac";

    /// <summary>The generic audio bitrate option this map synthesizes when the command carried none.</summary>
    public const string NativeBitrateArgument = "-b:a";

    /// <summary>
    /// Gets the native bitrate per channel, in bit/s, that each fdk VBR level maps to.
    /// </summary>
    /// <remarks>
    /// These are the documented per-channel floors of fdk's VBR modes 1-5 and, at the same time,
    /// the lower bounds of Jellyfin 12's own level thresholds - the two readings meet at these
    /// five numbers. The table is fixed data, not a measurement: nothing here runs FFmpeg.
    /// </remarks>
    public static IReadOnlyDictionary<int, int> VbrBitsPerChannel { get; } = new Dictionary<int, int>
    {
        [1] = 24000,
        [2] = 32000,
        [3] = 48000,
        [4] = 64000,
        [5] = 96000,
    };

    /// <summary>The lowest fdk VBR level; fdk's own encoder clamps anything below it up to this.</summary>
    private const int MinimumVbrLevel = 1;

    /// <summary>The highest fdk VBR level; fdk's own encoder clamps anything above it down to this.</summary>
    private const int MaximumVbrLevel = 5;

    /// <summary>The channel count assumed when the command states none with <c>-ac</c>.</summary>
    private const int DefaultChannelCount = 2;

    /// <summary>The smallest channel count a synthesized bitrate is computed for.</summary>
    private const int MinimumUsableChannelCount = 1;

    /// <summary>The largest channel count a synthesized bitrate is computed for.</summary>
    private const int MaximumUsableChannelCount = 16;

    /// <summary>
    /// Applies the marker-route audio map to one rewrite outcome, and records what it did as a
    /// wrapper notice on the result.
    /// </summary>
    /// <param name="rewrite">The decided command; anything but a rewrite is returned unchanged.</param>
    /// <returns>
    /// A rewrite outcome carrying the sanitized vector and the notice appended to the rewrite's own
    /// warnings, or <paramref name="rewrite"/> itself when the command carries no fdk audio
    /// selection and nothing would change.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="rewrite"/> is null.</exception>
    /// <remarks>
    /// The caller's gate is dispatch, not this method: it is the wrapper that knows a command is
    /// a rewritten marker job of a two-binary deployment. The status check here is the seam's own
    /// promise that this component cannot rewrite a refusal or stamp a pass-through - only
    /// <see cref="WrapperRewriteStatus.Rewritten"/> outcomes are ever rebuilt.
    /// </remarks>
    public static WrapperRewriteResult ApplyTo(WrapperRewriteResult rewrite)
    {
        ArgumentNullException.ThrowIfNull(rewrite);

        if (rewrite.Status != WrapperRewriteStatus.Rewritten)
        {
            return rewrite;
        }

        var sanitization = Sanitize(rewrite.Arguments);

        if (!sanitization.Changed)
        {
            return rewrite;
        }

        IReadOnlyList<string> warnings = rewrite.Warnings;

        if (sanitization.Notice is { } notice)
        {
            var withNotice = new List<string>(rewrite.Warnings.Count + 1);
            withNotice.Add(notice);
            withNotice.AddRange(rewrite.Warnings);
            warnings = withNotice;
        }

        return WrapperRewriteResult.Rewritten(sanitization.Arguments, rewrite.ProfileId!, warnings);
    }

    /// <summary>
    /// Maps a known fdk audio selection in one argument vector onto the native encoder.
    /// </summary>
    /// <param name="arguments">
    /// The vector to read, one entry per argument, without the executable token. It is never
    /// mutated.
    /// </param>
    /// <returns>
    /// <see cref="Sanitization.Changed"/> <c>false</c> with the received vector when the command
    /// carries no audio-scoped <c>libfdk_aac</c> selection; the rewritten vector, plus the authored
    /// notice describing the resulting encoder and bitrate, when it does.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    public static Sanitization Sanitize(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // Pass 1 reads the facts the mapping needs from the vector as received: whether the
        // trigger is on it at all, whether an audio bitrate the server stated is already
        // standing, and how many channels the command asks for. Reading the received vector -
        // rather than the one being rebuilt - keeps these facts independent of where pass 2
        // happens to cut tokens out.
        var fdkSelected = false;
        var hasAudioBitrate = false;
        var channels = DefaultChannelCount;
        var channelsStated = false;

        for (var i = 0; i < arguments.Count; i++)
        {
            if (!TryReadOption(arguments[i], out var name, out var specifier))
            {
                continue;
            }

            if (IsAudioCodecSelection(name, specifier)
                && i + 1 < arguments.Count
                && string.Equals(arguments[i + 1], FdkCodecArgument, StringComparison.Ordinal))
            {
                fdkSelected = true;
            }
            else if (IsAudioBitrate(name, specifier))
            {
                hasAudioBitrate = true;
            }
            else if (!channelsStated
                && IsChannelCount(name, specifier)
                && i + 1 < arguments.Count
                && int.TryParse(arguments[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stated)
                && stated is >= MinimumUsableChannelCount and <= MaximumUsableChannelCount)
            {
                channels = stated;
                channelsStated = true;
            }
        }

        // No trigger, no work: the vector of every command that does not name fdk for audio - the
        // native encoder, libopus with its own vbr, a copy, anything - goes back as it came.
        if (!fdkSelected)
        {
            return new Sanitization(Changed: false, arguments, Notice: null);
        }

        // Pass 2 rebuilds the vector: the codec value is swapped in place, and each audio-scoped
        // vbr pair is cut out. The slot of the first cut is kept because a synthesized bitrate
        // belongs where the removed quality setting stood, not behind the output file.
        var tokens = new List<string>(arguments.Count);
        var vbrRemoved = false;
        var bitrateSlot = -1;
        string? vbrLevelToken = null;

        for (var i = 0; i < arguments.Count; i++)
        {
            var token = arguments[i];

            if (TryReadOption(token, out var name, out var specifier))
            {
                if (IsAudioVbr(name, specifier))
                {
                    if (!vbrRemoved)
                    {
                        bitrateSlot = tokens.Count;
                        vbrLevelToken = HasUsableValue(arguments, i + 1) ? arguments[i + 1] : null;
                        vbrRemoved = true;
                    }

                    // The option's value goes with it - unless the next token is another option,
                    // in which case this command never carried a value here and removing one more
                    // token would eat an argument that is not fdk's.
                    if (HasUsableValue(arguments, i + 1))
                    {
                        i++;
                    }

                    continue;
                }

                if (IsAudioCodecSelection(name, specifier)
                    && i + 1 < arguments.Count
                    && string.Equals(arguments[i + 1], FdkCodecArgument, StringComparison.Ordinal))
                {
                    tokens.Add(token);
                    tokens.Add(NativeCodecArgument);
                    i++;
                    continue;
                }
            }

            tokens.Add(token);
        }

        string notice;

        if (!hasAudioBitrate && vbrRemoved && TryReadVbrLevel(vbrLevelToken, out var level))
        {
            var bits = VbrBitsPerChannel[level] * channels;
            tokens.Insert(bitrateSlot, NativeBitrateArgument);
            tokens.Insert(bitrateSlot + 1, bits.ToString(CultureInfo.InvariantCulture));
            notice = SynthesizedBitrateNotice(bits, level, channels);
        }
        else if (hasAudioBitrate)
        {
            notice = KeptBitrateNotice();
        }
        else
        {
            notice = DefaultBitrateNotice();
        }

        return new Sanitization(Changed: true, tokens, notice);
    }

    /// <summary>
    /// Whether a token position holds a usable option value rather than the end of the vector or
    /// another option.
    /// </summary>
    private static bool HasUsableValue(IReadOnlyList<string> arguments, int index)
        => index < arguments.Count && !arguments[index].StartsWith("-", StringComparison.Ordinal);

    /// <summary>
    /// Reads a fdk VBR level token, clamped to the range fdk's own encoder clamps to.
    /// </summary>
    /// <param name="token">The value as the command stated it, or null when it stated none.</param>
    /// <param name="level">The clamped level, when the token was a number.</param>
    /// <returns><c>false</c> only for a value that is not a number at all.</returns>
    private static bool TryReadVbrLevel(string? token, out int level)
    {
        level = 0;

        if (token is null
            || !int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        level = Math.Clamp(parsed, MinimumVbrLevel, MaximumVbrLevel);
        return true;
    }

    /// <summary>
    /// Splits an option token into its name and its stream specifier, the way FFmpeg's own
    /// splitter reads the name as the text up to the first <c>:</c>.
    /// </summary>
    /// <param name="token">The token to read.</param>
    /// <param name="name">The option name without its leading dash or specifier.</param>
    /// <param name="specifier">
    /// The stream specifier as written, or empty when the token carries none.
    /// </param>
    /// <returns><c>false</c> for anything that is not an option token.</returns>
    private static bool TryReadOption(string token, out string name, out string specifier)
    {
        name = string.Empty;
        specifier = string.Empty;

        if (token.Length < 2 || token[0] != '-')
        {
            return false;
        }

        var body = token.Substring(1);
        var colon = body.IndexOf(':');

        if (colon < 0)
        {
            name = body;
            return true;
        }

        name = body.Substring(0, colon);
        specifier = body.Substring(colon + 1);
        return true;
    }

    /// <summary>
    /// Whether a specifier denotes audio: FFmpeg reads it as such when it starts with
    /// <c>a</c> (<c>a</c>, <c>a:0</c>, <c>a:1</c>, …).
    /// </summary>
    private static bool DenotesAudio(string specifier)
        => specifier.Length > 0 && specifier[0] == 'a';

    /// <summary>
    /// Whether this option names the audio encoder: the audio-scoped spellings of the codec
    /// option, and <c>-acodec</c>, which is audio by definition. A bare <c>-c</c>/<c>-codec</c>
    /// with no specifier counts, because the value that triggers this map is an audio-only codec
    /// whatever scope it was written in; any other specifier never does.
    /// </summary>
    private static bool IsAudioCodecSelection(string name, string specifier)
        => (string.Equals(name, "c", StringComparison.Ordinal)
                || string.Equals(name, "codec", StringComparison.Ordinal)
                || string.Equals(name, "acodec", StringComparison.Ordinal))
            && (specifier.Length == 0 || DenotesAudio(specifier));

    /// <summary>
    /// Whether this option is fdk's private <c>vbr</c> as applied to audio. The specifier has to
    /// denote audio, and the fdk selection has to have been the trigger: this is fdk's option
    /// being retired, not any <c>vbr</c> on the command.
    /// </summary>
    private static bool IsAudioVbr(string name, string specifier)
        => string.Equals(name, "vbr", StringComparison.Ordinal) && DenotesAudio(specifier);

    /// <summary>
    /// Whether this option already states an audio bitrate: <c>-b:a</c> (or any specifier that
    /// denotes audio), the CLI's <c>-ab</c> - which is defined to set exactly <c>b:a</c> - and a
    /// bare <c>-b</c>, which is the generic bitrate and so applies to the audio streams of the
    /// output it stands on.
    /// </summary>
    private static bool IsAudioBitrate(string name, string specifier)
        => (string.Equals(name, "b", StringComparison.Ordinal)
                || string.Equals(name, "ab", StringComparison.Ordinal))
            && (specifier.Length == 0 || DenotesAudio(specifier));

    /// <summary>Whether this option states the audio channel count.</summary>
    private static bool IsChannelCount(string name, string specifier)
        => string.Equals(name, "ac", StringComparison.Ordinal)
            && (specifier.Length == 0 || DenotesAudio(specifier));

    /// <summary>The notice for a synthesized bitrate, naming it and where it came from.</summary>
    private static string SynthesizedBitrateNotice(int bits, int level, int channels)
        => "the FFmpeg-mvc build this marker command runs on cannot encode " + FdkCodecArgument
            + ", so the audio was switched to native " + NativeCodecArgument + " at "
            + NativeBitrateArgument + " " + bits.ToString(CultureInfo.InvariantCulture)
            + " bit/s, the documented floor of " + FdkCodecArgument + " VBR level "
            + level.ToString(CultureInfo.InvariantCulture) + " for "
            + channels.ToString(CultureInfo.InvariantCulture) + " audio channel(s).";

    /// <summary>The notice for a command whose own audio bitrate the map left standing.</summary>
    private static string KeptBitrateNotice()
        => "the FFmpeg-mvc build this marker command runs on cannot encode " + FdkCodecArgument
            + ", so the audio was switched to native " + NativeCodecArgument
            + ", keeping the audio bitrate the command already carried.";

    /// <summary>The notice for a command with neither a bitrate to keep nor a level to map.</summary>
    private static string DefaultBitrateNotice()
        => "the FFmpeg-mvc build this marker command runs on cannot encode " + FdkCodecArgument
            + ", so the audio was switched to native " + NativeCodecArgument
            + ", which will run at its own default bitrate because the command carried no audio"
            + " bitrate and no readable " + FdkCodecArgument + " VBR level to derive one from.";

    /// <summary>
    /// The outcome of one sanitization: whether the vector changed, the vector to run, and the
    /// authored notice that says what the audio now is.
    /// </summary>
    /// <param name="Changed">Whether the command carried the fdk selection this map retires.</param>
    /// <param name="Arguments">
    /// The sanitized vector when <paramref name="Changed"/>; the received vector otherwise.
    /// </param>
    /// <param name="Notice">
    /// The authored, fixed-vocabulary description of the fallback and the bitrate it produced, or
    /// null when nothing changed. Like every other wrapper diagnostic, it names the rule and the
    /// numbers this component computed, never text echoed from the request.
    /// </param>
    public sealed record Sanitization(bool Changed, IReadOnlyList<string> Arguments, string? Notice);
}
