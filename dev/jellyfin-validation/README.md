# Anaglyfin runtime validation harness

A disposable Docker stack that runs the packaged plugin and the packaged FFmpeg wrapper
inside the image the real test server runs in - `jellyfin/jellyfin:latest`, Jellyfin
`12.0.0`, Debian 13 - with the same mount shape the real server uses:

```text
./jellyfin/config -> /config
./jellyfin/cache  -> /cache
<media dir>       -> /media
```

Nothing is installed into the image and nothing in the image is replaced: the plugin
arrives as one mounted assembly, the wrapper as one mounted executable, and the server is
pointed at the wrapper with `JELLYFIN_FFMPEG`. That is `docs/install.md`'s container
install, executed rather than described, so a PASS here transfers to a real container
mounted the same way.

This directory is not part of the CI gate. The CI gate proves the code builds, tests
pass, formatting holds, and the artifacts pack and verify. It cannot see a server.

## What this harness proves, and what it cannot

Proves here, on your machine, before anyone touches a real server:

| Check | Evidence |
| --- | --- |
| The CI artifacts are the files a server accepts | `Loaded plugin: Anaglyfin 0.2.0.0` in the container log |
| Jellyfin 12 accepts the wrapper as its FFmpeg | `MediaEncoder: FFmpeg: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg` and `Found ffmpeg version 8.1.2` in the container log |
| The linux-x64 wrapper artifact executes in this image | `sh harness.sh check`, which also asserts pass-through, marker rewrite, and refusal inside the image |
| A missing real FFmpeg is loud, not silent | the server refuses to start with `FFmpeg validation: ... Path set by command line or environment variable is invalid` |
| FFprobe has to sit next to the wrapper | `Running /config/anaglyfin/ffmpeg/ffprobe ... No such file or directory` until it does |

Needs a client and real media, so needs you:

- that a client lists the Anaglyfin versions and picks the first one,
- that the marker `Path` survives Jellyfin's playback-info transport for a *real* playback,
- that FFmpeg-mvc decodes MVC and the profile output looks right on a TV,
- that hardware encode for ordinary playback still behaves as it did before the wrapper,
- the concurrency limit under two real clients, subtitle behaviour, and resume/seek UX.

Run the checklist in `docs/validation.md` against this stack where it can be answered
here, and against the real server for the rest.

## Layout

```text
compose.jellyfin.yml        the stack: one Jellyfin, mounts, wrapper environment
compose.qsv.yml             optional overlay: /dev/dri for Intel QSV
compose.host-network.yml    optional overlay: host network, for daemons out of bridge pools
harness.sh                  preflight / up / check / status / logs / ffprobe / collect / down
wrapper-check.sh            the in-image wrapper assertions, run by the check service
.env.example                every knob, with its default
jellyfin/, media/           state created by a run; ignored by git, delete freely
```

## Step 1 - build the artifacts

From a checkout of this repository:

```sh
docker compose --env-file .env -f .ci/test.yml run --rm test
```

The gate ends with the packaging steps, which leave three files in `artifacts/` of the
checkout that ran them (`artifacts/` is ignored, so a worktree is a good place to build):

| File | Where the harness mounts it |
| --- | --- |
| `src/Anaglyfin/bin/Release/net10.0/Anaglyfin.dll` | `/config/plugins/Anaglyfin/Anaglyfin.dll` |
| `artifacts/anaglyfin-ffmpeg` | `/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg` |
| `artifacts/Anaglyfin_0.2.0.zip` | not mounted - it contains exactly the DLL above, for servers you install by unpacking instead of mounting |

`.env` in the repository root is a local file, not a tracked one; see the comment at the
top of `.ci/test.yml` for `SRC_DIR` and `BUILD_CONTEXT`. The harness reads its own
`.env` in this directory, so the two never collide.

## Step 2 - assert the wrapper inside the image (optional, fast, no server)

```sh
sh harness.sh check
```

which is this, spelled out:

```sh
docker compose -f compose.jellyfin.yml run --rm wrapper-check
```

