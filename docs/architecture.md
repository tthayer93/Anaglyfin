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
3. For an eligible item the provider asks the catalog which profiles to offer and in what
   order, naming the device that asked. The interface carries no device parameter, so the
   exact id is read off the request itself, from the ambient HTTP context the server keeps for
   the call in flight (see "Which device is asking").
4. It creates one alternate `MediaSourceInfo` per offered profile - the profile that device's
   default resolves to first, the rest in catalog display order.
5. Each alternate source carries a profile marker URL instead of a playable path:

   ```text
   http://127.0.0.1/anaglyfin/profile/<profileId>?source=<percent-encoded-rooted-path>
   ```

6. The user picks a version.
7. Jellyfin builds its normal transcode command and passes the marker URL as the input path.
8. The wrapper receives that command because the server's FFmpeg path points at the wrapper.
9. `WrapperArgumentRewriter`:
   - recognizes a marker,
   - resolves the profile from the built-in catalog,
   - replaces the marker token with the real source path,
   - inserts the profile's FFmpeg arguments,
   - removes conflicting video and subtitle maps,
   - refuses unsafe or unsupported command shapes.
10. For a rewritten Anaglyfin command, `WrapperConcurrencyGuard` takes one slot before the
    real FFmpeg is started.
11. `FFmpegProcessLauncher` starts the real FFmpeg-mvc binary with no shell and inherited
    stdin/stdout/stderr.

## Major pieces

| Piece | Responsibility |
| --- | --- |
| `Plugin` | Fixed plugin identity and admin page registration |
| `PluginServiceRegistrator` | DI registration for catalog, detector, and settings source |
| `ProfileCatalog` | Fixed profile allowlist and display metadata |
| `PluginConfiguration` | Persisted profile ids, enabled profiles, concurrency, colors, subtitle depth |
| `DeviceProfileDefault` | One device's default profile: an exact device id and the profile to offer it first |
| `Configuration/configPage.html` | Dashboard-served settings page |
| `MvcSourceDetector` | Conservative MVC eligibility from metadata and names |
| `AnaglyfinMediaSourceProvider` | Alternate media sources carrying profile markers, offered in the asking device's order |
| `ProfileMarker` | Canonical marker construction and security invariants |
| `ProfileMarkerParser` | Wrapper-side marker recognition and validation |
| `FfmpegProfileArgumentBuilder` | Exact argument tokens per profile |
| `WrapperArgumentRewriter` | Pure command decision: pass through, rewrite, or refuse |
| `WrapperSettingsFile` | Settings document crossing the plugin/wrapper boundary |
| `WrapperSettingsPublicationService` | Writes that document at plugin startup; `Plugin` rewrites it on save |
| `Anaglyfin.FFmpegWrapper` | Out-of-process executable started by Jellyfin |
| `WrapperApplication` | Wrapper outcome, exit codes, diagnostics, slot use |
| `WrapperConcurrencyGuard` | File-slot concurrency limit for Anaglyfin jobs |
| `FFmpegProcessLauncher` | Starts real FFmpeg through argv, not a shell |

## Which device is asking

`IMediaSourceProvider` is handed the item and nothing else: on Jellyfin 12 the interface carries
no device, no client and no user. Which device is asking is still a fact about the request, and
the server settles it onto that request - a `Jellyfin-DeviceId` claim on every authenticated one -
so the provider reads that one claim from the ambient HTTP context the call runs inside. An
administrator's default for one exact registered device therefore goes first for that device and
for nobody else, and the resolution is per request rather than cached or written anywhere.

Because the seam is the request rather than a parameter, a request that names no device is an
ordinary case and not a failure: an API-key call, whose device field carries the server's own
system id rather than any client's device, and the library, DLNA and startup compositions that
reach a provider with no HTTP context at all. All of them leave the ordering to the global
default, and nothing else about the offer changes. A context that cannot be read is answered the
same way, because the whole thing a default can cost is one profile's place in a list.

