using System;
using System.Globalization;
using System.Text;
using Anaglyfin.Ffmpeg;
using Anaglyfin.Markers;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// The targeted edits this wrapper is willing to make to filter text it did not write.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole policy, in one place.</b> A received command line is data, and the rewriter
/// (<see cref="WrapperArgumentRewriter"/>) never reads it as filter syntax: nothing here
/// parses a graph, splits a chain, or rewrites a filter's options. What this class does is
/// bounded to three questions that can be answered by scanning text with the quoting and
/// escaping rules FFmpeg itself uses, and one edit those questions make safe:
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="EditVideoSourceReferences"/> retargets the labels naming the marker input's
/// <em>video</em> stream - the single graft a profile needs to put its conversion in front of a
/// server graph - and reports how many labels it retargeted, which is what tells the caller a
/// graph it can merge from a graph it cannot.
/// </item>
/// <item>
/// <see cref="HandlesSubtitles"/> asks whether a piece of filter text already renders
/// subtitles, so the caller leaves subtitle handling to the server that is doing it.
/// </item>
/// <item>
/// <see cref="ReplaceMarkerUrl"/> writes the real source path wherever the server quoted the
/// marker URL into a value, which is what makes a server-side <c>subtitles</c> filter read the
/// file the marker stands for instead of the marker text.
/// </item>
/// </list>
/// <para>
/// <b>Why labels are the only thing edited.</b> A filter graph links its chains by label, and a
/// label is one self-delimiting token - <c>[</c> name <c>]</c> - that FFmpeg's own parser reads
/// outside of quotes and outside of escapes. Retargeting a label therefore needs no knowledge of
/// what the chain around it does: the graph still parses, every filter, option and output label
/// in it stays the server's text byte for byte, and the only semantic fact used is which stream
/// the label names. Everything else about a graph - its filters, its layout, its output pads -
/// stays untouched.
/// </para>
/// <para>
/// <b>What is skipped while scanning.</b> The same constructs FFmpeg's own token reader gives
/// special meaning to, and for the same reason: a backslash spends itself on the character
/// behind it, so an escaped character is never a label boundary; a <c>'...'</c> run contributes
/// its contents literally, so a path holding brackets or the marker text is copied rather than
/// read; and an unterminated label is not a label, so a stray <c>[</c> is copied too. Nothing is
/// normalised, re-quoted, or repaired.
/// </para>
/// </remarks>
public static class AnaglyfinFilterGraphComposer
{
    /// <summary>
    /// The label a linear profile conversion writes its picture to when the command it is
    /// rewritten into carries a filter graph of its own.
    /// </summary>
    /// <remarks>
    /// A fixed name rather than a generated one: it has to be recognizable in a transcode log by
    /// whoever is reading the command, and a collision with a label the server wrote is checked
    /// for (<see cref="FilterGraphEdit.UsesAnaglyfinLabel"/>) rather than designed around. The
    /// prefix is what that check reads, so every label this product writes carries it - including
    /// the ones its own graphs build internally.
    /// </remarks>
    public const string ProfileOutputLabel = "[anaglyfin_profile]";

    /// <summary>
    /// The label the custom grayscale graph writes its picture to, named here so that the label
    /// a merged graph retargets its sources to and the label the command builder writes at the
    /// end of that graph are one constant rather than two strings that have to agree.
    /// </summary>
    public const string CustomOutputLabel = FfmpegProfileArgumentBuilder.CustomAnaglyphOutputLabel;

    /// <summary>
    /// The prefix every label this product writes carries, and the one the collision check
    /// reads. Kept separate from the labels above so that it reads as a prefix and not as a
    /// label that happens to be short.
    /// </summary>
    private const string OwnedLabelPrefix = "anaglyfin";

    /// <summary>
    /// The detail parts that make a specifier of the video type name one view or every view of a
    /// stream rather than the stream itself, as FFmpeg-mvc spells them: <c>view</c> by view id,
    /// <c>vidx</c> by view index, <c>vpos</c> by view position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same three selections the rewriter recognises in a <c>-map</c> value, and for the same
    /// reason: a decode configured through <c>-view_ids</c> refuses every one of them, so a graph
    /// holding one cannot be handed the composed picture a profile asked its input for.
    /// </para>
    /// <para>
    /// Spelled here as the part name rather than as the <c>view:</c> token it appears as in the
    /// text, because a label's detail is read part by part below - a view selection can stand
    /// beside a stream index (<c>0:v:1:vpos:left</c>) - and the separator is what splits the
    /// parts, not something a part carries.
    /// </para>
    /// </remarks>
    private static readonly string[] ViewSpecifierParts = ["view", "vidx", "vpos"];