The service starts `jellyfin/jellyfin:latest` with only the wrapper mounted and runs
`wrapper-check.sh`, which answers one question: does the CI artifact actually run - and
actually decide - inside the image Jellyfin runs in? Its real FFmpeg is the image's own
`/bin/echo`, which prints the arguments it was started with, so the three wrapper
outcomes become text. One line per assertion, so this run is the shape to expect, with a
few of the twenty-odd lines omitted:

```text
PASS  mounted as an executable file (100761100 bytes)
PASS  ELF magic and 64-bit little-endian class
PASS  ELF machine is x86-64
PASS  the binary executes in this image (missing-real-FFmpeg refusal reached)
PASS  pass-through kept -hwaccel vaapi
PASS  the child received the decoded source path
PASS  no marker text reached the child process
PASS  the sbs_full rewrite inserted -view_ids -1
PASS  the sbs_full rewrite inserted -sn
PASS  the sbs_full rewrite left no view specifier beside -view_ids
PASS  an unknown profile is refused with exit 65
PASS  pass-through reached the image's FFmpeg at /usr/lib/jellyfin-ffmpeg/ffmpeg
OK: the wrapper artifact runs and decides correctly in this image (0 check(s) skipped)
```

No GPU, no media, no server, and nothing installed, so it is the check to run first: a
missing artifact or a checkout that lost the executable bit shows up here instead of half
way through a playback test.

## Step 3 - start the stack

```sh
sh harness.sh up
```

`up` runs `preflight` first and refuses to start with anything missing, because a bind
mount whose source does not exist is created for you as an empty root-owned *directory* -
and a server that finds a directory where an assembly belongs is a confusing afternoon.

It pulls `jellyfin/jellyfin:latest` on first use, which is a few hundred MB.

Two overlays, as switches:

```sh
HARNESS_QSV=1 sh harness.sh up             # /dev/dri into the container
HARNESS_HOST_NETWORK=1 sh harness.sh up    # no published port, for daemons that will not
                                           # create another bridge network
```

Both overlays and their trade-offs are in the files themselves. Any other knob - image,
port, media directory, concurrency limit, real-FFmpeg path - is a variable in `.env`;
copy `.env.example` and edit it.

<details>
<summary>When the Docker daemon sees a different path than your shell does</summary>

A bind mount's source is read by the *daemon*, and an agent container, a remote daemon, or
a daemon inside VM (Docker Desktop) may see this checkout under another mount point -
the same reason `.ci/test.yml` carries `BUILD_CONTEXT`. Then compose happily creates an
empty root-owned directory on the daemon's side of the wall and the server finds nothing.

Write the daemon's paths in `.env` and teach `harness.sh` how to translate them back for
its checks:

```sh
HARNESS_DAEMON_PREFIX=/mnt/workspace/Anaglyfin/.worktrees/agent-T10
HARNESS_LOCAL_PREFIX=/workspace/Anaglyfin/.worktrees/agent-T10
HARNESS_PLUGIN_DLL=/mnt/workspace/Anaglyfin/.worktrees/agent-T10/src/Anaglyfin/bin/Release/net10.0/Anaglyfin.dll
```

Anywhere the two views are the same - a normal server, a normal developer machine - leave
both prefixes unset.

</details>

## Step 4 - confirm what the server actually picked up

```sh
sh harness.sh logs          # Ctrl-C stops following, not the stack
```

Expected lines on a healthy start, in this order:

```text
Loaded assembly Anaglyfin, Version=0.2.0.0, ... from /config/plugins/Anaglyfin/Anaglyfin.dll
Loaded plugin: Anaglyfin 0.2.0.0
MediaBrowser.MediaEncoding.Encoder.MediaEncoder: Found ffmpeg version 8.1.2
MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
```

That pair of `MediaEncoder` lines is the whole deployment contract in two lines: the
server believes its FFmpeg *is* the wrapper, and the wrapper let the server's version and
capability probes through to the real binary. No `anaglyfin-wrapper:` line belongs in a
healthy start.

Then, from the host:

```sh
docker compose -f compose.jellyfin.yml exec jellyfin ls -l /config/plugins/Anaglyfin /config/anaglyfin/ffmpeg
docker compose -f compose.jellyfin.yml exec jellyfin /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version
```

The second command is the wrapper answering as FFmpeg, which is also the proof that
`ANAGLYFIN_REAL_FFMPEG` names a binary that exists in this container.

