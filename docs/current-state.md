# Anaglyfin current state

This document records where the implementation stands: what the source tree
holds, how the shipped behavior reads for an administrator, and which limits are
known and deliberate. The design rationale lives in [architecture.md](architecture.md),
the install shapes in [install.md](install.md), and the runtime validation record
in [validation.md](validation.md).

The first release is prepared but not published: `v0.1.0` has no tag, GitHub
carries no release assets yet, and the plugin repository URL in
[install.md](install.md) therefore serves no manifest yet. All three appear when
the version tag is pushed.

## What the source tree holds

The source tree contains the working code path, not just the bootstrap scaffold:

- fixed plugin identity, manifest, and service registration
- profile catalog with the shipped 2D, SBS, anaglyph, and custom grayscale profiles
- plugin configuration model and dashboard admin page
- conservative MVC detection rules
- alternate media source provider that adds profile-marked playback versions
- one global default profile, offered first to every client ahead of the remaining
  enabled ones
- one admin switch over the raw 3D MVC file's place in the version pickers, on by
  default
- profile marker and parser contract
- exact FFmpeg profile argument builder
- out-of-process `Anaglyfin.FFmpegWrapper` executable with marker rewrite,
  concurrency guard, and real FFmpeg launcher
- xUnit tests covering the pure seams, the command contract, and the packaging job

Real Jellyfin runtime validation is tracked separately in
[validation.md](validation.md). Before a real server is visited, `dev/jellyfin-validation/`
runs the packaged plugin and wrapper against a disposable `jellyfin/jellyfin:latest`
mounted the way the target server is. In the recorded V0-V11 pass, everything the
headless validation host could decide passed; the rows that need a physical 3D
client or a remote target are deliberately PARTIAL and stay open until someone
with one records them.

## The default profile is global and only global

The version an administrator picks as the default is offered first to every
client - phone, TV, web, VR - with the remaining enabled versions following in
catalog order, and the same ordering is what materialised version items are
ranked by. A default only chooses which version is offered first; it authorises
no playback and changes no library item. Exact-device default overrides existed
on pre-release builds only: the provider once read the request's `Jellyfin-DeviceId`
claim so one registered device could start on a different version, and that
feature was removed before this release. The provider now consults nothing about
the request itself. Settings XML written by those pre-release builds loads intact -
a stale `DeviceDefaultProfiles` element decides neither the default nor the
offered order, and it is dropped the next time the settings are saved.

## The original MVC version switch

The admin page has one switch over the original file itself:

```text
Offer original 3D MVC version
```

It ships checked, because offering the raw file is what every build before the
setting did and what a settings file that predates it says nothing about.
Checked, the raw MVC file the scanner filed beside a movie is offered as a
version of that movie. Unchecked, that one raw source is left out of the two
lists a client picks a version from - the details-page version list and
PlaybackInfo - and nothing else moves:

- Anaglyfin's converted versions of that file stay offered, in the same order,
  because the converted versions are what the switch is an alternative to rather
  than a replacement for.
- The MVC library item, its alternate-version link to the movie it was filed
  beside, its path and its resume state are untouched. The switch is a read
  filter over the version lists, which is why saving it back brings the entry
  back on the next request: no library scan, no re-link, nothing re-scraped.
- An entry is only ever left out when a converted version of that same file is
  offered in its place, so the switch cannot leave a film with a version taken
  away and none added.
- An MVC movie asked about as itself keeps its own source: the entry this switch
  takes out of a list is a sibling version of the item being asked about, never
  that item's own entry.
- While an entry is out of those lists, a client cannot choose it through them. A
  PlaybackInfo request naming the hidden source id is answered the way the server
  answers any request for a source that is not in the list, and turning the switch
  back on restores it. This is the setting's known boundary, and the item, its
  source and its data are intact on the other side of it.

No branch of that list writes to the library, so the switch needs no migration
and no rescan in either direction.

## Known follow-ups

The remaining implementation follow-ups are recorded in
[architecture.md](architecture.md) and [validation.md](validation.md) and include
subtitle ordinal wiring through the provider. Selecting by approximate device
category - TV, phone, tablet, VR headset, 3D-capable projector - is deferred
future work and not a current capability: Jellyfin 12 has no reliable native
server-side device type to key one on, so such a feature can only ever be an
explicitly labelled heuristic over what clients report about themselves, layered
over the one global default this release ships.
