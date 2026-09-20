using System;
using System.Collections.Generic;

namespace Anaglyfin.Profiles;

/// <summary>
/// The allowlist of Anaglyfin profile identifiers.
/// </summary>
/// <remarks>
/// <para>
/// A profile id is the only conversion a client, a media source marker or the admin
/// UI is allowed to ask for. Every id a caller can present must round-trip through
/// <see cref="AllProfileIds"/>; anything unknown is rejected rather than interpreted.
/// </para>
/// <para>
/// Ids are opaque and stable. They are not FFmpeg syntax and they never carry
/// filter, view or colour arguments: what a profile id means is owned by
/// <see cref="ProfileCatalog"/> (metadata) and by the command builder (arguments).
/// </para>
/// </remarks>
public static class ProfileIds
{
    /// <summary>
    /// Prefix of every built-in <c>stereo3d</c> anaglyph profile id.
    /// </summary>
    public const string AnaglyphProfileIdPrefix = "anaglyph_";

    /// <summary>
    /// Plain 2D output using the decoder's default base view: the version that plays everywhere.
    /// </summary>
    public const string TwoDBase = "two_d_base";

    /// <summary>
    /// Full width side-by-side output, the native all-view output of the decoder.
    /// </summary>
    public const string SideBySideFull = "sbs_full";

    /// <summary>
    /// Half width side-by-side output.
    /// </summary>
    public const string SideBySideHalf = "sbs_half";

    /// <summary>
    /// Grayscale tinted anaglyph using admin chosen eye colours.
    /// </summary>
    public const string CustomGrayscale = "custom_grayscale";

    /// <summary>Red/cyan Dubois anaglyph (<c>stereo3d</c> code <c>arcd</c>). Shipped default profile.</summary>
    public const string AnaglyphRedCyanDubois = AnaglyphProfileIdPrefix + "arcd";

    /// <summary>Red/cyan anaglyph (<c>stereo3d</c> code <c>arcc</c>).</summary>
    public const string AnaglyphRedCyanColor = AnaglyphProfileIdPrefix + "arcc";

    /// <summary>Red/cyan half colour anaglyph (<c>stereo3d</c> code <c>arch</c>).</summary>
    public const string AnaglyphRedCyanHalfColor = AnaglyphProfileIdPrefix + "arch";

    /// <summary>Red/cyan gray anaglyph (<c>stereo3d</c> code <c>arcg</c>).</summary>
    public const string AnaglyphRedCyanGray = AnaglyphProfileIdPrefix + "arcg";

    /// <summary>Green/magenta Dubois anaglyph (<c>stereo3d</c> code <c>agmd</c>).</summary>
    public const string AnaglyphGreenMagentaDubois = AnaglyphProfileIdPrefix + "agmd";

    /// <summary>Green/magenta anaglyph (<c>stereo3d</c> code <c>agmc</c>).</summary>
    public const string AnaglyphGreenMagentaColor = AnaglyphProfileIdPrefix + "agmc";

    /// <summary>Green/magenta half colour anaglyph (<c>stereo3d</c> code <c>agmh</c>).</summary>
    public const string AnaglyphGreenMagentaHalfColor = AnaglyphProfileIdPrefix + "agmh";

    /// <summary>Green/magenta gray anaglyph (<c>stereo3d</c> code <c>agmg</c>).</summary>
    public const string AnaglyphGreenMagentaGray = AnaglyphProfileIdPrefix + "agmg";

    /// <summary>Yellow/blue Dubois anaglyph (<c>stereo3d</c> code <c>aybd</c>).</summary>
    public const string AnaglyphYellowBlueDubois = AnaglyphProfileIdPrefix + "aybd";

    /// <summary>Yellow/blue anaglyph (<c>stereo3d</c> code <c>aybc</c>).</summary>
    public const string AnaglyphYellowBlueColor = AnaglyphProfileIdPrefix + "aybc";

    /// <summary>Yellow/blue half colour anaglyph (<c>stereo3d</c> code <c>aybh</c>).</summary>
    public const string AnaglyphYellowBlueHalfColor = AnaglyphProfileIdPrefix + "aybh";