Selection is by exact device id and nothing else. That id is generated and self-reported by the
client - a reinstall, a reset, or cleared storage regenerates it, and the server's device list
only records what a client last claimed rather than issuing the id itself - which makes it a good
key for a preference and a poor one for anything else: a default authorises no playback, and a
pinned entry can quietly stop matching when a client re-registers. Client names are not a key at
all; they are free text that changes between releases of the same app, and matching on one would
promise a reliability the platform does not have. Rows an older build stored with a client name
still load, since the name is simply ignored, and they are gone from the settings file on the next
save; a row that pinned a client and no device matches nothing in the meantime.

Approximate device **categories** are not a key either, and cannot honestly be one today: Jellyfin
12 computes and stores no device type to consult, so there is no server-side answer to "is this a
TV?" waiting to be read. A future category feature would be a heuristic over what clients report
about themselves, would have to say so on its own face, and could not claim accurate handling of
3D TVs, VR headsets or 3D-capable projectors. See "Current follow-up areas".

What a default touches is the order of an offer, never its contents: the enabled profiles are the
set a client can pick and the set the library materialises version items from, so no device's
default enables a profile, hides a version, or starts a library write.

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

## Hardware acceleration passes through

Every Anaglyfin version declares its media as a **video file** (`ProfileVersionSource.VersionVideoType`,
written on the dynamic source and on the materialised item, and repaired on any item that has drifted
onto a disc type). That is not a description of a file the plugin owns; it is the answer to the one
question the server's hardware video encoders are gated on. `EncodingHelper` offers `h264_qsv`,
`hevc_nvenc`, `h264_vaapi` and their families only `if (state.VideoType == VideoType.VideoFile)`, and
what it refuses it refuses silently: the command comes out `libx264` on a machine whose administrator
turned Quick Sync on, with nothing in the log to say a choice had been made.

On Jellyfin 12 the declaration wins nothing at that gate, for a reason worth writing down: the
streaming path never writes the job's `VideoType` at all, and `VideoType` has no "unspecified"
member - `VideoFile` is its first, so a field nobody wrote already answers `VideoFile`. The value is
written because an enum member's position is a thin thing to hang a product behaviour on, and because
the claim is read as it stands by the version list a client renders, by the server's ordering of an
item's sources, by the item's own listing, and by this plugin's scanner, which tolerates a source
naming no type and refuses one naming a disc.

What settles which encoder a version actually gets is the FFmpeg the server runs, because the gate
asks `SupportsEncoder` and that is answered by probing the binary. A marker input can only be decoded
by the FFmpeg-mvc build, so that build's encoder list is the ceiling: the shipped `linux-x64`
`n8.1.2-mvc4-jf4` is configured `--disable-doc --enable-gpl --enable-libx264 --enable-libass` and
reports `libx264, libx264rgb, h264_v4l2m2m` for H.264 - no QSV, no VA-API, no NVENC. Pointing a
Quick Sync server at that FFmpeg is what made every cp16 run come out `libx264`; the fix is a build
with the hardware encoders compiled in, not a field on the media source. Anaglyfin names no encoder,
never forces `libx264`, and takes whatever the server's settings select out of whatever binary the
server was handed.

Nothing at all is taken off a command for a **decode**. The server attaches an accelerator to an input
per reported codec, writing that choice in front of the input's `-i` (`-hwaccel`,
`-hwaccel_output_format`, `-hwaccel_device`, `-hwaccel_args`, `-hwaccel_flags`), and it opens the
device an encoder or a filter graph runs on with `-init_hw_device`, `-filter_hw_device` and, in the
VA-API spelling, `-vaapi_device`. Every one of those arguments passes through the rewrite unchanged, in
the marker input's scope and in every other one. The wrapper names no decoder and no encoder of its own
and rewrites no hardware filter chain; the only argument it writes on the input side of a marker is
`-view_ids -1`, immediately before that input's `-i`.

