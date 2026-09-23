# Anaglyfin runtime validation

This checklist validates Anaglyfin against a real Jellyfin 12 server, the out-of-process
`Anaglyfin.FFmpegWrapper`, and an FFmpeg-mvc `jellyfin-8.1` build. The repository CI job
proves only that the code builds, the unit tests pass, and whitespace formatting is
stable. It does **not** prove that Jellyfin discovers the plugin, that markers survive
playback-info transport, that FFmpeg-mvc renders the selected view, or that real HLS
playback remains usable.

Use this document as a manual sign-off checklist. Record each item as `PASS`, `FAIL`,
or `BLOCKED`, with the Jellyfin version, client, host/container setup, FFmpeg-mvc path,
wrapper path, and relevant log excerpts.

## Scope

In scope:

- plugin installation and admin settings page on a real Jellyfin 12 server
- FFmpeg-mvc deployment and wrapper environment configuration
- conservative MVC eligibility detection
- alternate media source generation, ordering, and marker transport
- the original MVC version switch, and what it does and does not take out of the two version pickers
- rewritten FFmpeg command lines for the main Anaglyfin profiles
- wrapper pass-through behavior for ordinary Jellyfin playback
- concurrency limit behavior and wrapper refusal exit codes
- known MVP limitations: subtitles and the device-category gap V10 records

Out of scope:

- automated integration tests
- packaging as an official Jellyfin plugin repository package
- selection by approximate device category (TV, phone, tablet, headset, projector), which Jellyfin
  12 cannot answer and this build does not attempt; defaults are global - the exact-device matching
  a pre-release build had was removed before release - and what real clients see about the one
  default is V5.2's to record

## How to record results

Use the result table at the bottom of this document. For every `FAIL` or `BLOCKED`,
include:

- item id and item path
- profile id selected
- exact wrapper and Jellyfin log lines
- the real FFmpeg command visible in the process list, with paths redacted as needed
- whether the failure was seen before playback, at transcode start, or during playback

## Where to run it

Two places, and they are not interchangeable.

`dev/jellyfin-validation/` is a disposable Docker stack that runs these artifacts inside
`jellyfin/jellyfin:latest` with the mount shape of the real target
(`./jellyfin/config` -> `/config`, `./jellyfin/cache` -> `/cache`, media -> `/media`). It
answers, on your own machine and repeatably: whether the packaged plugin is discovered by
Jellyfin 12, whether the wrapper is accepted as the server's FFmpeg, whether the linux-x64
artifact executes and decides correctly in that image, whether a misconfigured
`ANAGLYFIN_REAL_FFMPEG` fails loudly, and - with an FFmpeg-mvc build mounted - what a real
converted profile looks like. Its README states which steps it can and cannot answer.

The real server answers the rest: what a client offers and picks, how a real playback's
marker survives transport, and how the picture looks on a television. Run the harness first
so that a `FAIL` on the real server is a finding about Anaglyfin and not about the install.

## V0. Code artifacts are current

- [x] CI passes from the intended branch:

  ```sh
  docker compose --env-file .env -f .ci/test.yml run --rm test
  ```

  Expected result:

  ```text
  anaglyfin CI gate: restore + build(-warnaserror) + test + format + packaging all passed
  ```

- [x] Plugin project builds to a loadable `Anaglyfin.dll`.
- [x] Wrapper project builds to an executable binary for the server's runtime.
- [x] The plugin manifest is embedded and declares:

  ```text
  name: Anaglyfin
  guid: c7f4a1d9-3b58-4e2a-9d6c-84f0b1e5a723
  version: 0.1.0
  targetAbi: 12.0.0
  framework: net10.0
  ```

Notes:

- The CI job packages the plugin archive, the install record, and the linux-x64 wrapper binary
  into `artifacts/`, and verifies all three. Where they go on a server is `docs/install.md`: the
  release workflow publishes the packaged archive and the wrapper as GitHub release assets, the
  project's own plugin catalog installs from them, and placing a file by hand is the troubleshooting
  route for a server that cannot add the repository.
- The wrapper is a normal .NET executable, not a Jellyfin plugin. It must be runnable by
  the Jellyfin server user.

## V1. Plugin installation and discovery

Install the plugin from the Anaglyfin repository URL `docs/install.md` gives, or, where adding
a repository is not the shape you want, by placing the built `Anaglyfin.dll` in the server's
configured plugin directory. `docs/install.md` gives the paths for a bare-metal server and for
a container that mounts `./jellyfin/config` at `/config`. That URL now serves the published
catalog - `v0.1.0` is tagged and its assets are on the release - so a run can install from the
catalog, and placement is the troubleshooting route for a server that cannot add a repository.
Restart or reload Jellyfin after installation.

- [x] Jellyfin lists the plugin with name `Anaglyfin`.
- [x] The plugin id shown by the server matches
  `c7f4a1d9-3b58-4e2a-9d6c-84f0b1e5a723`.
- [ ] The plugin description reads:

  ```text
  Exposes 3D MVC sources to Jellyfin as selectable playback versions.
  ```

- [ ] The dashboard's own settings menu lists `Anaglyfin` as an entry beside the server's
  settings. The page asks to be listed there, so a server whose menu omits it is a server
  where the settings cannot be reached at all.
- [x] `Dashboard -> Plugins -> Anaglyfin` reaches the same page.
- [x] Opening the settings page does not produce a blank page or an unstyled fragment.
- [x] The server does not log assembly-load errors, missing dependency errors, or
  target-framework mismatch errors for `Anaglyfin.dll`.

If Jellyfin does not discover the plugin, stop and record this as the first blocker. Every
later runtime check depends on discovery.

## V2. Admin settings page

Open the page by either route:

```text
Dashboard -> settings menu -> Anaglyfin
Dashboard -> Plugins -> Anaglyfin
```

The settings menu entry is where a plugin page like this one is meant to be
edited from; the plugin list opens the same page. Record which one was used.

Check the rendered page against the shipped defaults:

| Field | Shipped default | Validation |
| --- | --- | --- |
| Default profile | `3D Anaglyph Red/Cyan (Dubois)` (`anaglyph_arcd`) | Shown and selectable; the profile every client starts on |
| Enabled profiles | shipped MVP set | Red/Cyan Dubois, full SBS, half SBS, and 2D Base are enabled by default |
| Custom left eye colour | `#FF0000` | Color input defaults correctly |
| Custom right eye colour | `#00FFFF` | Color input defaults correctly |
| Offer original 3D MVC version | checked | Checkbox is ticked, and its help text is beside the box rather than part of its click target; V5.3 checks what turning it off does |
| Maximum concurrent Anaglyfin transcodes | `1` | Number input defaults correctly |
| Subtitle depth | `Automatic` | One dropdown: `Automatic`, `Constant shift`, `Plane`, and `Flat` are selectable; there is no separate enable switch |
| Constant shift | `0` pixels | Only shown for `Constant shift`; range `-64`..`64` |
| Depth plane | `0` | Only shown for `Plane`; range `0`..`31` |

- [x] Save succeeds and the settings round-trip after reopening the page.
- [ ] `GET /web/ConfigurationPages` reports the page with `EnableInMainMenu: true`, which is
  the flag the v12 web dashboard needs before it will list the page anywhere.
- [ ] Opening the page's own URL directly - outside the dashboard, so with no signed-in
  client - shows the shipped defaults behind a warning naming the dashboard route, and
  saving refuses without writing anything.
- [ ] The page states the deployment requirement once: it asks for a Jellyfin-compatible
  FFmpeg-mvc build and the Anaglyfin FFmpeg entry point, then sends the reader to the
  Anaglyfin documentation.
- [ ] The page does not carry deployment internals: no word `wrapper`, `environment`, or
  `variable`; and no `ANAGLYFIN_REAL_FFMPEG`, `FFMPEG_MVC_PATH`, `ANAGLYFIN_LOCK_DIR`,
  `ANAGLYFIN_WRAPPER_SETTINGS`, `ANAGLYFIN_MAX_CONCURRENT_TRANSCODES`, `JELLYFIN_FFMPEG`,
  or `FFMPEG_PATH`.
- [x] When `ANAGLYFIN_WRAPPER_SETTINGS` is configured, changing **Maximum concurrent Anaglyfin
  transcodes** and saving writes that number to the shared settings document; a wrapper started after
  the save sees it without another deployment step.
- [ ] Disabling all enabled profiles is not accepted as an empty offer: saving that state
  should come back as the shipped enabled set.
- [ ] Changing the default profile changes the first Anaglyfin version offered, for every client.
- [ ] The page carries no device defaults section at all: no rows, no device picker, nothing
  device-shaped - the default is global and there is nowhere else to set one.
- [ ] The default is applied by the provider and not merely stored: V5.2 is where an actual
  client offer is checked against it.
- [ ] A server upgrading from a build whose subtitle depth was switched off (or which
      predates the feature) loads on `Flat`, not on the shipped `Automatic`: an upgrade does not
      start moving captions nobody asked it to. An installation on this build already reopens
      with whatever mode it saved.
- [ ] The same upgrade story holds for the original MVC switch, in the other direction: a settings
      file with no element for it reopens **ticked**, because the shipped position is the behaviour
      that file was written against. Saving it unticked puts the element in the file, and reopening
      the page shows it unticked. What the box does when it is unticked is V5.3's to record; this row
      only asks that the page tell the truth about the stored value.


## V3. FFmpeg-mvc and wrapper deployment

Anaglyfin needs a real FFmpeg-mvc build and a real FFprobe that Jellyfin can use. The
wrapper must not become the FFprobe.

Record the deployment details:

```text
Jellyfin version:
Jellyfin plugin directory:
Wrapper binary path:
FFmpeg-mvc binary path:
ffprobe path:
ANAGLYFIN_LOCK_DIR:
ANAGLYFIN_MAX_CONCURRENT_TRANSCODES (optional):
ANAGLYFIN_WRAPPER_SETTINGS:
Jellyfin FFmpeg path setting:
```

Required deployment shape:

```text
Jellyfin -> Anaglyfin FFmpeg entry point -> ANAGLYFIN_REAL_FFMPEG -> FFmpeg-mvc
Jellyfin -> real ffprobe, in the entry point's own directory
plugin -> ANAGLYFIN_WRAPPER_SETTINGS -> wrapper (one shared admin-settings document)
```

The second line is not decoration. The server resolves `ffprobe` from the directory of the
FFmpeg path it was given, so putting the wrapper somewhere on its own moves the probe the
library scan depends on. On a container that mounts `./jellyfin/config` at `/config`, the
signature of getting this wrong is one line at startup and a library that never fills:

```text
[ERR] MediaEncoder: Running /config/anaglyfin/ffmpeg/ffprobe -loglevel quiet -f lavfi
      -i nullsrc=s=1x1:d=1 -only_first_vframe failed with exception An error occurred
      trying to start process '/config/anaglyfin/ffmpeg/ffprobe' ... No such file or directory
```

- [x] FFmpeg-mvc `jellyfin-8.1` is installed and executable by the Jellyfin service user. The
  current subtitle-depth target is the official `n8.1.2-mvc7-jf4` build; an older FFmpeg-mvc may
  run ordinary commands but cannot honour a depth request if it does not carry `mvcsubdepth`.
- [x] `FFmpeg -filters` names `mvcsubdepth` (the filter a depth request needs; it is not
  loaded when subtitle depth is `Flat`).
- [x] FFprobe from the same FFmpeg-mvc build is installed, executable, **in the same
  directory as the wrapper**, because that is where the server will look for it.
- [x] The wrapper executable is deployed to a stable path and executable by the Jellyfin
  service user.
- [x] The server's FFmpeg path points at the Anaglyfin FFmpeg entry point, not directly at
  FFmpeg-mvc, if wrapper-based rewriting is expected.
- [x] The server can still probe files with the real `ffprobe`.
- [x] The wrapper's real FFmpeg resolution is one of:

  ```text
  ANAGLYFIN_REAL_FFMPEG=/path/to/ffmpeg-mvc
  FFMPEG_MVC_PATH=/path/to/ffmpeg-mvc
  ffmpeg found through PATH
  ```

- [x] Wrapper environment variables are set in the environment that Jellyfin passes to
  started helper processes. Setting them only in the administrator's login shell is not
  sufficient.
- [x] `ANAGLYFIN_LOCK_DIR` is writable by the Jellyfin service user.
- [x] `ANAGLYFIN_WRAPPER_SETTINGS` names the same absolute file to the plugin and to every wrapper
  process that should see the admin page's subtitle-depth request and concurrency limit. Its
  directory must be writable by the Jellyfin service user, because the plugin writes it and the
  wrapper only reads it.
- [x] For container deployments, every wrapper process that should share one concurrency
  limit sees the same lock directory.
- [x] For container deployments, every wrapper process that should see one admin-settings request
  reads the same settings document.

Example environment block, adjusted to your deployment:

```sh
ANAGLYFIN_REAL_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg-mvc
ANAGLYFIN_LOCK_DIR=/tmp/anaglyfin/ffmpeg-wrapper
ANAGLYFIN_WRAPPER_SETTINGS=/var/lib/jellyfin/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
# Optional override for the admin page's concurrency limit:
# ANAGLYFIN_MAX_CONCURRENT_TRANSCODES=1
```

Notes:

- The wrapper checks the configured real binary and refuses if it is missing or points back
  at the wrapper itself.
- The wrapper reads the subtitle-depth request and concurrency limit from the document named by
  `ANAGLYFIN_WRAPPER_SETTINGS`. If that variable is unset or the document cannot be read, the
  wrapper does not fail the playback: it uses flat subtitles and falls back to its own configured or
  shipped concurrency limit.
- The settings document is schema version 2 when written by this build: a `subtitleDepth` section and
  a `transcoding.maxConcurrentTranscodes` field. A schema version 1 document is still readable, but
  it states only depth and states no concurrency limit. A newer or malformed schema version is
  ignored rather than trusted.
- The wrapper resolves the real FFmpeg binary in this order:
  1. `ANAGLYFIN_REAL_FFMPEG`
  2. `FFMPEG_MVC_PATH`
  3. `ffmpeg` found on `PATH`
- The wrapper resolves the concurrency limit in this order:
  1. a valid `ANAGLYFIN_MAX_CONCURRENT_TRANSCODES` override
  2. the settings document
  3. the shipped default

## V4. MVC detection and item eligibility

Use at least one positive MVC item and two negative items:

```text
positive: Movie.2010.3D.1080p.MVC.mkv
negative: Movie.2010.3D.1080p.HSBS.mkv
negative: Movie.2010.3D.1080p.mkv
```

And one item that is several files - a movie folder whose primary version is plain and whose
MVC version sits beside it, which is the layout that shows up as a stack or as local alternate
versions in the client's version menu:

```text
/Movies/Ready Player One (2018)/Ready Player One (2018) - 1080p.mkv
/Movies/Ready Player One (2018)/Ready Player One (2018) - 3D mvc.mkv
```

The detector is intentionally conservative:

- `Video3DFormat=MVC` metadata is enough by itself.
- Explicit MVC markers in the file name, item name, item tags, or containing folder make an
  item eligible.
- Plain `3D` does not make an item eligible.
- SBS/TAB names without an MVC marker are ineligible.
- Text that merely contains `mvc` inside a longer word is rejected.
- The signals are read per **media source**, not only per item: each file an item can be played
  from is judged by its own path, its own version label and the stereo format recorded for it.
  The item's own tags and name describe the item's own file and are applied to that file alone,
  never to a sibling version.

- [x] A file with explicit `MVC` in the file name produces Anaglyfin versions after a scan
  or metadata refresh.
- [x] A file with only `3D` does not produce Anaglyfin versions.
- [x] A file named `HSBS`, `SBS`, `TAB`, or similar but without an MVC marker does not
  produce Anaglyfin versions.
- [ ] A non-video item does not produce Anaglyfin versions.
- [ ] `.strm` items and disc-image-like items do not produce Anaglyfin versions.
- [x] A stacked movie whose primary file is plain and whose MVC version sits beside it produces
      Anaglyfin versions after a scan, even though the item itself reports no 3D at all. See V5.1.