    /// <summary>Yellow/blue gray anaglyph (<c>stereo3d</c> code <c>aybg</c>).</summary>
    public const string AnaglyphYellowBlueGray = AnaglyphProfileIdPrefix + "aybg";

    /// <summary>Red/blue gray anaglyph (<c>stereo3d</c> code <c>arbg</c>).</summary>
    public const string AnaglyphRedBlueGray = AnaglyphProfileIdPrefix + "arbg";

    /// <summary>Red/green gray anaglyph (<c>stereo3d</c> code <c>argg</c>).</summary>
    public const string AnaglyphRedGreenGray = AnaglyphProfileIdPrefix + "argg";

    /// <summary>
    /// The official <c>stereo3d</c> output codes of the anaglyph family, exactly as
    /// FFmpeg names them.
    /// </summary>
    /// <remarks>
    /// Source of truth: <c>libavfilter/vf_stereo3d.c</c> option table (unit <c>out</c>)
    /// and <c>doc/filters.texi</c> of the pinned FFmpeg-mvc <c>jellyfin-8.1</c> tree.
    /// The mono outputs (<c>ml</c>, <c>mr</c>) and the geometry conversions are not
    /// anaglyph modes and are intentionally not part of this set.
    /// </remarks>
    public static IReadOnlyList<string> BuiltInAnaglyphOutputCodes { get; } =
        new[] { "arcd", "arcc", "arch", "arcg", "agmd", "agmc", "agmh", "agmg", "aybd", "aybc", "aybh", "aybg", "arbg", "argg" };

    /// <summary>
    /// Every profile id Anaglyfin knows, in profile catalog display order.
    /// </summary>
    public static IReadOnlyList<string> AllProfileIds { get; } = BuildAllProfileIds();

    /// <summary>
    /// Case insensitive membership view over <see cref="AllProfileIds"/>.
    /// </summary>
    /// <remarks>
    /// Declared after the list it is built from: static initializers run in
    /// declaration order.
    /// </remarks>
    private static readonly HashSet<string> AllowedIds = new(AllProfileIds, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The profile ids enabled on a fresh installation: the MVP formats.
    /// </summary>
    /// <remarks>
    /// An admin enabling more presets is a settings change, not a code change.
    /// </remarks>
    public static IReadOnlyList<string> DefaultEnabledProfileIds { get; } =
        new[] { AnaglyphRedCyanDubois, SideBySideFull, SideBySideHalf, TwoDBase };

    /// <summary>
    /// Builds the profile id of a built-in anaglyph output code.
    /// </summary>
    /// <param name="stereo3DOutputCode">An official <c>stereo3d</c> anaglyph output code.</param>
    /// <returns>The allowlisted profile id for that code.</returns>
    /// <exception cref="ArgumentException">The code is empty or only whitespace.</exception>
    public static string BuildAnaglyphProfileId(string stereo3DOutputCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stereo3DOutputCode);

        return AnaglyphProfileIdPrefix + stereo3DOutputCode.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Checks a value against the profile id allowlist.
    /// </summary>
    /// <param name="profileId">The candidate id. May be null.</param>
    /// <returns><c>true</c> when the id is known; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Matching ignores surrounding whitespace and case, so a client that mangles the
    /// casing of an id still resolves, while anything that is not an id - a path, a
    /// filter string, a colour - never matches.
    /// </remarks>
    public static bool IsAllowed(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        return AllowedIds.Contains(profileId.Trim());
    }

    /// <summary>
    /// Compares profile ids the way the allowlist does.
    /// </summary>
    /// <param name="left">First id.</param>
    /// <param name="right">Second id.</param>
    /// <returns><c>true</c> when both denote the same profile.</returns>
    public static bool EqualsId(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string? profileId)
        => profileId is null ? string.Empty : profileId.Trim();

    private static IReadOnlyList<string> BuildAllProfileIds()
    {
        var ids = new List<string>(BuiltInAnaglyphOutputCodes.Count + 3)
        {
            SideBySideFull
        };

        foreach (var code in BuiltInAnaglyphOutputCodes)
        {
            ids.Add(BuildAnaglyphProfileId(code));
        }

        ids.Add(CustomGrayscale);
        ids.Add(SideBySideHalf);
        ids.Add(TwoDBase);

        return ids.ToArray();
    }
}
