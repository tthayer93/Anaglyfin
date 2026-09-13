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
- rewritten FFmpeg command lines for the main Anaglyfin profiles
- wrapper pass-through behavior for ordinary Jellyfin playback
- concurrency limit behavior and wrapper refusal exit codes
- known MVP limitations: subtitles, device defaults, encoder policy, and signal forwarding

Out of scope:

- automated integration tests
- packaging as an official Jellyfin plugin repository package
- hardware encoder policy enforcement, which is not wired through the current wrapper path
- per-device default application through playback-info device context, which is not wired
  through the current media-source provider
- signal forwarding, which is being handled separately in `task/T8-wrapper-signal-forwarding`

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

- [ ] CI passes from the intended branch:

  ```sh
  docker compose --env-file .env -f .ci/test.yml run --rm test
  ```

  Expected result:

  ```text
  anaglyfin CI gate: restore + build(-warnaserror) + test + format + packaging all passed
  ```

- [ ] Plugin project builds to a loadable `Anaglyfin.dll`.
- [ ] Wrapper project builds to an executable binary for the server's runtime.
- [ ] The plugin manifest is embedded and declares:

  ```text
  name: Anaglyfin
  guid: c7f4a1d9-3b58-4e2a-9d6c-84f0b1e5a723
  version: 0.1.0
  targetAbi: 12.0.0
  framework: net10.0
  ```

Notes:

- The CI job packages the plugin archive, the install record, and the linux-x64 wrapper binary
  into `artifacts/`, and verifies all three. Where they go on a server is `docs/install.md`;
  manual installation from those artifacts is still the expected install path.
- The wrapper is a normal .NET executable, not a Jellyfin plugin. It must be runnable by
  the Jellyfin server user.

## V1. Plugin installation and discovery

Install the plugin by placing the built `Anaglyfin.dll` in the server's configured plugin
directory, or by using your local plugin repository workflow. `docs/install.md` gives the paths
for a bare-metal server and for a container that mounts `./jellyfin/config` at `/config`.
Restart or reload Jellyfin after installation.

- [ ] Jellyfin lists the plugin with name `Anaglyfin`.
- [ ] The plugin id shown by the server matches
  `c7f4a1d9-3b58-4e2a-9d6c-84f0b1e5a723`.
- [ ] The plugin description reads:

  ```text
  Exposes 3D MVC sources to Jellyfin as selectable playback versions.
  ```

- [ ] The dashboard's own settings menu lists `Anaglyfin` as an entry beside the server's
  settings. The page asks to be listed there, so a server whose menu omits it is a server
  where the settings cannot be reached at all.
- [ ] `Dashboard -> Plugins -> Anaglyfin` reaches the same page.
- [ ] Opening the settings page does not produce a blank page or an unstyled fragment.
- [ ] The server does not log assembly-load errors, missing dependency errors, or
  target-framework mismatch errors for `Anaglyfin.dll`.

If Jellyfin does not discover the plugin, stop and record this as the first blocker. Every
later runtime check depends on discovery.

## V2. Admin settings page

Open the page by either route:

```text
Dashboard -> settings menu -> Anaglyfin
Dashboard -> Plugins -> Anaglyfin
```

The first is the entry the page exists in the dashboard for; the second is the same page
through the plugin list. Record which one was used.

Check the rendered page against the shipped defaults:

| Field | Shipped default | Validation |
| --- | --- | --- |
| Default profile | `3D Anaglyph Red/Cyan (Dubois)` (`anaglyph_arcd`) | Shown and selectable |
| Fallback profile | `2D Base` (`two_d_base`) | Shown and selectable |
| Enabled profiles | shipped MVP set | Red/Cyan Dubois, full SBS, half SBS, and 2D Base are enabled by default |
| Device and client defaults | none | UI allows adding/removing rows |
| Custom left eye colour | `#FF0000` | Color input defaults correctly |
| Custom right eye colour | `#00FFFF` | Color input defaults correctly |
| Maximum concurrent Anaglyfin transcodes | `1` | Number input defaults correctly |
| Video encoder policy | `Automatic` | Select exists |

- [ ] Save succeeds and the settings round-trip after reopening the page.
- [ ] `GET /web/ConfigurationPages` reports the page with `EnableInMainMenu: true`, which is
  the flag the v12 web dashboard needs before it will list the page anywhere.
- [ ] Opening the page's own URL directly - outside the dashboard, so with no signed-in
  client - shows the shipped defaults behind a warning naming the dashboard route, and
  saving refuses without writing anything.