### FFprobe: the one thing the mount shape has to carry

Jellyfin looks for `ffprobe` **next to the FFmpeg it was given**, not on `PATH`. Give it
the wrapper and nothing beside it and the server starts but cannot probe anything:

```text
[ERR] MediaEncoder: Running /config/anaglyfin/ffmpeg/ffprobe -loglevel quiet -f lavfi
      -i nullsrc=s=1x1:d=1 -only_first_vframe failed with exception An error occurred
      trying to start process '/config/anaglyfin/ffmpeg/ffprobe' ... No such file or directory
```

```sh
sh harness.sh ffprobe
```

copies the image's own `ffprobe` into `/config/anaglyfin/ffmpeg/`, which is where the
server will look, and is the same thing `docs/install.md` tells you to do on a real
server - there too you copy the **official** `ffprobe`, from the stock FFmpeg the server
runs ordinary playback on, *not* the one the FFmpeg-mvc build produced (the custom build
does build an `ffprobe`; installing it beside the wrapper is wrong). Restart after this step.

## Step 5 - library scan over `/media`

Open `http://localhost:8096` and finish the setup wizard, then add a **Movies** library
whose folder is `/media`. A few things worth knowing before you do:

- The container runs as root by default, and a first start with no user creates an
  administrator named after that user (`root`) with no password. Set a real password in
  the wizard, and treat this stack as local-only.
- Untick the metadata downloaders for a fast, offline, repeatable scan: the checks below
  read the file name, not the metadata.
- For the positive case, the file name needs an explicit MVC marker - `MVC`, `3D MVC`,
  `-mvc` - because plain `3D` does not make an item eligible (`docs/validation.md` V4).
  Two negatives next to it - one `3D` item without a marker and one `HSBS` item - make
  the result self-evident.
- `/media` is a mount, not a copy, and the default mount is read-write. The default
  `./media` starts empty; put or link a sample in, or point `HARNESS_MEDIA_DIR` at a
  library you already have. If you point it at a real library, first untick that library's
  metadata savers, or disable "Save metadata into media folders", or expect Jellyfin to
  write `.nfo` files and artwork into the mounted directory. A copy of the sample, or a
  read-only mount for the library folder, is the safer default.

Expected in the log: the scan finds the items and finishes without errors. Nothing
Anaglyfin-specific happens during the scan - the provider is asked at playback-info time.

## Step 6 - make the provider say what it decided

`docs/validation.md` V4 and V5 want the provider's own decisions, which are Debug lines.
Jellyfin 12 configures Serilog from JSON, so drop this beside the other config files -
`docker compose exec jellyfin sh -c "cat > /config/config/logging.json"` and paste:

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

Restart. The lines you are looking for, from
`Anaglyfin.MediaSources.AnaglyfinMediaSourceProvider`:

```text
Anaglyfin offers 4 versions for Ready Player One (2018) - 3D mvc.
Anaglyfin offers no versions for Ready Player One (2018) - 1080p: <decision>.
```

Delete the file, or set the level back to `Information`, when you are done: this is the
noisiest logging the plugin has and it is not meant to stay on.

## Step 7 - alternate media sources

Start the MVC item and open the client's version/source picker. Expected, for a fresh
install with shipped defaults:

```text
<the item's own library source>            (Jellyfin's, unchanged)
3D Anaglyph Red/Cyan (Dubois)              (the configured default, promoted first)
3D Full Side-by-Side
3D Half Side-by-Side
2D Base
```

To see the transport instead of the UI - which is what you want when a client shows
something surprising - open the browser's network inspector, play the item, and read the
`POST /Items/<id>/PlaybackInfo` response. Each Anaglyfin entry is checked field by field
in `docs/validation.md` V5; the short version is:

```json
{
  "Id": "3f2a1c9d7b6e4f5a8c2d9e0b1a4c7d63",
  "Name": "3D Anaglyph Red/Cyan (Dubois)",
  "Path": "http://127.0.0.1/anaglyfin/profile/anaglyph_arcd?source=%2Fmedia%2F...&video=0",
  "Protocol": "Http",
  "SupportsTranscoding": true,
  "SupportsDirectPlay": false,
  "SupportsDirectStream": false,
  "MediaStreams": [
    { "Type": "Video", "Codec": "mvc", "Index": 0 },
    { "Type": "Audio", "Codec": "DTS", "Index": 1 }
  ]
}
```

