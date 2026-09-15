using System;
using Anaglyfin.Detection;
using Anaglyfin.Markers;
using MediaBrowser.Controller.Entities;

namespace Anaglyfin.MediaSources;

/// <summary>
/// The cheap answer to the question <see cref="MvcEligibleSourceScanner.Scan"/> asks expensively: is
/// there a reason to read this item's media sources at all.
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
/// one? Only refusals are decided here, and only ones the expensive path would not have needed for
/// another reason.
/// </para>
/// <para>
/// <b>What is read.</b> Fields the item itself carries, all of them already in memory by the time a
/// caller holds the item: its type, its path, its name, its tags, its declared stereo format and the
/// versions it names. No media source is enumerated, no stream row read, no path opened, no process
/// started. The signals are handed to the same <see cref="IMvcSourceDetector"/> the scan uses, so the
/// two answers cannot drift: whatever the detector accepts from a plain candidate, this accepts too.
/// </para>
/// <para>
/// <b>What it refuses.</b> Anything that has no business in a version pass at all: an item that is
/// not a video (a track, a folder, a person), an item <see cref="MvcEligibleSourceScanner.IsOfferableVideo"/>
/// refuses outright (no path, a relative path, a <c>.strm</c> pointer, a disc image), and an item
/// whose own signals the detector rejects and which names no versions of anything.
/// </para>
/// <para>
/// <b>What it cannot refuse - and does not try to.</b> An item that names other versions of itself.
/// The signals of a stacked or merged movie describe one file - the primary's - while the MVC file
/// sits beside it, on another item, and a library that keeps its 3D rip as an alternate version is
/// precisely the case this feature exists for. A linked version may also be one of Anaglyfin's own,
/// and the link itself carries an item id rather than the marker path that would prove it; refusing
/// such a root from its own fields would strand the stale version rows attached to it.
/// </para>
/// <para>
/// <b>Fail open.</b> Anything this class cannot decide cheaply is passed through to the caller that
/// knows what to ask next: a manager that can read the linked item, or the reconciliation that will
/// diff the item and stop. A wrong pass costs one media-source read; a wrong refusal costs a movie
/// its versions, or leaves one of Anaglyfin's marker items orphaned, and nothing downstream notices.
/// </para>
/// </remarks>
public static class MvcEligibilityPrefilter
{
    /// <summary>
    /// Decides whether an item is worth asking about, from its own fields alone.
    /// </summary>
    /// <param name="item">The item to judge. May be <c>null</c>.</param>
    /// <param name="detector">The rules that decide whether one candidate is MVC.</param>
    /// <returns>
    /// <c>true</c> when the item could carry a version, or is one of the version items the janitor
    /// has to look after; <c>false</c> only when it provably belongs to neither group.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="detector"/> is <c>null</c>.</exception>
    public static bool IsCheapCandidate(BaseItem? item, IMvcSourceDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);

        // Only a video has a 3D question or a version's marker path. The test is on the CLR type
        // rather than on MediaType because the format claim this reads exists on Video alone, and an
        // item that is not one cannot make it - whatever its media type says.
        if (item is not Video video)
        {
            return false;
        }

        // An Anaglyfin version is never the owner of versions, but an event or a pass that finds one
        // must still hand it to the janitor: that item is either a healthy version of a primary that
        // needs the primary reconciled, or an orphan that must be removed before it lists as a movie
        // whose file is a marker URL. The manager decides which answer it is; this answer only says
        // the item cannot be ignored.
        if (ProfileMarkerParser.IsMarkerCandidate(video.Path))
        {
            return true;
        }

        // The structural refusals are the scanner's own and they are static property reads: a version
        // could not name a playable input for a wrapper out of any of these, so no scan is owed.
        if (!MvcEligibleSourceScanner.IsOfferableVideo(video))
        {
            return false;
        }

        // The same rules the scan applies to a file, applied to the item. This is the whole point of
        // sharing the detector: the item's declared format, its name, its file name and its tags are
        // the signals a media source repeats for the item's own file, so an item the detector accepts
        // is a file the scan would accept and an item it rejects with no other business in the
        // library is a media-source read a pass does not pay for.
        if (detector.Detect(MvcSourceCandidate.FromItem(video)).IsEligible)
        {
            return true;
        }

        // The item's own signals describe its own file, and nothing else it may hold: the MVC rip
        // filed as an alternate version of a plain movie declares itself on that version, not on the
        // primary the user browses to. A root that names versions therefore cannot be refused from
        // its own fields, and neither can a root that may own one of Anaglyfin's own versions - the
        // linked child carries an id, not the marker path that would prove the difference.
        return NamesAlternateVersions(video);
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
    private static bool NamesAlternateVersions(Video video)
        => (video.LinkedAlternateVersions is { Length: > 0 }) || (video.LocalAlternateVersions is { Length: > 0 });
}