- [ ] The wrapper environment guidance block lists:

  ```text
  ANAGLYFIN_REAL_FFMPEG
  FFMPEG_MVC_PATH
  ANAGLYFIN_MAX_CONCURRENT_TRANSCODES
  ANAGLYFIN_LOCK_DIR
  ```

- [ ] Disabling all enabled profiles is not accepted as an empty offer: saving that state
  should come back as the shipped enabled set.
- [ ] Changing the default profile changes the first Anaglyfin version offered to a client
  for an eligible MVC item.
- [ ] The device/client defaults UI saves rows, but see V10 for the current limitation:
  the provider does not yet apply those rows.
- [ ] The encoder policy saves, but see V10 for the current limitation: it is stored and
  not yet enforced in the wrapper or generated command.

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
ANAGLYFIN_MAX_CONCURRENT_TRANSCODES:
Jellyfin FFmpeg path setting:
```

Required deployment shape:

```text
Jellyfin -> wrapper executable -> ANAGLYFIN_REAL_FFMPEG -> FFmpeg-mvc
Jellyfin -> real ffprobe, in the wrapper's own directory
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

- [ ] FFmpeg-mvc `jellyfin-8.1` is installed and executable by the Jellyfin service user.
- [ ] FFprobe from the same FFmpeg-mvc build is installed, executable, **in the same
  directory as the wrapper**, because that is where the server will look for it.
- [ ] The wrapper executable is deployed to a stable path and executable by the Jellyfin
  service user.
- [ ] The server's FFmpeg path points at the wrapper, not directly at FFmpeg-mvc, if
  wrapper-based rewriting is expected.
- [ ] The server can still probe files with the real `ffprobe`.
- [ ] The wrapper's real FFmpeg resolution is one of:

  ```text
  ANAGLYFIN_REAL_FFMPEG=/path/to/ffmpeg-mvc
  FFMPEG_MVC_PATH=/path/to/ffmpeg-mvc
  ffmpeg found through PATH
  ```

- [ ] Wrapper environment variables are set in the environment that Jellyfin passes to
  started helper processes. Setting them only in the administrator's login shell is not
  sufficient.
- [ ] `ANAGLYFIN_LOCK_DIR` is writable by the Jellyfin service user.
- [ ] For container deployments, every wrapper process that should share one concurrency
  limit sees the same lock directory.

Example environment block, adjusted to your deployment:

```sh
ANAGLYFIN_REAL_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg-mvc
ANAGLYFIN_MAX_CONCURRENT_TRANSCODES=1
ANAGLYFIN_LOCK_DIR=/tmp/anaglyfin/ffmpeg-wrapper
```

Notes:

- The wrapper checks the configured real binary and refuses if it is missing or points back
  at the wrapper itself.
- The wrapper resolves the real FFmpeg binary in this order:
  1. `ANAGLYFIN_REAL_FFMPEG`
  2. `FFMPEG_MVC_PATH`
  3. `ffmpeg` found on `PATH`

## V4. MVC detection and item eligibility

Use at least one positive MVC item and two negative items:

```text
positive: Movie.2010.3D.1080p.MVC.mkv
negative: Movie.2010.3D.1080p.HSBS.mkv
negative: Movie.2010.3D.1080p.mkv
```

The detector is intentionally conservative:

- `Video3DFormat=MVC` metadata is enough by itself.
- Explicit MVC markers in the file name, item name, item tags, or containing folder make an
  item eligible.
- Plain `3D` does not make an item eligible.
- SBS/TAB names without an MVC marker are ineligible.
- Text that merely contains `mvc` inside a longer word is rejected.

- [ ] A file with explicit `MVC` in the file name produces Anaglyfin versions after a scan
  or metadata refresh.
- [ ] A file with only `3D` does not produce Anaglyfin versions.
- [ ] A file named `HSBS`, `SBS`, `TAB`, or similar but without an MVC marker does not
  produce Anaglyfin versions.
