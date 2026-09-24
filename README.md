# Anaglyfin

Anaglyfin is a Jellyfin plugin that exposes 3D MVC sources as selectable
playback versions, played through the normal Jellyfin HLS pipeline. A version is
picked from the movie's ordinary version list and behaves like any other version
of that movie: the server converts the MVC file into the picked version as it
plays.

## Supported playback versions

Every version below is built into the catalog and listed in catalog order -
18 supported playback versions. On a fresh install four of them are enabled -
3D Anaglyph Red/Cyan (Dubois), 3D Full Side-by-Side, 3D Half Side-by-Side, and
2D Base - and Red/Cyan (Dubois) is also the default offered first; the others are
switched on from the admin settings page.

- 3D Full Side-by-Side
- The anaglyph presets - every built-in FFmpeg `stereo3d` anaglyph output:
  - Red/Cyan:
    - 3D Anaglyph Red/Cyan (Dubois)
    - 3D Anaglyph Red/Cyan
    - 3D Anaglyph Red/Cyan (Half Colour)
    - 3D Anaglyph Red/Cyan (Gray)
  - Green/Magenta:
    - 3D Anaglyph Green/Magenta (Dubois)
    - 3D Anaglyph Green/Magenta
    - 3D Anaglyph Green/Magenta (Half Colour)
    - 3D Anaglyph Green/Magenta (Gray)
  - Yellow/Blue:
    - 3D Anaglyph Yellow/Blue (Dubois)
    - 3D Anaglyph Yellow/Blue
    - 3D Anaglyph Yellow/Blue (Half Colour)
    - 3D Anaglyph Yellow/Blue (Gray)
  - Red/Blue:
    - 3D Anaglyph Red/Blue (Gray)
  - Red/Green:
    - 3D Anaglyph Red/Green (Gray)
- 3D Anaglyph Custom Colours
- 3D Half Side-by-Side
- 2D Base

## Features

- Enabled profiles appear as versions of an eligible MVC movie in the stock
  version pickers - the details-page version list and the playback source menu -
  not in a plugin-only screen.
- One global default version is offered first to every client; the administrator
  picks it on the settings page and the rest follow in catalog order.
- Conservative MVC detection: versions arrive for files that really look like
  MVC, and a file whose name merely says `3D` is left alone.
- A custom anaglyph version tinted with two administrator-chosen colours.
- An admin switch over the raw 3D MVC entry in the version pickers - keep it or
  hide it - with the underlying library item untouched either way.
- Image subtitles (PGS and friends) can be rendered once, inside the stereo
  picture, through subtitle depth - `Automatic`, `Constant shift`, `Plane`, or
  `Flat` - instead of doubled onto each eye.
- Hardware-accelerated encoding follows the server's own hardware-acceleration
  settings (QSV, VA-API on Intel hosts); MVC decode itself is software, and
  ordinary 2D playback keeps its hardware decode.
- Ordinary Jellyfin playback is untouched: commands that carry no Anaglyfin
  version - and Jellyfin's startup capability probes - reach the server's own
  FFmpeg exactly as the server wrote them, while only the marker/profile
  commands reach the FFmpeg-mvc build. See
  [docs/install.md](docs/install.md) and the `ANAGLYFIN_SERVER_FFMPEG` variable.
- One configurable limit on concurrent Anaglyfin transcodes (default `1`); a
  second version requested past the limit is refused with a message that says
  how to raise it. A slot left by a wrapper that died is taken over on its own
  once the process that claimed it is gone, so slot files do not have to be
  cleared by hand after a crash or a restart.

## Requirements

| Item | Need |
| --- | --- |
| Jellyfin | 12 (`12.0.0`) |
| FFmpeg runtime | `anaglyfin-ffmpeg` (the wrapper), an [FFmpeg-mvc](https://github.com/tthayer93/FFmpeg-mvc) build for the marker/profile commands, and the `ffprobe` from the **official** Jellyfin FFmpeg placed beside the wrapper (not the FFmpeg-mvc one), placed by [docs/install.md](docs/install.md) - the plugin never bundles or installs them. Ordinary playback and the server's capability probes run on the stock FFmpeg the server would otherwise use, named by `ANAGLYFIN_SERVER_FFMPEG` |
| Docker + Intel QSV/VA-API hardware encode | the host GPU mapped into the container (`/dev/dri`); software encoding needs no device mapping |

## Install

### Plugin

Add the Anaglyfin plugin repository in **Dashboard -> Plugins -> Catalogs ->
Repositories -> Add plugin repository**, install **Anaglyfin** from the catalog,
and restart Jellyfin:

| Field | Value |
| --- | --- |
| Name | `Anaglyfin` |
| Url | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |

That installs the plugin only.

### FFmpeg runtime

Install the FFmpeg runtime by following [docs/install.md](docs/install.md).

## Environment variables

The Anaglyfin FFmpeg entry point is started by Jellyfin, not by the plugin, so
the deployment settings come from the server environment. Required:

- `JELLYFIN_FFMPEG` - Jellyfin's FFmpeg path, pointed at the `anaglyfin-ffmpeg`
  wrapper
- `ANAGLYFIN_REAL_FFMPEG` - the real `ffmpeg-mvc` binary the wrapper launches
- `ANAGLYFIN_LOCK_DIR` - the directory the wrapper takes its concurrency slots in,
  shared by every wrapper that should share one limit
- `ANAGLYFIN_WRAPPER_SETTINGS` - the settings document carrying the admin page's
  subtitle-depth request and concurrency limit from the plugin to the wrappers

Optional:

- `ANAGLYFIN_SERVER_FFMPEG` - the stock FFmpeg the server would otherwise run
  (on Docker, the image's own `/usr/lib/jellyfin-ffmpeg/ffmpeg`). Ordinary
  commands and Jellyfin's startup capability probes are dispatched to it, so a
  codec or tone-mapping tool the server probes for is available to the commands
  that expect it; marker/profile commands still go to the `ffmpeg-mvc` binary
  named by `ANAGLYFIN_REAL_FFMPEG`. Unset or blank leaves the older single-binary
  behaviour in place, where every command - ordinary and marker alike - goes to
  `ANAGLYFIN_REAL_FFMPEG`. (`ANAGLYFIN_OFFICIAL_FFMPEG` is accepted as an alias
  for the same value; prefer `ANAGLYFIN_SERVER_FFMPEG` as the one canonical name.)
- `ANAGLYFIN_MAX_CONCURRENT_TRANSCODES` - overrides the admin page's concurrency
  limit for wrapper processes that receive it
- `ANAGLYFIN_SLOT_WAIT_MS` - how long a wrapper keeps trying to take a concurrency
  slot before it refuses the playback with exit code `75`; `0` refuses as soon as
  every slot looks taken. Default `2000` ms, accepted range `0`-`30000`.

Values and the procedure for both server shapes are in
[docs/install.md](docs/install.md).

## Documentation

- [docs/install.md](docs/install.md) - installing and updating the plugin and the
  FFmpeg runtime, on Docker and bare metal
- [docs/architecture.md](docs/architecture.md) - plugin and wrapper design, and the
  known follow-ups
- [docs/validation.md](docs/validation.md) - runtime validation checklist and the
  recorded results

## License

Anaglyfin is free software and is licensed under the GNU General Public License,
version 3 only (`GPL-3.0-only`). See [`LICENSE`](LICENSE) for the complete terms.

Copyright © 2026 Tom Thayer

Third-party components used at runtime, including FFmpeg-mvc when installed
separately, remain under their respective licenses and are not re-licensed by
Anaglyfin.

## AI-assisted development

AI was used in the development of this project.