    /// <summary>
    /// The filter names that mean "this text renders subtitles onto the picture".
    /// </summary>
    /// <remarks>
    /// <c>subtitles</c> is the libass renderer the server uses on a text track of the file, and
    /// <c>ass</c> is the same renderer on an ASS stream it decoded beforehand; both say the
    /// server has decided what the output's subtitles look like. An image subtitle is burned in
    /// through <c>overlay</c> instead, fed by a stream label whose number this scan cannot type,
    /// which is why <see cref="HandlesSubtitles"/> is answered by subtitle-type labels as well as
    /// by filter names.
    /// </remarks>
    private static readonly string[] SubtitleRendererNames = ["subtitles", "ass"];

    /// <summary>
    /// Names the marker input's video stream inside a filter graph.
    /// </summary>
    /// <param name="videoStreamIndex">
    /// The stream index the marker named, or null when it named none.
    /// </param>
    /// <returns><c>[0:&lt;index&gt;]</c> where an index is known, <c>[0:v]</c> where it is not.</returns>
    /// <remarks>
    /// The spelling the profile's own conversion reads its composed picture from - the same one
    /// the command builder writes at the head of the custom graph, for the same reason: by index
    /// wherever one is known, because a bare <c>0:v</c> means every video stream of the input and
    /// so reads a second picture into the graph as soon as the file carries one (an attached cover
    /// track is enough to make that file). Neither spelling selects a view, so neither is refused
    /// by the <c>-view_ids</c> option the caller set on this input.
    /// </remarks>
    public static string ComposedVideoStreamLabel(int? videoStreamIndex)
        => videoStreamIndex is int index
            ? string.Concat("[0:", index.ToString(CultureInfo.InvariantCulture), "]")
            : FfmpegProfileArgumentBuilder.UnnamedVideoStreamFilterInput;

    /// <summary>
    /// Retargets every label of a filter graph that names the marker input's video stream.
    /// </summary>
    /// <param name="graph">The graph as the command wrote it.</param>
    /// <param name="videoStreamIndex">
    /// The video stream index the marker named, or null when it named none. It is what makes a
    /// numbered label like <c>[0:0]</c> recognizable as this source's video at all: without an
    /// index, only the labels naming the video <em>type</em> are answered.
    /// </param>
    /// <param name="replacementLabel">
    /// The label to write in place of each video source reference, brackets included -
    /// <see cref="ProfileOutputLabel"/> or <see cref="CustomOutputLabel"/>.
    /// </param>
    /// <returns>
    /// The edited graph, the number of labels retargeted, and the facts a caller needs in order
    /// to decide whether putting a profile in front of this graph is safe.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Four spellings name this input's video and only those four are touched:
    /// <c>[0:&lt;videoIndex&gt;]</c>, <c>[0:v]</c>, <c>[0:v:0]</c> and <c>[0:v:&lt;videoIndex&gt;]</c>.
    /// Everything else names somebody else's stream and is copied unchanged: audio, a subtitle
    /// stream by type or by number, another input file, and every label an earlier chain of this
    /// graph produced.
    /// </para>
    /// <para>
    /// <c>[0:v:0]</c> counts as this source's video without knowing how many streams the file
    /// carries, because a profile version is built from the first video stream of its file (see
    /// <c>Anaglyfin.MediaSources.ForceTranscodeVideoStreams</c>) and that is exactly what the
    /// first stream of the video type is. The index behind a type is not the same number as the
    /// index of a stream inside the file, so anything else behind the type - <c>[0:v:3]</c> - is
    /// only read as this source's video where the marker itself named that index: the same
    /// anchor, and the same tolerance, the rewriter uses on numbered <c>-map</c> values.
    /// </para>
    /// <para>
    /// A label naming a view of the video type - <c>[0:v:view:all]</c> - is neither replaced nor
    /// left as an ordinary stream: it is counted, because a profile that composed its decode
    /// cannot answer a request for one eye of it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Either text argument is blank.</exception>
    public static FilterGraphEdit EditVideoSourceReferences(
        string graph,
        int? videoStreamIndex,
        string replacementLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementLabel);

