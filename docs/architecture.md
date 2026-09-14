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
video stream index optional; the `MediaStream.Index` of the source's video stream, i.e. the
                  number a `-map 0:<index>` of that stream carries
subtitle ordinal  optional; currently not produced by the provider
```

The wrapper replaces the marker with the real source path before FFmpeg is started. A
marker that reaches the real FFmpeg child command is a bug.

## Forcing the encode

A profile only converts what the encoder is asked to encode, and Jellyfin decides video copy
from the data a source reports rather than from any flag a plugin sets: it overwrites a
dynamic source's transcode and direct-stream flags with the user's permissions, and its copy
decision then looks only at the reported video codec against the client's transcode profile.
So an alternate source reports its video as `mvc`. The value of that name is that nothing
contains it - no client direct-play list and no client transcode profile - which makes stream
copy impossible for every client and every permission. It is deliberately not a claim about
the wire format: ffprobe reports an MVC track as `hevc`, and the version's report changes that
field and the frame size the profile encodes, and no other. The stream carrying them is a clone
of the probed stream - a JSON round-trip of the model type rather than a hand-copied field list,
so nothing as visible as an HDR range can go missing on the way; the item's original source keeps
the objects it was probed with. The wrapper will not run a profile pipeline over a command that
copies its video anyway (`ServerChoseVideoCopy`), because the copy form of a Jellyfin command
carries no encoder stack for the profile to write into.

## Reported geometry

A version reports the frame its profile encodes, because that is the frame the server is about to
size. With a client that asks for no resolution and brings at least the reported bitrate, the
server defaults the request's `MaxWidth`/`MaxHeight` to the reported video stream's `Width` and
`Height`, and the software `scale` it writes from those is evaluated against whatever frame
reaches it. So full SBS - the native all-view output, both eyes side by side - reports double the
source's width at the source's height, while half SBS, both anaglyph families and 2D base land
back on the source's own size and report it unchanged. Nothing here is measured or stored: the
size is derived from the profile's conversion and the source's numbers, so no probe and no cache
is involved, and a source that named no width gets no invented one. `Video3DFormat` stays unset on
a version: the server reads a stereo format as an instruction to convert that format to 2D itself,
which is the opposite of what a source that has already converted the picture wants.

## Profile command rules

| Profile | Required fragments |
| --- | --- |
| `sbs_full` | `-view_ids -1` immediately before the marker input's `-i`, `-sn` |
| `sbs_half` | `-view_ids -1` immediately before the marker input's `-i`, `-vf scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p`, `-sn` |
| `anaglyph_arcd` | `-view_ids -1` immediately before the marker input's `-i`, `-vf stereo3d=sbsl:arcd,format=yuv420p`, `-sn` |
| `two_d_base` | marker replacement only; no `-view_ids` |
| `custom_grayscale` | `-view_ids -1` immediately before the marker input's `-i`, `-filter_complex <Anaglyfin graph>`, `-map [anaglyfin_custom]`, `-sn` |

The composed all-view request is an input option: FFmpeg reads `-view_ids` when it opens the
input, so it is written in front of that input's `-i`. A view specifier such as `0:v:view:all`
is the other, post-open route and is refused by FFmpeg-mvc on an input configured this way, so
no generated command carries one. The custom graph's source label addresses the marker's own
video stream (`[0:<index>]`, with `[0:v]` only when no index was carried), not a view selector.

The half-SBS chain's `setsar=sar=1` is not decoration: `scale` keeps the display aspect by
adjusting the sample aspect ratio it hands on, so halving the width of a square-pixel frame hands
on a 2:1 one, and the declaration behind it is what makes the reported 1920x1080 mean square
pixels all the way to the player.

Rewritten commands keep Jellyfin's encoder, muxer, HLS, and output choices. Where a profile's
conversion is a linear chain and the server already wrote a `-vf`, the profile's filters are
**prepended** to that chain: the server's scale is sized from the converted geometry the version
reports and evaluates against the frame that actually reaches it, so a conversion landing behind
it scales a picture that has not been made yet (a full-SBS version of a 1920x1080 source came out
of that ordering at 1920x540). Prepending keeps every filter the server wrote - the client's
resolution ceiling, the ladder's rung, any burn-in - and at native quality the server's scale
becomes an identity. A `-filter_complex` the server wrote is never reordered or interleaved: a
graph is not a stage of somebody else's chain, and a profile that owns the video pipeline refuses
a command that already carries one.

The only audio argument the rewriter adds is `-map 0:a?` when the server command had no maps at
all and the profile inserted a video map of its own. Linear profiles ride the stream the server
already named, so they insert no map and therefore need no replacement audio map.

For the one custom graph profile, which maps a new labelled output, the server's video maps give
way to that label: a video-type map (`0:v`, `0:v:0`, a graph label), the exclusion of that type,
and the numbered map identified by the marker's `video=<index>` are removed, while other stream
maps stay. For a linear profile, the server's ordinary video map is the converted picture's
carrier and is preserved; subtitle maps still give way to `-sn`, and a marker-input video-type
exclusion is removed because it would delete the stream the profile is about to convert. Any
positive marker-input view-specifier map (`0:v:view:all`, `0:v:vidx:<n>`, or `0:v:vpos:<pos>`)
is rewritten to the marker's ordinary video stream because a view specifier cannot stand beside
the input's `-view_ids` request. An exclusion that does not name video stays: `-map -0:s` under a
profile suppressing subtitles is the server agreeing with the profile, and deleting it would put
subtitles back.

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