`Id` is a GUID (lower-case `N` format), not a readable label: Jellyfin 12's DynamicHLS
endpoints `Guid.Parse` the `MediaSourceId` on the way to the transcoder, so a descriptive
id throws before FFmpeg is ever started. It is derived from the item id and the profile id,
so it is stable across restarts; the profile itself is carried by `Path`, which is what the
wrapper reads.

Read `MediaStreams` and `Path` rather than the three `Supports*` flags. The server overwrites
`SupportsTranscoding` and `SupportsDirectStream` on a dynamic source with what the user's
transcode profile and the device profile allow, so a `false` in the response is what the
plugin asked for and not what the server believes; and its own stream-copy decision never
consults those flags for video, only the reported codec. The two fields that do decide the
outcome are:

- `"Codec": "mvc"` on the video stream - no client transcode profile names that codec, so
  there is nothing to copy and the server must encode. That name is the lever and not a
  description of the track (ffprobe reports an MVC video as `hevc`); it is the only field the
  clone differs in, so the resolution, bit depth and HDR range the file was probed with stay
  on the version. The original library source keeps reporting its real codec; the `mvc`
  stream is a clone belonging to this version alone.
- `&video=<index>` in `Path` - the marker naming the video stream by its `Index` above, which
  is the number of that stream inside the file and the one the server spends on a
  `-map 0:<index>` for it. A source whose video stream carries no known index writes no
  `video` parameter at all, and keeps the codec report that forces the encode.

Original source stays first and stays playable; the `Id` changes with the profile and not
with the client; the item's own source is untouched.

## Step 8 - did the marker reach the wrapper

This is the load-bearing check of the whole architecture (`docs/validation.md` V6). Start
an Anaglyfin version and, while it is transcoding, look at the two processes:

```sh
# on the host; containers share the kernel, so this sees them
ps -ww -eo pid,ppid,args | grep -E 'anaglyfin-ffmpeg|ffmpeg' | grep -v grep
```

The image has no `ps`, so inside the container read `/proc` instead:

```sh
docker compose -f compose.jellyfin.yml exec jellyfin sh -c \
  'me=$$; for p in /proc/[0-9]*; do pid=${p##*/}; [ "$pid" = "$me" ] && continue
     c=$(tr "\0" " " < "$p/cmdline" 2>/dev/null)
     case "$c" in */proc/*) continue ;; *ffmpeg*) printf "%s  %s\n" "$pid" "$c" ;; esac
   done'
```

Both loops skip their own command line, which otherwise matches everything they are
looking for. The appendix at the bottom is a set of captures from this harness.

Expected shape, and this is the pass condition:

```text
jellyfin
  /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg ... -i http://127.0.0.1/anaglyfin/profile/sbs_full?source=%2Fmedia%2F... ...
    <ANAGLYFIN_REAL_FFMPEG> ... -view_ids -1 -i /media/Movie.2010.3D.1080p.MVC.mkv -sn -map 0:v ...
```

- the wrapper received the marker as **one** argument - not split, not decoded, not
  normalised away;
- the child received the decoded real path, and **no** marker text;
- the profile's inserted fragments are there (`docs/validation.md` V7 for the per-profile
  list), and Jellyfin's own encoder, muxer and HLS arguments survived around them;
- the child names an encoder for the video - `-codec:v libx264`, `-c:v libx265`, `-vcodec
  ...`, anything but `copy`. A profile that owns the video pipeline never runs over a copy:
  the copy form of a Jellyfin command carries no encoder stack to write into, so the wrapper
  refuses it and says `refused: the command was not started (ServerChoseVideoCopy)` in the
  log rather than passing a command through that would have played the raw MVC track;