- [ ] A non-video item does not produce Anaglyfin versions.
- [ ] `.strm` items and disc-image-like items do not produce Anaglyfin versions.

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
Anaglyfin offers no versions for <item name>: <decision>.
```

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

1. The configured default profile, promoted first.
2. The remaining enabled profiles in catalog display order.

For the default installation, the expected Anaglyfin order is therefore:

```text
1. 3D Anaglyph Red/Cyan (Dubois)
2. 3D Full Side-by-Side
3. 3D Half Side-by-Side
4. 2D Base
```

Notes:

- The server may keep the item's original source first in the client's version list.
- `DeviceDefaultProfiles` are saved by the admin page but are not applied by the current
  media-source provider, because the provider receives no device/client context.

For each alternate source, check:

| Field | Expected |
| --- | --- |
| `Id` | GUID, lower-case `N` format, derived from the item id and the profile id |
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

- [ ] The Anaglyfin source ids are lower-case `N`-format GUIDs, unique per profile and different from the item id.
      DynamicHLS parses `MediaSourceId` as a `Guid`, so a descriptive id fails playback before FFmpeg starts.
- [ ] The source id changes when the profile id changes.
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
- [ ] The original library source remains playable and unchanged.
- [ ] Changing enabled profiles changes the offered versions without a server restart.

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

- [ ] The provider's `MediaSourceInfo.Path` contains a canonical marker.
- [ ] The percent-encoded `source` query parameter contains the rooted library file path.
- [ ] The `video` query parameter is the index of the video stream in that source's
      `MediaStreams` list.
- [ ] The wrapper process receives the marker as one unbroken argv token.
- [ ] The real FFmpeg child process receives the decoded real source path.
- [ ] The marker text does **not** appear in the real FFmpeg child command.
- [ ] No network request is made to `127.0.0.1/anaglyfin/profile/...`.
- [ ] Wrapper refusal logs do not echo the marker URL or the source path.

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

- [ ] The child command carries a video encoder: `-codec:v <name>` / `-c:v <name>` naming
      something like `libx264`, `libx265`, `h264_qsv`, `hevc_nvenc`.
- [ ] The child command does **not** carry a video copy: no `-codec:v copy`, `-c:v copy`,
      `-c:v:0 copy`, `-codec:v:0 copy` or `-vcodec copy`.
- [ ] The server's encode stack came with it: a bitrate or quality argument, a preset, and the
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
-map 0:v:view:all
-sn
```

Expected behavior:

- native all-view output is selected
- no Anaglyfin `-vf` filter is inserted for full SBS itself
- a server-owned existing `-vf` chain is not removed
- audio maps chosen by Jellyfin remain intact

This is the profile the geometry fix was found on, so it is also the one where a wrong answer is
visible without any filter of ours in the command: the profile inserts no scaling at all, so
whatever the server's `scale` does to the frame is what the segment ends up being. See V7.8.

Example shape:

```text
-i <real source path> -map 0:v:view:all -sn -map 0:a ...
```

### 7.2 Half SBS

Profile id:

```text
sbs_half
```

Expected inserted fragments:

```text
-map 0:v:view:all
-vf scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p
-sn
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
-map 0:v:view:all
-vf stereo3d=sbsl:arcd,format=yuv420p
-sn
```

If Jellyfin already supplied `-vf`, the expected merged chain shape is:

```text
stereo3d=sbsl:arcd,format=yuv420p,<existing chain>
```

The same ordering rule as half SBS, with the same reason: profile conversion first, server
scaling of the converted picture second.

### 7.4 2D base

Profile id:

```text
two_d_base
```

Expected behavior:

- marker is replaced by the real source path
- no Anaglyfin `-map`, `-vf`, `-filter_complex`, or `-sn` is inserted
- Jellyfin's own maps and filters remain untouched

This is expected to be the least intrusive profile.

### 7.5 Custom grayscale anaglyph

Profile id:

```text
custom_grayscale
```

With the shipped default colors, the inserted filter graph should be:

```text
-filter_complex [0:v:view:all]split=2[anaglyfin_cg_left_in][anaglyfin_cg_right_in];[anaglyfin_cg_left_in]crop=iw/2:ih:0:0,format=gray,format=rgb24,colorchannelmixer=rr=1:gg=0:bb=0[anaglyfin_cg_left];[anaglyfin_cg_right_in]crop=iw/2:ih:iw/2:0,format=gray,format=rgb24,colorchannelmixer=rr=0:gg=1:bb=1[anaglyfin_cg_right];[anaglyfin_cg_left][anaglyfin_cg_right]blend=all_mode=screen,format=yuv420p[anaglyfin_custom]
-map [anaglyfin_custom]
-sn
```

- [ ] Left/right colors from the admin page become numeric `colorchannelmixer` coefficients,
  not arbitrary text.
