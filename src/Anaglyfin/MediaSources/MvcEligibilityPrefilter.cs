using System;
using System.Collections.Generic;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
using MediaBrowser.Controller.Entities;

namespace Anaglyfin.MediaSources;

/// <summary>
/// The cheap answer to the question <see cref="MvcEligibleSourceScanner"/> asks expensively: could
/// this item have an MVC file anywhere in it at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second answer to one question.</b> <see cref="MvcEligibleSourceScanner.Scan"/> is the
/// authority on which files a profile can convert, and it has to be: the signals that decide it are
/// per file, and a file's identity, label and stereo declaration arrive through the item's media
/// sources. Reading those sources is not free - the server answers one read with the persisted stream
/// rows, attachments and segment probes of the item and of every version grouped with it - and a
/// library is almost entirely made of items with no 3D question whatsoever. This class answers the
/// one question that can be answered without any of that: is there a reason to ask the expensive
/// one? Only refusals are decided here, and only ones the expensive scan would have refused anyway.
/// </para>
/// <para>
/// <b>What is read.</b> Fields the item itself carries, all of them already in memory by the time a
/// caller holds the item: its type, its path, its name, its tags and its declared stereo format. No
/// media source is enumerated, no stream row read, no path opened, no process started. The signals
/// are handed to the same <see cref="IMvcSourceDetector"/> the scan uses, so the two answers cannot
/// drift: whatever the detector accepts from a plain candidate, this accepts too.
/// </para>
/// <para>
/// <b>What it refuses.</b> Anything that could never be a version root: an item that is not a video
/// (a track, a folder, a person), an item whose path is one of Anaglyfin's own markers (a version is
/// never the owner of versions), an item <see cref="MvcEligibleSourceScanner.IsOfferableVideo"/>
/// refuses outright (no path, a relative path, a <c>.strm</c> pointer, a disc image), and an item
/// whose own signals the detector rejects.
/// </para>
/// <para>
/// <b>What it cannot refuse - and does not try to.</b> An item that names other versions of itself.
/// The signals of a stacked or merged movie describe one file - the primary's - while the MVC file
/// sits beside it, on another item, and a library that keeps its 3D rip as an alternate version is
/// precisely the case this feature exists for. Refusing such a root because its own fields describe
/// its 1080p file would lose the versions of its MVC sibling, so an item that names alternate
/// versions is always asked expensively. Naming versions is rare in a library and 3D among them is
/// rarer still, which is what keeps the expensive half of a pass small without guessing.
/// </para>
/// <para>
/// <b>Fail open.</b> Anything this class cannot decide cheaply is passed through to the scan. A
/// wrong pass costs one media-source read; a wrong refusal costs a movie its versions until the next
/// event that happens to mention it, and there is no way to notice from the outside.
/// </para>
/// </remarks>
public static class MvcEligibilityPrefilter
{
    /// <summary>
    /// Gets the tag spellings a server tag query can ask for, in the vocabulary the detector reads.
    /// </summary>
    /// <remarks>
    /// A tag query matches a tag whole (by its cleaned value) rather than token by token, so the
    /// compound spellings the detector reaches through its separators - <c>3D MVC</c>, <c>3D-MVC</c>,
    /// <c>3D_MVC</c> - have to be spelled out for a query to find them. These are the strings a user,
    /// an NFO file or a scraper actually writes; the query is a narrow over-ask, and whatever it
    /// returns is still judged by the detector before anything is written.
    /// </remarks>
    public static IReadOnlyList<string> TagQueryValues => MvcDetectionRules.MvcTagQueryTerms;

    /// <summary>
    /// Decides whether an item is worth the expensive question, from its own fields alone.
    /// </summary>
    /// <param name="item">The item to judge. May be <c>null</c>.</param>
    /// <param name="detector">The rules that decide whether one candidate is MVC.</param>
    /// <returns>
    /// <c>true</c> when the item could carry a version and has to be scanned to know; <c>false</c>
    /// only when it provably could not.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="detector"/> is <c>null</c>.</exception>
    public static bool IsCheapCandidate(BaseItem? item, IMvcSourceDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);

        // Only a video has a 3D question. The test is on the CLR type rather than on MediaType
        // because the format claim this reads exists on Video alone, and an item that is not one
        // cannot make it - whatever its media type says.
        if (item is not Video)
        {
            return false;
        }

        // An Anaglyfin version is never the owner of versions: its path is a marker into the file its
        // primary already owns the versions of. Refused here by name rather than by the rooted-path
        // rule below, because "this is one of ours" is the reason and not a coincidence.
        if (ProfileMarkerParser.IsMarkerCandidate(item.Path))
        {
            return false;
        }

        // The structural refusals are the scanner's own and they are static property reads: a version
        // could not name a playable input for a wrapper out of any of these, so no scan is owed.
        if (!MvcEligibleSourceScanner.IsOfferableVideo(item))
        {
            return false;
        }

        // The same rules the scan applies to a file, applied to the item. This is the whole point of
        // sharing the detector: the item's declared format, its name, its file name and its tags are
        // the signals a media source repeats for the item's own file, so an item the detector accepts
        // is a file the scan would accept and an item it rejects with no other business in the
        // library is a media-source read a pass does not pay for.
        if (detector.Detect(MvcSourceCandidate.FromItem(item)).IsEligible)
        {
            return true;
        }

        // The item's own signals describe its own file, and nothing else it may hold: the MVC rip
        // filed as an alternate version of a plain movie declares itself on that version, not on the
        // primary the user browses to. An item naming other versions therefore cannot be refused from
        // its own fields, and is asked expensively instead.
        return NamesAlternateVersions(item);
    }

    /// <summary>
    /// Whether an item names files of its own beyond the one it is.
    /// </summary>
    /// <remarks>
    /// Both shapes the server stores versions in are read, and both are free: the linked ones arrive
    /// as the child rows an item is loaded with, and the local ones are the paths a scanned stack
    /// recorded on its primary. Anything in either is a file whose signals this item does not speak
    /// for, which is all this answer needs to refuse to answer.
    /// </remarks>
    private static bool NamesAlternateVersions(BaseItem item)
        => item is Video video
           && ((video.LinkedAlternateVersions is { Length: > 0 }) || (video.LocalAlternateVersions is { Length: > 0 }));
}