- for the custom grayscale profile, no numbered map names the marker's video index:
  `-map 0:3` in the command the wrapper received is removed from the child's when the marker
  said `video=3`, because the graph output has to be the only mapped picture and argv alone
  cannot tell that `3` from an audio stream. For a linear profile, `-map 0:3` is the stream
  the profile converts and survives unchanged; the composed request is input-side, so it does
  not need a competing map. Audio and subtitle maps, exclusion maps (`-map -0:a`,
  `-map -0:s`, `-map -0`) and maps of other inputs survive untouched; the one exclusion that
  does not is `-map -0:v`, which would delete or subtract the picture being produced.

The same command lines are written to the server's transcode logs, which is the artifact
to capture rather than a screenshot:

```text
/config/log/transcode_*.log
```

and the concurrency slot the wrapper claims for the job:

```sh
docker compose -f compose.jellyfin.yml exec jellyfin ls -l /tmp/anaglyfin/ffmpeg-wrapper
# anaglyfin-transcode-0.lock while the job runs, gone when it finishes
```

## Step 9 - ordinary playback must be invisible

Play a non-MVC item in a way that forces a transcode - a phone on a metered connection, or
drop the quality setting - and repeat step 8. Pass condition:

- the child's arguments are the arguments the wrapper received, character for character,
  including `-hwaccel`/`-vaapi_device`/`-qsv` options the server chose;
- no `-view_ids`, no `-map`, no `-vf`, no `-filter_complex`, no `-sn` appeared;
- no slot file exists under `ANAGLYFIN_LOCK_DIR` for the ordinary job;
- no `anaglyfin-wrapper:` line in any log.

Comparing the two transcode logs - one Anaglyfin job, one ordinary job, same server, same
minute - is the evidence. `sh harness.sh collect` puts them in one directory.

<details>
<summary>What "works" means without an FFmpeg-mvc build</summary>

The harness default points both `ANAGLYFIN_REAL_FFMPEG` and `ANAGLYFIN_SERVER_FFMPEG` at the FFmpeg
the image ships, which cannot decode MVC. Steps 2, 4, 8's marker-transport half and all of step 9 are
valid anyway, because they are about plumbing and not about pixels; the converted profile will
fail at the decoder, and that failure is a correct observation about a stock FFmpeg.

To watch a real profile render, mount an FFmpeg-mvc build and point the wrapper's *marker* route at
it - leaving the *server* route on the image's own FFmpeg, which is what ordinary commands and the
startup probes should keep using. Both halves:

```yaml
# compose.jellyfin.yml, under jellyfin.volumes
- ${HARNESS_FFMPEG_MVC}:/opt/anaglyfin/ffmpeg-mvc/ffmpeg-mvc:ro
```

```sh
# .env: point the marker route at the mounted build; keep ordinary commands + probes on the stock
# FFmpeg, and remember ffprobe beside the wrapper is the OFFICIAL one (sh harness.sh ffprobe).
HARNESS_FFMPEG_MVC=/path/to/ffmpeg-mvc-n8.1.2-mvc3-jf4
HARNESS_REAL_FFMPEG=/opt/anaglyfin/ffmpeg-mvc/ffmpeg-mvc
HARNESS_SERVER_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg
```

</details>

## Step 10 - capture the evidence

```sh
sh harness.sh collect
```

writes everything below into `collect/<utc-stamp>/`:

```text
jellyfin-container.log            docker compose logs, with timestamps
inside-container.txt              the mounted files, the image's FFmpeg directory
processes-in-container.txt        /proc cmdlines from inside the container
processes-on-host.txt             ps -ww for jellyfin and ffmpeg on this host
jellyfin-server-logs/             /config/log, including transcode_*.log
```

The server writes those logs as the container user, which is root by default, so the copy
may need `sudo`; step 4's `docker compose exec` form works either way.

## Step 11 - tear it down

```sh
sh harness.sh down
```

The stack never writes outside this directory, so disposal is:

```sh
rm -rf jellyfin media collect
```

Anything the server left in `jellyfin/config` is harness state, not product state: delete
it rather than curating it, and never point `HARNESS_CONFIG_DIR` at a real server's
config.

## Troubleshooting