The fallback those options would need is already made further down the pipeline, by the component that
chooses the decoder. FFmpeg-mvc gates hardware decoding on the stream a decoder context is opening: once
a multiview SPS has been seen (`h->mvc_sps`, `libavcodec/h264_slice.c`) and an accelerator is attached
to that context, it drops the hardware pixel formats and goes on decoding in software, printing
`H.264/MVC: hardware acceleration is not supported, falling back to software decoding` once. A plain 2D
stream never sets that flag, so its hardware decode is untouched, and the hardware encode is a separate
path that works whichever decoder produced the frame. The gate is per decoder context, which is the
distinction that makes it usable: the marker's own input falls back even for its base view - the frame
the `two_d_base` profile plays - while an ordinary stream in the same command keeps its accelerator.

| Decode side, left where the server wrote it | Encode side, left where the server wrote it |
| --- | --- |
| `-hwaccel <value>`, `-hwaccel_output_format <value>`, `-hwaccel_device <value>`, `-hwaccel_args <value>`, `-hwaccel_flags <value>` | `-init_hw_device <name>:<type>[:<device>]`, `-filter_hw_device <name>`, `-vaapi_device <path>` |
| a forced input decoder (`-c:v <decoder>`), `-threads`, and every other option the server wrote in that scope | the encoder and its options (`-c:v h264_qsv`, `-global_quality`, …), and the upload and mapping filters (`hwupload`, `hwmap`, `vpp_qsv`, `format=nv12`) |

Preempting that fallback from the wrapper was cp17's correction, and it was wrong in both directions. A
marker URL names a file and a profile, which is not the information a decoder is about to weigh when it
picks a hardware path, and the removal was scoped to an input rather than to a stream - so the same
rule that "protected" an MVC input would also have taken the hardware decode away from an ordinary
stream carrying the server's setting. Two facts are worth holding on to. A command that carries no
hardware argument at all is unchanged by any of this, which is what keeps an ordinary hardware-disabled
server byte-for-byte in the commands it already ran. And a command whose input is not a marker is never
looked at: stock playback keeps the server's decode choices, which is the wrapper's pass-through rule
and not a special case of this one.

## Profile command rules

| Profile | Required fragments |
| --- | --- |
| `sbs_full` | `-view_ids -1` immediately before the marker input's `-i` |
| `sbs_half` | `-view_ids -1` immediately before the marker input's `-i`, `-vf scale=iw/2:ih:flags=bicubic,setsar=sar=1,format=yuv420p` |
| `anaglyph_arcd` | `-view_ids -1` immediately before the marker input's `-i`, `-vf stereo3d=sbsl:arcd,format=yuv420p` |
| `two_d_base` | marker replacement only; no `-view_ids` |
| `custom_grayscale` | `-view_ids -1` immediately before the marker input's `-i`, `-filter_complex <Anaglyfin graph>`, `-map [anaglyfin_custom]` (the map only when the server's own graph does not consume the label) |

Every one of those rows is also a command whose hardware arguments the server wrote are left where the
server wrote them - decode selection, device initialisation, encoder and upload filters alike - and
`two_d_base` is no exception: it writes no filter and no view request, so on that row the rewrite is
the marker replacement and nothing else. See "Hardware acceleration passes through" above.

A profile whose marker names a subtitle ordinal burns that track in - the `subtitles=` filter
lands as the last stage of the profile's chain, and only then does the rewrite add `-sn` and take
the server's subtitle maps out. A conversion on its own renders no text, so it says nothing about
subtitles: the server's subtitle maps and the subtitle streams its own filter graph reads survive
the rewrite untouched.

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
becomes an identity.

A `-filter_complex` the server wrote is not a chain the profile can join, but it does not need to
be: a graph links its chains by label, so the conversion arrives as one more chain of that graph -
the composed source stream through the profile's filters, into a label of its own
(`[anaglyfin_profile]`, or `[anaglyfin_custom]` for the graph profile) - and the server's own
references to the source video (`[0:<index>]`, `[0:v]`, `[0:v:0]`, `[0:v:<index>]`) are retargeted
onto it. Nothing else in the text is touched: the server's filters, its labels, the subtitle
streams it reads and the pad it feeds the encoder all stay as written, and the server's `-map` of
its own output stays with it. A profile with nothing to convert (full SBS with no burn-in) has no
chain to write and leaves the graph byte for byte alone. A graph this wrapper cannot merge refuses
the job - one read from a `-filter_complex_script` file, because the vector carries no graph text
to merge with; one that never names the stream the profile converts; one that reaches the source
through a view specifier, which a composed decode refuses; one that carries several graphs, where
no argument says which of them the output is drawn from; and one that already uses the label this
profile writes.

