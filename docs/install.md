# Installing Anaglyfin

Install the plugin from one repository URL, then place three binaries by hand and point Jellyfin's
FFmpeg at the wrapper. Target: Jellyfin **12.0.0** — Debian/Ubuntu package or `jellyfin/jellyfin:latest`.

## 1. Install the plugin

In **Dashboard → Plugins → Catalogs → Repositories → Add plugin repository**, enter:

| Field | Value |
| --- | --- |
| Name | `Anaglyfin` |
| Url | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |

Install **Anaglyfin** from the catalog, then **restart Jellyfin**: a plugin is loaded at startup.

That installs the plugin only — no runtime binaries. Place those before the final restart.

**No catalog?** Unpack `Anaglyfin_<version>.zip` from the
[GitHub release](https://github.com/tthayer93/Anaglyfin/releases) into
`/var/lib/jellyfin/plugins/Anaglyfin` or `./jellyfin/config/plugins/Anaglyfin`; on a package
install, run `sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Anaglyfin`, then restart.

## 2. Runtime files

All three live in **one** directory, named exactly:

- `anaglyfin-ffmpeg` — the [release asset](https://github.com/tthayer93/Anaglyfin/releases).
- `ffmpeg-mvc` — a Jellyfin-compatible FFmpeg-mvc build; target `n8.1.2-mvc7-jf4`.
- `ffprobe` — the `ffprobe` from that **same** build.

Jellyfin finds `ffprobe` **beside the FFmpeg path it was given** — beside the wrapper — never on `PATH`.

## 3. Docker server (recommended)

Assumes `./jellyfin/config` mounted at `/config`. Files go under `./jellyfin/config/anaglyfin/ffmpeg`:

```sh
mkdir -p ./jellyfin/config/anaglyfin/ffmpeg ./jellyfin/config/anaglyfin/lock ./jellyfin/config/anaglyfin/wrapper
install -m 0755 ./anaglyfin-ffmpeg  ./jellyfin/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
install -m 0755 /path/to/ffmpeg-mvc ./jellyfin/config/anaglyfin/ffmpeg/ffmpeg-mvc
install -m 0755 /path/to/ffprobe    ./jellyfin/config/anaglyfin/ffmpeg/ffprobe
```

Point Jellyfin at the wrapper in `compose.yml`; these paths are inside the container:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:latest
    environment:
      JELLYFIN_FFMPEG: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
      ANAGLYFIN_REAL_FFMPEG: /config/anaglyfin/ffmpeg/ffmpeg-mvc
      ANAGLYFIN_LOCK_DIR: /config/anaglyfin/lock
      ANAGLYFIN_WRAPPER_SETTINGS: /config/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

Recreate the container:

```sh
docker compose up -d --force-recreate jellyfin
```

`lock`, and the directory holding the settings file, must be writable by the Jellyfin user.

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

Pass the same environment in a systemd drop-in. Create the drop-in directory first:

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
- The server log names the wrapper as Jellyfin's FFmpeg and passes the version probe:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: Found ffmpeg version 8.1.2
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ```

- The wrapper answers `-version` straight through to the real encoder:
  `docker compose exec jellyfin /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version` (Docker),
  `sudo -u jellyfin /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version` (bare metal).

A server whose `ANAGLYFIN_REAL_FFMPEG` names a missing file does not start; full checks: `docs/validation.md`.

Update the plugin from the catalog and restart Jellyfin. `anaglyfin-ffmpeg`, `ffmpeg-mvc` and `ffprobe`
are replaced by hand: swap the file under `.../ffmpeg/` and restart — the plugin tracks none of them.
