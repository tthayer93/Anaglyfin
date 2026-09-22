# Installing Anaglyfin

Anaglyfin is a plugin plus a hand-placed FFmpeg entry point. Add one repository URL, install the
plugin, and restart; then place three runtime binaries and point Jellyfin's FFmpeg at the wrapper.
A Debian/Ubuntu package install and the official container install the plugin the same way.

## Prerequisites

- Jellyfin **12.0.0** — a Debian/Ubuntu package under systemd, or the official
  `jellyfin/jellyfin:latest` container with `./jellyfin/config` persisted at `/config`.
- The three runtime binaries in step 2, downloaded from GitHub.
  **The plugin does not install them**, on either shape.

## 1. Add the plugin repository

The plugin installs from one URL, with no download. In **Dashboard → Plugins → Catalogs →
Repositories → Add plugin repository**, enter:

| Field | Value |
| --- | --- |
| Name | `Anaglyfin` |
| Url | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |

Install **Anaglyfin** from the catalogue, then **restart Jellyfin** — the server loads a newly
installed plugin only at startup. The settings page then appears in the dashboard's settings menu.
Jellyfin shows its usual "third-party repository" warning; it is expected. A release published
minutes ago may not be in the catalogue yet — reopen later.

## 2. Obtain the runtime files

Download these before the final restart in step 3 or 4:

| File | Where it comes from |
| --- | --- |
| `anaglyfin-ffmpeg` | The Anaglyfin GitHub release, as a release asset (never in the plugin archive) |
| `ffmpeg-mvc` | Your Jellyfin-compatible FFmpeg-mvc build |
| `ffprobe` | The `ffprobe` from that **same** FFmpeg-mvc build |

Take `ffmpeg-mvc` and `ffprobe` from one build so probing and decoding agree, and check them against
the release's `SHA256SUMS.txt`. The current subtitle-depth target is FFmpeg-mvc `n8.1.2-mvc7-jf4`;
an older build without `mvcsubdepth` ignores a depth request. The server finds `ffprobe` **beside the
FFmpeg path it was given**, not on `PATH`, and the plugin installs none of these files — which is why
all three live in one directory in steps 3 and 4.

## 3. Docker install (recommended)

Assumes `./jellyfin/config` mounted at `/config`. Everything the plugin and wrapper need lives under
`/config` so it survives a container recreate. Put the three binaries in one tree:

```text
./jellyfin/config/anaglyfin/
├── ffmpeg/
│   ├── anaglyfin-ffmpeg
│   ├── ffmpeg-mvc
│   └── ffprobe
├── lock/
└── wrapper/
```

1. Create the directories and install the binaries on the host:

   ```sh
   mkdir -p ./jellyfin/config/anaglyfin/ffmpeg \
            ./jellyfin/config/anaglyfin/lock \
            ./jellyfin/config/anaglyfin/wrapper
   install -m 0755 ./anaglyfin-ffmpeg  ./jellyfin/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
   install -m 0755 /path/to/ffmpeg-mvc ./jellyfin/config/anaglyfin/ffmpeg/ffmpeg-mvc
   install -m 0755 /path/to/ffprobe    ./jellyfin/config/anaglyfin/ffmpeg/ffprobe
   ```

2. Point Jellyfin at the wrapper in `compose.yml`. These paths are inside the container:

   ```yaml
   services:
     jellyfin:
       image: jellyfin/jellyfin:latest
       environment:
         JELLYFIN_FFMPEG: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
         ANAGLYFIN_REAL_FFMPEG: /config/anaglyfin/ffmpeg/ffmpeg-mvc
         ANAGLYFIN_LOCK_DIR: /config/anaglyfin/lock
         ANAGLYFIN_WRAPPER_SETTINGS: /config/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
       volumes:
         - ./jellyfin/config:/config
         - ./jellyfin/cache:/cache
         - /path/to/media:/media
   ```

