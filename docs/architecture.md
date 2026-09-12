# Anaglyfin architecture

Anaglyfin has two runtime halves:

```text
Jellyfin plugin process                         FFmpeg child boundary
------------------------------------            --------------------------------------
profile catalog + admin settings                Anaglyfin.FFmpegWrapper
MVC detection                                         |
media source provider                                 v
profile marker construction                     real FFmpeg-mvc binary
```

The plugin process decides **what versions exist**. The wrapper decides **what command the
real FFmpeg should run** when a user picks one of those versions.

## Playback flow

1. Jellyfin asks for playback info.
2. `AnaglyfinMediaSourceProvider` checks whether the item is a video file and whether the
   conservative MVC detector accepts it.
3. For an eligible item, the provider creates one alternate `MediaSourceInfo` per enabled
   profile.
4. Each alternate source carries a profile marker URL instead of a playable path:

   ```text
   http://127.0.0.1/anaglyfin/profile/<profileId>?source=<percent-encoded-rooted-path>
   ```

5. The user picks a version.
6. Jellyfin builds its normal transcode command and passes the marker URL as the input path.
7. The wrapper receives that command because the server's FFmpeg path points at the wrapper.
8. `WrapperArgumentRewriter`:
   - recognizes a marker,
   - resolves the profile from the built-in catalog,
   - replaces the marker token with the real source path,
   - inserts the profile's FFmpeg arguments,
   - removes conflicting video and subtitle maps,
   - refuses unsafe or unsupported command shapes.
9. For a rewritten Anaglyfin command, `WrapperConcurrencyGuard` takes one slot before the
   real FFmpeg is started.
10. `FFmpegProcessLauncher` starts the real FFmpeg-mvc binary with no shell and inherited
    stdin/stdout/stderr.

## Major pieces

| Piece | Responsibility |
| --- | --- |
| `Plugin` | Fixed plugin identity and admin page registration |
| `PluginServiceRegistrator` | DI registration for catalog, detector, and settings source |
| `ProfileCatalog` | Fixed profile allowlist and display metadata |
| `PluginConfiguration` | Persisted profile ids, enabled profiles, concurrency, colors, encoder policy |
| `Configuration/configPage.html` | Dashboard-served settings page |
| `MvcSourceDetector` | Conservative MVC eligibility from metadata and names |
| `AnaglyfinMediaSourceProvider` | Alternate media sources carrying profile markers |
| `ProfileMarker` | Canonical marker construction and security invariants |
| `ProfileMarkerParser` | Wrapper-side marker recognition and validation |
| `FfmpegProfileArgumentBuilder` | Exact argument tokens per profile |
| `WrapperArgumentRewriter` | Pure command decision: pass through, rewrite, or refuse |
| `Anaglyfin.FFmpegWrapper` | Out-of-process executable started by Jellyfin |
| `WrapperApplication` | Wrapper outcome, exit codes, diagnostics, slot use |
| `WrapperConcurrencyGuard` | File-slot concurrency limit for Anaglyfin jobs |
| `FFmpegProcessLauncher` | Starts real FFmpeg through argv, not a shell |

## Marker contract

A marker is intentionally URL-shaped so it survives Jellyfin's transport layers as one
whitespace-free token. It is not fetched and it does not name an HTTP endpoint.

The marker may carry:

```text
profile id        allowlisted Anaglyfin profile id
source path       rooted library path, percent-encoded
video stream index optional; the position of the video stream in the source's stream list
subtitle ordinal  optional; currently not produced by the provider
```

The wrapper replaces the marker with the real source path before FFmpeg is started. A
marker that reaches the real FFmpeg child command is a bug.

## Forcing the encode

A profile only converts what the encoder is asked to encode, and Jellyfin decides video copy
from the data a source reports rather than from any flag a plugin sets: it overwrites a
dynamic source's transcode and direct-stream flags with the user's permissions, and its copy
decision then looks only at the reported video codec against the client's transcode profile.
So an alternate source reports its video as `mvc` - a codec no client profile names, and the
honest name for what the file holds - which makes stream copy impossible for every client and
every permission. The stream carrying that codec is a clone: the item's original source keeps
the objects it was probed with.

The wrapper will not run a profile pipeline over a command that copies its video anyway
(`ServerChoseVideoCopy`), because the copy form of a Jellyfin command carries no encoder stack
for the profile to write into.

## Profile command rules

| Profile | Required fragments |
| --- | --- |
| `sbs_full` | `-map 0:v:view:all`, `-sn` |
| `sbs_half` | `-map 0:v:view:all`, `-vf scale=iw/2:ih:flags=bicubic,format=yuv420p`, `-sn` |
| `anaglyph_arcd` | `-map 0:v:view:all`, `-vf stereo3d=sbsl:arcd,format=yuv420p`, `-sn` |
| `two_d_base` | marker replacement only |
| `custom_grayscale` | `-filter_complex <Anaglyfin graph>`, `-map [anaglyfin_custom]`, `-sn` |

Rewritten commands keep Jellyfin's encoder, muxer, HLS, and output choices. The only audio
argument the rewriter adds is `-map 0:a?` when the server command had no maps at all and the
profile inserted a video map. Of the server's own maps, a profile that owns the video pipeline
takes away two shapes: a map that names the video type (`0:v`, `0:v:0`, a graph label), and the
numbered map the marker's `video=<index>` identifies (`-map 0:<index>`, `-map 0:<index>?`),
which no argv reader can tell video from audio.

## Security boundaries

- Profile ids are an allowlist, not free-form conversion text.
- Settings store ids, numbers, enum names, and validated hex colors.
- Markers carry only a rooted provider-owned source path and an allowlisted profile id.
- The wrapper never treats arbitrary command text as filter syntax.
- The wrapper launches FFmpeg through `ArgumentList`, not a shell.
- Refusal diagnostics avoid echoing request paths and marker text.

## Current deployment assumptions

- The server's FFmpeg path points at the wrapper executable.
- The real FFmpeg-mvc binary is configured through `ANAGLYFIN_REAL_FFMPEG` or
  `FFMPEG_MVC_PATH`, with plain `ffmpeg` on `PATH` only as a fallback.
- The wrapper lock directory must be shared by every wrapper process that should share one
  Anaglyfin concurrency limit.
- Manual plugin installation is currently expected because no packaging job is present.

## Current follow-up areas

- Wire per-playback subtitle selection through marker subtitle ordinals and `SubtitleBurnIn`.
- Apply device/client defaults in the provider path, not only in the profile catalog.
- Enforce `EncoderPolicy` at the wrapper/command-building boundary, or remove it from the UI
  until enforcement exists.
- Merge wrapper signal forwarding from `task/T8-wrapper-signal-forwarding`.
- Add packaging metadata if a plugin repository workflow is desired.