If detection is wrong, record the item path, `Video3DFormat`, tags, and whether Anaglyfin
logged a no-source decision. Those decisions are Debug lines from
`Anaglyfin.MediaSources.AnaglyfinMediaSourceProvider` - on Jellyfin 12, which configures
Serilog from JSON, they are switched on with a `logging.json` next to the other files in the
server's config directory:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Override": {
        "Anaglyfin": "Debug"
      }
    }
  }
}
```

Then look for:

```text
Anaglyfin offers 4 versions for <item name>.
Anaglyfin offers no versions for <item name>: no media source of it is 3D MVC.
Anaglyfin offers no versions for the media source <version label> of <item name>: <decision>.
```

The second and third lines are the per-file decisions: an item that is several files is asked
about once per file, so a movie whose MVC version was missed shows up as a refusal naming that
version rather than as silence about the item.

## V5. Alternate media source list

For an eligible MVC item, request playback info from the client or inspect the playback-info
API response. The provider contributes one alternate source per enabled profile.

Default enabled profiles on a fresh install:

```text
anaglyph_arcd
sbs_full
sbs_half
two_d_base
```

Expected Anaglyfin contribution order:

1. The global default profile, promoted first: the same version leads the offer for every
   client, because the global default is the only default there is.
2. The remaining enabled profiles in catalog display order.

For the default installation the expected Anaglyfin order is therefore:

```text
1. 3D Anaglyph Red/Cyan (Dubois)
2. 3D Full Side-by-Side
3. 3D Half Side-by-Side
4. 2D Base
```

Notes:

- The server may keep the item's original source first in the client's version list.
- The promoted first version is the same for every request: the configured global default,
  which the provider resolves from the settings alone and consults nothing about the request
  to apply. V5.2 checks the ordering on real clients.

For each alternate source, check:

| Field | Expected |
| --- | --- |
| `Id` | GUID, lower-case `N` format, derived from the media source's identity and the profile id |
| `Name` | human-readable profile display name |
| `Path` | Anaglyfin marker URL, carrying `video=<index>` |
| `Protocol` | `Http` |
| `IsRemote` | `false` |
| `SupportsDirectPlay` | `false` |
| `SupportsDirectStream` | not assertable: see the note below |
| `SupportsTranscoding` | `true` unless the user's permission revokes it |
| `SupportsProbing` | `false` |
| `RequiresOpening` | `false` |
| `Type` | `Default` |
| `MediaStreams` | the original source's streams, with one exception: the video stream's `Codec` and its `Width`/`Height` |
| `Video3DFormat` | `null` on every Anaglyfin source, whatever the item's own source declares |
| `VideoType` | `VideoFile` on every Anaglyfin source, including the one the server builds from a materialised version item (see "What `VideoType: VideoFile` is for") |

- [x] The Anaglyfin source ids are lower-case `N`-format GUIDs, unique per profile and different from the item id.
      DynamicHLS parses `MediaSourceId` as a `Guid`, so a descriptive id fails playback before FFmpeg starts.
- [x] The source id changes when the profile id changes.
- [ ] The video stream of an Anaglyfin source reports `Codec` = `mvc`, while the original library
      source on the same item still reports its real codec (`hevc`, `h264`, ...).
- [ ] The video stream of an Anaglyfin source reports the frame **its profile encodes**, not the
      frame the file was probed at. For a 1920x1080 MVC source:

  | Profile | Reported `Width` x `Height` |
  | --- | --- |
  | `sbs_full` | `3840 x 1080` (both eyes, side by side, full height) |
  | `sbs_half` | `1920 x 1080` (the doubled frame is halved inside the profile chain) |
  | any `anaglyph_*` | `1920 x 1080` |
  | `custom_grayscale` | `1920 x 1080` |
  | `two_d_base` | `1920 x 1080` (the base view is the source's own frame) |

  This is not cosmetics. When the client asks for no resolution and brings at least the bitrate
  this stream reports, the server defaults its `MaxWidth`/`MaxHeight` to exactly these numbers
  and writes a `scale` from them into `-vf`; the scale is evaluated at run time against the frame
  that reaches it. Report the source's size for a profile that encodes a bigger frame and the
  server shrinks the converted picture into the source's box on the way out - which is precisely
  the 3840x1080-into-1920x540 failure these rows exist to prevent. See V7.8 for the segment
  measurement that proves the whole chain.
- [ ] `Video3DFormat` is absent on every Anaglyfin source even when the item's own source declares
      `MVC`. The server reads that field as an instruction to convert a stereo source to 2D itself;
      a version has already produced the picture it sells, so wearing the item's marker invites the
      server to undo part of the conversion.
- [ ] Every other field of that stream - bit depth, frame rate, colour transfer, the
      Dolby Vision flags, `AspectRatio`, `IsAnamorphic` - matches the original source's video
      stream. The version re-labels the codec and restates the size, and nothing else. A client
      that shows a stream title built from the codec will show `MVC` where the original shows
      `HEVC`, and a full-SBS version's badge reads `4K` for a 1080p film because its encoded frame
      really is 3840 wide; both are the expected visible difference.
- [ ] The original library source still reports the size it was probed with (the version's size is
      on a copy of the stream, never on the item's own).
- [ ] Resume position and seek behavior remain reasonable when switching between versions.
- [x] The original library source remains playable and unchanged.
- [ ] Changing enabled profiles changes the offered versions without a server restart.

### 5.1 Stacked items and alternate versions

A movie assembled from a stack, or carrying local or linked alternate versions, is **one**
library item over **several** files. Its own `Path`, `Name` and `Video3DFormat` describe the
primary file only; the other versions arrive as additional static media sources of the same item
and, in the case of a local alternate version, are not listable items a client could open by
themselves. Asking such an item whether it is 3D therefore asks about the primary file, and a
movie that is 3D in one of its versions answers "no".

This is the layout to reproduce it with:

```text
/Movies/Ready Player One (2018)/Ready Player One (2018) - 1080p.mkv
/Movies/Ready Player One (2018)/Ready Player One (2018) - 3D mvc.mkv
```

Request playback info for the movie item itself - not for the MVC file, which the client cannot
address directly - and expect the item's two originals followed by the four profiles of the MVC
one:

```text
1080p                          original, untouched
3D mvc                         original, untouched
3D Anaglyph Red/Cyan (Dubois)  of 3D mvc
3D Full Side-by-Side           of 3D mvc
3D Half Side-by-Side           of 3D mvc
2D Base                        of 3D mvc
```

- [x] The stack root's playback info includes Anaglyfin versions even though the item's own
      `Path`, `Name` and `Video3DFormat` carry no 3D signal.
- [x] Every Anaglyfin source built from a stacked version names **that version's file** in its
      marker (`source=`), and reports that file's duration, container, size and stream list - not
      the primary file's.
- [x] Each Anaglyfin source id is derived from the media source it converts: two files of one item
      never share an id, and every id is still a lower-case `N` GUID that differs from the item id
      and from the id of any version it was derived from.
- [x] Ids are stable across repeated playback-info requests and across a server restart.
- [ ] With one eligible MVC file among the versions the labels are unchanged: `3D Full
      Side-by-Side`, `3D Half Side-by-Side`, `3D Anaglyph Red/Cyan (Dubois)`, `2D Base`.
- [ ] With more than one eligible MVC file, every label names the file it converts, e.g.
      `3D mvc / 3D Full Side-by-Side`, and each profile appears once per eligible file.
- [x] Nothing is offered twice: one eligible file yields one source per profile, and the total
      number of Anaglyfin sources equals profiles x eligible files.
- [ ] A stack with no MVC file among its versions gets no Anaglyfin sources at all.
- [ ] The static sources themselves are untouched: the primary 1080p source and the MVC source
      both keep their real codec, frame size, runtime and stereo declaration.
- [ ] A single-file MVC movie is unaffected: same ids, same labels, same four versions.

### 5.2 Global default ordering

One default is wired: the global one. The page sets it, the provider resolves it from the
settings on every playback-info request, and the version it resolves to is offered first to
every client, with the remaining enabled versions following in catalog order. The provider
consults nothing about the request itself - pre-release builds carried exact-device default
rows, and that feature was removed before release, so there is no row to set, no picker to
choose from, and no request state the plugin reads to apply one.

This is ordering, not authorization. The enabled set, the versions in the library, and what a
user may play are the same on every branch of every check below; only which version the picker
leads with moves. A check that finds a default opening a version nobody enabled, or hiding one
somebody did, is a finding worth stopping on.

Set it up with the shipped global default, and with two clients if you have them - a TV and a
phone, or two browsers on two machines:

- [ ] The global default configured on the page is the **first** Anaglyfin version a client is
      offered, and the rest of the enabled set arrives beside it in catalog order.
- [ ] Two different clients see the same first version and the same order. Per-device ordering
      is not a thing this build can do, so a difference means something was misread or the
      build under test is not this one. Record the order each client showed.
- [ ] Changing the global default on the page moves which version every client leads on, after
      the save. Nothing on any client needs re-pinning - there is nothing to pin.
- [ ] A provider call that is not a client's playback-info request comes back ordered the same
      way: an API-key call to the playback-info API is the shape to run, and a background or
      DLNA composition is the same answer with no HTTP request in it. Record which one was
      exercised.
- [ ] An upgrade from a pre-release build whose settings file carries `DeviceDefaultProfiles`
      entries proves the stale element costs nothing: it decides neither the default nor the
      order, and the next save from the page takes it out of the file. No migration runs on
      disk, and none is needed for the entries to be inert.

No row here is checked off by a CI run or by reading the settings file: CI pins the resolution
rules, and what this section asks for is what real clients actually see. Leave every one of
them open until it has been observed, and record the observations in the result log below.

### 5.3 The original MVC version toggle

The admin page has one switch over the raw file itself:

```text
Offer original 3D MVC version
```

It ships checked, and a settings file written before the setting existed carries no element for it -
an upgrading server therefore loads the same checked answer it was already offering. Unchecked, the
raw MVC file stops being offered as a version of the item it was filed beside, and that is the whole
of the setting. V5.1's stacked layout is the setup it is checked against:

```text
/Movies/Ready Player One (2018)/Ready Player One (2018) - 1080p.mkv
/Movies/Ready Player One (2018)/Ready Player One (2018) - 3D mvc.mkv
```

What is switched is one entry in the two lists a version is picked from - the details-page version
list and the PlaybackInfo response - so the checks below are read off those, and off the item DTO the
details page renders its list from. Nothing in the library is expected to move on any of these saves;
the checks that would notice if it did are here for that reason, not because a change is expected.

- [x] **Checked, before anything else**: the item's version list is the one V5.1 recorded - the 1080p
      source, the `3D mvc` source, and the converted versions of the MVC file. Record the raw MVC
      entry's `Id` and `Path` here; the same pair has to reappear at the end of this section.
- [x] **Saved unchecked**, the raw MVC entry is out of both lists on the next request. No server
      restart, no library scan and no second save are part of this: the setting is read per request, so
      reloading the page is the whole of the wait. Record which of the two surfaces was read, and that
      both were.
- [x] **Nothing else moved in the same response**: the 1080p source is still listed, every converted
      version is still listed with its own id and in its usual order, and no Anaglyfin version
      disappeared because the file it converts stopped being offered. That last one is the check the
      whole design exists to satisfy - the plugin's detection reads the item's own sources underneath
      the filter, so taking the entry out of a list cannot take the file out of the offer.
- [x] **Saved back on**, the entry is in the list again on the next request with the `Id` and `Path`
      recorded at the top of this section, and with no scan in between. Resume position and played
      state are the same on both sides of the toggle.
- [x] **The library agrees**: the MVC child item still exists - a local alternate version is listed
      nowhere and is addressed through its parent, so `GET /Items/<mvc item id>` as an administrator is
      how to ask - and the movie's alternate-version link to it is what it was before the switch was
      touched. Running a library scan after the toggle changes neither.
- [x] **The item whose file this is still exposes its own source.** Ask for the source list of the MVC
      item itself - the child the scanner filed beside the movie - and its own entry is in it, with the
      switch off. What the setting takes out of a list is a sibling version of the item being asked
      about, never that item's own entry, so an item is never left with nothing to play. Record how the
      list was asked for; if this item shape is not reachable from the client or endpoint used, say so
      rather than leaving the row implied.
- [x] **A PlaybackInfo naming the hidden source id is refused, and that is the boundary.** Post a
      playback request with the raw `MediaSourceId` while the entry is out of the lists and the response
      comes back with no sources and `ErrorCode: NoCompatibleStream` - the server's ordinary answer for a
      source it was not offered, since a version picked out of a list is picked out of a list. Record the
      error code, and read it next to the two rows above: the entry is out of the lists, and the item and
      its own source are where they were.
- [x] **A single-file MVC movie is untouched by the switch.** With the MVC file as the movie's only
      file, its own source is still offered with the setting off: the entry this feature leaves out of a
      list is a sibling version of the item being asked about, never that item's own entry.
- [ ] **Nothing is hidden when there is nothing to offer instead of it.** That is the fail-open rule - a
      raw file is only ever left out of a list when a converted version of it is in the same list - and
      it is the difference between hiding a duplicate and taking away the only thing that plays. Ask
      whether the run can produce the state at all: with every profile disabled the page answers with the
      shipped enabled set (see V2), so there may be no way to reach it from a browser. If it cannot be
      produced, record that the rule is pinned by CI at the unit level rather than leaving this row to
      imply an observation that was not made.
- [ ] **A restricted user gets the same answer.** The setting is not a permission: an entry hidden for
      one user is hidden for all of them, and an entry an administrator can see is not offered to
      somebody the library is closed to.
- [x] Record the **known boundary** rather than filing it as a failure: while the entry is hidden, a
      client reading the version lists cannot discover that source id - it has to have it already.
      Turning the switch back on restores discovery with the entry.

Notes:

- The switch filters what the server's media source manager hands out. Nothing in this section should
  ever find an item deleted, unlinked, renamed, ignored or recreated. If a run does, that is a defect
  in the filter and not a limitation of it: record the item ids, the alternate-version links the server
  reports for the movie, and the exact sequence of saves that produced it.
- The source id is what proves nothing was rebuilt, which is why one pair of values is asked for twice
  above. An entry that came back with a new id would pass every weaker check in this section, and would
  mean something in the library had been made again rather than left alone.

### Why `SupportsDirectStream: false` is not a check

Jellyfin 12 overwrites both flags for every plugin-created source during PlaybackInfo, with
the user's permissions: `SupportsTranscoding` becomes "may transcode video" and
`SupportsDirectStream` becomes "may remux". A provider that reports `false` is overruled
before the response is written, and the transcode path that later decides whether to copy the
video stream never reads either flag anyway. So:

- [ ] Do **not** record a FAIL for `SupportsDirectStream: true` in the response. It is the
      server's permission, not Anaglyfin's claim.
- [ ] Do **not** use the response flags to judge whether a version will be converted. The
      evidence for that is the transcoding URL and the FFmpeg command line: see V7.

What the plugin does instead is report the version's video with a codec no client's transcode
profile names (`mvc`), which leaves the server no codec it is allowed to copy and therefore no
copy to build. The name is the lever, not a description: ffprobe reports an MVC track as
`hevc`. Apart from that codec and the frame size the profile encodes (the table above), the
version's stream report is the item's own, field for field. The server then builds a real encode
command, which is visible in the transcoding URL as a video codec that is not the source's own,
and in the child FFmpeg command as `-codec:v <encoder>`.

### What `VideoType: VideoFile` is for

Every Anaglyfin source declares its media a video file. The claim is read in four places, and the
one that has an encoder behind it is the server's gate: Jellyfin offers its hardware video encoders
(`h264_qsv`, `hevc_nvenc`, `h264_vaapi` and their families) only to a job whose `VideoType` is
`VideoFile`. On Jellyfin 12 that gate stands open to a version either way - the streaming path never
writes the job's field, and the enum has no "unspecified" member, so a field nobody wrote answers its
first value, which is `VideoFile`. What the checks below therefore record is the claim, not an
encoder unlocked: the encoder is settled by the two bullets that name the command and the binary.

- [ ] The dynamic Anaglyfin sources in the PlaybackInfo response carry `VideoType: "VideoFile"`.
      Before the declaration they carried nothing at all: an Anaglyfin version was a source with no
      video type on it, in a response where every other source names one.
- [ ] The version item's own `MediaSources` entry on the parent's item DTO carries
      `VideoType: "VideoFile"` as well, so the item lists as the file it converts and sorts with the
      sources that name that type. A version item that drifted onto a disc value - a resolver that
      read it from a name, a hand edit, an item merged in from elsewhere - is brought back by the next
      version-item pass; no re-scan of the files is needed.
- [ ] The declaration is not an encoder choice. Whatever the server picked - a hardware encoder, or
      `libx264` on a server with hardware acceleration off - is what the command has to carry. An
      encoder the plugin invented would be a bug, not a fix.
- [ ] On a server with hardware encoding switched on, the child FFmpeg command for a version names a
      hardware encoder rather than `libx264` (see V7.10). A version that still comes out `libx264` on
      a Quick Sync or NVENC host has an FFmpeg with no such encoder compiled into it - the gate asks
      `SupportsEncoder`, which probes the binary the server was pointed at - which is a build finding,
      not a plugin one. Capture that binary's `ffmpeg -hide_banner -encoders` list next to the
      server's `HardwareAccelerationType` before writing it up as anything.

### The DTO flags the plugin cannot write

A **dynamic** source is the plugin's own object, and its flags are what the provider wrote:
`SupportsDirectPlay: false`, `SupportsDirectStream: false`, `SupportsTranscoding: true`. A
**materialised version item** is not: its `MediaSources` entry is a `MediaSourceInfo` the server
constructs itself (in `BaseItem.GetVersionInfo`) out of the item's fields, and that constructor opens
every capability flag at `true`. The plugin is never asked, and the item model offers no hook that
would let it answer.

One of those flags does have a rule attached to it, and the rule is about something else: the server
sets `SupportsDirectStream = false` for a video whose type is *not* a video file. A version item that
claims a disc loses the flag through that rule; one declaring `VideoFile` - the value the
version-item pass repairs a drifted item back to - keeps the constructor's answer, and nothing in the
plugin can lower it from here:

| Flag on a materialised version item's source | Value | Why it says that |
| --- | --- | --- |
| `SupportsDirectPlay` | `true` | constructor default; nothing in the item model clears it for an HTTP-protocol path |
| `SupportsDirectStream` | `true` | computed from path and protocol, and an HTTP path that is not an `.m3u` passes that test |
| `SupportsTranscoding` | `true` | correct, and the one flag of the three the plugin would choose itself |

- [ ] Record the two `true` values above as **expected**, not as a FAIL. They are the server's own
      defaults on a source the plugin does not build. The same version offered dynamically - a
      profile the library has not materialised, or one whose item could not be created - still
      reports `SupportsDirectPlay: false`.
- [ ] Do not treat them as a playback risk to chase in this pass. A client that believed
      `SupportsDirectPlay` here would aim at the item's `Path`, which is a marker URL: the item is
      locked, its protocol is HTTP, and there is no file behind a marker to serve. What keeps a
      version encoding is the `mvc` codec it reports (above), and the evidence that it held is the
      child command and the segments it wrote (V7), not these flags.
- [ ] If a real client is ever seen honouring one of these two flags for a version item - requesting
      a direct play or a direct stream of a marker - record the client, the flag and the request URL
      as a finding. The fix for that is in the server's item model (a static source whose path is not
      a file), and the plugin has no lever on it short of not materialising items at all.

## V6. Marker transport

A marker is a URL-shaped namespace. It must survive transport as exactly one argument token
and must never be fetched over HTTP.

Canonical shape:

```text
http://127.0.0.1/anaglyfin/profile/<profileId>?source=<percent-encoded-rooted-path>&video=<index>
```

Example for:

```text
/movies/Movie (2010)/Movie.2010.3D.mkv
```

```text
http://127.0.0.1/anaglyfin/profile/anaglyph_arcd?source=%2Fmovies%2FMovie%20%282010%29%2FMovie.2010.3D.mkv&video=0
```

The `video` parameter names the source's video stream by its own stream index - `MediaStream.Index`
in the stream report, the number of that stream inside the file, and therefore the number
Jellyfin spends on its `-map 0:<index>`. It is not the position the stream happens to sit at in
the reported list: the two are the same number only while the list is in file order, and a
report that skips a stream the file carries (or adds one it does not) separates them. The
parameter is what lets the wrapper recognize the server's video map and remove it beside the
profile's own; without it the server's numeric video map survives next to the profile's, and
the output carries two videos. A source whose video stream carries no known index omits the
parameter rather than guessing one, and keeps the codec report that forces the encode.

MVP markers do not include `subtitle`:

```text
?subtitle=<ordinal>
```

That field is part of the marker contract, but the provider does not yet attach a subtitle
ordinal.

- [x] The provider's `MediaSourceInfo.Path` contains a canonical marker.
- [x] The percent-encoded `source` query parameter contains the rooted library file path.
- [x] The `video` query parameter is the index of the video stream in that source's
      `MediaStreams` list.
- [x] The wrapper process receives the marker as one unbroken argv token.
- [x] The real FFmpeg child process receives the decoded real source path.
- [x] The marker text does **not** appear in the real FFmpeg child command.
- [ ] No network request is made to `127.0.0.1/anaglyfin/profile/...`.
- [x] Wrapper refusal logs do not echo the marker URL or the source path.

Useful capture command:

```sh
ps -ww -eo pid,ppid,args | grep -E 'Anaglyfin.FFmpegWrapper|ffmpeg' | grep -v grep
```

Containers usually ship no `ps`; the same information is in `/proc`, from the host or from
inside the container:

```sh
docker compose exec jellyfin sh -c \
  'for p in /proc/[0-9]*; do tr "\0" " " < "$p/cmdline" 2>/dev/null | grep -q ffmpeg \
     && tr "\0" " " < "$p/cmdline" && echo; done'