| Symptom | What it is |
| --- | --- |
| `all predefined address pools have been fully subnetted`, or compose cannot create its network | daemon out of bridge pools: `HARNESS_HOST_NETWORK=1 sh harness.sh up` |
| Server starts, plugin list has no Anaglyfin | `/config/plugins/Anaglyfin/Anaglyfin.dll` is a directory: the bind source was not readable to the daemon, see step 3's split-path box |
| `FFmpeg validation: The process returned no result` + `Path set by command line or environment variable is invalid`, then the container exits | the wrapper is in place but `ANAGLYFIN_REAL_FFMPEG` names something that is not in the container. The message blames the wrapper path; the variable to fix is the other one. Verified with `ANAGLYFIN_REAL_FFMPEG=/config/anaglyfin/ffmpeg-mvc/ffmpeg-mvc` and no mount behind it |
| `Running /config/anaglyfin/ffmpeg/ffprobe ... No such file or directory` | no ffprobe beside the wrapper: `sh harness.sh ffprobe`, then restart |
| `anaglyfin-wrapper: refused: 1 Anaglyfin transcode(s) are already running`, child exit `75` | the concurrency limit doing its job; the harness sets the optional `ANAGLYFIN_MAX_CONCURRENT_TRANSCODES=1` override, so stop the first job or raise the limit on the admin page or that override |
| `anaglyfin-wrapper: refused: the command was not started (RejectedMarker/...)` | a marker the parser rejected. The line never contains the marker or the path, so look at the playback-info response (step 7) to see what was offered |
| `anaglyfin-wrapper: refused: the command was not started (ServerChoseVideoCopy)`, child exit `65` | the server asked for a video copy of an Anaglyfin version, so the profile had nothing to write into. Expected after a codec report the client can copy, which the step 7 `MediaStreams` check should have caught first; the reported `mvc` codec exists to make this line rare |
| `Unrecognized option 'vbr:a'` + `Error splitting the argument list: Option not found` immediately after the child's banner, nothing else, on an Anaglyfin version's child | audio capability inversion, the known pitfall of the two-binary split: the official build's probe advertised `libfdk_aac`, so the server wrote that selection plus its private `-vbr:a` option into the marker command, which runs on the minimal FFmpeg-mvc build that carries neither and dies parsing before any input opens. The v0.2.1 wrapper maps this one known selection to native `aac` on the marker route (notice on the log; see `docs/install.md` §3.1 step 9 and validation V7.11) - on an older wrapper, turn off **Enable audio VBR** in the server's playback settings or unset `ANAGLYFIN_SERVER_FFMPEG` |
| Anaglyfin versions never appear for a file you expect | the name has no MVC marker, or the provider is not eligible: step 6's Debug lines say which decision was made |
| `No users, creating one with username root` during first start | the container is root and the wizard had not run yet; set a password in the wizard |
| `harness: ... wrapper, not executable` | the checkout dropped the mode bit: `chmod 0755 <artifact>`, or let preflight do it |
| Port 8096 already used | `HARNESS_WEB_PORT=8097` in `.env`; a real Jellyfin on the same host will not thank you for the collision |
| Everything passes but the picture is wrong | the picture is the part this harness cannot judge: see the table at the top |

## Testing the real server with these notes

The real container is the gate, and it is not this one. The harness exists so that the
visit is short: run steps 4, 8 and 9 there exactly as here, and paste back

1. `docker inspect` image digest, and the server's `Version` from `/System/Info/Public`,
2. the four start-of-log lines from step 4,
3. the `PlaybackInfo` response for one MVC item (step 7),
4. `ps -ww` for the wrapper and its child during one Anaglyfin playback (step 8),
5. the `transcode_*.log` for that job **and** for one ordinary job (step 9),
6. the lock directory listing while the Anaglyfin job runs,
7. any line beginning `anaglyfin-wrapper:`, verbatim, in the server log or in a
   transcode log,
8. what the client offered and what it played.

Everything else in `docs/validation.md` is a checkbox; those eight items are what makes a
`FAIL` there debuggable from the other side of a screen.

## Appendix - captures from this run

These captures were made by driving the wrapper inside the harness container with the
image's stock FFmpeg and `ANAGLYFIN_MAX_CONCURRENT_TRANSCODES=1`. A real playback produces
the same two-process shape with the Jellyfin server as the parent and the server's HLS
arguments instead of the segment arguments used here.

### Pass-through