The only audio argument the rewriter adds is `-map 0:a?` when the server command had no maps at
all and the profile inserted a video map of its own. Linear profiles ride the stream the server
already named, so they insert no map and therefore need no replacement audio map.

For the one custom graph profile, which maps a new labelled output, the server's video maps give
way to that label: a video-type map (`0:v`, `0:v:0`, a graph label), the exclusion of that type,
and the numbered map identified by the marker's `video=<index>` are removed, while other stream
maps stay - unless the server's own graph consumes `[anaglyfin_custom]`, in which case that label
is already feeding the encoder through the server's chains and no map of it is written at all. For
a linear profile, the server's ordinary video map is the converted picture's carrier and is
preserved; subtitle maps give way to `-sn` only where the rewrite renders the text itself, and a
marker-input video-type exclusion is removed because it would delete the stream the profile is
about to convert. Any positive marker-input view-specifier map (`0:v:view:all`, `0:v:vidx:<n>`, or
`0:v:vpos:<pos>`) is rewritten to the marker's ordinary video stream because a view specifier
cannot stand beside the input's `-view_ids` request. An exclusion that does not name video stays:
`-map -0:s` under a profile suppressing subtitles is the server agreeing with the profile, and
deleting it would put subtitles back.

A marker reaching a rewritten command must not survive anywhere in it, including where the server
quoted it into its own filter values: a server-side burn-in is built from the path of the file
being transcoded, and for a version that path is the marker. So after the splice, every argument is
swept, and the marker text is replaced by the real source path in each spelling a server writes a
value under - the token itself, the whole token percent-encoded, and the token with its colons
escaped once or twice for the two passes a filter option value goes through. The replacement is
escaped the same way it was found.

## Security boundaries

- Profile ids are an allowlist, not free-form conversion text.
- Settings store ids, numbers, enum names, and validated hex colors.
- Markers carry only a rooted provider-owned source path and an allowlisted profile id.
- The wrapper never treats arbitrary command text as filter syntax.
- The wrapper launches FFmpeg through `ArgumentList`, not a shell.
- Refusal diagnostics avoid echoing request paths and marker text.
- A request's device id orders an offer and nothing else. It is text the client chose, so it is
  never read as an authorisation or as an identity a user can be held to.

## Current deployment assumptions

- The server's FFmpeg path points at the Anaglyfin FFmpeg entry point supplied with this plugin.
- The deployment installs a Jellyfin-compatible FFmpeg-mvc build and names it to the entry point
  through `ANAGLYFIN_REAL_FFMPEG` or `FFMPEG_MVC_PATH`, with plain `ffmpeg` on `PATH` only as a
  fallback.
- The wrapper lock directory must be shared by every wrapper process that should share one
  Anaglyfin concurrency limit.
- `ANAGLYFIN_WRAPPER_SETTINGS` names one settings document shared by the plugin and every wrapper
  process that should see the admin page's subtitle-depth request or concurrency limit. The plugin
  writes schema version 2; the wrapper also reads schema version 1, which carries the depth request
  but no concurrency limit.
- The concurrency limit is resolved per wrapper invocation in this order: valid
  `ANAGLYFIN_MAX_CONCURRENT_TRANSCODES`, then the settings document, then the shipped default.
- Manual plugin installation is currently expected because no packaging job is present.

## Current follow-up areas

- Wire per-playback subtitle selection through marker subtitle ordinals and `SubtitleBurnIn`.
- Offer approximate device categories (TV, phone, tablet, headset, projector) if a mapping from
  self-reported clients to a category can be built honest enough to be labelled the heuristic it
  is; Jellyfin 12 has no native device type to key one on, so this is future work rather than a
  wiring gap in the exact-device defaults already applied.
- Merge wrapper signal forwarding from `task/T8-wrapper-signal-forwarding`.
- Add packaging metadata if a plugin repository workflow is desired.
