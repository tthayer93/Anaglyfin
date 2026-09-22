# Installing Anaglyfin

Install the plugin from one repository URL, get three FFmpeg runtime files onto the server,
and point Jellyfin's FFmpeg at the wrapper. On Docker one image build does the middle step.
Target: Jellyfin **12.0.0** — Debian/Ubuntu package or `jellyfin/jellyfin:latest`.

## 1. Install the plugin

In **Dashboard → Plugins → Catalogs → Repositories → Add plugin repository**, enter:

| Field | Value |
| --- | --- |
| Name | `Anaglyfin` |
| Url | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |

Install **Anaglyfin** from the catalog, then **restart Jellyfin**: a plugin is loaded at startup.

That installs the plugin only — no runtime binaries. The server wants those in place before it next
starts, and §2 says where each shape gets them.

**No catalog?** Unpack `Anaglyfin_<version>.zip` from the
[GitHub release](https://github.com/tthayer93/Anaglyfin/releases) into
`/var/lib/jellyfin/plugins/Anaglyfin` or `./jellyfin/config/plugins/Anaglyfin`; on a package
install, run `sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Anaglyfin`, then restart.

## 2. Runtime files

All three live in **one** directory, `/opt/anaglyfin/ffmpeg`, named exactly:

- `anaglyfin-ffmpeg` — the [release asset](https://github.com/tthayer93/Anaglyfin/releases).
- `ffmpeg-mvc` — a Jellyfin-compatible FFmpeg-mvc build; target `n8.1.2-mvc7-jf4`.
- `ffprobe` — the `ffprobe` from that **same** build.

Jellyfin finds `ffprobe` **beside the FFmpeg path it was given** — beside the wrapper — never on `PATH`.
The two server shapes differ only in who fills that directory:

- **Docker:** the sample image downloads the wrapper and compiles FFmpeg-mvc into
  `/opt/anaglyfin/ffmpeg` while the image is being built. The Docker host places nothing.
- **Bare metal:** you install all three yourself (§4).

## 3. Docker server (recommended)

Nothing is installed on the Docker host and nothing extra is mounted for the runtime — the
image carries the server and the three files together. Assumes `./jellyfin/config` mounted
at `/config`.

1. **Build the sample image**, from the repository root:

   ```sh
   docker build -t anaglyfin-jellyfin:0.1.0 -f docs/Dockerfile.jellyfin .
   ```

   It starts from `jellyfin/jellyfin:latest`, fetches `anaglyfin-ffmpeg` from the
   [GitHub release](https://github.com/tthayer93/Anaglyfin/releases), compiles FFmpeg-mvc
   `n8.1.2-mvc7-jf4` inside the image, and installs all three under `/opt/anaglyfin/ffmpeg`.
   The compile takes a few minutes and is cached in an image layer; `--build-arg` on
   `ANAGLYFIN_RELEASE` or `FFMPEG_MVC_TAG` moves the image to another version.

2. **Point the Jellyfin service at it** — one line of your compose file:

   ```yaml
   image: anaglyfin-jellyfin:0.1.0
   ```

3. **Add the service, or recreate it** if it was already running:

   ```sh
   docker compose up -d --force-recreate jellyfin
   ```

4. **Install the plugin from the catalog** (§1) — the image carries the runtime, not the plugin.
   If a plugin install or update is pending, the recreate above, or a plain
   `docker compose restart jellyfin`, is what loads it.

The sample image already sets the FFmpeg path and the wrapper environment, so the service needs
no environment of its own. Write this only to manage environment explicitly in Compose — to move
one path, or the locks off `/config`:

```yaml
environment:
  JELLYFIN_FFMPEG: /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ANAGLYFIN_REAL_FFMPEG: /opt/anaglyfin/ffmpeg/ffmpeg-mvc
  ANAGLYFIN_LOCK_DIR: /config/anaglyfin/lock
  ANAGLYFIN_WRAPPER_SETTINGS: /config/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

The lock directory and the directory holding the settings file are created on demand; they need a
`/config` the Jellyfin user can write, which the mount above already gives them.

## 4. Bare-metal server

Same grouping under `/opt/anaglyfin/ffmpeg`; `lock` and `wrapper` under the server data directory,
everything owned by the `jellyfin` user:

```sh
sudo install -d -m 0755 /opt/anaglyfin/ffmpeg
sudo install -m 0755 -o jellyfin -g jellyfin ./anaglyfin-ffmpeg  /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
sudo install -m 0755 -o jellyfin -g jellyfin /path/to/ffmpeg-mvc /opt/anaglyfin/ffmpeg/ffmpeg-mvc
sudo install -m 0755 -o jellyfin -g jellyfin /path/to/ffprobe    /opt/anaglyfin/ffmpeg/ffprobe
sudo install -d -m 0755 -o jellyfin -g jellyfin /var/lib/jellyfin/anaglyfin/lock /var/lib/jellyfin/anaglyfin/wrapper
```

Pass those four variables in a systemd drop-in. Create the drop-in directory first:

```sh
sudo install -d /etc/systemd/system/jellyfin.service.d
```

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

## 5. Verify and update

- `Dashboard → Plugins` lists **Anaglyfin** and its settings page opens.
- The server log names the wrapper as Jellyfin's FFmpeg and passes the version probe. Same line on
  either shape, since both put the wrapper at the same path:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: Found ffmpeg version 8.1.2
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ```

- The wrapper answers `-version` straight through to the real encoder:
  `docker compose exec jellyfin /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version` (Docker),
  `sudo -u jellyfin /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version` (bare metal).

A server whose `ANAGLYFIN_REAL_FFMPEG` names a missing file does not start; full checks: `docs/validation.md`.

Update the plugin from the catalog and restart Jellyfin. The three runtime files are the server's
own, and the plugin tracks none of them:

- **Docker:** rebuild the image with `-f docs/Dockerfile.jellyfin`, then recreate the service.
- **Bare metal:** swap the file under `/opt/anaglyfin/ffmpeg/` by hand and restart.