The command below is an ordinary FFmpeg command with no Anaglyfin marker:

```sh
/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg \
  -hide_banner -re -f lavfi -i nullsrc=r=25:d=90 \
  -c:v libx264 -preset ultrafast \
  -f segment -segment_time 4 -reset_timestamps 1 /tmp/slowtest%03d.ts
```

The process capture while it was running:

```text
770  /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -hide_banner -re -f lavfi -i nullsrc=r=25:d=90 -c:v libx264 -preset ultrafast -f segment -segment_time 4 -reset_timestamps 1 /tmp/slowtest%03d.ts
783  /usr/lib/jellyfin-ffmpeg/ffmpeg -hide_banner -re -f lavfi -i nullsrc=r=25:d=90 -c:v libx264 -preset ultrafast -f segment -segment_time 4 -reset_timestamps 1 /tmp/slowtest%03d.ts
```

The wrapper and its child received the same arguments. No
`anaglyfin-transcode-*.lock` file existed for that job.

### Marker transport

The command below carries an Anaglyfin marker for the `two_d_base` profile:

```sh
/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg \
  -hide_banner -re \
  -i "http://127.0.0.1/anaglyfin/profile/two_d_base?source=%2Ftmp%2Fclip.mp4" \
  -c:v libx264 -preset ultrafast \
  -f segment -segment_time 4 -reset_timestamps 1 /tmp/markertest%03d.ts
```

The process capture while it was running:

```text
969  /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -hide_banner -re -i http://127.0.0.1/anaglyfin/profile/two_d_base?source=%2Ftmp%2Fclip.mp4 -c:v libx264 -preset ultrafast -f segment -segment_time 4 -reset_timestamps 1 /tmp/markertest%03d.ts
977  /usr/lib/jellyfin-ffmpeg/ffmpeg -hide_banner -re -i /tmp/clip.mp4 -c:v libx264 -preset ultrafast -f segment -segment_time 4 -reset_timestamps 1 /tmp/markertest%03d.ts
```

The wrapper received the marker as one input argument. The child received the decoded real
path and no marker text.

The lock directory while the marker job was running:

```text
total 4
-rw-r--r-- 1 root root 50 Sep 12 18:48 anaglyfin-transcode-0.lock
pid=969
claimed=2026-09-12T18:48:37.9226122+00:00
```

The lock file is under `/tmp/anaglyfin/ffmpeg-wrapper`. Its `pid` matches the wrapper PID
above, and the file is removed when the job finishes.

### Concurrency refusal

With the first marker job still running, a second marker job produces:

```text
anaglyfin-wrapper: refused: 1 Anaglyfin transcode(s) are already running, which is the configured maximum, so FFmpeg was not started. Raise the maximum concurrent Anaglyfin transcodes on the Anaglyfin settings page, or ANAGLYFIN_MAX_CONCURRENT_TRANSCODES to override it from the deployment, or remove slot files left in the directory ANAGLYFIN_LOCK_DIR names if a wrapper was killed without exiting.
exit=75
```

Captured as the build then running behaved. A later build waits briefly before refusing and no
longer tells anybody to delete slot files, since a leftover whose owner is gone is taken over on
its own; the current wording and how to check it are in `docs/validation.md` V9.

### Stock FFmpeg on a converted profile

A converted profile reaches the image's stock FFmpeg with multiview decoding requested:

```text
[vist#0:0/h264 @ 0x7f293fcb9180] [dec:h264 @ 0x7f293fc0e500] Multiview decoding requested, but decoder 'h264' does not support it
[vist#0:0/h264 @ 0x7f293fcb9180] [dec:h264 @ 0x7f293fc0e500] Error setting up multiview decoding: Function not implemented
[out#0/mp4 @ 0x7f293fc76d40] Output file is empty, nothing was encoded(check -ss / -t / -frames parameters if used)
frame=    0 fps=0.0 q=0.0 Lsize=       0KiB time=N/A bitrate=N/A speed=N/A elapsed=0:01:29.47
Conversion failed!
exit=69
```

This is the expected stock-FFmpeg symptom for a converted profile. It is not proof that the
plugin failed to deliver the profile: the child received the converted command line and
then could not decode MVC.