```

Both processes also live in the server's transcode log, which is the artifact to capture
rather than a screenshot - `<log dir>/transcode_<n>.log`, and `/config/log` for a container
that sets `JELLYFIN_LOG_DIR`:

```sh
ls -t /config/log/transcode_*.log | head -1 | xargs head -n 40
```

Expected process relationship:

```text
jellyfin
  Anaglyfin.FFmpegWrapper ... -i http://127.0.0.1/anaglyfin/profile/... ...
    ffmpeg-mvc ... -i /movies/Movie (2010)/Movie.2010.3D.mkv ...
```

A marker-splitting failure is a blocker: a marker token that is broken, decoded, normalized
away, or passed to HTTP is not safe to rewrite.

## V7. Profile command expectations

Capture the real child FFmpeg command while playback starts. The wrapper should replace the
marker token and insert the profile's output arguments after the last input. Server-owned
encoder, muxer, HLS, and output arguments should survive.

Before any per-profile fragment, check that the command is an encode at all. A profile converts
pictures; a command that copies the video stream has decided not to produce one.

- [x] The child command carries a video encoder: `-codec:v <name>` / `-c:v <name>` naming
      something like `libx264`, `libx265`, `h264_qsv`, `hevc_nvenc`.
- [x] The child command does **not** carry a video copy: no `-codec:v copy`, `-c:v copy`,
      `-c:v:0 copy`, `-codec:v:0 copy` or `-vcodec copy`.
- [x] The server's encode stack came with it: a bitrate or quality argument, a preset, and the
      HLS keyframe arguments. Their absence next to a video copy means the server stream-copied
      the version, and the profile fragments below will be present but inert.
- [ ] The transcoding URL the server built for the version names a video codec, and does not
      carry `allowVideoStreamCopy=false` as the only reason it encodes - the provider's job is
      to report a codec that cannot be copied, not to ask the client for a favour.
- [ ] If the wrapper refused the job instead, its line says so and names the reason:

  ```text
  anaglyfin-wrapper: refused: the command was not started (ServerChoseVideoCopy). ...
  ```

  A refusal with that classification is a plugin finding, not a configuration problem: it means
  the server still chose video copy for a source the provider said it had to encode. Capture the
  PlaybackInfo response (V5) and the full argv.

Useful capture command:

```sh
ps -ww -eo pid,ppid,args | grep -E 'Anaglyfin.FFmpegWrapper|ffmpeg-mvc' | grep -v grep
```

The exact server command may contain more Jellyfin arguments than the examples below. Look
for the required inserted fragments.

### 7.1 Full SBS

Profile id:

```text
sbs_full
```

Expected inserted fragments:

```text
-view_ids -1  (immediately before the marker input's -i)
```

Expected behavior:

- the composed all-view picture is selected before the input is opened
- no Anaglyfin `-vf` filter is inserted for full SBS itself
- a server-owned existing `-vf` chain is not removed
- ordinary video and audio maps chosen by Jellyfin remain intact
- subtitle maps the server wrote stay, and `-sn` is not inserted: this profile converts the
  picture and renders no text onto it. A `-sn` appears only beside a burn-in, which means the
  marker carried a subtitle ordinal

This is the profile the geometry fix was found on, so it is also the one where a wrong answer is
visible without any filter of ours in the command: the profile inserts no scaling at all, so
whatever the server's `scale` does to the frame is what the segment ends up being. See V7.8.

Example shape:

```text
-view_ids -1 -i <real source path> -map 0:v -map 0:a ...
```

### 7.2 Half SBS

Profile id:

```text
sbs_half
```

Expected inserted fragments:

```text
-view_ids -1  (immediately before the marker input's -i)
-vf scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p
```

- [ ] The `setsar=sar=1` is there, immediately behind the scale.

      `scale` preserves the display aspect by adjusting the pixel shape it hands on, so halving
      the width of a square-pixel frame hands on a 2:1 one. The encoded pixels are the right
      half-SBS frame either way, but every consumer that trusts the shape travelling with the
      frame stretches it sideways, so the declaration is part of the conversion and not a
      cosmetic touch.

- [ ] If Jellyfin already supplied `-vf`, the profile chain is **prepended** to the last existing
      chain rather than replacing it or landing behind it. The server's scale is sized from the
      geometry this source reports - the converted frame - and runs against whatever frame
      reaches it, so a conversion placed behind it scales a picture that does not exist yet.
      Nothing of the server's chain is deleted: it carries the client's resolution ceiling and
      the bitrate ladder's rung, and at native quality it evaluates to an identity.

### 7.3 Red/cyan Dubois anaglyph

Profile id:

```text
anaglyph_arcd
```

Expected inserted fragments:

```text
-view_ids -1  (immediately before the marker input's -i)
-vf stereo3d=sbsl:arcd,format=yuv420p
```

If Jellyfin already supplied `-vf`, the expected merged chain shape is:

```text
stereo3d=sbsl:arcd,format=yuv420p,<existing chain>
```

The same ordering rule as half SBS, with the same reason: profile conversion first, server
scaling of the converted picture second. See 7.9 for the same ordering inside a server
`-filter_complex` graph, which is the shape a burned-in image subtitle arrives as.

### 7.4 2D base

Profile id:

```text
two_d_base
```

Expected behavior:

- marker is replaced by the real source path
- no Anaglyfin `-view_ids`, `-map`, `-vf`, `-filter_complex`, or `-sn` is inserted
- Jellyfin's own maps and filters remain untouched

This is expected to be the least intrusive profile.

### 7.5 Custom grayscale anaglyph

Profile id:

```text
custom_grayscale
```

With the shipped default colors, the inserted composed-view input and filter graph should be:

```text
-view_ids -1  (immediately before the marker input's -i)
-filter_complex [0:v]split=2[anaglyfin_cg_left_in][anaglyfin_cg_right_in];[anaglyfin_cg_left_in]crop=iw/2:ih:0:0,format=gray,format=rgb24,colorchannelmixer=rr=1:gg=0:bb=0[anaglyfin_cg_left];[anaglyfin_cg_right_in]crop=iw/2:ih:iw/2:0,format=gray,format=rgb24,colorchannelmixer=rr=0:gg=1:bb=1[anaglyfin_cg_right];[anaglyfin_cg_left][anaglyfin_cg_right]blend=all_mode=screen,format=yuv420p[anaglyfin_custom]
-map [anaglyfin_custom]
```

`-map [anaglyfin_custom]` is written only when the server's own command does not already feed that
label to the encoder: if the server had a `-filter_complex` of its own and the merge retargeted its
source references onto `[anaglyfin_custom]`, the label is already the picture the server maps, and
mapping it again would be a second video in the output.

When the marker carries `video=<index>`, the graph's source label is `[0:<index>]` rather than
`[0:v]`. The command must not contain `0:v:view` in any form: the composed request replaces the
view-selector route.

- [ ] Left/right colors from the admin page become numeric `colorchannelmixer` coefficients,
  not arbitrary text.
- [ ] The command does not contain an Anaglyfin marker token.
- [ ] A foreign `-filter_complex` in the same output segment is merged, not grafted onto: the
  Anaglyfin graph goes in as the graph's first chain and the server's own source references are
  retargeted onto `[anaglyfin_custom]` (see 7.9). It is refused only where no merge is possible -
  a graph read from `-filter_complex_script`, one that never names this input's video stream, one
  that reaches it through a view specifier, several graphs at once, or one already carrying this
  profile's label.
- [ ] A server-owned `-vf` on the same command survives the rewrite unchanged. This profile's
  conversion is a graph of its own rather than a stage of that chain, so the ordering rule of
  7.2/7.3 does not apply to it and nothing is re-plumbed around it.

Known limitation of this profile's shape, recorded rather than fixed: FFmpeg refuses a simple
`-vf` on a stream fed from a complex filtergraph ("Simple and complex filtering cannot be used
together for the same stream"). The profile maps the graph's own label, so any `-vf` the server
writes for that output - which it writes whenever the request settles on a `MaxWidth`/`MaxHeight`,
even one the profile already satisfies - is refused by FFmpeg itself. Half SBS, the anaglyph
presets and 2D base do not have this problem because their conversion is a linear chain the server
chain continues. Capturing the exact argv for a failing `custom_grayscale` job, together with the
server's `-vf` value, is the evidence the next runtime task needs.

### 7.6 Map-less commands

Some Jellyfin transcode commands carry no `-map`. A linear profile still inserts no map: it
converts the composed picture delivered by whatever video stream FFmpeg selects automatically.
Only the custom graph profile names a new output stream; when it does so on a map-less command,
the rewriter also inserts:

```text
-map 0:a?
```

Expected custom-grayscale shape:

```text
-view_ids -1 -i <real source path> -filter_complex <Anaglyfin graph> -map [anaglyfin_custom] -map 0:a? ...
```

- [ ] Map-less full SBS, half SBS, and red-cyan commands insert no Anaglyfin stream map and keep FFmpeg's automatic audio selection.
- [ ] Map-less custom grayscale keeps audio through the explicit optional audio map.
- [ ] A command that already had audio maps is not given an extra optional audio map.
- [ ] 2D base does not invent stream maps.

### 7.7 The server's numbered maps

Jellyfin's HLS commands name the streams they chose by number: `-map 0:<index>`, the number
being that stream's index inside the file. For a linear profile, the map the marker's
`video=<index>` names is the stream the profile converts, so the wrapper preserves it. Only the
custom graph profile replaces that source map with its graph label; every other map remains
somebody else's stream.

- [ ] A linear profile's rewritten command carries the server's ordinary or numbered video map
      unchanged; it does not add a second Anaglyfin video map.
- [ ] A custom-grayscale rewrite removes the server's `-map 0:<index>` for the marker's video,
      including the optional `-map 0:<index>?` spelling, and maps only `[anaglyfin_custom]`.
- [ ] The server's numbered audio and subtitle maps are still there, in the order the server
      wrote them: a conversion that renders no text has no reason to touch either. (A rewrite that
      burns its own text in is the one that takes subtitle maps out, and a rewrite whose own
      filter text already renders the subtitles - a burn-in filter or a graph reading `[0:s]` -
      takes nothing out even then.)
- [ ] Exclusion maps survive: `-map -0:a`, `-map -0:s` and a bare `-map -0` are the server
      taking streams out of the output, and removing one would put the stream back -
      subtitles under a profile that burns its own in being the case that matters.
      (`-map -0:v` is the one video exclusion the wrapper takes away: on a linear profile it
      would delete the stream being converted, and on the graph profile it would subtract the
      graph output.)
- [ ] A 2D base command keeps every map the server wrote: that profile owns no video pipeline
      and takes nothing away.

Expected shape for a red-cyan version of a source whose video is stream 0 and audio is
stream 1:

```text
-view_ids -1 -i <real source path> -vf stereo3d=sbsl:arcd,format=yuv420p -map 0:0 -map 0:1 -codec:v libx264 ...
```

### 7.8 Segment dimensions: the geometry the whole chain agrees on

The command line says what was asked for; the segments say what came out. Measure one segment per
version with the same `ffprobe` the server uses:

```sh
ffprobe -v error -select_streams v:0 \
  -show_entries stream=width,height,sample_aspect_ratio,display_aspect_ratio \
  -of default=noprint_wrappers=1 \
  "http://<server>:8096/Videos/<item-id>/<media-source-id>/main/1.ts?api_key=<key>"
```

For a 1920x1080 MVC source, encoded at a quality setting that asks for no downscale (a client with
auto/high bitrate, or a `maxWidth`/`maxHeight` at or above the values in the second column):

| Version | Expected segment `width x height` | Expected `sample_aspect_ratio` |
| --- | --- | --- |
| `sbs_full` | `3840 x 1080` | `1:1` |
| `sbs_half` | `1920 x 1080` | `1:1` |
| `anaglyph_*` | `1920 x 1080` | `1:1` |
| `custom_grayscale` | `1920 x 1080` | `1:1` |
| `two_d_base` | `1920 x 1080` | `1:1` |

- [ ] Full SBS segments are **source width x 2** by **source height**, and no server shrink has been
      applied on top of the conversion.
- [ ] Half SBS and every anaglyph version are **source width** by **source height**.
- [ ] Half SBS reports `sample_aspect_ratio=1:1` and not `2:1`. A `2:1` there means the
      `setsar=sar=1` stage did not run (or ran before the scale, where it is overwritten), and
      players will stretch the frame sideways.
- [ ] The server's `scale` ran **after** the profile conversion. Read it off the command from V7:
      the merged `-vf` value starts with the profile's own filters and the server's chain follows.
      At native quality the server's scale is then an identity, which is exactly what the table
      above is. If the server's chain came first, a full-SBS version of this source measures
      `1920 x 540` - the right aspect ratio, half the pixels - and that number is a filter-order
      bug and nothing else.
- [ ] A segment smaller than the table is only expected when the *client* asked for it: a device
      ceiling (`maxWidth`/`maxHeight` in the transcoding URL) or a bitrate ladder rung. Note which
      one and its value; an Anaglyfin version of a 1080p film clamped to 1280 wide by an 8 Mbit
      bandwidth setting is policy working, not the geometry report failing. The bug shape is a
      segment that is smaller than the geometry report *while the aspect ratio is wrong too*, or a
      segment that is smaller while the request asked for native quality.
- [ ] Recording the segment size of one version without recording the `-vf` value it came from is
      not evidence: the two together are what show the order.

### 7.9 The server's own filter graph and its subtitles

The shape that made this worth testing end to end: Jellyfin burning an image subtitle (PGS) into
the picture itself. Its command carries the subtitle stream through a scale into `[sub]`, the
source video through its colour and scale chain into `[main]`, and overlays the two:

```text
-filter_complex [0:10]scale=1920:1080:flags=area[sub];[0:0]setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,scale=1920:1080:...,format=yuv420p[main];[main][sub]overlay=eof_action=pass:repeatlast=0[out]
-map [out] -map 0:1 -map -0:s
```

The profile's conversion belongs at the head of that graph - in front of the server's own scale,
which is sized for the converted frame - and the server's reference to the source video is
retargeted onto what the conversion produced. For a red-cyan version of a source whose video is
stream 0, with subtitle depth **off** - the `Flat` dropdown position, which asks for nothing:

```text
-filter_complex [0:0]stereo3d=sbsl:arcd,format=yuv420p[anaglyfin_profile];[0:10]scale=1920:1080:flags=area[sub];[anaglyfin_profile]setparams=...,format=yuv420p[main];[main][sub]overlay=eof_action=pass:repeatlast=0[out]
-view_ids -1 -i <real source path>            (in front of the input)
```

With subtitle depth **on** for the same converting profile, the recognized image-subtitle graph is
rewritten instead of merely merged. The subtitle chain and overlay are removed, and the depth filter
takes their place between the composed picture and the profile conversion:

```text
-filter_complex [0:0]format=rgba[anaglyfin_composed];[0:10]format=rgba[anaglyfin_subtitle];[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=auto:eof_action=pass[anaglyfin_depth];[anaglyfin_depth]stereo3d=sbsl:arcd,format=yuv420p[anaglyfin_profile];[anaglyfin_profile]setparams=...,format=yuv420p[out]
-view_ids -1 -i <real source path>
```

For full SBS, whose conversion is the composed decode itself, the depth output feeds the server's
non-subtitle chain directly:

```text
-filter_complex [0:0]format=rgba[anaglyfin_composed];[0:10]format=rgba[anaglyfin_subtitle];[anaglyfin_composed][anaglyfin_subtitle]mvcsubdepth=depth=auto:eof_action=pass[anaglyfin_depth];[anaglyfin_depth]setparams=...,format=yuv420p[out]
-view_ids -1 -i <real source path>
```

- [ ] The job runs. A version of a film with a burned-in subtitle used to fail before FFmpeg was
      started, with `IncompatibleFilterGraph` and exit code `65`.
- [ ] With depth off, the profile chain is the graph's first chain and the server's chains follow
      it, so the profile converts before the server scales.
- [ ] With depth off, the subtitle chain, the overlay and the label the output maps are the server's,
      byte for byte: only the label naming the source video moved.
- [x] With depth on, the launched `-filter_complex` carries one `mvcsubdepth` stage and its exact
      `depth=` option: `depth=auto`, `depth=shift=<pixels>`, or `depth=plane=<index>`.
- [ ] With depth on, the original subtitle chain and `overlay` are gone: the subtitle is rendered by
      the depth filter, not again flat on top of it.
- [ ] Stream `10` is still addressed by the graph, now as the subtitle input to the depth stage: a
      subtitle stream is not this source's video, whatever number it carries.
- [ ] No `-sn`, and the server's subtitle maps and exclusions are where it wrote them - the server
      is already rendering this text, and a conversion has nothing to say about it.
- [ ] Still one `-i`, one decode: the merge writes a chain, not a second input.
- [ ] A text subtitle the server burns in through `-vf subtitles=filename='<marker>'` keeps the
      server's filter and loses the marker: the sweep that keeps marker text out of the command
      reaches inside the filter value, and writes the real source path in the same escaping the
      server used.
- [x] With depth on, a graph rendering text itself through `subtitles=` or `ass` is **not** rewritten.
      The playback keeps the server's flat text and the wrapper writes a `warning: subtitle depth
      was asked for and not applied` diagnostic naming the text filter.
- [ ] Full SBS with depth off leaves the graph alone - it has no chain to contribute - and still gets
      its `-view_ids -1`; with depth on it receives the composed-picture depth graph above.
- [ ] If the settings document is absent, unreadable, or carries an out-of-range value, the wrapper
      behaves as if depth were off.

Where no merge is possible the wrapper refuses, and the log says which rule the command broke:
a graph read from `-filter_complex_script`; a graph that never names this input's video stream; a
graph that reaches it through a view specifier (`[0:v:view:all]`), which a composed decode refuses
outright; several graphs in one command; or a graph already carrying `[anaglyfin_profile]` or
`[anaglyfin_custom]`.

### 7.10 A hardware-accelerated server: the server's hardware arguments pass through

Run one profile with the server's `HardwareAccelerationType` set to `qsv`, `nvenc` or `vaapi` and
hardware encoding on, and capture the child command the way V7 describes. Compare it token by token
against the command the server built for that item; the difference has to be the profile's own
fragments plus the marker replacement, and nothing else. This is what a version's command looks like on
a host where the FFmpeg the server runs has that encoder compiled in - which is the part a plugin
cannot arrange, since a marker input has to be decoded by the FFmpeg-mvc build and that build's encoder
list is the ceiling. A command that comes out with no device options and `-codec:v libx264` on such a
server is that case, not a rewrite failure; check the binary's `-encoders` list before checking
anything here:

```text
-init_hw_device qsv=qsv:/dev/dri/renderD128 -filter_hw_device qsv -hwaccel qsv
-hwaccel_output_format qsv -view_ids -1 -i <real source path>
-map 0:0 -map 0:1 -c:v:0 h264_qsv ... -vf format=nv12,hwupload=derive_device=qsv,... <output>
```

- [x] Everything the server wrote in front of the marker's `-i` is still in front of it, the decode
      selection included: `-hwaccel`, `-hwaccel_output_format`, `-hwaccel_device`, `-hwaccel_args`,
      `-hwaccel_flags`, and on a VA-API server the `-vaapi_device <path>`. The wrapper removes none of
      them, for any profile, `two_d_base` included - a command missing them has regressed to cp17.
- [x] The device the **encode** runs on is still there as well: `-init_hw_device ...` and
      `-filter_hw_device ...`.
- [x] The one argument the wrapper added on that side of the `-i` is `-view_ids -1`, and it sits
      immediately in front of the option that opens the file - after the server's hardware arguments,
      not in place of them.
- [ ] Read FFmpeg's own output for what the decoder decided. On a multiview stream the build says
      `H.264/MVC: hardware acceleration is not supported, falling back to software decoding` once and
      carries on in software; that line is the fallback doing its job, and it is FFmpeg's line rather
      than an `anaglyfin-wrapper:` one. Nothing in the command had to be altered for it to appear.
- [ ] An ordinary (non-MVC) item transcoded on the same server with the same binary shows no such line
      and keeps its hardware decode. That is the same gate answering the other question, and it is the
      reason the wrapper does not answer it in advance - see V8.
- [x] The video encoder is the server's own, and on this host a hardware one: `h264_qsv`, `hevc_qsv`,
      `h264_nvenc`, `h264_vaapi`, and so on. Anaglyfin names no encoder and never forces `libx264`, so
      a command carrying `libx264` here is the binary answering the gate - the check above - and not a
      rewrite the wrapper performed.
- [ ] The filters that carry a frame to that encoder survive: `hwupload`,
      `hwupload=derive_device=...`, `hwmap`, `vpp_qsv`, `scale_qsv`, `format=nv12` - in the server's
      `-vf` chain, or inside its `-filter_complex` where the server wrote them. The profile's chain
      is still in front of them, which is what makes them upload the converted picture.
- [x] The profile's own fragments are where V7.1-V7.9 put them: `-view_ids -1` in front of `-i` for
      every converting profile, none for `two_d_base`, the profile's chain or graph merged in front of
      the server's.
- [ ] The segments still measure what V7.8 says they measure. The wrong picture arrives without an
      error, so this is the check that notices it - and it is the check that would notice a decode
      chosen wrongly as well.
- [ ] With the server's hardware acceleration **off**, this section asks for nothing: the command is
      the one from V7.1-V7.7, with the same encoder and no device options, because there was nothing
      there to pass through.

Nothing in this section is a refusal, and nothing in it produces a diagnostic: the wrapper's work on
such a command is the same work it does on any marker command, and the hardware arguments ride along
untouched. FFmpeg's output naming a device it opened is expected - that is the encoder's device.

## V8. Ordinary playback pass-through

The wrapper sits on the server's FFmpeg path, so it must be invisible for ordinary playback.

Play a non-MVC item that will be transcoded, or force transcoding.

- [x] The wrapper starts the real FFmpeg binary with the received arguments unchanged.
- [x] Jellyfin hardware decode options from the server remain present if configured: no marker is in
      this command, so nothing about its decode is Anaglyfin's to change. V7.10 asks for the same
      patience on the commands that do carry a marker, where those arguments pass through as well.
- [x] No Anaglyfin `-view_ids` option is inserted for a non-marker command.
- [x] No Anaglyfin `-vf`, `-filter_complex`, or `-sn` is inserted for a non-marker command.
- [ ] Ordinary playback does not create an Anaglyfin concurrency slot file.
- [x] No wrapper refusal line appears for ordinary playback.

Expected pass-through log behavior:

```text
no "anaglyfin-wrapper:" diagnostic for successful ordinary playback
```

## V9. Concurrency limit

Set the admin page's **Maximum concurrent Anaglyfin transcodes** to `1`, or use the optional
environment override on the server environment that starts wrappers:

```sh
# Optional only: leave unset to exercise the admin-page setting through the settings document.
ANAGLYFIN_MAX_CONCURRENT_TRANSCODES=1
ANAGLYFIN_LOCK_DIR=<shared writable directory>
```

Start one Anaglyfin profile version.

- [x] While the Anaglyfin job runs, a slot file exists under `ANAGLYFIN_LOCK_DIR`.
- [ ] The slot file name has the shape:

  ```text
  anaglyfin-transcode-0.lock
  ```

- [ ] The slot file content records a pid and claim timestamp, if the platform permits
  reading it while held.
- [x] Starting a second Anaglyfin version while the first is running is refused by the
  wrapper before FFmpeg is started.
- [x] The wrapper exits with code `75` for the concurrency refusal.
- [x] The wrapper diagnostic starts with:

  ```text
  anaglyfin-wrapper: refused: 1 Anaglyfin transcode(s) are already running, which is the configured maximum, so FFmpeg was not started. Raise the maximum concurrent Anaglyfin transcodes on the Anaglyfin settings page, or ANAGLYFIN_MAX_CONCURRENT_TRANSCODES to override it from the deployment, or remove slot files left in the directory ANAGLYFIN_LOCK_DIR names if a wrapper was killed without exiting.
  ```

- [ ] After the first job finishes, the slot file disappears.
- [ ] Raising the admin-page limit, or raising a valid optional
  `ANAGLYFIN_MAX_CONCURRENT_TRANSCODES` override, raises the number of slots and the second
  Anaglyfin job starts.

Slot behavior notes:

- The wrapper uses file existence as the claim.
- Slot files are opened with delete-on-close semantics, so normal process exit and most
  killed-process cases release the slot automatically.
- Young leftover files are treated as busy. Only files older than 24 hours and provably
  ownerless are considered abandoned.

## V10. Current limitations to record, not fix

These are known current limitations unless the checked-out branch has since merged the
relevant follow-up work.

### Signal forwarding

Current state:

- Forwarding is implemented in the shipped wrapper: `SIGTERM`, `SIGINT` and `SIGHUP`
  that reach the wrapper reach the encoder child (the branch that first carried it,
  `task/T8-wrapper-signal-forwarding`, merged long ago), and unit tests pin the three.

Validation expectation:

- [x] Record observed behavior when a transcode is stopped or cancelled. The cp30 pass
  recorded a direct `SIGTERM`: the wrapper terminated and the encoder stopped at a
  partial file.
- [ ] Do not record the partial file left by a deliberately stopped encode as a defect:
  stopping the encoder where it stands is what forwarding the signal is for.

### Device defaults (removed before release)

Current state:

- There are no device defaults. The one default is the global one, offered first to every
  client, and the provider resolves it from the settings without consulting anything about the
  request. The exact-device form of this feature existed only in pre-release builds and was
  removed before release: no admin-page row carries it, and nothing reads the request's
  `Jellyfin-DeviceId` claim. V5.2 is where a real client's answer is recorded against the
  global default.
- No approximate device category - TV, phone, tablet, headset, projector - is inferred. Jellyfin
  12 has no reliable native server-side device type to key a category on, so a category could
  only ever be a labelled heuristic over what a client claims about itself. That is deferred
  future work; it is not implemented, and it cannot claim accurate handling of 3D TVs, VR
  headsets, or 3D-capable projectors.
- A pre-release settings file may still carry `DeviceDefaultProfiles` entries. They load without
  error, decide neither the default nor the offered order, and are gone from the file on the next
  save. No migration runs on disk and none is needed: the entries are inert from the moment this
  build reads the file.
- A device id is the client's own invention - regenerable, re-registrable, and claimable by
  anything on the network that bothers to claim it. At best it could key an ordering preference
  for the pre-release builds that used it, never a security boundary. Nothing that decides what
  may be played, or by whom, reads it, and this build reads nothing off the request at all.

Validation expectation:

- [ ] The ordering itself belongs to V5.2; nothing here asks for it twice.
- [ ] An upgrade from a pre-release build that stored device rows is recorded through V5.2's
      stale-entry check - the entries load, decide nothing, and the next save drops them - not
      as a regression.
- [ ] Do not record any device-category behavior as expected, supported, or validated. None is
      implemented, and the platform would not make the claim accurate if it were.

### Original MVC version switch (recorded boundary)

Current state:

- With `Offer original 3D MVC version` unticked, the raw MVC source is left out of the two lists a
  version is picked from. A client that has the id already can still ask for it by id; a client that
  does not have it cannot discover it from those lists until the setting is ticked back on.
- That is the shape of a read filter, and it is not a bug to fix by writing to the library: the item,
  its alternate-version link, its paths and its resume state are what the scanner left them, and no
  lever that changed them could be reversed by a checkbox.

Validation expectation:

- [x] Record the boundary through V5.3, including the source id seen on both sides of the toggle.
- [x] Do not record the hidden entry as a deleted, unlinked, ignored or hidden library item. If a run
      finds the item itself changed, that is a defect in the filter; write it up as one.
- [x] Do not treat the switch as an access control. It decides which entries are listed, and never what
      a user may play.

### Subtitles

Current state:

- The provider currently creates markers without a subtitle ordinal.
- A profile rewrite touches subtitles only where it renders them itself: with a burn-in it adds
  `-sn` and removes the server's subtitle maps, and without one it leaves the server's subtitle
  selection - maps, exclusions, and the subtitle streams its own filter graph reads - alone.
- Subtitle selection in a client does not yet produce a burned-in subtitle filter.
- FFmpeg-mvc subtitle depth is one dropdown with four positions: `Automatic`, `ConstantShift`,
  `Plane`, and `Flat`. `Flat` is the position that asks for nothing - the wrapper adds no
  `mvcsubdepth` stage at all. A fresh installation ships on `Automatic`; a server upgrading from a
  build whose depth was switched off, or that predates the feature, is migrated to `Flat` on load, so
  an upgrade never turns depth on by itself.
- The admin page's subtitle-depth request travels through the settings document named by
  `ANAGLYFIN_WRAPPER_SETTINGS`, alongside the admin page's concurrency limit. The wrapper's view of
  that document is read once per invocation and cannot be changed during a running transcode.

Validation expectation:

- [ ] The subtitle tracks copied from the original source may still appear in client UI.
- [ ] Anaglyfin versions do not currently burn in a selected subtitle.
- [x] Converted profile commands contain neither `-sn` nor a `subtitles=` filter while no ordinal
      is wired through, and the server's own subtitle handling - its maps, its `-map -0:s`, its
      burn-in filter or overlay graph - survives the rewrite.
- [ ] A converted version of a film the server is already burning subtitles into still shows those
      subtitles (see 7.9): this is the check that the old blanket `-sn`, which muted them, is gone.
- [x] With depth enabled, the command carries the requested `mvcsubdepth` mode and the subtitle is
      rendered by that stage rather than by the server's overlay.
- [x] With depth enabled but no supported shape, the command starts the server's original graph and
      the wrapper's log says the request was not applied.

Supported image-subtitle shapes for depth:

```text
converting profile:
  composed picture -> mvcsubdepth -> profile conversion -> server non-subtitle chain

full SBS:
  composed picture -> mvcsubdepth -> server non-subtitle chain
```

Both shapes require one numbered image-subtitle stream feeding one `overlay`. Other shapes fall back
without corrupting the server's graph: text renderers such as `subtitles=` or `ass`, several subtitle
pictures, several overlays, missing server video chains, a profile whose only work is a burn-in, and
graphs whose labels or sources this wrapper cannot prove. The fallback keeps the server's flat
subtitles and writes a diagnostic; it is not a command refusal.

Expected command behavior today:

```text
no -sn for a converting profile, because nothing renders text on its picture
no "subtitles=filename=" inserted by Anaglyfin for the MVP provider path
mvcsubdepth only when the admin request is not Flat, the graph shape is supported, and the target
FFmpeg-mvc binary carries the mvcsubdepth filter
```

### Subtitle depth on hardware-accelerated hosts

Current state:

- The depth stage is inserted into the server's filter graph; it is not QSV-specific and no QSV
  hardware-frame path is claimed by this documentation.
- CI proves the rewritten command shapes, settings transport, and fallback warnings, but it does not
  run a real FFmpeg-mvc binary, a real QSV device, or real MVC depth output.

Validation expectation:

- [x] On QSV/NVENC/VA-API hosts, the server's hardware arguments still pass through unchanged (see
      V7.10), and the depth stage appears in the filter graph only for the supported shapes above.
- [ ] Record any depth result observed on real hardware as a manual runtime result. Until that result
      is recorded, do **not** mark QSV or real MVC subtitle depth as validated.

### Packaging

Current state:

- The CI job packs the plugin archive, generates `meta.json` from `Plugin.manifest.xml`, stages
  the self-contained linux-x64 wrapper, and verifies the layout, every metadata field, the
  recorded size and SHA-256, and the wrapper's ELF header.
- Publishing to the **official** Jellyfin plugin catalog remains out of scope. The project serves
  its own repository instead: `.github/workflows/release.yml` turns an already-existing `vX.Y.Z`
  tag into a GitHub release carrying the packaged artifacts and republishes the manifest at
  `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json`, the URL
  `docs/install.md` tells administrators to add. `v0.1.0` is published, so that manifest and the
  release assets behind it are live: the catalog lists Anaglyfin `0.1.0` and the release carries
  the plugin archive, the wrapper, and `SHA256SUMS.txt`.

Validation expectation:

- [x] Manual installation from the packaged artifacts is recorded as the troubleshooting install
  path, following `docs/install.md`.
- [x] The extracted plugin directory holds `Anaglyfin.dll` and nothing the archive should not ship.

## V11. Wrapper refusal behavior

The wrapper has three normal outcomes:

```text
PassedThrough -> start the received command unchanged
Rewritten     -> start the rewritten command, after taking an Anaglyfin slot
Refused       -> do not start FFmpeg
```

Expected exit codes:

| Code | Constant | Meaning |
| --- | --- | --- |
| `0` | `ExitCodeSuccess` | FFmpeg or the wrapper completed successfully |
| `65` | `ExitCodeMarkerRejected` | Marker/command was refused by the rewriter |
| `70` | `ExitCodeInternalError` | Wrapper could not reach a decision |
| `75` | `ExitCodeConcurrencyLimitReached` | Anaglyfin concurrency limit was full |
| `127` | `ExitCodeRealFFmpegNotStarted` | Real FFmpeg binary missing, not executable, or misconfigured |

Expected refusal log shape:

```text
anaglyfin-wrapper: refused: ...
```

Refusal safety requirements:

- [x] A broken or malformed marker-shaped token is refused rather than started.
- [x] Two valid markers in one command are refused.
- [x] A marker that is not the first input is refused.
- [x] A profile that owns the video pipeline refuses a graph it cannot merge: one read from a
  `-filter_complex_script` file, one that never names this input's video stream, one that reaches
  it through a view specifier, several graphs at once, or one already carrying an Anaglyfin label.
  A server graph that simply reads the source video is merged, not refused (see 7.9).
- [x] A profile that owns the video pipeline refuses a command that copies its video
  (`ServerChoseVideoCopy`) instead of running a pipeline that would convert nothing.
- [x] Refusal diagnostics do not echo marker URLs, query parameters, or media paths.
- [x] Refusal diagnostics name configured environment variables when the fix is administrator
  configuration.

Do not assume the real runtime can easily reach these paths through normal provider-created
versions. The provider only emits valid markers. These paths are mostly unit-tested; manual
checks are optional unless a failure shows a provider-created marker being refused.

## Result log

| Item | Area | Expected | Result | Notes |
| --- | --- | --- | --- | --- |
| V0 | CI and artifacts | CI green; DLL and wrapper binary present | PASS | CI container `cp30-formal-v0-ci` off the cp27 worktree: build succeeded with 0 warnings and 0 errors, 1419 tests passed (0 failed, 0 skipped), and packaging verified against `src/Anaglyfin/Plugin.manifest.xml`. The packager requires `Anaglyfin.dll` at the archive root and rejects a nested or extra layout. |
| V1 | Plugin discovery | Anaglyfin appears in Jellyfin plugins | PASS | `/Plugins` lists Anaglyfin `0.1.0.0` with `Status=Active`, the configuration endpoint returns HTTP 200, and the settings page renders at the plugin's admin page; no assembly-load or target-framework errors. The description string and the `/web/ConfigurationPages` `EnableInMainMenu` flag were not re-read in this pass, so those two rows stay open. |
| V2 | Admin page | Page renders and settings round-trip | PARTIAL | `GET` then unchanged `POST` then `GET` returned the same configuration (HTTP 204), and raising **Maximum concurrent Anaglyfin transcodes** to 2 rewrote the shared wrapper document to schema v2 with `maxConcurrentTranscodes=2`, restored afterwards to 1. The stored XML carries no device-default collection. Not validated here: the page's rendered wording, the direct out-of-dashboard URL, the empty-profile set (the page normalises it to the shipped set, so it cannot be saved from a browser - unit-pinned), upgrading a stored subtitle-depth or MVC-toggle value, and applying the default to a real client, which is V5.2. |
| V3 | Wrapper deployment | Jellyfin -> wrapper -> FFmpeg-mvc works | PASS | Official `ffmpeg-mvc` SHA-256 `93185e34400658121de60221d19e10d7b4d19218fe099d415271ab549bd83e7e`, banner `ffmpeg version n8.1.2-mvc7-jf4`; wrapper SHA-256 `cf607a93bc962caee3a07800c849267c83ee52f0f09b335ca22724183137a652`; `JELLYFIN_FFMPEG` points at the Anaglyfin entry point; `mvcsubdepth` and `h264_qsv` are in the official binary; the settings document is schema v2. ffprobe placement is evidenced by a library that scans and probes normally. |
| V4 | Detection | MVC-positive items offer versions; negatives do not | PASS | The stacked item carried exactly three marker sources plus the raw MVC source; the generated `- 3D`, `- HSBS` and plain files carried none. Not exercised here: a non-video item and `.strm` / disc-image items, which stay unchecked rather than passing by assumption. |
| V5 | Media sources | GUID source ids per media source, stacked alternate versions, a video codec no profile can stream-copy, the global default ordering of V5.2, and the original MVC version toggle of V5.3 | PARTIAL | Marker URLs have the documented shape, the default profile orders the stack as documented, converted versions are transcode-only, and V5.3's toggle passed end to end. Left open: every V5.2 row, which the document reserves for real clients; the reported frame size and `Video3DFormat` rows; the row asking the source's own video codec to read `mvc`, which this pass did not re-read directly; and resume across a version switch. See the entry below. |
| V6 | Marker transport | Marker survives one token, names the video stream, never reaches FFmpeg child | PASS | The wrapper received the marker as one input token and the argv handed to FFmpeg carried the decoded `/media/...` path with zero `anaglyfin/profile` occurrences (capture `/config/anaglyfin/capture/cp23/argv-405-1790027021.log`). A positive observation that nothing fetched the marker URL over HTTP was not recorded, so that row stays open. |
| V7 | Profile commands | Required profile fragments appear behind a real video encoder | PARTIAL | Composed commands captured: Red/Cyan puts the profile conversion with `-view_ids -1` in front of the real encoder plus `stereo3d=sbsl:arcd`, Half SBS applies the half-width scale, Full SBS receives raw view selection with no conversion graph, 2D Base is an ordinary pass-through, and the QSV init and encoder arguments survive. Not covered here: `custom_grayscale`, the map-less and numbered-map shapes, and V7.8's segment table - the probe binary in this container cannot load `libass.so.9`, which is a probe-environment limit and not a product result. |
| V8 | Pass-through | Ordinary playback remains unchanged | PASS | A forced ordinary 1080p HLS transcode ran through the wrapper on QSV with no marker and no Anaglyfin graph, and delivered its master playlist, main playlist and first segment (HTTP 200). That an ordinary job leaves no slot file behind was not read directly, so that one row stays open. |
| V9 | Concurrency | Slot file appears; second job exits `75`; release works | PASS | The safe probe ran two wrapper jobs against the admin-published limit 1: exactly one lock slot existed, and the second process exited `75` with a refusal naming the configured maximum and both places an administrator can raise it. Slot-file naming and contents, release after the first job exits, and raising the limit until a second job starts were not re-run here and are pinned by unit tests. |
| V10 | Current limitations | Limitations are recorded, not mistaken for regressions | PARTIAL | Implemented behaviour: PGS under Automatic composed `mvcsubdepth=depth=auto:eof_action=pass`; a text subtitle keeps the server's own filter path with no depth stage and an explicit wrapper warning; the stored XML has no device-default collection and no device category is inferred; a direct `SIGTERM` stopped the wrapper and its child at a partial file; QSV encoding was exercised. Recorded as limitations and not failures: what a physical 3D client displays and playback against a remote target, and the depth a rendered subtitle actually reaches. This environment is a headless local server. |
| V11 | Refusals | Invalid marker-shaped commands never start FFmpeg | PASS | Direct wrapper tests returned exit `65` for a malformed marker, two marker inputs, a marker that is not the first input, a server-selected video copy (`ServerChoseVideoCopy`) and `-filter_complex_script` (`IncompatibleFilterGraph`). Every diagnostic named its reason and none echoed the marker URL or a media path. The remaining merge-refusal shapes are unit-pinned rather than reproduced. |

## Result log entry - 2026-09-21, formal V0-V11 on cp30

**Artifact:** `main` @ `6c5a1da`, tag `cp30-mvc-toggle-docs`.
**Environment:** a headless local Jellyfin 12 container on an Intel Arc iGPU (QSV). There is no
physical 3D client and no remote target on this host, which is the reason the client-facing and
remote rows below are left open rather than ticked.

Everything this host can decide was read off the artifact and the running server. CI (V0) was
clean from the cp27 worktree and the packager confirmed the archive layout the rest of these
checks assume; the plugin loaded and its settings round-tripped through the plain `GET`/`POST`
API (V1, V2), with the concurrency limit travelling to the shared wrapper document as a schema-v2
write and back. The deployment identity (V3) - the official `n8.1.2-mvc7-jf4` with `mvcsubdepth`,
the wrapper standing in as `JELLYFIN_FFMPEG`, and one settings document and one lock directory
shared by every wrapper - is the shape V6 through V9 depend on, so it was pinned before anything
played.

The behaviour the plugin exists to provide came next. Detection (V4) offered exactly the stacked
MVC file and stayed silent about `- 3D`, `- HSBS` and a plain file. The marker reached the wrapper
as a single token and left the FFmpeg command as a decoded media path with no marker text in it
(V6). The composed commands for Red/Cyan, Half SBS, Full SBS and 2D Base sat behind a real QSV
encoder with the server's own hardware arguments untouched (V7, V7.10), an ordinary 1080p job went
through the same wrapper with nothing added and still delivered its playlists and first segment
(V8), and two wrapper jobs against the admin limit of 1 produced one lock slot and a second exit
`75` (V9). The original MVC toggle (V5.3) was read on both surfaces of the same request, with the
raw source's id and path reappearing unchanged when the switch went back on and PlaybackInfo
refusing a hidden source id by id - the boundary V10 records rather than a defect. The wrapper's
five refusals (V11) each came back exit `65`, naming the reason and echoing nothing.

Three table rows are deliberately **PARTIAL**, and none of them is a failure:

- **V5.** The real-client first-source and per-client ordering (V5.2) is the document's own
  reservation for a client, so every row in it stays open; and the row asking that an Anaglyfin
  source's own video codec read back as `mvc` was not confirmed here - this run recorded the
  converted versions advertising `h264`, so the codec row is left for a direct re-read rather than
  ticked on inference.
- **V7.** `custom_grayscale` and the map-less and numbered-map shapes were not run, and V7.8's
  segment dimensions were not measured, because this container's `ffprobe` cannot load
  `libass.so.9`. That is a probe-environment limit recorded here, not a result about the profiles.
- **V10.** What a physical 3D client displays, playback against a remote target, and the depth a
  rendered subtitle actually reach are all outside a headless server, so subtitle depth on
  hardware is not marked validated and no device-category behaviour is claimed at all.

A few rows are left to CI on purpose because they cannot be reached from a browser or a normal
request: an empty enabled-profile set, which the settings page normalises back to the shipped set
(V2, V5.3), and the wrapper's less common merge-refusal shapes (V11). Both are pinned by unit
tests, and the CI run at the head of this log covers them.

This disposable host was left as the run found it - the validation containers and the generated
test media remain for a later pass to clean up, and no client-side or remote validation was
attempted here.

## Result log entry - 2026-09-22/23, Docker runtime install and Intel hardware encode (cp35-cp36)

**Artifact:** the direct-`exec` install flow of `docs/install.md` as it now stands, with
`ffmpeg-mvc`, `ffprobe` and the wrapper installed inside a running `jellyfin/jellyfin:latest`
(container work on `task/T45-runtime-install-in-jellyfin-container` and
`task/T46-document-gpu-device`).
**Environment:** the same headless local host and Intel Arc iGPU, now with the host GPU mapped
into the service (`devices: - /dev/dri`). There is still no physical 3D client and no remote
target on this host, and nothing below changes any client-facing or remote-target row above.

What was exercised is the install and runtime shape the Docker page documents, not end-to-end
product playback:

- **RUNPATH against the image's own libraries.** `ffmpeg-mvc` and `ffprobe` were compiled inside
  the container with `-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib` (both `--extra-ldflags` and
  `--extra-ldexeflags`); the RUNPATH was verified present in both binaries and `ldd` reported
  no `not found`.
- **apt is build-time only.** After a full `docker compose down && docker compose up -d`, the
  recreated container had no build packages installed, yet the three runtime files survived
  under `/config`, every runtime dependency resolved against the library set the official image
  ships in `/usr/lib/jellyfin-ffmpeg/lib`, `ffmpeg-mvc` and `ffprobe` ran, Jellyfin reported
  `/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg` as its FFmpeg, and the container stayed healthy.
- **The Intel driver comes from the image; the GPU comes from Docker.** The image carries the
  `iHD`/`i965` VA-API drivers and the `libva`/`libvpl` runtime, but the GPU appeared only once
  the compose service mapped `/dev/dri` - the mapping `docs/install.md` now documents as the
  requirement for hardware encoding.
- **Hardware encodes through the installed wrapper.** After that `down`/`up` with zero apt
  packages: `h264_qsv` initialised a real MFX session (implementation version `2.17`) and
  produced a real H.264 file; `h264_vaapi` reported `va_openDriver() returns 0` against the
  iHD driver (`Intel iHD driver for Intel(R) Gen Graphics - 26.2.4`) and encoded 60 frames
  with the input frames context in `vaapi` format.
- **A VA-API caveat, recorded:** a manually issued plain `h264_vaapi` command needs
  `hwupload=extra_hw_frames=64` on this build. Jellyfin's own VA-API transcode path already
  emits that flag, so the documented product flow is unaffected; the plain-flag case that
  failed was the hand-written command, not the product.

These are synthetic single-encode runs against the installed runtime files: no Jellyfin
playback drove them and no rendered picture was inspected. Subtitle depth on hardware stays
unvalidated (V10), every V5.2 row stays open, and the PARTIAL statuses recorded above are
unchanged by this entry.
