using System;
using System.Collections.Generic;
using System.Text;

namespace Anaglyfin.Tests.Ffmpeg;

/// <summary>
/// Decodes a filter expression exactly the way the pinned FFmpeg-mvc tree parses it.
/// </summary>
/// <remarks>
/// <para>
/// The CI gate never runs FFmpeg, so this is the executable half of the escaping
/// contract: it mirrors the parsers the generated command is about to meet, taken from
/// the vendored copy of the very tree Anaglyfin ships against,
/// <c>.shared/vendor/FFmpeg-mvc-jellyfin-8.1</c>:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>libavutil/avstring.c:av_get_token</c> - the single quoting and escaping routine
/// both parsers run: leading whitespace is skipped, <c>\</c> consumes the character
/// after it, a <c>'...'</c> pair contributes its contents literally (the quote itself
/// cannot be quoted), the token ends at the first character of the terminator set that
/// was neither escaped nor quoted, and trailing whitespace is dropped except where an
/// escape or a quote protected it.
/// </item>
/// <item>
/// <c>libavfilter/graphparser.c:filter_parse</c> - the filtergraph level: the filter
/// name is read with <c>"=,;["</c> as terminators and, after the <c>=</c>, its option
/// string with <c>"[],;"</c>.
/// </item>
/// <item>
/// <c>libavfilter/avfilter.c:ff_filter_opt_parse</c> through
/// <c>libavutil/opt.c:av_opt_get_key_value</c> - the filter-option level: a key made of
/// alphanumeric characters plus <c>-</c>, <c>_</c>, <c>/</c> and <c>.</c>, terminated by
/// <c>=</c>, then a value read with <c>":"</c> as its only terminator.
/// </item>
/// </list>
/// <para>
/// Only the plain filter chains this plugin emits through <c>-vf</c> are decoded here:
/// no link labels, no second filter graph. Something the tokenizer cannot make sense of
/// throws instead of being quietly skipped, so an unescaped character breaking out of an
/// option value fails a test rather than passing one.
/// </para>
/// </remarks>
internal static class FilterGraphTokenizer
{
    /// <summary>The whitespace set FFmpeg trims, from <c>WHITESPACES</c> in <c>avstring.c</c>.</summary>
    private const string Whitespace = " \n\t\r";

    /// <summary>
    /// One filter as the filtergraph level sees it: its name and its once-decoded
    /// option string, which the option level then decodes again.
    /// </summary>
    internal readonly record struct ParsedFilter(string Name, string? Options);

    /// <summary>
    /// Runs the filtergraph pass over a <c>-vf</c> chain.
    /// </summary>
    /// <param name="graph">The chain as it goes into the argument vector.</param>
    /// <returns>The filters it decodes to, in chain order.</returns>
    /// <exception cref="FormatException">The chain does not decode as a plain chain.</exception>
    internal static IReadOnlyList<ParsedFilter> DecodeGraph(string graph)
    {
        var filters = new List<ParsedFilter>();
        var index = 0;

        while (index < graph.Length)
        {
            index = Skip(graph, index, Whitespace);

            var name = GetToken(graph, ref index, "=,;[");
            string? options = null;

            if (index < graph.Length && graph[index] == '=')
            {
                index++;
                options = GetToken(graph, ref index, "[],;");
            }

            if (name.Length == 0)
            {
                throw new FormatException($"No filter name at index {index} of '{graph}'.");
            }

            filters.Add(new ParsedFilter(name, options));

            index = Skip(graph, index, Whitespace);
            if (index >= graph.Length)
            {
                continue;
            }

            // Only a chain (',') or graph (';') separator may follow a decoded filter.
            // Anything else is a character that escaped too weakly and landed back in
            // the graph instead of inside its option value.
            if (graph[index] != ',' && graph[index] != ';')
            {
                throw new FormatException($"Unexpected '{graph[index]}' at index {index} of '{graph}'.");
            }

            index++;
        }

        return filters;
    }

    /// <summary>
    /// Runs the filter-option pass over the option string the graph pass produced.
    /// </summary>
    /// <param name="options">The once-decoded option string of one filter.</param>
    /// <returns>The options it decodes to, keyed by option name.</returns>
    /// <exception cref="FormatException">The string is not a sequence of key=value pairs.</exception>
    internal static Dictionary<string, string> DecodeOptions(string options)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;

        while (index < options.Length)
        {
            var keyStart = Skip(options, index, Whitespace);
            var keyEnd = keyStart;
            while (keyEnd < options.Length && IsOptionKeyChar(options[keyEnd]))
            {
                keyEnd++;
            }

            var separator = Skip(options, keyEnd, Whitespace);
            if (keyEnd == keyStart || separator >= options.Length || options[separator] != '=')
            {
                throw new FormatException($"No option key followed by '=' at index {index} of '{options}'.");
            }

            var key = options[keyStart..keyEnd];
            index = separator + 1;

            parsed[key] = GetToken(options, ref index, ":");

            index = Skip(options, index, Whitespace);
            if (index >= options.Length)
            {
                continue;
            }

            if (options[index] != ':')
            {
                throw new FormatException($"Unexpected '{options[index]}' at index {index} of '{options}'.");
            }

            index++;
        }

        return parsed;
    }

    /// <summary>
    /// The <c>av_get_token</c> of <c>libavutil/avstring.c</c>, line for line.
    /// </summary>
    /// <remarks>
    /// <paramref name="protectedLength"/> is the C routine's <c>end</c> marker: the
    /// length up to which the token's tail is protected from the trailing-whitespace
    /// trim, because that tail came from an escape or from between quotes rather than
    /// from bare text.
    /// </remarks>
    private static string GetToken(string source, ref int index, string terminators)
    {
        var token = new StringBuilder();
        var protectedLength = 0;

        index = Skip(source, index, Whitespace);

        while (index < source.Length && terminators.IndexOf(source[index]) < 0)
        {
            var current = source[index++];

            if (current == '\\' && index < source.Length)
            {
                token.Append(source[index++]);
                protectedLength = token.Length;
            }
            else if (current == '\'')
            {
                while (index < source.Length && source[index] != '\'')
                {
                    token.Append(source[index++]);
                }

                if (index < source.Length)
                {
                    index++;
                    protectedLength = token.Length;
                }
            }
            else
            {
                token.Append(current);
            }
        }

        var length = token.Length;
        while (length > protectedLength && Whitespace.IndexOf(token[length - 1]) >= 0)
        {
            length--;
        }

        return token.ToString(0, length);
    }

    private static int Skip(string source, int index, string characters)
    {
        while (index < source.Length && characters.IndexOf(source[index]) >= 0)
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// The <c>is_key_char</c> of <c>libavutil/opt.c</c>. Note that <c>/</c> and
    /// <c>.</c> are key characters, which is why an option value holding a path is
    /// written with an explicit key and never as a bare shorthand value.
    /// </summary>
    private static bool IsOptionKeyChar(char value)
        => value is >= 'a' and <= 'z'
           || value is >= 'A' and <= 'Z'
           || value is >= '0' and <= '9'
           || value is '-' or '_' or '/' or '.';
}