        var scan = Walk(graph, replacementLabel, videoStreamIndex);

        return new FilterGraphEdit
        {
            Graph = scan.Text,
            Replacements = scan.Replacements,
            CarriesViewSpecifier = scan.ViewSpecifiers > 0,
            HandlesSubtitles = scan.SubtitleHandling,
            UsesAnaglyfinLabel = scan.OwnedLabels > 0
        };
    }

    /// <summary>
    /// Whether a piece of filter text already handles the subtitles of this file.
    /// </summary>
    /// <param name="filterText">A graph or a linear chain, as the command wrote it.</param>
    /// <returns>
    /// <c>true</c> when it reads a subtitle stream of the first input or names a subtitle
    /// renderer; <c>false</c> for null, empty, and for text about pictures only.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the fact that keeps a profile from taking subtitle streams away from a server that
    /// is rendering them: a graph feeding <c>[0:s]</c> into a filter, or a chain asking
    /// <c>subtitles=</c> for a track, has already answered what the output's subtitles look like,
    /// and a second answer to that question - <c>-sn</c> beside the maps being taken out - is not
    /// a merge but a contradiction.
    /// </para>
    /// <para>
    /// <b>What it cannot see.</b> Numbers. An image subtitle reaches an <c>overlay</c> through a
    /// label like <c>[0:10]</c>, and nothing in that label says what kind of stream stream 10 is:
    /// the marker names one stream of this file and the stream it names is the video one. So a
    /// graph reading its subtitles by number is not reported here, which is why the question is
    /// only ever asked of a rewrite that renders subtitles itself - and why the numbered streams
    /// such a graph reads are never retargeted by <see cref="EditVideoSourceReferences"/> either.
    /// </para>
    /// </remarks>
    public static bool HandlesSubtitles(string? filterText)
        => !string.IsNullOrEmpty(filterText) && Walk(filterText!, null, null).SubtitleHandling;

    /// <summary>
    /// Writes the real source path wherever a value quotes the marker URL.
    /// </summary>
    /// <param name="value">One argument of the command, or one value carried inside one.</param>
    /// <param name="marker">The marker this command's input arrived as.</param>
    /// <returns>
    /// <paramref name="value"/> with every marker spelling replaced by the matching spelling of
    /// the real source path; the value itself when it carried no marker.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why a value can carry a marker at all.</b> The server builds its own subtitle burn-in
    /// from the path of the file it is transcoding, and for an Anaglyfin version that path is the
    /// marker. So a text-subtitle command for a profile version arrives with the marker URL
    /// inside a <c>subtitles=</c> filter, where FFmpeg would otherwise open that URL as a subtitle
    /// file - and fail, or silently render nothing. Replacing the URL is what lets the server's
    /// own burn-in read the real film.
    /// </para>
    /// <para>
    /// <b>The spellings, and why those.</b> A value holds the marker in whatever form the layer
    /// that wrote it escaped it to, and the wrapper cannot ask: a filter option escapes the URL's
    /// colons because <c>:</c> separates filter options, a value inside <c>filename='...'</c>
    /// carries those same escapes between its quotes, a value that travelled through a URL slot
    /// holds the whole marker percent-encoded, and a value meant to survive both filter decode
    /// passes carries the doubled escapes. Each spelling is matched against its own replacement -
    /// the real path, escaped that same way - and nothing else is looked for. Matching is literal,
    /// so a value that merely resembles a marker is left alone.
    /// </para>
    /// <para>
    /// Longest marker first, so that the doubly escaped colon (<c>\\:</c>) is matched before the
    /// singly escaped colon (<c>\:</c>) that is its own tail. A replacement can never be matched
    /// by a later spelling, because a replacement holds the source path and a source path holds no
    /// marker.
    /// </para>
    /// <para>
    /// <b>One limit worth naming.</b> Nothing is unescaped between single quotes, so a source path
    /// carrying a <c>'</c> cannot be written into quoting this wrapper did not choose. That is a
    /// property of the filter quoting rules rather than of this method, and it is why the burn-in
    /// this product writes for itself escapes and quotes the path itself instead of trusting a
    /// quoting layer it did not pick.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="marker"/> is null.</exception>
    public static string ReplaceMarkerUrl(string? value, ProfileMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var replaced = value;

        foreach (var spelling in MarkerSpellings(marker))
        {
            if (replaced.Contains(spelling.Marker, StringComparison.Ordinal))
            {
                replaced = replaced.Replace(spelling.Marker, spelling.Source);
            }
        }

        return replaced;
    }

    /// <summary>
    /// The marker URL and the real source path in each escaping form a server writes a value
    /// under, longest marker spelling first.
    /// </summary>
    private static (string Marker, string Source)[] MarkerSpellings(ProfileMarker marker)
    {
        var markerUrl = marker.ToMediaSourcePath();
        var sourcePath = marker.SourcePath;

        var spellings = new (string Marker, string Source)[]
        {
            // Percent-encoded whole URL: what a value written out of a URL slot holds.
            (Uri.EscapeDataString(markerUrl), Uri.EscapeDataString(sourcePath)),

            // Filter escapes at both depths: the graph pass and the option pass each consume one
            // backslash, so a value meant to reach the option parser still escaped carries two.
            (EscapeForFilter(markerUrl, 2), EscapeForFilter(sourcePath, 2)),
            (EscapeForFilter(markerUrl, 1), EscapeForFilter(sourcePath, 1)),

            // The token itself, bare or sitting between somebody else's quotes.
            (markerUrl, sourcePath)
        };

        // Longest first: the doubly escaped form contains the singly escaped one as a tail, so
        // reading the shorter spelling first would leave a stray backslash behind.
        Array.Sort(spellings, (left, right) => right.Marker.Length.CompareTo(left.Marker.Length));

        return spellings;
    }

    /// <summary>
    /// Escapes a value for a filter option value <paramref name="depth"/> decode passes deep.
    /// </summary>
    /// <remarks>
    /// Each pass consumes one backslash in front of an escaped character and nothing else, so
    /// escaping for two passes is escaping once and then escaping the result: every colon of the
    /// marker URL - after its scheme and again nowhere else - lands as <c>\:</c> once deep and as
    /// <c>\\:</c> twice deep, which is how the server's own filter values spell them.
    /// </remarks>
    private static string EscapeForFilter(string value, int depth)
    {
        var escaped = value;

        for (var pass = 0; pass < depth; pass++)
        {
            var builder = new StringBuilder(escaped.Length + 8);
            foreach (var character in escaped)
            {
                if (character is ':' or '\\')
                {
                    builder.Append('\\');
                }

                builder.Append(character);
            }

            escaped = builder.ToString();
        }

        return escaped;
    }

    /// <summary>
    /// One scan of filter text: copies it, retargeting labels when asked to, and reports what it
    /// recognised on the way.
    /// </summary>
    /// <param name="text">The filter text.</param>
    /// <param name="replacementLabel">
    /// The label to write over each video source reference, or null to only look.
    /// </param>
    /// <param name="videoStreamIndex">The marker's video stream index, or null.</param>
    /// <returns>What the scan produced and what it recognised.</returns>
    private static Scan Walk(string text, string? replacementLabel, int? videoStreamIndex)
    {
        var scanned = new StringBuilder(text.Length + 64);
        var replacements = 0;
        var viewSpecifiers = 0;
        var ownedLabels = 0;
        var handlesSubtitles = false;

        // Whether the token at the cursor is a filter name. FFmpeg reads one after the separators
        // a graph is built from - the head of the text, a ';' ending a chain, a ',' continuing
        // one, and the ']' closing the labels feeding a filter - and nowhere else, which is what
        // keeps a path or an option value that happens to read "ass" from being read as one.
        var atFilterName = true;

        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\\')
            {
                // The backslash is spent on the character behind it, so that character is neither
                // a label boundary nor the start of anything this scan reads.
                scanned.Append(current);

                if (index + 1 < text.Length)
                {
                    scanned.Append(text[index + 1]);
                }

                index += 2;
                atFilterName = false;

                continue;
            }

            if (current == '\'')
            {
                var closing = text.IndexOf('\'', index + 1);

                if (closing < 0)
                {
                    // An unopened quote run reaches to the end of the value, contents and all.
                    scanned.Append(text, index, text.Length - index);
                    break;
                }

                scanned.Append(text, index, closing - index + 1);
                index = closing + 1;
                atFilterName = false;

                continue;
            }

            if (current == '[')
            {
                var closing = text.IndexOf(']', index + 1);

                if (closing < 0)
                {
                    // An unterminated label is not a label: nothing is known about the text
                    // behind it, so it travels as the plain characters it is.
                    scanned.Append(current);
                    index++;
                    atFilterName = false;

                    continue;
                }

                var label = text.Substring(index + 1, closing - index - 1);
                var kind = ClassifyLabel(label, videoStreamIndex);

                if (kind == LabelKind.VideoStream && replacementLabel is not null)
                {
                    scanned.Append(replacementLabel);
                    replacements++;
                }
                else
                {
                    scanned.Append('[').Append(label).Append(']');
                }

                if (kind == LabelKind.ViewOfVideoStream)
                {
                    viewSpecifiers++;
                }

                if (kind == LabelKind.SubtitleStream)
                {
                    handlesSubtitles = true;
                }

                if (label.StartsWith(OwnedLabelPrefix, StringComparison.Ordinal))
                {
                    ownedLabels++;
                }

                index = closing + 1;

                // The token a label feeds is the filter reading it.
                atFilterName = true;

                continue;
            }

            if (current is ',' or ';')
            {
                scanned.Append(current);
                index++;
                atFilterName = true;

                continue;
            }

            if (atFilterName && IsNameStart(current))
            {
                var nameStart = index;
                while (index < text.Length && IsNameChar(text[index]))
                {
                    index++;
                }

                var name = text.Substring(nameStart, index - nameStart);
                scanned.Append(name);

                if (IsSubtitleRenderer(name))
                {
                    handlesSubtitles = true;
                }

                atFilterName = false;

                continue;
            }

            scanned.Append(current);

            if (!char.IsWhiteSpace(current))
            {
                // Whitespace is copied where it stands and leaves the name position open: both
                // filter parsers skip whitespace before a name, so "[0:v]  subtitles=..." does
                // name a subtitle renderer.
                atFilterName = false;
            }

            index++;
        }

        return new Scan(
            scanned.ToString(),
            replacements,
            viewSpecifiers,
            ownedLabels,
            handlesSubtitles);
    }

    /// <summary>
    /// Which stream a filter label names, as far as a scan of its text can tell.
    /// </summary>
    private static LabelKind ClassifyLabel(string label, int? videoStreamIndex)
    {
        // A label naming a stream of a file spells that file first, and only the marker's own
        // input - input 0, which the rewriter insists a marker be - is anybody's source picture
        // here. Everything else names a stream somebody else chose, including every label the
        // graph produced for itself.
        if (label.Length < 3 || label[0] != '0' || label[1] != ':')
        {
            return LabelKind.Other;
        }

        var specifier = label.Substring(2);
        var separator = specifier.IndexOf(':');
        var type = separator < 0 ? specifier : specifier.Substring(0, separator);
        var detail = separator < 0 ? null : specifier.Substring(separator + 1);

        if (type.Length == 1 && char.IsAsciiLetter(type[0]))
        {
            // The uppercase spelling of a type asks for the exact index within it instead of the
            // default one; the type it names is unchanged, which is the question here.
            switch (char.ToLowerInvariant(type[0]))
            {
                case 'v':
                    if (detail is null)
                    {
                        return LabelKind.VideoStream;
                    }

                    if (CarriesViewSpecifier(detail))
                    {
                        return LabelKind.ViewOfVideoStream;
                    }

                    return NamesThisVideosStream(detail, videoStreamIndex)
                        ? LabelKind.VideoStream
                        : LabelKind.Other;

                case 's':
                    return LabelKind.SubtitleStream;

                default:
                    return LabelKind.Other;
            }
        }

        // A numbered stream of this file can carry the same view detail as a typed one, and the
        // answer the rewriter needs is the same: whatever the stream behind it is, a composed
        // decode will not name one eye of it.
        if (detail is not null && CarriesViewSpecifier(detail))
        {
            return LabelKind.ViewOfVideoStream;
        }

        // A bare number names a stream of the file rather than a type of it, so nothing in the
        // label says which kind of stream it addresses. The marker says which number of this file
        // is its video, and that is the only numbered label read as a profile's source picture.
        return videoStreamIndex is int named
               && int.TryParse(type, NumberStyles.None, CultureInfo.InvariantCulture, out var streamIndex)
               && streamIndex == named
            ? LabelKind.VideoStream
            : LabelKind.Other;
    }

    /// <summary>
    /// Whether the detail behind a stream specifier asks for a view rather than for a stream.
    /// </summary>
    /// <remarks>
    /// Read part by part because a view detail can stand beside a stream index
    /// (<c>0:v:1:vpos:left</c>), and one of them is enough: any view selection at all is the
    /// thing a composed decode refuses to name.
    /// </remarks>
    private static bool CarriesViewSpecifier(string detail)
    {
        foreach (var part in detail.Split(':'))
        {
            foreach (var specifier in ViewSpecifierParts)
            {
                if (string.Equals(part, specifier, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the index behind a video type names this source's video stream: the first stream
    /// of that type, which is the one a profile version is built from, or the index the marker
    /// itself named.
    /// </summary>
    private static bool NamesThisVideosStream(string detail, int? videoStreamIndex)
        => int.TryParse(detail, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
           && (index == 0 || index == videoStreamIndex);

    private static bool IsNameStart(char value)
        => value is >= 'a' and <= 'z'
           || value is >= 'A' and <= 'Z'
           || value == '_';

    private static bool IsNameChar(char value)
        => IsNameStart(value) || value is >= '0' and <= '9';

    /// <summary>
    /// Whether a filter name says the text of this file is being rendered onto its picture.
    /// </summary>
    private static bool IsSubtitleRenderer(string name)
    {
        foreach (var renderer in SubtitleRendererNames)
        {
            if (string.Equals(name, renderer, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What one scan of filter text produced and recognised.
    /// </summary>
    private readonly record struct Scan(
        string Text,
        int Replacements,
        int ViewSpecifiers,
        int OwnedLabels,
        bool SubtitleHandling);

    /// <summary>
    /// The result of putting one profile's conversion in front of a graph the server wrote.
    /// </summary>
    public sealed record FilterGraphEdit
    {
        /// <summary>
        /// Gets the graph with its video source labels retargeted and every other character of it
        /// as the server wrote it.
        /// </summary>
        public required string Graph { get; init; }

        /// <summary>
        /// Gets how many labels were retargeted.
        /// </summary>
        /// <remarks>
        /// Zero says this graph never read this input's video stream. That is the difference
        /// between a graph a profile can be put in front of and one it cannot: a conversion
        /// inserted into the first would leave the server chain reading the unconverted stream,
        /// and a conversion inserted into the second would either replace the server's picture or
        /// be left unreferenced, which is a command FFmpeg refuses to run.
        /// </remarks>
        public required int Replacements { get; init; }

        /// <summary>
        /// Gets whether the graph names a view of this input's video stream.
        /// </summary>
        /// <remarks>
        /// A decode configured through <c>-view_ids</c> refuses a view specifier, so a graph
        /// holding one cannot be handed the composed picture this profile asked its input for: the
        /// rewritten command would be one FFmpeg refuses to run at all.
        /// </remarks>
        public bool CarriesViewSpecifier { get; init; }

        /// <summary>
        /// Gets whether the graph handles this file's subtitles itself.
        /// </summary>
        public bool HandlesSubtitles { get; init; }

        /// <summary>
        /// Gets whether the graph already carries a label this product writes.
        /// </summary>
        /// <remarks>
        /// Writing this profile's output label into a graph that already uses that name produces a
        /// graph with two producers for one pad, which FFmpeg reports as a parse error that names
        /// no wrapper. Refusing the merge is what turns that error into a message saying which
        /// merge was refused.
        /// </remarks>
        public bool UsesAnaglyfinLabel { get; init; }
    }

    /// <summary>
    /// What a filter label of the first input names, as far as a scan of its text can tell.
    /// </summary>
    private enum LabelKind
    {
        /// <summary>A stream this profile has no claim on, or a label that names no stream.</summary>
        Other,

        /// <summary>The marker input's video stream - the picture this profile converts.</summary>
        VideoStream,

        /// <summary>A view of it, which a composed decode refuses to name.</summary>
        ViewOfVideoStream,

        /// <summary>A subtitle stream of it.</summary>
        SubtitleStream
    }
}