- [ ] The command does not contain an Anaglyfin marker token.
- [ ] A foreign `-filter_complex` in the same output segment causes the wrapper to refuse the
  job, not graft the Anaglyfin graph onto it.
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

Some Jellyfin transcode commands carry no `-map`. When a profile inserts a video map, the
rewriter also inserts:

```text
-map 0:a?
```

Expected shape:

```text
-i <real source path> -map 0:v:view:all -map 0:a? -sn ...
```

- [ ] Map-less full SBS, half SBS, red-cyan, and custom grayscale commands keep audio.
- [ ] A command that already had audio maps is not given an extra optional audio map.
- [ ] 2D base does not invent stream maps.

### 7.7 The server's numbered maps

Jellyfin's HLS commands name the streams they chose by number: `-map 0:<index>`, the number
being that stream's index inside the file. When a profile owns the video pipeline, the map the
marker's `video=<index>` names is the server's video map, and the wrapper removes it; every
other map is somebody else's stream.

- [ ] The rewritten command carries exactly one video map, the profile's own.
- [ ] The server's `-map 0:<index>` for the video is gone, including the optional
      `-map 0:<index>?` spelling.
- [ ] The server's numbered audio and subtitle maps are still there, in the order the server
      wrote them.
- [ ] Exclusion maps survive: `-map -0:a`, `-map -0:s` and a bare `-map -0` are the server
      taking streams out of the output, and removing one would put the stream back -
      subtitles under a profile that burns its own in being the case that matters.
      (`-map -0:v` is the one exclusion the profile does take away: written after the
      profile's own video map it would subtract that map and leave an output with no picture.)
- [ ] A 2D base command keeps every map the server wrote: that profile owns no video pipeline
      and takes nothing away.

Expected shape for a red-cyan version of a source whose video is stream 0 and audio is
stream 1:

```text
-i <real source path> -map 0:v:view:all -vf stereo3d=sbsl:arcd,format=yuv420p -sn -map 0:1 -codec:v libx264 ...
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

## V8. Ordinary playback pass-through

The wrapper sits on the server's FFmpeg path, so it must be invisible for ordinary playback.

Play a non-MVC item that will be transcoded, or force transcoding.

- [ ] The wrapper starts the real FFmpeg binary with the received arguments unchanged.
- [ ] Jellyfin hardware decode options from the server remain present if configured.
- [ ] No Anaglyfin `-map 0:v:view:all` is inserted for a non-marker command.
- [ ] No Anaglyfin `-vf`, `-filter_complex`, or `-sn` is inserted for a non-marker command.
- [ ] Ordinary playback does not create an Anaglyfin concurrency slot file.
- [ ] No wrapper refusal line appears for ordinary playback.

Expected pass-through log behavior:

```text
no "anaglyfin-wrapper:" diagnostic for successful ordinary playback
```

## V9. Concurrency limit

Set:

```sh
ANAGLYFIN_MAX_CONCURRENT_TRANSCODES=1
ANAGLYFIN_LOCK_DIR=<shared writable directory>
```

Start one Anaglyfin profile version.

- [ ] While the Anaglyfin job runs, a slot file exists under `ANAGLYFIN_LOCK_DIR`.
- [ ] The slot file name has the shape:

  ```text
  anaglyfin-transcode-0.lock
  ```

- [ ] The slot file content records a pid and claim timestamp, if the platform permits
  reading it while held.
- [ ] Starting a second Anaglyfin version while the first is running is refused by the
  wrapper before FFmpeg is started.
- [ ] The wrapper exits with code `75` for the concurrency refusal.
- [ ] The wrapper diagnostic starts with:

  ```text
  anaglyfin-wrapper: refused: 1 Anaglyfin transcode(s) are already running
  ```

- [ ] After the first job finishes, the slot file disappears.
- [ ] Raising `ANAGLYFIN_MAX_CONCURRENT_TRANSCODES` raises the number of slots and the second
  Anaglyfin job starts.

Slot behavior notes:

- The wrapper uses file existence as the claim.
- Slot files are opened with delete-on-close semantics, so normal process exit and most
  killed-process cases release the slot automatically.
- Young leftover files are treated as busy. Only files older than 24 hours and provably
  ownerless are considered abandoned.

## V10. Current limitations to record, not fix

These are known current-state limitations unless the checked-out branch has since merged the
relevant follow-up work.

### Signal forwarding

Current state:

- Wrapper signal forwarding is being implemented in a separate branch:
  `task/T8-wrapper-signal-forwarding`.
- It is not merged into `task/T7-validation-docs` at the time this document is written.

Validation expectation:

- [ ] Record observed behavior when a transcode is stopped or cancelled.
- [ ] Do not treat missing explicit signal forwarding as a T7 code defect.
- [ ] Re-validate after T8 is merged.

### Device and client defaults

Current state:

- Settings and admin UI can store `DeviceDefaultProfiles`.
- The profile catalog can resolve them, but `AnaglyfinMediaSourceProvider` does not pass a
  device/client context into that resolution.

Validation expectation:

- [ ] Device/client overrides persist in settings.
- [ ] The current media source provider still orders versions by the global default.
- [ ] Record this as a provider follow-up, not a broken admin save.

### Subtitles

Current state:

- The provider currently creates markers without a subtitle ordinal.
- Converted profiles suppress subtitle streams with `-sn`.
- Subtitle selection in a client does not yet produce a burned-in subtitle filter.

Validation expectation:

- [ ] The subtitle tracks copied from the original source may still appear in client UI.
- [ ] Anaglyfin versions do not currently burn in a selected subtitle.
- [ ] Converted profile commands contain `-sn` and do not contain a `subtitles=` filter unless
  a later follow-up has wired the ordinal through.

Expected command behavior today:

```text
-sn
no "subtitles=filename=" inserted by Anaglyfin for the MVP provider path
```

### Encoder policy

Current state:

- `EncoderPolicy` is stored and exposed by the admin page.
- The current generated profile commands do not encode policy into encoder arguments.
- MVC decoding stays software-only; hardware decode choices for ordinary playback remain
  with Jellyfin.

Validation expectation:

- [ ] The policy persists.
- [ ] It does not currently change the Anaglyfin generated command.
- [ ] Record this as a follow-up, not as a hidden behavior in this milestone.

### Packaging

Current state:

- The CI job packs the plugin archive, generates `meta.json` from `Plugin.manifest.xml`, stages
  the self-contained linux-x64 wrapper, and verifies the layout, every metadata field, the
  recorded size and SHA-256, and the wrapper's ELF header.
- Publishing as an official Jellyfin plugin repository package is still out of scope: there is no
  release feed, and `meta.json` carries no download URL or timestamp.

Validation expectation:

- [ ] Manual installation from the packaged artifacts is recorded as the current install path,
  following `docs/install.md`.
- [ ] The extracted plugin directory holds `Anaglyfin.dll` and nothing the archive should not ship.

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

- [ ] A broken or malformed marker-shaped token is refused rather than started.
- [ ] Two valid markers in one command are refused.
- [ ] A marker that is not the first input is refused.
- [ ] A profile that owns the video pipeline refuses when the command already carries a foreign
  `-filter_complex` or `-filter_complex_script`.
- [ ] A profile that owns the video pipeline refuses a command that copies its video
  (`ServerChoseVideoCopy`) instead of running a pipeline that would convert nothing.
- [ ] Refusal diagnostics do not echo marker URLs, query parameters, or media paths.
- [ ] Refusal diagnostics name configured environment variables when the fix is administrator
  configuration.

Do not assume the real runtime can easily reach these paths through normal provider-created
versions. The provider only emits valid markers. These paths are mostly unit-tested; manual
checks are optional unless a failure shows a provider-created marker being refused.

## Result log

| Item | Area | Expected | Result | Notes |
| --- | --- | --- | --- | --- |
| V0 | CI and artifacts | CI green; DLL and wrapper binary present |  |  |
| V1 | Plugin discovery | Anaglyfin appears in Jellyfin plugins |  |  |
| V2 | Admin page | Page renders and settings round-trip |  |  |
| V3 | Wrapper deployment | Jellyfin -> wrapper -> FFmpeg-mvc works |  |  |
| V4 | Detection | MVC-positive items offer versions; negatives do not |  |  |
| V5 | Media sources | GUID source ids, and a video codec no profile can stream-copy |  |  |
| V6 | Marker transport | Marker survives one token, names the video stream, never reaches FFmpeg child |  |  |
| V7 | Profile commands | Required profile fragments appear behind a real video encoder |  |  |
| V8 | Pass-through | Ordinary playback remains unchanged |  |  |
| V9 | Concurrency | Slot file appears; second job exits `75`; release works |  |  |
| V10 | Current limitations | Limitations are recorded, not mistaken for regressions |  |  |
| V11 | Refusals | Invalid marker-shaped commands never start FFmpeg |  |  |
