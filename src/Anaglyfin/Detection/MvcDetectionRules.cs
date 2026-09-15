using System;
using System.Collections.Generic;

namespace Anaglyfin.Detection;

/// <summary>
/// The naming vocabulary the MVC detector matches paths, names and tags against.
/// </summary>
/// <remarks>
/// <para>
/// This is a naming-convention table, kept apart from the detector so that the precedence
/// logic stays readable and the vocabulary can grow (or move to configuration) without
/// touching decisions. The flag spellings follow what Jellyfin's own resolvers and Kodi NFO
/// files use, plus the scene variants seen on MVC rips.
/// </para>
/// <para>
/// All matching is ordinal and case insensitive, so results never depend on the server
/// culture and no file or process access happens.
/// </para>
/// </remarks>
internal sealed class MvcDetectionRules
{
    /// <summary>
    /// Text a flag must contain to be worth a second look, even when it is not a marker.
    /// </summary>
    private const string MvcFragment = "mvc";

    /// <summary>
    /// Characters that separate path segments. Both spellings are listed because a server
    /// reads library paths produced on the other platform as often as on its own.
    /// </summary>
    internal readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>
    /// Characters that separate release flags inside a file, folder or tag name.
    /// </summary>
    internal readonly char[] FlagSeparators =
    [
        ' ', '.', '-', '_', '+', ',', ';', ':', '/', '\\', '|',
        '(', ')', '[', ']', '{', '}', '\'', '"',
        '&', '%', '@', '#', '~', '!', '?', '*', '<', '>', '='
    ];

    /// <summary>
    /// Flags that state the file carries MVC. <c>3D_MVC</c> and <c>3D-MVC</c> reach this set
    /// after the separators split them into <c>3d</c> plus <c>mvc</c>.
    /// </summary>
    internal readonly HashSet<string> MvcMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "mvc", "3dmvc", "mvc3d"
    };

    /// <summary>
    /// Flags that state "this is 3D" without naming a format. They never make an item
    /// eligible on their own.
    /// </summary>
    internal readonly HashSet<string> ThreeDimensionalMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "3d", "3dtv", "3dhd"
    };

    /// <summary>
    /// The tag strings a database query can ask for by name, one per spelling a tag could reach
    /// this vocabulary in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MvcMarkers"/> is not enough for a query. Reading a tag is token-wise - the compound
    /// spellings below are MVC because their separators split them into <c>3d</c> plus <c>mvc</c> -
    /// while a tag query compares a whole cleaned value, so every spelling has to be named for the
    /// query to find it. The plain flags come first because a tag written as a single flag is what a
    /// scraper leaves behind; the separated spellings are what a person types into the tag box.
    /// </para>
    /// <para>
    /// Deliberately finite: the point of the list is a bounded query, not a general name search. A
    /// spelling absent from it is still detected wherever the item itself is asked about - the query
    /// only decides which items a background pass walks at all.
    /// </para>
    /// </remarks>
    internal static readonly string[] MvcTagQueryTerms =
    [
        "mvc", "3dmvc", "mvc3d",
        "3d mvc", "3d-mvc", "3d_mvc",
        "mvc 3d", "mvc-3d", "mvc_3d"
    ];

    /// <summary>
    /// Flags that state a stereoscopic format the MVP does not convert. They veto
    /// eligibility unless the item also carries an MVC marker.
    /// </summary>
    internal readonly HashSet<string> NonMvcFormatMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "sbs", "sbs3d", "hsbs", "fsbs", "3dsbs", "sidebyside", "halfsidebyside", "fullsidebyside",
        "tab", "htab", "ftab", "3dtab", "topandbottom", "halftopandbottom", "fulltopandbottom",
        "overunder", "anaglyph"
    };

    /// <summary>
    /// Gets the segment an item is actually named by: its file name, or the last folder of
    /// a folder based rip such as a Blu-ray structure.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The last path segment, or <c>null</c> when no path is known.</returns>
    internal string? GetItemSegment(string? path)
    {
        var trimmed = TrimTrailingSeparators(path);
        if (trimmed is null)
        {
            return null;
        }

        var separatorIndex = trimmed.LastIndexOfAny(PathSeparators);

        return separatorIndex < 0 ? trimmed : trimmed[(separatorIndex + 1)..];
    }

    /// <summary>
    /// Gets the folders containing the path, without the item segment itself.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The containing folders, or <c>null</c> when the item segment is all there is.</returns>
    internal string? GetDirectorySegment(string? path)
    {
        var trimmed = TrimTrailingSeparators(path);
        if (trimmed is null)
        {
            return null;
        }

        var separatorIndex = trimmed.LastIndexOfAny(PathSeparators);

        // A leading separator only, as in "/Movie.mkv", carries no informative folder name.
        return separatorIndex <= 0 ? null : trimmed[..separatorIndex];
    }

    /// <summary>
    /// Reads the 3D flags out of a single string.
    /// </summary>
    /// <param name="text">The file name, item name or tag to inspect.</param>
    /// <returns>The signals found, or <see cref="TokenSignals.Empty"/> when the text is unusable.</returns>
    internal TokenSignals EvaluateText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TokenSignals.Empty;
        }

        var mvcStrength = MvcMarkerStrength.None;
        string? mvcMarker = null;
        string? threeDimensionalMarker = null;
        string? nonMvcFormatMarker = null;

        foreach (var token in text.Split(FlagSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (MvcMarkers.Contains(token))
            {
                if (mvcStrength != MvcMarkerStrength.Explicit)
                {
                    mvcStrength = MvcMarkerStrength.Explicit;
                    mvcMarker = token;
                }
            }
            else if (mvcStrength == MvcMarkerStrength.None && token.Contains(MvcFragment, StringComparison.OrdinalIgnoreCase))
            {
                mvcStrength = MvcMarkerStrength.Inferred;
                mvcMarker = token;
            }

            if (threeDimensionalMarker is null && ThreeDimensionalMarkers.Contains(token))
            {
                threeDimensionalMarker = token;
            }

            if (nonMvcFormatMarker is null && NonMvcFormatMarkers.Contains(token))
            {
                nonMvcFormatMarker = token;
            }
        }

        return new TokenSignals(mvcStrength, mvcMarker, threeDimensionalMarker, nonMvcFormatMarker);
    }

    /// <summary>
    /// Reads the 3D flags out of a set of strings, such as the tags of an item.
    /// </summary>
    /// <param name="values">The texts to inspect.</param>
    /// <returns>The strongest signal of each kind found across the texts.</returns>
    internal TokenSignals EvaluateAll(IReadOnlyList<string>? values)
    {
        if (values is null or { Count: 0 })
        {
            return TokenSignals.Empty;
        }

        var mvcStrength = MvcMarkerStrength.None;
        string? mvcMarker = null;
        string? threeDimensionalMarker = null;
        string? nonMvcFormatMarker = null;

        foreach (var value in values)
        {
            var signals = EvaluateText(value);

            if (signals.MvcStrength > mvcStrength)
            {
                mvcStrength = signals.MvcStrength;
                mvcMarker = signals.MvcMarker;
            }

            threeDimensionalMarker ??= signals.ThreeDimensionalMarker;
            nonMvcFormatMarker ??= signals.NonMvcFormatMarker;
        }

        return new TokenSignals(mvcStrength, mvcMarker, threeDimensionalMarker, nonMvcFormatMarker);
    }

    private string? TrimTrailingSeparators(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd(PathSeparators);

        return trimmed.Length == 0 ? null : trimmed;
    }
}