3. Recreate the container:

   ```sh
   docker compose up -d --force-recreate jellyfin
   ```

`ANAGLYFIN_LOCK_DIR` and the directory holding `ANAGLYFIN_WRAPPER_SETTINGS` must be writable by the
Jellyfin user — the plugin writes that settings file and every wrapper reads it, which is how the
admin page's subtitle-depth and concurrency settings reach the wrapper. Add
`ANAGLYFIN_MAX_CONCURRENT_TRANSCODES` only to override the admin page's concurrency limit.

## 4. Bare-metal install

The same three binaries together under `/opt/anaglyfin/ffmpeg`; the lock and settings directories
under the server data directory. The package install runs as `jellyfin`, so own everything to it:

```sh
sudo install -d -m 0755 /opt/anaglyfin/ffmpeg
sudo install -m 0755 -o jellyfin -g jellyfin ./anaglyfin-ffmpeg  /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
sudo install -m 0755 -o jellyfin -g jellyfin /path/to/ffmpeg-mvc /opt/anaglyfin/ffmpeg/ffmpeg-mvc
sudo install -m 0755 -o jellyfin -g jellyfin /path/to/ffprobe    /opt/anaglyfin/ffmpeg/ffprobe
sudo install -d -m 0755 -o jellyfin -g jellyfin /var/lib/jellyfin/anaglyfin/lock
sudo install -d -m 0755 -o jellyfin -g jellyfin /var/lib/jellyfin/anaglyfin/wrapper
```

Pass the environment to the service in a systemd drop-in:

```ini
# /etc/systemd/system/jellyfin.service.d/anaglyfin.conf
[Service]
Environment=JELLYFIN_FFMPEG=/opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
Environment=ANAGLYFIN_REAL_FFMPEG=/opt/anaglyfin/ffmpeg/ffmpeg-mvc
Environment=ANAGLYFIN_LOCK_DIR=/var/lib/jellyfin/anaglyfin/lock
Environment=ANAGLYFIN_WRAPPER_SETTINGS=/var/lib/jellyfin/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

Apply it and restart:

```sh
sudo systemctl daemon-reload
sudo systemctl restart jellyfin
```

## Manual plugin install (offline only)

Use this only when the server cannot reach the plugin repository URL. Download
`Anaglyfin_0.1.0.zip` from the Anaglyfin GitHub release, unpack it into the plugin directory, and
restart Jellyfin.

Debian/Ubuntu package (data directory `/var/lib/jellyfin`):

```sh
sudo install -d /var/lib/jellyfin/plugins/Anaglyfin
sudo unzip -o Anaglyfin_0.1.0.zip -d /var/lib/jellyfin/plugins/Anaglyfin
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Anaglyfin
```

Docker (`/config/plugins`):

```sh
mkdir -p ./jellyfin/config/plugins/Anaglyfin
unzip -o Anaglyfin_0.1.0.zip -d ./jellyfin/config/plugins/Anaglyfin
```

## Verify the installation

- `Dashboard → Plugins` lists **Anaglyfin**, and its settings page opens from the dashboard's settings menu.
- The server log names the wrapper as Jellyfin's FFmpeg and passes the version probe:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: Found ffmpeg version 8.1.2
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ```

- The wrapper `-version` call passes straight through to `ANAGLYFIN_REAL_FFMPEG`, proving the real encoder was found:

  ```sh
  /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version                       # docker
  sudo -u jellyfin /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version         # bare metal
  ```

- A server whose `ANAGLYFIN_REAL_FFMPEG` names a missing file does not start; the log blames the
  wrapper path (`Failed version check`) but the fix is the encoder path. Full checks: `docs/validation.md`.

## Updating

A new plugin version appears behind the same repository URL; install the revision from the
catalogue and restart Jellyfin. Replacing `anaglyfin-ffmpeg`, `ffmpeg-mvc`, or `ffprobe` is a
separate manual step — swap the file under `.../ffmpeg/` and restart; the plugin tracks none of them.
